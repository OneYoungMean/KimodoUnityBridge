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

namespace KimodoUnityMotionTools.ProjectEditor
{
    [CustomEditor(typeof(KimodoPlayableClip))]
    public class KimodoPlayableClipEditor : UnityEditor.Editor
    {
        private const float TargetFps = 60f;
        private const string DefaultWorkflowResource = "kimodo-unity-workflow";
        private const string GeneratedClipFolder = "Assets/KimodoGeneratedClips";
        private const string GeneratedClipNamePrefix = "Kimodo_";

        private static readonly IKimodoGenerationBackend ComfyUiBackend = new ComfyUiGenerationBackend();
        private static readonly IKimodoGenerationBackend BridgeBackend = new KimodoBridgeGenerationBackend();

        private SerializedProperty generationBackend;
        private SerializedProperty comfyuiIP;
        private SerializedProperty comfyuiPort;
        private SerializedProperty bridgeModelName;
        private SerializedProperty bridgeVramMode;
        private SerializedProperty bridgeStartupTimeoutSeconds;
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

        private KimodoPlayableClip clip;
        private bool isGenerating;
        private string lastStatus;
        private string lastError;
        private CancellationTokenSource generationCts;
        private int lastSubmittedSeed = int.MinValue;
        private string lastConstraintsPath = string.Empty;
        private string lastRetargetMode = "SOMA Fallback";
        private bool bridgeEnvAutoRetryInProgress;

        private void OnEnable()
        {
            clip = (KimodoPlayableClip)target;
            generationBackend = serializedObject.FindProperty("generationBackend");
            comfyuiIP = serializedObject.FindProperty("comfyuiIP");
            comfyuiPort = serializedObject.FindProperty("comfyuiPort");
            bridgeModelName = serializedObject.FindProperty("bridgeModelName");
            bridgeVramMode = serializedObject.FindProperty("bridgeVramMode");
            bridgeStartupTimeoutSeconds = serializedObject.FindProperty("bridgeStartupTimeoutSeconds");
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
        }

        private void OnDisable()
        {
            CancelGenerationInternal();
            EditorUtility.ClearProgressBar();
        }

