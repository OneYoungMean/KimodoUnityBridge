using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>Runtime-only root2d queue. FullBody terminal frames come from KMB sampling.</summary>
    internal sealed class KimodoRuntimeConstraints
    {
        internal const string FullBodyType = "fullbody";
        internal const string Root2DType = "root2d";
        internal const string Root2DTargetType = "root2d_target";

        private KimodoConstraintInternalData terminal;
        private readonly List<KimodoRuntimeRoot2DConstraint> staged =
            new List<KimodoRuntimeRoot2DConstraint>();
        private readonly List<KimodoRuntimeRoot2DConstraint> pending =
            new List<KimodoRuntimeRoot2DConstraint>();
        private readonly List<KimodoRuntimeRoot2DTarget> stagedTargets =
            new List<KimodoRuntimeRoot2DTarget>();
        private readonly List<KimodoRuntimeRoot2DTarget> pendingTargets =
            new List<KimodoRuntimeRoot2DTarget>();
        private readonly List<KimodoRuntimeEffectorConstraint> stagedEffectors =
            new List<KimodoRuntimeEffectorConstraint>();
        private readonly List<KimodoRuntimeEffectorConstraint> pendingEffectors =
            new List<KimodoRuntimeEffectorConstraint>();

        internal int PendingRevision { get; private set; }

        internal void StageRoot2D(KimodoRuntimeRoot2DConstraint constraint, double absoluteTimeOffset = 0.0)
        {
            if (constraint == null)
            {
                return;
            }

            stagedTargets.Clear();
            pendingTargets.Clear();
            KimodoRuntimeRoot2DConstraint owned = constraint.Clone();
            owned.sampleTime += absoluteTimeOffset;
            Upsert(staged, owned);
        }

        internal void StageRoot2DTarget(KimodoRuntimeRoot2DTarget target)
        {
            if (target == null)
            {
                return;
            }

            // A target supersedes all future root constraints; the backend owns
            // the replacement and replanning from the current ARDY cursor.
            staged.Clear();
            pending.Clear();
            stagedTargets.Clear();
            pendingTargets.Clear();
            stagedTargets.Add(target.Clone());
        }

        internal bool Commit()
        {
            if (staged.Count == 0 && stagedTargets.Count == 0 && stagedEffectors.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < staged.Count; i++)
            {
                Upsert(pending, staged[i]);
            }

            for (int i = 0; i < stagedTargets.Count; i++)
            {
                pendingTargets.Add(stagedTargets[i].Clone());
            }

            for (int i = 0; i < stagedEffectors.Count; i++)
            {
                UpsertEffector(pendingEffectors, stagedEffectors[i]);
            }

            staged.Clear();
            stagedTargets.Clear();
            stagedEffectors.Clear();
            PendingRevision++;
            return true;
        }

        internal void ClearUser()
        {
            staged.Clear();
            pending.Clear();
            stagedTargets.Clear();
            pendingTargets.Clear();
            stagedEffectors.Clear();
            pendingEffectors.Clear();
            PendingRevision++;
        }

        internal void Clear()
        {
            ClearUser();
            terminal = null;
        }

        internal void SetTerminal(KimodoConstraintInternalData value) => terminal = value?.Clone();

        internal void ClearTerminal() => terminal = null;

        internal KimodoRuntimeConstraintSnapshot CaptureSnapshot()
        {
            var result = new KimodoRuntimeConstraintSnapshot
            {
                PendingRevision = PendingRevision,
                Terminal = terminal?.Clone()
            };
            for (int i = 0; i < pending.Count; i++)
            {
                result.Root2D.Add(pending[i].Clone());
            }

            for (int i = 0; i < pendingTargets.Count; i++)
            {
                result.Root2DTargets.Add(pendingTargets[i].Clone());
            }

            for (int i = 0; i < pendingEffectors.Count; i++)
            {
                result.Effectors.Add(pendingEffectors[i].Clone());
            }
            return result;
        }

        internal void CompleteGeneration(int consumedRevision)
        {
            if (consumedRevision == PendingRevision)
            {
                pending.Clear();
                pendingTargets.Clear();
                pendingEffectors.Clear();
            }
        }

        internal void StageEffector(KimodoRuntimeEffectorConstraint constraint)
        {
            if (constraint == null || constraint.json == null)
            {
                return;
            }

            UpsertEffector(stagedEffectors, constraint);
        }

        private static void Upsert(
            List<KimodoRuntimeRoot2DConstraint> constraints,
            KimodoRuntimeRoot2DConstraint constraint)
        {
            for (int i = constraints.Count - 1; i >= 0; i--)
            {
                if (constraints[i] == null ||
                    Math.Abs(constraints[i].sampleTime - constraint.sampleTime) <= 1e-6)
                {
                    constraints.RemoveAt(i);
                }
            }

            constraints.Add(constraint);
        }

        private static void UpsertEffector(
            List<KimodoRuntimeEffectorConstraint> constraints,
            KimodoRuntimeEffectorConstraint constraint)
        {
            for (int i = constraints.Count - 1; i >= 0; i--)
            {
                KimodoRuntimeEffectorConstraint existing = constraints[i];
                if (existing == null ||
                    string.Equals(existing.json?.type, constraint.json?.type, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(existing.sampleTime - constraint.sampleTime) <= 1e-6)
                {
                    constraints.RemoveAt(i);
                }
            }

            constraints.Add(constraint.Clone());
        }

        internal static KimodoConstraintJson CloneJson(KimodoConstraintJson source)
        {
            return new KimodoConstraintJson
            {
                type = source.type,
                frame_indices = source.frame_indices != null
                    ? new List<int>(source.frame_indices)
                    : new List<int>(),
                smooth_root_2d = CloneVectors(source.smooth_root_2d),
                global_root_heading = CloneVectors(source.global_root_heading),
                root_positions = CloneVectors(source.root_positions),
                target_positions = CloneVectors(source.target_positions),
                joint_names = source.joint_names != null
                    ? new List<string>(source.joint_names)
                    : null,
                local_joints_rot = CloneJointRotations(source.local_joints_rot),
                dense_path = source.dense_path
            };
        }

        private static List<float[]> CloneVectors(List<float[]> source)
        {
            if (source == null) return null;
            var result = new List<float[]>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                result.Add(source[i] != null ? (float[])source[i].Clone() : null);
            }
            return result;
        }

        private static List<float[][]> CloneJointRotations(List<float[][]> source)
        {
            if (source == null) return null;
            var result = new List<float[][]>(source.Count);
            for (int i = 0; i < source.Count; i++)
            {
                float[][] frame = source[i];
                if (frame == null)
                {
                    result.Add(null);
                    continue;
                }

                var clonedFrame = new float[frame.Length][];
                for (int joint = 0; joint < frame.Length; joint++)
                {
                    clonedFrame[joint] = frame[joint] != null
                        ? (float[])frame[joint].Clone()
                        : null;
                }
                result.Add(clonedFrame);
            }
            return result;
        }
    }

    internal sealed class KimodoRuntimeConstraintSnapshot
    {
        internal int PendingRevision;
        internal KimodoConstraintInternalData Terminal;
        internal readonly List<KimodoRuntimeRoot2DConstraint> Root2D =
            new List<KimodoRuntimeRoot2DConstraint>();
        internal readonly List<KimodoRuntimeRoot2DTarget> Root2DTargets =
            new List<KimodoRuntimeRoot2DTarget>();
        internal readonly List<KimodoRuntimeEffectorConstraint> Effectors =
            new List<KimodoRuntimeEffectorConstraint>();
    }

    internal static class KimodoRuntimeConstraintEncoder
    {
        internal static string BuildKimodo(
            KimodoRuntimeConstraintSnapshot snapshot,
            float duration,
            KimodoConstraintInternalData firstFrameOverride)
        {
            return Build(snapshot, false, 0.0, duration,
                KimodoMotionModelProfiles.DefaultFrameRate, firstFrameOverride);
        }

        internal static string BuildArdy(
            KimodoRuntimeConstraintSnapshot snapshot,
            double playbackTime,
            float duration,
            float sourceFps)
        {
            return Build(snapshot, true, playbackTime, duration, sourceFps, null);
        }

        private static string Build(
            KimodoRuntimeConstraintSnapshot snapshot,
            bool isArdy,
            double playbackTime,
            float duration,
            float exportFps,
            KimodoConstraintInternalData firstFrameOverride)
        {
            if (snapshot == null)
            {
                return string.Empty;
            }

            float fps = exportFps > 0f ? exportFps : KimodoMotionModelProfiles.DefaultFrameRate;
            int maxFrame = Mathf.Max(0, KimodoFrameTimeUtility.SecondsToFrameCount(duration, fps) - 1);
            var root2D = new List<KimodoRuntimeRoot2DConstraint>(snapshot.Root2D.Count);
            for (int i = 0; i < snapshot.Root2D.Count; i++)
            {
                KimodoRuntimeRoot2DConstraint constraint = snapshot.Root2D[i]?.Clone();
                if (constraint == null)
                {
                    continue;
                }

                constraint.sampleTime = isArdy
                    ? Math.Max(0.0, constraint.sampleTime - playbackTime)
                    : Mathf.Clamp((float)constraint.sampleTime, 0f, duration);
                root2D.Add(constraint);
            }
            root2D.Sort((left, right) => left.sampleTime.CompareTo(right.sampleTime));

            List<KimodoRuntimeRoot2DTarget> targets = isArdy
                ? snapshot.Root2DTargets
                : null;
            List<KimodoConstraintJson> effectors = BuildEffectors(
                snapshot.Effectors,
                isArdy,
                playbackTime,
                duration,
                fps,
                maxFrame);
            KimodoConstraintInternalData terminal = isArdy
                ? null
                : (firstFrameOverride ?? snapshot.Terminal)?.Clone();
            if (terminal != null)
            {
                terminal.sampleTime = 0.0;
            }

            if (root2D.Count == 0 && (targets == null || targets.Count == 0) &&
                effectors.Count == 0 && terminal == null)
            {
                return string.Empty;
            }

            string futureConstraints = BuildRoot2DJson(root2D, duration, fps);
            string targetConstraints = BuildRoot2DTargetJson(targets);
            if (!string.IsNullOrWhiteSpace(targetConstraints))
            {
                futureConstraints = targetConstraints;
            }

            if (effectors.Count > 0)
            {
                JArray combinedEffectors = string.IsNullOrWhiteSpace(futureConstraints)
                    ? new JArray()
                    : JArray.Parse(futureConstraints);
                for (int i = 0; i < effectors.Count; i++)
                {
                    if (effectors[i] == null)
                    {
                        continue;
                    }

                    string serialized = JsonConvert.SerializeObject(
                        effectors[i],
                        Formatting.None,
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    combinedEffectors.Add(JToken.Parse(serialized));
                }
                futureConstraints = combinedEffectors.Count > 0
                    ? combinedEffectors.ToString(Formatting.Indented)
                    : string.Empty;
            }

            if (terminal == null)
            {
                return futureConstraints;
            }

            JArray combined = string.IsNullOrWhiteSpace(futureConstraints)
                ? new JArray()
                : JArray.Parse(futureConstraints);
            JArray internalConstraints = KimodoRawMotionConstraintBuilder.BuildFullBodyConstraints(
                new[] { terminal }, fps, duration);
            foreach (JToken constraint in internalConstraints)
            {
                combined.Add(constraint);
            }
            return combined.ToString(Formatting.Indented);
        }

        private static List<KimodoConstraintJson> BuildEffectors(
            IReadOnlyList<KimodoRuntimeEffectorConstraint> source,
            bool isArdy,
            double playbackTime,
            float duration,
            float fps,
            int maxFrame)
        {
            var result = new List<KimodoConstraintJson>(source?.Count ?? 0);
            if (source == null)
            {
                return result;
            }

            for (int i = 0; i < source.Count; i++)
            {
                KimodoRuntimeEffectorConstraint pending = source[i];
                if (pending?.json == null)
                {
                    continue;
                }

                KimodoConstraintJson json = KimodoRuntimeConstraints.CloneJson(pending.json);
                double relativeTime = isArdy
                    ? Math.Max(0.0, pending.sampleTime - playbackTime)
                    : Math.Max(0.0, Math.Min(duration, pending.sampleTime));
                int frame = KimodoFrameTimeUtility.SecondsToFrameIndex(relativeTime, fps);
                if (!isArdy)
                {
                    frame = Mathf.Clamp(frame, 0, maxFrame);
                }
                json.frame_indices = new List<int> { frame };
                result.Add(json);
            }
            return result;
        }

        private static string BuildRoot2DJson(
            IReadOnlyList<KimodoRuntimeRoot2DConstraint> constraints,
            float duration,
            float fps)
        {
            if (constraints == null || constraints.Count == 0)
            {
                return string.Empty;
            }

            var frameIndices = new JArray();
            var roots = new JArray();
            var headings = new JArray();
            bool hasCompleteHeading = true;
            int maxFrame = Mathf.Max(0, KimodoFrameTimeUtility.SecondsToFrameCount(duration, fps) - 1);
            for (int i = 0; i < constraints.Count; i++)
            {
                KimodoRuntimeRoot2DConstraint constraint = constraints[i];
                if (constraint == null)
                {
                    continue;
                }

                int frame = Mathf.Clamp(
                    KimodoFrameTimeUtility.SecondsToFrameIndex(constraint.sampleTime, fps),
                    0,
                    maxFrame);
                frameIndices.Add(frame);
                roots.Add(new JArray(constraint.protocolRoot.x, constraint.protocolRoot.y));
                if (constraint.hasHeading)
                {
                    headings.Add(new JArray(constraint.protocolHeading.x, constraint.protocolHeading.y));
                }
                else
                {
                    hasCompleteHeading = false;
                }
            }

            if (frameIndices.Count == 0)
            {
                return string.Empty;
            }

            var root2d = new JObject
            {
                ["type"] = KimodoRuntimeConstraints.Root2DType,
                ["frame_indices"] = frameIndices,
                ["smooth_root_2d"] = roots
            };
            if (hasCompleteHeading)
            {
                root2d["global_root_heading"] = headings;
            }
            return new JArray(root2d).ToString(Formatting.Indented);
        }

        private static string BuildRoot2DTargetJson(IReadOnlyList<KimodoRuntimeRoot2DTarget> targets)
        {
            if (targets == null || targets.Count == 0)
            {
                return string.Empty;
            }

            var result = new JArray();
            for (int i = 0; i < targets.Count; i++)
            {
                KimodoRuntimeRoot2DTarget target = targets[i];
                if (target == null)
                {
                    continue;
                }

                var item = new JObject
                {
                    ["type"] = KimodoRuntimeConstraints.Root2DTargetType,
                    ["target_root_2d"] = new JArray(target.protocolRoot.x, target.protocolRoot.y),
                    ["max_speed"] = target.maxSpeed,
                    ["max_acceleration"] = target.maxAcceleration,
                    ["arrival_threshold"] = target.arrivalThreshold,
                    ["include_heading"] = target.includeHeading
                };
                if (target.includeHeading && target.hasHeading)
                {
                    item["heading"] = new JArray(target.protocolHeading.x, target.protocolHeading.y);
                }
                result.Add(item);
            }
            return result.Count == 0 ? string.Empty : result.ToString(Formatting.Indented);
        }
    }

    internal sealed class KimodoRuntimeRoot2DConstraint
    {
        internal double sampleTime;
        internal Vector2 protocolRoot;
        internal Vector2 protocolHeading;
        internal bool hasHeading;

        internal KimodoRuntimeRoot2DConstraint Clone() => new KimodoRuntimeRoot2DConstraint
        {
            sampleTime = sampleTime,
            protocolRoot = protocolRoot,
            protocolHeading = protocolHeading,
            hasHeading = hasHeading
        };
    }

    internal sealed class KimodoRuntimeRoot2DTarget
    {
        internal Vector2 protocolRoot;
        internal float maxSpeed;
        internal float maxAcceleration;
        internal float arrivalThreshold;
        internal bool includeHeading;
        internal Vector2 protocolHeading;
        internal bool hasHeading;

        internal KimodoRuntimeRoot2DTarget Clone() => new KimodoRuntimeRoot2DTarget
        {
            protocolRoot = protocolRoot,
            maxSpeed = maxSpeed,
            maxAcceleration = maxAcceleration,
            arrivalThreshold = arrivalThreshold,
            includeHeading = includeHeading,
            protocolHeading = protocolHeading,
            hasHeading = hasHeading
        };
    }

    internal static class KimodoRoot2DPlanner
    {
        internal static bool HasArrived(
            Vector3 currentWorldPosition,
            Vector2 targetWorldPosition,
            float thresholdMeters) =>
            (KimodoMotionMath.ToPlanar(currentWorldPosition) - targetWorldPosition).magnitude <=
            Mathf.Max(0f, thresholdMeters);

        internal static float EstimateDuration(
            float distanceMeters,
            float maxSpeedMetersPerSecond,
            float maxAccelerationMetersPerSecond2,
            float minimumDurationSeconds,
            float maximumDurationSeconds)
        {
            float distance = Mathf.Max(0f, distanceMeters);
            float maxSpeed = Mathf.Max(0.01f, maxSpeedMetersPerSecond);
            float maxAcceleration = Mathf.Max(0.01f, maxAccelerationMetersPerSecond2);
            float accelerationTime = maxSpeed / maxAcceleration;
            float accelerationDistance = 0.5f * maxAcceleration * accelerationTime * accelerationTime;
            float duration = distance <= 2f * accelerationDistance
                ? 2f * Mathf.Sqrt(distance / maxAcceleration)
                : 2f * accelerationTime + (distance - 2f * accelerationDistance) / maxSpeed;
            return Mathf.Clamp(duration, minimumDurationSeconds, maximumDurationSeconds);
        }

        internal static Vector2 ToModelOffset(
            Vector3 currentWorldPosition,
            Quaternion modelToWorldRotation,
            Vector3 targetWorldPosition)
        {
            return KimodoMotionMath.ToPlanarLocalDelta(
                targetWorldPosition - currentWorldPosition,
                modelToWorldRotation);
        }

        internal static Vector2 ToModelHeading(
            Quaternion modelToWorldRotation,
            Vector2 worldHeading)
        {
            Vector2 normalizedWorldHeading = NormalizeHeading(worldHeading);
            Vector2 modelHeading = KimodoMotionMath.ToPlanarLocalDelta(
                new Vector3(normalizedWorldHeading.x, 0f, normalizedWorldHeading.y),
                modelToWorldRotation);
            return NormalizeHeading(modelHeading);
        }

        internal static Vector2 NormalizeHeading(Vector2 heading)
        {
            if (heading.sqrMagnitude <= 1e-8f)
            {
                return Vector2.right;
            }

            return heading.normalized;
        }
    }
}
