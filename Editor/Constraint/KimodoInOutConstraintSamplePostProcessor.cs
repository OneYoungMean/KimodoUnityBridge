using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoInOutConstraintSamplePostProcessor
    {
        internal static void NormalizeConstraintOrigin(List<KimodoMarkerSampleResult> samples)
        {
            if (!TryResolveConstraintOriginAnchorSample(samples, out KimodoMarkerSampleResult anchor))
            {
                return;
            }

            Vector3 anchorRootPosition = anchor.unityRootPos;
            Quaternion inverseAnchorRootRotation = Quaternion.Inverse(anchor.unityRootRot);
            for (int i = 0; i < samples.Count; i++)
            {
                NormalizeConstraintOriginSample(samples[i], anchorRootPosition, inverseAnchorRootRotation);
            }
        }

        internal static void CopyPoseAxes(KimodoMarkerSampleResult sourceSample, KimodoMarkerSampleResult destinationSample)
        {
            if (sourceSample == null || destinationSample == null)
            {
                return;
            }

            destinationSample.localAxisAngles = sourceSample.localAxisAngles != null
                ? new List<Vector3>(sourceSample.localAxisAngles)
                : new List<Vector3>();
            destinationSample.sampledJointIndices = sourceSample.sampledJointIndices != null
                ? new List<int>(sourceSample.sampledJointIndices)
                : new List<int>();
            destinationSample.jointNames = sourceSample.jointNames != null
                ? new List<string>(sourceSample.jointNames)
                : new List<string>();
        }

        private static bool TryResolveConstraintOriginAnchorSample(
            List<KimodoMarkerSampleResult> samples,
            out KimodoMarkerSampleResult anchor)
        {
            anchor = null;
            if (samples == null || samples.Count == 0)
            {
                return false;
            }

            int anchorIndex = ResolveConstraintOriginAnchorIndex(samples);
            if (anchorIndex < 0 || anchorIndex >= samples.Count)
            {
                return false;
            }

            anchor = samples[anchorIndex];
            return anchor != null;
        }

        private static int ResolveConstraintOriginAnchorIndex(List<KimodoMarkerSampleResult> samples)
        {
            if (samples == null || samples.Count == 0)
            {
                return -1;
            }

            int earliest = -1;
            double earliestTime = double.MaxValue;
            for (int i = 0; i < samples.Count; i++)
            {
                KimodoMarkerSampleResult sample = samples[i];
                if (sample != null && sample.sampleTime < earliestTime)
                {
                    earliestTime = sample.sampleTime;
                    earliest = i;
                }
            }

            return earliest;
        }

        private static void NormalizeConstraintOriginSample(
            KimodoMarkerSampleResult sample,
            Vector3 anchorRootPosition,
            Quaternion inverseAnchorRootRotation)
        {
            if (sample == null)
            {
                return;
            }

            sample.kimodoRootPosition = inverseAnchorRootRotation * (sample.kimodoRootPosition - anchorRootPosition);
            if (sample.localAxisAngles == null || sample.localAxisAngles.Count == 0)
            {
                return;
            }

            Quaternion rootJointRotation = AxisAngleToQuaternion(sample.localAxisAngles[0]);
            Quaternion normalizedRootJointRotation = inverseAnchorRootRotation * rootJointRotation;
            sample.localAxisAngles[0] = KimodoRuntimeUtility.QuaternionToAxisAngleVector(normalizedRootJointRotation);
        }

        private static Quaternion AxisAngleToQuaternion(Vector3 axisAngle)
        {
            float angleRad = axisAngle.magnitude;
            if (angleRad <= 1e-8f)
            {
                return Quaternion.identity;
            }

            Vector3 axis = axisAngle / angleRad;
            return Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, axis);
        }
    }
}
