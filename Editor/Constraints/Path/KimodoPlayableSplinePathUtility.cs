#if KIMODO_SPLINES
using System;
using System.Collections.Generic;
using TimelineInject;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Splines;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal static class KimodoPlayableSplinePathUtility
    {
        private const string PathObjectPrefix = "Kimodo Spline Path ";
        private const string Root2DConstraintType = "root2d";
        private const float DefaultPathSpeedMetersPerSecond = 1f;
        private const int InitialCurveSampleCount = 8;
        private const HideFlags EditingProxyHideFlags =
            HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild;

        internal static bool TrySetEnabled(
            KimodoPlayableClip clip,
            bool enabled,
            out KimodoPlayableSplinePath path,
            out string error)
        {
            path = null;
            error = string.Empty;
            if (clip == null)
            {
                error = "Kimodo Playable clip is null.";
                return false;
            }

            if (enabled && !KimodoPlayableClipGenerationSettings.instance.EnableSplineExperimental)
            {
                error = "Enable Spline (Experimental) in Project Settings before creating a spline path.";
                return false;
            }

            if (!enabled)
            {
                DestroyEditingProxies(clip);
                return true;
            }

            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            if (!TryResolveContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out error) ||
                !TryEnsureAssetPath(clip, context, out error))
            {
                return false;
            }

            path = GetOrCreateEditingProxy(clip, context, out error);
            KimodoPlayableSplinePathSceneEditor.ScheduleRefresh();
            return path != null;
        }

        internal static bool TryGetPath(
            KimodoPlayableClip clip,
            TimelineClip timelineClip,
            out KimodoPlayableSplinePath path,
            out string error)
        {
            path = null;
            error = string.Empty;
            if (!TryResolveContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out error) ||
                !TryEnsureAssetPath(clip, context, out error))
            {
                return false;
            }

            path = GetOrCreateEditingProxy(clip, context, out error);
            return path != null;
        }

        internal static bool TryResetPath(
            KimodoPlayableClip clip,
            out KimodoPlayableSplinePath path,
            out string error)
        {
            path = null;
            error = string.Empty;
            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            if (!TryResolveContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out error) ||
                !TryStoreInitialAssetPath(clip, context, "Reset Kimodo Spline Path", out error))
            {
                return false;
            }

            path = GetOrCreateEditingProxy(clip, context, out error);
            if (path != null)
            {
                RefreshEditingProxy(path, context);
            }
            return path != null;
        }

        internal static bool TryBuildConstraintSamples(
            KimodoPlayableClip clip,
            TimelineClip timelineClip,
            int generationFrames,
            float generationFrameRate,
            out List<KimodoMarkerSampleResult> samples,
            out bool denseRootPath,
            out string error)
        {
            samples = new List<KimodoMarkerSampleResult>();
            denseRootPath = false;
            error = string.Empty;
            if (clip == null ||
                !KimodoPlayableClipGenerationSettings.instance.EnableSplineExperimental ||
                !clip.splinePathEnabled)
            {
                return true;
            }

            Spline spline = BuildSpline(clip.SplineKnots);
            if (spline == null || spline.Count < 2)
            {
                error = "Spline Path requires at least two knots.";
                return false;
            }

            if (!TryResolveContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out error) ||
                !TryResolvePathFrame(context, out Vector3 pathOrigin, out Quaternion pathRotation, out error))
            {
                return false;
            }

            int lastFrame = Mathf.Max(0, generationFrames - 1);
            int sampleCount = lastFrame == 0 ? 1 : clip.SplineWaypointCount;
            double durationSeconds = lastFrame / Mathf.Max(1f, generationFrameRate);
            Vector3 fallbackForward = pathRotation * Vector3.forward;
            for (int i = 0; i < sampleCount; i++)
            {
                float t = sampleCount <= 1 ? 0f : i / (float)(sampleCount - 1);
                if (!EvaluateSplineAtTime(clip, spline, t, out Vector3 localPosition, out Vector3 localTangent))
                {
                    error = "Spline Path evaluation failed.";
                    return false;
                }

                Vector3 worldPosition = pathOrigin + (pathRotation * localPosition);
                Vector3 worldForward = pathRotation * new Vector3(localTangent.x, 0f, localTangent.z);
                if (worldForward.sqrMagnitude <= 1e-8f)
                {
                    worldForward = fallbackForward;
                }
                else
                {
                    worldForward.Normalize();
                }

                double sampleTime = timelineClip.start + (durationSeconds * t);
                if (!KimodoTimelineConstraintMarkerSampler.TrySampleMarker(
                        context,
                        sampleTime,
                        sampleTime,
                        "fullbody",
                        context.ModelName,
                        out KimodoMarkerSampleResult sample,
                        out error) || sample.rootOverride == null)
                {
                    error = string.IsNullOrEmpty(error)
                        ? "Spline Path could not sample the complete root transform."
                        : error;
                    return false;
                }

                sample.constraintMode = Root2DConstraintType;
                sample.sampleTime = sampleTime;
                sample.rootOverride.t = KimodoMotionMath.ApplyPlanarPosition(
                    sample.rootOverride.t,
                    worldPosition);
                if (clip.SplineIncludeHeading)
                {
                    sample.rootOverride.q = KimodoMotionMath.ApplyPlanarHeading(
                        sample.rootOverride.q,
                        Quaternion.LookRotation(worldForward, Vector3.up));
                }
                sample.enableMask = new KimodoConstraintMask
                {
                    rootPosition = true,
                    rootHeading = clip.SplineIncludeHeading
                };
                sample.validMask = new KimodoConstraintMask
                {
                    rootPosition = true,
                    rootHeading = clip.SplineIncludeHeading
                };
                sample.enableMask.muscle = false;
                sample.enableMask.rootTQ = false;
                sample.enableMask.leftFootTQ = false;
                sample.enableMask.rightFootTQ = false;
                samples.Add(sample);
            }

            denseRootPath = clip.SplineDensePath;
            return true;
        }

        internal static bool EvaluateSplineAtTime(
            KimodoPlayableClip clip,
            Spline spline,
            float normalizedTime,
            out Vector3 position,
            out Vector3 tangent)
        {
            position = Vector3.zero;
            tangent = Vector3.forward;
            if (clip == null || spline == null || spline.Count < 2 ||
                !clip.TryResolveSplineCurveTime(normalizedTime, out int curveIndex, out float curveTime))
            {
                return false;
            }
            BezierCurve curve = spline.GetCurve(curveIndex);
            float3 evaluatedPosition = CurveUtility.EvaluatePosition(curve, curveTime);
            float3 evaluatedTangent = CurveUtility.EvaluateTangent(curve, curveTime);
            if (!math.all(math.isfinite(evaluatedPosition)) || !math.all(math.isfinite(evaluatedTangent)))
            {
                return false;
            }

            position = ToVector3(evaluatedPosition);
            tangent = ToVector3(evaluatedTangent);
            return true;
        }

        internal static float ResolveDefaultPathLength(double durationSeconds)
        {
            return Mathf.Max(0f, (float)durationSeconds) * DefaultPathSpeedMetersPerSecond;
        }

        internal static bool IsEditingProxy(KimodoPlayableSplinePath path)
        {
            return path != null && (path.gameObject.hideFlags & HideFlags.DontSaveInEditor) != 0;
        }

        internal static void DestroyEditingProxy(KimodoPlayableSplinePath path)
        {
            if (IsEditingProxy(path))
            {
                UnityEngine.Object.DestroyImmediate(path.gameObject);
            }
        }

        internal static void DestroyEditingProxies(KimodoPlayableClip clip = null)
        {
            KimodoPlayableSplinePath[] paths = Resources.FindObjectsOfTypeAll<KimodoPlayableSplinePath>();
            for (int i = 0; i < paths.Length; i++)
            {
                KimodoPlayableSplinePath path = paths[i];
                if (IsEditingProxy(path) && (clip == null || path.OwnerClip == clip))
                {
                    UnityEngine.Object.DestroyImmediate(path.gameObject);
                }
            }
        }

        internal static void RefreshEditingProxy(KimodoPlayableSplinePath path)
        {
            if (path == null || path.OwnerClip == null)
            {
                return;
            }

            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(path.OwnerClip);
            if (TryResolveContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out _))
            {
                RefreshEditingProxy(path, context);
            }
            else if (path.SplineContainer != null)
            {
                path.SplineContainer.Spline = BuildSpline(path.OwnerClip.SplineKnots);
            }
        }

        internal static void SyncEditingProxyToAsset(
            KimodoPlayableSplinePath path,
            int knotIndex,
            SplineModification modification)
        {
            if (path == null || path.OwnerClip == null || path.SplineContainer == null)
            {
                return;
            }

            KimodoPlayableClip clip = path.OwnerClip;
            Spline spline = path.SplineContainer.Spline;
            if (spline == null)
            {
                return;
            }

            List<float> times = ResolveEditedKnotTimes(
                clip.SplineKnots,
                spline.Count,
                knotIndex,
                modification);
            clip.SetSplinePathData(BuildKnotData(spline, times));
            EditorUtility.SetDirty(clip);
        }

        internal static bool TryMigrateLegacyPath(KimodoPlayableSplinePath legacyPath, out string error)
        {
            error = string.Empty;
            if (legacyPath == null || IsEditingProxy(legacyPath) || legacyPath.OwnerClip == null)
            {
                return false;
            }

            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(legacyPath.OwnerClip);
            if (!TryResolveContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out error))
            {
                return false;
            }
            if (legacyPath.OwnerDirector != null && context.Director != legacyPath.OwnerDirector)
            {
                error = "The legacy Spline Path belongs to a different PlayableDirector.";
                return false;
            }
            return TryMigrateLegacyPath(legacyPath, context, out error);
        }

        private static bool TryEnsureAssetPath(
            KimodoPlayableClip clip,
            KimodoTimelineInOutConstraintContext context,
            out string error)
        {
            error = string.Empty;
            if (clip.SplineKnots.Count >= 2)
            {
                return true;
            }

            KimodoPlayableSplinePath legacyPath = FindPath(clip, context.Director, editingProxy: false);
            if (legacyPath != null && TryMigrateLegacyPath(legacyPath, context, out error))
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            return TryStoreInitialAssetPath(clip, context, "Create Kimodo Spline Path", out error);
        }

        private static bool TryStoreInitialAssetPath(
            KimodoPlayableClip clip,
            KimodoTimelineInOutConstraintContext context,
            string undoName,
            out string error)
        {
            if (!TryBuildInitialWorldPoints(
                    clip,
                    context,
                    out List<Vector3> worldPoints,
                    out Vector3 pathOrigin,
                    out Quaternion pathRotation,
                    out error))
            {
                return false;
            }

            var spline = new Spline();
            PopulateSpline(spline, pathOrigin, pathRotation, worldPoints, clip.clip != null);
            Undo.RecordObject(clip, undoName);
            clip.SetSplinePathData(BuildKnotData(spline, BuildUniformKnotTimes(spline.Count)));
            EditorUtility.SetDirty(clip);
            return true;
        }

        private static KimodoPlayableSplinePath GetOrCreateEditingProxy(
            KimodoPlayableClip clip,
            KimodoTimelineInOutConstraintContext context,
            out string error)
        {
            error = string.Empty;
            KimodoPlayableSplinePath path = FindPath(clip, context.Director, editingProxy: true);
            if (path != null)
            {
                return path;
            }

            if (!TryResolvePathFrame(context, out Vector3 pathOrigin, out Quaternion pathRotation, out error))
            {
                return null;
            }

            var pathObject = new GameObject(PathObjectPrefix + clip.name)
            {
                hideFlags = EditingProxyHideFlags
            };
            pathObject.transform.SetPositionAndRotation(pathOrigin, pathRotation);
            SplineContainer container = pathObject.AddComponent<SplineContainer>();
            KimodoPlayableSplinePath created = pathObject.AddComponent<KimodoPlayableSplinePath>();
            container.Spline = BuildSpline(clip.SplineKnots);
            created.Configure(clip, context.Director, container);
            SceneVisibilityManager.instance.Show(pathObject, true);
            return created;
        }

        private static void RefreshEditingProxy(
            KimodoPlayableSplinePath path,
            KimodoTimelineInOutConstraintContext context)
        {
            if (path == null || path.OwnerClip == null || path.SplineContainer == null)
            {
                return;
            }

            path.SplineContainer.Spline = BuildSpline(path.OwnerClip.SplineKnots);
            if (TryResolvePathFrame(context, out Vector3 pathOrigin, out Quaternion pathRotation, out _))
            {
                path.transform.SetPositionAndRotation(pathOrigin, pathRotation);
            }
        }

        private static bool TryMigrateLegacyPath(
            KimodoPlayableSplinePath legacyPath,
            KimodoTimelineInOutConstraintContext context,
            out string error)
        {
            error = string.Empty;
            KimodoPlayableClip clip = legacyPath.OwnerClip;
            if (clip.SplineKnots.Count >= 2)
            {
                Undo.DestroyObjectImmediate(legacyPath.gameObject);
                return true;
            }

            Spline source = legacyPath.SplineContainer != null ? legacyPath.SplineContainer.Spline : null;
            if (source == null || source.Count < 2)
            {
                error = "The legacy Spline Path has fewer than two knots.";
                return false;
            }
            if (!TryResolvePathFrame(context, out Vector3 pathOrigin, out Quaternion pathRotation, out error))
            {
                return false;
            }

            Spline converted = ConvertSplineToPathFrame(source, legacyPath.transform, pathOrigin, pathRotation);
            Undo.RecordObject(clip, "Migrate Kimodo Spline Path");
            clip.SetSplinePathData(BuildKnotData(converted, BuildUniformKnotTimes(converted.Count)));
            clip.SetSplineAuthoringOptions(
                legacyPath.LegacyWaypointCount,
                legacyPath.LegacyDensePath,
                legacyPath.LegacyIncludeHeading);
            EditorUtility.SetDirty(clip);
            Undo.DestroyObjectImmediate(legacyPath.gameObject);
            return true;
        }

        private static Spline ConvertSplineToPathFrame(
            Spline source,
            Transform sourceTransform,
            Vector3 pathOrigin,
            Quaternion pathRotation)
        {
            var converted = new Spline(source);
            Quaternion inversePathRotation = Quaternion.Inverse(pathRotation);
            for (int i = 0; i < converted.Count; i++)
            {
                BezierKnot knot = converted[i];
                Vector3 worldPosition = sourceTransform.TransformPoint(ToVector3(knot.Position));
                Quaternion worldRotation = sourceTransform.rotation * ToQuaternion(knot.Rotation);
                knot.Position = ToFloat3(inversePathRotation * (worldPosition - pathOrigin));
                knot.Rotation = ToQuaternion(inversePathRotation * worldRotation);
                converted[i] = knot;
            }
            return converted;
        }

        private static bool TryBuildInitialWorldPoints(
            KimodoPlayableClip clip,
            KimodoTimelineInOutConstraintContext context,
            out List<Vector3> points,
            out Vector3 pathOrigin,
            out Quaternion pathRotation,
            out string error)
        {
            points = new List<Vector3>();
            if (!TryResolvePathFrame(context, out pathOrigin, out pathRotation, out error))
            {
                return false;
            }

            if (clip.clip == null)
            {
                Vector3 forward = pathRotation * Vector3.forward;
                points.Add(pathOrigin);
                points.Add(pathOrigin + (forward * ResolveDefaultPathLength(context.SourceClip?.duration ?? 0.0)));
                return true;
            }

            TimelineClip timelineClip = context.SourceClip;
            PlayableDirector director = context.Director;
            Animator animator = context.Animator;
            if (timelineClip == null || director == null || animator == null)
            {
                error = "Spline Path root-motion fitting requires a Timeline clip, PlayableDirector, and Animator.";
                return false;
            }

            double originalTime = director.time;
            try
            {
                double startTime = timelineClip.start;
                double endTime = Math.Max(startTime, timelineClip.end - 1e-6);
                for (int i = 0; i < InitialCurveSampleCount; i++)
                {
                    double t = i / (double)(InitialCurveSampleCount - 1);
                    director.time = startTime + ((endTime - startTime) * t);
                    director.Evaluate();
                    Vector3 root = animator.rootPosition;
                    root.y = 0f;
                    points.Add(root);
                }
                return true;
            }
            catch (Exception ex)
            {
                points.Clear();
                error = $"Fit spline from root motion failed: {ex.Message}";
                return false;
            }
            finally
            {
                RestoreDirectorTime(director, originalTime);
            }
        }

        private static bool TryResolvePathFrame(
            KimodoTimelineInOutConstraintContext context,
            out Vector3 position,
            out Quaternion rotation,
            out string error)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            error = string.Empty;
            if (context?.SourceClip == null)
            {
                error = "Spline Path requires a Timeline clip.";
                return false;
            }

            if (context.Director != null && context.Animator != null)
            {
                double originalTime = context.Director.time;
                try
                {
                    context.Director.time = context.SourceClip.start;
                    context.Director.Evaluate();
                    position = context.Animator.rootPosition;
                    rotation = context.Animator.rootRotation;
                    return true;
                }
                catch (Exception ex)
                {
                    error = $"Resolve spline path start pose failed: {ex.Message}";
                    return false;
                }
                finally
                {
                    RestoreDirectorTime(context.Director, originalTime);
                }
            }

            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                context.Track,
                context.Animator,
                out position,
                out rotation);
            return true;
        }

        private static void RestoreDirectorTime(PlayableDirector director, double time)
        {
            try
            {
                director.time = time;
                director.Evaluate();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kimodo][SplinePath] Failed to restore Timeline time: {ex.Message}");
            }
        }

        private static void PopulateSpline(
            Spline spline,
            Vector3 pathOrigin,
            Quaternion pathRotation,
            IReadOnlyList<Vector3> worldPoints,
            bool smooth)
        {
            spline.Clear();
            Quaternion inversePathRotation = Quaternion.Inverse(pathRotation);
            TangentMode tangentMode = smooth ? TangentMode.AutoSmooth : TangentMode.Linear;
            for (int i = 0; i < worldPoints.Count; i++)
            {
                Vector3 localPosition = inversePathRotation * (worldPoints[i] - pathOrigin);
                spline.Add(new BezierKnot(ToFloat3(localPosition)), tangentMode);
            }
        }

        private static Spline BuildSpline(IReadOnlyList<KimodoSplineKnotData> knots)
        {
            var spline = new Spline();
            if (knots == null)
            {
                return spline;
            }

            for (int i = 0; i < knots.Count; i++)
            {
                KimodoSplineKnotData data = knots[i];
                if (data == null)
                {
                    continue;
                }

                var knot = new BezierKnot(
                    ToFloat3(data.position),
                    ToFloat3(data.tangentIn),
                    ToFloat3(data.tangentOut),
                    ToQuaternion(NormalizeRotation(data.rotation)));
                spline.Add(knot, (TangentMode)Mathf.Clamp((int)data.tangentMode, 0, 4));
            }
            return spline;
        }

        private static List<KimodoSplineKnotData> BuildKnotData(
            Spline spline,
            IReadOnlyList<float> times)
        {
            var result = new List<KimodoSplineKnotData>(spline?.Count ?? 0);
            if (spline == null)
            {
                return result;
            }

            for (int i = 0; i < spline.Count; i++)
            {
                BezierKnot knot = spline[i];
                result.Add(new KimodoSplineKnotData
                {
                    position = ToVector3(knot.Position),
                    tangentIn = ToVector3(knot.TangentIn),
                    tangentOut = ToVector3(knot.TangentOut),
                    rotation = NormalizeRotation(ToQuaternion(knot.Rotation)),
                    tangentMode = (KimodoSplineTangentMode)spline.GetTangentMode(i),
                    time = times != null && i < times.Count
                        ? Mathf.Clamp01(times[i])
                        : (spline.Count <= 1 ? 0f : i / (float)(spline.Count - 1))
                });
            }
            return result;
        }

        private static List<float> ResolveEditedKnotTimes(
            IReadOnlyList<KimodoSplineKnotData> previousKnots,
            int knotCount,
            int knotIndex,
            SplineModification modification)
        {
            int previousCount = previousKnots?.Count ?? 0;
            if (knotCount == previousCount)
            {
                var preserved = new List<float>(knotCount);
                for (int i = 0; i < knotCount; i++)
                {
                    preserved.Add(previousKnots[i].time);
                }
                return preserved;
            }

            if (modification == SplineModification.KnotInserted &&
                knotCount == previousCount + 1 &&
                knotIndex > 0 &&
                knotIndex < knotCount - 1)
            {
                var inserted = new List<float>(knotCount);
                for (int i = 0; i < knotCount; i++)
                {
                    if (i < knotIndex)
                    {
                        inserted.Add(previousKnots[i].time);
                    }
                    else if (i == knotIndex)
                    {
                        inserted.Add((previousKnots[i - 1].time + previousKnots[i].time) * 0.5f);
                    }
                    else
                    {
                        inserted.Add(previousKnots[i - 1].time);
                    }
                }
                return inserted;
            }

            if (modification == SplineModification.KnotRemoved &&
                knotCount == previousCount - 1 &&
                knotIndex > 0 &&
                knotIndex < previousCount - 1)
            {
                var removed = new List<float>(knotCount);
                for (int i = 0; i < previousCount; i++)
                {
                    if (i != knotIndex)
                    {
                        removed.Add(previousKnots[i].time);
                    }
                }
                return removed;
            }

            return BuildUniformKnotTimes(knotCount);
        }

        private static List<float> BuildUniformKnotTimes(int knotCount)
        {
            var times = new List<float>(Mathf.Max(0, knotCount));
            for (int i = 0; i < knotCount; i++)
            {
                times.Add(knotCount <= 1 ? 0f : i / (float)(knotCount - 1));
            }
            return times;
        }

        private static Quaternion NormalizeRotation(Quaternion rotation)
        {
            return rotation.x * rotation.x +
                rotation.y * rotation.y +
                rotation.z * rotation.z +
                rotation.w * rotation.w > 1e-8f
                ? rotation.normalized
                : Quaternion.identity;
        }

        private static KimodoPlayableSplinePath FindPath(
            KimodoPlayableClip clip,
            PlayableDirector director,
            bool editingProxy)
        {
            KimodoPlayableSplinePath[] paths = Resources.FindObjectsOfTypeAll<KimodoPlayableSplinePath>();
            for (int i = 0; i < paths.Length; i++)
            {
                KimodoPlayableSplinePath candidate = paths[i];
                if (candidate != null &&
                    candidate.gameObject.scene.IsValid() &&
                    !EditorUtility.IsPersistent(candidate) &&
                    IsEditingProxy(candidate) == editingProxy &&
                    candidate.Matches(clip, director))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static bool TryResolveContext(
            TimelineClip timelineClip,
            out KimodoTimelineInOutConstraintContext context,
            out string error)
        {
            if (timelineClip == null)
            {
                context = null;
                error = "Spline Path requires this Kimodo Playable clip to be on an opened Timeline.";
                return false;
            }

            return KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                timelineClip,
                out context,
                out error);
        }

        private static Vector3 ToVector3(float3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        private static float3 ToFloat3(Vector3 value)
        {
            return new float3(value.x, value.y, value.z);
        }

        private static Quaternion ToQuaternion(quaternion value)
        {
            return new Quaternion(value.value.x, value.value.y, value.value.z, value.value.w);
        }

        private static quaternion ToQuaternion(Quaternion value)
        {
            return new quaternion(value.x, value.y, value.z, value.w);
        }
    }
}
#endif
