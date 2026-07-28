using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    public readonly struct KimodoSelectedPlayableClipInfo
    {
        public KimodoSelectedPlayableClipInfo(int clipInstanceId, string prompt)
        {
            ClipInstanceId = clipInstanceId;
            Prompt = prompt ?? string.Empty;
        }

        public int ClipInstanceId { get; }

        public string Prompt { get; }

        public bool IsValid => ClipInstanceId != 0;

        public string TargetKey => IsValid ? "clip:" + ClipInstanceId : "clip:null";
    }

    public static class KimodoEditorSelectionBridge
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
                    if (selected?.asset is not KimodoPlayableClip playable || result.Contains(selected))
                    {
                        continue;
                    }
                    result.Add(selected);
                    containsFallback |= ReferenceEquals(playable, fallback);
                }
            }

            if (result.Count == 0 || !containsFallback)
            {
                result.Clear();
                TimelineClip fallbackClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(fallback);
                if (fallbackClip != null)
                {
                    result.Add(fallbackClip);
                }
            }
            return result;
        }

        public static bool TryGetSelectedPlayableClip(out KimodoSelectedPlayableClipInfo info)
        {
            info = default;

            TimelineClip[] selectedClips = TimelineEditor.selectedClips;
            if (selectedClips != null)
            {
                for (int i = 0; i < selectedClips.Length; i++)
                {
                    if (selectedClips[i]?.asset is KimodoPlayableClip playableFromTimeline)
                    {
                        info = new KimodoSelectedPlayableClipInfo(
                            playableFromTimeline.GetInstanceID(),
                            playableFromTimeline.motionPrompt);
                        return true;
                    }
                }
            }

            if (Selection.activeObject is KimodoPlayableClip selectedAsset)
            {
                info = new KimodoSelectedPlayableClipInfo(selectedAsset.GetInstanceID(), selectedAsset.motionPrompt);
                return true;
            }

            return false;
        }
    }
}
