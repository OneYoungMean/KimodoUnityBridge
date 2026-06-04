using System;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoAnimatorApplyService
    {
        internal sealed class TransitionApplyContext
        {
            public AnimatorController Controller;
            public AnimatorStateMachine StateMachine;
            public AnimatorState FromState;
            public AnimatorState ToState;
            public AnimatorStateTransition OriginalTransition;
            public AnimationClip GeneratedClip;
            public string NewStateName;
        }

        internal sealed class StateApplyContext
        {
            public AnimatorController Controller;
            public AnimatorState State;
            public AnimationClip GeneratedClip;
        }

        public bool TryApplyTransition(TransitionApplyContext context, out string error)
        {
            error = string.Empty;
            if (!ValidateTransitionContext(context, out error))
            {
                return false;
            }

            if (TryApplyTransitionToController(context, context.Controller, out error))
            {
                return true;
            }

            string copyPath = EditorUtility.SaveFilePanelInProject(
                "Select Controller Copy Path",
                $"{context.Controller.name}_KimodoCopy",
                "controller",
                "Apply failed on original controller. Select a path to create a copy and retry.");
            if (string.IsNullOrWhiteSpace(copyPath))
            {
                error = "Apply canceled: no controller copy path selected.";
                return false;
            }

            string sourcePath = AssetDatabase.GetAssetPath(context.Controller);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                error = "Cannot resolve source controller asset path.";
                return false;
            }

            if (!AssetDatabase.CopyAsset(sourcePath, copyPath))
            {
                error = $"Failed to copy controller to '{copyPath}'.";
                return false;
            }

            AnimatorController copied = AssetDatabase.LoadAssetAtPath<AnimatorController>(copyPath);
            if (copied == null)
            {
                error = $"Failed to load copied controller at '{copyPath}'.";
                return false;
            }

            var copiedContext = BuildContextForCopiedController(context, copied, out error);
            if (copiedContext == null)
            {
                return false;
            }

            return TryApplyTransitionToController(copiedContext, copied, out error);
        }

        public bool TryApplyState(StateApplyContext context, out string error)
        {
            error = string.Empty;
            if (context == null || context.Controller == null || context.State == null || context.GeneratedClip == null)
            {
                error = "State apply context is invalid.";
                return false;
            }

            try
            {
                Undo.RegisterCompleteObjectUndo(context.Controller, "Kimodo Apply State Clip");
                context.State.motion = context.GeneratedClip;
                EditorUtility.SetDirty(context.Controller);
                EditorUtility.SetDirty(context.State);
                return true;
            }
            catch (Exception ex)
            {
                error = $"State apply failed: {ex.Message}";
                return false;
            }
        }

        private static bool ValidateTransitionContext(TransitionApplyContext context, out string error)
        {
            error = string.Empty;
            if (context == null || context.Controller == null || context.StateMachine == null ||
                context.FromState == null || context.ToState == null || context.OriginalTransition == null ||
                context.GeneratedClip == null)
            {
                error = "Transition apply context is invalid.";
                return false;
            }

            return true;
        }

        private static TransitionApplyContext BuildContextForCopiedController(
            TransitionApplyContext source,
            AnimatorController copiedController,
            out string error)
        {
            error = string.Empty;
            AnimatorStateMachine copiedSm = FindStateMachineByName(copiedController, source.StateMachine.name);
            if (copiedSm == null)
            {
                error = $"Cannot find copied state machine '{source.StateMachine.name}'.";
                return null;
            }

            AnimatorState copiedFrom = FindStateByName(copiedSm, source.FromState.name);
            AnimatorState copiedTo = FindStateByName(copiedSm, source.ToState.name);
            if (copiedFrom == null || copiedTo == null)
            {
                error = "Cannot resolve copied from/to states.";
                return null;
            }

            AnimatorStateTransition copiedOriginal = FindTransition(copiedFrom, copiedTo);
            if (copiedOriginal == null)
            {
                error = "Cannot resolve original transition in copied controller.";
                return null;
            }

            return new TransitionApplyContext
            {
                Controller = copiedController,
                StateMachine = copiedSm,
                FromState = copiedFrom,
                ToState = copiedTo,
                OriginalTransition = copiedOriginal,
                GeneratedClip = source.GeneratedClip,
                NewStateName = source.NewStateName
            };
        }

        private static bool TryApplyTransitionToController(
            TransitionApplyContext context,
            AnimatorController controllerToModify,
            out string error)
        {
            error = string.Empty;
            try
            {
                Undo.RegisterCompleteObjectUndo(controllerToModify, "Kimodo Apply Transition Insert");
                AnimatorStateMachine sm = context.StateMachine;
                AnimatorState from = context.FromState;
                AnimatorState to = context.ToState;
                AnimatorStateTransition original = context.OriginalTransition;

                string newStateName = EnsureUniqueStateName(sm, context.NewStateName);
                AnimatorState newState = sm.AddState(newStateName);
                newState.motion = context.GeneratedClip;

                bool hasExitTime = original.hasExitTime;
                float exitTime = original.exitTime;
                AnimatorCondition[] conditions = original.conditions;

                for (int i = from.transitions.Length - 1; i >= 0; i--)
                {
                    if (from.transitions[i] == original)
                    {
                        from.RemoveTransition(from.transitions[i]);
                        break;
                    }
                }

                AnimatorStateTransition fromToNew = from.AddTransition(newState);
                fromToNew.hasExitTime = hasExitTime;
                fromToNew.exitTime = exitTime;
                fromToNew.hasFixedDuration = true;
                fromToNew.duration = 0f;
                fromToNew.offset = 0f;
                CopyConditions(fromToNew, conditions);

                AnimatorStateTransition newToTo = newState.AddTransition(to);
                newToTo.hasExitTime = true;
                newToTo.exitTime = 1f;
                newToTo.hasFixedDuration = true;
                newToTo.duration = 0f;
                newToTo.offset = 0f;

                EditorUtility.SetDirty(controllerToModify);
                EditorUtility.SetDirty(sm);
                EditorUtility.SetDirty(from);
                EditorUtility.SetDirty(newState);
                EditorUtility.SetDirty(to);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Transition apply failed: {ex.Message}";
                return false;
            }
        }

        private static void CopyConditions(AnimatorStateTransition dst, AnimatorCondition[] conditions)
        {
            if (dst == null || conditions == null)
            {
                return;
            }

            for (int i = 0; i < conditions.Length; i++)
            {
                AnimatorCondition c = conditions[i];
                dst.AddCondition(c.mode, c.threshold, c.parameter);
            }
        }

        private static string EnsureUniqueStateName(AnimatorStateMachine sm, string preferred)
        {
            string baseName = string.IsNullOrWhiteSpace(preferred) ? "KimodoInsert" : preferred.Trim();
            string name = baseName;
            int suffix = 1;
            while (FindStateByName(sm, name) != null)
            {
                name = $"{baseName}_{suffix++}";
            }
            return name;
        }

        private static AnimatorStateMachine FindStateMachineByName(AnimatorController controller, string stateMachineName)
        {
            if (controller == null || string.IsNullOrWhiteSpace(stateMachineName))
            {
                return null;
            }

            for (int i = 0; i < controller.layers.Length; i++)
            {
                AnimatorStateMachine sm = controller.layers[i].stateMachine;
                if (TryFindStateMachineByNameRecursive(sm, stateMachineName, out AnimatorStateMachine found))
                {
                    return found;
                }
            }

            return null;
        }

        private static AnimatorState FindStateByName(AnimatorStateMachine sm, string stateName)
        {
            if (sm == null || string.IsNullOrWhiteSpace(stateName))
            {
                return null;
            }

            ChildAnimatorState[] states = sm.states;
            for (int i = 0; i < states.Length; i++)
            {
                AnimatorState s = states[i].state;
                if (s != null && string.Equals(s.name, stateName, StringComparison.Ordinal))
                {
                    return s;
                }
            }

            ChildAnimatorStateMachine[] childStateMachines = sm.stateMachines;
            for (int i = 0; i < childStateMachines.Length; i++)
            {
                AnimatorState found = FindStateByName(childStateMachines[i].stateMachine, stateName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static bool TryFindStateMachineByNameRecursive(
            AnimatorStateMachine stateMachine,
            string stateMachineName,
            out AnimatorStateMachine found)
        {
            found = null;
            if (stateMachine == null || string.IsNullOrWhiteSpace(stateMachineName))
            {
                return false;
            }

            if (string.Equals(stateMachine.name, stateMachineName, StringComparison.Ordinal))
            {
                found = stateMachine;
                return true;
            }

            ChildAnimatorStateMachine[] childStateMachines = stateMachine.stateMachines;
            for (int i = 0; i < childStateMachines.Length; i++)
            {
                if (TryFindStateMachineByNameRecursive(childStateMachines[i].stateMachine, stateMachineName, out found))
                {
                    return true;
                }
            }

            return false;
        }

        private static AnimatorStateTransition FindTransition(AnimatorState from, AnimatorState to)
        {
            if (from == null || to == null)
            {
                return null;
            }

            AnimatorStateTransition[] transitions = from.transitions;
            for (int i = 0; i < transitions.Length; i++)
            {
                if (transitions[i] != null && transitions[i].destinationState == to)
                {
                    return transitions[i];
                }
            }

            return null;
        }
    }
}
