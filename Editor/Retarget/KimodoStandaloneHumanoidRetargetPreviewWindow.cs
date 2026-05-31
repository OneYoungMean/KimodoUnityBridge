using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor.Retarget
{
    public sealed class KimodoStandaloneHumanoidRetargetPreviewWindow : EditorWindow
    {
        private const string MenuPath = "Kimodo/Standalone Humanoid Retarget Preview";

        [SerializeField] private GameObject sourcePrefab;
        [SerializeField] private Avatar sourceAvatar;
        [SerializeField] private AnimationClip sourceClip;
        [SerializeField] private GameObject targetPrefab;
        [SerializeField] private Avatar targetAvatar;

        [SerializeField] private float previewTime;
        [SerializeField] private float playbackSpeed = 1f;
        [SerializeField] private bool playing;

        private GameObject sourceInstance;
        private GameObject targetInstance;
        private Animator sourceAnimator;
        private Animator targetAnimator;
        private RigSnapshot sourceRig;
        private RigSnapshot targetRig;
        private GameObject sourceVizRoot;
        private GameObject targetVizRoot;
        private bool previewReady;
        private string status;
        private string error;

        private static readonly HumanBodyBones[] BonesToUse =
        {
            HumanBodyBones.Neck,
            HumanBodyBones.Head,
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Chest,
            HumanBodyBones.UpperChest,
            HumanBodyBones.LeftShoulder,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.LeftToes,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot,
            HumanBodyBones.RightToes
        };

        [MenuItem(MenuPath, priority = 121)]
        private static void Open()
        {
            GetWindow<KimodoStandaloneHumanoidRetargetPreviewWindow>("Retarget Preview");
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
            EditorGUILayout.LabelField("Standalone Retarget Preview", EditorStyles.boldLabel);

            sourcePrefab = (GameObject)EditorGUILayout.ObjectField("Source Prefab", sourcePrefab, typeof(GameObject), false);
            sourceAvatar = (Avatar)EditorGUILayout.ObjectField("Source Avatar", sourceAvatar, typeof(Avatar), false);
            sourceClip = (AnimationClip)EditorGUILayout.ObjectField("Source Clip", sourceClip, typeof(AnimationClip), false);
            targetPrefab = (GameObject)EditorGUILayout.ObjectField("Target Prefab", targetPrefab, typeof(GameObject), false);
            targetAvatar = (Avatar)EditorGUILayout.ObjectField("Target Avatar", targetAvatar, typeof(Avatar), false);

            EditorGUILayout.Space(6f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Build Preview", GUILayout.Height(28f)))
                {
                    BuildPreview();
                }

                if (GUILayout.Button(playing ? "Pause" : "Play", GUILayout.Height(28f)))
                {
                    playing = !playing;
                    status = playing ? "Preview playing." : "Preview paused.";
                }

                if (GUILayout.Button("Step +1 Frame", GUILayout.Height(28f)))
                {
                    StepFrame(1f);
                }

                if (GUILayout.Button("Clear", GUILayout.Height(28f)))
                {
                    ClearPreview();
                }
            }

            using (new EditorGUI.DisabledScope(!previewReady))
            {
                float duration = Mathf.Max(0.0001f, sourceClip != null ? sourceClip.length : 1f);
                float nextTime = EditorGUILayout.Slider("Preview Time", previewTime, 0f, duration);
                if (!Mathf.Approximately(nextTime, previewTime))
                {
                    previewTime = nextTime;
                    UpdatePreviewPose();
                }

                playbackSpeed = EditorGUILayout.FloatField("Playback Speed", playbackSpeed);
            }

            EditorGUILayout.Space(4f);

            if (previewReady)
            {
                EditorGUILayout.HelpBox("Source and target instances are placed in the scene for direct bone-level inspection.", MessageType.Info);
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }
            else if (!string.IsNullOrWhiteSpace(status))
            {
                EditorGUILayout.HelpBox(status, MessageType.Info);
            }
        }

        private void OnEditorUpdate()
        {
            if (!playing || !previewReady || sourceClip == null)
            {
                return;
            }

            float duration = Mathf.Max(0.0001f, sourceClip.length);
            previewTime += Time.deltaTime * Mathf.Max(0f, playbackSpeed);
            if (previewTime > duration)
            {
                previewTime = Mathf.Repeat(previewTime, duration);
            }

            UpdatePreviewPose();
            SceneView.RepaintAll();
            Repaint();
        }

        private void BuildPreview()
        {
            error = string.Empty;
            status = string.Empty;

            if (sourcePrefab == null || targetPrefab == null)
            {
                error = "Source prefab or target prefab is null.";
                return;
            }

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return;
            }

            if (!KimodoStandaloneHumanoidRetargetPreviewUtility.TryResolveAvatar(sourcePrefab, sourceAvatar, out Avatar resolvedSourceAvatar, out string sourceAvatarError))
            {
                error = $"Source avatar resolve failed: {sourceAvatarError}";
                return;
            }

            if (!KimodoStandaloneHumanoidRetargetPreviewUtility.TryResolveAvatar(targetPrefab, targetAvatar, out Avatar resolvedTargetAvatar, out string targetAvatarError))
            {
                error = $"Target avatar resolve failed: {targetAvatarError}";
                return;
            }

            ClearPreview();

            sourceInstance = KimodoStandaloneHumanoidRetargetPreviewUtility.CreatePreviewInstance(sourcePrefab, "KimodoPreviewSourceInstance");
            targetInstance = KimodoStandaloneHumanoidRetargetPreviewUtility.CreatePreviewInstance(targetPrefab, "KimodoPreviewTargetInstance");

            if (sourceInstance == null || targetInstance == null)
            {
                ClearPreview();
                error = "Failed to instantiate preview prefabs.";
                return;
            }

            sourceInstance.transform.position = new Vector3(-1.5f, 0f, 0f);
            targetInstance.transform.position = new Vector3(1.5f, 0f, 0f);

            sourceAnimator = KimodoStandaloneHumanoidRetargetPreviewUtility.EnsurePreviewAnimator(sourceInstance, resolvedSourceAvatar, out string sourceAnimatorError);
            if (sourceAnimator == null)
            {
                ClearPreview();
                error = $"Source animator setup failed: {sourceAnimatorError}";
                return;
            }

            targetAnimator = KimodoStandaloneHumanoidRetargetPreviewUtility.EnsurePreviewAnimator(targetInstance, resolvedTargetAvatar, out string targetAnimatorError);
            if (targetAnimator == null)
            {
                ClearPreview();
                error = $"Target animator setup failed: {targetAnimatorError}";
                return;
            }

            if (!TryBuildRigSnapshot(sourceAnimator, out sourceRig, out string sourceRigError))
            {
                ClearPreview();
                error = $"Source rig setup failed: {sourceRigError}";
                return;
            }

            if (!TryBuildRigSnapshot(targetAnimator, out targetRig, out string targetRigError))
            {
                ClearPreview();
                error = $"Target rig setup failed: {targetRigError}";
                return;
            }

            sourceVizRoot = KimodoStandaloneSkeletonVisualizerUtility.CreateSkeletonVisualization(sourceAnimator.transform, "KimodoPreviewSourceSkeleton");
            targetVizRoot = KimodoStandaloneSkeletonVisualizerUtility.CreateSkeletonVisualization(targetAnimator.transform, "KimodoPreviewTargetSkeleton");

            previewReady = true;
            previewTime = 0f;
            playing = false;
            UpdatePreviewPose();
            status = "Preview built.";
        }

        private void StepFrame(float frameDelta)
        {
            if (!previewReady || sourceClip == null)
            {
                return;
            }

            float frameRate = sourceClip.frameRate > 0f ? sourceClip.frameRate : 30f;
            previewTime = Mathf.Clamp(previewTime + frameDelta / frameRate, 0f, sourceClip.length);
            UpdatePreviewPose();
        }

        private void UpdatePreviewPose()
        {
            if (!previewReady || sourceClip == null || sourceInstance == null || targetInstance == null)
            {
                return;
            }

            sourceClip.SampleAnimation(sourceInstance, previewTime);
            ApplyRuntimeRetargetPose(sourceRig, targetRig);
            KimodoStandaloneSkeletonVisualizerUtility.SyncSkeletonVisualization(sourceAnimator.transform, sourceVizRoot != null ? sourceVizRoot.transform : null);
            KimodoStandaloneSkeletonVisualizerUtility.SyncSkeletonVisualization(targetAnimator.transform, targetVizRoot != null ? targetVizRoot.transform : null);
            SceneView.RepaintAll();
            Repaint();
        }

        private void ClearPreview()
        {
            playing = false;
            previewReady = false;
            previewTime = 0f;
            sourceRig = null;
            targetRig = null;

            if (sourceInstance != null)
            {
                DestroyImmediate(sourceInstance);
                sourceInstance = null;
            }

            if (targetInstance != null)
            {
                DestroyImmediate(targetInstance);
                targetInstance = null;
            }

            if (sourceVizRoot != null)
            {
                DestroyImmediate(sourceVizRoot);
                sourceVizRoot = null;
            }

            if (targetVizRoot != null)
            {
                DestroyImmediate(targetVizRoot);
                targetVizRoot = null;
            }

            sourceAnimator = null;
            targetAnimator = null;
        }

        private static bool TryBuildRigSnapshot(Animator animator, out RigSnapshot rig, out string error)
        {
            rig = new RigSnapshot();
            error = string.Empty;

            if (animator == null)
            {
                error = "Animator is null.";
                return false;
            }

            rig.ModelRoot = animator.transform;
            rig.ModelInitialWorldRotation = animator.transform.rotation;
            rig.Bones = new Transform[BonesToUse.Length];
            rig.InitialWorldRotations = new Quaternion[BonesToUse.Length];

            for (int i = 0; i < BonesToUse.Length; i++)
            {
                Transform bone = animator.GetBoneTransform(BonesToUse[i]);
                rig.Bones[i] = bone;
                rig.InitialWorldRotations[i] = bone != null ? bone.rotation : Quaternion.identity;
            }

            rig.Hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (rig.Hips == null)
            {
                error = "Hips bone is missing.";
                return false;
            }

            rig.HipsInitialLocalPosition = rig.Hips.localPosition;
            return true;
        }

        private static void ApplyRuntimeRetargetPose(RigSnapshot source, RigSnapshot target)
        {
            if (source == null || target == null)
            {
                return;
            }

            for (int i = 0; i < BonesToUse.Length; i++)
            {
                Transform srcBone = source.Bones[i];
                Transform dstBone = target.Bones[i];
                if (srcBone == null || dstBone == null)
                {
                    continue;
                }

                dstBone.rotation = target.ModelInitialWorldRotation;
                dstBone.rotation *= srcBone.rotation * Quaternion.Inverse(source.InitialWorldRotations[i]);
                dstBone.rotation *= target.InitialWorldRotations[i];
            }

            if (source.Hips != null && target.Hips != null)
            {
                target.Hips.localPosition = (source.Hips.localPosition - source.HipsInitialLocalPosition) + target.HipsInitialLocalPosition;
            }
        }

        private sealed class RigSnapshot
        {
            public Transform ModelRoot;
            public Quaternion ModelInitialWorldRotation;
            public Transform[] Bones;
            public Quaternion[] InitialWorldRotations;
            public Transform Hips;
            public Vector3 HipsInitialLocalPosition;
        }
    }

    internal static class KimodoStandaloneHumanoidRetargetPreviewUtility
    {
        public static bool TryResolveAvatar(GameObject prefab, Avatar explicitAvatar, out Avatar avatar, out string error)
        {
            avatar = null;
            error = string.Empty;

            if (explicitAvatar != null && explicitAvatar.isValid && explicitAvatar.isHuman)
            {
                avatar = explicitAvatar;
                return true;
            }

            if (prefab == null)
            {
                error = "Prefab is null.";
                return false;
            }

            Animator animator = prefab.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman)
            {
                avatar = animator.avatar;
                return true;
            }

            string[] guids = AssetDatabase.FindAssets($"t:Avatar {prefab.name}");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                Avatar candidate = AssetDatabase.LoadAssetAtPath<Avatar>(path);
                if (candidate != null && candidate.isValid && candidate.isHuman && string.Equals(candidate.name, prefab.name, StringComparison.Ordinal))
                {
                    avatar = candidate;
                    return true;
                }
            }

            error = $"Could not resolve humanoid avatar for prefab '{prefab.name}'.";
            return false;
        }

        public static GameObject CreatePreviewInstance(GameObject prefabOrSceneObject, string name)
        {
            if (prefabOrSceneObject == null)
            {
                return null;
            }

            GameObject instance = PrefabUtility.IsPartOfPrefabAsset(prefabOrSceneObject)
                ? (GameObject)PrefabUtility.InstantiatePrefab(prefabOrSceneObject)
                : UnityEngine.Object.Instantiate(prefabOrSceneObject);

            if (instance == null)
            {
                return null;
            }

            instance.name = name;
            instance.hideFlags = HideFlags.None;
            return instance;
        }

        public static Animator EnsurePreviewAnimator(GameObject root, Avatar avatar, out string error)
        {
            error = string.Empty;
            if (root == null)
            {
                error = "Root is null.";
                return null;
            }

            Animator animator = root.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                animator = root.AddComponent<Animator>();
            }

            animator.avatar = avatar;
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.enabled = true;
            animator.Rebind();
            animator.Update(0f);
            animator.enabled = false;
            return animator;
        }
    }
}
