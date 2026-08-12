using System;
using System.Collections.Generic;
using System.Globalization;
using CharacterAnimationCli.Unity.Command;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    public enum KimodoNavMeshConstraintPointType
    {
        Root2D = 0,
        LeftFoot = 1,
        RightFoot = 2
    }

    public sealed class KimodoNavMeshWaypointSample
    {
        public Vector3 Position;
        public bool HasHeading;
        public Vector2 Heading;
        public float LocalTimeSeconds;
    }

    public sealed class KimodoNavMeshRouteGroup
    {
        public readonly List<KimodoNavMeshWaypointSample> Samples = new List<KimodoNavMeshWaypointSample>();
        public float DurationSeconds;
        public float StartTimeSeconds;
        public float EndTimeSeconds => StartTimeSeconds + DurationSeconds;
    }

    public sealed class KimodoNavMeshRoutePlan
    {
        public readonly List<KimodoNavMeshRouteGroup> Groups = new List<KimodoNavMeshRouteGroup>();
        public int TotalWaypointCount;
        public float TotalDurationSeconds;
    }

    public sealed class KimodoNavMeshTimelineGenerationContext
    {
        public GameObject CharacterRoot;
        public GameObject TimelineHost;
        public Animator CharacterAnimator;
        public PlayableDirector Director;
        public TimelineAsset TimelineAsset;
        public AnimationTrack Track;
        public readonly List<TimelineClip> GeneratedTimelineClips = new List<TimelineClip>();

        public bool IsValidFor(GameObject characterRoot, GameObject timelineHost)
        {
            return characterRoot != null &&
                   CharacterRoot == characterRoot &&
                   TimelineHost == (timelineHost != null ? timelineHost : characterRoot) &&
                   Director != null &&
                   TimelineAsset != null &&
                   Track != null;
        }
    }

    public static class KimodoNavMeshTimelineGenerationService
    {
        private enum GeneratedConstraintKind
        {
            Root2D,
            LeftFoot,
            RightFoot
        }

        private const float DuplicateWaypointThresholdMeters = 0.01f;
        private const string GeneratedTimelineFolder = "Assets/KimodoGeneratedClips/Timelines";
        private const string GeneratedTrackNamePrefix = "Kimodo NavMesh Route";
        private const float MaxGroupDurationSeconds = 10f;
        private const float MaxSpeedMetersPerSecond = 1.25f;
        private const float MaxAccelerationMetersPerSecond2 = 1.5f;
        private const float MinSegmentDurationSeconds = 1f;
        private const float MaxSegmentDurationSeconds = 10f;
        public static bool TryResolveCharacterRoot(GameObject explicitCharacterRoot, out GameObject characterRoot, out string error)
        {
            characterRoot = null;
            error = string.Empty;

            if (explicitCharacterRoot != null)
            {
                if (!HasValidCharacterAnimator(explicitCharacterRoot))
                {
                    error = $"Character '{explicitCharacterRoot.name}' does not contain a valid humanoid Animator.";
                    return false;
                }

                characterRoot = explicitCharacterRoot;
                return true;
            }

            if (Selection.activeGameObject != null && HasValidCharacterAnimator(Selection.activeGameObject))
            {
                characterRoot = Selection.activeGameObject;
                return true;
            }

            Animator[] animators = UnityEngine.Object.FindObjectsOfType<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                Animator animator = animators[i];
                if (animator == null || !animator.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (IsValidHumanoidAnimator(animator))
                {
                    characterRoot = animator.gameObject;
                    return true;
                }
            }

            error = "Unable to resolve a valid character root. Assign a Character Root with a humanoid Animator, select one in Hierarchy, or keep a humanoid Animator in the scene.";
            return false;
        }

        public static bool TryBuildRoutePlan(
            IReadOnlyList<Vector3> rawPathPoints,
            bool useHeading,
            out KimodoNavMeshRoutePlan routePlan,
            out string error)
        {
            routePlan = null;
            error = string.Empty;

            if (rawPathPoints == null || rawPathPoints.Count < 2)
            {
                error = "NavMesh path must contain at least 2 points.";
                return false;
            }

            List<Vector3> expandedPoints = ExpandLongSegments(rawPathPoints);
            if (expandedPoints.Count < 2)
            {
                error = "Expanded NavMesh path does not contain enough points.";
                return false;
            }

            routePlan = BuildGroupedPlan(expandedPoints, useHeading);
            if (routePlan.Groups.Count == 0)
            {
                error = "Failed to build any NavMesh route groups from the current path.";
                routePlan = null;
                return false;
            }

            return true;
        }

        public static bool TryGeneratePathConstraints(
            GameObject explicitCharacterRoot,
            IReadOnlyList<Vector3> pathPoints,
            bool useHeading,
            KimodoNavMeshConstraintPointType pointType,
            string modelName,
            float footBoundarySkipSeconds,
            GameObject timelineHost,
            KimodoNavMeshTimelineGenerationContext existingContext,
            bool allowContextReuse,
            out KimodoNavMeshTimelineGenerationContext context,
            out KimodoNavMeshRoutePlan routePlan,
            out string error)
        {
            context = null;
            routePlan = null;
            error = string.Empty;

            if (!TryResolveCharacterRoot(explicitCharacterRoot, out GameObject characterRoot, out error))
            {
                return false;
            }

            if (!TryBuildRoutePlan(pathPoints, useHeading, out routePlan, out error))
            {
                return false;
            }

            if (!TryEnsureTrackContext(characterRoot, timelineHost, existingContext, allowContextReuse, out context, out error))
            {
                return false;
            }

            RegisterTimelineUndo(context, $"Generate NavMesh {pointType} Constraints");
            ClearMarkers(
                context.Track,
                marker => marker is KimodoRoot2DConstraintMarker ||
                          marker is KimodoLeftFootConstraintMarker ||
                          marker is KimodoRightFootConstraintMarker);
            Vector3? previousMarkerPosition = null;
            for (int groupIndex = 0; groupIndex < routePlan.Groups.Count; groupIndex++)
            {
                KimodoNavMeshRouteGroup group = routePlan.Groups[groupIndex];
                int startSampleIndex = groupIndex > 0 ? 1 : 0;
                for (int sampleIndex = startSampleIndex; sampleIndex < group.Samples.Count; sampleIndex++)
                {
                    KimodoNavMeshWaypointSample sample = group.Samples[sampleIndex];
                    double markerTime = group.StartTimeSeconds + sample.LocalTimeSeconds;
                    KimodoNavMeshConstraintPointType effectivePointType = ResolveBoundaryAdjustedPointType(
                        routePlan,
                        groupIndex,
                        markerTime,
                        pointType,
                        footBoundarySkipSeconds);

                    if (previousMarkerPosition.HasValue &&
                        AreConstraintMarkersNear(
                            previousMarkerPosition.Value,
                            sample.Position,
                            GetConstraintKind(effectivePointType),
                            GetConstraintKind(effectivePointType)))
                    {
                        continue;
                    }

                    if (!TryCreateConstraintMarker(context, effectivePointType, modelName, sample, markerTime, out KimodoConstraintMarkerBase marker, out KimodoMarkerSampleResult markerSample, out error))
                    {
                        return false;
                    }

                    Undo.RegisterCreatedObjectUndo(marker, $"Create {effectivePointType} Constraint");

                    if (!KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(marker, markerSample, keepOverrideEnabled: true, out error))
                    {
                        return false;
                    }

                    previousMarkerPosition = sample.Position;
                }
            }

            MarkTimelineDirty(context);
            FocusTimeline(context.Director, context.CharacterRoot);
            return true;
        }

        public static bool TryGeneratePathConstraintsAndTimelineClips(
            GameObject explicitCharacterRoot,
            IReadOnlyList<Vector3> pathPoints,
            bool useHeading,
            KimodoNavMeshConstraintPointType pointType,
            string modelName,
            string prompt,
            float footBoundarySkipSeconds,
            GameObject timelineHost,
            KimodoNavMeshTimelineGenerationContext existingContext,
            bool allowInitialContextReuse,
            out KimodoNavMeshTimelineGenerationContext context,
            out KimodoNavMeshRoutePlan routePlan,
            out string error)
        {
            context = null;
            routePlan = null;
            error = string.Empty;

            if (!TryGeneratePathConstraints(
                    explicitCharacterRoot,
                    pathPoints,
                    useHeading,
                    pointType,
                    modelName,
                    footBoundarySkipSeconds,
                    timelineHost,
                    existingContext,
                    allowInitialContextReuse,
                    out context,
                    out routePlan,
                    out error))
            {
                return false;
            }

            return TryGenerateTimelineClips(
                context != null ? context.CharacterRoot : explicitCharacterRoot,
                pathPoints,
                modelName,
                prompt,
                timelineHost,
                context,
                true,
                out context,
                out routePlan,
                out error);
        }

        public static bool TryGenerateTimelineClips(
            GameObject explicitCharacterRoot,
            IReadOnlyList<Vector3> pathPoints,
            string modelName,
            string prompt,
            GameObject timelineHost,
            KimodoNavMeshTimelineGenerationContext existingContext,
            bool allowContextReuse,
            out KimodoNavMeshTimelineGenerationContext context,
            out KimodoNavMeshRoutePlan routePlan,
            out string error)
        {
            context = null;
            routePlan = null;
            error = string.Empty;

            if (!TryResolveCharacterRoot(explicitCharacterRoot, out GameObject characterRoot, out error))
            {
                return false;
            }

            if (!TryBuildRoutePlan(pathPoints, useHeading: false, out routePlan, out error))
            {
                return false;
            }

            if (!TryEnsureTrackContext(characterRoot, timelineHost, existingContext, allowContextReuse, out context, out error))
            {
                return false;
            }

            RegisterTimelineUndo(context, "Generate NavMesh Timeline Clips");
            ClearTimelineClips(context.Track);
            context.GeneratedTimelineClips.Clear();

            TimelineClip lastTimelineClip = null;
            for (int groupIndex = 0; groupIndex < routePlan.Groups.Count; groupIndex++)
            {
                KimodoNavMeshRouteGroup group = routePlan.Groups[groupIndex];
                TimelineClip timelineClip = context.Track.CreateClip<KimodoPlayableClip>();
                timelineClip.start = group.StartTimeSeconds;

                float durationSeconds = Mathf.Max(
                    MinSegmentDurationSeconds,
                    KimodoInOutConstraintTools.FrameCountToDurationSeconds(
                        KimodoInOutConstraintTools.DurationSecondsToFrameCount(group.DurationSeconds)));
                timelineClip.duration = durationSeconds;
                timelineClip.displayName = $"{GeneratedTrackNamePrefix} {groupIndex + 1}";

                if (timelineClip.asset is KimodoPlayableClip playableClip)
                {
                    playableClip.bridgeModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
                    playableClip.motionPrompt = KimodoPlayableClipGenerationSettings.instance.ResolvePrompt(prompt);
                    playableClip.inOutConstraintMode = groupIndex == 0
                        ? KimodoInOutConstraintMode.None
                        : KimodoInOutConstraintMode.Outside;
                    playableClip.showConstraint = true;
                    playableClip.autoBeginAnchor = true;
                    EditorUtility.SetDirty(playableClip);
                }

                context.GeneratedTimelineClips.Add(timelineClip);
                lastTimelineClip = timelineClip;
            }

            MarkTimelineDirty(context);
            FocusTimeline(context.Director, lastTimelineClip != null ? lastTimelineClip.asset as UnityEngine.Object : context.Track);
            return true;
        }

        public static bool TryStartGenerateAnimations(
            KimodoNavMeshTimelineGenerationContext context,
            out string error)
        {
            error = string.Empty;

            if (context == null || context.Track == null)
            {
                error = "Timeline generation context is not ready.";
                return false;
            }

            List<KimodoPlayableClip> playableClips = CollectPlayableClips(context);
            if (playableClips.Count == 0)
            {
                error = "No KimodoPlayableClip instances were found on the generated track.";
                return false;
            }

            return command_generation_runner.Start(
                context.Track,
                $"navmesh-track:{KimodoUnityObjectIdUtility.NameKey(context.Track)}",
                command_kind.GenerateNavMeshTrackClips,
                async (handle, token) =>
                {
                    for (int i = 0; i < playableClips.Count; i++)
                    {
                        KimodoPlayableClip playableClip = playableClips[i];
                        string prefix = $"[{i + 1}/{playableClips.Count}] {playableClip.name}";
                        command_generation_runner.UpdateProgress(context.Track, handle.RequestId, KimodoBridgeCommandStage.InvokeBackend, $"Generating {prefix}...");

                        await KimodoPlayableClipGenerationExecutionService.GenerateAndFinalizeAsync(
                            playableClip,
                            externalConstraint: null,
                            (stage, message) => command_generation_runner.UpdateProgress(
                                context.Track,
                                handle.RequestId,
                                stage,
                                $"{prefix}: {message}"),
                            token);
                    }

                    if (playableClips.Count > 0)
                    {
                        FocusTimeline(context.Director, playableClips[playableClips.Count - 1]);
                    }

                    return command_noop_result.Instance;
                },
                out _,
                out error);
        }

        private static List<KimodoPlayableClip> CollectPlayableClips(KimodoNavMeshTimelineGenerationContext context)
        {
            var playableClips = new List<KimodoPlayableClip>();
            if (context == null || context.Track == null)
            {
                return playableClips;
            }

            IEnumerable<TimelineClip> clips = context.GeneratedTimelineClips.Count > 0
                ? context.GeneratedTimelineClips
                : context.Track.GetClips();

            foreach (TimelineClip timelineClip in clips)
            {
                if (timelineClip?.asset is KimodoPlayableClip playableClip)
                {
                    playableClips.Add(playableClip);
                }
            }

            return playableClips;
        }

        private static bool HasValidCharacterAnimator(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return false;
            }

            Animator animator = gameObject.GetComponentInChildren<Animator>(true);
            return IsValidHumanoidAnimator(animator);
        }

        private static bool IsValidHumanoidAnimator(Animator animator)
        {
            return animator != null &&
                   animator.gameObject.scene.IsValid() &&
                   animator.avatar != null &&
                   animator.avatar.isValid &&
                   animator.avatar.isHuman;
        }

        private static GeneratedConstraintKind GetConstraintKind(KimodoNavMeshConstraintPointType pointType)
        {
            switch (pointType)
            {
                case KimodoNavMeshConstraintPointType.LeftFoot:
                    return GeneratedConstraintKind.LeftFoot;
                case KimodoNavMeshConstraintPointType.RightFoot:
                    return GeneratedConstraintKind.RightFoot;
                default:
                    return GeneratedConstraintKind.Root2D;
            }
        }

        private static bool TryCreateConstraintMarker(
            KimodoNavMeshTimelineGenerationContext context,
            KimodoNavMeshConstraintPointType pointType,
            string modelName,
            KimodoNavMeshWaypointSample sample,
            double markerTime,
            out KimodoConstraintMarkerBase marker,
            out KimodoMarkerSampleResult markerSample,
            out string error)
        {
            marker = null;
            markerSample = null;
            error = string.Empty;

            if (context == null || context.Track == null || context.CharacterAnimator == null)
            {
                error = "Timeline generation context is incomplete.";
                return false;
            }

            switch (pointType)
            {
                case KimodoNavMeshConstraintPointType.LeftFoot:
                    marker = context.Track.CreateMarker<KimodoLeftFootConstraintMarker>(markerTime);
                    return TryBuildFootMarkerSample(modelName, sample, markerTime, true, out markerSample, out error);

                case KimodoNavMeshConstraintPointType.RightFoot:
                    marker = context.Track.CreateMarker<KimodoRightFootConstraintMarker>(markerTime);
                    return TryBuildFootMarkerSample(modelName, sample, markerTime, false, out markerSample, out error);

                default:
                    marker = context.Track.CreateMarker<KimodoRoot2DConstraintMarker>(markerTime);
                    markerSample = new KimodoMarkerSampleResult
                    {
                        constraintType = "root2d",
                        sampleTime = markerTime,
                        hasRootHeading = sample.HasHeading,
                        kimodoRootPosition = sample.Position,
                        unityRootPos = sample.Position,
                        unityRootRot = sample.HasHeading
                            ? Quaternion.LookRotation(new Vector3(sample.Heading.x, 0f, sample.Heading.y), Vector3.up)
                            : Quaternion.identity,
                        rootHeading = sample.HasHeading ? sample.Heading : Vector2.right,
                        jointNames = new List<string>(),
                        localAxisAngles = new List<Vector3>(),
                        sampledJointIndices = new List<int>()
                    };
                    return true;
            }
        }

        private static bool TryBuildFootMarkerSample(
            string modelName,
            KimodoNavMeshWaypointSample sample,
            double markerTime,
            bool isLeftFoot,
            out KimodoMarkerSampleResult markerSample,
            out string error)
        {
            markerSample = null;
            error = string.Empty;

            string resolvedModelName = KimodoMotionModelProfiles.NormalizeName(modelName);
            if (!TryResolveProfileFootOffset(resolvedModelName, isLeftFoot, out Vector3 footOffsetFromRoot, out int jointCount, out error))
            {
                return false;
            }

            var localAxisAngles = new List<Vector3>(jointCount);
            var sampledJointIndices = new List<int>(jointCount);
            for (int i = 0; i < jointCount; i++)
            {
                localAxisAngles.Add(Vector3.zero);
                sampledJointIndices.Add(i);
            }

            Vector3 rootPosition = sample.Position - footOffsetFromRoot;
            markerSample = new KimodoMarkerSampleResult
            {
                constraintType = isLeftFoot ? "left-foot" : "right-foot",
                sampleTime = markerTime,
                rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(resolvedModelName),
                hasRootHeading = false,
                kimodoRootPosition = rootPosition,
                unityRootPos = rootPosition,
                unityRootRot = Quaternion.identity,
                rootHeading = Vector2.right,
                jointNames = new List<string> { isLeftFoot ? "LeftFoot" : "RightFoot" },
                localAxisAngles = localAxisAngles,
                sampledJointIndices = sampledJointIndices
            };

            if (sample.HasHeading)
            {
                Vector3 forward = new Vector3(sample.Heading.x, 0f, sample.Heading.y);
                if (forward.sqrMagnitude > 1e-6f)
                {
                    Quaternion rootRotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
                    markerSample.unityRootRot = rootRotation;
                    markerSample.rootHeading = sample.Heading;

                    if (markerSample.localAxisAngles.Count > 0)
                    {
                        markerSample.localAxisAngles[0] = KimodoRuntimeUtility.QuaternionToAxisAngleVector(rootRotation);
                    }
                }
            }

            return true;
        }

        private static KimodoNavMeshConstraintPointType ResolveBoundaryAdjustedPointType(
            KimodoNavMeshRoutePlan routePlan,
            int groupIndex,
            double markerTime,
            KimodoNavMeshConstraintPointType pointType,
            float footBoundarySkipSeconds)
        {
            if (pointType != KimodoNavMeshConstraintPointType.LeftFoot &&
                pointType != KimodoNavMeshConstraintPointType.RightFoot)
            {
                return pointType;
            }

            if (routePlan == null || routePlan.Groups == null || routePlan.Groups.Count <= 1)
            {
                return pointType;
            }

            double threshold = Math.Max(0.0, footBoundarySkipSeconds);
            if (threshold <= 0.0)
            {
                return pointType;
            }

            if (groupIndex > 0)
            {
                double boundaryTime = routePlan.Groups[groupIndex].StartTimeSeconds;
                if (Math.Abs(markerTime - boundaryTime) < threshold)
                {
                    return KimodoNavMeshConstraintPointType.Root2D;
                }
            }

            if (groupIndex < routePlan.Groups.Count - 1)
            {
                double boundaryTime = routePlan.Groups[groupIndex].EndTimeSeconds;
                if (Math.Abs(markerTime - boundaryTime) < threshold)
                {
                    return KimodoNavMeshConstraintPointType.Root2D;
                }
            }

            return pointType;
        }

        private static bool TryResolveProfileFootOffset(
            string modelName,
            bool isLeftFoot,
            out Vector3 footOffsetFromRoot,
            out int jointCount,
            out string error)
        {
            footOffsetFromRoot = Vector3.zero;
            error = string.Empty;

            string[] modelJointNames = KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
            jointCount = modelJointNames != null ? modelJointNames.Length : 0;
            if (jointCount <= 0)
            {
                error = $"Model joint layout not found for '{modelName}'.";
                return false;
            }

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar avatar, out error))
            {
                return false;
            }

            GameObject tempRoot = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryCreateVirtualSkeleton(
                        avatar,
                        $"KimodoProfile_{modelName}_Temp",
                        animatorEnabled: false,
                        applyRootMotion: false,
                        out tempRoot,
                        out _,
                        out error))
                {
                    return false;
                }

                string profileRootName = KimodoRigProfileDatabase.GetProfileRootJointNameForModel(modelName);
                string profileFootName = ResolveProfileFootJointName(modelName, isLeftFoot);
                Transform profileRoot = KimodoRetargetAvatarUtility.FindTransformByName(tempRoot.transform, profileRootName);
                Transform profileFoot = KimodoRetargetAvatarUtility.FindTransformByName(tempRoot.transform, profileFootName);
                if (profileRoot == null)
                {
                    error = $"Profile root joint '{profileRootName}' not found for '{modelName}'.";
                    return false;
                }

                if (profileFoot == null)
                {
                    error = $"Profile foot joint '{profileFootName}' not found for '{modelName}'.";
                    return false;
                }

                footOffsetFromRoot = profileFoot.position - profileRoot.position;
                return true;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tempRoot);
            }
        }

        private static string ResolveProfileFootJointName(string modelName, bool isLeftFoot)
        {
            switch (KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName))
            {
                case KimodoConstraintRigType.G1:
                    return isLeftFoot ? "left_ankle_roll_skel" : "right_ankle_roll_skel";
                case KimodoConstraintRigType.Smplx:
                    return isLeftFoot ? "left_ankle" : "right_ankle";
                default:
                    return isLeftFoot ? "LeftFoot" : "RightFoot";
            }
        }

        private static void RegisterTimelineUndo(KimodoNavMeshTimelineGenerationContext context, string label)
        {
            if (context == null)
            {
                return;
            }

            var undoTargets = new List<UnityEngine.Object>();
            if (context.Track != null)
            {
                undoTargets.Add(context.Track);
            }

            if (context.TimelineAsset != null)
            {
                undoTargets.Add(context.TimelineAsset);
            }

            if (context.Director != null)
            {
                undoTargets.Add(context.Director);
            }

            if (undoTargets.Count > 0)
            {
                Undo.RegisterCompleteObjectUndo(undoTargets.ToArray(), label);
            }
        }

        private static bool TryEnsureTrackContext(
            GameObject characterRoot,
            GameObject timelineHost,
            KimodoNavMeshTimelineGenerationContext existingContext,
            bool allowContextReuse,
            out KimodoNavMeshTimelineGenerationContext context,
            out string error)
        {
            context = null;
            error = string.Empty;

            if (characterRoot == null)
            {
                error = "Character root is null.";
                return false;
            }

            Animator animator = characterRoot.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                error = $"Character '{characterRoot.name}' does not contain an Animator.";
                return false;
            }

            GameObject resolvedTimelineHost = timelineHost != null ? timelineHost : characterRoot;
            if (allowContextReuse && existingContext != null && existingContext.IsValidFor(characterRoot, resolvedTimelineHost))
            {
                existingContext.CharacterAnimator = animator;
                context = existingContext;
                return true;
            }

            PlayableDirector director = resolvedTimelineHost.GetComponent<PlayableDirector>();
            TimelineAsset timelineAsset = director != null ? director.playableAsset as TimelineAsset : null;
            AnimationTrack track;

            if (director == null || timelineAsset == null)
            {
                director = director != null ? director : Undo.AddComponent<PlayableDirector>(resolvedTimelineHost);
                timelineAsset = CreateTimelineAsset(resolvedTimelineHost.name);
                director.playableAsset = timelineAsset;
                track = timelineAsset.CreateTrack<AnimationTrack>(null, BuildTrackName());
            }
            else
            {
                MuteBoundAnimationTracks(timelineAsset, director, animator);
                track = timelineAsset.CreateTrack<AnimationTrack>(null, BuildTrackName());
            }

            director.SetGenericBinding(track, animator);
            context = new KimodoNavMeshTimelineGenerationContext
            {
                CharacterRoot = characterRoot,
                TimelineHost = resolvedTimelineHost,
                CharacterAnimator = animator,
                Director = director,
                TimelineAsset = timelineAsset,
                Track = track
            };

            MarkTimelineDirty(context);
            return true;
        }

        private static void MuteBoundAnimationTracks(TimelineAsset timelineAsset, PlayableDirector director, Animator animator)
        {
            if (timelineAsset == null || director == null || animator == null)
            {
                return;
            }

            foreach (TrackAsset outputTrack in timelineAsset.GetOutputTracks())
            {
                if (outputTrack is AnimationTrack animationTrack && director.GetGenericBinding(animationTrack) == animator)
                {
                    animationTrack.muted = true;
                    EditorUtility.SetDirty(animationTrack);
                }
            }
        }

        private static TimelineAsset CreateTimelineAsset(string characterName)
        {
            EnsureFolderExists("Assets/KimodoGeneratedClips");
            EnsureFolderExists(GeneratedTimelineFolder);

            string safeName = string.IsNullOrWhiteSpace(characterName) ? "Character" : characterName.Trim();
            string path = AssetDatabase.GenerateUniqueAssetPath(
                $"{GeneratedTimelineFolder}/{safeName}_NavMeshRoute_{DateTime.Now:yyyyMMdd_HHmmss_fff}.playable");

            var timelineAsset = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(timelineAsset, path);
            AssetDatabase.SaveAssets();
            return timelineAsset;
        }

        private static string BuildTrackName()
        {
            return $"{GeneratedTrackNamePrefix} {DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}";
        }

        private static void EnsureFolderExists(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            int slash = folderPath.LastIndexOf('/');
            string parent = slash > 0 ? folderPath.Substring(0, slash) : string.Empty;
            string leaf = slash > 0 ? folderPath.Substring(slash + 1) : folderPath;
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolderExists(parent);
            }

            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                AssetDatabase.CreateFolder(parent, leaf);
            }
        }

        private static List<Vector3> ExpandLongSegments(IReadOnlyList<Vector3> rawPathPoints)
        {
            var expanded = new List<Vector3>();
            if (rawPathPoints == null || rawPathPoints.Count == 0)
            {
                return expanded;
            }

            expanded.Add(rawPathPoints[0]);
            for (int i = 1; i < rawPathPoints.Count; i++)
            {
                Vector3 from = rawPathPoints[i - 1];
                Vector3 to = rawPathPoints[i];
                float estimatedDuration = EstimateSegmentDuration(Vector3.Distance(from, to));
                int splitCount = Mathf.Max(1, Mathf.CeilToInt(estimatedDuration / MaxSegmentDurationSeconds));
                for (int splitIndex = 1; splitIndex <= splitCount; splitIndex++)
                {
                    float t = splitIndex / (float)splitCount;
                    expanded.Add(Vector3.Lerp(from, to, t));
                }
            }

            return expanded;
        }

        private static KimodoNavMeshRoutePlan BuildGroupedPlan(IReadOnlyList<Vector3> expandedPoints, bool useHeading)
        {
            var routePlan = new KimodoNavMeshRoutePlan();
            if (expandedPoints == null || expandedPoints.Count < 2)
            {
                return routePlan;
            }

            var currentGroup = new KimodoNavMeshRouteGroup
            {
                StartTimeSeconds = 0f
            };
            currentGroup.Samples.Add(CreateWaypointSample(expandedPoints, 0, useHeading, 0f));

            float accumulatedStart = 0f;
            float currentDuration = 0f;

            for (int pointIndex = 1; pointIndex < expandedPoints.Count; pointIndex++)
            {
                float legDuration = EstimateSegmentDuration(Vector3.Distance(expandedPoints[pointIndex - 1], expandedPoints[pointIndex]));
                bool overflow = currentDuration + legDuration > MaxGroupDurationSeconds && currentGroup.Samples.Count > 1;
                if (overflow)
                {
                    currentGroup.DurationSeconds = Mathf.Max(MinSegmentDurationSeconds, currentDuration);
                    routePlan.Groups.Add(currentGroup);
                    accumulatedStart += currentGroup.DurationSeconds;

                    KimodoNavMeshWaypointSample previousGroupEndSample =
                        currentGroup.Samples[currentGroup.Samples.Count - 1];

                    currentGroup = new KimodoNavMeshRouteGroup
                    {
                        StartTimeSeconds = accumulatedStart
                    };

                    currentGroup.Samples.Add(CloneWaypointSample(previousGroupEndSample, 0f));
                    currentDuration = 0f;
                }

                currentDuration += legDuration;
                currentGroup.Samples.Add(CreateWaypointSample(expandedPoints, pointIndex, useHeading, currentDuration));
            }

            currentGroup.DurationSeconds = Mathf.Max(MinSegmentDurationSeconds, currentDuration);
            routePlan.Groups.Add(currentGroup);

            routePlan.TotalWaypointCount = expandedPoints.Count;
            routePlan.TotalDurationSeconds = accumulatedStart + currentGroup.DurationSeconds;
            return routePlan;
        }

        private static KimodoNavMeshWaypointSample CreateWaypointSample(
            IReadOnlyList<Vector3> points,
            int index,
            bool useHeading,
            float localTimeSeconds)
        {
            var sample = new KimodoNavMeshWaypointSample
            {
                Position = points[index],
                LocalTimeSeconds = localTimeSeconds,
                HasHeading = false,
                Heading = Vector2.right
            };

            if (!useHeading)
            {
                return sample;
            }

            if (TryResolveWaypointHeading(points, index, out Vector2 heading))
            {
                sample.HasHeading = true;
                sample.Heading = heading;
            }

            return sample;
        }

        private static KimodoNavMeshWaypointSample CloneWaypointSample(KimodoNavMeshWaypointSample source, float localTimeSeconds)
        {
            return new KimodoNavMeshWaypointSample
            {
                Position = source.Position,
                HasHeading = source.HasHeading,
                Heading = source.Heading,
                LocalTimeSeconds = localTimeSeconds
            };
        }

        private static float EstimateSegmentDuration(float distanceMeters)
        {
            float maxSpeed = Mathf.Max(0.01f, MaxSpeedMetersPerSecond);
            float maxAcceleration = Mathf.Max(0.01f, MaxAccelerationMetersPerSecond2);
            float accelTime = maxSpeed / maxAcceleration;
            float accelDistance = 0.5f * maxAcceleration * accelTime * accelTime;
            float durationSeconds;

            if (distanceMeters <= 2f * accelDistance)
            {
                durationSeconds = 2f * Mathf.Sqrt(Mathf.Max(distanceMeters, 0f) / maxAcceleration);
            }
            else
            {
                float cruiseDistance = distanceMeters - 2f * accelDistance;
                durationSeconds = 2f * accelTime + cruiseDistance / maxSpeed;
            }

            return Mathf.Clamp(durationSeconds, MinSegmentDurationSeconds, MaxSegmentDurationSeconds);
        }

        private static bool TryResolveWaypointHeading(IReadOnlyList<Vector3> points, int index, out Vector2 heading)
        {
            heading = Vector2.right;
            if (points == null || index < 0 || index >= points.Count)
            {
                return false;
            }

            Vector3 current = points[index];

            for (int nextIndex = index + 1; nextIndex < points.Count; nextIndex++)
            {
                if (TryNormalizePlanar(points[nextIndex] - current, out heading))
                {
                    return true;
                }
            }

            for (int previousIndex = index - 1; previousIndex >= 0; previousIndex--)
            {
                if (TryNormalizePlanar(current - points[previousIndex], out heading))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryNormalizePlanar(Vector3 delta, out Vector2 heading)
        {
            heading = new Vector2(delta.x, delta.z);
            if (heading.sqrMagnitude <= 1e-6f)
            {
                return false;
            }

            heading.Normalize();
            return true;
        }

        private static bool AreConstraintMarkersNear(
            Vector3 left,
            Vector3 right,
            GeneratedConstraintKind leftKind,
            GeneratedConstraintKind rightKind,
            float thresholdMeters = DuplicateWaypointThresholdMeters)
        {
            if (leftKind != rightKind)
            {
                return false;
            }

            Vector2 a = new Vector2(left.x, left.z);
            Vector2 b = new Vector2(right.x, right.z);
            return Vector2.Distance(a, b) <= thresholdMeters;
        }

        private static void ClearMarkers(AnimationTrack track, Predicate<IMarker> predicate)
        {
            if (track == null || predicate == null)
            {
                return;
            }

            var toDelete = new List<IMarker>();
            foreach (IMarker marker in track.GetMarkers())
            {
                if (predicate(marker))
                {
                    toDelete.Add(marker);
                }
            }

            for (int i = 0; i < toDelete.Count; i++)
            {
                track.DeleteMarker(toDelete[i]);
            }
        }

        private static void ClearTimelineClips(AnimationTrack track)
        {
            if (track == null)
            {
                return;
            }

            var clips = new List<TimelineClip>(track.GetClips());
            for (int i = 0; i < clips.Count; i++)
            {
                track.DeleteClip(clips[i]);
            }
        }

        private static void MarkTimelineDirty(KimodoNavMeshTimelineGenerationContext context)
        {
            if (context == null)
            {
                return;
            }

            if (context.Track != null)
            {
                EditorUtility.SetDirty(context.Track);
            }

            if (context.TimelineAsset != null)
            {
                EditorUtility.SetDirty(context.TimelineAsset);
            }

            if (context.Director != null)
            {
                EditorUtility.SetDirty(context.Director);
            }

            KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsAddedOrRemoved);
        }

        private static void FocusTimeline(PlayableDirector director, UnityEngine.Object selectionTarget)
        {
            if (director != null)
            {
                Selection.activeGameObject = director.gameObject;
            }

            EditorApplication.ExecuteMenuItem("Window/Sequencing/Timeline");

            if (selectionTarget != null)
            {
                EditorGUIUtility.PingObject(selectionTarget);
            }

            EditorApplication.delayCall += () =>
            {
                if (director != null && TimelineEditor.inspectedDirector == director)
                {
                    KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsAddedOrRemoved);
                }
            };
        }
    }
}
