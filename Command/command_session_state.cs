using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private const string CreateTimelineSessionUndoName = "Create Kimodo Timeline Session";
        private const string AnalysisContractVersion = "7-command-60-explicit-frame-units-v1";
        internal const int SessionCaptureLayer = 17;
        internal const int ClipSafeZoneFrames = 4;
        internal const double ClipSafeZoneSeconds = ClipSafeZoneFrames / KimodoFrameTimeUtility.CommandFrameRate;
        private const string GeneratedTimelineFolder = KimodoEditorClipWritebackService.GeneratedClipFolder + "/Timelines";

        private static GameObject[] GetSceneSessionRoots(Scene scene)
        {
            return FindLoadedSessionRoots(scene);
        }

        internal static GameObject[] FindLoadedSessionRoots(Scene? scene = null)
        {
            if (scene.HasValue && (!scene.Value.IsValid() || !scene.Value.isLoaded))
                return Array.Empty<GameObject>();

            return Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(item => item != null && item.scene.IsValid() && item.scene.isLoaded &&
                    (!scene.HasValue || item.scene == scene.Value) &&
                    item.name.StartsWith(KimodoRuntimeUtility.SessionRootPrefix, StringComparison.Ordinal))
                .ToArray();
        }

        private static void RequireSingleSceneSession(Scene scene)
        {
            GameObject[] roots = GetSceneSessionRoots(scene);
            if (roots.Length > 1)
            {
                throw new CommandException(
                    "multiple_contexts",
                    $"Scene '{scene.name}' contains {roots.Length} Kimodo contexts. This plugin supports exactly one context per scene. Run session.clear to destroy the duplicates, then retry.");
            }
        }

        private static int DestroySceneSessionRoots(Scene scene)
        {
            GameObject[] roots = GetSceneSessionRoots(scene);
            int removedCount = roots.Length;
            foreach (GameObject root in roots)
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }

            // Clean up the legacy container created by the former timeline
            // switching path. New switches use session.clear and never create it.
            GameObject legacyArchive = scene.IsValid()
                ? scene.GetRootGameObjects().FirstOrDefault(root => root != null && root.name == "KimodoSessionArchive")
                : null;
            if (legacyArchive != null)
            {
                removedCount += legacyArchive.transform.childCount;
                UnityEngine.Object.DestroyImmediate(legacyArchive);
            }

            return removedCount;
        }

        private static void ActivateTimelineSession(TimelineSessionRecord session)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                KimodoDisableOnRuntime.Ensure(session?.SessionRoot);
                return;
            }
            if (session?.SessionRoot != null) session.SessionRoot.SetActive(true);
            if (session?.Director != null) session.Director.enabled = true;
        }

        private static TimelineSessionRecord CreateTimelineSession(string requestedName)
        {
            string name = requestedName.Trim();
            if (name.Length == 0)
            {
                throw new InvalidOperationException("name cannot be empty.");
            }
            RequireSingleSceneSession(SceneManager.GetActiveScene());
            if (GetSceneSessionRoots(SceneManager.GetActiveScene()).Length != 0)
            {
                throw new CommandException("context_already_exists", "A context GameObject already exists in the active scene but is not registered. Re-resolve it or run session.clear before creating another.");
            }
            KimodoEditorClipWritebackService.EnsureFolderExists(GeneratedTimelineFolder);
            string safeName = KimodoRuntimeUtility.SanitizeName(name, "Session");
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{GeneratedTimelineFolder}/Kimodo_CommandSession_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.playable");
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(CreateTimelineSessionUndoName);
            try
            {
                TimelineAsset timelineAsset = ScriptableObject.CreateInstance<TimelineAsset>();
                Undo.RegisterCreatedObjectUndo(timelineAsset, CreateTimelineSessionUndoName);
                timelineAsset.editorSettings.frameRate = SessionFrameRate;
                AssetDatabase.CreateAsset(timelineAsset, assetPath);
                GameObject sessionRoot = new GameObject($"KimodoSession_{safeName}");
                sessionRoot.layer = SessionCaptureLayer;
                Undo.RegisterCreatedObjectUndo(sessionRoot, CreateTimelineSessionUndoName);
                KimodoDisableOnRuntime.Ensure(sessionRoot);
                PlayableDirector director = sessionRoot.AddComponent<PlayableDirector>();
                director.playableAsset = timelineAsset;
                director.time = 0.0;
                Guid sessionId = GetTimelineAssetGuid(timelineAsset);
                var record = new TimelineSessionRecord(sessionId, name, director, timelineAsset, assetPath, sessionRoot);
                EnsureSessionHistoryHeader(record);
                EditorUtility.SetDirty(timelineAsset);
                EditorUtility.SetDirty(director);
                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);
                return record;
            }
            catch
            {
                Undo.RevertAllDownToGroup(undoGroup);
                throw;
            }
        }

        private static void BindTimelineCharacter(
            TimelineSessionRecord session,
            GameObject timelineCharacter,
            GameObject source,
            Animator animator,
            AnimationTrack track)
        {
            if (timelineCharacter == null || source == null) throw new InvalidOperationException("Timeline-bound and source characters are required.");
            if (timelineCharacter != source) throw new InvalidOperationException("Timeline preview must bind the original scene character directly.");
            ArchiveLegacyGhostPreviews(session, source);
            if (animator == null || track == null || session?.Director == null) return;
            UnityEngine.Object existingBinding = session.Director.GetGenericBinding(track);
            if (existingBinding != null && !BindingMatches(existingBinding, animator))
            {
                Debug.LogWarning($"[Kimodo] Preserved existing binding on track '{track.name}' instead of replacing it.");
                return;
            }
            if (existingBinding != null)
            {
                return;
            }
            session.Director.SetGenericBinding(track, animator);
        }

        private static void ArchiveLegacyGhostPreviews(TimelineSessionRecord session, GameObject source)
        {
            if (session?.SessionRoot == null || source == null) return;
            Transform[] legacyGhosts = session.SessionRoot.transform.Cast<Transform>()
                .Where(child => child != null && child.gameObject != source &&
                    child.name.StartsWith("Ghost_", StringComparison.Ordinal))
                .ToArray();
            if (legacyGhosts.Length == 0) return;

            Transform archive = session.SessionRoot.transform.Find("Archive_GhostPreviews");
            if (archive == null)
            {
                var archiveObject = new GameObject("Archive_GhostPreviews");
                archiveObject.hideFlags = HideFlags.DontSaveInEditor;
                archiveObject.transform.SetParent(session.SessionRoot.transform, false);
                archive = archiveObject.transform;
            }

            foreach (Transform ghost in legacyGhosts)
            {
                ghost.gameObject.SetActive(false);
                ghost.SetParent(archive, true);
            }
        }

        private static GameObject[] ResolveRequestedSceneCharacters(string requestedName)
        {
            if (string.Equals(requestedName, "@active_animator", StringComparison.OrdinalIgnoreCase))
            {
                Animator activeAnimator = Selection.activeGameObject?.GetComponent<Animator>()
                    ?? Selection.activeGameObject?.GetComponentInParent<Animator>()
                    ?? Selection.activeGameObject?.GetComponentInChildren<Animator>(true);
                if (activeAnimator == null ||
                    !activeAnimator.gameObject.scene.IsValid() || EditorUtility.IsPersistent(activeAnimator))
                {
                    return Array.Empty<GameObject>();
                }
                if (activeAnimator.gameObject.scene != SceneManager.GetActiveScene())
                {
                    return Array.Empty<GameObject>();
                }
                if (IsSessionObject(activeAnimator.gameObject) || !activeAnimator.gameObject.activeInHierarchy)
                {
                    return Array.Empty<GameObject>();
                }
                GameObject activeRoot = activeAnimator.transform.root.gameObject;
                return IsIndexableHumanoidCharacter(activeRoot)
                    ? new[] { activeRoot }
                    : Array.Empty<GameObject>();
            }

            bool isPath = requestedName.Contains("/");
            // Match the requested identity before resolving Avatars: resolution may reimport a model.
            Scene activeScene = SceneManager.GetActiveScene();
            IEnumerable<GameObject> candidates = Resources.FindObjectsOfTypeAll<Animator>()
                .Where(animator => animator != null && animator.gameObject.scene.IsValid() &&
                    animator.gameObject.scene == activeScene &&
                    !EditorUtility.IsPersistent(animator) && !IsSessionObject(animator.gameObject))
                .Select(animator => animator.transform.root.gameObject);
            return candidates
                .GroupBy(item => KimodoUnityObjectIdUtility.IdHash(item))
                .Select(group => group.First())
                .Where(item => isPath
                    ? string.Equals(GetSceneHierarchyPath(item), requestedName, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(item.name, requestedName, StringComparison.OrdinalIgnoreCase))
                .Where(item => IsIndexableHumanoidCharacter(item, allowAvatarGeneration: true))
                .ToArray();
        }

        private static bool HasRenderableMesh(GameObject root)
        {
            return root != null && root.GetComponentsInChildren<Renderer>(true)
                .Any(renderer => renderer is MeshRenderer || renderer is SkinnedMeshRenderer);
        }

        private static bool IsIndexableHumanoidCharacter(GameObject root, bool allowAvatarGeneration = true)
        {
            if (root == null || EditorUtility.IsPersistent(root) || !root.scene.IsValid() || IsSessionObject(root))
            {
                return false;
            }

            Animator animator = root.GetComponentInChildren<Animator>(true);
            return animator != null &&
                KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root, allowAvatarGeneration).IsHumanoid;
        }

        private static Animator ResolveAutomaticSceneCharacter()
        {
            Animator selected = Selection.activeGameObject?.GetComponent<Animator>()
                ?? Selection.activeGameObject?.GetComponentInParent<Animator>()
                ?? Selection.activeGameObject?.GetComponentInChildren<Animator>(true);
            if (selected != null && selected.gameObject.scene == SceneManager.GetActiveScene() &&
                IsIndexableHumanoidCharacter(selected.transform.root.gameObject, allowAvatarGeneration: false))
            {
                return selected;
            }

            return SceneManager.GetActiveScene().GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Animator>(true))
                .FirstOrDefault(animator => IsIndexableHumanoidCharacter(
                    animator.transform.root.gameObject, allowAvatarGeneration: false));
        }

        private static string GetSceneHierarchyPath(GameObject gameObject)
        {
            return gameObject == null
                ? string.Empty
                : string.Join("/", gameObject.transform.GetComponentsInParent<Transform>(true)
                    .Reverse().Select(item => item.name));
        }

        private static GameObject ResolveCharacterIdentityRoot(GameObject root, Animator animator)
        {
            return animator != null ? animator.transform.root.gameObject : root;
        }

        private static bool AddCharacterTrack(
            TimelineSessionRecord session,
            GameObject root,
            Animator animator,
            bool tryResolveAvatar,
            out string error,
            bool requireAvatar = false)
        {
            error = string.Empty;
            if (session == null || session.TimelineAsset == null || root == null)
            {
                error = "Scene context and character root are required.";
                return false;
            }
            root = ResolveCharacterIdentityRoot(root, animator);
            animator = root != null ? root.GetComponentInChildren<Animator>(true) : null;
            if (requireAvatar && animator == null)
            {
                error = "avatar_required: a Humanoid Animator with a valid Avatar is required.";
                return false;
            }
            if (animator == null && !HasRenderableMesh(root))
            {
                error = "character_requires_humanoid_or_mesh: the scene object has neither a valid Animator nor a renderable Mesh.";
                return false;
            }
            if (session.Characters.Any(character => character.Root == root ||
                (animator != null && character.Animator == animator)))
            {
                error = "Character is already in the current scene context.";
                return false;
            }

            string characterRef = GetObjectReference(root);
            if (session.Characters.Any(character =>
                string.Equals(character.CharacterRef, characterRef, StringComparison.Ordinal)))
            {
                error = "Character is already in the current scene context.";
                return false;
            }
            GameObject sourceRoot = root;
            string characterName = MakeUniqueCharacterName(session, sourceRoot.name);
            root = sourceRoot;
            animator = root != null ? root.GetComponentInChildren<Animator>(true) : null;
            Avatar avatar = null;
            string avatarError = string.Empty;
            if (tryResolveAvatar)
            {
                KimodoLocalAvatarUtility.AvatarResolveResult result =
                    KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
                avatar = result.Avatar;
                avatarError = result.Error;
            }
            if (requireAvatar && !KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
            {
                error = string.IsNullOrWhiteSpace(avatarError)
                    ? "avatar_required: a valid humanoid Avatar is required."
                    : $"avatar_required: {avatarError}";
                return false;
            }

            AnimationTrack track = session.TimelineAsset.CreateTrack<AnimationTrack>(null, characterName);
            // Keep command-created tracks independent from the character's scene pose.
            track.trackOffset = TrackOffset.ApplyTransformOffsets;
            AnimationTrack poseCacheTrack = session.TimelineAsset.CreateTrack<AnimationTrack>(
                track,
                MakeUniqueSessionObjectName(session, $"{characterName}.Poses"));
            BindTimelineCharacter(session, root, sourceRoot, animator, track);
            var character = new TimelineCharacterRecord(
                characterRef, root, animator, avatar, track, poseCacheTrack, avatarError);
            session.Characters.Add(character);
            EditorUtility.SetDirty(track);
            EditorUtility.SetDirty(poseCacheTrack);
            EditorUtility.SetDirty(session.TimelineAsset);
            return true;
        }

        private static string MakeUniqueCharacterName(TimelineSessionRecord session, string requestedName)
        {
            string baseName = KimodoRuntimeUtility.SanitizeName(requestedName, "Character");
            string name = baseName;
            for (int suffix = 1; session.Characters.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
            {
                name = $"{baseName}_{suffix}";
            }
            return name;
        }

        private static string MakeUniqueSessionObjectName(TimelineSessionRecord session, string requestedName)
        {
            var names = new HashSet<string>(
                session.TimelineAsset.GetRootTracks()
                    .SelectMany(root => new[] { root }.Concat(root.GetChildTracks()))
                    .Select(track => track.name),
                StringComparer.OrdinalIgnoreCase);
            string name = requestedName;
            for (int suffix = 1; names.Contains(name); suffix++)
            {
                name = $"{requestedName}_{suffix}";
            }
            return name;
        }

        private static TimelineAnimationRecord AppendAnimationClip(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            AnimationClip clip,
            string source,
            JObject analysis,
            string requestedName = null)
        {
            double duration = Math.Max(0.0001, clip != null ? clip.length : 0.0001);
            TimelineClip timelineClip = character.Track.CreateClip<AnimationPlayableAsset>();
            timelineClip.start = character.NextStartSeconds;
            timelineClip.duration = duration;
            string animationName = MakeUniqueAnimationName(character,
                string.IsNullOrWhiteSpace(requestedName) ? (clip != null ? clip.name : "Animation") : requestedName);
            timelineClip.displayName = animationName;
            ((AnimationPlayableAsset)timelineClip.asset).clip = clip;
            var animation = new TimelineAnimationRecord(
                GetTimelineAnimationId(timelineClip), timelineClip.displayName, source, clip, timelineClip, analysis, null, 0, 0);
            character.Animations.Add(animation);
            character.NextStartSeconds = timelineClip.end + ClipSafeZoneSeconds;
            EditorUtility.SetDirty(character.Track);
            return animation;
        }


        private static string MakeUniqueAnimationName(TimelineCharacterRecord character, string requestedName)
        {
            string baseName = KimodoRuntimeUtility.SanitizeName(requestedName, "Animation");
            string name = baseName;
            for (int suffix = 1; character.Animations.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
            {
                name = $"{baseName}_{suffix}";
            }
            return name;
        }

        private static TimelineGenerationTrace PrepareGenerationTrace(TimelineSessionRecord session, TimelineCharacterRecord character, double duration)
        {
            if (!session.Characters.Contains(character))
            {
                throw new InvalidOperationException($"Character '{character.Name}' is not in the active scene context.");
            }
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                throw new InvalidOperationException($"Character '{character.Name}' requires a valid humanoid Avatar before generation.");
            }
            RefreshCharacterTimelineAppendPosition(character);
            return new TimelineGenerationTrace(session, character, character.NextStartSeconds, duration);
        }

        // The cached cursor is only a fast path. Timeline clips can be moved or
        // added outside the command view, so reconcile it with the actual track
        // before placing a new generation block.
        private static void RefreshCharacterTimelineAppendPosition(TimelineCharacterRecord character)
        {
            if (character?.Track == null)
            {
                return;
            }

            double occupiedEnd = character.Track.GetClips()
                .Where(clip => clip != null && !double.IsNaN(clip.end) && !double.IsInfinity(clip.end))
                .Select(clip => clip.end)
                .DefaultIfEmpty(double.NegativeInfinity)
                .Max();
            if (double.IsNegativeInfinity(occupiedEnd))
            {
                return;
            }

            character.NextStartSeconds = Math.Max(
                character.NextStartSeconds,
                occupiedEnd + ClipSafeZoneSeconds);
        }

        private static KimodoPlayableClip CreateGenerationPlayableClip(
            TimelineGenerationTrace trace,
            string prompt,
            bool useExplicitInOut)
        {
            if (trace?.Session == null || trace.Character == null || trace.Character.Track == null)
            {
                throw new InvalidOperationException("Timeline generation target is incomplete.");
            }

            TimelineAsset timelineAsset = trace.Session.TimelineAsset;
            if (timelineAsset == null || trace.Character.Track.timelineAsset != timelineAsset ||
                trace.Session.Director == null || trace.Character.Animator == null ||
                !BindingMatches(trace.Session.Director.GetGenericBinding(trace.Character.Track), trace.Character.Animator))
            {
                throw new InvalidOperationException("Scene context target is no longer valid.");
            }

            Undo.RegisterCompleteObjectUndo(
                new UnityEngine.Object[] { timelineAsset, trace.Character.Track, trace.Session.Director },
                "Kimodo Add Generation Clip");
            TimelineClip timelineClip = trace.Character.Track.CreateClip<KimodoPlayableClip>();
            timelineClip.start = trace.StartSeconds;
            timelineClip.duration = trace.DurationSeconds;
            timelineClip.displayName = MakeUniqueAnimationName(
                trace.Character,
                string.IsNullOrWhiteSpace(prompt) ? "Kimodo Generation" : prompt.Trim());

            KimodoPlayableClip playableClip = timelineClip.asset as KimodoPlayableClip;
            if (playableClip == null)
            {
                throw new InvalidOperationException("Timeline could not create a KimodoPlayableClip.");
            }
            playableClip.name = timelineClip.displayName;
            // Command In/Out windows default to trajectory alignment; explicit
            // inout.post_apply values can override each side.
            playableClip.postApplyOutConstraint = true;
            if (useExplicitInOut)
            {
                // Explicit sources own the boundaries; retain point markers
                // without inferring extra neighbors from Session layout.
                playableClip.enableInConstraint = playableClip.enableOutConstraint = false;
            }
            trace.TimelineClip = timelineClip;
            trace.PlayableClip = playableClip;
            trace.Animation = new TimelineAnimationRecord(
                GetTimelineAnimationId(timelineClip),
                timelineClip.displayName,
                "generated",
                null,
                timelineClip,
                null,
                null,
                0,
                0);
            trace.Character.Animations.Add(trace.Animation);
            EditorUtility.SetDirty(playableClip);
            EditorUtility.SetDirty(trace.Character.Track);
            EditorUtility.SetDirty(timelineAsset);
            return playableClip;
        }

        private static void WriteGenerationConstraintMarkers(
            TimelineGenerationTrace trace,
            IReadOnlyList<KimodoMarkerSampleResult> samples,
            float frameRate)
        {
            if (trace?.Character?.Track == null || samples == null || samples.Count == 0)
            {
                return;
            }

            double lastSampleTime = Math.Max(0.0, trace.DurationSeconds - 1.0 / Math.Max(1f, frameRate));
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample == null)
                {
                    continue;
                }

                double localTime = Math.Max(0.0, Math.Min(lastSampleTime, sample.sampleTime));
                double markerTime = trace.StartSeconds + localTime;
                int markerFrame = Mathf.RoundToInt((float)(markerTime * frameRate));
                KimodoConstraintMarker marker = trace.Character.Track.GetMarkers()
                    .OfType<KimodoConstraintMarker>()
                    .FirstOrDefault(existing =>
                        existing.ParticipatesInGeneration &&
                        Mathf.RoundToInt((float)(existing.time * frameRate)) == markerFrame);
                bool createdMarker = marker == null;
                if (marker != null)
                {
                    Debug.LogWarning($"[Kimodo] Constraint already exists at frame {markerFrame}; updating the existing marker.");
                    Selection.activeObject = marker;
                }
                else
                {
                    marker = trace.Character.Track.CreateMarker<KimodoConstraintMarker>(markerTime);
                }

                KimodoMarkerSampleResult markerSample = sample.Clone();
                marker.SampleData = markerSample;
                if (createdMarker)
                {
                    marker.name = MakeUniqueConstraintPoseSource(trace.Session, $"{trace.Character.Name}.Constraint");
                }
                marker.autoSample = false;
                marker.constraintEnabled = true;
            }

            EditorUtility.SetDirty(trace.Character.Track);
        }

        private static string MakeUniqueConstraintPoseSource(TimelineSessionRecord session, string requestedName)
        {
            var names = new HashSet<string>(session.Characters
                .SelectMany(character => character.Track.GetMarkers().OfType<KimodoConstraintMarker>()
                    .Where(marker => marker.ParticipatesInGeneration))
                .Select(marker => marker.name), StringComparer.OrdinalIgnoreCase);
            string name = requestedName;
            for (int suffix = 1; names.Contains(name); suffix++) name = $"{requestedName}_{suffix}";
            return name;
        }


        private static void ReserveGenerationTimelineRange(TimelineGenerationTrace trace)
        {
            if (trace == null)
            {
                return;
            }

            if (!IsSceneContextBound(trace.Session))
                throw new InvalidOperationException("Scene context was closed before generation could be started.");
            double reservedEnd = trace.StartSeconds + trace.DurationSeconds;
            foreach (CommandInOutEvidencePlan output in trace.InOutEvidencePlans.Where(item => !item.IsIn))
            {
                reservedEnd = Math.Max(
                    reservedEnd,
                    trace.StartSeconds + trace.DurationSeconds + output.Window.SourceWindowDurationSeconds);
            }
            trace.Character.NextStartSeconds = reservedEnd + ClipSafeZoneSeconds;
        }

        private static bool IsSceneContextBound(TimelineSessionRecord session)
        {
            GameObject root = session?.SessionRoot;
            if (root == null || !root.scene.IsValid() || !root.scene.isLoaded || root.scene != SceneManager.GetActiveScene())
                return false;
            GameObject[] roots = GetSceneSessionRoots(root.scene);
            return roots.Length == 1 && roots[0] == root &&
                session.TimelineAsset != null && GetTimelineAssetGuid(session.TimelineAsset) == session.Id;
        }

        internal static Guid GetTimelineAssetGuid(TimelineAsset timeline)
        {
            if (timeline == null) return Guid.Empty;
            string assetPath = AssetDatabase.GetAssetPath(timeline);
            string guidText = string.IsNullOrWhiteSpace(assetPath) ? string.Empty : AssetDatabase.AssetPathToGUID(assetPath);
            if (!Guid.TryParse(guidText, out Guid guid))
                throw new InvalidOperationException($"Timeline '{timeline.name}' must be saved as a Unity asset before it can identify a Session.");
            return guid;
        }

        private static void FinalizePlayableClipTrace(TimelineGenerationTrace trace, KimodoEditorGenerationResult result)
        {
            if (trace?.Session == null || trace.Character == null || trace.TimelineClip == null || trace.Animation == null)
            {
                throw new InvalidOperationException("Timeline generation trace is incomplete.");
            }

            TimelineAsset timelineAsset = trace.Session.TimelineAsset;
            JObject analysis = ParseAnalysisObject(result.AnalysisJson);
            trace.PlayableClip.clip = result.GeneratedClip;
            trace.Animation.ApplyResult(result.GeneratedClip, analysis, result.MotionBytes, result.StartFrame, result.EndFrameExclusive);

            JArray keyframes = analysis?["keyframes"] as JArray ?? new JArray();
            if (keyframes.Count > 0)
            {
                TrackAsset markerTrack = trace.Character.PoseCacheTrack ?? trace.Character.Track;
                WriteAnalysisMarkers(markerTrack, trace, keyframes);
                EditorUtility.SetDirty(markerTrack);
            }

            EditorUtility.SetDirty(trace.PlayableClip);
            EditorUtility.SetDirty(trace.Character.Track);
            EditorUtility.SetDirty(timelineAsset);
            EditorUtility.SetDirty(trace.Session.Director);
            AssetDatabase.SaveAssets();
        }

        private static JObject ParseAnalysisObject(string analysisJson)
        {
            try
            {
                return string.IsNullOrWhiteSpace(analysisJson) ? new JObject() : JObject.Parse(analysisJson);
            }
            catch
            {
                return new JObject { ["warnings"] = new JArray("Returned analysis metadata could not be parsed.") };
            }
        }

        private static void WriteAnalysisMarkers(TrackAsset track, TimelineGenerationTrace trace, JArray keyframes)
        {
            if (track == null || trace == null || keyframes == null)
            {
                return;
            }
            string sourceClipKey = trace.PlayableClip != null
                ? GlobalObjectId.GetGlobalObjectIdSlow(trace.PlayableClip).ToString()
                : string.Empty;
            foreach (KimodoAnalysisKeyframeMarker old in track.GetMarkers().OfType<KimodoAnalysisKeyframeMarker>()
                .Where(marker => string.IsNullOrEmpty(sourceClipKey) || marker.sourceClipKey == sourceClipKey).ToArray())
            {
                track.DeleteMarker(old);
            }
            foreach (JToken keyframe in keyframes)
            {
                double localTime = keyframe.Value<double?>("local_time_seconds")
                    ?? keyframe.Value<double?>("time")
                    ?? (keyframe.Value<int?>("local_frame_60") ?? keyframe.Value<int?>("frame") ?? 0) / SessionFrameRate;
                localTime = Math.Max(0.0, Math.Min(trace.DurationSeconds, localTime));
                KimodoAnalysisKeyframeMarker marker = track.CreateMarker<KimodoAnalysisKeyframeMarker>(trace.StartSeconds + localTime);
                marker.frame = keyframe.Value<int?>("frame") ?? 0;
                marker.eventKind = "keyframe";
                float saliency = keyframe.Value<float?>("saliency") ?? keyframe.Value<float?>("score") ?? 0f;
                string reasons = string.Join(", ", (keyframe["reasons"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>());
                marker.message = $"Keyframe | frame={marker.frame} | saliency={saliency:F2}" +
                    (string.IsNullOrWhiteSpace(reasons) ? string.Empty : $" | {reasons}");
                marker.color = Color.yellow;
                marker.sourceClipKey = sourceClipKey;
                marker.sourceRole = "A";
                marker.MarkerType = KimodoConstraintMarkerType.Analysis;
                marker.autoSample = false;
                marker.constraintEnabled = true;
            }
        }

        private static TimelineSessionRecord RequireCurrentTimelineSession() => GetOrCreateSession();

        internal static TimelineSessionRecord GetOrCreateSession()
        {
            EnsureEditorIsIdle("Kimodo Session commands");
            Scene scene = SceneManager.GetActiveScene();
            RequireSingleSceneSession(scene);
            TimelineSessionRecord session = BuildTimelineSessionView(scene);
            if (!IsTimelineSessionValid(session))
            {
                if (GetSceneSessionRoots(scene).Length != 0)
                    throw new CommandException("invalid_scene_context", "The active scene Session root has no usable Timeline. Repair it or run session.clear before retrying.");
                EnsureCanManageServer();
                session = CreateTimelineSession("AutoContext");
                Animator activeAnimator = ResolveAutomaticSceneCharacter();
                if (activeAnimator != null && !AddCharacterTrack(
                        session, activeAnimator.gameObject, activeAnimator, true, out string error, requireAvatar: true))
                    Debug.LogWarning($"[Kimodo] Skipped scene character '{activeAnimator.name}': {error}");
            }
            ActivateTimelineSession(session);
            EnsureSessionHistoryHeaderIfMissing(session);
            return session;
        }

        private static bool IsTimelineSessionValid(TimelineSessionRecord session)
        {
            if (session == null || session.Director == null || session.TimelineAsset == null ||
                session.Director.gameObject == null || !session.Director.gameObject.scene.IsValid() ||
                session.SessionRoot == null || !session.SessionRoot.scene.IsValid())
            {
                return false;
            }

            return session.Characters.All(character => character != null && character.Root != null &&
                character.Root.scene.IsValid() && character.Track != null && character.PoseCacheTrack != null);
        }

        internal static TimelineSessionRecord BuildTimelineSessionView(Scene scene)
        {
            GameObject root = GetSceneSessionRoots(scene).SingleOrDefault();
            if (root == null) return null;
            PlayableDirector director = root.GetComponent<PlayableDirector>();
            TimelineAsset timeline = director?.playableAsset as TimelineAsset;
            if (director == null || timeline == null) return null;
            string assetPath = AssetDatabase.GetAssetPath(timeline);
            string name = root.name.StartsWith(KimodoRuntimeUtility.SessionRootPrefix, StringComparison.Ordinal)
                ? root.name.Substring(KimodoRuntimeUtility.SessionRootPrefix.Length)
                : KimodoRuntimeUtility.SanitizeName(timeline.name, "Session");
            Guid id = GetTimelineAssetGuid(timeline);
            var session = new TimelineSessionRecord(
                id, name, director, timeline, assetPath, root);
            RestoreSessionCreationTime(session);
            foreach (AnimationTrack track in timeline.GetRootTracks().OfType<AnimationTrack>())
            {
                Animator animator = director.GetGenericBinding(track) as Animator;
                if (animator == null) continue;
                GameObject characterRoot = animator.transform.root.gameObject;
                KimodoLocalAvatarUtility.AvatarResolveResult avatarResult =
                    KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(characterRoot, allowGeneration: false);
                AnimationTrack poseTrack = track.GetChildTracks().OfType<AnimationTrack>().FirstOrDefault() ?? track;
                var character = new TimelineCharacterRecord(
                    GetObjectReference(characterRoot), characterRoot, animator, avatarResult.Avatar, track, poseTrack, avatarResult.Error);
                PopulateTimelineCharacterAnimations(character);
                session.Characters.Add(character);
            }
            return session;
        }

        private static void PopulateTimelineCharacterAnimations(TimelineCharacterRecord character)
        {
            if (character?.Track == null) return;
            foreach (TimelineClip timelineClip in character.Track.GetClips())
            {
                if (timelineClip == null || double.IsNaN(timelineClip.end) || double.IsInfinity(timelineClip.end))
                    continue;
                // A canceled generation still occupies its reserved Timeline range.
                // Keep it as an attempt record, but never place a retry on top of it.
                character.NextStartSeconds = Math.Max(character.NextStartSeconds, timelineClip.end + ClipSafeZoneSeconds);
                AnimationPlayableAsset playable = timelineClip.asset as AnimationPlayableAsset;
                if (playable?.clip == null) continue;
                int startFrame = Mathf.RoundToInt((float)(timelineClip.start * SessionFrameRate));
                int endFrame = startFrame + Mathf.Max(1, Mathf.RoundToInt((float)(timelineClip.duration * SessionFrameRate)));
                character.Animations.Add(new TimelineAnimationRecord(
                    GetTimelineAnimationId(timelineClip), playable.clip.name, "scene", playable.clip, timelineClip, null, null, startFrame, endFrame));
            }
        }

        private static Guid GetTimelineAnimationId(TimelineClip timelineClip)
        {
            UnityEngine.Object asset = timelineClip?.asset;
            if (asset != null && EditorUtility.IsPersistent(asset))
            {
                string globalId = GlobalObjectId.GetGlobalObjectIdSlow(asset).ToString();
                if (!string.IsNullOrWhiteSpace(globalId) &&
                    Guid.TryParseExact(Hash128.Compute(globalId).ToString(), "N", out Guid stableId) &&
                    stableId != Guid.Empty)
                {
                    return stableId;
                }
            }
            // Transient test/editor Timelines still need one stable ID for the
            // lifetime of their clip so a projection refresh cannot orphan its
            // analysis index. Persistent assets use GlobalObjectId above.
            string transientId = asset == null
                ? $"timeline-clip:{timelineClip?.GetHashCode() ?? 0}"
                : $"timeline-clip:{RuntimeHelpers.GetHashCode(asset)}";
            return Guid.TryParseExact(Hash128.Compute(transientId).ToString(), "N", out Guid generatedId) &&
                generatedId != Guid.Empty
                ? generatedId
                : Guid.NewGuid();
        }

        private static void ClearRuntimeSession(TimelineSessionRecord session)
        {
            if (session == null) return;
            KimodoTimelinePreviewRefreshUtility.DisablePreview(session.Director);
            if (session.Director != null) session.Director.Stop();
            if (session.SessionRoot != null)
            {
                session.SessionRoot.SetActive(false);
            }
        }

        internal static string ClearSession(string argumentsJson) => Execute(argumentsJson, ClearSession);

        private static string ClearSession(JObject arguments)
        {
                EnsureEditorIsIdle("Kimodo Session clearing");
                Scene scene = SceneManager.GetActiveScene();
                TimelineSessionRecord session = BuildTimelineSessionView(scene);
                if (session != null) CancelTimelineSessionGenerations(session, "Session cleared by session.clear.");
                if (session != null) KimodoTimelinePreviewRefreshUtility.DisablePreview(session.Director);
                int removedCount = DestroySceneSessionRoots(scene);
                if (session != null)
                {
                    if (session.Director != null) session.Director.Stop();
                }
                Debug.LogWarning($"[Kimodo] Cleared runtime context in scene '{scene.name}'; destroyed {removedCount} context object(s). Persisted Timeline and animation assets were retained.");
                return Ok(new JObject
                {
                    ["cleared"] = removedCount > 0 || session != null,
                    ["name"] = session != null ? session.Name : "__AutoContext",
                    ["destroyed_context_count"] = removedCount,
                    ["recreated_on_next_command"] = true
                });
        }

        private static TimelineSessionRecord RequireTimelineSession(JObject arguments)
        {
            return RequireCurrentTimelineSession();
        }

        private static void CancelTimelineSessionGenerations(TimelineSessionRecord session, string reason)
        {
            if (session == null) return;
            Guid[] requests;
            lock (JobsLock)
            {
                requests = Jobs.Values
                    .Where(record => record.Session.IsRunning && record.TimelineGenerationTrace != null &&
                        record.TimelineGenerationTrace.Session.Id == session.Id)
                    .Select(record => record.Session.RequestId)
                    .ToArray();
            }
            foreach (Guid requestId in requests)
            {
                KimodoEditorGenerationJobService.Cancel(requestId, reason);
            }
        }

        private static TimelineCharacterRecord ResolveSessionCharacter(
            TimelineSessionRecord session,
            GameObject root,
            string name)
        {
            if (session == null)
            {
                return null;
            }
            string reference = root != null ? GetObjectReference(root) : string.Empty;
            // Session clones carry a null GlobalObjectId. Match the cached
            // record through its source id and context-local character name.
            TimelineCharacterRecord match = !string.IsNullOrWhiteSpace(reference)
                ? session.Characters.FirstOrDefault(character =>
                        string.Equals(character.CharacterRef, reference, StringComparison.Ordinal) &&
                        string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? session.Characters.FirstOrDefault(character => character.CharacterRef == reference)
                    ?? session.Characters.FirstOrDefault(character => character.Root == root)
                : session.Characters.FirstOrDefault(character =>
                    !string.IsNullOrWhiteSpace(name) &&
                    string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase));
            return match;
        }

        public static string RetargetAnimation(string argumentsJson) => Execute(argumentsJson, RetargetAnimation);

        private static string RetargetAnimation(JObject arguments)
        {
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                TimelineCharacterRecord source = ResolveSessionCharacterByReference(
                    session, RequiredStringValue(arguments, "source_character"), addIfMissing: true);
                TimelineAnimationRecord sourceAnimation = ResolveAnimation(arguments, source);
                TimelineCharacterRecord target = ResolveSessionCharacterByReference(
                    session,
                    RequiredStringValue(arguments, "target_character"),
                    addIfMissing: true);
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(source.Avatar) ||
                    !KimodoRetargetCoreUtility.IsValidHumanoid(target.Avatar))
                {
                    throw new InvalidOperationException("Retarget requires valid humanoid source and target Avatars.");
                }

                AnimationClip output = null;
                try
                {
                    JObject outputOptions = arguments["output"] as JObject;
                    string assetName = outputOptions?.Value<string>("name")?.Trim();
                    if (string.IsNullOrWhiteSpace(assetName))
                    {
                        assetName = $"{sourceAnimation.Name}_To_{target.Name}";
                    }
                    output = KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(
                        assetName,
                        KimodoEditorOutputPathUtility.NormalizeOutputFolder(outputOptions?.Value<string>("folder")));
                    KimodoEditorClipUtility.CopyClipData(sourceAnimation.Clip, output);
                    AnimationClip providedHumanoidClip = sourceAnimation.Clip.isHumanMotion
                        ? sourceAnimation.Clip
                        : null;
                    if (!KimodoRetargetCoreUtility.TryRetargetClip(
                            output,
                            source.Avatar,
                            target.Avatar,
                            exportMuscleClip: false,
                            providedSourceHumanoidClip: providedHumanoidClip,
                            out AnimationClip retargeted,
                            out string error,
                            debugLog: KimodoPlayableClipGenerationSettings.DebugLog))
                    {
                        throw new InvalidOperationException($"Retarget failed: {error}");
                    }
                    output = retargeted;
                    EditorUtility.SetDirty(output);
                    TimelineAnimationRecord animation = AppendAnimationClip(session, target, output, "retargeted", null);
                    SaveTimelineSession(session);
                    return Ok(new JObject
                    {
                        ["retargeted"] = true,
                        ["source_character"] = source.Name,
                        ["character"] = target.Name,
                        ["animation"] = DescribeAnimation(animation)
                    });
                }
                catch
                {
                    string outputPath = output != null ? AssetDatabase.GetAssetPath(output) : string.Empty;
                    if (!string.IsNullOrWhiteSpace(outputPath))
                    {
                        AssetDatabase.DeleteAsset(outputPath);
                        AssetDatabase.SaveAssets();
                    }
                    throw;
                }
        }

        private static TimelineAnimationRecord ResolveAnimation(JObject arguments, TimelineCharacterRecord character)
        {
            string name = RequiredStringValue(arguments, "animation");
            TimelineAnimationRecord animation = character.Animations.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (animation == null)
            {
                throw new InvalidOperationException($"Animation '{name}' is not loaded for character '{character.Name}'.");
            }
            return animation;
        }

        private static TimelineCharacterRecord ResolveSessionCharacterByReference(
            TimelineSessionRecord session,
            string reference,
            bool addIfMissing = false)
        {
            if (IsLegacyPreviewCharacterReference(reference))
            {
                throw new InvalidOperationException($"Character reference '{reference}' identifies a session preview object. Use the source scene character name or object_ref instead.");
            }
            TimelineCharacterRecord match = session.Characters.FirstOrDefault(character =>
                string.Equals(character.CharacterRef, reference, StringComparison.Ordinal));
            if (match == null && addIfMissing)
            {
                UnityEngine.Object resolved = ResolveObject(reference);
                if (resolved == null)
                {
                    GameObject[] named = ResolveRequestedSceneCharacters(reference);
                    if (named.Length > 1)
                    {
                        string paths = string.Join(", ", named.Select(GetSceneHierarchyPath));
                        throw new InvalidOperationException(
                            $"Character name '{reference}' is ambiguous in the active scene ({paths}); use a hierarchy path or object_ref.");
                    }
                    if (named.Length == 1) resolved = named[0];
                }
                GameObject root = resolved as GameObject ?? (resolved as Animator)?.gameObject;
                Animator animator = root != null ? root.GetComponentInChildren<Animator>(true) : null;
                root = ResolveCharacterIdentityRoot(root, animator);
                if (root != null && (!root.scene.IsValid() || root.scene != SceneManager.GetActiveScene() || EditorUtility.IsPersistent(root)))
                {
                    throw new InvalidOperationException(
                        $"Character reference '{reference}' must identify a scene Humanoid Animator in the active scene '{SceneManager.GetActiveScene().name}'.");
                }
                string sourceId = root != null ? GetObjectReference(root) : string.Empty;
                match = session.Characters.FirstOrDefault(character =>
                    string.Equals(character.CharacterRef, sourceId, StringComparison.Ordinal));
                string error = string.Empty;
                bool added = match != null || root != null && root.scene.IsValid() && !EditorUtility.IsPersistent(root) &&
                    AddCharacterTrack(session, root, animator, true, out error, requireAvatar: true);
                if (added)
                {
                    match ??= session.Characters.FirstOrDefault(character =>
                        string.Equals(character.CharacterRef, sourceId, StringComparison.Ordinal))
                        ?? session.Characters.FirstOrDefault(character =>
                            character.Root != null && character.Root.transform.IsChildOf(session.SessionRoot.transform) &&
                            string.Equals(character.Name, root.name, StringComparison.OrdinalIgnoreCase));
                }
                else if (root != null)
                {
                    throw new InvalidOperationException($"Could not create a target AnimationTrack: {error}");
                }
            }
            if (match == null)
            {
                match = session.Characters.FirstOrDefault(character =>
                    string.Equals(character.Name, reference, StringComparison.OrdinalIgnoreCase));
            }
            if (match == null) throw new InvalidOperationException($"Character '{reference}' is not available in the active scene.");
            return match;
        }

        private static bool IsLegacyPreviewCharacterReference(string reference)
        {
            return !string.IsNullOrWhiteSpace(reference) &&
                (reference.IndexOf(KimodoRuntimeUtility.SessionRootPrefix, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 reference.IndexOf(KimodoRuntimeUtility.SessionDirectorPrefix, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 reference.StartsWith("ghost:", StringComparison.OrdinalIgnoreCase));
        }

        private static GameObject ResolveSourceCharacterRoot(TimelineCharacterRecord character)
        {
            if (character == null) return null;
            UnityEngine.Object source = ResolveObject(character.CharacterRef);
            return source as GameObject ?? (source as Animator)?.gameObject ?? character.Root;
        }


        private static string SessionContextGet(string argumentsJson) => Execute(argumentsJson, SessionContextGet);

        private static string SessionHistoryGet(string argumentsJson) => Execute(argumentsJson, SessionHistoryGet);

        private static string SessionHistoryGet(JObject arguments) => ReadSessionHistory(arguments);

        private static string SessionContextGet(JObject arguments)
        {
            EnsureEditorIsIdle("session.context");
            string timelinePath = arguments.Value<string>("timeline_path");
            JObject openedTimeline = string.IsNullOrWhiteSpace(timelinePath)
                ? null
                : OpenTimelineFromSessionContext(timelinePath);
            Scene scene = SceneManager.GetActiveScene();
            RequireSingleSceneSession(scene);
            TimelineSessionRecord session = BuildTimelineSessionView(scene);
            if (!IsTimelineSessionValid(session))
            {
                if (GetSceneSessionRoots(scene).Length != 0)
                    return Ok(BuildInvalidSessionContext(scene));
                session = GetOrCreateSession();
            }
            else
            {
                EnsureSessionHistoryHeaderIfMissing(session);
            }

            int activeTaskCount = CountActiveSessionTasks(session);
            int clipCount = CountTimelineAnimationClips(session);
            var result = new JObject
            {
                ["session"] = new JObject
                {
                    ["session_id"] = session.Id.ToString("D"),
                    ["name"] = session.Name,
                    ["created_at_utc"] = session.CreatedAtUtc,
                    ["status"] = activeTaskCount > 0 ? "running" : "ready",
                    ["clip_count"] = clipCount,
                    ["active_task_count"] = activeTaskCount
                },
                ["scene"] = new JObject
                {
                    ["name"] = scene.IsValid() ? scene.name : string.Empty,
                    ["path"] = scene.IsValid() ? scene.path : string.Empty
                },
                ["timeline"] = new JObject
                {
                    ["name"] = session.TimelineAsset != null ? session.TimelineAsset.name : string.Empty,
                    ["path"] = session.TimelineAssetPath ?? string.Empty
                }
            };
            JToken detailsToken = arguments["details"];
            if (detailsToken != null && detailsToken.Type != JTokenType.Array)
                throw new CommandException("invalid_argument", "details must be an array containing characters, clips, timeline, and/or tasks.");
            JArray requestedDetails = detailsToken as JArray;
            if (requestedDetails != null && requestedDetails.Count > 0)
            {
                var details = new JObject();
                foreach (JToken detailToken in requestedDetails)
                {
                    if (detailToken.Type != JTokenType.String)
                        throw new CommandException("invalid_argument", "Each details entry must be one of: characters, clips, timeline, tasks.");
                    string detail = detailToken.Value<string>();
                    if (detail != "characters" && detail != "clips" && detail != "timeline" && detail != "tasks")
                        throw new CommandException("invalid_argument", $"Unknown details group '{detail}'. Use characters, clips, timeline, or tasks.");
                    if (details.Property(detail) != null) continue;
                    switch (detail)
                    {
                        case "characters": details[detail] = BuildSessionCharacters(session); break;
                        case "clips": details[detail] = BuildSessionClips(session); break;
                        case "timeline": details[detail] = BuildSessionTimelineDetails(session); break;
                        case "tasks": details[detail] = BuildActiveSessionTasks(session); break;
                    }
                }
                result["details"] = details;
            }
            if (openedTimeline != null) result["opened_timeline"] = openedTimeline;
            return Ok(result);
        }

        private static JObject BuildInvalidSessionContext(Scene scene)
        {
            GameObject root = GetSceneSessionRoots(scene).SingleOrDefault();
            return new JObject
            {
                ["session"] = new JObject
                {
                    ["session_id"] = string.Empty,
                    ["name"] = root != null ? root.name : string.Empty,
                    ["created_at_utc"] = string.Empty,
                    ["status"] = "invalid",
                    ["clip_count"] = 0,
                    ["active_task_count"] = 0
                },
                ["scene"] = new JObject { ["name"] = scene.name, ["path"] = scene.path },
                ["timeline"] = new JObject { ["name"] = string.Empty, ["path"] = string.Empty }
            };
        }

        private static JArray BuildSessionCharacters(TimelineSessionRecord session) => new JArray(
            session.Characters.Where(IsIndexableSessionCharacter).Select(character =>
            {
                GameObject sourceRoot = ResolveSourceCharacterRoot(character);
                Animator sourceAnimator = sourceRoot != null ? sourceRoot.GetComponentInChildren<Animator>(true) : null;
                return new JObject
                {
                    ["name"] = character.Name,
                    ["object_ref"] = character.CharacterRef ?? string.Empty,
                    ["scene_object"] = sourceRoot != null ? GetSceneHierarchyPath(sourceRoot) : string.Empty,
                    ["animator"] = sourceAnimator != null ? GetSceneHierarchyPath(sourceAnimator.gameObject) : string.Empty,
                    ["avatar"] = character.Avatar != null ? character.Avatar.name : string.Empty,
                    ["avatar_valid_humanoid"] = KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar)
                };
            }));

        private static JArray BuildSessionClips(TimelineSessionRecord session)
        {
            var clips = new JArray();
            foreach (TimelineCharacterRecord character in session.Characters)
            {
                if (character?.Track == null) continue;
                foreach (TimelineClip timelineClip in character.Track.GetClips())
                {
                    AnimationPlayableAsset playable = timelineClip.asset as AnimationPlayableAsset;
                    if (playable == null) continue;
                    TimelineAnimationRecord animation = character.Animations.FirstOrDefault(item =>
                        item.TimelineClip == timelineClip || item.TimelineSegments.Any(segment => segment.TimelineClip == timelineClip));
                    clips.Add(new JObject
                    {
                        ["character"] = character.Name,
                        ["track"] = character.Track.name,
                        ["animation_id"] = animation != null ? animation.Id.ToString("D") : string.Empty,
                        ["name"] = playable.clip != null ? playable.clip.name : timelineClip.displayName,
                        ["kind"] = timelineClip.asset is KimodoPlayableClip
                            ? "kimodo_clip"
                            : animation?.Kind ?? "timeline_clip",
                        ["asset_type"] = timelineClip.asset != null ? timelineClip.asset.GetType().Name : string.Empty,
                        ["start_timeline_frame_60"] = Mathf.RoundToInt((float)(timelineClip.start * SessionFrameRate)),
                        ["duration_frames"] = Mathf.RoundToInt((float)(timelineClip.duration * SessionFrameRate)),
                        ["asset_path"] = playable.clip != null ? AssetDatabase.GetAssetPath(playable.clip) : string.Empty
                    });
                }
            }
            return clips;
        }

        private static JObject BuildSessionTimelineDetails(TimelineSessionRecord session)
        {
            TimelineAsset timeline = session.TimelineAsset;
            return new JObject
            {
                ["duration_frames_60"] = timeline != null ? Mathf.RoundToInt((float)(timeline.duration * SessionFrameRate)) : 0,
                ["frame_rate"] = timeline != null ? timeline.editorSettings.frameRate : SessionFrameRate,
                ["current_frame_60"] = session.Director != null ? Mathf.RoundToInt((float)(session.Director.time * SessionFrameRate)) : 0,
                ["tracks"] = new JArray(timeline != null
                    ? timeline.GetOutputTracks().Select(track => new JObject
                    {
                        ["name"] = track.name,
                        ["type"] = track.GetType().Name,
                        ["clip_count"] = track is AnimationTrack animationTrack ? animationTrack.GetClips().Count() : 0
                    })
                    : Enumerable.Empty<JObject>())
            };
        }

        private static int CountTimelineAnimationClips(TimelineSessionRecord session) =>
            session.Characters.Where(character => character?.Track != null)
                .Sum(character => character.Track.GetClips().Count());

        private static int CountActiveSessionTasks(TimelineSessionRecord session)
        {
            lock (JobsLock)
                return Jobs.Values.Count(record => record?.Session?.IsRunning == true &&
                    record.TimelineGenerationTrace?.Session.Id == session.Id);
        }

        private static JArray BuildActiveSessionTasks(TimelineSessionRecord session)
        {
            lock (JobsLock)
                return new JArray(Jobs.Values
                    .Where(record => record?.Session?.IsRunning == true && record.TimelineGenerationTrace?.Session.Id == session.Id)
                    .Select(record => new JObject
                    {
                        ["request_id"] = record.Session.RequestId.ToString("D"),
                        ["status"] = record.Session.Status.ToString().ToLowerInvariant(),
                        ["character"] = record.TimelineGenerationTrace.Character?.Name ?? string.Empty,
                        ["animation"] = record.TimelineGenerationTrace.Animation?.Name ?? string.Empty
                    }));
        }

        private static bool IsIndexableSessionCharacter(TimelineCharacterRecord character)
        {
            return character != null && character.Root != null && character.Animator != null &&
                character.Root.scene.IsValid() && !EditorUtility.IsPersistent(character.Root) &&
                KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar) &&
                KimodoLocalAvatarUtility.CheckAvatarValid(character.Avatar, character.Root);
        }

        private static JObject DescribeAnimation(TimelineAnimationRecord animation)
        {
            var result = new JObject
            {
                ["animation_id"] = animation.Id.ToString("D"),
                ["name"] = animation.Name,
                ["source"] = animation.Source,
                ["kind"] = animation.Kind,
                ["start_timeline_frame_60"] = animation.TimelineClip != null ? Mathf.RoundToInt((float)(animation.TimelineStartSeconds * SessionFrameRate)) : 0,
                ["duration_frames"] = animation.TimelineClip != null ? Mathf.RoundToInt((float)(animation.TimelineDurationSeconds * SessionFrameRate)) : 0,
                ["segments"] = new JArray(animation.TimelineSegments.Select(segment => new JObject
                {
                    ["role"] = segment.Role,
                    ["start_timeline_frame_60"] = segment.TimelineClip != null ? Mathf.RoundToInt((float)(segment.TimelineClip.start * SessionFrameRate)) : 0,
                    ["duration_frames"] = segment.TimelineClip != null ? Mathf.RoundToInt((float)(segment.TimelineClip.duration * SessionFrameRate)) : 0
                }))
            };
            if (animation.Transition != null)
            {
                result["transition"] = animation.Transition.DeepClone();
            }
            return result;
        }

        private static JObject DescribeTimelineConstraint(KimodoConstraintMarker marker, int relativeToFrame)
        {
            int globalFrame = Mathf.RoundToInt((float)(marker.time * SessionFrameRate));
            var result = new JObject
            {
                ["frame"] = globalFrame - relativeToFrame,
                ["type"] = marker.ConstraintType
            };
            if (relativeToFrame != 0)
            {
                result["global_frame"] = globalFrame;
            }
            if (marker.ConstraintType == "constraint" &&
                !KimodoConstraintMask.IsActive(marker.SampleData, "muscle") &&
                !KimodoConstraintMask.IsActive(marker.SampleData, "lefthand") &&
                !KimodoConstraintMask.IsActive(marker.SampleData, "righthand") &&
                !KimodoConstraintMask.IsActive(marker.SampleData, "leftfoot") &&
                !KimodoConstraintMask.IsActive(marker.SampleData, "rightfoot"))
            {
                GetRootTransform(marker.SampleData, out Vector3 rootPosition, out Quaternion rootRotation);
                Vector3 forward = rootRotation * Vector3.forward;
                result["position"] = new JArray(
                    rootPosition.x,
                    rootPosition.z);
                result["heading"] = new JArray(forward.x, forward.z);
            }
            else
            {
                result["sample_result"] = SampleResultJson(marker.SampleData);
            }
            return result;
        }



        private static void SaveTimelineSession(TimelineSessionRecord session)
        {
            PersistSessionArtifacts(session);
            EditorUtility.SetDirty(session.TimelineAsset);
            AssetDatabase.SaveAssets();
            session.Director.RebuildGraph();
            KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsAddedOrRemoved);
        }


        internal static bool GenerationRangesOverlap(int firstStart, int firstEnd, int secondStart, int secondEnd) =>
            firstStart < secondEnd && secondStart < firstEnd;

        private static void ThrowIfGenerationRangeLocked(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            int startFrame,
            int endFrame,
            string command)
        {
            if (session == null || character?.Track == null || endFrame <= startFrame)
            {
                return;
            }

            lock (JobsLock)
            {
                foreach (JobRecord record in Jobs.Values)
                {
                    TimelineGenerationTrace trace = record.TimelineGenerationTrace;
                    if (!record.Session.IsRunning || trace == null ||
                        !ReferenceEquals(trace.Session, session) ||
                        !ReferenceEquals(trace.Character?.Track, character.Track))
                    {
                        continue;
                    }

                    int lockedStart = Mathf.RoundToInt((float)(trace.StartSeconds * SessionFrameRate));
                    int lockedEnd = lockedStart + Math.Max(1, Mathf.RoundToInt((float)(trace.DurationSeconds * SessionFrameRate)));
                    if (GenerationRangesOverlap(startFrame, endFrame, lockedStart, lockedEnd))
                    {
                        throw new GenerationRangeLockedException(
                            command,
                            record.Session.RequestId,
                            character.Name,
                            character.Track.name,
                            lockedStart,
                            lockedEnd,
                            startFrame,
                            endFrame);
                    }
                }
            }
        }

        internal sealed class TimelineSessionRecord
        {
            public TimelineSessionRecord(
                Guid id,
                string name,
                PlayableDirector director,
                TimelineAsset timelineAsset,
                string timelineAssetPath,
                GameObject sessionRoot)
            {
                Id = id;
                Name = name;
                Director = director;
                TimelineAsset = timelineAsset;
                TimelineAssetPath = timelineAssetPath;
                SessionRoot = sessionRoot;
                CreatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            }

            public Guid Id { get; }
            public string Name { get; }
            public PlayableDirector Director { get; }
            public TimelineAsset TimelineAsset { get; }
            public string TimelineAssetPath { get; }
            public GameObject SessionRoot { get; }
            public string CreatedAtUtc { get; set; }
            public List<TimelineCharacterRecord> Characters { get; } = new List<TimelineCharacterRecord>();
        }

        internal sealed class TimelineCharacterRecord
        {
            public TimelineCharacterRecord(
                string characterRef,
                GameObject root,
                Animator animator,
                Avatar avatar,
                AnimationTrack track,
                AnimationTrack poseCacheTrack,
                string avatarError)
            {
                CharacterRef = characterRef;
                Root = root;
                Animator = animator;
                Avatar = avatar;
                Track = track;
                PoseCacheTrack = poseCacheTrack;
                AvatarError = avatarError ?? string.Empty;
            }

            public string CharacterRef { get; }
            public GameObject Root { get; }
            public Animator Animator { get; }
            public Avatar Avatar { get; }
            public AnimationTrack Track { get; }
            public AnimationTrack PoseCacheTrack { get; }
            public string AvatarError { get; }
            public double NextStartSeconds { get; set; }
            public List<TimelineAnimationRecord> Animations { get; } = new List<TimelineAnimationRecord>();
            public string Name => Track != null ? Track.name : (Root != null ? Root.name : string.Empty);

        }

        internal sealed class TimelineAnimationRecord
        {
            public TimelineAnimationRecord(
                Guid id,
                string name,
                string source,
                AnimationClip clip,
                TimelineClip timelineClip,
                JObject analysis,
                byte[] kmbBytes,
                int startFrame,
                int endFrameExclusive)
            {
                Id = id;
                fallbackName = name ?? string.Empty;
                Source = source ?? string.Empty;
                Clip = clip;
                TimelineClip = timelineClip;
                Analysis = analysis;
                KmbBytes = kmbBytes;
                StartFrame = startFrame;
                EndFrameExclusive = endFrameExclusive;
                Kind = timelineClip?.asset is KimodoPlayableClip ? "kimodo_clip" : "animation_clip";
                if (timelineClip != null)
                {
                    timelineSegments.Add(new TimelineAnimationSegment("clip", clip, timelineClip));
                }
            }

            public Guid Id { get; }
            private readonly string fallbackName;
            public string Name => fallbackName;
            public string Source { get; }
            public AnimationClip Clip { get; private set; }
            public TimelineClip TimelineClip { get; }
            public string Kind { get; private set; }
            public JObject Transition { get; private set; }
            public IReadOnlyList<TimelineAnimationSegment> TimelineSegments => timelineSegments;
            public JObject Analysis { get; private set; }
            public byte[] KmbBytes { get; private set; }
            public int StartFrame { get; private set; }
            public int EndFrameExclusive { get; private set; }

            private readonly List<TimelineAnimationSegment> timelineSegments = new List<TimelineAnimationSegment>();

            public double TimelineStartSeconds => timelineSegments.Count > 0
                ? timelineSegments.Min(item => item.TimelineClip != null ? item.TimelineClip.start : double.MaxValue)
                : TimelineClip != null ? TimelineClip.start : 0.0;

            public double TimelineEndSeconds => timelineSegments.Count > 0
                ? timelineSegments.Max(item => item.TimelineClip != null ? item.TimelineClip.end : 0.0)
                : TimelineClip != null ? TimelineClip.end : 0.0;

            public double TimelineDurationSeconds => Math.Max(0.0, TimelineEndSeconds - TimelineStartSeconds);

            public void ConfigureComposite(
                string kind,
                IEnumerable<TimelineAnimationSegment> segments,
                JObject transition = null)
            {
                Kind = string.IsNullOrWhiteSpace(kind) ? "animation_clip" : kind;
                timelineSegments.Clear();
                if (segments != null)
                {
                    timelineSegments.AddRange(segments.Where(item => item?.TimelineClip != null));
                }
                if (timelineSegments.Count == 0 && TimelineClip != null)
                {
                    timelineSegments.Add(new TimelineAnimationSegment("clip", Clip, TimelineClip));
                }
                Transition = transition != null ? (JObject)transition.DeepClone() : null;
            }

            public void ApplyResult(
                AnimationClip clip,
                JObject analysis,
                byte[] kmbBytes,
                int startFrame,
                int endFrameExclusive)
            {
                Clip = clip;
                Analysis = analysis;
                KmbBytes = kmbBytes;
                StartFrame = startFrame;
                EndFrameExclusive = endFrameExclusive;
                if (timelineSegments.Count > 0)
                {
                    timelineSegments[0].Clip = clip;
                }
            }

        }

        internal sealed class TimelineAnimationSegment
        {
            public TimelineAnimationSegment(string role, AnimationClip clip, TimelineClip timelineClip)
            {
                Role = role ?? string.Empty;
                Clip = clip;
                TimelineClip = timelineClip;
            }

            public string Role { get; }
            public AnimationClip Clip { get; internal set; }
            public TimelineClip TimelineClip { get; }
        }

        private sealed class TimelineGenerationTrace
        {
            public TimelineGenerationTrace(TimelineSessionRecord session, TimelineCharacterRecord character, double startSeconds, double durationSeconds)
            {
                Session = session;
                Character = character;
                StartSeconds = startSeconds;
                DurationSeconds = durationSeconds;
            }

            public TimelineSessionRecord Session { get; }
            public TimelineCharacterRecord Character { get; }
            public double StartSeconds { get; set; }
            public double DurationSeconds { get; }
            public TimelineClip TimelineClip { get; set; }
            public KimodoPlayableClip PlayableClip { get; set; }
            public JObject InOutSampling { get; set; }
            public List<CommandInOutEvidencePlan> InOutEvidencePlans { get; } = new List<CommandInOutEvidencePlan>();
            public TimelineAnimationRecord Animation { get; set; }
        }

    }
}
