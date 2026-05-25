#if UNITY_EDITOR
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using KimodoUnityMotionTools.Generation.Pipeline;

namespace KimodoUnityMotionTools
{
    // Kept class name for compatibility with existing call sites.
    public static class KimodoAnimationBaker
    {
        [Serializable]
        private class MotionJsonData
        {
            public int num_frames;
            public int num_joints;
            public int fps;
            public string[] joint_names;
            public int[] joint_parents;
            public List<List<List<float>>> positions;
            public List<float> local_rot_quats;
        }

        public static bool BakeIntoClip(
            AnimationClip targetClip,
            string motionJson,
            KimodoBakeSkeletonType skeletonType,
            string modelName,
            KimodoCurveFilterOptions curveFilterOptions,
            out string error)
        {
            error = string.Empty;

            if (targetClip == null)
            {
                error = "Target clip is null.";
                return false;
            }

            MotionJsonData data;
            try
            {
                data = ParseMotionJsonFlexible(motionJson);
            }
            catch (Exception e)
            {
                error = $"Failed to parse motionJson: {e.Message}";
                return false;
            }

            if (!ValidateData(data, out error))
            {
                return false;
            }

            if (skeletonType != KimodoBakeSkeletonType.SOMA)
            {
                error = "Only SOMA bake mode is supported.";
                return false;
            }

            float fps = data.fps > 0 ? data.fps : 30f;
            int positionFrames = data.positions != null ? data.positions.Count : 0;
            int frameHint = data.num_frames > 0 ? data.num_frames : positionFrames;
            int frameCount = positionFrames > 0
                ? Mathf.Min(frameHint, positionFrames)
                : Mathf.Max(2, frameHint);

            targetClip.ClearCurves();
            AnimationUtility.SetAnimationClipSettings(
                targetClip,
                new AnimationClipSettings
                {
                    loopTime = false,
                    keepOriginalPositionY = true
                });

            var rawClip = new AnimationClip
            {
                name = $"{targetClip.name}_Raw",
                legacy = false,
                frameRate = fps
            };
            BakeSomaCurvesDirect(rawClip, data, fps, frameCount);

            if (!TrySaveWithRecorder(rawClip, targetClip, data, fps, frameCount, modelName, curveFilterOptions, out error))
            {
                return false;
            }
            EditorUtility.SetDirty(targetClip);
            return true;
        }

        private static bool TrySaveWithRecorder(
            AnimationClip rawClip,
            AnimationClip targetClip,
            MotionJsonData data,
            float fps,
            int frameCount,
            string modelName,
            KimodoCurveFilterOptions options,
            out string error)
        {
            error = string.Empty;
            if (rawClip == null || targetClip == null)
            {
                error = "Raw clip or target clip is null.";
                return false;
            }

            int jointCount = Mathf.Min(data.joint_names.Length, data.num_joints > 0 ? data.num_joints : data.joint_names.Length);
            if (jointCount <= 0)
            {
                error = "Joint count is invalid for recorder save.";
                return false;
            }
            int rootJoint = FindRootJointIndex(data, jointCount);
            string[] jointPaths = BuildJointPaths(data, jointCount);

            GameObject samplerRoot = null;
            AnimationClip recordedClip = null;
            AnimationClip filteredClip = null;
            try
            {
                samplerRoot = CreateSamplerHierarchyForRecording(data, jointCount, modelName);
                var recorder = new GameObjectRecorder(samplerRoot);
                recorder.BindComponentsOfType<Transform>(samplerRoot, true);

                float dt = fps > 0f ? 1f / fps : 1f / 30f;
                for (int f = 0; f < frameCount; f++)
                {
                    float t = f / fps;
                    rawClip.SampleAnimation(samplerRoot, t);
                    recorder.TakeSnapshot(dt);
                }

                recordedClip = new AnimationClip
                {
                    name = $"{targetClip.name}_Recorded",
                    legacy = false,
                    frameRate = fps
                };

                CurveFilterOptions filter = BuildCurveFilterOptions(options);
                recorder.SaveToClip(recordedClip, fps, filter);

                filteredClip = BuildFilteredRecordedClip(recordedClip, jointPaths, rootJoint, targetClip.name, fps);
                CopyClipData(filteredClip, targetClip);

                if ((options ?? new KimodoCurveFilterOptions()).ensureQuaternionContinuity)
                {
                    targetClip.EnsureQuaternionContinuity();
                }

                return true;
            }
            catch (Exception ex)
            {
                error = $"Recorder SaveToClip failed: {ex.Message}";
                return false;
            }
            finally
            {
                if (filteredClip != null)
                {
                    UnityEngine.Object.DestroyImmediate(filteredClip);
                }
                if (recordedClip != null)
                {
                    UnityEngine.Object.DestroyImmediate(recordedClip);
                }
                if (samplerRoot != null)
                {
                    DestroySamplerHierarchyRoot(samplerRoot);
                }
            }
        }