        public override void OnInspectorGUI()
        {
            if (clip == null)
            {
                EditorGUILayout.HelpBox("Target clip is null.", MessageType.Error);
                return;
            }

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
                if (bridgeStartupTimeoutSeconds != null)
                {
                    bridgeStartupTimeoutSeconds.floatValue = Mathf.Max(500f,
                        EditorGUILayout.FloatField("Bridge Startup Timeout (sec)", bridgeStartupTimeoutSeconds.floatValue));
                }

                EditorGUILayout.HelpBox("显存说明：Kimodo 本体约 2G；低显存量化模型约 4G；高显存完整模型约 16G。", MessageType.Info);
                DrawEstimatedSetupTimeHint();
            }
            else
            {
                comfyuiIP.stringValue = EditorGUILayout.TextField("ComfyUI IP", comfyuiIP.stringValue);
                comfyuiPort.intValue = EditorGUILayout.IntField("ComfyUI Port", comfyuiPort.intValue);
                EditorGUILayout.PropertyField(workflowJsonAsset, new GUIContent("Workflow JSON Asset"));
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
                    new GUIContent("补间补帧", "使用前后邻接片段边界姿态作为约束生成中间动画。"));
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
                bool bridgeRunning = KimodoServerLifecycleManager.IsServerRunning;
                EditorGUI.BeginDisabledGroup(!bridgeRunning);
                if (GUILayout.Button("Close Bridge Server", GUILayout.Height(22)))
                {
                    _ = KimodoServerLifecycleManager.CloseServerAsync();
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
                IKimodoGenerationBackend backend = ResolveGenerationBackend();
                string motionJson = await backend.GenerateMotionJsonAsync(this, constraintsFilePath, effectiveSeed, generationCts.Token);
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

            string runtimeRoot = Path.Combine(ResolveProjectRoot(), "NvlabKimodoQuickServer");
            if (IsBridgeInstallerRunning(runtimeRoot))
            {
                return false;
            }

            bool shouldReset = EditorUtility.DisplayDialog(
                "Kimodo",
                $"构建环境失败，是否删除【{runtimeRoot}】重新生成？",
                "删除并重试",
                "取消");
            if (!shouldReset)
            {
                return false;
            }

            bridgeEnvAutoRetryInProgress = true;
            try
            {
                await KimodoServerLifecycleManager.CloseServerAsync();
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

        private static bool IsBridgeInstallerRunning(string runtimeRoot)
        {
            if (string.IsNullOrWhiteSpace(runtimeRoot))
            {
                return false;
            }

            string setupLockPath = Path.Combine(runtimeRoot, ".setup.lock");
            return File.Exists(setupLockPath);
        }

        private IKimodoGenerationBackend ResolveGenerationBackend()
        {
            return clip.generationBackend == KimodoGenerationBackend.KimodoBridge
                ? BridgeBackend
                : ComfyUiBackend;
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

        internal async Task<string> GenerateMotionJsonViaComfyUiBackendAsync(string constraintsFilePath, int effectiveSeed, CancellationToken token)
        {
            var service = new KimodoComfyUiService(
                getMotionPrompt: () => motionPrompt.stringValue,
                getGenerationFrames: () => generationFrames.intValue,
                getSemanticFps: () => TargetFps,
                getNumSamples: () => numSamples.intValue,
                getDiffusionSteps: () => diffusionSteps.intValue,
                getComfyIp: () => comfyuiIP.stringValue,
                getComfyPort: () => comfyuiPort.intValue,
                getTimeoutSeconds: () => generationTimeoutSeconds.floatValue,
                getPollIntervalSeconds: () => pollIntervalSeconds.floatValue,
                loadWorkflowText: LoadWorkflowText,
                setStatus: s => lastStatus = s,
                repaint: Repaint);

            return await service.GenerateMotionJsonAsync(constraintsFilePath, effectiveSeed, token);
        }

        internal async Task<string> GenerateMotionJsonViaBridgeBackendAsync(string constraintsFilePath, int effectiveSeed, CancellationToken token)
        {
            string projectRoot = ResolveProjectRoot();
            string expectedRuntimeRoot = Path.Combine(projectRoot, "NvlabKimodoQuickServer");
            if (!Directory.Exists(expectedRuntimeRoot))
            {
                lastStatus = "First setup: preparing NvlabKimodoQuickServer...";
                Repaint();
                Debug.Log("[Kimodo] First setup: runtime root missing, bootstrapping from package template.");
            }

            string kimodoRootPath = ResolveBridgeRootPath();
            string launcherPath = ResolveBridgeLauncherPath(kimodoRootPath);
            string modelName = string.IsNullOrWhiteSpace(clip.bridgeModelName) ? "Kimodo-SOMA-RP-v1" : clip.bridgeModelName.Trim();
            bool highVram = clip.bridgeVramMode == KimodoBridgeVramMode.High;
            float startupTimeout = Mathf.Max(500f, clip.bridgeStartupTimeoutSeconds);
            float durationSeconds = generationFrames.intValue / TargetFps;

            Debug.Log($"[Kimodo] Prompt: {motionPrompt.stringValue}");

            KimodoBridgeClient bridge = KimodoServerLifecycleManager.GetOrCreateClient();
            await bridge.StartAsync(
                launcherPath,
                modelName,
                highVram,
                kimodoRootPath,
                startupTimeout,
                progress =>
                {
                    lastStatus = progress;
                    Repaint();
                },
                token);

            lastStatus = $"Generating: {motionPrompt.stringValue}";
            Repaint();
            return await bridge.GenerateAsync(
                motionPrompt.stringValue,
                durationSeconds,
                effectiveSeed,
                diffusionSteps.intValue,
                constraintsFilePath ?? string.Empty,
                progress =>
                {
                    lastStatus = progress;
                    Repaint();
                },
                token);
        }

        private static string ResolveBridgeLauncherPath(string kimodoRootPath)
        {
            string resolved = KimodoServerRuntimeUtil.ResolveStartScript(kimodoRootPath);
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                return Path.GetFullPath(resolved);
            }

            throw new FileNotFoundException(
                $"Bridge launcher not found under runtime root: {kimodoRootPath}. Expected new pipeline launcher: run_server.bat or bash/start_server.bat.");
        }

        private static string ResolveBridgeRootPath()
        {
            string runtimeRoot = KimodoServerRuntimeUtil.GetRuntimeRootPath();
            if (!Directory.Exists(runtimeRoot) && !KimodoServerRuntimeUtil.EnsureRuntimeRootExists())
            {
                throw new DirectoryNotFoundException(
                    $"Bridge runtime root not found and bootstrap failed: {runtimeRoot}");
            }
            return Path.GetFullPath(runtimeRoot);
        }

        private static string ResolveProjectRoot()
        {
            string cwd = Path.GetFullPath(Environment.CurrentDirectory);
            if (Directory.Exists(Path.Combine(cwd, "Assets")))
            {
                return cwd;
            }

            return cwd;
        }

        private void DrawBridgeModelSelector()
        {
            string current = string.IsNullOrWhiteSpace(bridgeModelName.stringValue) ? "Kimodo-SOMA-RP-v1" : bridgeModelName.stringValue.Trim();
            string[] options = KimodoServerRuntimeUtil.SupportedModelNames;
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
            string runtimeRoot = KimodoServerRuntimeUtil.GetRuntimeRootPath();
            bool highVram = clip != null && clip.bridgeVramMode == KimodoBridgeVramMode.High;
            string modelName = clip == null || string.IsNullOrWhiteSpace(clip.bridgeModelName) ? "Kimodo-SOMA-RP-v1" : clip.bridgeModelName.Trim();
            int points = KimodoServerRuntimeUtil.EstimateMissingConfigPoints(runtimeRoot, highVram, modelName);
            int minutes = Mathf.Max(3, points * 3);
            EditorGUILayout.HelpBox($"预计配置时间（按缺失容量估算）：约 {minutes} 分钟。计算规则：(缺失模型容量 + 首次配置5) * 3 分钟。", MessageType.None);
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

        private string LoadWorkflowText()
        {
            if (workflowJsonAsset != null && workflowJsonAsset.objectReferenceValue is TextAsset customWorkflow && !string.IsNullOrWhiteSpace(customWorkflow.text))
            {
                return customWorkflow.text;
            }

            TextAsset defaultAsset = Resources.Load<TextAsset>(DefaultWorkflowResource);
            if (defaultAsset == null || string.IsNullOrWhiteSpace(defaultAsset.text))
            {
                throw new Exception($"Cannot load workflow asset '{DefaultWorkflowResource}'.");
            }
            return defaultAsset.text;
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

