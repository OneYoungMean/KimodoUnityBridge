using System.Collections.Generic;
using KimodoBridge;
using KimodoBridge.Editor;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    public readonly struct command_selected_clip
    {
        public command_selected_clip(int clipInstanceId, string prompt)
        {
            ClipInstanceId = clipInstanceId;
            Prompt = prompt ?? string.Empty;
        }

        public int ClipInstanceId { get; }

        public string Prompt { get; }

        public bool IsValid => ClipInstanceId != 0;

        public string TargetKey => IsValid ? "clip:" + ClipInstanceId : "clip:null";
    }

    public static class command_selection
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

        public static bool TryGetSelectedPlayableClip(out command_selected_clip info)
        {
            info = default;

            TimelineClip[] selectedClips = TimelineEditor.selectedClips;
            if (selectedClips != null)
            {
                for (int i = 0; i < selectedClips.Length; i++)
                {
                    if (selectedClips[i]?.asset is KimodoPlayableClip playableFromTimeline)
                    {
                        info = new command_selected_clip(
                            KimodoUnityObjectIdUtility.IdHash(playableFromTimeline),
                            playableFromTimeline.motionPrompt);
                        return true;
                    }
                }
            }

            if (Selection.activeObject is KimodoPlayableClip selectedAsset)
            {
                info = new command_selected_clip(KimodoUnityObjectIdUtility.IdHash(selectedAsset), selectedAsset.motionPrompt);
                return true;
            }

            return false;
        }
    }
}
