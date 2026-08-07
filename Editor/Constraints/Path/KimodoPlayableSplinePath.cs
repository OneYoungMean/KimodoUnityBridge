#if KIMODO_SPLINES
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Splines;

namespace KimodoBridge.Editor
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SplineContainer))]
    public sealed class KimodoPlayableSplinePath : MonoBehaviour
    {
        [SerializeField, HideInInspector] private KimodoPlayableClip ownerClip;
        [SerializeField, HideInInspector] private PlayableDirector ownerDirector;
        [SerializeField, HideInInspector, Range(2, 32)] private int waypointCount = 8;
        [SerializeField, HideInInspector] private bool densePath = true;
        [SerializeField, HideInInspector] private bool includeHeading = true;
        [SerializeField, HideInInspector] private SplineContainer splineContainer;

        internal KimodoPlayableClip OwnerClip => ownerClip;
        internal PlayableDirector OwnerDirector => ownerDirector;
        internal int WaypointCount => ownerClip != null
            ? ownerClip.SplineWaypointCount
            : Mathf.Clamp(waypointCount, 2, 32);
        internal int LegacyWaypointCount => Mathf.Clamp(waypointCount, 2, 32);
        internal bool LegacyDensePath => densePath;
        internal bool LegacyIncludeHeading => includeHeading;
        internal SplineContainer SplineContainer => splineContainer != null
            ? splineContainer
            : GetComponent<SplineContainer>();

        internal void Configure(
            KimodoPlayableClip clip,
            PlayableDirector director,
            SplineContainer container)
        {
            ownerClip = clip;
            ownerDirector = director;
            splineContainer = container != null ? container : GetComponent<SplineContainer>();
        }

        internal bool Matches(KimodoPlayableClip clip, PlayableDirector director)
        {
            return ownerClip == clip && ownerDirector == director;
        }

        private void Reset()
        {
            splineContainer = GetComponent<SplineContainer>();
        }
    }
}
#endif
