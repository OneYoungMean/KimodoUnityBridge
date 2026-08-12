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

namespace KimodoUnityBridge.Command
{
    /// <summary>
    /// Shared implementation behind the framework-neutral Kimodo command entry points.
    /// </summary>
    internal static partial class command_context
    {
        private static Vector2 LoopBezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            t = Mathf.Clamp01(t); float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }
        public const string HelpCommand = "kimodo_help";
        public const string DebugInstallServerCommand = "kimodo_debug_install_server";
        public const string GenerateAnimationCommand = "kimodo_generate_animation";
        public const string SessionOpenCommand = "session_open";
        public const string SessionCloseCommand = "session_close";
        public const string QueryCurrentSessionCommand = "query_current_session";
        public const string SessionTryAddCommand = "session_try_add";
        public const string SessionTryRemoveCommand = "session_try_remove";
        public const string KimodoAnalyzeCommand = "kimodo_analyze";
        public const string KimodoRecordRangeCommand = "kimodo_record_range";
        public const string KimodoRetargetAnimationCommand = "kimodo_retarget_animation";
        public const string QueryPictureCommand = "query_picture";
        public const string PoseCreateCommand = "pose_create";
        public const string PoseGetCommand = "pose_get";
        public const string PoseSetCommand = "pose_set";
        public const string PoseCopyCommand = "pose_copy";
        public const string BuildRoot2DPathCommand = "kimodo_build_root2d_path";
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
                    CommandDefinition(HelpCommand,
                        "Return the command manual, detailed parameter documentation for one command, or currently viable model configurations.",
                        Properties(
                            Optional("command", "string", "Command name whose full manual entry should be returned."),
                            Enum("section", "commands", "models", "constraints"))),
                    CommandDefinition(DebugInstallServerCommand,
                        "[debug-only] Incrementally install the QuickServer runtime from the package template, preserving models and the Python environment, then restart it.",
                        Properties(),
                        debugOnly: true),
                    CommandDefinition(SessionOpenCommand,
                        "Create a new current animation editing Session, or load an existing named Session.",
                        Properties(
                            Optional("session_name", "string", "Existing Session name to load; omitted always creates a new Session."))),
                    CommandDefinition(SessionCloseCommand,
                        "Close the current animation editing Session while preserving scene contents and the Session for a later named reopen. Kimodo detaches its shared transient Director instead of leaving one Director per Session.",
                        Properties()),
                    CommandDefinition(QueryCurrentSessionCommand,
                        "Query characters, a character's animations, one animation, character constraints, or animation constraints.",
                        Properties(
                            RequiredEnum("query", "characters", "character_animations", "animation", "character_constraints", "animation_constraints", "animation_transitions", "transition"),
                            Optional("character", "string", "Safe character name in the current Session."),
                            Optional("animation", "string", "Safe animation name in the selected character."))),
                    CommandDefinition(SessionTryAddCommand,
                        "TryAdd a humanoid character, AnimationClip, or AnimatorController content to the current Session. Appended clips keep a fixed 4-frame safezone. Deterministic Clip-to-Clip transitions are recorded; BlendTree choices remain explicit Timeline work.",
                        Properties(
                            Required("kind", "string", "character, clip, or animator."),
                            Required("character", "string", "Scene character name/path for kind=character, or target Session character name otherwise."),
                            Optional("clip", "string", "Project AnimationClip name for kind=clip."),
                            Optional("animator", "string", "Scene Animator name/path for kind=animator."))),
                    CommandDefinition(SessionTryRemoveCommand,
                        "TryRemove a character track or one clip. Removing a clip does not move other clips or reuse its virtual time address.",
                        Properties(
                            Required("kind", "string", "character or clip."),
                            Required("character", "string", "Safe character name in the current Session."),
                            Optional("animation", "string", "Safe animation name for kind=clip."))),
                    CommandDefinition(KimodoAnalyzeCommand,
                        "Analyze one named animation, a Session frame range, or two or more explicit poses. Clip/range analysis is performed by QuickServer; explicit poses return adjacent muscle cosine and root-motion comparisons. Overlap with a running generation on the same character track returns generation_range_locked.",
                        Properties(
                            Required("character", "string", "Safe character name in the current Session."),
                            Optional("animation", "string", "Safe animation name; mutually exclusive with start_frame/end_frame."),
                            Optional("start_frame", "integer", "Inclusive Session frame at 60 FPS; requires end_frame."),
                            Optional("end_frame", "integer", "Exclusive Session frame at 60 FPS; requires start_frame."),
                            OptionalPoseLocators("poses", "Two or more pose locators {source,frame}; mutually exclusive with animation and range. Compares each adjacent pair."),
                            Optional("analysis_option", "object", "Optional QuickServer analysis configuration; analysis_only is forced true."))),
                    CommandDefinition(KimodoRecordRangeCommand,
                        "Record a Session time range into an AnimationClip and append it to the source character with a fixed 4-frame safezone. Overlap with a running generation on the source track returns generation_range_locked.",
                        Properties(
                            Required("start_frame", "integer", "Inclusive Session frame at 60 FPS."),
                            Required("end_frame", "integer", "Exclusive Session frame at 60 FPS."),
                            Required("character", "string", "Safe source character name in the current Session."),
                            Optional("remove_root_motion", "boolean", "Keep vertical motion but remove horizontal root translation and yaw; defaults to false."),
                            Optional("speed", "number", "Playback speed multiplier; defaults to 1.0."),
                            Optional("name", "string", "Requested safe output animation name."),
                            Optional("output_folder", "string", "Unity folder under Assets; defaults to Assets/KimodoGeneratedClips."))),
                    CommandDefinition(KimodoRetargetAnimationCommand,
                        "Retarget one loaded animation to another current Session character and append the result with a fixed 4-frame safezone.",
                        Properties(
                            Required("character", "string", "Safe source character name in the current Session."),
                            Required("animation", "string", "Safe source animation name."),
                            Required("target_character", "string", "Safe target character name in the current Session."),
                            Optional("name", "string", "Requested safe output animation name."),
                            Optional("output_folder", "string", "Unity folder under Assets; defaults to Assets/KimodoGeneratedClips."))),
                    CommandDefinition(GenerateAnimationCommand,
                        "Append a configured KimodoPlayableClip to the character Timeline with a fixed 4-frame safezone and generate its AnimationClip asset. Without a current Session, a retained __KimodoAuto__ Session is created and closed after generation.",
                        Properties(
                            Required("character", "string", "Safe scene or Session character name."),
                            Required("prompt", "string", "Motion prompt."),
                            Optional("duration_frames", "integer", "Duration in 60 FPS Session frames; defaults to 300."),
                            Optional("loop", "boolean", "Enable loop-oriented generation preprocessing. If the extended duration exceeds 600 frames, generation falls back to the default flow with a warning."),
                            Optional("model", "string", "Registered model name/configuration id; omitted uses the Project Settings default. Use kimodo_help({section:'models'}) to query models."),
                            Enum("text_encoder_model", "high_performance", "high_precision"),
                            Optional("seed", "integer", "Deterministic seed; omitted chooses a random seed."),
                            Optional("diffusion_steps", "integer", "Diffusion steps; omitted uses the model default."),
                            Enum("output_mode", "humanoid_muscle", "character_bone", "model_bone"),
                            Optional("output_folder", "string", "Unity folder under Assets; defaults to Assets/KimodoGeneratedClips."),
                            Optional("name", "string", "Requested safe animation name; defaults to the prompt."),
                            Optional("analysis_option", "object", "Optional analysis object; set keyframes.enabled=true to return screenshot keyframes."),
                            OptionalConstraints("constraints", "One sparse constraint object per frame; combine fullbody, root2d, and hand/foot targets in the same object."))),
                    CommandDefinition(QueryPictureCommand,
                        "Render explicit poses, a cached analysis, or inline constraints together in one four-view square image.",
                        Properties(
                            OptionalPoseLocators("poses", "Pose locators {source,frame}."),
                            Optional("analysis_id", "string", "Stable id returned by kimodo_analyze."),
                            OptionalConstraints("constraints", "Inline constraints in the same format accepted by kimodo_generate_animation."),
                            Optional("character", "string", "Character used to frame position-only Root2D constraints."),
                            Optional("resolution", "integer", "Square output size in pixels; defaults to 512."),
                            Optional("scale", "number", "Camera framing scale; defaults to 1.0."))),
                    CommandDefinition(PoseCreateCommand,
                        "Create a writable pose for a character.",
                        Properties(
                            Required("character", "string", "Target character name."),
                            Required("pose", "object", "Pose data containing root.forwardPos, root.rightwardPos, root.rotateY, muscles, and optional foot_ik."))),
                    CommandDefinition(PoseGetCommand,
                        "Read a pose from any returned {source,frame} locator. Sampling a character Timeline frame locked by generation returns generation_range_locked.",
                        Properties(Required("pose", "object", "{source,frame} pose locator at fixed 60 FPS."))),
                    CommandDefinition(PoseSetCommand,
                        "Update a writable pose.",
                        Properties(
                            Required("pose", "object", "Writable {source,frame} pose locator."),
                            Optional("data", "object", "Partial pose data to update; root uses forwardPos, rightwardPos, and rotateY."))),
                    CommandDefinition(PoseCopyCommand,
                        "Copy any pose into a writable pose for the target character.",
                        Properties(
                            Required("character", "string", "Target character name."),
                            Required("pose", "object", "Source {source,frame} pose locator."))),
                    CommandDefinition(BuildRoot2DPathCommand,
                        "Build dependency-free Root2D path points at fixed 60 FPS. Each point returns forwardPos, rightwardPos, and rotateY.",
                        Properties(
                            Required("shape", "string", "line, turn, s, or circle."),
                            Optional("duration_frames", "integer", "Duration at 60 FPS; defaults to 300."),
                            Optional("max_speed", "number", "Maximum units per second; defaults to 2.5."),
                            Optional("acceleration", "number", "Acceleration/deceleration units per second squared; defaults to 2.5."),
                            Optional("direction", "string", "left or right; defaults to left."),
                            Optional("turn_degrees", "integer", "0, 45, 90, 135, or 180."))),
                    CommandDefinition(QueryGenerationCommand,
                        "Get generation progress and the generated animation safe name.",
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
                case HelpCommand:
                    return GetCommandHelp(argumentsJson);
                case DebugInstallServerCommand:
                    return DebugInstallServer(argumentsJson);
                case SessionOpenCommand:
                    return SessionOpenTimeline(argumentsJson);
                case SessionCloseCommand:
                    return SessionCloseTimeline(argumentsJson);
                case QueryCurrentSessionCommand:
                    return QueryCurrentSession(argumentsJson);
                case SessionTryAddCommand:
                    return SessionTryAdd(argumentsJson);
                case SessionTryRemoveCommand:
                    return SessionTryRemove(argumentsJson);
                case KimodoAnalyzeCommand:
                    return KimodoAnalyzeTimelineRange(argumentsJson);
                case KimodoRecordRangeCommand:
                    return KimodoRecordTimelineRange(argumentsJson);
                case KimodoRetargetAnimationCommand:
                    return KimodoRetargetAnimation(argumentsJson);
                case QueryPictureCommand:
                    return Capture(argumentsJson);
                case PoseCreateCommand:
                    return PoseCreate(argumentsJson);
                case PoseGetCommand:
                    return PoseGet(argumentsJson);
                case PoseSetCommand:
                    return PoseSet(argumentsJson);
                case PoseCopyCommand:
                    return PoseCopy(argumentsJson);
                case BuildRoot2DPathCommand:
                    return BuildRoot2DPath(argumentsJson);
                case GenerateAnimationCommand:
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
                    ["workflow"] = new JArray
                    {
                        new JObject { ["command"] = SessionOpenCommand, ["arguments"] = new JObject() },
                        new JObject
                        {
                            ["command"] = QueryCurrentSessionCommand,
                            ["arguments"] = new JObject { ["query"] = "characters" },
                            ["save"] = "one returned character name"
                        },
                        new JObject
                        {
                            ["command"] = GenerateAnimationCommand,
                            ["arguments"] = new JObject
                            {
                                ["character"] = "<character>",
                                ["prompt"] = "stand still and breathe naturally",
                                ["duration_frames"] = 60
                            },
                            ["save"] = "request_id"
                        },
                        new JObject
                        {
                            ["command"] = QueryGenerationCommand,
                            ["arguments"] = new JObject { ["request_id"] = "<request_id>" },
                            ["repeat_until"] = "status is completed, failed, or canceled"
                        },
                        new JObject { ["command"] = SessionCloseCommand, ["arguments"] = new JObject() }
                    },
                    ["commands"] = new JArray(all["tools"].Children<JObject>().Select(item => new JObject
                    {
                        ["name"] = item.Value<string>("name"),
                        ["description"] = item.Value<string>("description"),
                        ["required"] = item["inputSchema"]?["required"]?.DeepClone() ?? new JArray(),
                        ["debug_only"] = item.Value<bool?>("debug_only") ?? false
                    })),
                    ["constraint"] = constraintManual["constraint"].DeepClone(),
                    ["constraint_rules"] = constraintManual["rules"].DeepClone()
                });
            });
        }

        private static JObject BuildConstraintManual()
        {
            return new JObject
            {
                ["manual"] = "Kimodo generation constraint reference",
                ["constraint"] = new JObject
                {
                    ["frame"] = "Relative frame in the generated clip at 60 FPS.",
                    ["fullbody"] = "Optional {pose:{source,frame}}.",
                    ["root2d"] = "Optional {pose:{source,frame}} or {position:[x,z],heading:[x,z]}.",
                    ["left_hand"] = "Optional {pose?,position:[x,y,z]?,rotation:[x,y,z,w]?}.",
                    ["right_hand"] = "Same shape as left_hand.",
                    ["left_foot"] = "Same shape as left_hand.",
                    ["right_foot"] = "Same shape as left_hand."
                },
                ["rules"] = new JArray
                {
                    "Put every constraint for one frame in one object; the legacy {frame,type,...} union is not accepted.",
                    "root2d overrides the planar root position and heading inherited from fullbody at the same frame.",
                    "Hand/foot position and rotation override their pose or the same-frame fullbody pose.",
                    "Positions and rotations use canonical Unity coordinates: +X right, +Y up, +Z forward, quaternion [x,y,z,w]."
                }
            };
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
                if (!TryGetJob(requestId, out JobRecord record))
                {
                    JObject persisted = LoadPersistedGenerationJob(requestId);
                    persisted["target_alive"] = false;
                    return Ok(persisted);
                }
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
                if (!TryGetJob(requestId, out JobRecord record))
                {
                    JObject persisted = LoadPersistedGenerationJob(requestId);
                    persisted["canceled"] = false;
                    return Ok(persisted);
                }
                string reason = arguments.Value<string>("reason")?.Trim();
                bool canceled = command_generation_runner.Cancel(
                    requestId,
                    string.IsNullOrWhiteSpace(reason) ? "Generation canceled by command." : reason);
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
                RejectTimelineSessionId(arguments);
                string prompt = RequiredStringValue(arguments, "prompt");
                TimelineSessionRecord session = EnsureGenerationTimelineSession();
                ResolvedCharacter character = ResolveCharacter(RequiredStringValue(arguments, "character"));
                if (command_generation_runner.TryGet(character.Target, out command_generation_session activeGeneration) &&
                    activeGeneration != null && activeGeneration.IsRunning)
                {
                    throw new InvalidOperationException($"A generation session is already running for '{character.Name}'.");
                }
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
                // Loop preprocessing is deliberately bounded by the QuickServer 10 second limit.
                // For over-limit requests, preserve the normal generation path and report a warning.
                bool loopRequested = arguments.Value<bool?>("loop") ??
                    prompt.IndexOf("loop", StringComparison.OrdinalIgnoreCase) >= 0;
                bool loopFallback = false;
                string loopWarning = null;
                int requestedDurationFrames = durationFrames;
                if (loopRequested)
                {
                    long extended = (long)durationFrames * 2L;
                    if (extended > 600L)
                    {
                        loopFallback = true;
                        loopWarning = $"loop_requested_but_exceeds_max_duration: requested={durationFrames} frames, extended={extended} frames, max=600. Fallback to default generation.";
                        Debug.LogWarning("[Kimodo][Command] " + loopWarning);
                    }
                    else
                    {
                        durationFrames = (int)extended;
                        if (arguments["constraints"] is JArray loopConstraints)
                        {
                            int padding = requestedDurationFrames / 2;
                            foreach (JObject item in loopConstraints.OfType<JObject>().ToList())
                            {
                                item["frame"] = (item.Value<int?>("frame") ?? 0) + padding;
                            }
                            AddLoopBezierRootConstraints(loopConstraints, requestedDurationFrames, padding);
                        }
                    }
                    // Successful loop requests use the extended interval and injected Root2D
                    // samples; over-limit requests intentionally remain on the default path.
                }
                float duration = (float)(durationFrames / SessionFrameRate);
                string analysisOptionsJson = ParseAnalysisOptionsJson(arguments);
                int frameCount = Math.Max(1, KimodoFrameTimeUtility.SecondsToFrameCount(duration, frameRate));
                int seed = arguments.Value<int?>("seed") ?? (Guid.NewGuid().GetHashCode() & int.MaxValue);
                int steps = ResolveDiffusionSteps(arguments, modelName, modelConfiguration);
                string outputFolder = NormalizeOutputFolder(arguments.Value<string>("output_folder"));
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
                playableClip.analysisOptionsJson = analysisOptionsJson;
                playableClip.generatedAssetName = trace.Animation.Name;
                playableClip.generatedOutputFolder = outputFolder;
                playableClip.generationOutputMode = ParseGenerationOutputMode(outputMode);
                WriteGenerationConstraintMarkers(trace, poseConstraints, (float)SessionFrameRate);
                ReserveGenerationTimelineRange(trace);
                SaveTimelineSession(session);

                bool started = command_generation_runner.Start(
                    character.Target,
                    $"command-asset:{KimodoUnityObjectIdUtility.NameKey(character.Target)}",
                    command_kind.GenerateAnimationAsset,
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
                            FinishAutomaticTimelineSession(trace, generationSession.RequestId);
                        }
                    },
                    out command_generation_session generation,
                    out string error);
                if (!started)
                {
                    throw new InvalidOperationException(error);
                }

                Remember(character.Target, generation, trace);
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
                    startedResponse["temporary_session"] = trace.Session.IsAutomatic;
                    startedResponse["start_frame"] = Mathf.RoundToInt((float)(trace.StartSeconds * SessionFrameRate));
                    startedResponse["duration_frames"] = Mathf.RoundToInt((float)(trace.DurationSeconds * SessionFrameRate));
                }
                if (loopRequested && !loopFallback)
                {
                    startedResponse["loop"] = true;
                    startedResponse["loop_source_duration_frames"] = requestedDurationFrames;
                    startedResponse["loop_extended_duration_frames"] = durationFrames;
                }
                if (loopWarning != null)
                {
                    startedResponse["warnings"] = new JArray(loopWarning);
                    startedResponse["loop_fallback"] = loopFallback;
                }
                return Started(generation, startedResponse);
            });
        }

        private static async Task<command_generate_result> ExecutePlayableClipGenerationAsync(
            KimodoPlayableClip playableClip,
            TimelineGenerationTrace trace,
            UnityEngine.Object target,
            command_generation_session session,
            CancellationToken token)
        {
            command_generate_result result = await KimodoPlayableClipGenerationExecutionService.GenerateAndFinalizeAsync(
                playableClip,
                externalConstraint: null,
                (stage, message) => command_generation_runner.UpdateProgress(target, session.RequestId, stage, message),
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
            var samples = new List<KimodoMarkerSampleResult>(constraints.Count * 2);
            SkeletonCache targetCache = null;
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            KimodoTimelineEvaluationScope evaluation = KimodoTimelineEvaluationScope.Begin(session.Director);
            try
            {
                if (constraints.Count > 0 &&
                    !KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
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
                float targetRootHeight = ResolveTargetRootHeight(targetCache, modelName);

                for (int i = 0; i < constraints.Count; i++)
                {
                    if (constraints[i] is not JObject constraint)
                    {
                        throw new InvalidOperationException($"constraints[{i}] must be an object.");
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
                    KimodoMarkerSampleResult rootSample = root2D != null
                        ? BuildRoot2DConstraintSample(root2D, targetRootHeight, at, i)
                        : null;
                    KimodoMarkerSampleResult fullBodyPose = fullBody != null
                        ? ReadConstraintPose(fullBody, $"constraints[{i}].fullbody")
                        : null;
                    if (fullBodyPose != null && rootSample != null)
                    {
                        ApplyRoot2DOverride(fullBodyPose, rootSample);
                    }
                    if (fullBodyPose != null)
                    {
                        samples.Add(BuildProfileConstraintSample(
                            fullBodyPose, "fullbody", fullBody, targetCache, modelName, at, i));
                    }
                    if (rootSample != null)
                    {
                        samples.Add(rootSample);
                    }

                    for (int part = 0; part < endEffectors.Length; part++)
                    {
                        JObject value = endEffectors[part];
                        if (value == null)
                        {
                            continue;
                        }
                        KimodoMarkerSampleResult pose = value["pose"] is JObject
                            ? ReadConstraintPose(value, $"constraints[{i}].{endEffectorTypes[part].Replace('-', '_')}")
                            : fullBodyPose?.Clone();
                        pose ??= new KimodoMarkerSampleResult();
                        if (rootSample != null)
                        {
                            ApplyRoot2DOverride(pose, rootSample);
                        }
                        samples.Add(BuildProfileConstraintSample(
                            pose, endEffectorTypes[part], value, targetCache, modelName, at, i));
                    }
                }
            }
            finally
            {
                targetCache?.Dispose();
                evaluation.Dispose();
            }
            return samples;
        }

        private static void AddLoopBezierRootConstraints(JArray constraints, int sourceDuration, int padding)
        {
            JObject first = constraints.OfType<JObject>().OrderBy(x => x.Value<int?>("frame") ?? int.MaxValue).FirstOrDefault(x => x["root2d"] != null);
            JObject last = constraints.OfType<JObject>().OrderByDescending(x => x.Value<int?>("frame") ?? int.MinValue).FirstOrDefault(x => x["root2d"] != null);
            if (first == null || last == null) return;
            Vector2 a = ReadLoopPosition(first["root2d"] as JObject);
            Vector2 b = ReadLoopPosition(last["root2d"] as JObject);
            Vector2 d = (b - a) / 3f;
            for (int i = 0; i < padding; i++)
            {
                float t = (i + 1f) / (padding + 1f);
                Vector2 pre = LoopBezier(b, b - d, a + d, a, t);
                constraints.Add(CreateLoopRootConstraint(i, pre, a - b));
                Vector2 post = LoopBezier(b, b - d, a + d, a, t);
                constraints.Add(CreateLoopRootConstraint(padding + sourceDuration + i, post, a - b));
            }
        }

        private static JObject CreateLoopRootConstraint(int frame, Vector2 position, Vector2 heading)
        {
            return new JObject
            {
                ["frame"] = frame,
                ["root2d"] = new JObject
                {
                    ["position"] = new JObject { ["x"] = position.x, ["y"] = position.y },
                    ["heading"] = new JObject { ["x"] = heading.x, ["y"] = heading.y }
                }
            };
        }

        private static Vector2 ReadLoopPosition(JObject root)
        {
            JObject p = root?["position"] as JObject;
            return new Vector2(p?.Value<float?>("x") ?? 0f, p?.Value<float?>("y") ?? 0f);
        }

        private static KimodoMarkerSampleResult ReadConstraintPose(JObject value, string path)
        {
            if (value?["pose"] is not JObject pose)
            {
                throw new InvalidOperationException($"{path}.pose is required.");
            }
            return ReadPoseSample(RequirePoseLocator(pose)).Clone();
        }

        private static KimodoMarkerSampleResult BuildRoot2DConstraintSample(
            JObject value,
            float targetRootHeight,
            double sampleTime,
            int constraintIndex)
        {
            KimodoMarkerSampleResult sample = value["pose"] is JObject pose
                ? ReadPoseSample(RequirePoseLocator(pose)).Clone()
                : new KimodoMarkerSampleResult();
            bool hasPosition = value["position"] != null;
            bool hasHeading = value["heading"] != null;
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
                sample.kimodoRootPosition = new Vector3(position.x, targetRootHeight, position.y);
                sample.rootHeading = heading.normalized;
                sample.hasRootHeading = true;
            }
            else if (value["pose"] == null)
            {
                throw new InvalidOperationException($"constraints[{constraintIndex}].root2d requires pose or position plus heading.");
            }
            sample.constraintType = "root2d";
            sample.sampleTime = sampleTime;
            return sample;
        }

        private static KimodoMarkerSampleResult BuildProfileConstraintSample(
            KimodoMarkerSampleResult source,
            string constraintType,
            JObject value,
            SkeletonCache targetCache,
            string modelName,
            double sampleTime,
            int constraintIndex)
        {
            bool hasPose = value?["pose"] is JObject;
            bool hasPosition = value?["position"] != null;
            bool hasRotation = value?["rotation"] != null;
            if (constraintType != "fullbody" && !hasPose && !hasPosition && !hasRotation)
            {
                throw new InvalidOperationException(
                    $"constraints[{constraintIndex}].{constraintType.Replace('-', '_')} requires pose, position, or rotation.");
            }

            KimodoRetargetClipSamplingUtility.ResetSkeletonCachePose(targetCache);
            if (source?.muscles != null && source.muscles.Count == HumanTrait.MuscleCount)
            {
                var humanPose = new HumanPose { muscles = source.muscles.ToArray() };
                targetCache.poseHandler.SetHumanPose(ref humanPose);
            }
            if (hasRotation)
            {
                if (!KimodoMarkerSamplingUtility.TryResolveEndEffectorBone(
                        constraintType,
                        out HumanBodyBones bone) ||
                    targetCache.humanBoneTransforms == null ||
                    !targetCache.humanBoneTransforms.TryGetValue(bone, out Transform endEffector) ||
                    endEffector == null)
                {
                    throw new InvalidOperationException(
                        $"constraints[{constraintIndex}].{constraintType.Replace('-', '_')}.rotation cannot resolve its profile bone.");
                }
                endEffector.rotation = RequiredQuaternion(value, "rotation");
            }

            BoneSample boneSample = KimodoRetargetSamplingUtility.CaptureBoneSample(targetCache);
            if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    boneSample,
                    targetCache,
                    modelName,
                    constraintType,
                    sampleTime,
                    out KimodoMarkerSampleResult converted,
                    out string error))
            {
                throw new InvalidOperationException($"Convert constraints[{constraintIndex}] failed: {error}");
            }
            PreservePoseConstraintRoot(source, converted);
            if (constraintType == "fullbody" && source?.muscles != null)
            {
                converted.muscles = new List<float>(source.muscles);
            }
            if (hasPosition)
            {
                Vector3 target = RequiredVector3(value, "position");
                Quaternion rootRotation = converted.localAxisAngles != null && converted.localAxisAngles.Count > 0
                    ? KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(converted.localAxisAngles[0])
                    : Quaternion.identity;
                converted.hasEndEffectorTargetPosition = true;
                converted.endEffectorTargetPositionRootLocal = Quaternion.Inverse(rootRotation) *
                    (target - converted.kimodoRootPosition);
            }
            converted.sampleTime = sampleTime;
            return converted;
        }

        private static void ApplyRoot2DOverride(
            KimodoMarkerSampleResult destination,
            KimodoMarkerSampleResult root2D)
        {
            destination.kimodoRootPosition = new Vector3(
                root2D.kimodoRootPosition.x,
                destination.kimodoRootPosition.y,
                root2D.kimodoRootPosition.z);
            destination.rootHeading = root2D.rootHeading;
            destination.hasRootHeading = root2D.hasRootHeading;
            if (!root2D.hasRootHeading)
            {
                return;
            }
            destination.localAxisAngles ??= new List<Vector3>();
            if (destination.localAxisAngles.Count == 0)
            {
                destination.localAxisAngles.Add(Vector3.zero);
            }
            Quaternion rotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(destination.localAxisAngles[0]);
            float yaw = Mathf.Atan2(root2D.rootHeading.x, root2D.rootHeading.y) * Mathf.Rad2Deg;
            destination.localAxisAngles[0] = KimodoRuntimeUtility.QuaternionToAxisAngleVector(
                ApplyPoseRootYaw(rotation, yaw));
        }

        private static Vector2 RequiredVector2(JObject value, string name)
        {
            if (value?[name] is not JArray array || array.Count != 2)
            {
                throw new InvalidOperationException($"{name} must be a two-number array.");
            }
            float x = array[0].Value<float>();
            float y = array[1].Value<float>();
            if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y))
            {
                throw new InvalidOperationException($"{name} values must be finite.");
            }
            return new Vector2(x, y);
        }

        internal static void PreservePoseConstraintRoot(
            KimodoMarkerSampleResult source,
            KimodoMarkerSampleResult destination)
        {
            destination.kimodoRootPosition = source.kimodoRootPosition;
            destination.rootHeading = source.rootHeading;
            destination.hasRootHeading = source.hasRootHeading;
            if (source.localAxisAngles != null && source.localAxisAngles.Count > 0)
            {
                destination.localAxisAngles ??= new List<Vector3>();
                if (destination.localAxisAngles.Count == 0)
                {
                    destination.localAxisAngles.Add(source.localAxisAngles[0]);
                }
                else
                {
                    destination.localAxisAngles[0] = source.localAxisAngles[0];
                }
            }
        }

        internal static float ResolveTargetRootHeight(SkeletonCache targetCache, string modelName)
        {
            if (targetCache != null &&
                KimodoRetargetAvatarUtility.TryGetProfileRootJointTransform(
                    targetCache.uniqueNameMap,
                    modelName,
                    out Transform profileRootJoint) &&
                profileRootJoint != null)
            {
                return profileRootJoint.position.y;
            }

            return 0f;
        }

        private static bool TrySampleDirectSkeletonConstraint(
            TimelineCharacterRecord source,
            SkeletonCache targetCache,
            string modelName,
            string constraintType,
            double sampleTime,
            out KimodoMarkerSampleResult sample,
            out string error)
        {
            sample = null;
            error = string.Empty;
            if (source?.Root == null || !KimodoRetargetAvatarUtility.ValidateRetargetCache(targetCache, out error))
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

            KimodoRetargetClipSamplingUtility.ResetSkeletonCachePose(targetCache);
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
            sample.unityRootPos = source.Animator != null ? source.Animator.transform.position : source.Root.transform.position;
            sample.unityRootRot = source.Animator != null ? source.Animator.transform.rotation : source.Root.transform.rotation;
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

        private static ResolvedCharacter ResolveCharacter(string name)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord sessionCharacter = session.Characters.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            GameObject root = sessionCharacter?.Root;
            if (root == null)
            {
                Animator[] matches = FindSceneAnimators().Where(item =>
                    string.Equals(item.gameObject.name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1)
                {
                    throw new InvalidOperationException(matches.Length == 0
                        ? $"Character '{name}' was not found in the current Session or scene."
                        : $"Scene character name '{name}' is ambiguous; add or rename it before use.");
                }
                root = matches[0].gameObject;
            }
            if (EditorUtility.IsPersistent(root) || !root.scene.IsValid())
            {
                throw new InvalidOperationException("character must name a scene character.");
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

            sessionCharacter = session.Characters.FirstOrDefault(item => item.Animator == animator);
            if (sessionCharacter == null)
            {
                if (!AddCharacterTrack(session, root, animator, true, out string addError, requireAvatar: true))
                {
                    throw new InvalidOperationException($"Character could not be appended to the current Session: {addError}");
                }
                sessionCharacter = session.Characters.FirstOrDefault(item => item.Animator == animator);
            }
            else if (KimodoRetargetCoreUtility.IsValidHumanoid(avatarResult.Avatar) &&
                !KimodoRetargetCoreUtility.IsValidHumanoid(sessionCharacter.Avatar))
            {
                sessionCharacter.Avatar = avatarResult.Avatar;
                sessionCharacter.AvatarError = string.Empty;
                session.Director.SetGenericBinding(sessionCharacter.Track, animator);
            }
            if (sessionCharacter == null || !KimodoRetargetCoreUtility.IsValidHumanoid(sessionCharacter.Avatar))
            {
                throw new InvalidOperationException($"Character '{root.name}' requires a valid humanoid Avatar in the current Session.");
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
                ["name"] = root.name,
                ["source"] = source,
                ["avatar"] = resolvedAvatar != null ? resolvedAvatar.name : string.Empty,
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

        private static string Error(GenerationRangeLockedException error)
        {
            return new JObject
            {
                ["ok"] = false,
                ["code"] = "generation_range_locked",
                ["error"] = error.Message,
                ["command"] = error.Command,
                ["request_id"] = error.RequestId.ToString("D"),
                ["character"] = error.Character,
                ["track"] = error.Track,
                ["locked_range"] = new JArray(error.LockedStartFrame, error.LockedEndFrame),
                ["requested_range"] = new JArray(error.RequestedStartFrame, error.RequestedEndFrame),
                ["action"] = $"Wait for generation completion or cancel request {error.RequestId:D}."
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
                    throw new InvalidOperationException("model must be a registered model name/configuration id from kimodo_help section models, not a filesystem path.");
                }
            }
            return KimodoPlayableClip.NormalizeBridgeModelName(string.IsNullOrWhiteSpace(modelName)
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

        internal static void PersistGenerationJobStatus(command_generation_session session)
        {
            if (session == null)
            {
                return;
            }
            JObject status;
            lock (JobsLock)
            {
                status = Jobs.TryGetValue(session.RequestId, out JobRecord record)
                    ? BuildStatus(record)
                    : new JObject
                    {
                        ["request_id"] = session.RequestId.ToString("D"),
                        ["status"] = session.Status.ToString().ToLowerInvariant(),
                        ["stage"] = session.Stage.ToString(),
                        ["message"] = session.Message ?? string.Empty,
                        ["error"] = session.Error ?? string.Empty,
                        ["started_at_utc"] = session.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture)
                    };
            }
            string folder = System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? Application.temporaryCachePath,
                "Library", "KimodoCache", "Commands");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllText(GenerationJobPath(folder, session.RequestId), status.ToString(Formatting.Indented));
        }

        private static JObject LoadPersistedGenerationJob(Guid requestId)
        {
            string folder = System.IO.Path.Combine(System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? Application.temporaryCachePath,
                "Library", "KimodoCache", "Commands");
            string path = GenerationJobPath(folder, requestId);
            if (!System.IO.File.Exists(path))
            {
                throw new InvalidOperationException($"Unknown or expired request_id '{requestId}'.");
            }
            JObject status = JObject.Parse(System.IO.File.ReadAllText(path));
            status.Remove("asset_path");
            status.Remove("raw_bone_asset_path");
            status.Remove("timeline_clip_asset_ref");
            status.Remove("analysis_track_ref");
            status.Remove("animation_id");
            return status;
        }

        private static string GenerationJobPath(string folder, Guid requestId) =>
            System.IO.Path.Combine(folder, $"generation_{requestId:D}.json");

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
                : KimodoPlayableClip.FIXED_FRAME_RATE;
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

        private static PropertyDefinition OptionalConstraints(string name, string description)
        {
            JObject poseLocator = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["source"] = new JObject { ["type"] = "string" },
                    ["frame"] = new JObject { ["type"] = "integer" }
                },
                ["required"] = new JArray("source", "frame")
            };
            JObject vector2 = NumberArray(2);
            JObject vector3 = NumberArray(3);
            JObject quaternion = NumberArray(4);
            JObject fullBody = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject { ["pose"] = poseLocator.DeepClone() },
                ["required"] = new JArray("pose")
            };
            JObject root2D = new JObject
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
            JObject endEffector = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["pose"] = poseLocator.DeepClone(),
                    ["position"] = vector3,
                    ["rotation"] = quaternion
                },
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("pose") },
                    new JObject { ["required"] = new JArray("position") },
                    new JObject { ["required"] = new JArray("rotation") })
            };
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["description"] = description + " Coordinates use +X right, +Y up, +Z forward; rotations are [x,y,z,w].",
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["frame"] = new JObject { ["type"] = "integer", ["description"] = "Relative frame in the generated clip at 60 FPS." },
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
                }
            }, false);
        }

        private static JObject NumberArray(int count) => new JObject
        {
            ["type"] = "array",
            ["items"] = new JObject { ["type"] = "number" },
            ["minItems"] = count,
            ["maxItems"] = count
        };

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
                command_generation_session session,
                TimelineGenerationTrace timelineGenerationTrace)
            {
                Target = target;
                Session = session;
                TimelineGenerationTrace = timelineGenerationTrace;
            }

            public UnityEngine.Object Target { get; }
            public command_generation_session Session { get; }
            public TimelineGenerationTrace TimelineGenerationTrace { get; }
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