        private static CurveFilterOptions BuildCurveFilterOptions(KimodoCurveFilterOptions options)
        {
            KimodoCurveFilterOptions effective = options ?? new KimodoCurveFilterOptions();
            float positionError = Mathf.Clamp01(effective.positionError);
            float rotationError = Mathf.Clamp01(effective.rotationError);
            float floatError = Mathf.Clamp01(effective.floatError);

            return new CurveFilterOptions
            {
                keyframeReduction = effective.enabled,
                positionError = positionError,
                scaleError = positionError,
                floatError = floatError,
                rotationError = rotationError,
                unrollRotation = true
            };
        }

        private static GameObject CreateSamplerHierarchyForRecording(MotionJsonData data, int jointCount, string modelName)
        {
            var root = new GameObject("__KimodoRecorderRoot")
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            if (!TryLoadAndBuildAvatarHierarchy(modelName, root.transform, out Avatar avatar, out string buildError))
            {
                UnityEngine.Object.DestroyImmediate(root);
                throw new InvalidOperationException($"Failed to build recorder hierarchy from runtime avatar: {buildError}");
            }

            string skeletonRootName = KimodoRuntimeAvatarSkeletonBuilder.ResolveSkeletonRootName(avatar);
            Transform recordingRoot = string.IsNullOrWhiteSpace(skeletonRootName)
                ? root.transform
                : root.transform.Find(skeletonRootName);
            if (recordingRoot == null)
            {
                UnityEngine.Object.DestroyImmediate(root);
                throw new InvalidOperationException($"Avatar skeleton root '{skeletonRootName}' was not found in recorder hierarchy.");
            }

            return recordingRoot.gameObject;
        }

        private static AnimationClip BuildFilteredRecordedClip(
            AnimationClip sourceClip,
            string[] jointPaths,
            int rootJoint,
            string clipName,
            float fps)
        {
            if (sourceClip == null)
            {
                return null;
            }

            var output = new AnimationClip
            {
                name = $"{clipName}_Filtered",
                legacy = sourceClip.legacy,
                frameRate = fps > 0f ? fps : sourceClip.frameRate
            };
            AnimationUtility.SetAnimationClipSettings(output, AnimationUtility.GetAnimationClipSettings(sourceClip));

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(sourceClip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                if (!ShouldKeepRecordedBinding(binding, jointPaths, rootJoint))
                {
                    continue;
                }

                AnimationCurve curve = AnimationUtility.GetEditorCurve(sourceClip, binding);
                if (curve != null)
                {
                    output.SetCurve(binding.path, binding.type, binding.propertyName, curve);
                }
            }

            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(sourceClip);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                EditorCurveBinding binding = objectBindings[i];
                ObjectReferenceKeyframe[] curve = AnimationUtility.GetObjectReferenceCurve(sourceClip, binding);
                if (curve != null)
                {
                    AnimationUtility.SetObjectReferenceCurve(output, binding, curve);
                }
            }

