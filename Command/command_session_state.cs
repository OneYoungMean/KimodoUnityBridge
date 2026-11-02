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
        private const string AnalysisContractVersion = "6-command-60-force-line-v1";
        private const string MissingSceneContextWarning =
            "No live scene context was available; a new automatic context was created in the active scene.";
        internal const int SessionCaptureLayer = 17;
        internal const int ClipSafeZoneFrames = 4;
        internal const double ClipSafeZoneSeconds = ClipSafeZoneFrames / 60.0;
        private const string GeneratedTimelineFolder = KimodoEditorClipWritebackService.GeneratedClipFolder + "/Timelines";
        private static TimelineSessionRecord currentTimelineSession;

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
                KimodoSessionRuntimeGuard.DisableContextRoot(session?.SessionRoot);
                return;
            }
            if (session?.SessionRoot != null) session.SessionRoot.SetActive(true);
            if (session?.Director != null) session.Director.enabled = true;
        }

        private static TimelineSessionRecord CreateTimelineSession(string requestedName, bool isAutomatic)
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
                Guid sessionId = Guid.NewGuid();

                GameObject sessionRoot = new GameObject($"KimodoSession_{safeName}");
                sessionRoot.layer = SessionCaptureLayer;
                Undo.RegisterCreatedObjectUndo(sessionRoot, CreateTimelineSessionUndoName);
                KimodoSessionContextBinding binding = sessionRoot.AddComponent<KimodoSessionContextBinding>();
                binding.sessionId = sessionId.ToString("D");
                binding.sessionName = name;
                binding.timelineAssetPath = assetPath;
                binding.isAutomatic = isAutomatic;
                GameObject directorObject = new GameObject($"{KimodoRuntimeUtility.SessionDirectorPrefix}{safeName}");
                directorObject.transform.SetParent(sessionRoot.transform, false);
                PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
                director.playableAsset = timelineAsset;
                director.time = 0.0;

                Undo.RegisterCreatedObjectUndo(directorObject, CreateTimelineSessionUndoName);
                var record = new TimelineSessionRecord(sessionId, name, director, timelineAsset, assetPath, isAutomatic, sessionRoot);
                binding.sessionJsonPath = record.SessionJsonPath;
                PersistTimelineSessionJson(record);
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

        private static TimelineGenerationTrace PrepareGenerationTrace(JObject arguments, TimelineCharacterRecord character, double duration)
        {
            TimelineSessionRecord session = RequireTimelineSession(arguments);
            if (!session.Characters.Contains(character))
            {
                throw new InvalidOperationException($"Character '{character.Name}' is not in the active scene context.");
            }
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                throw new InvalidOperationException($"Character '{character.Name}' requires a valid humanoid Avatar before generation.");
            }
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
            // Command-created clips always retain AutoBegin. Explicit In/Out
            // samples still take precedence during constraint normalization;
            // the synthetic anchor is removed when a real boundary exists.
            playableClip.autoBeginAnchor = true;
            // Command generation keeps the historical two-pass Out alignment
            // default; explicit inout.post_apply values can override each side.
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
            foreach (CommandInOutEvidencePlan plan in trace.InOutEvidencePlans.Where(item => !item.IsIn))
                reservedEnd = Math.Max(reservedEnd, trace.StartSeconds + trace.DurationSeconds + plan.Window.SourceWindowDurationSeconds);
            trace.Character.NextStartSeconds = reservedEnd + ClipSafeZoneSeconds;
        }

        private static bool IsSceneContextBound(TimelineSessionRecord session)
        {
            GameObject root = session?.SessionRoot;
            if (root == null || !root.scene.IsValid() || !root.scene.isLoaded || root.scene != SceneManager.GetActiveScene())
                return false;
            GameObject[] roots = GetSceneSessionRoots(root.scene);
            KimodoSessionContextBinding binding = root.GetComponent<KimodoSessionContextBinding>();
            return roots.Length == 1 && roots[0] == root && binding != null &&
                Guid.TryParse(binding.sessionId, out Guid id) && id == session.Id;
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

            // Update duration_frames to reflect actual generated clip length
            if (result.GeneratedClip != null && analysis != null)
            {
                int actualFrames = Mathf.RoundToInt(result.GeneratedClip.length * result.GeneratedClip.frameRate);
                analysis["duration_frames"] = actualFrames;
            }

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

        private static TimelineSessionRecord RequireCurrentTimelineSession()
        {
            EnsureEditorIsIdle("Kimodo scene Session commands");
            RefreshSceneContextBinding();
            ReadSceneTimelineProjection(SceneManager.GetActiveScene());
            if (currentTimelineSession == null)
            {
                EnsureCanManageServer();
                Scene activeScene = SceneManager.GetActiveScene();
                TimelineSessionRecord session = BindLoadedSceneContext(activeScene);
                if (!IsTimelineSessionValid(session))
                {
                    if (GetSceneSessionRoots(activeScene).Length != 0)
                        throw new CommandException("invalid_scene_context", "The active scene context has no usable Timeline. Repair its serialized Timeline binding or run session.clear before retrying.");
                    session = CreateTimelineSession("__AutoContext", isAutomatic: true);
                }
                currentTimelineSession = session;
                ActivateTimelineSession(currentTimelineSession);
                Animator activeAnimator = ResolveAutomaticSceneCharacter();
                if (currentTimelineSession.Characters.Count == 0 && activeAnimator != null)
                {
                    if (!AddCharacterTrack(
                            currentTimelineSession,
                            activeAnimator.gameObject,
                            activeAnimator,
                            true,
                            out string error,
                            requireAvatar: true))
                    {
                        Debug.LogWarning($"[Kimodo] Skipped scene character '{activeAnimator.name}': {error}");
                    }
                }
                PersistTimelineSessionJson(currentTimelineSession);
            }
            if (!IsTimelineSessionValid(currentTimelineSession))
            {
                TimelineSessionRecord invalidSession = currentTimelineSession;
                CancelTimelineSessionGenerations(invalidSession, "The bound scene context became invalid.");
                ClearRuntimeSession(invalidSession);
                currentTimelineSession = null;
                throw new CommandException("invalid_scene_context", "The active scene context is no longer valid. Repair its serialized Timeline binding or run session.clear before retrying.");
            }
            return currentTimelineSession;
        }

        private static void RefreshSceneContextBinding()
        {
            Scene activeScene = SceneManager.GetActiveScene();
            RequireSingleSceneSession(activeScene);
            GameObject sceneRoot = GetSceneSessionRoots(activeScene).SingleOrDefault();
            if (currentTimelineSession != null &&
                (currentTimelineSession.SessionRoot == null ||
                 currentTimelineSession.SessionRoot.scene != activeScene ||
                 currentTimelineSession.SessionRoot != sceneRoot))
            {
                currentTimelineSession = null;
            }
        }

        private static void ReadSceneTimelineProjection(Scene scene)
        {
            if (currentTimelineSession == null || HasRunningGeneration(currentTimelineSession))
            {
                return;
            }

            TimelineSessionRecord bound = BindLoadedSceneContext(scene);
            if (bound == null) return;
            if (bound.SessionRoot != currentTimelineSession.SessionRoot || bound.Id != currentTimelineSession.Id)
            {
                return;
            }

            if (bound.Name != currentTimelineSession.Name || bound.Director != currentTimelineSession.Director ||
                bound.TimelineAsset != currentTimelineSession.TimelineAsset ||
                bound.TimelineAssetPath != currentTimelineSession.TimelineAssetPath ||
                bound.SessionJsonPath != currentTimelineSession.SessionJsonPath ||
                bound.IsAutomatic != currentTimelineSession.IsAutomatic)
            {
                currentTimelineSession = bound;
                PersistTimelineSessionJson(bound);
                return;
            }

            ReconcileSceneTimelineProjection(currentTimelineSession, bound);
            currentTimelineSession.SessionRevision = bound.SessionRevision;
        }

        private static void ReconcileSceneTimelineProjection(
            TimelineSessionRecord current,
            TimelineSessionRecord sceneProjection)
        {
            var existingCharacters = current.Characters.ToArray();
            var characters = new List<TimelineCharacterRecord>(sceneProjection.Characters.Count);
            foreach (TimelineCharacterRecord projected in sceneProjection.Characters)
            {
                TimelineCharacterRecord existing = existingCharacters.FirstOrDefault(item => item.Track == projected.Track);
                if (existing == null)
                {
                    characters.Add(projected);
                    continue;
                }

                existing.ReadSceneProjection(projected);
                ReconcileSceneAnimations(existing, projected);
                characters.Add(existing);
            }
            current.Characters.Clear();
            current.Characters.AddRange(characters);
        }

        private static void ReconcileSceneAnimations(
            TimelineCharacterRecord current,
            TimelineCharacterRecord sceneProjection)
        {
            var projectedByTimelineClip = sceneProjection.Animations
                .Where(item => item.TimelineClip != null)
                .ToDictionary(item => item.TimelineClip);
            var reconciled = new List<TimelineAnimationRecord>(projectedByTimelineClip.Count);
            var claimedTimelineClips = new HashSet<TimelineClip>();

            foreach (TimelineAnimationRecord existing in current.Animations)
            {
                IReadOnlyList<TimelineAnimationSegment> segments = existing.TimelineSegments;
                if (segments.Count == 0 || segments.Any(segment => segment?.TimelineClip == null ||
                        claimedTimelineClips.Contains(segment.TimelineClip) ||
                        !projectedByTimelineClip.ContainsKey(segment.TimelineClip)) ||
                    !existing.ReadSceneProjection(projectedByTimelineClip))
                {
                    continue;
                }
                foreach (TimelineAnimationSegment segment in segments)
                    claimedTimelineClips.Add(segment.TimelineClip);
                reconciled.Add(existing);
            }

            foreach (TimelineAnimationRecord projected in sceneProjection.Animations)
            {
                if (projected.TimelineClip != null && claimedTimelineClips.Add(projected.TimelineClip))
                    reconciled.Add(projected);
            }

            current.Animations.Clear();
            current.Animations.AddRange(reconciled);
            current.NextStartSeconds = sceneProjection.NextStartSeconds;
        }

        private static bool HasRunningGeneration(TimelineSessionRecord session)
        {
            if (session == null) return false;
            lock (JobsLock)
            {
                return Jobs.Values.Any(record => record?.Session?.IsRunning == true &&
                    record.TimelineGenerationTrace != null &&
                    record.TimelineGenerationTrace.Session.Id == session.Id);
            }
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

        private static TimelineSessionRecord BindLoadedSceneContext(Scene scene)
        {
            GameObject root = GetSceneSessionRoots(scene).SingleOrDefault();
            if (root == null) return null;
            PlayableDirector director = root.GetComponentInChildren<PlayableDirector>(true);
            TimelineAsset timeline = director?.playableAsset as TimelineAsset;
            if (director == null || timeline == null) return null;
            KimodoSessionContextBinding binding = root.GetComponent<KimodoSessionContextBinding>();
            if (binding == null) binding = root.AddComponent<KimodoSessionContextBinding>();
            string assetPath = AssetDatabase.GetAssetPath(timeline);
            string name = string.IsNullOrWhiteSpace(binding?.sessionName)
                ? KimodoRuntimeUtility.SanitizeName(timeline.name, "Session")
                : binding.sessionName;
            Guid id = Guid.TryParse(binding?.sessionId, out Guid parsed) ? parsed : Guid.NewGuid();
            string jsonPath = string.IsNullOrWhiteSpace(binding.sessionJsonPath)
                ? TimelineSessionRecord.BuildSessionJsonPath(id)
                : binding.sessionJsonPath;
            bool bindingChanged = binding.sessionId != id.ToString("D") || binding.sessionName != name ||
                binding.timelineAssetPath != assetPath || binding.sessionJsonPath != jsonPath;
            var session = new TimelineSessionRecord(
                id, name, director, timeline, assetPath, binding.isAutomatic, root, jsonPath, binding.sessionRevision);
            binding.sessionId = id.ToString("D");
            binding.sessionName = name;
            binding.timelineAssetPath = assetPath;
            binding.sessionJsonPath = session.SessionJsonPath;
            if (bindingChanged)
            {
                EditorUtility.SetDirty(binding);
                EditorSceneManager.MarkSceneDirty(scene);
            }
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
                AnimationPlayableAsset playable = timelineClip.asset as AnimationPlayableAsset;
                if (playable?.clip == null) continue;
                int startFrame = Mathf.RoundToInt((float)(timelineClip.start * SessionFrameRate));
                int endFrame = startFrame + Mathf.Max(1, Mathf.RoundToInt((float)(timelineClip.duration * SessionFrameRate)));
                character.Animations.Add(new TimelineAnimationRecord(
                    GetTimelineAnimationId(timelineClip), playable.clip.name, "scene", playable.clip, timelineClip, null, null, startFrame, endFrame));
                character.NextStartSeconds = Math.Max(character.NextStartSeconds, timelineClip.end + ClipSafeZoneSeconds);
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
                TimelineSessionRecord session = currentTimelineSession;
                if (session != null) CancelTimelineSessionGenerations(session, "Session cleared by session.clear.");
                if (session != null) KimodoTimelinePreviewRefreshUtility.DisablePreview(session.Director);
                int removedCount = DestroySceneSessionRoots(scene);
                if (session != null)
                {
                    if (session.Director != null) session.Director.Stop();
                }
                currentTimelineSession = null;
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


        private static string SceneContextGet(string argumentsJson) => Execute(argumentsJson, SceneContextGet);

        private static string SessionHistoryGet(string argumentsJson) => Execute(argumentsJson, SessionHistoryGet);

        private static string SessionHistoryGet(JObject arguments)
        {
            EnsureEditorIsIdle("session.history");
            Scene activeScene = SceneManager.GetActiveScene();
            RequireSingleSceneSession(activeScene);
            GameObject sessionRoot = GetSceneSessionRoots(activeScene).SingleOrDefault();
            KimodoSessionContextBinding binding = sessionRoot != null
                ? sessionRoot.GetComponent<KimodoSessionContextBinding>()
                : null;
            if (binding == null || !Guid.TryParse(binding.sessionId, out Guid sessionId))
            {
                throw new CommandException(
                    "invalid_scene_context",
                    "The active scene has no serialized Session binding. Call scene.context to create or inspect the current context first.");
            }

            string sessionJsonPath = string.IsNullOrWhiteSpace(binding.sessionJsonPath)
                ? TimelineSessionRecord.BuildSessionJsonPath(sessionId)
                : binding.sessionJsonPath;
            var session = new TimelineSessionRecord(
                sessionId,
                binding.sessionName,
                null,
                null,
                binding.timelineAssetPath,
                binding.isAutomatic,
                sessionRoot,
                sessionJsonPath,
                binding.sessionRevision);
            return Ok(new JObject
            {
                ["session"] = new JObject
                {
                    ["session_id"] = session.Id.ToString("D"),
                    ["session_revision"] = session.SessionRevision,
                    ["schema_version"] = SessionJsonSchemaVersion
                },
                ["history"] = new JObject
                {
                    ["generations"] = GetGenerationIndex(session),
                    ["analyses"] = GetAnalysisIndex(session)
                }
            });
        }

        private static string SceneContextGet(JObject arguments)
        {
            EnsureEditorIsIdle("scene.context");
            string timelinePath = arguments.Value<string>("timeline_path");
            JObject openedTimeline = string.IsNullOrWhiteSpace(timelinePath)
                ? null
                : OpenTimelineFromSceneContext(timelinePath);
            RefreshSceneContextBinding();
            ReadSceneTimelineProjection(SceneManager.GetActiveScene());
            TimelineSessionRecord session = currentTimelineSession;
            bool recreatedContext = false;
            if (!IsTimelineSessionValid(session))
            {
                session = RequireCurrentTimelineSession();
                recreatedContext = true;
            }
            PlayableDirector commandDirector = session.Director;
            TimelineAsset timeline = session.TimelineAsset;
            var characters = new JArray();
            foreach (TimelineCharacterRecord character in session.Characters.Where(IsIndexableSessionCharacter))
            {
                Animator animator = character.Animator;
                GameObject sourceRoot = ResolveSourceCharacterRoot(character);
                Animator sourceAnimator = sourceRoot != null ? sourceRoot.GetComponentInChildren<Animator>(true) : null;
                characters.Add(new JObject
                {
                    ["name"] = character.Name,
                    ["role"] = "scene",
                    ["scene_object"] = sourceRoot != null ? GetSceneHierarchyPath(sourceRoot) : string.Empty,
                    ["object_ref"] = character.CharacterRef ?? string.Empty,
                    ["animator"] = sourceAnimator != null ? GetSceneHierarchyPath(sourceAnimator.gameObject) : string.Empty,
                    ["timeline_preview"] = new JObject
                    {
                        ["role"] = "scene",
                        ["scene_object"] = character.Root != null ? GetSceneHierarchyPath(character.Root) : string.Empty,
                        ["animator"] = animator != null ? GetSceneHierarchyPath(animator.gameObject) : string.Empty,
                        ["bound_to_original"] = true
                    },
                    ["avatar"] = character.Avatar != null ? character.Avatar.name : string.Empty,
                    ["avatar_valid_humanoid"] = KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar),
                    ["animations"] = new JArray(character.Animations.Select(DescribeAnimation))
                });
            }
            Scene scene = SceneManager.GetActiveScene();
            PlayableDirector[] authoredDirectors = Resources.FindObjectsOfTypeAll<PlayableDirector>()
                .Where(item => item != null && item.gameObject != null && item.gameObject.scene == scene &&
                    !IsSessionObject(item.gameObject))
                .GroupBy(item => GetObjectReference(item))
                .Select(group => group.First())
                .ToArray();
            var directorDescriptions = new JArray(authoredDirectors.Select(item => new JObject
            {
                ["object_ref"] = GetObjectReference(item),
                ["hierarchy_path"] = GetSceneHierarchyPath(item.gameObject),
                ["name"] = item.name,
                ["enabled"] = item.enabled,
                ["playable_asset"] = item.playableAsset != null ? item.playableAsset.name : string.Empty,
                ["timeline_frame_60"] = Mathf.RoundToInt((float)(item.time * SessionFrameRate)),
                ["duration_frames_60"] = item.playableAsset != null
                    ? Mathf.RoundToInt((float)(item.playableAsset.duration * SessionFrameRate))
                    : 0,
                ["frame_rate"] = item.playableAsset is TimelineAsset authoredTimeline
                    ? authoredTimeline.editorSettings.frameRate
                    : 0.0
            }));
            PlayableDirector activeAuthoredDirector = authoredDirectors.FirstOrDefault(item => item.enabled)
                ?? authoredDirectors.FirstOrDefault();
            var result = new JObject
            {
                ["session"] = new JObject
                {
                    ["session_id"] = session.Id.ToString("D"),
                    ["session_revision"] = session.SessionRevision,
                    ["schema_version"] = SessionJsonSchemaVersion
                },
                ["scene"] = scene.IsValid() ? scene.path : string.Empty,
                ["scene_name"] = scene.IsValid() ? scene.name : string.Empty,
                ["director"] = activeAuthoredDirector != null ? GetSceneHierarchyPath(activeAuthoredDirector.gameObject) : string.Empty,
                ["directors"] = directorDescriptions,
                ["command_context_director"] = commandDirector != null ? GetSceneHierarchyPath(commandDirector.gameObject) : string.Empty,
                ["playable_asset"] = timeline != null ? timeline.name : string.Empty,
                ["playable_asset_path"] = session.TimelineAssetPath ?? string.Empty,
                ["duration_frames_60"] = timeline != null
                    ? Mathf.RoundToInt((float)(timeline.duration * SessionFrameRate))
                    : 0,
                ["frame_rate"] = timeline != null ? timeline.editorSettings.frameRate : SessionFrameRate,
                ["current_frame_60"] = activeAuthoredDirector != null
                    ? Mathf.RoundToInt((float)(activeAuthoredDirector.time * SessionFrameRate))
                    : 0,
                ["characters"] = characters
            };
            if (recreatedContext)
            {
                result["warnings"] = new JArray(MissingSceneContextWarning);
            }
            if (openedTimeline != null)
            {
                result["opened_timeline"] = openedTimeline;
            }
            return Ok(result);
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
            PersistTimelineSessionJson(session);
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

        private sealed class TimelineSessionRecord
        {
            public TimelineSessionRecord(
                Guid id,
                string name,
                PlayableDirector director,
                TimelineAsset timelineAsset,
                string timelineAssetPath,
                bool isAutomatic,
                GameObject sessionRoot,
                string sessionJsonPath = null,
                int sessionRevision = 0)
            {
                Id = id;
                Name = name;
                Director = director;
                TimelineAsset = timelineAsset;
                TimelineAssetPath = timelineAssetPath;
                IsAutomatic = isAutomatic;
                SessionRoot = sessionRoot;
                SessionJsonPath = string.IsNullOrWhiteSpace(sessionJsonPath) ? BuildSessionJsonPath(id) : sessionJsonPath;
                SessionRevision = Math.Max(0, sessionRevision);
            }

            internal static string BuildSessionJsonPath(Guid id) =>
                $"{GeneratedSessionsFolder}/{id:D}/session.json";

            public Guid Id { get; }
            public string Name { get; }
            public PlayableDirector Director { get; }
            public TimelineAsset TimelineAsset { get; }
            public string TimelineAssetPath { get; }
            public bool IsAutomatic { get; }
            public GameObject SessionRoot { get; }
            public string SessionJsonPath { get; }
            public int SessionRevision { get; set; }
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

            public string CharacterRef { get; private set; }
            public GameObject Root { get; private set; }
            public Animator Animator { get; private set; }
            public Avatar Avatar { get; set; }
            public AnimationTrack Track { get; private set; }
            public AnimationTrack PoseCacheTrack { get; private set; }
            public string AvatarError { get; set; }
            public double NextStartSeconds { get; set; }
            public List<TimelineAnimationRecord> Animations { get; } = new List<TimelineAnimationRecord>();
            public string Name => Track != null ? Track.name : (Root != null ? Root.name : string.Empty);

            public void ReadSceneProjection(TimelineCharacterRecord projection)
            {
                CharacterRef = projection.CharacterRef;
                Root = projection.Root;
                Animator = projection.Animator;
                Avatar = projection.Avatar;
                Track = projection.Track;
                PoseCacheTrack = projection.PoseCacheTrack;
                AvatarError = projection.AvatarError;
                NextStartSeconds = projection.NextStartSeconds;
            }
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
                if (timelineClip != null)
                {
                    timelineSegments.Add(new TimelineAnimationSegment("clip", clip, timelineClip));
                }
            }

            public Guid Id { get; }
            private string fallbackName;
            public string Name => fallbackName;
            public string Source { get; }
            public AnimationClip Clip { get; private set; }
            public TimelineClip TimelineClip { get; }
            public string Kind { get; private set; } = "animation_clip";
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

            public bool ReadSceneProjection(
                IReadOnlyDictionary<TimelineClip, TimelineAnimationRecord> projection)
            {
                if (timelineSegments.Count == 0 || timelineSegments.Any(segment =>
                        segment?.TimelineClip == null || !projection.TryGetValue(segment.TimelineClip, out _)))
                    return false;

                foreach (TimelineAnimationSegment segment in timelineSegments)
                    segment.Clip = projection[segment.TimelineClip].Clip;

                if (TimelineClip != null && projection.TryGetValue(TimelineClip, out TimelineAnimationRecord primary))
                {
                    Clip = primary.Clip;
                    fallbackName = primary.Name;
                    if (string.Equals(Source, "scene", StringComparison.Ordinal))
                    {
                        StartFrame = primary.StartFrame;
                        EndFrameExclusive = primary.EndFrameExclusive;
                    }
                }
                return true;
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
