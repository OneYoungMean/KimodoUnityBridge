using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace KimodoUnityMotionTools.Generation
{
    internal sealed class ComfyUiBackendAdapter : IGenerationBackendAdapter
    {
        private readonly string serverUrl;
        private readonly float timeoutSeconds;
        private readonly float pollIntervalSeconds;
        private readonly string workflowResourceName;
        private const int HistoryLogMaxChars = 4000;

        public ComfyUiBackendAdapter(string host, int port, float timeoutSeconds, float pollIntervalSeconds, string workflowResourceName)
        {
            serverUrl = $"http://{(string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host)}:{Mathf.Clamp(port, 1, 65535)}";
            this.timeoutSeconds = Mathf.Max(10f, timeoutSeconds);
            this.pollIntervalSeconds = Mathf.Max(0.1f, pollIntervalSeconds);
            this.workflowResourceName = string.IsNullOrWhiteSpace(workflowResourceName) ? "kimodo-unity-workflow" : workflowResourceName;
        }

        public Task<string> StartAsync(Action<string> progress, CancellationToken token)
        {
            progress?.Invoke("ComfyUI backend selected. Start is no-op.");
            return Task.FromResult("ComfyUI backend ready (no-op start).");
        }

        public async Task<KimodoGenerationResultDto> GenerateAsync(KimodoGenerationRequestDto request, Action<string> progress, CancellationToken token)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            progress?.Invoke("Submitting ComfyUI workflow...");

            string workflowText = LoadWorkflowText();
            JObject workflow = JObject.Parse(workflowText);
            ValidateWorkflowShape(workflow);
            InjectGenerationInputs(workflow, request);

            string promptId = await SubmitPromptAsync(serverUrl, workflow, token);
            if (string.IsNullOrWhiteSpace(promptId))
            {
                throw new Exception("ComfyUI did not return prompt_id.");
            }

            progress?.Invoke($"ComfyUI queued: {promptId}");
            string historyJson = await PollHistoryUntilDoneAsync(serverUrl, promptId, timeoutSeconds, pollIntervalSeconds, progress, token);
            string motionJson = ExtractMotionJsonFromHistory(historyJson, promptId);
            if (string.IsNullOrWhiteSpace(motionJson))
            {
                throw new Exception("No motion json found in workflow outputs.");
            }

            return new KimodoGenerationResultDto
            {
                backendType = KimodoBackendType.ComfyUi,
                rawStatus = "done",
                message = "ComfyUI generation complete.",
                motionJsonCompact = motionJson
            };
        }

        public async Task<bool> PingAsync(CancellationToken token)
        {
            try
            {
                string url = $"{serverUrl}/history";
                string _ = await SendJsonRequestAsync(url, "GET", null, token);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public Task DetachAsync(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public Task KillAsync(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            // no retained resources
        }

        private string LoadWorkflowText()
        {
            TextAsset asset = Resources.Load<TextAsset>(workflowResourceName);
            if (asset == null || string.IsNullOrWhiteSpace(asset.text))
            {
                throw new Exception($"Cannot load runtime workflow asset '{workflowResourceName}'.");
            }

            return asset.text;
        }

        private static void ValidateWorkflowShape(JObject workflow)
        {
            if (workflow == null)
            {
                throw new Exception("Workflow JSON is null.");
            }

            foreach (var prop in workflow.Properties())
            {
                if (prop.Value is not JObject node)
                {
                    throw new Exception($"Workflow node '{prop.Name}' must be object.");
                }

                if (string.IsNullOrWhiteSpace(node.Value<string>("class_type")))
                {
                    throw new Exception($"Workflow node '{prop.Name}' missing class_type.");
                }

                if (node["inputs"] is not JObject)
                {
                    throw new Exception($"Workflow node '{prop.Name}' missing object field inputs.");
                }
            }
        }

        private static void InjectGenerationInputs(JObject workflow, KimodoGenerationRequestDto req)
        {
            foreach (var prop in workflow.Properties())
            {
                if (prop.Value is not JObject node || node["inputs"] is not JObject inputs)
                {
                    continue;
                }

                string classType = node.Value<string>("class_type");
                if (string.Equals(classType, "Kimodo_TextEncode", StringComparison.OrdinalIgnoreCase))
                {
                    if (inputs["prompt"] != null)
                    {
                        inputs["prompt"] = req.prompt ?? string.Empty;
                    }
                }
                else if (string.Equals(classType, "Kimodo_Sampler", StringComparison.OrdinalIgnoreCase))
                {
                    if (inputs["duration"] != null)
                    {
                        inputs["duration"] = req.duration;
                    }
                    if (inputs["seed"] != null)
                    {
                        inputs["seed"] = req.seed ?? 0;
                    }
                    if (inputs["diffusion_steps"] != null)
                    {
                        inputs["diffusion_steps"] = Mathf.Max(1, req.steps);
                    }
                    if (inputs["constraints_json"] != null)
                    {
                        inputs["constraints_json"] = req.constraints_json ?? string.Empty;
                    }
                    if (inputs["boundary_pose_json"] != null)
                    {
                        inputs["boundary_pose_json"] = req.boundary_pose_json ?? string.Empty;
                    }
                    if (inputs["loop_hint"] != null)
                    {
                        inputs["loop_hint"] = req.loop_hint;
                    }
                    if (inputs["segment_index"] != null)
                    {
                        inputs["segment_index"] = req.segment_index;
                    }
                    if (inputs["transition_duration"] != null)
                    {
                        inputs["transition_duration"] = req.transition_duration;
                    }
                }
            }
        }

        private async Task<string> SubmitPromptAsync(string urlRoot, JObject workflow, CancellationToken token)
        {
            string url = $"{urlRoot}/prompt";
            JObject request = new JObject
            {
                ["client_id"] = Guid.NewGuid().ToString(),
                ["prompt"] = workflow
            };
            string body = request.ToString(Formatting.None);
            string response = await SendJsonRequestAsync(url, "POST", body, token, failBody =>
            {
                string details = TryBuildPromptFailureDetails(failBody);
                throw new Exception($"POST {url} rejected by ComfyUI. {details}");
            });
            return JObject.Parse(response).Value<string>("prompt_id");
        }

        private async Task<string> PollHistoryUntilDoneAsync(string urlRoot, string promptId, float timeoutSec, float pollSec, Action<string> progress, CancellationToken token)
        {
            double start = Time.realtimeSinceStartupAsDouble;
            string url = $"{urlRoot}/history/{promptId}";

            while (Time.realtimeSinceStartupAsDouble - start < timeoutSec)
            {
                token.ThrowIfCancellationRequested();
                string response = await SendJsonRequestAsync(url, "GET", null, token);
                if (!string.IsNullOrWhiteSpace(response) && response != "{}")
                {
                    JObject history = JObject.Parse(response);
                    if (TryResolveHistoryEntry(history, promptId, out JObject entry, out _))
                    {
                        string extracted = ExtractMotionJsonFromEntry(entry, promptId);
                        if (!string.IsNullOrWhiteSpace(extracted))
                        {
                            return response;
                        }

                        if (IsPromptFinished(entry, out string statusSummary))
                        {
                            throw new Exception($"ComfyUI finished prompt_id={promptId} but no usable outputs. {statusSummary}");
                        }
                    }
                }

                progress?.Invoke($"Waiting ComfyUI result ({Mathf.Clamp01((float)((Time.realtimeSinceStartupAsDouble - start) / timeoutSec)) * 100f:F0}%)");
                await Task.Delay(TimeSpan.FromSeconds(pollSec), token);
            }

            throw new TimeoutException($"Timeout waiting for prompt_id={promptId}.");
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

            if (history["outputs"] is JObject || history["status"] is JObject)
            {
                entry = history;
                note = "using root object as entry fallback.";
                return true;
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

            string topStatus = TokenToFlatString(entry["status"]);
            bool topLevelCompleted =
                string.Equals(topStatus, "completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(topStatus, "success", StringComparison.OrdinalIgnoreCase);
            bool topLevelSuccess = TokenToBoolLoose(entry["success"]);

            bool nestedCompleted = false;
            bool nestedSuccess = false;
            string nestedStatus = string.Empty;
            if (entry["status"] is JObject statusObj)
            {
                nestedStatus = TokenToFlatString(statusObj["status_str"]);
                nestedCompleted = string.Equals(nestedStatus, "success", StringComparison.OrdinalIgnoreCase);
                nestedSuccess = TokenToBoolLoose(statusObj["completed"]) || TokenToBoolLoose(statusObj["success"]);
            }

            if (topLevelCompleted || topLevelSuccess || nestedCompleted || nestedSuccess)
            {
                summary = $"top.status='{topStatus}', top.success='{TokenToFlatString(entry["success"])}', nested.status_str='{nestedStatus}'";
                return true;
            }

            return false;
        }

        private string ExtractMotionJsonFromHistory(string historyJson, string promptId)
        {
            JObject history = JObject.Parse(historyJson);
            if (!TryResolveHistoryEntry(history, promptId, out JObject entry, out string note))
            {
                throw new Exception($"ComfyUI history for prompt_id={promptId} has no compatible entry shape. {note}.");
            }

            return ExtractMotionJsonFromEntry(entry, promptId);
        }

        private static string ExtractMotionJsonFromEntry(JObject entry, string promptIdForLog)
        {
            if (entry == null || entry["outputs"] is not JObject outputs)
            {
                return null;
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

        private static void CollectMotionJsonCandidates(JToken token, List<string> results)
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

        private static void AddCandidate(JToken token, List<string> results)
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

        private static string TryExtractMotionJson(JToken token)
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
                    if (parsed.Type == JTokenType.Object)
                    {
                        return parsed.ToString(Formatting.None);
                    }
                    if (parsed.Type == JTokenType.Array)
                    {
                        return TryExtractMotionJson(parsed);
                    }
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
                    if (!string.IsNullOrWhiteSpace(v))
                    {
                        return v;
                    }
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

        private async Task<string> SendJsonRequestAsync(
            string url,
            string method,
            string body,
            CancellationToken token,
            Action<string> onHttpFailure = null)
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
                string responseText = request.downloadHandler?.text ?? string.Empty;
                onHttpFailure?.Invoke(responseText);
                throw new Exception($"{method} {url} failed: {request.error}. status_code={request.responseCode}, body={TruncateForLog(responseText, HistoryLogMaxChars)}");
            }

            return request.downloadHandler.text;
        }

        private static bool TokenToBoolLoose(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return false;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<long>() != 0;
            }

            if (token.Type == JTokenType.Float)
            {
                return Math.Abs(token.Value<double>()) > double.Epsilon;
            }

            if (token.Type == JTokenType.String)
            {
                string s = token.Value<string>()?.Trim();
                return
                    string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s, "success", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s, "completed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s, "1", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static string TokenToFlatString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return string.Empty;
            }

            if (token.Type == JTokenType.String)
            {
                return token.Value<string>() ?? string.Empty;
            }

            return token.ToString(Formatting.None);
        }

        private static string TruncateForLog(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            {
                return value;
            }
            return value.Substring(0, maxChars) + "...(truncated)";
        }

        private static string TryBuildPromptFailureDetails(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return "ComfyUI returned an empty error body.";
            }

            try
            {
                JObject parsed = JObject.Parse(responseBody);
                JObject error = parsed["error"] as JObject;
                JObject extraInfo = error?["extra_info"] as JObject;
                string type = error?.Value<string>("type");
                string message = error?.Value<string>("message");
                string details = error?.Value<string>("details");
                string nodeErrors = parsed["node_errors"]?.ToString(Formatting.None);
                string extra = extraInfo?.ToString(Formatting.None);

                return
                    $"ComfyUI error_type='{type}', message='{message}', details='{details}', " +
                    $"extra_info={extra ?? "{}"}, node_errors={nodeErrors ?? "{}"}.";
            }
            catch
            {
                return $"ComfyUI error body: {TruncateForLog(responseBody, HistoryLogMaxChars)}";
            }
        }
    }
}
