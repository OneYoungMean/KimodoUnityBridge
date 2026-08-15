#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoTimelineFootContactSampler
    {
        internal static bool TrySample(
            KimodoTimelineInOutConstraintContext context,
            double timelineTime,
            out byte[] contacts)
        {
            contacts = null;
            TimelineClip source = FindActiveClip(context?.Track, timelineTime);
            if (source == null ||
                !KimodoInOutConstraintAdapter.TryResolveAnimationClip(source, out AnimationClip clip, out _))
            {
                return false;
            }

            double clipTime = source.clipIn + (timelineTime - source.start) * source.timeScale;
            contacts = new byte[KimodoFootContactTrackUtility.ChannelCount];
            for (int channel = 0; channel < contacts.Length; channel++)
            {
                string propertyName = KimodoFootContactTrackUtility.GetPropertyName(channel);
                AnimationCurve curve = AnimationUtility.GetEditorCurve(
                    clip,
                    EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), propertyName));
                if (curve == null)
                {
                    contacts = null;
                    return false;
                }

                contacts[channel] = curve.Evaluate((float)clipTime) >= 0.5f ? (byte)1 : (byte)0;
            }
            return true;
        }

        private static TimelineClip FindActiveClip(TrackAsset track, double time)
        {
            TimelineClip selected = null;
            if (track == null)
            {
                return null;
            }

            foreach (TimelineClip candidate in track.GetClips())
            {
                if (candidate == null ||
                    (time < candidate.start &&
                        !KimodoTimelinePreviewRefreshUtility.ApproximatelyTimelineTime(time, candidate.start)) ||
                    time >= candidate.end)
                {
                    continue;
                }

                // ponytail: choose the latest active clip; overlapping contact blending is not a supported authoring path.
                if (selected == null || candidate.start > selected.start)
                {
                    selected = candidate;
                }
            }
            return selected;
        }
    }
}
#endif
