using KimodoBridge;
using KimodoBridge.Editor;
using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using TimelineInject;

namespace KimodoBridge.Editor
{
    public sealed class KimodoAnimatorToolWindow : EditorWindow
    {
        private const string MenuPath = "Kimodo/Kimodo Animator Tool";

        private string lastStatus = string.Empty;
        private string lastError = string.Empty;
        private bool isGenerating;
        private KimodoGenerationBackend generationBackend = KimodoGenerationBackend.KimodoBridge;
        private string bridgeModelName = "Kimodo-SOMA-RP-v1";
        private KimodoBridgeVramMode bridgeVramMode = KimodoBridgeVramMode.Low;
        private string motionPrompt = string.Empty;
        private bool autoDuration = true;
        private float customDurationSeconds = KimodoPlayableClip.DEFAULT_FRAMES / KimodoPlayableClip.FIXED_FRAME_RATE;
        private int diffusionSteps = 100;
        private bool enableInbetweenConstraints = true;
        private bool isLoop;
        private bool randomSeed;
        private int seed = 42;
        private AnimationClip lastSuccessfulGeneratedClipForApply;
        private readonly KimodoAnimatorApplyService applyService = new KimodoAnimatorApplyService();
        private readonly KimodoEditorGeneratePipelineOrchestrator generatePipelineOrchestrator = new KimodoEditorGeneratePipelineOrchestrator();
        private KimodoAnimatorPreviewPane previewPane;
        private KimodoAnimatorEditorPane editorPane;
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
            previewPane = new KimodoAnimatorPreviewPane();
            previewPane.Initialize();
            editorPane = new KimodoAnimatorEditorPane();
            SyncSelectionDrivenDefaults(forcePromptUpdate: true);
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            disposed = true;
            generationRunId++;
            EditorApplication.update -= OnEditorUpdate;
            CancelGenerate();
            previewPane?.Dispose();
            previewPane = null;
            editorPane = null;
        }

        private void OnSelectionChange()
        {
            previewPane?.OnSelectionChange();
            SyncSelectionDrivenDefaults(forcePromptUpdate: false);
            Repaint();
        }

        private void OnEditorUpdate()
        {
            if (previewPane != null && previewPane.Tick())
            {
                Repaint();
            }
        }

