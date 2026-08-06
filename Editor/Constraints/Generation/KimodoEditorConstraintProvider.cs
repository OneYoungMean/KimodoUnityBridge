using System;
using System.Collections.Generic;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoEditorConstraintProvider
    {
        public KimodoInOutConstraintResult BuildConstraintDataOrThrow(
            KimodoPlayableClip clip,
            int? generationFramesOverride = null,
            bool disableTimelineInOut = false,
            bool deferNormalization = false,
            bool enableAutoBeginAnchor = true,
            double sampleTimeOffsetSeconds = 0.0,
            TimelineClip timelineClipOverride = null)
        {
            TimelineClip sourceClip = timelineClipOverride ?? KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            if (sourceClip == null)
            {
                return new KimodoInOutConstraintResult();
            }

            bool ok = KimodoInOutConstraintAdapter.TryBuildConstraints(
                sourceClip,
                disableTimelineInOut ? KimodoInOutConstraintMode.None : clip.inOutConstraintMode,
                enableAutoBeginAnchor && clip.autoBeginAnchor,
                deferNormalization,
                // Mode=None prevents boundary sampling; true keeps manual-marker normalization independent of the In toggle.
                disableTimelineInOut || clip.enableInConstraint,
                !disableTimelineInOut && clip.enableOutConstraint,
                generationFramesOverride ?? clip.generationFrames,
                sampleTimeOffsetSeconds,
                out KimodoInOutConstraintResult result,
                out string error);

            if (!ok)
            {
                throw new InvalidOperationException($"Build constraints failed: {error}");
            }

            return result ?? new KimodoInOutConstraintResult();
        }

        public TimelineClip FindTimelineClipForAsset(PlayableAsset asset)
        {
            return KimodoTimelineClipResolver.FindTimelineClipForAsset(asset);
        }

        public GameObject FindTimelineBindingObjectForAsset(
            PlayableAsset asset,
            TimelineClip timelineClipOverride = null)
        {
            TimelineClip sourceClip = timelineClipOverride ?? FindTimelineClipForAsset(asset);
            if (sourceClip == null)
            {
                return null;
            }

            TrackAsset track = sourceClip.GetParentTrack();
            if (track == null)
            {
                return null;
            }

            if (!KimodoInOutConstraintAdapter.TryResolveDirector(
                    sourceClip,
                    track,
                    out PlayableDirector director,
                    out _))
            {
                return null;
            }

            TrackAsset currentTrack = track;
            while (currentTrack != null)
            {
                UnityEngine.Object binding = director.GetGenericBinding(currentTrack);
                if (binding is Animator animator && animator != null)
                {
                    return animator.gameObject;
                }

                if (binding is GameObject go && go != null)
                {
                    return go;
                }

                currentTrack = currentTrack.parent as TrackAsset;
            }

            return null;
        }
    }
}
