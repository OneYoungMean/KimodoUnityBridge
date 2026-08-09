using System;
using System.Collections.Generic;
using System.Threading;
using KimodoUnityBridge.Command;
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
        private KimodoTextEncoderMode textEncoderMode = KimodoTextEncoderMode.HighPerformance;
        private string motionPrompt = string.Empty;
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

        // TODO: Re-enable the Animator Tool entry after its workflow is reviewed.
        // The implementation below is intentionally preserved; only the editor menu entry is disabled.
        //[MenuItem(MenuPath, priority = 110)]
        private static void OpenWindow()
        {
            KimodoAnimatorToolWindow window = GetWindow<KimodoAnimatorToolWindow>("Kimodo Animator Tool");
            window.minSize = new Vector2(600f, 320f);
            window.Show();
        }

        private void OnEnable()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            if (settings != null)
            {
                bridgeModelName = settings.DefaultBridgeModelName;
                textEncoderMode = settings.DefaultTextEncoderMode;
            }

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
                    ref textEncoderMode,
                    ref motionPrompt,
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
                command_generation_runner.Clear(previewPanel.RequestTargetObject);
            }
            lastSuccessfulGeneratedClipForApply = null;
            SyncSelectionDrivenDefaults(forcePromptUpdate: false);
        }

        private void ResetGenerated()
        {
            if (previewPanel != null && previewPanel.RequestTargetObject != null)
            {
                command_generation_runner.Clear(previewPanel.RequestTargetObject);
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

            float generationDurationSeconds = previewPanel.GetSuggestedDurationSeconds();
            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(bridgeModelName);
            float targetFrameRate = KimodoMotionModelProfiles.TryGetArdy(resolvedModelName, out KimodoMotionModelProfile profile)
                ? profile.SourceFps
                : KimodoPlayableClip.FIXED_FRAME_RATE;
            int generationFrameCount = Mathf.Max(
                1,
                KimodoFrameTimeUtility.SecondsToFrameCount(generationDurationSeconds, targetFrameRate));
            int effectiveSeed = ResolveEffectiveSeedForRun();
            string constraintsJson = string.Empty;
            if (!previewPanel.TryBuildExternalConstraints(
                    bridgeModelName,
                    inOutConstraintMode,
                    generationDurationSeconds,
                    isLoop,
                    out constraintsJson,
                    out List<KimodoMarkerSampleResult> constraintSamples,
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
            if (!command_generation_runner.Start(
                    requestTarget,
                    $"animator:{KimodoUnityObjectIdUtility.NameKey(requestTarget)}",
                    command_kind.GeneratePlayableClip,
                    async (session, token) =>
                    {
                        KimodoEditorGenerateRequest request = BuildAnimatorGenerateRequest(
                            constraintsJson,
                            previewPanel.RetargetAvatarForPreview,
                            generationFrameCount,
                            targetFrameRate,
                            constraintSamples,
                            effectiveSeed,
                            token,
                            (stage, message) =>
                            {
                                command_generation_runner.UpdateProgress(requestTarget, session.RequestId, stage, message);
                            });

                        command_generate_result result = await KimodoEditorGeneratePipeline.ExecuteAsync(request);
                        KimodoTimelinePreviewRefreshUtility.RefreshIfPreviewing();
                        return (command_result)result;
                    },
                    out command_generation_session startedSession,
                    out error))
            {
                isGenerating = false;
                lastError = error;
                return;
            }

            command_generation_runner.UpdateProgress(
                requestTarget,
                startedSession.RequestId,
                KimodoBridgeCommandStage.Validate,
                lastStatus);
            Repaint();
        }

        private void CancelGenerate()
        {
            command_generation_runner.Cancel(previewPanel != null ? previewPanel.RequestTargetObject : null);
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

            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            string suggestedPrompt = settings.ResolvePrompt(previewPanel.GetSuggestedPrompt());
            if (forcePromptUpdate ||
                string.IsNullOrWhiteSpace(motionPrompt) ||
                string.Equals(motionPrompt, lastSuggestedPrompt, StringComparison.Ordinal))
            {
                motionPrompt = suggestedPrompt;
            }

            lastSuggestedPrompt = suggestedPrompt;
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
            float targetFrameRate,
            List<KimodoMarkerSampleResult> constraintSamples,
            int effectiveSeed,
            CancellationToken token,
            Action<KimodoBridgeCommandStage, string> progress)
        {
            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(bridgeModelName);
            List<KimodoMarkerSampleResult> samples = constraintSamples ?? new List<KimodoMarkerSampleResult>();
            string resolvedConstraintsJson = samples.Count > 0
                ? KimodoConstraintJsonExporter.ToConstraintsJson(
                    samples,
                    0.0,
                    generationFrameCount / targetFrameRate,
                    targetFrameRate)
                : constraintsJson ?? string.Empty;
            return new KimodoEditorGenerateRequest
            {
                Prompt = motionPrompt,
                ModelName = resolvedModelName,
                TextEncoderMode = textEncoderMode,
                TargetFrameCount = generationFrameCount,
                TargetFrameRate = targetFrameRate,
                DiffusionSteps = diffusionSteps,
                EffectiveSeed = effectiveSeed,
                Loop = isLoop,
                ConstraintsJson = resolvedConstraintsJson,
                ConstraintSamples = samples,
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

        private void SyncSharedRequestState()
        {
            UnityEngine.Object targetObject = previewPanel != null ? previewPanel.RequestTargetObject : null;
            if (targetObject == null ||
                !command_generation_runner.TryGet(targetObject, out command_generation_session handle) ||
                handle == null)
            {
                isGenerating = false;
                return;
            }

            isGenerating = handle.IsRunning;
            switch (handle.Status)
            {
                case command_status.Running:
                    lastStatus = string.IsNullOrWhiteSpace(handle.Message) ? "Generating and baking..." : handle.Message;
                    lastError = string.Empty;
                    break;
                case command_status.Completed:
                    lastStatus = string.IsNullOrWhiteSpace(handle.Message) ? "Generation complete." : handle.Message;
                    lastError = string.Empty;
                    if (handle.Payload is command_generate_result generateResult && generateResult.GeneratedClip != null)
                    {
                        seed = generateResult.Seed;
                        if (!ReferenceEquals(lastSuccessfulGeneratedClipForApply, generateResult.GeneratedClip))
                        {
                            previewPanel?.OnGenerateSuccess(generateResult.GeneratedClip);
                        }

                        lastSuccessfulGeneratedClipForApply = generateResult.GeneratedClip;
                    }
                    break;
                case command_status.Failed:
                    previewPanel?.OnGenerateFailedOrCanceled();
                    lastStatus = "Generation failed.";
                    lastError = handle.Error;
                    break;
                case command_status.Canceled:
                    previewPanel?.OnGenerateFailedOrCanceled();
                    lastStatus = string.IsNullOrWhiteSpace(handle.Message) ? "Generation canceled." : handle.Message;
                    lastError = string.Empty;
                    break;
            }
        }
    }

}
