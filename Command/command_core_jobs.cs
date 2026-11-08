using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KimodoUnityBridge;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;

namespace KimodoUnityBridge.Command

{
    internal static partial class command_context
    {
        private static TimelineCharacterRecord ResolveCharacter(TimelineSessionRecord session, string reference)
        {
            TimelineCharacterRecord sessionCharacter = ResolveSessionCharacterByReference(
                session,
                reference,
                addIfMissing: true);
            GameObject root = sessionCharacter.Root;
            Animator animator = sessionCharacter.Animator;
            if (root == null || animator == null || EditorUtility.IsPersistent(root) || !root.scene.IsValid())
            {
                throw new InvalidOperationException($"Character '{reference}' is no longer a valid scene Humanoid Animator.");
            }
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sessionCharacter.Avatar))
            {
                throw new InvalidOperationException($"Character '{sessionCharacter.Name}' does not provide a valid humanoid Avatar.");
            }
            KimodoTimelinePreviewRefreshUtility.InitializeSceneOffset(sessionCharacter.Track, animator);
            return sessionCharacter;
        }

        private static bool BindingMatches(UnityEngine.Object binding, Animator animator)
        {
            return binding == animator || binding == animator.gameObject;
        }

        private static UnityEngine.Object ResolveObject(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return null;
            }

            string trimmed = reference.Trim();
            if (GlobalObjectId.TryParse(trimmed, out GlobalObjectId globalId))
            {
                UnityEngine.Object globalObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId);
                if (globalObject != null)
                {
                    return globalObject;
                }
            }

            return trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? AssetDatabase.LoadMainAssetAtPath(trimmed)
                : null;
        }

        private static AnimationClip ResolveAnimationClip(string name)
        {
            string reference = name?.Trim();
            if (!string.IsNullOrWhiteSpace(reference) &&
                (reference.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                 reference.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)))
            {
                AnimationClip direct = AssetDatabase.LoadAssetAtPath<AnimationClip>(reference);
                if (direct != null) return direct;
            }
            AnimationClip[] matches = AssetDatabase.FindAssets($"t:AnimationClip {name}", new[] { "Assets" })
                .SelectMany(guid => AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)).OfType<AnimationClip>())
                .Where(clip => string.Equals(clip.name, name, StringComparison.OrdinalIgnoreCase))
                .Distinct().ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(matches.Length == 0
                    ? $"AnimationClip '{name}' was not found under Assets."
                    : $"AnimationClip name '{name}' is ambiguous; use a unique project clip name or an Assets/ or Packages/ path.");
            }
            return matches[0];
        }

        private static string GetObjectReference(UnityEngine.Object target)
        {
            return target == null ? string.Empty : GlobalObjectId.GetGlobalObjectIdSlow(target).ToString();
        }

        private static void Remember(
            UnityEngine.Object target,
            KimodoEditorGenerationJobSession session,
            TimelineGenerationTrace timelineGenerationTrace = null)
        {
            lock (JobsLock)
            {
                if (!Jobs.ContainsKey(session.RequestId))
                {
                    TrimTerminalGenerationJobsLocked();
                }
                Jobs[session.RequestId] = new JobRecord(target, session, timelineGenerationTrace);
            }
            PersistGenerationJobStatus(session);
        }

        private static void TrimTerminalGenerationJobsLocked()
        {
            while (Jobs.Count >= MaxRememberedJobs)
            {
                bool found = false;
                Guid oldestRequestId = default;
                DateTime oldestStartedAtUtc = DateTime.MaxValue;
                foreach (KeyValuePair<Guid, JobRecord> pair in Jobs)
                {
                    KimodoEditorGenerationJobStatus status = pair.Value?.Session?.Status ?? KimodoEditorGenerationJobStatus.None;
                    if (status != KimodoEditorGenerationJobStatus.Completed &&
                        status != KimodoEditorGenerationJobStatus.Failed &&
                        status != KimodoEditorGenerationJobStatus.Canceled)
                    {
                        continue;
                    }

                    DateTime startedAtUtc = pair.Value.Session.StartedAtUtc;
                    if (!found || startedAtUtc < oldestStartedAtUtc)
                    {
                        found = true;
                        oldestRequestId = pair.Key;
                        oldestStartedAtUtc = startedAtUtc;
                    }
                }

                // Active jobs must remain addressable so status, cancellation, persistence,
                // and Timeline range locking continue to work. The cap is a soft limit when
                // all remembered generations are still running.
                if (!found) return;
                Jobs.Remove(oldestRequestId);
            }
        }

        private static bool TryRemoveOldestTerminalTaskLocked(
            Dictionary<string, JObject> tasks,
            string firstTerminalStatus,
            string secondTerminalStatus)
        {
            string oldestTerminalKey = null;
            DateTime oldestStartedAtUtc = DateTime.MaxValue;
            foreach (KeyValuePair<string, JObject> pair in tasks)
            {
                string status = pair.Value?.Value<string>("status");
                if (string.Equals(status, firstTerminalStatus, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status, secondTerminalStatus, StringComparison.OrdinalIgnoreCase))
                {
                    DateTime startedAtUtc = DateTime.MinValue;
                    string startedAtValue = pair.Value?.Value<string>("started_at_utc")
                        ?? pair.Value?.Value<string>("retention_started_at_utc");
                    DateTime.TryParse(
                        startedAtValue,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out startedAtUtc);
                    if (oldestTerminalKey == null || startedAtUtc < oldestStartedAtUtc)
                    {
                        oldestTerminalKey = pair.Key;
                        oldestStartedAtUtc = startedAtUtc;
                    }
                }
            }
            return oldestTerminalKey != null && tasks.Remove(oldestTerminalKey);
        }

        private static void TrimTerminalTasksLocked(
            Dictionary<string, JObject> tasks,
            int maximumCount,
            string firstTerminalStatus,
            string secondTerminalStatus)
        {
            while (tasks.Count > maximumCount &&
                   TryRemoveOldestTerminalTaskLocked(tasks, firstTerminalStatus, secondTerminalStatus))
            {
            }
        }

        internal static void ClearRememberedJobsForTests()
        {
            lock (JobsLock)
            {
                Jobs.Clear();
            }
        }


        private static JObject BuildStatus(JobRecord record, bool includeBackendRequestJson = false)
        {
            KimodoEditorGenerationJobSession session = record.Session;
            TimelineGenerationTrace reservation = record.TimelineGenerationTrace;
            string generatedAssetPath = string.Empty;
            var result = new JObject
            {
                ["request_id"] = session.RequestId.ToString("D"),
                ["status"] = session.Status.ToString().ToLowerInvariant(),
                ["stage"] = session.Stage.ToString(),
                ["message"] = session.Message ?? string.Empty,
                ["error"] = session.Error ?? string.Empty,
                ["started_at_utc"] = session.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["eta_seconds"] = session.EstimatedSecondsRemaining.HasValue
                    ? (JToken)session.EstimatedSecondsRemaining.Value
                    : JValue.CreateNull(),
                ["estimated_completion_utc"] = session.EstimatedCompletionUtc ?? string.Empty,
                ["progress_current"] = session.ProgressCurrent,
                ["progress_total"] = session.ProgressTotal,
                ["progress_rate"] = session.ProgressRate.HasValue
                    ? (JToken)session.ProgressRate.Value
                    : JValue.CreateNull()
            };
            if (includeBackendRequestJson && !string.IsNullOrWhiteSpace(session.BackendRequestJson))
            {
                try
                {
                    result["backend_request_json"] = JToken.Parse(session.BackendRequestJson);
                }
                catch
                {
                    result["backend_request_json"] = session.BackendRequestJson;
                }
            }
            if (session.Payload is KimodoEditorGenerationResult generated)
            {
                result["seed"] = generated.Seed;
                result["prompt"] = generated.Prompt ?? string.Empty;
                if (generated.ConstraintLoss != null)
                    result["constraint_loss"] = generated.ConstraintLoss.DeepClone();
                if (!string.IsNullOrWhiteSpace(generated.AnalysisJson))
                {
                    try
                    {
                        result["analysis"] = JToken.Parse(generated.AnalysisJson);
                    }
                    catch
                    {
                        result["analysis"] = new JObject
                        {
                            ["warnings"] = new JArray("Returned analysis metadata could not be parsed.")
                        };
                    }
                }
                generatedAssetPath = generated.GeneratedClip != null
                    ? AssetDatabase.GetAssetPath(generated.GeneratedClip) ?? string.Empty
                    : string.Empty;
                if (generated.GeneratedClip != null)
                    result["clip_settings"] = AnimationClipInspector.InspectJson(generated.GeneratedClip, includeConsumers: false);
                result["post_analysis"] = BuildPostAnalysisStatus(
                    generated,
                    session.Status,
                    record.TimelineGenerationTrace?.Session,
                    session.RequestId);
            }
            else
            {
                if (record.TimelineGenerationTrace != null)
                {
                    result["post_analysis"] = new JObject
                    {
                        ["status"] = session.Status == KimodoEditorGenerationJobStatus.Running ? "pending" : "skipped"
                    };
                }
            }
            if (record.TimelineGenerationTrace?.PlayableClip != null)
            {
                KimodoPlayableClip placeholder = record.TimelineGenerationTrace.PlayableClip;
                placeholder.commandGenerationStatus = session.Status.ToString().ToLowerInvariant();
                if (session.Status == KimodoEditorGenerationJobStatus.Failed ||
                    session.Status == KimodoEditorGenerationJobStatus.Canceled)
                {
                    placeholder.isGenerated = false;
                    placeholder.commandGenerationError = string.IsNullOrWhiteSpace(session.Error)
                        ? session.Message ?? string.Empty
                        : session.Error;
                }
                else
                {
                    placeholder.commandGenerationError = string.Empty;
                }
                EditorUtility.SetDirty(placeholder);
            }
            if (reservation != null)
            {
                if (reservation.InOutSampling != null) result["inout_sampling"] = reservation.InOutSampling.DeepClone();
                if (reservation.Animation != null)
                {
                    result["animation_id"] = reservation.Animation.Id.ToString("D");
                }

                JObject clip = new JObject
                {
                    ["start_timeline_frame_60"] = Mathf.RoundToInt((float)(reservation.StartSeconds * SessionFrameRate)),
                    ["end_timeline_frame_60_exclusive"] = Mathf.RoundToInt((float)((reservation.StartSeconds + reservation.DurationSeconds) * SessionFrameRate))
                };
                if (reservation.Animation != null) clip["name"] = reservation.Animation.Name;
                if (reservation.Character != null) clip["character"] = reservation.Character.Name;
                if (!string.IsNullOrWhiteSpace(generatedAssetPath)) clip["asset_path"] = generatedAssetPath;
                result["clip"] = clip;
            }
            return result;
        }

        private static JObject BuildPostAnalysisStatus(
            KimodoEditorGenerationResult generated,
            KimodoEditorGenerationJobStatus generationStatus,
            TimelineSessionRecord session = null,
            Guid requestId = default)
        {
            if (generated?.PostAnalysisSkipped == true)
            {
                return new JObject { ["status"] = "skipped" };
            }
            if (!string.IsNullOrWhiteSpace(generated?.PostAnalysisJson))
            {
                JObject parsed;
                try
                {
                    parsed = JObject.Parse(generated.PostAnalysisJson);
                }
                catch
                {
                    return new JObject
                    {
                        ["status"] = "failed",
                        ["error"] = new JObject
                        {
                            ["code"] = "post_analysis_result_invalid",
                            ["message"] = "Post-generation analysis returned invalid JSON."
                        }
                    };
                }

                if (session == null)
                {
                    return new JObject
                    {
                        ["status"] = "failed",
                        ["error"] = new JObject
                        {
                            ["code"] = "post_analysis_path_unavailable",
                            ["message"] = "Post-generation analysis cannot be exposed without its owning scene session."
                        }
                    };
                }

                string path = Path.Combine(
                    GetSessionGeneratedFolder(session),
                    "Analyses",
                    $"generation_{requestId:D}_post_analysis.json");
                try
                {
                    if (!File.Exists(path)) WriteJsonAtomically(path, parsed);
                    return new JObject
                    {
                        ["status"] = "completed",
                        ["analysis_path"] = ToProjectRelativePath(path)
                    };
                }
                catch (Exception exception)
                {
                    return new JObject
                    {
                        ["status"] = "failed",
                        ["error"] = new JObject
                        {
                            ["code"] = "post_analysis_persist_failed",
                            ["message"] = exception.Message
                        }
                    };
                }
            }

            if (!string.IsNullOrWhiteSpace(generated?.PostAnalysisError))
            {
                return new JObject
                {
                    ["status"] = "failed",
                    ["error"] = new JObject
                    {
                        ["code"] = "post_analysis_failed",
                        ["message"] = generated.PostAnalysisError
                    }
                };
            }

            if (generationStatus != KimodoEditorGenerationJobStatus.Completed)
            {
                return new JObject { ["status"] = "skipped" };
            }

            return new JObject
            {
                ["status"] = "failed",
                ["error"] = new JObject
                {
                    ["code"] = "post_analysis_unavailable",
                    ["message"] = "No post-generation analysis result was recorded."
                }
            };
        }

        private static JObject NormalizeInstallStatus(JObject source, string requestId)
        {
            JObject result = (JObject)source.DeepClone();
            string status = result.Value<string>("status") ?? string.Empty;
            string message = result.Value<string>("message") ?? string.Empty;
            result["request_id"] = requestId;
            result.Remove("task_id");
            result.Remove("retention_started_at_utc");
            result.Remove("progress");
            result["status"] = status.ToLowerInvariant() switch
            {
                "queued" => "accepted",
                "loading" => "running",
                "done" => "completed",
                "error" => "failed",
                _ => status.ToLowerInvariant()
            };
            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                result["error"] = new JObject
                {
                    ["code"] = "installation_failed",
                    ["message"] = message
                };
                result.Remove("message");
            }
            else
            {
                result.Remove("error");
            }
            return result;
        }

        private static JObject NormalizeGenerationStatus(JObject source, string requestId, bool includeRequestJson = false)
        {
            JObject result = (JObject)source.DeepClone();
            JObject persistedClip = source["clip"] as JObject;
            result["request_id"] = requestId;
            result.Remove("task_id");
            string progress = result.Value<string>("progress") ?? string.Empty;
            result.Remove("progress");
            string[] progressParts = progress.Split('/');
            if (progressParts.Length == 2 &&
                int.TryParse(progressParts[0], out int progressCurrent) &&
                int.TryParse(progressParts[1], out int progressTotal))
            {
                result["progress_current"] = progressCurrent;
                result["progress_total"] = progressTotal;
            }
            result.Remove("prompt");
            JToken completedSeed = source["seed"]?.DeepClone();
            result.Remove("seed");
            result.Remove("animation");
            result.Remove("character");
            result.Remove("path");
            result.Remove("generated_asset_path");

            if (!includeRequestJson) result.Remove("backend_request_json");

            if (result["error"] is JValue errorValue && errorValue.Type == JTokenType.String)
            {
                string message = errorValue.Value<string>();
                if (string.IsNullOrWhiteSpace(message))
                {
                    result.Remove("error");
                }
                else
                {
                    result["error"] = new JObject
                    {
                        ["code"] = "generation_failed",
                        ["message"] = message
                    };
                }
            }

            if (string.Equals(result.Value<string>("status"), "completed", StringComparison.OrdinalIgnoreCase))
            {
                if (completedSeed != null) result["seed"] = completedSeed;
                JObject clip = persistedClip != null ? (JObject)persistedClip.DeepClone() : new JObject();
                if (source.Value<string>("animation") is string name) clip["name"] = name;
                if (source.Value<string>("character") is string character) clip["character"] = character;
                if (source.Value<int?>("start_timeline_frame_60") is int startFrame)
                    clip["start_timeline_frame_60"] = startFrame;
                if (source.Value<int?>("duration_frames") is int durationFrames)
                {
                    if (source.Value<int?>("start_timeline_frame_60") is int clipStart)
                        clip["end_timeline_frame_60_exclusive"] = clipStart + durationFrames;
                }
                string assetPath = source.Value<string>("path");
                if (!string.IsNullOrWhiteSpace(assetPath)) clip["asset_path"] = assetPath;
                if (clip.HasValues) result["clip"] = clip;
            }
            else
            {
                result.Remove("clip");
            }
            result.Remove("start_timeline_frame_60");
            result.Remove("duration_frames");
            return result;
        }

        private static string Execute(string argumentsJson, Func<JObject, string> action)
        {
            try
            {
                JObject arguments = ParseCommandArguments(argumentsJson);
                return Execute(arguments, action);
            }
            catch (Exception ex)
            {
                return Error(ex);
            }
        }

        private static string Execute(JObject arguments, Func<JObject, string> action)
        {
            try
            {
                return action(arguments ?? new JObject());
            }
            catch (Exception ex)
            {
                return Error(ex);
            }
        }

        private static string Error(Exception ex)
        {
            if (ex is GenerationRangeLockedException locked) return Error(locked);
            if (ex is CommandException command) return Error(command.Code, command.Message);
            if (ex is OperationCanceledException) return Error("canceled", ex.Message);
            if (ex is TimeoutException) return Error("timeout", ex.Message);
            if (ex is SocketException || ex is IOException &&
                !(ex is FileNotFoundException || ex is DirectoryNotFoundException ||
                  ex is DriveNotFoundException || ex is PathTooLongException))
                return Error("bridge_unavailable", ex.Message);
            if (ex is ArgumentException || ex is FormatException || ex is JsonException)
                return Error("invalid_argument", ex.Message);
            if (ex is InvalidOperationException) return Error("invalid_operation", ex.Message);
            return Error("internal_error", ex.Message);
        }

        private static JObject ParseCommandArguments(string argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson)) return new JObject();

            using (var textReader = new StringReader(argumentsJson))
            using (var jsonReader = new JsonTextReader(textReader) { DateParseHandling = DateParseHandling.None })
            {
                JObject arguments = JObject.Load(jsonReader);
                if (jsonReader.Read())
                {
                    throw new FormatException("Additional content after the command arguments object.");
                }
                return arguments;
            }
        }

        private static string Started(KimodoEditorGenerationJobSession session, JObject extra)
        {
            extra["request_id"] = session.RequestId.ToString("D");
            extra["status"] = "accepted";
            return Ok(extra);
        }

        private static string Ok(JObject result)
        {
            result ??= new JObject();
            result["ok"] = true;
            return result.ToString(Formatting.None);
        }

        private static string Error(string code, string message)
        {
            return new JObject
            {
                ["ok"] = false,
                ["error"] = new JObject
                {
                    ["code"] = string.IsNullOrWhiteSpace(code) ? "invalid_argument" : code,
                    ["message"] = message ?? string.Empty
                }
            }.ToString(Formatting.None);
        }

        private static string Error(GenerationRangeLockedException error)
        {
            return new JObject
            {
                ["ok"] = false,
                ["error"] = new JObject
                {
                    ["code"] = "generation_range_locked",
                    ["message"] = error.Message,
                    ["details"] = new JObject
                    {
                        ["command"] = error.Command,
                        ["request_id"] = error.RequestId.ToString("D"),
                        ["character"] = error.Character,
                        ["track"] = error.Track,
                        ["locked_range"] = new JArray(error.LockedStartFrame, error.LockedEndFrame),
                        ["requested_range"] = new JArray(error.RequestedStartFrame, error.RequestedEndFrame),
                        ["action"] = $"Wait for generation completion or cancel request {error.RequestId:D}."
                    }
                }
            }.ToString(Formatting.None);
        }

        private static string RequiredStringValue(JObject arguments, string name)
        {
            string value = arguments.Value<string>(name)?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{name} is required.");
            }
            return value;
        }


        private static Guid RequiredRequestId(JObject arguments)
        {
            string value = RequiredStringValue(arguments, "request_id");
            if (!Guid.TryParse(value, out Guid requestId))
            {
                throw new InvalidOperationException("request_id is not a valid GUID.");
            }
            return requestId;
        }

        private static string ResolveModelName(string modelName)
        {
            if (!string.IsNullOrWhiteSpace(modelName))
            {
                string candidate = modelName.Trim();
                if (candidate.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                {
                    throw new InvalidOperationException("model must be a registered model name/configuration id from kimodo.help section models, not a filesystem path.");
                }
            }
            return KimodoMotionModelProfiles.NormalizeName(string.IsNullOrWhiteSpace(modelName)
                ? KimodoPlayableClipGenerationSettings.instance.DefaultBridgeModelName
                : modelName);
        }

        private static bool TryGetJob(Guid requestId, out JobRecord record)
        {
            lock (JobsLock)
            {
                return Jobs.TryGetValue(requestId, out record);
            }
        }

        internal static void PersistGenerationJobStatus(KimodoEditorGenerationJobSession jobSession)
        {
            if (jobSession == null) return;
            JobRecord record;
            lock (JobsLock)
            {
                Jobs.TryGetValue(jobSession.RequestId, out record);
            }
            if (record?.TimelineGenerationTrace == null) return;
            TimelineSessionRecord timelineSession = record.TimelineGenerationTrace.Session;
            JObject status = BuildStatus(record, includeBackendRequestJson: true);
            WriteJsonAtomically(GenerationJobPath(timelineSession, jobSession.RequestId), status);
            PersistSessionArtifacts(timelineSession);
            RecordGenerationTerminalHistory(timelineSession, status);
            EditorUtility.SetDirty(timelineSession.TimelineAsset);
            AssetDatabase.SaveAssets();
        }

        private static string GenerationJobPath(TimelineSessionRecord session, Guid requestId) =>
            System.IO.Path.Combine(GetSessionGeneratedFolder(session), "Generations", $"generation_{requestId:D}.json");

        private static JObject LoadPersistedGenerationJob(TimelineSessionRecord session, Guid requestId)
        {
            string path = GenerationJobPath(session, requestId);
            if (!System.IO.File.Exists(path))
                throw new InvalidOperationException($"Unknown request_id '{requestId:D}' in the current scene context.");
            return JObject.Parse(System.IO.File.ReadAllText(path));
        }

        private static void EnsureGenerationBelongsToSession(JobRecord record, TimelineSessionRecord session)
        {
            if (record?.TimelineGenerationTrace == null || session == null ||
                record.TimelineGenerationTrace.Session == null ||
                record.TimelineGenerationTrace.Session.Id != session.Id)
                throw new InvalidOperationException("request_id belongs to a different scene context.");
        }

        private static JObject EnsureRegisteredModel(string modelName, KimodoTextEncoderMode textEncoderMode)
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            JObject response;
            using (var timeout = new CancellationTokenSource(CommandBridgeQueryTimeoutMs))
            {
                response = KimodoBridgeService.Shared.ListModelConfigurationsAsync(
                    modelName,
                    KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode),
                    settings.LocalModelsPath?.Trim() ?? string.Empty,
                    null,
                    timeout.Token).GetAwaiter().GetResult();
            }
            JObject found = (response["configs"] as JArray)?.Values<JObject>().FirstOrDefault(config =>
                string.Equals(config.Value<string>("model"), modelName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    config.Value<string>("text_encoder_model"),
                    KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode),
                    StringComparison.OrdinalIgnoreCase) &&
                config.Value<bool?>("available") != false);
            if (found == null)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}' with text_encoder_model '{KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode)}' is not listed by kimodo.help section models.");
            }
            return found;
        }

        internal static KimodoTextEncoderMode ResolveTextEncoderMode(string textEncoderModel)
        {
            if (string.IsNullOrWhiteSpace(textEncoderModel))
            {
                return KimodoPlayableClipGenerationSettings.instance.DefaultTextEncoderMode;
            }

            string normalized = textEncoderModel.Trim().ToLowerInvariant().Replace('-', '_');
            if (normalized == KimodoTextEncoderModeProtocol.HighPerformance)
            {
                return KimodoTextEncoderMode.HighPerformance;
            }
            if (normalized == KimodoTextEncoderModeProtocol.HighPrecision)
            {
                return KimodoTextEncoderMode.HighPrecision;
            }

            throw new InvalidOperationException(
                $"text_encoder_model must be '{KimodoTextEncoderModeProtocol.HighPerformance}' or '{KimodoTextEncoderModeProtocol.HighPrecision}'.");
        }

        private static float ResolveFrameRate(string modelName, JObject configuration)
        {
            double? configured = configuration?.Value<double?>("source_fps");
            if (configured.HasValue && configured.Value > 0.0 && !double.IsNaN(configured.Value) && !double.IsInfinity(configured.Value))
            {
                return (float)configured.Value;
            }
            return KimodoMotionModelProfiles.TryGet(modelName, out KimodoMotionModelProfile profile)
                ? profile.SourceFps
                : KimodoMotionModelProfiles.DefaultFrameRate;
        }

        private static int ResolveDiffusionSteps(JObject arguments, string modelName, JObject configuration)
        {
            int? supplied = arguments.Value<int?>("diffusion_steps");
            int? configuredMaximum = configuration?.Value<int?>("max_diffusion_steps");
            int? configuredDefault = configuration?.Value<int?>("default_diffusion_steps");
            if (configuredMaximum.HasValue && configuredMaximum.Value > 0)
            {
                bool isArdy = string.Equals(configuration.Value<string>("backend"), "ardy", StringComparison.OrdinalIgnoreCase);
                return supplied.HasValue
                    ? Mathf.Clamp(supplied.Value, isArdy ? 0 : 1, configuredMaximum.Value)
                    : (isArdy ? 0 : Mathf.Clamp(configuredDefault ?? 100, 1, configuredMaximum.Value));
            }
            if (KimodoMotionModelProfiles.TryGet(modelName, out KimodoMotionModelProfile profile))
            {
                int minimum = profile.IsArdy ? 0 : 1;
                int fallback = profile.IsArdy ? 0 : profile.DefaultDiffusionSteps;
                return supplied.HasValue
                    ? Mathf.Clamp(supplied.Value, minimum, profile.MaxDiffusionSteps)
                    : fallback;
            }
            return supplied.HasValue ? Mathf.Clamp(supplied.Value, 1, 1000) : 100;
        }


        private static void EnsureCanGenerate() => EnsureEditorIsIdle("Kimodo animation asset generation");

        private static void EnsureCanManageServer() => EnsureEditorIsIdle("Kimodo server maintenance");

        private static void EnsureEditorIsIdle(string operation)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Unity is compiling or importing assets. Retry when the Editor is ready.");
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException($"{operation} is available in Edit Mode only.");
            }
        }

    }
}
