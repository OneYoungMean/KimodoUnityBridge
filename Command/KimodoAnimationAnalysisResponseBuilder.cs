using System;
using System.Linq;
using KimodoBridge;
using Newtonsoft.Json.Linq;

namespace KimodoUnityBridge.Command
{
    internal sealed class KimodoAnimationAnalysisResponseData
    {
        internal KimodoAnimationAnalysisResponseData(
            string analysisId,
            string sessionId,
            string characterName,
            string animationName,
            int startFrame,
            int endFrameExclusive,
            JObject analysis,
            JObject rootTrajectory)
        {
            AnalysisId = analysisId ?? string.Empty;
            SessionId = sessionId ?? string.Empty;
            CharacterName = characterName ?? string.Empty;
            AnimationName = animationName ?? string.Empty;
            StartFrame = startFrame;
            EndFrameExclusive = endFrameExclusive;
            Analysis = analysis;
            RootTrajectory = rootTrajectory;
        }

        internal string AnalysisId { get; }
        internal string SessionId { get; }
        internal string CharacterName { get; }
        internal string AnimationName { get; }
        internal int StartFrame { get; }
        internal int EndFrameExclusive { get; }
        internal JObject Analysis { get; }
        internal JObject RootTrajectory { get; }
    }

    internal static class KimodoAnimationAnalysisResponseBuilder
    {
        // Keep public frame conversion independent of Timeline/session objects.
        internal static JObject BuildPublicResponse(
            KimodoAnimationAnalysisResponseData data,
            bool humanoid,
            JObject pictures,
            string schemaVersion)
        {
            return new JObject
            {
                ["analysis_schema_version"] = schemaVersion,
                ["clip"] = BuildClipResult(data, humanoid),
                ["pictures"] = BuildPictureMetadata(pictures, data)
            };
        }

        internal static JObject BuildClipResult(KimodoAnimationAnalysisResponseData data, bool humanoid)
        {
            var result = new JObject
            {
                ["analysis_id"] = data.AnalysisId,
                ["session_id"] = data.SessionId,
                ["character"] = data.CharacterName,
                ["name"] = data.AnimationName,
                ["start_timeline_frame_60"] = data.StartFrame,
                ["end_timeline_frame_60_exclusive"] = data.EndFrameExclusive,
                ["analysis_mode"] = humanoid ? "humanoid" : "mesh",
                ["phase_track_version"] = humanoid
                    ? data.Analysis?.Value<string>("phase_track_version") ?? string.Empty
                    : "NOT_APPLICABLE",
                ["phase_track"] = BuildPublicPhaseTrack(data),
                ["keyframes"] = data.Analysis?["keyframes"]?.DeepClone() ?? new JArray(),
                ["foot_contacts"] = BuildPublicFootContacts(data)
            };
            if (humanoid)
            {
                result["root_trajectory"] = BuildPublicRootTrajectory(data);
                result["endpoint_pose_comparison"] = BuildPublicEndpointPoseComparison(data);
                if (result["endpoint_pose_comparison"] == null)
                {
                    result["endpoint_pose_comparison"] = new JObject
                    {
                        ["status"] = "insufficient_evidence",
                        ["root_transform_included"] = true,
                        ["root2d_constraint_included"] = false
                    };
                }
                result["motion_profile"] = data.Analysis?["motion_profile"]?.DeepClone() ?? new JObject();
            }
            return result;
        }

