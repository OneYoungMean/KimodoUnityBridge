using System;
using System.Collections.Generic;
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
                rootObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
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

        internal static bool TryApplySampleToPoseRig(
            KimodoMarkerSampleResult sample,
            string modelName,
            PoseRigInstance instance,
            out string error)
        {
            error = string.Empty;
            if (sample == null || instance == null || instance.Root == null || instance.NameMap == null)
            {
                error = "invalid sample or pose rig instance";
                return false;
            }

            string[] modelJointNames = KimodoRigProfileDatabase.GetJointNamesForModel(modelName);
            if (modelJointNames == null || modelJointNames.Length == 0)
            {
                error = $"model joint layout not found for '{modelName}'";
                return false;
            }

            int count = sample.localAxisAngles != null ? sample.localAxisAngles.Count : 0;
            int applyCount = Mathf.Min(modelJointNames.Length, count);
            for (int i = 0; i < applyCount; i++)
            {
                string jointName = modelJointNames[i];
                if (!instance.NameMap.TryGetValue(jointName, out Transform t) || t == null)
                {
                    error = $"joint '{jointName}' missing on pose rig";
                    return false;
                }

                t.localRotation = AxisAngleToQuaternion(sample.localAxisAngles[i]);
            }

            string rootJointName = KimodoRigProfileDatabase.GetRootJointNameForModel(modelName);
            if (!string.IsNullOrWhiteSpace(rootJointName) &&
                instance.NameMap.TryGetValue(rootJointName, out Transform rootJoint) &&
                rootJoint != null)
            {
                rootJoint.position = sample.rootPosition;
            }
            else
            {
                instance.Root.transform.position = sample.rootPosition;
            }

            return true;
        }

        internal static bool TryResolveFootWorldPositions(
            PoseRigInstance instance,
            string modelName,
            out Vector3 leftFootPosition,
            out Vector3 rightFootPosition,
            out string error)
        {
            leftFootPosition = Vector3.zero;
            rightFootPosition = Vector3.zero;
            error = string.Empty;

            if (instance == null || instance.Root == null || instance.NameMap == null)
            {
                error = "invalid pose rig instance";
                return false;
            }

            if (!TryResolveFootTransform(instance, modelName, left: true, out Transform leftFoot, out error))
            {
                return false;
            }

            if (!TryResolveFootTransform(instance, modelName, left: false, out Transform rightFoot, out error))
            {
                return false;
            }

            leftFootPosition = leftFoot != null ? leftFoot.position : instance.Root.transform.position;
            rightFootPosition = rightFoot != null ? rightFoot.position : instance.Root.transform.position;
            return true;
        }

        private static bool TryResolveFootTransform(
            PoseRigInstance instance,
            string modelName,
            bool left,
            out Transform foot,
            out string error)
        {
            foot = null;
            error = string.Empty;

            string[] candidates = ResolveFootCandidates(modelName, left);
            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    instance.NameMap.TryGetValue(candidate, out foot) &&
                    foot != null)
                {
                    return true;
                }
            }

            error = left
                ? $"left foot transform not found for model '{modelName}'"
                : $"right foot transform not found for model '{modelName}'";
            return false;
        }

        private static string[] ResolveFootCandidates(string modelName, bool left)
        {
            KimodoConstraintRigType rigType = KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName);
            switch (rigType)
            {
                case KimodoConstraintRigType.G1:
                    return left
                        ? new[] { "left_ankle_roll_skel", "left_toe_base", "left_ankle_pitch_skel" }
                        : new[] { "right_ankle_roll_skel", "right_toe_base", "right_ankle_pitch_skel" };
                case KimodoConstraintRigType.Smplx:
                    return left
                        ? new[] { "left_foot", "left_ankle" }
                        : new[] { "right_foot", "right_ankle" };
                case KimodoConstraintRigType.Soma30:
                default:
                    return left
                        ? new[] { "LeftFoot", "LeftToeBase", "LeftShin" }
                        : new[] { "RightFoot", "RightToeBase", "RightShin" };
            }
        }

        private static Quaternion AxisAngleToQuaternion(Vector3 axisAngle)
        {
            float angleRad = axisAngle.magnitude;
            if (angleRad <= 1e-8f)
            {
                return Quaternion.identity;
            }

            Vector3 axis = axisAngle / angleRad;
            return Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, axis);
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
                case KimodoConstraintRigType.Soma30:
                default:
                    fileName = "SOMA30.fbx";
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

            if (mat.HasProperty("_Mode"))
            {
                mat.SetFloat("_Mode", 3f);
                configuredTransparentMode = true;
            }

            if (mat.HasProperty("_AlphaClip"))
            {
                mat.SetFloat("_AlphaClip", 0f);
            }

            if (mat.HasProperty("_SrcBlend"))
            {
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            }

            if (mat.HasProperty("_DstBlend"))
            {
                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            }

            if (mat.HasProperty("_ZWrite"))
            {
                mat.SetInt("_ZWrite", 0);
            }

            if (configuredTransparentMode)
            {
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = (int)RenderQueue.Transparent;
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.EnableKeyword("_ALPHABLEND_ON");
            }
            else
            {
                mat.renderQueue = -1;
            }
        }

        private const float NonConstraintAlpha = 0.7f;
        private static readonly Color NonConstraintColor = new Color(1f, 1f, 1f, NonConstraintAlpha);
    }
}
