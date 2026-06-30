using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoMarkerSamplingEditorUtility
    {
        public static bool TryWriteConstraintMarkerSample(
            KimodoConstraintMarkerBase marker,
            KimodoMarkerSampleResult sample,
            bool keepOverrideEnabled,
            out string error)
        {
            error = string.Empty;
            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (sample == null)
            {
                error = "sample is null";
                return false;
            }

            KimodoMarkerSampleResult normalized = KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, sample);
            if (normalized == null)
            {
                error = "failed to normalize sample";
                return false;
            }

            bool changed = !AreSamplesEquivalent(marker.SampleData, normalized) ||
                System.Math.Abs(marker.time - normalized.sampleTime) > 1e-9 ||
                (keepOverrideEnabled && !marker.useOverride);
            if (!changed)
            {
                return true;
            }

            marker.SampleData = normalized;
            marker.time = normalized.sampleTime;
            if (keepOverrideEnabled)
            {
                marker.useOverride = true;
            }

            MarkConstraintMarkerDirty(marker);
            return true;
        }

        private static void MarkConstraintMarkerDirty(KimodoConstraintMarkerBase marker)
        {
            if (marker == null)
            {
                return;
            }

            EditorUtility.SetDirty(marker);

            if (marker.parent is UnityEngine.Object parentObject)
            {
                EditorUtility.SetDirty(parentObject);
            }

            if (TimelineEditor.inspectedAsset != null)
            {
                EditorUtility.SetDirty(TimelineEditor.inspectedAsset);
            }

            TimelineEditor.Refresh(RefreshReason.ContentsModified);
        }

        private static bool AreSamplesEquivalent(KimodoMarkerSampleResult left, KimodoMarkerSampleResult right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(left.constraintType ?? string.Empty, right.constraintType ?? string.Empty, System.StringComparison.Ordinal) &&
                System.Math.Abs(left.sampleTime - right.sampleTime) <= 1e-9 &&
                left.rigType == right.rigType &&
                left.hasRootHeading == right.hasRootHeading &&
                Approximately(left.kimodoRootPosition, right.kimodoRootPosition) &&
                Approximately(left.rootHeading, right.rootHeading) &&
                Approximately(left.unityRootPos, right.unityRootPos) &&
                Approximately(left.unityRootRot, right.unityRootRot) &&
                StringListsEqual(left.jointNames, right.jointNames) &&
                Vector3ListsEqual(left.localAxisAngles, right.localAxisAngles) &&
                IntListsEqual(left.sampledJointIndices, right.sampledJointIndices);
        }

        private static bool StringListsEqual(System.Collections.Generic.IReadOnlyList<string> left, System.Collections.Generic.IReadOnlyList<string> right)
        {
            int leftCount = left != null ? left.Count : 0;
            int rightCount = right != null ? right.Count : 0;
            if (leftCount != rightCount)
            {
                return false;
            }

            for (int i = 0; i < leftCount; i++)
            {
                if (!string.Equals(left[i] ?? string.Empty, right[i] ?? string.Empty, System.StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Vector3ListsEqual(System.Collections.Generic.IReadOnlyList<Vector3> left, System.Collections.Generic.IReadOnlyList<Vector3> right)
        {
            int leftCount = left != null ? left.Count : 0;
            int rightCount = right != null ? right.Count : 0;
            if (leftCount != rightCount)
            {
                return false;
            }

            for (int i = 0; i < leftCount; i++)
            {
                if (!Approximately(left[i], right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IntListsEqual(System.Collections.Generic.IReadOnlyList<int> left, System.Collections.Generic.IReadOnlyList<int> right)
        {
            int leftCount = left != null ? left.Count : 0;
            int rightCount = right != null ? right.Count : 0;
            if (leftCount != rightCount)
            {
                return false;
            }

            for (int i = 0; i < leftCount; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Approximately(Vector2 left, Vector2 right)
        {
            return (left - right).sqrMagnitude <= 1e-10f;
        }

        private static bool Approximately(Vector3 left, Vector3 right)
        {
            return (left - right).sqrMagnitude <= 1e-10f;
        }

        private static bool Approximately(Quaternion left, Quaternion right)
        {
            return Mathf.Abs(Quaternion.Dot(left, right)) >= 1f - 1e-10f;
        }
    }
}
