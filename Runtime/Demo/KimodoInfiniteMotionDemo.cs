using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TimelineInject;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KimodoBridge
{
    public sealed class KimodoInfiniteMotionDemo : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private Animator somaAnimator;
        [SerializeField] private Animator dancerAnimator;
        [SerializeField] private Component promptTextSource;
        [SerializeField] private Component statusTextTarget;

        [Header("Bridge Runtime")]
        [SerializeField] private string runtimeRoot = @"C:\nvlab\KimodoUnityBridge\NvlabKimodoQuickServer~";
        [SerializeField] private string modelsRoot = string.Empty;
        [SerializeField] private string modelName = "Kimodo-SOMA-RP-v1";
        [SerializeField] private bool highVram;
        [SerializeField] private bool forceSetup;

        [Header("Generation")]
        [SerializeField] private string defaultPrompt = "A person dancing with energetic rhythm.";
        [SerializeField] [Min(1)] private int generationFrames = 150;
        [SerializeField] [Min(1)] private int diffusionSteps = 100;
        [SerializeField] private bool randomSeed = true;
        [SerializeField] private int fixedSeed = 42;
        [SerializeField] [Min(0.1f)] private float requestLeadSeconds = 1.5f;
        [SerializeField] [Min(0.1f)] private float segmentIntervalSeconds = 5f;
        [SerializeField] private bool loopHint = true;

        [Header("Debug")]
        [SerializeField] private bool autoStartOnEnable;
        [SerializeField] private bool verboseLogging = true;

        private const string FullBodyConstraintType = "fullbody";

        private KimodoRuntimeGenerationService generationService;
        private CancellationTokenSource lifetimeCts;
        private Task schedulerTask;
        private bool running;
        private bool startRequested;

        private ClipPlayer somaPlayer;
        private ClipPlayer dancerPlayer;

        private readonly Queue<GeneratedSegment> pendingSegments = new Queue<GeneratedSegment>();
        private readonly object queueGate = new object();

        private bool generationInFlight;
        private int segmentIndex;
        private KimodoMarkerSampleResult nextConstraintPose;
        private bool manualSendRequested;
        private string promptDraft;

        private Avatar somaAvatar;
        private Avatar dancerAvatar;

        private void Awake()
        {
            somaPlayer = new ClipPlayer("KimodoInfiniteMotionDemo_Soma");
            dancerPlayer = new ClipPlayer("KimodoInfiniteMotionDemo_Dancer");
            promptDraft = ResolveInitialPrompt();
        }

        private void OnEnable()
        {
            if (ValidateConfiguration(out _))
            {
                try
                {
                    ResolveAvatars();
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
            somaPlayer.Update();
            dancerPlayer.Update();
            TryPromoteNextSegment();
        }

        private void OnGUI()
        {
            DrawPromptBar();
        }

        private void OnDestroy()
        {
            somaPlayer.Dispose();
            dancerPlayer.Dispose();
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

                ResolveAvatars();
                lifetimeCts?.Cancel();
                lifetimeCts?.Dispose();
                lifetimeCts = new CancellationTokenSource();

                segmentIndex = 0;
                nextConstraintPose = null;
                generationInFlight = false;
                manualSendRequested = false;
                ClearPendingSegments();

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

            ClearPendingSegments();
            generationInFlight = false;
            nextConstraintPose = null;
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
            if (!manualTrigger && PendingSegmentCount > 0)
            {
                return;
            }

            bool shouldGenerate;
            if (manualTrigger)
            {
                shouldGenerate = true;
            }
            else if (!somaPlayer.HasActiveClip)
            {
                shouldGenerate = PendingSegmentCount == 0;
            }
            else
            {
                shouldGenerate = somaPlayer.RemainingSeconds <= Mathf.Max(0.1f, requestLeadSeconds);
            }

            if (!shouldGenerate)
            {
                return;
            }

            manualSendRequested = false;
            _ = GenerateNextSegmentAsync(token);
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

                AnimationClip somaClip = BuildSomaClip(result.motionJsonCompact, segmentIndex);
                AnimationClip dancerClip = BuildDancerClip(somaClip, segmentIndex);
                KimodoMarkerSampleResult tailPose = BuildTailPoseSample(somaClip);

                EnqueueSegment(new GeneratedSegment
                {
                    Index = segmentIndex,
                    SomaClip = somaClip,
                    DancerClip = dancerClip,
                    TailPose = tailPose
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

        private AnimationClip BuildSomaClip(string motionJson, int index)
        {
            var clip = new AnimationClip
            {
                name = $"Kimodo_Soma_{index:D4}",
                frameRate = KimodoPlayableClip.FIXED_FRAME_RATE,
                legacy = true
            };

            if (!KimodoRuntimeClipBaker.TryBake(clip, motionJson, out string error))
            {
                UnityEngine.Object.Destroy(clip);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "SOMA clip bake failed." : error);
            }

            return clip;
        }

        private AnimationClip BuildDancerClip(AnimationClip somaClip, int index)
        {
            if (dancerAvatar == null)
            {
                throw new InvalidOperationException("Dancer avatar is null or invalid.");
            }

            AnimationClip dancerClip = UnityEngine.Object.Instantiate(somaClip);
            dancerClip.name = $"Kimodo_Dancer_{index:D4}";

            if (!KimodoRetargetTools.TryRetargetNew(
                    dancerClip,
                    somaAvatar,
                    dancerAvatar,
                    exportMuscleClip: true,
                    out AnimationClip retargetedClip,
                    out string error))
            {
                UnityEngine.Object.Destroy(dancerClip);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Dancer clip retarget failed." : error);
            }

            if (!ReferenceEquals(retargetedClip, dancerClip))
            {
                dancerClip = retargetedClip;
                dancerClip.name = $"Kimodo_Dancer_{index:D4}";
            }

            return dancerClip;
        }

        private KimodoMarkerSampleResult BuildTailPoseSample(AnimationClip somaClip)
        {
            if (somaClip == null)
            {
                return null;
            }

            double sampleTime = Math.Max(0.0, somaClip.length - (1.0 / KimodoPlayableClip.FIXED_FRAME_RATE));
            if (!KimodoMarkerSamplingUtility.TrySampleMarkerFromClipWithRetargetCore(
                    somaClip,
                    FullBodyConstraintType,
                    sampleTime,
                    somaAvatar,
                    somaAvatar,
                    modelName,
                    out KimodoMarkerSampleResult sample,
                    out string error))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Tail pose sampling failed." : error);
            }

            sample.constraintType = FullBodyConstraintType;
            sample.sampleTime = 0.0;
            return sample;
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
            return KimodoConstraintJsonExporter.ToConstraintsJson(
                new List<KimodoMarkerSampleResult> { sample },
                0.0,
                Mathf.Max(segmentIntervalSeconds, generationFrames / KimodoPlayableClip.FIXED_FRAME_RATE));
        }

        private void TryPromoteNextSegment()
        {
            if (!running)
            {
                return;
            }

            if (somaPlayer.IsPlaying)
            {
                return;
            }

            GeneratedSegment next;
            lock (queueGate)
            {
                if (pendingSegments.Count == 0)
                {
                    return;
                }

                next = pendingSegments.Dequeue();
            }

            somaPlayer.Play(somaAnimator, next.SomaClip, destroyClipOnDispose: true);
            dancerPlayer.Play(dancerAnimator, next.DancerClip, destroyClipOnDispose: true);
            nextConstraintPose = next.TailPose;
            UpdateStatus($"Playing segment {next.Index}.");
        }

        private void EnqueueSegment(GeneratedSegment segment)
        {
            lock (queueGate)
            {
                pendingSegments.Enqueue(segment);
            }
        }

        private int PendingSegmentCount
        {
            get
            {
                lock (queueGate)
                {
                    return pendingSegments.Count;
                }
            }
        }

        private void ClearPendingSegments()
        {
            lock (queueGate)
            {
                while (pendingSegments.Count > 0)
                {
                    GeneratedSegment segment = pendingSegments.Dequeue();
                    if (segment?.SomaClip != null)
                    {
                        UnityEngine.Object.Destroy(segment.SomaClip);
                    }

                    if (segment?.DancerClip != null)
                    {
                        UnityEngine.Object.Destroy(segment.DancerClip);
                    }
                }
            }
        }

        private KimodoRuntimeGenerationSettings BuildRuntimeGenerationSettings()
        {
            string resolvedRuntimeRoot = Path.GetFullPath(runtimeRoot);
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

        private void ResolveAvatars()
        {
            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out somaAvatar, out string somaError))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(somaError) ? "Failed to load SOMA avatar." : somaError);
            }

            dancerAvatar = dancerAnimator != null ? dancerAnimator.avatar : null;
            if (!KimodoRetargetTools.IsValidHumanoid(dancerAvatar))
            {
                throw new InvalidOperationException("Dancer animator avatar is null, invalid, or not humanoid.");
            }
        }

        private bool ValidateConfiguration(out string error)
        {
            if (somaAnimator == null)
            {
                error = "SOMA animator is not assigned.";
                return false;
            }

            if (dancerAnimator == null)
            {
                error = "Dancer animator is not assigned.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                error = "Runtime root is empty.";
                return false;
            }

            if (!Directory.Exists(runtimeRoot))
            {
                error = $"Runtime root does not exist: {runtimeRoot}";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private string ResolvePrompt()
        {
            string prompt = promptDraft;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                prompt = ReadTextProperty(promptTextSource);
            }
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

            if (Event.current.type == EventType.KeyDown &&
                GUI.GetNameOfFocusedControl() == "KimodoPromptInput" &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                Event.current.Use();
                RequestManualSend();
            }

            if (GUI.Button(buttonRect, "Send"))
            {
                RequestManualSend();
            }
        }

        private void RequestManualSend()
        {
            manualSendRequested = true;
            if (!running || generationService == null || generationInFlight)
            {
                return;
            }

            MaybeQueueNextGeneration(lifetimeCts != null ? lifetimeCts.Token : CancellationToken.None);
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
            WriteTextProperty(statusTextTarget, string.IsNullOrWhiteSpace(message) ? " " : message);
        }

        private static string ReadTextProperty(Component component)
        {
            if (component == null)
            {
                return string.Empty;
            }

            PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            if (property == null || property.PropertyType != typeof(string) || !property.CanRead)
            {
                return string.Empty;
            }

            try
            {
                return property.GetValue(component) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void WriteTextProperty(Component component, string value)
        {
            if (component == null)
            {
                return;
            }

            PropertyInfo property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            if (property == null || property.PropertyType != typeof(string) || !property.CanWrite)
            {
                return;
            }

            try
            {
                property.SetValue(component, value ?? string.Empty);
            }
            catch
            {
            }
        }

        private string ResolveInitialPrompt()
        {
            string prompt = ReadTextProperty(promptTextSource);
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

        private sealed class GeneratedSegment
        {
            public int Index;
            public AnimationClip SomaClip;
            public AnimationClip DancerClip;
            public KimodoMarkerSampleResult TailPose;
        }

        private sealed class ClipPlayer : IDisposable
        {
            private readonly string graphName;
            private PlayableGraph graph;
            private AnimationClipPlayable clipPlayable;
            private AnimationPlayableOutput output;
            private AnimationClip activeClip;
            private Animator targetAnimator;
            private bool destroyClipOnDispose;
            private bool playing;

            public ClipPlayer(string graphName)
            {
                this.graphName = string.IsNullOrWhiteSpace(graphName) ? "KimodoClipPlayer" : graphName;
            }

            public bool IsPlaying => playing;
            public bool HasActiveClip => activeClip != null;

            public float RemainingSeconds
            {
                get
                {
                    if (!playing || activeClip == null || !clipPlayable.IsValid())
                    {
                        return 0f;
                    }

                    double duration = Math.Max(0.0, activeClip.length);
                    double time = clipPlayable.GetTime();
                    if (duration <= 0.0)
                    {
                        return 0f;
                    }

                    return Mathf.Max(0f, (float)(duration - time));
                }
            }

            public void Play(Animator animator, AnimationClip clip, bool destroyClipOnDispose = false)
            {
                if (animator == null)
                {
                    throw new ArgumentNullException(nameof(animator));
                }

                if (clip == null)
                {
                    throw new ArgumentNullException(nameof(clip));
                }

                DisposeGraph();

                targetAnimator = animator;
                activeClip = clip;
                this.destroyClipOnDispose = destroyClipOnDispose;
                graph = PlayableGraph.Create(graphName);
                graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
                clipPlayable = AnimationClipPlayable.Create(graph, clip);
                clipPlayable.SetTime(0.0);
                output = AnimationPlayableOutput.Create(graph, graphName + "_Output", animator);
                output.SetSourcePlayable(clipPlayable);
                graph.Play();
                playing = true;
            }

            public void Update()
            {
                if (!playing || activeClip == null || !clipPlayable.IsValid())
                {
                    return;
                }

                double duration = clipPlayable.GetDuration();
                double time = clipPlayable.GetTime();
                if (duration > 0.0 && time >= duration)
                {
                    playing = false;
                }
            }

            public void Dispose()
            {
                DisposeGraph();
            }

            private void DisposeGraph()
            {
                if (graph.IsValid())
                {
                    graph.Destroy();
                }

                if (destroyClipOnDispose && activeClip != null)
                {
                    UnityEngine.Object.Destroy(activeClip);
                }

                graph = default;
                clipPlayable = default;
                output = default;
                targetAnimator = null;
                activeClip = null;
                destroyClipOnDispose = false;
                playing = false;
            }
        }
    }
}
