using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using KimodoUnityMotionTools;
using KimodoUnityMotionTools.ProjectEditor;
using KimodoUnityMotionTools.Bridge;
using KimodoUnityMotionTools.Generation;

namespace KimodoUnityMotionTools.ProjectEditor
{
    [CustomEditor(typeof(KimodoPlayableClip))]
    public class KimodoPlayableClipEditor : UnityEditor.Editor
    {
        private const float TargetFps = 30f;
        private const string GeneratedClipFolder = "Assets/KimodoGeneratedClips";
        private const string GeneratedClipNamePrefix = "Kimodo_";
        private const double BridgeStatusQueryCooldownSeconds = 2.0;

        private SerializedProperty generationBackend;
        private SerializedProperty comfyuiIP;
        private SerializedProperty comfyuiPort;
        private SerializedProperty bridgeModelName;
        private SerializedProperty bridgeVramMode;
        private SerializedProperty motionPrompt;
        private SerializedProperty generationFrames;
        private SerializedProperty numSamples;
        private SerializedProperty diffusionSteps;
        private SerializedProperty randomSeed;
        private SerializedProperty seed;
        private SerializedProperty enableInbetweenInterpolation;
        private SerializedProperty workflowJsonAsset;
        private SerializedProperty generationTimeoutSeconds;
        private SerializedProperty pollIntervalSeconds;

        private SerializedProperty animationClipProp;
        private SerializedProperty footIKProp;
        private SerializedProperty loopProp;
        private SerializedProperty savedSkeletonTypeProp;
        private SerializedProperty autoRetargetOnBindingProp;
        private SerializedProperty bakeUseRecorderSaveToClipProp;
        private SerializedProperty bakeEnsureQuaternionContinuityProp;
        private SerializedProperty curveFilterOptionsProp;
        private bool showBakeAdvanced = false;

        private KimodoPlayableClip clip;
        private bool isGenerating;
        private string lastStatus;
        private string lastError;
        private CancellationTokenSource generationCts;
        private int lastSubmittedSeed = int.MinValue;
        private string lastConstraintsPath = string.Empty;
        private string lastRetargetMode = "SOMA Fallback";
        private bool bridgeEnvAutoRetryInProgress;
        private bool bridgeStatusQueryInFlight;
        private int bridgeStatusQueryVersion;
        private double nextBridgeStatusQueryAt;
        private bool bridgeRunningCached;
        private bool bridgeStatusReady;

        private void OnEnable()
        {
            InitializeSerializedBindings();
            ScheduleBridgeStatusQuery(force: true);
        }

        private void InitializeSerializedBindings()
        {
            clip = (KimodoPlayableClip)target;
            generationBackend = serializedObject.FindProperty("generationBackend");
            comfyuiIP = serializedObject.FindProperty("comfyuiIP");
            comfyuiPort = serializedObject.FindProperty("comfyuiPort");
            bridgeModelName = serializedObject.FindProperty("bridgeModelName");
            bridgeVramMode = serializedObject.FindProperty("bridgeVramMode");
            motionPrompt = serializedObject.FindProperty("motionPrompt");
            generationFrames = serializedObject.FindProperty("generationFrames");
            numSamples = serializedObject.FindProperty("numSamples");
            diffusionSteps = serializedObject.FindProperty("diffusionSteps");
            randomSeed = serializedObject.FindProperty("randomSeed");
            seed = serializedObject.FindProperty("seed");
            enableInbetweenInterpolation = serializedObject.FindProperty("enableInbetweenInterpolation");
            workflowJsonAsset = serializedObject.FindProperty("workflowJsonAsset");
            generationTimeoutSeconds = serializedObject.FindProperty("generationTimeoutSeconds");
            pollIntervalSeconds = serializedObject.FindProperty("pollIntervalSeconds");

            animationClipProp = serializedObject.FindProperty("m_Clip");
            footIKProp = serializedObject.FindProperty("m_ApplyFootIK");
            loopProp = serializedObject.FindProperty("m_Loop");
            savedSkeletonTypeProp = serializedObject.FindProperty("savedSkeletonType");
            autoRetargetOnBindingProp = serializedObject.FindProperty("autoRetargetOnBinding");
            bakeUseRecorderSaveToClipProp = serializedObject.FindProperty("bakeUseRecorderSaveToClip");
            bakeEnsureQuaternionContinuityProp = serializedObject.FindProperty("bakeEnsureQuaternionContinuity");
            curveFilterOptionsProp = serializedObject.FindProperty("curveFilterOptions");
        }

        internal Task GenerateForTestsAsync()
        {
            InitializeSerializedBindings();
            return GenerateAsync();
        }

        internal void CancelGenerationForTests()
        {
            CancelGenerationInternal();
        }

        internal bool IsGeneratingForTests => isGenerating;

        internal string LastStatusForTests => lastStatus;

        internal string LastErrorForTests => lastError;

