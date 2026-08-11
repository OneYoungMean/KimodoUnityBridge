using System;
using UnityEditor;
using UnityEngine;
namespace TimelineInject
{
    public static class AvatarSetupToolExtension
    {
        public static bool TryLoadImporterAvatar(GameObject avatarRoot, out Avatar avatar, out string modelImporterPath)
        {
            avatar = null;
            modelImporterPath = string.Empty;

            if (!TryGetModelImporter(avatarRoot, out _, out modelImporterPath))
            {
                return false;
            }

            avatar = AssetDatabase.LoadAssetAtPath<Avatar>(modelImporterPath);
            return avatar != null;
        }

        public static Avatar AutoGenerateHumanoidAvatarFromModelOrThrow(GameObject avatarRoot, bool forceReimport)
        {

            if (avatarRoot == null)
            {
                throw new InvalidOperationException("Avatar root object is null.");
            }

            if (!TryGetModelImporter(avatarRoot, out ModelImporter importer, out string modelImporterPath))
            {
                throw new InvalidOperationException("Cannot resolve ModelImporter from avatar root.");
            }

            if (importer == null)
            {
                throw new InvalidOperationException("ModelImporter is null.");
            }

            SerializedObject importerSo = new SerializedObject(importer);
            SerializedProperty animationTypeProp = importerSo.FindProperty("m_AnimationType");
            SerializedProperty humanBoneArrayProp = importerSo.FindProperty("m_HumanDescription.m_Human");
            SerializedProperty humanSkeletonArrayProp = importerSo.FindProperty("m_HumanDescription.m_Skeleton");
            if (animationTypeProp == null)
            {
                throw new InvalidOperationException("Cannot find ModelImporter property: m_AnimationType");
            }
            if (humanBoneArrayProp == null || humanSkeletonArrayProp == null)
            {
                throw new InvalidOperationException("Cannot find ModelImporter human description properties.");
            }

            ImportAssetOptions importOptions = forceReimport ? ImportAssetOptions.ForceUpdate : ImportAssetOptions.Default;

            // Step 1: reset avatar-related import settings.
            animationTypeProp.intValue = 2;
            AvatarSetupTool.ClearAll(humanBoneArrayProp, humanSkeletonArrayProp);
            importerSo.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.ImportAsset(modelImporterPath, importOptions);

            // Step 2: enable humanoid creation and let importer generate avatar on import.
            animationTypeProp.intValue = 3;
            importerSo.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.ImportAsset(modelImporterPath, importOptions);

            Avatar avatar = AssetDatabase.LoadAssetAtPath<Avatar>(modelImporterPath);
            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                throw new InvalidOperationException("Importer avatar is null or invalid after humanoid auto-setup import chain.");
            }

            return avatar;
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
                return TryGetModelImporterFromFirstMeshAsset(gameObject, out importer, out modelImporterPath);
            }

            GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                return TryGetModelImporterFromFirstMeshAsset(gameObject, out importer, out modelImporterPath);
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
            if (importer != null)
            {
                return true;
            }

            return TryGetModelImporterFromFirstMeshAsset(gameObject, out importer, out modelImporterPath);
        }

        private static bool TryGetModelImporterFromFirstMeshAsset(GameObject gameObject, out ModelImporter importer, out string modelImporterPath)
        {
            importer = null;
            modelImporterPath = string.Empty;
            if (gameObject == null)
            {
                return false;
            }

            if (!TryGetFirstMeshCarrier(gameObject, out Transform meshCarrier))
            {
                return false;
            }

            GameObject current = meshCarrier.gameObject;
            while (current != null)
            {
                string candidatePath = AssetDatabase.GetAssetPath(current);
                if (!string.IsNullOrEmpty(candidatePath))
                {
                    importer = AssetImporter.GetAtPath(candidatePath) as ModelImporter;
                    if (importer != null)
                    {
                        modelImporterPath = candidatePath;
                        return true;
                    }
                }

                current = current.transform.parent != null ? current.transform.parent.gameObject : null;
            }

            if (TryGetMeshAssetPath(meshCarrier.gameObject, out string meshAssetPath))
            {
                importer = AssetImporter.GetAtPath(meshAssetPath) as ModelImporter;
                if (importer != null)
                {
                    modelImporterPath = meshAssetPath;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetFirstMeshCarrier(GameObject root, out Transform carrier)
        {
            carrier = null;
            if (root == null)
            {
                return false;
            }

            SkinnedMeshRenderer[] skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skins.Length; i++)
            {
                if (skins[i] != null && skins[i].sharedMesh != null)
                {
                    carrier = skins[i].transform;
                    return true;
                }
            }

            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i] != null && filters[i].sharedMesh != null)
                {
                    carrier = filters[i].transform;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetMeshAssetPath(GameObject meshCarrier, out string meshAssetPath)
        {
            meshAssetPath = string.Empty;
            if (meshCarrier == null)
            {
                return false;
            }

            SkinnedMeshRenderer skin = meshCarrier.GetComponent<SkinnedMeshRenderer>();
            if (skin != null && skin.sharedMesh != null)
            {
                string path = AssetDatabase.GetAssetPath(skin.sharedMesh);
                if (!string.IsNullOrEmpty(path))
                {
                    meshAssetPath = path;
                    return true;
                }
            }

            MeshFilter filter = meshCarrier.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
            {
                string path = AssetDatabase.GetAssetPath(filter.sharedMesh);
                if (!string.IsNullOrEmpty(path))
                {
                    meshAssetPath = path;
                    return true;
                }
            }

            return false;
        }
    }
}
