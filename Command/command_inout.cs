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
            public TimelineClip EvidenceClip;
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
                if (animation.Clip == null ||
                    (animation.TimelineSegments.Count > 0 && animation.TimelineSegments.Any(segment => segment.Clip == null)))
                {
                    throw new InvalidOperationException($"In/Out source '{animation.Name}' does not contain a usable AnimationClip.");
                }

                CommandInOutWindow window = ResolveCommandInOutWindow(
                    side,
                    isIn,
                    animation.TimelineDurationSeconds,
                    targetFrames,
                    fps);
                if (animation.TimelineClip != null)
                {
                    int sourceStartFrame = Mathf.FloorToInt((float)((animation.TimelineStartSeconds + window.SourceTimes[0]) * SessionFrameRate));
                    int sourceEndFrame = Mathf.CeilToInt((float)((animation.TimelineStartSeconds + window.SourceTimes.Last()) * SessionFrameRate)) + 1;
                    ThrowIfGenerationRangeLocked(
                        trace.Session,
                        sourceCharacter,
                        sourceStartFrame,
                        sourceEndFrame,
                        GenerateAnimationCommand);
                }
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
                        playableClip.inConstraintSourceClip = animation.TimelineClip == null ? animation.Clip : null;
                        playableClip.inConstraintSourceTrack = animation.TimelineClip?.GetParentTrack();
                        playableClip.inConstraintSourceAsset = animation.TimelineClip?.asset as PlayableAsset;
                        playableClip.inConstraintSourceClipStart = animation.TimelineClip?.start ?? -1.0;
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
                        playableClip.outConstraintSourceClip = animation.TimelineClip == null ? animation.Clip : null;
                        playableClip.outConstraintSourceTrack = animation.TimelineClip?.GetParentTrack();
                        playableClip.outConstraintSourceAsset = animation.TimelineClip?.asset as PlayableAsset;
                        playableClip.outConstraintSourceClipStart = animation.TimelineClip?.start ?? -1.0;
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
            trace.InOutEvidencePlans.Clear();
            trace.InOutEvidencePlans.AddRange(plans.Select(plan => new CommandInOutEvidencePlan
            {
                IsIn = plan.IsIn,
                Animation = plan.Animation,
                Window = plan.Window
            }));
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
            if (animation.Clip == null)
                throw new InvalidOperationException($"Inside source '{animation.Name}' does not contain a usable AnimationClip.");

            // Inside is an optimization of the supplied AnimationClip. Keep that
            // clip on the KimodoPlayableClip itself so command and Inspector
            // generation use the same Timeline carrier. Finalization replaces it
            // with the generated result.
            playableClip.clip = animation.Clip;
            playableClip.insideConstraintSourceClip = null;
            playableClip.insideConstraintSourceTrack = null;
            playableClip.insideConstraintSourceAsset = null;
            playableClip.insideConstraintSourceClipStart = -1.0;
            TimelineClip sourceClip = animation.TimelineSegments.Count == 1
                ? animation.TimelineSegments[0]?.TimelineClip
                : null;
            if (sourceClip != null)
            {
                int sourceStartFrame = Mathf.FloorToInt((float)(sourceClip.start * SessionFrameRate));
                int sourceEndFrame = Mathf.CeilToInt((float)(sourceClip.end * SessionFrameRate));
                ThrowIfGenerationRangeLocked(
                    trace.Session,
                    sourceCharacter,
                    sourceStartFrame,
                    sourceEndFrame,
                    GenerateAnimationCommand);
            }

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
            trace.InOutSampling ??= new JObject();
            trace.InOutSampling["inside"] = new JObject
            {
                ["source"] = new JObject { ["character"] = sourceCharacter.Name, ["clip"] = animation.Name },
                ["post_apply"] = playableClip.postApplyInsideConstraint,
                ["source_duration_seconds"] = animation.TimelineDurationSeconds,
                ["target_duration_seconds"] = trace.DurationSeconds
            };
            return true;
        }

        private static void PrepareCommandOutsideEvidencePosition(TimelineGenerationTrace trace)
        {
            if (trace?.Character == null)
                return;

            RefreshCharacterTimelineAppendPosition(trace.Character);
            CommandInOutEvidencePlan input = trace.InOutEvidencePlans.FirstOrDefault(plan => plan.IsIn);
            trace.StartSeconds = trace.Character.NextStartSeconds +
                (input?.Window.SourceWindowDurationSeconds ?? 0.0);
        }

        private static void CreateCommandOutsideEvidenceClips(TimelineGenerationTrace trace)
        {
            if (trace?.TimelineClip == null || trace.Character?.Track == null ||
                trace.InOutEvidencePlans.Count == 0)
                return;

            var evidenceDescriptions = new JArray();
            foreach (CommandInOutEvidencePlan plan in trace.InOutEvidencePlans)
            {
                double sourceWindowStart = plan.Animation.TimelineStartSeconds + plan.Window.SourceWindowStartSeconds;
                double sourceWindowEnd = sourceWindowStart + plan.Window.SourceWindowDurationSeconds;
                double evidenceStart = plan.IsIn
                    ? trace.StartSeconds - plan.Window.SourceWindowDurationSeconds
                    : trace.TimelineClip.end;
                if (plan.EvidenceClip != null && plan.EvidenceClip.asset != null)
                {
                    BindCommandOutsideEvidence(trace, plan, plan.EvidenceClip);
                    AppendEvidenceDescription(evidenceDescriptions, plan, plan.EvidenceClip, sourceWindowStart, sourceWindowEnd, "materialized");
                    continue;
                }

                // External command references have no source TimelineClip.
                // Materialize only the visible evidence window so Context and
                // the Timeline UI still expose the same three-part layout.
                if (plan.Animation.TimelineSegments.Count == 0 && plan.Animation.Clip != null)
                {
                    double clippedStart = Math.Max(0.0, sourceWindowStart);
                    double clippedEnd = Math.Min(plan.Animation.Clip.length, sourceWindowEnd);
                    if (clippedEnd > clippedStart)
                    {
                        TimelineClip evidence = trace.Character.Track.CreateClip<AnimationPlayableAsset>();
                        AnimationPlayableAsset evidenceAsset = evidence.asset as AnimationPlayableAsset;
                        if (evidenceAsset == null)
                            throw new InvalidOperationException("Timeline could not create an In/Out evidence AnimationPlayableAsset.");
                        evidenceAsset.clip = plan.Animation.Clip;
                        evidenceAsset.removeStartOffset = false;
                        evidence.start = evidenceStart + (clippedStart - sourceWindowStart);
                        evidence.duration = clippedEnd - clippedStart;
                        evidence.timeScale = 1.0;
                        evidence.clipIn = clippedStart;
                        evidence.displayName = $"InOut {(plan.IsIn ? "In" : "Out")} {plan.Animation.Name}";
                        plan.EvidenceClip = evidence;
                        BindCommandOutsideEvidence(trace, plan, evidence);
                        AppendEvidenceDescription(evidenceDescriptions, plan, evidence, clippedStart, clippedEnd, "external");
                        EditorUtility.SetDirty(evidenceAsset);
                    }
                    continue;
                }

                foreach (TimelineAnimationSegment segment in plan.Animation.TimelineSegments)
                {
                    TimelineClip source = segment?.TimelineClip;
                    if (!(source?.asset is AnimationPlayableAsset sourceAsset) || sourceAsset.clip == null)
                        continue;

                    double clippedStart = Math.Max(sourceWindowStart, source.start);
                    double clippedEnd = Math.Min(sourceWindowEnd, source.end);
                    if (clippedEnd <= clippedStart)
                        continue;

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
                    plan.EvidenceClip = evidence;
                    BindCommandOutsideEvidence(trace, plan, evidence);
                    AppendEvidenceDescription(evidenceDescriptions, plan, evidence, clippedStart, clippedEnd, "timeline");
                    EditorUtility.SetDirty(evidenceAsset);
                }
            }

            if (trace.InOutSampling != null)
                trace.InOutSampling["evidence_clips"] = evidenceDescriptions;
            EditorUtility.SetDirty(trace.Character.Track);
            EditorUtility.SetDirty(trace.Session.TimelineAsset);
        }

        private static void BindCommandOutsideEvidence(
            TimelineGenerationTrace trace, CommandInOutEvidencePlan plan, TimelineClip evidence)
        {
            if (trace?.PlayableClip == null || plan == null || evidence == null)
                return;

            if (plan.IsIn)
            {
                trace.PlayableClip.inConstraintSourceClip = null;
                trace.PlayableClip.inConstraintSourceTrack = evidence.GetParentTrack();
                trace.PlayableClip.inConstraintSourceAsset = evidence.asset as PlayableAsset;
                trace.PlayableClip.inConstraintSourceClipStart = evidence.start;
                // The evidence clip already represents the requested source
                // window. Do not apply the original command-relative frame a
                // second time when the shared Timeline sampler resolves it.
                trace.PlayableClip.inConstraintSourceFrame = -1;
            }
            else
            {
                trace.PlayableClip.outConstraintSourceClip = null;
                trace.PlayableClip.outConstraintSourceTrack = evidence.GetParentTrack();
                trace.PlayableClip.outConstraintSourceAsset = evidence.asset as PlayableAsset;
                trace.PlayableClip.outConstraintSourceClipStart = evidence.start;
                // The evidence clip already represents the requested source
                // window. Do not apply the original command-relative frame a
                // second time when the shared Timeline sampler resolves it.
                trace.PlayableClip.outConstraintSourceFrame = -1;
            }
        }

        private static void AppendEvidenceDescription(
            JArray descriptions, CommandInOutEvidencePlan plan, TimelineClip evidence,
            double sourceStart, double sourceEnd, string sourceKind)
        {
            if (descriptions == null || plan == null || evidence == null)
                return;

            descriptions.Add(new JObject
            {
                ["side"] = plan.IsIn ? "in" : "out",
                ["display_name"] = evidence.displayName,
                ["start_timeline_seconds"] = evidence.start,
                ["duration_seconds"] = evidence.duration,
                ["clip_in_seconds"] = evidence.clipIn,
                ["time_scale"] = evidence.timeScale,
                ["source_timeline_start_seconds"] = sourceStart,
                ["source_timeline_end_seconds"] = sourceEnd,
                ["source"] = sourceKind
            });
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
            {
                reservedEnd = Math.Max(
                    reservedEnd,
                    trace.StartSeconds + trace.DurationSeconds + output.Window.SourceWindowDurationSeconds);
            }

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
