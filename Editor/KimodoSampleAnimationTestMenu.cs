using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor
{
    public static class KimodoSampleAnimationTestMenu
    {
        private const string MenuPath = "Assets/Kimodo/Retarget/Generate SOMA Sample Frames (Test)";
        private const string AvatarResourceName = "SOMAAvatar";
        private const string RootNamePrefix = "__KimodoSampleAnimationTest";
        private const string JointSphereName = "__joint";
        private const float JointSphereDiameter = 0.025f;
        private static readonly Color JointSphereColor = new Color(0.1f, 0.85f, 1f, 0.9f);
        private static Material sJointSphereMaterial;

        [MenuItem(MenuPath, false, 2050)]
        private static void GenerateSampleAnimationFrames()
        {
            AnimationClip sourceClip = Selection.activeObject as AnimationClip;
            if (sourceClip == null)
            {
                EditorUtility.DisplayDialog("Kimodo", "Please select an AnimationClip first.", "OK");
                return;
            }

            Avatar avatar = Resources.Load<Avatar>(AvatarResourceName);
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                EditorUtility.DisplayDialog("Kimodo", $"Avatar resource not found or invalid: {AvatarResourceName}", "OK");
                return;
            }

            List<string> bindingPaths = CollectTransformBindingPaths(sourceClip);
            if (bindingPaths.Count == 0)
            {
                EditorUtility.DisplayDialog("Kimodo", "Selected clip has no Transform bindings.", "OK");
                return;
            }

            CleanupPreviousRoots();

            string clipLabel = SanitizeFileName(sourceClip.name);
            GameObject outputRoot = new GameObject($"{RootNamePrefix}_{clipLabel}_{DateTime.Now:yyyyMMdd_HHmmss}");
            outputRoot.hideFlags = HideFlags.None;
            outputRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            outputRoot.transform.localScale = Vector3.one;

            GameObject templateRoot = new GameObject($"{RootNamePrefix}_Template_{clipLabel}");
            templateRoot.hideFlags = HideFlags.HideAndDontSave;
            templateRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            templateRoot.transform.localScale = Vector3.one;

            try
            {
                if (!TryBuildHierarchyViaPipeline(avatar, templateRoot.transform, out string error))
                {
                    throw new InvalidOperationException(error);
                }

                Transform rigRoot = ResolveRigRoot(templateRoot.transform, avatar);
                if (rigRoot == null)
                {
                    throw new InvalidOperationException("Failed to resolve SOMA skeleton root from built hierarchy.");
                }

                EnsureHierarchyMatchesClipBindings(sourceClip, rigRoot);
                InitializeAnimator(rigRoot.gameObject);

                float frameRate = sourceClip.frameRate > 0f ? sourceClip.frameRate : 30f;
                float duration = sourceClip.length > 0f ? sourceClip.length : 1f / Mathf.Max(1f, frameRate);
                int frameCount = Mathf.Max(2, Mathf.RoundToInt(duration * frameRate) + 1);
                float frameSpacing = 0.35f;

                for (int frame = 0; frame < frameCount; frame++)
                {
                    float t = FrameToTime(frame, frameCount, duration);
                    GameObject frameContainer = new GameObject($"frame_{frame:0000}_t_{t:F3}");
                    frameContainer.hideFlags = HideFlags.None;
                    frameContainer.transform.SetParent(outputRoot.transform, false);
                    frameContainer.transform.localPosition = new Vector3(frame * frameSpacing, 0f, 0f);
                    frameContainer.transform.localRotation = Quaternion.identity;
                    frameContainer.transform.localScale = Vector3.one;

                    GameObject frameRig = UnityEngine.Object.Instantiate(rigRoot.gameObject);
                    ClearHideFlagsRecursively(frameRig);
                    frameRig.name = rigRoot.name;
                    frameRig.transform.SetParent(frameContainer.transform, false);
                    frameRig.transform.localPosition = Vector3.zero;
                    frameRig.transform.localRotation = Quaternion.identity;
                    frameRig.transform.localScale = Vector3.one;
                    InitializeAnimator(frameRig);

                    sourceClip.SampleAnimation(frameRig, t);
                    AttachJointSpheres(frameRig.transform);

                    if (frame == 0)
                    {
                        LogHipsPaths(frameRig.transform);
                    }
                }

                Selection.activeGameObject = outputRoot;
                EditorGUIUtility.PingObject(outputRoot);
                Debug.Log($"[Kimodo][Test] SampleAnimation frame hierarchy exported. clip={sourceClip.name}, bindings={bindingPaths.Count}, frames={frameCount}, root={outputRoot.name}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Kimodo][Test] SampleAnimation frame test failed: {e}");
                EditorUtility.DisplayDialog("Kimodo", $"SampleAnimation test failed:\n{e.Message}", "OK");
            }
            finally
            {
                if (templateRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(templateRoot);
                }
            }
        }

        [MenuItem(MenuPath, true)]
        private static bool ValidateGenerateSampleAnimationFrames()
        {
            return Selection.activeObject is AnimationClip;
        }

        private static bool TryBuildHierarchyViaPipeline(Avatar avatar, Transform root, out string error)
        {
            error = string.Empty;
            MethodInfo method = typeof(KimodoRetargetPipeline).GetMethod(
                "TryBuildHierarchyFromAvatarSkeleton",
                BindingFlags.NonPublic | BindingFlags.Static);

            if (method == null)
            {
                error = "TryBuildHierarchyFromAvatarSkeleton not found on KimodoRetargetPipeline.";
                return false;
            }

            object[] args = { avatar, root, string.Empty };
            object result;
            try
            {
                result = method.Invoke(null, args);
            }
            catch (Exception e)
            {
                error = $"Invoke failed: {e.Message}";
                return false;
            }

            bool ok = result is bool b && b;
            if (!ok)
            {
                error = args[2] as string ?? "Unknown error.";
            }

            return ok;
        }

        private static void EnsureHierarchyMatchesClipBindings(AnimationClip clip, Transform root)
        {
            if (clip == null || root == null)
            {
                return;
            }

            var uniquePaths = new HashSet<string>(StringComparer.Ordinal);
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding b = bindings[i];
                if (b.type != typeof(Transform))
                {
                    continue;
                }

                string path = b.path ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path) || !uniquePaths.Add(path))
                {
                    continue;
                }

                EnsurePath(root, path);
            }
        }

        private static Transform EnsurePath(Transform root, string path)
        {
            string[] segments = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            Transform current = root;
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                Transform child = current.Find(segment);
                if (child == null)
                {
                    GameObject go = new GameObject(segment);
                    child = go.transform;
                    child.SetParent(current, false);
                    child.localPosition = Vector3.zero;
                    child.localRotation = Quaternion.identity;
                    child.localScale = Vector3.one;
                }

                current = child;
            }

            return current;
        }

        private static Transform ResolveRigRoot(Transform builtHierarchyRoot, Avatar avatar)
        {
            if (builtHierarchyRoot == null)
            {
                return null;
            }

            if (avatar != null && avatar.humanDescription.skeleton != null)
            {
                for (int i = 0; i < avatar.humanDescription.skeleton.Length; i++)
                {
                    SkeletonBone bone = avatar.humanDescription.skeleton[i];
                    if (string.IsNullOrWhiteSpace(bone.name))
                    {
                        continue;
                    }

                    Transform directChild = builtHierarchyRoot.Find(bone.name);
                    if (directChild != null)
                    {
                        return directChild;
                    }
                    break;
                }
            }

            if (builtHierarchyRoot.childCount > 0)
            {
                return builtHierarchyRoot.GetChild(0);
            }

            return builtHierarchyRoot;
        }

        private static List<string> CollectTransformBindingPaths(AnimationClip clip)
        {
            var result = new List<string>();
            if (clip == null)
            {
                return result;
            }

            var unique = new HashSet<string>(StringComparer.Ordinal);
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding b = bindings[i];
                if (b.type != typeof(Transform))
                {
                    continue;
                }

                string path = b.path ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (unique.Add(path))
                {
                    result.Add(path);
                }
            }

            return result;
        }

        private static float FrameToTime(int frame, int frameCount, float duration)
        {
            if (frameCount <= 1)
            {
                return 0f;
            }

            float normalized = frame / (frameCount - 1f);
            return Mathf.Clamp01(normalized) * Mathf.Max(0f, duration);
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "KimodoSampleAnimation";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = new char[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                chars[i] = Array.IndexOf(invalid, value[i]) >= 0 ? '_' : value[i];
            }

            return new string(chars);
        }

        private static void AttachJointSpheres(Transform root)
        {
            if (root == null)
            {
                return;
            }

            Transform[] bones = root.GetComponentsInChildren<Transform>(true);
            Material material = GetOrCreateJointSphereMaterial();
            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];
                if (bone == null || bone == root)
                {
                    continue;
                }

                Transform marker = bone.Find(JointSphereName);
                if (marker != null)
                {
                    continue;
                }

                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = JointSphereName;
                Transform t = sphere.transform;
                t.SetParent(bone, false);
                t.localPosition = Vector3.zero;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one * JointSphereDiameter;

                Collider col = sphere.GetComponent<Collider>();
                if (col != null)
                {
                    UnityEngine.Object.DestroyImmediate(col);
                }

                Renderer renderer = sphere.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = material;
                }
            }
        }

        private static Material GetOrCreateJointSphereMaterial()
        {
            if (sJointSphereMaterial != null)
            {
                return sJointSphereMaterial;
            }

            Shader shader = Shader.Find("Standard");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            sJointSphereMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            sJointSphereMaterial.color = JointSphereColor;
            return sJointSphereMaterial;
        }

        private static void ClearHideFlagsRecursively(GameObject go)
        {
            if (go == null)
            {
                return;
            }

            Transform[] all = go.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null)
                {
                    all[i].gameObject.hideFlags = HideFlags.None;
                }
            }
        }

        private static void InitializeAnimator(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            Animator animator = root.GetComponent<Animator>();
            if (animator == null)
            {
                animator = root.AddComponent<Animator>();
            }

            animator.avatar = null;
            animator.applyRootMotion = false;
            animator.enabled = false;
            animator.Rebind();
            animator.Update(0f);
        }

        private static void LogHipsPaths(Transform root)
        {
            if (root == null)
            {
                return;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null)
                {
                    continue;
                }

                if (!string.Equals(t.name, "Hips", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string path = AnimationUtility.CalculateTransformPath(t, root);
                Debug.Log($"[Kimodo][Test] Hips node: path='{path}', parent='{(t.parent != null ? t.parent.name : "<root>")}'");
            }
        }

        private static void CleanupPreviousRoots()
        {
            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                GameObject go = all[i];
                if (go == null)
                {
                    continue;
                }

                if (!go.name.StartsWith(RootNamePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if ((go.hideFlags & HideFlags.DontSave) != 0)
                {
                    continue;
                }

                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }
}
