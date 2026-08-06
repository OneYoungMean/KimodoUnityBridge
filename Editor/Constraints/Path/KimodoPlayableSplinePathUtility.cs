using System;
using System.Collections.Generic;
using TimelineInject;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
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
        private const float DefaultPathLength = 2.5f;

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

            if (!enabled)
            {
                SetAllPathsActive(clip, false);
                return true;
            }

            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            if (!TryResolveContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out error))
            {
                return false;
            }

            path = FindPath(clip, context.Director);
            if (path == null)
            {
                path = CreatePath(clip, context);
            }
            if (path == null)
            {
                error = "Failed to create the scene spline path.";
                return false;
            }

            path.Configure(clip, context.Director, path.SplineContainer);
            path.gameObject.SetActive(true);
            EditorUtility.SetDirty(path);
            EditorSceneManager.MarkSceneDirty(path.gameObject.scene);
            return true;
        }

        internal static bool TryGetPath(
            KimodoPlayableClip clip,
            TimelineClip timelineClip,
            out KimodoPlayableSplinePath path,
            out string error)
        {
            path = null;
            if (!TryResolveContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out error))
            {
                return false;
            }

            path = FindPath(clip, context.Director);
            if (path != null)
            {
                return true;
            }

            error = "The enabled spline path has not been created for this scene PlayableDirector.";
            return false;
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
            if (clip == null || !clip.splinePathEnabled)
            {
                return true;
            }

            if (!TryGetPath(clip, timelineClip, out KimodoPlayableSplinePath path, out error))
            {
                return false;
            }

            SplineContainer container = path.SplineContainer;
            Spline spline = container != null ? container.Spline : null;
            if (spline == null || spline.Count < 2)
            {
                error = "Spline Path requires at least two knots.";
                return false;
            }

            if (!TryResolveContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out error))
            {
                return false;
            }

            float rootPositionScale = ResolveKimodoRootPositionScale(context, clip.bridgeModelName);
            int lastFrame = Mathf.Max(0, generationFrames - 1);
            int sampleCount = lastFrame == 0 ? 1 : path.WaypointCount;
            double durationSeconds = lastFrame / Mathf.Max(1f, generationFrameRate);
            ResolveFallbackForward(timelineClip, out Vector3 fallbackForward);
            for (int i = 0; i < sampleCount; i++)
            {
                float t = sampleCount <= 1 ? 0f : i / (float)(sampleCount - 1);
                if (!container.Evaluate(t, out float3 position, out float3 tangent, out _))
                {
                    error = "Spline Path evaluation failed.";
                    return false;
                }

                Vector3 worldPosition = new Vector3(position.x, position.y, position.z);
                Vector3 worldForward = new Vector3(tangent.x, 0f, tangent.z);
                if (worldForward.sqrMagnitude <= 1e-8f)
                {
                    worldForward = fallbackForward;
                }
                else
                {
                    worldForward.Normalize();
                }

                samples.Add(new KimodoMarkerSampleResult
                {
                    constraintType = Root2DConstraintType,
                    sampleTime = timelineClip.start + (durationSeconds * t),
                    kimodoRootPosition = new Vector3(worldPosition.x, 0f, worldPosition.z) * rootPositionScale,
                    unityRootPos = worldPosition,
                    unityRootRot = Quaternion.LookRotation(worldForward, Vector3.up),
                    hasRootHeading = path.IncludeHeading,
                    rootHeading = new Vector2(worldForward.x, worldForward.z)
                });
            }

            denseRootPath = path.DensePath;
            return true;
        }

        private static float ResolveKimodoRootPositionScale(
            KimodoTimelineInOutConstraintContext context,
            string modelName)
        {
            float sourceHumanScale = KimodoConstraintNormalizationUtility.ResolveHumanScale(context?.SourceAvatar);
            float kimodoHumanScale = 1f;
            if (KimodoRetargetMarkerSamplingUtility.TryResolveTargetAvatar(
                    null,
                    context?.Animator,
                    modelName,
                    out Avatar targetAvatar,
                    out _) &&
                KimodoRetargetCoreUtility.IsValidHumanoid(targetAvatar))
            {
                kimodoHumanScale = KimodoConstraintNormalizationUtility.ResolveHumanScale(targetAvatar);
            }

            return Mathf.Max(1e-6f, kimodoHumanScale) /
                Mathf.Max(1e-6f, sourceHumanScale);
        }

        private static KimodoPlayableSplinePath CreatePath(
            KimodoPlayableClip clip,
            KimodoTimelineInOutConstraintContext context)
        {
            var pathObject = new GameObject(PathObjectPrefix + clip.name);
            Undo.RegisterCreatedObjectUndo(pathObject, "Create Kimodo Spline Path");
            SplineContainer container = Undo.AddComponent<SplineContainer>(pathObject);
            KimodoPlayableSplinePath path = Undo.AddComponent<KimodoPlayableSplinePath>(pathObject);
            path.Configure(clip, context.Director, container);

            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                context.Track,
                context.Animator,
                out Vector3 start,
                out Quaternion rotation);
            Vector3 forward = rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 1e-8f)
            {
                forward = Vector3.forward;
            }
            else
            {
                forward.Normalize();
            }

            Spline spline = container.Spline;
            spline.Clear();
            spline.Add(new BezierKnot(pathObject.transform.InverseTransformPoint(start)));
            spline.Add(new BezierKnot(pathObject.transform.InverseTransformPoint(start + (forward * DefaultPathLength))));
            spline.SetTangentMode(TangentMode.Linear);

            EditorUtility.SetDirty(container);
            EditorUtility.SetDirty(path);
            EditorSceneManager.MarkSceneDirty(pathObject.scene);
            return path;
        }

        private static KimodoPlayableSplinePath FindPath(
            KimodoPlayableClip clip,
            PlayableDirector director)
        {
            KimodoPlayableSplinePath[] paths = Resources.FindObjectsOfTypeAll<KimodoPlayableSplinePath>();
            for (int i = 0; i < paths.Length; i++)
            {
                KimodoPlayableSplinePath candidate = paths[i];
                if (candidate != null &&
                    candidate.gameObject.scene.IsValid() &&
                    !EditorUtility.IsPersistent(candidate) &&
                    candidate.Matches(clip, director))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static void SetAllPathsActive(KimodoPlayableClip clip, bool active)
        {
            KimodoPlayableSplinePath[] paths = Resources.FindObjectsOfTypeAll<KimodoPlayableSplinePath>();
            for (int i = 0; i < paths.Length; i++)
            {
                KimodoPlayableSplinePath path = paths[i];
                if (path == null ||
                    !path.gameObject.scene.IsValid() ||
                    EditorUtility.IsPersistent(path) ||
                    path.OwnerClip != clip)
                {
                    continue;
                }

                if (path.gameObject.activeSelf != active)
                {
                    path.gameObject.SetActive(active);
                    EditorSceneManager.MarkSceneDirty(path.gameObject.scene);
                }
            }
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

        private static void ResolveFallbackForward(TimelineClip timelineClip, out Vector3 forward)
        {
            forward = Vector3.forward;
            if (!TryResolveContext(timelineClip, out KimodoTimelineInOutConstraintContext context, out _))
            {
                return;
            }

            KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                context.Track,
                context.Animator,
                out _,
                out Quaternion rotation);
            forward = rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude <= 1e-8f)
            {
                forward = Vector3.forward;
            }
            else
            {
                forward.Normalize();
            }
        }
    }
}
