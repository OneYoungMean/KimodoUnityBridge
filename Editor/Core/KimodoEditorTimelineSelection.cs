using System.Collections.Generic;
using TimelineInject;
using UnityEditor.Timeline;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoEditorTimelineSelection
    {
        internal static List<TimelineClip> GetSelectedPlayableClips(KimodoPlayableClip fallback)
        {
            var result = new List<TimelineClip>();
            bool containsFallback = false;
            TimelineClip[] selectedClips = TimelineEditor.selectedClips;
            if (selectedClips != null)
            {
                for (int i = 0; i < selectedClips.Length; i++)
                {
                    TimelineClip selected = selectedClips[i];
                    if (selected?.asset is not KimodoPlayableClip playable || result.Contains(selected)) continue;
                    result.Add(selected);
                    containsFallback |= ReferenceEquals(playable, fallback);
                }
            }

            if (result.Count == 0 || !containsFallback)
            {
                result.Clear();
                TimelineClip fallbackClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(fallback);
                if (fallbackClip != null) result.Add(fallbackClip);
            }
            return result;
        }
    }
}
