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
        public const string HelpCommand = "kimodo.help";
        public const string SessionContextCommand = "session.context";
        public const string InstallServerCommand = "server.install";
        public const string GenerateAnimationCommand = "animation.generate";
        public const string AnimationAnalysisCommand = "animation.analysis";
        public const string ClipInspectCommand = "clip.inspect";
        public const string ClipSettingsSetCommand = "clip.settings.set";
        public const string PoseFromVideoCommand = "pose.fromvideo";
        public const string PoseFromPictureCommand = "pose.frompicture";
        public const string PoseAnalysisCommand = "pose.analysis";
        public const string RetargetAnimationCommand = "animation.retarget";
        public const string PoseGetCommand = "pose.get";
        public const string PoseSetCommand = "pose.set";
        public const string GetGenerationCommand = "generation.get";
        public const string CancelGenerationCommand = "generation.cancel";
        public const string SessionClearCommand = "session.clear";
        public const string SessionHistoryCommand = "session.history";
        internal const string HelpAssetPath = "Packages/com.unity.kimodo_unity_motion_tools/Command/help.json";

        private const int MaxRememberedJobs = 128;
        private const int CommandBridgeQueryTimeoutMs = 5000;
        private static readonly Dictionary<Guid, JobRecord> Jobs = new Dictionary<Guid, JobRecord>();
        private static readonly Dictionary<string, JObject> InstallTasks = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, JObject> MocapAnalysisTasks = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
        private static readonly object JobsLock = new object();
        private static readonly Lazy<CommandCatalog> Commands = new Lazy<CommandCatalog>(BuildCommandCatalog, LazyThreadSafetyMode.ExecutionAndPublication);

        public static string GetCommandDefinitionsJson() => BuildCommandDefinitionsJson();

        private static JObject SceneCameraSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["description"] = "SceneView camera controls shared by pose.analysis and animation.analysis.",
                ["properties"] = new JObject
                {
                    ["direction"] = new JObject
                    {
                        ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3,
                        ["items"] = new JObject { ["type"] = "number" },
                        ["description"] = "Optional camera-to-target direction [x,y,z]. animation.analysis keeps each tile's direction when omitted."
                    },
                    ["orthographic"] = new JObject { ["type"] = "boolean", ["default"] = true },
                    ["padding"] = new JObject { ["type"] = "number", ["minimum"] = 0.01, ["default"] = 1.1 },
                    ["near_clip"] = new JObject { ["type"] = "number", ["minimum"] = 0.001 },
                    ["far_clip"] = new JObject { ["type"] = "number", ["minimum"] = 0.01 },
                    ["resolution"] = new JObject
                    {
                        ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 2,
                        ["items"] = new JObject { ["type"] = "integer", ["minimum"] = 64, ["maximum"] = 4096 },
                        ["description"] = "SceneView render size [width,height]. Defaults to the current SceneView camera size."
                    },
                    ["scene_lighting"] = new JObject { ["type"] = "boolean", ["description"] = "Set SceneView scene lighting when explicitly supplied; otherwise keep its current setting." },
                    ["show_skybox"] = new JObject { ["type"] = "boolean", ["description"] = "Set SceneView skybox visibility when explicitly supplied; otherwise keep its current setting." },
                    ["show_gizmos"] = new JObject { ["type"] = "boolean", ["description"] = "Set SceneView gizmo visibility when explicitly supplied; otherwise keep its current setting." },
                    ["show_grid"] = new JObject { ["type"] = "boolean", ["description"] = "Set SceneView grid visibility when explicitly supplied; otherwise keep its current setting." },
                    ["scope"] = new JObject
                    {
                        ["type"] = "string", ["enum"] = new JArray("targets", "scene"), ["default"] = "targets",
                        ["description"] = "Render the requested objects and optionally include the active scene around the sampled pose."
                    }
                }
            };
        }

        private static JObject PoseAnalysisConstraintsSchema()
        {
            JObject schema = (JObject)OptionalConstraints(
                "constraints",
                "Point constraints to preview through pose.analysis before submitting the same payload to animation.generate.")
                .Schema.DeepClone();
            schema["items"] = schema["items"]?["oneOf"]?.First?.DeepClone();
            schema["minItems"] = 1;
            return schema;
        }

        private static string BuildCommandDefinitionsJson()
        {
            return new JObject
            {
                ["tools"] = new JArray
                {
                    CommandDefinition(HelpCommand,
                        "Return command schemas, shared usage guidance, detailed documentation for one command, or currently viable model configurations.",
                        Properties(
                            Optional("command", "string", "Command name whose full manual entry should be returned."),
                            new PropertyDefinition("compact", new JObject
                            {
                                ["type"] = "boolean",
                                ["default"] = false,
                                ["description"] = "For section=models, include model, text_encoder_model, and default for each configuration."
                            }, false),
                            new PropertyDefinition("section", new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray("commands", "models", "constraints"),
                                ["default"] = "commands",
                                ["description"] = "Select the command overview, currently viable models, or generation constraint reference. Set command for one detailed command entry."
                            }, false))),
                    CommandDefinition(SessionContextCommand,
                        "Read a compact Session summary built from the active scene and Timeline. Request details by tag when needed.",
                        Properties(
                            Optional("timeline_path", "string", "Open an Assets/... or Packages/... Timeline asset and make it the current Session Timeline."),
                            new PropertyDefinition("details", new JObject
                            {
                                ["type"] = "array",
                                ["uniqueItems"] = true,
                                ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray("characters", "clips", "timeline", "tasks") },
                                ["description"] = "Optional detail groups to construct: characters, clips, timeline, and tasks."
                            }, false))),
                    CommandDefinition(InstallServerCommand,
                        "Start or refresh the project-local QuickServer runtime when generation needs it. Returns a request handle and status for progress tracking.",
                        Properties(WaitSecondsProperty(10.0, allowAboveLimit: true))),
                    CommandDefinition(AnimationAnalysisCommand,
                        "Analyze one scene character's loaded animation Clip and render structured motion measurements with a 20-tile SceneView image. An external Clip is added to that character's Kimodo Timeline for analysis; the source asset remains unchanged.",
                        Properties(
                            RequiredAnalysisClips(),
                            new PropertyDefinition("resolution", new JObject
                            {
                                ["type"] = "integer",
                                ["minimum"] = 64,
                                ["maximum"] = 4096,
                                ["default"] = 1920,
                                ["description"] = "Final 16:9 composite width in pixels; accepts 64 through 4096. Defaults to 1920."
                            }, false),
                            new PropertyDefinition("camera", SceneCameraSchema(), false))),
                    CommandDefinition(ClipInspectCommand,
                        "Inspect one AnimationClip through the shared Clip Inspector mapping.",
                        Properties(
                            Required("clip", "string", "AnimationClip name or an Assets/Packages asset path."))),
                    CommandDefinition(ClipSettingsSetCommand,
                        "Write supported AnimationClip settings and return the values read back from Unity.",
                        Properties(
                            Required("clip", "string", "AnimationClip name or an Assets/Packages asset path."),
                            new PropertyDefinition("settings", new JObject
                            {
                                ["type"] = "object",
                                ["additionalProperties"] = false,
                                ["properties"] = new JObject
                                {
                                    ["loop_time"] = new JObject { ["type"] = "boolean" },
                                    ["keep_original_position_xz"] = new JObject { ["type"] = "boolean" },
                                    ["root_motion_enabled"] = new JObject { ["type"] = "boolean" }
                                },
                                ["minProperties"] = 1
                            }, true))),
                    CommandDefinition(PoseAnalysisCommand,
                        "Preview one or more animation point constraints as Humanoid poses through the real SceneView camera and return measured joint positions.",
                        Properties(
                            Required("character", "string", "Scene character that will receive the constraints in animation.generate."),
                            new PropertyDefinition("constraints", PoseAnalysisConstraintsSchema(), true),
                            new PropertyDefinition("combine", new JObject
                            {
                                ["type"] = "boolean",
                                ["default"] = false,
                                ["description"] = "Render all constrained poses together in one opaque SceneView image; defaults to false, which returns one image per constraint."
                            }, false),
                            new PropertyDefinition("transforms", new JObject
                            {
                                ["type"] = "array", ["minItems"] = 1,
                                ["items"] = new JObject { ["type"] = "string" },
                                ["description"] = "Scene hierarchy paths to frame and render. Defaults to @pose; use @pose/<relative bone path> for part of the sampled character."
                            }, false),
                            new PropertyDefinition("joints", new JObject
                            {
                                ["type"] = "array", ["uniqueItems"] = true,
                                ["items"] = new JObject
                                {
                                    ["type"] = "string",
                                    ["enum"] = new JArray("Hips", "Head", "LeftHand", "RightHand", "LeftFoot", "RightFoot",
                                        "LeftUpperArm", "RightUpperArm", "LeftLowerArm", "RightLowerArm",
                                        "LeftUpperLeg", "RightUpperLeg", "LeftLowerLeg", "RightLowerLeg")
                                },
                                ["description"] = "Humanoid joints to measure. Defaults to hips, head, hands, and feet; effector-constrained joints are included automatically."
                            }, false),
                            new PropertyDefinition("camera", SceneCameraSchema(), false))),
                    CommandDefinition(PoseFromVideoCommand,
                        "Estimate an ordered SMPL-X pose sequence from a local video. Returns raw poses and matching FullBody constraints for downstream commands.",
                        Properties(
                            Required("video_path", "string", "Absolute path to a local video readable by QuickServer."),
                            WaitSecondsProperty(10.0, allowAboveLimit: true),
                            new PropertyDefinition("person", new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray("left", "right"),
                                ["description"] = "Select one performer by screen side in a two-person video."
                            }, false))),
                    CommandDefinition(PoseFromPictureCommand,
                        "Estimate one SMPL-X pose from a local still image. Returns raw pose data and a matching FullBody constraint for downstream commands.",
                        Properties(
                            Required("image_path", "string", "Absolute path to a local PNG or JPEG readable by QuickServer."),
                            WaitSecondsProperty(10.0, allowAboveLimit: true))),
                    CommandDefinition(RetargetAnimationCommand,
                        "Retarget one loaded animation to another current scene context character and append the result.",
                        Properties(
                            Required("source_character", "string", "Source scene character name or object_ref from session.context."),
                            Required("animation", "string", "Safe source animation name."),
                            Required("target_character", "string", "Target scene character name or object_ref from session.context."),
                            OptionalOutput())),
                    CommandDefinition(GenerateAnimationCommand,
                        "Generate one animation Clip for a scene character. Use prompt for semantic motion or raw_instruction to replay an exact captured server payload; use structured constraint and trajectory fields for explicit requirements. Returns a request handle and preflight diagnostics; generation.get returns the completed Clip and analysis details.",
                        Properties(
                            Required("character", "string", "Scene character name or object_ref from session.context."),
                            Optional("prompt", "string", "Short semantic description of the current Clip's action, aim for 25 words or fewer; use raw_instruction to replay a captured payload."),
                            new PropertyDefinition("raw_instruction", new JObject
                            {
                                ["type"] = "object",
                                ["additionalProperties"] = false,
                                ["properties"] = new JObject
                                {
                                    ["role"] = new JObject { ["type"] = "string", ["enum"] = new JArray("generate") },
                                    ["text"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Exact JSON returned by generation.get(include_request_json=true)." }
                                },
                                ["required"] = new JArray("role", "text")
                            }, false),
                            new PropertyDefinition("duration_frames", new JObject
                            {
                                ["type"] = "integer",
                                ["minimum"] = 1,
                                ["default"] = 300,
                                ["description"] = "Duration in 60 FPS frames; defaults to 300 frames (5 seconds)."
                            }, false),
                            OptionalLoop(),
                            OptionalPath(),
                            OptionalGeneration(),
                            OptionalOutput(),
                            new PropertyDefinition("analyze", new JObject
                            {
                                ["type"] = "boolean",
                                ["default"] = true,
                                ["description"] = "Run the full animation.analysis command after generation; defaults to true."
                            }, false),
                            WaitSecondsProperty(10.0, allowAboveLimit: true),
                            OptionalConstraints("constraints", "Point constraints use Root2D for root, FullBody for pose with optional root/effector, or Effector for root with effector and no pose. Transforms accept World position and/or Euler rotation in degrees; omitted channels retain sampled values. Pose/effector preflight runs when both fields appear in one point. root_path and inout are separate forms; use unique command frames at 60 FPS."))),
                    CommandDefinition(PoseGetCommand,
                        "Sample one absolute Timeline-global frame at 60 FPS, or select mode scene_pose / skeleton_rest_pose to read the original scene Animator or Avatar rest pose. Every response includes character_root, the bound scene GameObject root Transform in world space; data.root is the sampled Pose root channel. Every call creates an External Pose slot and returns {track,index}.",
                        Properties(
                            RequiredPoseSource("source"),
                            Optional("full_data", "boolean", "Return all 49 muscles and TQ channels; defaults to false."))),
                    CommandDefinition(PoseSetCommand,
                        "Update an existing External Pose slot by supplying root, muscles, effector, or any combination.",
                        PoseSetSchema()),
                    CommandDefinition(GetGenerationCommand,
                        "Read progress and results for installation, generation, and Mocap analysis. Completed generation returns Clip metadata and a post-analysis path; completed Mocap analysis returns poses and coordinate metadata. include_request_json returns the submitted generation payload.",
                        Properties(
                            Required("request_id", "string", "Request id returned by server.install, animation.generate, pose.fromvideo, or pose.frompicture."),
                            Optional("include_request_json", "boolean", "Include the exact JSON payload sent to QuickServer; defaults to false."),
                            WaitSecondsProperty(10.0, allowAboveLimit: false))),
                    CommandDefinition(CancelGenerationCommand,
                        "Request cancellation of an active animation generation and return its compact lifecycle result. Use generation.get for detailed progress and diagnostics.",
                        Properties(
                            Required("request_id", "string", "Generation request id returned by animation.generate."),
                            Optional("reason", "string", "Optional cancellation reason."))),
                    CommandDefinition(SessionClearCommand,
                        "Clear the active scene context objects. The Timeline, Session data, and animation assets remain available; the next command creates a fresh scene context when needed.",
                        Properties()),
                    CommandDefinition(SessionHistoryCommand,
                        "Read append-only command history for the active Session, a specified Session, or the global Session directory.",
                        Properties(
                            Optional("session_id", "string", "Read one Session by its Timeline asset GUID; omitted uses the current Session."),
                            new PropertyDefinition("scope", new JObject
                            {
                                ["type"] = "string", ["enum"] = new JArray("session", "global"),
                                ["description"] = "Defaults to session; set global to list all created Sessions, including Sessions with no operations."
                            }, false),
                            new PropertyDefinition("historybegin", new JObject { ["type"] = "integer", ["minimum"] = 1 }, false),
                            new PropertyDefinition("historyend", new JObject { ["type"] = "integer", ["minimum"] = 1 }, false))),
                }
            }.ToString(Formatting.None);
        }

        public static string Invoke(string toolName, string argumentsJson = "{}")
        {
            string commandName = toolName?.Trim();
            if (Commands.Value.TryGet(commandName, out CommandRegistration command))
            {
                string response = command.Handler(argumentsJson);
                if (commandName == GenerateAnimationCommand || commandName == RetargetAnimationCommand || commandName == AnimationAnalysisCommand)
                {
                    RecordHistorySafely(commandName, argumentsJson, response);
                }
                return response;
            }
            return Error("unknown_command", $"Unknown Kimodo command '{toolName ?? string.Empty}'.");
        }

        internal static string InvokeAnimationApi(string commandName, string argumentsJson, Func<string, string> handler)
        {
            string response = handler(argumentsJson);
            RecordHistorySafely(commandName, argumentsJson, response);
            return response;
        }

        private static void RecordHistorySafely(string commandName, string argumentsJson, string response)
        {
            try
            {
                RecordCommandHistory(commandName, ParseCommandArguments(argumentsJson), JObject.Parse(response));
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Kimodo][SessionHistory] Failed to record {commandName}: {exception}");
            }
        }

        /// <summary>
        /// Async dispatcher for hosts that can await command completion. Submission commands
        /// wait cooperatively on EditorApplication.update and return a terminal result when it
        /// arrives within the requested window; otherwise they return the live request handle.
        /// The legacy synchronous Invoke entry remains immediate for existing editor scripts.
        /// </summary>
        public static async Task<string> InvokeAsync(string toolName, string argumentsJson = "{}")
        {
            string initial = Invoke(toolName, argumentsJson);
            return await AwaitCommandResultAsync(toolName, argumentsJson, initial);
        }

        internal static async Task<string> AwaitCommandResultAsync(
            string toolName,
            string argumentsJson,
            string initial)
        {
            if (!IsWaitableCommand(toolName)) return initial;
            JObject accepted;
            try
            {
                accepted = JObject.Parse(initial);
            }
            catch
            {
                return initial;
            }
            string requestId = accepted.Value<string>("request_id");
            if (string.IsNullOrWhiteSpace(requestId) || IsTerminalStatus(accepted.Value<string>("status")))
                return initial;

            double waitSeconds = ReadWaitSeconds(argumentsJson, toolName);
            if (waitSeconds <= 0d) return initial;
            JObject latest = await WaitForCommandStatusAsync(requestId, Math.Max(0d, waitSeconds));
            return latest == null ? initial : latest.ToString(Formatting.None);
        }

        public static Task<string> GenerateAnimationAssetAsync(string argumentsJson)
        {
            string initial = GenerateAnimationAsset(argumentsJson);
            return AwaitCommandResultAsync(GenerateAnimationCommand, argumentsJson, initial);
        }

        public static Task<string> InstallServerAsync(string argumentsJson = "{}")
        {
            string initial = InstallServer(argumentsJson);
            return AwaitCommandResultAsync(InstallServerCommand, argumentsJson, initial);
        }

        public static Task<string> GetGenerationAsync(string argumentsJson)
        {
            string initial = GetGeneration(argumentsJson);
            return AwaitCommandResultAsync(GetGenerationCommand, argumentsJson, initial);
        }

        private static bool IsWaitableCommand(string toolName)
        {
            return string.Equals(toolName, InstallServerCommand, StringComparison.Ordinal) ||
                string.Equals(toolName, GenerateAnimationCommand, StringComparison.Ordinal) ||
                string.Equals(toolName, PoseFromVideoCommand, StringComparison.Ordinal) ||
                string.Equals(toolName, PoseFromPictureCommand, StringComparison.Ordinal) ||
                string.Equals(toolName, GetGenerationCommand, StringComparison.Ordinal);
        }

        private static bool IsTerminalStatus(string status)
        {
            return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);
        }

        private static double ReadWaitSeconds(string argumentsJson, string commandName)
        {
            JObject arguments = ParseCommandArguments(argumentsJson);
            double defaultSeconds = 10d;
            double requested = arguments.Value<double?>("wait_seconds") ?? defaultSeconds;
            return commandName == GetGenerationCommand ? Math.Min(10d, Math.Max(0d, requested)) : Math.Max(0d, requested);
        }

        private static Task<JObject> WaitForCommandStatusAsync(string requestId, double timeoutSeconds)
        {
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            double startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.CallbackFunction update = null;
            update = () =>
            {
                JObject status = null;
                try
                {
                    status = JObject.Parse(GetGeneration(new JObject
                    {
                        ["request_id"] = requestId,
                        // Presence of this field selects the local-only status path and avoids
                        // synchronous backend enrichment from the cooperative wait loop.
                        ["wait_seconds"] = 0d
                    }));
                }
                catch (Exception ex)
                {
                    status = new JObject
                    {
                        ["ok"] = false,
                        ["error"] = new JObject
                        {
                            ["code"] = "status_unavailable",
                            ["message"] = ex.Message
                        }
                    };
                }

                bool finished = IsTerminalStatus(status.Value<string>("status")) ||
                    EditorApplication.timeSinceStartup - startedAt >= timeoutSeconds;
                if (!finished) return;
                double waitedSeconds = Math.Max(0d, EditorApplication.timeSinceStartup - startedAt);
                status["waited_seconds"] = waitedSeconds;
                if (!IsTerminalStatus(status.Value<string>("status")))
                    status["poll_after_seconds"] = Math.Min(2d, Math.Max(0.25d, timeoutSeconds - waitedSeconds));
                EditorApplication.update -= update;
                completion.TrySetResult(status);
            };
            EditorApplication.update += update;
            return completion.Task;
        }

        public static string ListModels(string argumentsJson = "{}") => Execute(argumentsJson, ListModels);

        private static string ListModels(JObject arguments)
        {
            return ListModels(arguments, KimodoBridgeService.Shared);
        }

        internal static string ListModels(JObject arguments, IKimodoModelConfigurationTransport transport)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            EnsureCanManageServer();
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            JObject response;
            using (var timeout = new CancellationTokenSource(CommandBridgeQueryTimeoutMs))
            {
                response = transport.ListModelConfigurationsAsync(
                    ResolveModelName(null),
                    KimodoTextEncoderModeProtocol.ToProtocolValue(settings.DefaultTextEncoderMode),
                    settings.LocalModelsPath?.Trim() ?? string.Empty,
                    null,
                    timeout.Token).GetAwaiter().GetResult();
            }
            var result = new JObject(response);
            result.Remove("status");
            result["count"] = (result["configs"] as JArray)?.Count ?? 0;
            if (arguments.Value<bool?>("compact") == true)
            {
                result = CompactModelConfigurations(result);
            }
            return Ok(result);

        }

        private static JObject CompactModelConfigurations(JObject response)
        {
            JArray configurations = response["configs"] as JArray ?? new JArray();
            return new JObject
            {
                ["default"] = response["default"]?.DeepClone(),
                ["count"] = configurations.Count,
                ["configs"] = new JArray(configurations.OfType<JObject>().Select(configuration => new JObject
                {
                    ["model"] = configuration["model"]?.DeepClone(),
                    ["text_encoder_model"] = configuration["text_encoder_model"]?.DeepClone(),
                    ["default"] = configuration["default"]?.DeepClone()
                }))
            };
        }

        public static string GetCommandHelp(string argumentsJson = "{}") => Execute(argumentsJson, GetCommandHelp);

        private static string GetCommandHelp(JObject arguments)
        {
                string section = (arguments.Value<string>("section") ?? "commands").Trim().ToLowerInvariant();
                string command = arguments.Value<string>("command")?.Trim();
                if (!string.IsNullOrWhiteSpace(command))
                {
                    if (!Commands.Value.TryGet(command, out CommandRegistration registration))
                    {
                        throw new InvalidOperationException($"Unknown Kimodo command '{command}'.");
                    }
                    JObject manual = registration.ToJson();
                    string[] sharedRuleRefs = manual["documentation"]?["shared_rule_refs"]?.Values<string>().ToArray() ?? Array.Empty<string>();
                    return Ok(new JObject
                    {
                        ["manual"] = manual,
                        ["shared_rules"] = BuildSharedHelpRules(sharedRuleRefs),
                        ["usage"] = $"{command}(<arguments matching inputSchema>)"
                    });
                }
                if (section == "models")
                {
                    return ListModels(arguments);
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
                        "Commands resolve scene references from the active scene and asset references from project assets.",
                        "Pass returned scene object references to later commands. Async submissions return request_id for progress and results through generation.get.",
                        "Installation, generation, and Mocap analysis use accepted, running, completed, and failed states. Generation supports cancellation.",
                        "Generation acceptance includes a request handle, preflight diagnostics, and warnings; completion includes Clip metadata and asset path. include_request_json returns the submitted payload."
                    },
                    ["shared_rules"] = BuildSharedHelpRules(),
                    ["routing"] = new JArray
                    {
                        Route("discover schema or models", HelpCommand),
                        Route("read active scene context", SessionContextCommand),
                        Route("reinstall the local server", InstallServerCommand, "then " + GetGenerationCommand),
                        Route("generate motion", GenerateAnimationCommand, "then " + GetGenerationCommand),
                        Route("analyze a video for SMPL-X poses", PoseFromVideoCommand, "then " + GetGenerationCommand),
                        Route("analyze a picture for one SMPL-X pose", PoseFromPictureCommand, "then " + GetGenerationCommand),
                        Route("analyze and render motion", AnimationAnalysisCommand, "returns one composite picture and self-describing tiles"),
                        Route("capture and measure a sampled Humanoid pose", PoseAnalysisCommand, "returns a SceneView image and world-space joint measurements"),
                        Route("materialize or edit a pose", PoseGetCommand, "then pose.set"),
                        Route("obtain a reusable root trajectory", AnimationAnalysisCommand, "then reference root_trajectory.path from a generation root_path constraint"),
                    },
                    ["handles"] = new JObject
                    {
                        ["request_id"] = "Returned by server.install, animation.generate, pose.fromvideo, or pose.frompicture. Pass it to generation.get; generation.cancel accepts animation generation request ids.",
                        ["pictures.image_path"] = "Read the composite PNG returned by animation.analysis.",
                        ["pose"] = "A {track,index} reference returned by pose.get or a pose editing command.",
                        ["clip.asset_path"] = "A project-relative generated animation asset path returned after generation completes."
                    },
                    ["workflow"] = new JArray
                    {
                        new JObject { ["command"] = InstallServerCommand, ["arguments"] = new JObject(), ["when"] = "QuickServer is unavailable or needs updating", ["save"] = "request_id" },
                        new JObject { ["command"] = GetGenerationCommand, ["arguments"] = new JObject { ["request_id"] = "<install_request_id>" }, ["repeat_until"] = "status is completed or failed" },
                        new JObject { ["command"] = GenerateAnimationCommand, ["arguments"] = new JObject { ["character"] = "<character>", ["prompt"] = "<short action for one Clip>", ["duration_frames"] = 60 }, ["save"] = "request_id" },
                        new JObject { ["command"] = GetGenerationCommand, ["arguments"] = new JObject { ["request_id"] = "<request_id>" }, ["repeat_until"] = "status is completed, failed, or canceled" },
                        new JObject { ["command"] = AnimationAnalysisCommand, ["arguments"] = new JObject { ["clips"] = new JArray(new JObject { ["character"] = "<character>", ["clip"] = "<completed animation>" }) }, ["save"] = "pictures.image_path" },
                    },
                    ["commands"] = new JArray(all["tools"].Children<JObject>().Select(item => new JObject
                    {
                        ["name"] = item.Value<string>("name"),
                        ["description"] = item.Value<string>("description"),
                        ["documentation"] = item["documentation"]?.DeepClone() ?? new JObject(),
                        ["required"] = item["inputSchema"]?["required"]?.DeepClone() ?? new JArray(),
                        ["outputSchema"] = item["outputSchema"]?.DeepClone() ?? new JObject(),
                        ["examples"] = item["examples"]?.DeepClone() ?? new JArray()
                    })),
                    ["constraints"] = constraintManual["constraints"].DeepClone(),
                    ["constraint_rules"] = constraintManual["rules"].DeepClone()
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
                        ["type"] = "point",
                        ["description"] = "Use the explicit External Pose, or the Avatar skeleton rest pose when pose is omitted. Select the point mode from root, pose, and effector inputs, then overlay the supplied data. Pose/effector safety preflight applies when both fields appear in one point constraint.",
                        ["shape"] = new JObject
                        {
                            ["frame"] = "Relative frame in the generated clip.",
                            ["pose"] = "Optional FullBody External Pose reference: {track,index}.",
                            ["root"] = "Optional World root position [x,y,z] and/or Euler rotation [x,y,z] in degrees; maps directly to UI root fields.",
                            ["effector"] = "Array of hand/foot targets. Each item uses joint=left_hand, right_hand, left_foot, or right_foot with World position and/or Euler rotation in degrees."
                        }
                    },
                    new JObject
                    {
                        ["type"] = "inout",
                        ["description"] = "Explicit source Clip windows used as boundary context. Outside adds and crops context; inside retains constrained windows within the output.",
                        ["shape"] = InOutConstraintSchema(),
                        ["examples"] = BuildCommandExamples(GenerateAnimationCommand)
                    },
                    new JObject
                    {
                        ["type"] = "root_path",
                        ["description"] = "A reusable analyzed Root Path compiled to Root2D samples during generation.",
                        ["shape"] = new JObject
                        {
                            ["frame"] = "Optional first path frame; defaults to 0.",
                            ["root_path"] = new JObject { ["path"] = "{track,index} from animation.analysis clip.root_trajectory.path" }
                        }
                    }
                },
                ["rules"] = new JArray
                {
                    "The command selects Root2D, FullBody, or Effector solely from explicit root, pose, and effector fields. Full root values map to the UI fields; the existing solver and exporter handle their semantics.",
                    "Pose/effector safety preflight applies when both fields appear in one point constraint; pose.analysis measures and captures the sampled pose after generation.",
                    "A point constraint selects one mode: pose with optional root/effector is FullBody after IK; root with effector is Effector; root alone is Root2D. Put combined data in one point object and use unique frames.",
                    "Use animation.analysis, confirm that the returned World-space Root Path belongs to the intended character and motion, then reference clip.root_trajectory.path from root_path; generation resolves it once against the target character's rest-pose World anchor.",
                    "Map explicit path, angle, distance, and heading requirements to the supported trajectory fields; use confirmed scene or analysis evidence to supply unspecified details and leave other optional fields at their schema defaults.",
                    "Use the intended character's World-space animation.analysis Root Path as the root_path source. Generation resolves it once against the target character's rest-pose World anchor and creates start and end Root2D constraints; add point constraints for intermediate route locations that must be followed.",
                    "Keep point frames distinct from root_path sample frames.",
                    "Use one standalone inout entry with explicit completed source Clips for in and out. Source character defaults to the generation character.",
                    "In/Out window_frames and source.frame use 60 FPS. Windows round up to model frames; sample_count maps to distinct model frames. One sample uses the seam; multiple samples include both endpoints.",
                    "In/Out uses supported source lengths, non-overlapping inside windows, compatible models, and the model duration limit; padding and cropping preserve the requested duration.",
                    "inout_sampling is returned after generation completes, with source-local and Timeline-global 60 FPS sample frames, output/runtime model frames, model-frame context counts and the exclusive model-frame crop range. Padding/cropping is automatic; the final requested duration is preserved."
                }
            };
        }

        public static string InstallServer(string argumentsJson = "{}") => Execute(argumentsJson, InstallServer);

        private static string InstallServer(JObject arguments)
        {
                EnsureCanManageServer();
                string requestId = "install:" + Guid.NewGuid().ToString("N");
                lock (JobsLock)
                {
                    InstallTasks[requestId] = new JObject
                    {
                        ["status"] = "queued",
                        ["task_id"] = requestId,
                        ["retention_started_at_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                        ["progress"] = "0/0",
                        ["eta_seconds"] = 60.0,
                        ["message"] = "Server installation waiting to start."
                    };
                    TrimTerminalTasksLocked(InstallTasks, MaxRememberedJobs, "done", "error");
                }
                EditorApplication.delayCall += async () =>
                {
                    await RunInstallServerTaskAsync(requestId);
                };
                return Ok(new JObject
                {
                    ["request_id"] = requestId,
                    ["status"] = "accepted"
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
                    // Installation is an explicit recovery/bootstrap boundary.  A
                    // refresh leaves stale setup sentinels and venv paths in a
                    // copied project runtime, which can make the launcher resolve
                    // Python from a different project.  Reinstall clears the
                    // runtime while preserving its models and copies a clean
                    // package template before warmup.
                    if (!KimodoBridgeServerTool.ReinstallRuntimeRoot())
                    {
                        throw new InvalidOperationException("Failed to reinstall runtime root from package template.");
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
                    task["install_mode"] = "reinstall";
                    task["server_connected"] = KimodoBridgeService.Shared.IsConnected;
                }
            }
        }

        public static string GetGeneration(string argumentsJson) => Execute(argumentsJson, GetGeneration);

        private static string GetGeneration(JObject arguments)
        {
                string requestValue = RequiredStringValue(arguments, "request_id");
                bool includeRequestJson = arguments.Value<bool?>("include_request_json") == true;
                if (requestValue.StartsWith("install:", StringComparison.OrdinalIgnoreCase))
                {
                    lock (JobsLock)
                    {
                        if (InstallTasks.TryGetValue(requestValue, out JObject installStatus))
                        {
                            return Ok(NormalizeInstallStatus(installStatus, requestValue));
                        }
                    }
                    throw new InvalidOperationException($"Unknown or expired request_id '{requestValue}'.");
                }
                if (requestValue.StartsWith("mocap:", StringComparison.OrdinalIgnoreCase))
                {
                    return Ok(GetMocapAnalysisTask(requestValue));
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
                    return Ok(NormalizeGenerationStatus(persisted, requestValue, includeRequestJson));
                }
                EnsureGenerationBelongsToSession(record, session);
                JObject status = BuildStatus(record, includeRequestJson);
                status["target_alive"] = record.Target != null;
                return Ok(NormalizeGenerationStatus(status, requestValue, includeRequestJson));

        }

        public static string CancelGeneration(string argumentsJson) => Execute(argumentsJson, CancelGeneration);

        private static string CancelGeneration(JObject arguments)
        {
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                Guid requestId = RequiredRequestId(arguments);
                if (!TryGetJob(requestId, out JobRecord record))
                {
                    JObject persisted = LoadPersistedGenerationJob(session, requestId);
                    persisted["canceled"] = false;
                    return Ok(BuildCancellationStatus(
                        NormalizeGenerationStatus(persisted, requestId.ToString("D")),
                        requestId.ToString("D"),
                        canceled: false));
                }
                EnsureGenerationBelongsToSession(record, session);
                string reason = arguments.Value<string>("reason")?.Trim();
                bool canceled = KimodoEditorGenerationJobService.Cancel(
                    requestId,
                    string.IsNullOrWhiteSpace(reason) ? "Generation canceled by command." : reason);
                PersistGenerationJobStatus(record.Session);
                JObject status = BuildStatus(record);
                status["canceled"] = canceled;
                return Ok(BuildCancellationStatus(
                    NormalizeGenerationStatus(status, requestId.ToString("D")),
                    requestId.ToString("D"),
                    canceled));

        }

        private static JObject BuildCancellationStatus(JObject normalized, string requestId, bool canceled)
        {
            var result = new JObject
            {
                ["request_id"] = requestId,
                ["status"] = normalized?.Value<string>("status") ?? string.Empty,
                ["stage"] = normalized?.Value<string>("stage") ?? string.Empty,
                ["message"] = normalized?.Value<string>("message") ?? string.Empty,
                ["canceled"] = canceled
            };
            if (normalized?["clip"] != null)
            {
                result["clip"] = normalized["clip"].DeepClone();
            }
            if (normalized?["error"] != null)
            {
                result["error"] = normalized["error"].DeepClone();
            }
            return result;
        }

        public static string GenerateAnimationAsset(string argumentsJson) => Execute(argumentsJson, GenerateAnimationAsset);

        private static string GenerateAnimationAsset(JObject arguments)
        {
                EnsureCanGenerate();
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                string rawGenerateInstructionJson = null;
                JObject rawGenerateInstruction = null;
                JObject rawInstructionObject = arguments["raw_instruction"] as JObject;
                if (rawInstructionObject != null)
                {
                    if (!string.Equals(rawInstructionObject.Value<string>("role")?.Trim(), "generate", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("raw_instruction.role must be generate.");
                    if (arguments["prompt"] != null)
                        throw new InvalidOperationException("prompt and raw_instruction are mutually exclusive.");
                    rawGenerateInstructionJson = rawInstructionObject.Value<string>("text");
                    rawGenerateInstruction = KimodoRawGenerateInstruction.Parse(rawGenerateInstructionJson);
                }
                string prompt = rawGenerateInstruction != null
                    ? KimodoRawGenerateInstruction.ReadPrompt(rawGenerateInstruction)
                    : RequiredStringValue(arguments, "prompt");
                if (string.IsNullOrWhiteSpace(prompt))
                    throw new InvalidOperationException("prompt is required unless raw_instruction contains a prompt.");
                TimelineCharacterRecord character = ResolveCharacter(session, RequiredStringValue(arguments, "character"));
                JObject pathOptions = arguments["path"] as JObject;
                JObject generationOptions = arguments["generation"] as JObject;
                JObject outputOptions = arguments["output"] as JObject;
                if (rawGenerateInstruction != null && generationOptions != null)
                    throw new InvalidOperationException("raw_instruction cannot be combined with structured generation options.");
                string outputMode = ParseOutputMode(outputOptions?.Value<string>("mode"));
                string requestedModel = generationOptions?.Value<string>("model")?.Trim() ?? rawGenerateInstruction?.Value<string>("model")?.Trim();
                string requestedTextEncoder = generationOptions?.Value<string>("text_encoder")?.Trim() ?? rawGenerateInstruction?.Value<string>("text_encoder_mode")?.Trim();
                string modelName = ResolveModelName(requestedModel);
                KimodoTextEncoderMode textEncoderMode = ResolveTextEncoderMode(requestedTextEncoder);
                JObject modelConfiguration = null;
                if (!string.IsNullOrWhiteSpace(requestedModel) || !string.IsNullOrWhiteSpace(requestedTextEncoder))
                {
                    modelConfiguration = EnsureRegisteredModel(modelName, textEncoderMode);
                }
                float frameRate = ResolveFrameRate(modelName, modelConfiguration);
                float? rawDuration = KimodoRawGenerateInstruction.ReadDuration(rawGenerateInstruction);
                int durationFrames = arguments.Value<int?>("duration_frames") ??
                    (rawDuration.HasValue ? Math.Max(1, KimodoFrameTimeUtility.SecondsToFrameCount(rawDuration.Value, (float)SessionFrameRate)) : 300);
                if (rawGenerateInstruction != null && arguments["duration_frames"] != null && rawDuration.HasValue &&
                    Math.Abs(durationFrames / SessionFrameRate - rawDuration.Value) > 1.0 / SessionFrameRate)
                    throw new InvalidOperationException("duration_frames must match raw_instruction.text duration when both are supplied.");

                JObject loopObject = arguments["loop"] as JObject;
                bool loopRequested = loopObject != null && (loopObject.Value<bool?>("enabled") ?? true);
                int loopWindowFrames60 = loopObject?.Value<int?>("window_frames") ?? 30;
                int loopWindowSampleCount = loopObject?.Value<int?>("sample_count") ?? 2;

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

                if (overridePathDistance && !overridePathAngle)
                {
                    throw new InvalidOperationException("path.distance requires a path angle override.");
                }
                bool overrideHeading = pathOptions?["heading"] != null;
                float headingDegrees = overrideHeading
                    ? ReadFiniteFloat(pathOptions["heading"], "path.heading")
                    : 0f;
                bool useRelativeOffset = pathOptions?["relative_offset"] != null;
                Vector3 relativeOffset = useRelativeOffset
                    ? ReadVector3(pathOptions["relative_offset"] as JArray, "path.relative_offset")
                    : Vector3.zero;
                if (rawGenerateInstruction != null && (loopRequested || pathOptions != null || arguments["constraints"] != null))
                    throw new InvalidOperationException("raw_instruction cannot be combined with loop, path, or structured constraints; replay the captured generate request as-is.");
                float duration = (float)(durationFrames / SessionFrameRate);
                bool analyzeAfterGeneration = arguments.Value<bool?>("analyze") ?? true;
                int frameCount = Math.Max(1, KimodoFrameTimeUtility.SecondsToFrameCount(duration, frameRate));
                int seed = generationOptions?.Value<int?>("seed") ?? rawGenerateInstruction?.Value<int?>("seed") ?? (Guid.NewGuid().GetHashCode() & int.MaxValue);
                int steps = generationOptions?.Value<int?>("diffusion_steps") ?? rawGenerateInstruction?.Value<int?>("diffusion_steps") ?? ResolveDiffusionSteps(arguments, modelName, modelConfiguration);
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
                    durationFrames,
                    character.Root,
                    character,
                    out List<string> poseWarnings);

                JObject generationCheck = BuildGenerationCheck(
                    arguments, character, durationFrames, (float)SessionFrameRate, modelName, poseConstraints);
                if (generationCheck.Value<bool>("blocking"))
                {
                    string blockingMessage = generationCheck["duration"]?["issues"]?
                        .OfType<JObject>()
                        .FirstOrDefault(issue => string.Equals(issue.Value<string>("code"), "duration_below_minimum", StringComparison.OrdinalIgnoreCase))?
                        .Value<string>("message")
                        ?? generationCheck["root_trajectory"]?["issues"]?
                        .OfType<JObject>()
                        .FirstOrDefault(issue => string.Equals(issue.Value<string>("code"), "speed_hard_limit", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(issue.Value<string>("code"), "acceleration_hard_limit", StringComparison.OrdinalIgnoreCase))?
                        .Value<string>("message");
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(blockingMessage)
                        ? "Generation preflight failed because the requested duration or root speed/acceleration exceeds a hard limit."
                        : blockingMessage);
                }
                TimelineGenerationTrace trace = PrepareGenerationTrace(arguments, character, duration);
                bool hasInOut = (arguments["constraints"] as JArray)?.OfType<JObject>()
                    .Any(item => item.Property("inout") != null) == true;
                if (hasInOut)
                {
                    // Resolve the same source windows used to place the temporary
                    // neighboring clips before reserving the output range.  The
                    // actual constraint samples are built later by BuildRequest.
                    ConfigureCommandInOutClip(
                        arguments,
                        trace,
                        null,
                        modelName,
                        frameCount,
                        frameRate,
                        loopRequested,
                        poseConstraints);
                    PrepareCommandOutsideEvidencePosition(trace);
                    ValidateCommandTimelineRange(trace);
                }
                KimodoPlayableClip playableClip = CreateGenerationPlayableClip(trace, requestedAnimationName, false);
                if (hasInOut)
                {
                    ConfigureCommandInOutClip(
                        arguments,
                        trace,
                        playableClip,
                        modelName,
                        frameCount,
                        frameRate,
                        loopRequested,
                        poseConstraints);
                    CreateCommandOutsideEvidenceClips(trace);
                }
                playableClip.bridgeModelName = modelName;
                playableClip.textEncoderMode = textEncoderMode;
                playableClip.motionPrompt = prompt;
                playableClip.generationFrames = frameCount;
                playableClip.diffusionSteps = steps;
                playableClip.randomSeed = false;
                playableClip.seed = seed;
                playableClip.generateLoop = loopRequested;
                playableClip.loopWindowFrames60 = loopWindowFrames60;
                playableClip.loopWindowSampleCount = loopWindowSampleCount;
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
                playableClip.useRelativeOffset = useRelativeOffset;
                playableClip.relativeOffset = relativeOffset;
                playableClip.loop = loopRequested
                    ? UnityEngine.Timeline.AnimationPlayableAsset.LoopMode.On
                    : UnityEngine.Timeline.AnimationPlayableAsset.LoopMode.Off;
                playableClip.analyzeAfterGeneration = analyzeAfterGeneration;
                playableClip.generatedAssetName = trace.Animation.Name;
                playableClip.generatedOutputFolder = outputFolder;
                playableClip.generationOutputMode = ParseGenerationOutputMode(outputMode);
                WriteGenerationConstraintMarkers(trace, poseConstraints, (float)SessionFrameRate);
                ReserveGenerationTimelineRange(trace);
                SaveTimelineSession(session);

                string previousTaskId = null;
                Action<string> previousRequestPayload = null;
                bool started = KimodoPlayableClipGenerationExecutionService.TryStartConfiguredGenerate(
                    playableClip,
                    character.Root,
                    trace.TimelineClip,
                    generationSession =>
                    {
                        previousTaskId = KimodoBridgeService.GenerationTaskIdContext.Value;
                        previousRequestPayload = KimodoBridgeService.GenerationRequestPayloadContext.Value;
                        KimodoBridgeService.GenerationTaskIdContext.Value = generationSession.RequestId.ToString("N");
                        KimodoBridgeService.GenerationRequestPayloadContext.Value = payload =>
                        {
                            if (string.IsNullOrWhiteSpace(generationSession.BackendRequestJson))
                                generationSession.BackendRequestJson = payload;
                        };
                    },
                    (generationSession, result, token) =>
                    {
                        FinalizePlayableClipTrace(trace, result);
                        if (!playableClip.analyzeAfterGeneration)
                        {
                            result.PostAnalysisSkipped = true;
                            return Task.FromResult(result);
                        }
                        KimodoEditorGenerationJobService.UpdateProgress(
                            character.Root,
                            generationSession.RequestId,
                            KimodoBridgeCommandStage.Finalize,
                            "Running post-generation animation analysis.");
                        try
                        {
                            JObject analysisResponse = AnimationAnalysisCore(new JObject
                            {
                                ["clips"] = new JArray(new JObject
                                {
                                    ["character"] = trace.Character.Name,
                                    ["clip"] = trace.Animation.Name
                                })
                            }, allowActiveGenerationRange: true);
                            result.PostAnalysisJson = analysisResponse.ToString(Formatting.None);
                        }
                        catch (Exception ex)
                        {
                            result.PostAnalysisError = ex.Message;
                            Debug.LogWarning($"[Kimodo][PostGenerationAnalysis] {ex}");
                        }
                        return Task.FromResult(result);
                    },
                    generationSession =>
                    {
                        KimodoBridgeService.GenerationTaskIdContext.Value = previousTaskId;
                        KimodoBridgeService.GenerationRequestPayloadContext.Value = previousRequestPayload;
                    },
                    PersistGenerationJobStatus,
                    out KimodoEditorGenerationJobSession generation,
                    out string error,
                    rawGenerateInstructionJson);
                if (!started)
                {
                    throw new InvalidOperationException(error);
                }

                Remember(character.Root, generation, trace);
                PersistGenerationJobStatus(generation);
                var accepted = new JObject { ["check"] = generationCheck };
                var warnings = new JArray();
                if (poseWarnings != null)
                    foreach (string warning in poseWarnings)
                        warnings.Add(warning);
                foreach (JObject issue in generationCheck["duration"]?["issues"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    if (!string.Equals(issue.Value<string>("code"), "duration_below_minimum", StringComparison.OrdinalIgnoreCase))
                        warnings.Add(issue.Value<string>("message"));
                }
                if (warnings.Count > 0)
                    accepted["warnings"] = warnings;
                return Started(generation, accepted);

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
