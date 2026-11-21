using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge
{
    [Serializable]
    public sealed class KimodoCameraMotionClip : PlayableAsset, ITimelineClipAsset
    {
        [SerializeField] internal Vector3[] positions = Array.Empty<Vector3>();
        [SerializeField] internal Quaternion[] rotations = Array.Empty<Quaternion>();
        [SerializeField] internal float frameRate = 30f;
        [SerializeField] internal Vector3 anchorPosition;
        [SerializeField] internal Quaternion anchorRotation = Quaternion.identity;
        // Scene cameras in the demo may carry an AimConstraint.  A sampled
        // camera track owns the rotation while it is playing, so the
        // constraint is suspended temporarily by the behaviour below.
        [SerializeField] internal bool disableAimConstraints = true;
        [SerializeField] internal AnimationCurve fieldOfViewCurve = new AnimationCurve();
        [SerializeField] internal AnimationCurve lensShiftXCurve = new AnimationCurve();
        [SerializeField] internal AnimationCurve lensShiftYCurve = new AnimationCurve();

        public ClipCaps clipCaps => ClipCaps.Blending | ClipCaps.ClipIn | ClipCaps.SpeedMultiplier;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            ScriptPlayable<KimodoCameraMotionBehaviour> playable = ScriptPlayable<KimodoCameraMotionBehaviour>.Create(graph);
            KimodoCameraMotionBehaviour behaviour = playable.GetBehaviour();
            behaviour.positions = positions;
            behaviour.rotations = rotations;
            behaviour.frameRate = Mathf.Max(1f, frameRate);
            behaviour.anchorPosition = anchorPosition;
            behaviour.anchorRotation = anchorRotation;
            behaviour.disableAimConstraints = disableAimConstraints;
            behaviour.fieldOfViewCurve = fieldOfViewCurve;
            behaviour.lensShiftXCurve = lensShiftXCurve;
            behaviour.lensShiftYCurve = lensShiftYCurve;
            return playable;
        }
    }

    public sealed class KimodoCameraMotionBehaviour : PlayableBehaviour
    {
        internal Vector3[] positions;
        internal Quaternion[] rotations;
        internal float frameRate;
        internal Vector3 anchorPosition;
        internal Quaternion anchorRotation;
        internal bool disableAimConstraints;
        internal AnimationCurve fieldOfViewCurve;
        internal AnimationCurve lensShiftXCurve;
        internal AnimationCurve lensShiftYCurve;

        private Transform constrainedTarget;
        private AimConstraint[] suspendedAimConstraints;
        private bool[] suspendedAimConstraintStates;
        private Camera targetCamera;
        private bool cameraParametersCaptured;
        private float originalFieldOfView;
        private Vector2 originalLensShift;

        private void SuspendAimConstraints(Transform target)
        {
            if (!disableAimConstraints || target == null || constrainedTarget == target)
                return;

            constrainedTarget = target;
            suspendedAimConstraints = target.GetComponents<AimConstraint>();
            suspendedAimConstraintStates = new bool[suspendedAimConstraints.Length];
            for (int index = 0; index < suspendedAimConstraints.Length; index++)
            {
                AimConstraint constraint = suspendedAimConstraints[index];
                suspendedAimConstraintStates[index] = constraint != null && constraint.enabled;
                if (constraint != null && constraint.enabled)
                    constraint.enabled = false;
            }
        }

        private void RestoreAimConstraints()
        {
            if (suspendedAimConstraints == null || suspendedAimConstraintStates == null)
                return;
            for (int index = 0; index < suspendedAimConstraints.Length; index++)
            {
                AimConstraint constraint = suspendedAimConstraints[index];
                if (constraint != null && index < suspendedAimConstraintStates.Length)
                    constraint.enabled = suspendedAimConstraintStates[index];
            }
            suspendedAimConstraints = null;
            suspendedAimConstraintStates = null;
            constrainedTarget = null;
        }

        internal void Apply(Playable playable, Transform target)
        {
            if (target == null || positions == null || rotations == null || positions.Length == 0 || rotations.Length != positions.Length)
                return;
            SuspendAimConstraints(target);
            ApplyCameraParameters(playable, target);
            float frame = Mathf.Clamp((float)playable.GetTime() * Mathf.Max(1f, frameRate), 0f, positions.Length - 1f);
            int first = Mathf.FloorToInt(frame);
            int second = Mathf.Min(first + 1, positions.Length - 1);
            float blend = frame - first;
            target.SetPositionAndRotation(
                anchorPosition + anchorRotation * Vector3.LerpUnclamped(positions[first], positions[second], blend),
                anchorRotation * Quaternion.SlerpUnclamped(rotations[first], rotations[second], blend));
        }

        private void ApplyCameraParameters(Playable playable, Transform target)
        {
            if (fieldOfViewCurve == null || lensShiftXCurve == null || lensShiftYCurve == null || fieldOfViewCurve.length == 0)
                return;
            Camera camera = target.GetComponent<Camera>();
            if (camera == null)
                return;
            if (!cameraParametersCaptured)
            {
                targetCamera = camera;
                originalFieldOfView = camera.fieldOfView;
                originalLensShift = camera.lensShift;
                cameraParametersCaptured = true;
            }
            float time = Mathf.Max(0f, (float)playable.GetTime());
            camera.fieldOfView = fieldOfViewCurve.Evaluate(time);
            camera.lensShift = new Vector2(lensShiftXCurve.Evaluate(time), lensShiftYCurve.Evaluate(time));
        }

        public override void OnBehaviourPause(Playable playable, FrameData info)
        {
            RestoreAimConstraints();
            if (cameraParametersCaptured && targetCamera != null)
            {
                targetCamera.fieldOfView = originalFieldOfView;
                targetCamera.lensShift = originalLensShift;
            }
            targetCamera = null;
            cameraParametersCaptured = false;
        }
    }

    public sealed class KimodoCameraMotionMixer : PlayableBehaviour
    {
        public override void ProcessFrame(Playable playable, FrameData info, object playerData)
        {
            Transform target = playerData as Transform;
            if (target == null) return;
            for (int index = 0; index < playable.GetInputCount(); index++)
            {
                if (playable.GetInputWeight(index) <= 0f) continue;
                ScriptPlayable<KimodoCameraMotionBehaviour> input = (ScriptPlayable<KimodoCameraMotionBehaviour>)playable.GetInput(index);
                input.GetBehaviour().Apply(input, target);
            }
        }
    }
}
