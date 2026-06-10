using System;
using System.Collections.Generic;
using System.Linq;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintPoseRigFactory
    {
        internal sealed class PoseRigInstance
        {
            public GameObject Root;
            public Dictionary<string, Transform> NameMap;
            public List<Material> GeneratedMaterials;
        }

        internal static bool TryCreatePoseRig(
            string modelName,
            int clipId,
            int animatorId,
            out PoseRigInstance instance,
            out string error)
        {
            instance = null;
            error = string.Empty;

            KimodoConstraintRigType rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName);
            GameObject prefab = LoadRigPrefab(rigType);
            if (prefab == null)
            {
                error = $"pose rig prefab not found for rig type '{rigType}'";
                return false;
            }

            GameObject rootObject = null;
            List<Material> generatedMaterials = null;
            try
            {
                rootObject = UnityEngine.Object.Instantiate(prefab);
                rootObject.name = $"__KimodoPoseCache_{clipId}_{animatorId}_{rigType}";
                rootObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable | HideFlags.DontSave;
                rootObject.SetActive(false);

                Transform root = rootObject.transform;
                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                var nameMap = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform t = transforms[i];
                    if (t == null || string.IsNullOrWhiteSpace(t.name) || nameMap.ContainsKey(t.name))
                    {
                        continue;
                    }

                    nameMap[t.name] = t;
                }

                generatedMaterials = ConfigurePreviewMeshAppearance(rootObject);
                rootObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.NotEditable | HideFlags.DontSave;
                instance = new PoseRigInstance
                {
                    Root = rootObject,
                    NameMap = nameMap,
                    GeneratedMaterials = generatedMaterials
                };
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (rootObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(rootObject);
                }

                if (generatedMaterials != null)
                {
                    for (int i = 0; i < generatedMaterials.Count; i++)
                    {
                        Material material = generatedMaterials[i];
                        if (material != null)
                        {
                            UnityEngine.Object.DestroyImmediate(material);
                        }
                    }
                }

                instance = null;
                return false;
            }
        }

        internal static void DestroyPoseRig(PoseRigInstance instance)
        {
            if (instance == null)
            {
                return;
            }

            if (instance.Root != null)
            {
                UnityEngine.Object.DestroyImmediate(instance.Root);
            }

            if (instance.GeneratedMaterials != null)
            {
                for (int i = 0; i < instance.GeneratedMaterials.Count; i++)
                {
                    Material material = instance.GeneratedMaterials[i];
                    if (material != null)
                    {
                        UnityEngine.Object.DestroyImmediate(material);
                    }
                }
            }
        }

        private static GameObject LoadRigPrefab(KimodoConstraintRigType rigType)
        {
            string path = ResolveRigModelPath(rigType);
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        private static string ResolveRigModelPath(KimodoConstraintRigType rigType)
        {
            string fileName;
            switch (rigType)
            {
                case KimodoConstraintRigType.Smplx:
                    fileName = "SMPLX.fbx";
                    break;
                case KimodoConstraintRigType.G1:
                    fileName = "G1.fbx";
                    break;
                case KimodoConstraintRigType.Soma77:
                default:
                    fileName = "SOMA77.fbx";
                    break;
            }

            UnityEditor.PackageManager.PackageInfo packageInfo =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(KimodoConstraintPoseRigFactory).Assembly);
            if (packageInfo != null)
            {
                string byAssemblyPackage = $"{NormalizeAssetPath(packageInfo.assetPath)}/Editor/Model/{fileName}";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(byAssemblyPackage) != null)
                {
                    return byAssemblyPackage;
                }
            }

            const string packageName = "com.unity.kimodo_unity_motion_tools";
            string byPackageName = $"Packages/{packageName}/Editor/Model/{fileName}";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(byPackageName) != null)
            {
                return byPackageName;
            }

            string byAssetsFolder = $"Assets/Editor/Model/{fileName}";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(byAssetsFolder) != null)
            {
                return byAssetsFolder;
            }

            return $"Editor/Model/{fileName}";
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return path.Replace('\\', '/').TrimEnd('/');
        }

        private static List<Material> ConfigurePreviewMeshAppearance(GameObject instance)
        {
            var generated = new List<Material>();
            if (instance == null)
            {
                return generated;
            }

            Material sharedPreviewMaterial = CreatePreviewMaterial();
            if (sharedPreviewMaterial != null)
            {
                generated.Add(sharedPreviewMaterial);
            }

            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                Transform tr = renderer.transform;

                Material[] shared = renderer.sharedMaterials;
                if (shared == null || shared.Length == 0)
                {
                    continue;
                }

                if (sharedPreviewMaterial == null)
                {
                    continue;
                }

                Material[] mats = new Material[shared.Length];
                for (int m = 0; m < mats.Length; m++)
                {
                    mats[m] = sharedPreviewMaterial;
                }

                renderer.sharedMaterials = mats;
            }

            return generated;
        }

        private static Material CreatePreviewMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                return null;
            }

            Material material = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = "__KimodoPoseCachePreview"
            };
            SetMaterialColor(material, NonConstraintColor, NonConstraintAlpha);
            return material;
        }

        private static void SetMaterialColor(Material mat, Color color, float alpha)
        {
            if (mat == null)
            {
                return;
            }

            Color c = new Color(color.r, color.g, color.b, alpha);
            if (mat.HasProperty("_BaseColor"))
            {
                mat.SetColor("_BaseColor", c);
            }

            if (mat.HasProperty("_Color"))
            {
                mat.SetColor("_Color", c);
            }

            bool configuredTransparentMode = false;
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                configuredTransparentMode = true;
            }
        }

        private const float NonConstraintAlpha = 0.7f;
        private static readonly Color NonConstraintColor = new Color(1f, 1f, 1f, NonConstraintAlpha);
    }
}
