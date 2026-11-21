using System;
using UnityEngine;
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

        internal void Apply(Playable playable, Transform target)
        {
            if (target == null || positions == null || rotations == null || positions.Length == 0 || rotations.Length != positions.Length)
                return;
            float frame = Mathf.Clamp((float)playable.GetTime() * Mathf.Max(1f, frameRate), 0f, positions.Length - 1f);
            int first = Mathf.FloorToInt(frame);
            int second = Mathf.Min(first + 1, positions.Length - 1);
            float blend = frame - first;
            target.SetPositionAndRotation(
                anchorPosition + anchorRotation * Vector3.LerpUnclamped(positions[first], positions[second], blend),
                anchorRotation * Quaternion.SlerpUnclamped(rotations[first], rotations[second], blend));
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