        internal static JObject BuildPictureMetadata(
            JObject pictures,
            KimodoAnimationAnalysisResponseData data)
        {
            if (pictures == null) return new JObject();
            JObject result = (JObject)pictures.DeepClone();
            // SceneView captures expose their title, timestamp, and per-tile
            // headers as part of the public evidence metadata.
            foreach (JObject image in (result["images"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (image["description"] is not JObject description) continue;
                if (description["frame"] is JValue frame && frame.Type == JTokenType.Integer)
                {
                    int localFrame = frame.Value<int>();
                    description.Remove("frame");
                    if (localFrame >= 0) description["timeline_frame_60"] = data.StartFrame + localFrame;
                    else description["timeline_frame_60"] = JValue.CreateNull();
                }
                if (description["frames"] is JArray frames)
                {
                    description["timeline_frames_60"] = new JArray(frames.Values<int>().Select(value => data.StartFrame + value));
                    description.Remove("frames");
                }
                if (description["selected_frames"] is JArray selectedFrames)
                {
                    description["selected_timeline_frames_60"] = new JArray(selectedFrames.Values<int>().Select(value => data.StartFrame + value));
                    description.Remove("selected_frames");
                }
                if (description["candidate_frames"] is JArray candidateFrames)
                {
                    description["candidate_timeline_frames_60"] = new JArray(candidateFrames.Values<int>().Select(value => data.StartFrame + value));
                    description.Remove("candidate_frames");
                }
            }
            return result;
        }

        private static JArray BuildPublicPhaseTrack(KimodoAnimationAnalysisResponseData data)
        {
            var result = new JArray();
            foreach (JObject phase in (data.Analysis?["phase_track"] as JArray ?? new JArray()).OfType<JObject>())
            {
                int localStart = phase.Value<int?>("start_frame") ?? 0;
                int localEnd = phase.Value<int?>("end_frame") ?? localStart;
                int anchor = phase.Value<int?>("anchor_frame") ?? localStart;
                JObject item = (JObject)phase.DeepClone();
                item.Remove("start_frame");
                item.Remove("end_frame");
                item.Remove("start_frame_60");
                item.Remove("end_frame_60");
                item.Remove("duration_frames");
                item.Remove("duration_seconds");
                item.Remove("anchor_frame");
                item.Remove("anchor_frame_60");
                item.Remove("anchor_time_seconds");
                item["start_timeline_frame_60"] = data.StartFrame + localStart;
                item["end_timeline_frame_60_exclusive"] = data.StartFrame + localEnd + 1;
                item["duration_frames_60"] = Math.Max(0, localEnd - localStart + 1);
                item["anchor_timeline_frame_60"] = data.StartFrame + anchor;
                result.Add(item);
            }
            return result;
        }

        private static JArray BuildPublicFootContacts(KimodoAnimationAnalysisResponseData data)
        {
            var result = new JArray();
            double sourceFrameRate = data?.Analysis?.Value<double?>("frame_rate")
                ?? data?.Analysis?.Value<double?>("fps")
                ?? KimodoFrameTimeUtility.CommandFrameRate;
            if (sourceFrameRate <= 0.0 || double.IsNaN(sourceFrameRate) || double.IsInfinity(sourceFrameRate))
                sourceFrameRate = KimodoFrameTimeUtility.CommandFrameRate;
            foreach (JObject contact in (data.Analysis?["foot_contacts"] as JArray ?? new JArray()).OfType<JObject>())
            {
                JObject item = (JObject)contact.DeepClone();
                int localFrame = item.Value<int?>("frame") ?? 0;
                int timelineFrame = item.Value<int?>("timeline_frame_60") ??
                    data.StartFrame + KimodoFrameTimeUtility.ConvertFrameIndex(
                        localFrame,
                        sourceFrameRate,
                        KimodoFrameTimeUtility.CommandFrameRate);
                int durationFrames60;
                if (item.Value<int?>("duration_frames") is int modelDuration)
                {
                    durationFrames60 = modelDuration <= 0
                        ? 0
                        : KimodoFrameTimeUtility.ConvertFrameCount(
                            modelDuration,
                            sourceFrameRate,
                            KimodoFrameTimeUtility.CommandFrameRate);
                }
                else
                {
                    durationFrames60 = Math.Max(0, item.Value<int?>("duration_frames_60") ?? 0);
                }
                item.Remove("frame");
                item.Remove("duration_frames");
                item.Remove("duration_frames_60");
                item["timeline_frame_60"] = timelineFrame;
                item["duration_frames_60"] = durationFrames60;
                result.Add(item);
            }
            return result;
        }

        private static JObject BuildPublicRootTrajectory(KimodoAnimationAnalysisResponseData data)
        {
            JObject result = data.RootTrajectory != null
                ? (JObject)data.RootTrajectory.DeepClone()
                : new JObject();
            int frameCount = result.Value<int?>("frame_count") ?? Math.Max(1, data.EndFrameExclusive - data.StartFrame);
            result.Remove("duration_seconds");
            result.Remove("sample_span_seconds");
            // Public analysis uses the command/session timeline basis. Never
            // expose an unqualified model-frame duration here.
            result.Remove("duration_frames");
            result.Remove("sample_span_frames");
            result.Remove("duration_frames_60");
            result.Remove("sample_span_frames_60");
            result["duration_frames_60"] = frameCount;
            result["sample_span_frames_60"] = Math.Max(0, frameCount - 1);
            if (result["samples"] is JArray samples)
            {
                foreach (JObject sample in samples.OfType<JObject>())
                {
                    int localFrame = sample.Value<int?>("frame") ?? 0;
                    sample.Remove("frame");
                    sample["timeline_frame_60"] = data.StartFrame + localFrame;
                }
            }
            return result;
        }

        private static JObject BuildPublicEndpointPoseComparison(KimodoAnimationAnalysisResponseData data)
        {
            JObject result = data.Analysis?["endpoint_pose_comparison"] as JObject;
            if (result == null) return null;
            result = (JObject)result.DeepClone();
            int localStart = result.Value<int?>("start_frame") ?? 0;
            int localEnd = result.Value<int?>("end_frame") ?? Math.Max(0, data.EndFrameExclusive - data.StartFrame - 1);
            result.Remove("start_frame");
            result.Remove("end_frame");
            result["start_timeline_frame_60"] = data.StartFrame + localStart;
            result["end_timeline_frame_60"] = data.StartFrame + localEnd;
            return result;
        }
    }
}
