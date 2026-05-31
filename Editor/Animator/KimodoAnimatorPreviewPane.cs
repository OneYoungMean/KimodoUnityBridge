using KimodoUnityMotionTools.Generation.Pipeline;
using KimodoUnityMotionTools.ProjectEditor.GenerationPipeline;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor.AnimatorTooling
{
    internal sealed class KimodoAnimatorPreviewPane : IDisposable
    {
        private const float MinLeftWidth = 360f;

        private enum PreviewMode
        {
            Original = 0,
            Generated = 1
        }

        private PreviewMode previewMode = PreviewMode.Original;
        private readonly List<AnimationClip> generatedPreviewHistory = new List<AnimationClip>();
        private int generatedPreviewIndex = -1;
        private AnimationClip generatedClipForPreview;
        private AnimationClip originalClipForPreview;
        private Avatar retargetAvatarForPreview;
        private GameObject previewSourceTemplate;
        private GameObject originalPreviewInstance;
        private GameObject generatedPreviewInstance;
        private KimodoAvatarPreviewCore avatarPreviewCore;
        private float transitionPreRollSeconds = 0.3f;
        private float transitionPostRollSeconds = 0.5f;

        private bool selectionLatched;
        private int latchedSelectionInstanceId;
        private AnimatorStateTransition selectedTransition;
        private AnimatorState selectedState;
        private AnimatorState selectedFromState;
        private AnimatorController selectedController;
        private AnimatorStateMachine selectedStateMachine;

        public void Initialize()
        {
            avatarPreviewCore = new KimodoAvatarPreviewCore();
            avatarPreviewCore.SetTransitionWindowPadding(transitionPreRollSeconds, transitionPostRollSeconds);
            TryCaptureSelectionIfNeeded();
            RefreshPreviewSource();
        }

        public void Dispose()
        {
            DestroyPreviewInstances();
            if (previewSourceTemplate != null)
            {
                UnityEngine.Object.DestroyImmediate(previewSourceTemplate);
                previewSourceTemplate = null;
            }
            avatarPreviewCore?.Dispose();
            avatarPreviewCore = null;
        }

        public void OnSelectionChange()
        {
            if (selectionLatched)
            {
                return;
            }

            if (TryCaptureSelectionIfNeeded())
            {
                RefreshPreviewSource();
            }
        }

        public bool HasSelection => selectedTransition != null || selectedState != null;
        public AnimationClip GeneratedClipForPreview => generatedClipForPreview;
        public Avatar RetargetAvatarForPreview => retargetAvatarForPreview;
        public AnimatorStateTransition SelectedTransition => selectedTransition;
        public AnimatorState SelectedState => selectedState;
        public AnimatorState SelectedFromState => selectedFromState;
        public AnimatorStateMachine SelectedStateMachine => selectedStateMachine;

        public void Tick()
        {
            avatarPreviewCore?.Tick();
        }

        public void DrawToolbar(ref string lastStatus, ref string lastError, Action onResetAll)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Selection Source: Selection.activeObject", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                bool wantOriginal = GUILayout.Toggle(previewMode == PreviewMode.Original, "Show Original", EditorStyles.toolbarButton);
                bool wantGenerated = GUILayout.Toggle(previewMode == PreviewMode.Generated, "Show Generated", EditorStyles.toolbarButton);
                if (wantOriginal && previewMode != PreviewMode.Original)
                {
                    previewMode = PreviewMode.Original;
                    avatarPreviewCore?.RestartFromZeroAndPlay();
                }
                else if (wantGenerated && previewMode != PreviewMode.Generated)
                {
                    previewMode = PreviewMode.Generated;
                    avatarPreviewCore?.RestartFromZeroAndPlay();
                }

                if (GUILayout.Button("Reselect", EditorStyles.toolbarButton, GUILayout.Width(100f)))
                {
                    ResetAll();
                    onResetAll?.Invoke();
                    lastStatus = string.Empty;
                    lastError = string.Empty;
                }

                GUILayout.Space(8f);
                GUILayout.Label("Pre(s)", EditorStyles.miniLabel, GUILayout.Width(38f));
                float newPre = EditorGUILayout.FloatField(transitionPreRollSeconds, EditorStyles.toolbarTextField, GUILayout.Width(50f));
                GUILayout.Label("Post(s)", EditorStyles.miniLabel, GUILayout.Width(44f));
                float newPost = EditorGUILayout.FloatField(transitionPostRollSeconds, EditorStyles.toolbarTextField, GUILayout.Width(50f));
                if (!Mathf.Approximately(newPre, transitionPreRollSeconds) || !Mathf.Approximately(newPost, transitionPostRollSeconds))
                {
                    transitionPreRollSeconds = Mathf.Max(0f, newPre);
                    transitionPostRollSeconds = Mathf.Max(0f, newPost);
                    avatarPreviewCore?.SetTransitionWindowPadding(transitionPreRollSeconds, transitionPostRollSeconds);
                    avatarPreviewCore?.RestartFromZeroAndPlay();
                }

                EditorGUI.BeginDisabledGroup(generatedPreviewHistory.Count == 0);
                if (GUILayout.Button("<", EditorStyles.toolbarButton, GUILayout.Width(24f)))
                {
                    generatedPreviewIndex = Mathf.Max(0, generatedPreviewIndex - 1);
                    SetGeneratedPreviewByIndex();
                }
                GUILayout.Label(generatedPreviewHistory.Count == 0 ? "Gen 0/0" : $"Gen {generatedPreviewIndex + 1}/{generatedPreviewHistory.Count}", EditorStyles.miniLabel, GUILayout.Width(84f));
                if (GUILayout.Button(">", EditorStyles.toolbarButton, GUILayout.Width(24f)))
                {
                    generatedPreviewIndex = Mathf.Min(generatedPreviewHistory.Count - 1, generatedPreviewIndex + 1);
                    SetGeneratedPreviewByIndex();
                }
                EditorGUI.EndDisabledGroup();
            }
        }

        public void DrawPreviewPane(float windowHeight)
        {
            Rect leftRect = EditorGUILayout.GetControlRect(false, windowHeight - 70f, GUILayout.MinWidth(MinLeftWidth), GUILayout.ExpandWidth(true));
            GUI.Box(leftRect, GUIContent.none);
            Handles.BeginGUI();
            Handles.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            Handles.DrawLine(new Vector3(leftRect.xMax, leftRect.yMin), new Vector3(leftRect.xMax, leftRect.yMax));
            Handles.EndGUI();

            Rect renderRect = new Rect(leftRect.x + 8f, leftRect.y + 6f, leftRect.width - 16f, leftRect.height - 14f);
            if (avatarPreviewCore == null)
            {
                avatarPreviewCore = new KimodoAvatarPreviewCore();
                avatarPreviewCore.SetTransitionWindowPadding(transitionPreRollSeconds, transitionPostRollSeconds);
            }

            if (previewMode == PreviewMode.Generated)
            {
                avatarPreviewCore.SetClipPreview(generatedPreviewInstance, generatedClipForPreview, generatedClipForPreview == null ? "No generated animation." : "Generated preview unavailable.");
            }
            else if (selectedTransition != null)
            {
                AnimationClip fromClip = selectedFromState != null ? selectedFromState.motion as AnimationClip : null;
                AnimationClip toClip = selectedTransition.destinationState != null ? selectedTransition.destinationState.motion as AnimationClip : null;
                if (fromClip != null && toClip != null)
                {
                    avatarPreviewCore.SetTransitionPreview(originalPreviewInstance, fromClip, toClip, selectedTransition, "No transition animation.");
                }
                else
                {
                    avatarPreviewCore.SetClipPreview(originalPreviewInstance, null, "Transition preview requires clips.");
                }
            }
            else
            {
                avatarPreviewCore.SetClipPreview(originalPreviewInstance, originalClipForPreview, originalClipForPreview == null ? "No original animation." : "Original preview unavailable.");
            }

            avatarPreviewCore.Draw(renderRect);
        }

        public void OnGenerateSuccess(AnimationClip clip)
        {
            generatedClipForPreview = clip;
            if (clip != null)
            {
                generatedPreviewHistory.Add(clip);
                generatedPreviewIndex = generatedPreviewHistory.Count - 1;
            }
            previewMode = PreviewMode.Generated;
            BuildOrRefreshPreviewInstances();
            avatarPreviewCore?.RestartFromZeroAndPlay();
        }

        public void OnGenerateFailedOrCanceled()
        {
            generatedClipForPreview = null;
        }

        public void ResetGeneratedOnly()
        {
            generatedClipForPreview = null;
            generatedPreviewHistory.Clear();
            generatedPreviewIndex = -1;
            previewMode = PreviewMode.Original;
        }

        public void ResetAll()
        {
            ResetGeneratedOnly();
            ClearSelectionLatch();
            DestroyPreviewInstances();
            avatarPreviewCore?.Dispose();
            avatarPreviewCore = new KimodoAvatarPreviewCore();
            avatarPreviewCore.SetTransitionWindowPadding(transitionPreRollSeconds, transitionPostRollSeconds);
            originalClipForPreview = null;
            retargetAvatarForPreview = null;

            if (TryCaptureSelectionIfNeeded())
            {
                RefreshPreviewSource();
            }
        }

        public void DrawSelectionInfo()
        {
            EditorGUILayout.LabelField("Selection Context", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            if (selectedTransition != null)
            {
                EditorGUILayout.LabelField("Mode: Transition");
                EditorGUILayout.ObjectField("Transition", selectedTransition, typeof(AnimatorStateTransition), false);
                EditorGUILayout.ObjectField("From", selectedFromState, typeof(AnimatorState), false);
                EditorGUILayout.ObjectField("To", selectedTransition.destinationState, typeof(AnimatorState), false);
            }
            else if (selectedState != null)
            {
                EditorGUILayout.LabelField("Mode: State");
                EditorGUILayout.ObjectField("State", selectedState, typeof(AnimatorState), false);
            }
            else
            {
                EditorGUILayout.HelpBox("Select AnimatorStateTransition or AnimatorState.", MessageType.Warning);
            }
            EditorGUILayout.EndVertical();
        }

        public bool TryBuildExternalConstraints(KimodoPlayableClip workingClip, out string constraintsJson, out string error)
        {
            constraintsJson = string.Empty;
            error = string.Empty;

            if (!TryResolveAvatarAndMotionForSampling(workingClip, out Avatar avatar, out AnimationClip sourceClip, out error))
            {
                return false;
            }

            string modelName = workingClip != null ? workingClip.bridgeModelName : "Kimodo-SOMA-RP-v1";
            var samples = new List<KimodoMarkerSampleResult>(2);
            if (!TrySampleAtNormalizedTime(avatar, sourceClip, modelName, 0.0, out KimodoMarkerSampleResult begin, out error))
            {
                return false;
            }
            if (!TrySampleAtNormalizedTime(avatar, sourceClip, modelName, 1.0, out KimodoMarkerSampleResult end, out error))
            {
                return false;
            }

            begin.constraintType = "fullbody";
            end.constraintType = "fullbody";
            begin.sampleTime = 0.0;
            end.sampleTime = Mathf.Max(1, workingClip.generationFrames) / 30.0;
            samples.Add(begin);
            samples.Add(end);
            constraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(samples, 0.0, end.sampleTime);
            return true;
        }

        private bool TryCaptureSelectionIfNeeded()
        {
            if (selectionLatched)
            {
                return false;
            }

            UnityEngine.Object obj = Selection.activeObject;
            if (obj == null)
            {
                return false;
            }

            if (obj is AnimatorStateTransition transition)
            {
                selectedTransition = transition;
                selectedState = null;
                selectedController = FindControllerForObject(transition);
                selectedStateMachine = FindStateMachineForTransition(selectedController, transition, out selectedFromState);
                selectionLatched = true;
                latchedSelectionInstanceId = obj.GetInstanceID();
                return true;
            }

            if (obj is AnimatorState state)
            {
                selectedState = state;
                selectedTransition = null;
                selectedFromState = null;
                selectedController = FindControllerForObject(state);
                selectedStateMachine = FindStateMachineForState(selectedController, state);
                selectionLatched = true;
                latchedSelectionInstanceId = obj.GetInstanceID();
                return true;
            }

            return false;
        }

        private void ClearSelectionLatch()
        {
            selectionLatched = false;
            latchedSelectionInstanceId = 0;
            selectedTransition = null;
            selectedState = null;
            selectedFromState = null;
            selectedController = null;
            selectedStateMachine = null;
        }

        private void RefreshPreviewSource()
        {
            if (!HasSelection)
            {
                DestroyPreviewInstances();
                return;
            }

            if (TryResolveAvatarAndMotionForSampling(null, out _, out _, out _))
            {
                BuildOrRefreshPreviewInstances();
            }
            else
            {
                DestroyPreviewInstances();
            }
        }

        private bool TryResolveAvatarAndMotionForSampling(KimodoPlayableClip workingClip, out Avatar avatar, out AnimationClip sourceClip, out string error)
        {
            avatar = null;
            sourceClip = null;
            error = string.Empty;

            if (selectedState != null)
            {
                sourceClip = selectedState.motion as AnimationClip;
                if (sourceClip == null)
                {
                    error = "State motion is not an AnimationClip.";
                    return false;
                }
                if (!TryPreparePreviewSource(sourceClip, workingClip, out avatar, out error))
                {
                    return false;
                }
            }
            else if (selectedTransition != null)
            {
                AnimatorState from = selectedFromState;
                AnimatorState to = selectedTransition.destinationState;
                AnimationClip fromClip = from != null ? from.motion as AnimationClip : null;
                AnimationClip toClip = to != null ? to.motion as AnimationClip : null;
                if (fromClip == null || toClip == null)
                {
                    error = "Transition preview requires from/to clips.";
                    return false;
                }
                sourceClip = fromClip;
                if (!TryPreparePreviewSource(sourceClip, workingClip, out avatar, out error))
                {
                    return false;
                }
            }

            originalClipForPreview = sourceClip;
            retargetAvatarForPreview = avatar;
            return true;
        }

        private static bool TrySampleAtNormalizedTime(Avatar avatar, AnimationClip clip, string modelName, double normalizedTime, out KimodoMarkerSampleResult sample, out string error)
        {
            sample = null;
            error = string.Empty;
            if (avatar == null || clip == null)
            {
                error = "Avatar or source clip is null.";
                return false;
            }

            double duration = Math.Max(0.001, clip.length);
            double globalTime = Mathf.Clamp01((float)normalizedTime) * duration;
            Animator tempAnimator = CreateSamplingAnimatorFromAvatar(avatar, out GameObject tempRoot, out error);
            if (tempAnimator == null)
            {
                return false;
            }

            clip.SampleAnimation(tempAnimator.gameObject, (float)globalTime);
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar originAvatar, out string originError))
            {
                error = $"Resolve origin avatar failed: {originError}";
                if (tempRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(tempRoot);
                }
                return false;
            }

            bool ok = KimodoMarkerSamplingUtility.TrySampleMarker(
                tempAnimator,
                tempAnimator.transform,
                null,
                modelName,
                globalTime,
                "fullbody",
                originAvatar,
                avatar,
                out sample,
                out error);
            if (tempRoot != null)
            {
                UnityEngine.Object.DestroyImmediate(tempRoot);
            }

            return ok;
        }

        private static Animator CreateSamplingAnimatorFromAvatar(Avatar avatar, out GameObject root, out string error)
        {
            root = null;
            error = string.Empty;
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                error = "Sampling avatar is null or invalid humanoid avatar.";
                return null;
            }

            root = new GameObject("KimodoAnimatorToolSamplingRoot");
            root.hideFlags = HideFlags.HideAndDontSave;
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(avatar, root.transform, out error))
            {
                UnityEngine.Object.DestroyImmediate(root);
                root = null;
                return null;
            }

            Animator animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.enabled = false;
            animator.Rebind();
            animator.Update(0f);
            return animator;
        }

        private void BuildOrRefreshPreviewInstances()
        {
            DestroyPreviewInstances();
            if (previewSourceTemplate == null)
            {
                return;
            }

            if (retargetAvatarForPreview == null || !retargetAvatarForPreview.isValid || !retargetAvatarForPreview.isHuman)
            {
                return;
            }

            originalPreviewInstance = CreatePreviewInstance("KimodoPreviewOriginal");
            generatedPreviewInstance = CreatePreviewInstance("KimodoPreviewGenerated");
        }

        private void SetGeneratedPreviewByIndex()
        {
            if (generatedPreviewHistory.Count == 0)
            {
                generatedClipForPreview = null;
                return;
            }

            generatedPreviewIndex = Mathf.Clamp(generatedPreviewIndex, 0, generatedPreviewHistory.Count - 1);
            generatedClipForPreview = generatedPreviewHistory[generatedPreviewIndex];
            previewMode = PreviewMode.Generated;
            BuildOrRefreshPreviewInstances();
            avatarPreviewCore?.RestartFromZeroAndPlay();
        }

        private GameObject CreatePreviewInstance(string name)
        {
            if (previewSourceTemplate == null)
            {
                return null;
            }

            GameObject root = UnityEngine.Object.Instantiate(previewSourceTemplate);
            root.name = name;
            root.hideFlags = HideFlags.HideAndDontSave;
            Animator animator = root.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                UnityEngine.Object.DestroyImmediate(root);
                return null;
            }

            animator.runtimeAnimatorController = null;
            animator.avatar = retargetAvatarForPreview;
            animator.enabled = false;
            animator.applyRootMotion = false;
            animator.Rebind();
            animator.Update(0f);
            return root;
        }

        private bool TryPreparePreviewSource(AnimationClip sourceClip, KimodoPlayableClip workingClip, out Avatar avatar, out string error)
        {
            avatar = null;
            error = string.Empty;
            if (previewSourceTemplate != null)
            {
                UnityEngine.Object.DestroyImmediate(previewSourceTemplate);
                previewSourceTemplate = null;
            }

            GameObject sourceRoot = FindScenePreviewSourceByController(selectedController);
            if (sourceRoot == null)
            {
                sourceRoot = LoadClipOwnerModelAsset(sourceClip);
            }

            if (sourceRoot == null)
            {
                string modelName = workingClip != null ? workingClip.bridgeModelName : string.Empty;
                if (KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar fallbackAvatar, out _)
                    && fallbackAvatar != null && fallbackAvatar.isValid && fallbackAvatar.isHuman)
                {
                    sourceRoot = BuildSkeletonTemplateFromAvatar(fallbackAvatar);
                }
            }

            if (sourceRoot == null)
            {
                error = "Cannot resolve preview character source.";
                return false;
            }

            previewSourceTemplate = UnityEngine.Object.Instantiate(sourceRoot);
            previewSourceTemplate.name = "KimodoPreviewTemplate";
            previewSourceTemplate.hideFlags = HideFlags.HideAndDontSave;

            Animator animator = previewSourceTemplate.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                animator = previewSourceTemplate.AddComponent<Animator>();
            }

            if (!KimodoLocalAvatarUtility.TryEnsureHumanoidAvatar(previewSourceTemplate, out avatar, out _, out error))
            {
                UnityEngine.Object.DestroyImmediate(previewSourceTemplate);
                previewSourceTemplate = null;
                return false;
            }

            animator.avatar = avatar;
            animator.enabled = false;
            animator.applyRootMotion = false;
            animator.Rebind();
            animator.Update(0f);
            return true;
        }

        private static AnimatorController FindControllerForObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return null;
            }
            string path = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        }

        private static AnimatorStateMachine FindStateMachineForState(AnimatorController controller, AnimatorState state)
        {
            if (controller == null || state == null)
            {
                return null;
            }

            for (int i = 0; i < controller.layers.Length; i++)
            {
                AnimatorStateMachine sm = controller.layers[i].stateMachine;
                ChildAnimatorState[] states = sm.states;
                for (int j = 0; j < states.Length; j++)
                {
                    if (states[j].state == state)
                    {
                        return sm;
                    }
                }
            }
            return null;
        }

        private static AnimatorStateMachine FindStateMachineForTransition(AnimatorController controller, AnimatorStateTransition transition, out AnimatorState fromState)
        {
            fromState = null;
            if (controller == null || transition == null)
            {
                return null;
            }

            for (int i = 0; i < controller.layers.Length; i++)
            {
                AnimatorStateMachine sm = controller.layers[i].stateMachine;
                ChildAnimatorState[] states = sm.states;
                for (int j = 0; j < states.Length; j++)
                {
                    AnimatorState s = states[j].state;
                    AnimatorStateTransition[] transitions = s.transitions;
                    for (int k = 0; k < transitions.Length; k++)
                    {
                        if (transitions[k] == transition)
                        {
                            fromState = s;
                            return sm;
                        }
                    }
                }
            }
            return null;
        }

        private static GameObject FindScenePreviewSourceByController(AnimatorController controller)
        {
            if (controller == null)
            {
                return null;
            }

            Animator[] animators = UnityEngine.Object.FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < animators.Length; i++)
            {
                Animator a = animators[i];
                if (a == null || a.runtimeAnimatorController == null)
                {
                    continue;
                }

                if (ReferenceEquals(a.runtimeAnimatorController, controller))
                {
                    return a.gameObject;
                }

                AnimatorOverrideController overrideController = a.runtimeAnimatorController as AnimatorOverrideController;
                if (overrideController != null && ReferenceEquals(overrideController.runtimeAnimatorController, controller))
                {
                    return a.gameObject;
                }
            }

            return null;
        }

        private static GameObject LoadClipOwnerModelAsset(AnimationClip clip)
        {
            if (clip == null)
            {
                return null;
            }

            string clipPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrWhiteSpace(clipPath))
            {
                return null;
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(clipPath);
        }

        private static GameObject BuildSkeletonTemplateFromAvatar(Avatar avatar)
        {
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                return null;
            }

            GameObject root = new GameObject("KimodoPreviewSkeletonTemplate");
            root.hideFlags = HideFlags.HideAndDontSave;
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(avatar, root.transform, out _))
            {
                UnityEngine.Object.DestroyImmediate(root);
                return null;
            }

            Animator animator = root.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                animator = root.AddComponent<Animator>();
            }
            animator.avatar = avatar;
            animator.enabled = false;
            animator.applyRootMotion = false;
            animator.Rebind();
            animator.Update(0f);
            return root;
        }

        private void DestroyPreviewInstances()
        {
            if (originalPreviewInstance != null)
            {
                UnityEngine.Object.DestroyImmediate(originalPreviewInstance);
            }
            if (generatedPreviewInstance != null)
            {
                UnityEngine.Object.DestroyImmediate(generatedPreviewInstance);
            }
            originalPreviewInstance = null;
            generatedPreviewInstance = null;
        }
    }
}
