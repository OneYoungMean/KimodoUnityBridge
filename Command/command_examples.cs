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
                    @"{'command':'kimodo_generate_animation'}");
                case InstallServerCommand: return Examples("Install the local server, then poll the returned request_id.", @"{}");
                case AnimationAnalyzeCommand: return Examples("Analyze one returned Clip; open the returned image to inspect it.",
                    @"{'clips':[{'character':'<character>','clip':'<clip>'}],'picture':{'output':'composite'},'resolution':512}");
                case RetargetAnimationCommand: return Examples("Retarget a loaded Clip to another scene character.",
                    @"{'source_character':'<source character>','animation':'<clip>','target_character':'<target character>','output':{'name':'RetargetedMotion'}}");
                case TransformCaptureCommand: return Examples("Capture the External Pose returned by pose_get together with scene objects, without moving the scene character.",
                    @"{'pose':{'track':'<returned pose track>','index':0},'transforms':['@pose','<table hierarchy path>','<apple hierarchy path>'],'resolution':768}");
                case GenerateAnimationCommand: return new JArray(
                    Example("Generate two seconds and poll the returned request_id.",
                        @"{'character':'<character>','prompt':'Stand still and breathe naturally','duration_frames':120}"),
                    Example("Continue a source Clip using its final half-second sampled five times. Names must come from the current scene context; the result contains inout_sampling diagnostics.",
                        @"{'character':'<character>','prompt':'Stop walking and stand upright','duration_frames':120,'loop':false,'constraints':[{'inout':{'mode':'outside','in':{'source':{'clip':'<source clip>'},'window_frames':30,'sample_count':5}}}]}"),
                    Example("Use explicit previous/next Clips as context on both sides; crop context from the returned two-second output.",
                        @"{'character':'<character>','prompt':'Reach for the apple and pick it up','duration_frames':120,'loop':false,'constraints':[{'inout':{'in':{'source':{'clip':'<previous clip>'},'window_frames':30,'sample_count':5},'out':{'source':{'clip':'<next clip>'},'window_frames':12,'sample_count':3}}}]}"),
                    Example("Keep sampled opening and closing windows inside the returned Clip.",
                        @"{'character':'<character>','prompt':'Bend down then stand up','duration_frames':120,'loop':false,'constraints':[{'inout':{'mode':'inside','in':{'source':{'clip':'<reference clip>','frame':0},'window_frames':12,'sample_count':3},'out':{'source':{'clip':'<reference clip>'},'window_frames':12,'sample_count':3}}}]}"));
                case PoseGetCommand: return Examples("Capture an animation_analyze keyframe at the absolute Timeline-global 60 FPS frame; reuse the returned {track,index}.",
                    @"{'source':{'character':'<character>','clip':'<clip>','timeline_frame_60':90},'full_data':true}");
                case PoseSetCommand: return Examples("Edit the root of the Pose slot returned by pose_get; position is in Character world space.",
                    @"{'pose':{'track':'<returned pose track>','index':0},'root':{'position':[0,1,0],'rotation':[0,0,0,1]}}");
                case PoseSetRootTransformCommand: return Examples("Set a returned Pose slot's root position.",
                    @"{'pose':{'track':'<returned pose track>','index':0},'root':{'position':[0,1,0]}}");
                case PoseSetMuscleCommand: return Examples("Edit the Spine Front-Back muscle of a returned Pose slot.",
                    @"{'pose':{'track':'<returned pose track>','index':0},'muscles':{'Spine Front-Back':0.1}}");
                case GetGenerationCommand: return Examples("Poll using the exact request_id returned by generation or installation.",
                    @"{'request_id':'11111111-1111-1111-1111-111111111111'}");
                case CancelGenerationCommand: return Examples("Cancel using the exact active generation request_id.",
                    @"{'request_id':'11111111-1111-1111-1111-111111111111','reason':'User requested cancellation'}");
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
