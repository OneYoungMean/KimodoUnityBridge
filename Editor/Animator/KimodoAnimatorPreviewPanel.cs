
using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoAnimatorPreviewPanel : IDisposable
    {
        private const float MinLeftWidth = 360f;
        private const string DefaultBridgeModelName = "Kimodo-SOMA-RP-v1";
        private const string BlendTreeUnsupportedMessage = "TODO: will support BlendTree on future.";

        private enum PreviewMode
        {
            Original = 0,
            Generated = 1
        }

        private PreviewMode previewMode = PreviewMode.Original;
        private readonly List<AnimationClip> generatedPreviewHistory = new List<AnimationClip>();
        private int generatedPreviewIndex = -1;
        private AnimationClip generatedClipForPreview;
        private AnimationClip originalClipForPreview;
        private AnimationClip overrideSourceClipForGeneration;
        private int overrideSourceSelectionInstanceId;
        private Avatar retargetAvatarForPreview;
        private bool retargetAvatarDependsOnBridgeModel;
        private string retargetAvatarBridgeModelName = string.Empty;
        private GameObject previewRootInstance;
        private KimodoAvatarPreviewCore avatarPreviewCore;
        private float transitionPreRollSeconds = 0.3f;
        private float transitionPostRollSeconds = 0.5f;

        private bool selectionLatched;
        private int latchedSelectionInstanceId;
        private AnimatorStateTransition selectedTransition;
        private AnimatorState selectedState;
        private AnimatorState selectedFromState;
        private AnimatorController selectedController;
        private AnimatorStateMachine selectedStateMachine;
        private bool HasGeneratedResult => generatedClipForPreview != null || generatedPreviewHistory.Count > 0;

        public void Initialize()
        {
            avatarPreviewCore = new KimodoAvatarPreviewCore();
            avatarPreviewCore.SetTransitionWindowPadding(transitionPreRollSeconds, transitionPostRollSeconds);
            TryCaptureSelectionFromActiveObject(lockSelection: false);
            RefreshPreviewSource();
        }

        public void Dispose()
        {
            DestroyPreviewRootInstance();
            ClearResolvedPreviewSource();
            avatarPreviewCore?.Dispose();
            avatarPreviewCore = null;
        }

        public void OnSelectionChange()
        {
            if (selectionLatched)
            {
                return;
            }

            TryCaptureSelectionFromActiveObject(lockSelection: false);
            RefreshPreviewSource();
        }

        public bool HasSelection => selectedTransition != null || selectedState != null;
        public bool HasUnsupportedBlendTreeSelection => TryGetBlendTreeSelectionWarning(out _);
        public AnimationClip GeneratedClipForPreview => generatedClipForPreview;
        public AnimationClip OriginalClipForPreview => originalClipForPreview;
        public Avatar RetargetAvatarForPreview => retargetAvatarForPreview;
        public GameObject PreviewAvatarRoot => previewRootInstance;
        public AnimatorStateTransition SelectedTransition => selectedTransition;
        public AnimatorState SelectedState => selectedState;
        public AnimatorState SelectedFromState => selectedFromState;
        public AnimatorStateMachine SelectedStateMachine => selectedStateMachine;

        public bool Tick()
        {
            return avatarPreviewCore != null && avatarPreviewCore.Tick();
        }

        public void DrawToolbar(ref string lastStatus, ref string lastError, Action onResetAll)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Selection Source: Selection.activeObject", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                bool wantOriginal = GUILayout.Toggle(previewMode == PreviewMode.Original, "Show Original", EditorStyles.toolbarButton);
                bool wantGenerated = GUILayout.Toggle(previewMode == PreviewMode.Generated, "Show Generated", EditorStyles.toolbarButton);
                if (wantOriginal && previewMode != PreviewMode.Original)
                {
                    previewMode = PreviewMode.Original;
                    avatarPreviewCore?.RestartFromZeroAndPlay();
                }
                else if (wantGenerated && previewMode != PreviewMode.Generated)
                {
                    previewMode = PreviewMode.Generated;
                    avatarPreviewCore?.RestartFromZeroAndPlay();
                }

                if (GUILayout.Button("Reselect", EditorStyles.toolbarButton, GUILayout.Width(100f)))
                {
                    ResetAll();
                    onResetAll?.Invoke();
                    lastStatus = string.Empty;
                    lastError = string.Empty;
                }

                GUILayout.Space(8f);
                GUILayout.Label("Pre(s)", EditorStyles.miniLabel, GUILayout.Width(38f));
                float newPre = EditorGUILayout.FloatField(transitionPreRollSeconds, EditorStyles.toolbarTextField, GUILayout.Width(50f));
                GUILayout.Label("Post(s)", EditorStyles.miniLabel, GUILayout.Width(44f));
                float newPost = EditorGUILayout.FloatField(transitionPostRollSeconds, EditorStyles.toolbarTextField, GUILayout.Width(50f));
                if (!Mathf.Approximately(newPre, transitionPreRollSeconds) || !Mathf.Approximately(newPost, transitionPostRollSeconds))
                {
                    transitionPreRollSeconds = Mathf.Max(0f, newPre);
                    transitionPostRollSeconds = Mathf.Max(0f, newPost);
                    avatarPreviewCore?.SetTransitionWindowPadding(transitionPreRollSeconds, transitionPostRollSeconds);
                    avatarPreviewCore?.RestartFromZeroAndPlay();
                }

                EditorGUI.BeginDisabledGroup(generatedPreviewHistory.Count == 0);
                if (GUILayout.Button("<", EditorStyles.toolbarButton, GUILayout.Width(24f)))
                {
                    generatedPreviewIndex = Mathf.Max(0, generatedPreviewIndex - 1);
                    SetGeneratedPreviewByIndex();
                }
                GUILayout.Label(generatedPreviewHistory.Count == 0 ? "Gen 0/0" : $"Gen {generatedPreviewIndex + 1}/{generatedPreviewHistory.Count}", EditorStyles.miniLabel, GUILayout.Width(84f));
                if (GUILayout.Button(">", EditorStyles.toolbarButton, GUILayout.Width(24f)))
                {
                    generatedPreviewIndex = Mathf.Min(generatedPreviewHistory.Count - 1, generatedPreviewIndex + 1);
                    SetGeneratedPreviewByIndex();
                }
                EditorGUI.EndDisabledGroup();
            }
        }

        public void DrawPreviewPanel(float windowHeight)
        {
            Rect leftRect = EditorGUILayout.GetControlRect(false, windowHeight - 70f, GUILayout.MinWidth(MinLeftWidth), GUILayout.ExpandWidth(true));
            GUI.Box(leftRect, GUIContent.none);
            Handles.BeginGUI();
            Handles.color = new Color(0.75f, 0.75f, 0.75f, 1f);
            Handles.DrawLine(new Vector3(leftRect.xMax, leftRect.yMin), new Vector3(leftRect.xMax, leftRect.yMax));
            Handles.EndGUI();

            Rect renderRect = new Rect(leftRect.x + 8f, leftRect.y + 6f, leftRect.width - 16f, leftRect.height - 14f);
            if (avatarPreviewCore == null)
            {
                avatarPreviewCore = new KimodoAvatarPreviewCore();
                avatarPreviewCore.SetTransitionWindowPadding(transitionPreRollSeconds, transitionPostRollSeconds);
            }

            if (previewMode == PreviewMode.Generated)
            {
                avatarPreviewCore.SetClipPreview(previewRootInstance, generatedClipForPreview, generatedClipForPreview == null ? "No generated animation." : "Generated preview unavailable.");
            }
            else if (TryGetBlendTreeSelectionWarning(out string warning))
            {
                avatarPreviewCore.SetClipPreview(previewRootInstance, null, warning);
            }
            else if (selectedTransition != null)
            {
                AnimationClip fromClip = selectedFromState != null ? selectedFromState.motion as AnimationClip : null;
                AnimationClip toClip = selectedTransition.destinationState != null ? selectedTransition.destinationState.motion as AnimationClip : null;
                if (fromClip != null && toClip != null)
                {
                    avatarPreviewCore.SetTransitionPreview(previewRootInstance, fromClip, toClip, selectedTransition, "No transition animation.");
                }
                else
                {
                    avatarPreviewCore.SetClipPreview(previewRootInstance, null, "Transition preview requires AnimationClip motions.");
                }
            }
            else
            {
                avatarPreviewCore.SetClipPreview(previewRootInstance, originalClipForPreview, originalClipForPreview == null ? "No original animation." : "Original preview unavailable.");
            }

            avatarPreviewCore.Draw(renderRect);
        }

        public void OnGenerateSuccess(AnimationClip clip)
        {
            generatedClipForPreview = clip;
            if (clip != null)
            {
                generatedPreviewHistory.Add(clip);
                generatedPreviewIndex = generatedPreviewHistory.Count - 1;
            }

            LockCurrentSelection();
            previewMode = PreviewMode.Generated;
            avatarPreviewCore?.RestartFromZeroAndPlay();
        }

        public void OnGenerateFailedOrCanceled()
        {
            generatedClipForPreview = null;
            ReleaseSelectionLockIfNoGeneratedResult();
        }

        public void ResetGeneratedOnly()
        {
            generatedClipForPreview = null;
            generatedPreviewHistory.Clear();
            generatedPreviewIndex = -1;
            previewMode = PreviewMode.Original;
            ReleaseSelectionLockIfNoGeneratedResult();
        }

        public void ResetAll()
        {
            ResetGeneratedOnly();
            ClearSelectionLatch();
            DestroyPreviewRootInstance();
            avatarPreviewCore?.Dispose();
            avatarPreviewCore = new KimodoAvatarPreviewCore();
            avatarPreviewCore.SetTransitionWindowPadding(transitionPreRollSeconds, transitionPostRollSeconds);
            ClearResolvedPreviewSource();

            TryCaptureSelectionFromActiveObject(lockSelection: false);
            RefreshPreviewSource();
        }

        public void DrawSelectionInfo()
        {
            EditorGUILayout.LabelField("Selection Context", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            if (selectionLatched)
            {
                EditorGUILayout.HelpBox(
                    $"Selection is locked to instance {latchedSelectionInstanceId}. Click Reselect to capture the current Animator State/Transition.",
                    MessageType.Info);
            }

            if (selectedTransition != null)
            {
                EditorGUILayout.LabelField("Mode: Transition");
                EditorGUILayout.ObjectField("Transition", selectedTransition, typeof(AnimatorStateTransition), false);
                EditorGUILayout.ObjectField("From", selectedFromState, typeof(AnimatorState), false);
                EditorGUILayout.ObjectField("To", selectedTransition.destinationState, typeof(AnimatorState), false);
            }
            else if (selectedState != null)
            {
                EditorGUILayout.LabelField("Mode: State");
                EditorGUILayout.ObjectField("State", selectedState, typeof(AnimatorState), false);
            }
            else
            {
                EditorGUILayout.HelpBox("Select AnimatorStateTransition or AnimatorState.", MessageType.Warning);
            }

            if (TryGetBlendTreeSelectionWarning(out string warning))
            {
                EditorGUILayout.HelpBox(warning, MessageType.Info);
            }
            EditorGUILayout.EndVertical();
        }

        public bool TryEnsureGenerationSourceReady(string bridgeModelName, out string error)
        {
            return TryResolveAvatarAndMotionForSampling(bridgeModelName, preparePreviewRoot: true, out _, out _, out error);
        }

        public bool TryResolveCurrentSourceClipAndAvatar(
            string bridgeModelName,
            out AnimationClip sourceClip,
            out Avatar avatar,
            out string error)
        {
            sourceClip = null;
            avatar = null;
            if (!TryResolveAvatarAndMotionForSampling(bridgeModelName, preparePreviewRoot: true, out avatar, out sourceClip, out error))
            {
                return false;
            }

            return sourceClip != null && avatar != null;
        }

        public void OverrideOriginalClipForPreview(AnimationClip clip, string bridgeModelName)
        {
            if (clip == null)
            {
                return;
            }

            ClearResolvedPreviewSource();
            overrideSourceClipForGeneration = clip;
            overrideSourceSelectionInstanceId = GetSelectedTargetInstanceId();
            originalClipForPreview = clip;
            generatedClipForPreview = null;
            generatedPreviewHistory.Clear();
            generatedPreviewIndex = -1;
            previewMode = PreviewMode.Original;
            retargetAvatarBridgeModelName = NormalizeBridgeModelName(bridgeModelName);
            avatarPreviewCore?.RestartFromZeroAndPlay();
        }

        public string GetSuggestedPrompt()
        {
            if (selectedState != null)
            {
                return ResolvePromptFromState(selectedState);
            }

            if (selectedTransition != null)
            {
                return ResolvePromptFromTransition(selectedTransition, selectedFromState);
            }

            AnimationClip clip = originalClipForPreview;
            if (clip == null)
            {
                TryResolveSelectedSourceClip(out clip, out _);
            }

            return clip != null && !string.IsNullOrWhiteSpace(clip.name)
                ? clip.name.Trim()
                : string.Empty;
        }

        public float GetSuggestedDurationSeconds()
        {
            if (selectedTransition != null)
            {
                AnimationClip fromClip = selectedFromState != null ? selectedFromState.motion as AnimationClip : null;
                float fromLength = Mathf.Max(0.001f, fromClip != null ? fromClip.length : 0.001f);
                float transitionDuration = selectedTransition.hasFixedDuration
                    ? Mathf.Max(0.001f, selectedTransition.duration)
                    : Mathf.Max(0.001f, selectedTransition.duration * fromLength);
                return Mathf.Max(1f / KimodoPlayableClip.FIXED_FRAME_RATE, transitionDuration);
            }

            AnimationClip clip = originalClipForPreview;
            if (clip == null)
            {
                TryResolveSelectedSourceClip(out clip, out _);
            }

            float defaultDuration = KimodoPlayableClip.DEFAULT_FRAMES / KimodoPlayableClip.FIXED_FRAME_RATE;
            return clip == null
                ? defaultDuration
                : Mathf.Max(1f / KimodoPlayableClip.FIXED_FRAME_RATE, clip.length);
        }

        public bool TryBuildExternalConstraints(string bridgeModelName, float generationDurationSeconds, bool isLoop, out string constraintsJson, out string error)
        {
            constraintsJson = string.Empty;
            error = string.Empty;

            if (!TryResolveAvatarAndMotionForSampling(bridgeModelName, preparePreviewRoot: true, out Avatar avatar, out AnimationClip sourceClip, out error))
            {
                return false;
            }

            string modelName = NormalizeBridgeModelName(bridgeModelName);
            AnimationClip beginClip = sourceClip;
            AnimationClip endClip = sourceClip;
            double beginNormalizedTime = 0.0;
            double endNormalizedTime = 1.0;
            if (selectedTransition != null)
            {
                AnimationClip destinationClip = selectedTransition.destinationState != null
                    ? selectedTransition.destinationState.motion as AnimationClip
                    : null;
                if (destinationClip == null)
                {
                    error = "Transition preview requires from/to clips.";
                    return false;
                }

                beginNormalizedTime = 1.0;
                endNormalizedTime = 0.0;
                endClip = destinationClip;
            }

            int generatedFrameCount = ResolveConstraintFrameCount(generationDurationSeconds);
            double generatedClipDurationSeconds = ResolveConstraintClipDurationSeconds(generatedFrameCount);
            double endConstraintTime = ResolveConstraintEndSampleTimeSeconds(generatedFrameCount);

            var samples = new List<KimodoMarkerSampleResult>(2);
            if (!KimodoRetargetToolsEditor.TrySampleMarkerForClip(
                    beginClip,
                    "fullbody",
                    ResolveClipSampleTime(beginClip, beginNormalizedTime),
                    avatar,
                    null,
                    null,
                    modelName,
                    forceRefresh: false,
                    out KimodoMarkerSampleResult begin,
                    out error))
            {
                return false;
            }
            if (!KimodoRetargetToolsEditor.TrySampleMarkerForClip(
                    endClip,
                    "fullbody",
                    ResolveClipSampleTime(endClip, endNormalizedTime),
                    avatar,
                    null,
                    null,
                    modelName,
                    forceRefresh: false,
                    out KimodoMarkerSampleResult end,
                    out error))
            {
                return false;
            }

            begin.constraintType = "fullbody";
            end.constraintType = "fullbody";
            begin.sampleTime = 0.0;
            end.sampleTime = endConstraintTime;
            if (isLoop)
            {
                OverwriteLoopEndPoseAxes(begin, end);
            }

            samples.Add(begin);
            if (generatedFrameCount > 1)
            {
                samples.Add(end);
            }

            constraintsJson = KimodoConstraintJsonExporter.ToConstraintsJson(samples, 0.0, generatedClipDurationSeconds);
            return true;
        }

        private static double ResolveClipSampleTime(AnimationClip clip, double normalizedTime)
        {
            if (clip == null)
            {
                return 0.0;
            }

            double duration = Math.Max(0.0, clip.length);
            if (duration <= 0.0)
            {
                return 0.0;
            }

            double clampedNormalizedTime = Math.Max(0.0, Math.Min(1.0, normalizedTime));
            if (clampedNormalizedTime <= 0.0)
            {
                return 0.0;
            }

            if (clampedNormalizedTime >= 1.0)
            {
                double epsilon = Math.Min(1e-3, duration * 0.5);
                return Math.Max(0.0, duration - epsilon);
            }

            return clampedNormalizedTime * duration;
        }

        private static void OverwriteLoopEndPoseAxes(KimodoMarkerSampleResult begin, KimodoMarkerSampleResult end)
        {
            if (begin == null || end == null)
            {
                return;
            }

            end.localAxisAngles = begin.localAxisAngles != null
                ? new List<Vector3>(begin.localAxisAngles)
                : new List<Vector3>();
            end.sampledJointIndices = begin.sampledJointIndices != null
                ? new List<int>(begin.sampledJointIndices)
                : new List<int>();
            end.jointNames = begin.jointNames != null
                ? new List<string>(begin.jointNames)
                : new List<string>();
        }

        public void LockCurrentSelection()
        {
            int selectionId = GetSelectedTargetInstanceId();
            if (selectionId == 0)
            {
                return;
            }

            selectionLatched = true;
            latchedSelectionInstanceId = selectionId;
        }

        private void ReleaseSelectionLockIfNoGeneratedResult()
        {
            if (HasGeneratedResult)
            {
                return;
            }

            selectionLatched = false;
            latchedSelectionInstanceId = 0;
            TryCaptureSelectionFromActiveObject(lockSelection: false);
            RefreshPreviewSource();
        }

        private int GetSelectedTargetInstanceId()
        {
            if (selectedTransition != null)
            {
                return selectedTransition.GetInstanceID();
            }

            if (selectedState != null)
            {
                return selectedState.GetInstanceID();
            }

            return 0;
        }

        private bool TryCaptureSelectionFromActiveObject(bool lockSelection)
        {
            ClearSelectionLatch();
            UnityEngine.Object obj = Selection.activeObject;
            if (obj == null)
            {
                return false;
            }

            if (obj is AnimatorStateTransition transition)
            {
                selectedTransition = transition;
                selectedState = null;
                selectedController = FindControllerForObject(transition);
                selectedStateMachine = FindStateMachineForTransition(selectedController, transition, out selectedFromState);
                selectionLatched = lockSelection;
                latchedSelectionInstanceId = lockSelection ? obj.GetInstanceID() : 0;
                return true;
            }

            if (obj is AnimatorState state)
            {
                selectedState = state;
                selectedTransition = null;
                selectedFromState = null;
                selectedController = FindControllerForObject(state);
                selectedStateMachine = FindStateMachineForState(selectedController, state);
                selectionLatched = lockSelection;
                latchedSelectionInstanceId = lockSelection ? obj.GetInstanceID() : 0;
                return true;
            }

            return false;
        }

        private void ClearSelectionLatch()
        {
            selectionLatched = false;
            latchedSelectionInstanceId = 0;
            selectedTransition = null;
            selectedState = null;
            selectedFromState = null;
            selectedController = null;
            selectedStateMachine = null;
        }

        private void RefreshPreviewSource()
        {
            if (!HasSelection)
            {
                DestroyPreviewRootInstance();
                ClearResolvedPreviewSource();
                return;
            }

            if (!TryResolveAvatarAndMotionForSampling(DefaultBridgeModelName, preparePreviewRoot: true, out _, out _, out _))
            {
                DestroyPreviewRootInstance();
                ClearResolvedPreviewSource();
            }
        }

        private bool TryResolveAvatarAndMotionForSampling(
            string bridgeModelName,
            bool preparePreviewRoot,
            out Avatar avatar,
            out AnimationClip sourceClip,
            out string error)
        {
            avatar = null;
            sourceClip = null;
            error = string.Empty;
            string modelName = NormalizeBridgeModelName(bridgeModelName);

            if (!TryResolveSelectedSourceClip(out sourceClip, out error))
            {
                return false;
            }

            if (!preparePreviewRoot && IsResolvedPreviewSourceCacheValid(sourceClip, modelName))
            {
                avatar = retargetAvatarForPreview;
                return true;
            }

            bool avatarDependsOnBridgeModel;
            if (preparePreviewRoot)
            {
                if (!TryPreparePreviewSource(sourceClip, modelName, out avatar, out avatarDependsOnBridgeModel, out error))
                {
                    return false;
                }
            }
            else if (!TryResolveAvatarWithoutPreviewRoot(sourceClip, modelName, out avatar, out avatarDependsOnBridgeModel, out error))
            {
                return false;
            }

            CacheResolvedPreviewSource(sourceClip, avatar, avatarDependsOnBridgeModel, modelName);
            return true;
        }

        private bool TryResolveSelectedSourceClip(out AnimationClip sourceClip, out string error)
        {
            sourceClip = null;
            error = string.Empty;
            if (TryGetBlendTreeSelectionWarning(out error))
            {
                return false;
            }

            if (overrideSourceClipForGeneration != null && IsOverrideSourceClipValidForCurrentSelection())
            {
                sourceClip = overrideSourceClipForGeneration;
                return true;
            }

            if (selectedState != null)
            {
                sourceClip = selectedState.motion as AnimationClip;
                if (sourceClip == null)
                {
                    error = "State motion is not an AnimationClip.";
                    return false;
                }
                return true;
            }

            if (selectedTransition != null)
            {
                AnimatorState from = selectedFromState;
                AnimatorState to = selectedTransition.destinationState;
                AnimationClip fromClip = from != null ? from.motion as AnimationClip : null;
                AnimationClip toClip = to != null ? to.motion as AnimationClip : null;
                if (fromClip == null || toClip == null)
                {
                    error = "Transition preview requires from/to clips.";
                    return false;
                }
                sourceClip = fromClip;
                return true;
            }

            error = "No selected transition or state.";
            return false;
        }

        private bool TryGetBlendTreeSelectionWarning(out string warning)
        {
            warning = string.Empty;

            if (selectedState != null && selectedState.motion is BlendTree)
            {
                warning = BlendTreeUnsupportedMessage;
                return true;
            }

            if (selectedFromState != null && selectedFromState.motion is BlendTree)
            {
                warning = BlendTreeUnsupportedMessage;
                return true;
            }

            if (selectedTransition != null &&
                selectedTransition.destinationState != null &&
                selectedTransition.destinationState.motion is BlendTree)
            {
                warning = BlendTreeUnsupportedMessage;
                return true;
            }

            return false;
        }

        private bool IsResolvedPreviewSourceCacheValid(AnimationClip sourceClip, string modelName)
        {
            if (sourceClip == null ||
                originalClipForPreview != sourceClip ||
                retargetAvatarForPreview == null ||
                !retargetAvatarForPreview.isValid ||
                !retargetAvatarForPreview.isHuman)
            {
                return false;
            }

            return !retargetAvatarDependsOnBridgeModel ||
                string.Equals(retargetAvatarBridgeModelName, modelName, StringComparison.Ordinal);
        }

        private bool IsOverrideSourceClipValidForCurrentSelection()
        {
            return overrideSourceClipForGeneration != null &&
                overrideSourceSelectionInstanceId != 0 &&
                overrideSourceSelectionInstanceId == GetSelectedTargetInstanceId();
        }

        private void CacheResolvedPreviewSource(AnimationClip sourceClip, Avatar avatar, bool avatarDependsOnBridgeModel, string bridgeModelName)
        {
            originalClipForPreview = sourceClip;
            retargetAvatarForPreview = avatar;
            retargetAvatarDependsOnBridgeModel = avatarDependsOnBridgeModel;
            retargetAvatarBridgeModelName = avatarDependsOnBridgeModel ? bridgeModelName : string.Empty;
        }

        private void ClearResolvedPreviewSource()
        {
            overrideSourceClipForGeneration = null;
            overrideSourceSelectionInstanceId = 0;
            originalClipForPreview = null;
            retargetAvatarForPreview = null;
            retargetAvatarDependsOnBridgeModel = false;
            retargetAvatarBridgeModelName = string.Empty;
        }

        private static int ResolveConstraintFrameCount(float generationDurationSeconds)
        {
            float minDuration = KimodoPlayableClip.MIN_FRAMES / KimodoPlayableClip.FIXED_FRAME_RATE;
            float maxDuration = KimodoPlayableClip.MAX_FRAMES / KimodoPlayableClip.FIXED_FRAME_RATE;
            float clampedDuration = Mathf.Clamp(generationDurationSeconds, minDuration, maxDuration);
            return Mathf.Clamp(
                Mathf.RoundToInt(clampedDuration * KimodoPlayableClip.FIXED_FRAME_RATE),
                KimodoPlayableClip.MIN_FRAMES,
                KimodoPlayableClip.MAX_FRAMES);
        }

        private static double ResolveConstraintClipDurationSeconds(int frameCount)
        {
            int clampedFrameCount = Mathf.Clamp(frameCount, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES);
            return clampedFrameCount / KimodoPlayableClip.FIXED_FRAME_RATE;
        }

        private static double ResolveConstraintEndSampleTimeSeconds(int frameCount)
        {
            int clampedFrameCount = Mathf.Clamp(frameCount, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES);
            return clampedFrameCount <= 1
                ? 0.0
                : (clampedFrameCount - 1.0) / KimodoPlayableClip.FIXED_FRAME_RATE;
        }

        private void SetGeneratedPreviewByIndex()
        {
            if (generatedPreviewHistory.Count == 0)
            {
                generatedClipForPreview = null;
                return;
            }

            generatedPreviewIndex = Mathf.Clamp(generatedPreviewIndex, 0, generatedPreviewHistory.Count - 1);
            generatedClipForPreview = generatedPreviewHistory[generatedPreviewIndex];
            previewMode = PreviewMode.Generated;
            avatarPreviewCore?.RestartFromZeroAndPlay();
        }


        private bool TryPreparePreviewSource(
            AnimationClip sourceClip,
            string bridgeModelName,
            out Avatar avatar,
            out bool avatarDependsOnBridgeModel,
            out string error)
        {
            avatar = null;
            avatarDependsOnBridgeModel = false;
            error = string.Empty;
            DestroyPreviewRootInstance();

            GameObject sourceRoot = null;
            bool sourceRootOwned = false;
            if (!TryResolveScenePreviewSourceByController(selectedController, out sourceRoot, out avatar))
            {
                sourceRoot = LoadClipOwnerModelAsset(sourceClip);
                avatar = null;
                if (sourceRoot == null)
                {
                    string modelName = NormalizeBridgeModelName(bridgeModelName);
                    if (KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(modelName, out Avatar fallbackAvatar, out _)
                        && fallbackAvatar != null && fallbackAvatar.isValid && fallbackAvatar.isHuman)
                    {
                        if (!KimodoRetargetAvatarUtility.TryCreateTemporaryHumanoidRoot(
                            fallbackAvatar,
                            "KimodoPreviewSkeletonTemplate",
                            animatorEnabled: false,
                            applyRootMotion: true,
                            out sourceRoot,
                            out _,
                            out _))
                        {
                            sourceRoot = null;
                        }
                        else
                        {
                            sourceRootOwned = true;
                            avatarDependsOnBridgeModel = true;
                        }
                    }
                }
            }

            if (sourceRoot == null)
            {
                error = "Cannot resolve preview character source.";
                return false;
            }

            previewRootInstance = sourceRootOwned
                ? sourceRoot
                : UnityEngine.Object.Instantiate(sourceRoot);
            previewRootInstance.name = "KimodoPreviewRoot";
            previewRootInstance.hideFlags = HideFlags.HideAndDontSave;

            Animator animator = previewRootInstance.GetComponentInChildren<Animator>(true);
            if (animator == null)
            {
                animator = previewRootInstance.AddComponent<Animator>();
            }

            if (avatar == null)
            {
                if (!KimodoLocalAvatarUtility.TryEnsureHumanoidAvatar(previewRootInstance, out avatar, out _, out error))
                {
                    DestroyPreviewRootInstance();
                    return false;
                }
            }
            else if (!KimodoLocalAvatarUtility.CheckAvatarValid(avatar, previewRootInstance))
            {
                DestroyPreviewRootInstance();
                error = "Scene animator avatar is not compatible with preview template.";
                return false;
            }

            animator.avatar = avatar;
            animator.enabled = false;
            animator.applyRootMotion = true;
            animator.Rebind();
            animator.Update(0f);
            return avatar != null && avatar.isValid && avatar.isHuman;
        }

        private bool TryResolveAvatarWithoutPreviewRoot(
            AnimationClip sourceClip,
            string bridgeModelName,
            out Avatar avatar,
            out bool avatarDependsOnBridgeModel,
            out string error)
        {
            avatar = null;
            avatarDependsOnBridgeModel = false;
            error = string.Empty;

            if (TryResolveScenePreviewSourceByController(selectedController, out _, out avatar))
            {
                return avatar != null && avatar.isValid && avatar.isHuman;
            }

            GameObject sourceRoot = LoadClipOwnerModelAsset(sourceClip);
            if (sourceRoot != null)
            {
                return KimodoLocalAvatarUtility.TryEnsureHumanoidAvatar(sourceRoot, out avatar, out _, out error) &&
                    avatar != null &&
                    avatar.isValid &&
                    avatar.isHuman;
            }

            if (KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(bridgeModelName, out avatar, out error) &&
                avatar != null &&
                avatar.isValid &&
                avatar.isHuman)
            {
                avatarDependsOnBridgeModel = true;
                return true;
            }

            if (string.IsNullOrWhiteSpace(error))
            {
                error = "Cannot resolve preview avatar.";
            }
            return false;
        }

        private static string NormalizeBridgeModelName(string bridgeModelName)
        {
            return string.IsNullOrWhiteSpace(bridgeModelName)
                ? DefaultBridgeModelName
                : bridgeModelName.Trim();
        }

        private static string ResolvePromptFromState(AnimatorState state)
        {
            if (state == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(state.name))
            {
                return state.name.Trim();
            }

            AnimationClip clip = state.motion as AnimationClip;
            return clip != null && !string.IsNullOrWhiteSpace(clip.name)
                ? clip.name.Trim()
                : string.Empty;
        }

        private static string ResolvePromptFromTransition(AnimatorStateTransition transition, AnimatorState fromState)
        {
            if (transition == null)
            {
                return string.Empty;
            }

            string fromName = ResolvePromptFromState(fromState);
            if (string.IsNullOrWhiteSpace(fromName))
            {
                fromName = "From";
            }

            string toName = ResolvePromptFromState(transition.destinationState);
            if (string.IsNullOrWhiteSpace(toName))
            {
                if (transition.isExit)
                {
                    toName = "Exit";
                }
                else if (transition.destinationStateMachine != null &&
                    !string.IsNullOrWhiteSpace(transition.destinationStateMachine.name))
                {
                    toName = transition.destinationStateMachine.name.Trim();
                }
                else
                {
                    toName = "To";
                }
            }

            return $"transition {fromName} to {toName}";
        }

        private static AnimatorController FindControllerForObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return null;
            }
            string path = AssetDatabase.GetAssetPath(target);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            return AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        }

        private static AnimatorStateMachine FindStateMachineForState(AnimatorController controller, AnimatorState state)
        {
            if (controller == null || state == null)
            {
                return null;
            }

            for (int i = 0; i < controller.layers.Length; i++)
            {
                AnimatorStateMachine sm = controller.layers[i].stateMachine;
                if (TryFindStateMachineForStateRecursive(sm, state, out AnimatorStateMachine owner))
                {
                    return owner;
                }
            }
            return null;
        }

        private static AnimatorStateMachine FindStateMachineForTransition(AnimatorController controller, AnimatorStateTransition transition, out AnimatorState fromState)
        {
            fromState = null;
            if (controller == null || transition == null)
            {
                return null;
            }

            for (int i = 0; i < controller.layers.Length; i++)
            {
                AnimatorStateMachine sm = controller.layers[i].stateMachine;
                if (TryFindStateMachineForTransitionRecursive(sm, transition, out AnimatorStateMachine owner, out fromState))
                {
                    return owner;
                }
            }
            return null;
        }

        private static bool TryFindStateMachineForStateRecursive(
            AnimatorStateMachine stateMachine,
            AnimatorState targetState,
            out AnimatorStateMachine owner)
        {
            owner = null;
            if (stateMachine == null || targetState == null)
            {
                return false;
            }

            ChildAnimatorState[] states = stateMachine.states;
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].state == targetState)
                {
                    owner = stateMachine;
                    return true;
                }
            }

            ChildAnimatorStateMachine[] childStateMachines = stateMachine.stateMachines;
            for (int i = 0; i < childStateMachines.Length; i++)
            {
                if (TryFindStateMachineForStateRecursive(childStateMachines[i].stateMachine, targetState, out owner))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindStateMachineForTransitionRecursive(
            AnimatorStateMachine stateMachine,
            AnimatorStateTransition transition,
            out AnimatorStateMachine owner,
            out AnimatorState fromState)
        {
            owner = null;
            fromState = null;
            if (stateMachine == null || transition == null)
            {
                return false;
            }

            ChildAnimatorState[] states = stateMachine.states;
            for (int i = 0; i < states.Length; i++)
            {
                AnimatorState state = states[i].state;
                if (state == null)
                {
                    continue;
                }

                AnimatorStateTransition[] transitions = state.transitions;
                for (int j = 0; j < transitions.Length; j++)
                {
                    if (transitions[j] == transition)
                    {
                        owner = stateMachine;
                        fromState = state;
                        return true;
                    }
                }
            }

            ChildAnimatorStateMachine[] childStateMachines = stateMachine.stateMachines;
            for (int i = 0; i < childStateMachines.Length; i++)
            {
                if (TryFindStateMachineForTransitionRecursive(
                        childStateMachines[i].stateMachine,
                        transition,
                        out owner,
                        out fromState))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveScenePreviewSourceByController(AnimatorController controller, out GameObject sourceRoot, out Avatar avatar)
        {
            sourceRoot = null;
            avatar = null;
            if (controller == null)
            {
                return false;
            }

            Animator[] animators = Resources.FindObjectsOfTypeAll<Animator>();
            for (int i = 0; i < animators.Length; i++)
            {
                Animator a = animators[i];
                if (!IsUsableSceneAnimator(a) || !IsControllerMatch(a, controller))
                {
                    continue;
                }

                GameObject candidateRoot = a.avatarRoot != null ? a.avatarRoot.gameObject : a.gameObject;
                Avatar candidateAvatar = a.avatar;
                if (candidateRoot == null || candidateAvatar == null || !candidateAvatar.isValid || !candidateAvatar.isHuman)
                {
                    continue;
                }

                if (!KimodoLocalAvatarUtility.CheckAvatarValid(candidateAvatar, candidateRoot))
                {
                    continue;
                }

                sourceRoot = candidateRoot;
                avatar = candidateAvatar;
                return true;
            }

            return false;
        }

        private static bool IsUsableSceneAnimator(Animator animator)
        {
            if (animator == null)
            {
                return false;
            }

            GameObject gameObject = animator.gameObject;
            if (gameObject == null || !gameObject.scene.IsValid())
            {
                return false;
            }

            if (EditorUtility.IsPersistent(animator) || EditorUtility.IsPersistent(gameObject))
            {
                return false;
            }

            if ((animator.hideFlags & HideFlags.HideAndDontSave) != 0 ||
                (gameObject.hideFlags & HideFlags.HideAndDontSave) != 0)
            {
                return false;
            }

            return true;
        }

        private static bool IsControllerMatch(Animator animator, AnimatorController controller)
        {
            if (animator == null || controller == null)
            {
                return false;
            }

            RuntimeAnimatorController runtimeController = ResolveAnimatorController(animator);
            if (runtimeController == null)
            {
                return false;
            }

            if (ReferenceEquals(runtimeController, controller))
            {
                return true;
            }

            AnimatorOverrideController overrideController = runtimeController as AnimatorOverrideController;
            return overrideController != null && ReferenceEquals(overrideController.runtimeAnimatorController, controller);
        }

        private static RuntimeAnimatorController ResolveAnimatorController(Animator animator)
        {
            if (animator == null)
            {
                return null;
            }

            if (animator.runtimeAnimatorController != null)
            {
                return animator.runtimeAnimatorController;
            }

            var serializedObject = new SerializedObject(animator);
            var controllerProperty = serializedObject.FindProperty("m_Controller");
            var controllerValue = controllerProperty != null ? controllerProperty.objectReferenceValue as RuntimeAnimatorController : null;
            return controllerValue;
        }

        private static GameObject LoadClipOwnerModelAsset(AnimationClip clip)
        {
            if (clip == null)
            {
                return null;
            }

            string clipPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrWhiteSpace(clipPath))
            {
                return null;
            }
            return AssetDatabase.LoadAssetAtPath<GameObject>(clipPath);
        }

        private void DestroyPreviewRootInstance()
        {
            if (previewRootInstance != null)
            {
                UnityEngine.Object.DestroyImmediate(previewRootInstance);
            }

            previewRootInstance = null;
        }
    }
}
