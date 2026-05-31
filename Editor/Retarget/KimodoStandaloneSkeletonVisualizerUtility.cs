using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor.Retarget
{
    internal static class KimodoStandaloneSkeletonVisualizerUtility
    {
        private const float SphereSize = 0.05f;

        public static GameObject CreateSkeletonVisualization(Transform sourceRoot, string name)
        {
            if (sourceRoot == null)
            {
                return null;
            }

            GameObject root = new GameObject(name);
            root.hideFlags = HideFlags.None;

            Transform[] all = sourceRoot.GetComponentsInChildren<Transform>(true);
            var byPath = new Dictionary<string, Transform>(all.Length, System.StringComparer.Ordinal);

            for (int i = 0; i < all.Length; i++)
            {
                Transform source = all[i];
                string path = AnimationUtility.CalculateTransformPath(source, sourceRoot);
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = source.name;
                sphere.hideFlags = HideFlags.None;
                Object.DestroyImmediate(sphere.GetComponent<Collider>());
                sphere.transform.localScale = Vector3.one * SphereSize;
                sphere.transform.SetParent(root.transform, false);
                sphere.transform.localPosition = source.localPosition;
                sphere.transform.localRotation = source.localRotation;
                sphere.transform.localScale = Vector3.one * SphereSize;
                byPath[path] = sphere.transform;
            }

            return root;
        }

        public static void SyncSkeletonVisualization(Transform sourceRoot, Transform visualizerRoot)
        {
            if (sourceRoot == null || visualizerRoot == null)
            {
                return;
            }

            Transform[] sourceAll = sourceRoot.GetComponentsInChildren<Transform>(true);
            var sourceByPath = new Dictionary<string, Transform>(sourceAll.Length, System.StringComparer.Ordinal);
            for (int i = 0; i < sourceAll.Length; i++)
            {
                string path = AnimationUtility.CalculateTransformPath(sourceAll[i], sourceRoot);
                if (!string.IsNullOrEmpty(path))
                {
                    sourceByPath[path] = sourceAll[i];
                }
            }

            Transform[] vizAll = visualizerRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < vizAll.Length; i++)
            {
                Transform viz = vizAll[i];
                if (viz == visualizerRoot)
                {
                    continue;
                }

                string path = AnimationUtility.CalculateTransformPath(viz, visualizerRoot);
                if (!sourceByPath.TryGetValue(path, out Transform source) || source == null)
                {
                    continue;
                }

                viz.localPosition = source.localPosition;
                viz.localRotation = source.localRotation;
            }
        }
    }
}
