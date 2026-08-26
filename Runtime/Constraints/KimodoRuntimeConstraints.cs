using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    /// <summary>Runtime-only root2d queue. FullBody terminal frames come from KMB sampling.</summary>
    internal sealed class KimodoRuntimeConstraints
    {
        internal const string FullBodyType = "fullbody";
        internal const string LeftHandType = "left-hand";
        internal const string RightHandType = "right-hand";
        internal const string LeftFootType = "left-foot";
        internal const string RightFootType = "right-foot";
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
            if (staged.Count == 0 && stagedTargets.Count == 0)
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

            staged.Clear();
            stagedTargets.Clear();
            PendingRevision++;
            return true;
        }

        internal void ClearUser()
        {
            staged.Clear();
            pending.Clear();
            stagedTargets.Clear();
            pendingTargets.Clear();
            PendingRevision++;
        }

        internal void Clear()
        {
            ClearUser();
            terminal = null;
        }

        internal void SetTerminal(KimodoConstraintInternalData value) => terminal = value?.Clone();

        internal void ClearTerminal() => terminal = null;

        internal List<KimodoRuntimeRoot2DConstraint> BuildRoot2DForGeneration(
            bool isArdy,
            double playbackTime,
            float duration)
        {
            var result = new List<KimodoRuntimeRoot2DConstraint>(pending.Count);
            for (int i = 0; i < pending.Count; i++)
            {
                KimodoRuntimeRoot2DConstraint constraint = pending[i].Clone();
                constraint.sampleTime = isArdy
                    ? Math.Max(0.0, constraint.sampleTime - playbackTime)
                    : Mathf.Clamp((float)constraint.sampleTime, 0f, duration);
                result.Add(constraint);
            }

            result.Sort((left, right) => left.sampleTime.CompareTo(right.sampleTime));
            return result;
        }

        internal List<KimodoRuntimeRoot2DTarget> BuildRoot2DTargetsForGeneration(bool isArdy)
        {
            var result = new List<KimodoRuntimeRoot2DTarget>();
            if (!isArdy)
            {
                return result;
            }

            for (int i = 0; i < pendingTargets.Count; i++)
            {
                result.Add(pendingTargets[i].Clone());
            }
            return result;
        }

        internal KimodoConstraintInternalData BuildTerminalForGeneration(bool isArdy)
        {
            if (isArdy || terminal == null)
            {
                return null;
            }

            KimodoConstraintInternalData result = terminal.Clone();
            result.sampleTime = 0.0;
            return result;
        }

        internal void CompleteGeneration(bool isArdy, int consumedRevision)
        {
            if (consumedRevision == PendingRevision)
            {
                pending.Clear();
                pendingTargets.Clear();
            }
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
            Vector2.Distance(
                new Vector2(currentWorldPosition.x, currentWorldPosition.z),
                targetWorldPosition) <= Mathf.Max(0f, thresholdMeters);

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
            Vector3 worldDelta = targetWorldPosition - currentWorldPosition;
            worldDelta.y = 0f;
            Vector3 localDelta = Quaternion.Inverse(modelToWorldRotation) * worldDelta;
            return new Vector2(localDelta.x, localDelta.z);
        }

        internal static Vector2 ToModelHeading(
            Quaternion modelToWorldRotation,
            Vector2 worldHeading)
        {
            Vector2 normalizedWorldHeading = NormalizeHeading(worldHeading);
            Vector3 modelHeading = Quaternion.Inverse(modelToWorldRotation) *
                new Vector3(normalizedWorldHeading.x, 0f, normalizedWorldHeading.y);
            return NormalizeHeading(new Vector2(modelHeading.x, modelHeading.z));
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
