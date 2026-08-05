using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    [AddComponentMenu("Kimodo/CLI Motion Route Planner")]
    public sealed class KimodoCliMotionRoutePlanner : MonoBehaviour
    {
        [Header("Scene References")]
        [SerializeField] private KimodoRuntimeMotionDriver motionDriver;
        [SerializeField] private Transform characterRoot;

        [Header("Route Timing")]
        [SerializeField][Min(0.01f)] private float maxSpeedMetersPerSecond = 1.25f;
        [SerializeField][Min(0.01f)] private float maxAccelerationMetersPerSecond2 = 1.5f;
        [SerializeField][Min(0.1f)] private float minSegmentDurationSeconds = 1f;
        [SerializeField][Min(0.1f)] private float maxSegmentDurationSeconds = 10f;

        [Header("Route Thresholds")]
        [SerializeField][Min(0f)] private float waypointArrivalThreshold = 0.1f;
        [SerializeField] private bool verboseLogging = true;

        private readonly Queue<Vector3> pendingWorldTargets = new Queue<Vector3>();
        private bool routeActive;
        private int dispatchedSegmentCount;
        private int startedSegmentCount;
        private string activePrompt = "idle";

        public bool RouteActive => routeActive;
        public int PendingWaypointCount => pendingWorldTargets.Count;

        private void Reset()
        {
            if (motionDriver == null)
            {
                motionDriver = GetComponent<KimodoRuntimeMotionDriver>();
            }

            if (characterRoot == null)
            {
                characterRoot = transform;
            }
        }

        private void Awake()
        {
            if (motionDriver == null)
            {
                motionDriver = GetComponent<KimodoRuntimeMotionDriver>();
            }

            if (characterRoot == null)
            {
                characterRoot = transform;
            }
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        public string Animate(string prompt, float worldX, float worldZ)
        {
            return AnimateRoute(prompt, new[] { new Vector2(worldX, worldZ) });
        }

        public string AnimateRoute(string prompt, IList<Vector2> worldWaypoints)
        {
            if (motionDriver == null)
            {
                return "Error: motionDriver is not assigned";
            }

            Transform root = ResolveRoot();
            if (root == null)
            {
                return "Error: characterRoot is not assigned";
            }

            pendingWorldTargets.Clear();
            routeActive = false;
            dispatchedSegmentCount = 0;
            startedSegmentCount = 0;
            activePrompt = string.IsNullOrWhiteSpace(prompt) ? "idle" : prompt.Trim();

            if (worldWaypoints == null || worldWaypoints.Count == 0)
            {
                motionDriver.SetAnimationPrompt(activePrompt);
                return $"Animation configured: prompt = \"{activePrompt}\" (no displacement)";
            }

            for (int i = 0; i < worldWaypoints.Count; i++)
            {
                Vector2 waypoint = worldWaypoints[i];
                pendingWorldTargets.Enqueue(new Vector3(waypoint.x, root.position.y, waypoint.y));
            }

            while (pendingWorldTargets.Count > 0)
            {
                Vector3 first = pendingWorldTargets.Peek();
                if (Vector2.Distance(
                        new Vector2(first.x, first.z),
                        new Vector2(root.position.x, root.position.z)) > waypointArrivalThreshold)
                {
                    break;
                }

                pendingWorldTargets.Dequeue();
            }

            if (pendingWorldTargets.Count == 0)
            {
                motionDriver.SetAnimationPrompt(activePrompt);
                return $"Animation configured: prompt = \"{activePrompt}\" (empty route)";
            }

            routeActive = true;
            DispatchNextSegment();

            return $"Route queued: prompt=\"{activePrompt}\", waypoints={worldWaypoints.Count}, pendingSegments={pendingWorldTargets.Count}";
        }

        private void Subscribe()
        {
            if (motionDriver == null)
            {
                return;
            }

            motionDriver.SegmentStarted -= HandleSegmentStarted;
            motionDriver.SegmentStarted += HandleSegmentStarted;
        }

        private void Unsubscribe()
        {
            if (motionDriver == null)
            {
                return;
            }

            motionDriver.SegmentStarted -= HandleSegmentStarted;
        }

        private void HandleSegmentStarted(KimodoRuntimeSegmentReport report)
        {
            if (!routeActive)
            {
                return;
            }

            if (startedSegmentCount >= dispatchedSegmentCount)
            {
                return;
            }

            startedSegmentCount++;
            DispatchNextSegment();
        }

        private void DispatchNextSegment()
        {
            if (!routeActive || motionDriver == null)
            {
                return;
            }

            Transform root = ResolveRoot();
            if (root == null)
            {
                routeActive = false;
                return;
            }

            while (pendingWorldTargets.Count > 0)
            {
                Vector3 targetWorld = pendingWorldTargets.Dequeue();
                Vector3 deltaWorld = targetWorld - root.position;
                deltaWorld.y = 0f;
                if (deltaWorld.magnitude <= waypointArrivalThreshold)
                {
                    continue;
                }

                float durationSeconds = EstimateSegmentDuration(deltaWorld.magnitude);
                motionDriver.QueuePromptedRoot2D(activePrompt, targetWorld.x, targetWorld.z, durationSeconds);
                dispatchedSegmentCount++;

                if (verboseLogging)
                {
                    Debug.Log(
                        $"[KimodoCliMotionRoutePlanner] Dispatch segment {dispatchedSegmentCount} worldTarget={targetWorld} duration={durationSeconds:0.###}",
                        this);
                }

                return;
            }

            routeActive = false;
        }

        private float EstimateSegmentDuration(float distanceMeters)
        {
            return KimodoRuntimeMotionDriver.EstimateRoot2DTargetDuration(
                distanceMeters,
                maxSpeedMetersPerSecond,
                maxAccelerationMetersPerSecond2,
                minSegmentDurationSeconds,
                maxSegmentDurationSeconds);
        }

        private Transform ResolveRoot()
        {
            if (characterRoot != null)
            {
                return characterRoot;
            }

            if (motionDriver != null)
            {
                return motionDriver.transform;
            }

            return transform;
        }
    }
}
