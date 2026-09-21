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
        public const string AnimationAnalyzeCommand = "animation_analyze";
        public const string TransformCaptureCommand = "transform_capture";
        public const string RetargetAnimationCommand = "kimodo_retarget_animation";
        public const string PoseGetCommand = "pose_get";
        public const string PoseSetCommand = "pose_set";
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
                    CommandDefinition(AnimationAnalyzeCommand,
                        "Analyze one immutable animation clip and render unified graph-space picture evidence. Select standard tile types explicitly and choose composite, individual tiles, or both outputs. Results include root_trajectory, endpoint_pose_comparison, motion_profile, foot_contacts, phase_track, and evidence paths. Completed Clips are never modified.",
                        Properties(
                            RequiredAnalysisClips(),
                            new PropertyDefinition("picture", new JObject
                            {
                                ["type"] = "object",
                                ["description"] = "Unified graph-space picture request with tiles and output mode."
                            }, false),
                            new PropertyDefinition("resolution", new JObject
                            {
                                ["type"] = "integer",
                                ["minimum"] = 64,
                                ["maximum"] = 4096,
                                ["description"] = "Final picture tile resolution in pixels; accepts 64 through 4096. Rendering uses a 2x supersample and downsamples to this size. Defaults to 1920."
                            }, false))),
                    CommandDefinition(TransformCaptureCommand,
                        "Capture the current scene or an External Pose returned by pose_get together with scene objects, in world space. Returns a four-view image and evaluated joint positions; does not change scene poses.",
                        Properties(
                            Optional("character_path", "string", "Active-scene character hierarchy path; required unless pose is supplied."),
                            new PropertyDefinition("pose", PoseReferenceSchema(), false),
                            new PropertyDefinition("transforms", new JObject
                            {
                                ["type"] = "array", ["minItems"] = 1,
                                ["items"] = new JObject { ["type"] = "string" },
                                ["description"] = "Scene hierarchy paths or character-relative paths. With pose, use @pose for the evaluated character or @pose/<relative bone path>. Include environment paths to frame them together."
                            }, true),
                            Optional("resolution", "integer", "Per-view pixel size, 64 through 4096; defaults to 1024. To capture an animation frame, first call pose_get and pass its pose reference."))),
                    CommandDefinition(RetargetAnimationCommand,
                        "Retarget one loaded animation to another current scene context character and append the result.",
                        Properties(
                            Required("source_character", "string", "Safe source character name in the resolved scene context."),
                            Required("animation", "string", "Safe source animation name."),
                            Required("target_character", "string", "Safe target character name in the resolved scene context."),
                            OptionalOutput())),
                    CommandDefinition(GenerateAnimationCommand,
                        "Start asynchronous generation for a character in the current scene context. The accepted request is recorded in the generation result and must be polled by request_id.",
                        Properties(
                            Required("character", "string", "Safe character name in the current scene context."),
                            Required("prompt", "string", "Motion prompt."),
                            Optional("duration_frames", "integer", "Duration in 60 FPS frames; defaults to 300."),
                            OptionalLoop(),
                            OptionalPath(),
                            OptionalGeneration(),
                            OptionalOutput(),
                            Optional("analysis_option", "object", "Optional analysis object for the phase-track analyzer. Legacy uniform keyframe-count controls are removed; Humanoid output always uses phase_track_version and continuous phase_track intervals."),
                            OptionalConstraints("constraints", "Point, root_path, and inout boundary constraints. In/Out sources are explicit Clips sampled in C#; command frames use 60 FPS."))),
                    CommandDefinition(PoseGetCommand,
                        "Sample one current-animation clip frame into a new External Pose slot. Returns the only reusable pose identity: {track,index}.",
                        Properties(
                            RequiredPoseSource("source"),
                            Optional("full_data", "boolean", "Return all 49 muscles and TQ channels; defaults to false."))),
                    CommandDefinition(PoseSetCommand,
                        "Modify an External Pose slot. Supply root, muscles, effector, or any combination.",
                        PoseSetSchema()),
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
                        "Commands resolve scene and asset references directly; no public Session lifecycle call is required.",
                        "Pass returned object references to later commands; request_id polls an installation or generation task.",
                        "Installation and generation are asynchronous: save request_id and poll kimodo_get_generation. Install terminal states are done or error; generation terminal states are completed, failed, or canceled.",
                        "Generation and analysis return independent asset and evidence references."
                    },
                    ["routing"] = new JArray
                    {
                        Route("discover schema or models", HelpCommand),
                        Route("install or refresh server", InstallServerCommand, "then " + GetGenerationCommand),
                        Route("generate motion", GenerateAnimationCommand, "then " + GetGenerationCommand),
                        Route("analyze and render motion", AnimationAnalyzeCommand, "returns one composite picture and self-describing tiles"),
                        Route("materialize or edit a pose", PoseGetCommand, "then pose_set / pose_set_root_transform / pose_set_muscle"),
                        Route("obtain a reusable root trajectory", AnimationAnalyzeCommand, "then reference root_trajectory.path from a generation root_path constraint"),
                    },
                    ["handles"] = new JObject
                    {
                        ["request_id"] = "Returned by kimodo_install_server or kimodo_generate_animation. Pass either to kimodo_get_generation; only generation request ids can be canceled.",
                        ["pictures.image_path"] = "Read the composite PNG returned by animation_analyze.",
                        ["pose"] = "A {track,index} reference returned by pose_get or a pose editing command.",
                        ["path"] = "A project-relative asset or analysis path returned by a command."
                    },
                    ["workflow"] = new JArray
                    {
                        new JObject { ["command"] = InstallServerCommand, ["arguments"] = new JObject(), ["save"] = "request_id", ["before"] = "all other Commands" },
                        new JObject { ["command"] = GetGenerationCommand, ["arguments"] = new JObject { ["request_id"] = "<install_request_id>" }, ["repeat_until"] = "status is done or error" },
                        new JObject { ["command"] = GenerateAnimationCommand, ["arguments"] = new JObject { ["character"] = "<character>", ["prompt"] = "stand still and breathe naturally", ["duration_frames"] = 60 }, ["save"] = "request_id" },
                        new JObject { ["command"] = GetGenerationCommand, ["arguments"] = new JObject { ["request_id"] = "<request_id>" }, ["repeat_until"] = "status is completed, failed, or canceled" },
                        new JObject { ["command"] = AnimationAnalyzeCommand, ["arguments"] = new JObject { ["clips"] = new JArray(new JObject { ["character"] = "<character>", ["clip"] = "<completed animation>" }), ["picture"] = new JObject { ["output"] = "both" } }, ["save"] = "pictures.image_path" },
                    },
                    ["commands"] = new JArray(all["tools"].Children<JObject>().Select(item => new JObject
                    {
                        ["name"] = item.Value<string>("name"),
                        ["description"] = item.Value<string>("description"),
                        ["required"] = item["inputSchema"]?["required"]?.DeepClone() ?? new JArray(),
                        ["examples"] = item["examples"]?.DeepClone() ?? new JArray()
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
                        ["type"] = "inout",
                        ["description"] = "Explicit source Clip windows sampled in C# into FullBody constraints. Outside adds and crops context; inside retains constrained windows within the output. Source times include Timeline offsets, clipIn, speed and blends.",
                        ["shape"] = InOutConstraintSchema(),
                        ["examples"] = BuildCommandExamples(GenerateAnimationCommand)
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
                    "An explicit root2d at a frame overrides root_path at that frame.",
                    "At most one standalone inout entry; in/out each require an explicit completed source Clip. Source character defaults to the generation character and must be the same character.",
                    "In/Out window_frames and source.frame use 60 FPS. Windows round up to model frames; sample_count must fit distinct model frames. One sample uses the seam; multiple samples include both endpoints.",
                    "In/Out rejects short sources, overlapping inside windows, conflicting point samples, unsupported ARDY models, loop generation, and runtime duration above the model limit. It does not silently clamp or downgrade.",
                    "inout_sampling is returned on acceptance and polling, with source times, output/runtime model frames, context counts and the exclusive crop range. Padding/cropping is automatic; the final requested duration is preserved."
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
                JObject pathOptions = arguments["path"] as JObject;
                JObject generationOptions = arguments["generation"] as JObject;
                JObject outputOptions = arguments["output"] as JObject;
                string outputMode = ParseOutputMode(outputOptions?.Value<string>("mode"));
                string requestedModel = generationOptions?.Value<string>("model")?.Trim();
                string requestedTextEncoder = generationOptions?.Value<string>("text_encoder")?.Trim();
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
                JObject loopObject = arguments["loop"] as JObject;
                bool loopRequested = loopObject != null
                    ? loopObject.Value<bool?>("enabled") ?? true
                    : arguments.Value<bool?>("loop") ??
                        prompt.IndexOf("loop", StringComparison.OrdinalIgnoreCase) >= 0;
                JObject lockPosition = loopObject?["lock_pos"] as JObject;
                JObject lockRotation = loopObject?["lock_rot"] as JObject;
                bool loopLockPositionX = lockPosition?.Value<bool?>("x") ?? false;
                bool loopLockPositionY = lockPosition?.Value<bool?>("y") ?? true;
                bool loopLockPositionZ = lockPosition?.Value<bool?>("z") ?? false;
                bool loopLockRotationX = lockRotation?.Value<bool?>("x") ?? true;
                bool loopLockRotationY = lockRotation?.Value<bool?>("y") ?? false;
                bool loopLockRotationZ = lockRotation?.Value<bool?>("z") ?? true;
                bool hasPathBeginAngle = pathOptions?["start_angle"] != null;
                float pathBeginAngleDegrees = hasPathBeginAngle
                    ? ReadFiniteFloat(pathOptions["start_angle"], "path.start_angle")
                    : 0f;
                bool hasPathEndAngle = pathOptions?["end_angle"] != null;
                float pathEndAngleDegrees = hasPathEndAngle
                    ? ReadFiniteFloat(pathOptions["end_angle"], "path.end_angle")
                    : 0f;
                bool overridePathAngle = hasPathBeginAngle || hasPathEndAngle;
                bool overridePathDistance = pathOptions?["distance"] != null;
                float pathDistance = overridePathDistance
                    ? ReadFiniteFloat(pathOptions["distance"], "path.distance")
                    : 0f;
                if (overridePathDistance && pathDistance < 0f)
                {
                    throw new InvalidOperationException("path.distance must be non-negative.");
                }
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
                if (overridePathDistance && !overridePathAngle)
                {
                    throw new InvalidOperationException("path.distance requires a path angle override.");
                }
                bool overrideHeading = pathOptions?["heading"] != null;
                float headingDegrees = overrideHeading
                    ? ReadFiniteFloat(pathOptions["heading"], "path.heading")
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
                int seed = generationOptions?.Value<int?>("seed") ?? (Guid.NewGuid().GetHashCode() & int.MaxValue);
                int steps = generationOptions?.Value<int?>("diffusion_steps") ?? ResolveDiffusionSteps(arguments, modelName, modelConfiguration);
                string outputFolder = KimodoEditorOutputPathUtility.NormalizeOutputFolder(outputOptions?.Value<string>("folder"));
                string requestedAnimationName = string.IsNullOrWhiteSpace(outputOptions?.Value<string>("name"))
                    ? prompt
                    : outputOptions.Value<string>("name").Trim();
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
                KimodoExternalConstraintRequest inOutConstraints = BuildCommandInOutConstraints(
                    arguments, trace, modelName, frameCount, frameRate, loopRequested || loopFallback, poseConstraints);
                KimodoPlayableClip playableClip = CreateGenerationPlayableClip(trace, requestedAnimationName, inOutConstraints != null);
                playableClip.bridgeModelName = modelName;
                playableClip.textEncoderMode = textEncoderMode;
                playableClip.motionPrompt = prompt;
                playableClip.generationFrames = frameCount;
                playableClip.diffusionSteps = steps;
                playableClip.randomSeed = false;
                playableClip.seed = seed;
                playableClip.generateLoop = loopRequested;
                playableClip.loopLockPositionX = loopLockPositionX;
                playableClip.loopLockPositionY = loopLockPositionY;
                playableClip.loopLockPositionZ = loopLockPositionZ;
                playableClip.loopLockRotationX = loopLockRotationX;
                playableClip.loopLockRotationY = loopLockRotationY;
                playableClip.loopLockRotationZ = loopLockRotationZ;
                playableClip.overridePathAngle = overridePathAngle;
                playableClip.pathBeginAngleDegrees = pathBeginAngleDegrees;
                playableClip.pathEndAngleDegrees = pathEndAngleDegrees;
                playableClip.overridePathDistance = overridePathDistance;
                playableClip.pathDistance = pathDistance;
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
                                inOutConstraints,
                                character.Target,
                                generationSession,
                                token);
                        }
                        finally
                        {
                            KimodoBridgeService.GenerationTaskIdContext.Value = previousTaskId;
                            // Temporary sampling context is owned by the command runtime.
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
                    startedResponse["start_frame"] = Mathf.RoundToInt((float)(trace.StartSeconds * SessionFrameRate));
                    startedResponse["duration_frames"] = Mathf.RoundToInt((float)(trace.DurationSeconds * SessionFrameRate));
                    if (trace.InOutSampling != null) startedResponse["inout_sampling"] = trace.InOutSampling.DeepClone();
                }
                if (loopRequested)
                {
                    startedResponse["loop"] = true;
                    startedResponse["loop_options"] = new JObject
                    {
                        ["enabled"] = true,
                        ["lock_pos"] = new JObject
                        {
                            ["x"] = loopLockPositionX,
                            ["y"] = loopLockPositionY,
                            ["z"] = loopLockPositionZ
                        },
                        ["lock_rot"] = new JObject
                        {
                            ["x"] = loopLockRotationX,
                            ["y"] = loopLockRotationY,
                            ["z"] = loopLockRotationZ
                        }
                    };
                    startedResponse["loop_source_duration_frames"] = durationFrames;
                    startedResponse["loop_extended_duration_frames"] = durationFrames * 2;
                }
                if (overridePathAngle)
                {
                    startedResponse["path"] = new JObject
                    {
                        ["start_angle"] = pathBeginAngleDegrees,
                        ["end_angle"] = pathEndAngleDegrees
                    };
                    if (overridePathDistance) ((JObject)startedResponse["path"])["distance"] = pathDistance;
                    if (overrideHeading) ((JObject)startedResponse["path"])["heading"] = headingDegrees;
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
            KimodoExternalConstraintRequest inOutConstraints,
            UnityEngine.Object target,
            KimodoEditorGenerationJobSession session,
            CancellationToken token)
        {
            KimodoEditorGenerationResult result = await KimodoPlayableClipGenerationExecutionService.GenerateAndFinalizeAsync(
                playableClip,
                externalConstraint: inOutConstraints,
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
