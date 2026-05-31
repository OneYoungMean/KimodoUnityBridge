using UnityEditor;
using UnityEngine;
using KimodoUnityMotionTools.ProjectEditor;

namespace KimodoUnityMotionTools.ProjectEditor.Retarget
{
    public sealed class KimodoStandaloneHumanoidRetargetFourStageWindow : EditorWindow
    {
        private const string MenuPath = "Kimodo/Standalone Humanoid Retarget Four Stage";

        [SerializeField] private AnimationClip sourceClip;
        [SerializeField] private Avatar sourceAvatar;
        [SerializeField] private Avatar targetAvatar;
        [SerializeField] private float previewTime;
        [SerializeField] private float playbackSpeed = 1f;
        [SerializeField] private bool playing;

        private KimodoRetargetDebugContext debugContext;
        private string status;
        private string error;

        [MenuItem(MenuPath, priority = 122)]
        private static void Open()
        {
            GetWindow<KimodoStandaloneHumanoidRetargetFourStageWindow>("Retarget 4 Stage");
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            ClearPreview();
        }

        private void OnGUI()
        {
            sourceClip = (AnimationClip)EditorGUILayout.ObjectField("Source Clip", sourceClip, typeof(AnimationClip), false);
            sourceAvatar = (Avatar)EditorGUILayout.ObjectField("Source Avatar", sourceAvatar, typeof(Avatar), false);
            targetAvatar = (Avatar)EditorGUILayout.ObjectField("Target Avatar", targetAvatar, typeof(Avatar), false);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Build", GUILayout.Height(28f)))
                {
                    BuildPreview();
                }

                if (GUILayout.Button(playing ? "Pause" : "Play", GUILayout.Height(28f)))
                {
                    playing = !playing;
                }

                if (GUILayout.Button("Clear", GUILayout.Height(28f)))
                {
                    ClearPreview();
                }
            }

            using (new EditorGUI.DisabledScope(sourceClip == null))
            {
                float duration = sourceClip != null ? Mathf.Max(0.0001f, sourceClip.length) : 1f;
                previewTime = EditorGUILayout.Slider("Preview Time", previewTime, 0f, duration);
                playbackSpeed = EditorGUILayout.FloatField("Playback Speed", playbackSpeed);
            }

            EditorGUILayout.Space(8f);
            DrawStage("Origin Bone", debugContext?.originBone);
            DrawStage("Origin Muscle", debugContext?.originMuscle);
            DrawStage("Target Muscle", debugContext?.targetMuscle);
            DrawStage("Target Bone", debugContext?.targetBone);

            if (!string.IsNullOrWhiteSpace(error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }
            else if (!string.IsNullOrWhiteSpace(status))
            {
                EditorGUILayout.HelpBox(status, MessageType.Info);
            }
        }

        private static void DrawStage(string label, KimodoRetargetStageCache cache)
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(cache?.root != null ? "Ready" : "Empty");
            }
        }

        private void OnEditorUpdate()
        {
            if (!playing || sourceClip == null)
            {
                return;
            }

            previewTime += Time.deltaTime * Mathf.Max(0f, playbackSpeed);
            if (previewTime > sourceClip.length)
            {
                previewTime = Mathf.Repeat(previewTime, Mathf.Max(0.0001f, sourceClip.length));
            }

            RefreshPreview();
            SceneView.RepaintAll();
            Repaint();
        }

        private void BuildPreview()
        {
            error = string.Empty;
            status = string.Empty;

            if (sourceClip == null || sourceAvatar == null || targetAvatar == null)
            {
                error = "Source clip and both avatars are required.";
                return;
            }

            ClearPreview();
            if (!KimodoRetargetPipeline.TryCreateRetargetDebugContext(sourceClip, sourceAvatar, targetAvatar, out debugContext, out string buildError))
            {
                error = buildError;
                return;
            }

            RefreshPreview();
            status = $"Preview built at t={previewTime:0.###}";
        }

        private void RefreshPreview()
        {
            if (debugContext == null)
            {
                return;
            }

            if (!KimodoRetargetPipeline.TryUpdateRetargetDebugFrame(debugContext, sourceClip, targetAvatar, previewTime, out KimodoRetargetDebugFrame frame, out string refreshError))
            {
                error = refreshError;
                return;
            }

            status = $"Preview time {previewTime:0.###}";
        }

        private void ClearPreview()
        {
            if (debugContext != null)
            {
                KimodoRetargetPipeline.DestroyRetargetDebugContext(debugContext);
                debugContext = null;
            }

            playing = false;
        }
    }
}
