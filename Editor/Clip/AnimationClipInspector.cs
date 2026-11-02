using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KimodoBridge.Editor
{
    // Transient result snapshot; intentionally not Unity-serializable.
    public sealed class AnimationClipSettingsSnapshot
    {
        public bool LoopTime { get; internal set; }
        public bool KeepOriginalPositionXZ { get; internal set; }
        public bool KeepOriginalPositionY { get; internal set; }
        public bool KeepOriginalOrientation { get; internal set; }
        public bool RootMotionEnabled => !KeepOriginalPositionXZ;
    }

    // Transient result snapshot; intentionally not Unity-serializable.
    public sealed class AnimationClipCurveSnapshot
    {
        public string Path { get; internal set; }
        public string Type { get; internal set; }
        public string Property { get; internal set; }
        public int KeyCount { get; internal set; }
        public bool IsRootMotion { get; internal set; }
    }

    // Transient result snapshot; intentionally not Unity-serializable.
    public sealed class AnimationClipConsumerSnapshot
    {
        public string Controller { get; internal set; }
        public string ControllerPath { get; internal set; }
        public string Layer { get; internal set; }
        public string State { get; internal set; }
        public string BlendSlot { get; internal set; }
        public float? Threshold { get; internal set; }
    }

    // Transient result snapshot; intentionally not Unity-serializable.
    public sealed class AnimationClipRootMotionSnapshot
    {
        public Vector3 FirstPosition { get; internal set; }
        public Vector3 LastPosition { get; internal set; }
        public Vector2 DisplacementXZ { get; internal set; }
        public float DisplacementXZMeters { get; internal set; }
        public float DerivedSpeedMetersPerSecond { get; internal set; }
        public IReadOnlyList<AnimationClipCurveSnapshot> Curves { get; internal set; }
    }

    // Transient result snapshot; intentionally not Unity-serializable.
    public sealed class AnimationClipInspection
    {
        public string Name { get; internal set; }
        public string AssetPath { get; internal set; }
        public float DurationSeconds { get; internal set; }
        public float FrameRate { get; internal set; }
        public AnimationClipSettingsSnapshot Settings { get; internal set; }
        public IReadOnlyList<AnimationClipCurveSnapshot> Curves { get; internal set; }
        public IReadOnlyList<AnimationClipCurveSnapshot> RootMotionCurves { get; internal set; }
        public Vector2 RootDisplacementXZ { get; internal set; }
        public float RootDisplacementXZMeters { get; internal set; }
        public float DerivedSpeedMetersPerSecond { get; internal set; }
        public string RigType { get; internal set; }
        public IReadOnlyList<AnimationClipConsumerSnapshot> Consumers { get; internal set; }
    }

    /// <summary>
    /// Shared read/write mapping for AnimationClip facts used by Inspector UI and Command.
    /// Keep Unity asset inspection here so the two surfaces cannot drift into different rules.
    /// </summary>
    public static class AnimationClipInspector
    {
        public static AnimationClipInspection Read(AnimationClip clip, bool includeConsumers = true)
        {
            if (clip == null) throw new InvalidOperationException("AnimationClip is null.");
            AnimationClipSettingsSnapshot settings = ReadSettings(clip);
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            List<AnimationClipCurveSnapshot> curves = bindings.Select(binding =>
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                return new AnimationClipCurveSnapshot
                {
                    Path = binding.path ?? string.Empty,
                    Type = binding.type != null ? binding.type.FullName : string.Empty,
                    Property = binding.propertyName ?? string.Empty,
                    KeyCount = curve != null ? curve.length : 0,
                    IsRootMotion = IsRootMotionBinding(binding)
                };
            }).ToList();
            Vector3 first = EvaluateRootPosition(clip, 0f, bindings);
            Vector3 last = EvaluateRootPosition(clip, Mathf.Max(0f, clip.length), bindings);
            Vector2 displacement = new Vector2(last.x - first.x, last.z - first.z);
            List<AnimationClipConsumerSnapshot> consumers = includeConsumers
                ? FindConsumerSnapshots(clip)
                : new List<AnimationClipConsumerSnapshot>();
            return new AnimationClipInspection
            {
                Name = clip.name,
                AssetPath = AssetDatabase.GetAssetPath(clip) ?? string.Empty,
                DurationSeconds = clip.length,
                FrameRate = clip.frameRate,
                Settings = settings,
                Curves = curves,
                RootMotionCurves = curves.Where(item => item.IsRootMotion).ToList(),
                RootDisplacementXZ = displacement,
                RootDisplacementXZMeters = displacement.magnitude,
                DerivedSpeedMetersPerSecond = clip.length > Mathf.Epsilon ? displacement.magnitude / clip.length : 0f,
                RigType = clip.humanMotion ? "humanoid" : "generic",
                Consumers = consumers
            };
        }

        public static AnimationClipSettingsSnapshot ReadSettings(AnimationClip clip)
        {
            if (clip == null) throw new InvalidOperationException("AnimationClip is null.");
            return FromUnitySettings(AnimationUtility.GetAnimationClipSettings(clip));
        }

        public static AnimationClipSettingsSnapshot WriteSettings(AnimationClip clip, bool? loopTime = null,
            bool? keepOriginalPositionXZ = null, bool? rootMotionEnabled = null)
        {
            if (clip == null) throw new InvalidOperationException("AnimationClip is null.");
            if (!loopTime.HasValue && !keepOriginalPositionXZ.HasValue && !rootMotionEnabled.HasValue)
                throw new InvalidOperationException("At least one clip setting is required.");
            if (keepOriginalPositionXZ.HasValue && rootMotionEnabled.HasValue &&
                keepOriginalPositionXZ.Value == rootMotionEnabled.Value)
                throw new InvalidOperationException("keep_original_position_xz and root_motion_enabled describe the same setting and conflict.");
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            if (loopTime.HasValue) settings.loopTime = loopTime.Value;
            if (keepOriginalPositionXZ.HasValue) settings.keepOriginalPositionXZ = keepOriginalPositionXZ.Value;
            if (rootMotionEnabled.HasValue)
            {
                settings.keepOriginalPositionXZ = !rootMotionEnabled.Value;
            }
            if (!settings.keepOriginalPositionXZ && !clip.humanMotion &&
                !AnimationUtility.GetCurveBindings(clip).Any(IsRootMotionBinding))
                throw new InvalidOperationException("root motion cannot be enabled for a generic Clip without root motion curves.");
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssetIfDirty(clip);
            return ReadSettings(clip);
        }

        public static IReadOnlyList<AnimationClipCurveSnapshot> ReadCurves(AnimationClip clip)
        {
            return Read(clip, includeConsumers: false).Curves;
        }

        public static AnimationClipRootMotionSnapshot ReadRootMotion(AnimationClip clip)
        {
            AnimationClipInspection inspection = Read(clip, includeConsumers: false);
            Vector3 first = EvaluateRootPosition(clip, 0f, AnimationUtility.GetCurveBindings(clip));
            Vector3 last = EvaluateRootPosition(clip, Mathf.Max(0f, clip.length), AnimationUtility.GetCurveBindings(clip));
            return new AnimationClipRootMotionSnapshot
            {
                FirstPosition = first,
                LastPosition = last,
                DisplacementXZ = inspection.RootDisplacementXZ,
                DisplacementXZMeters = inspection.RootDisplacementXZMeters,
                DerivedSpeedMetersPerSecond = inspection.DerivedSpeedMetersPerSecond,
                Curves = inspection.RootMotionCurves
            };
        }

        public static IReadOnlyList<AnimationClipConsumerSnapshot> ReadConsumers(AnimationClip clip)
        {
            return Read(clip, includeConsumers: true).Consumers;
        }

        public static JObject InspectJson(AnimationClip clip)
        {
            return InspectJson(clip, includeConsumers: true);
        }

        public static JObject InspectJson(AnimationClip clip, bool includeConsumers)
        {
            if (clip == null) throw new InvalidOperationException("AnimationClip is null.");

            AnimationClipInspection inspection = Read(clip, includeConsumers);
            AnimationClipSettingsSnapshot settings = inspection.Settings;
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            List<string> rootCurves = bindings
                .Where(IsRootMotionBinding)
                .Select(binding => string.IsNullOrWhiteSpace(binding.path)
                    ? binding.propertyName
                    : $"{binding.path}:{binding.propertyName}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();

            var result = new JObject
            {
                ["clip"] = inspection.Name,
                ["path"] = inspection.AssetPath,
                ["duration_seconds"] = inspection.DurationSeconds,
                ["clip_frame_rate"] = inspection.FrameRate,
                ["loop_time"] = settings.LoopTime,
                ["keep_original_position_xz"] = settings.KeepOriginalPositionXZ,
                ["keep_original_position_y"] = settings.KeepOriginalPositionY,
                ["keep_original_orientation"] = settings.KeepOriginalOrientation,
                ["root_motion_enabled"] = settings.RootMotionEnabled,
                ["root_motion_curves"] = new JArray(rootCurves),
                ["root_displacement_xz_m"] = inspection.RootDisplacementXZMeters,
                ["root_displacement_xz"] = new JArray(inspection.RootDisplacementXZ.x, inspection.RootDisplacementXZ.y),
                ["derived_speed_mps"] = inspection.DerivedSpeedMetersPerSecond,
                ["rig_type"] = inspection.RigType,
                ["curve_count"] = bindings.Length,
                ["consumers"] = ToJsonConsumers(inspection.Consumers)
            };
            return result;
        }

        public static JObject SetSettingsJson(AnimationClip clip, JObject requested)
        {
            if (clip == null) throw new InvalidOperationException("AnimationClip is null.");
            if (requested == null || !requested.Properties().Any())
                throw new InvalidOperationException("At least one clip setting is required.");

            bool? loopTimeValue = null;
            bool? keepXZValue = null;
            bool? rootMotionValue = null;
            if (requested.TryGetValue("loop_time", StringComparison.OrdinalIgnoreCase, out JToken loopTime))
            {
                loopTimeValue = RequireBoolean(loopTime, "loop_time");
            }
            if (requested.TryGetValue("keep_original_position_xz", StringComparison.OrdinalIgnoreCase, out JToken keepXZ))
            {
                keepXZValue = RequireBoolean(keepXZ, "keep_original_position_xz");
            }
            if (requested.TryGetValue("root_motion_enabled", StringComparison.OrdinalIgnoreCase, out JToken rootMotion))
            {
                rootMotionValue = RequireBoolean(rootMotion, "root_motion_enabled");
            }
            if (!loopTimeValue.HasValue && !keepXZValue.HasValue && !rootMotionValue.HasValue)
                throw new InvalidOperationException("Supported settings are loop_time, keep_original_position_xz, and root_motion_enabled.");
            WriteSettings(clip, loopTimeValue, keepXZValue, rootMotionValue);
            return InspectJson(clip);
        }

        private static AnimationClipSettingsSnapshot FromUnitySettings(AnimationClipSettings settings) => new AnimationClipSettingsSnapshot
        {
            LoopTime = settings.loopTime,
            KeepOriginalPositionXZ = settings.keepOriginalPositionXZ,
            KeepOriginalPositionY = settings.keepOriginalPositionY,
            KeepOriginalOrientation = settings.keepOriginalOrientation
        };

        private static bool RequireBoolean(JToken value, string name)
        {
            if (value == null || value.Type != JTokenType.Boolean)
                throw new InvalidOperationException($"{name} must be a boolean.");
            return value.Value<bool>();
        }

        private static bool IsRootMotionBinding(EditorCurveBinding binding)
        {
            string property = binding.propertyName ?? string.Empty;
            return property.StartsWith("RootT.", StringComparison.OrdinalIgnoreCase) ||
                property.StartsWith("RootQ.", StringComparison.OrdinalIgnoreCase) ||
                property.IndexOf("m_LocalPosition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                property.IndexOf("m_LocalRotation", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Vector3 EvaluateRootPosition(AnimationClip clip, float time, EditorCurveBinding[] bindings)
        {
            return new Vector3(
                EvaluateRootChannel(clip, time, bindings, "x", 0f),
                EvaluateRootChannel(clip, time, bindings, "y", 0f),
                EvaluateRootChannel(clip, time, bindings, "z", 0f));
        }

        private static float EvaluateRootChannel(AnimationClip clip, float time, EditorCurveBinding[] bindings, string axis, float fallback)
        {
            EditorCurveBinding? match = bindings.FirstOrDefault(binding =>
                IsRootMotionBinding(binding) &&
                binding.propertyName.EndsWith("." + axis, StringComparison.OrdinalIgnoreCase));
            if (!match.HasValue) return fallback;
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, match.Value);
            return curve == null ? fallback : curve.Evaluate(time);
        }

        private static JArray FindConsumers(AnimationClip clip)
        {
            var consumers = new JArray();
            string clipPath = AssetDatabase.GetAssetPath(clip);
            foreach (string guid in AssetDatabase.FindAssets("t:AnimatorController"))
            {
                string controllerPath = AssetDatabase.GUIDToAssetPath(guid);
                AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
                if (controller == null) continue;
                for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
                {
                    CollectStateConsumers(controller, controller.layers[layerIndex].stateMachine, clip, controllerPath, controller.layers[layerIndex].name, consumers);
                }
            }
            return consumers;
        }

        private static List<AnimationClipConsumerSnapshot> FindConsumerSnapshots(AnimationClip clip)
        {
            return FindConsumers(clip).OfType<JObject>().Select(item => new AnimationClipConsumerSnapshot
            {
                Controller = item.Value<string>("controller") ?? string.Empty,
                ControllerPath = item.Value<string>("controller_path") ?? string.Empty,
                Layer = item.Value<string>("layer") ?? string.Empty,
                State = item.Value<string>("state") ?? string.Empty,
                BlendSlot = item["blend_slot"]?.ToString() ?? string.Empty,
                Threshold = item.Value<float?>("threshold")
            }).ToList();
        }

        private static JArray ToJsonConsumers(IReadOnlyList<AnimationClipConsumerSnapshot> consumers)
        {
            var result = new JArray();
            foreach (AnimationClipConsumerSnapshot consumer in consumers ?? Array.Empty<AnimationClipConsumerSnapshot>())
            {
                var item = new JObject
                {
                    ["controller"] = consumer.Controller,
                    ["controller_path"] = consumer.ControllerPath,
                    ["layer"] = consumer.Layer,
                    ["state"] = consumer.State,
                    ["blend_slot"] = consumer.BlendSlot
                };
                if (consumer.Threshold.HasValue)
                    item["threshold"] = consumer.Threshold.Value;
                result.Add(item);
            }
            return result;
        }

        private static void CollectStateConsumers(
            AnimatorController controller,
            AnimatorStateMachine stateMachine,
            AnimationClip clip,
            string controllerPath,
            string layer,
            JArray consumers)
        {
            if (stateMachine == null) return;
            foreach (ChildAnimatorState child in stateMachine.states)
            {
                if (child.state == null) continue;
                CollectMotionConsumers(child.state.motion, clip, controllerPath, layer, child.state.name, consumers);
            }
            foreach (ChildAnimatorStateMachine child in stateMachine.stateMachines)
            {
                CollectStateConsumers(controller, child.stateMachine, clip, controllerPath, layer, consumers);
            }
        }

        private static void CollectMotionConsumers(Motion motion, AnimationClip clip, string controllerPath, string layer, string state, JArray consumers)
        {
            if (motion is AnimationClip animationClip && animationClip == clip)
            {
                consumers.Add(new JObject
                {
                    ["controller"] = System.IO.Path.GetFileNameWithoutExtension(controllerPath),
                    ["controller_path"] = controllerPath,
                    ["layer"] = layer,
                    ["state"] = state,
                    ["blend_slot"] = 0
                });
                return;
            }
            if (motion is BlendTree blendTree)
            {
                ChildMotion[] children = blendTree.children;
                for (int i = 0; i < children.Length; i++)
                {
                    if (children[i].motion is AnimationClip childClip && childClip == clip)
                    {
                        consumers.Add(new JObject
                        {
                            ["controller"] = System.IO.Path.GetFileNameWithoutExtension(controllerPath),
                            ["controller_path"] = controllerPath,
                            ["layer"] = layer,
                            ["state"] = state,
                            ["blend_slot"] = i,
                            ["threshold"] = children[i].threshold
                        });
                    }
                    else if (children[i].motion is BlendTree nested)
                    {
                        CollectNestedBlendConsumers(nested, clip, controllerPath, layer, state, consumers, i.ToString());
                    }
                }
            }
        }

        private static void CollectNestedBlendConsumers(BlendTree tree, AnimationClip clip, string controllerPath, string layer, string state, JArray consumers, string slotPrefix)
        {
            ChildMotion[] children = tree.children;
            for (int i = 0; i < children.Length; i++)
            {
                string slot = slotPrefix + "." + i;
                if (children[i].motion is AnimationClip childClip && childClip == clip)
                {
                    consumers.Add(new JObject
                    {
                        ["controller"] = System.IO.Path.GetFileNameWithoutExtension(controllerPath),
                        ["controller_path"] = controllerPath,
                        ["layer"] = layer,
                        ["state"] = state,
                        ["blend_slot"] = slot,
                        ["threshold"] = children[i].threshold
                    });
                }
                else if (children[i].motion is BlendTree nested)
                {
                    CollectNestedBlendConsumers(nested, clip, controllerPath, layer, state, consumers, slot);
                }
            }
        }
    }
}
