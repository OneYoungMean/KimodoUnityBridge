using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using KimodoBridge;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private static JObject OpenTimelineFromSceneContext(string requestedPath)
        {
            string assetPath = ResolveTimelineAssetPath(requestedPath);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            TimelineAsset timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);
            if (timeline == null)
            {
                throw new InvalidOperationException($"Timeline asset '{requestedPath}' could not be loaded.");
            }

            ClearCurrentTimelineContext();

            Scene scene = SceneManager.GetActiveScene();
            string safeName = KimodoRuntimeUtility.SanitizeName(timeline.name, "Timeline");
            GameObject sessionRoot = new GameObject($"{KimodoRuntimeUtility.SessionRootPrefix}{safeName}")
            {
                layer = SessionCaptureLayer
            };
            Guid sessionId = Guid.NewGuid();
            string sessionName = safeName;
            KimodoSessionContextBinding binding = sessionRoot.AddComponent<KimodoSessionContextBinding>();
            binding.sessionId = sessionId.ToString("D");
            binding.sessionName = sessionName;
            binding.timelineAssetPath = assetPath;
            binding.isAutomatic = false;
            GameObject directorObject = new GameObject($"{KimodoRuntimeUtility.SessionDirectorPrefix}{safeName}");
            directorObject.transform.SetParent(sessionRoot.transform, false);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            director.time = 0.0;
            director.enabled = true;

            var session = new TimelineSessionRecord(
                sessionId, sessionName, director, timeline, assetPath,
                isAutomatic: false, sessionRoot);
            binding.sessionJsonPath = session.SessionJsonPath;

            var result = new JObject
            {
                ["timeline_asset"] = timeline.name,
                ["timeline_asset_path"] = assetPath,
                ["director"] = GetSceneHierarchyPath(directorObject),
                ["bound_characters"] = new JArray(),
                ["warnings"] = new JArray()
            };
            JArray boundCharacters = (JArray)result["bound_characters"];
            JArray warnings = (JArray)result["warnings"];

            Animator[] sceneAnimators = Resources.FindObjectsOfTypeAll<Animator>()
                .Where(animator => animator != null && animator.gameObject != null &&
                    animator.gameObject.scene == scene && !EditorUtility.IsPersistent(animator) &&
                    !IsSessionObject(animator.gameObject))
                .GroupBy(animator => animator.transform.root.gameObject)
                .Select(group => group.First())
                .ToArray();

            foreach (AnimationTrack track in timeline.GetRootTracks().OfType<AnimationTrack>())
            {
                string trackName = track.name?.Trim() ?? string.Empty;
                Animator match = sceneAnimators.FirstOrDefault(animator =>
                    string.Equals(animator.transform.root.name, trackName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(animator.gameObject.name, trackName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    string warning = $"Timeline track '{track.name}' has no same-name scene character; Timeline was opened without a binding.";
                    Debug.LogWarning($"[Kimodo] {warning}");
                    warnings.Add(warning);
                    continue;
                }

                director.SetGenericBinding(track, match);
                AnimationTrack poseTrack = track.GetChildTracks().OfType<AnimationTrack>().FirstOrDefault() ?? track;
                string characterRef = GetObjectReference(match.transform.root.gameObject);
                var character = new TimelineCharacterRecord(
                    characterRef, match.transform.root.gameObject, match, match.avatar, track, poseTrack, string.Empty);
                PopulateTimelineCharacterAnimations(character);
                session.Characters.Add(character);
                boundCharacters.Add(new JObject
                {
                    ["track"] = track.name,
                    ["character"] = match.transform.root.name,
                    ["scene_object"] = GetSceneHierarchyPath(match.transform.root.gameObject),
                    ["animator"] = GetSceneHierarchyPath(match.gameObject)
                });
            }

            currentTimelineSession = session;
            PersistTimelineSessionJson(session);
            ActivateTimelineSession(session);
            OpenTimelineWindow(session.Director);
            return result;
        }

        private static string ResolveTimelineAssetPath(string requestedPath)
        {
            string path = requestedPath?.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("timeline_path is required when opening a Timeline.");
            }

            path = path.Replace('\\', '/');
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/').TrimEnd('/');
            if (Path.IsPathRooted(path))
            {
                string absolute = Path.GetFullPath(path).Replace('\\', '/');
                if (!absolute.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("timeline_path must point to an asset inside the active Unity project.");
                }
                path = absolute.Substring(projectRoot.Length + 1);
            }

            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("timeline_path must be an Assets/... or Packages/... Unity asset path.");
            }
            return path;
        }

        private static void ClearCurrentTimelineContext()
        {
            // Opening another Timeline replaces the current scene binding while
            // retaining all serialized Timeline, Session, generation, and analysis
            // data. The new binding is a complete scene context, not a temporary
            // history object.
            ClearSession(new JObject());
        }

        private static void OpenTimelineWindow(PlayableDirector director)
        {
            if (director == null || director.playableAsset is not TimelineAsset)
            {
                return;
            }

            TimelineEditorWindow window = TimelineEditor.GetOrCreateWindow();
            window.Focus();
            EditorApplication.delayCall += () =>
            {
                if (window == null || director == null || director.playableAsset is not TimelineAsset)
                {
                    return;
                }
                window.SetTimeline(director);
                window.locked = true;
                window.Focus();
                TimelineEditor.Refresh(RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
            };
        }
    }
}
