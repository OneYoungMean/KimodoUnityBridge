using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Splines;

namespace KimodoBridge
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SplineContainer))]
    public sealed class KimodoPlayableSplinePath : MonoBehaviour
    {
        [SerializeField] private KimodoPlayableClip ownerClip;
        [SerializeField] private PlayableDirector ownerDirector;
        [SerializeField, Range(2, 32), Tooltip("Number of evenly timed Root2D waypoints exported for this path.")]
        private int waypointCount = 8;
        [SerializeField, Tooltip("Ask Kimodo to expand Root2D waypoints into a dense path.")]
        private bool densePath = true;
        [SerializeField, Tooltip("Export the planar tangent direction as Root2D heading.")]
        private bool includeHeading = true;
        [SerializeField] private SplineContainer splineContainer;

        public KimodoPlayableClip OwnerClip => ownerClip;
        public PlayableDirector OwnerDirector => ownerDirector;
        public int WaypointCount => Mathf.Clamp(waypointCount, 2, 32);
        public bool DensePath => densePath;
        public bool IncludeHeading => includeHeading;
        public SplineContainer SplineContainer => splineContainer != null
            ? splineContainer
            : GetComponent<SplineContainer>();

        public void Configure(
            KimodoPlayableClip clip,
            PlayableDirector director,
            SplineContainer container)
        {
            ownerClip = clip;
            ownerDirector = director;
            splineContainer = container != null ? container : GetComponent<SplineContainer>();
        }

        public bool Matches(KimodoPlayableClip clip, PlayableDirector director)
        {
            return ownerClip == clip && ownerDirector == director;
        }

        private void Reset()
        {
            splineContainer = GetComponent<SplineContainer>();
        }

        private void OnValidate()
        {
            waypointCount = Mathf.Clamp(waypointCount, 2, 32);
            if (splineContainer == null)
            {
                splineContainer = GetComponent<SplineContainer>();
            }
        }
    }
}
