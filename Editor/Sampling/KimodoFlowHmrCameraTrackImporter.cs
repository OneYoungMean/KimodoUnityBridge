using Newtonsoft.Json.Linq;
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoFlowHmrCameraTrackImporter
    {
        internal static bool TryCreateCameraTrack(
            PlayableDirector director,
            TimelineAsset timelineAsset,
            Camera camera,
            JObject analysis,
            double startSeconds,
            out TimelineClip timelineClip,
            out string error)
        {
            timelineClip = null;
            error = string.Empty;
            if (timelineAsset == null || director == null || camera == null)
            {
                error = "A Timeline session and Main Camera are required for the camera track.";
                return false;
            }
            byte[] bytes;
            try { bytes = Convert.FromBase64String(analysis?.Value<string>("kmb_payload_base64") ?? string.Empty); }
            catch (Exception ex) { error = $"FlowHMR KMB payload is invalid: {ex.Message}"; return false; }
            if (!KimodoRawMotionUtility.TryParseFlatBuffer(bytes, out KimodoRawMotionData motion, out error)) return false;
            if (!motion.HasCameraTrack)
            {
                error = "FlowHMR KMB does not contain a camera track. Re-run with enable_camera=true.";
                return false;
            }

            KimodoCameraMotionTrack track = timelineAsset.CreateTrack<KimodoCameraMotionTrack>(null, "FlowHMR Camera");
            director.SetGenericBinding(track, camera.transform);
            TimelineClip timeline = track.CreateClip<KimodoCameraMotionClip>();
            KimodoCameraMotionClip clip = (KimodoCameraMotionClip)timeline.asset;
            clip.frameRate = motion.FrameRate;
            clip.anchorPosition = camera.transform.position;
            clip.anchorRotation = camera.transform.rotation;
            clip.positions = new Vector3[motion.FrameCount];
            clip.rotations = new Quaternion[motion.FrameCount];
            for (int frame = 0; frame < motion.FrameCount; frame++)
            {
                if (!motion.TryReadCamera(frame, out Vector3 position, out Quaternion rotation))
                {
                    error = $"FlowHMR camera track contains an invalid transform at frame {frame}.";
                    return false;
                }
                clip.positions[frame] = position;
                clip.rotations[frame] = rotation;
            }
            timeline.start = startSeconds;
            timeline.duration = Math.Max(1.0 / Math.Max(1f, motion.FrameRate), motion.DurationSeconds);
            timeline.displayName = "FlowHMR Camera";
            timelineClip = timeline;
            EditorUtility.SetDirty(clip);
            EditorUtility.SetDirty(track);
            EditorUtility.SetDirty(timelineAsset);
            return true;
        }
    }
}
