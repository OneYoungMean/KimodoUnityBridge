using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityBridge;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;

namespace KimodoUnityBridge.Command

{
    internal static partial class command_context
    {
        private static List<KimodoMarkerSampleResult> BuildPoseConstraints(
            JObject arguments,
            string modelName,
            Avatar targetAvatar,
            int frameCount,
            float frameRate,
            int durationFrames,
            GameObject sceneCharacterRoot,
            TimelineCharacterRecord sourceCharacter,
            out List<string> poseWarnings)
        {
            poseWarnings = new List<string>();
            if (arguments?["constraints"] == null)
            {
                return new List<KimodoMarkerSampleResult>();
            }
            if (arguments["constraints"] is not JArray constraints)
            {
                throw new InvalidOperationException("constraints must be an array.");
            }
            string frameError = ValidateUniqueConstraintFrames(constraints, durationFrames);
            if (!string.IsNullOrEmpty(frameError)) throw new InvalidOperationException(frameError);
            var samples = new List<KimodoMarkerSampleResult>(constraints.Count);
            var pathSamples = new List<KimodoMarkerSampleResult>();
            RetargetSkeleton targetCache = null;
            try
            {
                if (constraints.Count > 0 &&
                    !KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        targetAvatar,
                        "KimodoCommandPoseConstraints",
                        out targetCache,
                        out string cacheError))
                {
                    throw new InvalidOperationException($"Build pose constraint target failed: {cacheError}");
                }

                float targetRootHeight = 0f;
                if (targetCache != null &&
                    targetCache.GetBonePose(HumanBodyBones.Hips, out Vector3 initialHipsPosition, out _))
                {
                    targetRootHeight = initialHipsPosition.y;
                }

                var pointFrames = new HashSet<int>();
                var pathFrames = new HashSet<int>();
                var occupiedPathFrames = new HashSet<int>();
                Vector3 rootPathAnchorPosition = new Vector3(0f, targetRootHeight, 0f);
                Quaternion rootPathAnchorRotation = Quaternion.identity;
                if (constraints.OfType<JObject>().Any(item => item["root_path"] is JObject))
                {
                    ResolveRootPathWorldAnchor(
                        sourceCharacter,
                        sceneCharacterRoot,
                        targetAvatar,
                        targetRootHeight,
                        out rootPathAnchorPosition,
                        out rootPathAnchorRotation);
                }
                foreach (JObject item in constraints.OfType<JObject>())
                    if (item.Property("inout") == null && item["root_path"] == null)
                        pointFrames.Add(RequiredNonNegativeFrame(item, "frame"));
                for (int i = 0; i < constraints.Count; i++)
                {
                    if (constraints[i] is JObject item && item["root_path"] is JObject rootPath)
                    {
                        int startFrame = item.Value<int?>("frame") ?? 0;
                        if (startFrame < 0 || startFrame >= durationFrames)
                        {
                            throw new InvalidOperationException(
                                $"constraints[{i}].frame must be within [0,{durationFrames}).");
                        }
                        PoseReference reference = RequirePoseReference(rootPath["path"] as JObject);
                        if (!pathFrames.Add(startFrame))
                            throw new InvalidOperationException($"constraints[{i}].root_path reuses frame {startFrame}; command constraints are not merged.");
                        pathSamples.AddRange(BuildRootPathConstraintsWorld(
                            RequirePathMarker(reference).PathData,
                            i,
                            startFrame,
                            durationFrames,
                            targetCache != null ? Mathf.Max(1e-6f, targetCache.humanScale) : 1f,
                            rootPathAnchorPosition,
                            rootPathAnchorRotation,
                            pointFrames,
                            occupiedPathFrames));
                    }
                }
                for (int i = 0; i < constraints.Count; i++)
                {
                    if (constraints[i] is not JObject constraint)
                    {
                        throw new InvalidOperationException($"constraints[{i}] must be an object.");
                    }
                    if (constraint.Property("inout") != null) continue;
                    if (constraint["root_path"] is JObject)
                    {
                        if (constraint.Properties().Any(property =>
                            property.Name != "frame" && property.Name != "root_path"))
                        {
                            throw new InvalidOperationException(
                                $"constraints[{i}] root_path cannot be combined with point constraint fields.");
                        }
                        continue;
                    }
                    int relativeFrame = RequiredNonNegativeFrame(constraint, "frame");
                    if (relativeFrame >= durationFrames)
                    {
                        throw new InvalidOperationException($"constraints[{i}].frame must be within [0,{durationFrames}).");
                    }
                    if (pathFrames.Contains(relativeFrame))
                        throw new InvalidOperationException($"constraints[{i}].frame {relativeFrame} collides with root_path; command constraints are not merged.");
                    double at = relativeFrame / SessionFrameRate;
                    ValidatePointConstraintShape(constraint, i);
                    JObject canonical = constraint.DeepClone() as JObject;
                    KimodoMarkerSampleResult sample = BuildCanonicalConstraint(
                        canonical, targetCache, modelName, frameRate, at, i,
                        sceneCharacterRoot, sourceCharacter, poseWarnings);
                    if (HasPoseAndEffector(canonical))
                    {
                        JArray effect = (JArray)canonical["effector"];
                        if (!KimodoConstraintPosePipeline.TryApplyCanonical(
                            sample, frameRate, targetCache, out _, out _, out string validationError))
                        {
                            poseWarnings.Add($"constraints[{i}].effector preflight warning: {validationError}");
                        }
                        else
                        {
                            try
                            {
                                ValidateSolvedRootEffectors(sample, targetCache, effect, i);
                            }
                            catch (InvalidOperationException warning)
                            {
                                poseWarnings.Add(warning.Message);
                            }
                        }
                    }
                    samples.Add(sample);
                }
                // Root paths remain independent samples. No command-side row is
                // merged with a point constraint or overwritten by frame order.
                samples.AddRange(pathSamples);
            }
            finally
            {
                targetCache?.Dispose();
            }
            return samples;
        }

