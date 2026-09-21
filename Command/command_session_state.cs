using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
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
        private const string AnalysisContractVersion = "3-command-60-phase-track-v2";
        private const string TimelineDirectorNamePrefix = "Kimodo_CommandSession_";
        internal const int SessionCaptureLayer = 17;
        internal const int ClipSafeZoneFrames = 4;
        internal const double ClipSafeZoneSeconds = ClipSafeZoneFrames / 60.0;
        private const string GeneratedTimelineFolder = KimodoEditorClipWritebackService.GeneratedClipFolder + "/Timelines";
        private static readonly Dictionary<string, TimelineSessionRecord> TimelineSessions =
            new Dictionary<string, TimelineSessionRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly object TimelineSessionsLock = new object();
        private static TimelineSessionRecord currentTimelineSession;

        private static void ActivateTimelineSession(TimelineSessionRecord session)
        {
            foreach (GameObject root in Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(item => item != null && item.transform.parent == null &&
                    item.name.StartsWith("KimodoSession_", StringComparison.Ordinal)))
            {
                root.SetActive(root == session?.SessionRoot);
            }
            foreach (PlayableDirector director in Resources.FindObjectsOfTypeAll<PlayableDirector>())
            {
                if (director == null || director == session.Director || director.gameObject == null ||
                    !director.gameObject.scene.IsValid() ||
                    !director.name.StartsWith(TimelineDirectorNamePrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                director.Stop();
                director.enabled = false;
            }
            session.Director.enabled = true;
            if (session?.SessionRoot != null) session.SessionRoot.SetActive(true);
        }

        private static TimelineSessionRecord CreateTimelineSession(string requestedName, bool isAutomatic)
        {
            string name = requestedName.Trim();
            if (name.Length == 0)
            {
                throw new InvalidOperationException("name cannot be empty.");
            }
            lock (TimelineSessionsLock)
            {
                if (TimelineSessions.ContainsKey(name))
                {
                    throw new InvalidOperationException($"A Timeline Session named '{name}' already exists.");
                }
            }

            KimodoEditorClipWritebackService.EnsureFolderExists(GeneratedTimelineFolder);
            string safeName = KimodoRuntimeUtility.SanitizeName(name, "Session");
            string assetPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{GeneratedTimelineFolder}/Kimodo_CommandSession_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.playable");
            TimelineAsset timelineAsset = ScriptableObject.CreateInstance<TimelineAsset>();
            timelineAsset.editorSettings.frameRate = SessionFrameRate;
            AssetDatabase.CreateAsset(timelineAsset, assetPath);
            var metadata = ScriptableObject.CreateInstance<KimodoCommandSessionMetadata>();
            metadata.name = "Kimodo Session Metadata";
            metadata.sessionId = Guid.NewGuid().ToString("D");
            metadata.sessionName = name;
            metadata.isAutomatic = isAutomatic;
            AssetDatabase.AddObjectToAsset(metadata, timelineAsset);

            GameObject sessionRoot = new GameObject($"KimodoSession_{safeName}");
            sessionRoot.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            sessionRoot.layer = SessionCaptureLayer;
            GameObject directorObject = new GameObject($"Kimodo_CommandSession_{safeName}");
            directorObject.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            directorObject.transform.SetParent(sessionRoot.transform, false);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timelineAsset;
            director.time = 0.0;

            var record = new TimelineSessionRecord(Guid.Parse(metadata.sessionId), name, director, timelineAsset, assetPath, isAutomatic, metadata, sessionRoot);

            PersistTimelineSessionMetadata(record);
            EditorUtility.SetDirty(timelineAsset);
            EditorUtility.SetDirty(director);
            AssetDatabase.SaveAssets();
            return record;
        }

        private static GameObject CloneCharacterToSession(TimelineSessionRecord session, GameObject source)
        {
            if (source == null || session == null || session.SessionRoot == null || source.transform.IsChildOf(session.SessionRoot.transform))
            {
                return source;
            }
            Vector3 sourceWorldPosition = source.transform.position;
            Quaternion sourceWorldRotation = source.transform.rotation;
            GameObject clone = UnityEngine.Object.Instantiate(source);
            clone.name = source.name;
            clone.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            clone.transform.SetParent(session.SessionRoot.transform, true);
            SetLayerRecursively(clone, SessionCaptureLayer);
            // Preserve components from the source character. In particular, a
            // pre-existing CharacterController is part of the authored rig and
            // must not be removed merely because the clone is used for preview.
            foreach (Animator candidate in clone.GetComponentsInChildren<Animator>(true))
            {
                candidate.runtimeAnimatorController = null;
                candidate.applyRootMotion = false;
                candidate.Rebind();
                candidate.Update(0f);
            }
            // Rebinding must not move the duplicate away from the source's scene pose.
            clone.transform.SetPositionAndRotation(sourceWorldPosition, sourceWorldRotation);
            return clone;
        }

        private static IEnumerable<GameObject> FindSceneMeshObjects()
        {
            return Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(gameObject => gameObject != null && !EditorUtility.IsPersistent(gameObject) &&
                    gameObject.scene.IsValid() && !IsSessionObject(gameObject) && HasRenderableMesh(gameObject))
                .GroupBy(gameObject => KimodoUnityObjectIdUtility.IdHash(gameObject))
                .Select(group => group.First())
                .ToArray();
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
                return new[] { activeAnimator.transform.root.gameObject };
            }

            bool isPath = requestedName.Contains("/");
            return FindSceneMeshObjects()
                .Where(item => isPath
                    ? string.Equals(GetSceneHierarchyPath(item), requestedName, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(item.name, requestedName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private static bool HasRenderableMesh(GameObject root)
        {
            return root != null && root.GetComponentsInChildren<Renderer>(true)
                .Any(renderer => renderer is MeshRenderer || renderer is SkinnedMeshRenderer);
        }

        private static string GetSceneHierarchyPath(GameObject gameObject)
        {
            return gameObject == null
                ? string.Empty
                : string.Join("/", gameObject.transform.GetComponentsInParent<Transform>(true)
                    .Reverse().Select(item => item.name));
        }

        private static bool AddCharacterTrack(
            TimelineSessionRecord session,
            GameObject root,
            Animator animator,
            bool tryGenerateAvatar,
            out string error,
            bool requireAvatar = false)
        {
            error = string.Empty;
            if (session == null || session.TimelineAsset == null || root == null)
            {
                error = "Scene context and character root are required.";
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
            root = CloneCharacterToSession(session, root);
            animator = root != null ? root.GetComponentInChildren<Animator>(true) : null;
            Avatar avatar = null;
            string avatarError = string.Empty;
            if (tryGenerateAvatar)
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

            string characterName = MakeUniqueCharacterName(session, root.name);
            AnimationTrack track = session.TimelineAsset.CreateTrack<AnimationTrack>(null, characterName);
            // Match Timeline's "Apply Scene Offsets" using the clone's scene pose.
            track.trackOffset = TrackOffset.ApplySceneOffsets;
            AnimationTrack poseCacheTrack = session.TimelineAsset.CreateTrack<AnimationTrack>(
                track,
                MakeUniqueSessionObjectName(session, $"{characterName}.Poses"));
            if (animator != null)
            {
                session.Director.SetGenericBinding(track, animator);
                KimodoTimelinePreviewRefreshUtility.InitializeSceneOffset(track, animator);
            }
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
                Guid.NewGuid(), timelineClip.displayName, source, clip, timelineClip, analysis, null, 0, 0);
            character.Animations.Add(animation);
            character.NextStartSeconds = timelineClip.end + ClipSafeZoneSeconds;
            EditorUtility.SetDirty(character.Track);
            return animation;
        }

        private static TimelineClip AppendAnimationTimelineSegment(
            TimelineCharacterRecord character,
            AnimationClip clip,
            double startSeconds,
            double durationSeconds,
            double clipInSeconds,
            string displayName,
            bool loop)
        {
            if (character?.Track == null)
            {
                throw new InvalidOperationException("A character AnimationTrack is required.");
            }
            if (clip == null)
            {
                throw new InvalidOperationException("A source AnimationClip is required.");
            }

            TimelineClip timelineClip = character.Track.CreateClip<AnimationPlayableAsset>();
            timelineClip.start = Math.Max(0.0, startSeconds);
            timelineClip.clipIn = Math.Max(0.0, clipInSeconds);
            timelineClip.duration = Math.Max(1.0 / SessionFrameRate, durationSeconds);
            timelineClip.displayName = displayName ?? clip.name;
            AnimationPlayableAsset animationAsset = (AnimationPlayableAsset)timelineClip.asset;
            animationAsset.clip = clip;
            animationAsset.loop = loop
                ? AnimationPlayableAsset.LoopMode.On
                : AnimationPlayableAsset.LoopMode.UseSourceAsset;
            return timelineClip;
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

        private static TimelineGenerationTrace PrepareGenerationTrace(JObject arguments, ResolvedCharacter character, double duration)
        {
            TimelineSessionRecord session = RequireTimelineSession(arguments);
            TimelineCharacterRecord target = ResolveSessionCharacter(session, character.Root, character.Name)
                ?? throw new InvalidOperationException($"Character '{character.Name}' is not in the resolved scene context. Add it with automatic scene resolution.");
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(target.Avatar))
            {
                throw new InvalidOperationException($"Character '{target.Name}' requires a valid humanoid Avatar before generation.");
            }
            return new TimelineGenerationTrace(session, target, target.NextStartSeconds, duration);
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
            if (useExplicitInOut)
            {
                // Explicit sources own the boundaries; retain point markers
                // without inferring extra neighbors from Session layout.
                playableClip.enableInConstraint = playableClip.enableOutConstraint = false;
                playableClip.autoBeginAnchor = false;
            }
            trace.TimelineClip = timelineClip;
            trace.PlayableClip = playableClip;
            trace.Animation = new TimelineAnimationRecord(
                Guid.NewGuid(),
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

            List<KimodoMarkerSampleResult> unified =
                KimodoConstraintSampleComposer.ComposeCanonicalSamples(samples, frameRate);
            double lastSampleTime = Math.Max(0.0, trace.DurationSeconds - 1.0 / Math.Max(1f, frameRate));
            for (int i = 0; i < unified.Count; i++)
            {
                KimodoMarkerSampleResult sample = unified[i];
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
                // Preserve the canonical mode inferred by the lower
                // composer; command code does not choose a protocol family.
                markerSample.constraintMode = sample.constraintMode;
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

        private static void EnsureConstraintPoseSources(TimelineSessionRecord session)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (TimelineCharacterRecord character in session.Characters)
            foreach (KimodoConstraintMarker marker in character.Track.GetMarkers().OfType<KimodoConstraintMarker>()
                .Where(item => item.constraintEnabled && item.ParticipatesInGeneration))
            {
                string prefix = $"{character.Name}.Constraint";
                if (!string.IsNullOrWhiteSpace(marker.name) &&
                    marker.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && used.Add(marker.name)) continue;
                marker.name = MakeUniqueConstraintPoseSource(session, prefix);
                used.Add(marker.name);
                EditorUtility.SetDirty(marker);
                changed = true;
            }
            if (changed) SaveTimelineSession(session);
        }

        private static void ReserveGenerationTimelineRange(TimelineGenerationTrace trace)
        {
            if (trace == null)
            {
                return;
            }

            lock (TimelineSessionsLock)
            {
                if (!TimelineSessions.ContainsKey(trace.Session.Name) ||
                    !ReferenceEquals(TimelineSessions[trace.Session.Name], trace.Session))
                {
                throw new InvalidOperationException("Scene context was closed before generation could be started.");
                }
                trace.Character.NextStartSeconds = trace.StartSeconds + trace.DurationSeconds + ClipSafeZoneSeconds;
            }
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

        private static string ParseAnalysisOptionsJson(JObject arguments)
        {
            JToken token = arguments?["analysis_option"];
            if (token == null)
            {
                return string.Empty;
            }
            if (token is not JObject options)
            {
                throw new InvalidOperationException("analysis_option must be an object.");
            }
            return options.ToString(Formatting.None);
        }

        private static TimelineSessionRecord RequireCurrentTimelineSession()
        {
            EnsureTimelineSessionsRestored();
            if (currentTimelineSession == null)
            {
                EnsureCanManageServer();
                TimelineSessionRecord automatic;
                lock (TimelineSessionsLock)
                {
                    TimelineSessions.TryGetValue("__AutoContext", out automatic);
                }
                if (automatic == null)
                {
                    automatic = CreateTimelineSession("__AutoContext", isAutomatic: true);
                    lock (TimelineSessionsLock) TimelineSessions[automatic.Name] = automatic;
                }
                currentTimelineSession = automatic;
                ActivateTimelineSession(automatic);
                GameObject active = Selection.activeGameObject;
                if (active == null || active.GetComponentInChildren<Animator>(true) == null)
                    active = SceneManager.GetActiveScene().GetRootGameObjects()
                        .SelectMany(root => root.GetComponentsInChildren<Animator>(true).Select(animator => animator.gameObject))
                        .FirstOrDefault();
                if (automatic.Characters.Count == 0 && active != null)
                {
                    Animator animator = active.GetComponentInParent<Animator>() ?? active.GetComponentInChildren<Animator>(true);
                    if (animator != null && !AddCharacterTrack(
                            automatic,
                            animator.gameObject,
                            animator,
                            true,
                            out string error,
                            requireAvatar: true))
                    {
                        throw new InvalidOperationException(error);
                    }
                }
                PersistTimelineSessionMetadata(automatic);
            }
            if (currentTimelineSession.Director == null || currentTimelineSession.TimelineAsset == null)
            {
                throw new InvalidOperationException("Current scene context is no longer valid.");
            }
            return currentTimelineSession;
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
                        ReferenceEquals(record.TimelineGenerationTrace.Session, session))
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
            // Session clones carry a null GlobalObjectId, so the persisted
            // CharacterRef (the source scene object) can never match the clone's
            // reference. Fall back to the live object reference before giving up.
            TimelineCharacterRecord match = !string.IsNullOrWhiteSpace(reference)
                ? session.Characters.FirstOrDefault(character => character.CharacterRef == reference)
                    ?? session.Characters.FirstOrDefault(character => character.Root == root)
                : session.Characters.FirstOrDefault(character =>
                    !string.IsNullOrWhiteSpace(name) &&
                    string.Equals(character.Name, name, StringComparison.OrdinalIgnoreCase));
            return match;
        }

        public static string AnimationAnalyze(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                JArray requestedClips = arguments["clips"] as JArray;
                if (requestedClips == null || requestedClips.Count != 1)
                {
                    throw new InvalidOperationException("animation_analyze requires exactly one {character,clip,role?} object; analyze comparison clips separately.");
                }

                JObject picture = AnalysisPictureRequest.Parse(arguments["picture"] as JObject).ToJson();
                int pictureResolution = ResolveAnalysisPictureResolution(arguments["resolution"]);
                JObject requestedAnalysisOptions = null;
                string requestedAnalysisOptionsJson = ParseAnalysisOptionsJson(arguments);
                if (!string.IsNullOrWhiteSpace(requestedAnalysisOptionsJson))
                {
                    requestedAnalysisOptions = JObject.Parse(requestedAnalysisOptionsJson);
                }
                JObject analysisOptions = BuildEffectiveAnalysisOptions(requestedAnalysisOptions);
                var subjects = new List<AnalysisSubject>(requestedClips.Count);
                var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < requestedClips.Count; index++)
                {
                    if (requestedClips[index] is not JObject requested)
                    {
                        throw new InvalidOperationException($"clips[{index}] must be an object.");
                    }

                    string role = (requested.Value<string>("role") ?? (index == 0 ? "source" : "target")).Trim().ToLowerInvariant();
                    if ((role != "source" && role != "target") || !roles.Add(role))
                    {
                        throw new InvalidOperationException("Each clips item requires a unique role of source or target.");
                    }

                    string characterName = RequiredStringValue(requested, "character");
                    TimelineCharacterRecord character = session.Characters.FirstOrDefault(item =>
                        string.Equals(item.Name, characterName, StringComparison.OrdinalIgnoreCase));
                    if (character == null)
                    {
                throw new InvalidOperationException($"Character '{characterName}' is not in the current scene context.");
                    }

                    string clipName = RequiredStringValue(requested, "clip");
                    TimelineAnimationRecord animation = ResolveAnimation(new JObject { ["animation"] = clipName }, character);
                    int startFrame = Mathf.RoundToInt((float)(animation.TimelineStartSeconds * SessionFrameRate));
                    int endFrame = startFrame + Math.Max(1, Mathf.RoundToInt((float)(animation.TimelineDurationSeconds * SessionFrameRate)));
                    ThrowIfGenerationRangeLocked(session, character, startFrame, endFrame, AnimationAnalyzeCommand);

                    string inputSignature = BuildAnimationAnalysisSignature(character, animation, analysisOptions);
                    AnalysisCacheRecord record;
                    if (!TryFindCachedAnimationAnalysis(session, character, animation, inputSignature, out record))
                    {
                        if (!IsHumanoidCharacter(character) && !HasRenderableMesh(character.Root))
                        {
                            throw new InvalidOperationException(
                                $"Character '{character.Name}' is neither a valid humanoid nor a renderable Mesh object.");
                        }

                        JObject analysis;
                        byte[] analysisMotionBytes;
                        if (IsHumanoidCharacter(character))
                        {
                            analysis = AnalyzeAnimation(session, animation, analysisOptions, out analysisMotionBytes);
                        }
                        else
                        {
                            analysis = BuildMeshAnalysis(animation);
                            analysisMotionBytes = null;
                        }
                        NormalizeAnalysisContract(
                            analysis,
                            startFrame,
                            endFrame);
                        if (!IsHumanoidCharacter(character)) analysis["source"] = "mesh_only_pose_sampling";
                        string id = CacheAnalysisResult(
                            session, character, startFrame / SessionFrameRate, endFrame / SessionFrameRate,
                            new JArray(), analysis, analysisMotionBytes, animation, inputSignature);
                        record = GetCachedAnalysis(session, id);
                    }
                    if (IsHumanoidCharacter(character))
                    {
                        EnsureAnalysisRootTrajectory(session, character, record, startFrame, endFrame);
                        EnsureEndpointPoseComparison(session, character, animation, record, startFrame, endFrame);
                    }
                    subjects.Add(new AnalysisSubject(role, character, animation, record, startFrame, endFrame));
                }

                JObject pictures = RenderAnalysisPictures(session, subjects, picture, pictureResolution);
                SaveTimelineSession(session);
                string analysisImagePath = pictures?.Value<string>("image_path") ?? string.Empty;
                var analysisLog = new JObject
                {
                    ["picture"] = picture.DeepClone(),
                    ["clips"] = new JArray(subjects.Select(subject => new JObject
                    {
                        ["role"] = subject.Role,
                        ["character"] = subject.Character?.Name ?? string.Empty,
                        ["clip"] = subject.Animation?.Name ?? string.Empty,
                        ["analysis"] = subject.Record.Analysis?.DeepClone() ?? new JObject()
                    })),
                    ["image_path"] = analysisImagePath,
                    ["image_path_absolute"] = string.IsNullOrWhiteSpace(analysisImagePath)
                        ? string.Empty
                        : ProjectRelativePathToAbsolute(analysisImagePath)
                };
                Debug.Log("[Kimodo][Analysis] " + analysisLog.ToString(Formatting.None));
                return Ok(new JObject
                {
                    ["analysis_schema_version"] = AnalysisContractVersion,
                    ["picture"] = picture.DeepClone(),
                    ["clips"] = new JArray(subjects.Select(BuildAnimationAnalyzeClipResult)),
                    ["pictures"] = pictures
                });
            });
        }

        private static JObject BuildAnimationAnalyzeClipResult(AnalysisSubject subject)
        {
            bool humanoid = IsHumanoidCharacter(subject.Character);
            var result = new JObject
            {
                ["role"] = subject.Role,
                ["character"] = subject.Character.Name,
                ["clip"] = subject.Animation.Name,
                ["analysis_schema_version"] = AnalysisContractVersion,
                ["analysis_mode"] = humanoid ? "humanoid" : "mesh",
                ["phase_track_version"] = humanoid
                    ? subject.Record.Analysis?.Value<string>("phase_track_version") ?? string.Empty
                    : "NOT_APPLICABLE",
                ["phase_track"] = humanoid
                    ? subject.Record.Analysis?["phase_track"]?.DeepClone() ?? new JArray()
                    : "NOT_APPLICABLE",
                ["keyframes"] = subject.Record.Analysis?["keyframes"]?.DeepClone() ?? new JArray(),
                ["foot_contacts"] = subject.Record.Analysis?["foot_contacts"]?.DeepClone() ?? new JArray()
            };
            if (humanoid)
            {
                result["root_trajectory"] = subject.Record.RootTrajectory?.DeepClone() ?? new JObject();
                result["endpoint_pose_comparison"] = subject.Record.Analysis?["endpoint_pose_comparison"]?.DeepClone() ?? new JObject
                {
                    ["status"] = "insufficient_evidence",
                    ["root_transform_included"] = true,
                    ["root2d_constraint_included"] = false
                };
                result["motion_profile"] = subject.Record.Analysis?["motion_profile"]?.DeepClone() ?? new JObject();
            }
            return result;
        }

        private static void EnsureAnalysisRootTrajectory(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            AnalysisCacheRecord record,
            int startFrame,
            int endFrameExclusive)
        {
            if (record?.RootTrajectory?.Value<int?>("motion_semantics_version") >= 2 &&
                record.RootTrajectory["path"] is JObject pathReference)
            {
                int cachedIndex = pathReference.Value<int?>("index") ?? -1;
                string cachedTrack = pathReference.Value<string>("track");
                KimodoConstraintMarker cachedMarker = cachedIndex >= 0 &&
                    string.Equals(cachedTrack, character.PoseCacheTrack?.name, StringComparison.OrdinalIgnoreCase)
                        ? FindPoseMarker(character.PoseCacheTrack, cachedIndex)
                        : null;
                if (cachedMarker?.IsExternalPath == true && cachedMarker.PathData != null)
                {
                    return;
                }
            }

            int frameCount = Math.Max(1, endFrameExclusive - startFrame);
            KimodoMarkerSampleResult[] samples = CaptureCachedSampleResults(record, character, startFrame, frameCount);
            if (samples.Length == 0 || !TryGetRoot2DWorld(samples[0], out Vector3 startPosition, out Quaternion startRotation))
            {
                throw new InvalidOperationException(
                    $"Character '{character.Name}' root trajectory could not sample the first complete root pose.");
            }

            Quaternion startHeading = KimodoMotionMath.ResolvePlanarHeading(startRotation);
            Quaternion toStartLocal = Quaternion.Inverse(startHeading);
            var knots = new List<KimodoRootPathKnot>(samples.Length);
            var jsonSamples = new JArray();
            float pathLength = 0f;
            float minPitchDelta = 0f;
            float maxPitchDelta = 0f;
            float minRollDelta = 0f;
            float maxRollDelta = 0f;
            Vector2 previousPosition = Vector2.zero;
            Vector2 finalPosition = Vector2.zero;
            Vector2 firstHeading = Vector2.up;
            Vector2 finalHeading = Vector2.up;
            for (int frame = 0; frame < samples.Length; frame++)
            {
                if (!TryGetRoot2DWorld(samples[frame], out Vector3 worldPosition, out Quaternion worldRotation))
                {
                    throw new InvalidOperationException(
                        $"Character '{character.Name}' root trajectory could not sample complete root motion at frame {frame}.");
                }

                Vector3 localDelta = toStartLocal * (worldPosition - startPosition);
                Vector3 localForward = toStartLocal *
                    (KimodoMotionMath.ResolvePlanarHeading(worldRotation) * Vector3.forward);
                var position = new Vector2(localDelta.x, localDelta.z);
                var heading = new Vector2(localForward.x, localForward.z);
                heading = heading.sqrMagnitude > 1e-8f ? heading.normalized : finalHeading;
                Vector3 rootRotationDelta = KimodoMotionMath.RelativeEulerDegrees(startRotation, worldRotation);
                if (frame > 0) pathLength += Vector2.Distance(previousPosition, position);
                if (frame == 0) firstHeading = heading;
                minPitchDelta = Mathf.Min(minPitchDelta, rootRotationDelta.x);
                maxPitchDelta = Mathf.Max(maxPitchDelta, rootRotationDelta.x);
                minRollDelta = Mathf.Min(minRollDelta, rootRotationDelta.z);
                maxRollDelta = Mathf.Max(maxRollDelta, rootRotationDelta.z);
                previousPosition = position;
                finalPosition = position;
                finalHeading = heading;
                knots.Add(new KimodoRootPathKnot
                {
                    frame = frame,
                    position = position,
                    hasHeading = true,
                    heading = heading
                });
                jsonSamples.Add(new JObject
                {
                    ["frame"] = frame,
                    ["position_xz"] = new JArray(position.x, position.y),
                    ["heading_xz"] = new JArray(heading.x, heading.y),
                    ["root_rotation_delta_euler_degrees"] = new JArray(
                        rootRotationDelta.x,
                        rootRotationDelta.y,
                        rootRotationDelta.z)
                });
            }

            int index = AllocatePoseIndex(character.PoseCacheTrack);
            float sourceHumanScale = KimodoConstraintNormalizationUtility.ResolveHumanScale(character.Avatar);
            StoreExternalPath(character, index, new KimodoRootPathData
            {
                type = "analyzed",
                length = pathLength,
                sourceHumanScale = sourceHumanScale,
                inverse = false,
                knots = knots
            });

            float sampleSpanSeconds = samples.Length > 1
                ? (samples.Length - 1) / (float)SessionFrameRate
                : 0f;
            float firstYaw = Mathf.Atan2(firstHeading.x, firstHeading.y) * Mathf.Rad2Deg;
            float finalYaw = Mathf.Atan2(finalHeading.x, finalHeading.y) * Mathf.Rad2Deg;
            float signedHeadingChange = Mathf.DeltaAngle(firstYaw, finalYaw);
            record.RootTrajectory = new JObject
            {
                ["motion_semantics_version"] = 2,
                ["path"] = PoseReferenceJson(character.PoseCacheTrack.name, index),
                ["coordinate_space"] = "clip_start_local",
                ["frame_rate"] = SessionFrameRate,
                ["frame_count"] = samples.Length,
                ["duration_seconds"] = samples.Length / SessionFrameRate,
                ["sample_span_seconds"] = sampleSpanSeconds,
                ["path_length_xz"] = pathLength,
                ["net_displacement_xz"] = new JArray(finalPosition.x, finalPosition.y),
                ["net_distance_xz"] = finalPosition.magnitude,
                ["average_speed_xz"] = sampleSpanSeconds > 1e-6f ? pathLength / sampleSpanSeconds : 0f,
                ["heading_change_degrees"] = signedHeadingChange,
                ["root_pitch_delta_range_degrees"] = new JArray(minPitchDelta, maxPitchDelta),
                ["root_roll_delta_range_degrees"] = new JArray(minRollDelta, maxRollDelta),
                ["source_human_scale"] = sourceHumanScale,
                ["samples"] = jsonSamples
            };
            AnalysisCache[record.Id] = record;
            WriteJsonAtomically(AnalysisCachePath(session, record.Id), record.ToJson());
        }

        private static bool IsSessionObject(GameObject gameObject)
        {
            Transform current = gameObject != null ? gameObject.transform : null;
            while (current != null)
            {
                if (current.name.StartsWith("KimodoSession_", StringComparison.Ordinal)) return true;
                current = current.parent;
            }
            return false;
        }

        private static void EnsureEndpointPoseComparison(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            TimelineAnimationRecord animation,
            AnalysisCacheRecord record,
            int startFrame,
            int endFrameExclusive)
        {
            if (record?.Analysis == null || endFrameExclusive <= startFrame) return;
            if (record.Analysis["endpoint_pose_comparison"] is JObject existing &&
                existing["root_transform_included"]?.Value<bool>() == true)
            {
                return;
            }

            KimodoMarkerSampleResult[] endpointSamples = CaptureCachedSampleResults(record, character, startFrame, Math.Max(1, endFrameExclusive - startFrame));
            KimodoMarkerSampleResult first = endpointSamples[0];
            KimodoMarkerSampleResult last = endpointSamples[endpointSamples.Length - 1];
            bool valid = first?.sampleData?.IsValid == true && last?.sampleData?.IsValid == true;
            var comparison = new JObject
            {
                ["method"] = "humanoid_pose_and_root_motion_endpoint_compare",
                ["root_transform_included"] = true,
                ["root2d_constraint_included"] = false,
                ["start_frame"] = 0,
                ["end_frame"] = Math.Max(0, endFrameExclusive - startFrame - 1)
            };

            if (valid)
            {
                KimodoMotionMath.PoseDelta delta = ComputePoseMotionDelta(first, last);
                comparison["status"] = "ok";
                comparison["mean_muscle_delta"] = delta.MeanBodyMuscleDelta;
                comparison["body_pose_matches"] = delta.MeanBodyMuscleDelta <= 0.08f;
                comparison["root_position_delta_xyz"] = new JArray(
                    delta.RootPositionDelta.x,
                    delta.RootPositionDelta.y,
                    delta.RootPositionDelta.z);
                comparison["root_height_delta"] = delta.RootHeightDelta;
                comparison["root_rotation_delta_degrees"] = delta.RootRotationDeltaDegrees;
                comparison["root_rotation_delta_euler_degrees"] = new JArray(
                    delta.RootPitchDeltaDegrees,
                    delta.RootYawDeltaDegrees,
                    delta.RootRollDeltaDegrees);
            }
            else
            {
                comparison["status"] = "insufficient_evidence";
            }

            record.Analysis["endpoint_pose_comparison"] = comparison;
            JObject trajectory = record.RootTrajectory ?? new JObject();
            float pathLength = trajectory.Value<float?>("path_length_xz") ?? 0f;
            float netDistance = trajectory.Value<float?>("net_distance_xz") ?? 0f;
            float headingChange = trajectory.Value<float?>("heading_change_degrees") ?? 0f;
            bool bodyPoseMatches = valid && comparison.Value<bool?>("body_pose_matches") == true;
            float rootHeightDelta = comparison.Value<float?>("root_height_delta") ?? 0f;
            JArray rootEulerDelta = comparison["root_rotation_delta_euler_degrees"] as JArray;
            float rootPitchDelta = rootEulerDelta?[0]?.Value<float>() ?? 0f;
            float rootRollDelta = rootEulerDelta?[2]?.Value<float>() ?? 0f;
            bool rootVerticalAndTiltMatch = valid &&
                Mathf.Abs(rootHeightDelta) <= 0.03f &&
                Mathf.Abs(rootPitchDelta) <= 5f &&
                Mathf.Abs(rootRollDelta) <= 5f;
            record.Analysis["motion_profile"] = new JObject
            {
                ["clip_loop_time"] = animation?.Clip != null && animation.Clip.isLooping,
                ["is_loop_candidate"] = animation?.Clip != null && animation.Clip.isLooping &&
                    bodyPoseMatches && rootVerticalAndTiltMatch,
                ["root_vertical_and_tilt_matches"] = rootVerticalAndTiltMatch,
                ["has_clear_path"] = netDistance >= 0.05f || pathLength >= 0.08f,
                ["path_length_xz"] = pathLength,
                ["net_distance_xz"] = netDistance,
                ["heading_change_degrees"] = headingChange,
                ["heading_consistent"] = Mathf.Abs(headingChange) <= 8f,
                ["keyframe_heading_consistent"] = Mathf.Abs(headingChange) <= 8f,
                ["root_pitch_delta_range_degrees"] = trajectory["root_pitch_delta_range_degrees"]?.DeepClone() ?? new JArray(),
                ["root_roll_delta_range_degrees"] = trajectory["root_roll_delta_range_degrees"]?.DeepClone() ?? new JArray(),
                ["should_override_path"] = "defer_to_task_semantics",
                ["should_override_heading"] = "defer_to_task_semantics"
            };
            WriteJsonAtomically(AnalysisCachePath(session, record.Id), record.ToJson());
        }

        // Shared by automatic analysis endpoint checks.
        // The common math keeps body, root height and root rotation observable.
        private static KimodoMotionMath.PoseDelta ComputePoseMotionDelta(
            KimodoMarkerSampleResult origin,
            KimodoMarkerSampleResult target)
        {
            if (origin?.sampleData == null || target?.sampleData == null ||
                !origin.sampleData.IsValid || !target.sampleData.IsValid)
            {
                throw new InvalidOperationException("Pose comparison requires two valid Humanoid samples.");
            }
            GetRootTransform(origin, out Vector3 originPosition, out Quaternion originRotation);
            GetRootTransform(target, out Vector3 targetPosition, out Quaternion targetRotation);
            return KimodoMotionMath.Compare(
                origin.sampleData,
                target.sampleData,
                originPosition,
                originRotation,
                targetPosition,
                targetRotation);
        }

        private static int ResolveAnalysisPictureResolution(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return 1920;
            if (value.Type != JTokenType.Integer)
            {
                throw new InvalidOperationException("resolution must be a positive integer pixel size.");
            }
            int resolution = value.Value<int>();
            if (resolution < 64 || resolution > 4096)
            {
                throw new InvalidOperationException("resolution must be between 64 and 4096 pixels.");
            }
            return resolution;
        }

        private static void NormalizeAnalysisContract(
            JObject analysis,
            int startFrame,
            int endFrame)
        {
            analysis ??= new JObject();
            var keyframes = new JArray();
            foreach (JObject keyframe in (analysis?["keyframes"] as JArray ?? new JArray()).OfType<JObject>())
            {
                // QuickServer analysis is always relative to the requested
                // segment.  Convert that local frame to the Session frame once;
                // do not infer absolute-vs-local from its numeric value.
                int reportedFrame = keyframe.Value<int?>("frame")
                    ?? Mathf.RoundToInt((float)((keyframe.Value<double?>("time") ?? 0.0) * SessionFrameRate));
                int frame = Mathf.Clamp(startFrame + reportedFrame, startFrame, endFrame - 1);
                JObject annotation = (JObject)keyframe.DeepClone();
                annotation.Remove("time");
                annotation.Remove("session_time");
                annotation["frame"] = frame - startFrame;
                annotation["local_frame_60"] = frame - startFrame;
                annotation["timeline_frame_60"] = frame;
                annotation["local_time_seconds"] = (frame - startFrame) / SessionFrameRate;
                annotation["time_seconds"] = frame / SessionFrameRate;
                keyframes.Add(annotation);
            }
            JArray contacts = analysis["foot_contacts"] as JArray
                ?? analysis["foot_contact_changes"] as JArray
                ?? new JArray();
            var normalizedContacts = new JArray();
            foreach (JObject contact in contacts.OfType<JObject>())
            {
                normalizedContacts.Add(new JObject
                {
                    ["clip_index"] = contact.Value<int?>("clip_index") ?? 0,
                    ["foot"] = contact.Value<string>("foot") ?? string.Empty,
                    ["frame"] = contact.Value<int?>("frame") ?? 0,
                    ["contact"] = contact.Value<bool?>("contact") ?? false,
                    ["transition"] = contact.Value<string>("transition") ?? string.Empty,
                    ["duration_frames"] = contact.Value<int?>("duration_frames") ?? 0
                });
            }
            // Destructive v2 contract: the analyzer owns temporal phase
            // intervals. Do not re-sample or uniformly select keyframes here.
            // The renderer later replaces Humanoid anchors with phase anchors.
            analysis["keyframes"] = keyframes;
            analysis["foot_contacts"] = normalizedContacts;
            analysis["source"] = "quickserver_analysis_only";
            analysis["command_fps"] = SessionFrameRate;
        }

        private static JObject BuildEffectiveAnalysisOptions(JObject requested)
        {
            JObject result = requested != null
                ? (JObject)requested.DeepClone()
                : new JObject();
            // keyframe_count/max_count are intentionally not read in
            // the v2 command. The phase tracker is deterministic and owns
            // temporal segmentation.
            result.Remove("keyframe_count");
            if (result["keyframes"] is JObject keyframes)
            {
                keyframes.Remove("max_count");
                keyframes.Remove("enabled");
                if (!keyframes.HasValues) result.Remove("keyframes");
            }
            return result;
        }

        private static JObject BuildMeshAnalysis(TimelineAnimationRecord animation)
        {
            int frameCount = Math.Max(1, animation?.EndFrameExclusive > animation?.StartFrame
                ? animation.EndFrameExclusive - animation.StartFrame
                : Mathf.Max(1, Mathf.RoundToInt((float)((animation?.TimelineDurationSeconds ?? 0.0) * SessionFrameRate))));
            int count = Math.Min(2, frameCount);
            var keyframes = new JArray();
            for (int index = 0; index < count; index++)
            {
                keyframes.Add(new JObject
                {
                    ["frame"] = count <= 1
                        ? 0
                        : Mathf.RoundToInt(Mathf.Lerp(0f, frameCount - 1, index / (float)(count - 1))),
                    ["kind"] = "mesh_pose"
                });
            }
            return new JObject
            {
                ["keyframes"] = keyframes,
                ["foot_contacts"] = new JArray(),
                ["phase_track_version"] = "NOT_APPLICABLE",
                ["phase_track"] = "NOT_APPLICABLE",
                ["source"] = "mesh_only_pose_sampling"
            };
        }

        private static bool IsHumanoidCharacter(TimelineCharacterRecord character)
        {
            return character != null && KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar);
        }

        private static JObject AnalyzeAnimation(
            TimelineSessionRecord session,
            TimelineAnimationRecord animation,
            JObject analysisOptions,
            out byte[] analysisMotionBytes)
        {
            analysisMotionBytes = null;
            // Analysis is a command-level contract. Timeline editor FPS and
            // native KMB FPS are sampling details, never protocol frame rates.
            float frameRate = (float)KimodoFrameTimeUtility.CommandFrameRate;
            byte[] motionBytes = animation.KmbBytes;
            int startFrame = Math.Max(0, animation.StartFrame);
            int frameCount = animation.EndFrameExclusive > animation.StartFrame
                ? animation.EndFrameExclusive - animation.StartFrame
                : Math.Max(1, Mathf.CeilToInt((float)(animation.TimelineDurationSeconds * frameRate)));
            bool requiresFootContactEncoding = motionBytes == null || motionBytes.Length == 0;
            if (!requiresFootContactEncoding &&
                KimodoRawMotionUtility.TryParseFlatBuffer(motionBytes, out KimodoRawMotionData existingMotion, out _) &&
                !existingMotion.HasFootContacts)
            {
                requiresFootContactEncoding = true;
            }
            if (requiresFootContactEncoding)
            {
                motionBytes = KimodoClipConstraintEncoder.EncodeTimeline(animation.TimelineClip, ResolveModelName(null), frameCount,
                    frameRate, 0, KimodoInOutConstraintMode.None, false, false, includeFootContacts: true);
                startFrame = 0;
            }
            if (KimodoRawMotionUtility.TryParseFlatBuffer(motionBytes, out KimodoRawMotionData motion, out _) && motion.FrameCount > 0)
            {
                // KMB uses the model's native time base (Kimodo is normally
                // 30 FPS), while a Timeline Session is fixed at 60 FPS. The
                // analysis contract is consumed by Session-frame renderers,
                // so analyze a time-base-aligned copy instead of treating
                // native model frame numbers as Session frame numbers.
                int sessionFrameCount = Math.Max(1, Mathf.RoundToInt(
                    (float)(animation.TimelineDurationSeconds * frameRate)));
                if (sessionFrameCount != motion.FrameCount ||
                    !Mathf.Approximately(motion.FrameRate, frameRate))
                {
                    if (!KimodoRawMotionUtility.TryResample(
                            motion,
                            frameRate,
                            sessionFrameCount,
                            out KimodoRawMotionData aligned,
                            out string resampleError))
                    {
                        throw new InvalidOperationException(
                            $"Analysis motion time-base alignment failed: {resampleError}");
                    }
                    motion = aligned;
                    motionBytes = KimodoRawMotionUtility.ToFlatBuffer(motion, ResolveModelName(null));
                    startFrame = 0;
                    frameCount = sessionFrameCount;
                }
                else
                {
                    startFrame = Mathf.Clamp(startFrame, 0, motion.FrameCount - 1);
                    frameCount = Mathf.Clamp(frameCount, 1, motion.FrameCount - startFrame);
                }
            }
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            var input = new KimodoEditorAnalysisInput
            {
                MotionBytes = motionBytes,
                StartFrame = startFrame,
                EndFrameExclusive = startFrame + frameCount,
                ModelName = ResolveModelName(null),
                TextEncoderMode = settings.DefaultTextEncoderMode,
                ModelsRoot = settings.LocalModelsPath?.Trim() ?? string.Empty,
                AnalysisOptionsJson = (analysisOptions ?? new JObject()).ToString(Formatting.None)
            };
            if (!KimodoPlayableClipGenerationExecutionService.Analysis(
                    input,
                    out string analysisJson,
                    out analysisMotionBytes,
                    out string error))
            {
                throw new InvalidOperationException(error);
            }
            JObject analysis = ParseAnalysisObject(analysisJson);
            analysis["source"] = "quickserver_analysis_only";
            analysis["command_fps"] = KimodoFrameTimeUtility.CommandFrameRate;
            return analysis;
        }

        private static TimelineAnimationRecord BakeTransientAnalysisRange(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            int startFrame,
            int endFrame,
            out AnimationClip transientClip,
            out TimelineClip transientTimelineClip)
        {
            int frameCount = endFrame - startFrame;
            Transform[] transforms = character.Root.GetComponentsInChildren<Transform>(true);
            string[] paths = transforms.Select(transform => AnimationUtility.CalculateTransformPath(transform, character.Root.transform)).ToArray();
            var frames = new List<BakeBoneFrame>(frameCount);
            double originalTime = session.Director.time;
            for (int frame = 0; frame < frameCount; frame++)
            {
                session.Director.time = (startFrame + frame) / SessionFrameRate;
                session.Director.Evaluate();
                var sample = new BakeBoneFrame(transforms.Length);
                for (int index = 0; index < transforms.Length; index++)
                {
                    sample.Positions[index] = transforms[index].localPosition;
                    sample.Rotations[index] = transforms[index].localRotation;
                }
                frames.Add(sample);
            }
            session.Director.time = originalTime;
            session.Director.Evaluate();

            transientClip = new AnimationClip { name = "__KimodoAnalysisRange__", frameRate = (float)SessionFrameRate };
            transientClip.hideFlags = HideFlags.HideAndDontSave;
            WriteBoneBakeCurves(transientClip, transforms, paths, frames, (float)SessionFrameRate);
            transientTimelineClip = character.Track.CreateClip<AnimationPlayableAsset>();
            transientTimelineClip.start = character.NextStartSeconds;
            transientTimelineClip.duration = frameCount / SessionFrameRate;
            transientTimelineClip.displayName = transientClip.name;
            ((AnimationPlayableAsset)transientTimelineClip.asset).clip = transientClip;
            return new TimelineAnimationRecord(Guid.NewGuid(), transientClip.name, "temporary", transientClip,
                transientTimelineClip, null, null, 0, frameCount);
        }

        public static string RetargetAnimation(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                TimelineCharacterRecord source = ResolveSessionCharacterByReference(
                    session, RequiredStringValue(arguments, "source_character"), addIfMissing: false);
                TimelineAnimationRecord sourceAnimation = ResolveAnimation(arguments, source);
                TimelineCharacterRecord target = ResolveSessionCharacterByReference(
                    session,
                    RequiredStringValue(arguments, "target_character"),
                    addIfMissing: false);
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
            });
        }

        private static void WriteBoneBakeCurves(
            AnimationClip clip,
            Transform[] transforms,
            string[] paths,
            List<BakeBoneFrame> frames,
            float frameRate)
        {
            for (int index = 0; index < transforms.Length; index++)
            {
                var px = new AnimationCurve();
                var py = new AnimationCurve();
                var pz = new AnimationCurve();
                var rx = new AnimationCurve();
                var ry = new AnimationCurve();
                var rz = new AnimationCurve();
                var rw = new AnimationCurve();
                for (int frame = 0; frame < frames.Count; frame++)
                {
                    float time = frame / frameRate;
                    Vector3 position = frames[frame].Positions[index];
                    Quaternion rotation = frames[frame].Rotations[index];
                    px.AddKey(time, position.x); py.AddKey(time, position.y); pz.AddKey(time, position.z);
                    rx.AddKey(time, rotation.x); ry.AddKey(time, rotation.y); rz.AddKey(time, rotation.z); rw.AddKey(time, rotation.w);
                }
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalPosition.x", px);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalPosition.y", py);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalPosition.z", pz);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.x", rx);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.y", ry);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.z", rz);
                clip.SetCurve(paths[index], typeof(Transform), "m_LocalRotation.w", rw);
            }
            clip.EnsureQuaternionContinuity();
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
            TimelineCharacterRecord match = session.Characters.FirstOrDefault(character =>
                character.CharacterRef == reference || string.Equals(character.Name, reference, StringComparison.OrdinalIgnoreCase));
            if (match == null && addIfMissing)
            {
                UnityEngine.Object resolved = ResolveObject(reference);
                GameObject root = resolved as GameObject ?? (resolved as Animator)?.gameObject;
                Animator animator = root != null ? root.GetComponentInChildren<Animator>(true) : null;
                string error = string.Empty;
                bool added = root != null && root.scene.IsValid() && !EditorUtility.IsPersistent(root) &&
                    AddCharacterTrack(session, root, animator, true, out error, requireAvatar: false);
                if (added)
                {
                    match = session.Characters.FirstOrDefault(character =>
                        character.CharacterRef == reference || character.Root != null && character.Root.transform.IsChildOf(session.SessionRoot.transform) &&
                        string.Equals(character.Name, root.name, StringComparison.OrdinalIgnoreCase));
                }
                else if (root != null)
                {
                    throw new InvalidOperationException($"Could not create a target AnimationTrack: {error}");
                }
            }
            if (match == null)
            {
                throw new InvalidOperationException($"Character '{reference}' is not in the resolved scene context.");
            }
            return match;
        }

        private static JObject DescribeSession(TimelineSessionRecord session)
        {
            return new JObject
            {
                ["session"] = session.Name,
                ["session_game_object"] = session.SessionRoot != null ? session.SessionRoot.name : string.Empty,
                ["characters"] = new JArray(session.Characters.Select(DescribeCharacter)),
                ["current_frame"] = session.Director != null
                    ? Mathf.RoundToInt((float)(session.Director.time * SessionFrameRate))
                    : 0,
                ["current"] = ReferenceEquals(currentTimelineSession, session)
            };
        }

        private static JObject DescribeCharacter(TimelineCharacterRecord character)
        {
            return new JObject
            {
                ["name"] = character.Name,
                ["animations"] = new JArray(character.Animations.Select(DescribeAnimation))
            };
        }

        private static JObject DescribeAnimation(TimelineAnimationRecord animation)
        {
            var result = new JObject
            {
                ["name"] = animation.Name,
                ["source"] = animation.Source,
                ["kind"] = animation.Kind,
                ["start_frame"] = animation.TimelineClip != null ? Mathf.RoundToInt((float)(animation.TimelineStartSeconds * SessionFrameRate)) : 0,
                ["duration_frames"] = animation.TimelineClip != null ? Mathf.RoundToInt((float)(animation.TimelineDurationSeconds * SessionFrameRate)) : 0,
                ["segments"] = new JArray(animation.TimelineSegments.Select(segment => new JObject
                {
                    ["role"] = segment.Role,
                    ["start_frame"] = segment.TimelineClip != null ? Mathf.RoundToInt((float)(segment.TimelineClip.start * SessionFrameRate)) : 0,
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
                !KimodoConstraintMask.FromSample(marker.SampleData).muscle &&
                !KimodoConstraintMask.FromSample(marker.SampleData).AnyEndEffector)
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

        private static bool Overlaps(TimelineClip clip, double start, double end)
        {
            return clip != null && clip.end > start && clip.start < end;
        }

        private static double RequiredFiniteDouble(JObject arguments, string name)
        {
            if (!arguments.TryGetValue(name, out JToken token) ||
                (token.Type != JTokenType.Float && token.Type != JTokenType.Integer))
            {
                throw new InvalidOperationException($"{name} is required and must be a finite number.");
            }
            double value = token.Value<double>();
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new InvalidOperationException($"{name} must be finite.");
            }
            return value;
        }

        private static void SaveTimelineSession(TimelineSessionRecord session)
        {
            PersistTimelineSessionMetadata(session);
            EditorUtility.SetDirty(session.TimelineAsset);
            AssetDatabase.SaveAssets();
            session.Director.RebuildGraph();
            KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsAddedOrRemoved);
        }

        private static bool HasRunningTimelineGeneration(Guid timelineSessionId)
        {
            lock (JobsLock)
            {
                return Jobs.Values.Any(record => record.Session.IsRunning &&
                    record.TimelineGenerationTrace != null && record.TimelineGenerationTrace.Session.Id == timelineSessionId);
            }
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
                KimodoCommandSessionMetadata metadata,
                GameObject sessionRoot)
            {
                Id = id;
                Name = name;
                Director = director;
                TimelineAsset = timelineAsset;
                TimelineAssetPath = timelineAssetPath;
                IsAutomatic = isAutomatic;
                Metadata = metadata;
                SessionRoot = sessionRoot;
                CreatedAtUtc = DateTime.UtcNow;
            }

            public Guid Id { get; }
            public string Name { get; }
            public DateTime CreatedAtUtc { get; }
            public PlayableDirector Director { get; }
            public TimelineAsset TimelineAsset { get; }
            public string TimelineAssetPath { get; }
            public bool IsAutomatic { get; }
            public KimodoCommandSessionMetadata Metadata { get; }
            public GameObject SessionRoot { get; }
            public bool AutoCloseWhenIdle { get; set; }
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
            public Avatar Avatar { get; set; }
            public AnimationTrack Track { get; }
            public AnimationTrack PoseCacheTrack { get; }
            public string AvatarError { get; set; }
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
            public double StartSeconds { get; }
            public double DurationSeconds { get; }
            public TimelineClip TimelineClip { get; set; }
            public KimodoPlayableClip PlayableClip { get; set; }
            public JObject InOutSampling { get; set; }
            public TimelineAnimationRecord Animation { get; set; }
        }

        private sealed class BakeBoneFrame
        {
            public BakeBoneFrame(int count)
            {
                Positions = new Vector3[count];
                Rotations = new Quaternion[count];
            }
            public Vector3[] Positions { get; }
            public Quaternion[] Rotations { get; }
        }

    }
}
