using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor
{
    public static class KimodoAvatarRebuildTestMenu
    {
        private const string MenuPath = "Kimodo/Rebuild SOMA Avatar (Test)";
        private const string AvatarResourceName = "SOMAAvatar";
        private const string RootNamePrefix = "__KimodoSomaAvatarRebuildTest";
        private const string JointSphereName = "__joint";
        private const float JointSphereDiameter = 0.025f;
        private static readonly Color JointSphereColor = new Color(0.1f, 0.85f, 1f, 0.9f);
        private static Material sJointSphereMaterial;

        [MenuItem(MenuPath)]
        private static void RebuildSomaAvatarForTest()
        {
            Avatar avatar = Resources.Load<Avatar>(AvatarResourceName);
            if (avatar == null)
            {
                EditorUtility.DisplayDialog("Kimodo", $"Avatar resource not found: {AvatarResourceName}", "OK");
                return;
            }

            if (!avatar.isValid || !avatar.isHuman)
            {
                EditorUtility.DisplayDialog("Kimodo", $"Avatar '{AvatarResourceName}' is not a valid humanoid avatar.", "OK");
                return;
            }

            CleanupPreviousTestRoots();

            var root = new GameObject($"{RootNamePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}");
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;

            if (!TryBuildHierarchyViaPipeline(avatar, root.transform, out string error))
            {
                UnityEngine.Object.DestroyImmediate(root);
                EditorUtility.DisplayDialog("Kimodo", $"Rebuild failed:\n{error}", "OK");
                return;
            }

            AttachJointSpheres(root.transform);

            Animator animator = root.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.applyRootMotion = false;
            animator.enabled = false;
            animator.Rebind();
            animator.Update(0f);

            int skeletonCount = avatar.humanDescription.skeleton != null ? avatar.humanDescription.skeleton.Length : 0;
            int builtCount = root.GetComponentsInChildren<Transform>(true).Length - 1;

            Selection.activeGameObject = root;
            Debug.Log($"[Kimodo][Test] Rebuild SOMA avatar success. resource={AvatarResourceName}, skeleton={skeletonCount}, built={builtCount}, root={root.name}");
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

        private static void CleanupPreviousTestRoots()
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
