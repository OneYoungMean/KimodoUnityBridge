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

        internal sealed class CommandInOutEvidencePlan
        {
            public bool IsIn;
            public TimelineAnimationRecord Animation;
            public CommandInOutWindow Window;
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
            int frames = new KimodoFrameWindow(requested, count).ResolveModelFrameCount(fps);
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
                CommandFrameCount60 = requested,
                ModelFrameCount = frames,
                SourceTimes = indices.Select(i => Math.Max(0, first) + i / (double)fps).ToArray(),
                OutputFrames = indices.Select(i => start + i).ToArray(),
                SourceWindowStartSeconds = first,
                SourceWindowDurationSeconds = requested / SessionFrameRate
            };
        }

        // Command In/Out is materialized as the same KimodoPlayableClip fields
        // that the Inspector edits.  Outside evidence clips are placed on the
        // target track so the shared Timeline adapter samples the neighboring
        // clips; no command-owned marker samples are passed into generation.
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
            if (KimodoMotionModelProfiles.TryGetArdy(model, out _))
            {
                throw new InvalidOperationException("Command inout is not supported by ARDY; use the model's Timeline history path.");
            }
            if (loop)
            {
                throw new InvalidOperationException("inout cannot be combined with loop generation.");
            }

            JObject config = entries[0]["inout"] as JObject
                ?? throw new InvalidOperationException("inout must be an object.");
            RequireInOutFields(config, "mode", "in", "out");
            string mode = config.Value<string>("mode") ?? "outside";
            if (mode != "inside" && mode != "outside")
            {
                throw new InvalidOperationException("inout.mode must be inside or outside.");
            }
            if (config["in"] == null && config["out"] == null)
            {
                throw new InvalidOperationException("inout requires in, out, or both.");
            }

            bool inside = mode == "inside";
            if (playableClip != null)
            {
                playableClip.inOutConstraintMode = inside
                    ? KimodoInOutConstraintMode.Inside
                    : KimodoInOutConstraintMode.Outside;
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
                RequireInOutFields(side, "source", "window_frames", "sample_count", "post_apply");
                bool postApply = side.Value<bool?>("post_apply") ?? true;
                JObject source = side["source"] as JObject
                    ?? throw new InvalidOperationException($"inout.{key}.source is required.");
                RequireInOutFields(source, "character", "clip", "frame");
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
                    inside,
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

                if (!inside)
                {
                    if (isIn) contextBefore = window.ModelFrameCount;
                    else contextAfter = window.ModelFrameCount;
                }

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

            if (inside && plans.Sum(plan => plan.Window.ModelFrameCount) > targetFrames &&
                plans.Any(plan => plan.Window.SourceTimes.Length > 1))
            {
                throw new InvalidOperationException("Inside In/Out windows overlap.");
            }
            int runtimeFrames = targetFrames + contextBefore + contextAfter;
            if (runtimeFrames > KimodoMotionModelProfiles.MaxGenerationFrames)
            {
                throw new InvalidOperationException(
                    $"In/Out context needs {runtimeFrames} model frames, exceeding {KimodoMotionModelProfiles.MaxGenerationFrames}; shorten the output or windows.");
            }
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
            trace.InOutEvidencePlans.Clear();
            if (!inside)
            {
                trace.InOutEvidencePlans.AddRange(plans.Select(plan => new CommandInOutEvidencePlan
                {
                    IsIn = plan.IsIn,
                    Animation = plan.Animation,
                    Window = plan.Window
                }));
            }
            return true;
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
            var plans = new List<(bool IsIn, bool PostApply, TimelineAnimationRecord Animation, CommandInOutWindow Window)>();
            foreach (string key in new[] { "in", "out" })
            {
                if (config.Property(key) == null) continue;
                JObject side = config[key] as JObject ?? throw new InvalidOperationException($"inout.{key} must be an object.");
                RequireInOutFields(side, "source", "window_frames", "sample_count", "post_apply");
                bool postApply = side.Value<bool?>("post_apply") ?? true;
                JObject source = side["source"] as JObject ?? throw new InvalidOperationException($"inout.{key}.source is required.");
                RequireInOutFields(source, "character", "clip", "frame");
                string characterName = source.Value<string>("character") ?? trace.Character.Name;
                var character = ResolveSessionCharacterByReference(trace.Session, characterName, addIfMissing: true);
                if (!ReferenceEquals(character, trace.Character))
                    throw new InvalidOperationException("inout source.character must be the generation character; cross-character alignment is not supported.");
                var animation = ResolveAnimation(new JObject { ["animation"] = RequiredStringValue(source, "clip") }, character);
                if (animation.TimelineSegments.Count == 0 || animation.TimelineSegments.Any(s => s.Clip == null))
                    throw new InvalidOperationException($"In/Out source '{animation.Name}' is not a completed playable clip.");
                var window = ResolveCommandInOutWindow(side, mode == "inside", key == "in", animation.TimelineDurationSeconds, targetFrames, fps);
                int startFrame = (int)Math.Floor((animation.TimelineStartSeconds + window.SourceTimes[0]) * SessionFrameRate);
                int endFrame = (int)Math.Ceiling((animation.TimelineStartSeconds + window.SourceTimes.Last()) * SessionFrameRate) + 1;
                ThrowIfGenerationRangeLocked(trace.Session, character, startFrame, endFrame, GenerateAnimationCommand);
                plans.Add((key == "in", postApply, animation, window));
                if (key == "in") request.DeferInConstraint = postApply;
                else request.DeferOutConstraint = postApply;
                if (mode == "outside")
                {
                    if (key == "in") request.ContextBeforeFrames = window.ModelFrameCount;
                    else request.ContextAfterFrames = window.ModelFrameCount;
                }
                diagnostics[key] = new JObject
                {
                    ["source"] = new JObject { ["character"] = character.Name, ["clip"] = animation.Name },
                    ["window_frames"] = side.Value<int?>("window_frames") ?? 1,
                    ["model_window_frames"] = window.ModelFrameCount,
                    ["sample_count"] = window.SourceTimes.Length,
                    ["post_apply"] = postApply,
                    ["source_frames_60"] = new JArray(window.SourceTimes.Select(t => Mathf.RoundToInt((float)(t * SessionFrameRate)))),
                    ["source_timeline_frames_60"] = new JArray(window.SourceTimes.Select(t =>
                        Mathf.RoundToInt((float)((animation.TimelineStartSeconds + t) * SessionFrameRate)))),
                    ["output_model_frames"] = new JArray(window.OutputFrames)
                };
            }
            if (mode == "inside" && plans.Sum(p => p.Window.ModelFrameCount) > targetFrames &&
                plans.Any(p => p.Window.SourceTimes.Length > 1))
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
            var inOutputFrames = new HashSet<int>(plans
                .Where(plan => plan.IsIn)
                .SelectMany(plan => plan.Window.OutputFrames));
            bool skippedOutSampleAtInFrame = false;
            using (sampler)
            {
                foreach (var plan in plans)
                {
                    double[] times = plan.Window.SourceTimes.Select(t => plan.Animation.TimelineStartSeconds + t).ToArray();
                    if (!sampler.TryCaptureTargetBoneSamples(times, fps, out var bones, out error))
                        throw new InvalidOperationException($"In/Out sampling failed: {error}");
                    for (int i = 0; i < times.Length; i++)
                    {
                        if (!plan.IsIn && inOutputFrames.Contains(plan.Window.OutputFrames[i]))
                        {
                            skippedOutSampleAtInFrame = true;
                            continue;
                        }
                        if (!KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                            bones[i], sampler.TargetCache, model, "fullbody", plan.Window.OutputFrames[i] / (double)fps,
                            out var sample, out error))
                            throw new InvalidOperationException($"In/Out pose capture failed: {error}");
                        sample.constraintMode = "fullbody";
                        request.ConstraintSamples.Add(sample);
                        if (plan.IsIn && plan.PostApply)
                        {
                            request.DeferredInConstraintSamples.Add(sample);
                        }
                        else if (!plan.IsIn && plan.PostApply)
                        {
                            request.DeferredOutConstraintSamples.Add(sample);
                        }
                    }
                }
            }
            if (skippedOutSampleAtInFrame)
                diagnostics["overlap_resolution"] = "The In sample takes priority over an Out sample on the same model frame.";
            diagnostics["context_before_model_frames"] = request.ContextBeforeFrames;
            diagnostics["context_after_model_frames"] = request.ContextAfterFrames;
            diagnostics["runtime_model_frame_count"] = runtimeFrames;
            diagnostics["crop_start_model_frame"] = request.ContextBeforeFrames;
            diagnostics["crop_end_model_frame_exclusive"] = request.ContextBeforeFrames + targetFrames;
            foreach (var plan in plans)
                diagnostics[plan.IsIn ? "in" : "out"]["runtime_model_frames"] =
                    new JArray(plan.Window.OutputFrames.Select(f => f + request.ContextBeforeFrames));
            trace.InOutSampling = diagnostics;
            trace.InOutEvidencePlans.Clear();
            if (mode == "outside")
            {
                trace.InOutEvidencePlans.AddRange(plans.Select(plan => new CommandInOutEvidencePlan
                {
                    IsIn = plan.IsIn,
                    Animation = plan.Animation,
                    Window = plan.Window
                }));
            }
            return request;
        }

        private static void RequireInOutFields(JObject value, params string[] allowed)
        {
            string unknown = value.Properties().Select(p => p.Name).FirstOrDefault(n => !allowed.Contains(n));
            if (unknown != null) throw new InvalidOperationException($"Unknown inout parameter '{unknown}'.");
        }

        private static void PrepareCommandOutsideEvidencePosition(TimelineGenerationTrace trace)
        {
            if (trace?.Character == null) return;

            RefreshCharacterTimelineAppendPosition(trace.Character);
            if (trace.InOutEvidencePlans.Count == 0)
            {
                trace.StartSeconds = trace.Character.NextStartSeconds;
                return;
            }

            CommandInOutEvidencePlan input = trace.InOutEvidencePlans.FirstOrDefault(plan => plan.IsIn);
            if (input != null)
                trace.StartSeconds = trace.Character.NextStartSeconds + input.Window.SourceWindowDurationSeconds;
        }

        private static void CreateCommandOutsideEvidenceClips(TimelineGenerationTrace trace)
        {
            if (trace?.TimelineClip == null || trace.Character?.Track == null || trace.InOutEvidencePlans.Count == 0) return;

            var evidenceDescriptions = new JArray();
            foreach (CommandInOutEvidencePlan plan in trace.InOutEvidencePlans)
            {
                double sourceWindowStart = plan.Animation.TimelineStartSeconds + plan.Window.SourceWindowStartSeconds;
                double sourceWindowEnd = sourceWindowStart + plan.Window.SourceWindowDurationSeconds;
                double evidenceStart = plan.IsIn
                    ? trace.StartSeconds - plan.Window.SourceWindowDurationSeconds
                    : trace.TimelineClip.end;
                foreach (TimelineAnimationSegment segment in plan.Animation.TimelineSegments)
                {
                    TimelineClip source = segment?.TimelineClip;
                    if (!(source?.asset is AnimationPlayableAsset sourceAsset) || sourceAsset.clip == null) continue;

                    double clippedStart = Math.Max(sourceWindowStart, source.start);
                    double clippedEnd = Math.Min(sourceWindowEnd, source.end);
                    if (clippedEnd <= clippedStart) continue;

                    TimelineClip evidence = trace.Character.Track.CreateClip<AnimationPlayableAsset>();
                    AnimationPlayableAsset evidenceAsset = evidence.asset as AnimationPlayableAsset;
                    if (evidenceAsset == null)
                        throw new InvalidOperationException("Timeline could not create an In/Out evidence AnimationPlayableAsset.");

                    evidenceAsset.clip = sourceAsset.clip;
                    evidenceAsset.loop = sourceAsset.loop;
                    evidenceAsset.position = sourceAsset.position;
                    evidenceAsset.rotation = sourceAsset.rotation;
                    evidenceAsset.removeStartOffset = sourceAsset.removeStartOffset;
                    evidenceAsset.applyFootIK = sourceAsset.applyFootIK;
                    evidenceAsset.useTrackMatchFields = sourceAsset.useTrackMatchFields;
                    evidenceAsset.matchTargetFields = sourceAsset.matchTargetFields;
                    evidence.start = evidenceStart + (clippedStart - sourceWindowStart);
                    evidence.duration = clippedEnd - clippedStart;
                    evidence.timeScale = source.timeScale;
                    evidence.clipIn = source.ToLocalTime(clippedStart);
                    evidence.displayName = $"InOut {(plan.IsIn ? "In" : "Out")} {source.displayName}";
                    evidenceDescriptions.Add(new JObject
                    {
                        ["side"] = plan.IsIn ? "in" : "out",
                        ["display_name"] = evidence.displayName,
                        ["start_timeline_seconds"] = evidence.start,
                        ["duration_seconds"] = evidence.duration,
                        ["clip_in_seconds"] = evidence.clipIn,
                        ["time_scale"] = evidence.timeScale,
                        ["source_timeline_start_seconds"] = clippedStart,
                        ["source_timeline_end_seconds"] = clippedEnd
                    });
                    EditorUtility.SetDirty(evidenceAsset);
                }
            }

            if (trace.InOutSampling != null)
                trace.InOutSampling["evidence_clips"] = evidenceDescriptions;
            EditorUtility.SetDirty(trace.Character.Track);
            EditorUtility.SetDirty(trace.Session.TimelineAsset);
        }

        private static void ValidateCommandTimelineRange(TimelineGenerationTrace trace)
        {
            if (trace?.Character?.Track == null)
                throw new InvalidOperationException("Timeline generation target has no AnimationTrack.");

            double reservedStart = trace.StartSeconds;
            CommandInOutEvidencePlan input = trace.InOutEvidencePlans.FirstOrDefault(plan => plan.IsIn);
            if (input != null)
                reservedStart -= input.Window.SourceWindowDurationSeconds;

            double reservedEnd = trace.StartSeconds + trace.DurationSeconds;
            foreach (CommandInOutEvidencePlan output in trace.InOutEvidencePlans.Where(plan => !plan.IsIn))
                reservedEnd = Math.Max(reservedEnd, trace.StartSeconds + trace.DurationSeconds + output.Window.SourceWindowDurationSeconds);

            if (double.IsNaN(reservedStart) || double.IsInfinity(reservedStart) ||
                double.IsNaN(reservedEnd) || double.IsInfinity(reservedEnd) || reservedEnd <= reservedStart)
                throw new InvalidOperationException("Timeline generation interval is invalid.");

            TimelineClip conflict = trace.Character.Track.GetClips().FirstOrDefault(clip =>
                clip != null && clip.end > reservedStart && clip.start < reservedEnd);
            if (conflict != null)
            {
                throw new InvalidOperationException(
                    $"Cannot place the In/Out generation block [{reservedStart:F3}, {reservedEnd:F3}) because it overlaps Timeline clip '{conflict.displayName}' [{conflict.start:F3}, {conflict.end:F3}).");
            }
        }
    }
}
