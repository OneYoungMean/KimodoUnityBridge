using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CharacterAnimationCli.Unity;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;

namespace CharacterAnimationCli.Unity.Command
{
    /// <summary>
    /// Shared implementation behind the framework-neutral Kimodo command entry points.
    /// </summary>
    internal static partial class command_context
    {
        public const string HelpCommand = "kimodo_help";
        public const string InstallServerCommand = "kimodo_install_server";
        public const string GenerateAnimationCommand = "kimodo_generate_animation";
        public const string SessionGetOrCreateCommand = "session_get_or_create";
        public const string SessionCloseCommand = "session_close";
        public const string SessionAddCommand = "session_add";
        public const string AnimationAnalyzeCommand = "animation_analyze";
        public const string AnimationCompareCommand = "animation_compare";
        public const string RecordRangeCommand = "kimodo_record_range";
        public const string RetargetAnimationCommand = "kimodo_retarget_animation";
        public const string PoseGetCommand = "pose_get";
        public const string PoseContractCommand = "pose_contract";
        public const string PoseSetRootTransformCommand = "pose_set_root_transform";
        public const string PoseSetMuscleCommand = "pose_set_muscle";
        public const string GetGenerationCommand = "kimodo_get_generation";
        public const string CancelGenerationCommand = "kimodo_cancel_generation";

        private const int MaxRememberedJobs = 128;
        private static readonly Dictionary<Guid, JobRecord> Jobs = new Dictionary<Guid, JobRecord>();
        private static readonly object JobsLock = new object();

