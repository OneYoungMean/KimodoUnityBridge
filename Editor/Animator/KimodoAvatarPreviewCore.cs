using System;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoAvatarPreviewCore : IDisposable
    {
        private KimodoAvatarPreview avatarPreview;
        private GameObject sourcePreviewInstance;
        private AnimatorController previewController;
        private string previewControllerAssetPath;
        private AnimationClip activeClip;
        private string activeStateName;
        private string activeInputKey = string.Empty;
        private bool restartRequested;
        private float lastAppliedTime = float.NaN;
        private float preRollSeconds = 0.3f;
        private float postRollSeconds = 0.5f;
        private float windowStartTime = 0f;
        private float windowStopTime = 1f;
        private float transitionStartTime = 0f;
        private float transitionEndTime = 1f;
        private bool transitionModeActive;
        private string previewUnavailableMessage = "Preview not ready.";

        public void Dispose()
        {
            DestroySourcePreviewInstance();

            if (avatarPreview != null)
            {
                avatarPreview.OnDisable();
                avatarPreview.OnDestroy();
                avatarPreview = null;
            }

            activeClip = null;
            activeStateName = null;
            activeInputKey = string.Empty;
            lastAppliedTime = float.NaN;
            transitionModeActive = false;
            previewUnavailableMessage = "Preview not ready.";

            // Keep asset object intact; only drop runtime reference.
            previewController = null;
        }

        public void SetClipPreview(GameObject root, AnimationClip clip, string emptyStateMessage)
        {
            if (!TryBindInput(root, clip))
            {
                activeClip = null;
                activeStateName = null;
                activeInputKey = string.Empty;
                previewUnavailableMessage = string.IsNullOrWhiteSpace(emptyStateMessage) ? "Preview not ready." : emptyStateMessage;
                return;
            }

            string inputKey = BuildClipInputKey(root, clip);
            if (inputKey == activeInputKey && !string.IsNullOrEmpty(activeStateName))
            {
                return;
            }

            EnsurePreviewController();
            string stateName = EnsureClipState(clip);
            activeStateName = stateName;
            activeInputKey = inputKey;
            previewUnavailableMessage = string.Empty;
            EnsureAvatarPreview(clip);
            transitionModeActive = false;

            windowStartTime = 0f;
            windowStopTime = Mathf.Max(0.001f, clip.length);
            transitionStartTime = 0f;
            transitionEndTime = windowStopTime;
            ApplyTimeWindowToPreview();
            restartRequested = true;
        }

        public void SetTransitionPreview(GameObject root, AnimationClip fromClip, AnimationClip toClip, AnimatorStateTransition transition, string emptyStateMessage)
        {
            if (!TryBindInput(root, fromClip) || toClip == null || transition == null)
            {
                activeClip = null;
                activeStateName = null;
                activeInputKey = string.Empty;
                previewUnavailableMessage = string.IsNullOrWhiteSpace(emptyStateMessage) ? "Preview not ready." : emptyStateMessage;
                return;
            }

            string inputKey = BuildTransitionInputKey(root, fromClip, toClip, transition);
            if (inputKey == activeInputKey && !string.IsNullOrEmpty(activeStateName))
            {
                return;
            }

            EnsurePreviewController();
            string fromStateName = EnsureTransitionGraph(fromClip, toClip, transition);
            activeStateName = fromStateName;
            activeInputKey = inputKey;
            previewUnavailableMessage = string.Empty;
            EnsureAvatarPreview(fromClip);
            transitionModeActive = true;

            ComputeTransitionTimeWindow(fromClip, toClip, transition);
            ApplyTimeWindowToPreview();
            restartRequested = true;
        }

        public void RestartFromZeroAndPlay()
        {
            restartRequested = true;
        }

        public void SetTransitionWindowPadding(float preRoll, float postRoll)
        {
            preRollSeconds = Mathf.Max(0f, preRoll);
            postRollSeconds = Mathf.Max(0f, postRoll);
        }

        public bool Tick()
        {
            if (avatarPreview == null || activeClip == null || avatarPreview.timeControl == null)
            {
                return false;
            }

            Animator renderAnimator = avatarPreview.Animator;
            if (renderAnimator == null)
            {
                return false;
            }

            bool needsRepaint = false;
            if (restartRequested)
            {
                avatarPreview.timeControl.currentTime = windowStartTime;
                avatarPreview.timeControl.playing = false;
                if (!string.IsNullOrEmpty(activeStateName))
                {
                    AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(activeClip);
                    float denom = Mathf.Max(0.0001f, settings.stopTime - settings.startTime);
                    float normalized = (avatarPreview.timeControl.currentTime - settings.startTime) / denom;
                    normalized = Mathf.Clamp01(normalized);
                    renderAnimator.Play(activeStateName, 0, normalized);
                    renderAnimator.Update(0f);
                }
                lastAppliedTime = avatarPreview.timeControl.currentTime;
                restartRequested = false;
                needsRepaint = true;
            }

            float previousTime = avatarPreview.timeControl.currentTime;
            bool hasPendingManual = avatarPreview.timeControl.HasPendingManualTimeStep;
            bool isScrubbing = avatarPreview.timeControl.IsScrubbing;
            // Let timeControl.Update() compute deltaTime internally so playbackSpeed slider takes effect.
            avatarPreview.timeControl.Update();

            float currentTime = avatarPreview.timeControl.currentTime;
            bool wrapped = avatarPreview.timeControl.playing && currentTime < previousTime;
            bool timeChanged = float.IsNaN(lastAppliedTime) || !Mathf.Approximately(lastAppliedTime, currentTime);
            if (!timeChanged || string.IsNullOrEmpty(activeStateName))
            {
                return needsRepaint || hasPendingManual || isScrubbing;
            }

            if (transitionModeActive)
            {
                if (wrapped)
                {
                    AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(activeClip);
                    float denom = Mathf.Max(0.0001f, settings.stopTime - settings.startTime);
                    float normalizedStart = (windowStartTime - settings.startTime) / denom;
                    normalizedStart = Mathf.Clamp01(normalizedStart);
                    renderAnimator.Play(activeStateName, 0, normalizedStart);
                    renderAnimator.Update(0f);
                }
                renderAnimator.Update(avatarPreview.timeControl.playing ? avatarPreview.timeControl.deltaTime : 0f);
            }
            else
            {
                AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(activeClip);
                float denom = Mathf.Max(0.0001f, settings.stopTime - settings.startTime);
                float normalized = (currentTime - settings.startTime) / denom;
                normalized = Mathf.Clamp01(normalized);
                renderAnimator.Play(activeStateName, 0, normalized);
                renderAnimator.Update(avatarPreview.timeControl.playing ? avatarPreview.timeControl.deltaTime : 0f);
            }

            lastAppliedTime = currentTime;
            return true;
        }

        public void Draw(Rect rect)
        {
            if (avatarPreview == null || activeClip == null)
            {
                EditorGUI.DropShadowLabel(
                    rect,
                    string.IsNullOrWhiteSpace(previewUnavailableMessage) ? "Preview not ready." : previewUnavailableMessage);
                return;
            }

            Animator renderAnimator = avatarPreview.Animator;
            if (renderAnimator == null)
            {
                EditorGUI.DropShadowLabel(rect, "Preview animator not ready.");
                return;
            }

            avatarPreview.DoAvatarPreview(rect, KimodoPreviewConstants.PreviewBackgroundSolid);
        }

        private bool TryBindInput(GameObject root, AnimationClip clip)
        {
            if (root == null || clip == null)
            {
                return false;
            }

            activeClip = clip;
            return true;
        }

        private static string BuildClipInputKey(GameObject root, AnimationClip clip)
        {
            int rootId = root != null ? root.GetInstanceID() : 0;
            int clipId = clip != null ? clip.GetInstanceID() : 0;
            return "clip:" + rootId + ":" + clipId;
        }

        private static string BuildTransitionInputKey(GameObject root, AnimationClip fromClip, AnimationClip toClip, AnimatorStateTransition transition)
        {
            int rootId = root != null ? root.GetInstanceID() : 0;
            int fromId = fromClip != null ? fromClip.GetInstanceID() : 0;
            int toId = toClip != null ? toClip.GetInstanceID() : 0;
            int transitionId = transition != null ? transition.GetInstanceID() : 0;
            return "transition:" + rootId + ":" + fromId + ":" + toId + ":" + transitionId;
        }

        private void ComputeTransitionTimeWindow(AnimationClip fromClip, AnimationClip toClip, AnimatorStateTransition transition)
        {
            float fromLen = Mathf.Max(0.001f, fromClip != null ? fromClip.length : 0.001f);
            float toLen = Mathf.Max(0.001f, toClip != null ? toClip.length : 0.001f);
            float exitNormalized = transition != null ? Mathf.Clamp01(transition.exitTime) : 0f;
            transitionStartTime = exitNormalized * fromLen;

            float blendDuration = 0.2f;
            if (transition != null)
            {
                blendDuration = transition.hasFixedDuration
                    ? Mathf.Max(0.001f, transition.duration)
                    : Mathf.Max(0.001f, transition.duration * fromLen);
            }
            transitionEndTime = transitionStartTime + blendDuration;

            float windowStart = transitionStartTime - Mathf.Max(0f, preRollSeconds);
            float windowEnd = transitionEndTime + Mathf.Max(0f, postRollSeconds);
            windowStartTime = Mathf.Clamp(windowStart, 0f, fromLen);
            windowStopTime = Mathf.Clamp(windowEnd, windowStartTime + 0.001f, Mathf.Max(fromLen, transitionEndTime + toLen));
        }

        private void ApplyTimeWindowToPreview()
        {
            if (avatarPreview == null || avatarPreview.timeControl == null)
            {
                return;
            }

            avatarPreview.timeControl.startTime = windowStartTime;
            avatarPreview.timeControl.stopTime = windowStopTime;
            avatarPreview.timeControl.currentTime = windowStartTime;
            avatarPreview.timeControl.loop = true;
        }

        private void EnsurePreviewController()
        {
            if (previewController != null)
            {
                return;
            }

            if (string.IsNullOrEmpty(previewControllerAssetPath))
            {
                previewControllerAssetPath = "Assets/Temp/KimodoPreview_" + Guid.NewGuid().ToString("N") + ".controller";
            }

            string dir = Path.GetDirectoryName(previewControllerAssetPath);
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                AssetDatabase.CreateFolder("Assets", "Temp");
            }

            previewController = AnimatorController.CreateAnimatorControllerAtPath(previewControllerAssetPath);
            if (previewController.layers == null || previewController.layers.Length == 0)
            {
                previewController.AddLayer("Base Layer");
            }
            Debug.Log("[Kimodo][Preview] Created preview controller: " + previewControllerAssetPath);
        }

        private string EnsureClipState(AnimationClip clip)
        {
            AnimatorStateMachine sm = previewController.layers[0].stateMachine;
            string stateName = "Clip_" + clip.GetInstanceID();
            AnimatorState state = FindState(sm, stateName) ?? sm.AddState(stateName);
            state.motion = clip;
            sm.defaultState = state;
            EditorUtility.SetDirty(previewController);
            return stateName;
        }

        private string EnsureTransitionGraph(AnimationClip fromClip, AnimationClip toClip, AnimatorStateTransition source)
        {
            AnimatorStateMachine sm = previewController.layers[0].stateMachine;
            string fromName = "TransitionFrom_" + fromClip.GetInstanceID();
            string toName = "TransitionTo_" + toClip.GetInstanceID();

            AnimatorState from = FindState(sm, fromName) ?? sm.AddState(fromName);
            AnimatorState to = FindState(sm, toName) ?? sm.AddState(toName);
            from.motion = fromClip;
            to.motion = toClip;

            RemoveTransitionsTo(from, to);
            RemoveTransitionsTo(to, from);

            AnimatorStateTransition fromTo = from.AddTransition(to);
            CopyTransitionWithoutConditions(fromTo, source);
            if (!fromTo.hasExitTime)
            {
                fromTo.hasExitTime = true;
                fromTo.exitTime = 1f;
            }

            AnimatorStateTransition toFrom = to.AddTransition(from);
            toFrom.hasExitTime = true;
            toFrom.exitTime = 1f;
            toFrom.hasFixedDuration = true;
            toFrom.duration = 0f;
            toFrom.offset = 0f;
            toFrom.interruptionSource = TransitionInterruptionSource.None;
            toFrom.orderedInterruption = false;
            toFrom.canTransitionToSelf = false;

            sm.defaultState = from;
            EditorUtility.SetDirty(previewController);
            return fromName;
        }

        private void EnsureAvatarPreview(AnimationClip clip)
        {
            if (previewController == null)
            {
                return;
            }

            bool needRecreate = avatarPreview == null || activeClip != clip;
            if (!needRecreate)
            {
                return;
            }

            if (avatarPreview != null)
            {
                avatarPreview.OnDisable();
                avatarPreview.OnDestroy();
                avatarPreview = null;
            }

            DestroySourcePreviewInstance();

            Animator sourceAnimator = CreateSourceAnimatorWithController(previewController, activeInputKey, out sourcePreviewInstance);
            if (sourceAnimator == null)
            {
                return;
            }

            avatarPreview = new KimodoAvatarPreview(sourceAnimator, clip);
            avatarPreview.ShowIKOnFeetButton = clip.isHumanMotion;
            avatarPreview.ResetPreviewFocus();
            if (avatarPreview.timeControl.currentTime == Mathf.NegativeInfinity)
            {
                avatarPreview.timeControl.Update();
            }
            ApplyTimeWindowToPreview();
        }

        private void DestroySourcePreviewInstance()
        {
            if (sourcePreviewInstance == null)
            {
                return;
            }

            UnityEngine.Object.DestroyImmediate(sourcePreviewInstance);
            sourcePreviewInstance = null;
        }

        private static Animator CreateSourceAnimatorWithController(AnimatorController controller, string inputKey, out GameObject previewInstance)
        {
            previewInstance = null;

            int rootId = ParseRootInstanceId(inputKey);
            if (rootId == 0)
            {
                return null;
            }

            GameObject sourceRoot = EditorUtility.InstanceIDToObject(rootId) as GameObject;
            if (sourceRoot == null)
            {
                return null;
            }

            GameObject temp = UnityEngine.Object.Instantiate(sourceRoot);
            temp.hideFlags = HideFlags.HideAndDontSave;
            Animator animator = temp.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                UnityEngine.Object.DestroyImmediate(temp);
                return null;
            }

            animator.runtimeAnimatorController = controller;
            animator.enabled = true;
            animator.applyRootMotion = true;
            animator.Rebind();
            animator.Update(0f);
            previewInstance = temp;
            return animator;
        }

        private static int ParseRootInstanceId(string inputKey)
        {
            if (string.IsNullOrWhiteSpace(inputKey))
            {
                return 0;
            }

            string[] parts = inputKey.Split(':');
            if (parts.Length < 3)
            {
                return 0;
            }

            return int.TryParse(parts[1], out int rootId) ? rootId : 0;
        }

        private static AnimatorState FindState(AnimatorStateMachine sm, string stateName)
        {
            ChildAnimatorState[] states = sm.states;
            for (int i = 0; i < states.Length; i++)
            {
                AnimatorState s = states[i].state;
                if (s != null && s.name == stateName)
                {
                    return s;
                }
            }
            return null;
        }

        private static void RemoveTransitionsTo(AnimatorState from, AnimatorState to)
        {
            for (int i = from.transitions.Length - 1; i >= 0; i--)
            {
                if (from.transitions[i].destinationState == to)
                {
                    from.RemoveTransition(from.transitions[i]);
                }
            }
        }

        private static void CopyTransitionWithoutConditions(AnimatorStateTransition dst, AnimatorStateTransition src)
        {
            dst.hasExitTime = src.hasExitTime;
            dst.exitTime = src.exitTime;
            dst.duration = src.duration;
            dst.hasFixedDuration = src.hasFixedDuration;
            dst.offset = src.offset;
            dst.interruptionSource = src.interruptionSource;
            dst.orderedInterruption = src.orderedInterruption;
            dst.canTransitionToSelf = src.canTransitionToSelf;
        }
    }
}
