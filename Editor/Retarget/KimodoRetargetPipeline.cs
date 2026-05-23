using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    public enum KimodoRetargetResultMode
    {
        SomaFallback = 0,
        DirectBone = 1,
        HumanoidMuscle = 2,
        TargetBone = 3
    }

    public static class KimodoRetargetPipeline
    {
        private const string RetargetDebugRootName = "__KimodoRetargetDebug";
        private static readonly FieldInfo SkeletonBoneParentNameField =
            typeof(SkeletonBone).GetField("parentName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo SkeletonBoneParentNameLegacyField =
            typeof(SkeletonBone).GetField("m_ParentName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo SkeletonBoneParentNameProperty =
            typeof(SkeletonBone).GetProperty("parentName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly PropertyInfo SkeletonBoneParentNameLegacyProperty =
            typeof(SkeletonBone).GetProperty("m_ParentName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private readonly struct RetargetContext
        {
            public readonly AnimationClip SourceSomaBoneClip;
            public readonly Animator TargetAnimator;
            public readonly Avatar EnsuredAvatar;
            public readonly string AvatarSource;
            public readonly bool HadHumanoidAvatar;

            public RetargetContext(
                AnimationClip sourceSomaBoneClip,
                Animator targetAnimator,
                Avatar ensuredAvatar,
                string avatarSource,
                bool hadHumanoidAvatar)
            {
                SourceSomaBoneClip = sourceSomaBoneClip;
                TargetAnimator = targetAnimator;
                EnsuredAvatar = ensuredAvatar;
                AvatarSource = avatarSource;
                HadHumanoidAvatar = hadHumanoidAvatar;
            }
        }

        public static bool TryRetargetBakedClip(
            KimodoPlayableClip playableClip,
            TimelineClip timelineClip,
            out KimodoRetargetResultMode mode,
            out string details)
        {
            mode = KimodoRetargetResultMode.SomaFallback;
            details = string.Empty;
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE
            BeginRetargetDebugSession(playableClip);
#endif

            if (!TryPrepareRetargetContext(playableClip, timelineClip, out RetargetContext context, out bool completed, out mode, out details))
            {
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE || KIMODO_RETARGET_DEBUG_LOG
                SetRetargetDebugResult($"FAILED: {details}");
#endif
                return false;
            }

            if (completed)
            {
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE || KIMODO_RETARGET_DEBUG_LOG
                SetRetargetDebugResult($"COMPLETED: {details}");
#endif
                return true;
            }

            if (!TryCreateSomaSamplingAnimator(playableClip, out Animator somaAnimator, out GameObject somaTempRoot, out string somaError))
            {
                details = $"Ensure SOMA avatar failed: {somaError}";
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE || KIMODO_RETARGET_DEBUG_LOG
                SetRetargetDebugResult($"FAILED: {details}");
#endif
                return false;
            }

            try
            {
                if (!TryBuildSomaMuscleClip(context.SourceSomaBoneClip, somaAnimator, out AnimationClip muscleClip, out string muscleError))
                {
                    details = $"SOMA->Muscle failed: {muscleError}";
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE || KIMODO_RETARGET_DEBUG_LOG
                    SetRetargetDebugResult($"FAILED: {details}");
#endif
                    return false;
                }

                if (context.HadHumanoidAvatar)
                {
                    OverwriteClipCurves(playableClip.clip, muscleClip);
                    mode = KimodoRetargetResultMode.HumanoidMuscle;
                    details = $"Retarget ok (Avatar={context.AvatarSource}, Mode=HumanoidMuscle).";
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE || KIMODO_RETARGET_DEBUG_LOG
                    SetRetargetDebugResult($"SUCCESS: {details}");
#endif
                    return true;
                }

                if (!TryBuildTargetBoneClipFromMuscle(
                        context,
                        muscleClip,
                        out AnimationClip targetBoneClip,
                        out string toBoneError))
                {
                    details = $"Muscle->TargetBone failed: {toBoneError}";
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE || KIMODO_RETARGET_DEBUG_LOG
                    SetRetargetDebugResult($"FAILED: {details}");
#endif
                    return false;
                }

                OverwriteClipCurves(playableClip.clip, targetBoneClip);
                mode = KimodoRetargetResultMode.TargetBone;
                details = $"Retarget ok (Avatar={context.AvatarSource}, Mode=TargetBone).";
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE || KIMODO_RETARGET_DEBUG_LOG
                SetRetargetDebugResult($"SUCCESS: {details}");
#endif
                return true;
            }
            catch (Exception e)
            {
                details = $"Retarget exception: {e.Message}";
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE || KIMODO_RETARGET_DEBUG_LOG
                SetRetargetDebugResult($"EXCEPTION: {details}");
#endif
                return false;
            }
            finally
            {
                if (somaTempRoot != null)
                {
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE
                    KeepRetargetDebugObject(ref somaTempRoot, "SOMA_SamplingRig");
#endif
                    if (somaTempRoot != null)
                    {
                    UnityEngine.Object.DestroyImmediate(somaTempRoot);
                    }
                }
            }
        }

        private static bool TryPrepareRetargetContext(
            KimodoPlayableClip playableClip,
            TimelineClip timelineClip,
            out RetargetContext context,
            out bool completed,
            out KimodoRetargetResultMode mode,
            out string details)
        {
            context = default;
            completed = false;
            mode = KimodoRetargetResultMode.SomaFallback;
            details = string.Empty;

            if (playableClip == null || playableClip.clip == null)
            {
                details = "PlayableClip or animation clip is null.";
                return false;
            }

            if (timelineClip == null)
            {
                details = "Timeline clip not found. Keep SOMA bake.";
                return false;
            }

            if (!TryResolveBoundAnimator(timelineClip, out Animator targetAnimator, out string bindError))
            {
                details = bindError;
                return false;
            }

            if (CanDirectWriteBoneCurves(playableClip.clip, targetAnimator))
            {
                mode = KimodoRetargetResultMode.DirectBone;
                details = "Direct bone write path used (compatible skeleton binding).";
                completed = true;
                return true;
            }

            bool hadHumanoidAvatar = targetAnimator.avatar != null && targetAnimator.avatar.isValid && targetAnimator.avatar.isHuman;
            if (!KimodoLocalAvatarUtility.TryEnsureHumanoidAvatar(
                    targetAnimator,
                    out Avatar ensuredAvatar,
                    out string avatarSource,
                    out string avatarError))
            {
                details = $"Ensure target avatar failed: {avatarError}";
                return false;
            }

            context = new RetargetContext(playableClip.clip, targetAnimator, ensuredAvatar, avatarSource, hadHumanoidAvatar);
            return true;
        }

        private static bool TryBuildSomaMuscleClip(
            AnimationClip sourceSomaBoneClip,
            Animator somaAnimator,
            out AnimationClip muscleClip,
            out string error)
        {
            muscleClip = new AnimationClip
            {
                name = $"{sourceSomaBoneClip.name}_Muscle",
                legacy = false,
                frameRate = sourceSomaBoneClip.frameRate > 0f ? sourceSomaBoneClip.frameRate : 30f
            };

            return TryConvertBoneClipToMuscleByAvatar(
                sourceSomaBoneClip,
                somaAnimator,
                muscleClip,
                out error,
                sourceSomaBoneClip.frameRate);
        }

        private static bool TryBuildTargetBoneClipFromMuscle(
            RetargetContext context,
            AnimationClip muscleClip,
            out AnimationClip targetBoneClip,
            out string error)
        {
            targetBoneClip = new AnimationClip
            {
                name = $"{context.SourceSomaBoneClip.name}_TargetBone",
                legacy = false,
                frameRate = context.SourceSomaBoneClip.frameRate > 0f ? context.SourceSomaBoneClip.frameRate : 30f
            };

            error = string.Empty;
            Animator targetSamplingAnimator = CreateTempAnimatorForAvatar(context.TargetAnimator, context.EnsuredAvatar, out GameObject targetTempRoot);
            if (targetSamplingAnimator == null)
            {
                error = "Failed to create target sampling animator.";
                return false;
            }

            try
            {
                return TryConvertMuscleToTargetBoneClip(
                    muscleClip,
                    targetSamplingAnimator,
                    targetBoneClip,
                    out error,
                    context.SourceSomaBoneClip.frameRate);
            }
            finally
            {
                if (targetTempRoot != null)
                {
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE
                    KeepRetargetDebugObject(ref targetTempRoot, "Target_SamplingRig");
#endif
                    if (targetTempRoot != null)
                    {
                    UnityEngine.Object.DestroyImmediate(targetTempRoot);
                    }
                }
            }
        }

        public static bool TryConvertPoseToSomaSpace(
            Animator targetAnimator,
            Transform skeletonRoot,
            out Vector3 rootPositionSoma,
            out Vector2 rootHeadingSoma,
            out List<Vector3> somaLocalAxisAngles,
            out string error)
        {
            rootPositionSoma = Vector3.zero;
            rootHeadingSoma = new Vector2(1f, 0f);
            somaLocalAxisAngles = new List<Vector3>();
            error = string.Empty;

            if (targetAnimator == null || skeletonRoot == null)
            {
                error = "Animator or skeleton root is null.";
                return false;
            }

            // Keep backward compatibility: if no human avatar available, keep old direct sampling.
            if (!KimodoLocalAvatarUtility.TryEnsureHumanoidAvatar(targetAnimator, out Avatar targetAvatar, out _, out _)
                || targetAvatar == null || !targetAvatar.isValid || !targetAvatar.isHuman)
            {
                return false;
            }

            if (!TryCreateSomaSamplingAnimator(null, out Animator somaAnimator, out GameObject somaTempRoot, out string somaError))
            {
                error = somaError;
                return false;
            }

            GameObject targetTempRoot = null;
            try
            {
                Animator targetTempAnimator = CreateTempAnimatorForAvatar(targetAnimator, targetAvatar, out targetTempRoot, keepCurrentPose: true);
                if (targetTempAnimator == null)
                {
                    error = "Failed to create target temp animator.";
                    return false;
                }

                HumanPoseHandler targetPoseHandler = new HumanPoseHandler(targetTempAnimator.avatar, targetTempAnimator.transform);
                HumanPoseHandler somaPoseHandler = new HumanPoseHandler(somaAnimator.avatar, somaAnimator.transform);

                HumanPose pose = new HumanPose();
                targetPoseHandler.GetHumanPose(ref pose);
                somaPoseHandler.SetHumanPose(ref pose);

                Transform somaRoot = somaAnimator.transform;
                Transform pelvis = FindTransformByName(somaRoot, "Hips") ?? somaRoot;
                Vector3 worldPos = pelvis.position;
                rootPositionSoma = new Vector3(-worldPos.x, worldPos.y, worldPos.z);

                Vector3 forward = pelvis.forward;
                Vector2 heading = new Vector2(-forward.x, forward.z);
                if (heading.sqrMagnitude <= 1e-8f)
                {
                    heading = new Vector2(1f, 0f);
                }
                else
                {
                    heading.Normalize();
                }
                rootHeadingSoma = heading;

                Transform[] somaJoints = ResolveSoma30Joints(somaRoot);
                Quaternion[] worldRots = new Quaternion[somaJoints.Length];
                for (int i = 0; i < somaJoints.Length; i++)
                {
                    worldRots[i] = somaJoints[i] != null ? somaJoints[i].rotation : Quaternion.identity;
                }

                for (int i = 0; i < somaJoints.Length; i++)
                {
                    int parent = Soma30Parents[i];
                    Quaternion local = (parent >= 0 && parent < worldRots.Length)
                        ? Quaternion.Inverse(worldRots[parent]) * worldRots[i]
                        : worldRots[i];

                    Quaternion q = new Quaternion(local.x, -local.y, -local.z, local.w);
                    somaLocalAxisAngles.Add(QuaternionToAxisAngleVector(q));
                }

                return true;
            }
            catch (Exception e)
            {
                error = $"Pose convert failed: {e.Message}";
                return false;
            }
            finally
            {
                if (targetTempRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(targetTempRoot);
                }

                if (somaTempRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(somaTempRoot);
                }
            }
        }

        private static bool TryResolveBoundAnimator(TimelineClip timelineClip, out Animator animator, out string error)
        {
            animator = null;
            error = string.Empty;

            if (timelineClip == null)
            {
                error = "Timeline clip is null.";
                return false;
            }

            TrackAsset track = timelineClip.GetParentTrack();
            if (track == null)
            {
                error = "Timeline parent track not found.";
                return false;
            }

            PlayableDirector director = TimelineEditor.inspectedDirector;
            if (director == null)
            {
                error = "Timeline inspected director is null.";
                return false;
            }

            animator = director.GetGenericBinding(track) as Animator;
            if (animator == null)
            {
                error = "Animation track has no Animator binding.";
                return false;
            }

            return true;
        }

        private static bool TryCreateSomaSamplingAnimator(KimodoPlayableClip playableClip, out Animator animator, out GameObject tempRoot, out string error)
        {
            animator = null;
            tempRoot = null;
            error = string.Empty;

            string avatarResourceName = ResolveRuntimeAvatarResourceName(playableClip);
            Avatar runtimeAvatar = Resources.Load<Avatar>(avatarResourceName);
            if (runtimeAvatar == null || !runtimeAvatar.isValid || !runtimeAvatar.isHuman)
            {
                error = $"Runtime Resources avatar '{avatarResourceName}' not found or invalid humanoid avatar.";
                return false;
            }

            tempRoot = new GameObject($"KimodoSomaSampling_{avatarResourceName}");
            tempRoot.hideFlags = HideFlags.HideAndDontSave;
            tempRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            tempRoot.transform.localScale = Vector3.one;

            if (!TryBuildHierarchyFromAvatarSkeleton(runtimeAvatar, tempRoot.transform, out string hierarchyError))
            {
                UnityEngine.Object.DestroyImmediate(tempRoot);
                tempRoot = null;
                error = hierarchyError;
                return false;
            }

            if (playableClip != null && playableClip.clip != null)
            {
                Transform bindingRoot = ResolveSamplingRootForClip(playableClip.clip, tempRoot.transform);
                EnsureHierarchyMatchesClipBindings(playableClip.clip, bindingRoot);
            }

            animator = tempRoot.AddComponent<Animator>();
            animator.avatar = runtimeAvatar;

            animator.enabled = false;
            animator.applyRootMotion = false;
            animator.Rebind();
            animator.Update(0f);
            return true;
        }

        private static string ResolveRuntimeAvatarResourceName(KimodoPlayableClip playableClip)
        {
            string modelName = playableClip != null ? playableClip.bridgeModelName : string.Empty;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return "SOMAAvatar";
            }

            string normalized = modelName.Trim().ToLowerInvariant();
            if (normalized.Contains("smplx"))
            {
                return "SMPLXAvatar";
            }

            if (normalized.Contains("g1"))
            {
                return "G1Avatar";
            }

            return "SOMAAvatar";
        }

#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE
        private static void BeginRetargetDebugSession(KimodoPlayableClip playableClip)
        {
            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                GameObject go = all[i];
                if (go == null)
                {
                    continue;
                }

                if (!go.name.StartsWith(RetargetDebugRootName, StringComparison.Ordinal))
                {
                    continue;
                }

                UnityEngine.Object.DestroyImmediate(go);
            }

            GameObject root = new GameObject(RetargetDebugRootName);
            root.hideFlags = HideFlags.None;
            string clipName = playableClip != null && playableClip.clip != null ? playableClip.clip.name : "null";
            root.name = $"{RetargetDebugRootName}_{clipName}_{DateTime.Now:HHmmss}";
        }

        private static void SetRetargetDebugResult(string result)
        {
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_LOG
            Debug.Log($"[Kimodo][RetargetDebug] {result}");
#endif
#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE
            GameObject root = FindRetargetDebugRoot();
            if (root == null)
            {
                return;
            }

            root.name = $"{root.name} [{result}]";
#endif
        }

        private static void KeepRetargetDebugObject(ref GameObject obj, string label)
        {
            if (obj == null)
            {
                return;
            }

            GameObject root = FindRetargetDebugRoot();
            if (root == null)
            {
                return;
            }

            obj.hideFlags = HideFlags.None;
            obj.name = label;
            obj.transform.SetParent(root.transform, true);
            obj = null;
        }

        private static GameObject FindRetargetDebugRoot()
        {
            GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null)
                {
                    continue;
                }

                if (all[i].name.StartsWith(RetargetDebugRootName, StringComparison.Ordinal))
                {
                    return all[i];
                }
            }

            return null;
        }

        private static void CaptureDebugFrameFromAvatarPose(
            string streamName,
            int frame,
            float time,
            Avatar avatar,
            Transform sampledRoot,
            Vector3 streamOffset,
            float frameStride,
            float sphereDiameter)
        {
            GameObject debugRoot = FindRetargetDebugRoot();
            if (debugRoot == null || avatar == null || sampledRoot == null)
            {
                return;
            }

            Transform streamRoot = EnsureChild(debugRoot.transform, streamName);
            string frameName = $"frame_{frame:0000}_t_{time:F3}";
            Transform frameRoot = EnsureChild(streamRoot, frameName);

            frameRoot.localPosition = streamOffset + new Vector3(frame * frameStride, 0f, 0f);
            frameRoot.localRotation = Quaternion.identity;
            frameRoot.localScale = Vector3.one;

            if (!TryBuildHierarchyFromAvatarSkeleton(avatar, frameRoot, out _))
            {
                return;
            }

            CopyLocalPoseByPath(sampledRoot, frameRoot);
            AttachBoneSpheres(frameRoot, sphereDiameter);
        }

        private static Transform EnsureChild(Transform parent, string name)
        {
            Transform child = parent.Find(name);
            if (child != null)
            {
                return child;
            }

            GameObject go = new GameObject(name);
            child = go.transform;
            child.SetParent(parent, false);
            child.localPosition = Vector3.zero;
            child.localRotation = Quaternion.identity;
            child.localScale = Vector3.one;
            return child;
        }

        private static void CopyLocalPoseByPath(Transform sourceRoot, Transform dstRoot)
        {
            if (sourceRoot == null || dstRoot == null)
            {
                return;
            }

            var sourceByPath = new Dictionary<string, Transform>(StringComparer.Ordinal);
            var sourceByName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            var srcStack = new Stack<Transform>();
            srcStack.Push(sourceRoot);
            while (srcStack.Count > 0)
            {
                Transform current = srcStack.Pop();
                if (current == null)
                {
                    continue;
                }

                string path = AnimationUtility.CalculateTransformPath(current, sourceRoot) ?? string.Empty;
                if (!sourceByPath.ContainsKey(path))
                {
                    sourceByPath[path] = current;
                }

                if (!sourceByName.ContainsKey(current.name))
                {
                    sourceByName[current.name] = current;
                }

                for (int i = 0; i < current.childCount; i++)
                {
                    srcStack.Push(current.GetChild(i));
                }
            }

            var dstStack = new Stack<Transform>();
            dstStack.Push(dstRoot);
            while (dstStack.Count > 0)
            {
                Transform current = dstStack.Pop();
                if (current == null)
                {
                    continue;
                }

                string path = AnimationUtility.CalculateTransformPath(current, dstRoot) ?? string.Empty;
                Transform src = null;
                if (!sourceByPath.TryGetValue(path, out src) || src == null)
                {
                    sourceByName.TryGetValue(current.name, out src);
                }

                if (src != null)
                {
                    current.localPosition = src.localPosition;
                    current.localRotation = src.localRotation;
                }

                for (int i = 0; i < current.childCount; i++)
                {
                    dstStack.Push(current.GetChild(i));
                }
            }
        }

        private static void AttachBoneSpheres(Transform frameRoot, float sphereDiameter)
        {
            Transform[] bones = frameRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];
                if (bone == frameRoot)
                {
                    continue;
                }

                Transform marker = bone.Find("__joint");
                if (marker == null)
                {
                    GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    sphere.name = "__joint";
                    marker = sphere.transform;
                    marker.SetParent(bone, false);
                    marker.localPosition = Vector3.zero;
                    marker.localRotation = Quaternion.identity;
                    marker.localScale = ComputeMarkerLocalScaleForWorldDiameter(bone, sphereDiameter);

                    Collider collider = sphere.GetComponent<Collider>();
                    if (collider != null)
                    {
                        UnityEngine.Object.DestroyImmediate(collider);
                    }

                    Renderer renderer = sphere.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        renderer.sharedMaterial = GetOrCreateDebugSphereMaterial();
                    }
                }
            }
        }

        private static Vector3 ComputeMarkerLocalScaleForWorldDiameter(Transform bone, float worldDiameter)
        {
            const float eps = 1e-5f;
            if (bone == null)
            {
                return Vector3.one * worldDiameter;
            }

            Vector3 s = bone.lossyScale;
            float sx = Mathf.Max(eps, Mathf.Abs(s.x));
            float sy = Mathf.Max(eps, Mathf.Abs(s.y));
            float sz = Mathf.Max(eps, Mathf.Abs(s.z));
            return new Vector3(worldDiameter / sx, worldDiameter / sy, worldDiameter / sz);
        }

        private static Material sDebugSphereMaterial;

        private static Material GetOrCreateDebugSphereMaterial()
        {
            if (sDebugSphereMaterial != null)
            {
                return sDebugSphereMaterial;
            }

            Shader shader = Shader.Find("Standard");
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            sDebugSphereMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            sDebugSphereMaterial.color = new Color(0f, 0.9f, 1f, 0.9f);
            return sDebugSphereMaterial;
        }
