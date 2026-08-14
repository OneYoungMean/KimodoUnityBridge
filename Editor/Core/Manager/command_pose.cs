using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
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
            Ok(ReadPose(RequirePoseLocator(arguments["pose"] as JObject), PoseGetCommand)));

        public static string PoseCreate(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord character = ResolveSessionCharacterByReference(
                session, RequiredStringValue(arguments, "character"), false);
            RequireWritablePoseAvatar(character);
            JObject pose = arguments["pose"] as JObject ?? throw new InvalidOperationException("pose must be an object.");
            return Ok(new JObject { ["pose"] = StoreWritablePose(character, pose) });
        });

        public static string PoseCopy(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord character = ResolveSessionCharacterByReference(
                session, RequiredStringValue(arguments, "character"), false);
            RequireWritablePoseAvatar(character);
            KimodoMarkerSampleResult sourceSample = ReadPoseSample(
                RequirePoseLocator(arguments["pose"] as JObject), PoseCopyCommand);
            return Ok(new JObject { ["pose"] = StoreWritablePose(character, sourceSample) });
        });

        public static string PoseSet(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            PoseLocator locator = RequirePoseLocator(arguments["pose"] as JObject);
            TimelineCharacterRecord character = ResolvePoseCacheOwner(locator.Source)
                ?? throw new InvalidOperationException("pose_set requires a writable <character>.Poses source.");
            RequireWritablePoseAvatar(character);
            KimodoUntypedConstraintMarker marker = FindUntypedPose(character.PoseCacheTrack, locator.Frame)
                ?? throw new InvalidOperationException("Writable pose was not found at the requested source/frame.");
            JObject update = arguments["data"] as JObject ?? arguments;
            ApplyPoseJson(marker.SampleData, update);
            EditorUtility.SetDirty(marker);
            EditorUtility.SetDirty(character.PoseCacheTrack);
            SaveTimelineSession(RequireCurrentTimelineSession());
            return Ok(new JObject
            {
                ["pose"] = PoseLocatorJson(character.PoseCacheTrack.name, locator.Frame),
                ["data"] = PoseSampleToJson(marker.SampleData)
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
                    ["forwardPos"] = position.y,
                    ["rightwardPos"] = position.x,
                    ["rotateY"] = Mathf.Atan2(heading.x, heading.y) * Mathf.Rad2Deg
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
            KimodoMarkerSampleResult sample = ReadPoseSample(locator, command);
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord character = session.Characters.FirstOrDefault(item =>
                string.Equals(item.Name, locator.Source, StringComparison.OrdinalIgnoreCase));
            if (character != null)
            {
                return new JObject
                {
                    ["pose"] = PoseLocatorJson(character.Name, locator.Frame),
                    ["data"] = PoseSampleToJson(sample)
                };
            }
            character = ResolvePoseCacheOwner(locator.Source);
            if (character != null)
            {
                return new JObject
                {
                    ["pose"] = PoseLocatorJson(character.PoseCacheTrack.name, locator.Frame),
                    ["data"] = PoseSampleToJson(sample)
                };
            }
            return new JObject
            {
                ["pose"] = PoseLocatorJson(locator.Source, locator.Frame),
                ["data"] = PoseSampleToJson(sample)
            };
        }

        private static KimodoMarkerSampleResult ReadPoseSample(
            PoseLocator locator,
            string command = GenerateAnimationCommand)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord character = session.Characters.FirstOrDefault(item =>
                string.Equals(item.Name, locator.Source, StringComparison.OrdinalIgnoreCase));
            if (character != null)
            {
                ThrowIfGenerationRangeLocked(session, character, locator.Frame, locator.Frame + 1, command);
                return CaptureCharacterPoseSample(session, character, locator.Frame).Clone();
            }

            character = ResolvePoseCacheOwner(locator.Source);
            if (character != null)
            {
                KimodoUntypedConstraintMarker marker = FindUntypedPose(character.PoseCacheTrack, locator.Frame)
                    ?? throw new InvalidOperationException("Writable pose source does not contain a pose at the requested frame.");
                return marker.SampleData?.Clone() ?? new KimodoMarkerSampleResult();
            }

            KimodoConstraintMarkerBase constraint = session.Characters
                .SelectMany(item => item.Track.GetMarkers().OfType<KimodoConstraintMarkerBase>())
                .FirstOrDefault(item => item is not KimodoUntypedConstraintMarker &&
                    string.Equals(item.name, locator.Source, StringComparison.OrdinalIgnoreCase) &&
                    Mathf.RoundToInt((float)(item.time * SessionFrameRate)) == locator.Frame)
                ?? throw new InvalidOperationException($"Pose source '{locator.Source}' was not found.");
            return constraint.SampleData?.Clone() ?? new KimodoMarkerSampleResult();
        }

        private static KimodoMarkerSampleResult CaptureCharacterPoseSample(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            int frame)
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
            string modelName = KimodoPlayableClip.NormalizeBridgeModelName(context.ModelName);
            if (!KimodoTimelineConstraintClipCache.TrySampleMarker(
                    context,
                    sampleTime,
                    sampleTime,
                    "fullbody",
                    modelName,
                    out KimodoMarkerSampleResult sample,
                    out string sampleError))
            {
                throw new InvalidOperationException($"Timeline retarget pose sampling failed: {sampleError}");
            }
            return sample;
        }

        private static void RequireWritablePoseAvatar(TimelineCharacterRecord character)
        {
            if (character == null || !KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                throw new InvalidOperationException("Pose commands require a valid humanoid character Avatar.");
            }
        }

        private static JObject StoreWritablePose(TimelineCharacterRecord character, JObject data)
        {
            int frame = AllocatePoseFrame(character.PoseCacheTrack);
            KimodoUntypedConstraintMarker marker = character.PoseCacheTrack.CreateMarker<KimodoUntypedConstraintMarker>(frame / SessionFrameRate);
            marker.name = $"Pose_{frame}";
            marker.useOverride = true;
            marker.constraintEnabled = true;
            ApplyPoseJson(marker.SampleData, data);
            marker.SampleData.sampleTime = frame / SessionFrameRate;
            EditorUtility.SetDirty(marker);
            EditorUtility.SetDirty(character.PoseCacheTrack);
            SaveTimelineSession(RequireCurrentTimelineSession());
            return PoseLocatorJson(character.PoseCacheTrack.name, frame);
        }

        private static JObject StoreWritablePose(
            TimelineCharacterRecord character,
            KimodoMarkerSampleResult sourceSample)
        {
            int frame = AllocatePoseFrame(character.PoseCacheTrack);
            KimodoUntypedConstraintMarker marker = character.PoseCacheTrack.CreateMarker<KimodoUntypedConstraintMarker>(frame / SessionFrameRate);
            marker.name = $"Pose_{frame}";
            marker.useOverride = true;
            marker.constraintEnabled = true;
            marker.SampleData = sourceSample?.Clone() ?? new KimodoMarkerSampleResult();
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

        private static JObject PoseSampleToJson(KimodoMarkerSampleResult sample)
        {
            Quaternion rootRotation = sample.localAxisAngles != null && sample.localAxisAngles.Count > 0
                ? KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(sample.localAxisAngles[0])
                : Quaternion.identity;
            var muscles = new JObject();
            var groupedMuscles = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                float value = i < (sample.muscles?.Count ?? 0) ? sample.muscles[i] : 0f;
                if (TryResolveMuscleGroupAndAxis(HumanTrait.MuscleName[i], out string group, out string axis))
                {
                    if (!groupedMuscles.TryGetValue(group, out JObject values))
                    {
                        values = new JObject();
                        groupedMuscles[group] = values;
                    }
                    values[axis] = value;
                }
                else
                {
                    muscles[HumanTrait.MuscleName[i]] = value;
                }
            }
            foreach (KeyValuePair<string, JObject> group in groupedMuscles)
            {
                muscles[group.Key] = group.Value;
            }
            return new JObject
            {
                ["root"] = new JObject
                {
                    ["forwardPos"] = sample.kimodoRootPosition.z,
                    ["rightwardPos"] = sample.kimodoRootPosition.x,
                    ["rotateY"] = ResolvePoseRootYaw(rootRotation)
                },
                ["muscles"] = muscles,
                ["foot_ik"] = new JObject
                {
                    ["left"] = new JObject { ["position"] = Vector3Json(sample.leftFootPosition), ["rotation"] = QuaternionJson(sample.leftFootRotation) },
                    ["right"] = new JObject { ["position"] = Vector3Json(sample.rightFootPosition), ["rotation"] = QuaternionJson(sample.rightFootRotation) }
                }
            };
        }

        private static void ApplyPoseJson(KimodoMarkerSampleResult sample, JObject data)
        {
            JObject root = data["root"] as JObject;
            if (root != null && (root["position"] != null || root["rotation_y"] != null))
            {
                throw new InvalidOperationException("root.position/root.rotation_y were removed; use root.forwardPos, root.rightwardPos, and root.rotateY.");
            }
            if (root != null && root["forwardPos"] != null)
            {
                sample.kimodoRootPosition.z = RequiredFiniteScalar(root, "forwardPos");
            }
            if (root != null && root["rightwardPos"] != null)
            {
                sample.kimodoRootPosition.x = RequiredFiniteScalar(root, "rightwardPos");
            }
            if (root != null && root["rotateY"] != null)
            {
                float rotationY = RequiredFiniteScalar(root, "rotateY");
                sample.localAxisAngles ??= new List<Vector3>();
                if (sample.localAxisAngles.Count == 0) sample.localAxisAngles.Add(Vector3.zero);
                Quaternion currentRotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(sample.localAxisAngles[0]);
                Quaternion rotation = ApplyPoseRootYaw(currentRotation, rotationY);
                sample.localAxisAngles[0] = KimodoRuntimeUtility.QuaternionToAxisAngleVector(rotation);
                Vector3 forward = rotation * Vector3.forward;
                Vector2 heading = new Vector2(forward.x, forward.z);
                sample.rootHeading = heading.sqrMagnitude > 1e-8f ? heading.normalized : Vector2.up;
                sample.hasRootHeading = true;
            }
            if (data["muscles"] is JObject muscles)
            {
                if (sample.muscles == null || sample.muscles.Count != HumanTrait.MuscleCount)
                    sample.muscles = Enumerable.Repeat(0f, HumanTrait.MuscleCount).ToList();
                for (int i = 0; i < HumanTrait.MuscleCount; i++)
                {
                    JToken token = muscles[HumanTrait.MuscleName[i]];
                    if (token != null && token.Type != JTokenType.Object)
                    {
                        sample.muscles[i] = Mathf.Clamp(token.Value<float>(), -1f, 1f);
                        continue;
                    }

                    if (TryResolveMuscleGroupAndAxis(HumanTrait.MuscleName[i], out string group, out string axis) &&
                        muscles[group] is JObject values &&
                        values[axis] != null)
                    {
                        sample.muscles[i] = Mathf.Clamp(values[axis].Value<float>(), -1f, 1f);
                    }
                }
            }
            if (data["foot_ik"] is JObject foot)
            {
                ApplyFoot(foot["left"] as JObject, ref sample.leftFootPosition, ref sample.leftFootRotation);
                ApplyFoot(foot["right"] as JObject, ref sample.rightFootPosition, ref sample.rightFootRotation);
            }
        }

        private static bool TryResolveMuscleGroupAndAxis(
            string muscleName,
            out string group,
            out string axis)
        {
            group = string.Empty;
            axis = string.Empty;
            if (string.IsNullOrWhiteSpace(muscleName))
            {
                return false;
            }

            string[,] suffixes =
            {
                { " Twist Left-Right", "z" },
                { " Twist In-Out", "z" },
                { " Nod Down-Up", "x" },
                { " Tilt Left-Right", "y" },
                { " Turn Left-Right", "z" },
                { " Front-Back", "x" },
                { " Left-Right", "y" },
                { " In-Out", "y" },
                { " Down-Up", "x" },
                { " Up-Down", "x" },
                { " Stretched", "x" },
                { " Stretch", "x" },
                { " Close", "x" },
                { " Spread", "y" }
            };

            for (int i = 0; i < suffixes.GetLength(0); i++)
            {
                string suffix = suffixes[i, 0];
                if (!muscleName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                group = muscleName.Substring(0, muscleName.Length - suffix.Length).TrimEnd();
                axis = suffixes[i, 1];
                return !string.IsNullOrWhiteSpace(group);
            }

            return false;
        }

        internal static float ResolvePoseRootYaw(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            return Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        }

        internal static Quaternion ApplyPoseRootYaw(Quaternion rotation, float rotationY)
        {
            float delta = Mathf.DeltaAngle(ResolvePoseRootYaw(rotation), rotationY);
            return Quaternion.AngleAxis(delta, Vector3.up) * rotation;
        }

        private static void CaptureFoot(Animator animator, HumanBodyBones bone, out Vector3 position, out Quaternion rotation)
        {
            Transform transform = animator.GetBoneTransform(bone);
            position = transform != null ? transform.position : Vector3.zero;
            rotation = transform != null ? transform.rotation : Quaternion.identity;
        }

        private static void ApplyFoot(JObject value, ref Vector3 position, ref Quaternion rotation)
        {
            if (value?["position"] != null) position = RequiredVector3(value, "position");
            if (value?["rotation"] != null) rotation = RequiredQuaternion(value, "rotation");
        }

        private static float RequiredFiniteScalar(JObject value, string name)
        {
            JToken token = value?[name];
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                throw new InvalidOperationException($"{name} must be a number.");
            float result = token.Value<float>();
            if (float.IsNaN(result) || float.IsInfinity(result))
                throw new InvalidOperationException($"{name} must be finite.");
            return result;
        }

        private static Vector3 RequiredVector3(JObject value, string name)
        {
            JArray array = value?[name] as JArray;
            if (array == null || array.Count != 3) throw new InvalidOperationException($"{name} must contain three numbers.");
            return new Vector3(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>());
        }

        private static Quaternion RequiredQuaternion(JObject value, string name)
        {
            JArray array = value?[name] as JArray;
            if (array == null || array.Count != 4) throw new InvalidOperationException($"{name} must contain four numbers.");
            return new Quaternion(array[0].Value<float>(), array[1].Value<float>(), array[2].Value<float>(), array[3].Value<float>()).normalized;
        }

        private static JArray Vector3Json(Vector3 value) => new JArray(value.x, value.y, value.z);
        private static JArray QuaternionJson(Quaternion value) => new JArray(value.x, value.y, value.z, value.w);

        private readonly struct PoseLocator
        {
            public PoseLocator(string source, int frame) { Source = source; Frame = frame; }
            public string Source { get; }
            public int Frame { get; }
        }
    }
}
