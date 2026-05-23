using System;
using UnityEditor;
using UnityEngine;
using AnimationUtility = UnityEditor.AnimationUtility;
public static class AvatarSetupToolExtension
{
    
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
}
