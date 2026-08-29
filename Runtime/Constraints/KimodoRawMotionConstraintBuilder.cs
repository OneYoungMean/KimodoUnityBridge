using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>Builds protocol constraints directly from Kimodo raw motion.</summary>
    internal static class KimodoRawMotionConstraintBuilder
    {
        internal const int HeadingOverrideFrameInterval = 30;

        internal static bool TryBuildFullBodyFrame(
            KimodoRawMotionData motion,
            string modelName,
            int frameIndex,
            out KimodoConstraintInternalData frame,
            out string error)
        {
            frame = null;
            error = string.Empty;
            if (motion == null)
            {
                error = "Motion data is null.";
                return false;
            }

            KimodoRigProfileDatabase.ResolveProfile(modelName, out _, out string[] profileJointNames, out _);
            if (!KimodoRawMotionUtility.TryResolveMotionJointIndices(
                    motion,
                    profileJointNames,
                    allowPartialJoints: false,
                    out int[] motionJointIndices,
                    out error,
                    allowPositionalFallback: false))
            {
                return false;
            }

            int sourceFrame = Mathf.Clamp(frameIndex, 0, Mathf.Max(0, motion.FrameCount - 1));
            if (!motion.TryReadUnityRootPosition(sourceFrame, out Vector3 rootPosition))
            {
                error = $"Raw motion root position is invalid at frame {sourceFrame}.";
                return false;
            }

            int rotationJointCount = KimodoRawMotionUtility.ResolveRotationJointCount(motion);
            if (rotationJointCount <= 0)
            {
                error = "Raw motion local rotations are empty.";
                return false;
            }

            var localJointAxisAngles = new List<Vector3>(motionJointIndices.Length);
            for (int i = 0; i < motionJointIndices.Length; i++)
            {
                int motionJointIndex = motionJointIndices[i];
                if (!motion.TryReadUnityLocalRotation(
                        sourceFrame,
                        motionJointIndex,
                        rotationJointCount,
                        out Quaternion localRotation))
                {
                    error = $"Raw motion local rotation is invalid at frame {sourceFrame}, " +
                        $"profile joint '{profileJointNames[i]}'.";
                    return false;
                }

                localJointAxisAngles.Add(
                    KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(localRotation));
            }

            frame = new KimodoConstraintInternalData
            {
                rootPosition = rootPosition,
                localJointAxisAngles = localJointAxisAngles
            };
            return true;
        }

        internal static string BuildFullBodyConstraintsJson(
            KimodoRawMotionData motion,
            string modelName,
            IReadOnlyList<int> frames,
            double sampleTimeOffsetSeconds = 0.0,
            double? clipDurationSeconds = null)
        {
            if (motion == null || frames == null || frames.Count == 0)
            {
                throw new InvalidOperationException("FullBody bake requires motion keyframes.");
            }

            var fullBodyFrames = new List<KimodoConstraintInternalData>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                int frame = Mathf.Clamp(frames[i], 0, Mathf.Max(0, motion.FrameCount - 1));
                if (!TryBuildFullBodyFrame(motion, modelName, frame, out KimodoConstraintInternalData data, out string error))
                {
                    throw new InvalidOperationException($"FullBody bake sample failed at frame {frame}: {error}");
                }

                data.sampleTime = sampleTimeOffsetSeconds + frame / (double)motion.FrameRate;
                fullBodyFrames.Add(data);
            }

            return BuildFullBodyConstraints(
                fullBodyFrames,
                motion.FrameRate,
                clipDurationSeconds ?? 0.0,
                includeSmoothRoot: true).ToString(Formatting.None);
        }

        internal static JArray BuildFullBodyConstraints(
            IReadOnlyList<KimodoConstraintInternalData> frames,
            float frameRate,
            double clipDurationSeconds,
            bool includeSmoothRoot = false)
        {
            var result = new JArray();
            if (frames == null || frames.Count == 0)
            {
                return result;
            }

            var frameIndices = new JArray();
            var rootPositions = new JArray();
            var localRotations = new JArray();
            var smoothRoot2D = includeSmoothRoot ? new JArray() : null;
            float fps = frameRate > 0f ? frameRate : KimodoMotionModelProfiles.DefaultFrameRate;
            int maxFrame = clipDurationSeconds > 0.0
                ? Mathf.Max(0, KimodoFrameTimeUtility.SecondsToFrameCount(clipDurationSeconds, fps) - 1)
                : int.MaxValue;

            for (int i = 0; i < frames.Count; i++)
            {
                KimodoConstraintInternalData frame = frames[i];
                if (frame == null)
                {
                    continue;
                }

                int index = Mathf.Clamp(KimodoFrameTimeUtility.SecondsToFrameIndex(frame.sampleTime, fps), 0, maxFrame);
                Vector3 root = ToProtocolPosition(frame.rootPosition);
                frameIndices.Add(index);
                rootPositions.Add(new JArray(root.x, root.y, root.z));
                smoothRoot2D?.Add(new JArray(root.x, root.z));
                localRotations.Add(BuildProtocolJoints(frame.localJointAxisAngles));
            }

            if (frameIndices.Count == 0)
            {
                return result;
            }

            var constraint = new JObject
            {
                ["type"] = "fullbody",
                ["frame_indices"] = frameIndices,
                ["root_positions"] = rootPositions,
                ["local_joints_rot"] = localRotations
            };
            if (smoothRoot2D != null)
            {
                constraint["smooth_root_2d"] = smoothRoot2D;
            }
            result.Add(constraint);
            return result;
        }

        internal static string BuildRoot2DConstraintsJson(
            KimodoRawMotionData motion,
            string modelName,
            IReadOnlyList<int> frames,
            double sampleTimeOffsetSeconds = 0.0,
            double? clipDurationSeconds = null)
        {
            if (motion == null || frames == null || frames.Count == 0)
            {
                throw new InvalidOperationException("Root2D bake requires motion keyframes.");
            }

            float frameRate = motion.FrameRate;
            int maxFrame = clipDurationSeconds.HasValue
                ? Mathf.Max(0, KimodoFrameTimeUtility.SecondsToFrameCount(clipDurationSeconds.Value, frameRate) - 1)
                : int.MaxValue;
            var frameIndices = new JArray();
            var roots = new JArray();
            var headings = new JArray();
            for (int i = 0; i < frames.Count; i++)
            {
                int sourceFrame = Mathf.Clamp(frames[i], 0, Mathf.Max(0, motion.FrameCount - 1));
                if (!TryBuildFullBodyFrame(motion, modelName, sourceFrame, out KimodoConstraintInternalData data, out string error))
                {
                    throw new InvalidOperationException($"Root2D bake sample failed at frame {sourceFrame}: {error}");
                }

                int frame = KimodoFrameTimeUtility.SecondsToFrameIndex(
                    sampleTimeOffsetSeconds + sourceFrame / (double)frameRate,
                    frameRate);
                frameIndices.Add(Mathf.Clamp(frame, 0, maxFrame));
                Vector3 root = ToProtocolPosition(data.rootPosition);
                roots.Add(new JArray(root.x, root.z));
                Quaternion heading = ResolveRootRotation(data);
                Vector3 forward = heading * Vector3.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude <= 1e-8f) forward = Vector3.forward;
                forward.Normalize();
                headings.Add(new JArray(forward.z, -forward.x));
            }

            return new JArray(new JObject
            {
                ["type"] = "root2d",
                ["frame_indices"] = frameIndices,
                ["smooth_root_2d"] = roots,
                ["global_root_heading"] = headings
            }).ToString(Formatting.None);
        }

        internal static string BuildLoopConstraintJson(
            KimodoRawMotionData motion,
            string modelName,
            int runtimeTrimStartFrame,
            int targetFrameCount,
            int runtimeFrameCount,
            float frameRate)
        {
            if (motion == null || motion.FrameCount != targetFrameCount ||
                runtimeTrimStartFrame < 0 || targetFrameCount <= 1 || runtimeFrameCount <= 0 || frameRate <= 0f)
            {
                throw new InvalidOperationException("Loop constraint frame range is invalid.");
            }

            int terminalFrame = runtimeTrimStartFrame + targetFrameCount - 1;
            int virtualTailFrame = runtimeFrameCount - 1;
            if (terminalFrame >= runtimeFrameCount)
            {
                throw new InvalidOperationException("Loop terminal frame is outside the runtime range.");
            }
            bool hasFirstFrame = TryBuildFullBodyFrame(
                motion, modelName, 0, out KimodoConstraintInternalData first, out string firstError);
            bool hasTailFrame = TryBuildFullBodyFrame(
                motion, modelName, motion.FrameCount - 1, out KimodoConstraintInternalData tail, out string tailError);
            if (!hasFirstFrame || !hasTailFrame)
            {
                throw new InvalidOperationException(
                    $"Loop pass 1 raw sampling failed: first='{firstError}', tail='{tailError}'.");
            }

            Quaternion firstRotation = ResolveRootRotation(first);
            Quaternion tailRotation = ResolveRootRotation(tail);
            Vector3 planarDelta = tail.rootPosition - first.rootPosition;
            planarDelta.y = 0f;
            float sourceSpanFrames = targetFrameCount - 1f;
            float headRatio = runtimeTrimStartFrame / sourceSpanFrames;
            float tailRatio = (virtualTailFrame - terminalFrame) / sourceSpanFrames;
            Vector3 virtualHeadPosition = first.rootPosition - planarDelta * headRatio;
            Vector3 virtualTailPosition = tail.rootPosition + planarDelta * tailRatio;
            virtualHeadPosition.y = first.rootPosition.y;
            virtualTailPosition.y = tail.rootPosition.y;

            float firstYaw = ResolvePlanarYaw(firstRotation);
            float tailYaw = ResolvePlanarYaw(tailRotation);
            float yawDelta = Mathf.DeltaAngle(firstYaw, tailYaw);
            Quaternion virtualHeadHeading = Quaternion.Euler(0f, firstYaw - yawDelta * headRatio, 0f);
            Quaternion virtualTailHeading = Quaternion.Euler(0f, tailYaw + yawDelta * tailRatio, 0f);

            first.sampleTime = runtimeTrimStartFrame / (double)frameRate;
            KimodoConstraintInternalData terminal = first.Clone();
            terminal.sampleTime = terminalFrame / (double)frameRate;
            terminal.rootPosition = tail.rootPosition;
            Quaternion firstYawRotation = Quaternion.Euler(0f, firstYaw, 0f);
            Quaternion tailYawRotation = Quaternion.Euler(0f, tailYaw, 0f);
            Quaternion firstTilt = Quaternion.Inverse(firstYawRotation) * firstRotation;
            terminal.localJointAxisAngles[0] = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(
                tailYawRotation * firstTilt);

            var constraints = new JArray
            {
                BuildRoot2DConstraint(
                    new[] { 0, virtualTailFrame },
                    new[] { virtualHeadPosition, virtualTailPosition },
                    new[] { virtualHeadHeading, virtualTailHeading }),
                BuildFullBodyConstraints(
                    new[] { first, terminal },
                    frameRate,
                    runtimeFrameCount / (double)frameRate,
                    includeSmoothRoot: true)[0]
            };
            return constraints.ToString(Formatting.None);
        }

        internal static string BuildPathAngleConstraintJson(
            KimodoRawMotionData motion,
            string modelName,
            float pathBeginAngleDegrees,
            float pathEndAngleDegrees,
            int runtimeTrimStartFrame,
            int targetFrameCount,
            int runtimeFrameCount,
            float frameRate,
            string existingConstraintsJson,
            int regularFrameInterval = 0)
        {
            if (motion == null || motion.FrameCount != targetFrameCount ||
                runtimeTrimStartFrame < 0 || targetFrameCount <= 1 || runtimeFrameCount <= 0 || frameRate <= 0f ||
                regularFrameInterval < 0 ||
                float.IsNaN(pathBeginAngleDegrees) || float.IsInfinity(pathBeginAngleDegrees) ||
                float.IsNaN(pathEndAngleDegrees) || float.IsInfinity(pathEndAngleDegrees))
            {
                throw new InvalidOperationException("Path angle constraint frame range is invalid.");
            }
            if (!TryBuildFullBodyFrame(
                    motion, modelName, 0, out KimodoConstraintInternalData first, out string firstError))
            {
                throw new InvalidOperationException($"Path angle pass 1 raw sampling failed: {firstError}");
            }

            float pathLength = 0f;
            if (!motion.TryReadUnityRootPosition(0, out Vector3 previousRoot))
            {
                throw new InvalidOperationException("Path angle pass 1 has no valid first root position.");
            }
            for (int frame = 1; frame < motion.FrameCount; frame++)
            {
                if (!motion.TryReadUnityRootPosition(frame, out Vector3 root))
                {
                    throw new InvalidOperationException($"Path angle pass 1 has no valid root position at frame {frame}.");
                }
                pathLength += Vector2.Distance(
                    new Vector2(previousRoot.x, previousRoot.z),
                    new Vector2(root.x, root.z));
                previousRoot = root;
            }

            int terminalFrame = runtimeTrimStartFrame + targetFrameCount - 1;
            var frames = new HashSet<int> { runtimeTrimStartFrame, terminalFrame };
            if (runtimeTrimStartFrame > 0) frames.Add(0);
            if (terminalFrame < runtimeFrameCount - 1) frames.Add(runtimeFrameCount - 1);
            AddRegularFrames(frames, runtimeFrameCount, regularFrameInterval);
            CollectRootChannelState(existingConstraintsJson, runtimeFrameCount, frames, null);

            Quaternion startHeading = Quaternion.Euler(0f, pathBeginAngleDegrees, 0f);
            float totalAngleRadians = Mathf.DeltaAngle(
                pathBeginAngleDegrees,
                pathEndAngleDegrees) * Mathf.Deg2Rad;
            var orderedFrames = new List<int>(frames);
            orderedFrames.Sort();
            var positions = new List<Vector3>(orderedFrames.Count);
            var headings = new List<Quaternion>(orderedFrames.Count);
            for (int i = 0; i < orderedFrames.Count; i++)
            {
                float t = (orderedFrames[i] - runtimeTrimStartFrame) / (float)(targetFrameCount - 1);
                EvaluatePathAngleBezier(pathLength, totalAngleRadians, t, out Vector2 localPosition);
                Vector3 profileOffset = startHeading * new Vector3(localPosition.x, 0f, localPosition.y);
                Vector3 profilePosition = first.rootPosition + profileOffset;
                profilePosition.y = first.rootPosition.y;
                positions.Add(profilePosition);
                headings.Add(startHeading * Quaternion.Euler(0f, totalAngleRadians * Mathf.Rad2Deg * t, 0f));
            }

            return new JArray(BuildRoot2DConstraint(orderedFrames, positions, headings))
                .ToString(Formatting.None);
        }

        internal static string OverrideRoot2DHeadingsJson(string constraintsJson, float headingDegrees)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson) ||
                float.IsNaN(headingDegrees) || float.IsInfinity(headingDegrees))
            {
                throw new InvalidOperationException("Root2D heading override is invalid.");
            }

            JArray constraints = JArray.Parse(constraintsJson);
            JArray protocolHeading = BuildProtocolHeading(Quaternion.Euler(0f, headingDegrees, 0f));
            bool replaced = false;
            foreach (JObject constraint in constraints.Children<JObject>())
            {
                if (!string.Equals(constraint.Value<string>("type"), "root2d", StringComparison.OrdinalIgnoreCase) ||
                    constraint["frame_indices"] is not JArray frames)
                {
                    continue;
                }

                var headings = new JArray();
                for (int i = 0; i < frames.Count; i++)
                {
                    headings.Add(protocolHeading.DeepClone());
                }
                constraint["global_root_heading"] = headings;
                replaced = true;
            }
            if (!replaced)
            {
                throw new InvalidOperationException("Root2D heading override found no Root2D constraint.");
            }
            return constraints.ToString(Formatting.None);
        }

        internal static string BuildHeadingOverrideConstraintJson(
            KimodoRawMotionData motion,
            float headingDegrees,
            int runtimeTrimStartFrame,
            int targetFrameCount,
            int runtimeFrameCount,
            float frameRate,
            string existingConstraintsJson)
        {
            if (motion == null || motion.FrameCount != targetFrameCount ||
                runtimeTrimStartFrame < 0 || targetFrameCount <= 1 || runtimeFrameCount <= 0 || frameRate <= 0f ||
                float.IsNaN(headingDegrees) || float.IsInfinity(headingDegrees))
            {
                throw new InvalidOperationException("Heading override constraint frame range is invalid.");
            }

            int terminalFrame = runtimeTrimStartFrame + targetFrameCount - 1;
            var frames = new HashSet<int> { runtimeTrimStartFrame, terminalFrame };
            if (runtimeTrimStartFrame > 0) frames.Add(0);
            if (terminalFrame < runtimeFrameCount - 1) frames.Add(runtimeFrameCount - 1);
            AddRegularFrames(frames, runtimeFrameCount, HeadingOverrideFrameInterval);
            var resolvedPositions = new Dictionary<int, Vector3>();
            CollectRootChannelState(existingConstraintsJson, runtimeFrameCount, frames, resolvedPositions);

            Quaternion heading = Quaternion.Euler(0f, headingDegrees, 0f);
            var orderedFrames = new List<int>(frames);
            orderedFrames.Sort();
            var positions = new List<Vector3>(orderedFrames.Count);
            var headings = new List<Quaternion>(orderedFrames.Count);
            for (int i = 0; i < orderedFrames.Count; i++)
            {
                int frame = orderedFrames[i];
                if (!resolvedPositions.TryGetValue(frame, out Vector3 position))
                {
                    float t = (frame - runtimeTrimStartFrame) / (float)(targetFrameCount - 1);
                    position = EvaluateRawRootPosition(motion, t);
                }
                positions.Add(position);
                headings.Add(heading);
            }
            return new JArray(BuildRoot2DConstraint(orderedFrames, positions, headings))
                .ToString(Formatting.None);
        }

        private static void AddRegularFrames(ISet<int> frames, int runtimeFrameCount, int interval)
        {
            if (interval <= 0) return;
            for (int frame = 0; frame < runtimeFrameCount; frame += interval)
            {
                frames.Add(frame);
            }
            frames.Add(runtimeFrameCount - 1);
        }

        private static void CollectRootChannelState(
            string constraintsJson,
            int runtimeFrameCount,
            ISet<int> output,
            IDictionary<int, Vector3> resolvedPositions)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson)) return;
            JArray constraints = JArray.Parse(constraintsJson);
            foreach (JObject constraint in constraints.Children<JObject>())
            {
                string type = constraint.Value<string>("type");
                if ((!string.Equals(type, "fullbody", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(type, "root2d", StringComparison.OrdinalIgnoreCase)) ||
                    constraint["frame_indices"] is not JArray frames)
                {
                    continue;
                }
                JArray roots = constraint[type.Equals("fullbody", StringComparison.OrdinalIgnoreCase)
                    ? "root_positions"
                    : "smooth_root_2d"] as JArray;
                for (int i = 0; i < frames.Count; i++)
                {
                    JToken frameToken = frames[i];
                    if (frameToken.Type == JTokenType.Integer)
                    {
                        int frame = frameToken.Value<int>();
                        if (frame < 0 || frame >= runtimeFrameCount) continue;
                        output.Add(frame);
                        if (resolvedPositions != null && roots != null && i < roots.Count && roots[i] is JArray root)
                        {
                            int zIndex = type.Equals("fullbody", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                            if (root.Count > zIndex &&
                                (root[0].Type == JTokenType.Float || root[0].Type == JTokenType.Integer) &&
                                (root[zIndex].Type == JTokenType.Float || root[zIndex].Type == JTokenType.Integer))
                            {
                                resolvedPositions[frame] = new Vector3(
                                    -root[0].Value<float>(),
                                    0f,
                                    root[zIndex].Value<float>());
                            }
                        }
                    }
                }
            }
        }

        private static Vector3 EvaluateRawRootPosition(KimodoRawMotionData motion, float t)
        {
            float frame = t * (motion.FrameCount - 1);
            int firstFrame = Mathf.Clamp(Mathf.FloorToInt(frame), 0, motion.FrameCount - 2);
            int secondFrame = firstFrame + 1;
            if (!motion.TryReadUnityRootPosition(firstFrame, out Vector3 first) ||
                !motion.TryReadUnityRootPosition(secondFrame, out Vector3 second))
            {
                throw new InvalidOperationException("Heading override could not sample the baseline Root position.");
            }
            return Vector3.LerpUnclamped(first, second, frame - firstFrame);
        }

        private static void EvaluatePathAngleBezier(
            float pathLength,
            float totalAngleRadians,
            float t,
            out Vector2 position)
        {
            if (pathLength <= 1e-6f)
            {
                position = Vector2.zero;
                return;
            }
            if (Mathf.Abs(totalAngleRadians) <= 1e-5f)
            {
                position = new Vector2(0f, pathLength * t);
                return;
            }

            float radius = pathLength / totalAngleRadians;
            float step = Mathf.Min(1f, (Mathf.PI * 0.5f) / Mathf.Abs(totalAngleRadians));
            float startT = Mathf.Floor(t / step) * step;
            float endT = startT + step;
            float u = Mathf.Clamp01((t - startT) / step);
            Vector2 p0 = EvaluateCircularPath(radius, totalAngleRadians, startT);
            Vector2 p3 = EvaluateCircularPath(radius, totalAngleRadians, endT);
            Vector2 d0 = EvaluateCircularHeading(totalAngleRadians, startT);
            Vector2 d1 = EvaluateCircularHeading(totalAngleRadians, endT);
            float segmentAngle = Mathf.Abs(totalAngleRadians * (endT - startT));
            float handle = 4f / 3f * Mathf.Tan(segmentAngle * 0.25f) * Mathf.Abs(radius);
            position = EvaluateBezier(p0, p0 + d0 * handle, p3 - d1 * handle, p3, u);
        }

        private static Vector2 EvaluateCircularPath(float radius, float angle, float t)
        {
            float at = angle * t;
            return new Vector2(radius * (1f - Mathf.Cos(at)), radius * Mathf.Sin(at));
        }

        private static Vector2 EvaluateCircularHeading(float angle, float t)
        {
            float at = angle * t;
            return new Vector2(Mathf.Sin(at), Mathf.Cos(at));
        }

        private static JObject BuildRoot2DConstraint(
            IReadOnlyList<int> frames,
            IReadOnlyList<Vector3> positions,
            IReadOnlyList<Quaternion> headings)
        {
            if (frames == null || positions == null || headings == null ||
                frames.Count != positions.Count || frames.Count != headings.Count || frames.Count == 0)
            {
                throw new InvalidOperationException("Root2D constraint rows must align.");
            }

            var frameIndices = new JArray();
            var roots = new JArray();
            var rootHeadings = new JArray();
            for (int i = 0; i < frames.Count; i++)
            {
                Vector3 root = ToProtocolPosition(positions[i]);
                frameIndices.Add(frames[i]);
                roots.Add(new JArray(root.x, root.z));
                rootHeadings.Add(BuildProtocolHeading(headings[i]));
            }

            return new JObject
            {
                ["type"] = "root2d",
                ["frame_indices"] = frameIndices,
                ["smooth_root_2d"] = roots,
                ["global_root_heading"] = rootHeadings
            };
        }

        private static JArray BuildProtocolHeading(Quaternion heading)
        {
            Vector3 forward = heading * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 1e-8f) forward = Vector3.forward;
            forward.Normalize();
            return new JArray(forward.z, -forward.x);
        }

        private static Quaternion ResolveRootRotation(KimodoConstraintInternalData frame)
        {
            if (frame?.localJointAxisAngles == null || frame.localJointAxisAngles.Count == 0)
            {
                throw new InvalidOperationException("Raw motion FullBody frame has no root rotation.");
            }
            return KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(frame.localJointAxisAngles[0]);
        }

        private static float ResolvePlanarYaw(Quaternion rotation)
        {
            Vector3 forward = rotation * Vector3.forward;
            forward.y = 0f;
            return forward.sqrMagnitude <= 1e-8f ? 0f : Quaternion.LookRotation(forward).eulerAngles.y;
        }

        private static JArray BuildProtocolJoints(IReadOnlyList<Vector3> jointAxisAngles)
        {
            var joints = new JArray();
            if (jointAxisAngles == null) return joints;
            for (int i = 0; i < jointAxisAngles.Count; i++)
            {
                Vector3 axisAngle = ToProtocolAxisAngle(jointAxisAngles[i]);
                joints.Add(new JArray(axisAngle.x, axisAngle.y, axisAngle.z));
            }
            return joints;
        }

        private static Vector3 ToProtocolPosition(Vector3 position) => new Vector3(-position.x, position.y, position.z);

        private static Vector2 EvaluateBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float oneMinus = 1f - t;
            return oneMinus * oneMinus * oneMinus * p0 +
                3f * oneMinus * oneMinus * t * p1 +
                3f * oneMinus * t * t * p2 +
                t * t * t * p3;
        }

        private static Vector3 ToProtocolAxisAngle(Vector3 unityAxisAngle)
        {
            Quaternion unity = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(unityAxisAngle);
            Quaternion protocol = new Quaternion(unity.x, -unity.y, -unity.z, unity.w);
            return KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(protocol);
        }
    }
}
