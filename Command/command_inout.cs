using System;
using System.Collections.Generic;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using Newtonsoft.Json.Linq;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        internal sealed class CommandInOutWindow
        {
            public int FrameCount;
            public double[] SourceTimes;
            public int[] OutputFrames;
        }

        // Command frames use 60 FPS. The source is the played Clip range,
        // including clipIn/timeScale; the sampler evaluates its actual Timeline.
        internal static CommandInOutWindow ResolveCommandInOutWindow(
            JObject side, bool inside, bool isIn, double sourceDuration, int targetFrames, float fps)
        {
            int requested = side["window_frames"] == null ? 1 : RequiredNonNegativeFrame(side, "window_frames");
            int count = side["sample_count"] == null ? 1 : RequiredNonNegativeFrame(side, "sample_count");
            if (requested < 1 || requested / SessionFrameRate * fps > KimodoMotionModelProfiles.MaxGenerationFrames)
                throw new InvalidOperationException("inout.window_frames must be positive and fit the model frame limit.");
            int frames = Math.Max(1, (int)Math.Ceiling(requested / SessionFrameRate * fps - 1e-6));
            if (count < 1 || count > frames)
                throw new InvalidOperationException($"inout.sample_count must be within [1,{frames}] after conversion to model FPS; duplicate sample frames are not allowed.");
            if (inside && frames > targetFrames)
                throw new InvalidOperationException("Inside In/Out window exceeds the generated clip.");
            bool forward = inside == isIn;
            JObject source = side["source"] as JObject
                ?? throw new InvalidOperationException("inout.source must be an object.");
            double boundary = source["frame"] == null
                ? (forward ? 0 : sourceDuration - 1.0 / fps)
                : RequiredNonNegativeFrame(source, "frame") / SessionFrameRate;
            double first = boundary - (forward ? 0 : (frames - 1.0) / fps);
            double last = first + (frames - 1.0) / fps;
            if (sourceDuration <= 0 || first < -1e-7 || last >= sourceDuration - 1e-7)
                throw new InvalidOperationException("In/Out source clip is too short or source.frame/window_frames exceeds its played range.");
            int start = inside ? (isIn ? 0 : targetFrames - frames) : (isIn ? -frames : targetFrames);
            int[] indices = KimodoInOutConstraintTools.BuildWindowSampleFrames(frames, count, forward);
            return new CommandInOutWindow
            {
                FrameCount = frames,
                SourceTimes = indices.Select(i => Math.Max(0, first) + i / (double)fps).ToArray(),
                OutputFrames = indices.Select(i => start + i).ToArray()
            };
        }

        private static KimodoExternalConstraintRequest BuildCommandInOutConstraints(
            JObject arguments, TimelineGenerationTrace trace, string model, int targetFrames, float fps,
            bool loop, IReadOnlyList<KimodoMarkerSampleResult> pointSamples)
        {
            var entries = (arguments["constraints"] as JArray)?.OfType<JObject>()
                .Where(item => item.Property("inout") != null).ToArray() ?? Array.Empty<JObject>();
            if (entries.Length == 0) return null;
            if (entries.Length != 1 || entries[0].Properties().Count() != 1)
                throw new InvalidOperationException("Supply one standalone inout entry; put point constraints in separate entries.");
            if (KimodoMotionModelProfiles.TryGetArdy(model, out _))
                throw new InvalidOperationException("Command inout FullBody sampling is not supported by ARDY; its clip-constraint history requires a separate adapter.");
            if (loop) throw new InvalidOperationException("inout cannot be combined with loop generation.");
            JObject config = entries[0]["inout"] as JObject
                ?? throw new InvalidOperationException("inout must be an object.");
            RequireInOutFields(config, "mode", "in", "out");
            string mode = config.Value<string>("mode") ?? "outside";
            if (mode != "inside" && mode != "outside")
                throw new InvalidOperationException("inout.mode must be inside or outside.");
            if (config["in"] == null && config["out"] == null)
                throw new InvalidOperationException("inout requires in, out, or both.");
            var request = new KimodoExternalConstraintRequest { Enabled = true, IncludeTimelineConstraints = true };
            var diagnostics = new JObject { ["mode"] = mode, ["command_fps"] = SessionFrameRate, ["model_fps"] = fps };
            var plans = new List<(bool IsIn, TimelineAnimationRecord Animation, CommandInOutWindow Window)>();
            foreach (string key in new[] { "in", "out" })
            {
                if (config.Property(key) == null) continue;
                JObject side = config[key] as JObject ?? throw new InvalidOperationException($"inout.{key} must be an object.");
                RequireInOutFields(side, "source", "window_frames", "sample_count");
                JObject source = side["source"] as JObject ?? throw new InvalidOperationException($"inout.{key}.source is required.");
                RequireInOutFields(source, "character", "clip", "frame");
                string characterName = source.Value<string>("character") ?? trace.Character.Name;
                var character = ResolveSessionCharacterByReference(trace.Session, characterName, addIfMissing: false);
                if (!ReferenceEquals(character, trace.Character))
                    throw new InvalidOperationException("inout source.character must be the generation character; cross-character alignment is not supported.");
                var animation = ResolveAnimation(new JObject { ["animation"] = RequiredStringValue(source, "clip") }, character);
                if (animation.TimelineSegments.Count == 0 || animation.TimelineSegments.Any(s => s.Clip == null))
                    throw new InvalidOperationException($"In/Out source '{animation.Name}' is not a completed playable clip.");
                var window = ResolveCommandInOutWindow(side, mode == "inside", key == "in", animation.TimelineDurationSeconds, targetFrames, fps);
                int startFrame = (int)Math.Floor((animation.TimelineStartSeconds + window.SourceTimes[0]) * SessionFrameRate);
                int endFrame = (int)Math.Ceiling((animation.TimelineStartSeconds + window.SourceTimes.Last()) * SessionFrameRate) + 1;
                ThrowIfGenerationRangeLocked(trace.Session, character, startFrame, endFrame, GenerateAnimationCommand);
                plans.Add((key == "in", animation, window));
                if (mode == "outside")
                {
                    if (key == "in") request.ContextBeforeFrames = window.FrameCount;
                    else request.ContextAfterFrames = window.FrameCount;
                }
                diagnostics[key] = new JObject
                {
                    ["source"] = new JObject { ["character"] = character.Name, ["clip"] = animation.Name },
                    ["window_frames"] = side.Value<int?>("window_frames") ?? 1,
                    ["model_window_frames"] = window.FrameCount,
                    ["sample_count"] = window.SourceTimes.Length,
                    ["source_times_seconds"] = new JArray(window.SourceTimes),
                    ["source_timeline_times_seconds"] = new JArray(window.SourceTimes.Select(t => animation.TimelineStartSeconds + t)),
                    ["output_model_frames"] = new JArray(window.OutputFrames)
                };
            }
            if (mode == "inside" && plans.Sum(p => p.Window.FrameCount) > targetFrames)
                throw new InvalidOperationException("Inside In/Out windows overlap.");
            int runtimeFrames = targetFrames + request.ContextBeforeFrames + request.ContextAfterFrames;
            if (runtimeFrames > KimodoMotionModelProfiles.MaxGenerationFrames)
                throw new InvalidOperationException($"In/Out context needs {runtimeFrames} model frames, exceeding {KimodoMotionModelProfiles.MaxGenerationFrames}; shorten the output or windows.");
            var boundaryFrames = new HashSet<int>(plans.SelectMany(p => p.Window.OutputFrames));
            if (pointSamples.Any(s => boundaryFrames.Contains((int)Math.Round(s.sampleTime * fps))))
                throw new InvalidOperationException("An In/Out sample conflicts with an explicit constraint on the same model frame.");

            var context = new KimodoTimelineInOutConstraintContext
            {
                Director = trace.Session.Director, Track = trace.Character.Track,
                Animator = trace.Character.Animator, SourceClip = plans[0].Animation.TimelineClip, ModelName = model
            };
            if (!KimodoTimelineSamplingSession.TryCreate(context, model, out var sampler, out string error))
                throw new InvalidOperationException($"In/Out sampling failed: {error}");
            using (sampler)
            {
                foreach (var plan in plans)
                {
                    double[] times = plan.Window.SourceTimes.Select(t => plan.Animation.TimelineStartSeconds + t).ToArray();
                    if (!sampler.TryCaptureTargetBoneSamples(times, fps, out var bones, out error))
                        throw new InvalidOperationException($"In/Out sampling failed: {error}");
                    for (int i = 0; i < times.Length; i++)
                    {
                        if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                            bones[i], sampler.TargetCache, model, "fullbody", plan.Window.OutputFrames[i] / (double)fps,
                            out var sample, out error))
                            throw new InvalidOperationException($"In/Out pose capture failed: {error}");
                        sample.enableMask = KimodoConstraintMask.ForType("fullbody");
                        request.ConstraintSamples.Add(sample);
                    }
                }
            }
            diagnostics["context_before_frames"] = request.ContextBeforeFrames;
            diagnostics["context_after_frames"] = request.ContextAfterFrames;
            diagnostics["runtime_frame_count"] = runtimeFrames;
            diagnostics["crop_start_frame"] = request.ContextBeforeFrames;
            diagnostics["crop_end_frame_exclusive"] = request.ContextBeforeFrames + targetFrames;
            foreach (var plan in plans)
                diagnostics[plan.IsIn ? "in" : "out"]["runtime_model_frames"] =
                    new JArray(plan.Window.OutputFrames.Select(f => f + request.ContextBeforeFrames));
            trace.InOutSampling = diagnostics;
            return request;
        }

        private static void RequireInOutFields(JObject value, params string[] allowed)
        {
            string unknown = value.Properties().Select(p => p.Name).FirstOrDefault(n => !allowed.Contains(n));
            if (unknown != null) throw new InvalidOperationException($"Unknown inout parameter '{unknown}'.");
        }
    }
}
