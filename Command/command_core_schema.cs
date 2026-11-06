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
        private static CommandCatalog BuildCommandCatalog()
        {
            JObject document = JObject.Parse(GetCommandDefinitionsJson());
            var handlers = new Dictionary<string, Func<JObject, string>>(StringComparer.Ordinal)
            {
                [HelpCommand] = GetCommandHelp,
                [SceneContextCommand] = SceneContextGet,
                [SessionHistoryCommand] = SessionHistoryGet,
                [InstallServerCommand] = InstallServer,
                [PoseFromVideoCommand] = arguments => StartMocapAnalysis(arguments, "video"),
                [PoseFromPictureCommand] = arguments => StartMocapAnalysis(arguments, "picture"),
                [AnimationAnalysisCommand] = AnimationAnalysis,
                [ClipInspectCommand] = ClipInspect,
                [ClipSettingsSetCommand] = ClipSettingsSet,
                [PoseAnalysisCommand] = PoseAnalysis,
                [RetargetAnimationCommand] = RetargetAnimation,
                [GenerateAnimationCommand] = GenerateAnimationAsset,
                [PoseGetCommand] = PoseGet,
                [PoseSetCommand] = PoseSet,
                [GetGenerationCommand] = GetGeneration,
                [CancelGenerationCommand] = CancelGeneration,
                [SessionClearCommand] = ClearSession,
            };

            var registrations = new List<CommandRegistration>();
            foreach (JObject definition in document["tools"]?.Values<JObject>() ?? Enumerable.Empty<JObject>())
            {
                string name = definition.Value<string>("name");
                if (string.IsNullOrWhiteSpace(name) || !handlers.TryGetValue(name, out Func<JObject, string> handler))
                {
                    throw new InvalidOperationException($"Command definition '{name ?? string.Empty}' has no handler.");
                }
                registrations.Add(new CommandRegistration(definition, handler));
            }

            if (registrations.Count != handlers.Count)
            {
                throw new InvalidOperationException("Command definitions and handlers are out of sync.");
            }
            return new CommandCatalog(registrations);
        }

        [MenuItem("Kimodo/Command/Export Help JSON")]
        public static void ExportCommandDefinitionsJson()
        {
            UnityEditor.PackageManager.PackageInfo package =
                UnityEditor.PackageManager.PackageInfo.FindForAssetPath(HelpAssetPath);
            if (package == null || string.IsNullOrWhiteSpace(package.resolvedPath))
            {
                throw new InvalidOperationException($"Kimodo package was not found for '{HelpAssetPath}'.");
            }

            File.WriteAllText(
                Path.Combine(package.resolvedPath, "Command", "help.json"),
                JObject.Parse(BuildCommandDefinitionsJson()).ToString(Formatting.Indented));
            AssetDatabase.Refresh();
        }

        private static JObject CommandDefinition(string name, string description, JObject inputSchema)
        {
            return new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["documentation"] = BuildCommandDocumentation(name),
                ["inputSchema"] = inputSchema,
                ["outputSchema"] = BuildCommandOutputSchema(name),
                ["examples"] = BuildCommandExamples(name)
            };
        }

        private static JObject BuildCommandDocumentation(string command)
        {
            JObject documentation = command switch
            {
                HelpCommand => CommandDocumentation(
                    "Discover command schemas and shared rules, inspect one command, list viable models, or review generation constraints.",
                    new[] { "Set command for its full definition; otherwise section selects the command overview, model list, or constraint reference." },
                    new[] { "Command details include input/output schemas, examples, and applicable shared rules." },
                    new[] { "Overview and command details are read-only; section=models queries the configured generation service." },
                    new[] { "The models section reports current availability and may change with runtime configuration." },
                    SceneContextCommand, SessionHistoryCommand, GenerateAnimationCommand),
                SceneContextCommand => CommandDocumentation(
                    "Read the active Unity scene, its Kimodo Timeline, character handles, and current Session identity; optionally reload and open a Timeline asset.",
                    new[] { "Use returned scene object references in later commands.", "Set timeline_path to open an Assets/... or Packages/... Timeline; same-name root tracks bind to scene characters when available." },
                    new[] { "With no timeline_path, returns the current live scene context and creates its root when needed.", "With timeline_path, binds the loaded Timeline, opens it in the Timeline window, and returns opened_timeline details. Same-name scene characters are bound when available." },
                    new[] { "The context uses the active scene root, Timeline binding, and explicit JSON write path. Commands read the scene Timeline structure and write resulting snapshots through that path. Opening a Timeline asset preserves its asset data." },
                    new[] { "Context reflects the active scene and current Session. Use scene handles for Timeline objects and asset paths for project assets." },
                    GenerateAnimationCommand, AnimationAnalysisCommand, PoseGetCommand),
                SessionHistoryCommand => CommandDocumentation(
                    "Read the compact generation and animation analysis indexes for the active scene Session.",
                    new[] { "Use scene.context for active Session identity and Timeline handles.", "Use this command when generation or analysis record indexes are needed." },
                    new[] { "Returns generation and analysis indexes built from the original record files, plus the active Session identity." },
                    new[] { "Reads the recorded indexes for the active scene Session." },
                    new[] { "For full analysis keyframes and pictures, follow analysis_path. Use scene.context for current Timeline handles." },
                    SceneContextCommand, GetGenerationCommand, AnimationAnalysisCommand),
                InstallServerCommand => CommandDocumentation(
                    "Install or refresh the project-local QuickServer runtime before generation when it is unavailable or needs updating.",
                    new[] { "Starts an asynchronous install task." },
                    new[] { "Returns the request handle and status; generation.get reports progress and completion details." },
                    new[] { "Updates project-local runtime files while retaining existing model files and the Python environment." },
                    new[] { "Use this command when the local QuickServer runtime is unavailable or needs updating." },
                    GetGenerationCommand),
                AnimationAnalysisCommand => CommandDocumentation(
                    "Analyze one loaded animation Clip and produce structured motion data with a 20-tile SceneView image.",
                    new[] { "Select a scene character and one loaded Clip name or Assets/Packages asset path; use one call per Clip.", "Inspect returned pictures for visual observations and structured fields for measurements." },
                    new[] { "Returns one structured Clip analysis and one 20-tile pictures object from the synchronous SceneView renderer. Frame positions use absolute Timeline coordinates.", "Phase and foot-contact durations use duration_frames_60; foot contacts expose timeline_frame_60. These frame values use the 60 FPS command/session basis.", "Root trajectory uses duration_frames_60 and sample_span_frames_60 on the 60 FPS command/session basis.", "Accepts the same camera object as pose.analysis. Unspecified camera settings retain their current values." },
                    new[] { "An external Clip asset is added to the character's Kimodo Timeline for analysis; the original asset remains unchanged." },
                    new[] { "Use temporal analysis fields for continuity, velocity, foot contact, and other time-based properties." },
                    SceneContextCommand, PoseGetCommand, PoseAnalysisCommand),
                ClipInspectCommand => CommandDocumentation(
                    "Inspect one AnimationClip using the shared Inspector mapping used by the Unity panel.",
                    new[] { "Pass a unique Clip name or an Assets/Packages asset path." },
                    new[] { "Returns Clip settings, root-motion curves, measured XZ displacement, derived speed, rig type, curve count, and static AnimatorController consumers." },
                    new[] { "Reports Clip data while leaving the selected assets and scene unchanged." },
                    new[] { "Consumers list static AnimatorController asset references." },
                    ClipSettingsSetCommand, AnimationAnalysisCommand),
                ClipSettingsSetCommand => CommandDocumentation(
                    "Write supported AnimationClip settings and return Unity's values after the write.",
                    new[] { "Pass settings.loop_time, settings.keep_original_position_xz, or settings.root_motion_enabled." },
                    new[] { "Returns the same shared inspection payload used by clip.inspect and the Inspector panel." },
                    new[] { "Writes the selected AnimationClip asset settings and saves the serialized values." },
                    new[] { "Provide one or more supported settings; root_motion_enabled is the inverse of keep_original_position_xz." },
                    ClipInspectCommand),
                PoseAnalysisCommand => CommandDocumentation(
                    "Preview point constraints for one scene character when pose inspection is useful; capture sampled Humanoid poses and measure world-space joints and effector target distances.",
                    new[] { "Pass the same point constraints array that will be sent to animation.generate when previewing those constraints. Every entry is previewed in isolation by default; combine=true renders all opaque pose previews together.", "Use transforms when additional scene objects should be framed with each pose." },
                    new[] { "Returns one image per constraint by default, or one shared image with per-constraint measured joint positions when combine=true.", "Effector target distances use a 0.05 m default tolerance; the call is synchronous and returns no request_id.", "SceneView lighting, skybox, gizmos, and grid keep their current state unless explicitly set." },
                    new[] { "SceneView camera position, projection and settings remain available for inspection. Renderer visibility is restored after capture." },
                    new[] { "Requires a valid Humanoid character. If no Scene View is active, the command opens one before capture. This command previews point constraints; use animation.generate for inout and root_path constraints." },
                    PoseGetCommand, AnimationAnalysisCommand, GenerateAnimationCommand),
                RetargetAnimationCommand => CommandDocumentation(
                    "Retarget one loaded source Clip to another character in the current scene context.",
                    new[] { "Resolve both characters and the source Clip from current scene context before submission." },
                    new[] { "Returns the source character, target character, and the newly retargeted animation reference." },
                    new[] { "Appends a derived animation to the target character; the source Clip remains available." },
                    new[] { "Source and target resolve to supported scene characters from scene.context." },
                    SceneContextCommand, AnimationAnalysisCommand),
                GenerateAnimationCommand => CommandDocumentation(
                    "Submit one animation generation request for a character in the current scene context.",
                    new[] { "Describe one semantic action in prompt, or supply raw_instruction.role=generate with the exact JSON returned by generation.get(include_request_json=true).", "Use structured fields for exact pose, root, effector, duration, trajectory, loop, and Clip-window requirements; see kimodo.help(section=constraints) for constraint selection.", "Use point constraints and source Clip windows as documented in kimodo.help(section=constraints).", "Use wait_seconds for cooperative Editor waiting; generation.get continues a running task." },
                    new[] { "The accepted response contains request_id, status, preflight check results, and warnings. Root speed above 7 m/s warns; above 10 m/s blocks generation. Root acceleration above 10 m/s² warns; above 15 m/s blocks generation. Use the returned preflight diagnostics to assess pose/effector constraints; use pose.analysis when measured sampled joint positions are needed.", "On completion, generation.get returns Clip metadata, asset path, animation_id, generation diagnostics, and post_analysis.analysis_path when analysis is enabled. Set analyze=false to omit the post-generation analysis pass." },
                    new[] { "A completed result is appended as a Timeline Clip; existing completed Clips are retained.", "When no explicit first-frame or In/Out anchor takes precedence, AutoBegin adds a Root2D constraint at the character's original scene Hips position and heading." },
                    new[] { "Map explicit path and direction requirements to supported trajectory fields; use confirmed scene/analysis evidence to resolve details the prompt leaves unspecified. Leave other optional fields at their schema defaults.", "analyze controls the full post-generation animation.analysis pass and defaults to true.", "Command checks validate supported request boundaries. Evaluate semantic and artistic quality with the task's acceptance criteria and analysis evidence." },
                    SceneContextCommand, PoseGetCommand, GetGenerationCommand, CancelGenerationCommand, AnimationAnalysisCommand),
                PoseGetCommand => CommandDocumentation(
                    "Materialize an External Pose from a Timeline frame, the current scene Animator pose, or the Avatar skeleton rest pose.",
                    new[] { "For an animation frame, pass its absolute Timeline-global frame at 60 FPS.", "Inspect the returned Pose and confirm its role before using its {track,index} reference as a generation constraint." },
                    new[] { "Returns an External Pose reference, source details, character_root world Transform, and sampled Pose data.", "character_root is the bound scene GameObject root; data.root is the sampled Pose root channel." },
                    new[] { "Each call creates an External Pose slot in the current scene context." },
                    new[] { "Choose one source form: Timeline frame, scene_pose, or skeleton_rest_pose.", "Set full_data=true to include all muscle and TQ channels." },
                    SceneContextCommand, AnimationAnalysisCommand, PoseAnalysisCommand, PoseSetCommand),
                PoseSetCommand => CommandDocumentation(
                    "Update the root transform, muscle channels, or effector targets of an existing External Pose slot.",
                    new[] { "Use a {track,index} reference returned by pose.get or another Pose editing command.", "Supply one or more of root, muscles, or effector; multiple supplied channels are updated together." },
                    new[] { "Returns the Pose reference and the updated Pose data." },
                    new[] { "Mutates the referenced External Pose slot in the current scene context." },
                    new[] { "The command edits Pose data; animation generation is handled by animation.generate." },
                    PoseGetCommand, PoseSetCommand),
                GetGenerationCommand => CommandDocumentation(
                    "Read progress, terminal status, and results for an installation or generation request.",
                    new[] { "Pass the request_id returned by a task submission." },
                    new[] { "Returns lifecycle status, stage, progress, and error details; completed generation includes Clip metadata and available diagnostics.", "Set include_request_json=true to read the exact submitted backend payload." },
                    new[] { "The command reads task state and results." },
                    new[] { "Recent task records are retained up to the configured job limit." },
                    InstallServerCommand, GenerateAnimationCommand, CancelGenerationCommand),
                PoseFromVideoCommand => CommandDocumentation(
                    "Use when a video file should be converted into a time-ordered SMPL-X pose array for downstream retargeting or analysis.",
                    new[] { "Pass an absolute video_path readable by QuickServer. Supported formats are MP4, MOV, M4V, AVI, and MKV; maximum file size is 2 GiB.", "The command returns status=accepted and a request_id for continued progress tracking through generation.get.", "For two people, set person to left or right to select the performer by screen position." },
                    new[] { "A completed request returns poses in source-video order plus one top-level constraints array, fps, frame_count, source_frame_count, joint_names, betas, and rest_joints_m.", "Each pose contains raw global_orient_axis_angle, body_pose_axis_angle, and translation_m values. The top-level constraints array uses the same merged raw FullBody constraint format accepted by animation generation.", "Rotations are axis-angle radians; translation and rest joints are in meters. The global coordinate space is gravity-aligned Y-up. Optional incam_* fields use OpenCV camera coordinates: X-right, Y-down, Z-forward.", "The result is pose data for downstream retargeting and constraint use." },
                    new[] { "The result is data for downstream commands and leaves Unity scene objects and animation assets unchanged." },
                    new[] { "The top-level constraints array groups the sequence in the raw FullBody format for animation.generate.", "The request requires QuickServer's GVHMR runtime, checkpoints, and SMPL-X Avatar asset." },
                    PoseFromPictureCommand, GetGenerationCommand, RetargetAnimationCommand),
                PoseFromPictureCommand => CommandDocumentation(
                    "Use when a still image should be converted into a single SMPL-X pose sample for downstream retargeting or analysis.",
                    new[] { "Pass an absolute image_path for a PNG, JPG, or JPEG file readable by QuickServer; maximum file size is 2 GiB.", "The command returns status=accepted and a request_id for continued progress tracking through generation.get.", "The estimator treats the still image as a one-second clip and returns one pose sample." },
                    new[] { "A completed request returns one raw SMPL-X pose plus a top-level constraints array containing the matching raw FullBody constraint, along with joint_names, betas, and rest_joints_m.", "The pose contains raw global_orient_axis_angle, body_pose_axis_angle, and translation_m values. The constraints array uses the same format accepted by animation generation.", "Rotations are axis-angle radians; translation and rest joints are in meters. The global coordinate space is gravity-aligned Y-up. Optional incam_* fields use OpenCV camera coordinates: X-right, Y-down, Z-forward.", "The result is pose data for downstream retargeting and constraint use." },
                    new[] { "The result is data for downstream commands and leaves Unity scene objects and animation assets unchanged." },
                    new[] { "The constraints array contains one raw FullBody frame for animation.generate.", "The request requires QuickServer's GVHMR runtime, checkpoints, and SMPL-X Avatar asset." },
                    PoseFromVideoCommand, GetGenerationCommand, RetargetAnimationCommand),
                CancelGenerationCommand => CommandDocumentation(
                    "Request cancellation of an active animation generation.",
                    new[] { "Pass the exact generation request_id, then poll with generation.get to confirm its terminal state." },
                    new[] { "Returns the compact cancellation/lifecycle result and any completed Clip metadata." },
                    new[] { "May stop active generation work; completed Clips remain available." },
                    new[] { "Use generation.get for installation task status.", "Read the returned status to confirm the cancellation state." },
                    GetGenerationCommand, GenerateAnimationCommand),
                SessionClearCommand => CommandDocumentation(
                    "Clear transient Kimodo context objects from the active scene Session.",
                    new[] { "Use when the current transient Session context must be reset." },
                    new[] { "Returns whether the context was cleared, how many context objects were destroyed, and lazy recreation information." },
                    new[] { "Destroys scene-bound context GameObjects; persisted Timeline, Session JSON, and animation assets are retained." },
                    new[] { "Call scene.context after clearing to obtain fresh scene references." },
                    SceneContextCommand),
                _ => throw new InvalidOperationException($"Unknown command '{command}'.")
            };
            documentation["shared_rule_refs"] = new JArray(SharedRuleRefs(command));
            return documentation;
        }

        private static string[] SharedRuleRefs(string command)
        {
            return command switch
            {
                HelpCommand => new[] { "scene_references", "job_lifecycle", "frame_coordinates", "evidence", "operation_scope" },
                SceneContextCommand or SessionHistoryCommand => new[] { "scene_references", "operation_scope" },
                InstallServerCommand or GetGenerationCommand or CancelGenerationCommand => new[] { "job_lifecycle" },
                AnimationAnalysisCommand or PoseAnalysisCommand => new[] { "scene_references", "frame_coordinates", "evidence", "operation_scope" },
                ClipInspectCommand or ClipSettingsSetCommand or SessionClearCommand => new[] { "operation_scope" },
                RetargetAnimationCommand or PoseSetCommand => new[] { "scene_references", "operation_scope" },
                GenerateAnimationCommand => new[] { "scene_references", "job_lifecycle", "frame_coordinates", "evidence", "operation_scope" },
                PoseGetCommand => new[] { "scene_references", "frame_coordinates", "operation_scope" },
                PoseFromVideoCommand or PoseFromPictureCommand => new[] { "job_lifecycle", "operation_scope" },
                _ => Array.Empty<string>()
            };
        }

        private static JArray BuildSharedHelpRules(IEnumerable<string> ruleIds = null)
        {
            var selected = ruleIds == null ? null : new HashSet<string>(ruleIds, StringComparer.Ordinal);
            var rules = new[]
            {
                new JObject { ["id"] = "scene_references", ["guidance"] = "Use scene.context as the source for active-scene character, Clip, Timeline, and object references. Pass returned handles to later commands." },
                new JObject { ["id"] = "job_lifecycle", ["guidance"] = "Submission commands return request_id and status. Pass request_id to generation.get for progress and results; submission calls support cooperative Editor waiting, and generation.get waits at most 10 seconds per call." },
                new JObject { ["id"] = "frame_coordinates", ["guidance"] = "Frame positions use absolute Timeline coordinates at 60 FPS. duration_frames_60 values express 60 FPS durations; generation input duration_frames is documented separately." },
                new JObject { ["id"] = "evidence", ["guidance"] = "Use structured result fields for measurements and returned images for visual observations. Use temporal analysis results for motion properties across time." },
                new JObject { ["id"] = "operation_scope", ["guidance"] = "Use each command's named target and scene_effects as its operation scope; the result describes changes made by that call." }
            };
            return new JArray(rules.Where(rule => selected == null || selected.Contains(rule.Value<string>("id"))));
        }

        private static JObject CommandDocumentation(
            string whenToUse,
            string[] behavior,
            string[] resultNotes,
            string[] sceneEffects,
            string[] limits,
            params string[] relatedCommands)
        {
            return new JObject
            {
                ["when_to_use"] = whenToUse,
                ["behavior"] = new JArray(behavior),
                ["result_notes"] = new JArray(resultNotes),
                ["scene_effects"] = new JArray(sceneEffects),
                ["limits"] = new JArray(limits),
                ["shared_rule_refs"] = new JArray(),
                ["related_commands"] = new JArray(relatedCommands)
            };
        }

        private static JObject BuildCommandOutputSchema(string command)
        {
            var properties = new JObject
            {
                ["ok"] = new JObject { ["type"] = "boolean" },
                ["error"] = new JObject
                {
                    ["description"] = "Optional command error. Command errors use {code, message, details?}; asynchronous job failures include this object while the poll command itself succeeds.",
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["code"] = new JObject { ["type"] = "string" },
                        ["message"] = new JObject { ["type"] = "string" },
                        ["details"] = new JObject { ["type"] = "object" }
                    },
                    ["required"] = new JArray("code", "message"),
                    ["additionalProperties"] = true
                }
            };

            string[] fields = command switch
            {
                HelpCommand => new[] { "manual", "usage", "execution_model", "shared_rules", "routing", "handles", "workflow", "commands", "constraints", "constraint_rules", "configs", "count" },
                SceneContextCommand => new[] { "session", "scene", "scene_name", "director", "directors", "command_context_director", "playable_asset", "playable_asset_path", "duration_frames_60", "frame_rate", "current_frame_60", "characters", "warnings", "opened_timeline" },
                SessionHistoryCommand => new[] { "session", "history" },
                InstallServerCommand => new[] { "request_id", "status" },
                PoseFromVideoCommand or PoseFromPictureCommand => new[] { "request_id", "status" },
                AnimationAnalysisCommand => new[] { "analysis_schema_version", "clip", "pictures" },
                ClipInspectCommand or ClipSettingsSetCommand => new[] { "clip", "path", "duration_seconds", "clip_frame_rate", "loop_time", "keep_original_position_xz", "keep_original_position_y", "keep_original_orientation", "root_motion_enabled", "root_motion_curves", "root_displacement_xz_m", "root_displacement_xz", "derived_speed_mps", "rig_type", "curve_count", "consumers" },
                PoseAnalysisCommand => new[] { "image_path", "width", "height", "combine", "constraints" },
                RetargetAnimationCommand => new[] { "retargeted", "source_character", "character", "animation" },
                GenerateAnimationCommand => new[] { "request_id", "status", "check", "warnings", "wait_seconds" },
                PoseGetCommand => new[] { "pose", "source", "character_root", "data" },
                PoseSetCommand => new[] { "pose", "data" },
                GetGenerationCommand => new[] { "request_id", "status", "stage", "message", "started_at_utc", "estimated_completion_utc", "eta_seconds", "progress_current", "progress_total", "progress_rate", "backend_progress", "waited_seconds", "poll_after_seconds", "runtime_root", "runtime_version", "install_mode", "server_connected", "clip", "clip_settings", "animation_id", "seed", "analysis", "post_analysis", "source_type", "skeleton", "person", "fps", "source_frame_count", "frame_count", "joint_names", "pose_parameterization", "coordinate_spaces", "betas", "rest_joints_m", "poses", "constraints", "constraint_loss", "inout_sampling", "target_alive", "backend_request_json" },
                CancelGenerationCommand => new[] { "request_id", "status", "stage", "message", "canceled", "clip" },
                SessionClearCommand => new[] { "cleared", "name", "destroyed_context_count", "recreated_on_next_command" },
                _ => Array.Empty<string>()
            };

            foreach (string field in fields)
            {
                if (properties.Property(field) != null) continue;
                properties[field] = BuildCommandOutputFieldSchema(command, field);
            }

            return new JObject
            {
                ["type"] = "object",
                ["description"] = "Command responses keep domain fields at the top level. Success uses ok=true; command errors use ok=false and an error object. Async job failures are reported by status with an error object.",
                ["properties"] = properties,
                ["required"] = new JArray("ok"),
                ["additionalProperties"] = true
            };
        }

        private static JObject BuildCommandOutputFieldSchema(string command, string field)
        {
            switch (field)
            {
                case "status":
                    string[] statuses = command == InstallServerCommand || command == GenerateAnimationCommand
                        ? new[] { "accepted" }
                        : command == CancelGenerationCommand
                            ? new[] { "running", "completed", "failed", "canceled" }
                            : new[] { "accepted", "running", "completed", "failed", "canceled" };
                    return new JObject { ["type"] = "string", ["enum"] = new JArray(statuses) };
                case "request_id": case "message": case "usage": case "scene": case "scene_name": case "director": case "animation_id":
                case "command_context_director": case "playable_asset": case "playable_asset_path": case "runtime_root":
                case "runtime_version": case "install_mode": case "image_path": case "source_character": case "character":
                case "name": case "asset_path": case "stage": case "started_at_utc": case "estimated_completion_utc":
                case "backend_progress": case "analysis_schema_version":
                case "source_type": case "skeleton": case "person": case "path": case "rig_type":
                    return new JObject { ["type"] = "string" };
                case "width": case "height": case "tile_count": case "completed_tiles": case "duration_frames_60": case "current_frame_60":
                case "destroyed_context_count": case "duration_frames": case "progress_current": case "progress_total": case "count": case "seed":
                case "source_frame_count": case "frame_count": case "source_frame":
                    return new JObject { ["type"] = "integer" };
                case "frame_rate": case "fps":
                    return new JObject { ["type"] = "number" };
                case "duration_seconds": case "clip_frame_rate": case "root_displacement_xz_m": case "derived_speed_mps":
                    return new JObject { ["type"] = "number" };
                case "loop_time": case "keep_original_position_xz": case "keep_original_position_y": case "keep_original_orientation": case "root_motion_enabled":
                    return new JObject { ["type"] = "boolean" };
                case "eta_seconds": case "progress_rate": case "waited_seconds": case "poll_after_seconds":
                    return NullableSchema(new JObject { ["type"] = "number" });
                case "wait_seconds":
                    return new JObject { ["type"] = "number" };
                case "ok": case "retargeted": case "cleared": case "server_connected": case "canceled": case "target_alive":
                case "recreated_on_next_command": case "scene_lighting": case "show_skybox": case "show_gizmos":
                    return new JObject { ["type"] = "boolean" };
                case "manual":
                    return new JObject { ["oneOf"] = new JArray(new JObject { ["type"] = "string" }, new JObject { ["type"] = "object" }) };
                case "shared_rules":
                {
                    JObject schema = ArraySchema("object", "id", "guidance");
                    schema["items"]["properties"]["id"] = new JObject { ["type"] = "string" };
                    schema["items"]["properties"]["guidance"] = new JObject { ["type"] = "string" };
                    return schema;
                }
                case "session": return ObjectSchema("session_id", "session_revision", "schema_version");
                case "opened_timeline":
                {
                    JObject schema = ObjectSchema("timeline_asset", "timeline_asset_path", "director", "bound_characters", "warnings");
                    schema["properties"]["timeline_asset"] = new JObject { ["type"] = "string" };
                    schema["properties"]["timeline_asset_path"] = new JObject { ["type"] = "string" };
                    schema["properties"]["director"] = new JObject { ["type"] = "string" };
                    schema["properties"]["bound_characters"] = ArraySchema("object", "track", "character", "scene_object", "animator");
                    schema["properties"]["warnings"] = ArraySchema("string");
                    return schema;
                }
                case "history":
                {
                    JObject schema = ObjectSchema("generations", "analyses");
                    schema["properties"]["generations"] = ArraySchema("object", "request_id", "animation_id", "character", "animation", "status", "stage", "message", "error", "started_at_utc", "updated_at_utc");
                    schema["properties"]["analyses"] = ArraySchema("object", "analysis_id", "session_id", "character", "animation_id", "animation_name", "input_signature", "created_at_utc", "analysis_path", "picture_paths");
                    schema["properties"]["analyses"]["items"]["properties"]["picture_paths"] = ArraySchema("string");
                    return schema;
                }
                case "animation":
                    return ObjectSchema("animation_id", "name", "character", "source", "kind", "start_timeline_frame_60", "duration_frames", "segments", "transition");
                case "clip":
                {
                    if (command == ClipInspectCommand || command == ClipSettingsSetCommand)
                        return new JObject { ["type"] = "string" };
                    JObject schema = command == AnimationAnalysisCommand
                        ? ObjectSchema("analysis_id", "session_id", "character", "name", "start_timeline_frame_60", "end_timeline_frame_60_exclusive", "analysis_mode", "phase_track_version", "phase_track", "keyframes", "foot_contacts", "root_trajectory", "endpoint_pose_comparison", "motion_profile")
                        : ObjectSchema("name", "character", "asset_path", "start_timeline_frame_60", "end_timeline_frame_60_exclusive");
                    if (command == AnimationAnalysisCommand)
                    {
                        JObject footContacts = ArraySchema(
                            "object",
                            "clip_index",
                            "foot",
                            "timeline_frame_60",
                            "contact",
                            "transition",
                            "duration_frames_60");
                        schema["properties"]["foot_contacts"] = footContacts;
                        schema["properties"]["phase_track"] = PhaseTrackSchema();
                        JObject motionProfile = schema["properties"]?["motion_profile"] as JObject;
                        if (motionProfile != null)
                        {
                            motionProfile["properties"] = new JObject
                            {
                                ["force_line"] = ForceLineSchema()
                            };
                            motionProfile["additionalProperties"] = true;
                        }
                        schema["properties"]["root_trajectory"] = RootTrajectorySchema();
                    }
                    return schema;
                }
                case "clip_settings":
                    return ObjectSchema("clip", "path", "duration_seconds", "clip_frame_rate", "loop_time", "keep_original_position_xz", "keep_original_position_y", "keep_original_orientation", "root_motion_enabled", "root_motion_curves", "root_displacement_xz_m", "root_displacement_xz", "derived_speed_mps", "rig_type", "curve_count", "consumers");
                case "pictures":
                {
                    JObject schema = ObjectSchema(
                        "aspect", "tile_count", "render_version", "image_path", "width", "height",
                        "images", "title", "screenshot_generated_at", "header_height");
                    schema["properties"]["title"] = new JObject { ["type"] = "string" };
                    schema["properties"]["screenshot_generated_at"] = new JObject { ["type"] = "string", ["format"] = "date-time" };
                    schema["properties"]["header_height"] = new JObject { ["type"] = "integer" };
                    schema["properties"]["images"] = ArraySchema("object", "id", "tile", "rect", "description", "header");
                    schema["properties"]["images"]["items"]["properties"]["header"] = ObjectSchema("text", "color", "height", "character_count");
                    return schema;
                }
                case "root_motion_curves": return ArraySchema("string");
                case "root_displacement_xz": return NumberArraySchema();
                case "consumers": return ArraySchema("object", "controller", "controller_path", "layer", "state", "blend_slot", "threshold");
                case "animations": return ArraySchema("object", "animation_id", "name", "character", "source", "kind", "start_timeline_frame_60", "duration_frames", "segments", "transition");
                case "generations": return ArraySchema("object", "request_id", "animation_id", "character", "animation", "status", "stage", "message", "error", "started_at_utc", "updated_at_utc");
                case "analyses": return ArraySchema("object", "analysis_id", "session_id", "character", "animation_id", "animation_name", "input_signature", "created_at_utc", "analysis_path", "picture_paths");
                case "picture_paths": return ArraySchema("string");
                case "directors": return ArraySchema("object", "object_ref", "hierarchy_path", "name", "enabled", "playable_asset", "timeline_frame_60", "duration_frames_60", "frame_rate");
                case "characters":
                {
                    JObject schema = ArraySchema("object", "name", "role", "scene_object", "object_ref", "animator", "timeline_preview", "avatar", "avatar_valid_humanoid", "animations");
                    schema["items"]["properties"]["animations"] = ArraySchema("object", "animation_id", "name", "source", "kind", "start_timeline_frame_60", "duration_frames", "segments", "transition");
                    return schema;
                }
                case "tiles": return ArraySchema("object", "tile", "view", "projection", "transforms", "bounds");
                case "transforms" when command == PoseAnalysisCommand:
                {
                    JObject schema = ArraySchema("object", "path", "world_position");
                    schema["items"]["properties"]["world_position"] = NumberArraySchema();
                    return schema;
                }
                case "pose_analysis":
                {
                    JObject schema = ObjectSchema("overall", "joints");
                    schema["properties"]["overall"] = new JObject
                    {
                        ["type"] = "string", ["enum"] = new JArray("passed", "failed", "not_tested")
                    };
                    JObject joints = ArraySchema("object", "joint", "world_position", "target_position", "distance_m", "tolerance_m", "status");
                    joints["items"]["properties"]["world_position"] = NumberArraySchema();
                    joints["items"]["properties"]["target_position"] = NullableSchema(NumberArraySchema());
                    joints["items"]["properties"]["distance_m"] = NullableSchema(new JObject { ["type"] = "number" });
                    joints["items"]["properties"]["tolerance_m"] = NullableSchema(new JObject { ["type"] = "number" });
                    joints["items"]["properties"]["status"] = new JObject
                    {
                        ["type"] = "string", ["enum"] = new JArray("passed", "failed", "not_tested")
                    };
                    schema["properties"]["joints"] = joints;
                    return schema;
                }
                case "joints":
                {
                    JObject schema = ArraySchema("object", "joint", "world_position", "target_position", "distance_m", "tolerance_m", "status");
                    schema["items"]["properties"]["joint"] = new JObject { ["type"] = "string" };
                    schema["items"]["properties"]["world_position"] = NumberArraySchema();
                    schema["items"]["properties"]["target_position"] = NullableSchema(NumberArraySchema());
                    schema["items"]["properties"]["distance_m"] = NullableSchema(new JObject { ["type"] = "number" });
                    schema["items"]["properties"]["tolerance_m"] = NullableSchema(new JObject { ["type"] = "number" });
                    schema["items"]["properties"]["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray("passed", "failed", "not_tested") };
                    return schema;
                }
                case "constraints" when command == PoseAnalysisCommand:
                {
                    JObject schema = ArraySchema("object", "index", "frame", "source", "transforms", "pose_analysis", "image_path", "width", "height");
                    schema["items"]["properties"]["frame"] = new JObject { ["type"] = "integer" };
                    schema["items"]["properties"]["transforms"] = ArraySchema("object", "path", "world_position");
                    schema["items"]["properties"]["pose_analysis"] = ObjectSchema("overall", "joints");
                    schema["items"]["properties"]["source"] = ObjectSchema("type", "character", "frame", "constraint");
                    schema["items"]["properties"]["source"]["properties"]["frame"] = new JObject { ["type"] = "integer" };
                    schema["items"]["properties"]["source"]["properties"]["constraint"] = ObjectSchema("frame", "pose", "root", "effector");
                    return schema;
                }
                case "constraints" when command == GetGenerationCommand:
                    return new JObject { ["type"] = "array", ["items"] = RawConstraintSchema() };
                case "warnings": return ArraySchema("string");
                case "execution_model": case "routing": case "workflow": case "constraints": case "constraint_rules":
                    return new JObject { ["type"] = "array" };
                case "world_position": return NumberArraySchema();
                case "poses":
                {
                    JObject schema = ArraySchema("object", "frame", "source_frame", "timestamp_seconds", "global_orient_axis_angle", "body_pose_axis_angle", "translation_m", "incam_global_orient_axis_angle", "incam_body_pose_axis_angle", "incam_translation_m", "static_confidence");
                    return schema;
                }
                case "joint_names": return new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } };
                case "betas": case "rest_joints_m": return new JObject { ["type"] = "array" };
                case "configs": return ArraySchema("object");
                case "commands":
                {
                    JObject commands = ArraySchema("object", "name", "description", "documentation", "required", "outputSchema", "examples");
                    commands["items"]["properties"]["documentation"] = CommandDocumentationSchema();
                    return commands;
                }
                case "backend_request_json":
                    return new JObject { ["oneOf"] = new JArray(ObjectSchema(), new JObject { ["type"] = "string" }) };
                case "pose_parameterization": case "coordinate_spaces":
                    return ObjectSchema();
                case "pose": return ObjectSchema("track", "index");
                case "character_root":
                    return new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "The bound scene character GameObject root Transform in Unity world space. This is distinct from data.root, which is the sampled Pose root channel (Hips).",
                        ["properties"] = new JObject
                        {
                            ["position"] = NumberArraySchema(),
                            ["rotation"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "number" }, ["minItems"] = 4, ["maxItems"] = 4, ["description"] = "World-space quaternion [x,y,z,w]." },
                            ["space"] = new JObject { ["type"] = "string", ["enum"] = new JArray("world") }
                        },
                        ["required"] = new JArray("position", "rotation", "space"),
                        ["additionalProperties"] = false
                    };
                case "source": return ObjectSchema("character", "frame", "constraint");
                case "combine": return new JObject { ["type"] = "boolean" };
                case "overall": return new JObject { ["type"] = "string", ["enum"] = new JArray("passed", "failed", "not_tested") };
                case "data": case "check": case "analysis": case "constraint_loss": case "inout_sampling": case "evaluated_joints_world":
                    return ObjectSchema();
                case "post_analysis":
                    return new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["status"] = new JObject { ["type"] = "string", ["enum"] = new JArray("pending", "completed", "failed", "skipped") },
                            ["analysis_path"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "Project-relative JSON path containing the complete post-generation animation.analysis result."
                            },
                            ["error"] = new JObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JObject
                                {
                                    ["code"] = new JObject { ["type"] = "string" },
                                    ["message"] = new JObject { ["type"] = "string" }
                                },
                                ["required"] = new JArray("code", "message"),
                                ["additionalProperties"] = true
                            }
                        },
                        ["additionalProperties"] = true
                    };
                default:
                    return new JObject { ["type"] = "object" };
            }
        }

        private static JObject ObjectSchema(params string[] propertyNames)
        {
            var properties = new JObject();
            foreach (string name in propertyNames)
            {
                properties[name] = name switch
                {
                    "name" or "character" or "source" or "kind" or "asset_path" or "analysis_schema_version" or "track" or
                    "analysis_mode" or "phase_track_version" or "tile" or "view" or "projection" or "key" or "role" or
                    "scene_object" or "object_ref" or "animator" or "avatar" or "hierarchy_path" or "playable_asset" or
                    "render_version" or "level" or "aspect" or "image_path" or "output" or "mode" or "path" or "type" or "clip" or "screenshot_generated_at" => new JObject { ["type"] = "string" },
                    "session_id" or "schema_version" or "animation_id" or "analysis_id" or "animation_name" or "input_signature" or "created_at_utc" or "analysis_path" or "request_id" or "status" or "stage" or "message" or "error" or "started_at_utc" or "updated_at_utc" or "source_type" or "skeleton" or "person" or "pose_parameterization" => new JObject { ["type"] = "string" },
                    "session_revision" or "start_timeline_frame_60" or "end_timeline_frame_60_exclusive" or "duration_frames" or "timeline_frame_60" or
                    "duration_frames_60" or "tile_count" or "width" or "height" or "resolution" or "supersample" or "header_height" or "index" or "frame" or "source_frame" or "source_frame_count" or "frame_count" => new JObject { ["type"] = "integer" },
                    "tile_border_px" => NullableSchema(new JObject { ["type"] = "integer" }),
                    "enabled" or "avatar_valid_humanoid" or "cached" => new JObject { ["type"] = "boolean" },
                    "frame_rate" or "fps" or "timestamp_seconds" => new JObject { ["type"] = "number" },
                    "segments" or "phase_track" or "keyframes" or "foot_contacts" or "images" or "tile_paths" or "picture_paths" or "transforms" or "animations" or "generations" or "analyses" or "tiles" or "headers" or "global_orient_axis_angle" or "body_pose_axis_angle" or "translation_m" or "incam_global_orient_axis_angle" or "incam_body_pose_axis_angle" or "incam_translation_m" or "static_confidence" or "joint_names" or "coordinate_spaces" or "betas" or "rest_joints_m" or "poses" => new JObject { ["type"] = "array" },
                    "session" or "history" or "root_trajectory" or "endpoint_pose_comparison" or "motion_profile" or "environment" or "bounds" or "timeline_preview" or "outputSchema" or "title" or "header" or "root" or "hands" or "feet" => new JObject { ["type"] = "object" },
                    "world_position" => NumberArraySchema(),
                    "required" or "examples" => new JObject { ["type"] = "array" },
                    "description" => new JObject { ["type"] = "string" },
                    _ => new JObject { ["type"] = "object" }
                };
            }
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = true
            };
        }

        private static JObject ForceLineSchema()
        {
            JObject phase = ObjectSchema(
                "phase_index", "kind", "status", "reason", "start_timeline_frame_60",
                "end_timeline_frame_60_exclusive", "left_delta_angle_sum_degrees",
                "right_delta_angle_sum_degrees", "absolute_difference_degrees",
                "imbalance_ratio", "symmetry_score", "balanced", "delta_sample_count");
            JObject phaseProperties = phase["properties"] as JObject;
            phaseProperties["phase_index"] = new JObject { ["type"] = "integer" };
            phaseProperties["kind"] = new JObject { ["type"] = "string" };
            phaseProperties["status"] = new JObject { ["type"] = "string" };
            phaseProperties["reason"] = new JObject { ["type"] = "string" };
            phaseProperties["left_delta_angle_sum_degrees"] = new JObject { ["type"] = "number" };
            phaseProperties["right_delta_angle_sum_degrees"] = new JObject { ["type"] = "number" };
            phaseProperties["absolute_difference_degrees"] = new JObject { ["type"] = "number" };
            phaseProperties["imbalance_ratio"] = NullableSchema(new JObject { ["type"] = "number" });
            phaseProperties["symmetry_score"] = NullableSchema(new JObject { ["type"] = "number" });
            phaseProperties["balanced"] = NullableSchema(new JObject { ["type"] = "boolean" });
            phaseProperties["delta_sample_count"] = new JObject { ["type"] = "integer" };
            phase["additionalProperties"] = true;

            JObject result = ObjectSchema(
                "status", "reason", "method", "coordinate_space", "angle_unit",
                "balance_threshold_ratio", "start_timeline_frame_60",
                "end_timeline_frame_60_exclusive", "left_delta_angle_sum_degrees",
                "right_delta_angle_sum_degrees", "absolute_difference_degrees",
                "imbalance_ratio", "symmetry_score", "balanced", "delta_sample_count",
                "phases");
            JObject resultProperties = result["properties"] as JObject;
            resultProperties["status"] = new JObject
            {
                ["type"] = "string",
                ["enum"] = new JArray("ok", "insufficient_evidence")
            };
            resultProperties["reason"] = new JObject { ["type"] = "string" };
            resultProperties["method"] = new JObject { ["type"] = "string" };
            resultProperties["coordinate_space"] = new JObject { ["type"] = "string" };
            resultProperties["angle_unit"] = new JObject { ["type"] = "string" };
            resultProperties["balance_threshold_ratio"] = new JObject { ["type"] = "number" };
            resultProperties["left_delta_angle_sum_degrees"] = new JObject { ["type"] = "number" };
            resultProperties["right_delta_angle_sum_degrees"] = new JObject { ["type"] = "number" };
            resultProperties["absolute_difference_degrees"] = new JObject { ["type"] = "number" };
            resultProperties["imbalance_ratio"] = NullableSchema(new JObject { ["type"] = "number" });
            resultProperties["symmetry_score"] = NullableSchema(new JObject { ["type"] = "number" });
            resultProperties["balanced"] = NullableSchema(new JObject { ["type"] = "boolean" });
            resultProperties["delta_sample_count"] = new JObject { ["type"] = "integer" };
            resultProperties["start_timeline_frame_60"] = new JObject { ["type"] = "integer" };
            resultProperties["end_timeline_frame_60_exclusive"] = new JObject { ["type"] = "integer" };
            resultProperties["phases"] = new JObject
            {
                ["type"] = "array",
                ["items"] = phase
            };
            result["additionalProperties"] = true;
            return result;
        }

        private static JObject RootTrajectorySchema()
        {
            JObject schema = ObjectSchema(
                "path", "coordinate_space", "frame_rate", "frame_count", "duration_frames_60",
                "sample_span_frames_60", "path_length_xz", "net_displacement_xz", "net_distance_xz",
                "average_speed_xz", "heading_change_degrees", "root_pitch_delta_range_degrees",
                "root_roll_delta_range_degrees", "source_human_scale", "samples");
            schema["properties"]["path"] = ObjectSchema("track", "index");
            schema["properties"]["coordinate_space"] = new JObject { ["type"] = "string" };
            schema["properties"]["frame_rate"] = new JObject { ["type"] = "number" };
            schema["properties"]["duration_frames_60"] = new JObject { ["type"] = "integer" };
            schema["properties"]["sample_span_frames_60"] = new JObject { ["type"] = "integer" };
            foreach (string name in new[] { "path_length_xz", "net_distance_xz", "average_speed_xz", "heading_change_degrees", "source_human_scale" })
                schema["properties"][name] = new JObject { ["type"] = "number" };
            schema["properties"]["net_displacement_xz"] = NumberArraySchema();
            schema["properties"]["root_pitch_delta_range_degrees"] = NumberArraySchema();
            schema["properties"]["root_roll_delta_range_degrees"] = NumberArraySchema();
            schema["properties"]["samples"] = ArraySchema("object", "timeline_frame_60", "position_xz", "heading_xz", "root_rotation_delta_euler_degrees");
            JObject sampleProperties = schema["properties"]["samples"]?["items"]?["properties"] as JObject;
            sampleProperties["position_xz"] = NumberArraySchema();
            sampleProperties["heading_xz"] = NumberArraySchema();
            sampleProperties["root_rotation_delta_euler_degrees"] = NumberArraySchema();
            return schema;
        }

        private static JObject PhaseTrackSchema()
        {
            JObject schema = ArraySchema(
                "object", "phase_index", "start_timeline_frame_60", "end_timeline_frame_60_exclusive",
                "duration_frames_60", "anchor_timeline_frame_60", "kind", "confidence",
                "mean_feature_change", "mean_root_speed", "upper_body_activity", "foot_event_count");
            JObject properties = schema["items"]?["properties"] as JObject;
            foreach (string name in new[] { "phase_index", "foot_event_count", "start_timeline_frame_60", "end_timeline_frame_60_exclusive", "duration_frames_60", "anchor_timeline_frame_60" })
                properties[name] = new JObject { ["type"] = "integer" };
            properties["kind"] = new JObject { ["type"] = "string" };
            properties["confidence"] = new JObject { ["type"] = "string" };
            foreach (string name in new[] { "mean_feature_change", "mean_root_speed", "upper_body_activity" })
                properties[name] = new JObject { ["type"] = "number" };
            return schema;
        }

        private static JObject CommandDocumentationSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["when_to_use"] = new JObject { ["type"] = "string" },
                    ["behavior"] = ArraySchema("string"),
                    ["result_notes"] = ArraySchema("string"),
                    ["scene_effects"] = ArraySchema("string"),
                    ["limits"] = ArraySchema("string"),
                    ["shared_rule_refs"] = ArraySchema("string"),
                    ["related_commands"] = ArraySchema("string")
                },
                ["required"] = new JArray("when_to_use", "behavior", "result_notes", "scene_effects", "limits", "shared_rule_refs", "related_commands"),
                ["additionalProperties"] = false
            };
        }

        private static JObject ArraySchema(string itemType, params string[] itemProperties)
        {
            JObject items = itemType == "object" ? ObjectSchema(itemProperties) : new JObject { ["type"] = itemType };
            return new JObject { ["type"] = "array", ["items"] = items };
        }

        private static JObject NumberArraySchema()
        {
            return new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "number" }
            };
        }

        private static JObject RawConstraintSchema()
        {
            JObject schema = ObjectSchema("type", "frame_indices", "smooth_root_2d", "global_root_heading", "local_joints_rot", "root_positions", "target_positions", "joint_names", "dense_path");
            schema["properties"]["frame_indices"] = new JObject { ["type"] = "array" };
            schema["properties"]["smooth_root_2d"] = new JObject { ["type"] = "array" };
            schema["properties"]["global_root_heading"] = new JObject { ["type"] = "array" };
            schema["properties"]["local_joints_rot"] = new JObject { ["type"] = "array" };
            schema["properties"]["root_positions"] = new JObject { ["type"] = "array" };
            schema["properties"]["target_positions"] = new JObject { ["type"] = "array" };
            schema["properties"]["joint_names"] = new JObject { ["type"] = "array" };
            schema["properties"]["dense_path"] = new JObject { ["type"] = "boolean" };
            return schema;
        }

        private static JObject NullableSchema(JObject schema)
        {
            return new JObject
            {
                ["oneOf"] = new JArray(schema, new JObject { ["type"] = "null" })
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

        private static PropertyDefinition WaitSecondsProperty(double defaultSeconds, bool allowAboveLimit)
        {
            var schema = new JObject
            {
                ["type"] = "number",
                ["minimum"] = 0,
                ["default"] = defaultSeconds,
                ["description"] = allowAboveLimit
                    ? "Editor-side cooperative wait window in seconds. Values above 10 seconds are allowed; if the task is still running, return its request_id."
                    : "Editor-side cooperative wait window in seconds. The maximum is 10 seconds; if the task is still running, return its request_id."
            };
            if (!allowAboveLimit) schema["maximum"] = 10.0;
            return new PropertyDefinition("wait_seconds", schema, false);
        }

        private static PropertyDefinition OptionalLoop()
        {
            JObject axis = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["x"] = new JObject { ["type"] = "boolean" },
                    ["y"] = new JObject { ["type"] = "boolean" },
                    ["z"] = new JObject { ["type"] = "boolean" }
                }
            };
            JObject structured = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["enabled"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["default"] = true,
                        ["description"] = "Enable bounded loop preprocessing; defaults to true when the loop object is supplied."
                    },
                    ["window_frames"] = new JObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["default"] = 30,
                        ["description"] = "Loop window duration in 60 FPS command frames; converted to model frames when building the loop window. The copied tail starts on the core content's terminal frame, so the total extension is the window width minus one model frame."
                    },
                    ["sample_count"] = new JObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["default"] = 2,
                        ["description"] = "Number of FullBody samples in the loop window, including its endpoints when more than one sample is requested."
                    },
                    ["lock_pos"] = axis.DeepClone(),
                    ["lock_rot"] = axis.DeepClone()
                }
            };
            return new PropertyDefinition("loop", new JObject
            {
                ["description"] = "Configure loop preprocessing with {enabled, window_frames, sample_count, lock_pos:{x,y,z}, lock_rot:{x,y,z}}. The copied tail starts on the core content's terminal frame.",
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = structured["properties"].DeepClone()
            }, false);
        }

        private static PropertyDefinition OptionalObject(string name, JObject properties, string description)
        {
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = properties,
                ["description"] = description
            }, false);
        }

        private static PropertyDefinition OptionalPath()
        {
            return OptionalObject("path", new JObject
            {
                ["start_angle"] = new JObject { ["type"] = "number" },
                ["end_angle"] = new JObject { ["type"] = "number" },
                ["distance"] = new JObject { ["type"] = "number", ["minimum"] = 0 },
                ["heading"] = new JObject { ["type"] = "number" },
                ["relative_offset"] = new JObject
                {
                    ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3,
                    ["items"] = new JObject { ["type"] = "number" },
                    ["description"] = "World-space target position for the generated Clip. Root X/Z are normalized into the initial facing frame before Humanoid conversion; the initial yaw is transferred to the Timeline Clip rotation, and the generated root Y curve is preserved with its final frame-zero value accounted for in the Clip offset."
                }
            }, "Optional Root2D path and output placement settings. Any angle enables path override; distance replaces the measured path length; heading is applied after path composition; relative_offset places the Clip in World space while preserving generated vertical root motion for Humanoid conversion and transferring initial yaw to the Clip rotation.");
        }

        private static PropertyDefinition OptionalGeneration()
        {
            return OptionalObject("generation", new JObject
            {
                ["model"] = new JObject { ["type"] = "string" },
                ["text_encoder"] = new JObject { ["type"] = "string", ["enum"] = new JArray("high_performance", "high_precision") },
                ["seed"] = new JObject { ["type"] = "integer" },
                ["diffusion_steps"] = new JObject { ["type"] = "integer" }
            }, "Optional model and deterministic generation settings.");
        }

        private static PropertyDefinition OptionalOutput()
        {
            return OptionalObject("output", new JObject
            {
                ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("humanoid_muscle", "character_bone", "model_bone") },
                ["folder"] = new JObject { ["type"] = "string" },
                ["name"] = new JObject { ["type"] = "string" }
            }, "Optional animation output settings.");
        }






        private static PropertyDefinition OptionalConstraints(string name, string description)
        {
            JObject poseReference = PoseReferenceSchema();
            JObject effectTarget = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["position"] = new JObject
                    {
                        ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3,
                        ["items"] = new JObject { ["type"] = "number" },
                        ["description"] = "World-space target position [x,y,z]. Omitted channels retain sampled values."
                    },
                    ["rotation"] = new JObject
                    {
                        ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3,
                        ["items"] = new JObject { ["type"] = "number" },
                        ["description"] = "Rotation in Euler degrees [x,y,z], converted with Unity Quaternion.Euler. Omitted channels retain sampled values."
                    }
                },
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("position") },
                    new JObject { ["required"] = new JArray("rotation") })
            };
            JObject effectors = new JObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["joint"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray("left_hand", "right_hand", "left_foot", "right_foot")
                        },
                        ["position"] = effectTarget["properties"]["position"].DeepClone(),
                        ["rotation"] = effectTarget["properties"]["rotation"].DeepClone()
                    },
                    ["required"] = new JArray("joint"),
                    ["anyOf"] = new JArray(
                        new JObject { ["required"] = new JArray("position") },
                        new JObject { ["required"] = new JArray("rotation") })
                },
                ["description"] = "World-space targets for one or more hands or feet. Each item names a joint and supplies position and/or Euler rotation."
            };
            var pointItem = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["frame"] = new JObject { ["type"] = "integer", ["minimum"] = 0 },
                    ["pose"] = poseReference.DeepClone(),
                    ["root"] = effectTarget.DeepClone(),
                    ["effector"] = effectors.DeepClone()
                },
                ["required"] = new JArray("frame"),
                ["description"] = "Use the explicit External Pose, or the Avatar skeleton rest pose when pose is omitted. Select the point mode from root, pose, and effector inputs, then overlay data. root is Root2D; pose with optional root/effector is FullBody after IK; root with effector is Effector. Transforms accept position and/or Euler rotation; omitted channels retain sampled values.",
                ["oneOf"] = new JArray(
                    new JObject { ["required"] = new JArray("pose") },
                    new JObject { ["required"] = new JArray("root", "effector"),
                        ["not"] = new JObject { ["required"] = new JArray("pose") } },
                    new JObject { ["required"] = new JArray("root"),
                        ["not"] = new JObject { ["anyOf"] = new JArray(
                            new JObject { ["required"] = new JArray("pose") },
                            new JObject { ["required"] = new JArray("effector") }) } })
            };
            var rootPathItem = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["frame"] = new JObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["default"] = 0,
                        ["description"] = "First path frame; defaults to the clip start. animation.analysis returns this path in World coordinates because the analyzed Clip begins at frame zero; generation resolves it once against the target character's rest-pose World anchor. root_path constrains the start and end; add point constraints for intermediate route locations that must be followed."
                    },
                    ["root_path"] = new JObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JObject
                        {
                            ["path"] = poseReference.DeepClone()
                        },
                        ["required"] = new JArray("path")
                    }
                },
                ["required"] = new JArray("root_path")
            };
            ((JObject)rootPathItem["properties"]?["root_path"]?["properties"]?["path"])["description"] =
                "Confirmed analyzed Root Path slot for the intended character and motion, returned by animation.analysis at clip.root_trajectory.path.";
            return new PropertyDefinition(name, new JObject
            {
                ["type"] = "array",
                ["description"] = description,
                ["items"] = new JObject { ["oneOf"] = new JArray(pointItem, rootPathItem, InOutConstraintSchema()) }
            }, false);
        }

        private static JObject InOutConstraintSchema()
        {
            JObject source = Properties(
                Required("clip", "string", "Safe completed source Clip name in the current scene context."),
                Optional("character", "string", "Source character; defaults to the generation character. Use the generation character for source Clip sampling."),
                new PropertyDefinition("frame", new JObject { ["type"] = "integer", ["minimum"] = 0,
                    ["description"] = "Optional boundary frame at 60 FPS, local to the Clip's played Timeline range. Defaults: outside In=last model frame / Out=first; inside In=first / Out=last." }, false));
            JObject side = Properties(
                new PropertyDefinition("source", source, true),
                new PropertyDefinition("window_frames", new JObject { ["type"] = "integer", ["minimum"] = 1, ["default"] = 1,
                    ["description"] = "Coverage at 60 FPS. Converted to model frames by ceil(window_frames * model_fps / 60), minimum 1; the requested coverage is preserved." }, false),
                new PropertyDefinition("sample_count", new JObject { ["type"] = "integer", ["minimum"] = 1, ["default"] = 1,
                    ["description"] = "Distinct samples within the converted window. One preserves the seam; multiple samples include both endpoints." }, false),
                new PropertyDefinition("post_apply", new JObject { ["type"] = "boolean",
                    ["description"] = "Generate a trajectory first, align this boundary against it in SOMA Root2D space, then include the boundary in the final raw constraints." }, false));
            JObject inSide = (JObject)side.DeepClone();
            inSide["properties"]["post_apply"]["default"] = true;
            JObject outSide = (JObject)side.DeepClone();
            outSide["properties"]["post_apply"]["default"] = true;
            JObject config = Properties(
                new PropertyDefinition("mode", new JObject { ["type"] = "string", ["enum"] = new JArray("outside", "inside"), ["default"] = "outside",
                    ["description"] = "Outside pads generation with source context then crops it off; inside constrains the returned clip's start/end windows. Output duration is preserved." }, false),
                new PropertyDefinition("in", inSide, false),
                new PropertyDefinition("out", outSide, false));
            config["anyOf"] = new JArray(new JObject { ["required"] = new JArray("in") }, new JObject { ["required"] = new JArray("out") });
            return Properties(new PropertyDefinition("inout", config, true));
        }

        private static PropertyDefinition RequiredPoseSource(string name)
        {
            JObject schema = new JObject
            {
                ["type"] = "object",
                ["description"] = "Select one source form: a Timeline-global frame, the current scene Animator pose, or the Avatar skeleton rest pose.",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["character"] = new JObject { ["type"] = "string", ["description"] = "Current scene character name or object_ref." },
                    ["mode"] = new JObject { ["type"] = "string", ["enum"] = new JArray("scene_pose", "skeleton_rest_pose"), ["description"] = "Sample the original scene Animator's currently evaluated pose, or the Avatar's stored skeleton rest pose." },
                    ["timeline_frame_60"] = new JObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 0,
                        ["description"] = "Absolute Timeline-global frame at 60 FPS. Use animation.analysis to find the selected Clip's start frame, then add the clip-local frame offset."
                    }
                },
                ["required"] = new JArray("character"),
                ["oneOf"] = new JArray(
                    new JObject
                    {
                        ["required"] = new JArray("character", "timeline_frame_60"),
                        ["not"] = new JObject { ["required"] = new JArray("mode") }
                    },
                    new JObject
                    {
                        ["required"] = new JArray("character", "mode"),
                        ["properties"] = new JObject { ["mode"] = new JObject { ["enum"] = new JArray("scene_pose") } },
                        ["not"] = new JObject { ["required"] = new JArray("timeline_frame_60") }
                    },
                    new JObject
                    {
                        ["required"] = new JArray("character", "mode"),
                        ["properties"] = new JObject { ["mode"] = new JObject { ["enum"] = new JArray("skeleton_rest_pose") } },
                        ["not"] = new JObject { ["required"] = new JArray("timeline_frame_60") }
                    })
            };
            return new PropertyDefinition(name, schema, true);
        }


        private static JObject PoseSetSchema()
        {
            JObject transform = new JObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["position"] = new JObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = new JObject { ["type"] = "number" } },
                    ["rotation"] = new JObject { ["type"] = "array", ["minItems"] = 4, ["maxItems"] = 4, ["items"] = new JObject { ["type"] = "number" } }
                },
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("position") },
                    new JObject { ["required"] = new JArray("rotation") })
            };
            JObject effectors = new JObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["joint"] = new JObject
                        {
                            ["type"] = "string",
                            ["enum"] = new JArray("left_hand", "right_hand", "left_foot", "right_foot")
                        },
                        ["position"] = transform["properties"]["position"].DeepClone(),
                        ["rotation"] = transform["properties"]["rotation"].DeepClone()
                    },
                    ["required"] = new JArray("joint"),
                    ["anyOf"] = new JArray(
                        new JObject { ["required"] = new JArray("position") },
                        new JObject { ["required"] = new JArray("rotation") })
                }
            };
            effectors["description"] = "World-space targets for one or more hands or feet. Each item names a joint and supplies position and/or rotation.";
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["pose"] = PoseReferenceSchema(),
                    ["root"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "Root position [x,y,z] and/or rotation quaternion [x,y,z,w]. A rotation update requires an existing or supplied root position.",
                        ["properties"] = new JObject
                        {
                            ["position"] = new JObject { ["type"] = "array", ["minItems"] = 3, ["maxItems"] = 3, ["items"] = new JObject { ["type"] = "number" } },
                            ["rotation"] = new JObject { ["type"] = "array", ["minItems"] = 4, ["maxItems"] = 4, ["items"] = new JObject { ["type"] = "number" } }
                        },
                        ["additionalProperties"] = false,
                        ["minProperties"] = 1
                    },
                    ["muscles"] = new JObject
                    {
                        ["type"] = "object",
                        ["description"] = "Non-empty map of canonical Humanoid muscle channel names to finite numeric values.",
                        ["additionalProperties"] = new JObject { ["type"] = "number" },
                        ["minProperties"] = 1
                    },
                    ["effector"] = effectors
                },
                ["required"] = new JArray("pose"),
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("root") },
                    new JObject { ["required"] = new JArray("muscles") },
                    new JObject { ["required"] = new JArray("effector") }),
                ["additionalProperties"] = false
            };
        }

        private static JObject PoseReferenceSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = "External Pose slot in the current scene context.",
                ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["track"] = new JObject { ["type"] = "string" },
                    ["index"] = new JObject { ["type"] = "integer", ["minimum"] = 0 }
                },
                ["required"] = new JArray("track", "index")
            };
        }


        private static PropertyDefinition RequiredAnalysisClips(bool required = true)
        {
            return new PropertyDefinition("clips", new JObject
            {
                ["type"] = "array",
                ["description"] = "One animation Clip reference per call. Use a loaded Clip name or an Assets/Packages path. An external asset is appended to the character's Kimodo Timeline before analysis; the source asset remains unchanged.",
                ["minItems"] = 1,
                ["maxItems"] = 1,
                ["items"] = new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JObject
                    {
                        ["character"] = new JObject { ["type"] = "string" },
                        ["clip"] = new JObject { ["type"] = "string" }
                    },
                    ["required"] = new JArray("character", "clip")
                }
            }, required);
        }




        private sealed class CommandCatalog
        {
            private readonly IReadOnlyList<CommandRegistration> registrations;
            private readonly Dictionary<string, CommandRegistration> byName;

            public CommandCatalog(IEnumerable<CommandRegistration> registrations)
            {
                this.registrations = registrations.ToList();
                byName = new Dictionary<string, CommandRegistration>(StringComparer.Ordinal);
                foreach (CommandRegistration registration in this.registrations)
                {
                    if (byName.ContainsKey(registration.Name))
                    {
                        throw new InvalidOperationException($"Duplicate Kimodo command '{registration.Name}'.");
                    }
                    byName.Add(registration.Name, registration);
                }
            }

            public bool TryGet(string name, out CommandRegistration registration)
            {
                registration = null;
                return name != null && byName.TryGetValue(name, out registration);
            }

            public string ToJson()
            {
                var tools = new JArray();
                foreach (CommandRegistration registration in registrations)
                {
                    tools.Add(registration.ToJson());
                }
                return new JObject { ["tools"] = tools }.ToString(Formatting.None);
            }
        }

        private sealed class CommandRegistration
        {
            private readonly JObject definition;

            public CommandRegistration(JObject definition, Func<JObject, string> handler)
            {
                this.definition = (JObject)definition.DeepClone();
                Name = this.definition.Value<string>("name");
                JObject inputSchema = this.definition["inputSchema"] as JObject
                    ?? throw new InvalidOperationException($"Command '{Name ?? string.Empty}' has no input schema.");
                if (handler == null) throw new ArgumentNullException(nameof(handler));
                Handler = argumentsJson => Execute(argumentsJson, arguments =>
                {
                    if (!CommandInputSchemaValidator.TryValidate(inputSchema, arguments, out string validationError))
                    {
                        throw new CommandException("invalid_argument", validationError);
                    }
                    return handler(arguments);
                });
            }

            public string Name { get; }
            public Func<string, string> Handler { get; }

            public JObject ToJson()
            {
                return (JObject)definition.DeepClone();
            }
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
