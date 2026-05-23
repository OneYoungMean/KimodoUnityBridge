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

    public static partial class KimodoRetargetPipeline
    {
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

            if (!TryPrepareRetargetContext(playableClip, timelineClip, out RetargetContext context, out bool completed, out mode, out details))
            {
                return false;
            }

            if (completed)
            {
                return true;
            }

            if (!TryCreateSomaSamplingAnimator(playableClip, out Animator somaAnimator, out GameObject somaTempRoot, out string somaError))
            {
                details = $"Ensure SOMA avatar failed: {somaError}";
                return false;
            }

            try
            {
                if (!TryBuildSomaMuscleClip(context.SourceSomaBoneClip, somaAnimator, out AnimationClip muscleClip, out string muscleError))
                {
                    details = $"SOMA->Muscle failed: {muscleError}";
                    return false;
                }

                if (context.HadHumanoidAvatar)
                {
                    OverwriteClipCurves(playableClip.clip, muscleClip);
                    mode = KimodoRetargetResultMode.HumanoidMuscle;
                    details = $"Retarget ok (Avatar={context.AvatarSource}, Mode=HumanoidMuscle).";
                    return true;
                }

                if (!TryBuildTargetBoneClipFromMuscle(
                        context,
                        muscleClip,
                        out AnimationClip targetBoneClip,
                        out string toBoneError))
                {
                    details = $"Muscle->TargetBone failed: {toBoneError}";
                    return false;
                }

                OverwriteClipCurves(playableClip.clip, targetBoneClip);
                mode = KimodoRetargetResultMode.TargetBone;
                details = $"Retarget ok (Avatar={context.AvatarSource}, Mode=TargetBone).";
                return true;
            }
            catch (Exception e)
            {
                details = $"Retarget exception: {e.Message}";
                return false;
            }
            finally
            {
                if (somaTempRoot != null)
                {
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
                    if (targetTempRoot != null)
                    {
                    UnityEngine.Object.DestroyImmediate(targetTempRoot);
                    }
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

            Transform samplingRoot = ResolveBuiltAvatarSkeletonRoot(tempRoot.transform, runtimeAvatar);
            if (samplingRoot == null)
            {
                UnityEngine.Object.DestroyImmediate(tempRoot);
                tempRoot = null;
                error = "Failed to resolve built avatar skeleton root.";
                return false;
            }

            if (playableClip != null && playableClip.clip != null)
            {
                EnsureHierarchyMatchesClipBindings(playableClip.clip, samplingRoot);
            }

            animator = samplingRoot.gameObject.AddComponent<Animator>();
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

        private static Transform ResolveBuiltAvatarSkeletonRoot(Transform hierarchyRoot, Avatar avatar)
        {
            if (hierarchyRoot == null)
            {
                return null;
            }

            string expectedRootName = GetAvatarSkeletonRootName(avatar);
            if (!string.IsNullOrWhiteSpace(expectedRootName))
            {
                if (string.Equals(hierarchyRoot.name, expectedRootName, StringComparison.Ordinal))
                {
                    return hierarchyRoot;
                }

                Transform directChild = hierarchyRoot.Find(expectedRootName);
                if (directChild != null)
                {
                    return directChild;
                }

                Transform[] all = hierarchyRoot.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    Transform t = all[i];
                    if (t != null && string.Equals(t.name, expectedRootName, StringComparison.Ordinal))
                    {
                        return t;
                    }
                }
            }

            if (hierarchyRoot.childCount > 0)
            {
                return hierarchyRoot.GetChild(0);
            }

            return hierarchyRoot;
        }

        private static string GetAvatarSkeletonRootName(Avatar avatar)
        {
            if (avatar == null || avatar.humanDescription.skeleton == null)
            {
                return string.Empty;
            }

            SkeletonBone[] skeleton = avatar.humanDescription.skeleton;
            for (int i = 0; i < skeleton.Length; i++)
            {
                string name = skeleton[i].name;
                string parentName = GetSkeletonBoneParentNameReflective(skeleton[i]);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(parentName))
                {
                    return name;
                }
            }

            return string.Empty;
        }

        private static int CopyLocalPoseByPathForSampling(Transform sourceRoot, Transform dstRoot)
        {
            if (sourceRoot == null || dstRoot == null)
            {
                return 0;
            }

            var sourceByPath = new Dictionary<string, Transform>(StringComparer.Ordinal);
            Transform[] sourceAll = sourceRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < sourceAll.Length; i++)
            {
                Transform t = sourceAll[i];
                string path = AnimationUtility.CalculateTransformPath(t, sourceRoot) ?? string.Empty;
                if (!sourceByPath.ContainsKey(path))
                {
                    sourceByPath[path] = t;
                }
            }

            int copied = 0;
            Transform[] dstAll = dstRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < dstAll.Length; i++)
            {
                Transform dst = dstAll[i];
                string path = AnimationUtility.CalculateTransformPath(dst, dstRoot) ?? string.Empty;
                if (!sourceByPath.TryGetValue(path, out Transform src) || src == null)
                {
                    continue;
                }

                dst.localPosition = src.localPosition;
                dst.localRotation = src.localRotation;
                copied++;
            }

            return copied;
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

    }
}
