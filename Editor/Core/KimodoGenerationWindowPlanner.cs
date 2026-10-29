using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal enum KimodoRawConstraintKind
    {
        Root2D,
        FullBody,
        Effector
    }

    internal sealed class KimodoGlobalRawConstraint
    {
        public int Frame;
        public KimodoRawConstraintKind Kind;
        public JObject Payload;

        // Root state extracted from the canonical constraint before JSON export.
        public Vector2 RootPosition;
        public Vector2 Heading = Vector2.up;
    }

    internal sealed class KimodoWindowConstraint
    {
        public int LocalFrame;
        public KimodoRawConstraintKind Kind;
        public JObject Payload;
    }

    internal sealed class KimodoGenerationWindow
    {
        // Core output ownership interval, half-open and in global model frames.
        public int CoreStartFrame;
        public int CoreEndFrame;

        // Generation request interval, expanded symmetrically for overlap.
        public int RequestStartFrame;
        public int RequestEndFrame;

        public int CoreFrameCount => CoreEndFrame - CoreStartFrame;
        public int RequestFrameCount => RequestEndFrame - RequestStartFrame;
    }

    internal static class KimodoGenerationWindowPlanner
    {
        internal static List<KimodoGlobalRawConstraint> ReadRootAnchors(string constraintsJson)
        {
            var anchors = new List<KimodoGlobalRawConstraint>();
            if (string.IsNullOrWhiteSpace(constraintsJson))
                return anchors;

            foreach (JObject constraint in JArray.Parse(constraintsJson).Children<JObject>())
            {
                if (!(constraint["frame_indices"] is JArray frames))
                    continue;

                JArray positions = constraint["smooth_root_2d"] as JArray ??
                    constraint["root_positions"] as JArray;
                JArray headings = constraint["global_root_heading"] as JArray;
                JArray jointRotations = constraint["local_joints_rot"] as JArray;
                for (int index = 0; index < frames.Count; index++)
                {
                    if (frames[index].Type != JTokenType.Integer || positions == null || index >= positions.Count ||
                        !(positions[index] is JArray position) || position.Count < 2)
                        continue;

                    int frame = frames[index].Value<int>();
                    Vector2 rootPosition;
                    if (constraint.Value<string>("type") == "root2d")
                    {
                        rootPosition = new Vector2(position[0].Value<float>(), position[1].Value<float>());
                    }
                    else
                    {
                        if (position.Count < 3)
                            continue;
                        rootPosition = new Vector2(position[0].Value<float>(), position[2].Value<float>());
                    }

                    Vector2 heading = Vector2.up;
                    if (headings != null && index < headings.Count && headings[index] is JArray headingData && headingData.Count >= 2)
                    {
                        heading = new Vector2(headingData[0].Value<float>(), headingData[1].Value<float>());
                    }
                    else if (jointRotations != null && index < jointRotations.Count &&
                        jointRotations[index] is JArray frameRotations && frameRotations.Count > 0 &&
                        frameRotations[0] is JArray rootRotation && rootRotation.Count >= 3)
                    {
                        Quaternion protocolRotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(
                            new Vector3(rootRotation[0].Value<float>(), rootRotation[1].Value<float>(), rootRotation[2].Value<float>()));
                        Quaternion unityRotation = new Quaternion(
                            protocolRotation.x,
                            -protocolRotation.y,
                            -protocolRotation.z,
                            protocolRotation.w);
                        Vector3 forward = unityRotation * Vector3.forward;
                        heading = new Vector2(forward.z, -forward.x);
                    }

                    anchors.Add(new KimodoGlobalRawConstraint
                    {
                        Frame = frame,
                        Kind = string.Equals(constraint.Value<string>("type"), "root2d", StringComparison.OrdinalIgnoreCase)
                            ? KimodoRawConstraintKind.Root2D
                            : string.Equals(constraint.Value<string>("type"), "fullbody", StringComparison.OrdinalIgnoreCase)
                                ? KimodoRawConstraintKind.FullBody
                                : KimodoRawConstraintKind.Effector,
                        RootPosition = rootPosition,
                        Heading = heading,
                        Payload = (JObject)constraint.DeepClone()
                    });
                }
            }

            return anchors.OrderBy(item => item.Frame).ToList();
        }

        internal static List<KimodoGenerationWindow> Plan(
            int totalFrames,
            float frameRate,
            int maxRequestFrames,
            int overlapFrames,
            int minimumCoreFrames,
            int maximumCoreFrames,
            int preferredCoreFrames,
            IReadOnlyList<KimodoGlobalRawConstraint> constraints,
            int constraintGuardFrames)
        {
            if (totalFrames <= 0 || frameRate <= 0f || maxRequestFrames <= 0 ||
                overlapFrames < 0 ||
                minimumCoreFrames <= 0 || maximumCoreFrames < minimumCoreFrames ||
                preferredCoreFrames <= 0 || constraintGuardFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(totalFrames), "Generation window settings are invalid.");
            }

            if (totalFrames <= maxRequestFrames)
            {
                return new List<KimodoGenerationWindow>
                {
                    CreateWindow(0, totalFrames, totalFrames, 0)
                };
            }

            int maxCoreWithOverlap = maxRequestFrames - overlapFrames;
            int maxCore = Math.Min(maximumCoreFrames, maxCoreWithOverlap);
            if (maxCore < minimumCoreFrames)
            {
                throw new InvalidOperationException("Overlap leaves no valid core window within the request limit.");
            }

            // Dynamic programming over candidate core boundaries. The objective first
            // avoids unnecessary cuts, then favors the preferred duration and cuts far
            // from authored constraints. Exact and guarded constraint frames are blocked.
            var costs = Enumerable.Repeat(double.PositiveInfinity, totalFrames + 1).ToArray();
            var previous = Enumerable.Repeat(-1, totalFrames + 1).ToArray();
            costs[0] = 0d;

            for (int end = 1; end <= totalFrames; end++)
            {
                int minStart = Math.Max(0, end - maxCore);
                int maxStart = end - minimumCoreFrames;
                for (int start = minStart; start <= maxStart; start++)
                {
                    if (double.IsPositiveInfinity(costs[start]))
                        continue;

                    bool isCut = end < totalFrames;
                    if (isCut && IsNearConstraint(end, constraints, constraintGuardFrames))
                        continue;

                    int length = end - start;
                    double durationDelta = (length - preferredCoreFrames) / (double)preferredCoreFrames;
                    double durationCost = durationDelta * durationDelta;
                    double cutCost = isCut ? ConstraintDistanceCost(end, constraints, frameRate) : 0d;
                    double candidate = costs[start] + 100d + durationCost + cutCost;
                    if (candidate < costs[end])
                    {
                        costs[end] = candidate;
                        previous[end] = start;
                    }
                }
            }

            if (previous[totalFrames] < 0)
                throw new InvalidOperationException("No valid generation window plan satisfies the duration and constraint guards.");

            var boundaries = new List<int> { totalFrames };
            int cursor = totalFrames;
            while (cursor > 0)
            {
                cursor = previous[cursor];
                if (cursor < 0)
                    throw new InvalidOperationException("Generation window plan is incomplete.");
                boundaries.Add(cursor);
            }
            boundaries.Reverse();

            var windows = new List<KimodoGenerationWindow>(boundaries.Count - 1);
            for (int i = 0; i + 1 < boundaries.Count; i++)
            {
                windows.Add(CreateWindow(
                    boundaries[i],
                    boundaries[i + 1],
                    totalFrames,
                    overlapFrames));
            }
            return windows;
        }

        internal static List<KimodoWindowConstraint> MapConstraints(
            KimodoGenerationWindow window,
            IEnumerable<KimodoGlobalRawConstraint> constraints)
        {
            if (window == null)
                throw new ArgumentNullException(nameof(window));

            return (constraints ?? Enumerable.Empty<KimodoGlobalRawConstraint>())
                .Where(item => item != null &&
                    item.Frame >= window.RequestStartFrame &&
                    item.Frame < window.RequestEndFrame)
                .OrderBy(item => item.Frame)
                .Select(item => new KimodoWindowConstraint
                {
                    LocalFrame = item.Frame - window.RequestStartFrame,
                    Kind = item.Kind,
                    Payload = item.Payload?.DeepClone() as JObject
                })
                .ToList();
        }

        internal static bool TryGetOverlapBoundaryFrames(
            KimodoGenerationWindow first,
            KimodoGenerationWindow second,
            out int startFrame,
            out int endFrame)
        {
            startFrame = -1;
            endFrame = -1;
            if (first == null || second == null)
                return false;

            startFrame = Math.Max(first.RequestStartFrame, second.RequestStartFrame);
            int endFrameExclusive = Math.Min(first.RequestEndFrame, second.RequestEndFrame);
            // Window ranges are half-open; constrain the final sampled frame inside the overlap.
            endFrame = endFrameExclusive - 1;
            return endFrame > startFrame;
        }

        internal static bool TryInterpolateRoot2DForOverlap(
            IReadOnlyList<KimodoGlobalRawConstraint> anchors,
            int startFrame,
            int endFrame,
            out KimodoGlobalRawConstraint startRoot,
            out KimodoGlobalRawConstraint endRoot)
        {
            startRoot = null;
            endRoot = null;
            if (anchors == null || endFrame <= startFrame)
                return false;

            List<KimodoGlobalRawConstraint> ordered = anchors
                .Where(anchor => anchor != null)
                .OrderBy(anchor => anchor.Frame)
                .ToList();
            startRoot = TryInterpolateRootAtFrame(ordered, startFrame);
            endRoot = TryInterpolateRootAtFrame(ordered, endFrame);
            bool startConstrained = startRoot != null || ordered.Any(anchor => anchor.Frame == startFrame);
            bool endConstrained = endRoot != null || ordered.Any(anchor => anchor.Frame == endFrame);
            return startConstrained && endConstrained;
        }

        private static KimodoGlobalRawConstraint TryInterpolateRootAtFrame(
            IReadOnlyList<KimodoGlobalRawConstraint> ordered,
            int frame)
        {
            if (ordered.Any(anchor => anchor.Frame == frame))
                return null;
            KimodoGlobalRawConstraint before = ordered.LastOrDefault(anchor => anchor.Frame < frame);
            KimodoGlobalRawConstraint after = ordered.FirstOrDefault(anchor => anchor.Frame > frame);
            return before != null && after != null
                ? InterpolateRoot2D(before, after, frame)
                : null;
        }

        internal static string BuildWindowConstraintsJson(
            KimodoGenerationWindow window,
            string constraintsJson)
        {
            if (window == null)
                throw new ArgumentNullException(nameof(window));
            if (string.IsNullOrWhiteSpace(constraintsJson))
                return string.Empty;

            JArray source = JArray.Parse(constraintsJson);
            var result = new JArray();
            foreach (JObject constraint in source.Children<JObject>())
            {
                if (constraint["frame_indices"] is not JArray frames)
                    throw new InvalidOperationException("A generation constraint is missing frame_indices.");

                var selectedIndices = Enumerable.Range(0, frames.Count)
                    .Where(index => frames[index].Type == JTokenType.Integer &&
                        frames[index].Value<int>() >= window.RequestStartFrame &&
                        frames[index].Value<int>() < window.RequestEndFrame)
                    .ToArray();
                if (selectedIndices.Length == 0)
                    continue;

                var mapped = (JObject)constraint.DeepClone();
                mapped["frame_indices"] = new JArray(selectedIndices.Select(index =>
                    frames[index].Value<int>() - window.RequestStartFrame));

                string[] frameAlignedProperties =
                {
                    "smooth_root_2d",
                    "global_root_heading",
                    "local_joints_rot",
                    "root_positions",
                    "target_positions"
                };
                foreach (JProperty property in constraint.Properties())
                {
                    if (property.Name == "frame_indices" ||
                        !frameAlignedProperties.Contains(property.Name, StringComparer.Ordinal) ||
                        !(property.Value is JArray values))
                        continue;

                    if (values.Count != frames.Count)
                        throw new InvalidOperationException(
                            $"Constraint property '{property.Name}' does not match frame_indices.");
                    mapped[property.Name] = new JArray(selectedIndices.Select(index => values[index].DeepClone()));
                }

                result.Add(mapped);
            }

            return result.ToString(Formatting.None);
        }

        internal static JObject CreateRoot2DConstraint(
            int localFrame,
            Vector2 position,
            Vector2 heading)
        {
            Vector2 normalizedHeading = heading.sqrMagnitude > 1e-8f ? heading.normalized : Vector2.up;
            return new JObject
            {
                ["type"] = "root2d",
                ["frame_indices"] = new JArray(localFrame),
                ["smooth_root_2d"] = new JArray { new JArray(position.x, position.y) },
                ["global_root_heading"] = new JArray { new JArray(normalizedHeading.x, normalizedHeading.y) }
            };
        }

        internal static KimodoGlobalRawConstraint InterpolateRoot2D(
            KimodoGlobalRawConstraint from,
            KimodoGlobalRawConstraint to,
            int frame)
        {
            if (from == null || to == null || to.Frame <= from.Frame ||
                frame <= from.Frame || frame >= to.Frame)
            {
                throw new ArgumentOutOfRangeException(nameof(frame), "Interpolation frame must lie between two ordered anchors.");
            }

            float t = (frame - from.Frame) / (float)(to.Frame - from.Frame);
            Vector2 position = Vector2.Lerp(from.RootPosition, to.RootPosition, t);
            Vector2 heading = KimodoMotionMath.InterpolatePlanarHeading(from.Heading, to.Heading, t);

            return new KimodoGlobalRawConstraint
            {
                Frame = frame,
                Kind = KimodoRawConstraintKind.Root2D,
                RootPosition = position,
                Heading = heading,
                Payload = new JObject
                {
                    ["root2d"] = new JObject
                    {
                        ["position"] = new JArray(position.x, position.y),
                        ["heading"] = new JArray(heading.x, heading.y)
                    }
                }
            };
        }

        private static KimodoGenerationWindow CreateWindow(
            int coreStart,
            int coreEnd,
            int totalFrames,
            int overlapFrames)
        {
            int leftPadding = coreStart == 0 ? 0 : (overlapFrames + 1) / 2;
            int rightPadding = coreEnd == totalFrames ? 0 : overlapFrames / 2;
            return new KimodoGenerationWindow
            {
                CoreStartFrame = coreStart,
                CoreEndFrame = coreEnd,
                RequestStartFrame = Math.Max(0, coreStart - leftPadding),
                RequestEndFrame = Math.Min(totalFrames, coreEnd + rightPadding)
            };
        }

        private static bool IsNearConstraint(
            int frame,
            IReadOnlyList<KimodoGlobalRawConstraint> constraints,
            int guardFrames)
        {
            return constraints != null && constraints.Any(item =>
                item != null && Math.Abs(item.Frame - frame) <= guardFrames);
        }

        private static double ConstraintDistanceCost(
            int frame,
            IReadOnlyList<KimodoGlobalRawConstraint> constraints,
            float frameRate)
        {
            if (constraints == null || constraints.Count == 0)
                return 0d;

            int nearest = constraints.Where(item => item != null)
                .Select(item => Math.Abs(item.Frame - frame))
                .DefaultIfEmpty(int.MaxValue)
                .Min();
            if (nearest == int.MaxValue)
                return 0d;
            double seconds = Math.Max(0.1d, nearest / (double)frameRate);
            return 1d / (seconds * seconds);
        }
    }
}
