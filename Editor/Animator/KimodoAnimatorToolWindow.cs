using KimodoUnityMotionTools.Generation.Pipeline;
using KimodoUnityMotionTools.ProjectEditor.GenerationPipeline;
using KimodoUnityMotionTools.ProjectEditor.Manager;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor.AnimatorTooling
{
    public sealed class KimodoAnimatorToolWindow : EditorWindow
    {
        private const string MenuPath = "Tools/Kimodo/Animator/Kimodo Animator Tool";
        private const float MinLeftWidth = 360f;

        private KimodoPlayableClip workingClip;
        private SerializedObject clipSo;
        private string lastStatus = string.Empty;
        private string lastError = string.Empty;
        private bool isGenerating;
        private bool managerSubscribed;
        private AnimationClip generatedClipForPreview;
        private AnimationClip originalClipForPreview;
        private Avatar retargetAvatarForPreview;
        private GameObject originalPreviewInstance;
        private GameObject generatedPreviewInstance;
        private AnimatorStateTransition selectedTransition;
        private AnimatorState selectedState;
        private AnimatorState selectedFromState;
        private AnimatorController selectedController;
        private AnimatorStateMachine selectedStateMachine;
        private readonly KimodoAnimatorApplyService applyService = new KimodoAnimatorApplyService();
        private Vector2 rightScroll;
        private bool showOriginal = true;
        private bool showGenerated = true;
        private float spacingMultiplier = 0.6f;

        [MenuItem(MenuPath, priority = 110)]
        private static void OpenWindow()
        {
            KimodoAnimatorToolWindow window = GetWindow<KimodoAnimatorToolWindow>("Kimodo Animator Tool");
            window.minSize = new Vector2(1100f, 640f);
            window.Show();
        }

        private void OnEnable()
        {
            EnsureWorkingClip();
            ResolveSelectionContext();
            SubscribeManagerEvents();
        }

        private void OnDisable()
        {
            UnsubscribeManagerEvents();
            DestroyPreviewInstances();
        }

        private void OnSelectionChange()
        {
            ResolveSelectionContext();
            Repaint();
        }

        private void OnGUI()
        {
            EnsureWorkingClip();
            DrawToolbar();

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawLeftPreviewPane();
                DrawRightPanel();
            }

            if (!string.IsNullOrWhiteSpace(lastError))
            {
                EditorGUILayout.HelpBox(lastError, MessageType.Error);
            }
            else if (!string.IsNullOrWhiteSpace(lastStatus))
            {
                EditorGUILayout.HelpBox(lastStatus, MessageType.Info);
            }
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Selection Source: Selection.activeObject", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                showOriginal = GUILayout.Toggle(showOriginal, "Show Original", EditorStyles.toolbarButton);
                showGenerated = GUILayout.Toggle(showGenerated, "Show Generated", EditorStyles.toolbarButton);
                spacingMultiplier = EditorGUILayout.Slider(spacingMultiplier, 0.2f, 1.5f, GUILayout.Width(180f));
                if (GUILayout.Button("Reset Preview", EditorStyles.toolbarButton, GUILayout.Width(100f)))
                {
                    generatedClipForPreview = null;
                    lastStatus = "Generated preview cleared.";
                    lastError = string.Empty;
                }
            }
        }

        private void DrawLeftPreviewPane()
        {
            Rect leftRect = EditorGUILayout.GetControlRect(false, position.height - 70f, GUILayout.MinWidth(MinLeftWidth), GUILayout.ExpandWidth(true));
            GUI.Box(leftRect, GUIContent.none);
            Handles.BeginGUI();
            Handles.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            Handles.DrawLine(new Vector3(leftRect.xMax, leftRect.yMin), new Vector3(leftRect.xMax, leftRect.yMax));
            Handles.EndGUI();

            GUI.Label(new Rect(leftRect.x + 8f, leftRect.y + 6f, leftRect.width - 16f, 20f), "Preview (Original / Generated)", EditorStyles.boldLabel);
            string previewNote = originalClipForPreview == null
                ? "Preview wiring placeholder: select state/transition and generate clip."
                : $"Original: {originalClipForPreview.name}; Generated: {(generatedClipForPreview != null ? generatedClipForPreview.name : "(none)")}. Side-by-side preview renderer pending.";
            GUI.Label(new Rect(leftRect.x + 8f, leftRect.y + 26f, leftRect.width - 16f, 20f), previewNote, EditorStyles.miniLabel);
        }

        private void DrawRightPanel()
        {
            float width = Mathf.Max(420f, position.width * 0.46f);
            using (var scroll = new EditorGUILayout.ScrollViewScope(rightScroll, GUILayout.Width(width)))
            {
                rightScroll = scroll.scrollPosition;
                if (workingClip == null)
                {
                    EditorGUILayout.HelpBox("Failed to initialize working KimodoPlayableClip instance.", MessageType.Error);
                    return;
                }

                clipSo.UpdateIfRequiredOrScript();
                DrawSelectionInfo();
                DrawGeneratePanel();
                DrawBakePanel();
                DrawGeneratedPanel();
                DrawAnimationClipPanel();
                DrawApplyPanel();
                clipSo.ApplyModifiedProperties();
            }
        }

        private void DrawSelectionInfo()
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
                EditorGUILayout.HelpBox("Select AnimatorStateTransition or AnimatorState in Animator Controller inspector/graph.", MessageType.Warning);
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawGeneratePanel()
        {
            EditorGUILayout.LabelField("Generate Motion", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            DrawProp("generationBackend", "Backend");
            DrawProp("bridgeModelName", "Bridge Model");
            DrawProp("bridgeVramMode", "VRAM Mode");
            DrawProp("motionPrompt", "Prompt", true, 60f);
            DrawProp("generationFrames", "Duration (frames)");
            DrawProp("diffusionSteps", "Diffusion Steps");
            DrawProp("randomSeed", "Random");
            DrawProp("seed", "Seed");
            DrawProp("enableInbetweenInterpolation", "In-between Interpolation");
            DrawProp("showConstraint", "Show Constraint");
            DrawProp("workflowJsonAsset", "Workflow JSON Asset");

            bool canGenerate = !isGenerating && (selectedTransition != null || selectedState != null);
            EditorGUI.BeginDisabledGroup(!canGenerate);
            if (GUILayout.Button("Generate & Bake", GUILayout.Height(30f)))
            {
                StartGenerate();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!isGenerating);
            if (GUILayout.Button("Cancel", GUILayout.Height(24f)))
            {
                KimodoEditorCommandManager.Dispatch(new CancelPlayableClipGenerationCommand(workingClip));
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();
        }

        private void DrawBakePanel()
        {
            EditorGUILayout.LabelField("Animation Bake", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            DrawProp("autoRetargetOnBinding", "Auto Retarget On Binding");
            DrawProp("customRetargetAvatar", "Custom Avatar");
            DrawProp("curveFilterOptions", "Curve Filter Options", true);
            EditorGUILayout.EndVertical();
        }

        private void DrawGeneratedPanel()
        {
            EditorGUILayout.LabelField("Generated", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Generated Clip Preview", generatedClipForPreview, typeof(AnimationClip), false);
            }
            if (GUILayout.Button("Reset", GUILayout.Width(100)))
            {
                generatedClipForPreview = null;
                lastStatus = "Generated preview cleared.";
                lastError = string.Empty;
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawAnimationClipPanel()
        {
            EditorGUILayout.LabelField("Animation Clip", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            DrawProp("m_Clip", "Clip");
            DrawProp("m_ApplyFootIK", "Foot IK");
            DrawProp("m_Loop", "Loop");
            EditorGUILayout.EndVertical();
        }

        private void DrawApplyPanel()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Apply", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            bool canApply = generatedClipForPreview != null && !isGenerating && (selectedTransition != null || selectedState != null);
            EditorGUI.BeginDisabledGroup(!canApply);
            if (GUILayout.Button("Apply", GUILayout.Height(28f)))
            {
                ApplyGeneratedResult();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndVertical();
        }

        private void DrawProp(string propertyName, string label, bool multiLine = false, float height = 18f)
        {
            SerializedProperty p = clipSo.FindProperty(propertyName);
            if (p == null)
            {
                return;
            }
            if (multiLine && p.propertyType == SerializedPropertyType.String)
            {
                EditorGUILayout.LabelField(label);
                p.stringValue = EditorGUILayout.TextArea(p.stringValue ?? string.Empty, GUILayout.Height(height));
                return;
            }
            EditorGUILayout.PropertyField(p, new GUIContent(label), multiLine);
        }

        private void StartGenerate()
        {
            if (!TryBuildExternalConstraints(out string constraintsJson, out string error))
            {
                lastError = error;
                return;
            }

            bool accepted = KimodoEditorCommandManager.Dispatch(
                new GeneratePlayableClipCommand(
                    workingClip,
                    promptOverride: null,
                    externalConstraint: new KimodoExternalConstraintRequest
                    {
                        Enabled = true,
                        ConstraintsJson = constraintsJson,
                        RetargetAvatar = retargetAvatarForPreview
                    }));
            if (!accepted)
            {
                lastError = "Failed to dispatch generate command.";
                return;
            }

            isGenerating = true;
            lastError = string.Empty;
            lastStatus = "Queued generation...";
        }

        private bool TryBuildExternalConstraints(out string constraintsJson, out string error)
        {
            constraintsJson = string.Empty;
            error = string.Empty;

            if (!TryResolveAvatarAndMotion(out Avatar avatar, out AnimationClip sourceClip, out error))
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
            constraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(samples, clipStartSeconds: 0.0, clipDurationSeconds: end.sampleTime);
            return true;
        }

        private bool TrySampleAtNormalizedTime(
            Avatar avatar,
            AnimationClip clip,
            string modelName,
            double normalizedTime,
            out KimodoMarkerSampleResult sample,
            out string error)
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
            bool ok = KimodoMarkerSamplingUtility.TrySampleMarker(
                tempAnimator,
                tempAnimator.transform,
                sourceClip: null,
                modelName,
                globalTime,
                "fullbody",
                out sample,
                out error);

            if (tempRoot != null)
            {
                DestroyImmediate(tempRoot);
            }

            return ok;
        }

        private bool TryResolveAvatarAndMotion(out Avatar avatar, out AnimationClip sourceClip, out string error)
        {
            avatar = null;
            sourceClip = null;
            error = string.Empty;

            if (selectedState != null)
            {
                sourceClip = selectedState.motion as AnimationClip;
            }
            else if (selectedTransition != null)
            {
                AnimatorState from = selectedFromState != null ? selectedFromState : ResolveFromState(selectedStateMachine, selectedTransition);
                sourceClip = from != null ? from.motion as AnimationClip : null;
            }

            string modelName = workingClip != null ? workingClip.bridgeModelName : string.Empty;
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out avatar, out error))
            {
                return false;
            }

            if (sourceClip == null)
            {
                error = "Cannot resolve source AnimationClip from current selection.";
                return false;
            }

            originalClipForPreview = sourceClip;
            retargetAvatarForPreview = avatar;
            return true;
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
                DestroyImmediate(root);
                root = null;
                return null;
            }

            Transform samplingRoot = root.transform;
            Animator animator = samplingRoot.gameObject.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.enabled = false;
            animator.applyRootMotion = false;
            animator.Rebind();
            animator.Update(0f);
            return animator;
        }

        private void ApplyGeneratedResult()
        {
            if (generatedClipForPreview == null)
            {
                lastError = "No generated clip available for apply.";
                return;
            }

            string error;
            bool ok;
            if (selectedTransition != null)
            {
                AnimatorState from = selectedFromState != null ? selectedFromState : ResolveFromState(selectedStateMachine, selectedTransition);
                AnimatorState to = selectedTransition.destinationState;
                ok = applyService.TryApplyTransition(
                    new KimodoAnimatorApplyService.TransitionApplyContext
                    {
                        Controller = selectedController,
                        StateMachine = selectedStateMachine,
                        FromState = from,
                        ToState = to,
                        OriginalTransition = selectedTransition,
                        GeneratedClip = generatedClipForPreview,
                        NewStateName = $"{from?.name}_{to?.name}_KimodoInsert"
                    },
                    out error);
            }
            else if (selectedState != null)
            {
                ok = applyService.TryApplyState(
                    new KimodoAnimatorApplyService.StateApplyContext
                    {
                        Controller = selectedController,
                        State = selectedState,
                        GeneratedClip = generatedClipForPreview
                    },
                    out error);
            }
            else
            {
                ok = false;
                error = "No valid selection context for apply.";
            }

            if (!ok)
            {
                lastError = error;
                return;
            }

            lastError = string.Empty;
            lastStatus = "Apply succeeded. Assets marked dirty (not auto-saved).";
        }

        private void EnsureWorkingClip()
        {
            if (workingClip != null && clipSo != null)
            {
                return;
            }

            workingClip = CreateInstance<KimodoPlayableClip>();
            clipSo = new SerializedObject(workingClip);
        }

        private void ResolveSelectionContext()
        {
            selectedTransition = null;
            selectedState = null;
            selectedFromState = null;
            selectedController = null;
            selectedStateMachine = null;

            UnityEngine.Object obj = Selection.activeObject;
            if (obj is AnimatorStateTransition transition)
            {
                selectedTransition = transition;
                selectedController = FindControllerForObject(transition);
                selectedStateMachine = FindStateMachineForTransition(selectedController, transition, out selectedFromState);
                return;
            }

            if (obj is AnimatorState state)
            {
                selectedState = state;
                selectedController = FindControllerForObject(state);
                selectedStateMachine = FindStateMachineForState(selectedController, state);
            }
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

        private static AnimatorStateMachine FindStateMachineForTransition(
            AnimatorController controller,
            AnimatorStateTransition transition,
            out AnimatorState fromState)
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

        private static AnimatorState ResolveFromState(AnimatorStateMachine sm, AnimatorStateTransition transition)
        {
            if (sm == null || transition == null)
            {
                return null;
            }

            ChildAnimatorState[] states = sm.states;
            for (int i = 0; i < states.Length; i++)
            {
                AnimatorState s = states[i].state;
                AnimatorStateTransition[] ts = s.transitions;
                for (int j = 0; j < ts.Length; j++)
                {
                    if (ts[j] == transition)
                    {
                        return s;
                    }
                }
            }
            return null;
        }

        private void SubscribeManagerEvents()
        {
            if (managerSubscribed)
            {
                return;
            }

            KimodoEditorCommandManager.CommandProgress += OnCommandProgress;
            KimodoEditorCommandManager.CommandCompleted += OnCommandCompleted;
            KimodoEditorCommandManager.CommandFailed += OnCommandFailed;
            KimodoEditorCommandManager.CommandCanceled += OnCommandCanceled;
            managerSubscribed = true;
        }

        private void UnsubscribeManagerEvents()
        {
            if (!managerSubscribed)
            {
                return;
            }

            KimodoEditorCommandManager.CommandProgress -= OnCommandProgress;
            KimodoEditorCommandManager.CommandCompleted -= OnCommandCompleted;
            KimodoEditorCommandManager.CommandFailed -= OnCommandFailed;
            KimodoEditorCommandManager.CommandCanceled -= OnCommandCanceled;
            managerSubscribed = false;
        }

        private void OnCommandProgress(KimodoEditorCommandProgressEvent evt)
        {
            if (!IsCommandForWorkingClip(evt.Command))
            {
                return;
            }

            lastStatus = evt.Message;
            Repaint();
        }

        private void OnCommandCompleted(KimodoEditorCommandCompletedEvent evt)
        {
            if (!IsCommandForWorkingClip(evt.Command))
            {
                return;
            }

            isGenerating = false;
            if (evt.Payload is KimodoEditorGenerateResult gen)
            {
                generatedClipForPreview = gen.GeneratedClip;
                lastStatus = "Generation complete.";
                lastError = string.Empty;
            }

            Repaint();
        }

        private void OnCommandFailed(KimodoEditorCommandFailedEvent evt)
        {
            if (!IsCommandForWorkingClip(evt.Command))
            {
                return;
            }

            isGenerating = false;
            lastError = evt.Message;
            lastStatus = "Generation failed.";
            Repaint();
        }

        private void OnCommandCanceled(KimodoEditorCommandCanceledEvent evt)
        {
            if (!IsCommandForWorkingClip(evt.Command))
            {
                return;
            }

            isGenerating = false;
            lastStatus = "Generation canceled.";
            Repaint();
        }

        private bool IsCommandForWorkingClip(IKimodoEditorCommand command)
        {
            if (command == null || workingClip == null)
            {
                return false;
            }
            return string.Equals(command.TargetKey, "clip:" + workingClip.GetInstanceID(), StringComparison.Ordinal);
        }

        private void DestroyPreviewInstances()
        {
            if (originalPreviewInstance != null)
            {
                DestroyImmediate(originalPreviewInstance);
            }
            if (generatedPreviewInstance != null)
            {
                DestroyImmediate(generatedPreviewInstance);
            }
            originalPreviewInstance = null;
            generatedPreviewInstance = null;
        }
    }
}
