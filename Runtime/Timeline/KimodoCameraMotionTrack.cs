using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge
{
    [TrackClipType(typeof(KimodoCameraMotionClip))]
    [TrackBindingType(typeof(Transform))]
    [TrackColor(0.18f, 0.62f, 0.78f)]
    public sealed class KimodoCameraMotionTrack : TrackAsset
    {
        public override Playable CreateTrackMixer(PlayableGraph graph, GameObject go, int inputCount) =>
            ScriptPlayable<KimodoCameraMotionMixer>.Create(graph, inputCount);
    }
}
