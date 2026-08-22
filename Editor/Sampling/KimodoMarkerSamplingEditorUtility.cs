using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoMarkerSamplingEditorUtility
    {
        public static bool TryWriteConstraintMarkerSample(
            KimodoConstraintMarker marker,
            KimodoMarkerSampleResult sample,
            out string error,
            bool writeSampledCharacterPose = false)
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

            if (!KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(
                    marker,
                    sample,
                    out KimodoMarkerSampleResult normalized,
                    out error))
            {
                return false;
            }

            // Normalization preserves channel validity; a Scene drag is the
            // explicit editor path that may promote newly changed channels.
            if (sample.enableMask != null)
            {
                normalized.enableMask ??= new KimodoSampleChannelMask();
                normalized.enableMask.muscle49 |= sample.enableMask.muscle49;
                normalized.enableMask.rootTQ |= sample.enableMask.rootTQ;
                normalized.enableMask.leftFootTQ |= sample.enableMask.leftFootTQ;
                normalized.enableMask.rightFootTQ |= sample.enableMask.rightFootTQ;
                normalized.enableMask.root2DPosition |= sample.enableMask.root2DPosition;
                normalized.enableMask.root2DHeading |= sample.enableMask.root2DHeading;
                normalized.enableMask.leftHandEffector |= sample.enableMask.leftHandEffector;
                normalized.enableMask.rightHandEffector |= sample.enableMask.rightHandEffector;
                normalized.enableMask.leftFootEffector |= sample.enableMask.leftFootEffector;
                normalized.enableMask.rightFootEffector |= sample.enableMask.rightFootEffector;
                normalized.enableMask.NormalizeDependencies();
            }

            if (writeSampledCharacterPose && KimodoSampleDataLayout.IsValid(sample.sampleData))
            {
                normalized.sampleData = sample.sampleData.Clone();
                normalized.enableMask = sample.enableMask?.Clone() ?? new KimodoSampleChannelMask();
            }

            // Scene edits author effector targets separately from the canonical pose.
            // Normalization starts from the marker payload, so copy the edited
            // world-space targets explicitly or a drag is lost on the next
            // render.
            if (sample.effectors != null &&
                (!marker.autoSample || HasEffectors(sample.effectors)))
            {
                normalized.effectors = sample.effectors.Clone();
            }

            bool changed = !AreSamplesEquivalent(marker.SampleData, normalized);
            if (!changed)
            {
                return true;
            }

            marker.SampleData = normalized;

            MarkConstraintMarkerDirty(marker);
            return true;
        }

        private static void MarkConstraintMarkerDirty(KimodoConstraintMarker marker)
        {
            if (marker == null)
            {
                return;
            }

            KimodoConstraintMarkerSampling.ClearMarkerCache(marker);
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
                string.Equals(left.constraintMode ?? string.Empty, right.constraintMode ?? string.Empty, System.StringComparison.Ordinal) &&
                string.Equals(SampleDataSignature(left), SampleDataSignature(right), System.StringComparison.Ordinal) &&
                string.Equals(EffectorsSignature(left), EffectorsSignature(right), System.StringComparison.Ordinal) &&
                string.Equals(Root2DOverrideSignature(left), Root2DOverrideSignature(right), System.StringComparison.Ordinal) &&
                left.enableMask?.root2DHeading == right.enableMask?.root2DHeading &&
                left.enableMask?.root2DPosition == right.enableMask?.root2DPosition;
        }

        private static string SampleDataSignature(KimodoMarkerSampleResult sample)
        {
            return sample?.sampleData?.data != null ? string.Join(",", sample.sampleData.data) : string.Empty;
        }

        private static string Root2DOverrideSignature(KimodoMarkerSampleResult sample)
        {
            return sample?.enableMask?.root2DPosition == true
                ? JsonUtility.ToJson(sample.root2DOverride)
                : string.Empty;
        }

        private static string EffectorsSignature(KimodoMarkerSampleResult sample)
        {
            return sample?.effectors != null
                ? JsonUtility.ToJson(sample.effectors)
                : string.Empty;
        }

        private static bool HasEffectors(KimodoConstraintEffectors targets)
        {
            return targets?.leftHand != null || targets?.rightHand != null ||
                targets?.leftFoot != null || targets?.rightFoot != null;
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
