using System.Collections.Generic;
using TimelineInject;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal interface IKimodoSplinePathEditorIntegration
    {
        bool TrySetEnabled(KimodoPlayableClip clip, bool enabled, out string error);
        bool TryBeginEditing(KimodoPlayableClip clip, TimelineClip timelineClip, out string error);
        bool TryResetPath(KimodoPlayableClip clip, out string error);
        bool TryBuildConstraintSamples(
            KimodoPlayableClip clip,
            TimelineClip timelineClip,
            int generationFrames,
            float generationFrameRate,
            out List<KimodoMarkerSampleResult> samples,
            out bool denseRootPath,
            out string error);
        void ScheduleRefresh();
    }

    internal static class KimodoSplinePathEditorBridge
    {
        private static IKimodoSplinePathEditorIntegration integration;

        internal static bool IsAvailable => integration != null;

        internal static void Register(IKimodoSplinePathEditorIntegration value)
        {
            integration = value;
        }

        internal static bool TrySetEnabled(KimodoPlayableClip clip, bool enabled, out string error)
        {
            if (integration != null)
            {
                return integration.TrySetEnabled(clip, enabled, out error);
            }

            error = enabled
                ? "Install com.unity.splines to enable the experimental Spline Path integration."
                : string.Empty;
            return !enabled;
        }

        internal static bool TryBeginEditing(
            KimodoPlayableClip clip,
            TimelineClip timelineClip,
            out string error)
        {
            if (integration != null)
            {
                return integration.TryBeginEditing(clip, timelineClip, out error);
            }

            error = "Install com.unity.splines to edit Spline Path data.";
            return false;
        }

        internal static bool TryResetPath(KimodoPlayableClip clip, out string error)
        {
            if (integration != null)
            {
                return integration.TryResetPath(clip, out error);
            }

            error = "Install com.unity.splines to reset Spline Path data.";
            return false;
        }

        internal static bool TryBuildConstraintSamples(
            KimodoPlayableClip clip,
            TimelineClip timelineClip,
            int generationFrames,
            float generationFrameRate,
            out List<KimodoMarkerSampleResult> samples,
            out bool denseRootPath,
            out string error)
        {
            samples = new List<KimodoMarkerSampleResult>();
            denseRootPath = false;
            if (integration != null)
            {
                return integration.TryBuildConstraintSamples(
                    clip,
                    timelineClip,
                    generationFrames,
                    generationFrameRate,
                    out samples,
                    out denseRootPath,
                    out error);
            }

            error = clip != null && clip.splinePathEnabled
                ? "Spline Path is enabled, but com.unity.splines is not installed."
                : string.Empty;
            return string.IsNullOrEmpty(error);
        }

        internal static void ScheduleRefresh()
        {
            integration?.ScheduleRefresh();
        }
    }
}
