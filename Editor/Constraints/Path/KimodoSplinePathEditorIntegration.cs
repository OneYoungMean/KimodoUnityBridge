#if KIMODO_SPLINES
using System.Collections.Generic;
using TimelineInject;
using UnityEditor;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    [InitializeOnLoad]
    internal sealed class KimodoSplinePathEditorIntegration : IKimodoSplinePathEditorIntegration
    {
        static KimodoSplinePathEditorIntegration()
        {
            KimodoSplinePathEditorBridge.Register(new KimodoSplinePathEditorIntegration());
        }

        public bool TrySetEnabled(KimodoPlayableClip clip, bool enabled, out string error)
        {
            return KimodoPlayableSplinePathUtility.TrySetEnabled(clip, enabled, out _, out error);
        }

        public bool TryBeginEditing(
            KimodoPlayableClip clip,
            TimelineClip timelineClip,
            out string error)
        {
            if (!KimodoPlayableSplinePathUtility.TryGetPath(clip, timelineClip, out KimodoPlayableSplinePath path, out error))
            {
                return false;
            }

            KimodoPlayableSplinePathSceneEditor.BeginEditing(path);
            return true;
        }

        public bool TryResetPath(KimodoPlayableClip clip, out string error)
        {
            return KimodoPlayableSplinePathUtility.TryResetPath(clip, out _, out error);
        }

        public bool TryBuildConstraintSamples(
            KimodoPlayableClip clip,
            TimelineClip timelineClip,
            int generationFrames,
            float generationFrameRate,
            out List<KimodoMarkerSampleResult> samples,
            out bool denseRootPath,
            out string error)
        {
            return KimodoPlayableSplinePathUtility.TryBuildConstraintSamples(
                clip,
                timelineClip,
                generationFrames,
                generationFrameRate,
                out samples,
                out denseRootPath,
                out error);
        }

        public void ScheduleRefresh()
        {
            KimodoPlayableSplinePathSceneEditor.ScheduleRefresh();
        }
    }
}
#endif
