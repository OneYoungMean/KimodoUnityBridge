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
                normalized.enableMask ??= new KimodoConstraintMask();
                normalized.validMask ??= new KimodoConstraintMask();
                KimodoConstraintMask sourceValid = KimodoConstraintMask.FromSample(sample);
                normalized.enableMask.muscle |= sample.enableMask.muscle;
                normalized.validMask.muscle |= sourceValid.muscle;
                normalized.enableMask.rootTQ |= sample.enableMask.rootTQ;
                normalized.validMask.rootTQ |= sourceValid.rootTQ;
                normalized.enableMask.leftFootTQ |= sample.enableMask.leftFootTQ;
                normalized.validMask.leftFootTQ |= sourceValid.leftFootTQ;
                normalized.enableMask.rightFootTQ |= sample.enableMask.rightFootTQ;
                normalized.validMask.rightFootTQ |= sourceValid.rightFootTQ;
                normalized.enableMask.rootPosition |= sample.enableMask.rootPosition;
                normalized.validMask.rootPosition |= sourceValid.rootPosition;
                normalized.enableMask.rootHeading |= sample.enableMask.rootHeading;
                normalized.validMask.rootHeading |= sourceValid.rootHeading;
                normalized.enableMask.leftHand |= sample.enableMask.leftHand;
                normalized.validMask.leftHand |= sourceValid.leftHand;
                normalized.enableMask.rightHand |= sample.enableMask.rightHand;
                normalized.validMask.rightHand |= sourceValid.rightHand;
                normalized.enableMask.leftFoot |= sample.enableMask.leftFoot;
                normalized.validMask.leftFoot |= sourceValid.leftFoot;
                normalized.enableMask.rightFoot |= sample.enableMask.rightFoot;
                normalized.validMask.rightFoot |= sourceValid.rightFoot;
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

            // Root2D handles edit the same canonical world-space payload as
            // effectors. Preserve that edit explicitly because normalization
            // starts from the marker's authored sample for non-AutoSample
            // markers.
            if (sample.rootOverride != null &&
                (!marker.autoSample || KimodoConstraintMask.IsActive(sample, "rootposition")))
            {
                normalized.rootOverride = sample.rootOverride.Clone();
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

            return string.Equals(left.constraintMode ?? string.Empty, right.constraintMode ?? string.Empty, System.StringComparison.Ordinal) &&
                string.Equals(SampleDataSignature(left), SampleDataSignature(right), System.StringComparison.Ordinal) &&
                string.Equals(EffectorsSignature(left), EffectorsSignature(right), System.StringComparison.Ordinal) &&
                string.Equals(RootOverrideSignature(left), RootOverrideSignature(right), System.StringComparison.Ordinal) &&
                string.Equals(MaskSignature(left.enableMask), MaskSignature(right.enableMask), System.StringComparison.Ordinal) &&
                string.Equals(MaskSignature(left.validMask), MaskSignature(right.validMask), System.StringComparison.Ordinal);
        }

        private static string SampleDataSignature(KimodoMarkerSampleResult sample)
        {
            return sample?.sampleData?.data != null ? string.Join(",", sample.sampleData.data) : string.Empty;
        }

        private static string MaskSignature(KimodoConstraintMask mask) =>
            mask != null ? JsonUtility.ToJson(mask) : string.Empty;

        private static string RootOverrideSignature(KimodoMarkerSampleResult sample)
        {
            return KimodoConstraintMask.IsActive(sample, "rootposition")
                ? JsonUtility.ToJson(sample.rootOverride)
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
