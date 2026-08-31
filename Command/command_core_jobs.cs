using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private static ResolvedCharacter ResolveCharacter(TimelineSessionRecord session, string name)
        {
            TimelineCharacterRecord sessionCharacter = ResolveSessionCharacterByReference(session, name, addIfMissing: false);
            GameObject root = sessionCharacter.Root;
            Animator animator = sessionCharacter.Animator;
            if (root == null || animator == null || EditorUtility.IsPersistent(root) || !root.scene.IsValid())
            {
                throw new InvalidOperationException($"Character '{name}' is no longer a valid scene Humanoid Animator.");
            }
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(sessionCharacter.Avatar))
            {
                throw new InvalidOperationException($"Character '{sessionCharacter.Name}' does not provide a valid humanoid Avatar.");
            }
            return new ResolvedCharacter(root, animator, sessionCharacter.Avatar, sessionCharacter.Name);
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
                if (Jobs.Count >= MaxRememberedJobs)
                {
                    Guid oldest = Jobs.OrderBy(pair => pair.Value.Session.StartedAtUtc).First().Key;
                    Jobs.Remove(oldest);
                }
                Jobs[session.RequestId] = new JobRecord(target, session, timelineGenerationTrace);
            }
            PersistGenerationJobStatus(session);
        }

        internal static void ClearRememberedJobsForTests()
        {
            lock (JobsLock)
            {
                Jobs.Clear();
            }
        }

        private static JobRecord GetJob(Guid requestId)
        {
            lock (JobsLock)
            {
                if (!Jobs.TryGetValue(requestId, out JobRecord record))
                {
                    throw new InvalidOperationException($"Unknown or expired request_id '{requestId}'.");
                }
                return record;
            }
        }

        private static JObject BuildStatus(JobRecord record)
        {
            KimodoEditorGenerationJobSession session = record.Session;
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
            if (session.Payload is KimodoEditorGenerationResult generated)
            {
                result["seed"] = generated.Seed;
                result["prompt"] = generated.Prompt ?? string.Empty;
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
                result["path"] = generated.GeneratedClip != null
                    ? AssetDatabase.GetAssetPath(generated.GeneratedClip) ?? string.Empty
                    : string.Empty;
            }
            else
            {
                result["path"] = string.Empty;
            }
            if (record.TimelineGenerationTrace != null)
            {
                TimelineGenerationTrace reservation = record.TimelineGenerationTrace;
                result["session_name"] = reservation.Session.Name;
                result["start_frame"] = Mathf.RoundToInt((float)(reservation.StartSeconds * SessionFrameRate));
                result["duration_frames"] = Mathf.RoundToInt((float)(reservation.DurationSeconds * SessionFrameRate));
                if (reservation.Animation != null)
                {
                    result["animation"] = reservation.Animation.Name;
                }
            }
            return result;
        }

        private static string Execute(string argumentsJson, Func<JObject, string> action)
        {
            try
            {
                JObject arguments = string.IsNullOrWhiteSpace(argumentsJson)
                    ? new JObject()
                    : JObject.Parse(argumentsJson);
                return action(arguments);
            }
            catch (Exception ex)
            {
                if (ex is GenerationRangeLockedException locked)
                {
                    return Error(locked);
                }
                if (ex is CommandException command)
                {
                    return Error(command.Code, command.Message);
                }
                return Error("invalid_argument", ex.Message);
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
            if (currentTimelineSession != null)
            {
                result["session_id"] = currentTimelineSession.Id.ToString("D");
                result["session_json_path"] = currentTimelineSession.Metadata?.sessionJsonPath ?? string.Empty;
                result["session_revision"] = currentTimelineSession.Metadata?.sessionRevision ?? 0;
            }
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

        private static Vector2 RequiredVector2(JObject value, string name)
        {
            JArray array = value?[name] as JArray;
            if (array == null || array.Count != 2)
            {
                throw new InvalidOperationException($"{name} must be [x,z].");
            }
            if ((array[0].Type != JTokenType.Integer && array[0].Type != JTokenType.Float) ||
                (array[1].Type != JTokenType.Integer && array[1].Type != JTokenType.Float))
            {
                throw new InvalidOperationException($"{name} must contain finite numbers.");
            }
            float x = array[0].Value<float>();
            float y = array[1].Value<float>();
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y))
            {
                throw new InvalidOperationException($"{name} must contain finite numbers.");
            }
            return new Vector2(x, y);
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
                    throw new InvalidOperationException("model must be a registered model name/configuration id from kimodo_help section models, not a filesystem path.");
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
            JObject status = BuildStatus(record);
            WriteJsonAtomically(GenerationJobPath(timelineSession, jobSession.RequestId), status);
            UpdateGenerationHistory(timelineSession, status);
            PersistTimelineSessionMetadata(timelineSession);
            EditorUtility.SetDirty(timelineSession.TimelineAsset);
            AssetDatabase.SaveAssets();
        }

        private static string GenerationJobPath(TimelineSessionRecord session, Guid requestId) =>
            System.IO.Path.Combine(GetSessionGeneratedFolder(session), "Generations", $"generation_{requestId:D}.json");

        private static JObject LoadPersistedGenerationJob(TimelineSessionRecord session, Guid requestId)
        {
            string path = GenerationJobPath(session, requestId);
            if (!System.IO.File.Exists(path))
                throw new InvalidOperationException($"Unknown request_id '{requestId:D}' in the selected Session.");
            return JObject.Parse(System.IO.File.ReadAllText(path));
        }

        private static void EnsureGenerationBelongsToSession(JobRecord record, TimelineSessionRecord session)
        {
            if (record?.TimelineGenerationTrace == null || !ReferenceEquals(record.TimelineGenerationTrace.Session, session))
                throw new InvalidOperationException("request_id belongs to a different Session.");
        }

        private static void UpdateGenerationHistory(TimelineSessionRecord session, JObject status)
        {
            if (session?.Metadata == null || status == null) return;
            string requestId = status.Value<string>("request_id") ?? string.Empty;
            KimodoCommandGenerationMetadata history = (session.Metadata.generations ??= new List<KimodoCommandGenerationMetadata>())
                .FirstOrDefault(item => string.Equals(item.requestId, requestId, StringComparison.OrdinalIgnoreCase));
            if (history == null)
            {
                history = new KimodoCommandGenerationMetadata { requestId = requestId };
                session.Metadata.generations.Add(history);
            }
            history.character = status.Value<string>("character") ?? history.character ?? string.Empty;
            history.animation = status.Value<string>("animation") ?? history.animation ?? string.Empty;
            history.status = status.Value<string>("status") ?? string.Empty;
            history.stage = status.Value<string>("stage") ?? string.Empty;
            history.message = status.Value<string>("message") ?? string.Empty;
            history.error = status.Value<string>("error") ?? string.Empty;
            history.startedAtUtc = status.Value<string>("started_at_utc") ?? history.startedAtUtc ?? string.Empty;
            history.updatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }

        private static JObject EnsureRegisteredModel(string modelName, KimodoTextEncoderMode textEncoderMode)
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            JObject response = KimodoBridgeService.Shared.ListModelConfigurationsAsync(
                modelName,
                KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode),
                settings.LocalModelsPath?.Trim() ?? string.Empty,
                null,
                CancellationToken.None).GetAwaiter().GetResult();
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
                    $"Model '{modelName}' with text_encoder_model '{KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode)}' is not listed by kimodo_help section models.");
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

        private static float PositiveFloat(JObject arguments, string name, float fallback)
        {
            double value = PositiveDouble(arguments, name, fallback);
            return (float)value;
        }

        private static double PositiveDouble(JObject arguments, string name, double fallback)
        {
            double value = arguments.Value<double?>(name) ?? fallback;
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            {
                throw new InvalidOperationException($"{name} must be positive and finite.");
            }
            return value;
        }

        private static double NonNegativeDouble(JObject arguments, string name, double fallback)
        {
            double value = arguments.Value<double?>(name) ?? fallback;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
            {
                throw new InvalidOperationException($"{name} must be non-negative and finite.");
            }
            return value;
        }

        private static void EnsureCanGenerate()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Unity is compiling or importing assets. Retry when the Editor is ready.");
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("Kimodo animation asset generation is available in Edit Mode only.");
            }
        }

        private static void EnsureCanManageServer()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                throw new InvalidOperationException("Unity is compiling or importing assets. Retry when the Editor is ready.");
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("Kimodo server maintenance is available in Edit Mode only.");
            }
        }

    }
}
