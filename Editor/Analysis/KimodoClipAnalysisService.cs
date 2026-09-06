using System;
using System.Collections.Generic;
using System.Linq;
using KimodoUnityBridge;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoClipAnalysisResult
    {
        internal KimodoPlayableClip Clip;
        internal TimelineClip TimelineClip;
        internal string SourceClipKey;
        internal string SourceRole;
        internal JObject Analysis;
        internal IReadOnlyList<KimodoMarkerSampleResult> FrameSamples;
        internal IReadOnlyList<KimodoAnalysisFrame> MarkerFrames;
    }

    internal sealed class KimodoAnalysisFrame
    {
        internal int Frame;
        internal double Time;
        internal string EventKind;
        internal string Message;
        internal Color Color;
        internal KimodoMarkerSampleResult Sample;
    }

    internal static class KimodoClipAnalysisService
    {
        internal static bool TryAnalyzeSelected(
            KimodoPlayableClip fallback,
            out List<KimodoClipAnalysisResult> results,
            out string error)
        {
            results = new List<KimodoClipAnalysisResult>();
            error = string.Empty;
            List<TimelineClip> clips = KimodoEditorTimelineSelection.GetSelectedPlayableClips(fallback);
            clips.Sort(KimodoPlayableClipGenerationExecutionService.CompareTimelineClips);
            if (clips.Count < 1 || clips.Count > 2)
            {
                error = "Select one or two KimodoPlayableClip Timeline clips.";
                return false;
            }

            foreach (TimelineClip timelineClip in clips)
            {
                if (timelineClip?.asset is not KimodoPlayableClip clip)
                {
                    error = "Selection contains a non-Kimodo playable clip.";
                    return false;
                }
                if (!TryAnalyze(clip, timelineClip, results.Count == 0 ? "A" : "B", out KimodoClipAnalysisResult result, out error))
                {
                    return false;
                }
                results.Add(result);
            }
            return true;
        }

        internal static bool TryRebuildMarkers(IReadOnlyList<KimodoClipAnalysisResult> results, out int markerCount, out string error)
        {
            markerCount = 0;
            error = string.Empty;
            if (results == null || results.Count == 0) return true;

            var undoTargets = new List<UnityEngine.Object>();
            foreach (KimodoClipAnalysisResult result in results)
            {
                TrackAsset track = result?.TimelineClip?.GetParentTrack();
                if (track == null)
                {
                    error = "Selected clip has no parent Timeline track.";
                    return false;
                }
                undoTargets.Add(track);
                if (track.timelineAsset != null) undoTargets.Add(track.timelineAsset);
            }
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Analyze Kimodo Clips");
            Undo.RegisterCompleteObjectUndo(undoTargets.Distinct().ToArray(), "Analyze Kimodo Clips");
            foreach (KimodoClipAnalysisResult result in results)
            {
                TrackAsset track = result.TimelineClip.GetParentTrack();
                foreach (KimodoAnalysisKeyframeMarker old in track.GetMarkers().OfType<KimodoAnalysisKeyframeMarker>()
                    .Where(marker => marker.sourceClipKey == result.SourceClipKey).ToArray())
                {
                    track.DeleteMarker(old);
                }
                foreach (KimodoAnalysisFrame frame in result.MarkerFrames)
                {
                    var marker = track.CreateMarker<KimodoAnalysisKeyframeMarker>(result.TimelineClip.start + frame.Time);
                    marker.frame = frame.Frame;
                    marker.eventKind = frame.EventKind;
                    marker.message = frame.Message;
                    marker.color = frame.Color;
                    marker.sourceClipKey = result.SourceClipKey;
                    marker.sourceRole = result.SourceRole;
                    marker.MarkerType = KimodoConstraintMarkerType.Analysis;
                    marker.autoSample = false;
                    marker.constraintEnabled = true;
                    marker.ConstraintMode = KimodoConstraintMode.FullBody;
                    marker.SampleData = frame.Sample;
                    markerCount++;
                }
                EditorUtility.SetDirty(track);
                if (track.timelineAsset != null) EditorUtility.SetDirty(track.timelineAsset);
            }
            AssetDatabase.SaveAssets();
            Undo.CollapseUndoOperations(undoGroup);
            KimodoConstraintSelectionPreviewTool.SchedulePreviewUpdate();
            return true;
        }

        private static bool TryAnalyze(
            KimodoPlayableClip clip,
            TimelineClip timelineClip,
            string role,
            out KimodoClipAnalysisResult result,
            out string error)
        {
            result = null;
            error = string.Empty;
            try
            {
                TrackAsset parentTrack = timelineClip.GetParentTrack();
                float frameRate = parentTrack?.timelineAsset?.editorSettings.frameRate > 0f
                    ? (float)parentTrack.timelineAsset.editorSettings.frameRate
                    : KimodoMotionModelProfiles.DefaultFrameRate;
                int frameCount = Mathf.Max(1, Mathf.RoundToInt((float)(timelineClip.duration * frameRate)));
                string modelName = KimodoMotionModelProfiles.NormalizeName(clip.bridgeModelName);
                byte[] kmb = KimodoClipConstraintEncoder.EncodeTimeline(
                    timelineClip, modelName, frameCount, frameRate, 0,
                    KimodoInOutConstraintMode.None, false, false, includeFootContacts: true);
                var input = new KimodoEditorAnalysisInput
                {
                    MotionBytes = kmb,
                    StartFrame = 0,
                    EndFrameExclusive = frameCount,
                    ModelName = modelName,
                    TextEncoderMode = KimodoPlayableClipGenerationSettings.instance.DefaultTextEncoderMode,
                    ModelsRoot = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath,
                    AnalysisOptionsJson = new JObject { ["keyframes"] = new JObject { ["enabled"] = true } }.ToString(Formatting.None)
                };
                if (!KimodoPlayableClipGenerationExecutionService.Analysis(input, out string json, out byte[] denseKmb, out error))
                {
                    return false;
                }
                if (!KimodoRawMotionUtility.TryParseFlatBuffer(denseKmb, out KimodoRawMotionData motion, out error)) return false;
                var samples = new List<KimodoMarkerSampleResult>(motion.FrameCount);
                for (int frame = 0; frame < motion.FrameCount; frame++)
                {
                    if (!KimodoRawMotionUtility.TryExtractMarkerSample(motion, modelName, frame, out KimodoMarkerSampleResult sample, out error,
                            constraintType: "fullbody", sampleTime: frame / (double)motion.FrameRate)) return false;
                    samples.Add(sample);
                }
                JObject analysis = string.IsNullOrWhiteSpace(json) ? new JObject() : JObject.Parse(json);
                var frames = BuildMarkerFrames(analysis, samples, role, motion.FrameRate);
                result = new KimodoClipAnalysisResult
                {
                    Clip = clip,
                    TimelineClip = timelineClip,
                    SourceClipKey = GlobalObjectId.GetGlobalObjectIdSlow(clip).ToString(),
                    SourceRole = role,
                    Analysis = analysis,
                    FrameSamples = samples,
                    MarkerFrames = frames
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static List<KimodoAnalysisFrame> BuildMarkerFrames(JObject analysis, IReadOnlyList<KimodoMarkerSampleResult> samples, string role, float frameRate)
        {
            var events = new Dictionary<int, List<string>>();
            void Add(int frame, string kind, string message)
            {
                frame = Mathf.Clamp(frame, 0, Mathf.Max(0, samples.Count - 1));
                if (!events.TryGetValue(frame, out List<string> list)) events.Add(frame, list = new List<string>());
                list.Add(kind + "|" + message);
            }
            Add(0, "start", "Start");
            Add(samples.Count - 1, "end", "End");
            foreach (JObject keyframe in (analysis?["keyframes"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                int frame = keyframe.Value<int?>("frame") ?? Mathf.RoundToInt((float)((keyframe.Value<double?>("time") ?? 0) * frameRate));
                float saliency = keyframe.Value<float?>("saliency") ?? keyframe.Value<float?>("score") ?? 0f;
                string reasons = string.Join(", ", (keyframe["reasons"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>());
                Add(frame, "keyframe", $"Keyframe | frame={frame} | saliency={saliency:F2}" + (string.IsNullOrWhiteSpace(reasons) ? string.Empty : " | " + reasons));
            }
            return events.OrderBy(pair => pair.Key).Select(pair =>
            {
                string[] values = pair.Value.ToArray();
                string kind = values[0].Split('|')[0];
                string message = string.Join(", ", values.Select(value => value.Substring(value.IndexOf('|') + 1)));
                return new KimodoAnalysisFrame
                {
                    Frame = pair.Key,
                    Time = pair.Key / (double)frameRate,
                    EventKind = kind,
                    Message = $"[{role}] {message}",
                    Color = KimodoAnalysisPreviewStyle.ResolveColor(kind, role),
                    Sample = samples[pair.Key]
                };
            }).ToList();
        }
    }
}
