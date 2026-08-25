using System;
using System.Collections.Generic;
using KimodoUnityBridge;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    internal sealed class KimodoRuntimeConstraints
    {
        internal const string FullBodyType = "fullbody";
        internal const string LeftHandType = "left-hand";
        internal const string RightHandType = "right-hand";
        internal const string LeftFootType = "left-foot";
        internal const string RightFootType = "right-foot";
        internal const string Root2DType = "root2d";

        private KimodoConstraintInternalData terminal;
        private readonly List<KimodoMarkerSampleResult> staged = new List<KimodoMarkerSampleResult>();
        private readonly List<KimodoMarkerSampleResult> pending = new List<KimodoMarkerSampleResult>();

        internal int StagedCount => staged.Count;
        internal int PendingCount => pending.Count;
        internal int PendingRevision { get; private set; }

        internal void Stage(KimodoMarkerSampleResult sample, double absoluteTimeOffset = 0.0)
        {
            if (sample == null)
            {
                return;
            }

            KimodoMarkerSampleResult owned = sample.Clone();
            owned.sampleTime += absoluteTimeOffset;
            Upsert(staged, owned);
        }

        internal bool Commit()
        {
            if (staged.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < staged.Count; i++)
            {
                Upsert(pending, staged[i]);
            }

            staged.Clear();
            PendingRevision++;
            return true;
        }

        internal void ClearUser()
        {
            staged.Clear();
            pending.Clear();
            PendingRevision++;
        }

        internal void Clear()
        {
            ClearUser();
            terminal = null;
        }

        internal void SetTerminal(KimodoConstraintInternalData value)
        {
            terminal = value?.Clone();
        }

        internal void ClearTerminal() => terminal = null;

        internal List<KimodoMarkerSampleResult> BuildForGeneration(
            bool isArdy,
            double playbackTime,
            float duration)
        {
            var result = new List<KimodoMarkerSampleResult>();
            for (int i = 0; i < pending.Count; i++)
            {
                KimodoMarkerSampleResult sample = pending[i].Clone();
                sample.sampleTime = isArdy
                    ? Math.Max(0.0, sample.sampleTime - playbackTime)
                    : Mathf.Clamp((float)sample.sampleTime, 0f, duration);
                result.Add(sample);
            }

            result.Sort((left, right) => left.sampleTime.CompareTo(right.sampleTime));
            List<KimodoMarkerSampleResult> merged = KimodoConstraintSampleComposer.ComposeCanonicalSamples(
                result,
                KimodoMotionModelProfiles.DefaultFrameRate);
            return merged;
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

        internal void CompleteGeneration(bool isArdy) => CompleteGeneration(isArdy, PendingRevision);

        internal void CompleteGeneration(bool isArdy, int consumedRevision)
        {
            if (!isArdy && consumedRevision == PendingRevision)
            {
                pending.Clear();
            }
        }

        private static void Upsert(
            List<KimodoMarkerSampleResult> samples,
            KimodoMarkerSampleResult sample)
        {
            bool isWaypoint = KimodoConstraintMask.IsActive(sample, "rootposition");
            for (int i = samples.Count - 1; i >= 0; i--)
            {
                KimodoMarkerSampleResult existing = samples[i];
                if (existing == null ||
                    (isWaypoint
                        ? KimodoConstraintMask.IsActive(existing, "rootposition") &&
                          Math.Abs(existing.sampleTime - sample.sampleTime) <= 1e-6
                        : SameChannels(existing, sample)))
                {
                    samples.RemoveAt(i);
                }
            }

            samples.Add(sample);
        }

        private static bool SameChannels(KimodoMarkerSampleResult left, KimodoMarkerSampleResult right)
        {
            if (left == null || right == null || Math.Abs(left.sampleTime - right.sampleTime) > 1e-6)
            {
                return false;
            }

            return KimodoConstraintMask.IsActive(left, "muscle") ==
                       KimodoConstraintMask.IsActive(right, "muscle") &&
                   KimodoConstraintMask.IsActive(left, "rootposition") ==
                       KimodoConstraintMask.IsActive(right, "rootposition") &&
                   KimodoConstraintMask.IsActive(left, "rootheading") ==
                       KimodoConstraintMask.IsActive(right, "rootheading") &&
                   KimodoConstraintMask.IsActive(left, "leftfoot") ==
                       KimodoConstraintMask.IsActive(right, "leftfoot") &&
                   KimodoConstraintMask.IsActive(left, "rightfoot") ==
                       KimodoConstraintMask.IsActive(right, "rightfoot") &&
                   KimodoConstraintMask.IsActive(left, "lefthand") ==
                       KimodoConstraintMask.IsActive(right, "lefthand") &&
                   KimodoConstraintMask.IsActive(left, "righthand") ==
                       KimodoConstraintMask.IsActive(right, "righthand");
        }
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
