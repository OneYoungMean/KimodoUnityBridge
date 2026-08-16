using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

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
            var warnings = new JArray();
            for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
            {
                AnimatorControllerLayer layer = controller.layers[layerIndex];
                CollectAnimatorStates(layer.stateMachine, layer.name, layerIndex, imported, session, character,
                    addedAnimations, warnings);
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
                ["skipped"] = warnings
            };
        }

        private static void CollectAnimatorStates(
            AnimatorStateMachine machine,
            string path,
            int layerIndex,
            AnimatorImportRecord imported,
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            ICollection<TimelineAnimationRecord> added,
            JArray warnings)
        {
            foreach (ChildAnimatorState child in machine.states)
            {
                AnimatorState state = child.state;
                string statePath = $"{path}.{state.name}";
                string stateKey = $"{imported.SourceAnimatorRef}|state|{layerIndex}|{statePath}";
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
                }
            }
            foreach (ChildAnimatorStateMachine child in machine.stateMachines)
                CollectAnimatorStates(child.stateMachine, $"{path}.{child.stateMachine.name}", layerIndex, imported,
                    session, character, added, warnings);
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

    }
}
