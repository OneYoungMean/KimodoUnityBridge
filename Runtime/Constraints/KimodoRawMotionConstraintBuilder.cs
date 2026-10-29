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

        private sealed class Root2DPathRow
        {
            public int Frame;
            public Vector2 Position;
            public JArray Heading;
        }

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
                Vector3 forward = KimodoMotionMath.ResolvePlanarHeading(heading) * Vector3.forward;
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

        internal static string BuildPathDistanceConstraintJson(
            KimodoRawMotionData motion,
            string modelName,
            float distanceOverride,
            int runtimeTrimStartFrame,
            int targetFrameCount,
            int runtimeFrameCount,
            float frameRate,
            string existingConstraintsJson)
        {
            if (motion == null || motion.FrameCount != targetFrameCount ||
                runtimeTrimStartFrame < 0 || targetFrameCount <= 1 || runtimeFrameCount <= 0 ||
                runtimeTrimStartFrame > runtimeFrameCount - targetFrameCount || frameRate <= 0f ||
                distanceOverride < 0f || float.IsNaN(distanceOverride) || float.IsInfinity(distanceOverride))
            {
                throw new InvalidOperationException("Path distance constraint frame range is invalid.");
            }

            var rows = new SortedDictionary<int, Root2DPathRow>();
            JObject template = null;
            if (!string.IsNullOrWhiteSpace(existingConstraintsJson))
            {
                JArray existing = JArray.Parse(existingConstraintsJson);
                foreach (JObject constraint in existing.Children<JObject>())
                {
                    if (!string.Equals(
                            constraint.Value<string>("type"),
                            "root2d",
                            StringComparison.OrdinalIgnoreCase) ||
                        constraint["frame_indices"] is not JArray frames ||
                        constraint["smooth_root_2d"] is not JArray positions)
                    {
                        continue;
                    }

                    template ??= (JObject)constraint.DeepClone();
                    JArray headings = constraint["global_root_heading"] as JArray;
                    for (int i = 0; i < frames.Count && i < positions.Count; i++)
                    {
                        if (frames[i].Type != JTokenType.Integer || positions[i] is not JArray position || position.Count < 2)
                            continue;
                        int frame = frames[i].Value<int>();
                        if (frame < 0 || frame >= runtimeFrameCount)
                            throw new InvalidOperationException("Path distance constraint contains a Root2D frame outside the runtime range.");
                        rows[frame] = new Root2DPathRow
                        {
                            Frame = frame,
                            Position = new Vector2(position[0].Value<float>(), position[1].Value<float>()),
                            Heading = headings != null && i < headings.Count && headings[i] is JArray heading
                                ? (JArray)heading.DeepClone()
                                : null
                        };
                    }
                }
            }

            var sampleFrames = new List<int>();
            if (rows.Count == 0)
            {
                for (int frame = 0; frame < targetFrameCount; frame++)
                    sampleFrames.Add(frame);
            }
            else
            {
                int startFrame = runtimeTrimStartFrame;
                int terminalFrame = runtimeTrimStartFrame + targetFrameCount - 1;
                if (!rows.ContainsKey(startFrame)) sampleFrames.Add(0);
                if (!rows.ContainsKey(terminalFrame)) sampleFrames.Add(targetFrameCount - 1);
            }
            if (sampleFrames.Count > 0)
            {
                JArray sampled = JArray.Parse(BuildRoot2DConstraintsJson(
                    motion,
                    modelName,
                    sampleFrames,
                    runtimeTrimStartFrame / (double)frameRate,
                    runtimeFrameCount / (double)frameRate));
                JObject sampledRoot = sampled[0] as JObject;
                template ??= (JObject)sampledRoot.DeepClone();
                JArray frames = (JArray)sampledRoot["frame_indices"];
                JArray positions = (JArray)sampledRoot["smooth_root_2d"];
                JArray headings = (JArray)sampledRoot["global_root_heading"];
                for (int i = 0; i < frames.Count; i++)
                {
                    int frame = frames[i].Value<int>();
                    rows[frame] = new Root2DPathRow
                    {
                        Frame = frame,
                        Position = new Vector2(positions[i][0].Value<float>(), positions[i][1].Value<float>()),
                        Heading = (JArray)headings[i].DeepClone()
                    };
                }
            }

            int coreStartFrame = runtimeTrimStartFrame;
            int coreEndFrame = runtimeTrimStartFrame + targetFrameCount - 1;
            var coreRows = new List<Root2DPathRow>();
            foreach (Root2DPathRow row in rows.Values)
            {
                if (row.Frame >= coreStartFrame && row.Frame <= coreEndFrame)
                    coreRows.Add(row);
            }
            if (coreRows.Count < 2)
                throw new InvalidOperationException("Path distance pass requires Root2D samples at both core endpoints.");

            float sourceDistance = 0f;
            for (int i = 1; i < coreRows.Count; i++)
                sourceDistance += Vector2.Distance(coreRows[i - 1].Position, coreRows[i].Position);
            Vector2 origin = coreRows[0].Position;
            Vector2 forward = Vector2.up;
            if (coreRows[0].Heading != null && coreRows[0].Heading.Count >= 2)
            {
                forward = new Vector2(
                    coreRows[0].Heading[1].Value<float>(),
                    coreRows[0].Heading[0].Value<float>());
                if (forward.sqrMagnitude > 1e-8f) forward.Normalize();
                else forward = Vector2.up;
            }

            JArray outputFrames = new JArray();
            JArray outputPositions = new JArray();
            JArray outputHeadings = new JArray();
            foreach (Root2DPathRow row in rows.Values)
            {
                Vector2 scaled;
                if (sourceDistance > 1e-6f)
                {
                    scaled = origin + (row.Position - origin) * (distanceOverride / sourceDistance);
                }
                else
                {
                    float t = Mathf.Clamp01((row.Frame - coreStartFrame) / (float)(targetFrameCount - 1));
                    scaled = origin + forward * (distanceOverride * t);
                }
                outputFrames.Add(row.Frame);
                outputPositions.Add(new JArray(scaled.x, scaled.y));
                outputHeadings.Add(row.Heading?.DeepClone() ?? new JArray(1f, 0f));
            }

            template["frame_indices"] = outputFrames;
            template["smooth_root_2d"] = outputPositions;
            template["global_root_heading"] = outputHeadings;
            return new JArray(template).ToString(Formatting.None);
        }

        internal static string BuildLoopConstraintJson(
            KimodoRawMotionData motion,
            string modelName,
            int runtimeTrimStartFrame,
            int targetFrameCount,
            int runtimeFrameCount,
            float frameRate,
            bool lockPositionX = false,
            bool lockPositionY = true,
            bool lockPositionZ = false,
            bool lockRotationX = true,
            bool lockRotationY = false,
            bool lockRotationZ = true)
        {
            if (motion == null || motion.FrameCount < 2 || motion.FrameRate <= 0f ||
                runtimeTrimStartFrame < 0 || targetFrameCount <= 1 || runtimeFrameCount <= 0 || frameRate <= 0f)
            {
                throw new InvalidOperationException("Loop constraint frame range is invalid.");
            }

            int anchorSourceFrame = ResolveLoopAnchorFrameForGeneration(motion);
            if (!TryBuildFullBodyFrame(
                    motion, modelName, anchorSourceFrame,
                    out KimodoConstraintInternalData first, out string anchorError))
            {
                throw new InvalidOperationException(
                    $"Loop pass 1 anchor pose sampling failed at frame {anchorSourceFrame}: {anchorError}");
            }
            if (!TryBuildFullBodyFrame(
                    motion, modelName, motion.FrameCount - 1,
                    out KimodoConstraintInternalData tail, out string tailError))
            {
                throw new InvalidOperationException(
                    $"Loop pass 1 tail pose sampling failed at frame {motion.FrameCount - 1}: {tailError}");
            }
            if (!TryEstimatePlanarVelocity(motion, out Vector3 averageVelocity, out string velocityError))
            {
                throw new InvalidOperationException(
                    $"Loop pass 1 average velocity estimation failed: {velocityError}");
            }

            int terminalFrame = runtimeTrimStartFrame + targetFrameCount - 1;
            int virtualTailFrame = runtimeFrameCount - 1;
            if (terminalFrame >= runtimeFrameCount || virtualTailFrame < terminalFrame)
            {
                throw new InvalidOperationException("Loop terminal frame is outside the runtime range.");
            }

            Quaternion firstRotation = ResolveRootRotation(first);
            Quaternion tailRotation = ResolveRootRotation(tail);
            float targetSpanSeconds = targetFrameCount / frameRate;
            Vector3 projectedDelta = averageVelocity * targetSpanSeconds;
            projectedDelta.y = 0f;
            Vector3 projectedTerminalRoot = first.rootPosition + projectedDelta;
            Vector3 terminalRoot = new Vector3(
                lockPositionX ? first.rootPosition.x : projectedTerminalRoot.x,
                lockPositionY ? first.rootPosition.y : tail.rootPosition.y,
                lockPositionZ ? first.rootPosition.z : projectedTerminalRoot.z);
            Vector3 planarDelta = terminalRoot - first.rootPosition;
            planarDelta.y = 0f;
            float headRatio = runtimeTrimStartFrame / (float)targetFrameCount;
            float tailRatio = (virtualTailFrame - terminalFrame) / (float)targetFrameCount;
            Vector3 virtualHeadPosition = first.rootPosition - planarDelta * headRatio;
            Vector3 virtualTailPosition = terminalRoot + planarDelta * tailRatio;
            virtualHeadPosition.y = first.rootPosition.y;
            virtualTailPosition.y = terminalRoot.y;

            float firstYaw = KimodoMotionMath.ResolvePlanarYawDegrees(firstRotation);
            float tailYaw = KimodoMotionMath.ResolvePlanarYawDegrees(tailRotation);
            float yawDelta = Mathf.DeltaAngle(firstYaw, tailYaw);
            Quaternion virtualHeadHeading = Quaternion.Euler(0f, firstYaw - yawDelta * headRatio, 0f);
            Quaternion virtualTailHeading = Quaternion.Euler(0f, tailYaw + yawDelta * tailRatio, 0f);

            first.sampleTime = runtimeTrimStartFrame / (double)frameRate;
            KimodoConstraintInternalData terminal = first.Clone();
            terminal.sampleTime = terminalFrame / (double)frameRate;
            terminal.rootPosition = terminalRoot;
            Quaternion firstYawRotation = Quaternion.Euler(0f, firstYaw, 0f);
            Quaternion tailYawRotation = Quaternion.Euler(0f, tailYaw, 0f);
            Quaternion firstTilt = Quaternion.Inverse(firstYawRotation) * firstRotation;
            Quaternion tailTilt = Quaternion.Inverse(tailYawRotation) * tailRotation;
            Vector3 firstTiltEuler = ToSignedEuler(firstTilt);
            Vector3 tailTiltEuler = ToSignedEuler(tailTilt);
            Quaternion terminalTilt = Quaternion.Euler(
                lockRotationX ? firstTiltEuler.x : tailTiltEuler.x,
                0f,
                lockRotationZ ? firstTiltEuler.z : tailTiltEuler.z);
            Quaternion terminalYaw = Quaternion.Euler(0f, lockRotationY ? firstYaw : tailYaw, 0f);
            terminal.localJointAxisAngles[0] = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(
                terminalYaw * terminalTilt);

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

        internal static string BuildLoopWindowConstraintJson(
            KimodoRawMotionData motion,
            string modelName,
            int sourceStartFrame,
            int windowFrameCount,
            int sampleCount,
            int sourceOutputStartFrame,
            int tailStartFrame,
            int runtimeFrameCount,
            float frameRate,
            bool lockPositionX = false,
            bool lockPositionY = false,
            bool lockPositionZ = false,
            bool lockRotationX = false,
            bool lockRotationY = false,
            bool lockRotationZ = false,
            Vector3? targetAnchorPositionOverride = null,
            Quaternion? targetAnchorHeadingOverride = null)
        {
            if (motion == null || motion.FrameCount <= 0 || motion.FrameRate <= 0f ||
                sourceStartFrame < 0 || sourceStartFrame >= motion.FrameCount ||
                windowFrameCount <= 0 || sampleCount <= 0 || sampleCount > windowFrameCount ||
                sourceOutputStartFrame < 0 || tailStartFrame < 0 || runtimeFrameCount <= 0 || frameRate <= 0f ||
                tailStartFrame < sourceOutputStartFrame + windowFrameCount ||
                sourceOutputStartFrame + windowFrameCount > runtimeFrameCount ||
                tailStartFrame + windowFrameCount > runtimeFrameCount)
            {
                throw new InvalidOperationException("Loop window constraint frame range is invalid.");
            }
            if (sourceStartFrame + windowFrameCount > motion.FrameCount)
            {
                throw new InvalidOperationException("Loop window exceeds the source motion range.");
            }

            var sourceSamples = new List<KimodoConstraintInternalData>(sampleCount);
            var tailSamples = new List<KimodoConstraintInternalData>(sampleCount);
            for (int i = 0; i < sampleCount; i++)
            {
                int offset = sampleCount == 1
                    ? 0
                    : Mathf.RoundToInt(i * (windowFrameCount - 1f) / (sampleCount - 1f));
                int sourceFrame = sourceStartFrame + offset;
                if (!TryBuildFullBodyFrame(
                        motion,
                        modelName,
                        sourceFrame,
                        out KimodoConstraintInternalData source,
                        out string sourceError))
                {
                    throw new InvalidOperationException(
                        $"Loop window source sampling failed at frame {sourceFrame}: {sourceError}");
                }
                source.sampleTime = (sourceOutputStartFrame + offset) / (double)frameRate;
                sourceSamples.Add(source);

                KimodoConstraintInternalData tail = source.Clone();
                tail.sampleTime = (tailStartFrame + offset) / (double)frameRate;
                tailSamples.Add(tail);
            }
            ResolveLoopWindowTargetAnchor(
                motion,
                modelName,
                sourceSamples[0],
                targetAnchorPositionOverride,
                targetAnchorHeadingOverride,
                out Vector3 targetAnchorPosition,
                out Quaternion targetAnchorHeading);
            ApplyLoopAnchorOffset(
                sourceSamples[0],
                tailSamples,
                targetAnchorPosition,
                targetAnchorHeading,
                lockPositionX,
                lockPositionY,
                lockPositionZ,
                lockRotationX,
                lockRotationY,
                lockRotationZ);
            var samples = new List<KimodoConstraintInternalData>(sampleCount * 2);
            samples.AddRange(sourceSamples);
            samples.AddRange(tailSamples);

            return BuildFullBodyConstraints(
                samples,
                frameRate,
                runtimeFrameCount / (double)frameRate,
                includeSmoothRoot: true).ToString(Formatting.None);
        }

        internal static string ApplyLoopRoot2DHeadingOverridesJson(
            KimodoRawMotionData motion,
            string modelName,
            string constraintsJson,
            int sourceStartFrame,
            int windowFrameCount,
            int sourceOutputStartFrame,
            int tailStartFrame,
            int runtimeFrameCount,
            bool lockPositionX = false,
            bool lockPositionY = false,
            bool lockPositionZ = false,
            bool lockRotationX = false,
            bool lockRotationY = false,
            bool lockRotationZ = false,
            Vector3? targetAnchorPositionOverride = null,
            Quaternion? targetAnchorHeadingOverride = null)
        {
            int sourceTailFrame = sourceStartFrame + windowFrameCount - 1;
            int tailOutputFrame = tailStartFrame + windowFrameCount - 1;
            if (motion == null || motion.FrameCount <= 0 || motion.FrameRate <= 0f ||
                sourceStartFrame < 0 || windowFrameCount <= 0 || sourceTailFrame >= motion.FrameCount ||
                sourceOutputStartFrame < 0 || tailStartFrame < 0 ||
                sourceOutputStartFrame >= runtimeFrameCount || tailOutputFrame >= runtimeFrameCount)
            {
                throw new InvalidOperationException("Loop Root2D heading frame range is invalid.");
            }

            if (!TryBuildFullBodyFrame(
                    motion,
                    modelName,
                    sourceStartFrame,
                    out KimodoConstraintInternalData sourceStart,
                    out string startError))
            {
                throw new InvalidOperationException($"Loop start heading sampling failed: {startError}");
            }
            if (!TryBuildFullBodyFrame(
                    motion,
                    modelName,
                    sourceTailFrame,
                    out KimodoConstraintInternalData sourceTail,
                    out string tailError))
            {
                throw new InvalidOperationException($"Loop tail heading sampling failed: {tailError}");
            }
            ResolveLoopWindowTargetAnchor(
                motion,
                modelName,
                sourceStart,
                targetAnchorPositionOverride,
                targetAnchorHeadingOverride,
                out Vector3 targetAnchorPosition,
                out Quaternion targetAnchorHeading);
            ApplyLoopAnchorOffset(
                sourceStart,
                new List<KimodoConstraintInternalData> { sourceTail },
                targetAnchorPosition,
                targetAnchorHeading,
                lockPositionX,
                lockPositionY,
                lockPositionZ,
                lockRotationX,
                lockRotationY,
                lockRotationZ);

            int[] endpointFrames = { sourceOutputStartFrame, tailOutputFrame };
            KimodoConstraintInternalData[] endpointSamples = { sourceStart, sourceTail };
            Quaternion[] endpointHeadings =
            {
                ResolveRootRotation(sourceStart),
                ResolveRootRotation(sourceTail)
            };
            var constraints = string.IsNullOrWhiteSpace(constraintsJson)
                ? new JArray()
                : JArray.Parse(constraintsJson);
            var missingFrames = new List<int>(2);
            var missingPositions = new List<Vector3>(2);
            var missingHeadings = new List<Quaternion>(2);

            for (int endpointIndex = 0; endpointIndex < endpointFrames.Length; endpointIndex++)
            {
                int endpointFrame = endpointFrames[endpointIndex];
                JArray protocolHeading = BuildProtocolHeading(endpointHeadings[endpointIndex]);
                bool found = false;
                foreach (JObject constraint in constraints.Children<JObject>())
                {
                    if (!string.Equals(constraint.Value<string>("type"), "root2d", StringComparison.OrdinalIgnoreCase) ||
                        constraint["frame_indices"] is not JArray frameIndices)
                    {
                        continue;
                    }

                    JArray headings = constraint["global_root_heading"] as JArray;
                    JArray roots = constraint["smooth_root_2d"] as JArray;
                    if (headings == null || headings.Count != frameIndices.Count)
                    {
                        headings = BuildSampledRoot2DHeadings(
                            motion,
                            modelName,
                            frameIndices,
                            sourceOutputStartFrame,
                            tailStartFrame,
                            sourceStartFrame);
                    }
                    for (int i = 0; i < frameIndices.Count; i++)
                    {
                        if (frameIndices[i].Type != JTokenType.Integer ||
                            frameIndices[i].Value<int>() != endpointFrame)
                        {
                            continue;
                        }
                        headings[i] = protocolHeading.DeepClone();
                        if (endpointIndex == 1 && roots != null && i < roots.Count)
                        {
                            Vector3 protocolRoot = ToProtocolPosition(endpointSamples[endpointIndex].rootPosition);
                            roots[i] = new JArray(protocolRoot.x, protocolRoot.z);
                        }
                        found = true;
                    }
                    constraint["global_root_heading"] = headings;
                }

                if (!found)
                {
                    missingFrames.Add(endpointFrame);
                    missingPositions.Add(endpointSamples[endpointIndex].rootPosition);
                    missingHeadings.Add(endpointHeadings[endpointIndex]);
                }
            }

            if (missingFrames.Count > 0)
            {
                constraints.Add(BuildRoot2DConstraint(missingFrames, missingPositions, missingHeadings));
            }
            return constraints.ToString(Formatting.None);
        }

        internal static bool TryReadRoot2DAnchorJson(
            string constraintsJson,
            int frame,
            out Vector3 position,
            out Quaternion heading)
        {
            position = Vector3.zero;
            heading = Quaternion.identity;
            if (string.IsNullOrWhiteSpace(constraintsJson)) return false;
            JToken parsed = JToken.Parse(constraintsJson);
            if (parsed is not JArray constraints) return false;
            foreach (JObject constraint in constraints.Children<JObject>())
            {
                if (!string.Equals(constraint.Value<string>("type"), "root2d", StringComparison.OrdinalIgnoreCase) ||
                    constraint["frame_indices"] is not JArray frames ||
                    constraint["smooth_root_2d"] is not JArray roots)
                {
                    continue;
                }
                JArray headings = constraint["global_root_heading"] as JArray;
                for (int i = 0; i < frames.Count && i < roots.Count; i++)
                {
                    if (frames[i].Type != JTokenType.Integer || frames[i].Value<int>() != frame ||
                        roots[i] is not JArray root || root.Count < 2)
                    {
                        continue;
                    }
                    position = new Vector3(-root[0].Value<float>(), 0f, root[1].Value<float>());
                    if (headings != null && i < headings.Count && headings[i] is JArray protocolHeading &&
                        protocolHeading.Count >= 2)
                    {
                        float cosine = protocolHeading[0].Value<float>();
                        float sine = protocolHeading[1].Value<float>();
                        heading = Quaternion.AngleAxis(
                            Mathf.Atan2(-sine, cosine) * Mathf.Rad2Deg,
                            Vector3.up);
                    }
                    return true;
                }
            }
            return false;
        }

        internal static string RemoveRoot2DFrameJson(string constraintsJson, int frame)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson)) return string.Empty;
            JToken parsed = JToken.Parse(constraintsJson);
            if (parsed is not JArray constraints) return parsed.ToString(Formatting.None);
            var output = new JArray();
            foreach (JObject constraint in constraints.Children<JObject>())
            {
                if (!string.Equals(constraint.Value<string>("type"), "root2d", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add(constraint);
                    continue;
                }
                if (constraint["frame_indices"] is not JArray frames)
                {
                    output.Add(constraint);
                    continue;
                }
                var keep = new List<int>(frames.Count);
                for (int i = 0; i < frames.Count; i++)
                {
                    if (frames[i].Type == JTokenType.Integer && frames[i].Value<int>() == frame) continue;
                    keep.Add(i);
                }
                if (keep.Count == 0) continue;
                string[] alignedProperties =
                {
                    "frame_indices", "smooth_root_2d", "global_root_heading",
                    "root_positions", "target_positions", "local_joints_rot"
                };
                foreach (string propertyName in alignedProperties)
                {
                    if (constraint[propertyName] is JArray aligned && aligned.Count == frames.Count)
                    {
                        var filtered = new JArray();
                        foreach (int index in keep) filtered.Add(aligned[index].DeepClone());
                        constraint[propertyName] = filtered;
                    }
                }
                output.Add(constraint);
            }
            return output.ToString(Formatting.None);
        }

        private static void ResolveLoopWindowTargetAnchor(
            KimodoRawMotionData motion,
            string modelName,
            KimodoConstraintInternalData sourceAnchor,
            Vector3? targetAnchorPositionOverride,
            Quaternion? targetAnchorHeadingOverride,
            out Vector3 targetPosition,
            out Quaternion targetHeading)
        {
            targetPosition = sourceAnchor?.rootPosition ?? Vector3.zero;
            targetHeading = sourceAnchor == null ? Quaternion.identity : ResolveRootRotation(sourceAnchor);
            if (motion != null && motion.FrameCount > 0 &&
                TryBuildFullBodyFrame(
                    motion,
                    modelName,
                    motion.FrameCount - 1,
                    out KimodoConstraintInternalData terminal,
                    out _))
            {
                targetPosition = terminal.rootPosition;
                targetHeading = ResolveRootRotation(terminal);
            }
            if (targetAnchorPositionOverride.HasValue)
            {
                targetPosition.x = targetAnchorPositionOverride.Value.x;
                targetPosition.z = targetAnchorPositionOverride.Value.z;
            }
            if (targetAnchorHeadingOverride.HasValue)
                targetHeading = targetAnchorHeadingOverride.Value;
        }

        private static void ApplyLoopAnchorOffset(
            KimodoConstraintInternalData sourceAnchor,
            List<KimodoConstraintInternalData> tailSamples,
            Vector3 targetAnchorPosition,
            Quaternion targetAnchorHeading,
            bool lockPositionX,
            bool lockPositionY,
            bool lockPositionZ,
            bool lockRotationX,
            bool lockRotationY,
            bool lockRotationZ)
        {
            if (sourceAnchor == null || tailSamples == null || tailSamples.Count == 0) return;
            Quaternion sourceHeading = ResolveRootRotation(sourceAnchor);
            Quaternion sourceYaw = KimodoMotionMath.ResolvePlanarHeading(sourceHeading);
            Quaternion targetYaw = lockRotationY
                ? sourceYaw
                : KimodoMotionMath.ResolvePlanarHeading(targetAnchorHeading);
            Quaternion planarOffset = targetYaw * Quaternion.Inverse(sourceYaw);
            Vector3 selectedTarget = targetAnchorPosition;
            if (lockPositionX) selectedTarget.x = sourceAnchor.rootPosition.x;
            if (lockPositionZ) selectedTarget.z = sourceAnchor.rootPosition.z;
            Vector3 sourcePlanar = sourceAnchor.rootPosition;
            sourcePlanar.y = 0f;
            Vector3 targetPlanar = selectedTarget;
            targetPlanar.y = 0f;
            Vector3 translation = targetPlanar - planarOffset * sourcePlanar;
            float verticalOffset = lockPositionY
                ? 0f
                : targetAnchorPosition.y - sourceAnchor.rootPosition.y;
            foreach (KimodoConstraintInternalData tail in tailSamples)
            {
                if (tail == null) continue;
                float verticalPosition = tail.rootPosition.y + verticalOffset;
                tail.rootPosition = planarOffset * tail.rootPosition + translation;
                tail.rootPosition.y = verticalPosition;
                if (tail.localJointAxisAngles == null || tail.localJointAxisAngles.Count == 0) continue;
                Quaternion rootRotation = planarOffset * ResolveRootRotation(tail);
                tail.localJointAxisAngles[0] = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(rootRotation);
            }
            ApplyLoopAxisLocks(
                sourceAnchor,
                tailSamples[tailSamples.Count - 1],
                lockPositionX,
                lockPositionY,
                lockPositionZ,
                lockRotationX,
                lockRotationY,
                lockRotationZ);
        }

        private static void ApplyLoopAxisLocks(
            KimodoConstraintInternalData start,
            KimodoConstraintInternalData tail,
            bool lockPositionX,
            bool lockPositionY,
            bool lockPositionZ,
            bool lockRotationX,
            bool lockRotationY,
            bool lockRotationZ)
        {
            if (start == null || tail == null) return;
            if (lockPositionX) tail.rootPosition.x = start.rootPosition.x;
            if (lockPositionY) tail.rootPosition.y = start.rootPosition.y;
            if (lockPositionZ) tail.rootPosition.z = start.rootPosition.z;
            if ((!lockRotationX && !lockRotationY && !lockRotationZ) ||
                tail.localJointAxisAngles == null || tail.localJointAxisAngles.Count == 0 ||
                start.localJointAxisAngles == null || start.localJointAxisAngles.Count == 0)
            {
                return;
            }

            Quaternion startRotation = ResolveRootRotation(start);
            Quaternion tailRotation = ResolveRootRotation(tail);
            float startYaw = KimodoMotionMath.ResolvePlanarYawDegrees(startRotation);
            float tailYaw = KimodoMotionMath.ResolvePlanarYawDegrees(tailRotation);
            Quaternion startTilt = Quaternion.Inverse(Quaternion.Euler(0f, startYaw, 0f)) * startRotation;
            Quaternion tailTilt = Quaternion.Inverse(Quaternion.Euler(0f, tailYaw, 0f)) * tailRotation;
            Vector3 startTiltEuler = ToSignedEuler(startTilt);
            Vector3 tailTiltEuler = ToSignedEuler(tailTilt);
            Quaternion terminalTilt = Quaternion.Euler(
                lockRotationX ? startTiltEuler.x : tailTiltEuler.x,
                0f,
                lockRotationZ ? startTiltEuler.z : tailTiltEuler.z);
            Quaternion terminalYaw = Quaternion.Euler(0f, lockRotationY ? startYaw : tailYaw, 0f);
            tail.localJointAxisAngles[0] = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(
                terminalYaw * terminalTilt);
        }

        private static JArray BuildSampledRoot2DHeadings(
            KimodoRawMotionData motion,
            string modelName,
            JArray outputFrames,
            int sourceOutputStartFrame,
            int tailStartFrame,
            int sourceStartFrame)
        {
            var headings = new JArray();
            for (int i = 0; i < outputFrames.Count; i++)
            {
                int outputFrame = outputFrames[i].Type == JTokenType.Integer
                    ? outputFrames[i].Value<int>()
                    : sourceOutputStartFrame;
                int sourceFrame = outputFrame >= tailStartFrame
                    ? sourceStartFrame + outputFrame - tailStartFrame
                    : outputFrame - sourceOutputStartFrame;
                sourceFrame = Mathf.Clamp(sourceFrame, 0, motion.FrameCount - 1);
                if (!TryBuildFullBodyFrame(
                        motion,
                        modelName,
                        sourceFrame,
                        out KimodoConstraintInternalData sample,
                        out string error))
                {
                    throw new InvalidOperationException(
                        $"Loop Root2D heading sampling failed at source frame {sourceFrame}: {error}");
                }
                headings.Add(BuildProtocolHeading(ResolveRootRotation(sample)));
            }
            return headings;
        }

        private static bool TryEstimatePlanarVelocity(
            KimodoRawMotionData motion,
            out Vector3 velocity,
            out string error)
        {
            velocity = Vector3.zero;
            error = string.Empty;
            if (motion == null || motion.FrameCount < 2 || motion.FrameRate <= 0f)
            {
                error = "Raw motion does not contain enough timed frames.";
                return false;
            }

            double meanFrame = (motion.FrameCount - 1) * 0.5;
            double denominator = 0.0;
            double numeratorX = 0.0;
            double numeratorZ = 0.0;
            for (int frame = 0; frame < motion.FrameCount; frame++)
            {
                if (!motion.TryReadUnityRootPosition(frame, out Vector3 position))
                {
                    error = $"Raw motion root position is invalid at frame {frame}.";
                    return false;
                }

                double centeredFrame = frame - meanFrame;
                denominator += centeredFrame * centeredFrame;
                numeratorX += position.x * centeredFrame;
                numeratorZ += position.z * centeredFrame;
            }

            if (denominator <= 1e-9)
            {
                error = "Raw motion frame times have no span.";
                return false;
            }

            velocity = new Vector3(
                (float)(numeratorX / denominator * motion.FrameRate),
                0f,
                (float)(numeratorZ / denominator * motion.FrameRate));
            return true;
        }

        internal static int ResolveLoopAnchorFrameForGeneration(KimodoRawMotionData motion)
        {
            // Temporary policy: Stage 3 always anchors from the first frame.
            // Keep the contact-exchange selector below so the later policy can
            // be restored without losing the validated detection logic.
            return 0;
        }

        internal static int ResolveLoopAnchorFrameFromFootContactExchange(KimodoRawMotionData motion)
        {
            if (motion == null || !motion.HasFootContacts || motion.FrameCount < 2)
            {
                return 0;
            }

            for (int liftFrame = 1; liftFrame < motion.FrameCount - 1; liftFrame++)
            {
                if (!TryReadFootState(
                        motion,
                        liftFrame,
                        out bool leftFullyLifted,
                        out _,
                        out bool rightFullyLifted,
                        out _))
                {
                    return 0;
                }

                if (!TryReadFootState(
                        motion,
                        liftFrame - 1,
                        out bool previousLeftFullyLifted,
                        out _,
                        out bool previousRightFullyLifted,
                        out _))
                {
                    return 0;
                }

                bool leftLiftedThisFrame = leftFullyLifted && !previousLeftFullyLifted;
                bool rightLiftedThisFrame = rightFullyLifted && !previousRightFullyLifted;
                if (leftLiftedThisFrame && TryFindFollowingLandingFrame(
                        motion,
                        liftFrame,
                        isLeftFoot: false,
                        out int rightLandingFrame))
                {
                    return liftFrame + (rightLandingFrame - liftFrame) / 2;
                }

                if (rightLiftedThisFrame && TryFindFollowingLandingFrame(
                        motion,
                        liftFrame,
                        isLeftFoot: true,
                        out int leftLandingFrame))
                {
                    return liftFrame + (leftLandingFrame - liftFrame) / 2;
                }
            }

            return 0;
        }

        private static bool TryFindFollowingLandingFrame(
            KimodoRawMotionData motion,
            int liftFrame,
            bool isLeftFoot,
            out int landingFrame)
        {
            landingFrame = -1;
            if (!TryReadFootState(
                    motion,
                    liftFrame,
                    out _,
                    out bool initialLeftFullyLanded,
                    out _,
                    out bool initialRightFullyLanded))
            {
                return false;
            }
            bool wasFullyLanded = isLeftFoot
                ? initialLeftFullyLanded
                : initialRightFullyLanded;

            for (int frame = liftFrame + 1; frame < motion.FrameCount; frame++)
            {
                if (!TryReadFootState(
                        motion,
                        frame,
                        out _,
                        out bool leftFullyLanded,
                        out _,
                        out bool rightFullyLanded))
                {
                    return false;
                }

                bool oppositeFootLanded = isLeftFoot ? leftFullyLanded : rightFullyLanded;
                if (oppositeFootLanded && !wasFullyLanded)
                {
                    landingFrame = frame;
                    return true;
                }
                wasFullyLanded = oppositeFootLanded;
            }

            return false;
        }

        private static bool TryReadFootState(
            KimodoRawMotionData motion,
            int frame,
            out bool leftFullyLifted,
            out bool leftFullyLanded,
            out bool rightFullyLifted,
            out bool rightFullyLanded)
        {
            leftFullyLifted = leftFullyLanded = rightFullyLifted = rightFullyLanded = false;
            if (motion == null || !motion.HasFootContacts || frame < 0 || frame >= motion.FrameCount ||
                !motion.TryReadFootContact(frame, 0, out float leftHeel) ||
                !motion.TryReadFootContact(frame, 1, out float leftToe) ||
                !motion.TryReadFootContact(frame, 2, out float rightHeel) ||
                !motion.TryReadFootContact(frame, 3, out float rightToe))
            {
                return false;
            }

            leftFullyLifted = leftHeel == 0f && leftToe == 0f;
            leftFullyLanded = leftHeel == 1f && leftToe == 1f;
            rightFullyLifted = rightHeel == 0f && rightToe == 0f;
            rightFullyLanded = rightHeel == 1f && rightToe == 1f;
            return true;
        }

        private static Vector3 ToSignedEuler(Quaternion rotation)
        {
            return KimodoMotionMath.SignedEulerDegrees(rotation);
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
            int regularFrameInterval = 0,
            float distanceOverride = -1f,
            Vector3? rootOriginOverride = null)
        {
            if (motion == null || motion.FrameCount != targetFrameCount ||
                runtimeTrimStartFrame < 0 || targetFrameCount <= 1 || runtimeFrameCount <= 0 ||
                runtimeTrimStartFrame > runtimeFrameCount - targetFrameCount || frameRate <= 0f ||
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
                pathLength += KimodoMotionMath.PlanarDistance(previousRoot, root);
                previousRoot = root;
            }
            if (distanceOverride >= 0f) pathLength = distanceOverride;

            int terminalFrame = runtimeTrimStartFrame + targetFrameCount - 1;
            var frames = new HashSet<int> { runtimeTrimStartFrame, terminalFrame };
            if (runtimeTrimStartFrame > 0) frames.Add(0);
            if (terminalFrame < runtimeFrameCount - 1) frames.Add(runtimeFrameCount - 1);
            AddRegularFrames(frames, runtimeFrameCount, regularFrameInterval);
            CollectRootChannelState(existingConstraintsJson, runtimeFrameCount, frames, null);

            Quaternion startHeading = Quaternion.Euler(0f, pathBeginAngleDegrees, 0f);
            Vector3 pathOrigin = rootOriginOverride ?? first.rootPosition;
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
                Vector3 profilePosition = pathOrigin + profileOffset;
                profilePosition.y = pathOrigin.y;
                positions.Add(profilePosition);
                headings.Add(EvaluateRawRootRotation(motion, modelName, t));
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

        internal static string OverrideEffectiveConstraintHeadingsJson(
            string constraintsJson,
            float headingDegrees)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson) ||
                float.IsNaN(headingDegrees) || float.IsInfinity(headingDegrees))
                throw new InvalidOperationException("Constraint heading override is invalid.");

            JArray constraints = JArray.Parse(constraintsJson);
            JArray protocolHeading = BuildProtocolHeading(Quaternion.Euler(0f, headingDegrees, 0f));
            foreach (JObject constraint in constraints.Children<JObject>())
            {
                string type = constraint.Value<string>("type")?.Trim().ToLowerInvariant();
                if (type == "root2d" && constraint["frame_indices"] is JArray rootFrames)
                {
                    var headings = new JArray();
                    for (int i = 0; i < rootFrames.Count; i++)
                        headings.Add(protocolHeading.DeepClone());
                    constraint["global_root_heading"] = headings;
                }

                bool hasBodyRoot = type == "fullbody" || type == "effector" ||
                    type == "end-effector" || type == "left-hand" ||
                    type == "right-hand" || type == "left-foot" || type == "right-foot";
                if (!hasBodyRoot ||
                    constraint["local_joints_rot"] is not JArray localJoints)
                    continue;

                for (int frame = 0; frame < localJoints.Count; frame++)
                {
                    if (localJoints[frame] is not JArray joints ||
                        joints.Count == 0 || joints[0] is not JArray rootAxisAngle ||
                        rootAxisAngle.Count < 3)
                        continue;
                    Vector3 protocolAxisAngle = new Vector3(
                        rootAxisAngle[0].Value<float>(),
                        rootAxisAngle[1].Value<float>(),
                        rootAxisAngle[2].Value<float>());
                    Quaternion protocolRotation =
                        KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(protocolAxisAngle);
                    Quaternion unityRotation = new Quaternion(
                        protocolRotation.x, -protocolRotation.y, -protocolRotation.z, protocolRotation.w);
                    float currentYaw = KimodoMotionMath.ResolvePlanarYawDegrees(unityRotation);
                    Quaternion tilt = Quaternion.Inverse(Quaternion.Euler(0f, currentYaw, 0f)) * unityRotation;
                    Quaternion overriddenUnity = Quaternion.Euler(0f, headingDegrees, 0f) * tilt;
                    Quaternion overriddenProtocol = new Quaternion(
                        overriddenUnity.x, -overriddenUnity.y, -overriddenUnity.z, overriddenUnity.w);
                    Vector3 overriddenAxisAngle =
                        KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(overriddenProtocol);
                    joints[0] = new JArray(
                        overriddenAxisAngle.x, overriddenAxisAngle.y, overriddenAxisAngle.z);
                }
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
                runtimeTrimStartFrame < 0 || targetFrameCount <= 1 || runtimeFrameCount <= 0 ||
                runtimeTrimStartFrame > runtimeFrameCount - targetFrameCount || frameRate <= 0f ||
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

        /// <summary>
        /// Re-samples root values on FullBody and end-effector records from the
        /// motion produced by Stage 2.  The body pose and effector targets stay
        /// untouched; only the root channels follow the resolved trajectory.
        /// </summary>
        internal static string InterpolateFullBodyRootPositionsJson(
            string constraintsJson,
            KimodoRawMotionData motion,
            int runtimeTrimStartFrame)
        {
            if (string.IsNullOrWhiteSpace(constraintsJson) || motion == null || motion.FrameCount <= 0)
                return constraintsJson ?? string.Empty;

            JArray constraints = JArray.Parse(constraintsJson);
            foreach (JObject constraint in constraints.Children<JObject>())
            {
                string type = constraint.Value<string>("type")?.Trim().ToLowerInvariant();
                bool isFullBody = string.Equals(type, "fullbody", StringComparison.OrdinalIgnoreCase);
                bool isEffector = type == "effector" ||
                                  string.Equals(type, "end-effector", StringComparison.OrdinalIgnoreCase) ||
                                  type == "left-hand" || type == "right-hand" ||
                                  type == "left-foot" || type == "right-foot";
                if ((!isFullBody && !isEffector) ||
                    constraint["frame_indices"] is not JArray frames ||
                    constraint["root_positions"] is not JArray roots)
                {
                    continue;
                }

                JArray smoothRoots = constraint["smooth_root_2d"] as JArray;
                for (int i = 0; i < frames.Count && i < roots.Count; i++)
                {
                    if (frames[i].Type != JTokenType.Integer)
                        continue;
                    int sourceFrame = Mathf.Clamp(
                        frames[i].Value<int>() - Mathf.Max(0, runtimeTrimStartFrame),
                        0,
                        motion.FrameCount - 1);
                    if (!motion.TryReadUnityRootPosition(sourceFrame, out Vector3 root))
                        continue;

                    Vector3 protocolRoot = ToProtocolPosition(root);
                    roots[i] = new JArray(protocolRoot.x, protocolRoot.y, protocolRoot.z);
                    if (smoothRoots != null && i < smoothRoots.Count)
                        smoothRoots[i] = new JArray(protocolRoot.x, protocolRoot.z);
                }
            }
            return constraints.ToString(Formatting.None);
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

        private static Quaternion EvaluateRawRootRotation(
            KimodoRawMotionData motion,
            string modelName,
            float t)
        {
            float frame = Mathf.Clamp01(t) * (motion.FrameCount - 1);
            int firstFrame = Mathf.Clamp(Mathf.FloorToInt(frame), 0, motion.FrameCount - 1);
            int secondFrame = Mathf.Min(firstFrame + 1, motion.FrameCount - 1);
            if (!TryBuildFullBodyFrame(
                    motion,
                    modelName,
                    firstFrame,
                    out KimodoConstraintInternalData first,
                    out string firstError))
            {
                throw new InvalidOperationException(
                    $"Path angle pass could not sample the baseline root heading at frame {firstFrame}: {firstError}");
            }
            if (firstFrame == secondFrame) return ResolveRootRotation(first);
            if (!TryBuildFullBodyFrame(
                    motion,
                    modelName,
                    secondFrame,
                    out KimodoConstraintInternalData second,
                    out string secondError))
            {
                throw new InvalidOperationException(
                    $"Path angle pass could not sample the baseline root heading at frame {secondFrame}: {secondError}");
            }
            float blend = frame - firstFrame;
            Vector2 heading = KimodoMotionMath.InterpolatePlanarHeading(
                KimodoMotionMath.ToPlanarHeadingVector(ResolveRootRotation(first)),
                KimodoMotionMath.ToPlanarHeadingVector(ResolveRootRotation(second)),
                blend);
            return Quaternion.LookRotation(new Vector3(heading.x, 0f, heading.y), Vector3.up);
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
            position = KimodoBezierUtility.EvaluateCubic(p0, p0 + d0 * handle, p3 - d1 * handle, p3, u);
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
            Vector3 forward = KimodoMotionMath.ResolvePlanarHeading(heading) * Vector3.forward;
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

        private static Vector3 ToProtocolAxisAngle(Vector3 unityAxisAngle)
        {
            Quaternion unity = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(unityAxisAngle);
            Quaternion protocol = new Quaternion(unity.x, -unity.y, -unity.z, unity.w);
            return KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(protocol);
        }
    }
}
