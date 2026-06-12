using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    public sealed class KimodoInfiniteMotionDemo : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private Transform profileSkeletonRoot;
        [SerializeField] private Animator humanoidRetargetAnimator;

        [Header("Bridge Runtime")]
        [SerializeField] private string modelsRoot = string.Empty;
        [SerializeField] private string modelName = "Kimodo-SOMA-RP-v1";
        [SerializeField] private bool highVram;
        [SerializeField] private bool forceSetup;

        [Header("Generation")]
        [SerializeField] private string defaultPrompt = "A person dancing with energetic rhythm.";
        [SerializeField][Min(1)] private int generationFrames = 150;
        [SerializeField][Min(1)] private int diffusionSteps = 100;
        [SerializeField] private bool randomSeed = true;
        [SerializeField] private int fixedSeed = 42;
        [SerializeField][Min(0.1f)] private float segmentIntervalSeconds = 5f;
        [SerializeField] private bool loopHint = true;
        [SerializeField] private bool allowPartialJoints;

        [Header("Debug")]
        [SerializeField] private bool autoStartOnEnable;
        [SerializeField] private bool verboseLogging = true;

        private const string FullBodyConstraintType = "fullbody";
        private const string KimodoFolderName = "NvlabKimodoQuickServer";

        private KimodoRuntimeGenerationService generationService;
        private CancellationTokenSource lifetimeCts;
        private Task schedulerTask;
        private bool running;
        private bool startRequested;

        private RawMotionPlayer motionPlayer;

        private bool generationInFlight;
        private int segmentIndex;
        private int lastGenerationWaitStatusSegment = -1;
        private KimodoMarkerSampleResult nextConstraintPose;
        private bool manualSendRequested;
        private int activePromptHash;
        private string promptDraft;
        private string statusMessage = "Idle.";

        private void Awake()
        {
            motionPlayer = new RawMotionPlayer();
            promptDraft = ResolveInitialPrompt();
        }

        private void OnEnable()
        {
            if (ValidateConfiguration(out _))
            {
                try
                {
                    EnsurePromptDraftInitialized();
                    UpdateStatus("Idle.");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Config warning: {ex.Message}");
                }
            }
            else
            {
                EnsurePromptDraftInitialized();
                UpdateStatus("Idle.");
            }

            if (autoStartOnEnable)
            {
                _ = StartDemoAsync();
            }
        }

        private void OnDisable()
        {
            _ = StopDemoAsync();
        }

        private void Update()
        {
            motionPlayer.Update(
                Time.deltaTime,
                modelName,
                profileSkeletonRoot,
                humanoidRetargetAnimator,
                allowPartialJoints,
                out GeneratedSegment startedSegment,
                out string playbackError);
            if (!string.IsNullOrWhiteSpace(playbackError))
            {
                UpdateStatus($"Playback failed: {playbackError}");
            }

            if (startedSegment != null)
            {
                nextConstraintPose = startedSegment.ConstraintTailPose;
                activePromptHash = startedSegment.PromptHash;
                UpdateStatus($"Playing segment {startedSegment.Index}.");
            }
        }

        private void OnGUI()
        {
            DrawPromptBar();
        }

        private void OnDestroy()
        {
            motionPlayer.Stop();
        }

        public async Task StartDemoAsync()
        {
            if (running || startRequested)
            {
                return;
            }

            startRequested = true;
            try
            {
                if (!ValidateConfiguration(out string error))
                {
                    UpdateStatus(error);
                    Debug.LogError($"[KimodoInfiniteMotionDemo] {error}");
                    return;
                }

                lifetimeCts?.Cancel();
                lifetimeCts?.Dispose();
                lifetimeCts = new CancellationTokenSource();

                segmentIndex = 0;
                lastGenerationWaitStatusSegment = -1;
                nextConstraintPose = null;
                generationInFlight = false;
                manualSendRequested = false;
                activePromptHash = 0;
                motionPlayer.ResetCompletionState();
                motionPlayer.ClearQueue();

                generationService?.Dispose();
                generationService = new KimodoRuntimeGenerationService(BuildRuntimeGenerationSettings());

                UpdateStatus("Starting Kimodo bridge...");
                await generationService.StartAsync(KimodoBackendType.Bridge, OnProgress, lifetimeCts.Token);

                running = true;
                schedulerTask = RunSchedulerLoopAsync(lifetimeCts.Token);
                UpdateStatus("Bridge ready.");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                UpdateStatus($"Start failed: {ex.Message}");
                await StopDemoAsync();
            }
            finally
            {
                startRequested = false;
            }
        }

        public async Task StopDemoAsync()
        {
            running = false;

            CancellationTokenSource cts = lifetimeCts;
            lifetimeCts = null;
            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch
                {
                }
            }

            Task task = schedulerTask;
            schedulerTask = null;
            if (task != null)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[KimodoInfiniteMotionDemo] Scheduler stop observed exception: {ex.Message}");
                }
            }

            if (generationService != null)
            {
                try
                {
                    await generationService.StopAsync(KimodoBackendType.Bridge, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[KimodoInfiniteMotionDemo] Stop bridge failed: {ex.Message}");
                }

                generationService.Dispose();
                generationService = null;
            }

            if (cts != null)
            {
                cts.Dispose();
            }

            generationInFlight = false;
            lastGenerationWaitStatusSegment = -1;
            nextConstraintPose = null;
            activePromptHash = 0;
            motionPlayer.Stop();
            motionPlayer.ResetCompletionState();
            motionPlayer.ClearQueue();
            UpdateStatus("Stopped.");
        }

        private async Task RunSchedulerLoopAsync(CancellationToken token)
        {
            try
            {
                await GenerateNextSegmentAsync(token);

                while (!token.IsCancellationRequested)
                {
                    MaybeQueueNextGeneration(token);
                    await Task.Delay(100, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                UpdateStatus($"Scheduler failed: {ex.Message}");
                running = false;
            }
        }

        private void MaybeQueueNextGeneration(CancellationToken token)
        {
            if (!running || generationInFlight || generationService == null)
            {
                return;
            }

            bool manualTrigger = manualSendRequested;
            if (!manualTrigger && motionPlayer.QueuedSegmentCount > 0)
            {
                return;
            }

            if (!CanStartGenerationForCurrentSegment(out int waitingForSegment))
            {
                if (lastGenerationWaitStatusSegment != segmentIndex)
                {
                    UpdateStatus($"Waiting for segment {waitingForSegment} to finish before generating segment {segmentIndex}.");
                    lastGenerationWaitStatusSegment = segmentIndex;
                }

                return;
            }

            bool shouldGenerate;
            if (manualTrigger)
            {
                shouldGenerate = true;
            }
            else
            {
                shouldGenerate = motionPlayer.QueuedSegmentCount == 0;
            }

            if (!shouldGenerate)
            {
                return;
            }

            manualSendRequested = false;
            lastGenerationWaitStatusSegment = -1;
            _ = GenerateNextSegmentAsync(token);
        }

        private bool CanStartGenerationForCurrentSegment(out int waitingForSegment)
        {
            int requiredCompletedSegment = segmentIndex - 2;
            waitingForSegment = requiredCompletedSegment;
            if (requiredCompletedSegment < 0)
            {
                return true;
            }

            return motionPlayer.LastCompletedSegmentIndex >= requiredCompletedSegment;
        }

        private async Task GenerateNextSegmentAsync(CancellationToken token)
        {
            if (generationInFlight || generationService == null)
            {
                return;
            }

            generationInFlight = true;
            try
            {
                string prompt = ResolvePrompt();
                int promptHash = ComputePromptHash(prompt, randomSeed ? null : fixedSeed);
                string constraintsJson = BuildNextConstraintsJson();
                var request = new KimodoGenerationRequestDto
                {
                    prompt = prompt,
                    duration = Mathf.Max(segmentIntervalSeconds, generationFrames / KimodoPlayableClip.FIXED_FRAME_RATE),
                    seed = randomSeed ? (int?)null : fixedSeed,
                    steps = Mathf.Max(1, diffusionSteps),
                    constraints_json = constraintsJson,
                    boundary_pose_json = string.Empty,
                    loop_hint = loopHint,
                    segment_index = segmentIndex,
                    transition_duration = 0f
                };

                OnProgress($"Generating segment {segmentIndex}...");
                KimodoGenerationResultDto result = await generationService.GenerateAsync(
                    request,
                    KimodoBackendType.Bridge,
                    OnProgress,
                    token);

                KimodoRawMotionMetadata metadata = await Task.Run(() =>
                {
                    if (!KimodoRawMotionUtility.TryParseAndAnalyze(
                            result.motionJsonCompact,
                            modelName,
                            out KimodoRawMotionMetadata parsedMetadata,
                            out string parseError,
                            FullBodyConstraintType,
                            0.0,
                            allowPartialJoints))
                    {
                        throw new InvalidOperationException(parseError);
                    }

                    return parsedMetadata;
                }, token);

                KimodoMarkerSampleResult constraintTailPose = metadata.TailPose.Clone();
                constraintTailPose.kimodoRootPosition = new Vector3(0f, constraintTailPose.kimodoRootPosition.y, 0f);
                constraintTailPose.unityRootPos = constraintTailPose.kimodoRootPosition;

                motionPlayer.Enqueue(new GeneratedSegment
                {
                    Index = segmentIndex,
                    PromptHash = promptHash,
                    PromptText = prompt,
                    Motion = metadata.Motion,
                    ConstraintTailPose = constraintTailPose,
                    FirstRootPosition = metadata.FirstRootPosition,
                    LastRootPosition = metadata.LastRootPosition,
                    WorldAccumulatedOffset = Vector3.zero
                });

                segmentIndex++;
                UpdateStatus($"Segment {segmentIndex - 1} ready.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                UpdateStatus($"Generate failed: {ex.Message}");
            }
            finally
            {
                generationInFlight = false;
            }
        }

        private string BuildNextConstraintsJson()
        {
            if (nextConstraintPose == null)
            {
                return string.Empty;
            }

            KimodoMarkerSampleResult sample = nextConstraintPose.Clone();
            sample.constraintType = FullBodyConstraintType;
            sample.sampleTime = 0.0;
            sample.kimodoRootPosition = new Vector3(0f, sample.kimodoRootPosition.y, 0f);
            sample.unityRootPos = sample.kimodoRootPosition;
            return KimodoConstraintJsonExporter.ToConstraintsJson(
                new List<KimodoMarkerSampleResult> { sample },
                0.0,
                Mathf.Max(segmentIntervalSeconds, generationFrames / KimodoPlayableClip.FIXED_FRAME_RATE));
        }

        private KimodoRuntimeGenerationSettings BuildRuntimeGenerationSettings()
        {
            string resolvedRuntimeRoot = ResolveRuntimeRoot();
            string launcherPath = BridgeLauncherResolver.ResolveStartScript(resolvedRuntimeRoot);
            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                throw new FileNotFoundException($"Cannot resolve bridge launcher under '{resolvedRuntimeRoot}'.");
            }

            return new KimodoRuntimeGenerationSettings
            {
                bridgeSettings = new BridgeRuntimeSettings
                {
                    runtimeRoot = resolvedRuntimeRoot,
                    launcherPath = launcherPath,
                    modelName = modelName,
                    highVram = highVram,
                    forceSetup = forceSetup,
                    modelsRoot = string.IsNullOrWhiteSpace(modelsRoot) ? null : Path.GetFullPath(modelsRoot)
                }
            };
        }

        private bool ValidateConfiguration(out string error)
        {
            if (profileSkeletonRoot == null)
            {
                error = "Profile skeleton root is not assigned.";
                return false;
            }

            string resolvedRuntimeRoot = ResolveRuntimeRoot();
            if (string.IsNullOrWhiteSpace(resolvedRuntimeRoot))
            {
                error = "Runtime root is empty.";
                return false;
            }

            if (!Directory.Exists(resolvedRuntimeRoot))
            {
                error = $"Runtime root does not exist: {resolvedRuntimeRoot}";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private string ResolveRuntimeRoot()
        {
            if (Application.isEditor)
            {
                return Path.GetFullPath(Path.Combine(Application.dataPath, "..", KimodoFolderName));
            }

            return Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, KimodoFolderName));
        }

        private string ResolvePrompt()
        {
            string prompt = promptDraft;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = defaultPrompt;
            }

            return string.IsNullOrWhiteSpace(prompt) ? "A person dancing." : prompt.Trim();
        }

        public void StartDemo()
        {
            _ = StartDemoAsync();
        }

        public void StopDemo()
        {
            _ = StopDemoAsync();
        }

        private void DrawPromptBar()
        {
            const float margin = 12f;
            const float panelHeight = 74f;
            const float buttonWidth = 110f;
            const float fieldHeight = 28f;

            DrawStatusPanel(margin);

            Rect panelRect = new Rect(
                margin,
                Mathf.Max(margin, Screen.height - panelHeight - margin),
                Mathf.Max(0f, Screen.width - margin * 2f),
                panelHeight);

            GUI.Box(panelRect, GUIContent.none);

            Rect fieldRect = new Rect(
                panelRect.x + 12f,
                panelRect.y + 14f,
                Mathf.Max(0f, panelRect.width - buttonWidth - 32f),
                fieldHeight);

            Rect buttonRect = new Rect(
                panelRect.xMax - buttonWidth - 12f,
                fieldRect.y,
                buttonWidth,
                fieldHeight);

            GUI.SetNextControlName("KimodoPromptInput");
            promptDraft = GUI.TextField(fieldRect, promptDraft ?? string.Empty);
            bool sendDisabled = IsSendDisabled();

            if (Event.current.type == EventType.KeyDown &&
                GUI.GetNameOfFocusedControl() == "KimodoPromptInput" &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) &&
                !sendDisabled)
            {
                Event.current.Use();
                RequestManualSend();
            }

            bool previousGuiEnabled = GUI.enabled;
            GUI.enabled = !sendDisabled;
            if (GUI.Button(buttonRect, "Send"))
            {
                RequestManualSend();
            }
            GUI.enabled = previousGuiEnabled;
        }

        private void DrawStatusPanel(float margin)
        {
            const float panelHeight = 42f;
            Rect panelRect = new Rect(
                margin,
                margin,
                Mathf.Max(0f, Screen.width - margin * 2f),
                panelHeight);

            GUI.Box(panelRect, GUIContent.none);

            Rect labelRect = new Rect(
                panelRect.x + 12f,
                panelRect.y + 10f,
                Mathf.Max(0f, panelRect.width - 24f),
                22f);

            GUI.Label(labelRect, string.IsNullOrWhiteSpace(statusMessage) ? " " : statusMessage);
        }

        private void RequestManualSend()
        {
            if (IsSendDisabled())
            {
                return;
            }

            manualSendRequested = true;
            if (!running || generationService == null || generationInFlight)
            {
                return;
            }

            MaybeQueueNextGeneration(lifetimeCts != null ? lifetimeCts.Token : CancellationToken.None);
        }

        private bool IsSendDisabled()
        {
            string prompt = ResolvePrompt();
            return !randomSeed && activePromptHash != 0 && ComputePromptHash(prompt, fixedSeed) == activePromptHash;
        }

        private void OnProgress(string message)
        {
            if (verboseLogging && !string.IsNullOrWhiteSpace(message))
            {
                Debug.Log($"[KimodoInfiniteMotionDemo] {message}");
            }

            UpdateStatus(message);
        }

        private void UpdateStatus(string message)
        {
            statusMessage = string.IsNullOrWhiteSpace(message) ? " " : message;
        }

        private string ResolveInitialPrompt()
        {
            string prompt = defaultPrompt;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = defaultPrompt;
            }

            return string.IsNullOrWhiteSpace(prompt) ? "A person dancing." : prompt.Trim();
        }

        private void EnsurePromptDraftInitialized()
        {
            if (string.IsNullOrWhiteSpace(promptDraft))
            {
                promptDraft = ResolveInitialPrompt();
            }
        }

        private static int ComputePromptHash(string prompt, int? seed)
        {
            string normalizedPrompt = string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt.Trim();
            string raw = seed.HasValue ? $"{normalizedPrompt}##{seed.Value}" : normalizedPrompt;
            byte[] bytes = Encoding.UTF8.GetBytes(raw);
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }

            return BitConverter.ToInt32(hash, 0);
        }

        private sealed class GeneratedSegment
        {
            public int Index;
            public int PromptHash;
            public string PromptText;
            public KimodoRawMotionData Motion;
            public KimodoMarkerSampleResult ConstraintTailPose;
            public Vector3 FirstRootPosition;
            public Vector3 LastRootPosition;
            public Vector3 WorldAccumulatedOffset;
        }

        private sealed class RawMotionPlayer
        {
            private readonly Queue<GeneratedSegment> queuedSegments = new Queue<GeneratedSegment>();
            private readonly object queueGate = new object();

            private KimodoRawMotionPlaybackBinding profileBinding;
            private KimodoRawMotionPlaybackBinding sourceBinding;
            private SkeletonCache sourceCache;
            private string sourceCacheModelName;
            private HumanPoseHandler targetPoseHandler;
            private Animator targetAnimator;
            private bool restoreTargetAnimatorEnabled;
            private bool targetAnimatorWasEnabled;
            private Transform profileRootJoint;
            private Vector3 currentSegmentRootBaseline;
            private Vector3 lastCompletedWorldOffset;
            private GeneratedSegment currentSegment;
            private float timeSeconds;
            private bool playing;

            public bool IsPlaying => playing;
            public int LastCompletedSegmentIndex { get; private set; } = -1;

            public int QueuedSegmentCount
            {
                get
                {
                    lock (queueGate)
                    {
                        return queuedSegments.Count;
                    }
                }
            }

            public void Enqueue(GeneratedSegment segment)
            {
                if (segment == null)
                {
                    return;
                }

                lock (queueGate)
                {
                    queuedSegments.Enqueue(segment);
                }
            }

            public void ClearQueue()
            {
                lock (queueGate)
                {
                    queuedSegments.Clear();
                }
            }

            public void ResetCompletionState()
            {
                LastCompletedSegmentIndex = -1;
                lastCompletedWorldOffset = Vector3.zero;
            }

            public void Update(
                float deltaTime,
                string modelName,
                Transform profileSkeletonRoot,
                Animator humanoidRetargetAnimator,
                bool allowPartialJoints,
                out GeneratedSegment startedSegment,
                out string error)
            {
                startedSegment = null;
                error = string.Empty;

                if (playing && profileBinding != null)
                {
                    AdvanceCurrentMotion(deltaTime, out error);
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        return;
                    }
                }

                if (!playing && TryDequeue(out GeneratedSegment next))
                {
                    if (!Play(
                            next,
                            modelName,
                            profileSkeletonRoot,
                            humanoidRetargetAnimator,
                            allowPartialJoints,
                            out error))
                    {
                        return;
                    }

                    startedSegment = next;
                }
            }

            public void Stop()
            {
                StopActiveMotion();
                DisposeRetargetCache();
            }

            private bool Play(
                GeneratedSegment segment,
                string modelName,
                Transform profileSkeletonRoot,
                Animator humanoidRetargetAnimator,
                bool allowPartialJoints,
                out string error)
            {
                StopActiveMotion();
                if (!KimodoRawMotionUtility.TryCreatePlaybackBinding(
                        segment.Motion,
                        modelName,
                        profileSkeletonRoot,
                        out profileBinding,
                        out error,
                        allowPartialJoints))
                {
                    return false;
                }

                profileRootJoint = profileBinding.joints != null && profileBinding.joints.Length > 0
                    ? profileBinding.joints[0]
                    : null;
                if (humanoidRetargetAnimator != null &&
                    !TryCreateDirectRetargetBinding(segment.Motion, modelName, humanoidRetargetAnimator, allowPartialJoints, out error))
                {
                    StopActiveMotion();
                    return false;
                }

                currentSegment = segment;
                currentSegment.WorldAccumulatedOffset = ResolveNextWorldOffset(segment.FirstRootPosition);
                currentSegmentRootBaseline = segment.FirstRootPosition;
                timeSeconds = 0f;
                playing = true;
                return TryApplyFrame(0, out error);
            }

            private void AdvanceCurrentMotion(float deltaTime, out string error)
            {
                error = string.Empty;
                if (!playing || profileBinding == null)
                {
                    return;
                }

                timeSeconds += Mathf.Max(0f, deltaTime);
                bool reachedEnd = false;
                if (profileBinding.Motion != null && timeSeconds >= profileBinding.Motion.LastFrameTimeSeconds)
                {
                    timeSeconds = profileBinding.Motion.LastFrameTimeSeconds;
                    reachedEnd = true;
                }

                if (!TryApplyTime(timeSeconds, out error))
                {
                    StopActiveMotion();
                    return;
                }

                if (reachedEnd)
                {
                    MarkCurrentSegmentCompleted();
                    StopActiveMotion();
                }
            }

            private bool TryDequeue(out GeneratedSegment segment)
            {
                lock (queueGate)
                {
                    if (queuedSegments.Count == 0)
                    {
                        segment = null;
                        return false;
                    }

                    segment = queuedSegments.Dequeue();
                    return true;
                }
            }

            private void MarkCurrentSegmentCompleted()
            {
                if (currentSegment != null && currentSegment.Index > LastCompletedSegmentIndex)
                {
                    LastCompletedSegmentIndex = currentSegment.Index;
                    lastCompletedWorldOffset = currentSegment.WorldAccumulatedOffset + new Vector3(
                        currentSegment.LastRootPosition.x,
                        0f,
                        currentSegment.LastRootPosition.z);
                }
            }

            private void StopActiveMotion()
            {
                profileBinding = null;
                sourceBinding = null;
                if (targetAnimator != null && restoreTargetAnimatorEnabled)
                {
                    targetAnimator.enabled = targetAnimatorWasEnabled;
                }

                profileRootJoint = null;
                targetAnimator = null;
                restoreTargetAnimatorEnabled = false;
                targetAnimatorWasEnabled = false;
                currentSegment = null;
                currentSegmentRootBaseline = Vector3.zero;
                timeSeconds = 0f;
                playing = false;
            }

            private void DisposeRetargetCache()
            {
                sourceBinding = null;
                sourceCache?.Dispose();
                sourceCache = null;
                sourceCacheModelName = null;
                targetPoseHandler = null;
            }

            private Vector3 ResolveNextWorldOffset(Vector3 nextSegmentFirstRootPosition)
            {
                return lastCompletedWorldOffset - new Vector3(nextSegmentFirstRootPosition.x, 0f, nextSegmentFirstRootPosition.z);
            }

            private bool TryCreateDirectRetargetBinding(
                KimodoRawMotionData motion,
                string modelName,
                Animator humanoidAnimator,
                bool allowPartialJoints,
                out string error)
            {
                error = string.Empty;
                if (humanoidAnimator == null)
                {
                    return true;
                }

                Avatar targetAvatar = humanoidAnimator.avatar;
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(targetAvatar))
                {
                    error = "Humanoid retarget animator avatar is null, invalid, or not humanoid.";
                    return false;
                }

                if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar sourceAvatar, out error))
                {
                    return false;
                }

                if (sourceCache == null || !string.Equals(sourceCacheModelName, modelName, StringComparison.OrdinalIgnoreCase))
                {
                    DisposeRetargetCache();
                    if (!KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                            sourceAvatar,
                            "KimodoInfiniteMotionDemo_SourceRetarget",
                            out sourceCache,
                            out error))
                    {
                        return false;
                    }

                    sourceCacheModelName = modelName;
                }

                if (!KimodoRawMotionUtility.TryCreatePlaybackBinding(
                        motion,
                        modelName,
                        sourceCache.skeletonRoot,
                        out sourceBinding,
                        out error,
                        allowPartialJoints))
                {
                    return false;
                }

                bool needsNewPoseHandler = targetPoseHandler == null || targetAnimator != humanoidAnimator;
                targetAnimator = humanoidAnimator;
                if (needsNewPoseHandler)
                {
                    targetPoseHandler = new HumanPoseHandler(targetAvatar, humanoidAnimator.transform);
                }

                targetAnimatorWasEnabled = humanoidAnimator.enabled;
                restoreTargetAnimatorEnabled = true;
                humanoidAnimator.enabled = false;
                return true;
            }

            private bool TryApplyFrame(int frameIndex, out string error)
            {
                if (!KimodoRawMotionUtility.TryApplyFrame(profileBinding, frameIndex, out error, applyRootPosition: false))
                {
                    return false;
                }

                if (!TryApplyProfileDeltaRoot(frameIndex, out error))
                {
                    return false;
                }

                if (sourceBinding != null && !KimodoRawMotionUtility.TryApplyFrame(sourceBinding, frameIndex, out error, applyRootPosition: false))
                {
                    return false;
                }

                if (!TryApplySourceDeltaRoot(frameIndex, out error))
                {
                    return false;
                }

                return TryApplyHumanoidPose(out error);
            }

            private bool TryApplyTime(float sampleTimeSeconds, out string error)
            {
                if (!KimodoRawMotionUtility.TryApplyTime(profileBinding, sampleTimeSeconds, out error, loop: false, applyRootPosition: false))
                {
                    return false;
                }

                if (!TryApplyProfileDeltaRoot(sampleTimeSeconds, out error))
                {
                    return false;
                }

                if (sourceBinding != null && !KimodoRawMotionUtility.TryApplyTime(sourceBinding, sampleTimeSeconds, out error, loop: false, applyRootPosition: false))
                {
                    return false;
                }

                if (!TryApplySourceDeltaRoot(sampleTimeSeconds, out error))
                {
                    return false;
                }

                return TryApplyHumanoidPose(out error);
            }

            private bool TryApplyProfileDeltaRoot(int frameIndex, out string error)
            {
                error = string.Empty;
                if (profileRootJoint == null || currentSegment == null)
                {
                    return true;
                }

                if (!currentSegment.Motion.TryReadUnityRootPosition(frameIndex, out Vector3 rootPosition))
                {
                    error = $"Failed to read profile root position for frame {frameIndex}.";
                    return false;
                }

                Vector3 delta = rootPosition - currentSegmentRootBaseline;
                profileRootJoint.localPosition = new Vector3(
                    currentSegment.WorldAccumulatedOffset.x + delta.x,
                    rootPosition.y,
                    currentSegment.WorldAccumulatedOffset.z + delta.z);
                return true;
            }

            private bool TryApplyProfileDeltaRoot(float sampleTimeSeconds, out string error)
            {
                error = string.Empty;
                if (profileRootJoint == null || currentSegment == null)
                {
                    return true;
                }

                if (!KimodoRawMotionUtility.ResolveInterpolatedRootPosition(currentSegment.Motion, sampleTimeSeconds, false, out Vector3 rootPosition))
                {
                    error = $"Failed to sample profile root position at time {sampleTimeSeconds:0.###}.";
                    return false;
                }

                Vector3 delta = rootPosition - currentSegmentRootBaseline;
                profileRootJoint.localPosition = new Vector3(
                    currentSegment.WorldAccumulatedOffset.x + delta.x,
                    rootPosition.y,
                    currentSegment.WorldAccumulatedOffset.z + delta.z);
                return true;
            }

            private bool TryApplySourceDeltaRoot(int frameIndex, out string error)
            {
                error = string.Empty;
                if (sourceBinding?.joints == null || sourceBinding.joints.Length == 0 || currentSegment == null)
                {
                    return true;
                }

                if (!currentSegment.Motion.TryReadUnityRootPosition(frameIndex, out Vector3 rootPosition))
                {
                    error = $"Failed to read source root position for frame {frameIndex}.";
                    return false;
                }

                Vector3 delta = rootPosition - currentSegmentRootBaseline;
                sourceBinding.joints[0].localPosition = new Vector3(delta.x, rootPosition.y, delta.z);
                return true;
            }

            private bool TryApplySourceDeltaRoot(float sampleTimeSeconds, out string error)
            {
                error = string.Empty;
                if (sourceBinding?.joints == null || sourceBinding.joints.Length == 0 || currentSegment == null)
                {
                    return true;
                }

                if (!KimodoRawMotionUtility.ResolveInterpolatedRootPosition(currentSegment.Motion, sampleTimeSeconds, false, out Vector3 rootPosition))
                {
                    error = $"Failed to sample source root position at time {sampleTimeSeconds:0.###}.";
                    return false;
                }

                Vector3 delta = rootPosition - currentSegmentRootBaseline;
                sourceBinding.joints[0].localPosition = new Vector3(delta.x, rootPosition.y, delta.z);
                return true;
            }

            private bool TryApplyHumanoidPose(out string error)
            {
                error = string.Empty;
                if (sourceCache == null || targetPoseHandler == null)
                {
                    return true;
                }

                if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(sourceCache, out MuscleSample sample, out error))
                {
                    return false;
                }

                HumanPose pose = sample.pose;
                targetPoseHandler.SetHumanPose(ref pose);
                return true;
            }
        }
    }
}
