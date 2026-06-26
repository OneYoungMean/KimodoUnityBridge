using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TimelineInject;
using UnityEngine;
using UnityEngine.Animations;
using Process = System.Diagnostics.Process;

namespace KimodoBridge
{
    public sealed partial class KimodoInfiniteMotionDemo : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private Transform profileSkeletonRoot;
        [SerializeField] private List<Animator> humanoidRetargetAnimators = new List<Animator>();
        [SerializeField][Min(0.01f)] private float characterSwitchDurationSeconds = 0.3f;

        [Header("Bridge Runtime")]
        [SerializeField] private string modelsRoot = string.Empty;
        [SerializeField] private string modelName = "Kimodo-SOMA-RP-v1";
        [SerializeField] private bool highVram;
        [SerializeField] private bool forceSetup;
        [SerializeField][Min(1f)] private float startupTimeoutMinutes = 30f;

        [Header("Generation")]
        [SerializeField] private string defaultPrompt = "A person dancing with energetic rhythm.";
        [SerializeField][Min(1)] private int generationFrames = 150;
        [SerializeField][Min(1)] private int diffusionSteps = 100;
        [SerializeField] private bool randomSeed = true;
        [SerializeField] private int fixedSeed = 42;
        [SerializeField][Min(0.1f)] private float segmentIntervalSeconds = 5f;
        [SerializeField] private bool loopHint = true;
        [SerializeField] private bool allowPartialJoints;
        [SerializeField] private bool trimSegmentTail = true;
        [SerializeField][Range(0f, 0.2f)] private float segmentTailTrimPercent = 0.1f;

        [Header("Foot IK Targets")]
        [SerializeField] private bool driveFootIkTargets = true;
        [SerializeField] private string leftFootIkTargetName = "LeftFootIK";
        [SerializeField] private string rightFootIkTargetName = "RightFootIK";

        [Header("Debug")]
        [SerializeField] private bool autoStartOnEnable;
        [SerializeField] private bool verboseLogging = true;

        private const string FullBodyConstraintType = "fullbody";
        private const string KimodoFolderName = "NvlabKimodoQuickServer~";
        private const float MinGenerationDurationSeconds = 1f;
        private const float MaxGenerationDurationSeconds = 10f;
        private static readonly string[] RandomMotionPrompts =
        {
            "A woman walks and says hello.",
            "A person waves with a friendly smile.",
            "A person walks forward at an easy pace.",
            "A woman turns around and greets someone.",
            "A person jogs lightly in place.",
            "A person takes a few steps and looks around.",
            "A woman walks forward and raises one hand.",
            "A person steps sideways and keeps balance.",
            "A person walks in a small circle.",
            "A woman pauses and gestures while talking.",
            "A person walks forward and nods politely.",
            "A person sways to a gentle rhythm.",
            "A woman takes small quick steps.",
            "A person marches in place with relaxed arms.",
            "A person pivots and points ahead.",
            "A woman waves both hands in excitement.",
            "A person steps back and then forward again.",
            "A person stretches arms and shifts weight.",
            "A woman walks confidently and looks ahead.",
            "A person makes a cheerful hand gesture.",
            "A person takes careful steps to the side.",
            "A woman walks and lightly swings her arms.",
            "A person turns the torso and looks left and right.",
            "A person bounces gently with upbeat energy.",
            "A woman walks up and gives a small salute.",
            "A person gestures as if introducing something.",
            "A person takes a step forward and waves.",
            "A woman moves with calm rhythmic footwork.",
            "A person rotates in place with relaxed posture.",
            "A person walks and briefly reaches out a hand."
        };

        private KimodoBridgeService bridgeService;
        private CancellationTokenSource lifetimeCts;
        private Task schedulerTask;
        private bool running;
        private bool startRequested;

        private RawMotionPlayer motionPlayer;