            return output;
        }

        private static bool ShouldKeepRecordedBinding(EditorCurveBinding binding, string[] jointPaths, int rootJoint)
        {
            if (jointPaths == null || jointPaths.Length == 0)
            {
                return true;
            }

            string property = binding.propertyName ?? string.Empty;
            if (!property.StartsWith("m_Local", StringComparison.Ordinal))
            {
                return true;
            }

            var allowedPaths = new HashSet<string>(jointPaths, StringComparer.Ordinal);
            string rootPath = (rootJoint >= 0 && rootJoint < jointPaths.Length) ? jointPaths[rootJoint] : string.Empty;

            if (property.StartsWith("m_LocalRotation", StringComparison.Ordinal))
            {
                return allowedPaths.Contains(binding.path ?? string.Empty);
            }

            if (property.StartsWith("m_LocalPosition", StringComparison.Ordinal))
            {
                return string.Equals(binding.path ?? string.Empty, rootPath, StringComparison.Ordinal);
            }

            if (property.StartsWith("m_LocalScale", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static void CopyClipData(AnimationClip sourceClip, AnimationClip targetClip)
        {
            if (sourceClip == null || targetClip == null)
            {
                return;
            }

            targetClip.ClearCurves();
            targetClip.frameRate = sourceClip.frameRate > 0f ? sourceClip.frameRate : targetClip.frameRate;
            AnimationUtility.SetAnimationClipSettings(targetClip, AnimationUtility.GetAnimationClipSettings(sourceClip));

            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(sourceClip);
            for (int i = 0; i < bindings.Length; i++)
            {
                EditorCurveBinding binding = bindings[i];
                AnimationCurve curve = AnimationUtility.GetEditorCurve(sourceClip, binding);
                if (curve != null)
                {
                    targetClip.SetCurve(binding.path, binding.type, binding.propertyName, curve);
                }
            }
            var resultBinding = AnimationUtility.GetCurveBindings(targetClip);

            EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(sourceClip);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                EditorCurveBinding binding = objectBindings[i];
                ObjectReferenceKeyframe[] curve = AnimationUtility.GetObjectReferenceCurve(sourceClip, binding);
                if (curve != null)
                {
                    AnimationUtility.SetObjectReferenceCurve(targetClip, binding, curve);
                }
            }
        }

        private static bool TryLoadAndBuildAvatarHierarchy(string modelName, Transform root, out Avatar avatar, out string error)
        {
            avatar = null;
            error = string.Empty;

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out avatar, out string loadError))
            {
                error = loadError;
                return false;
            }

            if (!KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(avatar, root, out string buildError))
            {
                error = buildError;
                return false;
            }

            return true;
        }

        private static void DestroySamplerHierarchyRoot(GameObject samplingObject)
        {
            if (samplingObject == null)
            {
                return;
            }

            Transform t = samplingObject.transform;
            while (t.parent != null)
            {
                t = t.parent;
            }

            UnityEngine.Object.DestroyImmediate(t.gameObject);
        }

        private static MotionJsonData ParseMotionJsonFlexible(string motionJson)
        {
            JToken token = JToken.Parse(motionJson);
            if (token.Type != JTokenType.Object)
            {
                throw new Exception("motionJson root is not an object.");
            }

            JObject obj = (JObject)token;
            MotionJsonData data = obj.ToObject<MotionJsonData>() ?? new MotionJsonData();

            if (data.positions != null && data.positions.Count > 0)
            {
                return data;
            }

            JToken posed = obj["posed_joints"];
            if (posed != null && posed.Type == JTokenType.Array)
            {
                data.positions = posed.ToObject<List<List<List<float>>>>();
                if (data.positions != null && data.positions.Count > 0)
                {
                    if (data.num_frames <= 0) data.num_frames = data.positions.Count;
                    if (data.num_joints <= 0 && data.positions[0] != null) data.num_joints = data.positions[0].Count;
                    return data;
                }
            }

            JToken flat = obj["joints"];
            if (flat != null && flat.Type == JTokenType.Array)
            {
                List<float> flatVals = flat.ToObject<List<float>>();
                int frames = data.num_frames;
                int joints = data.num_joints;
                if (frames > 0 && joints > 0 && flatVals != null && flatVals.Count >= frames * joints * 3)
                {
                    data.positions = new List<List<List<float>>>(frames);
                    int ptr = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        List<List<float>> frame = new List<List<float>>(joints);
                        for (int j = 0; j < joints; j++)
                        {
                            frame.Add(new List<float> { flatVals[ptr], flatVals[ptr + 1], flatVals[ptr + 2] });
                            ptr += 3;
                        }
                        data.positions.Add(frame);
                    }
                    return data;
                }
            }

