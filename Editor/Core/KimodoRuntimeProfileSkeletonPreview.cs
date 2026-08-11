using System.Collections.Generic;
using TimelineInject;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    [InitializeOnLoad]
    internal static class KimodoRuntimeProfileSkeletonPreview
    {
        private const string Soma77Guid = "138d6af561fb0f444b376fcf343f7217";
        private const string Core27Guid = "6e1c47532d2e35c4084a31676521099c";
        private const string G1Guid = "cc09b26154e1aac448af3abae05da792";
        private const string SmplxGuid = "d1515f7acb0ea8d49b6ce2db95af38a7";

        private sealed class PreviewEntry
        {
            public string ModelName;
            public GameObject Instance;
            public Transform[] SourceJoints;
            public Transform[] PreviewJoints;
        }

        private static readonly Dictionary<int, PreviewEntry> Entries = new Dictionary<int, PreviewEntry>();
        private static readonly HashSet<int> ActiveDriverIds = new HashSet<int>();
        private static readonly List<int> StaleDriverIds = new List<int>();

        static KimodoRuntimeProfileSkeletonPreview()
        {
            EditorApplication.update += Update;
            AssemblyReloadEvents.beforeAssemblyReload += DestroyAll;
            EditorApplication.playModeStateChanged += _ => DestroyAll();
        }

        private static void Update()
        {
            ActiveDriverIds.Clear();
            if (Application.isPlaying)
            {
                KimodoRuntimeMotionDriver[] drivers =
                    Resources.FindObjectsOfTypeAll<KimodoRuntimeMotionDriver>();
                for (int i = 0; i < drivers.Length; i++)
                {
                    KimodoRuntimeMotionDriver driver = drivers[i];
                    if (driver == null || EditorUtility.IsPersistent(driver) ||
                        !driver.isActiveAndEnabled || !driver.DrawDebugSkeleton)
                    {
                        continue;
                    }

                    int id = KimodoUnityObjectIdUtility.IdHash(driver);
                    ActiveDriverIds.Add(id);
                    UpdateDriver(id, driver);
                }
            }

            StaleDriverIds.Clear();
            foreach (KeyValuePair<int, PreviewEntry> pair in Entries)
            {
                if (!ActiveDriverIds.Contains(pair.Key))
                {
                    StaleDriverIds.Add(pair.Key);
                }
            }
            for (int i = 0; i < StaleDriverIds.Count; i++)
            {
                DestroyEntry(StaleDriverIds[i]);
            }
        }

        private static void UpdateDriver(int id, KimodoRuntimeMotionDriver driver)
        {
            Transform sourceRoot = driver.DebugProfileSkeletonRoot;
            string modelName = KimodoPlayableClip.NormalizeBridgeModelName(driver.DebugModelName);
            if (sourceRoot == null)
            {
                DestroyEntry(id);
                return;
            }

            if (!Entries.TryGetValue(id, out PreviewEntry entry) ||
                entry == null || entry.Instance == null || entry.ModelName != modelName)
            {
                DestroyEntry(id);
                entry = CreateEntry(driver, sourceRoot, modelName);
                if (entry == null)
                {
                    return;
                }
                Entries[id] = entry;
            }

            int count = Mathf.Min(entry.SourceJoints.Length, entry.PreviewJoints.Length);
            for (int i = 0; i < count; i++)
            {
                Transform source = entry.SourceJoints[i];
                Transform preview = entry.PreviewJoints[i];
                if (source == null || preview == null)
                {
                    continue;
                }
                preview.localPosition = source.localPosition;
                preview.localRotation = source.localRotation;
                preview.localScale = source.localScale;
            }

            entry.Instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        }

        private static PreviewEntry CreateEntry(
            KimodoRuntimeMotionDriver driver,
            Transform sourceRoot,
            string modelName)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(ResolveModelGuid(modelName));
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (model == null)
            {
                Debug.LogWarning($"[KimodoRuntimeMotionDriver] Debug profile model not found for '{modelName}'.", driver);
                return null;
            }

            GameObject instance = Object.Instantiate(model);
            instance.name = $"KimodoDebugProfile_{driver.name}_{model.name}";
            SetHideFlags(instance.transform, HideFlags.HideAndDontSave);
            Animator animator = instance.GetComponentInChildren<Animator>(true);
            if (animator != null)
            {
                animator.enabled = false;
            }

            bool sourceResolved = KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                modelName,
                sourceRoot,
                out _,
                out _,
                out Transform[] sourceJoints,
                out string sourceError);
            bool previewResolved = KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                modelName,
                instance.transform,
                out _,
                out _,
                out Transform[] previewJoints,
                out string previewError);
            if (!sourceResolved || !previewResolved)
            {
                Object.DestroyImmediate(instance);
                Debug.LogWarning(
                    $"[KimodoRuntimeMotionDriver] Debug profile skeleton mapping failed: " +
                    $"{sourceError}{previewError}",
                    driver);
                return null;
            }

            return new PreviewEntry
            {
                ModelName = modelName,
                Instance = instance,
                SourceJoints = sourceJoints,
                PreviewJoints = previewJoints
            };
        }

        private static string ResolveModelGuid(string modelName)
        {
            return KimodoRigProfileDatabase.ResolveRigTypeFromModelName(modelName) switch
            {
                KimodoConstraintRigType.Core27 => Core27Guid,
                KimodoConstraintRigType.G1 => G1Guid,
                KimodoConstraintRigType.Smplx => SmplxGuid,
                _ => Soma77Guid
            };
        }

        private static void SetHideFlags(Transform root, HideFlags flags)
        {
            if (root == null)
            {
                return;
            }
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i].gameObject.hideFlags = flags;
            }
        }

        private static void DestroyEntry(int id)
        {
            if (!Entries.TryGetValue(id, out PreviewEntry entry))
            {
                return;
            }
            if (entry?.Instance != null)
            {
                Object.DestroyImmediate(entry.Instance);
            }
            Entries.Remove(id);
        }

        private static void DestroyAll()
        {
            foreach (PreviewEntry entry in Entries.Values)
            {
                if (entry?.Instance != null)
                {
                    Object.DestroyImmediate(entry.Instance);
                }
            }
            Entries.Clear();
        }
    }
}
