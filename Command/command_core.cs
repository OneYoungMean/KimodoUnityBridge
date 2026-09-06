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
        public const string HelpCommand = "kimodo_help";
        public const string InstallServerCommand = "kimodo_install_server";
        public const string GenerateAnimationCommand = "kimodo_generate_animation";
        public const string SessionGetOrCreateCommand = "session_get_or_create";
        public const string SessionGetRawCommand = "session_get_raw";
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
        internal const string HelpAssetPath = "Packages/com.unity.kimodo_unity_motion_tools/Command/help.json";

        private const int MaxRememberedJobs = 128;
        private static readonly Dictionary<Guid, JobRecord> Jobs = new Dictionary<Guid, JobRecord>();
        private static readonly Dictionary<string, JObject> InstallTasks = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        private static readonly object JobsLock = new object();
        private static readonly Lazy<CommandCatalog> Commands = new Lazy<CommandCatalog>(BuildCommandCatalog, LazyThreadSafetyMode.ExecutionAndPublication);

        public static string GetCommandDefinitionsJson()
        {
            TextAsset help = AssetDatabase.LoadAssetAtPath<TextAsset>(HelpAssetPath);
            if (help != null)
            {
                try
                {
                    return JObject.Parse(help.text).ToString(Formatting.None);
                }
                catch (JsonException)
                {
                    // The code-built schema keeps command discovery available while an edited help file is invalid.
                }
            }
            return BuildCommandDefinitionsJson();
        }

        private static string BuildCommandDefinitionsJson()
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
                        "Start an asynchronous project-local QuickServer installation task. Returns a request_id (install:<guid>) that can be polled with kimodo_get_generation; models and the Python environment are preserved.",
                        Properties()),
                    CommandDefinition(SessionGetOrCreateCommand,
                        "Create the current animation Session and its dedicated visible Session GameObject, or reopen an existing named Session. Optionally add the current active-scene Animator character at creation; use character=@active_animator to bind the selected/open Animator instead of a saved prefab path.",
                        Properties(
                            Optional("name", "string", "Stable Session name. An existing name selects that Session; omit it to return the current Session or create one when none exists."),
                            Optional("character", "string", "Optional scene character name or hierarchy path; use @active_animator to use the currently selected/open Animator character."))),
                    CommandDefinition(SessionGetRawCommand,
                        "Resolve a named Session character, track, clip, or constraint to portable Unity object metadata for external API or tool interop; the result includes guid, asset_guid, path, object_type, and optional character.",
                        Properties(
                            RequiredEnum("kind", "character", "track", "clip", "constraint"),
                            Required("name", "string", "Exact Session object name."),
                            Optional("character", "string", "Optional character name to disambiguate clips, tracks, or constraints."))),
                    CommandDefinition(SessionCloseCommand,
                        "Close the selected animation editing Session while preserving its Timeline, assets, and AI-readable Session JSON.",
                        Properties(Optional("session_id", "string", "Session id; omitted uses the current Session."))),
                    CommandDefinition(SessionAddCommand,
                        "Add scene or project content to the current Session. kind=character adds one scene Humanoid Animator or renderable Mesh object (use character=@active_animator for the selected/open Animator); kind=clip appends one project AnimationClip to a Session character; kind=animator imports same-Layer State-to-State transitions as Timeline-composed transition_clip records without baking transition assets. Returns safe names to reuse. Appended clips keep a fixed 4-frame safezone.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            RequiredEnum("kind", "character", "clip", "animator"),
                            Required("character", "string", "Scene character name/path for kind=character, or @active_animator for the currently selected/open Animator; target Session character name otherwise."),
                            Optional("clip", "string", "Project AnimationClip name for kind=clip."),
                             Optional("animator", "string", "Scene Animator name/path for kind=animator."),
                             Optional("ignore_warning", "boolean", "Import all transition variants when the projected transition count exceeds 128; defaults to false."))),
                    CommandDefinition(AnimationAnalyzeCommand,
                        "Analyze one or two immutable Session clips and render visual evidence synchronously. Each Humanoid clips[] result includes root_trajectory.path plus clip-start-local Root XZ, heading, vertical motion, root pitch/roll samples, distance/speed/heading metrics, source human scale, endpoint_pose_comparison (body muscles plus complete root position/rotation), and motion_profile (loop/path/heading/vertical/tilt evidence plus deferred override decisions); the path is stored on the Pose Cache Track for reuse. Root2D is a planar XZ/heading override and never suppresses sampled Y, pitch, or roll. Mesh-only results omit Humanoid trajectory/contact data. Completed Clips are never modified.",
                        Properties(
                            Optional("session_id", "string", "Session id; omitted uses the current Session."),
                            RequiredAnalysisClips(),
                             OptionalEnumWithDefault("level", "middle", "low", "middle", "high"),
                            new PropertyDefinition("resolution", new JObject
                            {
                                ["type"] = "integer",
                                ["minimum"] = 64,
                                ["maximum"] = 4096,
                                ["description"] = "Final picture tile resolution in pixels; accepts 64 through 4096. Rendering uses a 2x supersample and downsamples to this size. Defaults to 512."
                            }, false))),
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
                        "Start asynchronous generation for a character in the current Session. The accepted request is recorded in session.json and must be polled by request_id.",
                        Properties(
                            Required("character", "string", "Safe character name in the current Session."),
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
                            Optional("analysis_option", "object", "Optional analysis object; set keyframes.enabled=true and keyframes.max_count (or keyframe_count) to control keyframe sampling."),
                            Optional("path_begin_angle_degrees", "number", "Absolute Unity yaw for the Root2D path start; providing either path angle enables same-seed Path Override, and an omitted peer defaults to zero."),
                            Optional("path_end_angle_degrees", "number", "Absolute Unity yaw for the Root2D path end; providing either path angle enables same-seed Path Override, and an omitted peer defaults to zero."),
                            Optional("override_heading_degrees", "number", "Regenerate with the same seed and apply this absolute Unity yaw to Root2D constraints every 30 frames; positive turns right and zero faces Unity forward."),
                            OptionalConstraints("constraints", "Point constraints and reusable root_path constraints for the generated clip."))),
                    CommandDefinition(PoseGetCommand,
                        "Sample one current-Session clip frame into a new External Pose slot. Returns the only reusable pose identity: {track,index}.",
                        Properties(
                            RequiredPoseSource("source"),
                            Optional("full_data", "boolean", "Return all 49 muscles and TQ channels; defaults to false."))),
                    CommandDefinition(PoseContractCommand,
                        "Align a target External Pose end-effector to an origin External Pose and create a new External Pose slot.",
                        Properties(
                            RequiredPoseReference("origin"),
                            RequiredPoseReference("target"),
                            RequiredEnumArray("endeffectors", "End effectors to align.", "left_hand", "right_hand", "left_foot", "right_foot"),
                            RequiredEnumArray("components", "Components to align.", "position", "rotation"),
                            RequiredEnum("mode", "align_target_root", "least_squares_root_fit"))),
                    CommandDefinition(PoseSetRootTransformCommand,
                        "Modify the root transform of an External Pose slot.",
                        Properties(
                            RequiredPoseReference("pose"),
                            Required("root", "object", "Root position and rotation."))),
                    CommandDefinition(PoseSetMuscleCommand,
                        "Modify one or more muscles of an External Pose slot.",
                        Properties(
                            RequiredPoseReference("pose"),
                            Required("muscles", "object", "Map of muscle channel names to values."))),
                    CommandDefinition(GetGenerationCommand,
                        "Get status, progress, remaining seconds, and message for an install or generation request. Generated animation metadata and its project-relative asset path are included only after a generation completes.",
                        Properties(
                            Required("request_id", "string", "Request id returned by kimodo_install_server or kimodo_generate_animation."))),
                    CommandDefinition(CancelGenerationCommand,
                        "Cancel an active animation generation request. Installation requests cannot be canceled.",
                        Properties(
                            Required("request_id", "string", "Generation request id returned by kimodo_generate_animation."),
                            Optional("reason", "string", "Optional cancellation reason.")))
                }
            }.ToString(Formatting.None);
        }

        public static string Invoke(string toolName, string argumentsJson = "{}")
        {
            string commandName = toolName?.Trim();
            if (Commands.Value.TryGet(commandName, out CommandRegistration command))
            {
                return command.Handler(argumentsJson);
            }
            return Error("unknown_command", $"Unknown Kimodo command '{toolName ?? string.Empty}'.");
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
                    if (!Commands.Value.TryGet(command, out CommandRegistration registration))
                    {
                        throw new InvalidOperationException($"Unknown Kimodo command '{command}'.");
                    }
                    return Ok(new JObject
                    {
                        ["manual"] = registration.ToJson(),
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

                JObject all = JObject.Parse(Commands.Value.ToJson());
                JObject constraintManual = BuildConstraintManual();
                return Ok(new JObject
                {
                    ["manual"] = "Kimodo command reference",
                    ["execution_model"] = new JArray
                    {
                        "A command may omit session_id only when a current Session exists; otherwise it fails with session_required.",
                        "session_get_or_create is the only command that creates Sessions and their dedicated visible Session GameObject. A scene character may be copied at creation or added explicitly with session_add.",
                        "Pass returned identity fields only to commands whose schemas consume them: safe names identify Session content, request_id polls an installation or generation task, and {track,index} identifies a Pose or analyzed Root Path. Picture paths are output files to inspect, not reusable handles.",
                        "Installation and generation are asynchronous: save request_id and poll kimodo_get_generation. Install terminal states are done or error; generation terminal states are completed, failed, or canceled.",
                        "Read session_json_path after Session-changing commands for the complete AI-readable Session state."
                    },
                    ["routing"] = new JArray
                    {
                        Route("discover schema or models", HelpCommand),
                        Route("install or refresh server", InstallServerCommand, "then " + GetGenerationCommand),
                        Route("select or create a Session", SessionGetOrCreateCommand),
                        Route("add a character, clip, or Animator", SessionAddCommand),
                        Route("generate motion", GenerateAnimationCommand, "then " + GetGenerationCommand),
                        Route("analyze and render motion", AnimationAnalyzeCommand, "returns one composite picture and self-describing tiles"),
                        Route("materialize or edit a pose", PoseGetCommand, "then pose_set_root_transform / pose_set_muscle / pose_contract"),
                        Route("obtain a reusable root trajectory", AnimationAnalyzeCommand, "then reference root_trajectory.path from a generation root_path constraint"),
                        Route("record or retarget", RecordRangeCommand, "or " + RetargetAnimationCommand)
                    },
                    ["handles"] = new JObject
                    {
                        ["session_id"] = "Pass to any Session-scoped command; omission selects the current Session.",
                        ["request_id"] = "Returned by kimodo_install_server or kimodo_generate_animation. Pass either to kimodo_get_generation; only generation request ids can be canceled.",
                        ["pictures.image_path"] = "Read the composite PNG returned by animation_analyze.",
                        ["pose"] = "A {track,index} reference returned by pose_get or a pose editing command.",
                        ["path"] = "For animation_analyze, this is the {track,index} Root Path reference passed only as root_path.path; for kimodo_get_generation or session_get_raw, it is a project-relative Unity asset path.",
                        ["raw_object"] = "The portable metadata returned by session_get_raw for Unity-external API or tool interop; it does not replace Session handles."
                    },
                    ["workflow"] = new JArray
                    {
                        new JObject { ["command"] = InstallServerCommand, ["arguments"] = new JObject(), ["save"] = "request_id", ["before"] = "all other Commands" },
                        new JObject { ["command"] = GetGenerationCommand, ["arguments"] = new JObject { ["request_id"] = "<install_request_id>" }, ["repeat_until"] = "status is done or error" },
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
                        ["required"] = item["inputSchema"]?["required"]?.DeepClone() ?? new JArray()
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
                        ["description"] = "A complete body pose constraint from a materialized pose. It constrains the full-body joints and also includes the root bone position and heading.",
                        ["shape"] = new JObject
                        {
                            ["frame"] = "Relative frame in the generated clip.",
                            ["fullbody"] = new JObject { ["pose"] = "{track,index}" }
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
                                ["pose"] = "{track,index}, or direct position + heading",
                                ["position"] = "[x,z]",
                                ["heading"] = "[x,z] forward direction"
                            }
                        }
                    },
                    new JObject
                    {
                        ["type"] = "root_path",
                        ["description"] = "A reusable analyzed Root Path compiled to root2d constraints during generation.",
                        ["shape"] = new JObject
                        {
                            ["frame"] = "Optional first path frame; defaults to 0.",
                            ["root_path"] = new JObject { ["path"] = "{track,index} from animation_analyze clips[].root_trajectory.path" }
                        }
                    }
                },
                ["rules"] = new JArray
                {
                    "At the same frame, fullbody supplies the base pose, root2d overrides RootTQ, and hand/foot effector channels override their matching protocol fields.",
                    "Use animation_analyze, then reference clips[].root_trajectory.path from root_path.",
                    "An explicit root2d at a frame overrides root_path at that frame."
                }
            };
        }

        public static string InstallServer(string argumentsJson = "{}")
        {
            return Execute(argumentsJson, _ =>
            {
                EnsureCanManageServer();
                string requestId = "install:" + Guid.NewGuid().ToString("N");
                lock (JobsLock)
                {
                    InstallTasks[requestId] = new JObject
                    {
                        ["status"] = "queued",
                        ["task_id"] = requestId,
                        ["progress"] = "0/0",
                        ["eta_seconds"] = 60.0,
                        ["message"] = "Server installation waiting to start."
                    };
                    while (InstallTasks.Count > MaxRememberedJobs)
                    {
                        InstallTasks.Remove(InstallTasks.Keys.First());
                    }
                }
                EditorApplication.delayCall += async () =>
                {
                    await RunInstallServerTaskAsync(requestId);
                };
                return Ok(new JObject
                {
                    ["request_id"] = requestId,
                    ["status"] = "accepted",
                    ["message"] = "Server installation accepted."
                });
            });
        }

        private static async Task RunInstallServerTaskAsync(string requestId)
        {
            try
            {
                UpdateInstallTask(requestId, "loading", 60.0, "Installing server runtime...");
                string runtimeRoot = KimodoBridgeServerTool.GetRuntimeRootPath();
                using (KimodoBridgeServerTool.EnterRuntimeMaintenanceScope())
                {
                    await KimodoBridgeService.Shared.StopAsync(CancellationToken.None);
                    if (!KimodoBridgeServerTool.RefreshRuntimeRoot())
                    {
                        throw new InvalidOperationException("Failed to incrementally install runtime root from package template.");
                    }
                }

                UpdateInstallTask(requestId, "loading", 60.0, "Starting QuickServer...");
                await KimodoBridgeService.Shared.WarmupAsync(null, CancellationToken.None);
                UpdateInstallTask(requestId, "done", 0.0, "Server installation complete.", runtimeRoot);
            }
            catch (Exception ex)
            {
                UpdateInstallTask(requestId, "error", 0.0, ex.Message);
            }
        }

        private static void UpdateInstallTask(string requestId, string status, double? eta, string message, string runtimeRoot = null)
        {
            lock (JobsLock)
            {
                if (!InstallTasks.TryGetValue(requestId, out JObject task)) return;
                task["status"] = status;
                task["eta_seconds"] = eta.HasValue ? (JToken)eta.Value : JValue.CreateNull();
                task["message"] = message ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(runtimeRoot))
                {
                    task["runtime_root"] = runtimeRoot;
                    task["runtime_version"] = KimodoServerRuntimeUtil.ReadQuickServerVersion(runtimeRoot);
                    task["install_mode"] = "incremental";
                    task["server_connected"] = KimodoBridgeService.Shared.IsConnected;
                }
            }
        }

        public static string GetGeneration(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                string requestValue = RequiredStringValue(arguments, "request_id");
                if (requestValue.StartsWith("install:", StringComparison.OrdinalIgnoreCase))
                {
                    lock (JobsLock)
                    {
                        if (InstallTasks.TryGetValue(requestValue, out JObject installStatus))
                        {
                            return Ok(new JObject(installStatus));
                        }
                    }
                    throw new InvalidOperationException($"Unknown or expired request_id '{requestValue}'.");
                }
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                if (!Guid.TryParse(requestValue, out Guid requestId))
                {
                    throw new InvalidOperationException("request_id is not a valid GUID or install task id.");
                }
                if (!TryGetJob(requestId, out JobRecord record))
                {
                    JObject persisted = LoadPersistedGenerationJob(session, requestId);
                    persisted["target_alive"] = false;
                    return Ok(persisted);
                }
                EnsureGenerationBelongsToSession(record, session);
                JObject status = BuildStatus(record);
                try
                {
                    JObject serverStatus = KimodoBridgeService.Shared
                        .GetStatusAsync(requestId.ToString("N"), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    string serverState = serverStatus?.Value<string>("status");
                    if (serverStatus != null && !string.IsNullOrWhiteSpace(serverState) &&
                        !string.Equals(serverState, "idle", StringComparison.OrdinalIgnoreCase))
                    {
                        status["request_id"] = requestValue;
                        // Keep the Unity job status as the public lifecycle source of truth.
                        // QuickServer's `done` only means the backend response is ready; the
                        // Unity-side asset write/bake may still be running.
                        status["task_id"] = serverStatus["task_id"]?.DeepClone() ?? requestValue;
                        status["progress"] = serverStatus["progress"]?.DeepClone() ?? "0/0";
                        status["eta_seconds"] = serverStatus["eta_seconds"]?.DeepClone() ?? JValue.CreateNull();
                        status["message"] = serverStatus["message"]?.DeepClone() ?? string.Empty;
                        status.Remove("stage");
                        status.Remove("error");
                        status.Remove("started_at_utc");
                        status.Remove("estimated_completion_utc");
                        status.Remove("progress_current");
                        status.Remove("progress_total");
                        status.Remove("progress_rate");
                    }
                }
                catch
                {
                    // Preserve the local completed result if the server has already exited.
                }
                status["target_alive"] = record.Target != null;
                return Ok(status);
            });
        }

        public static string CancelGeneration(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireCurrentTimelineSession();
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
                TimelineSessionRecord session = RequireCurrentTimelineSession();
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
                bool hasPathBeginAngle = arguments["path_begin_angle_degrees"] != null;
                float pathBeginAngleDegrees = hasPathBeginAngle
                    ? ReadFiniteFloat(arguments["path_begin_angle_degrees"], "path_begin_angle_degrees")
                    : 0f;
                bool hasPathEndAngle = arguments["path_end_angle_degrees"] != null;
                float pathEndAngleDegrees = hasPathEndAngle
                    ? ReadFiniteFloat(arguments["path_end_angle_degrees"], "path_end_angle_degrees")
                    : 0f;
                bool overridePathAngle = hasPathBeginAngle || hasPathEndAngle;
                // Directional language is part of the generation contract,
                // not merely prompt decoration. When callers omit explicit
                // PathAngle values, resolve the character's current planar
                // yaw and use it for both path endpoints. Explicit values
                // always take precedence.
                if (!overridePathAngle && TryResolvePromptPathAngles(
                        prompt,
                        character.Root,
                        out float inferredPathAngle))
                {
                    pathBeginAngleDegrees = inferredPathAngle;
                    pathEndAngleDegrees = inferredPathAngle;
                    overridePathAngle = true;
                }
                bool overrideHeading = arguments["override_heading_degrees"] != null;
                float headingDegrees = overrideHeading
                    ? ReadFiniteFloat(arguments["override_heading_degrees"], "override_heading_degrees")
                    : 0f;
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
                playableClip.overridePathAngle = overridePathAngle;
                playableClip.pathBeginAngleDegrees = pathBeginAngleDegrees;
                playableClip.pathEndAngleDegrees = pathEndAngleDegrees;
                playableClip.overrideHeading = overrideHeading;
                playableClip.headingDegrees = headingDegrees;
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
                        string previousTaskId = KimodoBridgeService.GenerationTaskIdContext.Value;
                        KimodoBridgeService.GenerationTaskIdContext.Value = generationSession.RequestId.ToString("N");
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
                            KimodoBridgeService.GenerationTaskIdContext.Value = previousTaskId;
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
                if (overridePathAngle)
                {
                    startedResponse["path_begin_angle_degrees"] = pathBeginAngleDegrees;
                    startedResponse["path_end_angle_degrees"] = pathEndAngleDegrees;
                }
                if (overrideHeading)
                {
                    startedResponse["override_heading_degrees"] = headingDegrees;
                }
                if (loopWarning != null)
                {
                    startedResponse["warnings"] = new JArray(loopWarning);
                    startedResponse["loop_fallback"] = loopFallback;
                }
                return Started(generation, startedResponse);
            });
        }

        private static bool TryResolvePromptPathAngles(
            string prompt,
            GameObject characterRoot,
            out float yawDegrees)
        {
            yawDegrees = 0f;
            string text = (prompt ?? string.Empty).Trim().ToLowerInvariant();
            if (text.Length == 0 || characterRoot == null)
            {
                return false;
            }
            bool hasMotion = text.Contains("walk") || text.Contains("run") ||
                text.Contains("move") || text.Contains("step") || text.Contains("jog");
            bool hasDirection = text.Contains("forward") || text.Contains("backward") ||
                text.Contains("backwards") || text.Contains("straight") ||
                text.Contains("left") || text.Contains("right");
            if (!hasMotion || !hasDirection)
            {
                return false;
            }

            float offset = 0f;
            if (text.Contains("backward") || text.Contains("backwards")) offset = 180f;
            else if (text.Contains("left")) offset = -90f;
            else if (text.Contains("right")) offset = 90f;
            yawDegrees = Mathf.Repeat(characterRoot.transform.eulerAngles.y + offset + 180f, 360f) - 180f;
            return true;
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

    }
}