#endif

        private static bool TryBuildHierarchyFromAvatarSkeleton(Avatar avatar, Transform root, out string error)
        {
            error = string.Empty;
            if (avatar == null || root == null)
            {
                error = "Avatar or root is null while building sampling hierarchy.";
                return false;
            }

            SkeletonBone[] skeleton = avatar.humanDescription.skeleton;
            if (skeleton == null || skeleton.Length == 0)
            {
                error = "Avatar humanDescription.skeleton is empty.";
                return false;
            }

            var nodes = new List<SkeletonBuildNode>(skeleton.Length);
            var firstByName = new Dictionary<string, Transform>(StringComparer.Ordinal);

            for (int i = 0; i < skeleton.Length; i++)
            {
                SkeletonBone bone = skeleton[i];
                string name = string.IsNullOrWhiteSpace(bone.name) ? $"Bone_{i}" : bone.name;
                string parentName = GetSkeletonBoneParentNameReflective(bone);

                GameObject go = new GameObject(name);
                Transform t = go.transform;
                nodes.Add(new SkeletonBuildNode
                {
                    Name = name,
                    ParentName = parentName,
                    LocalPosition = bone.position,
                    LocalRotation = bone.rotation,
                    LocalScale = bone.scale,
                    Transform = t
                });

                if (!firstByName.ContainsKey(name))
                {
                    firstByName[name] = t;
                }
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                SkeletonBuildNode node = nodes[i];
                Transform parent = root;
                if (!string.IsNullOrWhiteSpace(node.ParentName) &&
                    firstByName.TryGetValue(node.ParentName, out Transform resolvedParent) &&
                    resolvedParent != null)
                {
                    parent = resolvedParent;
                }

                node.Transform.SetParent(parent, false);
                node.Transform.localPosition = node.LocalPosition;
                node.Transform.localRotation = node.LocalRotation;
                node.Transform.localScale = node.LocalScale;
            }

            return true;
        }

        private static void EnsureHierarchyMatchesClipBindings(AnimationClip clip, Transform root)
        {
            if (clip == null || root == null)
            {
                return;
            }

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            var uniquePaths = new HashSet<string>(StringComparer.Ordinal);
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

        private static string GetSkeletonBoneParentNameReflective(SkeletonBone bone)
        {
            try
            {
                object boxed = bone;
                string value = GetSkeletonBoneStringMember(
                    boxed,
                    SkeletonBoneParentNameField,
                    SkeletonBoneParentNameLegacyField,
                    SkeletonBoneParentNameProperty,
                    SkeletonBoneParentNameLegacyProperty);
                return value ?? string.Empty;
            }
            catch
            {
                // ignore
            }

            return string.Empty;
        }

        private static string GetSkeletonBoneStringMember(
            object boxed,
            FieldInfo primaryField,
            FieldInfo secondaryField,
            PropertyInfo primaryProperty,
            PropertyInfo secondaryProperty)
        {
            if (boxed == null)
            {
                return string.Empty;
            }

            if (primaryField != null)
            {
                object value = primaryField.GetValue(boxed);
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }

            if (secondaryField != null)
            {
                object value = secondaryField.GetValue(boxed);
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }

            if (primaryProperty != null)
            {
                object value = primaryProperty.GetValue(boxed, null);
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }

            if (secondaryProperty != null)
            {
                object value = secondaryProperty.GetValue(boxed, null);
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }

            return string.Empty;
        }

        private static Transform ResolveSamplingRootForClip(AnimationClip clip, Transform hierarchyRoot)
        {
            if (clip == null || hierarchyRoot == null)
            {
                return hierarchyRoot;
            }

            List<string> paths = CollectTransformBindingPaths(clip);
            if (paths.Count == 0)
            {
                return hierarchyRoot;
            }

            if (AllClipPathsResolveUnderRoot(hierarchyRoot, paths))
            {
                return hierarchyRoot;
            }

            Transform resolved = null;
            for (int i = 0; i < hierarchyRoot.childCount; i++)
            {
                Transform child = hierarchyRoot.GetChild(i);
                if (!AllClipPathsResolveUnderRoot(child, paths))
                {
                    continue;
                }

                if (resolved != null)
                {
                    return hierarchyRoot;
                }

                resolved = child;
            }

            return resolved != null ? resolved : hierarchyRoot;
        }

        private static List<string> CollectTransformBindingPaths(AnimationClip clip)
        {
            var result = new List<string>();
            if (clip == null)
            {
                return result;
            }

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            var unique = new HashSet<string>(StringComparer.Ordinal);
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

        private static bool AllClipPathsResolveUnderRoot(Transform root, List<string> paths)
        {
            if (root == null || paths == null || paths.Count == 0)
            {
                return true;
            }

            for (int i = 0; i < paths.Count; i++)
            {
                if (root.Find(paths[i]) == null)
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class SkeletonBuildNode
        {
            public string Name;
            public string ParentName;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
            public Transform Transform;
        }

        private static Animator CreateTempAnimatorForAvatar(
            Animator sourceAnimator,
            Avatar avatar,
            out GameObject tempRoot,
            bool keepCurrentPose = false)
        {
            tempRoot = null;
            if (sourceAnimator == null)
            {
                return null;
            }

            tempRoot = UnityEngine.Object.Instantiate(sourceAnimator.gameObject);
            tempRoot.hideFlags = HideFlags.HideAndDontSave;
            tempRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            tempRoot.transform.localScale = Vector3.one;

            Animator tempAnimator = tempRoot.GetComponent<Animator>();
            if (tempAnimator == null)
            {
                return null;
            }

            tempAnimator.avatar = avatar;
            tempAnimator.enabled = false;
            tempAnimator.applyRootMotion = false;
            if (!keepCurrentPose)
            {
                tempAnimator.Rebind();
                tempAnimator.Update(0f);
            }
            return tempAnimator;
        }

        private static void OverwriteClipCurves(AnimationClip dst, AnimationClip src)
        {
            dst.ClearCurves();

            EditorCurveBinding[] floatBindings = AnimationUtility.GetCurveBindings(src);
            for (int i = 0; i < floatBindings.Length; i++)
            {
                EditorCurveBinding b = floatBindings[i];
                AnimationCurve c = AnimationUtility.GetEditorCurve(src, b);
                dst.SetCurve(b.path, b.type, b.propertyName, c);
            }

            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(src);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                EditorCurveBinding b = objectBindings[i];
                ObjectReferenceKeyframe[] k = AnimationUtility.GetObjectReferenceCurve(src, b);
                AnimationUtility.SetObjectReferenceCurve(dst, b, k);
            }

            dst.frameRate = src.frameRate;
            dst.legacy = false;
            dst.EnsureQuaternionContinuity();
            EditorUtility.SetDirty(dst);
        }

        private static bool CanDirectWriteBoneCurves(AnimationClip clip, Animator targetAnimator)
        {
            if (clip == null || targetAnimator == null)
            {
                return false;
            }

            Transform root = targetAnimator.transform;
            if (root == null)
            {
                return false;
            }

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            if (bindings == null || bindings.Length == 0)
            {
                return false;
            }

            // Consider direct write compatible when all transform paths in clip exist on target binding hierarchy.
            var checkedPaths = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding b = bindings[i];
                if (b.type != typeof(Transform))
                {
                    continue;
                }

                string path = b.path ?? string.Empty;
                if (!checkedPaths.Add(path))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                Transform t = root.Find(path);
                if (t == null)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryConvertMuscleToTargetBoneClip(
            AnimationClip muscleClip,
            Animator targetHumanoidAnimator,
            AnimationClip outputTargetBoneClip,
            out string error,
            float sampleRate = 30f)
        {
            error = string.Empty;
            if (muscleClip == null || targetHumanoidAnimator == null || outputTargetBoneClip == null)
            {
                error = "Input clip/animator/output is null.";
                return false;
            }

            if (targetHumanoidAnimator.avatar == null || !targetHumanoidAnimator.avatar.isValid || !targetHumanoidAnimator.avatar.isHuman)
            {
                error = "Target animator must have valid humanoid avatar.";
                return false;
            }

            try
            {
                float effectiveRate = sampleRate > 0f ? sampleRate : (muscleClip.frameRate > 0f ? muscleClip.frameRate : 30f);
                float duration = muscleClip.length > 0f ? muscleClip.length : 1f / Mathf.Max(1f, effectiveRate);
                int frameCount = Mathf.Max(2, Mathf.RoundToInt(duration * effectiveRate) + 1);

                var curveLookup = BuildAnimatorCurveLookup(muscleClip);
                HumanPoseHandler poseHandler = new HumanPoseHandler(targetHumanoidAnimator.avatar, targetHumanoidAnimator.transform);
                HumanPose basePose = new HumanPose();
                poseHandler.GetHumanPose(ref basePose);

                Transform root = targetHumanoidAnimator.transform;
                Transform[] all = root.GetComponentsInChildren<Transform>(true);
                string[] paths = new string[all.Length];
                for (int i = 0; i < all.Length; i++)
                {
                    paths[i] = AnimationUtility.CalculateTransformPath(all[i], root);
                }

                var px = new AnimationCurve[all.Length];
                var py = new AnimationCurve[all.Length];
                var pz = new AnimationCurve[all.Length];
                var qx = new AnimationCurve[all.Length];
                var qy = new AnimationCurve[all.Length];
                var qz = new AnimationCurve[all.Length];
                var qw = new AnimationCurve[all.Length];
                for (int i = 0; i < all.Length; i++)
                {
                    px[i] = new AnimationCurve();
                    py[i] = new AnimationCurve();
                    pz[i] = new AnimationCurve();
                    qx[i] = new AnimationCurve();
                    qy[i] = new AnimationCurve();
                    qz[i] = new AnimationCurve();
                    qw[i] = new AnimationCurve();
                }

                for (int frame = 0; frame < frameCount; frame++)
                {
                    float t = FrameToTime(frame, frameCount, duration);
                    HumanPose pose = BuildPoseAtTime(curveLookup, basePose, t);
                    poseHandler.SetHumanPose(ref pose);

#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE
                    CaptureDebugFrameFromAvatarPose(
                        streamName: "TargetFrames",
                        frame: frame,
                        time: t,
                        avatar: targetHumanoidAnimator.avatar,
                        sampledRoot: root,
                        streamOffset: new Vector3(0f, 0f, 2f),
                        frameStride: 0.2f,
                        sphereDiameter: 0.03f);
#endif

                    for (int i = 0; i < all.Length; i++)
                    {
                        Transform bone = all[i];
                        Vector3 lp = bone.localPosition;
                        Quaternion lr = bone.localRotation;

                        px[i].AddKey(t, lp.x);
                        py[i].AddKey(t, lp.y);
                        pz[i].AddKey(t, lp.z);
                        qx[i].AddKey(t, lr.x);
                        qy[i].AddKey(t, lr.y);
                        qz[i].AddKey(t, lr.z);
                        qw[i].AddKey(t, lr.w);
                    }
                }

                outputTargetBoneClip.ClearCurves();
                outputTargetBoneClip.legacy = false;
                outputTargetBoneClip.frameRate = effectiveRate;
                for (int i = 0; i < all.Length; i++)
                {
                    string path = paths[i];
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", px[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", py[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", pz[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", qx[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", qy[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", qz[i]);
                    outputTargetBoneClip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", qw[i]);
                }
                outputTargetBoneClip.EnsureQuaternionContinuity();
                return true;
            }
            catch (Exception e)
            {
                error = $"Convert muscle->target bone failed: {e.Message}";
                return false;
            }
        }

        private static bool TryConvertBoneClipToMuscleByAvatar(
            AnimationClip sourceBoneClip,
            Animator sourceHumanoidAnimator,
            AnimationClip outputMuscleClip,
            out string error,
            float sampleRate = 30f)
        {
            error = string.Empty;
            if (sourceBoneClip == null || sourceHumanoidAnimator == null || outputMuscleClip == null)
            {
                error = "Input clip/animator/output is null.";
                return false;
            }

            if (sourceHumanoidAnimator.avatar == null || !sourceHumanoidAnimator.avatar.isValid || !sourceHumanoidAnimator.avatar.isHuman)
            {
                error = "Source animator must have valid humanoid avatar.";
                return false;
            }

            try
            {
                float effectiveRate = sampleRate > 0f ? sampleRate : (sourceBoneClip.frameRate > 0f ? sourceBoneClip.frameRate : 30f);
                float duration = sourceBoneClip.length > 0f ? sourceBoneClip.length : 1f / Mathf.Max(1f, effectiveRate);
                int frameCount = Mathf.Max(2, Mathf.RoundToInt(duration * effectiveRate) + 1);

                GameObject tempRig = UnityEngine.Object.Instantiate(sourceHumanoidAnimator.gameObject);
                tempRig.hideFlags = HideFlags.HideAndDontSave;
                tempRig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                tempRig.transform.localScale = Vector3.one;

                try
                {
                    Animator tempAnimator = tempRig.GetComponent<Animator>();
                    if (tempAnimator == null)
                    {
                        error = "Failed to create source sampling animator.";
                        return false;
                    }

                    tempAnimator.enabled = false;
                    tempAnimator.applyRootMotion = false;
                    tempAnimator.Rebind();
                    tempAnimator.Update(0f);
                    Transform samplingRoot = ResolveSamplingRootForClip(sourceBoneClip, tempAnimator.transform);
                    if (samplingRoot == null)
                    {
                        samplingRoot = tempAnimator.transform;
                    }

                    HumanPoseHandler poseHandler = new HumanPoseHandler(tempAnimator.avatar, tempAnimator.transform);
                    HumanPose pose = new HumanPose();

                    outputMuscleClip.ClearCurves();
                    outputMuscleClip.legacy = false;
                    outputMuscleClip.frameRate = effectiveRate;

                    AnimationCurve[] muscleCurves = new AnimationCurve[HumanTrait.MuscleCount];
                    for (int i = 0; i < muscleCurves.Length; i++)
                    {
                        muscleCurves[i] = new AnimationCurve();
                    }

                    AnimationCurve rootTx = new AnimationCurve();
                    AnimationCurve rootTy = new AnimationCurve();
                    AnimationCurve rootTz = new AnimationCurve();
                    AnimationCurve rootQx = new AnimationCurve();
                    AnimationCurve rootQy = new AnimationCurve();
                    AnimationCurve rootQz = new AnimationCurve();
                    AnimationCurve rootQw = new AnimationCurve();

                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        float t = FrameToTime(frame, frameCount, duration);
                        sourceBoneClip.SampleAnimation(tempRig, t);
                        poseHandler.GetHumanPose(ref pose);

#if KIMODO_RETARGET_DEBUG || KIMODO_RETARGET_DEBUG_SCENE
                        CaptureDebugFrameFromAvatarPose(
                            streamName: "OriginFrames",
                            frame: frame,
                            time: t,
                            avatar: tempAnimator.avatar,
                            sampledRoot: samplingRoot,
                            streamOffset: Vector3.zero,
                            frameStride: 0.2f,
                            sphereDiameter: 0.03f);
#endif

                        for (int i = 0; i < HumanTrait.MuscleCount && i < pose.muscles.Length; i++)
                        {
                            muscleCurves[i].AddKey(t, pose.muscles[i]);
                        }

                        rootTx.AddKey(t, pose.bodyPosition.x);
                        rootTy.AddKey(t, pose.bodyPosition.y);
                        rootTz.AddKey(t, pose.bodyPosition.z);
                        rootQx.AddKey(t, pose.bodyRotation.x);
                        rootQy.AddKey(t, pose.bodyRotation.y);
                        rootQz.AddKey(t, pose.bodyRotation.z);
                        rootQw.AddKey(t, pose.bodyRotation.w);
                    }

                    for (int i = 0; i < HumanTrait.MuscleCount; i++)
                    {
                        outputMuscleClip.SetCurve(string.Empty, typeof(Animator), MusclePropertyNames[i], muscleCurves[i]);
                    }

                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootT.x", rootTx);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootT.y", rootTy);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootT.z", rootTz);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.x", rootQx);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.y", rootQy);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.z", rootQz);
                    outputMuscleClip.SetCurve(string.Empty, typeof(Animator), "RootQ.w", rootQw);
                    outputMuscleClip.EnsureQuaternionContinuity();
                    return true;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tempRig);
                }
            }
            catch (Exception e)
            {
                error = $"Convert source bone->muscle by avatar failed: {e.Message}";
                return false;
            }
        }

        private static HumanPose BuildPoseAtTime(Dictionary<string, AnimationCurve> curveLookup, HumanPose basePose, float t)
        {
            HumanPose pose = basePose;

            for (int i = 0; i < HumanTrait.MuscleCount && i < pose.muscles.Length; i++)
            {
                string prop = MusclePropertyNames[i];
                if (curveLookup.TryGetValue(prop, out AnimationCurve curve) && curve != null)
                {
                    pose.muscles[i] = curve.Evaluate(t);
                }
            }

            Vector3 bodyPos = pose.bodyPosition;
            if (TryEvaluate(curveLookup, "RootT.x", t, out float tx)) bodyPos.x = tx;
            if (TryEvaluate(curveLookup, "RootT.y", t, out float ty)) bodyPos.y = ty;
            if (TryEvaluate(curveLookup, "RootT.z", t, out float tz)) bodyPos.z = tz;
            pose.bodyPosition = bodyPos;

            Quaternion bodyRot = pose.bodyRotation;
            if (TryEvaluate(curveLookup, "RootQ.x", t, out float qx)) bodyRot.x = qx;
            if (TryEvaluate(curveLookup, "RootQ.y", t, out float qy)) bodyRot.y = qy;
            if (TryEvaluate(curveLookup, "RootQ.z", t, out float qz)) bodyRot.z = qz;
            if (TryEvaluate(curveLookup, "RootQ.w", t, out float qw)) bodyRot.w = qw;
            if (bodyRot != Quaternion.identity)
            {
                bodyRot.Normalize();
            }
            pose.bodyRotation = bodyRot;
            return pose;
        }

        private static bool TryEvaluate(Dictionary<string, AnimationCurve> lookup, string prop, float t, out float value)
        {
            value = 0f;
            if (!lookup.TryGetValue(prop, out AnimationCurve curve) || curve == null)
            {
                return false;
            }

            value = curve.Evaluate(t);
            return true;
        }

        private static Dictionary<string, AnimationCurve> BuildAnimatorCurveLookup(AnimationClip clip)
        {
            var lookup = new Dictionary<string, AnimationCurve>(StringComparer.Ordinal);
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                if (binding.type != typeof(Animator))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(binding.path))
                {
                    continue;
                }

                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                lookup[binding.propertyName] = curve;
            }

            return lookup;
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

        private static Transform FindTransformByName(Transform root, string name)
        {
            if (root == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var stack = new Stack<Transform>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                Transform current = stack.Pop();
                if (string.Equals(current.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return current;
                }

                for (int i = 0; i < current.childCount; i++)
                {
                    stack.Push(current.GetChild(i));
                }
            }

            return null;
        }

        private static Transform[] ResolveSoma30Joints(Transform root)
        {
            var joints = new Transform[Soma30Names.Length];
            for (int i = 0; i < Soma30Names.Length; i++)
            {
                joints[i] = FindTransformByName(root, Soma30Names[i]) ?? root;
            }
            return joints;
        }

        private static Vector3 QuaternionToAxisAngleVector(Quaternion q)
        {
            q.Normalize();
            q.ToAngleAxis(out float degrees, out Vector3 axis);
            if (float.IsNaN(axis.x) || axis == Vector3.zero)
            {
                return Vector3.zero;
            }

            if (degrees > 180f)
            {
                degrees -= 360f;
            }

            float radians = degrees * Mathf.Deg2Rad;
            return axis.normalized * radians;
        }

        private static readonly string[] Soma30Names =
        {
            "Hips", "Spine1", "Spine2", "Chest", "Neck1", "Neck2", "Head", "Jaw", "LeftEye", "RightEye",
            "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "LeftHandThumbEnd", "LeftHandMiddleEnd",
            "RightShoulder", "RightArm", "RightForeArm", "RightHand", "RightHandThumbEnd", "RightHandMiddleEnd",
            "LeftLeg", "LeftShin", "LeftFoot", "LeftToeBase", "RightLeg", "RightShin", "RightFoot", "RightToeBase"
        };

        private static readonly int[] Soma30Parents =
        {
            -1, 0, 1, 2, 3, 4, 5, 6, 6, 6, 3, 10, 11, 12, 13, 13, 3, 16, 17, 18, 19, 19, 0, 22, 23, 24, 0, 26, 27, 28
        };

        private static readonly string[] MusclePropertyNames = BuildMusclePropertyNames();

        private static string[] BuildMusclePropertyNames()
        {
            string[] names = new string[HumanTrait.MuscleCount];
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
            {
                string n = HumanTrait.MuscleName[i];
                if (i >= 55)
                {
                    string[] parts = n.Split(' ');
                    if (parts.Length == 2)
                    {
                        parts[0] += "Hand.";
                        parts[1] += ".";
                        n = string.Join(" ", parts);
                    }
                }

                names[i] = n;
            }

            return names;
        }
    }
}
