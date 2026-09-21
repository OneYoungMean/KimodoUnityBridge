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

        public static string PoseGet(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            JObject source = arguments["source"] as JObject
                ?? throw new InvalidOperationException("source must be an object.");
            TimelineCharacterRecord character = ResolveSessionCharacterByReference(
                session,
                RequiredStringValue(source, "character"),
                addIfMissing: false);
            bool hasTimelineTime = source["timeline_time_seconds"] != null;
            bool hasClipTime = source["clip"] != null || source["clip_time_seconds"] != null;
            if (hasTimelineTime == hasClipTime)
            {
                throw new InvalidOperationException(
                    "source must use exactly one mode: timeline_time_seconds, or clip with clip_time_seconds.");
            }

            TimelineAnimationRecord animation = null;
            double timelineTime;
            double clipTime = double.NaN;
            if (hasTimelineTime)
            {
                timelineTime = ReadFiniteDouble(source["timeline_time_seconds"], "source.timeline_time_seconds");
                ValidateTimelineTime(character, timelineTime, "source.timeline_time_seconds");
            }
            else
            {
                animation = ResolveAnimation(
                    new JObject { ["animation"] = RequiredStringValue(source, "clip") },
                    character);
                clipTime = ReadFiniteDouble(source["clip_time_seconds"], "source.clip_time_seconds");
                double clipDuration = animation.TimelineDurationSeconds;
                if (clipTime < 0.0 || clipTime >= clipDuration - KimodoFrameTimeUtility.FrameTolerance)
                {
                    throw new InvalidOperationException(
                        $"source.clip_time_seconds must be inside clip '{animation.Name}' range [0,{clipDuration:F6}).");
                }
                timelineTime = animation.TimelineStartSeconds + clipTime;
            }

            int timelineFrame = (int)Math.Round(timelineTime * SessionFrameRate, MidpointRounding.AwayFromZero);
            int absoluteFrame = timelineFrame;
            ThrowIfGenerationRangeLocked(
                session,
                character,
                absoluteFrame,
                absoluteFrame + 1,
                PoseGetCommand);
            bool fullData = arguments.Value<bool?>("full_data") ?? false;
            KimodoMarkerSampleResult sourceSample = CaptureSampleResult(character, timelineTime);
            int index = AllocatePoseIndex(character.PoseCacheTrack);
            KimodoConstraintMarker marker = StoreExternalPose(character, index, sourceSample);
            SaveTimelineSession(session);
            JObject result = new JObject
            {
                ["pose"] = PoseReferenceJson(character.PoseCacheTrack.name, index),
                ["source"] = new JObject
                {
                    ["character"] = character.Name,
                    ["timeline_time_seconds"] = timelineTime,
                    ["timeline_frame_60"] = timelineFrame
                }
            };
            JObject resultSource = (JObject)result["source"];
            if (animation != null)
            {
                resultSource["clip"] = animation.Name;
                resultSource["clip_time_seconds"] = clipTime;
            }
            result["data"] = fullData
                ? BuildPoseJson(marker.SampleData)
                : BuildCompactPose(marker.SampleData);
            return Ok(result);
        });

        public static string PoseSetRootTransform(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            PoseReference reference = RequirePoseReference(arguments["pose"] as JObject);
            KimodoConstraintMarker marker = RequirePoseMarker(reference, out TimelineCharacterRecord character);
            JObject root = arguments["root"] as JObject ?? throw new InvalidOperationException("root must be an object.");
            KimodoMarkerSampleResult sample = marker.SampleData;
            ApplyPoseRootTransform(sample, root);
            marker.CommitSampleData();
            EditorUtility.SetDirty(marker);
            SaveTimelineSession(session);
            return Ok(new JObject
            {
                ["pose"] = PoseReferenceJson(character.PoseCacheTrack.name, reference.Index),
                ["data"] = BuildPoseJson(sample)
            });
        });

        public static string PoseSet(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            PoseReference reference = RequirePoseReference(arguments["pose"] as JObject);
            KimodoConstraintMarker marker = RequirePoseMarker(reference, out TimelineCharacterRecord character);
            JObject root = arguments["root"] as JObject;
            JObject muscles = arguments["muscles"] as JObject;
            JObject effectors = arguments["effector"] as JObject;
            if (root == null && muscles == null && effectors == null)
            {
                throw new InvalidOperationException("pose_set requires root, muscles, or effector.");
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
                if (!effectors.Properties().Any())
                    throw new InvalidOperationException("effector must contain at least one end effector.");
                foreach (JProperty property in effectors.Properties())
                {
                    JObject value = property.Value as JObject ?? throw new InvalidOperationException($"effector.{property.Name} must be an object.");
                    KimodoRigidTransform target = GetEndEffector(sample, property.Name);
                    if (value["position"] is JArray position) target.t = ReadVector3(position, $"effector.{property.Name}.position");
                    if (value["rotation"] is JArray rotation)
                    {
                        target.q = ReadQuaternion(rotation, $"effector.{property.Name}.rotation");
                        target.enableRotation = true;
                    }
                    else if (value["position"] != null)
                    {
                        target.enableRotation = false;
                    }
                    if (value["position"] == null && value["rotation"] == null)
                        throw new InvalidOperationException($"effector.{property.Name} must contain position or rotation.");
                    switch (property.Name)
                    {
                        case "left_hand": sample.effectors.leftHand = target; sample.validMask.leftHand = true; break;
                        case "right_hand": sample.effectors.rightHand = target; sample.validMask.rightHand = true; break;
                        case "left_foot": sample.effectors.leftFoot = target; sample.validMask.leftFoot = true; break;
                        case "right_foot": sample.effectors.rightFoot = target; sample.validMask.rightFoot = true; break;
                        default: throw new InvalidOperationException($"Unsupported end effector '{property.Name}'.");
                    }
                }
            }
            marker.CommitSampleData();
            EditorUtility.SetDirty(marker);
            SaveTimelineSession(session);
            return Ok(new JObject
            {
                ["pose"] = PoseReferenceJson(character.PoseCacheTrack.name, reference.Index),
                ["data"] = BuildPoseJson(sample)
            });
        });

        private static void ApplyPoseRootTransform(KimodoMarkerSampleResult sample, JObject root)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            if (root == null) throw new InvalidOperationException("root must be an object.");
            sample.rootOverride ??= KimodoRigidTransform.Identity;
            sample.validMask ??= new KimodoConstraintMask();
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
            bool hasPosition = root["position"] is JArray;
            bool hasRotation = root["rotation"] is JArray;
            if (hasRotation && !hasPosition && !sample.validMask.rootPosition)
            {
                throw new InvalidOperationException("root.rotation requires an existing or supplied root.position.");
            }
            sample.validMask.rootPosition |= hasPosition;
            sample.validMask.rootHeading |= hasRotation && sample.validMask.rootPosition;
        }

        public static string PoseSetMuscle(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            PoseReference reference = RequirePoseReference(arguments["pose"] as JObject);
            KimodoConstraintMarker marker = RequirePoseMarker(reference, out TimelineCharacterRecord character);
            JObject muscles = arguments["muscles"] as JObject ?? throw new InvalidOperationException("muscles must be an object.");
            if (!muscles.Properties().Any())
            {
                throw new InvalidOperationException("muscles must contain at least one channel.");
            }
            KimodoMarkerSampleResult sample = marker.SampleData;
            if (sample.sampleData == null || !sample.sampleData.IsValid)
            {
                throw new InvalidOperationException("Pose has no valid 70-value sampleData payload.");
            }
            foreach (JProperty property in muscles.Properties())
            {
                int index = ResolveCanonicalMuscleIndex(property.Name);
                float value = ReadFiniteFloat(property.Value, $"muscles.{property.Name}");
                sample.sampleData.data[index] = value;
            }
            marker.CommitSampleData();
            EditorUtility.SetDirty(marker);
            SaveTimelineSession(session);
            return Ok(new JObject
            {
                ["pose"] = PoseReferenceJson(character.PoseCacheTrack.name, reference.Index),
                ["data"] = BuildPoseJson(sample)
            });
        });

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
            string contextError = string.Empty;
            if (sourceClip == null || !KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    sourceClip,
                    out KimodoTimelineInOutConstraintContext context,
                    out contextError))
            {
                throw new InvalidOperationException($"Character '{character.Name}' has no retargetable Timeline clip: {contextError}");
            }

            if (KimodoMarkerSamplingUtility.TryResolveAnimationClipFromTimelineClip(
                    sourceClip,
                    out AnimationClip sourceAnimation,
                    out _))
            {
                return CaptureSampleResultFromSourceClip(
                    character,
                    sourceClip,
                    sourceAnimation,
                    sampleTime);
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
                return BuildCapturedSampleResult(samples[0], sampler.TargetCache, sampleTime);
            }
        }

        private static KimodoMarkerSampleResult CaptureSampleResultFromSourceClip(
            TimelineCharacterRecord character,
            TimelineClip timelineClip,
            AnimationClip sourceAnimation,
            double sampleTime)
        {
            RetargetSkeleton cache = null;
            KimodoRetargetClipSamplingUtility.ClipSamplingSession samplingSession = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        character.Avatar,
                        "KimodoPoseGetSampler",
                        out cache,
                        out string error))
                {
                    throw new InvalidOperationException($"Timeline pose sampling failed: {error}");
                }
                if (!KimodoRetargetClipSamplingUtility.ClipSamplingSession.TryCreate(
                        sourceAnimation,
                        cache,
                        "KimodoPoseGetSampler",
                        KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(sourceAnimation),
                        out samplingSession,
                        out error))
                {
                    throw new InvalidOperationException($"Timeline pose sampling failed: {error}");
                }

                Transform characterRoot = character.Animator != null
                    ? character.Animator.transform
                    : (character.Root != null ? character.Root.transform : null);
                if (characterRoot != null)
                {
                    cache.root.transform.SetPositionAndRotation(characterRoot.position, characterRoot.rotation);
                }

                double sourceTime = KimodoMarkerSamplingUtility.ResolveAnimationSourceTime(
                    timelineClip,
                    sampleTime);
                if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(
                        samplingSession.Context,
                        sourceTime,
                        out error))
                {
                    throw new InvalidOperationException($"Timeline pose sampling failed: {error}");
                }
                if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        cache,
                        out MuscleSample sample,
                        out error))
                {
                    throw new InvalidOperationException($"Timeline pose sampling failed: {error}");
                }
                return BuildCapturedSampleResult(sample, cache, sampleTime);
            }
            finally
            {
                samplingSession?.Dispose();
                cache?.Dispose();
            }
        }

        private static KimodoMarkerSampleResult BuildCapturedSampleResult(
            MuscleSample sample,
            RetargetSkeleton cache,
            double sampleTime)
        {
            bool hasSampleData = sample?.IsValid == true;
            var result = new KimodoMarkerSampleResult
            {
                sampleData = sample?.Clone() ?? new MuscleSample(),
                // A captured sample is a complete evaluated pose, not a user
                // constraint selection. The preview pipeline gates pose
                // application on active channels, so a captured sample must
                // advertise its full-body payload; channel validity stays
                // owned by validMask and CaptureWorldTargets below.
                enableMask = hasSampleData ? KimodoConstraintMask.ForType("fullbody") : new KimodoConstraintMask(),
                validMask = new KimodoConstraintMask
                {
                    muscle = hasSampleData,
                    rootTQ = hasSampleData,
                    leftFootTQ = hasSampleData,
                    rightFootTQ = hasSampleData
                },
                constraintMode = "constraint",
                sampleTime = sampleTime,
                enabled = true
            };
            KimodoRetargetMarkerSamplingUtility.CaptureWorldTargets(cache, result);
            return result;
        }

        private static KimodoMarkerSampleResult[] CaptureSampleResults(
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
            string contextError = string.Empty;
            if (sourceClip == null || !KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    sourceClip,
                    out KimodoTimelineInOutConstraintContext context,
                    out contextError))
            {
                throw new InvalidOperationException($"Character '{character.Name}' has no retargetable Timeline clip: {contextError}");
            }

            if (KimodoMarkerSamplingUtility.TryResolveAnimationClipFromTimelineClip(
                sourceClip,
                out AnimationClip sourceAnimation,
                out _))
            {
                return CaptureSampleResultsFromSourceClip(character, sourceClip, sourceAnimation, startFrame, frameCount);
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
                var sampleTimes = new double[frameCount];
                for (int index = 0; index < frameCount; index++)
                {
                    sampleTimes[index] = (startFrame + index) / SessionFrameRate;
                }
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
                    results[index] = BuildCapturedSampleResult(
                        samples[index],
                        sampler.TargetCache,
                        sampleTimes[index]);
                }
                return results;
            }
        }

        private static KimodoMarkerSampleResult[] CaptureSampleResultsFromSourceClip(
            TimelineCharacterRecord character,
            TimelineClip timelineClip,
            AnimationClip sourceAnimation,
            int startFrame,
            int frameCount)
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
                    throw new InvalidOperationException($"Timeline pose sampler failed: {error}");
                }
                if (!KimodoRetargetClipSamplingUtility.ClipSamplingSession.TryCreate(
                        sourceAnimation,
                        cache,
                        "KimodoSampleResultSampler",
                        KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(sourceAnimation),
                        out session,
                        out error))
                {
                    throw new InvalidOperationException($"Timeline pose sampler failed: {error}");
                }

                Transform characterRoot = character.Animator != null
                    ? character.Animator.transform
                    : (character.Root != null ? character.Root.transform : null);
                if (characterRoot != null)
                {
                    cache.root.transform.SetPositionAndRotation(characterRoot.position, characterRoot.rotation);
                }

                var results = new KimodoMarkerSampleResult[frameCount];
                for (int index = 0; index < frameCount; index++)
                {
                    double timelineTime = (startFrame + index) / SessionFrameRate;
                    double sourceTime = KimodoMarkerSamplingUtility.ResolveAnimationSourceTime(timelineClip, timelineTime);
                    if (!KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(
                            session.Context,
                            sourceTime,
                            out error))
                    {
                        throw new InvalidOperationException($"Timeline pose sampling failed: {error}");
                    }
                    if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(cache, out MuscleSample sample, out error))
                    {
                        throw new InvalidOperationException($"Timeline pose sampling failed: {error}");
                    }
                    results[index] = BuildCapturedSampleResult(sample, cache, timelineTime);
                }
                return results;
            }
            finally
            {
                session?.Dispose();
                cache?.Dispose();
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

        private static KimodoConstraintMarker StoreExternalPath(
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
            return marker;
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

        private static JObject BuildPoseJson(KimodoMarkerSampleResult sample)
        {
            ValidateCommandSample(sample);
            return new JObject
            {
                ["muscles"] = new JArray(sample.sampleData.data.Take(KimodoSampleDataLayout.BodyMuscleCount)),
                ["root"] = FullTransformJson(GetRootTransform(sample)),
                ["hands"] = new JObject
                {
                    ["left"] = FullTransformJson(GetEndEffector(sample, "left_hand")),
                    ["right"] = FullTransformJson(GetEndEffector(sample, "right_hand"))
                },
                ["feet"] = new JObject
                {
                    ["left"] = FullTransformJson(GetEndEffector(sample, "left_foot")),
                    ["right"] = FullTransformJson(GetEndEffector(sample, "right_foot"))
                }
            };
        }

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

        private static JObject FullTransformJson(KimodoRigidTransform transform)
        {
            var result = new JObject
            {
                ["t"] = new JArray(transform.t.x, transform.t.y, transform.t.z),
                ["q"] = new JArray(transform.q.x, transform.q.y, transform.q.z, transform.q.w),
                ["enable_rotation"] = transform.enableRotation
            };
            return result;
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
                "left_hand" => sample.effectors.leftHand?.Clone() ?? KimodoRigidTransform.Identity,
                "right_hand" => sample.effectors.rightHand?.Clone() ?? KimodoRigidTransform.Identity,
                "left_foot" => sample.effectors.leftFoot?.Clone() ?? KimodoRigidTransform.Identity,
                "right_foot" => sample.effectors.rightFoot?.Clone() ?? KimodoRigidTransform.Identity,
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
            if (sample.validMask?.rootPosition == true && sample.rootOverride != null)
            {
                position = sample.rootOverride.t;
                rotation = sample.rootOverride.q;
                return;
            }
            if (sample.sampleData == null || !sample.sampleData.IsValid)
            {
                throw new InvalidOperationException("SampleResult has no valid sampleData payload.");
            }
            sample.sampleData.GetRoot(out position, out rotation);
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

        private static double ReadFiniteDouble(JToken value, string name)
        {
            if (value == null || (value.Type != JTokenType.Integer && value.Type != JTokenType.Float))
            {
                throw new InvalidOperationException($"{name} must be a number.");
            }
            double result = value.Value<double>();
            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                throw new InvalidOperationException($"{name} must be finite.");
            }
            return result;
        }

        private static void ValidateTimelineTime(
            TimelineCharacterRecord character,
            double timelineTime,
            string name)
        {
            TimelineClip[] clips = character.Track != null
                ? character.Track.GetClips().ToArray()
                : Array.Empty<TimelineClip>();
            if (clips.Length == 0)
            {
                throw new InvalidOperationException($"Character '{character.Name}' has no Timeline clips.");
            }
            bool insideClip = clips.Any(item =>
                (timelineTime >= item.start - KimodoFrameTimeUtility.FrameTolerance) &&
                (timelineTime < item.end - KimodoFrameTimeUtility.FrameTolerance));
            if (!insideClip)
            {
                double start = clips.Min(item => item.start);
                double end = clips.Max(item => item.end);
                throw new InvalidOperationException(
                    $"{name} must be inside Character Timeline range [{start:F6},{end:F6}).");
            }
        }

        private static KimodoConstraintMarker FindUntypedPose(AnimationTrack track, int frame) =>
            FindPoseMarker(track, frame);

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
