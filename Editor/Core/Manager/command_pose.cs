using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
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
            Ok(ReadPose(RequirePoseLocator(arguments["pose"] as JObject), PoseGetCommand)));

        public static string PoseCreate(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord character = ResolveSessionCharacterByReference(
                session, RequiredStringValue(arguments, "character"), false);
            RequireWritablePoseAvatar(character);
            CharacterPose pose = CharacterPoseJson.Parse(arguments["pose"] as JObject
                ?? throw new InvalidOperationException("pose must be an object."));
            return Ok(new JObject { ["pose"] = StoreWritablePose(character, pose) });
        });

        public static string PoseCopy(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord character = ResolveSessionCharacterByReference(
                session, RequiredStringValue(arguments, "character"), false);
            RequireWritablePoseAvatar(character);
            JObject result = ReadPose(RequirePoseLocator(arguments["pose"] as JObject), PoseCopyCommand);
            JObject data = result["data"] as JObject
                ?? throw new InvalidOperationException("Source pose data is unavailable.");
            return Ok(new JObject { ["pose"] = StoreWritablePose(character, CharacterPoseJson.Parse(data)) });
        });

        public static string PoseSet(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            PoseLocator locator = RequirePoseLocator(arguments["pose"] as JObject);
            TimelineCharacterRecord character = ResolvePoseCacheOwner(locator.Source)
                ?? throw new InvalidOperationException("pose_set requires a writable <character>.Poses source.");
            RequireWritablePoseAvatar(character);
            KimodoUntypedConstraintMarker marker = FindUntypedPose(character.PoseCacheTrack, locator.Frame)
                ?? throw new InvalidOperationException("Writable pose was not found at the requested source/frame.");
            JObject update = arguments["data"] as JObject
                ?? throw new InvalidOperationException("data must be a partial pose object.");
            CharacterPose pose = CharacterPoseJson.ApplyPatch(RequireCanonicalPose(marker.SampleData), update);
            SetCanonicalPose(marker.SampleData, pose, character);
            EditorUtility.SetDirty(marker);
            EditorUtility.SetDirty(character.PoseCacheTrack);
            SaveTimelineSession(RequireCurrentTimelineSession());
            return Ok(new JObject
            {
                ["pose"] = PoseLocatorJson(character.PoseCacheTrack.name, locator.Frame),
                ["data"] = CharacterPoseJson.ToJson(pose)
            });
        });

        public static string BuildRoot2DPath(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            string shape = RequiredStringValue(arguments, "shape").ToLowerInvariant();
            int durationFrames = arguments.Value<int?>("duration_frames") ?? 300;
            float maxSpeed = arguments.Value<float?>("max_speed") ?? 2.5f;
            float acceleration = arguments.Value<float?>("acceleration") ?? 2.5f;
            if (durationFrames < 2 || maxSpeed <= 0f || acceleration <= 0f ||
                float.IsNaN(maxSpeed) || float.IsInfinity(maxSpeed) ||
                float.IsNaN(acceleration) || float.IsInfinity(acceleration))
                throw new InvalidOperationException("duration_frames must be at least 2; max_speed and acceleration must be positive finite values.");
            string direction = (arguments.Value<string>("direction") ?? "left").Trim().ToLowerInvariant();
            if (direction != "left" && direction != "right") throw new InvalidOperationException("direction must be left or right.");
            int degrees = arguments.Value<int?>("turn_degrees") ?? 90;
            if (shape == "turn" && degrees != 0 && degrees != 45 && degrees != 90 && degrees != 135 && degrees != 180)
                throw new InvalidOperationException("turn_degrees must be 0, 45, 90, 135, or 180.");
            if (shape != "line" && shape != "turn" && shape != "s" && shape != "circle")
                throw new InvalidOperationException("shape must be line, turn, s, or circle.");

            float duration = (durationFrames - 1) / (float)SessionFrameRate;
            float accelerateTime = Mathf.Min(maxSpeed / acceleration, duration * 0.5f);
            float cruiseTime = Mathf.Max(0f, duration - 2f * accelerateTime);
            float peakSpeed = acceleration * accelerateTime;
            float distance = acceleration * accelerateTime * accelerateTime + peakSpeed * cruiseTime;
            int count = (shape == "line" || (shape == "turn" && (degrees == 0 || degrees == 180)))
                ? 2
                : durationFrames;
            var points = new JArray();
            float sign = direction == "left" ? 1f : -1f;
            for (int index = 0; index < count; index++)
            {
                int frame = count == 2 ? (index == 0 ? 0 : durationFrames - 1) : index;
                float elapsed = frame / (float)SessionFrameRate;
                float traveled;
                if (elapsed <= accelerateTime)
                    traveled = 0.5f * acceleration * elapsed * elapsed;
                else if (elapsed <= accelerateTime + cruiseTime)
                    traveled = 0.5f * acceleration * accelerateTime * accelerateTime + peakSpeed * (elapsed - accelerateTime);
                else
                {
                    float decelerationTime = Mathf.Min(accelerateTime, elapsed - accelerateTime - cruiseTime);
                    traveled = 0.5f * acceleration * accelerateTime * accelerateTime + peakSpeed * cruiseTime +
                        peakSpeed * decelerationTime - 0.5f * acceleration * decelerationTime * decelerationTime;
                }
                float u = distance > 1e-6f ? Mathf.Clamp01(traveled / distance) : 0f;
                Vector2 position;
                Vector2 heading;
                EvaluateRoot2DShape(shape, degrees, sign, u, distance, out position, out heading);
                points.Add(new JObject
                {
                    ["frame"] = frame,
                    ["position"] = new JArray(position.x, position.y),
                    ["heading"] = new JArray(heading.x, heading.y)
                });
            }
            return Ok(new JObject
            {
                ["fps"] = 60,
                ["duration_frames"] = durationFrames,
                ["max_speed"] = maxSpeed,
                ["acceleration"] = acceleration,
                ["distance"] = distance,
                ["points"] = points
            });
        });

        private static void EvaluateRoot2DShape(
            string shape, int degrees, float sign, float u, float distance,
            out Vector2 position, out Vector2 heading)
        {
            if (shape == "line" || (shape == "turn" && degrees == 0))
            {
                position = new Vector2(distance * u, 0f);
                heading = Vector2.right;
                return;
            }
            if (shape == "turn" && degrees == 180)
            {
                position = new Vector2(distance * u, 0f);
                heading = u < 1f ? Vector2.right : Vector2.left;
                return;
            }
            if (shape == "circle")
            {
                float radius = distance / (2f * Mathf.PI);
                float angle = u * 2f * Mathf.PI;
                position = new Vector2(radius * Mathf.Sin(angle), sign * radius * (1f - Mathf.Cos(angle)));
                heading = new Vector2(Mathf.Cos(angle), sign * Mathf.Sin(angle)).normalized;
                return;
            }
            if (shape == "s")
            {
                Vector2 p0 = Vector2.zero;
                Vector2 p1 = new Vector2(distance / 3f, sign * distance / 3f);
                Vector2 p2 = new Vector2(2f * distance / 3f, -sign * distance / 3f);
                Vector2 p3 = new Vector2(distance, 0f);
                float v = 1f - u;
                position = v * v * v * p0 + 3f * v * v * u * p1 + 3f * v * u * u * p2 + u * u * u * p3;
                heading = (3f * v * v * (p1 - p0) + 6f * v * u * (p2 - p1) + 3f * u * u * (p3 - p2)).normalized;
                return;
            }

            float radians = degrees * Mathf.Deg2Rad;
            Vector2 q0 = Vector2.zero;
            Vector2 q1 = new Vector2(Mathf.Tan(radians * 0.5f), 0f);
            Vector2 q2 = new Vector2(Mathf.Sin(radians), sign * (1f - Mathf.Cos(radians)));
            float approximateLength = Mathf.Max(1e-5f, Vector2.Distance(q0, q1) + Vector2.Distance(q1, q2));
            float scale = distance / approximateLength;
            q1 *= scale;
            q2 *= scale;
            float oneMinus = 1f - u;
            position = oneMinus * oneMinus * q0 + 2f * oneMinus * u * q1 + u * u * q2;
            heading = (2f * oneMinus * (q1 - q0) + 2f * u * (q2 - q1)).normalized;
        }

        private static JObject ReadPose(PoseLocator locator, string command = GenerateAnimationCommand)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord character = session.Characters.FirstOrDefault(item =>
                string.Equals(item.Name, locator.Source, StringComparison.OrdinalIgnoreCase));
            if (character != null)
            {
                ThrowIfGenerationRangeLocked(session, character, locator.Frame, locator.Frame + 1, command);
                CharacterPose data = CaptureCharacterPose(character, locator.Frame);
                return new JObject
                {
                    ["pose"] = PoseLocatorJson(character.Name, locator.Frame),
                    ["data"] = CharacterPoseJson.ToJson(data)
                };
            }
            character = ResolvePoseCacheOwner(locator.Source);
            if (character != null)
            {
                KimodoUntypedConstraintMarker marker = FindUntypedPose(character.PoseCacheTrack, locator.Frame)
                    ?? throw new InvalidOperationException("Writable pose source does not contain a pose at the requested frame.");
                return new JObject
                {
                    ["pose"] = PoseLocatorJson(character.PoseCacheTrack.name, locator.Frame),
                    ["data"] = CharacterPoseJson.ToJson(RequireCanonicalPose(marker.SampleData))
                };
            }
            TimelineSessionRecord current = RequireCurrentTimelineSession();
            KimodoConstraintMarkerBase constraint = current.Characters
                .SelectMany(item => item.Track.GetMarkers().OfType<KimodoConstraintMarkerBase>())
                .FirstOrDefault(item => item is not KimodoUntypedConstraintMarker &&
                    string.Equals(item.name, locator.Source, StringComparison.OrdinalIgnoreCase) &&
                    Mathf.RoundToInt((float)(item.time * SessionFrameRate)) == locator.Frame)
                ?? throw new InvalidOperationException($"Pose source '{locator.Source}' was not found.");
            return new JObject
            {
                ["pose"] = PoseLocatorJson(constraint.name, locator.Frame),
                ["data"] = CharacterPoseJson.ToJson(RequireCanonicalPose(constraint.SampleData))
            };
        }

        private static CharacterPose CaptureCharacterPose(TimelineCharacterRecord character, int frame)
        {
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                throw new InvalidOperationException($"Character '{character.Name}' requires a valid humanoid Avatar for pose sampling.");
            }
            TimelineClip sourceClip = character.Track.GetClips()
                .FirstOrDefault(item => frame / SessionFrameRate >= item.start && frame / SessionFrameRate <= item.end)
                ?? character.Track.GetClips().FirstOrDefault();
            string contextError = string.Empty;
            if (sourceClip == null || !KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                    sourceClip,
                    out KimodoTimelineInOutConstraintContext context,
                    out contextError))
            {
                throw new InvalidOperationException($"Character '{character.Name}' has no retargetable Timeline clip: {contextError}");
            }

            double sampleTime = frame / SessionFrameRate;
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
                if (!sampler.TryCaptureMuscleSample(
                        sampleTime,
                        normalizeRootToAnchor: false,
                        Vector3.zero,
                        Quaternion.identity,
                        out MuscleSample sample,
                        out sampleError))
                {
                    throw new InvalidOperationException($"Timeline pose sampling failed: {sampleError}");
                }
                return CharacterPoseMuscleAdapter.FromMuscleSample(sample);
            }
        }

        private static void RequireWritablePoseAvatar(TimelineCharacterRecord character)
        {
            if (character == null || !KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                throw new InvalidOperationException("Pose commands require a valid humanoid character Avatar.");
            }
        }

        private static JObject StoreWritablePose(TimelineCharacterRecord character, CharacterPose pose)
        {
            int frame = AllocatePoseFrame(character.PoseCacheTrack);
            KimodoUntypedConstraintMarker marker = character.PoseCacheTrack.CreateMarker<KimodoUntypedConstraintMarker>(frame / SessionFrameRate);
            marker.name = $"Pose_{frame}";
            marker.useOverride = true;
            marker.constraintEnabled = true;
            SetCanonicalPose(marker.SampleData, pose, character);
            marker.SampleData.sampleTime = frame / SessionFrameRate;
            EditorUtility.SetDirty(marker);
            EditorUtility.SetDirty(character.PoseCacheTrack);
            SaveTimelineSession(RequireCurrentTimelineSession());
            return PoseLocatorJson(character.PoseCacheTrack.name, frame);
        }

        private static int AllocatePoseFrame(AnimationTrack track)
        {
            var occupied = new HashSet<int>(track.GetMarkers().OfType<KimodoUntypedConstraintMarker>()
                .Select(marker => Mathf.RoundToInt((float)(marker.time * SessionFrameRate))));
            int frame = 0;
            while (occupied.Contains(frame)) frame++;
            return frame;
        }

        private static KimodoUntypedConstraintMarker FindUntypedPose(AnimationTrack track, int frame) =>
            track.GetMarkers().OfType<KimodoUntypedConstraintMarker>().FirstOrDefault(marker =>
                Mathf.RoundToInt((float)(marker.time * SessionFrameRate)) == frame);

        private static TimelineCharacterRecord ResolvePoseCacheOwner(string source)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            return session.Characters.FirstOrDefault(item => item.PoseCacheTrack != null &&
                    string.Equals(item.PoseCacheTrack.name, source, StringComparison.OrdinalIgnoreCase));
        }

        private static TimelineCharacterRecord ResolvePoseCharacter(PoseLocator locator)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord character = session.Characters.FirstOrDefault(item =>
                string.Equals(item.Name, locator.Source, StringComparison.OrdinalIgnoreCase))
                ?? ResolvePoseCacheOwner(locator.Source)
                ?? session.Characters.FirstOrDefault(item => item.Track.GetMarkers()
                    .OfType<KimodoConstraintMarkerBase>()
                    .Any(marker => string.Equals(marker.name, locator.Source, StringComparison.OrdinalIgnoreCase) &&
                        Mathf.RoundToInt((float)(marker.time * SessionFrameRate)) == locator.Frame));
            return character
                ?? throw new InvalidOperationException($"Pose source '{locator.Source}' has no owning character track.");
        }

        private static PoseLocator RequirePoseLocator(JObject value)
        {
            if (value == null) throw new InvalidOperationException("pose must be an object containing source and frame.");
            string source = RequiredStringValue(value, "source");
            int frame = RequiredNonNegativeFrame(value, "frame");
            return new PoseLocator(source, frame);
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
            CharacterPose pose = sample?.characterPose
                ?? throw new InvalidOperationException("Pose source has no canonical CharacterPose data; resample or copy it from a character Timeline frame.");
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
            MuscleSample muscleSample = CharacterPoseMuscleAdapter.ToMuscleSample(pose);
            sample.characterPose = pose.Clone();
            sample.muscles = new List<float>(muscleSample.pose.muscles);
            sample.leftFootPosition = muscleSample.leftFootPosition;
            sample.leftFootRotation = muscleSample.leftFootRotation;
            sample.rightFootPosition = muscleSample.rightFootPosition;
            sample.rightFootRotation = muscleSample.rightFootRotation;
            if (character?.Animator != null)
            {
                sample.unityRootPos = character.Animator.transform.position;
                sample.unityRootRot = character.Animator.transform.rotation;
            }
        }

        private static Vector2 RequiredVector2(JObject value, string name)
        {
            JArray array = value?[name] as JArray;
            if (array == null || array.Count != 2) throw new InvalidOperationException($"{name} must be [x,z].");
            return new Vector2(array[0].Value<float>(), array[1].Value<float>());
        }

        private readonly struct PoseLocator
        {
            public PoseLocator(string source, int frame) { Source = source; Frame = frame; }
            public string Source { get; }
            public int Frame { get; }
        }
    }
}
