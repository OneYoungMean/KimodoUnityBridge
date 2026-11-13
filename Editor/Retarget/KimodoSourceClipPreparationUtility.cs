using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoSourceClipPreparationUtility
    {
        internal static bool TryResolveSourceAvatar(
            ref AnimationClip clip, Avatar bindingAvatar, out Avatar sourceAvatar, out string error)
        {
            sourceAvatar = null;
            error = string.Empty;
            if (clip == null)
            {
                error = "Source AnimationClip is missing.";
                return false;
            }
            if (clip.isHumanMotion)
            {
                sourceAvatar = bindingAvatar;
                if (KimodoRetargetCoreUtility.IsValidHumanoid(sourceAvatar)) return true;
                error = "Humanoid source Clip requires a valid sampling Avatar.";
                return false;
            }

            string path = AssetDatabase.GetAssetPath(clip);
            if (AssetImporter.GetAtPath(path) is ModelImporter importer)
            {
                string clipName = clip.name;
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(clip, out string guid, out long localId);
                GameObject instance = null;
                try
                {
                    var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (model == null) throw new InvalidOperationException("Source model hierarchy is missing.");
                    instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
                    instance.hideFlags = HideFlags.HideAndDontSave;
                    // A model identity prevents different rest poses sharing a hierarchy-only Avatar cache.
                    var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
                    string physicalPath = package == null ? System.IO.Path.GetFullPath(path)
                        : package.resolvedPath + path.Substring(package.assetPath.Length);
                    string modelHash;
                    using (var hash = System.Security.Cryptography.SHA256.Create())
                        modelHash = Convert.ToBase64String(hash.ComputeHash(System.IO.File.ReadAllBytes(physicalPath)));
                    instance.name = model.name + "_" + guid + "_" + Hash128.Compute(modelHash + EditorJsonUtility.ToJson(importer));
                    PrefabUtility.RecordPrefabInstancePropertyModifications(instance);
                    var result = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(instance);
                    if (!result.IsHumanoid) throw new InvalidOperationException(result.Error);
                    sourceAvatar = result.Avatar;

                    // Generating the Avatar temporarily reimports the model and can invalidate its sub-assets.
                    AnimationClip[] clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().ToArray();
                    clip = clips.FirstOrDefault(candidate =>
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out string _, out long id) && id == localId)
                        ?? clips.SingleOrDefault(candidate => candidate.name == clipName);
                    if (clip == null) throw new InvalidOperationException("Source Clip could not be reloaded after Avatar preparation.");
                    return TryValidateTransformPaths(clip, sourceAvatar, out error);
                }
                catch (Exception ex)
                {
                    error = $"Cannot prepare source Clip '{clipName}' from '{path}': {ex.Message}";
                    return false;
                }
                finally
                {
                    if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                }
            }

            // Standalone bone clips have no model provenance; accept only the supplied matching rig.
            sourceAvatar = bindingAvatar;
            if (TryValidateTransformPaths(clip, sourceAvatar, out error)) return true;
            error = $"Cannot resolve source rig for Clip '{clip.name}': {error} Use the Clip in its source model or bind it to its original character.";
            return false;
        }

        internal static bool TryPrepareForAvatar(
            AnimationClip source, Avatar targetAvatar, out AnimationClip prepared, out string error,
            bool persist = false, bool requireBoneClip = false)
        {
            prepared = null;
            if (!TryResolveSourceAvatar(ref source, targetAvatar, out Avatar sourceAvatar, out error)) return false;
            if ((!requireBoneClip && source.isHumanMotion) ||
                (!source.isHumanMotion && sourceAvatar == targetAvatar))
            {
                prepared = source;
                return true;
            }
            return KimodoRetargetToolsEditor.TryGetOrCreateEditorBoneClip(
                source, sourceAvatar, targetAvatar, out prepared, out _, out error, persist);
        }

        private static bool TryValidateTransformPaths(AnimationClip clip, Avatar avatar, out string error)
        {
            if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    avatar, "KimodoSourceClipValidation", out RetargetSkeleton rig, out error)) return false;
            using (rig)
            {
                var paths = new HashSet<string>(rig.root.GetComponentsInChildren<Transform>(true)
                    .Select(bone => AnimationUtility.CalculateTransformPath(bone, rig.root.transform)), StringComparer.Ordinal);
                string[] animatedPaths = AnimationUtility.GetCurveBindings(clip)
                    .Where(binding => binding.type == typeof(Transform)).Select(binding => binding.path).Distinct().ToArray();
                if (animatedPaths.Length == 0)
                {
                    error = "The Clip contains no Humanoid motion or bone Transform curves.";
                    return false;
                }
                string[] missing = animatedPaths.Where(path => !paths.Contains(path)).Take(3).ToArray();
                if (missing.Length > 0)
                {
                    error = "Bone curve paths do not match the sampling Avatar: " + string.Join(", ", missing);
                    return false;
                }
                error = string.Empty;
                return true;
            }
        }

        internal sealed class TimelineScope : IDisposable
        {
            private readonly List<(AnimationPlayableAsset asset, GlobalObjectId originalId, AnimationClip original, AnimationClip prepared)> replacements =
                new List<(AnimationPlayableAsset, GlobalObjectId, AnimationClip, AnimationClip)>();

            internal bool Changed => replacements.Count > 0;

            internal static bool TryCreate(
                KimodoTimelineInOutConstraintContext context, Avatar avatar, out TimelineScope scope, out string error,
                double? rangeStart, double? rangeEnd)
            {
                scope = new TimelineScope();
                error = string.Empty;
                try
                {
                    AnimationTrack root = context.Track as AnimationTrack
                        ?? throw new InvalidOperationException("Source Timeline track is not an AnimationTrack.");
                    while (root.parent is AnimationTrack parent) root = parent;
                    foreach (var asset in EnumerateTracks(root).SelectMany(track => track.GetClips())
                                 .Where(clip => (!rangeStart.HasValue || clip.end >= rangeStart.Value) &&
                                                (!rangeEnd.HasValue || clip.start <= rangeEnd.Value))
                                 .Select(clip => clip.asset).OfType<AnimationPlayableAsset>().Distinct())
                    {
                        if (asset.clip == null) continue;
                        AnimationClip original = asset.clip;
                        GlobalObjectId originalId = GlobalObjectId.GetGlobalObjectIdSlow(original);
                        if (!TryPrepareForAvatar(original, avatar, out AnimationClip prepared, out error,
                                requireBoneClip: !context.Animator.isHuman))
                            throw new InvalidOperationException(error);
                        original = asset.clip;
                        if (prepared == original) continue;
                        scope.replacements.Add((asset, originalId, original, prepared));
                        asset.clip = prepared;
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    scope.Dispose();
                    scope = null;
                    error = ex.Message;
                    return false;
                }
            }

            private static IEnumerable<AnimationTrack> EnumerateTracks(AnimationTrack track)
            {
                yield return track;
                foreach (var child in track.GetChildTracks().OfType<AnimationTrack>())
                    foreach (var descendant in EnumerateTracks(child)) yield return descendant;
            }

            internal void Restore()
            {
                foreach (var entry in replacements)
                    if (entry.asset != null)
                        entry.asset.clip = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(entry.originalId) as AnimationClip ?? entry.original;
            }

            public void Dispose()
            {
                Restore();
                foreach (var clip in replacements.Select(entry => entry.prepared).Distinct())
                    if (clip != null && !EditorUtility.IsPersistent(clip)) UnityEngine.Object.DestroyImmediate(clip);
                replacements.Clear();
            }
        }
    }
}
