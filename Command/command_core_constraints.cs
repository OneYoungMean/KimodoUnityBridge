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
            int durationFrames)
        {
            if (arguments?["constraints"] == null)
            {
                return new List<KimodoMarkerSampleResult>();
            }
            if (arguments["constraints"] is not JArray constraints)
            {
                throw new InvalidOperationException("constraints must be an array.");
            }
            var samples = new List<KimodoMarkerSampleResult>(constraints.Count * 3);
            var pathSamples = new List<KimodoMarkerSampleResult>();
            RetargetSkeleton targetCache = null;
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            double originalSessionTime = session.Director.time;
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

                // Fullbody FK sampling mutates the shared cache pose. Capture the
                // profile root height once so later root2d constraints do not
                // inherit the previous fullbody pose's transient hips height.
                float targetRootHeight = ResolveTargetRootHeight(targetCache);

                var explicitRootFrames = new HashSet<int>(constraints
                    .OfType<JObject>()
                    .Where(item => item["root2d"] is JObject)
                    .Select(item => RequiredNonNegativeFrame(item, "frame")));
                var existingConstraintFrames = new HashSet<int>(constraints
                    .OfType<JObject>()
                    .Where(item => item["root_path"] == null)
                    .Select(item => item.Value<int?>("frame") ?? -1)
                    .Where(frame => frame >= 0 && frame < durationFrames));
                var occupiedPathFrames = new HashSet<int>();
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
                        pathSamples.AddRange(BuildRootPathConstraintsSparse(
                            RequirePathMarker(reference).PathData,
                            i,
                            startFrame,
                            durationFrames,
                            targetRootHeight,
                            targetCache != null ? Mathf.Max(1e-6f, targetCache.humanScale) : 1f,
                            explicitRootFrames,
                            existingConstraintFrames,
                            occupiedPathFrames));
                    }
                }

                for (int i = 0; i < constraints.Count; i++)
                {
                    if (constraints[i] is not JObject constraint)
                    {
                        throw new InvalidOperationException($"constraints[{i}] must be an object.");
                    }
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
                    double at = relativeFrame / SessionFrameRate;
                    JObject fullBody = constraint["fullbody"] as JObject;
                    JObject root2D = constraint["root2d"] as JObject;
                    JObject[] endEffectors =
                    {
                        constraint["left_hand"] as JObject,
                        constraint["right_hand"] as JObject,
                        constraint["left_foot"] as JObject,
                        constraint["right_foot"] as JObject
                    };
                    string[] endEffectorTypes = { "left-hand", "right-hand", "left-foot", "right-foot" };
                    if (fullBody == null && root2D == null && endEffectors.All(value => value == null))
                    {
                        throw new InvalidOperationException($"constraints[{i}] must contain at least one constraint field.");
                    }

                    if (fullBody != null)
                    {
                        samples.Add(BuildReferencedPoseConstraint(
                            fullBody, "fullbody", targetCache, modelName, frameRate, at, i));
                    }
                    if (root2D != null)
                    {
                        samples.Add(BuildRoot2DConstraint(root2D, targetRootHeight, at, i));
                    }

                    for (int part = 0; part < endEffectors.Length; part++)
                    {
                        if (endEffectors[part] == null)
                        {
                            continue;
                        }
                        samples.Add(BuildReferencedPoseConstraint(
                            endEffectors[part], endEffectorTypes[part], targetCache, modelName, frameRate, at, i));
                    }
                }
                // Root paths are profile-space Root2D overrides. Append them
                // after FullBody rows so they replace only Root XZ/heading at
                // shared frames; explicit root2d rows were excluded above.
                samples.AddRange(pathSamples);
            }
            finally
            {
                session.Director.time = originalSessionTime;
                session.Director.Evaluate();
                targetCache?.Dispose();
            }
            return samples;
        }

        private static IEnumerable<KimodoMarkerSampleResult> BuildRootPathConstraintsSparse(
            KimodoRootPathData path,
            int constraintIndex,
            int startFrame,
            int durationFrames,
            float targetRootHeight,
            float targetHumanScale,
            ISet<int> explicitRootFrames,
            ISet<int> existingConstraintFrames,
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
            var sampleFrames = new SortedSet<int>(existingConstraintFrames ?? new HashSet<int>())
            {
                startFrame,
                endFrame
            };
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
                if (!explicitRootFrames.Contains(frame))
                {
                    result.Add(CreateRoot2DSample(
                        frame,
                        new Vector3(position.x, targetRootHeight, position.y),
                        new Vector3(tangent.x, 0f, tangent.y).normalized));
                }
            }
            return result;
        }

        // Compatibility entry retained for older reflection-based callers.
        private static IEnumerable<KimodoMarkerSampleResult> BuildRootPathConstraints(
            KimodoRootPathData path,
            int constraintIndex,
            int startFrame,
            int durationFrames,
            float targetRootHeight,
            float targetHumanScale,
            ISet<int> explicitRootFrames,
            ISet<int> occupiedPathFrames)
        {
            return BuildRootPathConstraintsSparse(
                path,
                constraintIndex,
                startFrame,
                durationFrames,
                targetRootHeight,
                targetHumanScale,
                explicitRootFrames,
                new HashSet<int>(Enumerable.Range(startFrame, Math.Max(0, durationFrames - startFrame))),
                occupiedPathFrames);
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
            position = EvaluateBezier(p0, p1, p2, p3, t);
            tangent = EvaluateBezierTangent(p0, p1, p2, p3, t);
            if (tangent.sqrMagnitude <= 1e-8f) tangent = chord;
        }

        private static KimodoMarkerSampleResult CreateRoot2DSample(
            int frame,
            Vector3 position,
            Vector3 heading)
        {
            return new KimodoMarkerSampleResult
            {
                constraintMode = "root2d",
                sampleTime = frame / SessionFrameRate,
                root2DOverride = new KimodoRigidTransform
                {
                    t = position,
                    q = Quaternion.LookRotation(heading, Vector3.up)
                },
                enableMask = new KimodoConstraintMask { rootPosition = true, rootHeading = true },
                validMask = new KimodoConstraintMask { rootPosition = true, rootHeading = true }
            };
        }

        private static Vector2 EvaluateBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float oneMinus = 1f - t;
            return oneMinus * oneMinus * oneMinus * p0 +
                3f * oneMinus * oneMinus * t * p1 +
                3f * oneMinus * t * t * p2 +
                t * t * t * p3;
        }

        private static Vector2 EvaluateBezierTangent(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float oneMinus = 1f - t;
            return 3f * oneMinus * oneMinus * (p1 - p0) +
                6f * oneMinus * t * (p2 - p1) +
                3f * t * t * (p3 - p2);
        }

        private static KimodoMarkerSampleResult BuildRoot2DConstraint(
            JObject value,
            float targetRootHeight,
            double sampleTime,
            int constraintIndex)
        {
            KimodoMarkerSampleResult poseSample = value?["pose"] is JObject locator
                ? ReadPoseSample(
                    RequirePoseReference(locator),
                    $"constraints[{constraintIndex}].root2d")
                : null;
            Vector3 rootPosition = Vector3.zero;
            Quaternion rootRotation = Quaternion.identity;
            if (poseSample != null)
            {
                GetRootTransform(poseSample, out rootPosition, out rootRotation);
            }
            bool hasPosition = value?["position"] != null;
            bool hasHeading = value?["heading"] != null;
            bool hasRootPose = value?["pose"] is JObject;
            if (hasPosition != hasHeading)
            {
                throw new InvalidOperationException($"constraints[{constraintIndex}].root2d requires position and heading together.");
            }
            if (hasPosition)
            {
                Vector2 position = RequiredVector2(value, "position");
                Vector2 heading = RequiredVector2(value, "heading");
                if (heading.sqrMagnitude <= 1e-8f)
                {
                    throw new InvalidOperationException($"constraints[{constraintIndex}].root2d.heading must be non-zero.");
                }
                rootPosition = new Vector3(position.x, targetRootHeight, position.y);
                rootRotation = Quaternion.LookRotation(new Vector3(heading.x, 0f, heading.y), Vector3.up);
            }
            else if (value?["pose"] == null)
            {
                throw new InvalidOperationException($"constraints[{constraintIndex}].root2d requires pose or position plus heading.");
            }

            var result = new KimodoMarkerSampleResult
            {
                constraintMode = "root2d",
                sampleTime = sampleTime,
                root2DOverride = new KimodoRigidTransform
                {
                    t = rootPosition,
                    q = rootRotation
                },
                enableMask = new KimodoConstraintMask
                {
                    rootPosition = hasPosition || hasRootPose,
                    rootHeading = hasPosition || hasRootPose
                },
                validMask = new KimodoConstraintMask
                {
                    rootPosition = hasPosition || hasRootPose,
                    rootHeading = hasPosition || hasRootPose
                }
            };
            result.enableMask.muscle = false;
            result.enableMask.rootTQ = false;
            result.enableMask.leftFootTQ = false;
            result.enableMask.rightFootTQ = false;
            return result;
        }

        private static KimodoMarkerSampleResult BuildReferencedPoseConstraint(
            JObject value,
            string constraintType,
            RetargetSkeleton targetCache,
            string modelName,
            float frameRate,
            double sampleTime,
            int constraintIndex)
        {
            if (value?["pose"] is not JObject poseReference)
            {
                throw new InvalidOperationException($"constraints[{constraintIndex}].{constraintType.Replace('-', '_')}.pose is required.");
            }
            KimodoMarkerSampleResult sourceResult = ReadPoseSample(
                RequirePoseReference(poseReference),
                $"constraints[{constraintIndex}].{constraintType.Replace('-', '_')}");
            return BuildModelNativeConstraintSample(
                sourceResult, constraintType, targetCache, modelName, frameRate, sampleTime);
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
            if (!KimodoConstraintPosePipeline.TryApply(
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
            ConfigureConstraintIntent(converted, constraintType, sampleTime);
            return converted;
        }

        private static void ConfigureConstraintIntent(
            KimodoMarkerSampleResult sample,
            string constraintType,
            double sampleTime)
        {
            string type = (constraintType ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
            KimodoConstraintMask valid = KimodoConstraintMask.FromSample(sample);
            if (type == "fullbody" && !valid.muscle)
            {
                throw new InvalidOperationException("FullBody pose constraint requires valid muscle data.");
            }
            bool effectorValid = type switch
            {
                "left-hand" => valid.leftHand,
                "right-hand" => valid.rightHand,
                "left-foot" => valid.leftFoot,
                "right-foot" => valid.rightFoot,
                "fullbody" => true,
                _ => false
            };
            if (!effectorValid)
            {
                throw new InvalidOperationException($"Pose constraint '{type}' has no valid target data.");
            }
            sample.constraintMode = type;
            sample.enableMask = KimodoConstraintMask.ForType(type);
            sample.sampleTime = sampleTime;
            sample.enabled = true;
        }

        internal static float ResolveTargetRootHeight(RetargetSkeleton targetCache)
        {
            if (targetCache != null &&
                targetCache.GetBonePose(
                    HumanBodyBones.Hips,
                    out Vector3 hipsPosition,
                    out _))
            {
                return hipsPosition.y;
            }

            return 0f;
        }

        private static JObject Route(string intent, string command, string next = null)
        {
            var route = new JObject { ["intent"] = intent, ["command"] = command };
            if (!string.IsNullOrWhiteSpace(next)) route["next"] = next;
            return route;
        }

        private static bool TrySampleDirectSkeletonConstraint(
            TimelineCharacterRecord source,
            RetargetSkeleton targetCache,
            string modelName,
            string constraintType,
            double sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (source?.Root == null || !KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
            {
                return false;
            }

            Transform[] allSourceTransforms = source.Root.GetComponentsInChildren<Transform>(true);
            Transform[] rootCandidates = allSourceTransforms
                .Where(transform => string.Equals(
                    transform.name, targetCache.canonicalRootBoneName, StringComparison.Ordinal))
                .ToArray();
            Transform sourceSkeletonRoot = rootCandidates.Length == 1 ? rootCandidates[0] : null;
            if (sourceSkeletonRoot == null)
            {
                error = $"incompatible_skeleton: source must contain one unambiguous '{targetCache.canonicalRootBoneName}' root bone";
                return false;
            }
            Transform[] sourceTransforms = sourceSkeletonRoot.GetComponentsInChildren<Transform>(true);
            var sourceNames = sourceTransforms.ToDictionary(transform => transform, transform => transform.name);
            var sourceByPath = sourceTransforms.ToDictionary(
                transform => KimodoRetargetAvatarUtility.CalculateTransformPath(
                    transform, sourceSkeletonRoot, targetCache.canonicalRootBoneName, sourceNames),
                transform => transform,
                StringComparer.Ordinal);
            var targetPaths = new HashSet<string>(targetCache.bonePaths, StringComparer.Ordinal);
            string missing = sourceByPath.Keys.FirstOrDefault(path => !targetPaths.Contains(path));
            if (missing != null)
            {
                error = $"incompatible_skeleton: target skeleton is missing source bone path '{missing}'";
                return false;
            }

            KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(targetCache);
            for (int i = 0; i < targetCache.bonePaths.Length; i++)
            {
                if (sourceByPath.TryGetValue(targetCache.bonePaths[i], out Transform sourceTransform) &&
                    targetCache.boneTransforms[i] != null)
                {
                    targetCache.boneTransforms[i].localPosition = sourceTransform.localPosition;
                    targetCache.boneTransforms[i].localRotation = sourceTransform.localRotation;
                }
            }
            BoneSample targetSample = KimodoRetargetSamplingUtility.CaptureBoneSample(targetCache);
            if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    targetSample, targetCache, modelName, constraintType, sampleTime, out sample, out error))
            {
                return false;
            }
            return true;
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
                if (type != "fullbody" && type != "root2d")
                {
                    throw new InvalidOperationException($"constraint_types[{i}] must be fullbody or root2d.");
                }
                types.Add(type);
            }
            return types;
        }

        private static List<double> ParsePoseTimes(JToken token, int poseCount)
        {
            if (token == null)
            {
                return null;
            }
            if (token is not JArray values || values.Count != poseCount)
            {
                throw new InvalidOperationException("times count must match pose_refs count.");
            }

            var times = new List<double>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                if ((values[i].Type != JTokenType.Float && values[i].Type != JTokenType.Integer) ||
                    double.IsNaN(values[i].Value<double>()) ||
                    double.IsInfinity(values[i].Value<double>()))
                {
                    throw new InvalidOperationException($"times[{i}] must be a finite number.");
                }
                times.Add(values[i].Value<double>());
            }
            return times;
        }

        private static List<string> ParsePoseConstraintTypes(JToken token, int poseCount)
        {
            if (token == null)
            {
                return ResolvePoseConstraintTypes(poseCount, null);
            }
            if (token is not JArray values || values.Count != poseCount)
            {
                throw new InvalidOperationException("constraint_types count must match pose_refs count.");
            }
            return ResolvePoseConstraintTypes(
                poseCount,
                values.Select((value, index) => value.Type == JTokenType.String
                    ? value.Value<string>()
                    : throw new InvalidOperationException($"constraint_types[{index}] must be a string.")).ToArray());
        }

        private static bool TrySamplePoseConstraint(
            ResolvedCharacter pose,
            RetargetSkeleton targetCache,
            string modelName,
            string constraintType,
            double sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (pose.Animator == null || !KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
            {
                return false;
            }

            try
            {
                var humanPose = new HumanPose();
                using (var poseHandler = new HumanPoseHandler(pose.Avatar, pose.Animator.transform))
                {
                    poseHandler.GetHumanPose(ref humanPose);
                }
                KimodoRetargetClipWriter.EnsureHumanPoseMuscles(ref humanPose);
                KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(targetCache);
                targetCache.poseHandler.SetHumanPose(ref humanPose);
                BoneSample targetSample = KimodoRetargetSamplingUtility.CaptureBoneSample(targetCache);
                if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                        targetSample,
                        targetCache,
                        modelName,
                        constraintType,
                        sampleTime,
                        out sample,
                        out error))
                {
                    return false;
                }

                if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        targetCache,
                        out MuscleSample evaluatedSample,
                        out error))
                {
                    return false;
                }
                sample.sampleData = evaluatedSample;
                sample.enableMask ??= new KimodoConstraintMask();
                sample.enableMask.muscle = true;
                sample.enableMask.rootTQ = true;
                sample.enableMask.leftFootTQ = true;
                sample.enableMask.rightFootTQ = true;
                sample.validMask ??= new KimodoConstraintMask();
                sample.validMask.muscle = true;
                sample.validMask.rootTQ = true;
                sample.validMask.leftFootTQ = true;
                sample.validMask.rightFootTQ = true;

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

    }
}