            return data;
        }

        private static bool ValidateData(MotionJsonData data, out string error)
        {
            error = string.Empty;
            if (data == null)
            {
                error = "Parsed motion data is null.";
                return false;
            }
            if (data.positions == null || data.positions.Count == 0)
            {
                if (data.local_rot_quats == null || data.local_rot_quats.Count == 0)
                {
                    error = "No positions or local_rot_quats in motion data.";
                    return false;
                }
            }
            if (data.joint_names == null || data.joint_names.Length == 0)
            {
                error = "No joint_names in motion data.";
                return false;
            }
            int positionFrames = data.positions != null ? data.positions.Count : 0;
            int frameHint = data.num_frames > 0 ? data.num_frames : positionFrames;
            if (frameHint < 2)
            {
                error = "Need at least 2 frames for baking.";
                return false;
            }
            return true;
        }

        private static void BakeSomaCurvesDirect(AnimationClip targetClip, MotionJsonData data, float fps, int frameCount)
        {
            int jointCount = Mathf.Min(data.joint_names.Length, data.num_joints > 0 ? data.num_joints : data.joint_names.Length);
            bool hasPositions = data.positions != null && data.positions.Count > 0;
            int rotJointCount = jointCount;
            bool hasRotations = false;
            if (data.local_rot_quats != null && data.local_rot_quats.Count > 0 && frameCount > 0)
            {
                int availableJointCount = data.local_rot_quats.Count / (frameCount * 4);
                rotJointCount = Mathf.Min(jointCount, availableJointCount);
                hasRotations = rotJointCount > 0;
            }
            int rootJoint = FindRootJointIndex(data, jointCount);
            string[] jointPaths = BuildJointPaths(data, jointCount);
            Debug.Log($"[Kimodo] BakeSomaCurves frameCount={frameCount}, jointCount={jointCount}, rootJoint={rootJoint}, local_rot_quats={(data.local_rot_quats != null ? data.local_rot_quats.Count : 0)}, rotJointCount={rotJointCount}, hasRotations={hasRotations}");

            for (int joint = 0; joint < jointCount; joint++)
            {
                string path = jointPaths[joint];

                // Positions are world-space from Kimodo output. For hierarchy-consistent baking,
                // only bake root translation and drive child motion by local rotations.
                if (hasPositions && joint == rootJoint)
                {
                    AnimationCurve px = new AnimationCurve();
                    AnimationCurve py = new AnimationCurve();
                    AnimationCurve pz = new AnimationCurve();

                    for (int f = 0; f < frameCount; f++)
                    {
                        float t = f / fps;
                        Vector3 p = ReadPos(data, f, joint);
                        px.AddKey(t, p.x);
                        py.AddKey(t, p.y);
                        pz.AddKey(t, p.z);
                    }

                    targetClip.SetCurve(path, typeof(Transform), "m_LocalPosition.x", px);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", py);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalPosition.z", pz);
                }

                if (hasRotations && joint < rotJointCount)
                {
                    AnimationCurve qx = new AnimationCurve();
                    AnimationCurve qy = new AnimationCurve();
                    AnimationCurve qz = new AnimationCurve();
                    AnimationCurve qw = new AnimationCurve();

                    for (int f = 0; f < frameCount; f++)
                    {
                        float t = f / fps;
                        Quaternion q = ReadLocalQuat(data, f, joint, rotJointCount);
                        qx.AddKey(t, q.x);
                        qy.AddKey(t, q.y);
                        qz.AddKey(t, q.z);
                        qw.AddKey(t, q.w);
                    }

                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.x", qx);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.y", qy);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.z", qz);
                    targetClip.SetCurve(path, typeof(Transform), "m_LocalRotation.w", qw);
                }
            }

        }

        private static Vector3 ReadPos(MotionJsonData data, int frame, int joint)
        {
            List<float> p = data.positions[frame][joint];
            Vector3 kimodoPos = new Vector3(p[0], p[1], p[2]);
            return ConvertKimodoPosition(kimodoPos);
        }

        private static Quaternion ReadLocalQuat(MotionJsonData data, int frame, int joint, int jointCount)
        {
            int baseIdx = (frame * jointCount + joint) * 4;
            float w = data.local_rot_quats[baseIdx + 0];
            float x = data.local_rot_quats[baseIdx + 1];
            float y = data.local_rot_quats[baseIdx + 2];
            float z = data.local_rot_quats[baseIdx + 3];
            Quaternion q = new Quaternion(x, y, z, w);
            q = q.normalized;
            return ConvertKimodoRotation(q);
        }

        private static Vector3 ConvertKimodoPosition(Vector3 src)
        {
            // Keep consistent with KimodoBVHLoader's Kimodo -> Unity conversion.
            return new Vector3(-src.x, src.y, src.z);
        }

        private static Quaternion ConvertKimodoRotation(Quaternion src)
        {
            // Keep consistent with KimodoBVHLoader's Kimodo -> Unity conversion.
            return new Quaternion(src.x, -src.y, -src.z, src.w);
        }


        private static int FindRootJointIndex(MotionJsonData data, int jointCount)
        {
            if (jointCount <= 0)
            {
                return 0;
            }

            if (data.joint_parents != null && data.joint_parents.Length >= jointCount)
            {
                for (int i = 0; i < jointCount; i++)
                {
                    if (data.joint_parents[i] < 0)
                    {
                        return i;
                    }
                }
            }

            return 0;
        }

        private static string[] BuildJointPaths(MotionJsonData data, int jointCount)
        {
            string[] paths = new string[jointCount];
            bool[] visiting = new bool[jointCount];
            for (int i = 0; i < jointCount; i++)
            {
                paths[i] = BuildJointPathRecursive(data, i, jointCount, paths, visiting);
            }

            return paths;
        }

        private static string BuildJointPathRecursive(MotionJsonData data, int joint, int jointCount, string[] cache, bool[] visiting)
        {
            if (joint < 0 || joint >= jointCount)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(cache[joint]))
            {
                return cache[joint];
            }

            if (visiting[joint])
            {
                cache[joint] = SanitizeName(data.joint_names[joint]);
                return cache[joint];
            }

            visiting[joint] = true;
            string safeName = SanitizeName(data.joint_names[joint]);
            int parent = (data.joint_parents != null && joint < data.joint_parents.Length) ? data.joint_parents[joint] : -1;
            if (parent >= 0 && parent < jointCount && parent != joint)
            {
                string parentPath = BuildJointPathRecursive(data, parent, jointCount, cache, visiting);
                cache[joint] = string.IsNullOrWhiteSpace(parentPath) ? safeName : $"{parentPath}/{safeName}";
            }
            else
            {
                cache[joint] = safeName;
            }

            visiting[joint] = false;
            return cache[joint];
        }

        private static string SanitizeName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return "joint";
            }

            return input.Replace("/", "_").Replace("\\", "_").Replace(":", "_");
        }
    }
}
#endif
