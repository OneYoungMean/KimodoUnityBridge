using System;
using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    public enum KimodoSplineTangentMode
    {
        AutoSmooth = 0,
        Linear = 1,
        Mirrored = 2,
        Continuous = 3,
        Broken = 4
    }

    [Serializable]
    public sealed class KimodoSplineKnotData
    {
        public Vector3 position;
        public Vector3 tangentIn;
        public Vector3 tangentOut;
        public Quaternion rotation = Quaternion.identity;
        public KimodoSplineTangentMode tangentMode = KimodoSplineTangentMode.AutoSmooth;
        [Range(0f, 1f)] public float time;

        public KimodoSplineKnotData Clone()
        {
            return new KimodoSplineKnotData
            {
                position = position,
                tangentIn = tangentIn,
                tangentOut = tangentOut,
                rotation = rotation,
                tangentMode = tangentMode,
                time = time
            };
        }
    }

    public partial class KimodoPlayableClip
    {
        [Tooltip("Enable the experimental spline path for this Timeline clip.")]
        public bool splinePathEnabled;
        [SerializeField, HideInInspector] private List<KimodoSplineKnotData> splineKnots = new List<KimodoSplineKnotData>();
        [SerializeField, HideInInspector, Range(2, 32)] private int splineWaypointCount = 8;
        [SerializeField, HideInInspector] private bool splineDensePath = true;
        [SerializeField, HideInInspector] private bool splineIncludeHeading = true;

        public IReadOnlyList<KimodoSplineKnotData> SplineKnots
        {
            get
            {
                EnsureSplineKnotTimes();
                return splineKnots;
            }
        }

        public int SplineWaypointCount => Mathf.Clamp(splineWaypointCount, 2, 32);
        public bool SplineDensePath => splineDensePath;
        public bool SplineIncludeHeading => splineIncludeHeading;

        public void SetSplinePathData(IReadOnlyList<KimodoSplineKnotData> knots)
        {
            splineKnots = new List<KimodoSplineKnotData>();
            if (knots != null)
            {
                for (int i = 0; i < knots.Count; i++)
                {
                    if (knots[i] != null)
                    {
                        splineKnots.Add(knots[i].Clone());
                    }
                }
            }
            EnsureSplineKnotTimes();
        }

        public void SetSplineAuthoringOptions(int waypointCount, bool densePath, bool includeHeading)
        {
            splineWaypointCount = Mathf.Clamp(waypointCount, 2, 32);
            splineDensePath = densePath;
            splineIncludeHeading = includeHeading;
        }

        public void SetSplineKnotTime(int knotIndex, float normalizedTime)
        {
            EnsureSplineKnotTimes();
            if (knotIndex > 0 && knotIndex < splineKnots.Count - 1)
            {
                splineKnots[knotIndex].time = Mathf.Clamp(
                    normalizedTime,
                    splineKnots[knotIndex - 1].time,
                    splineKnots[knotIndex + 1].time);
            }
        }

        public bool TryResolveSplineCurveTime(
            float normalizedTime,
            out int curveIndex,
            out float curveTime)
        {
            EnsureSplineKnotTimes();
            curveIndex = -1;
            curveTime = 0f;
            if (splineKnots.Count < 2)
            {
                return false;
            }

            float time = Mathf.Clamp01(normalizedTime);
            curveIndex = splineKnots.Count - 2;
            for (int i = 0; i < splineKnots.Count - 1; i++)
            {
                if (time <= splineKnots[i + 1].time)
                {
                    curveIndex = i;
                    break;
                }
            }

            float startTime = splineKnots[curveIndex].time;
            float endTime = splineKnots[curveIndex + 1].time;
            curveTime = endTime > startTime
                ? Mathf.Clamp01((time - startTime) / (endTime - startTime))
                : 0f;
            return true;
        }

        private void EnsureSplineKnotTimes()
        {
            splineKnots ??= new List<KimodoSplineKnotData>();
            bool valid = true;
            for (int i = 0; valid && i < splineKnots.Count; i++)
            {
                KimodoSplineKnotData knot = splineKnots[i];
                valid = knot != null &&
                    !float.IsNaN(knot.time) &&
                    !float.IsInfinity(knot.time) &&
                    knot.time >= 0f &&
                    knot.time <= 1f &&
                    (i == 0 || knot.time > splineKnots[i - 1].time);
            }
            if (!valid ||
                (splineKnots.Count > 0 && !Mathf.Approximately(splineKnots[0].time, 0f)) ||
                (splineKnots.Count > 1 && !Mathf.Approximately(splineKnots[splineKnots.Count - 1].time, 1f)))
            {
                for (int i = 0; i < splineKnots.Count; i++)
                {
                    splineKnots[i] ??= new KimodoSplineKnotData();
                    splineKnots[i].time = splineKnots.Count <= 1
                        ? 0f
                        : i / (float)(splineKnots.Count - 1);
                }
            }
        }
    }
}
