using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor.Retarget
{
    public sealed class KimodoStandaloneHumanoidRetargetToolWindow : EditorWindow
    {
        private const string MenuPath = "Kimodo/Standalone Humanoid Retarget Test";

        [SerializeField] private GameObject sourcePrefab;
        [SerializeField] private Avatar sourceAvatar;
        [SerializeField] private AnimationClip sourceClip;
        [SerializeField] private GameObject targetPrefab;
        [SerializeField] private Avatar targetAvatar;
        [SerializeField] private string outputAssetPath = "Assets/Retargeted.anim";

        private string status;
        private string error;

        [MenuItem(MenuPath, priority = 120)]
        private static void Open()
        {
            GetWindow<KimodoStandaloneHumanoidRetargetToolWindow>("Standalone Retarget");
        }

        private void OnGUI()
        {
            sourcePrefab = (GameObject)EditorGUILayout.ObjectField("Source Prefab", sourcePrefab, typeof(GameObject), false);
            sourceAvatar = (Avatar)EditorGUILayout.ObjectField("Source Avatar", sourceAvatar, typeof(Avatar), false);
            sourceClip = (AnimationClip)EditorGUILayout.ObjectField("Source Clip", sourceClip, typeof(AnimationClip), false);
            targetPrefab = (GameObject)EditorGUILayout.ObjectField("Target Prefab", targetPrefab, typeof(GameObject), false);
            targetAvatar = (Avatar)EditorGUILayout.ObjectField("Target Avatar", targetAvatar, typeof(Avatar), false);
            outputAssetPath = EditorGUILayout.TextField("Output Asset", outputAssetPath);

            if (GUILayout.Button("Retarget And Save", GUILayout.Height(32f)))
            {
                RunRetarget();
            }

            if (!string.IsNullOrWhiteSpace(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            else if (!string.IsNullOrWhiteSpace(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
        }

        private void RunRetarget()
        {
            error = string.Empty;
            status = string.Empty;

            if (!KimodoStandaloneHumanoidRetargetPreviewUtility.TryResolveAvatar(sourcePrefab, sourceAvatar, out Avatar resolvedSourceAvatar, out string sourceAvatarError))
            {
                error = sourceAvatarError;
                return;
            }

            if (!KimodoStandaloneHumanoidRetargetPreviewUtility.TryResolveAvatar(targetPrefab, targetAvatar, out Avatar resolvedTargetAvatar, out string targetAvatarError))
            {
                error = targetAvatarError;
                return;
            }

            if (!KimodoRetargetPipeline.TryRetargetClip(sourceClip, resolvedSourceAvatar, resolvedTargetAvatar, out AnimationClip outputClip, out KimodoRetargetResultMode mode, out string retargetError))
            {
                error = retargetError;
                return;
            }

            if (outputClip == null)
            {
                error = "Retarget returned null output clip.";
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(outputAssetPath) != null)
            {
                AssetDatabase.DeleteAsset(outputAssetPath);
            }

            AssetDatabase.CreateAsset(outputClip, outputAssetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            status = $"Retarget complete: {AssetDatabase.GetAssetPath(outputClip)} ({mode})";
            EditorGUIUtility.PingObject(outputClip);
        }
    }
}
