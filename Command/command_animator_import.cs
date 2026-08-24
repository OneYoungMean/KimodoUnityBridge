using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private const int TransitionImportWarningLimit = 128;
        private const double TransitionSafeZoneSeconds = 1.0;

        private static string ImportAnimator(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            Animator sourceAnimator,
            bool ignoreWarning)
        {
            AnimatorController controller = sourceAnimator.runtimeAnimatorController as AnimatorController;
            if (controller == null)
            {
                if (sourceAnimator.runtimeAnimatorController is AnimatorOverrideController)
                {
                    return Ok(new JObject
                    {
                        ["added"] = false,
                        ["refreshed"] = false,
                        ["kind"] = "animator",
                        ["character"] = character.Name,
                        ["skipped"] = new JArray(UnsupportedTransitionWarning(
                            "override_controller",
                            sourceAnimator.runtimeAnimatorController.name))
                    });
                }
                return AddAnimatorRecordWithoutController(session, character, sourceAnimator);
            }

            var stateDescriptors = new List<AnimatorStateDescriptor>();
            var transitionPlans = new List<AnimatorTransitionPlan>();
            var warnings = new JArray();
            for (int layerIndex = 0; layerIndex < controller.layers.Length; layerIndex++)
            {
                AnimatorControllerLayer layer = controller.layers[layerIndex];
                CollectAnimatorStates(layer.stateMachine, layer.name, layerIndex, stateDescriptors, warnings);
            }
            foreach (AnimatorStateDescriptor state in stateDescriptors)
            {
                CollectStateTransitions(state, stateDescriptors, transitionPlans, warnings);
            }

            int projectedTransitionCount = transitionPlans.Sum(item => item.ProjectedClipCount);
            if (projectedTransitionCount > TransitionImportWarningLimit && !ignoreWarning)
            {
                return Error(
                    "transition_limit_exceeded",
                    $"Animator '{sourceAnimator.gameObject.name}' would import {projectedTransitionCount} transition clips, which exceeds the {TransitionImportWarningLimit} clip safety limit. Re-run session_add with ignore_warning=true to import all transition clips.");
            }

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

            var retargetedCandidates = new Dictionary<AnimationClip, AnimationClip>();
            var addedAnimations = new List<TimelineAnimationRecord>();
            foreach (AnimatorStateDescriptor state in stateDescriptors)
            {
                foreach (AnimatorClipCandidate candidate in state.Candidates)
                {
                    string key = $"{sourceRef}|state|{state.LayerIndex}|{state.Path}|candidate|{candidate.Index}|{candidate.SourceClip.name}";
                    if (character.Animations.Any(item => string.Equals(item.ImportKey, key, StringComparison.Ordinal)))
                    {
                        continue;
                    }
                    AnimationClip clip = ResolveAnimatorCandidateClip(character, candidate.SourceClip, retargetedCandidates);
                    string requestedName = state.Candidates.Count == 1
                        ? $"{imported.Name}.{state.Path}"
                        : $"{imported.Name}.{state.Path}.{candidate.SourceClip.name}";
                    TimelineAnimationRecord animation = AppendAnimationClip(session, character, clip, "animator", null, requestedName);
                    animation.AnimatorImportName = imported.Name;
                    animation.ImportKey = key;
                    addedAnimations.Add(animation);
                }
            }

            string importBatchId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            foreach (AnimatorTransitionPlan plan in transitionPlans)
            {
                foreach (AnimatorClipCandidate sourceCandidate in plan.Source.Candidates)
                {
                    AnimationClip sourceClip = ResolveAnimatorCandidateClip(character, sourceCandidate.SourceClip, retargetedCandidates);
                    IReadOnlyList<double> sourceExitTimes = plan.Transition.hasExitTime
                        ? new[] { ResolveExitTimeSeconds(plan.Transition, sourceClip) }
                        : SelectRepresentativeExitTimes(sourceClip);
                    foreach (AnimatorClipCandidate targetCandidate in plan.Target.Candidates)
                    {
                        AnimationClip targetClip = ResolveAnimatorCandidateClip(character, targetCandidate.SourceClip, retargetedCandidates);
                        double targetEntrySeconds = ResolveTargetEntrySeconds(plan.Transition, plan.Target.State, targetClip);
                        for (int variantIndex = 0; variantIndex < sourceExitTimes.Count; variantIndex++)
                        {
                            TimelineAnimationRecord transition = AppendTransitionClip(
                                character,
                                imported,
                                plan,
                                sourceCandidate,
                                sourceClip,
                                targetCandidate,
                                targetClip,
                                sourceExitTimes[variantIndex],
                                targetEntrySeconds,
                                variantIndex,
                                importBatchId);
                            addedAnimations.Add(transition);
                        }
                    }
                }
            }

            SaveTimelineSession(session);
            return Ok(new JObject
            {
                ["added"] = true,
                ["refreshed"] = refreshed,
                ["kind"] = "animator",
                ["character"] = character.Name,
                ["animator"] = imported.Name,
                ["transition_clip_count"] = projectedTransitionCount,
                ["animations"] = new JArray(addedAnimations.Select(DescribeAnimation)),
                ["skipped"] = warnings
            });
        }

        private static string AddAnimatorRecordWithoutController(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            Animator sourceAnimator)
        {
            string sourceRef = GetObjectReference(sourceAnimator);
            AnimatorImportRecord imported = character.AnimatorImports.FirstOrDefault(item =>
                string.Equals(item.SourceAnimatorRef, sourceRef, StringComparison.Ordinal));
            bool refreshed = imported != null;
            if (imported == null)
            {
                string baseName = KimodoRuntimeUtility.SanitizeName(sourceAnimator.gameObject.name, "Animator");
                string name = baseName;
                for (int suffix = 1; character.AnimatorImports.Any(item =>
                    string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)); suffix++)
                {
                    name = $"{baseName}_{suffix}";
                }
                imported = new AnimatorImportRecord(sourceRef, name);
                character.AnimatorImports.Add(imported);
            }

            SaveTimelineSession(session);
            return Ok(new JObject
            {
                ["added"] = true,
                ["refreshed"] = refreshed,
                ["kind"] = "animator",
                ["character"] = character.Name,
                ["animator"] = imported.Name,
                ["transition_clip_count"] = 0,
                ["animations"] = new JArray(),
                ["skipped"] = new JArray(new JObject
                {
                    ["kind"] = "animator_without_controller",
                    ["reason"] = "Animator has no AnimatorController; it was added without clip or transition records."
                })
            });
        }

        private static AnimationClip ResolveAnimatorCandidateClip(
            TimelineCharacterRecord character,
            AnimationClip candidate,
            IDictionary<AnimationClip, AnimationClip> retargetedCandidates)
        {
            if (candidate == null)
            {
                throw new InvalidOperationException("Animator state contains a null AnimationClip candidate.");
            }
            if (candidate.isHumanMotion)
            {
                return candidate;
            }
            if (!retargetedCandidates.TryGetValue(candidate, out AnimationClip retargeted))
            {
                retargeted = RetargetAddedClipToMuscle(character, candidate);
                retargetedCandidates[candidate] = retargeted;
            }
            return retargeted;
        }

        private static void CollectAnimatorStates(
            AnimatorStateMachine machine,
            string path,
            int layerIndex,
            ICollection<AnimatorStateDescriptor> states,
            JArray warnings)
        {
            if (machine == null)
            {
                return;
            }
            if (machine.anyStateTransitions != null && machine.anyStateTransitions.Length > 0)
            {
                warnings.Add(UnsupportedTransitionWarning("any_state_transition", path));
            }
            if (machine.entryTransitions != null && machine.entryTransitions.Length > 0)
            {
                warnings.Add(UnsupportedTransitionWarning("entry_transition", path));
            }
            foreach (ChildAnimatorState child in machine.states)
            {
                AnimatorState state = child.state;
                if (state == null)
                {
                    continue;
                }
                string statePath = $"{path}.{state.name}";
                var candidates = StateClips(state.motion)
                    .Select((clip, index) => new AnimatorClipCandidate(clip, index))
                    .ToList();
                if (candidates.Count == 0)
                {
                    warnings.Add(new JObject
                    {
                        ["kind"] = "state_without_clip",
                        ["state"] = statePath,
                        ["reason"] = "State has no AnimationClip candidate."
                    });
                }
                states.Add(new AnimatorStateDescriptor(state, statePath, layerIndex, candidates));
            }
            foreach (ChildAnimatorStateMachine child in machine.stateMachines)
            {
                AnimatorTransition[] stateMachineTransitions = machine.GetStateMachineTransitions(child.stateMachine);
                if (stateMachineTransitions != null && stateMachineTransitions.Length > 0)
                {
                    warnings.Add(UnsupportedTransitionWarning(
                        "state_machine_transition",
                        $"{path}.{child.stateMachine.name}"));
                }
                CollectAnimatorStates(child.stateMachine, $"{path}.{child.stateMachine.name}", layerIndex, states, warnings);
            }
        }

        private static JObject UnsupportedTransitionWarning(string kind, string stateMachinePath) => new JObject
        {
            ["kind"] = kind,
            ["state_machine"] = stateMachinePath,
            ["reason"] = "Only same-Layer State-to-State transitions are materialized as transition_clip."
        };

        private static void CollectStateTransitions(
            AnimatorStateDescriptor source,
            IReadOnlyList<AnimatorStateDescriptor> states,
            ICollection<AnimatorTransitionPlan> plans,
            JArray warnings)
        {
            if (source?.State?.transitions == null)
            {
                return;
            }
            AnimatorStateTransition[] transitions = source.State.transitions;
            for (int index = 0; index < transitions.Length; index++)
            {
                AnimatorStateTransition transition = transitions[index];
                if (transition == null)
                {
                    continue;
                }
                if (transition.destinationState == null)
                {
                    warnings.Add(new JObject
                    {
                        ["kind"] = "unsupported_state_machine_transition",
                        ["source_state"] = source.Path,
                        ["reason"] = "State-to-State transitions require a destinationState; transitions to a StateMachine are not materialized."
                    });
                    continue;
                }
                AnimatorStateDescriptor target = states.FirstOrDefault(item =>
                    item.LayerIndex == source.LayerIndex && item.State == transition.destinationState);
                if (target == null)
                {
                    warnings.Add(new JObject
                    {
                        ["kind"] = "transition_target_not_found",
                        ["source_state"] = source.Path,
                        ["reason"] = "Transition destination State is not in the same imported Layer."
                    });
                    continue;
                }
                if (source.Candidates.Count == 0 || target.Candidates.Count == 0)
                {
                    warnings.Add(new JObject
                    {
                        ["kind"] = "transition_without_clip_candidate",
                        ["source_state"] = source.Path,
                        ["target_state"] = target.Path,
                        ["reason"] = "Source and target States must both resolve to AnimationClip leaves."
                    });
                    continue;
                }
                plans.Add(new AnimatorTransitionPlan(source, target, transition, index));
            }
        }

        private static TimelineAnimationRecord AppendTransitionClip(
            TimelineCharacterRecord character,
            AnimatorImportRecord imported,
            AnimatorTransitionPlan plan,
            AnimatorClipCandidate sourceCandidate,
            AnimationClip sourceClip,
            AnimatorClipCandidate targetCandidate,
            AnimationClip targetClip,
            double sourceExitSeconds,
            double targetEntrySeconds,
            int variantIndex,
            string importBatchId)
        {
            double sourceLength = Math.Max(1.0 / SessionFrameRate, sourceClip.length);
            double targetLength = Math.Max(1.0 / SessionFrameRate, targetClip.length);
            double sourceSafeSeconds = Math.Min(TransitionSafeZoneSeconds, sourceLength);
            if (!plan.Transition.hasExitTime)
            {
                sourceSafeSeconds = Math.Min(sourceSafeSeconds, Math.Max(1.0 / SessionFrameRate, sourceExitSeconds));
            }
            double targetSafeSeconds = Math.Min(TransitionSafeZoneSeconds, targetLength);
            double blendSeconds = ResolveBlendDurationSeconds(plan.Transition, sourceLength);
            double sourceClipIn = NormalizeClipTime(sourceExitSeconds - sourceSafeSeconds, sourceLength);
            double targetClipIn = NormalizeClipTime(targetEntrySeconds, targetLength);
            double startSeconds = character.NextStartSeconds;

            string logicalName = MakeUniqueAnimationName(
                character,
                $"{imported.Name}.Transition.{plan.Source.Path}.To.{plan.Target.Path}.{sourceCandidate.Index}.{targetCandidate.Index}.{variantIndex + 1}.{importBatchId}");
            TimelineClip sourceSegment = AppendAnimationTimelineSegment(
                character,
                sourceClip,
                startSeconds,
                sourceSafeSeconds + blendSeconds,
                sourceClipIn,
                $"{logicalName}.Source",
                sourceExitSeconds > sourceLength || sourceClipIn + sourceSafeSeconds + blendSeconds > sourceLength);
            TimelineClip targetSegment = AppendAnimationTimelineSegment(
                character,
                targetClip,
                startSeconds + sourceSafeSeconds,
                blendSeconds + targetSafeSeconds,
                targetClipIn,
                $"{logicalName}.Target",
                targetClipIn + blendSeconds + targetSafeSeconds > targetLength);
            // Timeline computes the source/target mix from the overlap automatically.

            var transitionMetadata = new JObject
            {
                ["source_state"] = plan.Source.Path,
                ["target_state"] = plan.Target.Path,
                ["source_candidate"] = sourceCandidate.SourceClip.name,
                ["target_candidate"] = targetCandidate.SourceClip.name,
                ["has_exit_time"] = plan.Transition.hasExitTime,
                ["exit_time_normalized"] = plan.Transition.hasExitTime
                    ? (JToken)new JValue(plan.Transition.exitTime)
                    : JValue.CreateNull(),
                ["duration_mode"] = plan.Transition.hasFixedDuration ? "fixed" : "normalized",
                ["duration_seconds"] = blendSeconds,
                ["variant_index"] = variantIndex,
                ["source_exit_frame"] = Mathf.RoundToInt((float)(sourceExitSeconds * SessionFrameRate)),
                ["target_entry_frame"] = Mathf.RoundToInt((float)(targetEntrySeconds * SessionFrameRate)),
                ["source_safezone_frames"] = Mathf.RoundToInt((float)(sourceSafeSeconds * SessionFrameRate)),
                ["target_safezone_frames"] = Mathf.RoundToInt((float)(targetSafeSeconds * SessionFrameRate)),
                ["conditions"] = DescribeTransitionConditions(plan.Transition.conditions),
                ["import_batch_id"] = importBatchId
            };
            var record = new TimelineAnimationRecord(
                Guid.NewGuid(),
                logicalName,
                "animator_transition",
                sourceClip,
                sourceSegment,
                null,
                null,
                0,
                0)
            {
                AnimatorImportName = imported.Name,
                ImportKey = $"{imported.SourceAnimatorRef}|transition|{plan.Source.LayerIndex}|{plan.Source.Path}|{plan.Target.Path}|{plan.Ordinal}|{sourceCandidate.Index}|{targetCandidate.Index}|{variantIndex}|{importBatchId}"
            };
            record.ConfigureComposite(
                "transition_clip",
                new[]
                {
                    new TimelineAnimationSegment("source", sourceClip, sourceSegment),
                    new TimelineAnimationSegment("target", targetClip, targetSegment)
                },
                transitionMetadata);
            character.Animations.Add(record);
            character.NextStartSeconds = record.TimelineEndSeconds + ClipSafeZoneSeconds;
            EditorUtility.SetDirty(character.Track);
            return record;
        }

        private static double ResolveExitTimeSeconds(AnimatorStateTransition transition, AnimationClip sourceClip)
        {
            double length = Math.Max(1.0 / SessionFrameRate, sourceClip.length);
            return Math.Max(0.0, transition.exitTime * length);
        }

        private static double ResolveTargetEntrySeconds(
            AnimatorStateTransition transition,
            AnimatorState targetState,
            AnimationClip targetClip)
        {
            double length = Math.Max(1.0 / SessionFrameRate, targetClip.length);
            float stateOffset = targetState != null ? targetState.cycleOffset : 0f;
            return NormalizeClipTime((stateOffset + transition.offset) * length, length);
        }

        private static double ResolveBlendDurationSeconds(AnimatorStateTransition transition, double sourceLength)
        {
            return Math.Max(0.0, transition.hasFixedDuration
                ? transition.duration
                : transition.duration * sourceLength);
        }

        private static double NormalizeClipTime(double seconds, double length)
        {
            if (length <= 0.0)
            {
                return 0.0;
            }
            double result = seconds % length;
            return result < 0.0 ? result + length : result;
        }

        private static JArray DescribeTransitionConditions(IEnumerable<AnimatorCondition> conditions)
        {
            return new JArray((conditions ?? Enumerable.Empty<AnimatorCondition>()).Select(condition => new JObject
            {
                ["parameter"] = condition.parameter ?? string.Empty,
                ["mode"] = condition.mode.ToString(),
                ["threshold"] = condition.threshold
            }));
        }

        private static IReadOnlyList<double> SelectRepresentativeExitTimes(AnimationClip clip)
        {
            const int requiredCount = 4;
            double length = Math.Max(1.0 / SessionFrameRate, clip != null ? clip.length : 0.0);
            int frameCount = Math.Max(1, Mathf.RoundToInt((float)(length * SessionFrameRate)));
            var scores = new float[frameCount];
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip).Take(48).ToArray();
            float sampleInterval = 1f / (float)SessionFrameRate;
            for (int bindingIndex = 0; bindingIndex < bindings.Length; bindingIndex++)
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, bindings[bindingIndex]);
                if (curve == null || curve.length == 0)
                {
                    continue;
                }
                for (int frame = 1; frame < frameCount - 1; frame++)
                {
                    float time = frame * sampleInterval;
                    float before = curve.Evaluate(Mathf.Max(0f, time - sampleInterval));
                    float current = curve.Evaluate(time);
                    float after = curve.Evaluate(Mathf.Min((float)length, time + sampleInterval));
                    scores[frame] += Mathf.Abs(after - 2f * current + before) + 0.1f * Mathf.Abs(after - before);
                }
            }

            int minimumSpacing = Math.Max(1, frameCount / (requiredCount * 2));
            var selected = new List<int>(requiredCount);
            foreach (int frame in Enumerable.Range(0, frameCount)
                .OrderByDescending(item => scores[item])
                .ThenBy(item => item))
            {
                if (selected.All(item => Math.Abs(item - frame) >= minimumSpacing))
                {
                    selected.Add(frame);
                    if (selected.Count == requiredCount)
                    {
                        break;
                    }
                }
            }
            for (int index = 0; selected.Count < requiredCount && index < requiredCount; index++)
            {
                int fallback = Mathf.Clamp(Mathf.RoundToInt((frameCount - 1) * (index + 1f) / (requiredCount + 1f)), 0, frameCount - 1);
                selected.Add(fallback);
            }
            return selected.Select(frame => Math.Min(length, frame / SessionFrameRate)).ToArray();
        }

        private static IEnumerable<AnimationClip> StateClips(Motion motion)
        {
            if (motion is AnimationClip clip)
            {
                yield return clip;
            }
            else if (motion is BlendTree tree)
            {
                foreach (ChildMotion child in tree.children)
                foreach (AnimationClip candidate in StateClips(child.motion))
                {
                    yield return candidate;
                }
            }
        }

        private sealed class AnimatorStateDescriptor
        {
            public AnimatorStateDescriptor(AnimatorState state, string path, int layerIndex, List<AnimatorClipCandidate> candidates)
            {
                State = state;
                Path = path;
                LayerIndex = layerIndex;
                Candidates = candidates ?? new List<AnimatorClipCandidate>();
            }

            public AnimatorState State { get; }
            public string Path { get; }
            public int LayerIndex { get; }
            public List<AnimatorClipCandidate> Candidates { get; }
        }

        private sealed class AnimatorClipCandidate
        {
            public AnimatorClipCandidate(AnimationClip sourceClip, int index)
            {
                SourceClip = sourceClip;
                Index = index;
            }

            public AnimationClip SourceClip { get; }
            public int Index { get; }
        }

        private sealed class AnimatorTransitionPlan
        {
            public AnimatorTransitionPlan(
                AnimatorStateDescriptor source,
                AnimatorStateDescriptor target,
                AnimatorStateTransition transition,
                int ordinal)
            {
                Source = source;
                Target = target;
                Transition = transition;
                Ordinal = ordinal;
            }

            public AnimatorStateDescriptor Source { get; }
            public AnimatorStateDescriptor Target { get; }
            public AnimatorStateTransition Transition { get; }
            public int Ordinal { get; }
            public int ProjectedClipCount => Source.Candidates.Count * Target.Candidates.Count *
                (Transition.hasExitTime ? 1 : 4);
        }
    }
}
