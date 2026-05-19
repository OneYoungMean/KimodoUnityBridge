using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Playables;
using KimodoUnityMotionTools.ProjectEditor;

namespace UnityEngine.Timeline
{
    [CustomEditor(typeof(KimodoPlayableClip))]
    public class KimodoPlayableClipEditor : UnityEditor.Editor
    {
        private const float TargetFps = 60f;
        private const string DefaultWorkflowResource = "kimodo-unity-workflow";
        private const string GeneratedClipFolder = "Assets/KimodoGeneratedClips";
        private const int HistoryLogMaxChars = 4000;

        private SerializedProperty comfyuiIP;
        private SerializedProperty comfyuiPort;
        private SerializedProperty motionPrompt;
        private SerializedProperty generationFrames;
        private SerializedProperty numSamples;
        private SerializedProperty diffusionSteps;
        private SerializedProperty randomSeed;
        private SerializedProperty seed;
        private SerializedProperty workflowJsonAsset;
        private SerializedProperty generationTimeoutSeconds;
        private SerializedProperty pollIntervalSeconds;

        private SerializedProperty animationClipProp;
        private SerializedProperty footIKProp;
        private SerializedProperty loopProp;
        private SerializedProperty savedSkeletonTypeProp;

        private KimodoPlayableClip clip;
        private bool isGenerating;
        private string lastStatus;
        private string lastError;
        private CancellationTokenSource generationCts;
        private int lastSubmittedSeed = int.MinValue;
        private string lastConstraintsPath = string.Empty;