        public static string GetCommandDefinitionsJson()
        {
            return new JObject
            {
                ["tools"] = new JArray
                {
                    CommandDefinition(HelpCommand,
                        "Return the command manual, detailed parameter documentation for one command, or currently viable model configurations.",
                        Properties(
                            Optional("command", "string", "Command name whose full manual entry should be returned."),
                            Enum("section", "commands", "models", "constraints"))),
                    CommandDefinition(InstallServerCommand,
                        "[debug-only] Incrementally install the QuickServer runtime from the package template, preserving models and the Python environment, then restart it.",
                        Properties(),
                        debugOnly: true),
                    CommandDefinition(SessionGetOrCreateCommand,
                        "Create an empty current animation Session, or reopen an existing named Session. Add a scene humanoid before using character-scoped commands in a new Session.",
                        Properties(
                            Optional("name", "string", "Stable Session name. An existing name selects that Session; omit it to return the current Session or create one when none exists."))),
                    CommandDefinition(SessionCloseCommand,
                        "Close the selected animation editing Session while preserving its Timeline, assets, and AI-readable Session JSON.",
                        Properties(Optional("session_id", "string", "Session id; omitted uses the current Session."))),
                    CommandDefinition(SessionAddCommand,
                        "Add scene or project content to the current Session. kind=character adds one scene Humanoid Animator; kind=clip appends one project AnimationClip to a Session character; kind=animator imports same-Layer State-to-State transitions as Timeline-composed transition_clip records without baking transition assets. Returns safe names to reuse. Appended clips keep a fixed 4-frame safezone.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            RequiredEnum("kind", "character", "clip", "animator"),
                            Required("character", "string", "Scene character name/path for kind=character, or target Session character name otherwise."),
                            Optional("clip", "string", "Project AnimationClip name for kind=clip."),
                             Optional("animator", "string", "Scene Animator name/path for kind=animator."),
                             Optional("ignore_warning", "boolean", "Import all transition variants when the projected transition count exceeds 128; defaults to false."))),
                    CommandDefinition(AnimationAnalyzeCommand,
                        "Analyze one or two immutable Session clips and render their visual evidence synchronously. Each clip explicitly names its Session character. Returns sparse keyframes, foot contacts, and self-describing picture tiles; completed Clips are never modified.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            RequiredAnalysisClips(),
                             OptionalEnumWithDefault("level", "middle", "low", "middle", "high", "-test"),
                             Optional("resolution", "integer", "Final picture tile resolution in pixels; rendering uses a 2x supersample and downsamples to this size. Defaults to 512."))),
                    CommandDefinition(AnimationCompareCommand,
                        "Compare two animation ranges or transition-like clip ranges without modifying the Session.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            Required("character", "string", "Safe character name in the current Session."),
                            Required("origin", "object", "Origin animation and half-open frame range."),
                            Required("target", "object", "Target animation and half-open frame range."))),
                    CommandDefinition(RecordRangeCommand,
                        "Record a Session time range into an AnimationClip and append it to the source character.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            Required("start_frame", "integer", "Inclusive Session frame at 60 FPS."),
                            Required("end_frame", "integer", "Exclusive Session frame at 60 FPS."),
                            Required("character", "string", "Safe source character name in the current Session."),
                            Optional("remove_root_motion", "boolean", "Keep vertical motion but remove horizontal root translation and yaw; defaults to false."),
                            Optional("speed", "number", "Playback speed multiplier; defaults to 1.0."),
                            Optional("name", "string", "Requested safe output animation name."),
                            Optional("output_folder", "string", "Unity folder under Assets; defaults to Assets/KimodoGeneratedClips."))),
                    CommandDefinition(RetargetAnimationCommand,
                        "Retarget one loaded animation to another current Session character and append the result.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            Required("source_character", "string", "Safe source character name in the selected Session."),
                            Required("animation", "string", "Safe source animation name."),
                            Required("target_character", "string", "Safe target character name in the selected Session."),
                            Optional("name", "string", "Requested safe output animation name."),
                            Optional("output_folder", "string", "Unity folder under Assets; defaults to Assets/KimodoGeneratedClips."))),
                    CommandDefinition(GenerateAnimationCommand,
                        "Start asynchronous generation for a character already added to the selected Session. The accepted request is recorded in session.json and must be polled by request_id.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            Required("character", "string", "Safe character name in the selected Session."),
                            Required("prompt", "string", "Motion prompt."),
                            Optional("duration_frames", "integer", "Duration in 60 FPS Session frames; defaults to 300."),
                            Optional("loop", "boolean", "Enable bounded loop preprocessing; over-limit requests fall back to normal generation."),
                            Optional("model", "string", "Registered model name/configuration id; omitted uses the Project Settings default. Use kimodo_help({section:'models'}) to query models."),
                            Enum("text_encoder_model", "high_performance", "high_precision"),
                            Optional("seed", "integer", "Deterministic seed; omitted chooses a random seed."),
                            Optional("diffusion_steps", "integer", "Diffusion steps; omitted uses the model default."),
                            Enum("output_mode", "humanoid_muscle", "character_bone", "model_bone"),
                            Optional("output_folder", "string", "Unity folder under Assets; defaults to Assets/KimodoGeneratedClips."),
                            Optional("name", "string", "Requested safe animation name; defaults to the prompt."),
                            Optional("analysis_option", "object", "Optional analysis object; set keyframes.enabled=true to return screenshot keyframes."),
                            OptionalConstraints("constraints", "One sparse constraint object per frame; combine fullbody, root2d, and pose-based hand/foot constraints in the same object."))),
                    CommandDefinition(PoseGetCommand,
                        "Read a canonical pose from a source locator, creating or reusing a Pose Cache marker. full_data=true returns all muscles and TQ channels.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            Required("source", "string", "Animation or Pose Cache source name."),
                            Required("frame", "integer", "Animation-local frame for an animation source, or the returned frame for a Pose Cache source."),
                            Optional("full_data", "boolean", "Return all 49 muscles and TQ channels; defaults to false."))),
                    CommandDefinition(PoseContractCommand,
                        "Align a target Pose end-effector to an origin Pose and create a cached constraint description.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            Required("origin", "object", "Origin Pose locator."),
                            Required("target", "object", "Target Pose locator."),
                            RequiredEnumArray("endeffectors", "End effectors to align.", "left_hand", "right_hand", "left_foot", "right_foot"),
                            RequiredEnumArray("components", "Components to align.", "position", "rotation"),
                            RequiredEnum("mode", "align_target_root", "least_squares_root_fit"))),
                    CommandDefinition(PoseSetRootTransformCommand,
                        "Modify the root transform of a cached Pose marker.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            Required("pose", "object", "Pose Cache marker locator."),
                            Required("root", "object", "Root position and rotation."))),
                    CommandDefinition(PoseSetMuscleCommand,
                        "Modify one or more muscles of a cached Pose marker.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            Required("pose", "object", "Pose Cache marker locator."),
                            Required("muscles", "object", "Map of muscle channel names to values."))),
                    CommandDefinition(GetGenerationCommand,
                        "Get generation progress and the generated animation safe name.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            Required("request_id", "string", "Request id returned by a generate tool."))),
                    CommandDefinition(CancelGenerationCommand,
                        "Cancel an active Kimodo generation request.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            Required("request_id", "string", "Request id returned by a generate tool."),
                            Optional("reason", "string", "Optional cancellation reason.")))
                }
            }.ToString(Formatting.None);
        }

        public static string Invoke(string toolName, string argumentsJson = "{}")
        {
            switch (toolName?.Trim())
            {
                case HelpCommand:
                    return GetCommandHelp(argumentsJson);
                case InstallServerCommand:
                    return InstallServer(argumentsJson);
                case SessionGetOrCreateCommand:
                    return SessionGetOrCreate(argumentsJson);
                case SessionCloseCommand:
                    return SessionClose(argumentsJson);
                case SessionAddCommand:
                    return SessionAdd(argumentsJson);
                case AnimationAnalyzeCommand:
                    return AnimationAnalyze(argumentsJson);
                case AnimationCompareCommand:
                    return AnimationCompare(argumentsJson);
                case RecordRangeCommand:
                    return RecordRange(argumentsJson);
                case RetargetAnimationCommand:
                    return RetargetAnimation(argumentsJson);
                case PoseGetCommand:
                    return PoseGet(argumentsJson);
                case PoseContractCommand:
                    return PoseContract(argumentsJson);
                case PoseSetRootTransformCommand:
                    return PoseSetRootTransform(argumentsJson);
                case PoseSetMuscleCommand:
                    return PoseSetMuscle(argumentsJson);
                case GenerateAnimationCommand:
                    return GenerateAnimationAsset(argumentsJson);
                case GetGenerationCommand:
                    return GetGeneration(argumentsJson);
                case CancelGenerationCommand:
                    return CancelGeneration(argumentsJson);
                default:
                    return Error("unknown_command", $"Unknown Kimodo command '{toolName ?? string.Empty}'.");
            }
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

        public static string GetCommandHelp(string argumentsJson = "{}")
        {
            return Execute(argumentsJson, arguments =>
            {
                string section = (arguments.Value<string>("section") ?? "commands").Trim().ToLowerInvariant();
                string command = arguments.Value<string>("command")?.Trim();
                if (!string.IsNullOrWhiteSpace(command))
                {
                    JObject definitions = JObject.Parse(GetCommandDefinitionsJson());
                    JObject definition = definitions["tools"]?.Children<JObject>()
                        .FirstOrDefault(item => string.Equals(item.Value<string>("name"), command, StringComparison.Ordinal));
                    if (definition == null)
                    {
                        throw new InvalidOperationException($"Unknown Kimodo command '{command}'.");
                    }
                    return Ok(new JObject
                    {
                        ["manual"] = definition.DeepClone(),
                        ["usage"] = $"{command}(<arguments matching inputSchema>)"
                    });
                }
                if (section == "models")
                {
                    return ListModels("{}");
                }
                if (section == "constraints")
                {
                    return Ok(BuildConstraintManual());
                }
                if (section != "commands")
                {
                    throw new InvalidOperationException("section must be commands, models, or constraints.");
                }

                JObject all = JObject.Parse(GetCommandDefinitionsJson());
                JObject constraintManual = BuildConstraintManual();
                return Ok(new JObject
                {
                    ["manual"] = "Kimodo command reference",
                    ["execution_model"] = new JArray
                    {
                        "A command may omit session_id only when a current Session exists; otherwise it fails with session_required.",
                        "session_get_or_create is the only command that creates Sessions. New Sessions are empty; add scene content explicitly with session_add.",
                        "Treat names, request_id, picture paths, and Pose Cache locators as opaque values returned by Kimodo.",
                        "Generation is asynchronous: save request_id and poll kimodo_get_generation until completed, failed, or canceled.",
                        "Read session_json_path after Session-changing commands for the complete AI-readable Session state."
                    },
                    ["routing"] = new JArray
                    {
                        Route("discover schema or models", HelpCommand),
                        Route("select or create a Session", SessionGetOrCreateCommand),
                        Route("add a character, clip, or Animator", SessionAddCommand),
                        Route("generate motion", GenerateAnimationCommand, "then " + GetGenerationCommand),
                        Route("analyze and render motion", AnimationAnalyzeCommand, "returns one composite picture and self-describing tiles"),
                        Route("sample or edit a cached pose", PoseGetCommand, "then pose_set_root_transform / pose_set_muscle / pose_contract"),
                        Route("record or retarget", RecordRangeCommand, "or " + RetargetAnimationCommand)
                    },
                    ["handles"] = new JObject
                    {
                        ["session_id"] = "Pass to any Session-scoped command; omission selects the current Session.",
                        ["request_id"] = "Pass only to kimodo_get_generation or kimodo_cancel_generation.",
                        ["pictures.image_path"] = "Read the composite PNG returned by animation_analyze.",
                        ["Pose Cache locator"] = "Pass only to pose_set_root_transform, pose_set_muscle, or pose_contract."
                    },
                    ["workflow"] = new JArray
                    {
                        new JObject { ["command"] = SessionGetOrCreateCommand, ["arguments"] = new JObject { ["name"] = "Locomotion" } },
                        new JObject { ["command"] = SessionAddCommand, ["arguments"] = new JObject { ["kind"] = "character", ["character"] = "<scene name or path>" } },
                        new JObject { ["command"] = GenerateAnimationCommand, ["arguments"] = new JObject { ["character"] = "<character>", ["prompt"] = "stand still and breathe naturally", ["duration_frames"] = 60 }, ["save"] = "request_id" },
                        new JObject { ["command"] = GetGenerationCommand, ["arguments"] = new JObject { ["request_id"] = "<request_id>" }, ["repeat_until"] = "status is completed, failed, or canceled" },
                        new JObject { ["command"] = AnimationAnalyzeCommand, ["arguments"] = new JObject { ["clips"] = new JArray(new JObject { ["character"] = "<character>", ["clip"] = "<completed animation>" }), ["level"] = "middle" }, ["save"] = "pictures.image_path" },
                        new JObject { ["command"] = SessionCloseCommand, ["arguments"] = new JObject() }
                    },
                    ["commands"] = new JArray(all["tools"].Children<JObject>().Select(item => new JObject
                    {
                        ["name"] = item.Value<string>("name"),
                        ["description"] = item.Value<string>("description"),
                        ["required"] = item["inputSchema"]?["required"]?.DeepClone() ?? new JArray(),
                        ["debug_only"] = item.Value<bool?>("debug_only") ?? false
                    })),
                    ["constraints"] = constraintManual["constraints"].DeepClone(),
                    ["constraint_rules"] = constraintManual["rules"].DeepClone()
                });
            });
        }

        private static JObject BuildConstraintManual()
        {
            return new JObject
            {
                ["manual"] = "Kimodo generation constraint reference",
                ["constraints"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "fullbody",
                        ["description"] = "A complete body pose constraint from a pose locator. It constrains the full-body joints and also includes the root bone position and heading.",
                        ["shape"] = new JObject
                        {
                            ["frame"] = "Relative frame in the generated clip.",
                            ["fullbody"] = new JObject { ["pose"] = "{source,frame} pose locator" }
                        }
                    },
                    new JObject
                    {
                        ["type"] = "root2d",
                        ["description"] = "A root-only constraint. It constrains the root bone position and heading on the ground plane, without constraining the rest of the body.",
                        ["shape"] = new JObject
                        {
                            ["frame"] = "Relative frame in the generated clip.",
                            ["root2d"] = new JObject
                            {
                                ["pose"] = "{source,frame} pose locator, or direct position + heading",
                                ["position"] = "[x,z]",
                                ["heading"] = "[x,z] forward direction"
                            }
                        }
                    }
                },
                ["rules"] = new JArray
                {
                    "At the same frame, fullbody supplies the base pose, root2d overrides RootTQ, and hand/foot effector channels override their matching protocol fields.",
                    "Use fullbody for a complete pose and root2d when only the root trajectory or heading should be constrained."
                }
            };
        }

        public static string InstallServer(string argumentsJson = "{}")
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

        public static string GetGeneration(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                Guid requestId = RequiredRequestId(arguments);
                if (!TryGetJob(requestId, out JobRecord record))
                {
                    JObject persisted = LoadPersistedGenerationJob(session, requestId);
                    persisted["target_alive"] = false;
                    return Ok(persisted);
                }
                EnsureGenerationBelongsToSession(record, session);
                JObject status = BuildStatus(record);
                status["target_alive"] = record.Target != null;
                return Ok(status);
            });
        }

        public static string CancelGeneration(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                Guid requestId = RequiredRequestId(arguments);
                if (!TryGetJob(requestId, out JobRecord record))
                {
                    JObject persisted = LoadPersistedGenerationJob(session, requestId);
                    persisted["canceled"] = false;
                    return Ok(persisted);
                }
                EnsureGenerationBelongsToSession(record, session);
                string reason = arguments.Value<string>("reason")?.Trim();
                bool canceled = KimodoEditorGenerationJobService.Cancel(
                    requestId,
                    string.IsNullOrWhiteSpace(reason) ? "Generation canceled by command." : reason);
                PersistGenerationJobStatus(record.Session);
                JObject status = BuildStatus(record);
                status["canceled"] = canceled;
                return Ok(status);
            });
        }

        public static string GenerateAnimationAsset(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                EnsureCanGenerate();
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                string prompt = RequiredStringValue(arguments, "prompt");
                ResolvedCharacter character = ResolveCharacter(session, RequiredStringValue(arguments, "character"));
                string outputMode = ParseOutputMode(arguments.Value<string>("output_mode"));
                string requestedModel = arguments.Value<string>("model")?.Trim();
                string requestedTextEncoder = arguments.Value<string>("text_encoder_model")?.Trim();
                string modelName = ResolveModelName(requestedModel);
                KimodoTextEncoderMode textEncoderMode = ResolveTextEncoderMode(requestedTextEncoder);
                JObject modelConfiguration = null;
                if (!string.IsNullOrWhiteSpace(requestedModel) || !string.IsNullOrWhiteSpace(requestedTextEncoder))
                {
                    modelConfiguration = EnsureRegisteredModel(modelName, textEncoderMode);
                }
                float frameRate = ResolveFrameRate(modelName, modelConfiguration);
                int durationFrames = arguments.Value<int?>("duration_frames") ?? 300;
                if (durationFrames <= 0)
                {
                    throw new InvalidOperationException("duration_frames must be a positive integer at 60 FPS.");
                }
                bool loopRequested = arguments.Value<bool?>("loop") ??
                    prompt.IndexOf("loop", StringComparison.OrdinalIgnoreCase) >= 0;
                bool loopFallback = loopRequested && durationFrames > 300;
                string loopWarning = loopFallback
                    ? $"loop_requested_but_exceeds_max_duration: requested={durationFrames} frames, extended={durationFrames * 2} frames, max=600. Fallback to default generation."
                    : null;
                if (loopFallback)
                {
                    Debug.LogWarning("[Kimodo][Command] " + loopWarning);
                    loopRequested = false;
                }
                float duration = (float)(durationFrames / SessionFrameRate);
                string analysisOptionsJson = ParseAnalysisOptionsJson(arguments);
                int frameCount = Math.Max(1, KimodoFrameTimeUtility.SecondsToFrameCount(duration, frameRate));
                int seed = arguments.Value<int?>("seed") ?? (Guid.NewGuid().GetHashCode() & int.MaxValue);
                int steps = ResolveDiffusionSteps(arguments, modelName, modelConfiguration);
                string outputFolder = KimodoEditorOutputPathUtility.NormalizeOutputFolder(arguments.Value<string>("output_folder"));
                string requestedAnimationName = string.IsNullOrWhiteSpace(arguments.Value<string>("name"))
                    ? prompt
                    : arguments.Value<string>("name").Trim();
                Avatar originAvatar = KimodoTimelineGenerationOutputPlanner.ResolveOriginRetargetAvatar(modelName);
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
                    durationFrames);

                if (outputMode != "model_bone" && !KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
                {
                    throw new InvalidOperationException($"Character '{character.Name}' does not provide a valid target humanoid Avatar for output_mode '{outputMode}'.");
                }

                TimelineGenerationTrace trace = PrepareGenerationTrace(arguments, character, duration);
                KimodoPlayableClip playableClip = CreateGenerationPlayableClip(trace, requestedAnimationName);
                playableClip.bridgeModelName = modelName;
                playableClip.textEncoderMode = textEncoderMode;
                playableClip.motionPrompt = prompt;
                playableClip.generationFrames = frameCount;
                playableClip.diffusionSteps = steps;
                playableClip.randomSeed = false;
                playableClip.seed = seed;
                playableClip.generateLoop = loopRequested;
                playableClip.loop = loopRequested
                    ? UnityEngine.Timeline.AnimationPlayableAsset.LoopMode.On
                    : UnityEngine.Timeline.AnimationPlayableAsset.LoopMode.Off;
                playableClip.analysisOptionsJson = analysisOptionsJson;
                playableClip.generatedAssetName = trace.Animation.Name;
                playableClip.generatedOutputFolder = outputFolder;
                playableClip.generationOutputMode = ParseGenerationOutputMode(outputMode);
                WriteGenerationConstraintMarkers(trace, poseConstraints, (float)SessionFrameRate);
                ReserveGenerationTimelineRange(trace);
                SaveTimelineSession(session);

                bool started = KimodoEditorGenerationJobService.Start(
                    character.Target,
                    async (generationSession, token) =>
                    {
                        try
                        {
                            return await ExecutePlayableClipGenerationAsync(
                                playableClip,
                                trace,
                                character.Target,
                                generationSession,
                                token);
                        }
                        finally
                        {
                            // Session lifetime is owned exclusively by session_close.
                        }
                    },
                    PersistGenerationJobStatus,
                    out KimodoEditorGenerationJobSession generation,
                    out string error);
                if (!started)
                {
                    throw new InvalidOperationException(error);
                }

                Remember(character.Target, generation, trace);
                PersistGenerationJobStatus(generation);
                var startedResponse = new JObject
                {
                    ["character"] = trace.Character.Name,
                    ["animation"] = trace.Animation.Name,
                    ["output_mode"] = outputMode,
                    ["model"] = modelName,
                    ["text_encoder_model"] = KimodoTextEncoderModeProtocol.ToProtocolValue(textEncoderMode),
                    ["seed"] = seed
                };
                if (trace != null)
                {
                    startedResponse["session_name"] = trace.Session.Name;
                    startedResponse["start_frame"] = Mathf.RoundToInt((float)(trace.StartSeconds * SessionFrameRate));
                    startedResponse["duration_frames"] = Mathf.RoundToInt((float)(trace.DurationSeconds * SessionFrameRate));
                }
                if (loopRequested)
                {
                    startedResponse["loop"] = true;
                    startedResponse["loop_source_duration_frames"] = durationFrames;
                    startedResponse["loop_extended_duration_frames"] = durationFrames * 2;
                }
                if (loopWarning != null)
                {
                    startedResponse["warnings"] = new JArray(loopWarning);
                    startedResponse["loop_fallback"] = loopFallback;
                }
                return Started(generation, startedResponse);
            });
        }

        private static async Task<KimodoEditorGenerationResult> ExecutePlayableClipGenerationAsync(
            KimodoPlayableClip playableClip,
            TimelineGenerationTrace trace,
            UnityEngine.Object target,
            KimodoEditorGenerationJobSession session,
            CancellationToken token)
        {
            KimodoEditorGenerationResult result = await KimodoPlayableClipGenerationExecutionService.GenerateAndFinalizeAsync(
                playableClip,
                externalConstraint: null,
                (stage, message) => KimodoEditorGenerationJobService.UpdateProgress(target, session.RequestId, stage, message),
                token,
                trace.TimelineClip);
            FinalizePlayableClipTrace(trace, result);
            return result;
        }

        private static KimodoGenerationOutputMode ParseGenerationOutputMode(string outputMode)
        {
            switch (outputMode)
            {
                case "character_bone":
                    return KimodoGenerationOutputMode.CharacterBone;
                case "model_bone":
                    return KimodoGenerationOutputMode.ModelBone;
                default:
                    return KimodoGenerationOutputMode.HumanoidMuscle;
            }
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
            int durationFrames)
        {
            if (arguments?["constraints"] == null)
            {
                return new List<KimodoMarkerSampleResult>();
            }
            if (arguments["constraints"] is not JArray constraints)
            {
                throw new InvalidOperationException("constraints must be an array.");
            }
            var samples = new List<KimodoMarkerSampleResult>(constraints.Count * 3);
            RetargetSkeleton targetCache = null;
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            double originalSessionTime = session.Director.time;
            try
            {
                if (constraints.Count > 0 &&
                    !KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        targetAvatar,
                        "KimodoCommandPoseConstraints",
                        out targetCache,
                        out string cacheError))
                {
                    throw new InvalidOperationException($"Build pose constraint target failed: {cacheError}");
                }

                // Fullbody FK sampling mutates the shared cache pose. Capture the
                // profile root height once so later root2d constraints do not
                // inherit the previous fullbody pose's transient hips height.
                float targetRootHeight = ResolveTargetRootHeight(targetCache);

                for (int i = 0; i < constraints.Count; i++)
                {
                    if (constraints[i] is not JObject constraint)
                    {
                        throw new InvalidOperationException($"constraints[{i}] must be an object.");
                    }
                    if (constraint["type"] != null || constraint["pose"] != null ||
                        constraint["position"] != null || constraint["heading"] != null)
                    {
                        throw new InvalidOperationException(
                            $"constraints[{i}] uses the removed legacy shape; use fullbody, root2d, or end-effector fields.");
                    }
                    int relativeFrame = RequiredNonNegativeFrame(constraint, "frame");
                    if (relativeFrame >= durationFrames)
                    {
                        throw new InvalidOperationException($"constraints[{i}].frame must be within [0,{durationFrames}).");
                    }
                    double at = relativeFrame / SessionFrameRate;
                    JObject fullBody = constraint["fullbody"] as JObject;
                    JObject root2D = constraint["root2d"] as JObject;
                    JObject[] endEffectors =
                    {
                        constraint["left_hand"] as JObject,
                        constraint["right_hand"] as JObject,
                        constraint["left_foot"] as JObject,
                        constraint["right_foot"] as JObject
                    };
                    string[] endEffectorTypes = { "left-hand", "right-hand", "left-foot", "right-foot" };
                    if (fullBody == null && root2D == null && endEffectors.All(value => value == null))
                    {
                        throw new InvalidOperationException($"constraints[{i}] must contain at least one constraint field.");
                    }

                    if (fullBody != null)
                    {
                        samples.Add(BuildProfilePoseConstraint(
                            fullBody, "fullbody", targetCache, modelName, frameRate, at, i));
                    }
                    if (root2D != null)
                    {
                        samples.Add(BuildRoot2DConstraint(root2D, targetRootHeight, targetCache, at, i));
                    }

                    for (int part = 0; part < endEffectors.Length; part++)
                    {
                        if (endEffectors[part] == null)
                        {
                            continue;
                        }
                        samples.Add(BuildProfilePoseConstraint(
                            endEffectors[part], endEffectorTypes[part], targetCache, modelName, frameRate, at, i));
                    }
                }
            }
            finally
            {
                session.Director.time = originalSessionTime;
                session.Director.Evaluate();
                targetCache?.Dispose();
            }
            return samples;
        }

        private static KimodoMarkerSampleResult BuildRoot2DConstraint(
            JObject value,
            float targetRootHeight,
            RetargetSkeleton targetCache,
            double sampleTime,
            int constraintIndex)
        {
            CharacterPose pose = value?["pose"] is JObject locator
                ? ReadCharacterPose(locator, $"constraints[{constraintIndex}].root2d")
                : new CharacterPose();
            bool hasPosition = value?["position"] != null;
            bool hasHeading = value?["heading"] != null;
            bool hasRootPose = value?["pose"] is JObject;
            if (hasPosition != hasHeading)
            {
                throw new InvalidOperationException($"constraints[{constraintIndex}].root2d requires position and heading together.");
            }
            if (hasPosition)
            {
                Vector2 position = RequiredVector2(value, "position");
                Vector2 heading = RequiredVector2(value, "heading");
                if (heading.sqrMagnitude <= 1e-8f)
                {
                    throw new InvalidOperationException($"constraints[{constraintIndex}].root2d.heading must be non-zero.");
                }
                pose.root.t = new Vector3(position.x, targetRootHeight, position.y);
                pose.root.q = Quaternion.LookRotation(new Vector3(heading.x, 0f, heading.y), Vector3.up);
            }
            else if (value?["pose"] == null)
            {
                throw new InvalidOperationException($"constraints[{constraintIndex}].root2d requires pose or position plus heading.");
            }

            var result = new KimodoMarkerSampleResult
            {
                constraintMode = "root2d",
                sampleTime = sampleTime,
                root2DOverride = new KimodoRigidTransform
                {
                    t = pose.root.t,
                    q = pose.root.q
                },
                enableMask = new KimodoSampleChannelMask
                {
                    root2DPosition = hasPosition || hasRootPose,
                    root2DHeading = hasPosition || hasRootPose
                }
            };
            result.enableMask.muscle49 = false;
            result.enableMask.rootTQ = false;
            result.enableMask.leftFootTQ = false;
            result.enableMask.rightFootTQ = false;
            return result;
        }

        private static KimodoMarkerSampleResult BuildProfilePoseConstraint(
            JObject value,
            string constraintType,
            RetargetSkeleton targetCache,
            string modelName,
            float frameRate,
            double sampleTime,
            int constraintIndex)
        {
            CharacterPose sourcePose = ReadCharacterPose(value?["pose"] as JObject,
                $"constraints[{constraintIndex}].{constraintType.Replace('-', '_')}");
            MuscleSample sourceSample = CharacterPoseMuscleAdapter.ToBodyMuscleSample(sourcePose);
            KimodoConstraintMask mask = KimodoConstraintMask.ForType(constraintType);
            if (!KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                    sourceSample, frameRate, targetCache,
                    out BoneSample boneSample, out MuscleSample targetMuscleSample, out string retargetError,
                    includeLeftHandEffector: mask.leftHand,
                    includeRightHandEffector: mask.rightHand,
                    includeFootEffectors: mask.leftFoot || mask.rightFoot,
                    includeLeftFootEffector: mask.leftFoot,
                    includeRightFootEffector: mask.rightFoot))
            {
                throw new InvalidOperationException($"Retarget constraints[{constraintIndex}] failed: {retargetError}");
            }
            if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    boneSample, targetCache, modelName, constraintType, sampleTime,
                    out KimodoMarkerSampleResult converted, out string convertError))
            {
                throw new InvalidOperationException($"Convert constraints[{constraintIndex}] failed: {convertError}");
            }
            converted.constraintMode = constraintType;
            // Retarget sampling already returned the canonical 70D payload,
            // including the evaluated body-relative footTQ channels. Do not
            // round-trip through CharacterPose, whose cached foot fields are
            // scene/world values and would overwrite footTQ.
            converted.sampleData = targetMuscleSample?.Clone() ?? new MuscleSample();
            converted.enableMask ??= new KimodoSampleChannelMask();
            converted.enableMask.muscle49 = true;
            converted.enableMask.rootTQ = true;
            converted.enableMask.leftFootTQ = true;
            converted.enableMask.rightFootTQ = true;
            converted.sampleTime = sampleTime;
            return converted;
        }

        private static CharacterPose ReadCharacterPose(JObject value, string path)
        {
            if (value == null)
            {
                throw new InvalidOperationException($"{path}.pose is required.");
            }
            PoseLocator locator = RequirePoseLocator(value);
            JObject poseResult = ReadPose(locator);
            return CharacterPoseJson.Parse(poseResult["data"] as JObject
                ?? throw new InvalidOperationException($"{path}.pose data is unavailable."));
        }

        internal static float ResolveTargetRootHeight(RetargetSkeleton targetCache)
        {
            if (targetCache != null &&
                targetCache.GetBonePose(
                    HumanBodyBones.Hips,
                    out Vector3 hipsPosition,
                    out _))
            {
                return hipsPosition.y;
            }

            return 0f;
        }

        private static JObject Route(string intent, string command, string next = null)
        {
            var route = new JObject { ["intent"] = intent, ["command"] = command };
            if (!string.IsNullOrWhiteSpace(next)) route["next"] = next;
            return route;
        }

        private static bool TrySampleDirectSkeletonConstraint(
            TimelineCharacterRecord source,
            RetargetSkeleton targetCache,
            string modelName,
            string constraintType,
            double sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (source?.Root == null || !KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
            {
                return false;
            }

            Transform[] allSourceTransforms = source.Root.GetComponentsInChildren<Transform>(true);
            Transform[] rootCandidates = allSourceTransforms
                .Where(transform => string.Equals(
                    transform.name, targetCache.canonicalRootBoneName, StringComparison.Ordinal))
                .ToArray();
            Transform sourceSkeletonRoot = rootCandidates.Length == 1 ? rootCandidates[0] : null;
            if (sourceSkeletonRoot == null)
            {
                error = $"incompatible_skeleton: source must contain one unambiguous '{targetCache.canonicalRootBoneName}' root bone";
                return false;
            }
            Transform[] sourceTransforms = sourceSkeletonRoot.GetComponentsInChildren<Transform>(true);
            var sourceNames = sourceTransforms.ToDictionary(transform => transform, transform => transform.name);
            var sourceByPath = sourceTransforms.ToDictionary(
                transform => KimodoRetargetAvatarUtility.CalculateTransformPath(
                    transform, sourceSkeletonRoot, targetCache.canonicalRootBoneName, sourceNames),
                transform => transform,
                StringComparer.Ordinal);
            var targetPaths = new HashSet<string>(targetCache.bonePaths, StringComparer.Ordinal);
            string missing = sourceByPath.Keys.FirstOrDefault(path => !targetPaths.Contains(path));
            if (missing != null)
            {
                error = $"incompatible_skeleton: target skeleton is missing source bone path '{missing}'";
                return false;
            }

            KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(targetCache);
            for (int i = 0; i < targetCache.bonePaths.Length; i++)
            {
                if (sourceByPath.TryGetValue(targetCache.bonePaths[i], out Transform sourceTransform) &&
                    targetCache.boneTransforms[i] != null)
                {
                    targetCache.boneTransforms[i].localPosition = sourceTransform.localPosition;
                    targetCache.boneTransforms[i].localRotation = sourceTransform.localRotation;
                }
            }
            BoneSample targetSample = KimodoRetargetSamplingUtility.CaptureBoneSample(targetCache);
            if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    targetSample, targetCache, modelName, constraintType, sampleTime, out sample, out error))
            {
                return false;
            }
            return true;
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
            RetargetSkeleton targetCache,
            string modelName,
            string constraintType,
            double sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (pose.Animator == null || !KimodoRetargetAvatarUtility.ValidateRetargetSkeleton(targetCache, out error))
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
                KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(targetCache);
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

                if (!KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        targetCache,
                        out MuscleSample evaluatedSample,
                        out error))
                {
                    return false;
                }
                sample.sampleData = evaluatedSample;
                sample.enableMask ??= new KimodoSampleChannelMask();
                sample.enableMask.muscle49 = true;
                sample.enableMask.rootTQ = true;
                sample.enableMask.leftFootTQ = true;
                sample.enableMask.rightFootTQ = true;

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

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
            AnimationClip[] matches = AssetDatabase.FindAssets($"t:AnimationClip {name}", new[] { "Assets" })
                .SelectMany(guid => AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)).OfType<AnimationClip>())
                .Where(clip => string.Equals(clip.name, name, StringComparison.OrdinalIgnoreCase))
                .Distinct().ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(matches.Length == 0
                    ? $"AnimationClip '{name}' was not found under Assets."
                    : $"AnimationClip name '{name}' is ambiguous; use a unique project clip name.");
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
                ["started_at_utc"] = session.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)
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

        private static PropertyDefinition RequiredArray(string name, string itemType, string description)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = itemType },
                ["description"] = description
            }, true);
        }

        private static PropertyDefinition RequiredAnalysisClips()
        {
            return new PropertyDefinition("clips", new JObject
            {
                ["type"] = "array",
                ["description"] = "One or two immutable Session clip references. Every item explicitly names its Session character; role defaults to source for the first item and target for the second.",
                ["minItems"] = 1,
                ["maxItems"] = 2,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["role"] = new JObject { ["type"] = "string", ["enum"] = new JArray("source", "target") },
                        ["character"] = new JObject { ["type"] = "string" },
                        ["clip"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray("character", "clip")
                }
            }, true);
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

        private static PropertyDefinition RequiredEnumArray(string name, string description, params string[] values)
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
            }, true);
        }

        private static PropertyDefinition OptionalConstraints(string name, string description)
        {
            var poseLocator = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["source"] = new JObject { ["type"] = "string" },
                    ["frame"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }
                },
                ["required"] = new JArray("source", "frame")
            };
            var vector2 = new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "number" },
                ["minItems"] = 2,
                ["maxItems"] = 2
            };
            var fullBody = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject { ["pose"] = poseLocator.DeepClone() },
                ["required"] = new JArray("pose")
            };
            var root2D = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["pose"] = poseLocator.DeepClone(),
                    ["position"] = vector2.DeepClone(),
                    ["heading"] = vector2.DeepClone()
                },
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("pose") },
                    new JObject { ["required"] = new JArray("position", "heading") })
            };
            var endEffector = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject { ["pose"] = poseLocator.DeepClone() },
                ["required"] = new JArray("pose")
            };
            var sparseItem = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["frame"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Relative frame in the generated clip at 60 FPS." },
                    ["fullbody"] = fullBody,
                    ["root2d"] = root2D,
                    ["left_hand"] = endEffector.DeepClone(),
                    ["right_hand"] = endEffector.DeepClone(),
                    ["left_foot"] = endEffector.DeepClone(),
                    ["right_foot"] = endEffector.DeepClone()
                },
                ["required"] = new JArray("frame"),
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("fullbody") },
                    new JObject { ["required"] = new JArray("root2d") },
                    new JObject { ["required"] = new JArray("left_hand") },
                    new JObject { ["required"] = new JArray("right_hand") },
                    new JObject { ["required"] = new JArray("left_foot") },
                    new JObject { ["required"] = new JArray("right_foot") })
            };
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = sparseItem
            }, false);
        }

        private static PropertyDefinition OptionalPoseLocators(string name, string description)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["source"] = new JObject { ["type"] = "string" },
                        ["frame"] = new JObject { ["type"] = "integer" }
                    },
                    ["required"] = new JArray("source", "frame")
                }
            }, false);
        }

        private static PropertyDefinition RequiredPoseLocator(string name)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "object",
                ["description"] = "Pose locator at fixed 60 FPS.",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["source"] = new JObject { ["type"] = "string" },
                    ["frame"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }
                },
                ["required"] = new JArray("source", "frame")
            }, true);
        }

        private static PropertyDefinition RequiredCharacterPose(string name, bool partial)
        {
            return new PropertyDefinition(name, CharacterPoseSchema(partial), true);
        }

        private static JObject CharacterPoseSchema(bool partial)
        {
            JObject schema = new JObject
            {
                ["type"] = "object",
                ["description"] = partial
                    ? "Partial canonical pose patch. Only supplied channels change."
                    : "Canonical pose: 49 body muscles in Unity index order 0-14,21-54; T is meters and Q is [x,y,z,w].",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["muscles"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject { ["type"] = "number" },
                        ["minItems"] = CharacterPose.MuscleCount,
                        ["maxItems"] = CharacterPose.MuscleCount
                    },
                    ["root"] = PoseTransformSchema(partial),
                    ["hands"] = PoseSidesSchema(partial),
                    ["feet"] = PoseSidesSchema(partial)
                }
            };
            if (partial)
            {
                schema["minProperties"] = 1;
            }
            else
            {
                schema["required"] = new JArray("muscles", "root", "hands", "feet");
            }
            return schema;
        }

        private static JObject PoseSidesSchema(bool partial)
        {
            JObject schema = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["left"] = PoseTransformSchema(partial),
                    ["right"] = PoseTransformSchema(partial)
                }
            };
            if (partial)
            {
                schema["minProperties"] = 1;
            }
            else
            {
                schema["required"] = new JArray("left", "right");
            }
            return schema;
        }

        private static JObject PoseTransformSchema(bool partial)
        {
            JObject schema = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["t"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject { ["type"] = "number" },
                        ["minItems"] = 3,
                        ["maxItems"] = 3
                    },
                    ["q"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject { ["type"] = "number" },
                        ["minItems"] = 4,
                        ["maxItems"] = 4
                    }
                }
            };
            if (partial)
            {
                schema["minProperties"] = 1;
            }
            else
            {
                schema["required"] = new JArray("t", "q");
            }
            return schema;
        }

        private static PropertyDefinition RequiredSamples(string name)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["character"] = new JObject { ["type"] = "string" },
                        ["time"] = new JObject { ["type"] = "number" }
                    },
                    ["required"] = new JArray("character", "time")
                }
            }, true);
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

        private static PropertyDefinition OptionalEnumWithDefault(string name, string defaultValue, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(values),
                ["default"] = defaultValue
            }, false);
        }

        private static PropertyDefinition RequiredEnum(string name, params string[] values)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray(values)
            }, true);
        }

        private sealed class JobRecord
        {
            public JobRecord(
                UnityEngine.Object target,
                KimodoEditorGenerationJobSession session,
                TimelineGenerationTrace timelineGenerationTrace)
            {
                Target = target;
                Session = session;
                TimelineGenerationTrace = timelineGenerationTrace;
            }

            public UnityEngine.Object Target { get; }
            public KimodoEditorGenerationJobSession Session { get; }
            public TimelineGenerationTrace TimelineGenerationTrace { get; }
        }

        private sealed class CommandException : InvalidOperationException
        {
            public CommandException(string code, string message) : base(message)
            {
                Code = string.IsNullOrWhiteSpace(code) ? "invalid_argument" : code;
            }

            public string Code { get; }
        }

        private sealed class GenerationRangeLockedException : InvalidOperationException
        {
            public GenerationRangeLockedException(
                string command,
                Guid requestId,
                string character,
                string track,
                int lockedStartFrame,
                int lockedEndFrame,
                int requestedStartFrame,
                int requestedEndFrame)
                : base($"{command} cannot access [{requestedStartFrame},{requestedEndFrame}) on '{track}' while generation {requestId:D} locks [{lockedStartFrame},{lockedEndFrame}).")
            {
                Command = command;
                RequestId = requestId;
                Character = character;
                Track = track;
                LockedStartFrame = lockedStartFrame;
                LockedEndFrame = lockedEndFrame;
                RequestedStartFrame = requestedStartFrame;
                RequestedEndFrame = requestedEndFrame;
            }

            public string Command { get; }
            public Guid RequestId { get; }
            public string Character { get; }
            public string Track { get; }
            public int LockedStartFrame { get; }
            public int LockedEndFrame { get; }
            public int RequestedStartFrame { get; }
            public int RequestedEndFrame { get; }
        }

        private readonly struct ResolvedCharacter
        {
            public ResolvedCharacter(GameObject root, Animator animator, Avatar avatar, string name)
            {
                Root = root;
                Animator = animator;
                Avatar = avatar;
                Name = name;
            }

            public GameObject Root { get; }
            public Animator Animator { get; }
            public Avatar Avatar { get; }
            public UnityEngine.Object Target => Root;
            public string Name { get; }
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
