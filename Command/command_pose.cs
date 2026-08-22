using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CharacterAnimationCli.Unity;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace CharacterAnimationCli.Unity.Command
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
            JObject sourceResult = ReadPose(source, PoseGetCommand);
            CharacterPose pose = CharacterPoseJson.Parse(sourceResult["data"] as JObject
                ?? throw new InvalidOperationException("Source pose data is unavailable."));
            TimelineCharacterRecord character = ResolvePoseCharacter(source);
            KimodoConstraintMarker marker = GetOrCreateCachedPose(character, source, pose);
            SaveTimelineSession(session);
            JObject result = new JObject
            {
                ["source_pose"] = PoseLocatorJson(source.Source, source.Frame),
                ["cache_pose"] = PoseCacheLocatorJson(session, character, marker, source.Frame)
            };
            result["pose"] = fullData ? CharacterPoseJson.ToJson(RequireCanonicalPose(marker.SampleData)) : BuildCompactPose(RequireCanonicalPose(marker.SampleData));
            return Ok(result);
        });

        public static string PoseSetRootTransform(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireTimelineSession(arguments);
            KimodoConstraintMarker marker = RequirePoseCacheMarker(arguments["pose"] as JObject, out TimelineCharacterRecord character, out int frame);
            JObject root = arguments["root"] as JObject ?? throw new InvalidOperationException("root must be an object.");
            KimodoMarkerSampleResult sample = marker.SampleData;
            CharacterPose pose = RequireCanonicalPose(sample).Clone();
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
            sample.enableMask ??= new KimodoSampleChannelMask();
            if (hasRotation && !hasPosition && !sample.enableMask.root2DPosition)
            {
                throw new InvalidOperationException("root.rotation requires an existing or supplied root.position.");
            }
            sample.enableMask.root2DPosition |= hasPosition;
            sample.enableMask.root2DHeading = hasRotation && sample.enableMask.root2DPosition;
            pose.root.t = sample.root2DOverride.t;
            pose.root.q = sample.root2DOverride.q;
            marker.CommitSampleData();
            EditorUtility.SetDirty(marker);
            SaveTimelineSession(session);
            return Ok(new JObject
            {
                ["cache_pose"] = PoseCacheLocatorJson(session, character, marker, frame),
                ["pose"] = CharacterPoseJson.ToJson(pose)
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
            CharacterPose pose = RequireCanonicalPose(marker.SampleData).Clone();
            foreach (JProperty property in muscles.Properties())
            {
                int index = ResolveCanonicalMuscleIndex(property.Name);
                float value = ReadFiniteFloat(property.Value, $"muscles.{property.Name}");
                pose.muscles[index] = value;
            }
            SetCanonicalPose(marker.SampleData, pose, character);
            marker.CommitSampleData();
            EditorUtility.SetDirty(marker);
            SaveTimelineSession(session);
            return Ok(new JObject
            {
                ["cache_pose"] = PoseCacheLocatorJson(session, character, marker, frame),
                ["pose"] = CharacterPoseJson.ToJson(pose)
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
            JObject originResult = ReadPose(originLocator, PoseContractCommand);
            JObject targetResult = ReadPose(targetLocator, PoseContractCommand);
            CharacterPose origin = CharacterPoseJson.Parse(originResult["data"] as JObject);
            CharacterPose target = CharacterPoseJson.Parse(targetResult["data"] as JObject);
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
                    positionDelta += (origin.root.t + originTransform.t) - (target.root.t + targetTransform.t);
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
            CharacterPose contracted = target.Clone();
            if (components.Contains("position")) contracted.root.t += positionDelta;
            if (components.Contains("rotation")) contracted.root.q = (rotationDelta * contracted.root.q).normalized;

            KimodoConstraintMarker marker = GetOrCreateCachedPose(targetCharacter, targetLocator, contracted, overwriteExisting: true);
            float residual = 0f;
            if (components.Contains("position"))
            {
                foreach (string endEffector in endEffectors)
                {
                    Vector3 originPosition = origin.root.t + GetEndEffector(origin, endEffector).t;
                    Vector3 targetPosition = contracted.root.t + GetEndEffector(contracted, endEffector).t;
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

        private static JObject ReadPose(PoseLocator locator, string command = GenerateAnimationCommand)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord cacheOwner = ResolvePoseCacheOwner(locator.Source);
            if (cacheOwner != null)
            {
                KimodoConstraintMarker marker = FindCachedPose(cacheOwner.PoseCacheTrack, locator.Frame, locator.MarkerId)
                    ?? throw new InvalidOperationException("Pose Cache source does not contain the requested marker.");
                return new JObject
                {
                    ["pose"] = PoseLocatorJson(cacheOwner.PoseCacheTrack.name, locator.Frame),
                    ["data"] = CharacterPoseJson.ToJson(RequireCanonicalPose(marker.SampleData))
                };
            }
            if (TryResolveAnimationPoseSource(session, locator, out TimelineCharacterRecord animationCharacter, out int absoluteFrame))
            {
                ThrowIfGenerationRangeLocked(session, animationCharacter, absoluteFrame, absoluteFrame + 1, command);
                return new JObject
                {
                    ["pose"] = PoseLocatorJson(locator.Source, locator.Frame),
                    ["data"] = CharacterPoseJson.ToJson(CaptureCharacterPose(animationCharacter, absoluteFrame))
                };
            }
            KimodoConstraintMarker constraint = session.Characters
                .SelectMany(item => item.Track.GetMarkers().OfType<KimodoConstraintMarker>())
                .FirstOrDefault(item => string.Equals(item.name, locator.Source, StringComparison.OrdinalIgnoreCase) &&
                    Mathf.RoundToInt((float)(item.time * SessionFrameRate)) == locator.Frame)
                ?? throw new InvalidOperationException($"Pose source '{locator.Source}' was not found.");
            return new JObject
            {
                ["pose"] = PoseLocatorJson(constraint.name, locator.Frame),
                ["data"] = CharacterPoseJson.ToJson(RequireCanonicalPose(constraint.SampleData))
            };
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

        private static CharacterPose CaptureCharacterPose(TimelineCharacterRecord character, int frame)
        {
            return CaptureCharacterPoses(character, frame, 1)[0];
        }

        private static CharacterPose[] CaptureCharacterPoses(
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
                return CaptureCharacterPosesFromSourceClip(character, sourceClip, sourceAnimation, startFrame, frameCount);
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

                var poses = new CharacterPose[samples.Length];
                for (int index = 0; index < samples.Length; index++)
                {
                    poses[index] = CharacterPoseMuscleAdapter.FromMuscleSample(samples[index], sampler.TargetCache);
                }
                return poses;
            }
        }

        private static CharacterPose[] CaptureCharacterPosesFromSourceClip(
            TimelineCharacterRecord character,
            TimelineClip timelineClip,
            AnimationClip sourceAnimation,
            int startFrame,
            int frameCount)
        {
            SkeletonCache cache = null;
            KimodoRetargetClipSamplingUtility.ClipSamplingSession session = null;
            try
            {
                if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        character.Avatar,
                        "KimodoCharacterPoseSampler",
                        out cache,
                        out string error))
                {
                    throw new InvalidOperationException($"Timeline pose sampler failed: {error}");
                }
                if (!KimodoRetargetClipSamplingUtility.ClipSamplingSession.TryCreate(
                        sourceAnimation,
                        cache,
                        "KimodoCharacterPoseSampler",
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

                var poses = new CharacterPose[frameCount];
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
                    poses[index] = CharacterPoseMuscleAdapter.FromMuscleSample(sample, cache);
                }
                return poses;
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
            CharacterPose pose,
            bool overwriteExisting = false)
        {
            RequireWritablePoseAvatar(character);
            string markerId = BuildPoseMarkerId(RequireCurrentTimelineSession(), character, source);
            KimodoConstraintMarker marker = FindCachedPose(character.PoseCacheTrack, source.Frame, markerId);
            if (marker == null)
            {
                marker = character.PoseCacheTrack.CreateMarker<KimodoConstraintMarker>(source.Frame / SessionFrameRate);
                marker.name = markerId;
                marker.autoSample = false;
                marker.constraintEnabled = true;
                overwriteExisting = true;
            }
            if (overwriteExisting)
            {
                SetCanonicalPose(marker.SampleData, pose, character);
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

        private static JObject BuildCompactPose(CharacterPose pose) => new JObject
        {
            ["root"] = new JObject
            {
                ["position"] = new JArray(pose.root.t.x, pose.root.t.y, pose.root.t.z),
                ["rotation"] = new JArray(pose.root.q.x, pose.root.q.y, pose.root.q.z, pose.root.q.w)
            },
            ["hands"] = new JObject
            {
                ["left"] = TransformJson(pose.hands.left),
                ["right"] = TransformJson(pose.hands.right)
            },
            ["feet"] = new JObject
            {
                ["left"] = TransformJson(pose.feet.left),
                ["right"] = TransformJson(pose.feet.right)
            }
        };

        private static JObject TransformJson(KimodoRigidTransform transform) => new JObject
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

        private static KimodoRigidTransform GetEndEffector(CharacterPose pose, string endEffector)
        {
            return endEffector switch
            {
                "left_hand" => pose.hands.left,
                "right_hand" => pose.hands.right,
                "left_foot" => pose.feet.left,
                "right_foot" => pose.feet.right,
                _ => throw new InvalidOperationException($"Unsupported end effector '{endEffector}'.")
            };
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

        private static CharacterPose RequireCanonicalPose(KimodoMarkerSampleResult sample)
        {
            CharacterPose pose = null;
            if (sample != null && sample.enableMask?.muscle49 == true &&
                CharacterPoseMuscleAdapter.TryFromSampleData(
                    sample.sampleData,
                    out CharacterPose decoded,
                    out _))
            {
                pose = decoded;
            }
            if (pose == null)
            {
                throw new InvalidOperationException("Pose source has no valid 70-value sampleData payload.");
            }
            if (!pose.TryValidate(out string error))
            {
                throw new InvalidOperationException(error);
            }
            return pose;
        }

        private static void SetCanonicalPose(
            KimodoMarkerSampleResult sample,
            CharacterPose pose,
            TimelineCharacterRecord character)
        {
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }
            if (!KimodoSampleResultPoseUtility.TryEncode(sample, pose.Clone(), out string encodeError))
            {
                throw new InvalidOperationException(encodeError);
            }
            sample.enableMask ??= new KimodoSampleChannelMask();
            sample.enableMask.muscle49 = true;
            sample.enableMask.rootTQ = true;
            sample.enableMask.leftFootTQ = true;
            sample.enableMask.rightFootTQ = true;
            sample.constraintType = "constraint";
            sample.mask = KimodoConstraintMask.Resolve(sample.mask, "constraint");
        }

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
