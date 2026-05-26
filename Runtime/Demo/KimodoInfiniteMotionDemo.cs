using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityMotionTools.Generation;
using KimodoUnityMotionTools.Generation.Pipeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Animations;

namespace KimodoUnityMotionTools.Demo
{
    public sealed class KimodoInfiniteMotionDemo : MonoBehaviour
    {
        [Header("Target")]
        public Animator targetAnimator;
        public string prompt = "a person walks forward.";
        public KimodoBackendType backendType = KimodoBackendType.Bridge;
        public KimodoRuntimeGenerationSettings runtimeSettings;

        [Header("Loop")]
        [Range(1f, 10f)]
        public float segmentDurationSeconds = 4f;
        [Range(0.25f, 2f)]
        public float prefetchLeadSeconds = 1f;
        [Range(1, 4)]
        public int maxQueueSize = 2;
        [Range(1, 300)]
        public int diffusionSteps = 100;
        public bool autoStart = true;
        public bool loopHint = true;

        [Header("UI")]
        public bool showOverlay = true;

        private readonly Queue<MotionSegment> queuedSegments = new Queue<MotionSegment>();
        private CancellationTokenSource lifetimeCts;
        private KimodoGeneratePipeline pipeline;
        private PlayableGraph graph;
        private AnimationPlayableOutput output;
        private AnimationMixerPlayable mixer;
        private AnimationClipPlayable currentPlayable;
        private AnimationClipPlayable nextPlayable;
        private MotionSegment currentSegment;
        private MotionSegment transitionSegment;
        private bool started;
        private bool generationInFlight;
        private string status = "idle";
        private string error = string.Empty;
        private float transitionT;

        private void Awake()
        {
            pipeline = new KimodoGeneratePipeline();
            lifetimeCts = new CancellationTokenSource();
        }

        private void Start()
        {
            if (autoStart)
            {
                _ = StartDemoAsync();
            }
        }

        private void OnDisable()
        {
            StopDemo();
        }

        private void OnDestroy()
        {
            StopDemo();
        }

        private void Update()
        {
            if (!started || currentSegment == null || targetAnimator == null)
            {
                return;
            }

            float currentTime = GetCurrentClipTime();
            float currentLength = Mathf.Max(0.01f, currentSegment.Clip != null ? currentSegment.Clip.length : segmentDurationSeconds);
            float remaining = currentLength - currentTime;

            if (remaining <= prefetchLeadSeconds)
            {
                _ = EnsureNextSegmentAsync();
            }

            if (queuedSegments.Count > 0 && transitionSegment == null && remaining <= Mathf.Max(0.25f, prefetchLeadSeconds * 0.5f))
            {
                BeginTransition();
            }

            if (transitionSegment != null)
            {
                transitionT += Time.deltaTime / Mathf.Max(0.05f, transitionSegment.Duration);
                mixer.SetInputWeight(0, 1f - transitionT);
                mixer.SetInputWeight(1, transitionT);
                if (transitionT >= 1f)
                {
                    CommitTransition();
                }
            }
        }

        public async Task StartDemoAsync()
        {
            if (started)
            {
                return;
            }

            if (targetAnimator == null)
            {
                error = "Target Animator is not assigned.";
                status = "failed";
                return;
            }

            EnsurePlayableGraph();
            started = true;
            status = "starting";
            error = string.Empty;

            if (currentSegment == null)
            {
                currentSegment = await GenerateSegmentAsync(0, null, lifetimeCts.Token);
                if (currentSegment == null)
                {
                    started = false;
                    status = "failed";
                    return;
                }
            }

            PlaySegment(currentSegment, false);
            _ = EnsureNextSegmentAsync();
            status = "running";
        }

        public void StopDemo()
        {
            started = false;
            status = "stopped";

            lifetimeCts?.Cancel();
            lifetimeCts?.Dispose();
            lifetimeCts = new CancellationTokenSource();

            queuedSegments.Clear();
            if (graph.IsValid())
            {
                graph.Destroy();
            }

            DestroySegmentClips();
            currentSegment = null;
            transitionSegment = null;
            transitionT = 0f;
            generationInFlight = false;
        }

        private async Task EnsureNextSegmentAsync()
        {
            if (!started || generationInFlight)
            {
                return;
            }

            if (queuedSegments.Count >= maxQueueSize)
            {
                return;
            }

            generationInFlight = true;
            try
            {
                int index = (currentSegment?.Index ?? -1) + 1 + queuedSegments.Count;
                var segment = await GenerateSegmentAsync(index, CaptureBoundaryPose(), lifetimeCts.Token);
                if (segment != null && started)
                {
                    queuedSegments.Enqueue(segment);
                }
            }
            finally
            {
                generationInFlight = false;
            }
        }

        private async Task<MotionSegment> GenerateSegmentAsync(int index, string boundaryPoseJson, CancellationToken token)
        {
            if (pipeline == null)
            {
                pipeline = new KimodoGeneratePipeline();
            }

            var request = new KimodoGeneratePipelineRequest
            {
                BackendType = backendType,
                RuntimeSettings = runtimeSettings,
                GenerationRequest = new KimodoGenerationRequestDto
                {
                    prompt = prompt ?? string.Empty,
                    duration = Mathf.Max(0.25f, segmentDurationSeconds),
                    seed = null,
                    steps = Mathf.Max(1, diffusionSteps),
                    constraints_json = string.Empty,
                    boundary_pose_json = boundaryPoseJson ?? string.Empty,
                    loop_hint = loopHint,
                    segment_index = index,
                    transition_duration = Mathf.Min(0.5f, prefetchLeadSeconds)
                }
            };

            try
            {
                status = $"generating #{index}";
                var result = await pipeline.ExecuteAsync(
                    request,
                    (stage, message) => status = $"{stage}: {message}",
                    token);

                if (result == null || string.IsNullOrWhiteSpace(result.MotionJsonCompact))
                {
                    error = "Generation returned empty motion json.";
                    status = "failed";
                    return null;
                }

                if (!started)
                {
                    return null;
                }

                var clip = new AnimationClip
                {
                    name = $"KimodoSegment_{index}",
                    legacy = true
                };
                if (!KimodoUnityMotionTools.Ai.KimodoRuntimeClipBaker.TryBake(clip, result.MotionJsonCompact, out string bakeError))
                {
                    error = bakeError;
                    status = "failed";
                    return null;
                }

                return new MotionSegment(index, clip);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                status = "failed";
                return null;
            }
        }

