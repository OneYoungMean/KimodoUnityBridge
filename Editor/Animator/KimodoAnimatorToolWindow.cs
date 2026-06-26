using System;
using System.Threading;
using TimelineInject;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KimodoBridge.Editor
{
    public sealed class KimodoAnimatorToolWindow : EditorWindow
    {
        private const string MenuPath = "Kimodo/Kimodo Animator Tool";

        private string lastStatus = string.Empty;
        private string lastError = string.Empty;
        private bool isGenerating;
        private string bridgeModelName = KimodoPlayableClip.DefaultBridgeModelName;
        private KimodoBridgeVramMode bridgeVramMode = KimodoBridgeVramMode.Low;
        private string motionPrompt = string.Empty;
        private bool autoDuration = true;
        private float customDurationSeconds = KimodoPlayableClip.DEFAULT_FRAMES / KimodoPlayableClip.FIXED_FRAME_RATE;
        private int diffusionSteps = 100;
        private KimodoInOutConstraintMode inOutConstraintMode = KimodoInOutConstraintMode.Inside;
        private bool isLoop;
        private bool randomSeed;
        private int seed = 42;
        private AnimationClip lastSuccessfulGeneratedClipForApply;
        private readonly KimodoAnimatorApplyService applyService = new KimodoAnimatorApplyService();
        private KimodoAnimatorPreviewPanel previewPanel;
        private KimodoAnimatorEditorPanel editorPanel;
        private string lastSuggestedPrompt = string.Empty;

        [MenuItem(MenuPath, priority = 110)]
        private static void OpenWindow()
        {
            KimodoAnimatorToolWindow window = GetWindow<KimodoAnimatorToolWindow>("Kimodo Animator Tool");
            window.minSize = new Vector2(600f, 320f);
            window.Show();
        }

        private void OnEnable()
        {
            previewPanel = new KimodoAnimatorPreviewPanel();
            previewPanel.Initialize();
            editorPanel = new KimodoAnimatorEditorPanel();
            SyncSelectionDrivenDefaults(forcePromptUpdate: true);
            SyncSharedRequestState();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            CancelGenerate();
            previewPanel?.Dispose();
            previewPanel = null;
            editorPanel = null;
        }

        private void OnSelectionChange()
        {
            previewPanel?.OnSelectionChange();
            SyncSelectionDrivenDefaults(forcePromptUpdate: false);
            SyncSharedRequestState();
            Repaint();
        }

        private void OnEditorUpdate()
        {
            if (previewPanel != null && previewPanel.Tick())
            {
                Repaint();
            }
        }

        private void OnGUI()
        {
            if (previewPanel == null)
            {
                previewPanel = new KimodoAnimatorPreviewPanel();
                previewPanel.Initialize();
                SyncSelectionDrivenDefaults(forcePromptUpdate: true);
            }

            if (editorPanel == null)
            {
                editorPanel = new KimodoAnimatorEditorPanel();
            }

            SyncSharedRequestState();

            previewPanel.DrawToolbar(ref lastStatus, ref lastError, OnResetAll);

            string previousBridgeModelName = bridgeModelName;
            float suggestedDurationSeconds = previewPanel != null
                ? previewPanel.GetSuggestedDurationSeconds()
                : (KimodoPlayableClip.DEFAULT_FRAMES / KimodoPlayableClip.FIXED_FRAME_RATE);

            using (new EditorGUILayout.HorizontalScope())
            {
                previewPanel.DrawPreviewPanel(position.height);
                editorPanel.Draw(
                    position.width,
                    position.height,
                    previewPanel,
                    ref bridgeModelName,
                    ref bridgeVramMode,
                    ref motionPrompt,
                    ref autoDuration,
                    ref customDurationSeconds,
                    suggestedDurationSeconds,
                    ref diffusionSteps,
                    ref inOutConstraintMode,
                    ref isLoop,
                    ref randomSeed,
                    ref seed,
                    previewPanel != null && previewPanel.HasUnsupportedBlendTreeSelection,
                    isGenerating,
                    StartGenerate,
                    CancelGenerate,
                    ApplyGeneratedResult,
                    ResetGenerated,
                    previewPanel.GeneratedClipForPreview,
                    lastSuccessfulGeneratedClipForApply);
            }

            if (!string.Equals(previousBridgeModelName, bridgeModelName, StringComparison.Ordinal) &&
                previewPanel != null &&
                previewPanel.HasSelection)
            {
                previewPanel.TryEnsureGenerationSourceReady(bridgeModelName, out _);
                Repaint();
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
            if (previewPanel != null && previewPanel.RequestTargetObject != null)
            {
                EditorGenerateSessionRunner.Clear(previewPanel.RequestTargetObject);
            }
            lastSuccessfulGeneratedClipForApply = null;
            SyncSelectionDrivenDefaults(forcePromptUpdate: false);
        }

        private void ResetGenerated()
        {
            if (previewPanel != null && previewPanel.RequestTargetObject != null)
            {
                EditorGenerateSessionRunner.Clear(previewPanel.RequestTargetObject);
            }
            previewPanel?.ResetGeneratedOnly();
            lastSuccessfulGeneratedClipForApply = null;
            lastStatus = "Generated preview cleared.";
            lastError = string.Empty;
        }

        private void StartGenerate()
        {
            if (previewPanel == null)
            {
                lastError = "Preview panel is not ready.";
                return;
            }

            if (isGenerating)
            {
                return;
            }

            if (!previewPanel.TryEnsureGenerationSourceReady(bridgeModelName, out string error))
            {
                lastError = error;
                return;
            }

            if (previewPanel.RetargetAvatarForPreview == null)
            {
                lastError = "Preview retarget avatar is not ready.";
                return;
            }

            float generationDurationSeconds = ResolveGenerationDurationSeconds();
            int generationFrameCount = KimodoInOutConstraintAdapter.DurationSecondsToFrameCount(generationDurationSeconds);
            int effectiveSeed = ResolveEffectiveSeedForRun();
            string constraintsJson = string.Empty;
            if (!previewPanel.TryBuildExternalConstraints(
                    bridgeModelName,
                    inOutConstraintMode,
                    generationDurationSeconds,
                    isLoop,
                    out constraintsJson,
                    out error))
            {
                lastError = error;
                return;
            }

            previewPanel.LockCurrentSelection();
            UnityEngine.Object requestTarget = previewPanel.RequestTargetObject;
            if (requestTarget == null)
            {
                lastError = "Selection target is not ready.";
                return;
            }

            isGenerating = true;
            lastError = string.Empty;
            lastStatus = "Generating and baking...";
            if (!EditorGenerateSessionRunner.Start(
                    requestTarget,
                    $"animator:{requestTarget.GetInstanceID()}",
                    KimodoEditorCommandKind.GeneratePlayableClip,
                    async (session, token) =>
                    {
                        KimodoEditorGenerateRequest request = BuildAnimatorGenerateRequest(
                            constraintsJson,
                            previewPanel.RetargetAvatarForPreview,
                            generationFrameCount,
                            effectiveSeed,
                            token,
                            (stage, message) =>
                            {
                                EditorGenerateSessionRunner.UpdateProgress(requestTarget, session.RequestId, stage, message);
                            });

                        KimodoEditorGenerateResult result = await KimodoEditorRuntimeGeneratePipeline.ExecuteAsync(request);
                        KimodoTimelinePreviewRefreshUtility.RefreshIfPreviewing();
                        return (IKimodoEditorCommandResult)result;
                    },
                    out EditorGenerateSession startedSession,
                    out error))
            {
                isGenerating = false;
                lastError = error;
                return;
            }

            EditorGenerateSessionRunner.UpdateProgress(
                requestTarget,
                startedSession.RequestId,
                KimodoBridgeCommandStage.Validate,
                lastStatus);
            Repaint();
        }

        private void CancelGenerate()
        {
            EditorGenerateSessionRunner.Cancel(previewPanel != null ? previewPanel.RequestTargetObject : null);
        }

        private void ApplyGeneratedResult()
        {
            if (previewPanel == null || lastSuccessfulGeneratedClipForApply == null)
            {
                lastError = "No generated clip available to apply.";
                return;
            }

            bool success;
            string error;
            if (previewPanel.SelectedTransition != null)
            {
                AnimatorState toState = previewPanel.SelectedTransition.destinationState;
                string suggestedStateName = string.Format(
                    "{0}_{1}_KimodoInsert",
                    previewPanel.SelectedFromState != null ? previewPanel.SelectedFromState.name : "From",
                    toState != null ? toState.name : "To");

                success = applyService.TryApplyTransition(
                    new KimodoAnimatorApplyService.TransitionApplyContext
                    {
                        Controller = KimodoAnimatorSelectionUtility.FindControllerForObject(previewPanel.SelectedTransition),
                        StateMachine = previewPanel.SelectedStateMachine,
                        FromState = previewPanel.SelectedFromState,
                        ToState = toState,
                        OriginalTransition = previewPanel.SelectedTransition,
                        GeneratedClip = lastSuccessfulGeneratedClipForApply,
                        NewStateName = suggestedStateName
                    },
                    out error);
            }
            else if (previewPanel.SelectedState != null)
            {
                success = applyService.TryApplyState(
                    new KimodoAnimatorApplyService.StateApplyContext
                    {
                        Controller = KimodoAnimatorSelectionUtility.FindControllerForObject(previewPanel.SelectedState),
                        State = previewPanel.SelectedState,
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

        private void SyncSelectionDrivenDefaults(bool forcePromptUpdate)
        {
            if (previewPanel == null)
            {
                return;
            }

            string suggestedPrompt = previewPanel.GetSuggestedPrompt();
            if (forcePromptUpdate ||
                string.IsNullOrWhiteSpace(motionPrompt) ||
                string.Equals(motionPrompt, lastSuggestedPrompt, StringComparison.Ordinal))
            {
                motionPrompt = suggestedPrompt;
            }

            lastSuggestedPrompt = suggestedPrompt;
            if (autoDuration)
            {
                customDurationSeconds = previewPanel.GetSuggestedDurationSeconds();
            }
            else
            {
                customDurationSeconds = ClampDurationSeconds(customDurationSeconds);
            }
        }

        private float ResolveGenerationDurationSeconds()
        {
            float durationSeconds = autoDuration && previewPanel != null
                ? previewPanel.GetSuggestedDurationSeconds()
                : customDurationSeconds;
            return ClampDurationSeconds(durationSeconds);
        }

        private int ResolveEffectiveSeedForRun()
        {
            int effectiveSeed = randomSeed
                ? Guid.NewGuid().GetHashCode() & int.MaxValue
                : seed;
            seed = effectiveSeed;
            return effectiveSeed;
        }

        private KimodoEditorGenerateRequest BuildAnimatorGenerateRequest(
            string constraintsJson,
            Avatar explicitRetargetAvatar,
            int generationFrameCount,
            int effectiveSeed,
            CancellationToken token,
            Action<KimodoBridgeCommandStage, string> progress)
        {
            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(bridgeModelName);
            return new KimodoEditorGenerateRequest
            {
                Prompt = motionPrompt,
                ModelName = resolvedModelName,
                BridgeVramMode = bridgeVramMode,
                DurationSeconds = generationFrameCount / KimodoPlayableClip.FIXED_FRAME_RATE,
                DiffusionSteps = diffusionSteps,
                EffectiveSeed = effectiveSeed,
                ConstraintsJson = constraintsJson ?? string.Empty,
                CreateTargetClip = CreateAnimatorTargetClip,
                ResolveOutputPlan = (generatedClip, modelName) => ResolveAnimatorOutputPlan(
                    generatedClip,
                    explicitRetargetAvatar,
                    modelName),
                ModelsRoot = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim() ?? string.Empty,
                GenerationTimeoutSeconds = KimodoPlayableClipGenerationSettings.instance.GenerationTimeoutSeconds,
                Progress = progress,
                Token = token
            };
        }

        private static AnimationClip CreateAnimatorTargetClip()
        {
            return KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(
                $"Kimodo_Animator_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
        }

        private KimodoEditorGenerateOutputPlan ResolveAnimatorOutputPlan(
            AnimationClip generatedClip,
            Avatar explicitRetargetAvatar,
            string modelName)
        {
            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(modelName);
            bool canSkipRetarget =
                previewPanel != null &&
                previewPanel.PreviewAvatarRoot != null &&
                KimodoEditorClipUtility.CanApplyClipDirectlyToProfileSkeleton(
                    generatedClip,
                    previewPanel.PreviewAvatarRoot,
                    resolvedModelName,
                    out _);
            if (canSkipRetarget)
            {
                return new KimodoEditorGenerateOutputPlan
                {
                    SkipRetarget = true
                };
            }

            Avatar targetRetargetAvatar =
                explicitRetargetAvatar != null &&
                explicitRetargetAvatar.isValid &&
                explicitRetargetAvatar.isHuman
                    ? explicitRetargetAvatar
                    : null;

            return new KimodoEditorGenerateOutputPlan
            {
                OriginRetargetAvatar = ResolveOriginRetargetAvatar(resolvedModelName),
                TargetRetargetAvatar = targetRetargetAvatar,
                ExportMuscleClip = true,
                CurveFilterOptions = null,
                SkipRetarget = false
            };
        }

        private static Avatar ResolveOriginRetargetAvatar(string modelName)
        {
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar avatar, out _))
            {
                return null;
            }

            return KimodoRetargetCoreUtility.IsValidHumanoid(avatar) ? avatar : null;
        }

        private static float ClampDurationSeconds(float durationSeconds)
        {
            float minDuration = KimodoPlayableClip.MIN_FRAMES / KimodoPlayableClip.FIXED_FRAME_RATE;
            float maxDuration = KimodoPlayableClip.MAX_FRAMES / KimodoPlayableClip.FIXED_FRAME_RATE;
            return Mathf.Clamp(durationSeconds, minDuration, maxDuration);
        }

        private void SyncSharedRequestState()
        {
            UnityEngine.Object targetObject = previewPanel != null ? previewPanel.RequestTargetObject : null;
            if (targetObject == null ||
                !EditorGenerateSessionRunner.TryGet(targetObject, out EditorGenerateSession handle) ||
                handle == null)
            {
                isGenerating = false;
                return;
            }

            isGenerating = handle.IsRunning;
            switch (handle.Status)
            {
                case KimodoEditorRequestStatus.Running:
                    lastStatus = string.IsNullOrWhiteSpace(handle.Message) ? "Generating and baking..." : handle.Message;
                    lastError = string.Empty;
                    break;
                case KimodoEditorRequestStatus.Completed:
                    lastStatus = string.IsNullOrWhiteSpace(handle.Message) ? "Generation complete." : handle.Message;
                    lastError = string.Empty;
                    if (handle.Payload is KimodoEditorGenerateResult generateResult && generateResult.GeneratedClip != null)
                    {
                        seed = generateResult.Seed;
                        if (!ReferenceEquals(lastSuccessfulGeneratedClipForApply, generateResult.GeneratedClip))
                        {
                            previewPanel?.OnGenerateSuccess(generateResult.GeneratedClip);
                        }

                        lastSuccessfulGeneratedClipForApply = generateResult.GeneratedClip;
                    }
                    break;
                case KimodoEditorRequestStatus.Failed:
                    previewPanel?.OnGenerateFailedOrCanceled();
                    lastStatus = "Generation failed.";
                    lastError = handle.Error;
                    break;
                case KimodoEditorRequestStatus.Canceled:
                    previewPanel?.OnGenerateFailedOrCanceled();
                    lastStatus = string.IsNullOrWhiteSpace(handle.Message) ? "Generation canceled." : handle.Message;
                    lastError = string.Empty;
                    break;
            }
        }
    }

}