        private bool generationInFlight;
        private int segmentIndex;
        private int lastGenerationWaitStatusSegment = -1;
        private KimodoMarkerSampleResult nextConstraintPose;
        private string promptDraft;
        private string statusMessage = "Idle.";
        private TransformSnapshot initialProfileSnapshot;
        private readonly List<TransformSnapshot> initialHumanoidSnapshots = new List<TransformSnapshot>();
        private readonly QueueDebugState queueDebugState = new QueueDebugState();
        private PositionConstraint hipsPositionConstraint;
        private readonly List<CharacterConstraintState> characterConstraintStates = new List<CharacterConstraintState>();
        private int currentCharacterStateIndex = -1;
        private Coroutine characterSwitchCoroutine;
        private bool preserveNextConstraintPoseOnStart;
        private int generationRequestVersion;

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

        private sealed class CharacterConstraintState
        {
            public Animator Animator;
            public Transform Hips;
            public int ConstraintSourceIndex;
        }

        private void Awake()
        {
            motionPlayer = new RawMotionPlayer();
            promptDraft = ResolveInitialPrompt();
            SyncGenerationDurationFromCurrentSettings();
            CacheInitialTransformSnapshots();
            InitializeCharacterConstraintStates();
        }

        private void OnEnable()
        {
            EnsureInitialTransformSnapshots();
            InitializeCharacterConstraintStates();
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
                driveFootIkTargets,
                leftFootIkTargetName,
                rightFootIkTargetName,
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
                UpdateStatus($"Playing segment {startedSegment.Index}.");
            }
        }

        private void OnGUI()
        {
            DrawPromptBar();
        }

        private void OnDestroy()
        {
            if (characterSwitchCoroutine != null)
            {
                StopCoroutine(characterSwitchCoroutine);
                characterSwitchCoroutine = null;
            }

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
                if (!preserveNextConstraintPoseOnStart)
                {
                    nextConstraintPose = null;
                }

                preserveNextConstraintPoseOnStart = false;
                generationInFlight = false;
                motionPlayer.ResetCompletionState();
                motionPlayer.ClearQueue();

                bridgeService?.Dispose();
                bridgeService = new KimodoBridgeService(BuildBridgeRuntimeSettings());

                UpdateStatus("Starting Kimodo bridge...");
                await bridgeService.StartAsync(OnProgress, lifetimeCts.Token);

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

            if (bridgeService != null)
            {
                try
                {
                    await bridgeService.StopAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[KimodoInfiniteMotionDemo] Stop bridge failed: {ex.Message}");
                }

                bridgeService.Dispose();
                bridgeService = null;
            }

            if (cts != null)
            {
                cts.Dispose();
            }

            generationInFlight = false;
            lastGenerationWaitStatusSegment = -1;
            nextConstraintPose = null;
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
            if (!running || generationInFlight || bridgeService == null)
            {
                return;
            }

            if (motionPlayer.QueuedSegmentCount > 0)
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
            if (generationInFlight || bridgeService == null)
            {
                return;
            }

            generationInFlight = true;
            int requestVersion = generationRequestVersion;
            try
            {
                string prompt = ResolvePrompt();
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
                string motionJson = await bridgeService.GenerateAsync(request, OnProgress, token);

                KimodoRawMotionMetadata metadata = await Task.Run(() =>
                {
                    if (!KimodoRawMotionUtility.TryParseAndAnalyze(
                            motionJson,
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

                int effectiveLastFrameIndex = ResolveEffectiveLastFrameIndex(metadata.Motion);
                if (!metadata.Motion.TryReadUnityRootPosition(effectiveLastFrameIndex, out Vector3 effectiveLastRootPosition))
                {
                    throw new InvalidOperationException(
                        $"Failed to read effective tail root position for frame {effectiveLastFrameIndex}.");
                }

                if (!KimodoRawMotionUtility.TryExtractMarkerSample(
                        metadata.Motion,
                        modelName,
                        effectiveLastFrameIndex,
                        out KimodoMarkerSampleResult effectiveTailPose,
                        out string tailError,
                        FullBodyConstraintType,
                        0.0,
                        allowPartialJoints))
                {
                    throw new InvalidOperationException(tailError);
                }

                if (requestVersion != generationRequestVersion || token.IsCancellationRequested)
                {
                    if (verboseLogging)
                    {
                        Debug.Log($"[KimodoInfiniteMotionDemo] Discard stale segment {segmentIndex} generation result.");
                    }

                    return;
                }

                KimodoMarkerSampleResult constraintTailPose = effectiveTailPose.Clone();
                constraintTailPose.kimodoRootPosition = new Vector3(0f, constraintTailPose.kimodoRootPosition.y, 0f);
                constraintTailPose.unityRootPos = constraintTailPose.kimodoRootPosition;

                motionPlayer.Enqueue(new GeneratedSegment
                {
                    Index = segmentIndex,
                    PromptText = prompt,
                    Motion = metadata.Motion,
                    ConstraintTailPose = constraintTailPose,
                    FirstRootPosition = metadata.FirstRootPosition,
                    LastRootPosition = effectiveLastRootPosition,
                    WorldAccumulatedOffset = Vector3.zero,
                    EffectiveLastFrameIndex = effectiveLastFrameIndex,
                    EffectiveLastFrameTimeSeconds = metadata.Motion.FrameRate > 0f
                        ? effectiveLastFrameIndex / metadata.Motion.FrameRate
                        : metadata.Motion.LastFrameTimeSeconds
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

        private BridgeRuntimeSettings BuildBridgeRuntimeSettings()
        {
            string resolvedRuntimeRoot = EnsureRuntimeRootReady();
            string launcherPath = BridgeLauncherResolver.ResolveStartScript(resolvedRuntimeRoot);
            if (string.IsNullOrWhiteSpace(launcherPath))
            {
                throw new FileNotFoundException($"Cannot resolve bridge launcher under '{resolvedRuntimeRoot}'.");
            }

            return new BridgeRuntimeSettings
            {
                runtimeRoot = resolvedRuntimeRoot,
                launcherPath = launcherPath,
                modelName = modelName,
                highVram = highVram,
                forceSetup = forceSetup,
                modelsRoot = string.IsNullOrWhiteSpace(modelsRoot) ? null : Path.GetFullPath(modelsRoot),
                ownerProcessId = Process.GetCurrentProcess().Id,
                startupTimeoutMs = Mathf.Max(
                    BridgeRuntimeSettings.DefaultStartupTimeoutMs,
                    Mathf.RoundToInt(Mathf.Max(1f, startupTimeoutMinutes) * 60f * 1000f))
            };
        }

        private bool ValidateConfiguration(out string error)
        {
            if (profileSkeletonRoot == null)
            {
                error = "Profile skeleton root is not assigned.";
                return false;
            }

            string resolvedRuntimeRoot = EnsureRuntimeRootReady();
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

        private string EnsureRuntimeRootReady()
        {
            return KimodoRuntimeBootstrapUtility.EnsureRuntimeRootForCurrentMode(ResolveRuntimeRoot());
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

        public async Task ResetDemoAsync()
        {
            promptDraft = ResolveInitialPrompt();
            nextConstraintPose = null;
            preserveNextConstraintPoseOnStart = false;
            generationRequestVersion++;
            lastGenerationWaitStatusSegment = -1;
            motionPlayer.ClearQueue();

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
            bool wasActive = running || startRequested || bridgeService != null;
            if (wasActive)
            {
                UpdateStatus("Prompt reset.");
                return;
            }

            if (generationInFlight)
            {
                UpdateStatus("Prompt reset. Waiting for current generation to finish.");
                return;
            }

            UpdateStatus("Prompt reset. Generating fresh segment.");
            await GenerateNextSegmentAsync(lifetimeCts.Token);
        }

        private void DrawPromptBar()
        {
            const float margin = 12f;
            const float panelHeight = 118f;
            const float buttonWidth = 110f;
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
                Mathf.Max(0f, panelRect.width - buttonWidth * 2f - 44f),
                fieldHeight);

            Rect resetButtonRect = new Rect(
                panelRect.xMax - buttonWidth - 12f,
                fieldRect.y,
                buttonWidth,
                fieldHeight);

            Rect randomButtonRect = new Rect(
                resetButtonRect.x - buttonWidth - 8f,
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
                Mathf.Max(0f, panelRect.width - buttonWidth - 208f),
                sliderHeight);

            Rect sliderValueRect = new Rect(
                sliderRect.xMax + 8f,
                sliderLabelRect.y,
                64f,
                sliderHeight);

            GUI.SetNextControlName("KimodoPromptInput");
            promptDraft = GUI.TextField(fieldRect, promptDraft ?? string.Empty);

            if (GUI.Button(randomButtonRect, "Random"))
            {
                ApplyRandomPrompt();
            }

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
            if (GUI.Button(resetButtonRect, "Reset"))
            {
                ResetDemo();
            }
        }

        private void ApplyRandomPrompt()
        {
            if (RandomMotionPrompts == null || RandomMotionPrompts.Length == 0)
            {
                promptDraft = ResolveInitialPrompt();
                return;
            }

            promptDraft = RandomMotionPrompts[UnityEngine.Random.Range(0, RandomMotionPrompts.Length)];
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

        private int ResolveEffectiveLastFrameIndex(KimodoRawMotionData motion)
        {
            if (motion == null || motion.FrameCount <= 1)
            {
                return 0;
            }

            int lastFrameIndex = motion.FrameCount - 1;
            if (!trimSegmentTail)
            {
                return lastFrameIndex;
            }

            float trimPercent = Mathf.Clamp(segmentTailTrimPercent, 0.05f, 0.2f);
            int trimmedFrameCount = Mathf.FloorToInt(lastFrameIndex * trimPercent);
            return Mathf.Clamp(lastFrameIndex - trimmedFrameCount, 1, lastFrameIndex);
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

        private void CancelCharacterSwitch()
        {
            if (characterSwitchCoroutine == null)
            {
                return;
            }

            StopCoroutine(characterSwitchCoroutine);
            characterSwitchCoroutine = null;
        }

        private void InitializeCharacterConstraintStates()
        {
            hipsPositionConstraint = hipsPositionConstraint != null
                ? hipsPositionConstraint
                : GetComponent<PositionConstraint>();
            characterConstraintStates.Clear();

            if (humanoidRetargetAnimators == null)
            {
                return;
            }

            int firstActiveIndex = -1;
            for (int i = 0; i < humanoidRetargetAnimators.Count; i++)
            {
                Animator animator = humanoidRetargetAnimators[i];
                if (animator == null)
                {
                    continue;
                }

                Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips == null)
                {
                    Debug.LogWarning($"[KimodoInfiniteMotionDemo] Animator '{animator.name}' has no Hips bone and will be skipped.");
                    continue;
                }

                int sourceIndex = characterConstraintStates.Count;
                characterConstraintStates.Add(new CharacterConstraintState
                {
                    Animator = animator,
                    Hips = hips,
                    ConstraintSourceIndex = sourceIndex
                });

                if (firstActiveIndex < 0 && animator.gameObject.activeSelf)
                {
                    firstActiveIndex = sourceIndex;
                }
            }

            if (firstActiveIndex < 0 && characterConstraintStates.Count > 0)
            {
                firstActiveIndex = 0;
            }

            currentCharacterStateIndex = firstActiveIndex;
            RebuildPositionConstraintSources();
            ApplyCharacterActivationState();
        }

        private void RebuildPositionConstraintSources()
        {
            if (hipsPositionConstraint == null)
            {
                return;
            }

            hipsPositionConstraint.constraintActive = false;
            hipsPositionConstraint.SetSources(new List<ConstraintSource>());
            for (int i = 0; i < characterConstraintStates.Count; i++)
            {
                CharacterConstraintState state = characterConstraintStates[i];
                var source = new ConstraintSource
                {
                    sourceTransform = state.Hips,
                    weight = i == currentCharacterStateIndex ? 1f : 0f
                };
                hipsPositionConstraint.AddSource(source);
                state.ConstraintSourceIndex = i;
                SetConstraintSourceWeightBySourceIndex(i, source.weight);
            }

            hipsPositionConstraint.constraintActive = characterConstraintStates.Count > 0;
        }

        private void ApplyCharacterActivationState()
        {
            for (int i = 0; i < characterConstraintStates.Count; i++)
            {
                CharacterConstraintState state = characterConstraintStates[i];
                bool active = i == currentCharacterStateIndex;
                if (state.Animator != null)
                {
                    state.Animator.gameObject.SetActive(active);
                }

                if (hipsPositionConstraint != null && state.ConstraintSourceIndex >= 0 && state.ConstraintSourceIndex < hipsPositionConstraint.sourceCount)
                {
                    SetConstraintSourceWeightBySourceIndex(state.ConstraintSourceIndex, active ? 1f : 0f);
                }
            }
        }

        private int ResolveNextCharacterStateIndex()
        {
            if (characterConstraintStates.Count < 2)
            {
                return -1;
            }

            int startIndex = currentCharacterStateIndex >= 0 ? currentCharacterStateIndex : 0;
            for (int step = 1; step <= characterConstraintStates.Count; step++)
            {
                int nextIndex = (startIndex + step) % characterConstraintStates.Count;
                if (nextIndex != currentCharacterStateIndex)
                {
                    return nextIndex;
                }
            }

            return -1;
        }

        private System.Collections.IEnumerator SwitchCharacterCoroutine(int fromIndex, int toIndex)
        {
            if (toIndex < 0 || toIndex >= characterConstraintStates.Count)
            {
                characterSwitchCoroutine = null;
                yield break;
            }

            CharacterConstraintState toState = characterConstraintStates[toIndex];
            if (toState.Animator != null)
            {
                toState.Animator.gameObject.SetActive(true);
            }

            float duration = Mathf.Max(0.01f, characterSwitchDurationSeconds);
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                SetConstraintWeight(fromIndex, 1f - t);
                SetConstraintWeight(toIndex, t);
                yield return null;
            }

            SetConstraintWeight(fromIndex, 0f);
            SetConstraintWeight(toIndex, 1f);

            if (fromIndex >= 0 && fromIndex < characterConstraintStates.Count)
            {
                CharacterConstraintState fromState = characterConstraintStates[fromIndex];
                if (fromState.Animator != null)
                {
                    fromState.Animator.gameObject.SetActive(false);
                }
            }

            currentCharacterStateIndex = toIndex;
            ApplyCharacterActivationState();
            characterSwitchCoroutine = null;
        }

        private void SetConstraintWeight(int stateIndex, float weight)
        {
            if (hipsPositionConstraint == null || stateIndex < 0 || stateIndex >= characterConstraintStates.Count)
            {
                return;
            }

            int sourceIndex = characterConstraintStates[stateIndex].ConstraintSourceIndex;
            if (sourceIndex < 0 || sourceIndex >= hipsPositionConstraint.sourceCount)
            {
                return;
            }

            SetConstraintSourceWeightBySourceIndex(sourceIndex, weight);
        }

        private void SetConstraintSourceWeightBySourceIndex(int sourceIndex, float weight)
        {
            if (hipsPositionConstraint == null || sourceIndex < 0 || sourceIndex >= hipsPositionConstraint.sourceCount)
            {
                return;
            }

            ConstraintSource source = hipsPositionConstraint.GetSource(sourceIndex);
            source.weight = Mathf.Clamp01(weight);
            hipsPositionConstraint.SetSource(sourceIndex, source);
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

    }
}
