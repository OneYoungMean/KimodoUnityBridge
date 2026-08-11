using UnityEngine;
using UnityEngine.Playables;

namespace KimodoBridge
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayableDirector))]
    public sealed class KimodoAutoOpenDirectorTimeline : MonoBehaviour
    {
        public PlayableDirector Director => GetComponent<PlayableDirector>();
    }
}
