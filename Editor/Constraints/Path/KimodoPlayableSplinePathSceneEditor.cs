#if KIMODO_SPLINES
using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Splines;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    [InitializeOnLoad]
    internal static class KimodoPlayableSplinePathSceneEditor
    {
        private const float InsertHitRadiusPixels = 12f;
        private const double SelectionPollIntervalSeconds = 0.2;

        private static bool refreshQueued;
        private static double nextSelectionPollTime;

        static KimodoPlayableSplinePathSceneEditor()
        {
            Selection.selectionChanged += ScheduleRefresh;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.update += PollSelection;
            EditorApplication.quitting += DestroyEditingProxies;
            AssemblyReloadEvents.beforeAssemblyReload += DestroyEditingProxies;
            SceneView.beforeSceneGui += OnBeforeSceneGui;
            SceneView.duringSceneGui += OnSceneGui;
            Spline.Changed += OnSplineChanged;
            ScheduleRefresh();
        }

        internal static void BeginEditing(KimodoPlayableSplinePath path)
        {
            if (path == null ||
                !KimodoPlayableSplinePathUtility.IsEditingProxy(path) ||
                !KimodoPlayableClipGenerationSettings.instance.EnableSplineExperimental)
            {
                return;
            }

            SceneVisibilityManager.instance.Show(path.gameObject, true);
            Selection.activeGameObject = path.gameObject;
            Type contextType = Type.GetType("UnityEditor.Splines.SplineToolContext, Unity.Splines.Editor");
            Type moveToolType = Type.GetType("UnityEditor.Splines.SplineMoveTool, Unity.Splines.Editor");
            if (contextType != null && moveToolType != null)
            {
                ToolManager.SetActiveContext(contextType);
                ToolManager.SetActiveTool(moveToolType);
            }
            else
            {
                Debug.LogWarning("[Kimodo][SplinePath] Unity Splines editor tools are unavailable.");
            }
            SceneView.lastActiveSceneView?.FrameSelected();
            ScheduleRefresh();
        }

        internal static void ScheduleRefresh()
        {
            if (refreshQueued)
            {
                return;
            }

            refreshQueued = true;
            EditorApplication.delayCall += RefreshVisibility;
        }

        private static void PollSelection()
        {
            if (EditorApplication.timeSinceStartup < nextSelectionPollTime)
            {
                return;
            }

            nextSelectionPollTime = EditorApplication.timeSinceStartup + SelectionPollIntervalSeconds;
            ScheduleRefresh();
        }

        private static void RefreshVisibility()
        {
            refreshQueued = false;
            bool featureEnabled = KimodoPlayableClipGenerationSettings.instance.EnableSplineExperimental;
            var selectedClips = new HashSet<KimodoPlayableClip>();
            TimelineClip[] timelineClips = TimelineEditor.selectedClips;
            if (timelineClips != null)
            {
                for (int i = 0; i < timelineClips.Length; i++)
                {
                    TimelineClip timelineClip = timelineClips[i];
                    if (timelineClip?.asset is not KimodoPlayableClip clip)
                    {
                        continue;
                    }

                    selectedClips.Add(clip);
                    if (featureEnabled && clip.splinePathEnabled)
                    {
                        KimodoPlayableSplinePathUtility.TryGetPath(clip, timelineClip, out _, out _);
                    }
                }
            }

            bool changed = false;
            KimodoPlayableSplinePath[] paths = Resources.FindObjectsOfTypeAll<KimodoPlayableSplinePath>();
            for (int i = 0; i < paths.Length; i++)
            {
                KimodoPlayableSplinePath path = paths[i];
                if (path == null || !path.gameObject.scene.IsValid() || EditorUtility.IsPersistent(path))
                {
                    continue;
                }

                if (!KimodoPlayableSplinePathUtility.IsEditingProxy(path))
                {
                    if (featureEnabled && KimodoPlayableSplinePathUtility.TryMigrateLegacyPath(path, out _))
                    {
                        changed = true;
                        continue;
                    }
                    if (!SceneVisibilityManager.instance.IsHidden(path.gameObject))
                    {
                        SceneVisibilityManager.instance.Hide(path.gameObject, true);
                        changed = true;
                    }
                    continue;
                }

                bool shouldKeep = featureEnabled &&
                    path.OwnerClip != null &&
                    path.OwnerClip.splinePathEnabled &&
                    (selectedClips.Contains(path.OwnerClip) || Selection.activeGameObject == path.gameObject);
                if (!shouldKeep)
                {
                    KimodoPlayableSplinePathUtility.DestroyEditingProxy(path);
                    changed = true;
                    continue;
                }

                if (SceneVisibilityManager.instance.IsHidden(path.gameObject))
                {
                    SceneVisibilityManager.instance.Show(path.gameObject, true);
                    changed = true;
                }
            }

            if (changed)
            {
                SceneView.RepaintAll();
            }
        }

        private static void OnBeforeSceneGui(SceneView sceneView)
        {
            Event current = Event.current;
            bool mayEditSpline =
                (current.type == EventType.MouseDown && current.button == 0) ||
                current.type == EventType.KeyDown ||
                current.type == EventType.ExecuteCommand;
            if (mayEditSpline &&
                TryGetSelectedPath(out KimodoPlayableSplinePath path) &&
                path.OwnerClip != null)
            {
                Undo.RecordObject(path.OwnerClip, "Edit Kimodo Spline Path");
            }
        }

        private static void OnSceneGui(SceneView sceneView)
        {
            Event current = Event.current;
            if (current.type != EventType.MouseDown ||
                current.button != 1 ||
                current.alt ||
                GUIUtility.hotControl != 0 ||
                Tools.viewToolActive ||
                !TryGetSelectedPath(out KimodoPlayableSplinePath path))
            {
                return;
            }

            if (!TryGetNearestSplinePoint(path, current.mousePosition, out float splineT))
            {
                return;
            }

            SplineContainer container = path.SplineContainer;
            KimodoPlayableClip clip = path.OwnerClip;
            IReadOnlyList<KimodoSplineKnotData> existingKnots = clip.SplineKnots;
            var existingTimes = new List<float>(existingKnots.Count);
            for (int i = 0; i < existingKnots.Count; i++)
            {
                existingTimes.Add(existingKnots[i].time);
            }
            Undo.RecordObject(clip, "Insert Kimodo Spline Knot");
            Undo.RecordObject(container, "Insert Kimodo Spline Knot");
            if (!InsertKnotPreservingCurve(
                    container.Spline,
                    splineT,
                    out int insertedIndex,
                    out float curveTime))
            {
                return;
            }

            float insertedTime = Mathf.Lerp(
                existingTimes[insertedIndex - 1],
                existingTimes[insertedIndex],
                curveTime);
            clip.SetSplineKnotTime(insertedIndex, insertedTime);
            EditorUtility.SetDirty(clip);
            current.Use();
            SceneView.RepaintAll();
        }

        internal static void InsertKnotPreservingCurve(Spline spline, float splineT)
        {
            InsertKnotPreservingCurve(spline, splineT, out _, out _);
        }

        private static bool InsertKnotPreservingCurve(
            Spline spline,
            float splineT,
            out int insertedIndex,
            out float curveTime)
        {
            insertedIndex = -1;
            curveTime = 0f;
            if (spline == null || spline.Count < 2 || spline.Closed)
            {
                return false;
            }

            int curveIndex = spline.SplineToCurveT(
                Mathf.Clamp(splineT, 0.0001f, 0.9999f),
                out curveTime);
            int nextIndex = spline.NextIndex(curveIndex);
            if (curveIndex == nextIndex)
            {
                return false;
            }

            BezierKnot previous = spline[curveIndex];
            BezierKnot next = spline[nextIndex];
            CurveUtility.Split(spline.GetCurve(curveIndex), curveTime, out BezierCurve left, out BezierCurve right);

            previous.TangentOut = math.mul(math.inverse(previous.Rotation), left.Tangent0);
            next.TangentIn = math.mul(math.inverse(next.Rotation), right.Tangent1);
            quaternion rotation = quaternion.LookRotationSafe(
                math.normalizesafe(right.Tangent0, new float3(0f, 0f, 1f)),
                math.up());
            quaternion inverseRotation = math.inverse(rotation);
            var inserted = new BezierKnot(
                left.P3,
                math.mul(inverseRotation, left.Tangent1),
                math.mul(inverseRotation, right.Tangent0),
                rotation);

            spline.SetTangentMode(curveIndex, TangentMode.Broken);
            spline.SetTangentMode(nextIndex, TangentMode.Broken);
            spline[curveIndex] = previous;
            spline[nextIndex] = next;
            spline.Insert(nextIndex, inserted, TangentMode.Broken);
            insertedIndex = nextIndex;
            return true;
        }

        private static void OnSplineChanged(
            Spline spline,
            int knotIndex,
            SplineModification modification)
        {
            KimodoPlayableSplinePath[] paths = Resources.FindObjectsOfTypeAll<KimodoPlayableSplinePath>();
            for (int i = 0; i < paths.Length; i++)
            {
                KimodoPlayableSplinePath path = paths[i];
                if (!KimodoPlayableSplinePathUtility.IsEditingProxy(path) ||
                    path.SplineContainer == null ||
                    path.SplineContainer.Spline != spline ||
                    path.OwnerClip == null)
                {
                    continue;
                }

                KimodoPlayableSplinePathUtility.SyncEditingProxyToAsset(path, knotIndex, modification);
                return;
            }
        }

        private static void OnUndoRedo()
        {
            KimodoPlayableSplinePath[] paths = Resources.FindObjectsOfTypeAll<KimodoPlayableSplinePath>();
            for (int i = 0; i < paths.Length; i++)
            {
                if (KimodoPlayableSplinePathUtility.IsEditingProxy(paths[i]))
                {
                    KimodoPlayableSplinePathUtility.RefreshEditingProxy(paths[i]);
                }
            }
            ScheduleRefresh();
        }

        private static void DestroyEditingProxies()
        {
            KimodoPlayableSplinePathUtility.DestroyEditingProxies();
        }

        private static bool TryGetSelectedPath(out KimodoPlayableSplinePath path)
        {
            path = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<KimodoPlayableSplinePath>()
                : null;
            return KimodoPlayableSplinePathUtility.IsEditingProxy(path) &&
                path.isActiveAndEnabled &&
                path.SplineContainer != null;
        }

        private static bool TryGetNearestSplinePoint(
            KimodoPlayableSplinePath path,
            Vector2 mousePosition,
            out float splineT)
        {
            splineT = 0f;
            SplineContainer container = path.SplineContainer;
            Spline spline = container != null ? container.Spline : null;
            if (spline == null || spline.Count < 2)
            {
                return false;
            }

            Ray worldRay = HandleUtility.GUIPointToWorldRay(mousePosition);
            Transform transform = container.transform;
            var localRay = new Ray(
                transform.InverseTransformPoint(worldRay.origin),
                transform.InverseTransformDirection(worldRay.direction).normalized);
            SplineUtility.GetNearestPoint(spline, localRay, out float3 nearest, out splineT);
            Vector3 screenPoint = HandleUtility.WorldToGUIPointWithDepth(transform.TransformPoint(nearest));
            return screenPoint.z > 0f &&
                Vector2.Distance(mousePosition, new Vector2(screenPoint.x, screenPoint.y)) <= InsertHitRadiusPixels;
        }
    }

    [CustomEditor(typeof(KimodoPlayableSplinePath))]
    internal sealed class KimodoPlayableSplinePathEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var path = (KimodoPlayableSplinePath)target;
            EditorGUILayout.ObjectField("Playable Clip", path.OwnerClip, typeof(KimodoPlayableClip), false);
            EditorGUILayout.LabelField($"{path.WaypointCount} Root2D samples", EditorStyles.miniLabel);
            EditorGUILayout.HelpBox(
                "This is a temporary editor proxy. Use Unity's Spline tools for knots and tangents; right-click the curve to insert a time-ordered knot.",
                MessageType.None);
        }
    }
}
#endif
