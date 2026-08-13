using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace CharacterAnimationCli.Unity.Command
{
    internal static partial class command_context
    {
        private static JObject ImportAnimator(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            Animator sourceAnimator)
        {
            AnimatorController controller = sourceAnimator.runtimeAnimatorController as AnimatorController
                ?? throw new InvalidOperationException("The source Animator must use an AnimatorController. OverrideController imports are not supported yet.");
            string sourceRef = GetObjectReference(sourceAnimator);
            AnimatorImportRecord imported = character.AnimatorImports.FirstOrDefault(item =>
                string.Equals(item.SourceAnimatorRef, sourceRef, StringComparison.Ordinal));
            bool refreshed = imported != null;
            if (imported != null) imported.TransitionAnalysis.Clear();
            if (imported == null)
            {
                string baseName = KimodoRuntimeUtility.SanitizeName(sourceAnimator.gameObject.name, "Animator");
                string name = baseName;
                for (int suffix = 1; character.AnimatorImports.Any(item =>
                    string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++) name = $"{baseName}_{suffix}";
                imported = new AnimatorImportRecord(sourceRef, name);
                character.AnimatorImports.Add(imported);
            }

            var addedAnimations = new List<TimelineAnimationRecord>();
            var addedTransitions = new List<TimelineAnimationRecord>();
            var warnings = new JArray();
            var states = new Dictionary<AnimatorState, ImportedState>();
            for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
            {
                AnimatorControllerLayer layer = controller.layers[layerIndex];
                CollectAnimatorStates(layer.stateMachine, layer.name, layerIndex, imported, session, character,
                    states, addedAnimations, warnings);
            }

            foreach (KeyValuePair<AnimatorState, ImportedState> pair in states)
            {
                AnimatorState sourceState = pair.Key;
                ImportedState from = pair.Value;
                AnimatorStateTransition[] transitions = sourceState.transitions ?? Array.Empty<AnimatorStateTransition>();
                for (int ordinal = 0; ordinal < transitions.Length; ordinal++)
                {
                    AnimatorStateTransition transition = transitions[ordinal];
                    if (transition?.destinationState == null || !states.TryGetValue(transition.destinationState, out ImportedState to))
                    {
                        warnings.Add(TransitionWarning("unsupported_transition", from, null,
                            "Only direct state-to-state transitions are imported."));
                        continue;
                    }
                    JObject transitionPlan = BuildTransitionAnalysisPlan(from, to, transition, ordinal);
                    imported.TransitionAnalysis.Add(transitionPlan);
                    if (from.Animations.Count != 1 || to.Animations.Count != 1 ||
                        sourceState.motion is not AnimationClip || transition.destinationState.motion is not AnimationClip)
                    {
                        JObject warning = TransitionWarning("blend_tree_transition", from, to,
                            "Transition has multiple possible motions; construct the desired overlap or transition through Timeline APIs.");
                        warning["from_candidates"] = new JArray(from.Animations.Select(item => item.Name));
                        warning["to_candidates"] = new JArray(to.Animations.Select(item => item.Name));
                        warnings.Add(warning);
                        continue;
                    }
                    string key = $"{sourceRef}|transition|{from.Key}|{ordinal}|{to.Key}";
                    TimelineAnimationRecord existing = character.Animations.FirstOrDefault(item =>
                        string.Equals(item.ImportKey, key, StringComparison.Ordinal));
                    if (existing != null)
                    {
                        transitionPlan["materialized"] = true;
                        transitionPlan["materialized_animation"] = existing.Name;
                        continue;
                    }
                    string requestedName = $"{from.Animations[0].Name}__to__{to.Animations[0].Name}";
                    AnimationClip baked = BakeAnimatorTransition(character, from.Animations[0].Clip,
                        to.Animations[0].Clip, transition, requestedName);
                    TimelineAnimationRecord animation = AppendAnimationClip(session, character, baked,
                        "animator_transition", null, requestedName);
                    animation.AnimatorImportName = imported.Name;
                    animation.ImportKey = key;
                    animation.FromAnimation = from.Animations[0].Name;
                    animation.ToAnimation = to.Animations[0].Name;
                    transitionPlan["materialized"] = true;
                    transitionPlan["materialized_animation"] = animation.Name;
                    transitionPlan["materialized_case_id"] = transition.hasExitTime ? "case_0_0_0" : "case_0_0_3";
                    addedTransitions.Add(animation);
                }
            }
            SaveTimelineSession(session);
            return new JObject
            {
                ["added"] = true,
                ["refreshed"] = refreshed,
                ["kind"] = "animator",
                ["character"] = character.Name,
                ["animator"] = imported.Name,
                ["animations"] = new JArray(addedAnimations.Select(DescribeAnimation)),
                ["transitions"] = new JArray(addedTransitions.Select(DescribeTransition)),
                ["transition_analysis"] = imported.TransitionAnalysis.DeepClone(),
                ["skipped"] = warnings
            };
        }

        private static JObject BuildTransitionAnalysisPlan(
            ImportedState from,
            ImportedState to,
            AnimatorStateTransition transition,
            int ordinal)
        {
            int sourceCount = from.Animations.Count;
            int targetCount = to.Animations.Count;
            bool sampleBoundary = !transition.hasExitTime;
            int boundaryCount = sampleBoundary ? 4 : 1;
            long candidateCaseCount = (long)sourceCount * targetCount * boundaryCount;
            var cases = new JArray();
            for (int sourceIndex = 0; sourceIndex < from.Animations.Count; sourceIndex++)
            for (int targetIndex = 0; targetIndex < to.Animations.Count; targetIndex++)
            for (int boundaryIndex = 0; boundaryIndex < boundaryCount; boundaryIndex++)
            {
                cases.Add(new JObject
                {
                    ["id"] = $"case_{sourceIndex}_{targetIndex}_{boundaryIndex}",
                    ["source_candidate"] = from.Animations[sourceIndex].Name,
                    ["target_candidate"] = to.Animations[targetIndex].Name,
                    ["boundary_index"] = boundaryIndex,
                    ["source_sample_role"] = sampleBoundary
                        ? $"source_edge_keyframe_{boundaryIndex + 1}_of_4"
                        : "source_exit_time_boundary",
                    ["source_sample_frame"] = SourceBoundaryFrame(
                        from.Animations[sourceIndex].Clip,
                        transition,
                        boundaryIndex,
                        boundaryCount),
                    ["target_sample_frame"] = TargetEntryFrame(to.Animations[targetIndex].Clip, transition),
                    ["target_sample_role"] = "target_entry_boundary",
                    ["materialized"] = false
                });
            }
            return new JObject
            {
                ["id"] = $"{from.Key}|transition|{ordinal}|{to.Key}",
                ["from_state"] = from.Path,
                ["to_state"] = to.Path,
                ["from_motion"] = from.MotionKind,
                ["to_motion"] = to.MotionKind,
                ["from_candidates"] = new JArray(from.Animations.Select(item => item.Name)),
                ["to_candidates"] = new JArray(to.Animations.Select(item => item.Name)),
                ["has_exit_time"] = transition.hasExitTime,
                ["exit_time"] = transition.exitTime,
                ["duration"] = transition.duration,
                ["has_fixed_duration"] = transition.hasFixedDuration,
                ["offset"] = transition.offset,
                ["conditions"] = new JArray((transition.conditions ?? Array.Empty<AnimatorCondition>()).Select(condition => new JObject
                {
                    ["mode"] = condition.mode.ToString(),
                    ["parameter"] = condition.parameter,
                    ["threshold"] = condition.threshold
                })),
                ["sample_strategy"] = sampleBoundary ? "four_boundary_keyframes" : "exit_time_boundary",
                ["boundary_sample_count"] = boundaryCount,
                ["candidate_case_count"] = candidateCaseCount,
                ["case_descriptors"] = cases,
                ["materialized"] = false,
                ["note"] = "Analysis plan only. Use a future materialization step to create transition clips or generation requests."
            };
        }

        private static int SourceBoundaryFrame(
            AnimationClip clip,
            AnimatorStateTransition transition,
            int boundaryIndex,
            int boundaryCount)
        {
            int lastFrame = Math.Max(0, Mathf.CeilToInt(Mathf.Max(0f, clip?.length ?? 0f) * 60f) - 1);
            if (transition.hasExitTime)
            {
                return Mathf.Clamp(Mathf.RoundToInt(lastFrame * Mathf.Clamp01(transition.exitTime)), 0, lastFrame);
            }
            return Mathf.Clamp(lastFrame - (boundaryCount - 1 - boundaryIndex), 0, lastFrame);
        }

        private static int TargetEntryFrame(AnimationClip clip, AnimatorStateTransition transition)
        {
            int lastFrame = Math.Max(0, Mathf.CeilToInt(Mathf.Max(0f, clip?.length ?? 0f) * 60f) - 1);
            return Mathf.Clamp(Mathf.RoundToInt(lastFrame * Mathf.Clamp01(transition.offset)), 0, lastFrame);
        }

        private static void CollectAnimatorStates(
            AnimatorStateMachine machine,
            string path,
            int layerIndex,
            AnimatorImportRecord imported,
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            IDictionary<AnimatorState, ImportedState> states,
            ICollection<TimelineAnimationRecord> added,
            JArray warnings)
        {
            foreach (ChildAnimatorState child in machine.states)
            {
                AnimatorState state = child.state;
                string statePath = $"{path}.{state.name}";
                string stateKey = $"{imported.SourceAnimatorRef}|state|{layerIndex}|{statePath}";
                var record = new ImportedState(stateKey, statePath);
                record.MotionKind = MotionKind(state.motion);
                AnimationClip[] clips = StateClips(state.motion).ToArray();
                if (clips.Length == 0)
                {
                    warnings.Add(new JObject { ["kind"] = "state_without_clip", ["state"] = statePath,
                        ["reason"] = "State has no AnimationClip candidate." });
                }
                for (int index = 0; index < clips.Length; index++)
                {
                    string key = clips.Length == 1 ? stateKey : $"{stateKey}|candidate|{index}|{clips[index].name}";
                    TimelineAnimationRecord animation = character.Animations.FirstOrDefault(item =>
                        string.Equals(item.ImportKey, key, StringComparison.Ordinal));
                    if (animation == null)
                    {
                        AnimationClip clip = clips[index].isHumanMotion ? clips[index] : RetargetAddedClipToMuscle(character, clips[index]);
                        string requestedName = clips.Length == 1
                            ? $"{imported.Name}.{statePath}"
                            : $"{imported.Name}.{statePath}.{clips[index].name}";
                        animation = AppendAnimationClip(session, character, clip, "animator", null, requestedName);
                        animation.AnimatorImportName = imported.Name;
                        animation.ImportKey = key;
                        added.Add(animation);
                    }
                    record.Animations.Add(animation);
                }
                states[state] = record;
            }
            foreach (ChildAnimatorStateMachine child in machine.stateMachines)
                CollectAnimatorStates(child.stateMachine, $"{path}.{child.stateMachine.name}", layerIndex, imported,
                    session, character, states, added, warnings);
        }

        private static IEnumerable<AnimationClip> StateClips(Motion motion)
        {
            if (motion is AnimationClip clip) yield return clip;
            else if (motion is BlendTree tree)
            {
                foreach (ChildMotion child in tree.children)
                foreach (AnimationClip candidate in StateClips(child.motion)) yield return candidate;
            }
        }

        private static string MotionKind(Motion motion)
        {
            if (motion is AnimationClip) return "clip";
            if (motion is BlendTree tree)
            {
                return tree.children.Any(child => child.motion is BlendTree)
                    ? "blend_tree_nested"
                    : "blend_tree";
            }
            return motion == null ? "none" : motion.GetType().Name;
        }

        private static JObject TransitionWarning(string kind, ImportedState from, ImportedState to, string reason) => new JObject
        {
            ["kind"] = kind,
            ["from_state"] = from?.Path ?? string.Empty,
            ["to_state"] = to?.Path ?? string.Empty,
            ["reason"] = reason,
            ["suggestion"] = "Choose concrete animation candidates and use Timeline clip overlap/blend APIs; Kimodo does not select BlendTree branches."
        };

        private static AnimationClip BakeAnimatorTransition(
            TimelineCharacterRecord character,
            AnimationClip fromClip,
            AnimationClip toClip,
            AnimatorStateTransition transition,
            string requestedName)
        {
            float fromLength = Mathf.Max(0.001f, fromClip.length);
            float duration = Mathf.Max(1f / 60f, transition.hasFixedDuration
                ? transition.duration
                : transition.duration * fromLength);
            int frameCount = Math.Max(2, Mathf.CeilToInt(duration * 60f) + 1);
            GameObject preview = UnityEngine.Object.Instantiate(character.Root);
            preview.hideFlags = HideFlags.HideAndDontSave;
            Animator animator = preview.GetComponentInChildren<Animator>(true);
            animator.runtimeAnimatorController = null;
            Transform[] transforms = preview.GetComponentsInChildren<Transform>(true);
            string[] paths = transforms.Select(item => AnimationUtility.CalculateTransformPath(item, preview.transform)).ToArray();
            var frames = new List<BakeBoneFrame>(frameCount);
            PlayableGraph graph = PlayableGraph.Create("Kimodo Animator Transition Bake");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            try
            {
                AnimationMixerPlayable mixer = AnimationMixerPlayable.Create(graph, 2);
                AnimationClipPlayable fromPlayable = AnimationClipPlayable.Create(graph, fromClip);
                AnimationClipPlayable toPlayable = AnimationClipPlayable.Create(graph, toClip);
                graph.Connect(fromPlayable, 0, mixer, 0);
                graph.Connect(toPlayable, 0, mixer, 1);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "Transition", animator);
                output.SetSourcePlayable(mixer);
                graph.Play();
                float fromStart = (transition.hasExitTime ? Mathf.Clamp01(transition.exitTime) : 1f) * fromLength;
                float toStart = Mathf.Clamp01(transition.offset) * Mathf.Max(0.001f, toClip.length);
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float u = frame / (float)(frameCount - 1);
                    float elapsed = duration * u;
                    fromPlayable.SetTime(fromStart + elapsed);
                    toPlayable.SetTime(toStart + elapsed);
                    mixer.SetInputWeight(0, 1f - u);
                    mixer.SetInputWeight(1, u);
                    graph.Evaluate(0f);
                    var sample = new BakeBoneFrame(transforms.Length);
                    for (int index = 0; index < transforms.Length; index++)
                    {
                        sample.Positions[index] = transforms[index].localPosition;
                        sample.Rotations[index] = transforms[index].localRotation;
                    }
                    frames.Add(sample);
                }
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                UnityEngine.Object.DestroyImmediate(preview);
            }
            AnimationClip baked = KimodoEditorClipWritebackService.CreateGeneratedAnimationClipAsset(
                requestedName, KimodoEditorClipWritebackService.GeneratedClipFolder);
            baked.frameRate = 60f;
            WriteBoneBakeCurves(baked, transforms, paths, frames, 60f);
            return baked;
        }

        private sealed class ImportedState
        {
            public ImportedState(string key, string path) { Key = key; Path = path; }
            public string Key { get; }
            public string Path { get; }
            public string MotionKind { get; set; } = "none";
            public List<TimelineAnimationRecord> Animations { get; } = new List<TimelineAnimationRecord>();
        }
    }
}
