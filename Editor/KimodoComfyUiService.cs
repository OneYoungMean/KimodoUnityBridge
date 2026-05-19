using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal sealed class KimodoComfyUiService
    {
        private readonly Func<string> getMotionPrompt;
        private readonly Func<int> getGenerationFrames;
        private readonly Func<float> getSemanticFps;
        private readonly Func<int> getNumSamples;
        private readonly Func<int> getDiffusionSteps;
        private readonly Func<string> getComfyIp;
        private readonly Func<int> getComfyPort;
        private readonly Func<float> getTimeoutSeconds;
        private readonly Func<float> getPollIntervalSeconds;
        private readonly Func<string> loadWorkflowText;
        private readonly Action<string> setStatus;
        private readonly Action repaint;

        private const int HistoryLogMaxChars = 4000;

        public KimodoComfyUiService(
            Func<string> getMotionPrompt,
            Func<int> getGenerationFrames,
            Func<float> getSemanticFps,
            Func<int> getNumSamples,
            Func<int> getDiffusionSteps,
            Func<string> getComfyIp,
            Func<int> getComfyPort,
            Func<float> getTimeoutSeconds,
            Func<float> getPollIntervalSeconds,
            Func<string> loadWorkflowText,
            Action<string> setStatus,
            Action repaint)
        {
            this.getMotionPrompt = getMotionPrompt;
            this.getGenerationFrames = getGenerationFrames;
            this.getSemanticFps = getSemanticFps;
            this.getNumSamples = getNumSamples;
            this.getDiffusionSteps = getDiffusionSteps;
            this.getComfyIp = getComfyIp;
            this.getComfyPort = getComfyPort;
            this.getTimeoutSeconds = getTimeoutSeconds;
            this.getPollIntervalSeconds = getPollIntervalSeconds;
            this.loadWorkflowText = loadWorkflowText;
            this.setStatus = setStatus;
            this.repaint = repaint;
        }

        public async Task<string> GenerateMotionJsonAsync(string constraintsFilePath, int effectiveSeed, CancellationToken token)
        {
            setStatus?.Invoke("Submitting workflow...");
            repaint?.Invoke();

            string serverUrl = $"http://{getComfyIp()}:{getComfyPort()}";
            string workflowText = loadWorkflowText();
            JObject workflow = JObject.Parse(workflowText);
            InjectGenerationInputs(workflow, constraintsFilePath, effectiveSeed);

            string promptId = await SubmitPromptAsync(serverUrl, workflow, token);
            if (string.IsNullOrWhiteSpace(promptId))
            {
                throw new Exception("ComfyUI did not return prompt_id.");
            }

            setStatus?.Invoke($"Queued: {promptId}");
            repaint?.Invoke();

            string historyJson = await PollHistoryUntilDoneAsync(
                serverUrl,
                promptId,
                getTimeoutSeconds(),
                getPollIntervalSeconds(),
                token);

            string motionJson = ExtractMotionJsonFromHistory(historyJson, promptId);
            if (string.IsNullOrWhiteSpace(motionJson))
            {
                throw new Exception("No motion json found in workflow outputs.");
            }

            return motionJson;
        }

        private void InjectGenerationInputs(JObject workflow, string constraintsFilePath, int effectiveSeed)
        {
            string prompt = getMotionPrompt();
            float duration = getGenerationFrames() / getSemanticFps();

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
                    if (inputs["num_samples"] != null) inputs["num_samples"] = getNumSamples();
                    if (inputs["diffusion_steps"] != null) inputs["diffusion_steps"] = getDiffusionSteps();
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

            bool topLevelCompleted = string.Equals(entry.Value<string>("status"), "completed", StringComparison.OrdinalIgnoreCase);
            bool topLevelSuccess = string.Equals(entry.Value<string>("success"), "true", StringComparison.OrdinalIgnoreCase) || entry.Value<bool?>("success") == true;

            bool nestedCompleted = false;
            bool nestedSuccess = false;
            string nestedStatus = string.Empty;
            if (entry["status"] is JObject statusObj)
            {
                nestedStatus = statusObj.Value<string>("status_str") ?? string.Empty;
                nestedCompleted = string.Equals(nestedStatus, "success", StringComparison.OrdinalIgnoreCase);
                nestedSuccess = statusObj.Value<bool?>("completed") == true || statusObj.Value<bool?>("success") == true;
            }

            if (topLevelCompleted || topLevelSuccess || nestedCompleted || nestedSuccess)
            {
                summary = $"top.status='{entry.Value<string>("status")}', top.success='{entry["success"]}', nested.status_str='{nestedStatus}', has_outputs={HasUsableOutputs(entry)}";
                return true;
            }

            return false;
        }

        private string ExtractMotionJsonFromHistory(string historyJson, string promptId)
        {
            JObject history = JObject.Parse(historyJson);
            if (!TryResolveHistoryEntry(history, promptId, out JObject entry, out string note))
            {
                throw new Exception(
                    $"ComfyUI history for prompt_id={promptId} has no compatible entry shape. {note}. " +
                    $"history={TruncateForLog(historyJson, HistoryLogMaxChars)}");
            }
            return ExtractMotionJsonFromEntry(entry, promptId);
        }

        private static bool TryResolveHistoryEntry(JObject history, string promptId, out JObject entry, out string note)
        {
            entry = null;
            note = string.Empty;
            if (history == null)
            {
                note = "history is null";
                return false;
            }

            if (history[promptId] is JObject direct)
            {
                entry = direct;
                return true;
            }

            foreach (var prop in history.Properties())
            {
                if (prop.Value is JObject child && string.Equals(child.Value<string>("prompt_id"), promptId, StringComparison.OrdinalIgnoreCase))
                {
                    entry = child;
                    note = $"matched child key '{prop.Name}' by nested prompt_id.";
                    return true;
                }
            }

            if (history["outputs"] is JObject rootOutputs || history["status"] is JObject rootStatus)
            {
                entry = history;
                note = "using root object as entry fallback (history already looks like a single entry).";
                return true;
            }

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
                parts.Add($"status.keys=[{string.Join(",", status.Properties().Select(p => p.Name))}]");
                parts.Add($"status.status_str='{status.Value<string>("status_str")}'");
                parts.Add($"status.completed='{status["completed"]}'");
                parts.Add($"status.success='{status["success"]}'");
            }
            else
            {
                parts.Add("status=<missing>");
            }

            parts.Add($"top.status='{entry.Value<string>("status")}'");
            parts.Add($"top.success='{entry["success"]}'");

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
    }
}