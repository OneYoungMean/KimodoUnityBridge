using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor.GenerationPipeline
{
    internal sealed class KimodoEditorRetargetService
    {
        public bool TryRetarget(KimodoPlayableClip clip, TimelineClip timelineClip, Avatar explicitAvatar, out string details)
        {
            details = string.Empty;
            bool didRetarget = false;

            if (clip.autoRetargetOnBinding)
            {
                bool retargetOk = KimodoRetargetPipeline.TryRetargetBakedClip(
                    clip,
                    timelineClip,
                    explicitAvatar,
                    out KimodoRetargetResultMode retargetMode,
                    out string retargetDetails);

                if (retargetOk)
                {
                    details = $"Retarget success ({retargetMode}). {retargetDetails}";
                    Debug.Log($"[Kimodo] {details}");
                    didRetarget = true;
                }
                else
                {
                    details = $"Retarget fallback to SOMA. {retargetDetails}";
                    Debug.LogWarning($"[Kimodo] {details}");
                }
            }
            else if (clip.CustomRetargetAvatar != null)
            {
                if (KimodoRetargetPipeline.TryRetargetClipToAvatar(
                        clip.clip,
                        clip.CustomRetargetAvatar,
                        out AnimationClip customRetargetClip,
                        out string customRetargetDetails))
                {
                    if (customRetargetClip != null)
                    {
                        UnityEditor.AnimationUtility.SetAnimationClipSettings(
                            customRetargetClip,
                            new UnityEditor.AnimationClipSettings
                            {
                                loopTime = false,
                                keepOriginalPositionY = true
                            });

                        string clipPath = UnityEditor.AssetDatabase.GetAssetPath(clip.clip);
                        if (!string.IsNullOrWhiteSpace(clipPath))
                        {
                            UnityEditor.EditorCurveBinding[] bindings = UnityEditor.AnimationUtility.GetCurveBindings(customRetargetClip);
                            clip.clip.ClearCurves();
                            for (int i = 0; i < bindings.Length; i++)
                            {
                                UnityEditor.EditorCurveBinding b = bindings[i];
                                AnimationCurve c = UnityEditor.AnimationUtility.GetEditorCurve(customRetargetClip, b);
                                clip.clip.SetCurve(b.path, b.type, b.propertyName, c);
                            }

                            clip.clip.frameRate = customRetargetClip.frameRate;
                            UnityEditor.EditorUtility.SetDirty(clip.clip);
                            UnityEditor.AssetDatabase.SaveAssets();
                            didRetarget = true;
                        }
                    }

                    details = $"Custom avatar retarget success. {customRetargetDetails}";
                    Debug.Log($"[Kimodo] {details}");
                }
                else
                {
                    details = $"Custom avatar retarget failed. {customRetargetDetails}";
                    Debug.LogWarning($"[Kimodo] {details}");
                }
            }

            if (!didRetarget)
            {
                return false;
            }

            ApplyCurveFilterAfterRetarget(clip.clip, clip.curveFilterOptions);
            return didRetarget;
        }

        private static void ApplyCurveFilterAfterRetarget(AnimationClip targetClip, KimodoCurveFilterOptions options)
        {
            if (targetClip == null || options == null)
            {
                return;
            }

            GameObject tempRoot = BuildHierarchyFromClipBindings(targetClip, "KimodoPostRetargetFilterRoot");
            tempRoot.hideFlags = HideFlags.HideAndDontSave;
            AnimationClip recordedClip = null;
            AnimationClip filteredClip = null;
            try
            {
                var recorder = new UnityEditor.Animations.GameObjectRecorder(tempRoot);
                recorder.BindComponentsOfType<Transform>(tempRoot, true);
                float fps = targetClip.frameRate > 0f ? targetClip.frameRate : 30f;
                int frameCount = Mathf.Max(2, Mathf.RoundToInt(targetClip.length * fps));
                float dt = 1f / fps;
                for (int f = 0; f < frameCount; f++)
                {
                    float t = f / fps;
                    targetClip.SampleAnimation(tempRoot, t);
                    recorder.TakeSnapshot(dt);
                }

                recordedClip = new AnimationClip
                {
                    name = $"{targetClip.name}_RetargetRecorded",
                    legacy = false,
                    frameRate = fps
                };

                var filter = new UnityEditor.Animations.CurveFilterOptions
                {
                    keyframeReduction = options.enabled,
                    positionError = Mathf.Clamp01(options.positionError),
                    rotationError = Mathf.Clamp01(options.rotationError),
                    scaleError = Mathf.Clamp01(options.positionError),
                    floatError = Mathf.Clamp01(options.floatError),
                    unrollRotation = true
                };

                recorder.SaveToClip(recordedClip, fps, filter);

                filteredClip = BuildFilteredRecordedClip(recordedClip, targetClip.name, fps);
                KimodoRuntimeUtility.CopyClipData(filteredClip, targetClip, forceNoLoopKeepY: true);

                if (options.ensureQuaternionContinuity)
                {
                    targetClip.EnsureQuaternionContinuity();
                }
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
                Object.DestroyImmediate(tempRoot);
            }
        }

        private static AnimationClip BuildFilteredRecordedClip(AnimationClip sourceClip, string clipName, float fps)
        {
            if (sourceClip == null)
            {
                return null;
            }

            string rootPath = ResolveRootBindingPath(sourceClip);
            var output = new AnimationClip
            {
                name = $"{clipName}_Filtered",
                legacy = sourceClip.legacy,
                frameRate = fps > 0f ? fps : sourceClip.frameRate
            };
            UnityEditor.AnimationUtility.SetAnimationClipSettings(
                output,
                new UnityEditor.AnimationClipSettings
                {
                    loopTime = false,
                    keepOriginalPositionY = true
                });

            UnityEditor.EditorCurveBinding[] bindings = UnityEditor.AnimationUtility.GetCurveBindings(sourceClip);
            for (int i = 0; i < bindings.Length; i++)
            {
                UnityEditor.EditorCurveBinding binding = bindings[i];
                if (!ShouldKeepRecordedBinding(binding, rootPath))
                {
                    continue;
                }

                AnimationCurve curve = UnityEditor.AnimationUtility.GetEditorCurve(sourceClip, binding);
                if (curve != null)
                {
                    output.SetCurve(binding.path, binding.type, binding.propertyName, curve);
                }
            }

            UnityEditor.EditorCurveBinding[] objectBindings = UnityEditor.AnimationUtility.GetObjectReferenceCurveBindings(sourceClip);
            for (int i = 0; i < objectBindings.Length; i++)
            {
                UnityEditor.EditorCurveBinding binding = objectBindings[i];
                UnityEditor.ObjectReferenceKeyframe[] curve = UnityEditor.AnimationUtility.GetObjectReferenceCurve(sourceClip, binding);
                if (curve != null)
                {
                    UnityEditor.AnimationUtility.SetObjectReferenceCurve(output, binding, curve);
                }
            }

            return output;
        }

        private static bool ShouldKeepRecordedBinding(UnityEditor.EditorCurveBinding binding, string rootPath)
        {
            string property = binding.propertyName ?? string.Empty;
            string path = binding.path ?? string.Empty;

            if (property.StartsWith("m_LocalRotation", System.StringComparison.Ordinal))
            {
                return true;
            }

            if (property.StartsWith("m_LocalPosition", System.StringComparison.Ordinal))
            {
                return string.Equals(path, rootPath, System.StringComparison.Ordinal);
            }

            if (property.StartsWith("m_LocalScale", System.StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static string ResolveRootBindingPath(AnimationClip clip)
        {
            if (clip == null)
            {
                return string.Empty;
            }

            UnityEditor.EditorCurveBinding[] bindings = UnityEditor.AnimationUtility.GetCurveBindings(clip);
            string bestPath = string.Empty;
            int bestDepth = int.MaxValue;

            for (int i = 0; i < bindings.Length; i++)
            {
                string property = bindings[i].propertyName ?? string.Empty;
                if (!property.StartsWith("m_Local", System.StringComparison.Ordinal))
                {
                    continue;
                }

                string path = bindings[i].path ?? string.Empty;
                int depth = string.IsNullOrEmpty(path) ? 0 : path.Split('/').Length;
                if (depth < bestDepth)
                {
                    bestDepth = depth;
                    bestPath = path;
                }
            }

            return bestPath;
        }

        private static GameObject BuildHierarchyFromClipBindings(AnimationClip clipAsset, string rootName)
        {
            var root = new GameObject(rootName);
            var created = new System.Collections.Generic.Dictionary<string, Transform>(System.StringComparer.Ordinal);
            created[string.Empty] = root.transform;

            UnityEditor.EditorCurveBinding[] bindings = UnityEditor.AnimationUtility.GetCurveBindings(clipAsset);
            for (int i = 0; i < bindings.Length; i++)
            {
                string path = bindings[i].path ?? string.Empty;
                if (created.ContainsKey(path))
                {
                    continue;
                }

                EnsurePath(path, root.transform, created);
            }

            return root;
        }

        private static Transform EnsurePath(string path, Transform root, System.Collections.Generic.Dictionary<string, Transform> cache)
        {
            if (cache.TryGetValue(path, out Transform existing))
            {
                return existing;
            }

            if (string.IsNullOrEmpty(path))
            {
                cache[string.Empty] = root;
                return root;
            }

            int split = path.LastIndexOf('/');
            string parentPath = split > 0 ? path.Substring(0, split) : string.Empty;
            string selfName = split >= 0 ? path.Substring(split + 1) : path;
            Transform parent = EnsurePath(parentPath, root, cache);

            var go = new GameObject(string.IsNullOrWhiteSpace(selfName) ? "Bone" : selfName);
            Transform t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
            cache[path] = t;
            return t;
        }

    }
}

