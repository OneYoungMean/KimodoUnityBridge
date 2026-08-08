using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    /// <summary>
    /// Framework-neutral entry points for MCP adapters. A concrete MCP package only needs to
    /// forward the tool name and JSON arguments to <see cref="Invoke"/>.
    /// </summary>
    public static partial class KimodoMcpTools
    {
        public const string ListCharactersTool = "kimodo_list_characters";
        public const string ListModelsTool = "kimodo_list_models";
        public const string ListTextEncoderModelsTool = "kimodo_list_text_encoder_models";
        public const string ReinstallServerTool = "kimodo_reinstall_server";
        public const string GenerateAnimationAssetTool = "kimodo_generate_animation_asset";
        public const string GenerateTimelineAnimationTool = "kimodo_generate_timeline_animation";
        public const string OpenTimelineSessionTool = "kimodo_open_timeline_session";
        public const string CloseTimelineSessionTool = "kimodo_close_timeline_session";
        public const string GetGenerationTool = "kimodo_get_generation";
        public const string CancelGenerationTool = "kimodo_cancel_generation";

        private const int MaxRememberedJobs = 128;
        private static readonly Dictionary<Guid, JobRecord> Jobs = new Dictionary<Guid, JobRecord>();
        private static readonly object JobsLock = new object();

        public static string GetToolDefinitionsJson()
        {
            return new JObject
            {
                ["tools"] = new JArray
                {
                    Tool(ListCharactersTool,
                        "List humanoid characters that can be used for Kimodo animation generation.",
                        Properties(
                            Optional("include_project_assets", "boolean", "Also scan prefab/model assets under Assets."),
                            Optional("max_results", "integer", "Maximum returned characters; defaults to 100."))),
                    Tool(ListModelsTool,
                        "List model names supported by the installed Kimodo QuickServer and identify the project default.",
                        Properties()),
                    Tool(ListTextEncoderModelsTool,
                        "List text encoder profiles accepted by Kimodo generation. High performance chooses the local INT8/NF4 route automatically.",
                        Properties()),
                    Tool(ReinstallServerTool,
                        "Stop, reinstall the packaged Kimodo QuickServer runtime, then activate the project default model with high performance text encoding.",
                        Properties()),
                    Tool(OpenTimelineSessionTool,
                        "Create and load a temporary TimelineAsset for a Session; completed AnimationClip generations are inserted in order.",
                        Properties(
                            Required("director_ref", "string", "Scene PlayableDirector or GameObject GlobalObjectId."),
                            Required("character_ref", "string", "Scene humanoid character GlobalObjectId."),
                            Optional("start_seconds", "number", "First insertion time; defaults to 0."))),
                    Tool(CloseTimelineSessionTool,
                        "Close a Timeline Session, clear its Timeline Window selection, and delete its temporary TimelineAsset. Sessions with running generations cannot close.",
                        Properties(Required("timeline_session_id", "string", "Timeline Session id returned by kimodo_open_timeline_session."))),
                    Tool(GenerateAnimationAssetTool,
                        "Generate an AnimationClip asset for a humanoid character from a text prompt.",
                        Properties(
                            Required("character_ref", "string", "Scene GlobalObjectId or Assets/... GameObject asset path."),
                            Required("prompt", "string", "Motion prompt."),
                            Optional("duration_seconds", "number", "Duration in seconds; defaults to 5."),
                            Optional("model", "string", "Kimodo model name; defaults to Project Settings."),
                            Enum("text_encoder_model", "high_performance", "high_precision"),
                            Optional("seed", "integer", "Deterministic seed; omitted chooses a random seed."),
                            Optional("diffusion_steps", "integer", "Diffusion steps; omitted uses the model default."),
                            Optional("text_weight", "number", "Prompt weight in [0,4]; defaults to 1."),
                            Enum("output_mode", "humanoid_muscle", "character_bone", "model_bone"),
                            Optional("output_folder", "string", "Unity folder under Assets; defaults to Assets/KimodoGeneratedClips."),
                            Optional("asset_name", "string", "Output asset name without extension."),
                            Optional("timeline_session_id", "string", "Optional Timeline Session id that receives the completed AnimationClip."),
                            Optional("analysis_options", "object", "Optional analysis object; set keyframes.enabled=true to return screenshot keyframes."),
                            OptionalArray("pose_refs", "string", "Scene humanoid GameObject or Animator GlobalObjectIds used as pose constraints."),
                            OptionalArray("times", "number", "Pose times in seconds; omitted distributes poses from the first through the last generated frame."),
                            OptionalEnumArray("constraint_types", "Constraint type per pose; omitted defaults every pose to fullbody.", "fullbody", "root2d"))),
                    Tool(GenerateTimelineAnimationTool,
                        "Create and generate a Kimodo clip on a PlayableDirector Timeline.",
                        Properties(
                            Required("director_ref", "string", "Scene PlayableDirector or GameObject GlobalObjectId."),
                            Required("character_ref", "string", "Scene GlobalObjectId or Assets/... GameObject asset path."),
                            Required("prompt", "string", "Motion prompt."),
                            Optional("track_ref", "string", "AnimationTrack GlobalObjectId; omitted reuses or creates a bound track."),
                            Optional("start_seconds", "number", "Timeline clip start; defaults to 0."),
                            Optional("duration_seconds", "number", "Timeline clip duration; defaults to 5."),
                            Optional("model", "string", "Kimodo model name; defaults to Project Settings."),
                            Enum("text_encoder_model", "high_performance", "high_precision"),
                            Optional("seed", "integer", "Deterministic seed; omitted chooses a random seed."),
                            Optional("diffusion_steps", "integer", "Diffusion steps; omitted uses the model default."),
                            Optional("text_weight", "number", "Prompt weight in [0,4]; defaults to 1."),
                            Optional("use_constraints", "boolean", "Use enabled Timeline constraints; defaults to true."),
                            Optional("analysis_options", "object", "Optional analysis object; set keyframes.enabled=true to return screenshot keyframes."),
                            OptionalArray("pose_refs", "string", "Scene humanoid GameObject or Animator GlobalObjectIds used as pose constraints."),
                            OptionalArray("times", "number", "Pose times in seconds; omitted distributes poses from the first through the last generated frame."),
                            OptionalEnumArray("constraint_types", "Constraint type per pose; omitted defaults every pose to fullbody.", "fullbody", "root2d"))),
                    Tool(GetGenerationTool,
                        "Get generation progress and the generated AnimationClip asset path.",
                        Properties(Required("request_id", "string", "Request id returned by a generate tool."))),
                    Tool(CancelGenerationTool,
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
                case ListCharactersTool:
                    return ListCharacters(argumentsJson);
                case ListModelsTool:
                    return ListModels(argumentsJson);
                case ListTextEncoderModelsTool:
                    return ListTextEncoderModels(argumentsJson);
                case ReinstallServerTool:
                    return ReinstallServer(argumentsJson);
                case OpenTimelineSessionTool:
                    return OpenTimelineSession(argumentsJson);
                case CloseTimelineSessionTool:
                    return CloseTimelineSession(argumentsJson);
                case GenerateAnimationAssetTool:
                    return GenerateAnimationAsset(argumentsJson);
                case GenerateTimelineAnimationTool:
                    return GenerateTimelineAnimation(argumentsJson);
                case GetGenerationTool:
                    return GetGeneration(argumentsJson);
                case CancelGenerationTool:
                    return CancelGeneration(argumentsJson);
                default:
                    return Error($"Unknown Kimodo MCP tool '{toolName ?? string.Empty}'.");
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
                string defaultModel = ResolveModelName(null);
                var models = new JArray();
                foreach (string modelName in KimodoBridgeServerTool.SupportedModelNames)
                {
                    bool isArdy = KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile ardyProfile);
                    models.Add(new JObject
                    {
                        ["model"] = modelName,
                        ["backend"] = isArdy ? "ardy" : "kimodo",
                        ["default"] = string.Equals(modelName, defaultModel, StringComparison.OrdinalIgnoreCase)
                    });
                }
                return Ok(new JObject
                {
                    ["models"] = models,
                    ["default_model"] = defaultModel,
                    ["count"] = models.Count
                });
            });
        }

        public static string ListTextEncoderModels(string argumentsJson = "{}")
        {
            return Execute(argumentsJson, _ =>
            {
                string defaultModel = KimodoTextEncoderModeProtocol.ToProtocolValue(
                    KimodoPlayableClipGenerationSettings.instance.DefaultTextEncoderMode);
                var models = new JArray
                {
                    new JObject
                    {
                        ["text_encoder_model"] = KimodoTextEncoderModeProtocol.HighPerformance,
                        ["label"] = "High Performance",
                        ["default"] = string.Equals(defaultModel, KimodoTextEncoderModeProtocol.HighPerformance, StringComparison.Ordinal),
                        ["description"] = "Automatically selects the local INT8/NF4 route based on device and free VRAM."
                    },
                    new JObject
                    {
                        ["text_encoder_model"] = KimodoTextEncoderModeProtocol.HighPrecision,
                        ["label"] = "High Precision",
                        ["default"] = string.Equals(defaultModel, KimodoTextEncoderModeProtocol.HighPrecision, StringComparison.Ordinal),
                        ["description"] = "Uses the local FP16 route when available; otherwise falls back to CPU."
                    }
                };
                return Ok(new JObject
                {
                    ["text_encoder_models"] = models,
                    ["default_text_encoder_model"] = defaultModel,
                    ["count"] = models.Count
                });
            });
        }

        public static string ReinstallServer(string argumentsJson = "{}")
        {
            return Execute(argumentsJson, _ =>
            {
                EnsureCanManageServer();
                KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
                settings.DefaultTextEncoderMode = KimodoTextEncoderMode.HighPerformance;
                settings.SaveSettings();
                string modelName = ResolveModelName(null);
                string modelsRoot = settings.LocalModelsPath?.Trim() ?? string.Empty;

                using (KimodoBridgeServerTool.EnterRuntimeMaintenanceScope())
                {
                    KimodoBridgeService.Shared.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                    if (!KimodoBridgeServerTool.ReinstallRuntimeRoot())
                    {
                        throw new InvalidOperationException("Failed to reinstall runtime root from package template.");
                    }
                }

                JObject activation = KimodoBridgeService.Shared.ActivateRuntimeAsync(
                    modelName,
                    KimodoTextEncoderModeProtocol.HighPerformance,
                    modelsRoot,
                    null,
                    CancellationToken.None).GetAwaiter().GetResult();
                return Ok(new JObject
                {
                    ["reinstalled"] = true,
                    ["runtime_root"] = KimodoBridgeServerTool.ResolveRuntimeRootOrThrow(),
                    ["model"] = activation.Value<string>("model") ?? modelName,
                    ["text_encoder_model"] = activation.Value<string>("text_encoder_mode") ?? KimodoTextEncoderModeProtocol.HighPerformance,
                    ["text_encoder_route"] = activation.Value<string>("text_encoder_route") ?? string.Empty,
                    ["text_encoder_device"] = activation.Value<string>("text_encoder_device") ?? string.Empty,
                    ["runtime_device"] = activation.Value<string>("runtime_device") ?? string.Empty
                });
            });
        }

        public static string GenerateAnimationAsset(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                EnsureCanGenerate();
                string prompt = RequiredStringValue(arguments, "prompt");
                ResolvedCharacter character = ResolveCharacter(RequiredStringValue(arguments, "character_ref"));
                string outputMode = ParseOutputMode(arguments.Value<string>("output_mode"));
                string modelName = ResolveModelName(arguments.Value<string>("model"));
                KimodoTextEncoderMode textEncoderMode = ResolveTextEncoderMode(arguments.Value<string>("text_encoder_model"));
                float frameRate = ResolveFrameRate(modelName);
                float duration = PositiveFloat(arguments, "duration_seconds", 5f);
                TimelineReservation timelineReservation = PrepareTimelineReservation(arguments, character, duration);
                string analysisOptionsJson = ParseAnalysisOptionsJson(arguments);
                int frameCount = Math.Max(1, KimodoFrameTimeUtility.SecondsToFrameCount(duration, frameRate));
                int seed = arguments.Value<int?>("seed") ?? (Guid.NewGuid().GetHashCode() & int.MaxValue);
                int steps = ResolveDiffusionSteps(arguments, modelName);
                float textWeight = Mathf.Clamp(arguments.Value<float?>("text_weight") ?? 1f, 0f, 4f);
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
                    TextWeight = textWeight,
                    EffectiveSeed = seed,
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

                bool started = EditorGenerateSessionRunner.Start(
                    character.Target,
                    $"mcp-asset:{KimodoUnityObjectIdUtility.NameKey(character.Target)}",
                    KimodoEditorCommandKind.GenerateAnimationAsset,
                    async (session, token) => await ExecuteAssetGenerationAsync(
                        request,
                        character.Target,
                        session,
                        token,
                        timelineReservation),
                    out EditorGenerateSession generation,
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
                    startedResponse["timeline_session_id"] = timelineReservation.Session.Id.ToString("D");
                    startedResponse["timeline_start_seconds"] = timelineReservation.StartSeconds;
                    startedResponse["timeline_duration_seconds"] = timelineReservation.DurationSeconds;
                }
                return Started(generation, startedResponse);
            });
        }

        public static string GenerateTimelineAnimation(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                EnsureCanGenerate();
                string prompt = RequiredStringValue(arguments, "prompt");
                ResolvedCharacter character = ResolveCharacter(RequiredStringValue(arguments, "character_ref"));
                if (EditorUtility.IsPersistent(character.Root) || !character.Root.scene.IsValid())
                {
                    throw new InvalidOperationException("Timeline generation requires character_ref to resolve to a scene character, not a project asset.");
                }
                PlayableDirector director = ResolveDirector(RequiredStringValue(arguments, "director_ref"));
                TimelineAsset timelineAsset = director.playableAsset as TimelineAsset;
                if (timelineAsset == null)
                {
                    throw new InvalidOperationException("PlayableDirector does not reference a TimelineAsset.");
                }

                AnimationTrack track = ResolveOrCreateTrack(
                    arguments.Value<string>("track_ref"),
                    director,
                    timelineAsset,
                    character.Animator);
                double start = NonNegativeDouble(arguments, "start_seconds", 0.0);
                double duration = PositiveDouble(arguments, "duration_seconds", 5.0);
                string modelName = ResolveModelName(arguments.Value<string>("model"));
                KimodoTextEncoderMode textEncoderMode = ResolveTextEncoderMode(arguments.Value<string>("text_encoder_model"));
                float frameRate = ResolveFrameRate(modelName);
                int frameCount = Math.Max(1, KimodoFrameTimeUtility.SecondsToFrameCount(duration, frameRate));
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
                int seed = arguments.Value<int?>("seed") ?? (Guid.NewGuid().GetHashCode() & int.MaxValue);
                bool useConstraints = arguments.Value<bool?>("use_constraints") ?? true;
                string analysisOptionsJson = ParseAnalysisOptionsJson(arguments);

                Undo.RegisterCompleteObjectUndo(new UnityEngine.Object[] { timelineAsset, track }, "Kimodo MCP Generate Timeline Animation");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.start = start;
                timelineClip.duration = duration;
                timelineClip.displayName = prompt;
                if (timelineClip.asset is not KimodoPlayableClip playableClip)
                {
                    throw new InvalidOperationException("Failed to create KimodoPlayableClip.");
                }

                playableClip.motionPrompt = prompt;
                playableClip.bridgeModelName = modelName;
                playableClip.textEncoderMode = textEncoderMode;
                playableClip.diffusionSteps = ResolveDiffusionSteps(arguments, modelName);
                playableClip.textWeight = Mathf.Clamp(arguments.Value<float?>("text_weight") ?? 1f, 0f, 4f);
                playableClip.randomSeed = false;
                playableClip.seed = seed;
                playableClip.autoBeginAnchor = true;
                playableClip.showConstraint = useConstraints;
                EditorUtility.SetDirty(playableClip);
                EditorUtility.SetDirty(track);
                EditorUtility.SetDirty(timelineAsset);
                AssetDatabase.SaveAssets();

                KimodoExternalConstraintRequest externalConstraint = useConstraints && poseConstraints.Count == 0 &&
                    string.IsNullOrWhiteSpace(analysisOptionsJson)
                    ? null
                    : new KimodoExternalConstraintRequest
                    {
                        Enabled = true,
                        ConstraintsJson = string.Empty,
                        AnalysisOptionsJson = analysisOptionsJson,
                        RetargetAvatar = character.Avatar,
                        IncludeTimelineConstraints = useConstraints,
                        ConstraintSamples = poseConstraints
                    };
                bool started = EditorGenerateSessionRunner.Start(
                    playableClip,
                    $"mcp-timeline:{KimodoUnityObjectIdUtility.NameKey(playableClip)}",
                    KimodoEditorCommandKind.GeneratePlayableClip,
                    async (session, token) => await KimodoPlayableClipGenerationExecutionService.GenerateAndFinalizeAsync(
                        playableClip,
                        externalConstraint,
                        (stage, message) => EditorGenerateSessionRunner.UpdateProgress(playableClip, session.RequestId, stage, message),
                        token),
                    out EditorGenerateSession generation,
                    out string error);
                if (!started)
                {
                    throw new InvalidOperationException(error);
                }

                Remember(playableClip, generation);
                return Started(generation, new JObject
                {
                    ["timeline_clip_ref"] = GetObjectReference(playableClip),
                    ["track_ref"] = GetObjectReference(track),
                    ["model"] = modelName,
                    ["text_encoder_model"] = KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode),
                    ["seed"] = seed
                });
            });
        }

        public static string GetGeneration(string argumentsJson)
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

        public static string CancelGeneration(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                Guid requestId = RequiredRequestId(arguments);
                JobRecord record = GetJob(requestId);
                string reason = arguments.Value<string>("reason")?.Trim();
                bool canceled = EditorGenerateSessionRunner.Cancel(
                    requestId,
                    string.IsNullOrWhiteSpace(reason) ? "Generation canceled by MCP." : reason);
                JObject status = BuildStatus(record);
                status["canceled"] = canceled;
                return Ok(status);
            });
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

        private static async Task<KimodoEditorGenerateResult> ExecuteAssetGenerationAsync(
            KimodoEditorGenerateRequest request,
            UnityEngine.Object target,
            EditorGenerateSession session,
            CancellationToken token)
        {
            request.Token = token;
            request.Progress = (stage, message) => EditorGenerateSessionRunner.UpdateProgress(target, session.RequestId, stage, message);
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
                        "KimodoMcpPoseConstraints",
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

            return new ResolvedCharacter(root, animator, avatarResult.Avatar);
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

        private static AnimationTrack ResolveOrCreateTrack(
            string trackReference,
            PlayableDirector director,
            TimelineAsset timelineAsset,
            Animator animator)
        {
            AnimationTrack track = null;
            if (!string.IsNullOrWhiteSpace(trackReference))
            {
                track = ResolveObject(trackReference) as AnimationTrack;
                if (track == null || track.timelineAsset != timelineAsset)
                {
                    throw new InvalidOperationException("track_ref does not resolve to an AnimationTrack on director_ref's Timeline.");
                }
            }
            else
            {
                foreach (TrackAsset outputTrack in timelineAsset.GetOutputTracks())
                {
                    if (outputTrack is AnimationTrack animationTrack && BindingMatches(director.GetGenericBinding(animationTrack), animator))
                    {
                        track = animationTrack;
                        break;
                    }
                }
            }

            if (track == null)
            {
                track = timelineAsset.CreateTrack<AnimationTrack>(null, $"Kimodo MCP - {animator.gameObject.name}");
                director.SetGenericBinding(track, animator);
                EditorUtility.SetDirty(timelineAsset);
                EditorUtility.SetDirty(director);
                return track;
            }

            UnityEngine.Object binding = director.GetGenericBinding(track);
            if (binding == null)
            {
                director.SetGenericBinding(track, animator);
            }
            else if (!BindingMatches(binding, animator))
            {
                throw new InvalidOperationException("track_ref is bound to a different character.");
            }

            return track;
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
            EditorGenerateSession session,
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
            EditorGenerateSession session = record.Session;
            var result = new JObject
            {
                ["request_id"] = session.RequestId.ToString("D"),
                ["status"] = session.Status.ToString().ToLowerInvariant(),
                ["stage"] = session.Stage.ToString(),
                ["message"] = session.Message ?? string.Empty,
                ["error"] = session.Error ?? string.Empty,
                ["started_at_utc"] = session.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)
            };
            if (session.Payload is KimodoEditorGenerateResult generated)
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
                result["timeline_session_id"] = reservation.Session.Id.ToString("D");
                result["timeline_start_seconds"] = reservation.StartSeconds;
                result["timeline_duration_seconds"] = reservation.DurationSeconds;
                if (reservation.TimelineClip != null)
                {
                    result["timeline_clip_asset_ref"] = GetObjectReference(reservation.TimelineClip.asset);
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

        private static string Started(EditorGenerateSession session, JObject extra)
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
            return KimodoPlayableClip.NormalizeBridgeModelName(string.IsNullOrWhiteSpace(modelName)
                ? KimodoPlayableClipGenerationSettings.instance.DefaultBridgeModelName
                : modelName);
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

        private static JObject Tool(string name, string description, JObject inputSchema)
        {
            return new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = inputSchema
            };
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
                EditorGenerateSession session,
                TimelineReservation timelineReservation)
            {
                Target = target;
                Session = session;
                TimelineReservation = timelineReservation;
            }

            public UnityEngine.Object Target { get; }
            public EditorGenerateSession Session { get; }
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
