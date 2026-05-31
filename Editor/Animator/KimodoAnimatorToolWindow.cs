using KimodoUnityMotionTools.Generation.Pipeline;
using KimodoUnityMotionTools.ProjectEditor.GenerationPipeline;
using KimodoUnityMotionTools.ProjectEditor.Manager;
using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor.AnimatorTooling
{
    public sealed class KimodoAnimatorToolWindow : EditorWindow
    {
        private const string MenuPath = "Kimodo/Kimodo Animator Tool";

        private KimodoPlayableClip workingClip;
        private SerializedObject clipSo;
        private string lastStatus = string.Empty;
        private string lastError = string.Empty;
        private bool isGenerating;
        private bool managerSubscribed;
        private AnimationClip lastSuccessfulGeneratedClipForApply;
        private readonly KimodoAnimatorApplyService applyService = new KimodoAnimatorApplyService();
        private KimodoAnimatorPreviewPane previewPane;
        private KimodoAnimatorEditorPane editorPane;
        private double lastPreviewRepaintTime;

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
            previewPane = new KimodoAnimatorPreviewPane();
            previewPane.Initialize();
            editorPane = new KimodoAnimatorEditorPane();
            SubscribeManagerEvents();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            UnsubscribeManagerEvents();
            previewPane?.Dispose();
            previewPane = null;
            editorPane = null;
        }

        private void OnSelectionChange()
        {
            previewPane?.OnSelectionChange();
            Repaint();
        }

        private void OnInspectorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - lastPreviewRepaintTime >= 0.2d)
            {
                lastPreviewRepaintTime = now;
                Repaint();
            }
        }

        private void OnEditorUpdate()
        {
            previewPane?.Tick();
            Repaint();
        }

        private void OnGUI()
        {
            EnsureWorkingClip();
            if (previewPane == null)
            {
                previewPane = new KimodoAnimatorPreviewPane();
                previewPane.Initialize();
            }
            if (editorPane == null)
            {
                editorPane = new KimodoAnimatorEditorPane();
            }

            previewPane.DrawToolbar(ref lastStatus, ref lastError, OnResetAll);

            using (new EditorGUILayout.HorizontalScope())
            {
                previewPane.DrawPreviewPane(position.height);

                if (workingClip == null || clipSo == null)
                {
                    EditorGUILayout.HelpBox("Failed to initialize working KimodoPlayableClip instance.", MessageType.Error);
                }
                else
                {
                    editorPane.Draw(
                        position.width,
                        clipSo,
                        previewPane,
                        isGenerating,
                        StartGenerate,
                        CancelGenerate,
                        ApplyGeneratedResult,
                        ResetGenerated,
                        previewPane.GeneratedClipForPreview,
                        lastSuccessfulGeneratedClipForApply);
                }
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

        private void OnResetAll()
        {
            lastSuccessfulGeneratedClipForApply = null;
        }

        private void ResetGenerated()
        {
            previewPane?.ResetGeneratedOnly();
            lastSuccessfulGeneratedClipForApply = null;
            lastStatus = "Generated preview cleared.";
            lastError = string.Empty;
        }

        private void StartGenerate()
        {
            if (previewPane == null || workingClip == null)
            {
                lastError = "Preview pane or working clip is not ready.";
                return;
            }

            if (!previewPane.TryBuildExternalConstraints(workingClip, out string constraintsJson, out string error))
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
                        RetargetAvatar = previewPane.RetargetAvatarForPreview
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

        private void CancelGenerate()
        {
            if (workingClip == null)
            {
                return;
            }
            KimodoEditorCommandManager.Dispatch(new CancelPlayableClipGenerationCommand(workingClip));
        }

        private void ApplyGeneratedResult()
        {
            if (previewPane == null || lastSuccessfulGeneratedClipForApply == null)
            {
                lastError = "No generated clip available to apply.";
                return;
            }

            bool success;
            string error;
            if (previewPane.SelectedTransition != null)
            {
                AnimatorState toState = previewPane.SelectedTransition.destinationState;
                string suggestedStateName = string.Format("{0}_{1}_KimodoInsert", previewPane.SelectedFromState != null ? previewPane.SelectedFromState.name : "From", toState != null ? toState.name : "To");

                success = applyService.TryApplyTransition(
                    new KimodoAnimatorApplyService.TransitionApplyContext
                    {
                        Controller = KimodoAnimatorSelectionResolver.FindControllerForObject(previewPane.SelectedTransition),
                        StateMachine = previewPane.SelectedStateMachine,
                        FromState = previewPane.SelectedFromState,
                        ToState = toState,
                        OriginalTransition = previewPane.SelectedTransition,
                        GeneratedClip = lastSuccessfulGeneratedClipForApply,
                        NewStateName = suggestedStateName
                    },
                    out error);
            }
            else if (previewPane.SelectedState != null)
            {
                success = applyService.TryApplyState(
                    new KimodoAnimatorApplyService.StateApplyContext
                    {
                        Controller = KimodoAnimatorSelectionResolver.FindControllerForObject(previewPane.SelectedState),
                        State = previewPane.SelectedState,
                        GeneratedClip = lastSuccessfulGeneratedClipForApply
                    },
                    out error);
            }
            else
            {
                lastError = "No selected transition or state to apply.";
                return;
            }

            if (!success)
            {
                lastError = error;
                return;
            }

            lastError = string.Empty;
            lastStatus = "Apply completed.";
        }

        private void EnsureWorkingClip()
        {
            if (workingClip != null && clipSo != null)
            {
                return;
            }

            workingClip = CreateInstance<KimodoPlayableClip>();
            workingClip.name = "KimodoAnimatorTool_WorkingClip";
            clipSo = new SerializedObject(workingClip);
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
                previewPane?.OnGenerateSuccess(gen.GeneratedClip);
                lastSuccessfulGeneratedClipForApply = gen.GeneratedClip;
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
            previewPane?.OnGenerateFailedOrCanceled();
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
            previewPane?.OnGenerateFailedOrCanceled();
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
    }

    internal static class KimodoAnimatorSelectionResolver
    {
        public static UnityEditor.Animations.AnimatorController FindControllerForObject(UnityEngine.Object target)
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

            return AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(path);
        }
    }
}