        private void OnGUI()
        {
            if (previewPane == null)
            {
                previewPane = new KimodoAnimatorPreviewPane();
                previewPane.Initialize();
                SyncSelectionDrivenDefaults(forcePromptUpdate: true);
            }

            if (editorPane == null)
            {
                editorPane = new KimodoAnimatorEditorPane();
            }

            previewPane.DrawToolbar(ref lastStatus, ref lastError, OnResetAll);

            string previousBridgeModelName = bridgeModelName;
            float suggestedDurationSeconds = previewPane != null
                ? previewPane.GetSuggestedDurationSeconds()
                : (KimodoPlayableClip.DEFAULT_FRAMES / KimodoPlayableClip.FIXED_FRAME_RATE);

            using (new EditorGUILayout.HorizontalScope())
            {
                previewPane.DrawPreviewPane(position.height);
                editorPane.Draw(
                    position.width,
                    position.height,
                    previewPane,
                    ref generationBackend,
                    ref bridgeModelName,
                    ref bridgeVramMode,
                    ref motionPrompt,
                    ref autoDuration,
                    ref customDurationSeconds,
                    suggestedDurationSeconds,
                    ref diffusionSteps,
                    ref enableInbetweenConstraints,
                    ref isLoop,
                    ref randomSeed,
                    ref seed,
                    previewPane != null && previewPane.HasUnsupportedBlendTreeSelection,
                    isGenerating,
                    StartGenerate,
                    CancelGenerate,
                    ApplyGeneratedResult,
                    ResetGenerated,
                    previewPane.GeneratedClipForPreview,
                    lastSuccessfulGeneratedClipForApply);
            }

            if (!string.Equals(previousBridgeModelName, bridgeModelName, StringComparison.Ordinal) &&
                previewPane != null &&
                previewPane.HasSelection)
            {
                previewPane.TryEnsureGenerationSourceReady(bridgeModelName, out _);
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
            previewPane?.ResetGeneratedOnly();
            lastSuccessfulGeneratedClipForApply = null;
            lastStatus = "Generated preview cleared.";
            lastError = string.Empty;
        }

        private void StartGenerate()
        {
            if (previewPane == null)
            {
                lastError = "Preview pane is not ready.";
                return;
            }

            if (isGenerating)
            {
                return;
            }

            if (!previewPane.TryEnsureGenerationSourceReady(bridgeModelName, out string error))
            {
                lastError = error;
                return;
            }

            if (previewPane.RetargetAvatarForPreview == null)
            {
                lastError = "Preview retarget avatar is not ready.";
                return;
            }

            float generationDurationSeconds = ResolveGenerationDurationSeconds();
            int generationFrameCount = DurationSecondsToFrameCount(generationDurationSeconds);
            int effectiveSeed = ResolveEffectiveSeedForRun();
            string constraintsJson = string.Empty;
            if (enableInbetweenConstraints &&
                !previewPane.TryBuildExternalConstraints(bridgeModelName, generationDurationSeconds, isLoop, out constraintsJson, out error))
            {
                lastError = error;
                return;
            }

            previewPane.LockCurrentSelection();
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
                previewPane.RetargetAvatarForPreview,
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
                AnimationClip targetClip = KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset();
                KimodoEditorGenerateRequest request = BuildAnimatorGenerateRequest(
                    constraintsJson,
                    explicitRetargetAvatar,
                    generationFrameCount,
                    effectiveSeed,
                    targetClip,
                    runCts.Token,
                    (stage, message) =>
                    {
                        RunOnEditorThread(runId, () =>
                        {
                            lastStatus = string.IsNullOrWhiteSpace(message) ? stage.ToString() : message;
                            Repaint();
                        });
                    });

                KimodoEditorGenerateResult result = await generatePipelineOrchestrator.ExecuteAsync(request);

                RunOnEditorThread(runId, () =>
                {
                    isGenerating = false;
                    seed = result.Seed;
                    previewPane?.OnGenerateSuccess(result.GeneratedClip);
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
                    previewPane?.OnGenerateFailedOrCanceled();
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
                    previewPane?.OnGenerateFailedOrCanceled();
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
                string suggestedStateName = string.Format(
                    "{0}_{1}_KimodoInsert",
                    previewPane.SelectedFromState != null ? previewPane.SelectedFromState.name : "From",
                    toState != null ? toState.name : "To");

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
            if (previewPane == null)
            {
                return;
            }

            string suggestedPrompt = previewPane.GetSuggestedPrompt();
            if (forcePromptUpdate ||
                string.IsNullOrWhiteSpace(motionPrompt) ||
                string.Equals(motionPrompt, lastSuggestedPrompt, StringComparison.Ordinal))
            {
                motionPrompt = suggestedPrompt;
            }

            lastSuggestedPrompt = suggestedPrompt;
            if (autoDuration)
            {
                customDurationSeconds = previewPane.GetSuggestedDurationSeconds();
            }
            else
            {
                customDurationSeconds = ClampDurationSeconds(customDurationSeconds);
            }
        }

        private float ResolveGenerationDurationSeconds()
        {
            float durationSeconds = autoDuration && previewPane != null
                ? previewPane.GetSuggestedDurationSeconds()
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
            AnimationClip targetClip,
            CancellationToken token,
            Action<KimodoGeneratePipelineStage, string> progress)
        {
            string resolvedModelName = string.IsNullOrWhiteSpace(bridgeModelName) ? "Kimodo-SOMA-RP-v1" : bridgeModelName.Trim();
            Avatar originRetargetAvatar = null;
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(resolvedModelName, out originRetargetAvatar, out _) ||
                originRetargetAvatar == null ||
                !originRetargetAvatar.isValid ||
                !originRetargetAvatar.isHuman)
            {
                throw new InvalidOperationException("Failed to resolve origin retarget avatar.");
            }

            if (explicitRetargetAvatar == null || !explicitRetargetAvatar.isValid || !explicitRetargetAvatar.isHuman)
            {
                throw new InvalidOperationException("Preview retarget avatar is null/invalid/non-humanoid.");
            }

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
                OriginRetargetAvatar = originRetargetAvatar,
                TargetRetargetAvatar = explicitRetargetAvatar,
                ExportMuscleClip = true,
                CanSkipRetarget = generatedClip =>
                    previewPane != null &&
                    previewPane.PreviewAvatarRoot != null &&
                    KimodoEditorClipUtility.CanApplyClipDirectlyToProfileSkeleton(
                        generatedClip,
                        previewPane.PreviewAvatarRoot,
                        resolvedModelName,
                        out _),
                ModelsRoot = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim() ?? string.Empty,
                ComfyHost = string.Empty,
                ComfyPort = 8188,
                GenerationTimeoutSeconds = KimodoPlayableClipGenerationSettings.instance.GenerationTimeoutSeconds,
                TargetClip = targetClip,
                Progress = progress,
                Token = token
            };
        }

        private static int DurationSecondsToFrameCount(float durationSeconds)
        {
            return Mathf.Clamp(
                Mathf.RoundToInt(ClampDurationSeconds(durationSeconds) * KimodoPlayableClip.FIXED_FRAME_RATE),
                KimodoPlayableClip.MIN_FRAMES,
                KimodoPlayableClip.MAX_FRAMES);
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

    internal static class KimodoAnimatorSelectionResolver
    {
        public static AnimatorController FindControllerForObject(UnityEngine.Object target)
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
    }
}
