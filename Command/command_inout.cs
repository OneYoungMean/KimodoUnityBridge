using System;
using System.Collections.Generic;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        internal sealed class CommandInOutWindow
        {
            public int CommandFrameCount60;
            public int ModelFrameCount;
            public double[] SourceTimes;
            public int[] OutputFrames;
            public double SourceWindowStartSeconds;
            public double SourceWindowDurationSeconds;
        }

        // Command frames use 60 FPS. The source is the played Clip range,
        // including clipIn/timeScale; the sampler evaluates its actual Timeline.
        internal static CommandInOutWindow ResolveCommandInOutWindow(
            JObject side, bool isIn, double sourceDuration, int targetFrames, float fps)
        {
            int requested = side["window_frames"] == null ? 1 : RequiredNonNegativeFrame(side, "window_frames");
            int count = side["sample_count"] == null ? 1 : RequiredNonNegativeFrame(side, "sample_count");
            if (requested < 1)
                throw new InvalidOperationException("inout.window_frames must be positive and fit the model frame limit.");
            KimodoConstraintWindowSpec windowSpec = KimodoConstraintWindowSpec.FromCommandFrames60(requested, count, fps);
            int frames = windowSpec.ModelFrameCount;
            if (frames > KimodoMotionModelProfiles.MaxGenerationFrames)
                throw new InvalidOperationException("inout.window_frames must be positive and fit the model frame limit.");
            if (count < 1 || count > frames)
                throw new InvalidOperationException($"inout.sample_count must be within [1,{frames}] after conversion to model FPS; duplicate sample frames are not allowed.");
            bool forward = !isIn;
            JObject source = side["source"] as JObject
                ?? throw new InvalidOperationException("inout.source must be an object.");
            double boundary = source["frame"] == null
                ? (forward ? 0 : sourceDuration - 1.0 / fps)
                : KimodoFrameTimeUtility.CommandFrameIndexToSeconds(RequiredNonNegativeFrame(source, "frame"));
            double first = boundary - (forward ? 0 : (frames - 1.0) / fps);
            double last = first + (frames - 1.0) / fps;
            if (sourceDuration <= 0 || first < -1e-7 || last >= sourceDuration - 1e-7)
                throw new InvalidOperationException("In/Out source clip is too short or source.frame/window_frames exceeds its played range.");
            int start = isIn ? -frames : targetFrames;
            int[] indices = KimodoInOutConstraintTools.BuildWindowSampleFrames(frames, count, forward);
            return new CommandInOutWindow
            {
                CommandFrameCount60 = requested,
                ModelFrameCount = frames,
                SourceTimes = indices.Select(i => Math.Max(0, first) + i / (double)fps).ToArray(),
                OutputFrames = indices.Select(i => start + i).ToArray(),
                SourceWindowStartSeconds = first,
                SourceWindowDurationSeconds = KimodoFrameTimeUtility.CommandFramesToSeconds(requested)
            };
        }

        // Outside windows are sampled into KMB ClipConstraints by the shared request builder.
        private static bool ConfigureCommandInOutClip(
            JObject arguments,
            TimelineGenerationTrace trace,
            KimodoPlayableClip playableClip,
            string model,
            int targetFrames,
            float fps,
            bool loop,
            IReadOnlyList<KimodoMarkerSampleResult> pointSamples)
        {
            var entries = (arguments["constraints"] as JArray)?.OfType<JObject>()
                .Where(item => item.Property("inout") != null).ToArray() ?? Array.Empty<JObject>();
            if (entries.Length == 0)
            {
                return false;
            }
            if (entries.Length != 1 || entries[0].Properties().Count() != 1)
            {
                throw new InvalidOperationException("Supply one standalone inout entry; put point constraints in separate entries.");
            }
            JObject config = entries[0]["inout"] as JObject
                ?? throw new InvalidOperationException("inout must be an object.");
            string mode = config.Value<string>("mode") ?? "outside";
            if (mode != "outside")
                throw new InvalidOperationException("inout.mode only supports outside; use the separate inside constraint for Inside.");
            if (config["in"] == null && config["out"] == null)
            {
                throw new InvalidOperationException("inout requires in, out, or both.");
            }

            if (playableClip != null)
            {
                playableClip.inOutConstraintMode = KimodoInOutConstraintMode.Outside;
                playableClip.enableInConstraint = false;
                playableClip.enableOutConstraint = false;
            }

            int contextBefore = 0;
            int contextAfter = 0;
            var plans = new List<(bool IsIn, TimelineAnimationRecord Animation, CommandInOutWindow Window, bool PostApply)>();
            var diagnostics = new JObject
            {
                ["mode"] = mode,
                ["command_fps"] = SessionFrameRate,
                ["model_fps"] = fps
            };

            foreach (string key in new[] { "in", "out" })
            {
                if (config.Property(key) == null)
                {
                    continue;
                }

                bool isIn = key == "in";
                JObject side = config[key] as JObject
                    ?? throw new InvalidOperationException($"inout.{key} must be an object.");
                bool postApply = side.Value<bool?>("post_apply") ?? true;
                JObject source = side["source"] as JObject
                    ?? throw new InvalidOperationException($"inout.{key}.source is required.");
                string characterName = source.Value<string>("character") ?? trace.Character.Name;
                TimelineCharacterRecord sourceCharacter = ResolveSessionCharacterByReference(
                    trace.Session, characterName, addIfMissing: true);
                if (!ReferenceEquals(sourceCharacter, trace.Character))
                {
                    throw new InvalidOperationException("inout source.character must be the generation character; cross-character alignment is not supported.");
                }

                TimelineAnimationRecord animation = ResolveAnimation(
                    new JObject { ["animation"] = RequiredStringValue(source, "clip") },
                    sourceCharacter);
                if (animation.TimelineSegments.Count == 0 || animation.TimelineSegments.Any(segment => segment.Clip == null))
                {
                    throw new InvalidOperationException($"In/Out source '{animation.Name}' is not a completed playable clip.");
                }

                CommandInOutWindow window = ResolveCommandInOutWindow(
                    side,
                    isIn,
                    animation.TimelineDurationSeconds,
                    targetFrames,
                    fps);
                int sourceStartFrame = Mathf.FloorToInt((float)((animation.TimelineStartSeconds + window.SourceTimes[0]) * SessionFrameRate));
                int sourceEndFrame = Mathf.CeilToInt((float)((animation.TimelineStartSeconds + window.SourceTimes.Last()) * SessionFrameRate)) + 1;
                ThrowIfGenerationRangeLocked(
                    trace.Session,
                    sourceCharacter,
                    sourceStartFrame,
                    sourceEndFrame,
                    GenerateAnimationCommand);
                plans.Add((isIn, animation, window, postApply));

                if (isIn) contextBefore = window.ModelFrameCount;
                else contextAfter = window.ModelFrameCount;

                if (playableClip != null)
                {
                    if (isIn)
                    {
                        playableClip.enableInConstraint = true;
                        playableClip.postApplyInConstraint = postApply;
                        playableClip.inConstraintWindowFrames60 = window.CommandFrameCount60;
                        playableClip.inConstraintSampleCount = window.SourceTimes.Length;
                        playableClip.inConstraintSourceTrack = animation.TimelineClip.GetParentTrack();
                        playableClip.inConstraintSourceAsset = animation.TimelineClip.asset as PlayableAsset;
                        playableClip.inConstraintSourceClipStart = animation.TimelineClip.start;
                        playableClip.inConstraintSourceFrame = source["frame"] == null
                            ? -1
                            : RequiredNonNegativeFrame(source, "frame");
                    }
                    else
                    {
                        playableClip.enableOutConstraint = true;
                        playableClip.postApplyOutConstraint = postApply;
                        playableClip.outConstraintWindowFrames60 = window.CommandFrameCount60;
                        playableClip.outConstraintSampleCount = window.SourceTimes.Length;
                        playableClip.outConstraintSourceTrack = animation.TimelineClip.GetParentTrack();
                        playableClip.outConstraintSourceAsset = animation.TimelineClip.asset as PlayableAsset;
                        playableClip.outConstraintSourceClipStart = animation.TimelineClip.start;
                        playableClip.outConstraintSourceFrame = source["frame"] == null
                            ? -1
                            : RequiredNonNegativeFrame(source, "frame");
                    }
                }

                diagnostics[key] = new JObject
                {
                    ["source"] = new JObject { ["character"] = sourceCharacter.Name, ["clip"] = animation.Name },
                    ["window_frames"] = side.Value<int?>("window_frames") ?? 1,
                    ["model_window_frames"] = window.ModelFrameCount,
                    ["sample_count"] = window.SourceTimes.Length,
                    ["post_apply"] = postApply,
                    ["source_frames_60"] = new JArray(window.SourceTimes.Select(time =>
                        Mathf.RoundToInt((float)(time * SessionFrameRate)))),
                    ["source_timeline_frames_60"] = new JArray(window.SourceTimes.Select(time =>
                        Mathf.RoundToInt((float)((animation.TimelineStartSeconds + time) * SessionFrameRate)))),
                    ["output_model_frames"] = new JArray(window.OutputFrames)
                };
            }

            int runtimeFrames = targetFrames + contextBefore + contextAfter;
            var boundaryFrames = new HashSet<int>(plans.SelectMany(plan => plan.Window.OutputFrames));
            if ((pointSamples ?? Array.Empty<KimodoMarkerSampleResult>())
                .Any(sample => sample != null && boundaryFrames.Contains((int)Math.Round(sample.sampleTime * fps))))
            {
                throw new InvalidOperationException("An In/Out sample conflicts with an explicit constraint on the same model frame.");
            }

            diagnostics["context_before_model_frames"] = contextBefore;
            diagnostics["context_after_model_frames"] = contextAfter;
            diagnostics["runtime_model_frame_count"] = runtimeFrames;
            diagnostics["crop_start_model_frame"] = contextBefore;
            diagnostics["crop_end_model_frame_exclusive"] = contextBefore + targetFrames;
            foreach (var plan in plans)
            {
                diagnostics[plan.IsIn ? "in" : "out"]["runtime_model_frames"] =
                    new JArray(plan.Window.OutputFrames.Select(frame => frame + contextBefore));
            }
            trace.InOutSampling = diagnostics;
            return true;
        }

        private static bool ConfigureCommandInsideClip(
            JObject arguments,
            TimelineGenerationTrace trace,
            KimodoPlayableClip playableClip)
        {
            var entries = (arguments["constraints"] as JArray)?.OfType<JObject>()
                .Where(item => item.Property("inside") != null).ToArray() ?? Array.Empty<JObject>();
            if (entries.Length == 0) return false;
            if (entries.Length != 1 || entries[0].Properties().Count() != 1)
                throw new InvalidOperationException("Supply one standalone inside entry; put point constraints in separate entries.");
            if (playableClip == null)
                throw new ArgumentNullException(nameof(playableClip));

            JObject config = entries[0]["inside"] as JObject
                ?? throw new InvalidOperationException("inside must be an object.");
            JObject source = config["source"] as JObject
                ?? throw new InvalidOperationException("inside.source is required.");
            string characterName = source.Value<string>("character") ?? trace.Character.Name;
            TimelineCharacterRecord sourceCharacter = ResolveSessionCharacterByReference(
                trace.Session, characterName, addIfMissing: true);
            if (!ReferenceEquals(sourceCharacter, trace.Character))
                throw new InvalidOperationException("inside.source.character must be the generation character.");

            TimelineAnimationRecord animation = ResolveAnimation(
                new JObject { ["animation"] = RequiredStringValue(source, "clip") },
                sourceCharacter);
            if (animation.TimelineSegments.Count != 1 || animation.TimelineSegments[0]?.TimelineClip == null)
                throw new InvalidOperationException($"Inside source '{animation.Name}' must resolve to one completed Timeline Clip.");
            TimelineClip sourceClip = animation.TimelineSegments[0].TimelineClip;
            int sourceStartFrame = Mathf.FloorToInt((float)(sourceClip.start * SessionFrameRate));
            int sourceEndFrame = Mathf.CeilToInt((float)(sourceClip.end * SessionFrameRate));
            ThrowIfGenerationRangeLocked(
                trace.Session,
                sourceCharacter,
                sourceStartFrame,
                sourceEndFrame,
                GenerateAnimationCommand);

            playableClip.insideConstraintEnabled = true;
            playableClip.postApplyInsideConstraint = config.Value<bool?>("post_apply") ?? true;
            string samplingMode = config.Value<string>("sampling_mode") ?? "uniform";
            playableClip.insideKeyframeMode = samplingMode switch
            {
                "uniform" => KimodoInsideKeyframeMode.Uniform,
                "foot_exchange" => KimodoInsideKeyframeMode.FootExchange,
                "mixed" => KimodoInsideKeyframeMode.Mixed,
                _ => throw new InvalidOperationException("inside.sampling_mode must be uniform, foot_exchange, or mixed.")
            };
            playableClip.insideKeyframeSampleCount = config.Value<int?>("sample_count") ?? 2;
            if (playableClip.insideKeyframeSampleCount < 1)
                throw new InvalidOperationException("inside.sample_count must be positive.");
            playableClip.insideConstraintSourceTrack = sourceClip.GetParentTrack();
            playableClip.insideConstraintSourceAsset = sourceClip.asset as PlayableAsset;
            playableClip.insideConstraintSourceClipStart = sourceClip.start;
            trace.InOutSampling ??= new JObject();
            trace.InOutSampling["inside"] = new JObject
            {
                ["source"] = new JObject { ["character"] = sourceCharacter.Name, ["clip"] = animation.Name },
                ["post_apply"] = playableClip.postApplyInsideConstraint,
                ["source_duration_seconds"] = sourceClip.duration,
                ["target_duration_seconds"] = trace.DurationSeconds
            };
            return true;
        }

    }
}
