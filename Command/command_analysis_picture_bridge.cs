using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Linq;
using Newtonsoft.Json.Linq;
using KimodoUnityBridge;
using KimodoBridge;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    /// <summary>Editor-facing bridge for rendering the command analysis picture.</summary>
    public static class KimodoAnalysisPictureBridge
    {
        public static void RegisterPrecomputedAnalysis(
            TimelineClip clip,
            JObject analysis,
            System.Collections.Generic.IReadOnlyList<KimodoBridge.KimodoMarkerSampleResult> samples)
        {
            command_context.RegisterPrecomputedAnalysis(clip, analysis, samples);
        }

        public static bool TryRenderSelectedTest(TimelineClip selectedClip, int resolution, out string imagePath, out string error)
        {
            return command_context.TryRenderSelectedAnalysis(new[] { selectedClip }, "-test", resolution, out imagePath, out error);
        }

        public static bool TryRenderSelectedAnalysis(TimelineClip[] selectedClips, string level, int resolution, out string imagePath, out string error)
        {
            return command_context.TryRenderSelectedAnalysis(selectedClips, level, resolution, out imagePath, out error);
        }
    }

    internal static partial class command_context
    {
        internal static void RegisterPrecomputedAnalysis(
            TimelineClip clip,
            JObject analysis,
            IReadOnlyList<KimodoMarkerSampleResult> samples)
        {
            if (clip == null) return;
            string key = RuntimeHelpers.GetHashCode(clip).ToString(System.Globalization.CultureInfo.InvariantCulture);
            PrecomputedAnalyses[key] = new PrecomputedAnalysis
            {
                Analysis = analysis != null ? (JObject)analysis.DeepClone() : new JObject(),
                Samples = samples?.ToArray()
            };
        }

        internal static bool TryRenderSelectedAnalysis(TimelineClip[] selectedClips, string level, int resolution, out string imagePath, out string error)
        {
            imagePath = string.Empty;
            error = string.Empty;
            var precomputedKeys = new List<string>();
            try
            {
                EnsureTimelineSessionsRestored();
                TimelineSessionRecord session = currentTimelineSession;
                if (session == null) { error = "No active command Session. Create/select a Session before rendering -test."; return false; }
                var subjects = new System.Collections.Generic.List<AnalysisSubject>();
                foreach (TimelineClip selectedClip in selectedClips ?? Array.Empty<TimelineClip>())
                {
                    TimelineCharacterRecord character = session.Characters.FirstOrDefault(item => item.Animations.Any(animation => ReferenceEquals(animation.TimelineClip, selectedClip)));
                    TimelineAnimationRecord animation = character?.Animations.FirstOrDefault(item => ReferenceEquals(item.TimelineClip, selectedClip));
                    if (character == null || animation == null) { error = "Selected clip is not loaded in the active command Session."; return false; }
                    int startFrame = Math.Max(0, animation.StartFrame);
                    int endFrame = Math.Max(startFrame + 1, animation.EndFrameExclusive);
                    JObject options = level == "-test" ? new JObject { ["keyframe_count"] = 8 } : new JObject();
                    string inputSignature = BuildAnimationAnalysisSignature(character, animation, options);
                    AnalysisCacheRecord record = EnumerateAnalysisCacheRecords(session).FirstOrDefault(item =>
                        string.Equals(item.AnimationId, animation.Id.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(item.InputSignature, inputSignature, StringComparison.Ordinal));
                    string precomputedKey = RuntimeHelpers.GetHashCode(selectedClip).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    precomputedKeys.Add(precomputedKey);
                    // Inspector analysis is authoritative for this render. It
                    // may arrive alongside an older persisted record with the
                    // same signature; always attach the fresh samples so the
                    // picture path cannot trigger a second pose sampling pass.
                    if (PrecomputedAnalyses.TryGetValue(precomputedKey, out PrecomputedAnalysis precomputed))
                    {
                        JObject precomputedAnalysis = precomputed.Analysis != null ? (JObject)precomputed.Analysis.DeepClone() : new JObject();
                        if (record == null)
                        {
                            string precomputedId = CacheAnalysisResult(session, character, startFrame / SessionFrameRate, endFrame / SessionFrameRate,
                                new JArray(), precomputedAnalysis, null, animation, inputSignature);
                            record = GetCachedAnalysis(session, precomputedId);
                        }
                        else
                        {
                            record.Analysis = precomputedAnalysis;
                            AnalysisCache[record.Id] = record;
                        }
                        if (precomputed.Samples != null) AnalysisPoseSamples[record.Id] = precomputed.Samples;
                    }
                    if (record == null)
                    {
                        JObject analysis = AnalyzeAnimation(session, animation, options, out byte[] motionBytes);
                        NormalizeAnalysisContract(analysis, startFrame, endFrame);
                        string id = CacheAnalysisResult(session, character, startFrame / SessionFrameRate, endFrame / SessionFrameRate,
                            new JArray(), analysis, motionBytes, animation, inputSignature);
                        record = GetCachedAnalysis(session, id);
                    }
                    subjects.Add(new AnalysisSubject(subjects.Count == 0 ? "source" : "target", character, animation, record, startFrame, endFrame));
                }
                if (subjects.Count == 0) { error = "No selected clips were provided."; return false; }
                JObject pictures = level == "-test"
                    ? RenderTestAnalysisPictures(session, subjects[0], resolution < 64 ? 512 : resolution)
                    : RenderAnalysisPictures(session, subjects, "middle", resolution < 64 ? 512 : resolution);
                imagePath = pictures?.Value<string>("image_path") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(imagePath)) { error = "-test rendering completed without an image path."; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                // Inspector analysis hands the bridge one-shot backend output and
                // samples. Do not retain clip references or large sample arrays
                // after the corresponding render has completed (or failed).
                foreach (string key in precomputedKeys.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    PrecomputedAnalyses.Remove(key);
                }
            }
        }
    }
}
