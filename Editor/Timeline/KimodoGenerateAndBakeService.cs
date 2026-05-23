using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal static class KimodoGenerateAndBakeService
    {
        private static readonly HashSet<int> RunningClipIds = new HashSet<int>();

        internal static bool IsAnyGenerating => RunningClipIds.Count > 0;

        internal static bool TryGetSelectedKimodoClip(out KimodoPlayableClip selectedClip, out TimelineClip selectedTimelineClip)
        {
            selectedClip = null;
            selectedTimelineClip = null;

            TimelineClip[] selectedClips = TimelineEditor.selectedClips;
            if (selectedClips == null || selectedClips.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < selectedClips.Length; i++)
            {
                TimelineClip timelineClip = selectedClips[i];
                if (timelineClip?.asset is KimodoPlayableClip kimodoClip)
                {
                    selectedClip = kimodoClip;
                    selectedTimelineClip = timelineClip;
                    return true;
                }
            }

            return false;
        }

        internal static bool TryWritePromptToClip(KimodoPlayableClip clip, string prompt)
        {
            if (clip == null)
            {
                return false;
            }

            string safePrompt = prompt ?? string.Empty;
            if (string.Equals(clip.motionPrompt, safePrompt, StringComparison.Ordinal))
            {
                return true;
            }

            Undo.RecordObject(clip, "Update Kimodo Prompt");
            clip.motionPrompt = safePrompt;
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            return true;
        }

        internal static async Task<bool> GenerateFromInspectorAsync(KimodoPlayableClipEditor editor)
        {
            if (editor == null || editor.target is not KimodoPlayableClip clip)
            {
                return false;
            }

            return await RunWithBusyGuardAsync(clip, () => editor.GenerateForTestsAsync());
        }

        internal static async Task<bool> GenerateForClipAsync(KimodoPlayableClip clip, string prompt)
        {
            if (clip == null)
            {
                return false;
            }

            TryWritePromptToClip(clip, prompt);

            KimodoPlayableClipEditor tempEditor = null;
            try
            {
                //tempEditor = UnityEditor.Editor.CreateEditor(clip, typeof(KimodoPlayableClipEditor)) as KimodoPlayableClipEditor;
                if (tempEditor == null)
                {
                    //Debug.LogError("[Kimodo] Failed to create KimodoPlayableClipEditor for floating toolbar send action.");
                    return false;
                }

                return await RunWithBusyGuardAsync(clip, () => tempEditor.GenerateForTestsAsync());
            }
            finally
            {
                if (tempEditor != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempEditor);
                }
            }
        }

        internal static bool IsClipGenerating(KimodoPlayableClip clip)
        {
            return clip != null && RunningClipIds.Contains(clip.GetInstanceID());
        }

        private static async Task<bool> RunWithBusyGuardAsync(KimodoPlayableClip clip, Func<Task> action)
        {
            if (clip == null || action == null)
            {
                return false;
            }

            int clipId = clip.GetInstanceID();
            if (RunningClipIds.Contains(clipId))
            {
                return false;
            }

            RunningClipIds.Add(clipId);
            try
            {
                await action();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Kimodo] GenerateAndBake service failed: {exception}");
                return false;
            }
            finally
            {
                RunningClipIds.Remove(clipId);
            }
        }
    }
}
