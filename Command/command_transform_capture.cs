using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        public static string TransformCapture(string argumentsJson) => Execute(argumentsJson, arguments =>
        {
            string characterPath = RequiredStringValue(arguments, "character_path");
            GameObject character = FindSceneObjectByPath(characterPath);
            if (character == null) throw new InvalidOperationException($"Scene character path '{characterPath}' was not found.");
            JArray transformValues = arguments["transforms"] as JArray
                ?? throw new InvalidOperationException("transforms must be an array.");
            if (transformValues.Count < 1)
                throw new InvalidOperationException("transforms must contain at least one path.");
            int size = Mathf.Clamp(arguments.Value<int?>("resolution") ?? 1024, 64, 4096);
            int[] frames = (arguments["frames"] as JArray)?.Values<int>().ToArray() ?? Array.Empty<int>();
            var targets = transformValues.Values<string>().Select(path =>
            {
                Transform transform = FindRelativeTransform(character.transform, path) ?? FindSceneTransform(path);
                if (transform == null) throw new InvalidOperationException($"Transform path '{path}' was not found below '{characterPath}' or in the active scene.");
                return new CaptureTarget(path, transform);
            }).ToList();
            var renderers = character.GetComponentsInChildren<Renderer>(true);
            Bounds bounds = default;
            for (int index = 0; index < targets.Count; index++)
            {
                CaptureTarget target = targets[index];
                Renderer[] targetRenderers = target.Transform.IsChildOf(character.transform)
                    ? renderers
                    : target.Transform.root.GetComponentsInChildren<Renderer>(true);
                Bounds targetBounds = CalculateTransformBounds(target.Transform, targetRenderers);
                if (index == 0) bounds = targetBounds;
                else bounds.Encapsulate(targetBounds);
            }

            string folder = Path.Combine(Application.dataPath, "KimodoGeneratedClips", "TransformCaptures");
            Directory.CreateDirectory(folder);
            // Scene poses, lighting and visibility can change between identical requests.
            string fileName = "transform_capture_" + Guid.NewGuid().ToString("N");
            string file = Path.Combine(folder, fileName + ".png");
            var descriptions = new JArray();
            var directions = new[] { Vector3.back, Vector3.right, Vector3.up, new Vector3(1f, .75f, -1f) };
            var views = new[] { "front", "right", "top", "3d" };
            var sheet = new Texture2D(size * 2, size * 2, TextureFormat.RGBA32, false);
            try
            {
                for (int index = 0; index < views.Length; index++)
                {
                    Camera camera = CreateTransformCaptureCamera(bounds, directions[index], index != 3);
                    Texture2D image = null;
                    try
                    {
                        image = RenderCamera(camera, size, size, new Color(.12f, .12f, .12f, 1f));
                        sheet.SetPixels(index % 2 * size, (1 - index / 2) * size, size, size, image.GetPixels());
                    }
                    finally
                    {
                        if (image != null) UnityEngine.Object.DestroyImmediate(image);
                        UnityEngine.Object.DestroyImmediate(camera.gameObject);
                    }
                    descriptions.Add(new JObject
                    {
                        ["tile"] = $"{index / 2 + 1}-{index % 2 + 1}",
                        ["view"] = views[index],
                        ["projection"] = index == 3 ? "perspective" : "orthographic",
                        ["transforms"] = new JArray(targets.Select(target => target.Path)),
                        ["bounds"] = new JObject
                        {
                            ["min"] = new JArray(bounds.min.x, bounds.min.y, bounds.min.z),
                            ["max"] = new JArray(bounds.max.x, bounds.max.y, bounds.max.z)
                        },
                        ["frames"] = new JArray(frames)
                    });
                }
                sheet.Apply(false, false);
                File.WriteAllBytes(file, sheet.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(sheet); }
            AssetDatabase.Refresh();
            JObject response = new JObject
            {
                ["image_path"] = "Assets/KimodoGeneratedClips/TransformCaptures/" + Path.GetFileName(file),
                ["width"] = size * 2,
                ["height"] = size * 2,
                ["tile_size"] = size,
                ["tiles"] = descriptions,
                ["ok"] = true
            };
            File.WriteAllText(Path.Combine(folder, fileName + ".json"), response.ToString(Newtonsoft.Json.Formatting.Indented));
            return response.ToString(Newtonsoft.Json.Formatting.None);
        });

        private static Camera CreateTransformCaptureCamera(Bounds bounds, Vector3 direction, bool orthographic)
        {
            Camera camera = CreateAnalysisPictureCamera("Kimodo Transform Capture Camera");
            camera.cullingMask = -1; // Render all layers for transform capture
            camera.orthographic = orthographic;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.orthographicSize = Mathf.Max(2.5f, bounds.extents.magnitude * 1.05f);
            camera.fieldOfView = 35f;
            camera.transform.position = bounds.center + direction.normalized * Mathf.Max(7f, bounds.extents.magnitude * 3.2f);
            Vector3 up = Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            camera.transform.LookAt(bounds.center + Vector3.up, up);
            return camera;
        }

        private static Bounds CalculateTransformBounds(Transform target, IEnumerable<Renderer> renderers)
        {
            Bounds bounds = new Bounds(target.position, Vector3.zero);
            bool hasBounds = false;
            foreach (Renderer renderer in renderers.Where(item => IsRelevantRenderer(item, target)))
            {
                if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                {
                    Mesh baked = new Mesh();
                    skinned.BakeMesh(baked);
                    Vector3[] vertices = baked.vertices;
                    BoneWeight[] weights = skinned.sharedMesh.boneWeights;
                    for (int i = 0; i < Mathf.Min(vertices.Length, weights.Length); i++)
                    {
                        BoneWeight weight = weights[i];
                        if ((weight.weight0 > .1f && BoneMatches(skinned, weight.boneIndex0, target)) ||
                            (weight.weight1 > .1f && BoneMatches(skinned, weight.boneIndex1, target)) ||
                            (weight.weight2 > .1f && BoneMatches(skinned, weight.boneIndex2, target)) ||
                            (weight.weight3 > .1f && BoneMatches(skinned, weight.boneIndex3, target)))
                        {
                            Vector3 world = skinned.transform.TransformPoint(vertices[i]);
                            if (!hasBounds) { bounds = new Bounds(world, Vector3.zero); hasBounds = true; } else bounds.Encapsulate(world);
                        }
                    }
                    UnityEngine.Object.DestroyImmediate(baked);
                }
                else if (renderer is MeshRenderer)
                {
                    if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; } else bounds.Encapsulate(renderer.bounds);
                }
            }
            if (!hasBounds) throw new InvalidOperationException($"No renderable geometry found for '{GetSceneHierarchyPath(target)}'.");
            return bounds;
        }

        private static bool IsRelevantRenderer(Renderer renderer, Transform target) => renderer != null &&
            (renderer.transform == target || renderer.transform.IsChildOf(target) ||
             (renderer is SkinnedMeshRenderer skinned && skinned.bones.Any(bone => bone != null && (bone == target || bone.IsChildOf(target)))));

        private static bool BoneMatches(SkinnedMeshRenderer renderer, int index, Transform target) =>
            index >= 0 && index < renderer.bones.Length && renderer.bones[index] != null && (renderer.bones[index] == target || renderer.bones[index].IsChildOf(target));

        private static Transform FindRelativeTransform(Transform root, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return root;
            return root.Find(path.Trim()) ?? (root.name == path.Trim() ? root : null);
        }

        private static Transform FindSceneTransform(string path)
        {
            string normalized = (path ?? string.Empty).Trim('/');
            return Resources.FindObjectsOfTypeAll<Transform>().FirstOrDefault(item => item.gameObject.scene.IsValid() &&
                string.Equals(GetSceneHierarchyPath(item), normalized, StringComparison.Ordinal));
        }

        private static GameObject FindSceneObjectByPath(string path) =>
            Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(item => item.scene.IsValid() &&
                string.Equals(GetSceneHierarchyPath(item.transform), path.Trim('/'), StringComparison.Ordinal));

        private static string GetSceneHierarchyPath(Transform transform) =>
            transform.parent == null ? transform.name : GetSceneHierarchyPath(transform.parent) + "/" + transform.name;

        private readonly struct CaptureTarget { public CaptureTarget(string path, Transform transform) { Path = path; Transform = transform; } public string Path { get; } public Transform Transform { get; } }
    }
}
