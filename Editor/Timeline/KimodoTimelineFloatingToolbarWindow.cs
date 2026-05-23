using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor
{
#if KIMODO_TIMELINE_FLOATING_UI
    // Temporary floating toolbar for Timeline:
    // - Anchored to Timeline window bottom-center.
    // - Collapses when no Kimodo clip is selected.
    // - Expands with prompt input + send action when selected.
    internal sealed class KimodoTimelineFloatingToolbarWindow : EditorWindow
    {
        private const string WindowTitle = "Kimodo Timeline Toolbar";

        // Expanded/collapsed sizes for the bottom-center anchor mode.
        private const float ExpandedWidth = 560f;
        private const float ExpandedHeight = 140f;
        private const float CollapsedWidth = 280f;
        private const float CollapsedHeight = 22f;
        private const float BottomMargin = 8f;

        private static KimodoTimelineFloatingToolbarWindow instance;
        private static bool bootstrapUpdateHooked;

        private bool dialogExpanded = true;
        private bool isSending;
        private string promptInput = string.Empty;
        private int lastClipInstanceId;
        private bool lastHasSelection;

        // Hook once after domain reload; keep window presence in sync with Timeline visibility.
        [InitializeOnLoadMethod]
        private static void Bootstrap()
        {
            if (bootstrapUpdateHooked)
            {
                return;
            }

            bootstrapUpdateHooked = true;
            EditorApplication.update += EnsureWindowWhenTimelineVisible;
        }

        // Auto show/hide behavior:
        // - If Timeline window is closed, close this floating toolbar.
        // - If Timeline window is open and toolbar not created yet, create it.
        private static void EnsureWindowWhenTimelineVisible()
        {
            if (TimelineEditor.GetWindow() == null)
            {
                if (instance != null)
                {
                    instance.Close();
                }
                return;
            }

            if (instance == null)
            {
                EnsureWindow();
            }
        }

        private static void EnsureWindow(bool forceFocus = false)
        {
            if (TimelineEditor.GetWindow() == null && !forceFocus)
            {
                return;
            }

            // Popup style keeps the tool lightweight and visually floating.
            if (instance == null)
            {
                instance = CreateInstance<KimodoTimelineFloatingToolbarWindow>();
                instance.titleContent = new GUIContent(WindowTitle);
                instance.ShowPopup();
            }

            if (forceFocus)
            {
                instance.Focus();
            }

            instance.AlignToTimelineWindow();
        }

        private void OnEnable()
        {
            instance = this;
            hideFlags = HideFlags.DontSave;
            EditorApplication.update += OnEditorUpdate;
            wantsMouseMove = true;
            AlignToTimelineWindow();
        }

        private void OnDisable()
        {
            if (ReferenceEquals(instance, this))
            {
                instance = null;
            }

            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (this == null)
            {
                return;
            }

            TimelineEditorWindow timelineWindow = TimelineEditor.GetWindow();
            if (timelineWindow == null)
            {
                Close();
                return;
            }

            // Sync prompt text when Timeline selection switches to another Kimodo clip.
            bool hasSelection = KimodoGenerateAndBakeService.TryGetSelectedKimodoClip(out KimodoPlayableClip clip, out _);
            if (hasSelection)
            {
                int clipId = clip.GetInstanceID();
                if (clipId != lastClipInstanceId)
                {
                    lastClipInstanceId = clipId;
                    promptInput = clip.motionPrompt ?? string.Empty;
                }
            }
            else
            {
                lastClipInstanceId = 0;
            }

            if (hasSelection != lastHasSelection)
            {
                lastHasSelection = hasSelection;
            }

            AlignToTimelineWindow();
            Repaint();
        }

        // Anchor to Timeline window bottom-center.
        // The toolbar position follows the window frame, not track content scroll/drag.
        private void AlignToTimelineWindow()
        {
            TimelineEditorWindow timelineWindow = TimelineEditor.GetWindow();
            if (timelineWindow == null)
            {
                return;
            }

            Rect timelineRect = timelineWindow.position;
            bool hasSelection = KimodoGenerateAndBakeService.TryGetSelectedKimodoClip(out _, out _);
            bool expandedVisible = hasSelection;

            float width = expandedVisible ? ExpandedWidth : CollapsedWidth;
            float height = expandedVisible ? ExpandedHeight : CollapsedHeight;

            float x = timelineRect.x + (timelineRect.width - width) * 0.5f;
            float y = timelineRect.yMax - height - BottomMargin;

            Rect targetRect = new Rect(Mathf.Round(x), Mathf.Round(y), width, height);
            if (position != targetRect)
            {
                position = targetRect;
            }
        }

        private void OnGUI()
        {
            bool hasSelection = KimodoGenerateAndBakeService.TryGetSelectedKimodoClip(out KimodoPlayableClip clip, out _);

            // Collapsed state when no valid Kimodo clip is selected in Timeline.
            if (!hasSelection)
            {
                DrawCollapsedBar();
                return;
            }

            DrawExpandedToolbar(clip);
        }

        private void DrawCollapsedBar()
        {
            Rect rect = GUILayoutUtility.GetRect(position.width - 8f, CollapsedHeight - 4f);
            EditorGUI.DrawRect(rect, new Color(0.10f, 0.10f, 0.10f, 0.90f));
            GUI.Label(rect, "Select a Kimodo clip in Timeline to show toolbar.", EditorStyles.centeredGreyMiniLabel);
        }

        private void DrawExpandedToolbar(KimodoPlayableClip clip)
        {
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Dialog", GUILayout.Width(90f), GUILayout.Height(24f)))
            {
                dialogExpanded = !dialogExpanded;
            }

            // Disable send while current clip is already generating to avoid duplicate requests.
            using (new EditorGUI.DisabledScope(isSending || KimodoGenerateAndBakeService.IsClipGenerating(clip)))
            {
                if (GUILayout.Button("Send", GUILayout.Width(90f), GUILayout.Height(24f)))
                {
                    _ = SendAsync(clip);
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label("Kimodo", EditorStyles.miniBoldLabel);
            EditorGUILayout.EndHorizontal();

            if (dialogExpanded)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("Prompt", EditorStyles.miniBoldLabel);
                promptInput = EditorGUILayout.TextArea(promptInput ?? string.Empty, GUILayout.MinHeight(56f));
            }

            if (isSending || KimodoGenerateAndBakeService.IsClipGenerating(clip))
            {
                EditorGUILayout.LabelField("Sending / Generating...", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        // Send flow:
        // 1) Write prompt into selected Kimodo clip.
        // 2) Reuse existing inspector generation path through shared service.
        private async Task SendAsync(KimodoPlayableClip clip)
        {
            if (clip == null || isSending)
            {
                return;
            }

            isSending = true;
            Repaint();
            try
            {
                string prompt = promptInput ?? string.Empty;
                KimodoGenerateAndBakeService.TryWritePromptToClip(clip, prompt);
                await KimodoGenerateAndBakeService.GenerateForClipAsync(clip, prompt);
            }
            finally
            {
                isSending = false;
                Repaint();
            }
        }
    }
#endif
}
