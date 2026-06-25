using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
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
        private KimodoGenerationBackend generationBackend = KimodoGenerationBackend.KimodoBridge;
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
        private CancellationTokenSource generationCancellationTokenSource;
        private bool disposed;
        private int generationRunId;
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
            disposed = false;
            previewPanel = new KimodoAnimatorPreviewPanel();
            previewPanel.Initialize();
            editorPanel = new KimodoAnimatorEditorPanel();
            SyncSelectionDrivenDefaults(forcePromptUpdate: true);
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            disposed = true;
            generationRunId++;
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
                    ref generationBackend,
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
            lastSuccessfulGeneratedClipForApply = null;
            SyncSelectionDrivenDefaults(forcePromptUpdate: false);
        }

        private void ResetGenerated()
        {
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
            int generationFrameCount = KimodoInOutConstraintTimingUtility.DurationSecondsToFrameCount(generationDurationSeconds);
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
            DisposeGenerationCancellation();
            int runId = ++generationRunId;
            generationCancellationTokenSource = new CancellationTokenSource();
            CancellationTokenSource runCts = generationCancellationTokenSource;

            isGenerating = true;
            lastError = string.Empty;
            lastStatus = "Generating and baking...";
            Repaint();
            _ = StartGenerateAsync(
                constraintsJson,
                previewPanel.RetargetAvatarForPreview,
                generationFrameCount,
                effectiveSeed,
                runCts,
                runId);
        }

        private async Task StartGenerateAsync(
            string constraintsJson,
            Avatar explicitRetargetAvatar,
            int generationFrameCount,
            int effectiveSeed,
            CancellationTokenSource runCts,
            int runId)
        {
            try
            {
                KimodoEditorGenerateRequest request = BuildAnimatorGenerateRequest(
                    constraintsJson,
                    explicitRetargetAvatar,
                    generationFrameCount,
                    effectiveSeed,
                    runCts.Token,
                    (stage, message) =>
                    {
                        RunOnEditorThread(runId, () =>
                        {
                            lastStatus = string.IsNullOrWhiteSpace(message) ? stage.ToString() : message;
                            Repaint();
                        });
                    });

                KimodoEditorGenerateResult result = await KimodoEditorRuntimeGeneratePipeline.ExecuteAsync(request);

                RunOnEditorThread(runId, () =>
                {
                    isGenerating = false;
                    seed = result.Seed;
                    previewPanel?.OnGenerateSuccess(result.GeneratedClip);
                    lastSuccessfulGeneratedClipForApply = result.GeneratedClip;
                    lastStatus = "Generation complete.";
                    lastError = string.Empty;
                    KimodoTimelinePreviewRefreshUtility.RefreshIfPreviewing();
                    Repaint();
                });
            }
            catch (OperationCanceledException)
            {
                RunOnEditorThread(runId, () =>
                {
                    isGenerating = false;
                    previewPanel?.OnGenerateFailedOrCanceled();
                    lastStatus = "Generation canceled.";
                    lastError = string.Empty;
                    Repaint();
                });
            }
            catch (Exception ex)
            {
                RunOnEditorThread(runId, () =>
                {
                    isGenerating = false;
                    previewPanel?.OnGenerateFailedOrCanceled();
                    lastError = ex.Message;
                    lastStatus = "Generation failed.";
                    Repaint();
                    RethrowOnNextEditorTick(runId, ex);
                });
            }
            finally
            {
                RunOnEditorThreadForCleanup(() =>
                {
                    DisposeGenerationCancellation(runCts);
                });
            }
        }

        private void CancelGenerate()
        {
            CancellationTokenSource cts = generationCancellationTokenSource;
            if (cts == null)
            {
                return;
            }

            try
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }
            }
            catch
            {
                // Ignore cancellation errors.
            }
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

        private void DisposeGenerationCancellation()
        {
            DisposeGenerationCancellation(generationCancellationTokenSource);
        }

        private void DisposeGenerationCancellation(CancellationTokenSource cts)
        {
            if (cts == null)
            {
                return;
            }

            if (ReferenceEquals(generationCancellationTokenSource, cts))
            {
                generationCancellationTokenSource = null;
            }

            cts.Dispose();
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
            Action<KimodoGeneratePipelineStage, string> progress)
        {
            string resolvedModelName = KimodoPlayableClip.NormalizeBridgeModelName(bridgeModelName);
            return new KimodoEditorGenerateRequest
            {
                Prompt = motionPrompt,
                ModelName = resolvedModelName,
                GenerationBackend = generationBackend,
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
                ComfyHost = string.Empty,
                ComfyPort = 8188,
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

        private void RunOnEditorThread(int runId, Action action)
        {
            if (action == null)
            {
                return;
            }

            EditorApplication.delayCall += () =>
            {
                if (disposed || runId != generationRunId)
                {
                    return;
                }

                action();
            };
        }

        private static void RunOnEditorThreadForCleanup(Action action)
        {
            if (action == null)
            {
                return;
            }

            EditorApplication.delayCall += () => action();
        }

        private void RethrowOnNextEditorTick(int runId, Exception exception)
        {
            if (exception == null)
            {
                return;
            }

            ExceptionDispatchInfo dispatchInfo = ExceptionDispatchInfo.Capture(exception);
            EditorApplication.delayCall += () =>
            {
                if (disposed || runId != generationRunId)
                {
                    return;
                }

                dispatchInfo.Throw();
            };
        }
    }

}
