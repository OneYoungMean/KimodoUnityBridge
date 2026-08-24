using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        private const double SessionFrameRate = 60.0;

        public static string PoseGet(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireTimelineSession(arguments);
            var source = new PoseLocator(
                RequiredStringValue(arguments, "source"),
                RequiredNonNegativeFrame(arguments, "frame"));
            bool fullData = arguments.Value<bool?>("full_data") ?? false;
            KimodoMarkerSampleResult sourceSample = ReadSampleResult(source, PoseGetCommand);
            TimelineCharacterRecord character = ResolvePoseCharacter(source);
            KimodoConstraintMarker marker = GetOrCreateCachedPose(
                character,
                source,
                sourceSample,
                overwriteExisting: true);
            SaveTimelineSession(session);
            JObject result = new JObject
            {
                ["source_pose"] = PoseLocatorJson(source.Source, source.Frame),
                ["cache_pose"] = PoseCacheLocatorJson(session, character, marker, source.Frame)
            };
            result["pose"] = fullData
                ? BuildPoseJson(marker.SampleData)
                : BuildCompactPose(marker.SampleData);
            return Ok(result);
        });

        public static string PoseSetRootTransform(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireTimelineSession(arguments);
            KimodoConstraintMarker marker = RequirePoseCacheMarker(arguments["pose"] as JObject, out TimelineCharacterRecord character, out int frame);
            JObject root = arguments["root"] as JObject ?? throw new InvalidOperationException("root must be an object.");
            KimodoMarkerSampleResult sample = marker.SampleData;
            if (root["position"] is JArray position)
            {
                sample.root2DOverride.t = ReadVector3(position, "root.position");
            }
            if (root["rotation"] is JArray rotation)
            {
                sample.root2DOverride.q = ReadQuaternion(rotation, "root.rotation");
            }
            if (root["position"] == null && root["rotation"] == null)
            {
                throw new InvalidOperationException("root must contain position and/or rotation.");
            }
            bool hasPosition = root["position"] is JArray;
            bool hasRotation = root["rotation"] is JArray;
            sample.enableMask ??= new KimodoConstraintMask();
            if (hasRotation && !hasPosition && !KimodoConstraintMask.IsActive(sample, "rootposition"))
            {
                throw new InvalidOperationException("root.rotation requires an existing or supplied root.position.");
            }
            sample.enableMask.rootPosition |= hasPosition;
            sample.enableMask.rootHeading = hasRotation && sample.enableMask.rootPosition;
            sample.validMask ??= new KimodoConstraintMask();
            sample.validMask.rootPosition |= hasPosition;
            sample.validMask.rootHeading = hasRotation && sample.validMask.rootPosition;
            marker.CommitSampleData();
            EditorUtility.SetDirty(marker);
            SaveTimelineSession(session);
            return Ok(new JObject
            {
                ["cache_pose"] = PoseCacheLocatorJson(session, character, marker, frame),
                ["pose"] = BuildPoseJson(sample)
            });
        });

        public static string PoseSetMuscle(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireTimelineSession(arguments);
            KimodoConstraintMarker marker = RequirePoseCacheMarker(arguments["pose"] as JObject, out TimelineCharacterRecord character, out int frame);
            JObject muscles = arguments["muscles"] as JObject ?? throw new InvalidOperationException("muscles must be an object.");
            if (!muscles.Properties().Any())
            {
                throw new InvalidOperationException("muscles must contain at least one channel.");
            }
            KimodoMarkerSampleResult sample = marker.SampleData;
            if (sample.sampleData == null || !sample.sampleData.IsValid)
            {
                throw new InvalidOperationException("Pose cache has no valid 70-value sampleData payload.");
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
                ["cache_pose"] = PoseCacheLocatorJson(session, character, marker, frame),
                ["pose"] = BuildPoseJson(sample)
            });
        });

        public static string PoseContract(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireTimelineSession(arguments);
            PoseLocator originLocator = RequireReadablePoseLocator(arguments["origin"] as JObject);
            PoseLocator targetLocator = RequireReadablePoseLocator(arguments["target"] as JObject);
            string mode = RequiredStringValue(arguments, "mode");
            if (mode != "align_target_root" && mode != "least_squares_root_fit")
            {
                throw new InvalidOperationException("mode must be align_target_root or least_squares_root_fit.");
            }
            string[] endEffectors = RequiredStringArray(arguments, "endeffectors", "left_hand", "right_hand", "left_foot", "right_foot");
            string[] components = RequiredStringArray(arguments, "components", "position", "rotation");
            KimodoMarkerSampleResult origin = ReadSampleResult(originLocator, PoseContractCommand);
            KimodoMarkerSampleResult target = ReadSampleResult(targetLocator, PoseContractCommand);
            TimelineCharacterRecord targetCharacter = ResolvePoseCharacter(targetLocator);

            Vector3 positionDelta = Vector3.zero;
            Quaternion rotationDelta = Quaternion.identity;
            int count = 0;
            foreach (string endEffector in endEffectors)
            {
                KimodoRigidTransform originTransform = GetEndEffector(origin, endEffector);
                KimodoRigidTransform targetTransform = GetEndEffector(target, endEffector);
                if (components.Contains("position"))
                {
                    positionDelta += originTransform.t - targetTransform.t;
                }
                if (components.Contains("rotation"))
                {
                    Quaternion delta = originTransform.q * Quaternion.Inverse(targetTransform.q);
                    rotationDelta = count == 0 ? delta : Quaternion.Slerp(rotationDelta, delta, 1f / (count + 1));
                }
                count++;
            }
            if (count == 0)
            {
                throw new InvalidOperationException("endeffectors must contain at least one item.");
            }
            if (components.Contains("position")) positionDelta /= count;
            KimodoMarkerSampleResult contracted = target.Clone();
            GetRootTransform(contracted, out Vector3 contractedRootPosition, out Quaternion contractedRootRotation);
            if (components.Contains("position")) contractedRootPosition += positionDelta;
            if (components.Contains("rotation")) contractedRootRotation = (rotationDelta * contractedRootRotation).normalized;
            if (KimodoConstraintMask.IsActive(contracted, "rootposition"))
            {
                contracted.rootOverride.t = contractedRootPosition;
                contracted.rootOverride.q = contractedRootRotation;
            }
            else
            {
                contracted.sampleData.SetRoot(contractedRootPosition, contractedRootRotation);
            }

            KimodoConstraintMarker marker = GetOrCreateCachedPose(targetCharacter, targetLocator, contracted, overwriteExisting: true);
            float residual = 0f;
            if (components.Contains("position"))
            {
                foreach (string endEffector in endEffectors)
                {
                    Vector3 originPosition = GetEndEffector(origin, endEffector).t;
                    Vector3 targetPosition = GetEndEffector(contracted, endEffector).t;
                    residual += Vector3.Distance(originPosition, targetPosition);
                }
                residual /= count;
            }
            SaveTimelineSession(session);
            return Ok(new JObject
            {
                ["cache_pose"] = PoseCacheLocatorJson(session, targetCharacter, marker, targetLocator.Frame),
                ["root_delta"] = new JObject
                {
                    ["position"] = new JArray(positionDelta.x, positionDelta.y, positionDelta.z),
                    ["yaw_degrees"] = components.Contains("rotation") ? rotationDelta.eulerAngles.y : 0f
                },
                ["residual_error"] = residual,
                ["constraint"] = new JObject
                {
                    ["origin"] = PoseLocatorJson(originLocator.Source, originLocator.Frame),
                    ["target"] = PoseLocatorJson(targetLocator.Source, targetLocator.Frame),
                    ["endeffectors"] = new JArray(endEffectors),
                    ["components"] = new JArray(components),
                    ["mode"] = mode
                }
            });
        });

        private static KimodoMarkerSampleResult ReadSampleResult(
            PoseLocator locator,
            string command = GenerateAnimationCommand)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord cacheOwner = ResolvePoseCacheOwner(locator.Source);
            if (cacheOwner != null)
            {
                KimodoConstraintMarker marker = FindCachedPose(
                    cacheOwner.PoseCacheTrack,
                    locator.Frame,
                    locator.MarkerId)
                    ?? throw new InvalidOperationException("Pose Cache source does not contain the requested marker.");
                return marker.SampleData.Clone();
            }

            if (TryResolveAnimationPoseSource(session, locator, out TimelineCharacterRecord animationCharacter, out int absoluteFrame))
            {
                ThrowIfGenerationRangeLocked(session, animationCharacter, absoluteFrame, absoluteFrame + 1, command);
                return CaptureSampleResult(animationCharacter, absoluteFrame);
            }

            KimodoConstraintMarker constraint = session.Characters
                .SelectMany(item => item.Track.GetMarkers().OfType<KimodoConstraintMarker>())
                .FirstOrDefault(item => string.Equals(item.name, locator.Source, StringComparison.OrdinalIgnoreCase) &&
                    Mathf.RoundToInt((float)(item.time * SessionFrameRate)) == locator.Frame)
                ?? throw new InvalidOperationException($"Pose source '{locator.Source}' was not found.");
            return constraint.SampleData.Clone();
        }

        private static KimodoMarkerSampleResult CaptureSampleResult(
            TimelineCharacterRecord character,
            int frame)
        {
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                throw new InvalidOperationException($"Character '{character.Name}' requires a valid humanoid Avatar for pose sampling.");
            }

            double sampleTime = frame / SessionFrameRate;
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

                float sourceTime = (float)KimodoMarkerSamplingUtility.ResolveAnimationSourceTime(
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
            var result = new KimodoMarkerSampleResult
            {
                sampleData = sample?.Clone() ?? new MuscleSample(),
                enableMask = new KimodoConstraintMask
                {
                    muscle = true,
                    rootTQ = true,
                    leftFootTQ = true,
                    rightFootTQ = true
                },
                validMask = new KimodoConstraintMask
                {
                    muscle = true,
                    rootTQ = true,
                    leftFootTQ = true,
                    rightFootTQ = true
                },
                constraintMode = "fullbody",
                sampleTime = sampleTime,
                enabled = true
            };
            KimodoRetargetMarkerSamplingUtility.CaptureWorldTargets(cache, result);
            return result;
        }

        private static bool TryResolveAnimationPoseSource(
            TimelineSessionRecord session,
            PoseLocator locator,
            out TimelineCharacterRecord character,
            out int absoluteFrame)
        {
            var matches = session.Characters
                .SelectMany(owner => owner.Animations
                    .Where(animation => string.Equals(animation.Name, locator.Source, StringComparison.OrdinalIgnoreCase))
                    .Select(animation => (owner, animation)))
                .ToArray();
            if (matches.Length == 0)
            {
                character = null;
                absoluteFrame = 0;
                return false;
            }
            if (matches.Length != 1)
                throw new InvalidOperationException($"Animation pose source '{locator.Source}' is ambiguous in the selected Session.");
            TimelineAnimationRecord animation = matches[0].animation;
            int startFrame = animation.TimelineClip != null
                ? Mathf.RoundToInt((float)(animation.TimelineStartSeconds * SessionFrameRate))
                : animation.StartFrame;
            int duration = animation.TimelineClip != null
                ? Math.Max(1, Mathf.RoundToInt((float)(animation.TimelineDurationSeconds * SessionFrameRate)))
                : Math.Max(1, animation.EndFrameExclusive - animation.StartFrame);
            if (locator.Frame < 0 || locator.Frame >= duration)
                throw new InvalidOperationException($"frame must be within animation '{animation.Name}' local range [0,{duration}).");
            character = matches[0].owner;
            absoluteFrame = startFrame + locator.Frame;
            return true;
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
                    float sourceTime = (float)KimodoMarkerSamplingUtility.ResolveAnimationSourceTime(timelineClip, timelineTime);
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

        private static KimodoConstraintMarker GetOrCreateCachedPose(
            TimelineCharacterRecord character,
            PoseLocator source,
            KimodoMarkerSampleResult sample,
            bool overwriteExisting = false)
        {
            RequireWritablePoseAvatar(character);
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }

            string markerId = BuildPoseMarkerId(RequireCurrentTimelineSession(), character, source);
            KimodoConstraintMarker marker = FindCachedPose(character.PoseCacheTrack, source.Frame, markerId);
            if (marker == null)
            {
                marker = character.PoseCacheTrack.CreateMarker<KimodoConstraintMarker>(source.Frame / SessionFrameRate);
                marker.name = markerId;
                overwriteExisting = true;
            }

            marker.MarkerType = KimodoConstraintMarkerType.External;
            marker.autoSample = false;
            marker.constraintEnabled = false;
            if (overwriteExisting)
            {
                KimodoMarkerSampleResult owned = sample.Clone();
                owned.sampleTime = source.Frame / SessionFrameRate;
                marker.SampleData = owned;
                marker.CommitSampleData();
            }
            EditorUtility.SetDirty(marker);
            EditorUtility.SetDirty(character.PoseCacheTrack);
            return marker;
        }

        private static KimodoConstraintMarker RequirePoseCacheMarker(
            JObject value,
            out TimelineCharacterRecord character,
            out int frame)
        {
            if (value == null)
            {
                throw new InvalidOperationException("pose must be a Pose Cache marker locator.");
            }
            string track = RequiredStringValue(value, "track");
            frame = RequiredNonNegativeFrame(value, "frame");
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            string sessionId = value.Value<string>("session_id");
            if (!string.IsNullOrWhiteSpace(sessionId) && !string.Equals(sessionId, session.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Pose Cache marker belongs to a different Session.");
            }
            character = session.Characters.FirstOrDefault(item => item.PoseCacheTrack != null &&
                string.Equals(item.PoseCacheTrack.name, track, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Pose Cache track '{track}' was not found.");
            string markerId = value.Value<string>("marker_id")?.Trim();
            KimodoConstraintMarker marker = FindCachedPose(character.PoseCacheTrack, frame, markerId)
                ?? throw new InvalidOperationException("Pose Cache marker was not found at the requested track/frame.");
            return marker;
        }

        private static string BuildPoseMarkerId(TimelineSessionRecord session, TimelineCharacterRecord character, PoseLocator source)
        {
            string text = string.Join("|", new[]
            {
                session.Id.ToString("D"),
                character.Name ?? string.Empty,
                source.Source ?? string.Empty,
                source.Frame.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
            using (SHA256 sha = SHA256.Create())
            {
                string hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty).ToLowerInvariant();
                return "pose_" + hash;
            }
        }

        private static JObject PoseCacheLocatorJson(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            KimodoConstraintMarker marker,
            int frame) => new JObject
            {
                ["session_id"] = session.Id.ToString("D"),
                ["track"] = character.PoseCacheTrack.name,
                ["frame"] = frame,
                ["marker_id"] = marker.name ?? string.Empty
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

        private static JObject FullTransformJson(KimodoRigidTransform transform) => new JObject
        {
            ["t"] = new JArray(transform.t.x, transform.t.y, transform.t.z),
            ["q"] = new JArray(transform.q.x, transform.q.y, transform.q.z, transform.q.w)
        };

        private static JObject CompactTransformJson(KimodoRigidTransform transform) => new JObject
        {
            ["position"] = new JArray(transform.t.x, transform.t.y, transform.t.z),
            ["rotation"] = new JArray(transform.q.x, transform.q.y, transform.q.z, transform.q.w)
        };

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

        private static string[] RequiredStringArray(JObject arguments, string name, params string[] allowed)
        {
            if (arguments?[name] is not JArray array || array.Count == 0)
            {
                throw new InvalidOperationException($"{name} must be a non-empty array.");
            }
            var values = new List<string>();
            foreach (JToken item in array)
            {
                string value = item.Value<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(value) || !allowed.Contains(value, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"{name} contains an unsupported value.");
                }
                if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
            }
            return values.ToArray();
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
            if (KimodoConstraintMask.IsActive(sample, "rootposition") && sample.rootOverride != null)
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

        private static KimodoConstraintMarker FindUntypedPose(AnimationTrack track, int frame) =>
            FindCachedPose(track, frame, null);

        private static KimodoConstraintMarker FindCachedPose(AnimationTrack track, int frame, string markerId) =>
            track.GetMarkers().OfType<KimodoConstraintMarker>().FirstOrDefault(marker =>
                Mathf.RoundToInt((float)(marker.time * SessionFrameRate)) == frame &&
                (string.IsNullOrWhiteSpace(markerId) || string.Equals(marker.name, markerId, StringComparison.Ordinal)));

        private static TimelineCharacterRecord ResolvePoseCacheOwner(string source)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            return session.Characters.FirstOrDefault(item => item.PoseCacheTrack != null &&
                    string.Equals(item.PoseCacheTrack.name, source, StringComparison.OrdinalIgnoreCase));
        }

        private static TimelineCharacterRecord ResolvePoseCharacter(PoseLocator locator)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord character = ResolvePoseCacheOwner(locator.Source);
            if (character != null) return character;
            if (TryResolveAnimationPoseSource(session, locator, out character, out _)) return character;
            character = session.Characters.FirstOrDefault(item => item.Track.GetMarkers()
                .OfType<KimodoConstraintMarker>()
                .Any(marker => string.Equals(marker.name, locator.Source, StringComparison.OrdinalIgnoreCase) &&
                    Mathf.RoundToInt((float)(marker.time * SessionFrameRate)) == locator.Frame));
            return character ?? throw new InvalidOperationException($"Pose source '{locator.Source}' has no owning character track.");
        }

        private static PoseLocator RequirePoseLocator(JObject value)
        {
            if (value == null) throw new InvalidOperationException("pose must be an object containing source and frame.");
            string source = RequiredStringValue(value, "source");
            int frame = RequiredNonNegativeFrame(value, "frame");
            return new PoseLocator(source, frame);
        }

        private static PoseLocator RequireReadablePoseLocator(JObject value)
        {
            if (value == null) throw new InvalidOperationException("pose must be an object.");
            if (value["source"] != null)
            {
                return RequirePoseLocator(value);
            }
            string track = RequiredStringValue(value, "track");
            int frame = RequiredNonNegativeFrame(value, "frame");
            string markerId = value.Value<string>("marker_id")?.Trim();
            RequirePoseCacheMarker(value, out _, out _);
            return new PoseLocator(track, frame, markerId);
        }

        private static int RequiredNonNegativeFrame(JObject value, string name)
        {
            if (value?[name]?.Type != JTokenType.Integer) throw new InvalidOperationException($"{name} must be an integer frame at 60 FPS.");
            int frame = value.Value<int>(name);
            if (frame < 0) throw new InvalidOperationException($"{name} must be non-negative.");
            return frame;
        }

        private static JObject PoseLocatorJson(string source, int frame) => new JObject { ["source"] = source, ["frame"] = frame };

        private readonly struct PoseLocator
        {
            public PoseLocator(string source, int frame, string markerId = null)
            {
                Source = source;
                Frame = frame;
                MarkerId = markerId;
            }
            public string Source { get; }
            public int Frame { get; }
            public string MarkerId { get; }
        }
    }
}
