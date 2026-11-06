using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using KimodoUnityBridge;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private const double SessionFrameRate = KimodoFrameTimeUtility.CommandFrameRate;

        public static string PoseGet(string argumentsJson) => Execute(argumentsJson, PoseGet);

        private static string PoseGet(JObject arguments)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            JObject source = arguments["source"] as JObject
                ?? throw new InvalidOperationException("source must be an object.");
            TimelineCharacterRecord character = ResolveSessionCharacterByReference(
                session,
                RequiredStringValue(source, "character"),
                addIfMissing: true);
            string sourceMode = source.Value<string>("mode") ?? string.Empty;
            bool hasScenePose = string.Equals(sourceMode, "scene_pose", StringComparison.OrdinalIgnoreCase);
            bool hasRestPose = string.Equals(sourceMode, "skeleton_rest_pose", StringComparison.OrdinalIgnoreCase);
            bool hasTimelineFrame = source["timeline_frame_60"] != null;
            if (new[] { hasTimelineFrame, hasScenePose, hasRestPose }.Count(value => value) != 1)
            {
                throw new InvalidOperationException(
                    "source must use exactly one mode: timeline_frame_60, mode='scene_pose', or mode='skeleton_rest_pose'.");
            }

            int timelineFrame = hasTimelineFrame
                ? RequiredNonNegativeFrame(source, "timeline_frame_60")
                : Mathf.RoundToInt((float)((session.Director != null ? session.Director.time : 0.0) * SessionFrameRate));
            if (timelineFrame == int.MaxValue)
                throw new InvalidOperationException("source.timeline_frame_60 is too large.");
            double timelineTime = timelineFrame / SessionFrameRate;
            ThrowIfGenerationRangeLocked(
                session,
                character,
                timelineFrame,
                timelineFrame + 1,
                PoseGetCommand);
            bool fullData = arguments.Value<bool?>("full_data") ?? false;
            KimodoMarkerSampleResult sourceSample;
            if (hasScenePose)
            {
                bool restorePreview = false;
                try
                {
                    restorePreview = KimodoTimelinePreviewRefreshUtility.TryDisablePreview(session.Director);
                    sourceSample = CaptureOriginalScenePose(character, restPose: false);
                }
                finally
                {
                    if (restorePreview && KimodoTimelinePreviewRefreshUtility.TryEnablePreview())
                        KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.SceneNeedsUpdate);
                }
            }
            else
            {
                sourceSample = hasRestPose
                    ? CaptureOriginalScenePose(character, restPose: true)
                    : CaptureSampleResult(session, character, timelineTime);
            }
            int index = AllocatePoseIndex(character.PoseCacheTrack);
            KimodoConstraintMarker marker = StoreExternalPose(character, index, sourceSample);
            SaveTimelineSession(session);
            JObject result = new JObject
            {
                ["pose"] = PoseReferenceJson(character.PoseCacheTrack.name, index),
                ["source"] = new JObject { ["character"] = character.Name },
                ["character_root"] = CharacterRootTransformJson(character.Root)
            };
            JObject resultSource = (JObject)result["source"];
            if (hasTimelineFrame) resultSource["timeline_frame_60"] = timelineFrame;
            else if (hasScenePose) resultSource["mode"] = "scene_pose";
            else if (hasRestPose) resultSource["mode"] = "skeleton_rest_pose";
            result["data"] = fullData
                ? KimodoConstraintPoseUtility.BuildPoseJson(marker.SampleData)
                : BuildCompactPose(marker.SampleData);
            return Ok(result);

        }

        private static KimodoMarkerSampleResult CaptureOriginalScenePose(TimelineCharacterRecord character, bool restPose)
        {
            GameObject sourceRoot = ResolveSourceCharacterRoot(character);
            Animator sourceAnimator = sourceRoot != null ? sourceRoot.GetComponentInChildren<Animator>(true) : null;
            KimodoLocalAvatarUtility.AvatarResolveResult avatarResult =
                KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(character?.Track, sourceAnimator);
            Avatar avatar = avatarResult.Avatar;
            if (!avatarResult.IsHumanoid || !KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
            {
                string avatarError = string.IsNullOrWhiteSpace(avatarResult.Error)
                    ? "A valid Humanoid Avatar is required."
                    : avatarResult.Error;
                throw new InvalidOperationException(
                    $"Pose sampling failed: {avatarError}");
            }
            if (!KimodoInOutConstraintTools.TryCaptureOriginalScenePose(
                    sourceRoot,
                    sourceAnimator,
                    avatar,
                    restPose,
                    character?.Name,
                    out KimodoMarkerSampleResult sample,
                    out string error))
            {
                throw new InvalidOperationException($"Pose sampling failed: {error}");
            }
            return sample;
        }

        public static string PoseSet(string argumentsJson) => Execute(argumentsJson, PoseSet);

        private static string PoseSet(JObject arguments)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            PoseReference reference = RequirePoseReference(arguments["pose"] as JObject);
            KimodoConstraintMarker marker = RequirePoseMarker(reference, out TimelineCharacterRecord character);
            JObject root = arguments["root"] as JObject;
            JObject muscles = arguments["muscles"] as JObject;
            JArray effectors = arguments["effector"] as JArray;
            if (root == null && muscles == null && effectors == null)
            {
                throw new InvalidOperationException("pose.set requires root, muscles, or effector.");
            }

            KimodoMarkerSampleResult sample = marker.SampleData;
            if (root != null)
            {
                ApplyPoseRootTransform(sample, root);
            }
            if (muscles != null)
            {
                if (!muscles.Properties().Any())
                {
                    throw new InvalidOperationException("muscles must contain at least one channel.");
                }
                if (sample.sampleData == null || !sample.sampleData.IsValid)
                {
                    throw new InvalidOperationException("Pose has no valid 70-value sampleData payload.");
                }
                foreach (JProperty property in muscles.Properties())
                {
                    int index = ResolveCanonicalMuscleIndex(property.Name);
                    sample.sampleData.data[index] = ReadFiniteFloat(property.Value, $"muscles.{property.Name}");
                }
            }
            if (effectors != null)
            {
                if (effectors.Count == 0)
                    throw new InvalidOperationException("effector must contain at least one end effector.");
                var seenJoints = new HashSet<string>(StringComparer.Ordinal);
                foreach (JObject value in effectors.OfType<JObject>())
                {
                    string joint = value.Value<string>("joint");
                    if (string.IsNullOrWhiteSpace(joint) || !seenJoints.Add(joint))
                        throw new InvalidOperationException($"effector contains duplicate or missing joint '{joint}'.");
                    KimodoRigidTransform target = GetEndEffector(sample, joint);
                    if (value["position"] is JArray position) target.t = ReadVector3(position, $"effector[{joint}].position");
                    if (value["rotation"] is JArray rotation)
                    {
                        target.q = ReadQuaternion(rotation, $"effector[{joint}].rotation");
                        target.enableRotation = true;
                    }
                    else if (value["position"] != null)
                    {
                        target.enableRotation = false;
                    }
                    if (value["position"] == null && value["rotation"] == null)
                        throw new InvalidOperationException($"effector[{joint}] must contain position or rotation.");
                    target.space = KimodoConstraintSpace.World;
                    switch (joint)
                    {
                        case "left_hand": sample.effectors.leftHand = target; sample.enableMask.leftHand = true; break;
                        case "right_hand": sample.effectors.rightHand = target; sample.enableMask.rightHand = true; break;
                        case "left_foot": sample.effectors.leftFoot = target; sample.enableMask.leftFoot = true; break;
                        case "right_foot": sample.effectors.rightFoot = target; sample.enableMask.rightFoot = true; break;
                        default: throw new InvalidOperationException($"Unsupported end effector '{joint}'.");
                    }
                }
            }
            sample.sampleValid = true;
            marker.CommitSampleData();
            EditorUtility.SetDirty(marker);
            SaveTimelineSession(session);
            return Ok(new JObject
            {
                ["pose"] = PoseReferenceJson(character.PoseCacheTrack.name, reference.Index),
                ["data"] = KimodoConstraintPoseUtility.BuildPoseJson(sample)
            });

        }

        private static void ApplyPoseRootTransform(KimodoMarkerSampleResult sample, JObject root)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            if (root == null) throw new InvalidOperationException("root must be an object.");
            bool hadRootOverride = sample.rootOverride != null;
            if (root["rotation"] is JArray && root["position"] == null && !hadRootOverride)
            {
                throw new InvalidOperationException("root.rotation requires an existing or supplied root.position.");
            }
            sample.rootOverride ??= KimodoRigidTransform.Identity;
            sample.enableMask ??= new KimodoConstraintMask();
            if (root["position"] is JArray position)
            {
                sample.rootOverride.t = ReadVector3(position, "root.position");
            }
            if (root["rotation"] is JArray rotation)
            {
                sample.rootOverride.q = ReadQuaternion(rotation, "root.rotation");
            }
            if (root["position"] == null && root["rotation"] == null)
            {
                throw new InvalidOperationException("root must contain position and/or rotation.");
            }
            bool hasRotation = root["rotation"] is JArray;
            if (hasRotation) sample.enableMask.rootHeading = true;
            sample.sampleValid = true;
        }

        private static KimodoMarkerSampleResult ReadPoseSample(
            PoseReference reference,
            string command = GenerateAnimationCommand)
        {
            KimodoConstraintMarker marker = RequirePoseMarker(reference, out _);
            if (marker.SampleData == null)
            {
                throw new InvalidOperationException(
                    $"{command} pose '{reference.Track}' index {reference.Index} has no sample data.");
            }
            return marker.SampleData.Clone();
        }

        private static KimodoMarkerSampleResult CaptureSampleResult(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            double sampleTime)
        {
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                throw new InvalidOperationException($"Character '{character.Name}' requires a valid humanoid Avatar for pose sampling.");
            }

            TimelineClip sourceClip = character.Track.GetClips()
                .FirstOrDefault(item =>
                    (sampleTime >= item.start ||
                        KimodoTimelinePreviewRefreshUtility.ApproximatelyTimelineTime(sampleTime, item.start)) &&
                    sampleTime <= item.end)
                ?? character.Track.GetClips().FirstOrDefault();
            if (sourceClip == null || !KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    sourceClip,
                    out KimodoTimelineInOutConstraintContext context,
                    out _))
            {
                return CaptureSceneSampleResult(session, character, sampleTime);
            }

            if (KimodoMarkerSamplingUtility.TryResolveAnimationClipFromTimelineClip(
                    sourceClip,
                    out AnimationClip sourceAnimation,
                    out _))
            {
                return CaptureSampleResultsFromSourceClip(
                    character,
                    sourceClip,
                    sourceAnimation,
                    new[] { sampleTime })[0];
            }

            string modelName = KimodoMotionModelProfiles.NormalizeName(context.ModelName);
            if (!KimodoTimelineSamplingSession.TryCreate(
                    context,
                    modelName,
                    out KimodoTimelineSamplingSession sampler,
                    out string sampleError))
            {
                throw new InvalidOperationException($"Timeline pose sampler failed: {sampleError}");
            }
            using (sampler)
            {
                if (!sampler.TryCaptureMuscleSamples(
                        new[] { sampleTime },
                        out MuscleSample[] samples,
                        out sampleError))
                {
                    throw new InvalidOperationException($"Timeline pose sampling failed: {sampleError}");
                }
                if (samples == null || samples.Length != 1 || samples[0] == null)
                {
                    throw new InvalidOperationException("Timeline pose sampling returned no sample.");
                }
                return KimodoConstraintPoseUtility.BuildCapturedSampleResult(samples[0], sampler.TargetCache, sampleTime);
            }
        }

        private static KimodoMarkerSampleResult CaptureSceneSampleResult(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            double sampleTime)
        {
            var context = new KimodoTimelineInOutConstraintContext
            {
                SourceClip = null,
                Track = character.Track,
                Director = session.Director,
                Animator = character.Animator,
                ModelName = KimodoMotionModelProfiles.DefaultModelName
            };
            if (!KimodoTimelineConstraintSampler.TrySampleMarker(
                    context,
                    sampleTime,
                    sampleTime,
                    "fullbody",
                    context.ModelName,
                    out KimodoMarkerSampleResult sceneSample,
                    out string sceneError))
            {
                throw new InvalidOperationException(
                    $"Scene-time pose sampling failed for character '{character.Name}': {sceneError}");
            }
            return sceneSample;
        }

        private static KimodoMarkerSampleResult[] CaptureSceneSampleResults(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            IReadOnlyList<double> sampleTimes)
        {
            var results = new KimodoMarkerSampleResult[sampleTimes.Count];
            for (int index = 0; index < sampleTimes.Count; index++)
            {
                results[index] = CaptureSceneSampleResult(session, character, sampleTimes[index]);
            }
            return results;
        }

        private static KimodoMarkerSampleResult[] CaptureSampleResultsFromSourceClip(
            TimelineCharacterRecord character,
            TimelineClip timelineClip,
            AnimationClip sourceAnimation,
            IReadOnlyList<double> sampleTimes)
        {
            RetargetSkeleton cache = null;
            KimodoRetargetClipSamplingUtility.ClipSamplingSession session = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        character.Avatar,
                        "KimodoSampleResultSampler",
                        out cache,
                        out string error))
                {
                    throw new InvalidOperationException($"Timeline pose sampling failed: {error}");
                }
                if (!KimodoRetargetClipSamplingUtility.ClipSamplingSession.TryCreate(
                        sourceAnimation,
                        cache,
                        "KimodoSampleResultSampler",
                        KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(sourceAnimation),
                        out session,
                        out error))
                {
                    throw new InvalidOperationException($"Timeline pose sampling failed: {error}");
                }

                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    character.Track,
                    character.Animator,
                    out _,
                    out _,
                    out bool isSceneOffset);
                if (isSceneOffset)
                {
                    Transform characterRoot = character.Animator != null
                        ? character.Animator.transform
                        : (character.Root != null ? character.Root.transform : null);
                    if (characterRoot != null)
                    {
                        cache.root.transform.SetPositionAndRotation(characterRoot.position, characterRoot.rotation);
                    }
                }

                var results = new KimodoMarkerSampleResult[sampleTimes.Count];
                for (int index = 0; index < sampleTimes.Count; index++)
                {
                    double timelineTime = sampleTimes[index];
                    double sourceTime = KimodoMarkerSamplingUtility.ResolveAnimationSourceTime(timelineClip, timelineTime);
                    if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(
                            session.Context,
                            sourceTime,
                            out error))
                    {
                        throw new InvalidOperationException($"Timeline pose sampling failed: {error}");
                    }
                    ApplyPoseSamplingWorldOffset(character, cache);
                    if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(cache, out MuscleSample sample, out error))
                    {
                        throw new InvalidOperationException($"Timeline pose sampling failed: {error}");
                    }
                    results[index] = KimodoConstraintPoseUtility.BuildCapturedSampleResult(sample, cache, timelineTime);
                }
                return results;
            }
            finally
            {
                session?.Dispose();
                cache?.Dispose();
            }
        }

        private static void ApplyPoseSamplingWorldOffset(
            TimelineCharacterRecord character,
            RetargetSkeleton cache)
        {
            if (cache?.root == null)
            {
                return;
            }

            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                character.Track,
                character.Animator,
                out Vector3 trackPosition,
                out Quaternion trackRotation,
                out bool isSceneOffset);
            if (isSceneOffset)
            {
                return;
            }
            Vector3 sampledPosition = cache.root.transform.position;
            Quaternion sampledRotation = cache.root.transform.rotation;

            KimodoTimelineTrackOffsetUtility.TrackToWorldPose(
                sampledPosition,
                sampledRotation,
                trackPosition,
                trackRotation,
                out Vector3 worldPosition,
                out Quaternion worldRotation);
            cache.root.transform.SetPositionAndRotation(worldPosition, worldRotation);
        }

        private static KimodoMarkerSampleResult[] CaptureSampleResults(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            int startFrame,
            int frameCount)
        {
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                throw new InvalidOperationException($"Character '{character.Name}' requires a valid humanoid Avatar for pose sampling.");
            }
            if (frameCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameCount));
            }

            double sampleTime = startFrame / SessionFrameRate;
            TimelineClip sourceClip = character.Track.GetClips()
                .FirstOrDefault(item =>
                    (sampleTime >= item.start ||
                        KimodoTimelinePreviewRefreshUtility.ApproximatelyTimelineTime(sampleTime, item.start)) &&
                    sampleTime <= item.end)
                ?? character.Track.GetClips().FirstOrDefault();
            var sampleTimes = new double[frameCount];
            for (int index = 0; index < frameCount; index++)
            {
                sampleTimes[index] = (startFrame + index) / SessionFrameRate;
            }
            if (sourceClip == null || !KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    sourceClip,
                    out KimodoTimelineInOutConstraintContext context,
                    out _))
            {
                return CaptureSceneSampleResults(session, character, sampleTimes);
            }

            if (KimodoMarkerSamplingUtility.TryResolveAnimationClipFromTimelineClip(
                sourceClip,
                out AnimationClip sourceAnimation,
                out _))
            {
                return CaptureSampleResultsFromSourceClip(character, sourceClip, sourceAnimation, sampleTimes);
            }

            string modelName = KimodoMotionModelProfiles.NormalizeName(context.ModelName);
            if (!KimodoTimelineSamplingSession.TryCreate(
                    context,
                    modelName,
                    out KimodoTimelineSamplingSession sampler,
                    out string sampleError))
            {
                throw new InvalidOperationException($"Timeline pose sampler failed: {sampleError}");
            }
            using (sampler)
            {
                if (!sampler.TryCaptureMuscleSamples(
                        sampleTimes,
                        out MuscleSample[] samples,
                        out sampleError))
                {
                    throw new InvalidOperationException($"Timeline pose sampling failed: {sampleError}");
                }

                var results = new KimodoMarkerSampleResult[samples.Length];
                for (int index = 0; index < samples.Length; index++)
                {
                    results[index] = KimodoConstraintPoseUtility.BuildCapturedSampleResult(
                        samples[index],
                        sampler.TargetCache,
                        sampleTimes[index]);
                }
                return results;
            }
        }

        private static void RequireWritablePoseAvatar(TimelineCharacterRecord character)
        {
            if (character == null || !KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                throw new InvalidOperationException("Pose commands require a valid humanoid character Avatar.");
            }
        }

        private static KimodoConstraintMarker StoreExternalPose(
            TimelineCharacterRecord character,
            int index,
            KimodoMarkerSampleResult sample)
        {
            RequireWritablePoseAvatar(character);
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }
            if (FindPoseMarker(character.PoseCacheTrack, index) != null)
            {
                throw new InvalidOperationException(
                    $"Pose track '{character.PoseCacheTrack.name}' already contains index {index}.");
            }
            KimodoConstraintMarker marker = character.PoseCacheTrack.CreateMarker<KimodoConstraintMarker>(
                index / SessionFrameRate);
            marker.name = $"Pose_{index}";
            marker.MarkerType = KimodoConstraintMarkerType.External;
            marker.autoSample = false;
            marker.constraintEnabled = false;
            KimodoMarkerSampleResult owned = sample.Clone();
            owned.sampleTime = index / SessionFrameRate;
            marker.SampleData = owned;
            marker.CommitSampleData();
            EditorUtility.SetDirty(marker);
            EditorUtility.SetDirty(character.PoseCacheTrack);
            return marker;
        }

        private static void StoreExternalPath(
            TimelineCharacterRecord character,
            int index,
            KimodoRootPathData path)
        {
            if (character?.PoseCacheTrack == null)
            {
                throw new InvalidOperationException("Character does not have a Pose Track.");
            }
            if (FindPoseMarker(character.PoseCacheTrack, index) != null)
            {
                throw new InvalidOperationException(
                    $"Pose track '{character.PoseCacheTrack.name}' already contains index {index}.");
            }
            KimodoConstraintMarker marker = character.PoseCacheTrack.CreateMarker<KimodoConstraintMarker>(
                index / SessionFrameRate);
            marker.name = $"Path_{index}";
            marker.MarkerType = KimodoConstraintMarkerType.ExternalPath;
            marker.autoSample = false;
            marker.constraintEnabled = false;
            marker.PathData = path;
            marker.CommitSampleData();
            EditorUtility.SetDirty(marker);
            EditorUtility.SetDirty(character.PoseCacheTrack);
        }

        private static JObject BuildPathJson(KimodoRootPathData path) => new JObject
        {
            ["type"] = path.type,
            ["length"] = path.length,
            ["source_human_scale"] = path.sourceHumanScale,
            ["inverse"] = path.inverse,
            ["knots"] = new JArray((path.knots ?? new List<KimodoRootPathKnot>()).Select(knot =>
            {
                var result = new JObject
                {
                    ["frame"] = knot.frame,
                    ["position"] = new JArray(knot.position.x, knot.position.y)
                };
                if (knot.hasHeading) result["heading"] = new JArray(knot.heading.x, knot.heading.y);
                if (knot.hasTangentIn) result["tangent_in"] = new JArray(knot.tangentIn.x, knot.tangentIn.y);
                if (knot.hasTangentOut) result["tangent_out"] = new JArray(knot.tangentOut.x, knot.tangentOut.y);
                return result;
            }))
        };

        private static int AllocatePoseIndex(AnimationTrack track)
        {
            var occupied = new HashSet<int>(track.GetMarkers()
                .OfType<KimodoConstraintMarker>()
                .Select(marker => Mathf.RoundToInt((float)(marker.time * SessionFrameRate))));
            int index = 0;
            while (occupied.Contains(index)) index++;
            return index;
        }

        private static KimodoConstraintMarker RequirePoseMarker(
            PoseReference reference,
            out TimelineCharacterRecord character)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            character = session.Characters.FirstOrDefault(item => item.PoseCacheTrack != null &&
                string.Equals(item.PoseCacheTrack.name, reference.Track, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Pose track '{reference.Track}' was not found in the current scene context.");
            KimodoConstraintMarker marker = FindPoseMarker(character.PoseCacheTrack, reference.Index)
                ?? throw new InvalidOperationException(
                    $"Pose track '{reference.Track}' does not contain index {reference.Index}.");
            if (marker.MarkerType != KimodoConstraintMarkerType.External)
            {
                throw new InvalidOperationException(
                    $"Pose track '{reference.Track}' index {reference.Index} is not an External Pose.");
            }
            return marker;
        }

        private static KimodoConstraintMarker RequirePathMarker(PoseReference reference)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord character = session.Characters.FirstOrDefault(item => item.PoseCacheTrack != null &&
                string.Equals(item.PoseCacheTrack.name, reference.Track, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Pose track '{reference.Track}' was not found in the current scene context.");
            KimodoConstraintMarker marker = FindPoseMarker(character.PoseCacheTrack, reference.Index)
                ?? throw new InvalidOperationException(
                    $"Pose track '{reference.Track}' does not contain index {reference.Index}.");
            if (!marker.IsExternalPath || marker.PathData == null)
            {
                throw new InvalidOperationException(
                    $"Pose track '{reference.Track}' index {reference.Index} is not an External Path.");
            }
            return marker;
        }

        private static JObject PoseReferenceJson(string track, int index) => new JObject
        {
            ["track"] = track,
            ["index"] = index
        };

        private static JObject BuildCompactPose(KimodoMarkerSampleResult sample)
        {
            ValidateCommandSample(sample);
            return new JObject
            {
                ["root"] = CompactTransformJson(GetRootTransform(sample)),
                ["hands"] = new JObject
                {
                    ["left"] = CompactTransformJson(GetEndEffector(sample, "left_hand")),
                    ["right"] = CompactTransformJson(GetEndEffector(sample, "right_hand"))
                },
                ["feet"] = new JObject
                {
                    ["left"] = CompactTransformJson(GetEndEffector(sample, "left_foot")),
                    ["right"] = CompactTransformJson(GetEndEffector(sample, "right_foot"))
                }
            };
        }

        private static void ValidateCommandSample(KimodoMarkerSampleResult sample)
        {
            if (sample?.sampleData == null || !sample.sampleData.IsValid)
            {
                throw new InvalidOperationException("Pose source has no valid 70-value sampleData payload.");
            }
        }

        private static JObject CharacterRootTransformJson(GameObject characterRoot)
        {
            if (characterRoot == null)
                throw new InvalidOperationException("Pose character has no scene root Transform.");
            Transform root = characterRoot.transform;
            return new JObject
            {
                ["position"] = new JArray(root.position.x, root.position.y, root.position.z),
                ["rotation"] = new JArray(root.rotation.x, root.rotation.y, root.rotation.z, root.rotation.w),
                ["space"] = "world"
            };
        }

        private static JObject CompactTransformJson(KimodoRigidTransform transform)
        {
            var result = new JObject
            {
                ["position"] = new JArray(transform.t.x, transform.t.y, transform.t.z),
                ["enable_rotation"] = transform.enableRotation
            };
            if (transform.enableRotation)
                result["rotation"] = new JArray(transform.q.x, transform.q.y, transform.q.z, transform.q.w);
            return result;
        }

        private static readonly int[] CanonicalMuscleIndices = Enumerable.Range(0, 15)
            .Concat(Enumerable.Range(21, 34)).ToArray();

        private static int ResolveCanonicalMuscleIndex(string name)
        {
            for (int index = 0; index < CanonicalMuscleIndices.Length; index++)
            {
                int humanIndex = CanonicalMuscleIndices[index];
                if (humanIndex < HumanTrait.MuscleName.Length &&
                    string.Equals(HumanTrait.MuscleName[humanIndex], name, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
            throw new InvalidOperationException($"Unknown canonical muscle '{name}'.");
        }

        private static KimodoRigidTransform GetEndEffector(
            KimodoMarkerSampleResult sample,
            string endEffector)
        {
            if (sample?.effectors == null)
            {
                throw new InvalidOperationException("SampleResult has no effector payload.");
            }
            return endEffector switch
            {
                "left_hand" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.leftHand, sample.rootOverride),
                "right_hand" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.rightHand, sample.rootOverride),
                "left_foot" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.leftFoot, sample.rootOverride),
                "right_foot" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.rightFoot, sample.rootOverride),
                _ => throw new InvalidOperationException($"Unsupported end effector '{endEffector}'.")
            };
        }

        private static KimodoRigidTransform GetRootTransform(KimodoMarkerSampleResult sample)
        {
            GetRootTransform(sample, out Vector3 position, out Quaternion rotation);
            return new KimodoRigidTransform { t = position, q = rotation };
        }

        private static void GetRootTransform(
            KimodoMarkerSampleResult sample,
            out Vector3 position,
            out Quaternion rotation)
        {
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }
            if (sample.rootOverride != null)
            {
                position = sample.rootOverride.t;
                rotation = sample.rootOverride.q;
                return;
            }
            // Muscle Root TQ is an internal Humanoid transport channel. It is
            // not an implicit fallback for the command-level root field.
            position = Vector3.zero;
            rotation = Quaternion.identity;
        }

        private static Vector3 ReadVector3(JArray value, string name)
        {
            if (value == null || value.Count != 3) throw new InvalidOperationException($"{name} must be [x,y,z].");
            return new Vector3(ReadFiniteFloat(value[0], name + "[0]"), ReadFiniteFloat(value[1], name + "[1]"), ReadFiniteFloat(value[2], name + "[2]"));
        }

        private static Quaternion ReadQuaternion(JArray value, string name)
        {
            if (value == null || value.Count != 4) throw new InvalidOperationException($"{name} must be [x,y,z,w].");
            var result = new Quaternion(ReadFiniteFloat(value[0], name + "[0]"), ReadFiniteFloat(value[1], name + "[1]"), ReadFiniteFloat(value[2], name + "[2]"), ReadFiniteFloat(value[3], name + "[3]"));
            float magnitudeSquared = result.x * result.x + result.y * result.y + result.z * result.z + result.w * result.w;
            if (magnitudeSquared <= 1e-8f) throw new InvalidOperationException($"{name} must be non-zero.");
            return result.normalized;
        }

        private static float ReadFiniteFloat(JToken value, string name)
        {
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float)) throw new InvalidOperationException($"{name} must be a number.");
            float result = value.Value<float>();
            if (float.IsNaN(result) || float.IsInfinity(result)) throw new InvalidOperationException($"{name} must be finite.");
            return result;
        }

        private static KimodoConstraintMarker FindPoseMarker(AnimationTrack track, int index) =>
            track.GetMarkers().OfType<KimodoConstraintMarker>().FirstOrDefault(marker =>
                Mathf.RoundToInt((float)(marker.time * SessionFrameRate)) == index);

        private static PoseReference RequirePoseReference(JObject value)
        {
            if (value == null)
            {
                throw new InvalidOperationException("pose must be an object containing track and index.");
            }
            return new PoseReference(
                RequiredStringValue(value, "track"),
                RequiredNonNegativeFrame(value, "index"));
        }

        private static int RequiredNonNegativeFrame(JObject value, string name)
        {
            if (value?[name]?.Type != JTokenType.Integer) throw new InvalidOperationException($"{name} must be an integer frame at 60 FPS.");
            int frame = value.Value<int>(name);
            if (frame < 0) throw new InvalidOperationException($"{name} must be non-negative.");
            return frame;
        }

        private readonly struct PoseReference
        {
            public PoseReference(string track, int index)
            {
                Track = track;
                Index = index;
            }
            public string Track { get; }
            public int Index { get; }
        }
    }
}