        private void EnsurePlayableGraph()
        {
            if (graph.IsValid())
            {
                return;
            }

            graph = PlayableGraph.Create("KimodoInfiniteMotionDemo");
            graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            mixer = AnimationMixerPlayable.Create(graph, 2, true);
            output = AnimationPlayableOutput.Create(graph, "KimodoInfiniteMotionDemoOutput", targetAnimator);
            output.SetSourcePlayable(mixer);
            graph.Play();
        }

        private void PlaySegment(MotionSegment segment, bool asTransition)
        {
            if (segment == null || segment.Clip == null)
            {
                return;
            }

            if (!currentPlayable.IsValid())
            {
                currentPlayable = AnimationClipPlayable.Create(graph, segment.Clip);
                currentPlayable.SetApplyFootIK(false);
                currentPlayable.SetTime(0d);
                mixer.SetInputCount(1);
                mixer.ConnectInput(0, currentPlayable, 0, 1f);
                mixer.SetInputWeight(0, 1f);
                output.SetSourcePlayable(mixer);
            }
            else if (asTransition)
            {
                nextPlayable = AnimationClipPlayable.Create(graph, segment.Clip);
                nextPlayable.SetApplyFootIK(false);
                nextPlayable.SetTime(0d);
                mixer.SetInputCount(2);
                mixer.DisconnectInput(0);
                mixer.DisconnectInput(1);
                mixer.ConnectInput(0, currentPlayable, 0, 1f);
                mixer.ConnectInput(1, nextPlayable, 0, 0f);
                transitionSegment = segment;
                transitionT = 0f;
            }
            else
            {
                currentPlayable = AnimationClipPlayable.Create(graph, segment.Clip);
                currentPlayable.SetApplyFootIK(false);
                currentPlayable.SetTime(0d);
                mixer.SetInputCount(1);
                mixer.ConnectInput(0, currentPlayable, 0, 1f);
            }
        }

        private void BeginTransition()
        {
            if (queuedSegments.Count == 0 || transitionSegment != null)
            {
                return;
            }

            MotionSegment segment = queuedSegments.Dequeue();
            PlaySegment(segment, true);
        }

        private void CommitTransition()
        {
            if (transitionSegment == null)
            {
                return;
            }

            double nextTime = nextPlayable.IsValid() ? nextPlayable.GetTime() : 0d;
            currentSegment = transitionSegment;
            transitionSegment = null;
            transitionT = 0f;

            if (graph.IsValid())
            {
                graph.Destroy();
            }

            EnsurePlayableGraph();
            currentPlayable = AnimationClipPlayable.Create(graph, currentSegment.Clip);
            currentPlayable.SetApplyFootIK(false);
            currentPlayable.SetTime(nextTime);
            mixer.SetInputCount(1);
            mixer.ConnectInput(0, currentPlayable, 0, 1f);
            mixer.SetInputWeight(0, 1f);
            nextPlayable = default;
        }

        private float GetCurrentClipTime()
        {
            if (!currentPlayable.IsValid())
            {
                return 0f;
            }

            return (float)currentPlayable.GetTime();
        }

        private string CaptureBoundaryPose()
        {
            if (targetAnimator == null)
            {
                return string.Empty;
            }

            var root = targetAnimator.transform;
            var hips = targetAnimator.GetBoneTransform(HumanBodyBones.Hips) ?? root;
            var data = new KimodoBoundaryPoseDto
            {
                rootPosition = hips.position,
                rootRotation = hips.rotation
            };
            return JsonUtility.ToJson(data);
        }

        private void OnGUI()
        {
            if (!showOverlay)
            {
                return;
            }

            GUILayout.BeginArea(new Rect(10, 10, 360, 160), GUI.skin.box);
            GUILayout.Label($"Status: {status}");
            GUILayout.Label($"Error: {error}");
            GUILayout.Label($"Queue: {queuedSegments.Count}");
            GUILayout.Label($"Current: {(currentSegment != null ? currentSegment.Index.ToString() : "-")}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Start"))
            {
                _ = StartDemoAsync();
            }
            if (GUILayout.Button("Stop"))
            {
                StopDemo();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private sealed class MotionSegment
        {
            public MotionSegment(int index, AnimationClip clip)
            {
                Index = index;
                Clip = clip;
                Duration = clip != null ? clip.length : 0f;
            }

            public int Index { get; }
            public AnimationClip Clip { get; }
            public float Duration { get; }
        }

        private void DestroySegmentClips()
        {
            if (currentSegment?.Clip != null)
            {
                Destroy(currentSegment.Clip);
            }

            while (queuedSegments.Count > 0)
            {
                MotionSegment segment = queuedSegments.Dequeue();
                if (segment?.Clip != null)
                {
                    Destroy(segment.Clip);
                }
            }

            if (transitionSegment?.Clip != null)
            {
                Destroy(transitionSegment.Clip);
            }
        }
    }
}
