using KimodoBridge;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class FootRootMotionClipBaker
    {
        private const float MinAverageRootMotionDistance = 0.001f;

        public static bool HasMeaningfulRootMotion(AnimationClip clip)
        {
            return !NeedsAutoFixRootMotion(clip);
        }

        public static bool NeedsAutoFixRootMotion(AnimationClip clip)
        {
            if (clip == null)
            {
                return false;
            }

            if (TryGetAverageRootMotionDistance(clip, "RootT", out float rootDistance) &&
                rootDistance >= MinAverageRootMotionDistance)
            {
                return false;
            }

            if (TryGetAverageRootMotionDistance(clip, "MotionT", out float motionDistance) &&
                motionDistance >= MinAverageRootMotionDistance)
            {
                return false;
            }

            return true;
        }

        public static AnimationClip AutoFixRootMotion(AnimationClip sourceClip, Avatar avatar, out string error)
        {
            return AutoFixRootMotion(sourceClip, avatar, FootRootMotionSolverSettings.CreateZero(), out error);
        }

        public static AnimationClip AutoFixRootMotion(
            AnimationClip sourceClip,
            Avatar avatar,
            FootRootMotionSolverSettings settings,
            out string error)
        {
            TryAutoFixRootMotion(sourceClip, avatar, settings, out AnimationClip outputClip, out _, out error);
            return outputClip;
        }

        public static bool TryAutoFixRootMotion(
            AnimationClip sourceClip,
            Avatar avatar,
            FootRootMotionSolverSettings settings,
            out AnimationClip outputClip,
            out bool didModify,
            out string error)
        {
            outputClip = null;
            didModify = false;
            error = string.Empty;

            if (sourceClip == null)
            {
                error = "Source clip is null.";
                return false;
            }

            if (!KimodoRetargetTools.IsValidHumanoid(avatar))
            {
                error = "Avatar is null/invalid/non-humanoid.";
                return false;
            }

            settings ??= FootRootMotionSolverSettings.CreateZero();

            outputClip = CloneClip(sourceClip);
            if (outputClip == null)
            {
                error = "Failed to clone source clip.";
                return false;
            }

            if (!NeedsAutoFixRootMotion(sourceClip))
            {
                return true;
            }

            if (!FootRootMotionSamplingUtility.TrySampleClip(sourceClip, avatar, out FootRootMotionFrame[] frames, out error))
            {
                UnityEngine.Object.DestroyImmediate(outputClip);
                outputClip = null;
                return false;
            }

            if (frames == null || frames.Length == 0)
            {
                UnityEngine.Object.DestroyImmediate(outputClip);
                outputClip = null;
                error = "No sampled frames for root motion solve.";
                return false;
            }

            FootRootMotionResult solved = FootRootMotionSolver.Solve(frames, settings);
            if (solved == null || solved.rootXZ == null || solved.rootXZ.Length == 0)
            {
                UnityEngine.Object.DestroyImmediate(outputClip);
                outputClip = null;
                error = "Root motion solve result is empty.";
                return false;
            }

            string rootPath = ResolveRootBindingPath(sourceClip, avatar);
            if (!RewriteRootMotionCurves(outputClip, rootPath, frames, solved, out error))
            {
                UnityEngine.Object.DestroyImmediate(outputClip);
                outputClip = null;
                return false;
            }

            outputClip.EnsureQuaternionContinuity();
            didModify = true;
            return true;
        }

        public static bool NeedsAutoFixRootMotion(AnimationClip clip, Avatar avatar)
        {
            if (!KimodoRetargetTools.IsValidHumanoid(avatar))
            {
                return false;
            }

            return NeedsAutoFixRootMotion(clip);
        }

        private static AnimationClip CloneClip(AnimationClip sourceClip)
        {
            if (sourceClip == null)
            {
                return null;
            }

            var clone = new AnimationClip
            {
                name = string.IsNullOrWhiteSpace(sourceClip.name) ? "RootMotionClip" : $"{sourceClip.name}_RootMotion",
                legacy = sourceClip.legacy
            };

            KimodoEditorClipUtility.CopyClipData(sourceClip, clone, forceNoLoopKeepY: false);
            return clone;
        }

        private static bool RewriteRootMotionCurves(
            AnimationClip clip,
            string rootPath,
            FootRootMotionFrame[] frames,
            FootRootMotionResult solved,
            out string error)
        {
            error = string.Empty;
            if (clip == null)
            {
                error = "Target clip is null.";
                return false;
            }

            int frameCount = Mathf.Min(frames != null ? frames.Length : 0, solved != null && solved.rootXZ != null ? solved.rootXZ.Length : 0);
            if (frameCount <= 0)
            {
                error = "No solved frames available.";
                return false;
            }

            AnimationCurve rootPosX = new AnimationCurve();
            AnimationCurve rootPosY = new AnimationCurve();
            AnimationCurve rootPosZ = new AnimationCurve();
            AnimationCurve rootRotX = new AnimationCurve();
            AnimationCurve rootRotY = new AnimationCurve();
            AnimationCurve rootRotZ = new AnimationCurve();
            AnimationCurve rootRotW = new AnimationCurve();
            AnimationCurve motionTx = new AnimationCurve();
            AnimationCurve motionTy = new AnimationCurve();
            AnimationCurve motionTz = new AnimationCurve();
            AnimationCurve motionQx = new AnimationCurve();
            AnimationCurve motionQy = new AnimationCurve();
            AnimationCurve motionQz = new AnimationCurve();
            AnimationCurve motionQw = new AnimationCurve();

            for (int i = 0; i < frameCount; i++)
            {
                float time = frames[i].time;
                Vector2 solvedXZ = solved.rootXZ[i];
                Vector3 sampledRoot = frames[i].sampledRootWorld;
                Quaternion sampledRootRotation = frames[i].sampledRootRotation;

                rootPosX.AddKey(time, solvedXZ.x);
                rootPosY.AddKey(time, sampledRoot.y);
                rootPosZ.AddKey(time, solvedXZ.y);
                rootRotX.AddKey(time, sampledRootRotation.x);
                rootRotY.AddKey(time, sampledRootRotation.y);
                rootRotZ.AddKey(time, sampledRootRotation.z);
                rootRotW.AddKey(time, sampledRootRotation.w);

                motionTx.AddKey(time, solvedXZ.x);
                motionTy.AddKey(time, sampledRoot.y);
                motionTz.AddKey(time, solvedXZ.y);
                motionQx.AddKey(time, sampledRootRotation.x);
                motionQy.AddKey(time, sampledRootRotation.y);
                motionQz.AddKey(time, sampledRootRotation.z);
                motionQw.AddKey(time, sampledRootRotation.w);
            }

            if (!string.IsNullOrWhiteSpace(rootPath))
            {
                clip.SetCurve(rootPath, typeof(Transform), "m_LocalPosition.x", rootPosX);
                clip.SetCurve(rootPath, typeof(Transform), "m_LocalPosition.y", rootPosY);
                clip.SetCurve(rootPath, typeof(Transform), "m_LocalPosition.z", rootPosZ);
                clip.SetCurve(rootPath, typeof(Transform), "m_LocalRotation.x", rootRotX);
                clip.SetCurve(rootPath, typeof(Transform), "m_LocalRotation.y", rootRotY);
                clip.SetCurve(rootPath, typeof(Transform), "m_LocalRotation.z", rootRotZ);
                clip.SetCurve(rootPath, typeof(Transform), "m_LocalRotation.w", rootRotW);
            }

            SetAnimatorFloatCurve(clip, "RootT.x", motionTx);
            SetAnimatorFloatCurve(clip, "RootT.y", motionTy);
            SetAnimatorFloatCurve(clip, "RootT.z", motionTz);
            SetAnimatorFloatCurve(clip, "RootQ.x", motionQx);
            SetAnimatorFloatCurve(clip, "RootQ.y", motionQy);
            SetAnimatorFloatCurve(clip, "RootQ.z", motionQz);
            SetAnimatorFloatCurve(clip, "RootQ.w", motionQw);
            SetAnimatorFloatCurve(clip, "MotionT.x", motionTx);
            SetAnimatorFloatCurve(clip, "MotionT.y", motionTy);
            SetAnimatorFloatCurve(clip, "MotionT.z", motionTz);
            SetAnimatorFloatCurve(clip, "MotionQ.x", motionQx);
            SetAnimatorFloatCurve(clip, "MotionQ.y", motionQy);
            SetAnimatorFloatCurve(clip, "MotionQ.z", motionQz);
            SetAnimatorFloatCurve(clip, "MotionQ.w", motionQw);

            return true;
        }

        private static string ResolveRootBindingPath(AnimationClip clip, Avatar avatar)
        {
            if (clip == null)
            {
                return string.Empty;
            }

            string preferredRootName = KimodoRetargetAvatarUtility.ResolveSkeletonRootBoneName(avatar);
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            if (bindings == null || bindings.Length == 0)
            {
                return string.IsNullOrWhiteSpace(preferredRootName) ? string.Empty : preferredRootName;
            }

            var candidates = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                if (binding.type != typeof(Transform))
                {
                    continue;
                }

                if (!binding.propertyName.StartsWith("m_LocalPosition.", StringComparison.Ordinal) &&
                    !binding.propertyName.StartsWith("m_LocalRotation.", StringComparison.Ordinal))
                {
                    continue;
                }

                candidates.Add(binding.path ?? string.Empty);
            }

            if (candidates.Count == 0)
            {
                return string.IsNullOrWhiteSpace(preferredRootName) ? string.Empty : preferredRootName;
            }

            if (!string.IsNullOrWhiteSpace(preferredRootName))
            {
                foreach (string candidate in candidates)
                {
                    string leaf = candidate;
                    int slash = candidate.LastIndexOf('/');
                    if (slash >= 0 && slash < candidate.Length - 1)
                    {
                        leaf = candidate.Substring(slash + 1);
                    }

                    if (string.Equals(candidate, preferredRootName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(leaf, preferredRootName, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }

            string bestPath = string.Empty;
            float bestMovement = float.NegativeInfinity;
            int bestDepth = int.MaxValue;
            foreach (string path in candidates)
            {
                AnimationCurve posX = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalPosition.x"));
                AnimationCurve posZ = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), "m_LocalPosition.z"));
                float movement = ComputeCurveMovementXZ(posX, posZ);
                int depth = string.IsNullOrEmpty(path) ? 0 : path.Split('/').Length;
                if (movement > bestMovement || (Mathf.Approximately(movement, bestMovement) && depth < bestDepth))
                {
                    bestMovement = movement;
                    bestDepth = depth;
                    bestPath = path;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestPath))
            {
                return bestPath;
            }

            return string.IsNullOrWhiteSpace(preferredRootName) ? string.Empty : preferredRootName;
        }

        private static void SetAnimatorFloatCurve(AnimationClip clip, string propertyName, AnimationCurve curve)
        {
            clip.SetCurve(string.Empty, typeof(Animator), propertyName, curve);
        }

        private static bool TryGetAverageRootMotionDistance(AnimationClip clip, string prefix, out float averageDistance)
        {
            averageDistance = -1f;
            if (clip == null || string.IsNullOrWhiteSpace(prefix))
            {
                return false;
            }

            AnimationCurve curveX = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), prefix + ".x"));
            AnimationCurve curveZ = AnimationUtility.GetEditorCurve(clip, EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), prefix + ".z"));
            if (curveX == null || curveZ == null)
            {
                return false;
            }

            int keyCount = Mathf.Min(curveX.length, curveZ.length);
            if (keyCount < 2)
            {
                averageDistance = 0f;
                return true;
            }

            float totalDistance = 0f;
            Vector2 previous = new Vector2(curveX.keys[0].value, curveZ.keys[0].value);
            for (int i = 1; i < keyCount; i++)
            {
                Vector2 current = new Vector2(curveX.keys[i].value, curveZ.keys[i].value);
                totalDistance += Vector2.Distance(previous, current);
                previous = current;
            }

            averageDistance = totalDistance / (keyCount - 1);
            return true;
        }

        private static float ComputeCurveMovementXZ(AnimationCurve posX, AnimationCurve posZ)
        {
            if (posX == null || posZ == null)
            {
                return 0f;
            }

            int keyCount = Mathf.Min(posX.length, posZ.length);
            if (keyCount < 2)
            {
                return 0f;
            }

            float totalDistance = 0f;
            Vector2 previous = new Vector2(posX.keys[0].value, posZ.keys[0].value);
            for (int i = 1; i < keyCount; i++)
            {
                Vector2 current = new Vector2(posX.keys[i].value, posZ.keys[i].value);
                totalDistance += Vector2.Distance(previous, current);
                previous = current;
            }

            return totalDistance / (keyCount - 1);
        }
    }
}