        internal static string ValidateUniqueConstraintFrames(JArray constraints, int durationFrames)
        {
            if (constraints == null) return string.Empty;
            var pointFrames = new HashSet<int>();
            var rootPathFrames = new HashSet<int>();
            var rootPathIndices = new Dictionary<int, int>();

            // Collect path endpoints before validating. Intermediate adaptive
            // samples are checked after the path reference is resolved.
            for (int i = 0; i < constraints.Count; i++)
            {
                if (constraints[i] is not JObject item || item.Property("inout") != null)
                    continue;
                if (item["root_path"] is JObject)
                {
                    int rootPathFrame = item.Value<int?>("frame") ?? 0;
                    if (rootPathFrame < 0 || rootPathFrame >= durationFrames)
                        return $"constraints[{i}].frame must be within [0,{durationFrames}).";
                    if (!rootPathFrames.Add(rootPathFrame))
                        return $"constraints[{i}].root_path reuses frame {rootPathFrame}; multiple constraints at one frame are undefined.";
                    rootPathIndices[rootPathFrame] = i;
                    int endpoint = durationFrames - 1;
                    if (endpoint != rootPathFrame && !rootPathFrames.Add(endpoint))
                    {
                        return $"constraints[{i}].root_path overlaps another root_path endpoint; multiple constraints at one frame are undefined.";
                    }
                    rootPathIndices[endpoint] = i;
                    continue;
                }
                int pointFrame = RequiredNonNegativeFrame(item, "frame");
                if (pointFrame >= durationFrames)
                    return $"constraints[{i}].frame must be within [0,{durationFrames}).";
                if (!pointFrames.Add(pointFrame))
                    return $"constraints[{i}].frame {pointFrame} duplicates another point constraint; multiple constraints at one frame are undefined.";
            }

            foreach (int frame in pointFrames)
            {
                if (rootPathIndices.TryGetValue(frame, out int rootPathIndex))
                    return $"constraints[{rootPathIndex}].root_path frame {frame} collides with a point constraint; root_path has priority and multiple constraints at one frame are undefined.";
            }
            return string.Empty;
        }

