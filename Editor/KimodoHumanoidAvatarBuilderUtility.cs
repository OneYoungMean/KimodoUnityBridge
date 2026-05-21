using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor
{
    internal static class KimodoHumanoidAvatarBuilderUtility
    {
        internal static bool TryLoadImporterAvatar(GameObject gameObject, out Avatar avatar, out string modelImporterPath)
        {
            avatar = null;
            modelImporterPath = string.Empty;
            if (!TryGetModelImporter(gameObject, out ModelImporter importer, out modelImporterPath))
            {
                return false;
            }

            avatar = AssetDatabase.LoadAssetAtPath<Avatar>(modelImporterPath);
            return avatar != null;
        }

        internal static Avatar GenerateHumanoidAvatar(
            GameObject sourceRoot,
            bool includeExtendedNameAliases,
            bool normalizeSourceTransformBeforeClone,
            bool forceUnitScaleOnClone,
            string avatarNameSuffix,
            out string error)
        {
            error = string.Empty;
            if (sourceRoot == null)
            {
                error = "Avatar root object is null.";
                return null;
            }

            Vector3 oldPos = sourceRoot.transform.position;
            Quaternion oldRot = sourceRoot.transform.rotation;
            GameObject editable = null;

            try
            {
                if (normalizeSourceTransformBeforeClone)
                {
                    sourceRoot.transform.position = Vector3.zero;
                    sourceRoot.transform.rotation = Quaternion.identity;
                }

                GameObject rootObject = sourceRoot;
                if (sourceRoot.TryGetComponent(out Animator animator) && animator.avatarRoot != null)
                {
                    rootObject = animator.avatarRoot.gameObject;
                }

                editable = UnityEngine.Object.Instantiate(rootObject);
                editable.name = rootObject.name;
                editable.hideFlags = HideFlags.HideAndDontSave;
                editable.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                if (forceUnitScaleOnClone)
                {
                    editable.transform.localScale = Vector3.one;
                }

                TryForceTPoseReflective(editable);
                Dictionary<int, Transform> mapping = TryMappingHumanoidLikeReflective(editable.transform);
                if (mapping == null || mapping.Count == 0)
                {
                    mapping = BuildFallbackBoneMapping(editable.transform, includeExtendedNameAliases);
                }

                if (mapping == null || mapping.Count == 0)
                {
                    error = $"Failed to create humanoid mapping for {rootObject.name}.";
                    return null;
                }

                HumanBone[] humanBones = mapping
                    .Select(pair => CreateHumanBone(pair.Key, pair.Value))
                    .Where(h => !string.IsNullOrWhiteSpace(h.boneName))
                    .ToArray();

                if (humanBones.Length == 0)
                {
                    error = "No valid human bones mapped.";
                    return null;
                }

                SkeletonBone[] skeletonBones = editable.GetComponentsInChildren<Transform>(true)
                    .Select(t => new SkeletonBone
                    {
                        name = t.name,
                        position = t.localPosition,
                        rotation = t.localRotation,
                        scale = t.localScale
                    })
                    .ToArray();

                HumanDescription humanDescription = new HumanDescription
                {
                    upperArmTwist = 1f,
                    lowerArmTwist = 0f,
                    upperLegTwist = 1f,
                    lowerLegTwist = 0f,
                    armStretch = 0f,
                    legStretch = 0f,
                    feetSpacing = 0f,
                    hasTranslationDoF = false,
                    human = humanBones,
                    skeleton = skeletonBones
                };

                Avatar generated = AvatarBuilder.BuildHumanAvatar(editable, humanDescription);
                if (generated == null || !generated.isValid || !generated.isHuman)
                {
                    error = "AvatarBuilder.BuildHumanAvatar returned invalid avatar.";
                    return null;
                }

                string suffix = string.IsNullOrWhiteSpace(avatarNameSuffix) ? "Humanoid" : avatarNameSuffix.Trim();
                generated.name = $"{rootObject.name}_{suffix}";
                return generated;
            }
            catch (Exception e)
            {
                error = $"GenerateHumanoidAvatar failed: {e.Message}";
                return null;
            }
            finally
            {
                if (editable != null)
                {
                    UnityEngine.Object.DestroyImmediate(editable);
                }

                if (normalizeSourceTransformBeforeClone)
                {
                    sourceRoot.transform.position = oldPos;
                    sourceRoot.transform.rotation = oldRot;
                }
            }
        }

        private static bool TryGetModelImporter(GameObject gameObject, out ModelImporter importer, out string modelImporterPath)
        {
            importer = null;
            modelImporterPath = string.Empty;
            if (gameObject == null)
            {
                return false;
            }

            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            if (string.IsNullOrEmpty(prefabPath))
            {
                return false;
            }

            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                return false;
            }

            PrefabAssetType prefabAssetType = PrefabUtility.GetPrefabAssetType(prefabAsset);
            if (prefabAssetType == PrefabAssetType.Variant)
            {
                GameObject parentVariant = PrefabUtility.GetCorrespondingObjectFromSource(prefabAsset);
                if (parentVariant == null)
                {
                    return false;
                }

                string parentPath = AssetDatabase.GetAssetPath(parentVariant);
                modelImporterPath = parentPath;
                importer = AssetImporter.GetAtPath(parentPath) as ModelImporter;
                return importer != null;
            }

            modelImporterPath = prefabPath;
            importer = AssetImporter.GetAtPath(prefabPath) as ModelImporter;
            return importer != null;
        }

        private static HumanBone CreateHumanBone(int humanBoneId, Transform bone)
        {
            if (bone == null || humanBoneId < 0 || humanBoneId >= HumanTrait.BoneCount)
            {
                return default;
            }

            HumanBone hb = new HumanBone
            {
                boneName = bone.name,
                humanName = HumanTrait.BoneName[humanBoneId]
            };
            hb.limit.useDefaultValues = true;
            return hb;
        }

        private static void TryForceTPoseReflective(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            try
            {
                Type setupTool = GetAvatarSetupToolType();
                if (setupTool == null)
                {
                    return;
                }

                MethodInfo forceTPose = setupTool.GetMethod(
                    "ForceTPose",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(GameObject), typeof(Transform) },
                    null);
                forceTPose?.Invoke(null, new object[] { root, root.transform });
            }
            catch
            {
                // fallback mapping still applies
            }
        }

        private static Dictionary<int, Transform> TryMappingHumanoidLikeReflective(Transform root)
        {
            if (root == null)
            {
                return null;
            }

            try
            {
                Type setupTool = GetAvatarSetupToolType();
                if (setupTool == null)
                {
                    return null;
                }

                MethodInfo mappingMethod = setupTool.GetMethod(
                    "MappingHumanoidLike",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(Transform) },
                    null);
                if (mappingMethod == null)
                {
                    return null;
                }

                object result = mappingMethod.Invoke(null, new object[] { root });
                if (result is IDictionary dictionary)
                {
                    var output = new Dictionary<int, Transform>();
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Key is int key && entry.Value is Transform value && !output.ContainsKey(key))
                        {
                            output[key] = value;
                        }
                    }
                    return output;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static Type GetAvatarSetupToolType()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type t = assemblies[i].GetType("UnityEditor.AvatarSetupTool");
                if (t != null)
                {
                    return t;
                }
            }
            return null;
        }

        private static Dictionary<int, Transform> BuildFallbackBoneMapping(Transform root, bool includeExtendedNameAliases)
        {
            var output = new Dictionary<int, Transform>();
            var used = new HashSet<Transform>();
            Dictionary<string, Transform> names = BuildNameLookup(root);

            AddBone(output, used, HumanBodyBones.Hips, names, "Hips", "Pelvis", "hip", "hips", "pelvis");
            AddBone(output, used, HumanBodyBones.Spine, names, "Spine", "Spine1", "spine", "spine1");
            AddBone(output, used, HumanBodyBones.Chest, names, "Chest", "Spine2", "Spine3", "chest", "spine2", "spine3");
            if (includeExtendedNameAliases)
            {
                AddBone(output, used, HumanBodyBones.UpperChest, names, "UpperChest", "upperchest");
            }
            AddBone(output, used, HumanBodyBones.Neck, names, "Neck", "Neck1", "neck", "neck1");
            AddBone(output, used, HumanBodyBones.Head, names, "Head", "head");

            AddBone(output, used, HumanBodyBones.LeftShoulder, names, "LeftShoulder", "L_Clavicle", "Shoulder_L", "leftshoulder", "l_clavicle");
            AddBone(output, used, HumanBodyBones.LeftUpperArm, names, "LeftArm", "LeftUpperArm", "L_Shoulder", "upperarm_l", "leftarm", "l_shoulder");
            AddBone(output, used, HumanBodyBones.LeftLowerArm, names, "LeftForeArm", "LeftLowerArm", "L_Elbow", "lowerarm_l", "leftforearm", "l_elbow");
            AddBone(output, used, HumanBodyBones.LeftHand, names, "LeftHand", "L_Hand", "hand_l", "lefthand", "l_hand");

            AddBone(output, used, HumanBodyBones.RightShoulder, names, "RightShoulder", "R_Clavicle", "Shoulder_R", "rightshoulder", "r_clavicle");
            AddBone(output, used, HumanBodyBones.RightUpperArm, names, "RightArm", "RightUpperArm", "R_Shoulder", "upperarm_r", "rightarm", "r_shoulder");
            AddBone(output, used, HumanBodyBones.RightLowerArm, names, "RightForeArm", "RightLowerArm", "R_Elbow", "lowerarm_r", "rightforearm", "r_elbow");
            AddBone(output, used, HumanBodyBones.RightHand, names, "RightHand", "R_Hand", "hand_r", "righthand", "r_hand");

            AddBone(output, used, HumanBodyBones.LeftUpperLeg, names, "LeftUpLeg", "LeftLeg", "L_Hip", "thigh_l", "leftupleg", "leftleg", "l_hip");
            AddBone(output, used, HumanBodyBones.LeftLowerLeg, names, "LeftShin", "L_Knee", "calf_l", "leftshin", "l_knee");
            AddBone(output, used, HumanBodyBones.LeftFoot, names, "LeftFoot", "L_Foot", "foot_l", "leftfoot", "l_foot");
            AddBone(output, used, HumanBodyBones.LeftToes, names, "LeftToeBase", "L_Toes", "toe_l", "lefttoebase", "l_toes");
            AddLegChainFallback(output, used, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes);

            AddBone(output, used, HumanBodyBones.RightUpperLeg, names, "RightUpLeg", "RightLeg", "R_Hip", "thigh_r", "rightupleg", "rightleg", "r_hip");
            AddBone(output, used, HumanBodyBones.RightLowerLeg, names, "RightShin", "R_Knee", "calf_r", "rightshin", "r_knee");
            AddBone(output, used, HumanBodyBones.RightFoot, names, "RightFoot", "R_Foot", "foot_r", "rightfoot", "r_foot");
            AddBone(output, used, HumanBodyBones.RightToes, names, "RightToeBase", "R_Toes", "toe_r", "righttoebase", "r_toes");
            AddLegChainFallback(output, used, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.RightToes);

            return output;
        }

        private static Dictionary<string, Transform> BuildNameLookup(Transform root)
        {
            var dict = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            if (root == null)
            {
                return dict;
            }

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (!dict.ContainsKey(all[i].name))
                {
                    dict[all[i].name] = all[i];
                }
            }
            return dict;
        }

        private static void AddBone(
            Dictionary<int, Transform> output,
            HashSet<Transform> used,
            HumanBodyBones bone,
            Dictionary<string, Transform> names,
            params string[] candidates)
        {
            int id = (int)bone;
            if (id < 0 || id >= HumanTrait.BoneCount || output.ContainsKey(id))
            {
                return;
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                string candidate = candidates[i];
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (names.TryGetValue(candidate, out Transform t) && t != null && !used.Contains(t))
                {
                    output[id] = t;
                    used.Add(t);
                    return;
                }
            }
        }

        private static void AddLegChainFallback(
            Dictionary<int, Transform> output,
            HashSet<Transform> used,
            HumanBodyBones upperBone,
            HumanBodyBones lowerBone,
            HumanBodyBones footBone,
            HumanBodyBones toeBone)
        {
            int upperId = (int)upperBone;
            int lowerId = (int)lowerBone;
            int footId = (int)footBone;
            int toeId = (int)toeBone;

            if (!output.TryGetValue(upperId, out Transform upper) || upper == null)
            {
                return;
            }

            if (!output.ContainsKey(lowerId))
            {
                Transform lower = FindFirstUnusedDescendant(upper, used, "knee", "shin", "calf", "lower", "leg");
                if (lower != null)
                {
                    output[lowerId] = lower;
                    used.Add(lower);
                }
            }

            if (output.TryGetValue(lowerId, out Transform lowerAssigned) && lowerAssigned != null && !output.ContainsKey(footId))
            {
                Transform foot = FindFirstUnusedDescendant(lowerAssigned, used, "foot", "ankle");
                if (foot != null)
                {
                    output[footId] = foot;
                    used.Add(foot);
                }
            }

            if (output.TryGetValue(footId, out Transform footAssigned) && footAssigned != null && !output.ContainsKey(toeId))
            {
                Transform toe = FindFirstUnusedDescendant(footAssigned, used, "toe");
                if (toe != null)
                {
                    output[toeId] = toe;
                    used.Add(toe);
                }
            }
        }

        private static Transform FindFirstUnusedDescendant(Transform root, HashSet<Transform> used, params string[] keywords)
        {
            if (root == null)
            {
                return null;
            }

            var queue = new Queue<Transform>();
            for (int i = 0; i < root.childCount; i++)
            {
                queue.Enqueue(root.GetChild(i));
            }

            while (queue.Count > 0)
            {
                Transform current = queue.Dequeue();
                if (!used.Contains(current))
                {
                    string n = current.name.ToLowerInvariant();
                    for (int k = 0; k < keywords.Length; k++)
                    {
                        if (n.Contains(keywords[k]))
                        {
                            return current;
                        }
                    }
                }

                for (int i = 0; i < current.childCount; i++)
                {
                    queue.Enqueue(current.GetChild(i));
                }
            }

            return null;
        }
    }
}