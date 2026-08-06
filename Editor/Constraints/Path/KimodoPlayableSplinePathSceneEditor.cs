using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;

namespace KimodoBridge.Editor
{
    [InitializeOnLoad]
    internal static class KimodoPlayableSplinePathSceneEditor
    {
        private const float InsertHitRadiusPixels = 12f;

        static KimodoPlayableSplinePathSceneEditor()
        {
            SceneView.duringSceneGui += OnSceneGui;
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
            Undo.RecordObject(container, "Insert Kimodo Spline Knot");
            InsertKnotPreservingCurve(container.Spline, splineT);
            EditorUtility.SetDirty(container);
            EditorSceneManager.MarkSceneDirty(path.gameObject.scene);
            current.Use();
            SceneView.RepaintAll();
        }

        internal static void InsertKnotPreservingCurve(Spline spline, float splineT)
        {
            if (spline == null || spline.Count < 2)
            {
                return;
            }

            int curveIndex = spline.SplineToCurveT(
                Mathf.Clamp(splineT, 0.0001f, 0.9999f),
                out float curveT);
            int nextIndex = spline.NextIndex(curveIndex);
            if (curveIndex == nextIndex)
            {
                return;
            }

            BezierKnot previous = spline[curveIndex];
            BezierKnot next = spline[nextIndex];
            CurveUtility.Split(spline.GetCurve(curveIndex), curveT, out BezierCurve left, out BezierCurve right);

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
        }

        private static bool TryGetSelectedPath(out KimodoPlayableSplinePath path)
        {
            path = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponent<KimodoPlayableSplinePath>()
                : null;
            return path != null && path.isActiveAndEnabled && path.SplineContainer != null;
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
            DrawDefaultInspector();
            EditorGUILayout.HelpBox(
                "Use Unity's Spline tools for knot and tangent editing. Right-click directly on the curve to insert a knot while preserving its current shape.",
                MessageType.None);
        }
    }
}