        private static void ValidateSolvedRootEffectors(
            KimodoMarkerSampleResult result,
            RetargetSkeleton targetCache,
            JArray effect,
            int constraintIndex)
        {
            string[] names = { "left_hand", "right_hand", "left_foot", "right_foot" };
            HumanBodyBones[] bones =
            {
                HumanBodyBones.LeftHand,
                HumanBodyBones.RightHand,
                HumanBodyBones.LeftFoot,
                HumanBodyBones.RightFoot
            };
            foreach (JObject entry in effect.OfType<JObject>())
            {
                string joint = entry.Value<string>("joint");
                int i = Array.IndexOf(names, joint);
                if (i < 0) continue;
                if (!targetCache.GetBonePose(bones[i], out Vector3 solvedPosition, out Quaternion solvedRotation))
                {
                    throw new InvalidOperationException(
                        $"constraints[{constraintIndex}].effector[{joint}] could not be read after IK preflight.");
                }

                KimodoRigidTransform target = joint switch
                {
                    "left_hand" => result.effectors.leftHand,
                    "right_hand" => result.effectors.rightHand,
                    "left_foot" => result.effectors.leftFoot,
                    "right_foot" => result.effectors.rightFoot,
                    _ => KimodoRigidTransform.Identity
                };
                float positionError = Vector3.Distance(solvedPosition, target.t);
                float rotationError = target.enableRotation
                    ? Quaternion.Angle(solvedRotation, target.q)
                    : 0f;
                if (positionError > 0.03f || rotationError > 15f)
                {
                    throw new InvalidOperationException(
                        $"constraints[{constraintIndex}].effector[{joint}] is unreachable " +
                        $"(position_error={positionError.ToString("0.####", CultureInfo.InvariantCulture)}, " +
                        $"rotation_error_degrees={rotationError.ToString("0.##", CultureInfo.InvariantCulture)}).");
                }
            }
        }

        private static KimodoMarkerSampleResult BuildCanonicalConstraint(
            JObject value,
            RetargetSkeleton targetCache,
            string modelName,
            float frameRate,
            double sampleTime,
            int constraintIndex,
            GameObject sceneCharacterRoot,
            TimelineCharacterRecord sourceCharacter,
            List<string> poseWarnings)
        {
            KimodoMarkerSampleResult source = ResolveConstraintPose(
                value, "point", targetCache, sceneCharacterRoot, sourceCharacter,
                poseWarnings, constraintIndex);
            KimodoMarkerSampleResult result = BuildModelNativeConstraintSample(
                source, "fullbody", targetCache, modelName, frameRate, sampleTime);
            ApplyPointConstraintData(value, result, sampleTime, constraintIndex);
            return result;
        }

        internal static void ApplyPointConstraintData(
            JObject value, KimodoMarkerSampleResult sample, double sampleTime, int index)
        {
            ValidatePointConstraintShape(value, index);
            if (sample?.sampleValid != true || sample.rootOverride == null ||
                sample.sampleData?.IsValid != true)
                throw new InvalidOperationException($"constraints[{index}] requires a complete sampled pose and root.");

            // Only explicit command fields select the type. Sampled channels are support data.
            sample.constraintMode = value["pose"] != null ? "fullbody" :
                value["effector"] != null ? "effector" : "root2d";
            sample.sampleTime = sampleTime;
            sample.enabled = true;
            sample.enableMask = new KimodoConstraintMask { rootHeading = sample.enableMask?.rootHeading == true };

            // Preserve sampled World targets before changing the root used by Local targets.
            sample.effectors.leftHand = KimodoConstraintSpaceUtility.ToWorld(sample.effectors.leftHand, sample.rootOverride);
            sample.effectors.rightHand = KimodoConstraintSpaceUtility.ToWorld(sample.effectors.rightHand, sample.rootOverride);
            sample.effectors.leftFoot = KimodoConstraintSpaceUtility.ToWorld(sample.effectors.leftFoot, sample.rootOverride);
            sample.effectors.rightFoot = KimodoConstraintSpaceUtility.ToWorld(sample.effectors.rightFoot, sample.rootOverride);
            if (value["root"] is JObject root)
            {
                OverlayConstraintTransform(root, sample.rootOverride, $"constraints[{index}].root");
                if (root["rotation"] != null) sample.enableMask.rootHeading = true;
            }
            if (value["effector"] is not JArray effectors) return;
            foreach (JObject entry in effectors.OfType<JObject>())
            {
                string joint = entry.Value<string>("joint");
                KimodoRigidTransform target;
                switch (joint)
                {
                    case "left_hand": target = sample.effectors.leftHand; sample.enableMask.leftHand = true; break;
                    case "right_hand": target = sample.effectors.rightHand; sample.enableMask.rightHand = true; break;
                    case "left_foot": target = sample.effectors.leftFoot; sample.enableMask.leftFoot = true; break;
                    case "right_foot": target = sample.effectors.rightFoot; sample.enableMask.rightFoot = true; break;
                    default: throw new InvalidOperationException($"Unsupported effector joint '{joint}'.");
                }
                OverlayConstraintTransform(entry, target, $"constraints[{index}].effector[{joint}]");
            }
        }

        private static void OverlayConstraintTransform(JObject value, KimodoRigidTransform target, string path)
        {
            if (value["position"] is JArray position) target.t = ReadVector3(position, path + ".position");
            if (value["rotation"] is JArray rotation)
            {
                target.q = Quaternion.Euler(ReadVector3(rotation, path + ".rotation"));
                target.enableRotation = true;
            }
            target.space = KimodoConstraintSpace.World;
        }

        internal static bool HasPoseAndEffector(JObject constraint)
        {
            return constraint?["pose"] is JObject && constraint["effector"] is JArray;
        }

        private static void ValidatePointConstraintShape(JObject constraint, int index)
        {
            string path = $"constraints[{index}]";
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "frame", "pose", "root", "effector" };
            foreach (JProperty property in constraint.Properties())
            {
                if (!allowed.Contains(property.Name))
                    throw new InvalidOperationException(property.Name == "type"
                        ? $"{path}.type is not used; select the type with root, pose, and effector data."
                        : $"{path} field '{property.Name}' is not valid for a point constraint.");
                if (property.Name != "frame" &&
                    property.Name != "effector" &&
                    property.Value is not JObject)
                    throw new InvalidOperationException($"{path}.{property.Name} must be an object.");
            }
            if (constraint["pose"] == null && constraint["root"] == null)
                throw new InvalidOperationException($"{path} must include pose or root; effector requires explicit root context.");
            if (constraint["root"] is JObject root) ValidateConstraintTransform(root, path + ".root");
            if (constraint["effector"] == null) return;
            if (constraint["effector"] is not JArray effectors)
                throw new InvalidOperationException($"{path}.effector must be an array.");
            if (effectors.Count == 0) throw new InvalidOperationException($"{path}.effector requires at least one end effector.");
            var seenJoints = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < effectors.Count; i++)
            {
                if (effectors[i] is not JObject target)
                    throw new InvalidOperationException($"{path}.effector[{i}] must be an object.");
                string joint = target.Value<string>("joint");
                if (joint != "left_hand" && joint != "right_hand" &&
                    joint != "left_foot" && joint != "right_foot")
                    throw new InvalidOperationException($"{path}.effector[{i}].joint '{joint}' is unsupported.");
                if (!seenJoints.Add(joint))
                    throw new InvalidOperationException($"{path}.effector contains duplicate joint '{joint}'.");
                ValidateConstraintTransform(target, path + ".effector[" + i + "]", allowJoint: true);
            }
        }

        private static void ValidateConstraintTransform(JObject value, string path, bool allowJoint = false)
        {
            if (!value.HasValues) throw new InvalidOperationException($"{path} requires position or rotation.");
            foreach (JProperty property in value.Properties())
            {
                if (allowJoint && property.Name == "joint") continue;
                if (property.Name != "position" && property.Name != "rotation")
                    throw new InvalidOperationException($"{path} has unsupported field '{property.Name}'.");
                ReadVector3(property.Value as JArray, path + "." + property.Name);
            }
        }

        private static IEnumerable<KimodoMarkerSampleResult> BuildRootPathConstraintsWorld(
            KimodoRootPathData path,
            int constraintIndex,
            int startFrame,
            int durationFrames,
            float targetHumanScale,
            Vector3 worldAnchorPosition,
            Quaternion worldAnchorRotation,
            ISet<int> explicitPointFrames,
            ISet<int> occupiedPathFrames)
        {
            List<KimodoRootPathKnot> knots = path?.knots;
            if (path == null || path.length < 0f || knots == null || knots.Count < 1 || knots.Any(knot => knot == null))
            {
                throw new InvalidOperationException(
                    $"constraints[{constraintIndex}].root_path references invalid path data.");
            }
            float sourceLength = EstimatePathLength(knots);
            bool hasStoredHeadings = knots.All(knot => knot.hasHeading);
            if (sourceLength <= 1e-6f && (path.length > 1e-6f || !hasStoredHeadings))
            {
                throw new InvalidOperationException(
                    $"constraints[{constraintIndex}].root_path has zero source length without reusable headings.");
            }
            float sourceHumanScale = path.sourceHumanScale > 1e-6f ? path.sourceHumanScale : 1f;
            float retargetScale = Mathf.Max(1e-6f, targetHumanScale) / sourceHumanScale;
            float scale = sourceLength > 1e-6f
                ? path.length / sourceLength * retargetScale
                : retargetScale;
            int endFrame = durationFrames - 1;
            // A root_path is an endpoint constraint pair. Keep the path reusable
            // without turning it into a dense sequence of authored constraints.
            SortedSet<int> sampleFrames = new SortedSet<int> { startFrame, endFrame };
            foreach (int frame in sampleFrames)
            {
                if (frame < startFrame || frame > endFrame)
                {
                    continue;
                }
                if (!occupiedPathFrames.Add(frame))
                {
                    throw new InvalidOperationException("root_path frame ranges cannot overlap.");
                }
            }

            var result = new List<KimodoMarkerSampleResult>(sampleFrames.Count);
            foreach (int frame in sampleFrames)
            {
                if (frame < startFrame || frame > endFrame)
                {
                    continue;
                }
                float progress = endFrame <= startFrame ? 0f : (frame - startFrame) / (float)(endFrame - startFrame);
                float pathTime = path.inverse ? 1f - progress : progress;
                EvaluatePath(knots, pathTime, out Vector2 position, out Vector2 tangent);
                position *= scale;
                if (hasStoredHeadings)
                {
                    tangent = EvaluatePathHeading(knots, pathTime);
                }
                if (path.inverse) tangent = -tangent;
                if (tangent.sqrMagnitude <= 1e-8f)
                {
                    throw new InvalidOperationException(
                        $"constraints[{constraintIndex}].root_path has a zero heading at frame {frame}.");
                }
                if (explicitPointFrames != null && explicitPointFrames.Contains(frame))
                    throw new InvalidOperationException($"constraints[{constraintIndex}].root_path collides with point frame {frame}; command constraints are not merged.");
                Vector3 localPlanarPosition = new Vector3(position.x, 0f, position.y);
                Vector3 worldPlanarPosition = worldAnchorPosition + worldAnchorRotation * localPlanarPosition;
                Vector3 worldHeading = worldAnchorRotation * new Vector3(tangent.x, 0f, tangent.y);
                result.Add(CreateRootOverrideSample(
                    frame,
                    // Analysis returns the path in its frame-zero World
                    // reference. Resolve it once against the generation
                    // character's World anchor; downstream exporters consume
                    // the resulting World-space values directly.
                    new Vector3(worldPlanarPosition.x, worldAnchorPosition.y, worldPlanarPosition.z),
                    worldHeading.normalized));
            }
            return result;
        }

        private static void ResolveRootPathWorldAnchor(
            TimelineCharacterRecord sourceCharacter,
            GameObject sceneCharacterRoot,
            Avatar targetAvatar,
            float targetRootHeight,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = new Vector3(0f, targetRootHeight, 0f);
            rotation = Quaternion.identity;
            string anchorError = string.Empty;
            if (sourceCharacter != null)
            {
                if (!KimodoConstraintPoseUtility.TryCreateRestPoseSample(
                    new KimodoTimelineInOutConstraintContext { Track = sourceCharacter.Track, Animator = sourceCharacter.Animator },
                    out var sample, out anchorError))
                    throw new InvalidOperationException($"Root Path generation could not resolve its rest pose: {anchorError}");
                GetRootTransform(sample, out position, out rotation);
                rotation = KimodoMotionMath.ResolvePlanarHeading(rotation);
                return;
            }

            if (targetAvatar != null && sceneCharacterRoot != null &&
                KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    targetAvatar,
                    "KimodoCommandRootPathAnchor",
                    out RetargetSkeleton anchorCache,
                    out anchorError))
            {
                try
                {
                    if (!TryCreateAvatarRestPoseSample(
                        anchorCache,
                        sceneCharacterRoot,
                        alignToSceneRoot: false,
                        out KimodoMarkerSampleResult restPose,
                        out anchorError))
                    {
                        throw new InvalidOperationException(
                            $"Root Path generation could not resolve its World anchor: {anchorError}");
                    }
                    GetRootTransform(restPose, out position, out rotation);
                    rotation = KimodoMotionMath.ResolvePlanarHeading(rotation);
                }
                finally
                {
                    anchorCache.Dispose();
                }
            }
            else if (targetAvatar != null && sceneCharacterRoot != null)
            {
                throw new InvalidOperationException(
                    $"Root Path generation could not resolve its World anchor: {anchorError}");
            }
        }

        private static Vector2 EvaluatePathHeading(IReadOnlyList<KimodoRootPathKnot> knots, float time)
        {
            if (knots.Count == 1)
            {
                return knots[0].heading.sqrMagnitude > 1e-8f ? knots[0].heading.normalized : Vector2.up;
            }
            float scaled = Mathf.Clamp01(time) * (knots.Count - 1);
            int segment = Mathf.Min(Mathf.FloorToInt(scaled), knots.Count - 2);
            float t = segment == knots.Count - 2 && time >= 1f ? 1f : scaled - segment;
            Vector2 heading = Vector2.Lerp(knots[segment].heading, knots[segment + 1].heading, t);
            return heading.sqrMagnitude > 1e-8f ? heading.normalized : Vector2.up;
        }

        private static float EstimatePathLength(IReadOnlyList<KimodoRootPathKnot> knots)
        {
            if (knots.Count < 2) return 0f;
            const int samplesPerSegment = 16;
            float length = 0f;
            EvaluatePath(knots, 0f, out Vector2 previous, out _);
            int sampleCount = (knots.Count - 1) * samplesPerSegment;
            for (int i = 1; i <= sampleCount; i++)
            {
                EvaluatePath(knots, i / (float)sampleCount, out Vector2 position, out _);
                length += Vector2.Distance(previous, position);
                previous = position;
            }
            return length;
        }

        private static void EvaluatePath(
            IReadOnlyList<KimodoRootPathKnot> knots,
            float time,
            out Vector2 position,
            out Vector2 tangent)
        {
            if (knots.Count == 1)
            {
                position = knots[0].position;
                tangent = knots[0].hasHeading ? knots[0].heading : Vector2.up;
                return;
            }
            float scaled = Mathf.Clamp01(time) * (knots.Count - 1);
            int segment = Mathf.Min(Mathf.FloorToInt(scaled), knots.Count - 2);
            float t = segment == knots.Count - 2 && time >= 1f ? 1f : scaled - segment;
            KimodoRootPathKnot first = knots[segment];
            KimodoRootPathKnot second = knots[segment + 1];
            Vector2 chord = second.position - first.position;
            Vector2 p0 = first.position;
            Vector2 p1 = p0 + (first.hasTangentOut ? first.tangentOut : chord / 3f);
            Vector2 p3 = second.position;
            Vector2 p2 = p3 + (second.hasTangentIn ? second.tangentIn : -chord / 3f);
            position = KimodoBezierUtility.EvaluateCubic(p0, p1, p2, p3, t);
            tangent = EvaluateBezierTangent(p0, p1, p2, p3, t);
            if (tangent.sqrMagnitude <= 1e-8f) tangent = chord;
        }

        private static KimodoMarkerSampleResult CreateRootOverrideSample(
            int frame,
            Vector3 position,
            Vector3 heading)
        {
            return new KimodoMarkerSampleResult
            {
                sampleValid = true,
                constraintMode = "root2d",
                sampleTime = frame / SessionFrameRate,
                rootOverride = new KimodoRigidTransform
                {
                    t = position,
                    q = Quaternion.LookRotation(heading, Vector3.up)
                },
                enableMask = new KimodoConstraintMask { rootHeading = true },
            };
        }

        private static Vector2 EvaluateBezierTangent(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float oneMinus = 1f - t;
            return 3f * oneMinus * oneMinus * (p1 - p0) +
                6f * oneMinus * t * (p2 - p1) +
                3f * t * t * (p3 - p2);
        }

        private static KimodoMarkerSampleResult ResolveConstraintPose(
            JObject value,
            string constraintType,
            RetargetSkeleton targetCache,
            GameObject sceneCharacterRoot,
            TimelineCharacterRecord sourceCharacter,
            List<string> poseWarnings,
            int constraintIndex)
        {
            if (value?["pose"] is JObject poseReference)
            {
                return ReadPoseSample(
                    RequirePoseReference(poseReference),
                    $"constraints[{constraintIndex}].{constraintType}");
            }

            if (sourceCharacter != null)
            {
                // Match pose.get(skeleton_rest_pose); empty Timeline ranges are not valid support poses.
                if (!KimodoConstraintPoseUtility.TryCreateRestPoseSample(
                    new KimodoTimelineInOutConstraintContext { Track = sourceCharacter.Track, Animator = sourceCharacter.Animator },
                    out var sourceRestPose, out string restError))
                    throw new InvalidOperationException($"constraints[{constraintIndex}].{constraintType} rest pose could not be created: {restError}");
                return sourceRestPose;
            }

            if (!TryCreateAvatarRestPoseSample(
                targetCache,
                sceneCharacterRoot,
                    alignToSceneRoot: false,
                    out KimodoMarkerSampleResult restPose,
                    out string error))
            {
                throw new InvalidOperationException(
                    $"constraints[{constraintIndex}].{constraintType} omitted pose and Avatar rest pose could not be created: {error}");
            }

            poseWarnings?.Add(
                $"constraints[{constraintIndex}].{constraintType} omitted pose; used Avatar skeleton rest pose.");
            return restPose;
        }

        internal static bool TryCreateAvatarRestPoseSample(
            RetargetSkeleton targetCache,
            GameObject sceneRoot,
            bool alignToSceneRoot,
            out KimodoMarkerSampleResult result,
            out string error)
        {
            result = null;
            error = string.Empty;
            if (!KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
            {
                return false;
            }

            KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(targetCache);
            if (alignToSceneRoot && sceneRoot != null && targetCache.root != null)
            {
                targetCache.root.transform.SetPositionAndRotation(
                    sceneRoot.transform.position,
                    sceneRoot.transform.rotation);
                targetCache.root.transform.localScale = sceneRoot.transform.lossyScale;
                targetCache.animator.Rebind();
                targetCache.animator.Update(0f);
            }

            if (!KimodoRetargetSamplingUtility.TryCaptureSampleData(
                    targetCache,
                    out MuscleSample sampleData,
                    out error) ||
                sampleData == null || !sampleData.IsValid)
            {
                if (string.IsNullOrEmpty(error)) error = "Avatar rest pose sampling returned no valid MuscleSample.";
                return false;
            }

            result = new KimodoMarkerSampleResult
            {
                sampleValid = true,
                sampleData = sampleData,
                constraintMode = "fullbody",
                enabled = true,
                enableMask = KimodoConstraintMask.ForType("fullbody"),
                effectors = new KimodoConstraintEffectors()
            };
            KimodoRetargetMarkerSamplingUtility.CaptureWorldTargets(targetCache, result);
            return true;
        }

        private static KimodoMarkerSampleResult BuildModelNativeConstraintSample(
            KimodoMarkerSampleResult sourceResult,
            string constraintType,
            RetargetSkeleton modelSkeleton,
            string modelName,
            float frameRate,
            double sampleTime)
        {
            if (sourceResult?.sampleData == null || !sourceResult.sampleData.IsValid)
            {
                throw new InvalidOperationException("Pose constraint has no valid 70-value sampleData payload.");
            }
            KimodoMarkerSampleResult solveInput = sourceResult.Clone();
            ConfigureConstraintIntent(solveInput, constraintType, sampleTime);
            if (!KimodoConstraintPosePipeline.TryApplyCanonical(
                    solveInput, frameRate, modelSkeleton,
                    out BoneSample boneSample, out MuscleSample targetMuscleSample, out string retargetError))
            {
                throw new InvalidOperationException($"Retarget pose constraint failed: {retargetError}");
            }
            if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    boneSample, modelSkeleton, modelName, constraintType, sampleTime,
                    out KimodoMarkerSampleResult converted, out string convertError))
            {
                throw new InvalidOperationException($"Convert pose constraint failed: {convertError}");
            }
            converted.sampleData = targetMuscleSample?.Clone() ?? new MuscleSample();
            // Preserve the complete sampled support root through retargeting.
            // It remains available for inspection and later edits even when
            // this command does not enable the root channel.
            if (solveInput.rootOverride != null)
            {
                converted.rootOverride = solveInput.rootOverride.Clone();
            }
            ConfigureConstraintIntent(converted, constraintType, sampleTime);
            return converted;
        }

        private static void ConfigureConstraintIntent(
            KimodoMarkerSampleResult sample,
            string constraintType,
            double sampleTime)
        {
            string type = (constraintType ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
            if (sample == null || !sample.sampleValid)
            {
                throw new InvalidOperationException("Pose constraint sample is invalid.");
            }
            if (type == "fullbody" && !KimodoSampleDataLayout.IsValid(sample.sampleData))
            {
                throw new InvalidOperationException("FullBody pose constraint requires a valid sampleData payload.");
            }
            bool supportedEffector = type == "left-hand" || type == "right-hand" ||
                type == "left-foot" || type == "right-foot";
            if (!supportedEffector && type != "fullbody")
            {
                throw new InvalidOperationException($"Pose constraint '{type}' is unsupported.");
            }
            if (supportedEffector && ResolveConstraintEffector(sample, type) == null)
            {
                throw new InvalidOperationException($"Pose constraint '{type}' has no target payload.");
            }
            sample.constraintMode = supportedEffector ? "effector" : "fullbody";
            sample.enableMask = KimodoConstraintMask.ForType(type);
            sample.sampleTime = sampleTime;
            sample.enabled = true;
        }

        private static KimodoRigidTransform ResolveConstraintEffector(
            KimodoMarkerSampleResult sample,
            string type)
        {
            if (sample?.effectors == null) return null;
            return type switch
            {
                "left-hand" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.leftHand, sample.rootOverride),
                "right-hand" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.rightHand, sample.rootOverride),
                "left-foot" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.leftFoot, sample.rootOverride),
                "right-foot" => KimodoConstraintSpaceUtility.ToWorld(sample.effectors.rightFoot, sample.rootOverride),
                _ => null
            };
        }

        private static JObject Route(string intent, string command, string next = null)
        {
            var route = new JObject { ["intent"] = intent, ["command"] = command };
            if (!string.IsNullOrWhiteSpace(next)) route["next"] = next;
            return route;
        }


        internal static List<double> ResolvePoseConstraintTimes(
            int poseCount,
            int frameCount,
            float frameRate,
            IReadOnlyList<double> suppliedTimes)
        {
            if (poseCount < 0)
            {
                throw new InvalidOperationException("pose count cannot be negative.");
            }
            if (suppliedTimes != null)
            {
                if (suppliedTimes.Count != poseCount)
                {
                    throw new InvalidOperationException("times count must match pose_refs count.");
                }
                return new List<double>(suppliedTimes);
            }

            var times = new List<double>(poseCount);
            if (poseCount == 0)
            {
                return times;
            }
            double endTime = KimodoInOutConstraintTools.ResolveConstraintEndSampleTimeSeconds(frameCount, frameRate);
            for (int i = 0; i < poseCount; i++)
            {
                times.Add(poseCount == 1 ? 0.0 : endTime * i / (poseCount - 1));
            }
            return times;
        }

        internal static List<string> ResolvePoseConstraintTypes(
            int poseCount,
            IReadOnlyList<string> suppliedTypes)
        {
            if (suppliedTypes != null && suppliedTypes.Count != poseCount)
            {
                throw new InvalidOperationException("constraint_types count must match pose_refs count.");
            }

            var types = new List<string>(poseCount);
            for (int i = 0; i < poseCount; i++)
            {
                string type = suppliedTypes == null ? "fullbody" : suppliedTypes[i]?.Trim().ToLowerInvariant();
                if (type != "fullbody" && type != "root2d" && type != "effector" &&
                    type != "left-hand" && type != "right-hand" && type != "left-foot" && type != "right-foot")
                    throw new InvalidOperationException(
                        $"constraint_types[{i}] must be fullbody, root2d, effector, or a concrete effector type.");
                types.Add(type);
            }
            return types;
        }




    }
}
