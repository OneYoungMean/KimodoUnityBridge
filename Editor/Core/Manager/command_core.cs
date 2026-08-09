using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    /// <summary>
    /// Shared implementation behind the framework-neutral Kimodo command entry points.
    /// </summary>
    internal static partial class command_context
    {
        public const string ListCharactersCommand = "kimodo_list_characters";
        public const string ListModelsCommand = "Kimodo_list_models";
        public const string HelpCommand = "kimodo_help";
        public const string DebugInstallServerCommand = "kimodo_debug_install_server";
        public const string GenerateAnimationAssetCommand = "kimodo_generate_animation_asset";
        public const string SessionOpenTimelineCommand = "session_open_timeline";
        public const string SessionCloseTimelineCommand = "session_close_timeline";
        public const string QueryCurrentSessionCommand = "query_current_session";
        public const string SessionLocateAnimationCommand = "session_locate_animation";
        public const string SessionSamplePoseCommand = "session_sample_pose";
        public const string SessionTryAddCommand = "session_try_add";
        public const string SessionTryRemoveCommand = "session_try_remove";
        public const string KimodoAnalyzeTimelineRangeCommand = "kimodo_analyze_timeline_range";
        public const string KimodoBakeTimelineRangeCommand = "kimodo_bake_timeline_range";
        public const string QueryGenerationCommand = "kimodo_get_generation";
        public const string QueryCancelGenerationCommand = "kimodo_cancel_generation";

        private const int MaxRememberedJobs = 128;
        private static readonly Dictionary<Guid, JobRecord> Jobs = new Dictionary<Guid, JobRecord>();
        private static readonly object JobsLock = new object();

        public static string GetCommandDefinitionsJson()
        {
            return new JObject
            {
                ["tools"] = new JArray
                {
                    CommandDefinition(ListCharactersCommand,
                        "List humanoid characters that can be used for Kimodo animation generation.",
                        Properties(
                            Optional("include_project_assets", "boolean", "Also scan prefab/model assets under Assets."),
                            Optional("max_results", "integer", "Maximum returned characters; defaults to 100."))),
                    CommandDefinition(ListModelsCommand,
                        "List every model and text encoder configuration currently viable on the Kimodo QuickServer. Call this only when the user wants to switch away from the Project Settings defaults.",
                        Properties()),
                    CommandDefinition(HelpCommand,
                        "Return the Kimodo QuickServer built-in protocol reference.",
                        Properties()),
                    CommandDefinition(DebugInstallServerCommand,
                        "[debug-only] Incrementally install the QuickServer runtime from the package template, preserving models and the Python environment, then restart it.",
                        Properties(),
                        debugOnly: true),
                    CommandDefinition(SessionOpenTimelineCommand,
                        "Create a new current Session, or load an existing named Session. The Session creates its own Director and TimelineAsset, adds scene Animators, and opens Timeline preview.",
                        Properties(
                            Optional("session_name", "string", "Existing Session name to load; omitted always creates a new Session."))),
                    CommandDefinition(SessionCloseTimelineCommand,
                        "Close the current Timeline editing environment, clear Timeline selection, and preserve the Session, Director, and TimelineAsset for a later named reopen.",
                        Properties()),
                    CommandDefinition(QueryCurrentSessionCommand,
                        "Query the current Session using Maya ls-like object type, wildcard pattern, and result limits. The legacy operation selector remains accepted.",
                        Properties(
                            Enum("type", "session", "character", "animation"),
                            Optional("pattern", "string", "Wildcard name pattern; * matches any sequence and ? matches one character. Defaults to *."),
                            OptionalArray("objects", "string", "Explicit character or animation names/references to match; analogous to ls positional objects."),
                            Optional("long", "boolean", "Return full object details; defaults to true."),
                            Optional("head", "integer", "Return only the first N matches."),
                            Optional("tail", "integer", "Return only the last N matches."),
                            Optional("show_type", "boolean", "Include the resolved object type in each returned item."),
                            Enum("operation", "session", "characters", "character", "animations", "animation", "analysis"),
                            Optional("character_ref", "string", "Scene character GlobalObjectId."),
                            Optional("character_name", "string", "Character name in the current Session."),
                            Optional("animation_id", "string", "Animation id returned by this query."),
                            Optional("animation_name", "string", "Animation name in the selected character."))),
                    CommandDefinition(SessionLocateAnimationCommand,
                        "Move the current Director to a flattened animation's global time and select that Timeline clip.",
                        Properties(
                            Optional("character_ref", "string", "Scene character GlobalObjectId."),
                            Optional("character_name", "string", "Character name in the current Session."),
                            Optional("animation_id", "string", "Animation id returned by query_current_session."),
                            Optional("animation_name", "string", "Animation name in the selected character."),
                            Optional("session_global", "number", "Global Session time; defaults to the animation start."))),
                    CommandDefinition(SessionSamplePoseCommand,
                        "Evaluate the current Timeline at a global Session time and return a reusable pose_sample_id plus humanoid pose data.",
                        Properties(
                            Required("session_global", "number", "Global Session time in seconds."),
                            Optional("character_ref", "string", "Scene character GlobalObjectId."),
                            Optional("character_name", "string", "Character name in the current Session."))),
                    CommandDefinition(SessionTryAddCommand,
                        "TryAdd a scene character or AnimationClip to the current Session. Clips are always appended at the end of the character track.",
                        Properties(
                            Required("kind", "string", "character or clip."),
                            Optional("character_ref", "string", "Scene character GlobalObjectId."),
                            Optional("character_name", "string", "Character name in the current Session."),
                            Optional("clip_ref", "string", "AnimationClip GlobalObjectId or Assets/... path."))),
                    CommandDefinition(SessionTryRemoveCommand,
                        "TryRemove a character track or one clip. Removing a clip does not move other clips or reuse its virtual time address.",
                        Properties(
                            Required("kind", "string", "character or clip."),
                            Optional("character_ref", "string", "Scene character GlobalObjectId."),
                            Optional("character_name", "string", "Character name in the current Session."),
                            Optional("animation_id", "string", "Animation id returned by query_current_session."),
                            Optional("animation_name", "string", "Animation name in the selected character."))),
                    CommandDefinition(KimodoAnalyzeTimelineRangeCommand,
                        "Return stored generation keyframe analysis and range analysis for the current character Timeline range.",
                        Properties(
                            Required("start_global", "number", "Inclusive global Session start."),
                            Required("end_global", "number", "Exclusive global Session end."),
                            Optional("character_ref", "string", "Scene character GlobalObjectId."),
                            Optional("character_name", "string", "Character name in the current Session."),
                            Optional("analysis_option", "object", "Optional QuickServer analysis configuration; analysis_only is forced true."))),
                    CommandDefinition(KimodoBakeTimelineRangeCommand,
                        "Bake a Timeline global range into an AnimationClip and append it to a character track; optionally retarget it to another current Session character.",
                        Properties(
                            Required("start_global", "number", "Inclusive global Session start."),
                            Required("end_global", "number", "Exclusive global Session end."),
                            Optional("character_ref", "string", "Source scene character GlobalObjectId."),
                            Optional("character_name", "string", "Source character name in the current Session."),
                            Optional("retarget_character_ref", "string", "Optional target character; its track is used and a valid humanoid Avatar is required."),
                            Optional("asset_name", "string", "Output AnimationClip name without extension."),
                            Optional("output_folder", "string", "Unity folder under Assets; defaults to Assets/KimodoGeneratedClips."))),
                    CommandDefinition(GenerateAnimationAssetCommand,
                        "Generate an AnimationClip asset for a humanoid character from a text prompt. A current Timeline Session is required; call session_open_timeline first.",
                        Properties(
                            Required("character_ref", "string", "Scene character GlobalObjectId in the current Session."),
                            Required("prompt", "string", "Motion prompt."),
                            Optional("duration_seconds", "number", "Duration in seconds; defaults to 5."),
                            Optional("model", "string", "Registered model name/configuration id; omitted uses the Project Settings default. Call Kimodo_list_models only when switching models."),
                            Enum("text_encoder_model", "high_performance", "high_precision"),
                            Optional("seed", "integer", "Deterministic seed; omitted chooses a random seed."),
                            Optional("diffusion_steps", "integer", "Diffusion steps; omitted uses the model default."),
                            Enum("output_mode", "humanoid_muscle", "character_bone", "model_bone"),
                            Optional("output_folder", "string", "Unity folder under Assets; defaults to Assets/KimodoGeneratedClips."),
                             Optional("asset_name", "string", "Output asset name without extension."),
                             Optional("loop", "boolean", "When true, the server generates a seed motion and regenerates it with the seed first pose constrained at both boundary frames. Root translation remains unconstrained."),
                             Optional("analysis_option", "object", "Optional analysis object; set keyframes.enabled=true to return screenshot keyframes."),
                            OptionalArray("pose_refs", "string", "Scene humanoid GameObject or Animator GlobalObjectIds used as pose constraints."),
                            OptionalArray("times", "number", "Pose times in seconds; omitted distributes poses from the first through the last generated frame."),
                            OptionalEnumArray("constraint_types", "Constraint type per pose; omitted defaults every pose to fullbody.", "fullbody", "root2d"))),
                    CommandDefinition(QueryGenerationCommand,
                        "Get generation progress and the generated AnimationClip asset path.",
                        Properties(Required("request_id", "string", "Request id returned by a generate tool."))),
                    CommandDefinition(QueryCancelGenerationCommand,
                        "Cancel an active Kimodo generation request.",
                        Properties(
                            Required("request_id", "string", "Request id returned by a generate tool."),
                            Optional("reason", "string", "Optional cancellation reason.")))
                }
            }.ToString(Formatting.None);
        }

        public static string Invoke(string toolName, string argumentsJson = "{}")
        {
            switch (toolName?.Trim())
            {
                case ListCharactersCommand:
                    return ListCharacters(argumentsJson);
                case ListModelsCommand:
                    return ListModels(argumentsJson);
                case HelpCommand:
                    return GetServerHelp(argumentsJson);
                case DebugInstallServerCommand:
                    return DebugInstallServer(argumentsJson);
                case SessionOpenTimelineCommand:
                    return SessionOpenTimeline(argumentsJson);
                case SessionCloseTimelineCommand:
                    return SessionCloseTimeline(argumentsJson);
                case QueryCurrentSessionCommand:
                    return QueryCurrentSession(argumentsJson);
                case SessionLocateAnimationCommand:
                    return SessionLocateAnimation(argumentsJson);
                case SessionSamplePoseCommand:
                    return SessionSamplePose(argumentsJson);
                case SessionTryAddCommand:
                    return SessionTryAdd(argumentsJson);
                case SessionTryRemoveCommand:
                    return SessionTryRemove(argumentsJson);
                case KimodoAnalyzeTimelineRangeCommand:
                    return KimodoAnalyzeTimelineRange(argumentsJson);
                case KimodoBakeTimelineRangeCommand:
                    return KimodoBakeTimelineRange(argumentsJson);
                case GenerateAnimationAssetCommand:
                    return GenerateAnimationAsset(argumentsJson);
                case QueryGenerationCommand:
                    return QueryGeneration(argumentsJson);
                case QueryCancelGenerationCommand:
                    return QueryCancelGeneration(argumentsJson);
                default:
                    return Error($"Unknown Kimodo command '{toolName ?? string.Empty}'.");
            }
        }

        public static string ListCharacters(string argumentsJson = "{}")
        {
            return Execute(argumentsJson, arguments =>
            {
                bool includeProjectAssets = arguments.Value<bool?>("include_project_assets") ?? false;
                int maxResults = Mathf.Clamp(arguments.Value<int?>("max_results") ?? 100, 1, 1000);
                var characters = new JArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                Animator[] sceneAnimators = Resources.FindObjectsOfTypeAll<Animator>();
                for (int i = 0; i < sceneAnimators.Length && characters.Count < maxResults; i++)
                {
                    Animator animator = sceneAnimators[i];
                    if (animator == null || EditorUtility.IsPersistent(animator) ||
                        animator.gameObject == null || !animator.gameObject.scene.IsValid())
                    {
                        continue;
                    }

                    if (KimodoRetargetCoreUtility.IsValidHumanoid(animator.avatar))
                    {
                        AddCharacter(characters, seen, animator.gameObject, animator, "scene", animator.avatar);
                    }
                }

                if (includeProjectAssets)
                {
                    string[] guids = AssetDatabase.FindAssets("t:GameObject", new[] { "Assets" });
                    for (int i = 0; i < guids.Length && characters.Count < maxResults; i++)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                        GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        Animator animator = root != null ? root.GetComponentInChildren<Animator>(true) : null;
                        if (animator == null)
                        {
                            continue;
                        }

                        if (KimodoRetargetCoreUtility.IsValidHumanoid(animator.avatar))
                        {
                            AddCharacter(characters, seen, root, animator, "project", animator.avatar);
                        }
                    }
                }

                return Ok(new JObject
                {
                    ["characters"] = characters,
                    ["count"] = characters.Count
                });
            });
        }

        public static string ListModels(string argumentsJson = "{}")
        {
            return Execute(argumentsJson, _ =>
            {
                EnsureCanManageServer();
                KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
                JObject response = KimodoBridgeService.Shared.ListModelConfigurationsAsync(
                    ResolveModelName(null),
                    KimodoTextEncoderModeProtocol.ToProtocolValue(settings.DefaultTextEncoderMode),
                    settings.LocalModelsPath?.Trim() ?? string.Empty,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                var result = new JObject(response);
                result.Remove("status");
                result["count"] = (result["configs"] as JArray)?.Count ?? 0;
                return Ok(result);
            });
        }

        public static string GetServerHelp(string argumentsJson = "{}")
        {
            return Execute(argumentsJson, _ =>
            {
                EnsureCanManageServer();
                JObject response = KimodoBridgeService.Shared.GetServerHelpAsync(
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                var result = new JObject(response);
                result.Remove("status");
                return Ok(result);
            });
        }

        public static string DebugInstallServer(string argumentsJson = "{}")
        {
            return Execute(argumentsJson, _ =>
            {
                EnsureCanManageServer();
                string runtimeRoot = KimodoBridgeServerTool.GetRuntimeRootPath();
                using (KimodoBridgeServerTool.EnterRuntimeMaintenanceScope())
                {
                    KimodoBridgeService.Shared.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (!KimodoBridgeServerTool.RefreshRuntimeRoot())
                    {
                        throw new InvalidOperationException("Failed to incrementally install runtime root from package template.");
                    }
                }

                KimodoBridgeService.Shared.WarmupAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                return Ok(new JObject
                {
                    ["runtime_root"] = runtimeRoot,
                    ["runtime_version"] = KimodoServerRuntimeUtil.ReadQuickServerVersion(runtimeRoot),
                    ["install_mode"] = "incremental",
                    ["server_connected"] = KimodoBridgeService.Shared.IsConnected
                });
            });
        }

        public static string QueryGeneration(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                Guid requestId = RequiredRequestId(arguments);
                JobRecord record = GetJob(requestId);
                JObject status = BuildStatus(record);
                status["target_alive"] = record.Target != null;
                return Ok(status);
            });
        }

        public static string QueryCancelGeneration(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                Guid requestId = RequiredRequestId(arguments);
                JobRecord record = GetJob(requestId);
                string reason = arguments.Value<string>("reason")?.Trim();
                bool canceled = command_generation_runner.Cancel(
                    requestId,
                    string.IsNullOrWhiteSpace(reason) ? "Generation canceled by command." : reason);
                JObject status = BuildStatus(record);
                status["canceled"] = canceled;
                return Ok(status);
            });
        }

        private static async Task<command_generate_result> ExecuteAssetGenerationAsync(
            KimodoEditorGenerateRequest request,
            UnityEngine.Object target,
            command_generation_session session,
            CancellationToken token)
        {
            request.Token = token;
            request.Progress = (stage, message) => command_generation_runner.UpdateProgress(target, session.RequestId, stage, message);
            try
            {
                return await KimodoEditorGeneratePipeline.ExecuteAsync(request);
            }
            catch
            {
                KimodoPlayableClipGenerationHostService.CleanupFailedGeneration(request);
                throw;
            }
        }

        public static string GenerateAnimationAsset(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                EnsureCanGenerate();
                RequireCurrentTimelineSession();
                string prompt = RequiredStringValue(arguments, "prompt");
                ResolvedCharacter character = ResolveCharacter(RequiredStringValue(arguments, "character_ref"));
                string outputMode = ParseOutputMode(arguments.Value<string>("output_mode"));
                string requestedModel = arguments.Value<string>("model")?.Trim();
                string requestedTextEncoder = arguments.Value<string>("text_encoder_model")?.Trim();
                string modelName = ResolveModelName(requestedModel);
                KimodoTextEncoderMode textEncoderMode = ResolveTextEncoderMode(requestedTextEncoder);
                if (!string.IsNullOrWhiteSpace(requestedModel) || !string.IsNullOrWhiteSpace(requestedTextEncoder))
                {
                    EnsureRegisteredModel(modelName, textEncoderMode);
                }
                float frameRate = ResolveFrameRate(modelName);
                float duration = PositiveFloat(arguments, "duration_seconds", 5f);
                TimelineReservation timelineReservation = PrepareTimelineReservation(arguments, character, duration);
                string analysisOptionsJson = ParseAnalysisOptionsJson(arguments);
                int frameCount = Math.Max(1, KimodoFrameTimeUtility.SecondsToFrameCount(duration, frameRate));
                int seed = arguments.Value<int?>("seed") ?? (Guid.NewGuid().GetHashCode() & int.MaxValue);
                int steps = ResolveDiffusionSteps(arguments, modelName);
                bool loop = arguments.Value<bool?>("loop") ?? false;
                string outputFolder = NormalizeOutputFolder(arguments.Value<string>("output_folder"));
                string assetName = string.IsNullOrWhiteSpace(arguments.Value<string>("asset_name"))
                    ? $"{character.Name}_{DateTime.Now:yyyyMMdd_HHmmss_fff}"
                    : arguments.Value<string>("asset_name").Trim();
                KimodoEditorClipWritebackService.EnsureFolderExists(outputFolder);
                Avatar originAvatar = KimodoPlayableClipGenerationHostService.ResolveOriginRetargetAvatar(modelName);
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(originAvatar))
                {
                    throw new InvalidOperationException($"Model '{modelName}' does not provide a valid humanoid origin Avatar.");
                }
                List<KimodoMarkerSampleResult> poseConstraints = BuildPoseConstraints(
                    arguments,
                    modelName,
                    originAvatar,
                    frameCount,
                    frameRate,
                    duration);

                var request = new KimodoEditorGenerateRequest
                {
                    Prompt = prompt,
                    ModelName = modelName,
                    TextEncoderMode = textEncoderMode,
                    TargetFrameCount = frameCount,
                    TargetFrameRate = frameRate,
                    DiffusionSteps = steps,
                    EffectiveSeed = seed,
                    Loop = loop,
                    ConstraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(
                        poseConstraints,
                        0.0,
                        duration,
                        frameRate),
                    AnalysisOptionsJson = analysisOptionsJson,
                    ModelsRoot = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim() ?? string.Empty,
                    GenerationTimeoutSeconds = KimodoPlayableClipGenerationSettings.instance.GenerationTimeoutSeconds,
                    CreateTargetClip = () => KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(assetName, outputFolder),
                    OutputPlan = BuildOutputPlan(outputMode, originAvatar, character.Avatar),
                    ResolveOutputPlan = (generatedClip, _) => BuildOutputPlan(outputMode, originAvatar, character.Avatar),
                    Token = CancellationToken.None,
                    ConstraintSamples = poseConstraints
                };

                if (outputMode != "model_bone" && !KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
                {
                    throw new InvalidOperationException($"Character '{character.Name}' does not provide a valid target humanoid Avatar for output_mode '{outputMode}'.");
                }

                bool started = command_generation_runner.Start(
                    character.Target,
                    $"command-asset:{KimodoUnityObjectIdUtility.NameKey(character.Target)}",
                    command_kind.GenerateAnimationAsset,
                    async (session, token) => await ExecuteAssetGenerationAsync(
                        request,
                        character.Target,
                        session,
                        token,
                        timelineReservation),
                    out command_generation_session generation,
                    out string error);
                if (!started)
                {
                    throw new InvalidOperationException(error);
                }

                CommitTimelineReservation(timelineReservation);
                Remember(character.Target, generation, timelineReservation);
                var startedResponse = new JObject
                {
                    ["character"] = character.Name,
                    ["output_mode"] = outputMode,
                    ["model"] = modelName,
                    ["text_encoder_model"] = KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode),
                    ["seed"] = seed
                };
                if (timelineReservation != null)
                {
                    startedResponse["session_name"] = timelineReservation.Session.Name;
                    startedResponse["timeline_start_seconds"] = timelineReservation.StartSeconds;
                    startedResponse["timeline_duration_seconds"] = timelineReservation.DurationSeconds;
                }
                return Started(generation, startedResponse);
            });
        }

        private static KimodoEditorGenerateOutputPlan BuildOutputPlan(string outputMode, Avatar originAvatar, Avatar targetAvatar)
        {
            switch (outputMode)
            {
                case "model_bone":
                    return new KimodoEditorGenerateOutputPlan { SkipRetarget = true };
                case "character_bone":
                    return new KimodoEditorGenerateOutputPlan
                    {
                        OriginRetargetAvatar = originAvatar,
                        TargetRetargetAvatar = targetAvatar,
                        ExportMuscleClip = false,
                        SkipRetarget = false
                    };
                default:
                    return new KimodoEditorGenerateOutputPlan
                    {
                        OriginRetargetAvatar = originAvatar,
                        TargetRetargetAvatar = targetAvatar,
                        ExportMuscleClip = true,
                        SkipRetarget = false
                    };
            }
        }

        internal static string NormalizeOutputFolder(string value)
        {
            string folder = string.IsNullOrWhiteSpace(value)
                ? KimodoEditorClipWritebackService.GeneratedClipFolder
                : value.Trim().Replace('\\', '/').TrimEnd('/');
            if (!folder.Equals("Assets", StringComparison.OrdinalIgnoreCase) &&
                !folder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("output_folder must be under Assets.");
            }
            if (folder.Split('/').Any(part => part == ".." || part == "." || string.IsNullOrWhiteSpace(part)))
            {
                throw new InvalidOperationException("output_folder contains an invalid path segment.");
            }

            return folder;
        }

        internal static string ParseOutputMode(string value)
        {
            string mode = string.IsNullOrWhiteSpace(value) ? "humanoid_muscle" : value.Trim().ToLowerInvariant();
            if (mode != "humanoid_muscle" && mode != "character_bone" && mode != "model_bone")
            {
                throw new InvalidOperationException("output_mode must be humanoid_muscle, character_bone, or model_bone.");
            }

            return mode;
        }

        private static List<KimodoMarkerSampleResult> BuildPoseConstraints(
            JObject arguments,
            string modelName,
            Avatar targetAvatar,
            int frameCount,
            float frameRate,
            double durationSeconds)
        {
            JToken poseRefsToken = arguments?["pose_refs"];
            JToken timesToken = arguments?["times"];
            JToken typesToken = arguments?["constraint_types"];
            if (poseRefsToken == null)
            {
                if (timesToken != null || typesToken != null)
                {
                    throw new InvalidOperationException("pose_refs is required when times or constraint_types is supplied.");
                }
                return new List<KimodoMarkerSampleResult>();
            }
            if (poseRefsToken is not JArray poseRefs)
            {
                throw new InvalidOperationException("pose_refs must be an array.");
            }

            List<double> times = ParsePoseTimes(timesToken, poseRefs.Count);
            times = ResolvePoseConstraintTimes(poseRefs.Count, frameCount, frameRate, times);
            for (int i = 0; i < times.Count; i++)
            {
                if (times[i] < 0.0 || times[i] > durationSeconds)
                {
                    throw new InvalidOperationException($"times[{i}] must be between 0 and duration_seconds ({durationSeconds:0.###}).");
                }
            }
            List<string> constraintTypes = ParsePoseConstraintTypes(typesToken, poseRefs.Count);
            var samples = new List<KimodoMarkerSampleResult>(poseRefs.Count);
            SkeletonCache targetCache = null;
            try
            {
                if (poseRefs.Count > 0 &&
                    !KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        targetAvatar,
                        "KimodoCommandPoseConstraints",
                        out targetCache,
                        out string cacheError))
                {
                    throw new InvalidOperationException($"Build pose constraint target failed: {cacheError}");
                }

                for (int i = 0; i < poseRefs.Count; i++)
                {
                    string poseReference = poseRefs[i]?.Type == JTokenType.String
                        ? poseRefs[i].Value<string>()?.Trim()
                        : null;
                    if (string.IsNullOrWhiteSpace(poseReference))
                    {
                        throw new InvalidOperationException($"pose_refs[{i}] must be a non-empty GlobalObjectId string.");
                    }

                    ResolvedCharacter pose;
                    try
                    {
                        pose = ResolveCharacter(poseReference);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Resolve pose_refs[{i}] failed: {ex.Message}");
                    }
                    if (EditorUtility.IsPersistent(pose.Root) || !pose.Root.scene.IsValid())
                    {
                        throw new InvalidOperationException($"pose_refs[{i}] must resolve to a scene GameObject or Animator.");
                    }
                    if (!TrySamplePoseConstraint(
                            pose,
                            targetCache,
                            modelName,
                            constraintTypes[i],
                            times[i],
                            out KimodoMarkerSampleResult sample,
                            out string error))
                    {
                        throw new InvalidOperationException($"Sample pose_refs[{i}] failed: {error}");
                    }
                    samples.Add(sample);
                }
            }
            finally
            {
                targetCache?.Dispose();
            }
            return samples;
        }

        internal static List<double> ResolvePoseConstraintTimes(
            int poseCount,
            int frameCount,
            float frameRate,
            IReadOnlyList<double> suppliedTimes)
        {
            if (poseCount < 0)
            {
                throw new InvalidOperationException("pose count cannot be negative.");
            }
            if (suppliedTimes != null)
            {
                if (suppliedTimes.Count != poseCount)
                {
                    throw new InvalidOperationException("times count must match pose_refs count.");
                }
                return new List<double>(suppliedTimes);
            }

            var times = new List<double>(poseCount);
            if (poseCount == 0)
            {
                return times;
            }
            double endTime = KimodoInOutConstraintTools.ResolveConstraintEndSampleTimeSeconds(frameCount, frameRate);
            for (int i = 0; i < poseCount; i++)
            {
                times.Add(poseCount == 1 ? 0.0 : endTime * i / (poseCount - 1));
            }
            return times;
        }

        internal static List<string> ResolvePoseConstraintTypes(
            int poseCount,
            IReadOnlyList<string> suppliedTypes)
        {
            if (suppliedTypes != null && suppliedTypes.Count != poseCount)
            {
                throw new InvalidOperationException("constraint_types count must match pose_refs count.");
            }

            var types = new List<string>(poseCount);
            for (int i = 0; i < poseCount; i++)
            {
                string type = suppliedTypes == null ? "fullbody" : suppliedTypes[i]?.Trim().ToLowerInvariant();
                if (type != "fullbody" && type != "root2d")
                {
                    throw new InvalidOperationException($"constraint_types[{i}] must be fullbody or root2d.");
                }
                types.Add(type);
            }
            return types;
        }

        private static List<double> ParsePoseTimes(JToken token, int poseCount)
        {
            if (token == null)
            {
                return null;
            }
            if (token is not JArray values || values.Count != poseCount)
            {
                throw new InvalidOperationException("times count must match pose_refs count.");
            }

            var times = new List<double>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                if ((values[i].Type != JTokenType.Float && values[i].Type != JTokenType.Integer) ||
                    double.IsNaN(values[i].Value<double>()) ||
                    double.IsInfinity(values[i].Value<double>()))
                {
                    throw new InvalidOperationException($"times[{i}] must be a finite number.");
                }
                times.Add(values[i].Value<double>());
            }
            return times;
        }

        private static List<string> ParsePoseConstraintTypes(JToken token, int poseCount)
        {
            if (token == null)
            {
                return ResolvePoseConstraintTypes(poseCount, null);
            }
            if (token is not JArray values || values.Count != poseCount)
            {
                throw new InvalidOperationException("constraint_types count must match pose_refs count.");
            }
            return ResolvePoseConstraintTypes(
                poseCount,
                values.Select((value, index) => value.Type == JTokenType.String
                    ? value.Value<string>()
                    : throw new InvalidOperationException($"constraint_types[{index}] must be a string.")).ToArray());
        }

        private static bool TrySamplePoseConstraint(
            ResolvedCharacter pose,
            SkeletonCache targetCache,
            string modelName,
            string constraintType,
            double sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (pose.Animator == null || !KimodoRetargetAvatarUtility.ValidateRetargetCache(targetCache, out error))
            {
                return false;
            }

            try
            {
                var humanPose = new HumanPose();
                using (var poseHandler = new HumanPoseHandler(pose.Avatar, pose.Animator.transform))
                {
                    poseHandler.GetHumanPose(ref humanPose);
                }
                KimodoRetargetClipWriter.EnsureHumanPoseMuscles(ref humanPose);
                KimodoRetargetClipSamplingUtility.ResetSkeletonCachePose(targetCache);
                targetCache.poseHandler.SetHumanPose(ref humanPose);
                BoneSample targetSample = KimodoRetargetSamplingUtility.CaptureBoneSample(targetCache);
                if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                        targetSample,
                        targetCache,
                        modelName,
                        constraintType,
                        sampleTime,
                        out sample,
                        out error))
                {
                    return false;
                }

                sample.unityRootPos = pose.Animator.transform.position;
                sample.unityRootRot = pose.Animator.transform.rotation;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static ResolvedCharacter ResolveCharacter(string reference)
        {
            RequireCurrentTimelineSession();
            UnityEngine.Object resolved = ResolveObject(reference);
            GameObject root = resolved as GameObject;
            if (resolved is Animator directAnimator)
            {
                root = directAnimator.gameObject;
            }
            if (root == null)
            {
                throw new InvalidOperationException($"character_ref '{reference}' does not resolve to a GameObject or Animator.");
            }
            if (EditorUtility.IsPersistent(root) || !root.scene.IsValid())
            {
                throw new InvalidOperationException("character_ref must resolve to a scene character in the current Session, not a Project asset.");
            }

            Animator animator = root.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                throw new InvalidOperationException($"Character '{root.name}' does not contain an Animator.");
            }

            KimodoLocalAvatarUtility.AvatarResolveResult avatarResult = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
            if (!avatarResult.IsHumanoid || !KimodoRetargetCoreUtility.IsValidHumanoid(avatarResult.Avatar))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(avatarResult.Error)
                    ? $"Character '{root.name}' does not provide a valid humanoid Avatar."
                    : avatarResult.Error);
            }

            TimelineCharacterRecord sessionCharacter = currentTimelineSession.Characters.FirstOrDefault(item => item.Animator == animator);
            if (sessionCharacter == null)
            {
                if (!AddCharacterTrack(currentTimelineSession, root, animator, true, out string addError))
                {
                    throw new InvalidOperationException($"Character could not be appended to the current Session: {addError}");
                }
                sessionCharacter = currentTimelineSession.Characters.FirstOrDefault(item => item.Animator == animator);
            }
            else if (KimodoRetargetCoreUtility.IsValidHumanoid(avatarResult.Avatar) &&
                !KimodoRetargetCoreUtility.IsValidHumanoid(sessionCharacter.Avatar))
            {
                sessionCharacter.Avatar = avatarResult.Avatar;
                sessionCharacter.AvatarError = string.Empty;
                currentTimelineSession.Director.SetGenericBinding(sessionCharacter.Track, animator);
            }
            if (sessionCharacter == null || !KimodoRetargetCoreUtility.IsValidHumanoid(sessionCharacter.Avatar))
            {
                throw new InvalidOperationException($"Character '{root.name}' requires a valid humanoid Avatar in the current Session.");
            }

            return new ResolvedCharacter(root, animator, sessionCharacter.Avatar);
        }

        private static PlayableDirector ResolveDirector(string reference)
        {
            UnityEngine.Object resolved = ResolveObject(reference);
            PlayableDirector director = resolved as PlayableDirector;
            if (director == null && resolved is GameObject go)
            {
                director = go.GetComponent<PlayableDirector>();
            }
            if (director == null || EditorUtility.IsPersistent(director))
            {
                throw new InvalidOperationException($"director_ref '{reference}' does not resolve to a scene PlayableDirector.");
            }

            return director;
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

        private static AnimationClip ResolveAnimationClip(string reference)
        {
            UnityEngine.Object resolved = ResolveObject(reference);
            AnimationClip clip = resolved as AnimationClip;
            if (clip == null && reference.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(reference);
            }
            if (clip == null)
            {
                throw new InvalidOperationException($"clip_ref '{reference}' does not resolve to an AnimationClip asset.");
            }
            return clip;
        }

        private static void AddCharacter(
            JArray output,
            HashSet<string> seen,
            GameObject root,
            Animator animator,
            string source,
            Avatar resolvedAvatar)
        {
            string reference = source == "project" ? AssetDatabase.GetAssetPath(root) : GetObjectReference(root);
            if (string.IsNullOrWhiteSpace(reference) || !seen.Add(reference))
            {
                return;
            }

            output.Add(new JObject
            {
                ["character_ref"] = reference,
                ["name"] = root.name,
                ["source"] = source,
                ["avatar"] = resolvedAvatar != null ? resolvedAvatar.name : string.Empty,
                ["asset_path"] = AssetDatabase.GetAssetPath(root) ?? string.Empty,
                ["scene_path"] = root.scene.IsValid() ? root.scene.path : string.Empty,
                ["active"] = root.activeInHierarchy
            });
        }

        private static string GetObjectReference(UnityEngine.Object target)
        {
            return target == null ? string.Empty : GlobalObjectId.GetGlobalObjectIdSlow(target).ToString();
        }

        private static void Remember(
            UnityEngine.Object target,
            command_generation_session session,
            TimelineReservation timelineReservation = null)
        {
            lock (JobsLock)
            {
                if (Jobs.Count >= MaxRememberedJobs)
                {
                    Guid oldest = Jobs.OrderBy(pair => pair.Value.Session.StartedAtUtc).First().Key;
                    Jobs.Remove(oldest);
                }
                Jobs[session.RequestId] = new JobRecord(target, session, timelineReservation);
            }
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
            command_generation_session session = record.Session;
            var result = new JObject
            {
                ["request_id"] = session.RequestId.ToString("D"),
                ["status"] = session.Status.ToString().ToLowerInvariant(),
                ["stage"] = session.Stage.ToString(),
                ["message"] = session.Message ?? string.Empty,
                ["error"] = session.Error ?? string.Empty,
                ["started_at_utc"] = session.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)
            };
            if (session.Payload is command_generate_result generated)
            {
                result["asset_path"] = generated.GeneratedClip != null
                    ? AssetDatabase.GetAssetPath(generated.GeneratedClip)
                    : string.Empty;
                result["raw_bone_asset_path"] = generated.RawBoneClip != null
                    ? AssetDatabase.GetAssetPath(generated.RawBoneClip)
                    : string.Empty;
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
            }
            if (record.TimelineReservation != null)
            {
                TimelineReservation reservation = record.TimelineReservation;
                result["session_name"] = reservation.Session.Name;
                result["timeline_start_seconds"] = reservation.StartSeconds;
                result["timeline_duration_seconds"] = reservation.DurationSeconds;
                if (reservation.TimelineClip != null)
                {
                    result["timeline_clip_asset_ref"] = GetObjectReference(reservation.TimelineClip.asset);
                }
                if (reservation.Animation != null)
                {
                    result["animation_id"] = reservation.Animation.Id.ToString("D");
                }
                if (reservation.AnalysisTrack != null)
                {
                    result["analysis_track_ref"] = GetObjectReference(reservation.AnalysisTrack);
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
                return Error(ex.Message);
            }
        }

        private static string Started(command_generation_session session, JObject extra)
        {
            extra["request_id"] = session.RequestId.ToString("D");
            extra["status"] = "running";
            return Ok(extra);
        }

        private static string Ok(JObject result)
        {
            result["ok"] = true;
            return result.ToString(Formatting.None);
        }

        private static string Error(string message)
        {
            return new JObject
            {
                ["ok"] = false,
                ["error"] = message ?? string.Empty
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
                    throw new InvalidOperationException("model must be a registered model name/configuration id from Kimodo_list_models, not a filesystem path.");
                }
            }
            return KimodoPlayableClip.NormalizeBridgeModelName(string.IsNullOrWhiteSpace(modelName)
                ? KimodoPlayableClipGenerationSettings.instance.DefaultBridgeModelName
                : modelName);
        }

        private static void EnsureRegisteredModel(string modelName, KimodoTextEncoderMode textEncoderMode)
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            JObject response = KimodoBridgeService.Shared.ListModelConfigurationsAsync(
                modelName,
                KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode),
                settings.LocalModelsPath?.Trim() ?? string.Empty,
                null,
                CancellationToken.None).GetAwaiter().GetResult();
            bool found = (response["configs"] as JArray)?.Values<JObject>().Any(config =>
                string.Equals(config.Value<string>("model"), modelName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    config.Value<string>("text_encoder_model"),
                    KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode),
                    StringComparison.OrdinalIgnoreCase) &&
                config.Value<bool?>("available") != false) == true;
            if (!found)
            {
                throw new InvalidOperationException(
                    $"Model '{modelName}' with text_encoder_model '{KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode)}' is not listed by Kimodo_list_models.");
            }
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

        private static float ResolveFrameRate(string modelName)
        {
            return KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile)
                ? profile.SourceFps
                : KimodoPlayableClip.FIXED_FRAME_RATE;
        }

        private static int ResolveDiffusionSteps(JObject arguments, string modelName)
        {
            int? supplied = arguments.Value<int?>("diffusion_steps");
            if (KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile))
            {
                return supplied.HasValue ? Mathf.Clamp(supplied.Value, 0, profile.MaxDiffusionSteps) : 0;
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

        private static JObject CommandDefinition(string name, string description, JObject inputSchema, bool debugOnly = false)
        {
            var definition = new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = inputSchema
            };
            if (debugOnly)
            {
                definition["debug_only"] = true;
            }
            return definition;
        }

        private static JObject Properties(params PropertyDefinition[] definitions)
        {
            var properties = new JObject();
            var required = new JArray();
            foreach (PropertyDefinition definition in definitions)
            {
                properties[definition.Name] = definition.Schema;
                if (definition.IsRequired)
                {
                    required.Add(definition.Name);
                }
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false
            };
        }

        private static PropertyDefinition Required(string name, string type, string description)
        {
            return new PropertyDefinition(name, type, description, true);
        }

        private static PropertyDefinition Optional(string name, string type, string description)
        {
            return new PropertyDefinition(name, type, description, false);
        }

        private static PropertyDefinition OptionalArray(string name, string itemType, string description)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = itemType },
                ["description"] = description
            }, false);
        }

        private static PropertyDefinition OptionalEnumArray(string name, string description, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray(values)
                },
                ["description"] = description
            }, false);
        }

        private static PropertyDefinition Enum(string name, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(values),
                ["default"] = values[0]
            }, false);
        }

        private sealed class JobRecord
        {
            public JobRecord(
                UnityEngine.Object target,
                command_generation_session session,
                TimelineReservation timelineReservation)
            {
                Target = target;
                Session = session;
                TimelineReservation = timelineReservation;
            }

            public UnityEngine.Object Target { get; }
            public command_generation_session Session { get; }
            public TimelineReservation TimelineReservation { get; }
        }

        private readonly struct ResolvedCharacter
        {
            public ResolvedCharacter(GameObject root, Animator animator, Avatar avatar)
            {
                Root = root;
                Animator = animator;
                Avatar = avatar;
            }

            public GameObject Root { get; }
            public Animator Animator { get; }
            public Avatar Avatar { get; }
            public UnityEngine.Object Target => Root;
            public string Name => Root != null ? Root.name : Avatar.name;
        }

        private readonly struct PropertyDefinition
        {
            public PropertyDefinition(string name, string type, string description, bool required)
                : this(name, new JObject { ["type"] = type, ["description"] = description }, required)
            {
            }

            public PropertyDefinition(string name, JObject schema, bool required)
            {
                Name = name;
                Schema = schema;
                IsRequired = required;
            }

            public string Name { get; }
            public JObject Schema { get; }
            public bool IsRequired { get; }
        }
    }
}
