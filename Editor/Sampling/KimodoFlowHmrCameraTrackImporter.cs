using Newtonsoft.Json.Linq;
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoFlowHmrCameraTrackImporter
    {
        private const string CameraTrackName = "FlowHMR Camera";
        private const string CameraClipPrefix = "Kimodo_FlowHMR_Camera_";

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
            try
            {
                bytes = Convert.FromBase64String(analysis?.Value<string>("kmb_payload_base64") ?? string.Empty);
            }
            catch (Exception ex)
            {
                error = $"FlowHMR KMB payload is invalid: {ex.Message}";
                return false;
            }

            if (!KimodoRawMotionUtility.TryParseFlatBuffer(bytes, out KimodoRawMotionData motion, out error))
                return false;
            if (!motion.HasCameraTrack)
            {
                error = "FlowHMR KMB does not contain a camera track. Re-run with enable_camera=true.";
                return false;
            }

            if (!KimodoFlowHmrTiming.TryRead(
                motion, analysis,
                out float[] frameTimes, out float sourceDuration,
                out string timingError))
            {
                error = timingError;
                return false;
            }

            // AnimationTrack is Unity Timeline's native transform/property route.
            // It binds to an Animator, so a camera receives a native AnimationTrack
            // binding rather than a custom camera PlayableAsset.
            Animator animator = camera.GetComponent<Animator>();
            if (animator == null)
                animator = Undo.AddComponent<Animator>(camera.gameObject);
            if (animator == null)
            {
                error = "Could not add an Animator to Main Camera for the native camera animation track.";
                return false;
            }
            AnimationClip cameraClip = KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(
                $"{CameraClipPrefix}{Guid.NewGuid():N}");
            cameraClip.frameRate = motion.FrameRate;
            cameraClip.legacy = false;
            if (!TryWriteCameraCurves(
                    cameraClip,
                    motion,
                    camera.transform.parent,
                    frameTimes,
                    sourceDuration,
                    out error))
            {
                if (!KimodoEditorClipWritebackService.TryArchiveGeneratedAnimationClipAsset(
                        cameraClip, out string archiveError) && string.IsNullOrEmpty(error))
                    error = $"Camera curve write failed and the generated clip could not be archived: {archiveError}";
                return false;
            }

            AnimationTrack track = timelineAsset.CreateTrack<AnimationTrack>(null, CameraTrackName);
            // The clip stores local curves relative to the camera's parent. An
            // explicit transform-offset track keeps Timeline from deriving a
            // scene offset from an existing Animator Controller.
            track.trackOffset = TrackOffset.ApplyTransformOffsets;
            track.position = Vector3.zero;
            track.rotation = Quaternion.identity;
            director.SetGenericBinding(track, animator);
            TimelineClip timeline = track.CreateClip(cameraClip);
            timeline.start = startSeconds;
            timeline.duration = Math.Max(
                1.0 / Math.Max(1f, motion.FrameRate),
                sourceDuration > 0f ? sourceDuration : motion.DurationSeconds);
            timeline.displayName = CameraTrackName;
            if (timeline.asset is AnimationPlayableAsset playableAsset)
            {
                // These are world-space camera samples. Do not subtract the first
                // key or apply a Timeline scene offset to them.
                playableAsset.removeStartOffset = false;
                playableAsset.useTrackMatchFields = false;
                playableAsset.position = Vector3.zero;
                playableAsset.rotation = Quaternion.identity;
            }

            EditorUtility.SetDirty(cameraClip);
            EditorUtility.SetDirty(track);
            EditorUtility.SetDirty(timelineAsset);
            AssetDatabase.SaveAssets();
            timelineClip = timeline;
            return true;
        }

        private static bool TryWriteCameraCurves(
            AnimationClip clip,
            KimodoRawMotionData motion,
            Transform parent,
            float[] frameTimes,
            float sourceDuration,
            out string error)
        {
            error = string.Empty;
            var positionX = new AnimationCurve();
            var positionY = new AnimationCurve();
            var positionZ = new AnimationCurve();
            var rotationX = new AnimationCurve();
            var rotationY = new AnimationCurve();
            var rotationZ = new AnimationCurve();
            var rotationW = new AnimationCurve();
            var fov = new AnimationCurve();
            var lensShiftX = new AnimationCurve();
            var lensShiftY = new AnimationCurve();
            Quaternion previousRotation = Quaternion.identity;
            bool hasPreviousRotation = false;

            for (int frame = 0; frame < motion.FrameCount; frame++)
            {
                if (!motion.TryReadCamera(frame, out Vector3 position, out Quaternion rotation))
                {
                    error = $"FlowHMR camera track contains an invalid transform at frame {frame}.";
                    return false;
                }

                if (hasPreviousRotation && Quaternion.Dot(previousRotation, rotation) < 0f)
                    rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                previousRotation = rotation;
                hasPreviousRotation = true;

                float time = frameTimes[frame];
                // KMB stores camera-to-world samples. AnimationClip Transform
                // bindings are local, so convert each sample through the current
                // parent (the Main Camera is normally a root object).
                Vector3 localPosition = parent != null
                    ? parent.InverseTransformPoint(position)
                    : position;
                Quaternion localRotation = parent != null
                    ? (Quaternion.Inverse(parent.rotation) * rotation).normalized
                    : rotation;
                AddVector3Key(time, localPosition, positionX, positionY, positionZ);
                AddQuaternionKey(time, localRotation, rotationX, rotationY, rotationZ, rotationW);

                if (motion.HasCameraParameters)
                {
                    if (!motion.TryReadCameraParameters(frame, out float fieldOfView, out Vector2 lensShift))
                    {
                        error = $"FlowHMR camera parameters contain an invalid value at frame {frame}.";
                        return false;
                    }
                    fov.AddKey(new Keyframe(time, fieldOfView));
                    lensShiftX.AddKey(new Keyframe(time, lensShift.x));
                    lensShiftY.AddKey(new Keyframe(time, lensShift.y));
                }

            }

            if (frameTimes.Length > 0 && frameTimes[0] > 0f)
            {
                AddHoldKey(0f, positionX, positionY, positionZ, rotationX, rotationY, rotationZ, rotationW);
                if (motion.HasCameraParameters)
                    AddHoldKey(0f, fov, lensShiftX, lensShiftY);
            }

            float lastSampleTime = frameTimes.Length > 0
                ? frameTimes[frameTimes.Length - 1]
                : (motion.FrameCount - 1) / Mathf.Max(1f, motion.FrameRate);
            if (sourceDuration > lastSampleTime)
            {
                AddHoldKey(sourceDuration, positionX, positionY, positionZ, rotationX, rotationY, rotationZ, rotationW);
                if (motion.HasCameraParameters)
                    AddHoldKey(sourceDuration, fov, lensShiftX, lensShiftY);
            }

            SetLinearTangents(positionX);
            SetLinearTangents(positionY);
            SetLinearTangents(positionZ);
            SetLinearTangents(fov);
            SetLinearTangents(lensShiftX);
            SetLinearTangents(lensShiftY);
            SetCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalPosition.x"), positionX);
            SetCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalPosition.y"), positionY);
            SetCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalPosition.z"), positionZ);
            SetCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalRotation.x"), rotationX);
            SetCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalRotation.y"), rotationY);
            SetCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalRotation.z"), rotationZ);
            SetCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalRotation.w"), rotationW);
            if (motion.HasCameraParameters)
            {
                SetCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Camera), "field of view"), fov);
                SetCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Camera), "m_LensShift.x"), lensShiftX);
                SetCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Camera), "m_LensShift.y"), lensShiftY);
            }
            clip.EnsureQuaternionContinuity();
            return true;
        }

        private static void SetCurve(AnimationClip clip, EditorCurveBinding binding, AnimationCurve curve)
        {
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private static void AddVector3Key(float time, Vector3 value, AnimationCurve x, AnimationCurve y, AnimationCurve z)
        {
            x.AddKey(new Keyframe(time, value.x));
            y.AddKey(new Keyframe(time, value.y));
            z.AddKey(new Keyframe(time, value.z));
        }

        private static void AddQuaternionKey(
            float time, Quaternion value,
            AnimationCurve x, AnimationCurve y, AnimationCurve z, AnimationCurve w)
        {
            x.AddKey(new Keyframe(time, value.x));
            y.AddKey(new Keyframe(time, value.y));
            z.AddKey(new Keyframe(time, value.z));
            w.AddKey(new Keyframe(time, value.w));
        }

        private static void AddHoldKey(float time, params AnimationCurve[] curves)
        {
            foreach (AnimationCurve curve in curves)
            {
                if (curve != null && curve.length > 0)
                    curve.AddKey(new Keyframe(time, curve.Evaluate(time)));
            }
        }

        private static void SetLinearTangents(AnimationCurve curve)
        {
            if (curve == null || curve.length < 2)
                return;

            for (int index = 0; index < curve.length; index++)
            {
                Keyframe key = curve[index];
                float leftSlope = index > 0
                    ? (key.value - curve[index - 1].value) / (key.time - curve[index - 1].time)
                    : 0f;
                float rightSlope = index + 1 < curve.length
                    ? (curve[index + 1].value - key.value) / (curve[index + 1].time - key.time)
                    : 0f;
                key.inTangent = leftSlope;
                key.outTangent = rightSlope;
                curve.MoveKey(index, key);
            }
        }
    }
}