        private void OnEnable()
        {
            clip = (KimodoPlayableClip)target;
            comfyuiIP = serializedObject.FindProperty("comfyuiIP");
            comfyuiPort = serializedObject.FindProperty("comfyuiPort");
            motionPrompt = serializedObject.FindProperty("motionPrompt");
            generationFrames = serializedObject.FindProperty("generationFrames");
            numSamples = serializedObject.FindProperty("numSamples");
            diffusionSteps = serializedObject.FindProperty("diffusionSteps");
            randomSeed = serializedObject.FindProperty("randomSeed");
            seed = serializedObject.FindProperty("seed");
            workflowJsonAsset = serializedObject.FindProperty("workflowJsonAsset");
            generationTimeoutSeconds = serializedObject.FindProperty("generationTimeoutSeconds");
            pollIntervalSeconds = serializedObject.FindProperty("pollIntervalSeconds");

            animationClipProp = serializedObject.FindProperty("m_Clip");
            footIKProp = serializedObject.FindProperty("m_ApplyFootIK");
            loopProp = serializedObject.FindProperty("m_Loop");
            savedSkeletonTypeProp = serializedObject.FindProperty("savedSkeletonType");
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

            comfyuiIP.stringValue = EditorGUILayout.TextField("ComfyUI IP", comfyuiIP.stringValue);
            comfyuiPort.intValue = EditorGUILayout.IntField("ComfyUI Port", comfyuiPort.intValue);
            EditorGUILayout.PropertyField(workflowJsonAsset, new GUIContent("Workflow JSON Asset"));

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

            if (clip != null && clip.savedSkeletonType != KimodoBakeSkeletonType.SOMA)
            {
                clip.savedSkeletonType = KimodoBakeSkeletonType.SOMA;
                EditorUtility.SetDirty(clip);
            }

            EditorGUILayout.LabelField("Saved As: SOMA", EditorStyles.miniLabel);
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
            lastStatus = "Submitting workflow...";
            generationCts = new CancellationTokenSource();
            Repaint();

            try
            {
                EnsureAnimationClipExists();
                TimelineClip timelineClip = FindTimelineClipForAsset(clip);
                string constraintsFilePath = string.Empty;
                if (timelineClip != null)
                {
                    if (!KimodoConstraintExportUtility.TryBuildAndWriteConstraintsFile(
                        timelineClip,
                        out constraintsFilePath,
                        out string constraintError))
                    {
                        throw new Exception($"Failed to export constraints: {constraintError}");
                    }
                }

                string serverUrl = $"http://{comfyuiIP.stringValue}:{comfyuiPort.intValue}";
                string workflowText = LoadWorkflowText();
                JObject workflow = JObject.Parse(workflowText);
                InjectGenerationInputs(workflow, constraintsFilePath);
                lastConstraintsPath = constraintsFilePath;

                string promptId = await SubmitPromptAsync(serverUrl, workflow, generationCts.Token);
                if (string.IsNullOrWhiteSpace(promptId))
                {
                    throw new Exception("ComfyUI did not return prompt_id.");
                }

                lastStatus = $"Queued: {promptId}";
                Repaint();

                string historyJson = await PollHistoryUntilDoneAsync(serverUrl, promptId, generationTimeoutSeconds.floatValue, pollIntervalSeconds.floatValue, generationCts.Token);
                string motionJson = ExtractMotionJsonFromHistory(historyJson, promptId);
                if (string.IsNullOrWhiteSpace(motionJson))
                {
                    throw new Exception("No motion json found in workflow outputs.");
                }

                ApplyMotionJsonToClip(motionJson);
                BakeCurrentMotionData();
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
            }
            finally
            {
                isGenerating = false;
                CancelGenerationInternal();
                EditorUtility.ClearProgressBar();
                Repaint();
            }
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
            string launcherPath = ResolveBridgeLauncherPath();
            string kimodoRootPath = ResolveKimodoRootPath(launcherPath);
            string modelName = string.IsNullOrWhiteSpace(clip.bridgeModelName) ? "Kimodo-SOMA-RP-v1" : clip.bridgeModelName.Trim();
            float startupTimeout = Mathf.Max(500f, clip.bridgeStartupTimeoutSeconds);
            float durationSeconds = generationFrames.intValue / TargetFps;

            using var bridge = new KimodoBridgeClient();
            await bridge.StartAsync(
                launcherPath,
                modelName,
                kimodoRootPath,
                startupTimeout,
                progress =>
                {
                    lastStatus = progress;
                    Repaint();
                },
                token);

            try
            {
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
            finally
            {
                await bridge.StopAsync(CancellationToken.None);
            }
        }

        private string ResolveBridgeLauncherPath()
        {
            if (!string.IsNullOrWhiteSpace(clip.bridgeLauncherPath) && File.Exists(clip.bridgeLauncherPath))
            {
                return Path.GetFullPath(clip.bridgeLauncherPath.Trim());
            }

            string[] candidates =
            {
                Path.Combine(Environment.CurrentDirectory, "NvlabKimodoQuickServer", "start_kimodo_bridge_offline.bat"),
                Path.Combine(Environment.CurrentDirectory, "NvlabKimodoQuickServer", "start_kimodo_bridge_offline.sh"),
                Path.Combine(Environment.CurrentDirectory, "KimodoUnityBridge", "NvlabKimodoQuickServer", "start_kimodo_bridge_offline.bat"),
                Path.Combine(Environment.CurrentDirectory, "KimodoUnityBridge", "NvlabKimodoQuickServer", "start_kimodo_bridge_offline.sh"),
                Path.Combine(Environment.CurrentDirectory, "KimodoUnityBridge", "kimodo_offline_assets~", "start_kimodo_bridge_offline.bat"),
                Path.Combine(Environment.CurrentDirectory, "KimodoUnityBridge", "kimodo_offline_assets~", "start_kimodo_bridge_offline.sh")
            };

            foreach (string p in candidates)
            {
                if (File.Exists(p))
                {
                    return Path.GetFullPath(p);
                }
            }

            throw new FileNotFoundException(
                "Bridge launcher not found. Set bridgeLauncherPath or place start_kimodo_bridge_offline.bat under NvlabKimodoQuickServer.");
        }

        private static string ResolveKimodoRootPath(string launcherPath)
        {
            string dir = Path.GetDirectoryName(launcherPath);
            if (string.IsNullOrWhiteSpace(dir))
            {
                throw new Exception($"Invalid launcher path: {launcherPath}");
            }

            return Path.GetFullPath(dir);
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

        private void InjectGenerationInputs(JObject workflow, string constraintsFilePath)
        {
            string prompt = motionPrompt.stringValue;
            float duration = generationFrames.intValue / TargetFps;
            int effectiveSeed = randomSeed.boolValue ? Guid.NewGuid().GetHashCode() & int.MaxValue : seed.intValue;
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

            foreach (var prop in workflow.Properties())
            {
                if (prop.Value is not JObject node) continue;
                string classType = node.Value<string>("class_type");
                if (node["inputs"] is not JObject inputs) continue;

                if (string.Equals(classType, "Kimodo_TextEncode", StringComparison.OrdinalIgnoreCase))
                {
                    if (inputs["prompt"] != null) inputs["prompt"] = prompt;
                }
                else if (string.Equals(classType, "Kimodo_Sampler", StringComparison.OrdinalIgnoreCase))
                {
                    if (inputs["duration"] != null) inputs["duration"] = duration;
                    if (inputs["seed"] != null) inputs["seed"] = effectiveSeed;
                    if (inputs["num_samples"] != null) inputs["num_samples"] = numSamples.intValue;
                    if (inputs["diffusion_steps"] != null) inputs["diffusion_steps"] = diffusionSteps.intValue;
                    if (inputs["constraints_json"] != null) inputs["constraints_json"] = constraintsFilePath ?? string.Empty;
                }
            }
        }

        private async Task<string> SubmitPromptAsync(string serverUrl, JObject workflow, CancellationToken token)
        {
            JObject req = new JObject
            {
                ["client_id"] = Guid.NewGuid().ToString(),
                ["prompt"] = workflow
            };
            string body = req.ToString(Formatting.None);
            string url = $"{serverUrl}/prompt";
            string response = await SendJsonRequestAsync(url, "POST", body, token);
            JObject parsed = JObject.Parse(response);
            return parsed.Value<string>("prompt_id");
        }

        private async Task<string> PollHistoryUntilDoneAsync(string serverUrl, string promptId, float timeoutSeconds, float pollIntervalSecondsValue, CancellationToken token)
        {
            double start = EditorApplication.timeSinceStartup;
            string url = $"{serverUrl}/history/{promptId}";
            int historyDebugLogCount = 0;
            int loopCount = 0;

            while (EditorApplication.timeSinceStartup - start < timeoutSeconds)
            {
                loopCount++;
                token.ThrowIfCancellationRequested();
                string response = await SendJsonRequestAsync(url, "GET", null, token);
                if (string.IsNullOrWhiteSpace(response) || response == "{}")
                {
                    if (historyDebugLogCount < 3)
                    {
                        historyDebugLogCount++;
                        Debug.Log($"[Kimodo] /history poll #{loopCount} prompt_id={promptId}: empty payload '{response}'.");
                    }
                }
                else
                {
                    if (historyDebugLogCount < 3)
                    {
                        historyDebugLogCount++;
                        Debug.Log($"[Kimodo] /history poll #{loopCount} prompt_id={promptId}: {TruncateForLog(response, HistoryLogMaxChars)}");
                    }

                    JObject history;
                    try
                    {
                        history = JObject.Parse(response);
                    }
                    catch (Exception parseEx)
                    {
                        throw new Exception(
                            $"Failed to parse /history response for prompt_id={promptId}: {parseEx.Message}. " +
                            $"response={TruncateForLog(response, HistoryLogMaxChars)}");
                    }
                    if (TryResolveHistoryEntry(history, promptId, out JObject entry, out string resolveNote))
                    {
                        if (!string.IsNullOrWhiteSpace(resolveNote))
                        {
                            Debug.Log($"[Kimodo] History entry resolved for {promptId}: {resolveNote}");
                        }

                        string extracted = ExtractMotionJsonFromEntry(entry, promptId);
                        if (!string.IsNullOrWhiteSpace(extracted))
                        {
                            return response;
                        }

                        if (IsPromptFinished(entry, out string statusSummary))
                        {
                            throw new Exception(
                                $"ComfyUI finished prompt_id={promptId} but returned no usable outputs. {statusSummary} " +
                                $"Output summary: {BuildOutputSummary(entry)}. " +
                                $"History entry summary: {BuildEntrySummary(entry)}. " +
                                "This is usually caused by cache hit with no output payload or a workflow output node mismatch. " +
                                "Try changing seed and ensure the workflow ends at Kimodo_OutputMotionCompact.");
                        }
                    }
                    else if (historyDebugLogCount < 6)
                    {
                        historyDebugLogCount++;
                        Debug.LogWarning($"[Kimodo] /history has no entry for prompt_id={promptId}. root_keys={string.Join(",", history.Properties().Select(p => p.Name))}");
                    }
                }

                float progress = (float)Mathf.Clamp01((float)((EditorApplication.timeSinceStartup - start) / timeoutSeconds));
                EditorUtility.DisplayProgressBar("Kimodo Generate", $"Waiting ComfyUI ({progress * 100f:F0}%)", progress);
                await Task.Delay(TimeSpan.FromSeconds(pollIntervalSecondsValue), token);
            }

            throw new TimeoutException($"Timeout waiting for prompt_id={promptId}.");
        }

        private static bool HasUsableOutputs(JObject entry)
        {
            if (entry == null || entry["outputs"] is not JObject outputs || !outputs.HasValues)
            {
                return false;
            }
            foreach (var output in outputs.Properties())
            {
                if (output.Value is JObject data && data.HasValues)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsPromptFinished(JObject entry, out string summary)
        {
            summary = string.Empty;
            if (entry == null)
            {
                return false;
            }

            if (entry["status"] is JObject statusObj)
            {
                bool completed = statusObj.Value<bool?>("completed") ?? false;
                string statusStr = statusObj.Value<string>("status_str") ?? string.Empty;
                summary = $"status.completed={completed}, status.status_str='{statusStr}'.";
                if (completed)
                {
                    return true;
                }
            }

            // Some ComfyUI variants populate only top-level flags.
            if (entry.Value<bool?>("completed") == true)
            {
                summary = "entry.completed=true.";
                return true;
            }

            return false;
        }

        private string ExtractMotionJsonFromHistory(string historyJson, string promptId)
        {
            JObject history = JObject.Parse(historyJson);
            if (!TryResolveHistoryEntry(history, promptId, out JObject entry, out _))
            {
                return null;
            }

            return ExtractMotionJsonFromEntry(entry, promptId);
        }

        private static bool TryResolveHistoryEntry(JObject history, string promptId, out JObject entry, out string note)
        {
            entry = null;
            note = string.Empty;
            if (history == null)
            {
                note = "history is null.";
                return false;
            }

            if (history[promptId] is JObject byPromptId)
            {
                entry = byPromptId;
                note = "matched history[prompt_id].";
                return true;
            }

            // Compatibility fallback: some variants may return the entry object directly for /history/{prompt_id}.
            if (history["outputs"] is JObject || history["status"] is JObject)
            {
                entry = history;
                note = "using root object as history entry (direct /history payload fallback).";
                return true;
            }

            // Last resort: if exactly one object child exists and it looks like an entry, use it.
            JObject singleObject = null;
            string singleKey = null;
            foreach (var prop in history.Properties())
            {
                if (prop.Value is JObject child)
                {
                    if (singleObject != null)
                    {
                        singleObject = null;
                        break;
                    }
                    singleObject = child;
                    singleKey = prop.Name;
                }
            }

            if (singleObject != null && (singleObject["outputs"] is JObject || singleObject["status"] is JObject))
            {
                entry = singleObject;
                note = $"using single child object '{singleKey}' as history entry fallback.";
                return true;
            }

            note = $"no compatible entry shape. root_keys={string.Join(",", history.Properties().Select(p => p.Name))}";
            return false;
        }

        private string ExtractMotionJsonFromEntry(JObject entry, string promptIdForLog)
        {
            if (entry == null)
            {
                return null;
            }

            if (entry["outputs"] is not JObject outputs)
            {
                return null;
            }

            try
            {
                List<string> outputSummaries = new List<string>();
                foreach (var output in outputs.Properties())
                {
                    if (output.Value is JObject o)
                    {
                        outputSummaries.Add($"{output.Name}:[{string.Join(",", o.Properties().Select(p => p.Name))}]");
                    }
                    else
                    {
                        outputSummaries.Add($"{output.Name}:[{output.Value?.Type}]");
                    }
                }
                Debug.Log($"[Kimodo] History outputs for {promptIdForLog}: {string.Join(" | ", outputSummaries)}");
            }
            catch
            {
                // keep extraction robust even if debug formatting fails
            }

            List<string> candidates = new List<string>();
            foreach (var output in outputs.Properties())
            {
                CollectMotionJsonCandidates(output.Value, candidates);
            }

            foreach (string extracted in candidates)
            {
                if (HasNonEmptyLocalRotQuats(extracted))
                {
                    return extracted;
                }
            }

            return candidates.Count > 0 ? candidates[0] : null;
        }

        private static string BuildOutputSummary(JObject entry)
        {
            if (entry == null || entry["outputs"] is not JObject outputs || !outputs.HasValues)
            {
                return "outputs=<empty>";
            }

            List<string> parts = new List<string>();
            foreach (var output in outputs.Properties())
            {
                if (output.Value is JObject obj)
                {
                    parts.Add($"{output.Name}:[{string.Join(",", obj.Properties().Select(p => p.Name))}]");
                }
                else
                {
                    parts.Add($"{output.Name}:[{output.Value?.Type}]");
                }
            }
            return string.Join(" | ", parts);
        }

        private static string BuildEntrySummary(JObject entry)
        {
            if (entry == null)
            {
                return "entry=<null>";
            }

            List<string> parts = new List<string>
            {
                $"keys=[{string.Join(",", entry.Properties().Select(p => p.Name))}]"
            };

            if (entry["status"] is JObject status)
            {
                bool completed = status.Value<bool?>("completed") ?? false;
                string statusStr = status.Value<string>("status_str") ?? string.Empty;
                parts.Add($"status.completed={completed}");
                parts.Add($"status.status_str='{statusStr}'");
            }

            if (entry["outputs"] is JObject outputs)
            {
                parts.Add($"outputs.keys=[{string.Join(",", outputs.Properties().Select(p => p.Name))}]");
            }
            else
            {
                parts.Add("outputs=<missing>");
            }

            return string.Join(", ", parts);
        }

        private static string TruncateForLog(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value;
            }
            return value.Substring(0, maxChars) + "...(truncated)";
        }

        private void CollectMotionJsonCandidates(JToken token, List<string> results)
        {
            if (token == null)
            {
                return;
            }

            if (token.Type == JTokenType.Object)
            {
                JObject obj = (JObject)token;

                // Prefer known output keys first.
                AddCandidate(obj["motion_json_compact"], results);
                AddCandidate(obj["motion_json"], results);
                AddCandidate(obj["text"], results);

                foreach (var prop in obj.Properties())
                {
                    CollectMotionJsonCandidates(prop.Value, results);
                }
                return;
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (JToken item in token)
                {
                    CollectMotionJsonCandidates(item, results);
                }
                return;
            }

            AddCandidate(token, results);
        }

        private void AddCandidate(JToken token, List<string> results)
        {
            string extracted = TryExtractMotionJson(token);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                return;
            }

            if (!results.Contains(extracted))
            {
                results.Add(extracted);
            }
        }

        private string TryExtractMotionJson(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Object)
            {
                return token.ToString(Formatting.None);
            }

            if (token.Type == JTokenType.String)
            {
                string s = token.ToString().Trim();
                try
                {
                    JToken parsed = JToken.Parse(s);
                    if (parsed.Type == JTokenType.Object) return parsed.ToString(Formatting.None);
                    if (parsed.Type == JTokenType.Array) return TryExtractMotionJson(parsed);
                }
                catch
                {
                    return null;
                }
            }

            if (token.Type == JTokenType.Array)
            {
                foreach (JToken item in token)
                {
                    string v = TryExtractMotionJson(item);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }

            return null;
        }

        private static bool HasNonEmptyLocalRotQuats(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                JToken parsed = JToken.Parse(json);
                if (parsed is not JObject obj)
                {
                    return false;
                }

                JToken rot = obj["local_rot_quats"];
                return rot is JArray arr && arr.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task<string> SendJsonRequestAsync(string url, string method, string body, CancellationToken token)
        {
            using UnityWebRequest request = new UnityWebRequest(url, method);
            if (method == UnityWebRequest.kHttpVerbPOST)
            {
                byte[] data = Encoding.UTF8.GetBytes(body ?? string.Empty);
                request.uploadHandler = new UploadHandlerRaw(data);
                request.SetRequestHeader("Content-Type", "application/json");
            }
            request.downloadHandler = new DownloadHandlerBuffer();

            UnityWebRequestAsyncOperation op = request.SendWebRequest();
            while (!op.isDone)
            {
                token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            token.ThrowIfCancellationRequested();

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new Exception($"{method} {url} failed: {request.error}");
            }

            return request.downloadHandler.text;
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

        private void EnsureAnimationClipExists()
        {
            if (clip.clip != null)
            {
                return;
            }

            var newAnimationClip = new AnimationClip
            {
                name = $"Kimodo_{DateTime.Now:yyyyMMdd_HHmmss}"
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
            TimelineClip timelineClip = FindTimelineClipForAsset(clip);
            if (timelineClip == null)
            {
                return;
            }

            float newDuration = frames / TargetFps;
            UndoExtensions.RegisterClip(timelineClip, L10n.Tr("Modify Clip Duration"));
            timelineClip.duration = newDuration;
        }

        private TimelineClip FindTimelineClipForAsset(PlayableAsset asset)
        {
            if (TimelineEditor.inspectedAsset == null)
            {
                return null;
            }

            foreach (TimelineClip selectedClip in TimelineEditor.selectedClips)
            {
                if (selectedClip.asset == asset)
                {
                    return selectedClip;
                }
            }

            foreach (TrackAsset track in TimelineEditor.inspectedAsset.GetOutputTracks())
            {
                foreach (TimelineClip timelineClip in track.GetClips())
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