        internal void SetBridgeGenerationInputsForTests(
            string prompt,
            int generationFramesValue,
            int diffusionStepsValue,
            bool randomSeedEnabled,
            int seedValue)
        {
            InitializeSerializedBindings();
            serializedObject.Update();
            generationBackend.intValue = (int)KimodoGenerationBackend.KimodoBridge;
            motionPrompt.stringValue = prompt ?? string.Empty;
            generationFrames.intValue = Mathf.Clamp(generationFramesValue, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES);
            diffusionSteps.intValue = Mathf.Clamp(diffusionStepsValue, 1, 1000);
            randomSeed.boolValue = randomSeedEnabled;
            seed.intValue = seedValue;
            numSamples.intValue = Mathf.Clamp(numSamples.intValue <= 0 ? 1 : numSamples.intValue, 1, 8);
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private void OnDisable()
        {
            CancelGenerationInternal();
            bridgeStatusQueryVersion++;
            bridgeStatusQueryInFlight = false;
            EditorUtility.ClearProgressBar();
        }

        public override void OnInspectorGUI()
        {
            if (clip == null)
            {
                EditorGUILayout.HelpBox("Target clip is null.", MessageType.Error);
                return;
            }

            ScheduleBridgeStatusQuery(force: false);
            serializedObject.Update();
            DrawGenerationSection();
            DrawAnimationClipSection();
            DrawBakeSection();
            DrawErrorSection();
            DrawGeneratedInfo();
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawGenerationSection()
        {
            EditorGUILayout.LabelField("Generate Motion", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (generationBackend != null)
            {
                EditorGUILayout.PropertyField(generationBackend, new GUIContent("Backend"));
            }

            bool useBridge = clip.generationBackend == KimodoGenerationBackend.KimodoBridge;
            if (useBridge)
            {
                if (bridgeModelName != null)
                {
                    DrawBridgeModelSelector();
                }
                if (bridgeVramMode != null)
                {
                    EditorGUILayout.PropertyField(
                        bridgeVramMode,
                        new GUIContent("VRAM Mode", "Low: quantized text encoder (~4G). High: full Llama+LLM2Vec (~16G)."));
                }

                int encoderVramGb = clip.bridgeVramMode == KimodoBridgeVramMode.High ? 16 : 4;
                int totalVramGb = 2 + encoderVramGb;
                EditorGUILayout.HelpBox(
                    $"Estimated VRAM for selected mode: ~{totalVramGb} GB (core 2 GB + encoder {encoderVramGb} GB).",
                    MessageType.Info);
            }
            else
            {
                comfyuiIP.stringValue = EditorGUILayout.TextField("ComfyUI IP", comfyuiIP.stringValue);
                comfyuiPort.intValue = EditorGUILayout.IntField("ComfyUI Port", comfyuiPort.intValue);
                EditorGUILayout.HelpBox("Workflow source is fixed to Runtime/Resources/kimodo-unity-workflow.json.", MessageType.Info);
            }

            motionPrompt.stringValue = EditorGUILayout.TextArea(motionPrompt.stringValue, GUILayout.Height(60));

            int oldFrames = generationFrames.intValue;
            int newFrames = EditorGUILayout.IntSlider("Duration (frames)", oldFrames, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES);
            if (newFrames != oldFrames)
            {
                generationFrames.intValue = newFrames;
                TrySyncTimelineDuration(newFrames);
            }

            numSamples.intValue = Mathf.Clamp(EditorGUILayout.IntField("Num Samples", numSamples.intValue), 1, 8);
            diffusionSteps.intValue = Mathf.Clamp(EditorGUILayout.IntField("Diffusion Steps", diffusionSteps.intValue), 1, 1000);

            EditorGUILayout.BeginHorizontal();
            randomSeed.boolValue = EditorGUILayout.ToggleLeft("Random Seed", randomSeed.boolValue, GUILayout.Width(110f));
            EditorGUI.BeginDisabledGroup(randomSeed.boolValue);
            seed.intValue = EditorGUILayout.IntField("Seed", seed.intValue);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            if (enableInbetweenInterpolation != null)
            {
                EditorGUILayout.PropertyField(
                    enableInbetweenInterpolation,
                    new GUIContent("In-between Interpolation", "Use neighboring clip boundary poses as constraints to generate in-between motion."));
            }

            generationTimeoutSeconds.floatValue = Mathf.Max(10f, EditorGUILayout.FloatField("Timeout (sec)", generationTimeoutSeconds.floatValue));
            pollIntervalSeconds.floatValue = Mathf.Max(0.1f, EditorGUILayout.FloatField("Poll Interval (sec)", pollIntervalSeconds.floatValue));

            float seconds = generationFrames.intValue / TargetFps;
            EditorGUILayout.LabelField($"Duration: {seconds:F2}s", EditorStyles.miniLabel);

            GUI.enabled = !isGenerating;
            if (GUILayout.Button("Generate & Bake", GUILayout.Height(32)))
            {
                _ = GenerateAsync();
            }
            GUI.enabled = isGenerating;
            if (GUILayout.Button("Cancel", GUILayout.Height(24)))
            {
                CancelGenerationInternal();
                lastStatus = "Generation canceled.";
            }
            GUI.enabled = true;

            if (useBridge)
            {
                DrawEstimatedSetupTimeHint();
            }

            if (useBridge)
            {
                if (!bridgeStatusReady)
                {
                    EditorGUILayout.LabelField("Bridge status: checking...", EditorStyles.miniLabel);
                }

                EditorGUI.BeginDisabledGroup(!bridgeRunningCached);
                if (GUILayout.Button("Close Bridge Server", GUILayout.Height(22)))
                {
                    _ = KimodoBridgeController.CloseServerAsync();
                }
                EditorGUI.EndDisabledGroup();
            }

            if (!string.IsNullOrWhiteSpace(lastStatus))
            {
                EditorGUILayout.LabelField(lastStatus, EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void ScheduleBridgeStatusQuery(bool force)
        {
            if (clip == null || clip.generationBackend != KimodoGenerationBackend.KimodoBridge)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (!force && (bridgeStatusQueryInFlight || now < nextBridgeStatusQueryAt))
            {
                return;
            }

            bridgeStatusQueryInFlight = true;
            nextBridgeStatusQueryAt = now + BridgeStatusQueryCooldownSeconds;
            int queryVersion = ++bridgeStatusQueryVersion;
            string runtimeRoot = KimodoBridgeController.GetRuntimeRootPath();
            _ = QueryBridgeStatusAsync(runtimeRoot, queryVersion);
        }

        private async Task QueryBridgeStatusAsync(string runtimeRoot, int queryVersion)
        {
            bool running = false;
            try
            {
                running = await Task.Run(() =>
                {
                    if (string.IsNullOrWhiteSpace(runtimeRoot) || !Directory.Exists(runtimeRoot))
                    {
                        return false;
                    }

                    if (!KimodoBridgeController.TryReadServerPort(runtimeRoot, out string host, out int port))
                    {
                        return false;
                    }

                    return KimodoBridgeController.IsServerResponsive(host, port);
                });
            }
            catch
            {
                running = false;
            }

            if (queryVersion != bridgeStatusQueryVersion)
            {
                return;
            }

            bridgeRunningCached = running;
            bridgeStatusReady = true;
            bridgeStatusQueryInFlight = false;
            Repaint();
        }

        private void DrawAnimationClipSection()
        {
            EditorGUILayout.LabelField("Animation Clip", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (animationClipProp != null)
            {
                EditorGUILayout.PropertyField(animationClipProp, new GUIContent("Clip"));
            }
            else
            {
                EditorGUILayout.HelpBox("Clip property not found.", MessageType.Warning);
            }

            if (footIKProp != null)
            {
                EditorGUILayout.PropertyField(footIKProp, new GUIContent("Foot IK"));
            }

            if (loopProp != null)
            {
                EditorGUILayout.PropertyField(loopProp, new GUIContent("Loop"));
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void DrawBakeSection()
        {
            EditorGUILayout.LabelField("Animation Bake", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (savedSkeletonTypeProp != null)
            {
                EditorGUILayout.PropertyField(savedSkeletonTypeProp, new GUIContent("Skeleton Type"));
            }

            if (autoRetargetOnBindingProp != null)
            {
                EditorGUILayout.PropertyField(autoRetargetOnBindingProp, new GUIContent("Auto Retarget On Binding"));
            }

            showBakeAdvanced = EditorGUILayout.Foldout(showBakeAdvanced, "Advanced / CurveFilterOptions", true);
            if (showBakeAdvanced)
            {
                EditorGUI.indentLevel++;

                if (bakeUseRecorderSaveToClipProp != null)
                {
                    EditorGUILayout.PropertyField(
                        bakeUseRecorderSaveToClipProp,
                        new GUIContent(
                            "Use Recorder SaveToClip",
                            "Use GameObjectRecorder.SaveToClip(..., fps, CurveFilterOptions). Disable to fallback to direct SetCurve path."));
                }

                if (bakeEnsureQuaternionContinuityProp != null)
                {
                    EditorGUILayout.PropertyField(
                        bakeEnsureQuaternionContinuityProp,
                        new GUIContent(
                            "Ensure Quaternion Continuity",
                            "Call AnimationClip.EnsureQuaternionContinuity() after bake."));
                }

                bool enableFilterOptions = bakeUseRecorderSaveToClipProp != null && bakeUseRecorderSaveToClipProp.boolValue;
                EditorGUI.BeginDisabledGroup(!enableFilterOptions);
                DrawCurveFilterOptionsFields();
                EditorGUI.EndDisabledGroup();

                EditorGUI.indentLevel--;
            }

            if (clip != null && clip.savedSkeletonType != KimodoBakeSkeletonType.SOMA)
            {
                clip.savedSkeletonType = KimodoBakeSkeletonType.SOMA;
                EditorUtility.SetDirty(clip);
            }

            EditorGUILayout.LabelField("Saved As: SOMA", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Retarget Result: {lastRetargetMode}", EditorStyles.miniLabel);
            EditorGUILayout.HelpBox("Baking is now part of 'Generate & Bake'.", MessageType.Info);

            EditorGUILayout.EndVertical();
        }

        private async Task GenerateAsync()
        {
            if (isGenerating)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(motionPrompt.stringValue))
            {
                lastError = "Prompt is empty.";
                Repaint();
                return;
            }

            isGenerating = true;
            lastError = string.Empty;
            lastStatus = $"Generating: {motionPrompt.stringValue}";
            generationCts = new CancellationTokenSource();
            Repaint();
            bool retryAfterReset = false;

            try
            {
                UnityEngine.Timeline.TimelineClip timelineClip = FindTimelineClipForAsset(clip);
                string constraintsFilePath = string.Empty;
                if (timelineClip != null)
                {
                    if (!KimodoInbetweenConstraintUtility.TryBuildAndWriteConstraintsFile(
                        timelineClip,
                        clip.enableInbetweenInterpolation,
                        Mathf.Max(1, generationFrames.intValue),
                        out constraintsFilePath,
                        out string constraintError))
                    {
                        Debug.LogWarning($"[Kimodo] Constraint export skipped: {constraintError}");
                        constraintsFilePath = string.Empty;
                    }
                }
                int effectiveSeed = ResolveEffectiveSeed();
                lastConstraintsPath = constraintsFilePath;
                string motionJson = await GenerateMotionJsonViaRuntimeServiceAsync(constraintsFilePath, effectiveSeed, generationCts.Token);
                if (string.IsNullOrWhiteSpace(motionJson))
                {
                    throw new Exception("No motion json found in workflow outputs.");
                }

                CreateAndAssignNewAnimationClip();
                ApplyMotionJsonToClip(motionJson);
                BakeCurrentMotionData();
                if (string.IsNullOrEmpty(lastError))
                {
                    TrimGeneratedClipsToLimit();
                }
                lastStatus = "Generation complete.";
            }
            catch (OperationCanceledException)
            {
                lastStatus = "Generation canceled.";
            }
            catch (Exception e)
            {
                lastError = e.Message;
                lastStatus = "Generation failed.";
                Debug.LogError($"[Kimodo] Generate failed: {e}");
                retryAfterReset = await TryHandleBridgeBuildFailureAndMaybeRetryAsync();
            }
            finally
            {
                isGenerating = false;
                CancelGenerationInternal();
                EditorUtility.ClearProgressBar();
                Repaint();
            }

            if (retryAfterReset)
            {
                try
                {
                    await GenerateAsync();
                }
                finally
                {
                    bridgeEnvAutoRetryInProgress = false;
                }
            }
        }

        private async Task<bool> TryHandleBridgeBuildFailureAndMaybeRetryAsync()
        {
            if (clip == null || clip.generationBackend != KimodoGenerationBackend.KimodoBridge)
            {
                return false;
            }

            if (bridgeEnvAutoRetryInProgress)
            {
                return false;
            }

            string runtimeRoot = KimodoBridgeController.GetRuntimeRootPath();
            if (KimodoBridgeController.IsSetupRunning(runtimeRoot))
            {
                return false;
            }

            bool shouldReset = EditorUtility.DisplayDialog(
                "Kimodo",
                $"Build environment failed. Delete [{runtimeRoot}] and regenerate?",
                "Delete and Retry",
                "Cancel");
            if (!shouldReset)
            {
                return false;
            }

            bridgeEnvAutoRetryInProgress = true;
            try
            {
                await KimodoBridgeController.CloseServerAsync();
                if (Directory.Exists(runtimeRoot))
                {
                    Directory.Delete(runtimeRoot, true);
                }

                lastError = string.Empty;
                lastStatus = "Runtime folder removed. Regenerating...";
                Repaint();
                return true;
            }
            catch (Exception deleteException)
            {
                bridgeEnvAutoRetryInProgress = false;
                lastError = $"Failed to reset runtime folder: {deleteException.Message}";
                lastStatus = "Generation failed.";
                Repaint();
                return false;
            }
        }

        private int ResolveEffectiveSeed()
        {
            int effectiveSeed = randomSeed.boolValue
                ? Guid.NewGuid().GetHashCode() & int.MaxValue
                : seed.intValue;

            if (randomSeed.boolValue)
            {
                seed.intValue = effectiveSeed;
            }

            if (effectiveSeed == lastSubmittedSeed)
            {
                unchecked { effectiveSeed = effectiveSeed + 1; }
                seed.intValue = effectiveSeed;
                lastStatus = $"Seed auto-incremented to {effectiveSeed} to avoid cache hit.";
            }

            lastSubmittedSeed = effectiveSeed;
            return effectiveSeed;
        }

        private async Task<string> GenerateMotionJsonViaRuntimeServiceAsync(string constraintsFilePath, int effectiveSeed, CancellationToken token)
        {
            string expectedRuntimeRoot = KimodoBridgeController.GetRuntimeRootPath();
            if (!Directory.Exists(expectedRuntimeRoot))
            {
                lastStatus = "First setup: preparing NvlabKimodoQuickServer...";
                Repaint();
                Debug.Log("[Kimodo] First setup: runtime root missing, bootstrapping from package template.");
            }

            string kimodoRootPath = KimodoBridgeController.ResolveRuntimeRootOrThrow();
            string launcherPath = KimodoBridgeController.ResolveStartScriptOrThrow(kimodoRootPath);
            string modelName = string.IsNullOrWhiteSpace(clip.bridgeModelName) ? "Kimodo-SOMA-RP-v1" : clip.bridgeModelName.Trim();
            bool highVram = clip.bridgeVramMode == KimodoBridgeVramMode.High;
            float durationSeconds = generationFrames.intValue / TargetFps;
            string modelsRoot = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim();
            if (!string.IsNullOrWhiteSpace(modelsRoot))
            {
                modelsRoot = Path.GetFullPath(modelsRoot);
            }

            Debug.Log($"[Kimodo] Prompt: {motionPrompt.stringValue}");
            if (!string.IsNullOrWhiteSpace(modelsRoot))
            {
                Debug.Log($"[Kimodo] Using custom models root: {modelsRoot}");
            }

            var settings = new KimodoRuntimeGenerationSettings
            {
                bridgeSettings = new BridgeRuntimeSettings
                {
                    runtimeRoot = kimodoRootPath,
                    launcherPath = launcherPath,
                    modelName = modelName,
                    highVram = highVram,
                    modelsRoot = modelsRoot,
                    startupTimeoutMs = ComputeBridgeStartupTimeoutMs(kimodoRootPath, highVram, modelName)
                },
                comfyHost = comfyuiIP.stringValue,
                comfyPort = comfyuiPort.intValue,
                comfyTimeoutSeconds = generationTimeoutSeconds.floatValue,
                comfyPollIntervalSeconds = pollIntervalSeconds.floatValue,
                comfyWorkflowResourceName = "kimodo-unity-workflow"
            };

            KimodoBackendType backendType = clip.generationBackend == KimodoGenerationBackend.KimodoBridge
                ? KimodoBackendType.Bridge
                : KimodoBackendType.ComfyUi;

            using var runtimeService = new KimodoRuntimeGenerationService(settings);
            if (backendType == KimodoBackendType.Bridge)
            {
                await runtimeService.StartAsync(
                    backendType,
                    progress =>
                    {
                        lastStatus = progress;
                        Repaint();
                    },
                    token);
            }

            lastStatus = $"Generating: {motionPrompt.stringValue}";
            Repaint();

            var request = new KimodoGenerationRequestDto
            {
                prompt = motionPrompt.stringValue,
                duration = durationSeconds,
                seed = effectiveSeed,
                steps = diffusionSteps.intValue,
                constraints_json = constraintsFilePath ?? string.Empty
            };

            KimodoGenerationResultDto result = await runtimeService.GenerateAsync(
                request,
                backendType,
                progress =>
                {
                    lastStatus = progress;
                    Repaint();
                },
                token);

            if (result == null || string.IsNullOrWhiteSpace(result.motionJsonCompact))
            {
                throw new Exception(result?.message ?? "No motion json found in runtime generation result.");
            }

            return result.motionJsonCompact;
        }

        private int ComputeBridgeStartupTimeoutMs(string runtimeRoot, bool highVram, string modelName)
        {
            int requestedMs = Math.Max(30000, Mathf.RoundToInt(generationTimeoutSeconds.floatValue * 1000f));
            int timeoutMs = requestedMs;

            int points = KimodoServerRuntimeUtil.EstimateMissingConfigPoints(runtimeRoot, highVram, modelName);
            if (points > 0)
            {
                int minutes = Math.Max(3, points * 3);
                int dynamicMs = (int)Math.Round(Math.Max(600f, minutes * 60f) * 1000f);
                timeoutMs = Math.Max(timeoutMs, dynamicMs);
            }

            return timeoutMs;
        }

        private void DrawBridgeModelSelector()
        {
            string current = string.IsNullOrWhiteSpace(bridgeModelName.stringValue) ? "Kimodo-SOMA-RP-v1" : bridgeModelName.stringValue.Trim();
            string[] options = KimodoBridgeController.SupportedModelNames;
            int idx = Array.IndexOf(options, current);
            if (idx < 0)
            {
                idx = 0;
            }

            int newIdx = EditorGUILayout.Popup(new GUIContent("Bridge Model"), idx, options);
            bridgeModelName.stringValue = options[Mathf.Clamp(newIdx, 0, options.Length - 1)];
        }

        private void DrawEstimatedSetupTimeHint()
        {
            string runtimeRoot = KimodoBridgeController.GetRuntimeRootPath();
            bool highVram = clip != null && clip.bridgeVramMode == KimodoBridgeVramMode.High;
            string modelName = clip == null || string.IsNullOrWhiteSpace(clip.bridgeModelName) ? "Kimodo-SOMA-RP-v1" : clip.bridgeModelName.Trim();
            string modelsRootOverride = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim();
            if (!KimodoBridgeController.TryGetModelMissingSetupMinutes(runtimeRoot, highVram, modelName, modelsRootOverride, out int minutes))
            {
                return;
            }
            EditorGUILayout.HelpBox($"Model missing detected, update required, approximately {minutes} minutes.", MessageType.None);
        }

        private void CancelGenerationInternal()
        {
            CancellationTokenSource cts = Interlocked.Exchange(ref generationCts, null);
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
            catch (ObjectDisposedException)
            {
                // Already disposed by another path.
            }
            finally
            {
                cts.Dispose();
            }
        }

        private void ApplyMotionJsonToClip(string motionJson)
        {
            JObject obj = JObject.Parse(motionJson);

            Undo.RecordObject(clip, "Apply Kimodo Motion");
            clip.motionData = motionJson;
            clip.lastGeneratedPrompt = motionPrompt.stringValue;
            clip.isGenerated = true;

            clip.frameCount = obj.Value<int?>("num_frames") ?? 0;
            clip.jointCount = obj.Value<int?>("num_joints") ?? 0;
            clip.fps = obj.Value<int?>("fps") ?? 30;

            if (obj["joint_names"] is JArray names)
            {
                string[] arr = new string[names.Count];
                for (int i = 0; i < names.Count; i++)
                {
                    arr[i] = names[i]?.ToString();
                }
                clip.jointNames = arr;
            }
            else
            {
                clip.jointNames = null;
            }

            if (obj["joints"] is JArray joints)
            {
                float[] arr = new float[joints.Count];
                for (int i = 0; i < joints.Count; i++)
                {
                    arr[i] = joints[i] != null ? joints[i].Value<float>() : 0f;
                }
                clip.motionPositions = arr;
            }
            else
            {
                clip.motionPositions = null;
            }

            clip.savedSkeletonType = KimodoBakeSkeletonType.SOMA;
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
        }

        private void BakeCurrentMotionData()
        {
            if (clip == null || clip.clip == null || string.IsNullOrWhiteSpace(clip.motionData))
            {
                lastError = "Clip / motionData is missing.";
                return;
            }

            Undo.RecordObject(clip, "Bake Kimodo Motion");
            string error;
            bool ok = KimodoUnityMotionTools.KimodoAnimationBaker.BakeIntoClip(
                targetClip: clip.clip,
                motionJson: clip.motionData,
                skeletonType: KimodoBakeSkeletonType.SOMA,
                useRecorderSaveToClip: clip.bakeUseRecorderSaveToClip,
                ensureQuaternionContinuity: clip.bakeEnsureQuaternionContinuity,
                curveFilterSettings: clip.curveFilterOptions,
                out error
            );

            if (!ok)
            {
                lastError = error;
                lastStatus = string.Empty;
                Debug.LogWarning($"[Kimodo] Bake failed: {error}");
                return;
            }

            clip.savedSkeletonType = KimodoBakeSkeletonType.SOMA;
            clip.isGenerated = true;
            EditorUtility.SetDirty(clip);
            EditorUtility.SetDirty(clip.clip);
            AssetDatabase.SaveAssets();

            lastRetargetMode = "SOMA Fallback";
            if (clip.autoRetargetOnBinding)
            {
                TimelineClip timelineClip = FindTimelineClipForAsset(clip);
                bool retargetOk = KimodoRetargetPipeline.TryRetargetBakedClip(
                    clip,
                    timelineClip,
                    out KimodoRetargetResultMode retargetMode,
                    out string retargetDetails);

                if (retargetOk)
                {
                    lastRetargetMode = retargetMode switch
                    {
                        KimodoRetargetResultMode.HumanoidMuscle => "Humanoid Muscle",
                        KimodoRetargetResultMode.TargetBone => "Target Bone",
                        _ => "SOMA Fallback"
                    };
                    Debug.Log($"[Kimodo] Retarget success. {retargetDetails}");
                    EditorUtility.SetDirty(clip.clip);
                    AssetDatabase.SaveAssets();
                }
                else
                {
                    lastRetargetMode = "SOMA Fallback";
                    Debug.LogWarning($"[Kimodo] Retarget fallback to SOMA. {retargetDetails}");
                }
            }

            RefreshTimelinePreviewGraph();

            lastError = string.Empty;
            lastStatus = "Bake complete.";
            Debug.Log("[Kimodo] Bake complete (SOMA).");
        }

        private void DrawErrorSection()
        {
            if (!string.IsNullOrEmpty(lastError))
            {
                EditorGUILayout.HelpBox(lastError, MessageType.Error);
            }
        }

        private void DrawCurveFilterOptionsFields()
        {
            if (curveFilterOptionsProp == null)
            {
                return;
            }

            SerializedProperty keyframeReduction = curveFilterOptionsProp.FindPropertyRelative("keyframeReduction");
            SerializedProperty positionError = curveFilterOptionsProp.FindPropertyRelative("positionError");
            SerializedProperty rotationError = curveFilterOptionsProp.FindPropertyRelative("rotationError");
            SerializedProperty scaleError = curveFilterOptionsProp.FindPropertyRelative("scaleError");
            SerializedProperty floatError = curveFilterOptionsProp.FindPropertyRelative("floatError");
            SerializedProperty unrollRotation = curveFilterOptionsProp.FindPropertyRelative("unrollRotation");

            if (keyframeReduction != null)
            {
                EditorGUILayout.PropertyField(
                    keyframeReduction,
                    new GUIContent("Keyframe Reduction", "Enable keyframe reduction in CurveFilterOptions."));
            }
            if (positionError != null)
            {
                EditorGUILayout.Slider(
                    positionError,
                    0f,
                    1f,
                    new GUIContent("Position Error", "Allowed position curve deviation percentage."));
            }
            if (rotationError != null)
            {
                EditorGUILayout.Slider(
                    rotationError,
                    0f,
                    1f,
                    new GUIContent("Rotation Error", "Allowed rotation curve deviation in degrees."));
            }
            if (scaleError != null)
            {
                EditorGUILayout.Slider(
                    scaleError,
                    0f,
                    1f,
                    new GUIContent("Scale Error", "Allowed scale curve deviation percentage."));
            }
            if (floatError != null)
            {
                EditorGUILayout.Slider(
                    floatError,
                    0f,
                    1f,
                    new GUIContent("Float Error", "Allowed float curve deviation percentage."));
            }
            if (unrollRotation != null)
            {
                EditorGUILayout.PropertyField(
                    unrollRotation,
                    new GUIContent("Unroll Rotation", "If supported by this Unity version, attempt rotation unroll."));
            }
        }

        private void DrawGeneratedInfo()
        {
            if (!clip.isGenerated)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Generated", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (!string.IsNullOrWhiteSpace(clip.lastGeneratedPrompt))
            {
                EditorGUILayout.LabelField($"Prompt: {clip.lastGeneratedPrompt}", EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField($"Frames: {clip.frameCount}, Joints: {clip.jointCount}, FPS: {clip.fps}", EditorStyles.miniLabel);
            if (!string.IsNullOrWhiteSpace(lastConstraintsPath))
            {
                EditorGUILayout.LabelField($"Constraints: {lastConstraintsPath}", EditorStyles.miniLabel);
            }

            if (GUILayout.Button("Reset", GUILayout.Width(100)))
            {
                Undo.RecordObject(clip, "Reset Kimodo Clip");
                clip.ResetGeneration();
                EditorUtility.SetDirty(clip);
            }

            EditorGUILayout.EndVertical();
        }

        private void CreateAndAssignNewAnimationClip()
        {
            var newAnimationClip = new AnimationClip
            {
                name = $"{GeneratedClipNamePrefix}{DateTime.Now:yyyyMMdd_HHmmss_fff}"
            };

            if (!AssetDatabase.IsValidFolder(GeneratedClipFolder))
            {
                AssetDatabase.CreateFolder("Assets", "KimodoGeneratedClips");
            }

            string fileName = $"{newAnimationClip.name}.anim";
            string savePath = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedClipFolder}/{fileName}");
            AssetDatabase.CreateAsset(newAnimationClip, savePath);

            clip.clip = newAnimationClip;
            EditorUtility.SetDirty(clip);
            EditorUtility.SetDirty(clip.clip);
            AssetDatabase.SaveAssets();
            RefreshTimelinePreviewGraph();
        }

        private void TrimGeneratedClipsToLimit()
        {
            int maxCount = Mathf.Clamp(
                KimodoPlayableClipGenerationSettings.instance.MaxGeneratedClips,
                KimodoPlayableClipGenerationSettings.MinGeneratedClipsLimit,
                KimodoPlayableClipGenerationSettings.MaxGeneratedClipsLimit);

            if (!AssetDatabase.IsValidFolder(GeneratedClipFolder))
            {
                return;
            }

            string[] clipGuids = AssetDatabase.FindAssets("t:AnimationClip", new[] { GeneratedClipFolder });
            if (clipGuids == null || clipGuids.Length <= maxCount)
            {
                return;
            }

            var clipPaths = new List<string>(clipGuids.Length);
            foreach (string guid in clipGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                if (!name.StartsWith(GeneratedClipNamePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                clipPaths.Add(path);
            }

            if (clipPaths.Count <= maxCount)
            {
                return;
            }

            clipPaths.Sort(CompareGeneratedClipPathsByAgeOldestFirst);
            string activeClipPath = clip.clip != null ? AssetDatabase.GetAssetPath(clip.clip) : string.Empty;

            foreach (string candidatePath in clipPaths)
            {
                if (!string.IsNullOrWhiteSpace(activeClipPath) &&
                    string.Equals(candidatePath, activeClipPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsAssetReferencedByOtherAssets(candidatePath))
                {
                    Debug.Log($"[Kimodo] Generated clip cleanup skipped referenced clip: {candidatePath}");
                    return;
                }

                if (AssetDatabase.DeleteAsset(candidatePath))
                {
                    AssetDatabase.SaveAssets();
                }
                return;
            }
        }

        private static int CompareGeneratedClipPathsByAgeOldestFirst(string leftPath, string rightPath)
        {
            string leftName = Path.GetFileNameWithoutExtension(leftPath) ?? string.Empty;
            string rightName = Path.GetFileNameWithoutExtension(rightPath) ?? string.Empty;
            string leftStamp = leftName.StartsWith(GeneratedClipNamePrefix, StringComparison.Ordinal)
                ? leftName.Substring(GeneratedClipNamePrefix.Length)
                : leftName;
            string rightStamp = rightName.StartsWith(GeneratedClipNamePrefix, StringComparison.Ordinal)
                ? rightName.Substring(GeneratedClipNamePrefix.Length)
                : rightName;
            return string.Compare(leftStamp, rightStamp, StringComparison.Ordinal);
        }

        private static bool IsAssetReferencedByOtherAssets(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return false;
            }

            string[] allAssets = AssetDatabase.GetAllAssetPaths();
            foreach (string path in allAssets)
            {
                if (string.IsNullOrWhiteSpace(path) ||
                    string.Equals(path, assetPath, StringComparison.OrdinalIgnoreCase) ||
                    !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string[] dependencies;
                try
                {
                    dependencies = AssetDatabase.GetDependencies(path, false);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < dependencies.Length; i++)
                {
                    if (string.Equals(dependencies[i], assetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void RefreshTimelinePreviewGraph()
        {
            if (clip == null || TimelineEditor.inspectedAsset == null)
            {
                return;
            }

            // Refresh only when Timeline preview is currently enabled.
            if (!TryGetTimelinePreviewMode(out bool isPreviewMode) || !isPreviewMode)
            {
                return;
            }

            // Refresh only when this playable clip is selected in Timeline.
            if (FindTimelineClipForAsset(clip) == null)
            {
                return;
            }

            if (!TrySetTimelinePreviewMode(false))
            {
                return;
            }

            TrySetTimelinePreviewMode(true);
            TimelineEditor.Refresh(RefreshReason.ContentsModified | RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
        }

        private static bool TryGetTimelinePreviewMode(out bool previewMode)
        {
            previewMode = false;
            object timelineState = GetTimelineEditorState();
            if (timelineState == null)
            {
                return false;
            }

            Type stateType = timelineState.GetType();
            PropertyInfo previewModeProperty = stateType.GetProperty("previewMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (previewModeProperty != null && previewModeProperty.PropertyType == typeof(bool))
            {
                previewMode = (bool)previewModeProperty.GetValue(timelineState, null);
                return true;
            }

            FieldInfo previewModeField = stateType.GetField("previewMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (previewModeField != null && previewModeField.FieldType == typeof(bool))
            {
                previewMode = (bool)previewModeField.GetValue(timelineState);
                return true;
            }

            return false;
        }

        private static bool TrySetTimelinePreviewMode(bool value)
        {
            object timelineState = GetTimelineEditorState();
            if (timelineState == null)
            {
                return false;
            }

            Type stateType = timelineState.GetType();
            PropertyInfo previewModeProperty = stateType.GetProperty("previewMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (previewModeProperty != null && previewModeProperty.PropertyType == typeof(bool) && previewModeProperty.CanWrite)
            {
                previewModeProperty.SetValue(timelineState, value, null);
                return true;
            }

            FieldInfo previewModeField = stateType.GetField("previewMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (previewModeField != null && previewModeField.FieldType == typeof(bool))
            {
                previewModeField.SetValue(timelineState, value);
                return true;
            }

            return false;
        }

        private static object GetTimelineEditorState()
        {
            Type timelineEditorType = typeof(TimelineEditor);
            const BindingFlags StaticMemberFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            PropertyInfo stateProperty = timelineEditorType.GetProperty("state", StaticMemberFlags);
            if (stateProperty != null)
            {
                return stateProperty.GetValue(null, null);
            }

            FieldInfo stateField = timelineEditorType.GetField("state", StaticMemberFlags);
            if (stateField != null)
            {
                return stateField.GetValue(null);
            }

            return null;
        }

        private void TrySyncTimelineDuration(int frames)
        {
            UnityEngine.Timeline.TimelineClip timelineClip = FindTimelineClipForAsset(clip);
            if (timelineClip == null)
            {
                return;
            }

            float newDuration = frames / TargetFps;
            UndoExtensions.RegisterClip(timelineClip, L10n.Tr("Modify Clip Duration"));
            timelineClip.duration = newDuration;
        }

        private UnityEngine.Timeline.TimelineClip FindTimelineClipForAsset(PlayableAsset asset)
        {
            if (TimelineEditor.inspectedAsset == null)
            {
                return null;
            }

            foreach (UnityEngine.Timeline.TimelineClip selectedClip in TimelineEditor.selectedClips)
            {
                if (selectedClip.asset == asset)
                {
                    return selectedClip;
                }
            }

            foreach (UnityEngine.Timeline.TrackAsset track in TimelineEditor.inspectedAsset.GetOutputTracks())
            {
                foreach (UnityEngine.Timeline.TimelineClip timelineClip in track.GetClips())
                {
                    if (timelineClip.asset == asset)
                    {
                        return timelineClip;
                    }
                }
            }

            return null;
        }

    }
}


