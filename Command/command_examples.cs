using System;
using Newtonsoft.Json.Linq;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        // Examples are part of the generated catalog, not a second hand-written
        // help file. Angle-bracket names must be replaced by returned handles.
        private static JArray BuildCommandExamples(string command)
        {
            switch (command)
            {
                case HelpCommand: return Examples("Inspect generation parameters and examples.",
                    @"{'command':'animation.generate'}");
                case SceneContextCommand: return new JArray(
                    Example("Read the scene and current Session identity.", @"{}"),
                    Example("Reload and open a historical Timeline, binding same-name scene characters when available.", @"{'timeline_path':'Assets/Timelines/WalkHistory.playable'}"));
                case SessionHistoryCommand: return Examples("Read the compact generation and analysis history for the active Session.", @"{}");
                case InstallServerCommand: return Examples("Install the local server, then poll the returned request_id.", @"{}");
                case PoseFromVideoCommand: return Examples("Estimate SMPL-X poses from a video, then poll for the pose array.",
                    @"{'video_path':'C:/data/mocap.mp4','person':'left'}");
                case PoseFromPictureCommand: return Examples("Estimate one SMPL-X pose from a still image, then poll for the pose array.",
                    @"{'image_path':'C:/data/pose.jpg'}");
                case AnimationAnalysisCommand: return new JArray(
                    Example("Analyze one Clip and open its returned image to inspect it.",
                        @"{'clips':[{'character':'<character>','clip':'<clip>'}],'resolution':1920,'camera':{'resolution':[1280,720]}}"),
                    Example("Analyze another Clip separately, then map each result to its source for comparison.",
                        @"{'clips':[{'character':'<character>','clip':'<another clip>'}],'resolution':1920,'camera':{'resolution':[1280,720]}}"));
                case PoseAnalysisCommand: return new JArray(
                    Example("Preview a point constraint when pose inspection will help assess the requested result.",
                        @"{'character':'<character>','constraints':[{'frame':90,'pose':{'track':'<track>','index':0},'root':{'position':[1.1,0,2.4]},'effector':{'right_hand':{'position':[1.2,0.9,2.4]}}}],'combine':false,'camera':{'resolution':[1280,720]}}"),
                    Example("Preview a Root2D point constraint and inspect the returned measurements.",
                        @"{'character':'<character>','constraints':[{'frame':60,'root':{'position':[2.4,0.95,5.0],'rotation':[0,0,0]}}]}"));
                case RetargetAnimationCommand: return Examples("Retarget a loaded Clip to another scene character.",
                    @"{'source_character':'<source character>','animation':'<clip>','target_character':'<target character>','output':{'name':'RetargetedMotion'}}");
                case GenerateAnimationCommand: return new JArray(
                    Example("Submit one Clip request; use the returned request_id with generation.get.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':120}"),
                    Example("Continue a source Clip using its final half-second sampled five times. Names must come from the current scene context; inspect inout_sampling after generation completes.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':120,'loop':{'enabled':false},'constraints':[{'inout':{'mode':'outside','in':{'source':{'clip':'<source clip>'},'window_frames':30,'sample_count':5}}}]}"),
                    Example("Use explicit previous/next Clips as context on both sides; crop context from the returned two-second output.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':120,'loop':{'enabled':false},'constraints':[{'inout':{'in':{'source':{'clip':'<previous clip>'},'window_frames':30,'sample_count':5},'out':{'source':{'clip':'<next clip>'},'window_frames':12,'sample_count':3}}}]}"),
                    Example("Keep sampled opening and closing windows inside the returned Clip.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':120,'loop':{'enabled':false},'constraints':[{'inout':{'mode':'inside','in':{'source':{'clip':'<reference clip>','frame':0},'window_frames':12,'sample_count':3},'out':{'source':{'clip':'<reference clip>'},'window_frames':12,'sample_count':3}}}]}"),
                    Example("Create a Root2D constraint from a complete World root transform with Euler rotation in degrees.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':120,'constraints':[{'frame':60,'root':{'position':[2.4,0.95,5.0],'rotation':[0,0,0]}}]}"),
                    Example("Create an IK-solved FullBody constraint from a referenced pose, root override, and hand target.",
                        @"{'character':'<character>','prompt':'<short action for one Clip>','duration_frames':180,'loop':{'enabled':false},'constraints':[{'frame':90,'pose':{'track':'<pose track>','index':0},'root':{'position':[2.4,0.95,5.0],'rotation':[0,0,0]},'effector':{'right_hand':{'position':[2.55,0.95,5.35]}}}]}"));
                case PoseGetCommand: return new JArray(
                    Example("Capture a pose at an absolute Timeline-global frame resolved from scene.context or existing analysis.",
                        @"{'source':{'character':'<character>','timeline_frame_60':90}}"),
                    Example("Capture the original scene Animator's currently evaluated pose, independent of Timeline preview time.",
                        @"{'source':{'character':'<character>','mode':'scene_pose'},'full_data':true}"),
                    Example("Capture the Humanoid Avatar's stored skeleton rest pose.",
                        @"{'source':{'character':'<character>','mode':'skeleton_rest_pose'},'full_data':true}"));
                case PoseSetCommand: return Examples("Edit the root of the Pose slot returned by pose.get; position is in Character world space.",
                    @"{'pose':{'track':'<returned pose track>','index':0},'root':{'position':[0,1,0],'rotation':[0,0,0,1]}}");
                case GetGenerationCommand: return Examples("Poll using the exact request_id returned by generation or installation.",
                    @"{'request_id':'11111111-1111-1111-1111-111111111111'}");
                case CancelGenerationCommand: return Examples("Cancel using the exact active generation request_id.",
                    @"{'request_id':'11111111-1111-1111-1111-111111111111','reason':'User requested cancellation'}");
                case SessionClearCommand: return Examples("Clear the current runtime session while retaining persisted assets.", @"{}");
                default: throw new InvalidOperationException($"Command '{command}' is missing a help example.");
            }
        }

        private static JArray Examples(string description, string arguments) => new JArray(Example(description, arguments));
        private static JObject Example(string description, string arguments) => new JObject
        {
            ["description"] = description,
            ["arguments"] = JObject.Parse(arguments)
        };
    }
}
