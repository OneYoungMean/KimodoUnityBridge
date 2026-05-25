using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    [InitializeOnLoad]
    internal static class KimodoConstraintSnapshotVisualizer
    {
        private const double RebuildDebounceSeconds = 0.05;
        private const string LogPrefix = "[Kimodo][ConstraintSnapshot]";

        private static readonly Dictionary<string, RigCacheEntry> RigCache = new Dictionary<string, RigCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, SnapshotCacheEntry> SnapshotCache = new Dictionary<int, SnapshotCacheEntry>();
        private static readonly List<SnapshotRenderEntry> ActiveRenders = new List<SnapshotRenderEntry>();

        private static bool dirty = true;
        private static bool hooksReady;
        private static double rebuildAfterTime;
        private static int lastSelectionHash;
        private static int lastMarkerStateHash;

        static KimodoConstraintSnapshotVisualizer()
        {
            EnsureHooks();
            MarkDirty();
        }

        private static void EnsureHooks()
        {
            if (hooksReady)
            {
                return;
            }

            hooksReady = true;
            Selection.selectionChanged += OnSelectionChanged;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
            EditorApplication.update += OnEditorUpdate;
            SceneView.duringSceneGui += OnSceneGui;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        private static void OnSelectionChanged()
        {
            if (CollectSelectedMarkers().Count == 0)
            {
                HideAllRigsAndClearActiveRenders();
                SceneView.RepaintAll();
            }
            MarkDirty();
        }

        private static void OnUndoRedoPerformed()
        {
            MarkDirty();
        }

        private static void OnPlayModeChanged(PlayModeStateChange _)
        {
            MarkDirty();
        }

        private static void OnBeforeAssemblyReload()
        {
            ClearAllCaches();
        }

        private static void MarkDirty()
        {
            dirty = true;
            rebuildAfterTime = EditorApplication.timeSinceStartup + RebuildDebounceSeconds;
        }

        internal static void RequestManualRefresh()
        {
            MarkDirty();
            SceneView.RepaintAll();
        }

        private static void OnEditorUpdate()
        {
            if (!dirty)
            {
                if (SelectionHash() != lastSelectionHash)
                {
                    MarkDirty();
                }
                else if (ComputeMarkerStateHash() != lastMarkerStateHash)
                {
                    MarkDirty();
                }
                return;
            }

            if (EditorApplication.timeSinceStartup < rebuildAfterTime)
            {
                return;
            }

            RebuildSnapshots();
            dirty = false;
            SceneView.RepaintAll();
        }

        private static int SelectionHash()
        {
            unchecked
            {
                int hash = 17;
                UnityEngine.Object[] selected = Selection.objects;
                for (int i = 0; i < selected.Length; i++)
                {
                    hash = hash * 31 + (selected[i] != null ? selected[i].GetInstanceID() : 0);
                }

                return hash;
            }
        }

        private static int ComputeMarkerStateHash()
        {
            unchecked
            {
                int hash = 41;
                List<KimodoConstraintMarkerBase> markers = CollectSelectedMarkers();
                for (int i = 0; i < markers.Count; i++)
                {
                    KimodoConstraintMarkerBase marker = markers[i];
                    if (marker == null)
                    {
                        continue;
                    }

                    hash = hash * 31 + marker.GetInstanceID();
                    hash = hash * 31 + marker.time.GetHashCode();
                    if (KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out TimelineClip clipRange) && clipRange != null)
                    {
                        hash = hash * 31 + clipRange.start.GetHashCode();
                        hash = hash * 31 + clipRange.end.GetHashCode();
                        hash = hash * 31 + clipRange.duration.GetHashCode();
                    }
                }
                return hash;
            }
        }

        private static void RebuildSnapshots()
        {
            lastSelectionHash = SelectionHash();
            lastMarkerStateHash = ComputeMarkerStateHash();
            SetAllRigVisibility(false);
            ActiveRenders.Clear();

            List<KimodoConstraintMarkerBase> markers = CollectSelectedMarkers();
            if (markers.Count == 0)
            {
                return;
            }

            for (int i = 0; i < markers.Count; i++)
            {
                KimodoConstraintMarkerBase marker = markers[i];
                if (!TryBuildRenderEntry(marker, out SnapshotRenderEntry entry, out string error))
                {
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        Debug.LogWarning($"{LogPrefix} Skip marker '{marker?.name}': {error}");
                    }
                    continue;
                }

                ActiveRenders.Add(entry);
            }
        }

        private static List<KimodoConstraintMarkerBase> CollectSelectedMarkers()
        {
            var result = new List<KimodoConstraintMarkerBase>();
            UnityEngine.Object[] selected = Selection.objects;
            for (int i = 0; i < selected.Length; i++)
            {
                if (selected[i] is KimodoConstraintMarkerBase marker)
                {
                    result.Add(marker);
                }
            }
            return result;
        }

        private static bool TryBuildRenderEntry(
            KimodoConstraintMarkerBase marker,
            out SnapshotRenderEntry renderEntry,
            out string error)
        {
            renderEntry = default;
            error = string.Empty;

            if (marker == null)
            {
                error = "marker is null";
                return false;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out TimelineClip clipRange) || clipRange == null)
            {
                error = "clip range not found";
                return false;
            }

            if (!(clipRange.asset is KimodoPlayableClip playableClip))
            {
                error = "clip asset is not KimodoPlayableClip";
                return false;
            }

            TrackAsset track = clipRange.GetParentTrack();
            if (track == null && marker.parent is TrackAsset markerTrack)
            {
                track = markerTrack;
            }
            if (track == null)
            {
                error = "parent track not found";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null";
                return false;
            }

            if (!TryResolveBoundAnimatorForTrack(director, track, out Animator boundAnimator, out string bindError))
            {
                error = bindError;
                return false;
            }

            SkeletonPreviewRigType rigType = ResolveRigTypeFromModel(playableClip.bridgeModelName);
            RigCacheEntry rig = GetOrCreateRig(rigType);
            if (rig.Root == null || rig.Transforms == null || rig.Transforms.Length == 0)
            {
                error = "preview rig unavailable";
                return false;
            }

            int cacheKey = BuildSnapshotKey(marker, clipRange, boundAnimator, playableClip.bridgeModelName, rigType);
            if (!SnapshotCache.TryGetValue(cacheKey, out SnapshotCacheEntry cache) || !cache.IsValid)
            {
                if (!TryBuildSnapshot(marker, clipRange, boundAnimator, rigType, rig, out cache, out error))
                {
                    return false;
                }
                SnapshotCache[cacheKey] = cache;
            }

            if (!ApplySnapshotToRig(cache, rig))
            {
                error = "failed to apply snapshot to rig";
                return false;
            }

            if (rig.Root != null && !rig.Root.gameObject.activeSelf)
            {
                rig.Root.gameObject.SetActive(true);
            }

            renderEntry = new SnapshotRenderEntry
            {
                Marker = marker,
                ClipRange = clipRange,
                RigType = rigType,
                RigKey = rig.Key,
                FrameIndex = cache.FrameIndex
            };

            return true;
        }

        private static bool TryResolveBoundAnimatorForTrack(
            PlayableDirector director,
            TrackAsset track,
            out Animator animator,
            out string error)
        {
            animator = null;
            error = string.Empty;

            if (director == null)
            {
                error = "Timeline inspected director is null";
                return false;
            }

            if (track == null)
            {
                error = "track is null";
                return false;
            }

            // 1) Prefer the binding directly on this clip's own track.
            animator = director.GetGenericBinding(track) as Animator;
            if (animator != null && animator.transform != null)
            {
                return true;
            }

            // 2) Fallback to parent track bindings only when self track has no binding.
            TrackAsset current = track.parent as TrackAsset;
            while (current != null)
            {
                animator = director.GetGenericBinding(current) as Animator;
                if (animator != null && animator.transform != null)
                {
                    return true;
                }

                current = current.parent as TrackAsset;
            }

            error = "track has no animator binding on self track or parent tracks";
            return false;
        }

        private static int BuildSnapshotKey(
            KimodoConstraintMarkerBase marker,
            TimelineClip clipRange,
            Animator boundAnimator,
            string modelName,
            SkeletonPreviewRigType rigType)
        {
            unchecked
            {
                int hash = 23;
                hash = hash * 31 + marker.GetInstanceID();
                hash = hash * 31 + (clipRange.asset != null ? clipRange.asset.GetInstanceID() : 0);
                hash = hash * 31 + (boundAnimator != null ? boundAnimator.GetInstanceID() : 0);
                hash = hash * 31 + (int)rigType;
                hash = hash * 31 + StableStringHash(modelName ?? string.Empty);
                hash = hash * 31 + StableStringHash(BuildMarkerDigest(marker));
                hash = hash * 31 + clipRange.start.GetHashCode();
                hash = hash * 31 + clipRange.end.GetHashCode();
                hash = hash * 31 + marker.time.GetHashCode();
                return hash;
            }
        }

        private static int StableStringHash(string s)
        {
            unchecked
            {
                int hash = 5381;
                for (int i = 0; i < s.Length; i++)
                {
                    hash = ((hash << 5) + hash) ^ s[i];
                }
                return hash;
            }
        }

        private static string BuildMarkerDigest(KimodoConstraintMarkerBase marker)
        {
            if (marker == null)
            {
                return string.Empty;
            }

            try
            {
                SerializedObject so = new SerializedObject(marker);
                return JsonUtility.ToJson(new MarkerDigestWrapper
                {
                    markerType = marker.ConstraintType ?? string.Empty,
                    markerTime = marker.time,
                    useOverride = so.FindProperty("useOverride")?.boolValue ?? false,
                    serializedStamp = marker.GetHashCode()
                });
            }
            catch
            {
                return marker.name + "|" + marker.time.ToString("F6");
            }
        }

        private static bool TryBuildSnapshot(
            KimodoConstraintMarkerBase marker,
            TimelineClip clipRange,
            Animator boundAnimator,
            SkeletonPreviewRigType rigType,
            RigCacheEntry rig,
            out SnapshotCacheEntry snapshot,
            out string error)
        {
            snapshot = default;
            error = string.Empty;

            int frameIndex = KimodoConstraintMarkerEditorUtility.TimeToKimodoFrameIndex(clipRange, marker.time);
            if (TryBuildRetargetedSnapshotFromTimeline(marker, clipRange, boundAnimator, rig, frameIndex, out snapshot))
            {
                return true;
            }

            KimodoMarkerSampleResult unityPose;

            if (!TryResolveUnityPoseForMarker(marker, clipRange, boundAnimator, frameIndex, out unityPose, out error))
            {
                return false;
            }

            if (unityPose == null || unityPose.localAxisAngles == null || unityPose.localAxisAngles.Count == 0)
            {
                error = "unity pose is empty";
                return false;
            }

            if (!TryBuildLocalPoseArrays(unityPose, rig.Transforms.Length, out Vector3 rootPos, out Quaternion[] localRotations))
            {
                error = "failed to build local pose arrays";
                return false;
            }

            snapshot = new SnapshotCacheEntry
            {
                IsValid = true,
                FrameIndex = frameIndex,
                RootPosition = rootPos,
                LocalRotations = localRotations
            };
            return true;
        }

        private static bool TryBuildRetargetedSnapshotFromTimeline(
            KimodoConstraintMarkerBase marker,
            TimelineClip clipRange,
            Animator boundAnimator,
            RigCacheEntry rig,
            int frameIndex,
            out SnapshotCacheEntry snapshot)
        {
            snapshot = default;
            if (marker == null || clipRange == null || boundAnimator == null || !rig.IsAlive || rig.Animator == null)
            {
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                return false;
            }

            if (!KimodoLocalAvatarUtility.TryEnsureHumanoidAvatar(boundAnimator, out Avatar sourceAvatar, out _, out _))
            {
                return false;
            }

            if (!KimodoLocalAvatarUtility.TryEnsureHumanoidAvatar(rig.Animator, out Avatar targetAvatar, out _, out _))
            {
                return false;
            }

            if (sourceAvatar == null || targetAvatar == null)
            {
                return false;
            }

            double originalTime = director.time;
            DirectorWrapMode originalWrap = director.extrapolationMode;
            try
            {
                director.extrapolationMode = DirectorWrapMode.Hold;
                director.time = marker.time;
                director.Evaluate();

                var srcPoseHandler = new HumanPoseHandler(sourceAvatar, boundAnimator.transform);
                var dstPoseHandler = new HumanPoseHandler(targetAvatar, rig.Animator.transform);
                try
                {
                    var pose = new HumanPose();
                    srcPoseHandler.GetHumanPose(ref pose);
                    dstPoseHandler.SetHumanPose(ref pose);
                }
                finally
                {
                    srcPoseHandler.Dispose();
                    dstPoseHandler.Dispose();
                }

                Transform[] ts = rig.Transforms;
                if (ts == null || ts.Length == 0)
                {
                    return false;
                }

                Quaternion[] rots = new Quaternion[ts.Length];
                for (int i = 0; i < ts.Length; i++)
                {
                    rots[i] = ts[i].localRotation;
                }

                snapshot = new SnapshotCacheEntry
                {
                    IsValid = true,
                    FrameIndex = frameIndex,
                    RootPosition = ts[0].position,
                    LocalRotations = rots
                };
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                director.time = originalTime;
                director.Evaluate();
                director.extrapolationMode = originalWrap;
            }
        }

        private static bool TryResolveUnityPoseForMarker(
            KimodoConstraintMarkerBase marker,
            TimelineClip clipRange,
            Animator boundAnimator,
            int frameIndex,
            out KimodoMarkerSampleResult unityPose,
            out string error)
        {
            unityPose = null;
            error = string.Empty;

            if (marker == null || clipRange == null || boundAnimator == null)
            {
                error = "invalid marker/clip/animator";
                return false;
            }

            bool isCustomEndEffector = marker is KimodoEndEffectorConstraintMarker ee &&
                                       string.Equals(ee.ConstraintType, "end-effector", StringComparison.OrdinalIgnoreCase);
            bool useOverride = marker.useOverride && !isCustomEndEffector;
            if (useOverride && TryBuildPoseFromOverride(marker, clipRange, out unityPose))
            {
                return true;
            }

            if (!KimodoConstraintExportUtility.TrySamplePoseFromClipAsset(
                    clipRange,
                    boundAnimator,
                    boundAnimator.transform,
                    marker.time,
                    frameIndex,
                    marker.ConstraintType,
                    out KimodoMarkerSampleResult kimodoPose,
                    out error))
            {
                return false;
            }

            unityPose = KimodoSpaceConversionUtility.ToUnitySample(kimodoPose);
            if (unityPose == null)
            {
                error = "Kimodo->Unity pose conversion failed";
                return false;
            }

            if (marker is KimodoRoot2DConstraintMarker root2D)
            {
                // root2d: keep sampled rotations, override root plane/heading.
                if (root2D.smoothRoot2D != null && root2D.smoothRoot2D.Count > 0)
                {
                    Vector2 r = root2D.smoothRoot2D[0];
                    unityPose.rootPosition = new Vector3(r.x, unityPose.rootPosition.y, r.y);
                }
            }

            return true;
        }

        private static bool TryBuildPoseFromOverride(
            KimodoConstraintMarkerBase marker,
            TimelineClip clipRange,
            out KimodoMarkerSampleResult unityPose)
        {
            unityPose = null;

            KimodoConstraintJson json = marker.ToJson();
            if (json == null || json.local_joints_rot == null || json.local_joints_rot.Count == 0)
            {
                return false;
            }

            int markerFrame = KimodoConstraintMarkerEditorUtility.TimeToKimodoFrameIndex(clipRange, marker.time);
            int idx = FindFrameIndex(json.frame_indices, markerFrame);
            if (idx < 0)
            {
                idx = 0;
            }

            if (idx >= json.local_joints_rot.Count)
            {
                idx = json.local_joints_rot.Count - 1;
            }

            if (idx < 0)
            {
                return false;
            }

            float[][] aa = json.local_joints_rot[idx];
            if (aa == null || aa.Length == 0)
            {
                return false;
            }

            Vector3 rootPos = Vector3.zero;
            if (json.root_positions != null && idx < json.root_positions.Count)
            {
                float[] rp = json.root_positions[idx];
                if (rp != null && rp.Length >= 3)
                {
                    rootPos = KimodoSpaceConversionUtility.ToUnityRootPosition(new Vector3(rp[0], rp[1], rp[2]));
                }
            }
            else if (json.smooth_root_2d != null && idx < json.smooth_root_2d.Count)
            {
                float[] r2 = json.smooth_root_2d[idx];
                if (r2 != null && r2.Length >= 2)
                {
                    Vector2 heading2D = KimodoSpaceConversionUtility.ToUnityHeading(new Vector2(r2[0], r2[1]));
                    rootPos = new Vector3(heading2D.x, 0f, heading2D.y);
                }
            }

            var local = new List<Vector3>(aa.Length);
            for (int i = 0; i < aa.Length; i++)
            {
                float[] v = aa[i];
                if (v == null || v.Length < 3)
                {
                    local.Add(Vector3.zero);
                    continue;
                }
                local.Add(KimodoSpaceConversionUtility.ToUnityAxisAngle(new Vector3(v[0], v[1], v[2])));
            }

            unityPose = new KimodoMarkerSampleResult
            {
                rootPosition = rootPos,
                rootHeading = Vector2.right,
                localAxisAngles = local
            };

            return true;
        }

        private static int FindFrameIndex(List<int> frames, int frame)
        {
            if (frames == null || frames.Count == 0)
            {
                return -1;
            }

            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i] == frame)
                {
                    return i;
                }
            }
            return -1;
        }

        private static bool TryBuildLocalPoseArrays(
            KimodoMarkerSampleResult unityPose,
            int rigJointCount,
            out Vector3 rootPosition,
            out Quaternion[] localRotations)
        {
            rootPosition = unityPose.rootPosition;
            localRotations = new Quaternion[rigJointCount];

            for (int i = 0; i < localRotations.Length; i++)
            {
                localRotations[i] = Quaternion.identity;
            }

            int count = Mathf.Min(rigJointCount, unityPose.localAxisAngles.Count);
            for (int i = 0; i < count; i++)
            {
                Vector3 aa = unityPose.localAxisAngles[i];
                float angleRad = aa.magnitude;
                if (angleRad <= 1e-8f)
                {
                    localRotations[i] = Quaternion.identity;
                    continue;
                }
                Vector3 axis = aa / angleRad;
                localRotations[i] = Quaternion.AngleAxis(angleRad * Mathf.Rad2Deg, axis);
            }

            return true;
        }

        private static RigCacheEntry GetOrCreateRig(SkeletonPreviewRigType rigType)
        {
            string key = rigType.ToString();
            if (RigCache.TryGetValue(key, out RigCacheEntry entry) && entry.IsAlive)
            {
                return entry;
            }

            RigCacheEntry created = CreateRig(rigType);
            if (created.IsAlive)
            {
                RigCache[key] = created;
            }
            return created;
        }

        private static RigCacheEntry CreateRig(SkeletonPreviewRigType rigType)
        {
            GameObject prefab = LoadRigPrefab(rigType);
            if (prefab == null)
            {
                return default;
            }

            GameObject instance = UnityEngine.Object.Instantiate(prefab);
            instance.name = $"__KimodoSnapshot_{rigType}";
            instance.hideFlags = HideFlags.HideAndDontSave;
            instance.SetActive(false);

            Transform root = instance.transform;
            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            if (transforms == null || transforms.Length == 0)
            {
                UnityEngine.Object.DestroyImmediate(instance);
                return default;
            }

            Animator animator = instance.GetComponent<Animator>();
            if (animator == null)
            {
                animator = instance.AddComponent<Animator>();
            }

            return new RigCacheEntry
            {
                Key = rigType.ToString(),
                RigType = rigType,
                Root = root,
                Transforms = transforms,
                Animator = animator
            };
        }

        private static string ResolveRigModelPath(SkeletonPreviewRigType rigType)
        {
            string fileName = ResolveRigFileName(rigType);

            UnityEditor.PackageManager.PackageInfo packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(KimodoConstraintSnapshotVisualizer).Assembly);
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

            // Legacy relative path fallback for older local layouts.
            return $"Editor/Model/{fileName}";
        }

        private static GameObject LoadRigPrefab(SkeletonPreviewRigType rigType)
        {
            string path = ResolveRigModelPath(rigType);
            return AssetDatabase.LoadAssetAtPath<GameObject>(path);
        }

        private static string ResolveRigFileName(SkeletonPreviewRigType rigType)
        {
            switch (rigType)
            {
                case SkeletonPreviewRigType.Smplx:
                    return "SMPLX.fbx";
                case SkeletonPreviewRigType.G1:
                    return "G1.fbx";
                case SkeletonPreviewRigType.Soma30:
                default:
                    return "SOMA30.fbx";
            }
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return path.Replace('\\', '/').TrimEnd('/');
        }

        private static bool ApplySnapshotToRig(SnapshotCacheEntry snapshot, RigCacheEntry rig)
        {
            if (!snapshot.IsValid || !rig.IsAlive)
            {
                return false;
            }

            Transform[] t = rig.Transforms;
            if (t == null || t.Length == 0)
            {
                return false;
            }

            int rotCount = Mathf.Min(snapshot.LocalRotations != null ? snapshot.LocalRotations.Length : 0, t.Length);
            for (int i = 0; i < t.Length; i++)
            {
                t[i].localRotation = i < rotCount ? snapshot.LocalRotations[i] : Quaternion.identity;
            }

            t[0].position = snapshot.RootPosition;
            return true;
        }

        private static void OnSceneGui(SceneView sceneView)
        {
            // If no marker is selected now, clear stale renders immediately so gizmos disappear at once.
            if (CollectSelectedMarkers().Count == 0)
            {
                if (ActiveRenders.Count > 0)
                {
                    HideAllRigsAndClearActiveRenders();
                    SceneView.RepaintAll();
                }
                return;
            }

            if (ActiveRenders.Count == 0)
            {
                return;
            }

            for (int i = 0; i < ActiveRenders.Count; i++)
            {
                SnapshotRenderEntry entry = ActiveRenders[i];
                if (!RigCache.TryGetValue(entry.RigKey, out RigCacheEntry rig) || !rig.IsAlive)
                {
                    continue;
                }

                DrawRigGizmo(rig, entry, i);
            }
        }

        private static void DrawRigGizmo(RigCacheEntry rig, SnapshotRenderEntry entry, int index)
        {
            Transform[] ts = rig.Transforms;
            if (ts == null || ts.Length == 0)
            {
                return;
            }

            Color c = Color.HSVToRGB((index * 0.17f) % 1f, 0.85f, 1f);
            c.a = 0.9f;
            Handles.color = c;

            for (int i = 1; i < ts.Length; i++)
            {
                Transform child = ts[i];
                Transform parent = child.parent;
                if (child == null || parent == null)
                {
                    continue;
                }
                Handles.DrawLine(parent.position, child.position, 1.5f);
            }

            float size = HandleUtility.GetHandleSize(ts[0].position) * 0.03f;
            for (int i = 0; i < ts.Length; i++)
            {
                Handles.SphereHandleCap(0, ts[i].position, Quaternion.identity, size, EventType.Repaint);
            }

            string label = $"{entry.Marker.name} [{entry.RigType}] f={entry.FrameIndex}";
            Handles.Label(ts[0].position + Vector3.up * size * 6f, label);
        }

        internal static SkeletonPreviewRigType ResolveRigTypeFromModel(string modelName)
        {
            string m = (modelName ?? string.Empty).Trim().ToLowerInvariant();
            if (m.Contains("smplx"))
            {
                return SkeletonPreviewRigType.Smplx;
            }

            if (m.Contains("g1"))
            {
                return SkeletonPreviewRigType.G1;
            }

            return SkeletonPreviewRigType.Soma30;
        }

        private static void ClearAllCaches()
        {
            ActiveRenders.Clear();
            SnapshotCache.Clear();

            foreach (KeyValuePair<string, RigCacheEntry> kv in RigCache)
            {
                RigCacheEntry rig = kv.Value;
                if (rig.Root != null)
                {
                    UnityEngine.Object.DestroyImmediate(rig.Root.gameObject);
                }
            }
            RigCache.Clear();
        }

        private static void HideAllRigsAndClearActiveRenders()
        {
            ActiveRenders.Clear();
            SetAllRigVisibility(false);
        }

        private static void SetAllRigVisibility(bool visible)
        {
            foreach (KeyValuePair<string, RigCacheEntry> kv in RigCache)
            {
                RigCacheEntry rig = kv.Value;
                if (rig.Root == null || rig.Root.gameObject == null)
                {
                    continue;
                }

                if (rig.Root.gameObject.activeSelf != visible)
                {
                    rig.Root.gameObject.SetActive(visible);
                }
            }
        }

        [Serializable]
        private sealed class MarkerDigestWrapper
        {
            public string markerType;
            public double markerTime;
            public bool useOverride;
            public int serializedStamp;
        }

        internal enum SkeletonPreviewRigType
        {
            Soma30 = 0,
            Smplx = 1,
            G1 = 2
        }

        private struct RigCacheEntry
        {
            public string Key;
            public SkeletonPreviewRigType RigType;
            public Transform Root;
            public Transform[] Transforms;
            public Animator Animator;

            public bool IsAlive => Root != null && Root.gameObject != null;
        }

        private struct SnapshotCacheEntry
        {
            public bool IsValid;
            public int FrameIndex;
            public Vector3 RootPosition;
            public Quaternion[] LocalRotations;
        }

        private struct SnapshotRenderEntry
        {
            public KimodoConstraintMarkerBase Marker;
            public TimelineClip ClipRange;
            public SkeletonPreviewRigType RigType;
            public string RigKey;
            public int FrameIndex;
        }
    }
}
