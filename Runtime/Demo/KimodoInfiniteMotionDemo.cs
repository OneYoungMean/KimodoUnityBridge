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
        [SerializeField] private List<Animator> humanoidRetargetAnimators = new List<Animator>();

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
        private const float MinGenerationDurationSeconds = 1f;
        private const float MaxGenerationDurationSeconds = 10f;

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
        private TransformSnapshot initialProfileSnapshot;
        private readonly List<TransformSnapshot> initialHumanoidSnapshots = new List<TransformSnapshot>();
        private readonly QueueDebugState queueDebugState = new QueueDebugState();

        private sealed class TransformSnapshot
        {
            public Transform Transform;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 LocalScale;
        }

        private sealed class QueueDebugState
        {
            public int LastEnqueuedSegmentIndex = -1;
            public int LastDequeuedSegmentIndex = -1;
            public int LastPlayAttemptSegmentIndex = -1;
            public int LastPlayStartedSegmentIndex = -1;
        }

        private void Awake()
        {
            motionPlayer = new RawMotionPlayer();
            promptDraft = ResolveInitialPrompt();
            SyncGenerationDurationFromCurrentSettings();
            CacheInitialTransformSnapshots();
        }

        private void OnEnable()
        {
            EnsureInitialTransformSnapshots();
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
                humanoidRetargetAnimators,
                allowPartialJoints,
                verboseLogging,
                queueDebugState,
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
                }, verboseLogging, queueDebugState);

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

        public void ResetDemo()
        {
            _ = ResetDemoAsync();
        }

        public async Task ResetDemoAsync()
        {
            bool wasActive = running || startRequested || generationService != null;
            if (wasActive)
            {
                await StopDemoAsync();
            }

            RestoreInitialTransformSnapshots();
            UpdateStatus("Reset to initial transforms.");

            if (wasActive)
            {
                await StartDemoAsync();
            }
        }

        private void DrawPromptBar()
        {
            const float margin = 12f;
            const float panelHeight = 118f;
            const float buttonWidth = 110f;
            const float buttonSpacing = 10f;
            const float fieldHeight = 28f;
            const float sliderHeight = 22f;

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
                Mathf.Max(0f, panelRect.width - buttonWidth * 2f - buttonSpacing - 44f),
                fieldHeight);

            Rect resetButtonRect = new Rect(
                panelRect.xMax - buttonWidth * 2f - buttonSpacing - 12f,
                fieldRect.y,
                buttonWidth,
                fieldHeight);

            Rect sendButtonRect = new Rect(
                panelRect.xMax - buttonWidth - 12f,
                fieldRect.y,
                buttonWidth,
                fieldHeight);

            Rect sliderLabelRect = new Rect(
                fieldRect.x,
                fieldRect.yMax + 10f,
                160f,
                sliderHeight);

            Rect sliderRect = new Rect(
                sliderLabelRect.xMax + 8f,
                sliderLabelRect.y + 2f,
                Mathf.Max(0f, panelRect.width - buttonWidth * 2f - buttonSpacing - 216f),
                sliderHeight);

            Rect sliderValueRect = new Rect(
                sliderRect.xMax + 8f,
                sliderLabelRect.y,
                64f,
                sliderHeight);

            GUI.SetNextControlName("KimodoPromptInput");
            promptDraft = GUI.TextField(fieldRect, promptDraft ?? string.Empty);

            float currentDurationSeconds = GetGenerationDurationSeconds();
            GUI.Label(sliderLabelRect, "Segment Length");
            float nextDurationSeconds = GUI.HorizontalSlider(
                sliderRect,
                currentDurationSeconds,
                MinGenerationDurationSeconds,
                MaxGenerationDurationSeconds);
            GUI.Label(sliderValueRect, $"{currentDurationSeconds:0.0}s");
            if (!Mathf.Approximately(nextDurationSeconds, currentDurationSeconds))
            {
                ApplyGenerationDurationSeconds(nextDurationSeconds);
                currentDurationSeconds = GetGenerationDurationSeconds();
            }

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
            if (GUI.Button(resetButtonRect, "Reset"))
            {
                ResetDemo();
            }

            GUI.enabled = !sendDisabled;
            if (GUI.Button(sendButtonRect, "Send"))
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

        private void SyncGenerationDurationFromCurrentSettings()
        {
            ApplyGenerationDurationSeconds(GetGenerationDurationSeconds());
        }

        private float GetGenerationDurationSeconds()
        {
            float frameDuration = generationFrames / KimodoPlayableClip.FIXED_FRAME_RATE;
            return Mathf.Clamp(
                Mathf.Max(segmentIntervalSeconds, frameDuration),
                MinGenerationDurationSeconds,
                MaxGenerationDurationSeconds);
        }

        private void ApplyGenerationDurationSeconds(float durationSeconds)
        {
            float clamped = Mathf.Clamp(durationSeconds, MinGenerationDurationSeconds, MaxGenerationDurationSeconds);
            segmentIntervalSeconds = clamped;
            generationFrames = Mathf.Max(1, Mathf.RoundToInt(clamped * KimodoPlayableClip.FIXED_FRAME_RATE));
        }

        private void CacheInitialTransformSnapshots()
        {
            initialProfileSnapshot = CreateSnapshot(profileSkeletonRoot);
            initialHumanoidSnapshots.Clear();
            if (humanoidRetargetAnimators == null)
            {
                return;
            }

            var seen = new HashSet<Transform>();
            for (int i = 0; i < humanoidRetargetAnimators.Count; i++)
            {
                Animator animator = humanoidRetargetAnimators[i];
                Transform targetTransform = animator != null ? animator.transform : null;
                if (targetTransform == null || !seen.Add(targetTransform))
                {
                    continue;
                }

                TransformSnapshot snapshot = CreateSnapshot(targetTransform);
                if (snapshot != null)
                {
                    initialHumanoidSnapshots.Add(snapshot);
                }
            }
        }

        private void EnsureInitialTransformSnapshots()
        {
            if (initialProfileSnapshot?.Transform == null && profileSkeletonRoot != null)
            {
                CacheInitialTransformSnapshots();
                return;
            }

            if (humanoidRetargetAnimators == null || humanoidRetargetAnimators.Count == 0)
            {
                return;
            }

            int validAnimatorCount = 0;
            for (int i = 0; i < humanoidRetargetAnimators.Count; i++)
            {
                if (humanoidRetargetAnimators[i] != null)
                {
                    validAnimatorCount++;
                }
            }

            if (initialHumanoidSnapshots.Count < validAnimatorCount)
            {
                CacheInitialTransformSnapshots();
            }
        }

        private void RestoreInitialTransformSnapshots()
        {
            RestoreSnapshot(initialProfileSnapshot);
            for (int i = 0; i < initialHumanoidSnapshots.Count; i++)
            {
                RestoreSnapshot(initialHumanoidSnapshots[i]);
            }
        }

        private static TransformSnapshot CreateSnapshot(Transform target)
        {
            if (target == null)
            {
                return null;
            }

            return new TransformSnapshot
            {
                Transform = target,
                Position = target.position,
                Rotation = target.rotation,
                LocalScale = target.localScale
            };
        }

        private static void RestoreSnapshot(TransformSnapshot snapshot)
        {
            if (snapshot?.Transform == null)
            {
                return;
            }

            snapshot.Transform.SetPositionAndRotation(snapshot.Position, snapshot.Rotation);
            snapshot.Transform.localScale = snapshot.LocalScale;
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
            private readonly List<TargetRetargetState> targetStates = new List<TargetRetargetState>();

            private KimodoRawMotionPlaybackBinding profileBinding;
            private KimodoRawMotionPlaybackBinding sourceBinding;
            private SkeletonCache sourceCache;
            private string sourceCacheModelName;
            private Transform profileRootJoint;
            private Vector3 currentSegmentRootBaseline;
            private Vector3 lastCompletedWorldOffset;
            private GeneratedSegment currentSegment;
            private float timeSeconds;
            private bool playing;

            private sealed class TargetRetargetState : IDisposable
            {
                public Animator Animator;
                public Avatar Avatar;
                public HumanPoseHandler PoseHandler;
                public bool AnimatorWasEnabled;
                public bool AnimatorDisabledForRetarget;

                public void Dispose()
                {
                    if (Animator != null && AnimatorDisabledForRetarget)
                    {
                        Animator.enabled = AnimatorWasEnabled;
                    }

                    Animator = null;
                    Avatar = null;
                    PoseHandler = null;
                    AnimatorDisabledForRetarget = false;
                    AnimatorWasEnabled = false;
                }
            }

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

            public void Enqueue(GeneratedSegment segment, bool verboseLogging, QueueDebugState debugState)
            {
                if (segment == null)
                {
                    return;
                }

                lock (queueGate)
                {
                    queuedSegments.Enqueue(segment);
                    if (debugState != null)
                    {
                        debugState.LastEnqueuedSegmentIndex = segment.Index;
                    }

                    if (verboseLogging)
                    {
                        Debug.Log(
                            $"[KimodoInfiniteMotionDemo] Enqueue segment {segment.Index}. queueCount={queuedSegments.Count}");
                    }
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
                IReadOnlyList<Animator> humanoidRetargetAnimators,
                bool allowPartialJoints,
                bool verboseLogging,
                QueueDebugState debugState,
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
                    if (debugState != null)
                    {
                        debugState.LastDequeuedSegmentIndex = next.Index;
                    }

                    if (verboseLogging)
                    {
                        Debug.Log($"[KimodoInfiniteMotionDemo] Attempting to play dequeued segment {next.Index}.");
                    }

                    if (!Play(
                            next,
                            modelName,
                            profileSkeletonRoot,
                            humanoidRetargetAnimators,
                            allowPartialJoints,
                            out error,
                            verboseLogging,
                            debugState))
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
                IReadOnlyList<Animator> humanoidRetargetAnimators,
                bool allowPartialJoints,
                out string error,
                bool verboseLogging,
                QueueDebugState debugState)
            {
                StopActiveMotion();
                if (debugState != null)
                {
                    debugState.LastPlayAttemptSegmentIndex = segment != null ? segment.Index : -1;
                }

                if (!KimodoRawMotionUtility.TryCreatePlaybackBinding(
                        segment.Motion,
                        modelName,
                        profileSkeletonRoot,
                        out profileBinding,
                        out error,
                        allowPartialJoints))
                {
                    if (verboseLogging)
                    {
                        Debug.LogWarning(
                            $"[KimodoInfiniteMotionDemo] Play segment {segment?.Index ?? -1} failed while creating profile binding: {error}");
                    }

                    return false;
                }

                profileRootJoint = profileBinding.joints != null && profileBinding.joints.Length > 0
                    ? profileBinding.joints[0]
                    : null;
                if (!TryCreateDirectRetargetBinding(segment.Motion, modelName, humanoidRetargetAnimators, allowPartialJoints, out error))
                {
                    if (verboseLogging)
                    {
                        Debug.LogWarning(
                            $"[KimodoInfiniteMotionDemo] Play segment {segment?.Index ?? -1} failed while creating retarget binding: {error}");
                    }

                    StopActiveMotion();
                    return false;
                }

                currentSegment = segment;
                currentSegment.WorldAccumulatedOffset = ResolveNextWorldOffset(segment.FirstRootPosition);
                currentSegmentRootBaseline = segment.FirstRootPosition;
                timeSeconds = 0f;
                playing = true;
                if (!TryApplyFrame(0, out error))
                {
                    if (verboseLogging)
                    {
                        Debug.LogWarning(
                            $"[KimodoInfiniteMotionDemo] Play segment {segment?.Index ?? -1} failed while applying frame 0: {error}");
                    }

                    return false;
                }

                if (debugState != null)
                {
                    debugState.LastPlayStartedSegmentIndex = segment.Index;
                }

                if (verboseLogging)
                {
                    Debug.Log(
                        $"[KimodoInfiniteMotionDemo] Play segment {segment.Index} started. worldOffset={currentSegment.WorldAccumulatedOffset}");
                }

                return true;
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
                    Vector3 completedDelta = currentSegment.LastRootPosition - currentSegment.FirstRootPosition;
                    lastCompletedWorldOffset = currentSegment.WorldAccumulatedOffset + new Vector3(
                        completedDelta.x,
                        0f,
                        completedDelta.z);
                }
            }

            private void StopActiveMotion()
            {
                profileBinding = null;
                sourceBinding = null;
                profileRootJoint = null;
                currentSegment = null;
                currentSegmentRootBaseline = Vector3.zero;
                timeSeconds = 0f;
                playing = false;
            }

            private void DisposeRetargetCache()
            {
                DisposeSourceRetargetCache();
                DisposeTargetStates();
            }

            private void DisposeSourceRetargetCache()
            {
                sourceBinding = null;
                sourceCache?.Dispose();
                sourceCache = null;
                sourceCacheModelName = null;
            }

            private Vector3 ResolveNextWorldOffset(Vector3 nextSegmentFirstRootPosition)
            {
                return lastCompletedWorldOffset;
            }

            private bool TryCreateDirectRetargetBinding(
                KimodoRawMotionData motion,
                string modelName,
                IReadOnlyList<Animator> humanoidAnimators,
                bool allowPartialJoints,
                out string error)
            {
                error = string.Empty;
                if (!TrySyncTargetStates(humanoidAnimators, out bool hasTargets, out error))
                {
                    return false;
                }

                if (!hasTargets)
                {
                    sourceBinding = null;
                    return true;
                }

                if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar sourceAvatar, out error))
                {
                    return false;
                }

                if (sourceCache == null || !string.Equals(sourceCacheModelName, modelName, StringComparison.OrdinalIgnoreCase))
                {
                    DisposeSourceRetargetCache();
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
                sourceBinding.joints[0].localPosition = new Vector3(
                    currentSegment.WorldAccumulatedOffset.x + delta.x,
                    rootPosition.y,
                    currentSegment.WorldAccumulatedOffset.z + delta.z);
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
                sourceBinding.joints[0].localPosition = new Vector3(
                    currentSegment.WorldAccumulatedOffset.x + delta.x,
                    rootPosition.y,
                    currentSegment.WorldAccumulatedOffset.z + delta.z);
                return true;
            }

            private bool TryApplyHumanoidPose(out string error)
            {
                error = string.Empty;
                if (sourceCache == null || targetStates.Count == 0)
                {
                    return true;
                }

                if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(sourceCache, out MuscleSample sample, out error))
                {
                    return false;
                }

                HumanPose pose = sample.pose;
                for (int i = 0; i < targetStates.Count; i++)
                {
                    HumanPoseHandler poseHandler = targetStates[i].PoseHandler;
                    if (poseHandler == null)
                    {
                        continue;
                    }

                    poseHandler.SetHumanPose(ref pose);
                }

                return true;
            }

            private bool TrySyncTargetStates(
                IReadOnlyList<Animator> humanoidAnimators,
                out bool hasTargets,
                out string error)
            {
                error = string.Empty;
                hasTargets = false;

                var desiredAnimators = new HashSet<Animator>();
                if (humanoidAnimators != null)
                {
                    for (int i = 0; i < humanoidAnimators.Count; i++)
                    {
                        Animator animator = humanoidAnimators[i];
                        if (animator == null || !desiredAnimators.Add(animator))
                        {
                            continue;
                        }

                        Avatar avatar = animator.avatar;
                        if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
                        {
                            error = $"Humanoid retarget animator at index {i} has a null, invalid, or non-humanoid avatar.";
                            return false;
                        }

                        hasTargets = true;
                    }
                }

                for (int i = targetStates.Count - 1; i >= 0; i--)
                {
                    TargetRetargetState state = targetStates[i];
                    if (state == null || state.Animator == null || !desiredAnimators.Contains(state.Animator))
                    {
                        state?.Dispose();
                        targetStates.RemoveAt(i);
                    }
                }

                foreach (Animator animator in desiredAnimators)
                {
                    if (!TryEnsureTargetState(animator, out error))
                    {
                        return false;
                    }
                }

                return true;
            }

            private bool TryEnsureTargetState(Animator animator, out string error)
            {
                error = string.Empty;
                if (animator == null)
                {
                    return true;
                }

                Avatar avatar = animator.avatar;
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(avatar))
                {
                    error = "Humanoid retarget animator avatar is null, invalid, or not humanoid.";
                    return false;
                }

                TargetRetargetState state = null;
                for (int i = 0; i < targetStates.Count; i++)
                {
                    if (ReferenceEquals(targetStates[i].Animator, animator))
                    {
                        state = targetStates[i];
                        break;
                    }
                }

                bool needsNewState = state == null;
                bool needsNewPoseHandler = state == null || state.PoseHandler == null || !ReferenceEquals(state.Avatar, avatar);
                if (needsNewState)
                {
                    state = new TargetRetargetState
                    {
                        Animator = animator
                    };
                    targetStates.Add(state);
                }

                if (needsNewPoseHandler)
                {
                    state.Avatar = avatar;
                    state.PoseHandler = new HumanPoseHandler(avatar, animator.transform);
                }

                if (!state.AnimatorDisabledForRetarget)
                {
                    state.AnimatorWasEnabled = animator.enabled;
                    state.AnimatorDisabledForRetarget = true;
                }

                animator.enabled = false;
                return true;
            }

            private void DisposeTargetStates()
            {
                for (int i = targetStates.Count - 1; i >= 0; i--)
                {
                    targetStates[i]?.Dispose();
                }

                targetStates.Clear();
            }
        }
    }
}
