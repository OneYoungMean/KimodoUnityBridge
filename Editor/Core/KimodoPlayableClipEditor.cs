using System;
using System.Collections.Generic;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoPlayableClip))]
    public partial class KimodoPlayableClipEditor : UnityEditor.Editor
    {
        private const double RepaintIntervalSeconds = 0.2d;

        private SerializedProperty generationBackend;
        private SerializedProperty comfyuiIP;
        private SerializedProperty comfyuiPort;
        private SerializedProperty bridgeModelName;
        private SerializedProperty bridgeVramMode;
        private SerializedProperty motionPrompt;
        private SerializedProperty generationFrames;
        private SerializedProperty diffusionSteps;
        private SerializedProperty randomProp;
        private SerializedProperty seed;
        private SerializedProperty inOutConstraintModeProp;
        private SerializedProperty showConstraint;
        private SerializedProperty normalizeConstraintOrigin;

        private SerializedProperty animationClipProp;
        private SerializedProperty footIKProp;
        private SerializedProperty loopProp;
        private SerializedProperty autoRetargetOnBindingProp;
        private SerializedProperty customRetargetAvatarProp;
        private SerializedProperty curveFilterOptionsProp;

        private KimodoPlayableClip clip;
        private bool isGenerating;
        private string lastStatus;
        private string lastError;
        private string lastConstraintsPath = string.Empty;
        private readonly List<KimodoConstraintMarkerBase> lastConstraintMarkers = new List<KimodoConstraintMarkerBase>();
        private bool bridgeRunningCached;
        private bool bridgePortDiscoveredCached;
        private bool bridgeStatusReady;
        private bool showAdvancedFoldout = true;
        private bool managerSubscribed;
        private double lastRepaintTime;
        private bool repaintQueued;

        private void OnEnable()
        {
            InitializeSerializedBindings();
            showAdvancedFoldout = KimodoPlayableClipGenerationSettings.instance.AdvancedCurveFilterFoldout;
            PullBridgeStatusSnapshot(forceRefresh: true);
            SubscribeManagerEvents();
        }

        private void InitializeSerializedBindings()
        {
            clip = (KimodoPlayableClip)target;
            generationBackend = serializedObject.FindProperty("generationBackend");
            comfyuiIP = serializedObject.FindProperty("comfyuiIP");
            comfyuiPort = serializedObject.FindProperty("comfyuiPort");
            bridgeModelName = serializedObject.FindProperty("bridgeModelName");
            bridgeVramMode = serializedObject.FindProperty("bridgeVramMode");
            motionPrompt = serializedObject.FindProperty("motionPrompt");
            generationFrames = serializedObject.FindProperty("generationFrames");
            diffusionSteps = serializedObject.FindProperty("diffusionSteps");
            randomProp = serializedObject.FindProperty("randomSeed");
            seed = serializedObject.FindProperty("seed");
            inOutConstraintModeProp = serializedObject.FindProperty("inOutConstraintMode");
            showConstraint = serializedObject.FindProperty("showConstraint");
            normalizeConstraintOrigin = serializedObject.FindProperty("normalizeConstraintOrigin");

            animationClipProp = serializedObject.FindProperty("m_Clip");
            footIKProp = serializedObject.FindProperty("m_ApplyFootIK");
            loopProp = serializedObject.FindProperty("m_Loop");
            autoRetargetOnBindingProp = serializedObject.FindProperty("autoRetargetOnBinding");
            customRetargetAvatarProp = serializedObject.FindProperty("customRetargetAvatar");
            curveFilterOptionsProp = serializedObject.FindProperty("curveFilterOptions");
        }

        internal void SetBridgeGenerationInputsForTests(
            string prompt,
            int generationFramesValue,
            int diffusionStepsValue,
            bool randomSeedEnabled,
            int seedValue)
        {
            InitializeSerializedBindings();
            serializedObject.UpdateIfRequiredOrScript();
            generationBackend.intValue = (int)KimodoGenerationBackend.KimodoBridge;
            motionPrompt.stringValue = prompt ?? string.Empty;
            generationFrames.intValue = Mathf.Clamp(generationFramesValue, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES);
            diffusionSteps.intValue = Mathf.Clamp(diffusionStepsValue, 1, 1000);
            randomProp.boolValue = randomSeedEnabled;
            seed.intValue = seedValue;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private void OnDisable()
        {
            UnsubscribeManagerEvents();
            TryHideConstraintPreview();
            EditorUtility.ClearProgressBar();
            repaintQueued = false;
        }

        public override void OnInspectorGUI()
        {
            if (clip == null)
            {
                EditorGUILayout.HelpBox("Target clip is null.", MessageType.Error);
                return;
            }

            PullBridgeStatusSnapshot(forceRefresh: false);
            serializedObject.UpdateIfRequiredOrScript();
            DrawGenerationSection();
            DrawBakeSection();
            DrawErrorSection();
            DrawGeneratedInfo();
            DrawAnimationClipSection();
            if (serializedObject.hasModifiedProperties)
            {
                serializedObject.ApplyModifiedProperties();
            }
        }

        private void DrawGenerationSection()
        {
            EditorGUILayout.LabelField("Generate Motion", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            if (generationBackend != null)
            {
                EditorGUILayout.PropertyField(generationBackend, new GUIContent("Backend"));
            }

            bool useBridge = clip.generationBackend == KimodoGenerationBackend.KimodoBridge;
            if (useBridge)
            {
                if (bridgeModelName != null)
                {
                    DrawBridgeModelSelector();
                }
                if (bridgeVramMode != null)
                {
                    EditorGUILayout.PropertyField(
                        bridgeVramMode,
                        new GUIContent("VRAM Mode", "Low: quantized text encoder (~4G). High: full Llama+LLM2Vec (~16G)."));
                }

                int encoderVramGb = clip.bridgeVramMode == KimodoBridgeVramMode.High ? 16 : 4;
                int totalVramGb = 2 + encoderVramGb;
                EditorGUILayout.HelpBox(
                    $"Estimated VRAM for selected mode: ~{totalVramGb} GB (core 2 GB + encoder {encoderVramGb} GB).",
                    MessageType.Info);
            }
            else
            {
                comfyuiIP.stringValue = EditorGUILayout.TextField("ComfyUI IP", comfyuiIP.stringValue);
                comfyuiPort.intValue = EditorGUILayout.IntField("ComfyUI Port", comfyuiPort.intValue);
                EditorGUILayout.HelpBox("Workflow source is fixed to Runtime/Resources/kimodo-unity-workflow.json.", MessageType.Info);
            }

            EditorGUILayout.LabelField(new GUIContent("Prompt", "Natural-language motion prompt sent to the selected generation backend."));
            motionPrompt.stringValue = EditorGUILayout.TextArea(motionPrompt.stringValue, GUILayout.Height(60));

            int oldFrames = generationFrames.intValue;
            float minDurationSeconds = KimodoInOutConstraintTimingUtility.FrameCountToDurationSeconds(KimodoPlayableClip.MIN_FRAMES);
            float maxDurationSeconds = KimodoInOutConstraintTimingUtility.FrameCountToDurationSeconds(KimodoPlayableClip.MAX_FRAMES);
            float oldDurationSeconds = KimodoInOutConstraintTimingUtility.FrameCountToDurationSeconds(oldFrames);
            float newDurationSeconds = EditorGUILayout.Slider(
                new GUIContent("Duration (s)", "Target generated clip length in seconds. Internally uses the fixed Kimodo sample rate and also syncs timeline clip duration when changed."),
                oldDurationSeconds,
                minDurationSeconds,
                maxDurationSeconds);
            int newFrames = KimodoInOutConstraintTimingUtility.DurationSecondsToFrameCount(newDurationSeconds);
            if (newFrames != oldFrames)
            {
                generationFrames.intValue = newFrames;
                TrySyncTimelineDuration(newFrames);
            }

            diffusionSteps.intValue = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Diffusion Steps", "Sampling steps for generation. Higher values increase compute time and may improve fidelity."), diffusionSteps.intValue), 1, 1000);

            EditorGUILayout.BeginHorizontal();
            randomProp.boolValue = EditorGUILayout.ToggleLeft(new GUIContent("Random", "Use a random seed on each generation run."), randomProp.boolValue, GUILayout.Width(110f));
            EditorGUI.BeginDisabledGroup(randomProp.boolValue);
            seed.intValue = EditorGUILayout.IntField(new GUIContent("Seed", "Deterministic seed used when Random is disabled."), seed.intValue);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            if (inOutConstraintModeProp != null)
            {
                EditorGUILayout.PropertyField(
                    inOutConstraintModeProp,
                    new GUIContent("InOut Constraint", "None disables boundary constraints. Inside uses this clip's own start/end poses. Outside uses neighboring clip boundary poses."));
            }
            if (showConstraint != null)
            {
                EditorGUILayout.PropertyField(
                    showConstraint,
                    new GUIContent("Show Constraint", "Show constraint previews for this clip when selected."));
            }
            DrawConstraintPreviewIfNeeded();

            float seconds = KimodoInOutConstraintTimingUtility.FrameCountToDurationSeconds(generationFrames.intValue);
            EditorGUILayout.LabelField($"Duration: {seconds:F2}s", EditorStyles.miniLabel);
            DrawConstraintReferenceList();

            bool disableGenerate =
                isGenerating ||
                KimodoBridgeController.IsRuntimeMaintenanceInProgress ||
                EditorCompilationStateGate.IsCompilingOrReloading;
            GUI.enabled = !disableGenerate;
            if (GUILayout.Button(new GUIContent("Generate & Bake", "Generate motion using current settings and bake result back into this playable clip."), GUILayout.Height(32)))
            {
                bool accepted = KimodoEditorCommandManager.Dispatch(
                    new GeneratePlayableClipCommand(clip));
                if (accepted)
                {
                    isGenerating = true;
                    lastError = string.Empty;
                    lastStatus = "Queued generation...";
                }
            }
            GUI.enabled = isGenerating;
            if (GUILayout.Button(new GUIContent("Cancel", "Cancel the current generation command for this clip."), GUILayout.Height(24)))
            {
                KimodoEditorCommandManager.Dispatch(
                    new CancelPlayableClipGenerationCommand(clip));
            }
            GUI.enabled = true;

            if (useBridge)
            {
                DrawEstimatedSetupTimeHint();
            }

            if (useBridge)
            {
                if (!bridgeStatusReady)
                {
                    EditorGUILayout.LabelField("Bridge status: checking...", EditorStyles.miniLabel);
                }

                if (!bridgeRunningCached && bridgePortDiscoveredCached)
                {
                    EditorGUILayout.HelpBox(
                        "Bridge process is not running, but endpoint file still exists. This is usually a stale serverport record.",
                        MessageType.None);
                }
            }

            if (!string.IsNullOrWhiteSpace(lastStatus))
            {
                EditorGUILayout.LabelField(lastStatus, EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void DrawConstraintReferenceList()
        {
            EditorGUILayout.LabelField("Constraint References", EditorStyles.miniBoldLabel);
            if (lastConstraintMarkers.Count == 0)
            {
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            }
            else
            {
                for (int i = 0; i < lastConstraintMarkers.Count; i++)
                {
                    KimodoConstraintMarkerBase marker = lastConstraintMarkers[i];
                    if (marker == null)
                    {
                        continue;
                    }

                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(
                            new GUIContent($"{marker.ConstraintType} @ {marker.time:F3}s"),
                            marker,
                            typeof(KimodoConstraintMarkerBase),
                            true);
                    }
                }
            }
        }

        private void PullBridgeStatusSnapshot(bool forceRefresh)
        {
            if (clip == null || clip.generationBackend != KimodoGenerationBackend.KimodoBridge)
            {
                return;
            }

            _ = forceRefresh;
            ServerStatusSnapshot snapshot = KimodoBridgeController.GetServerStatusSnapshot();
            bridgeStatusReady = snapshot.Ready;
            bridgeRunningCached = snapshot.Running;
            bridgePortDiscoveredCached = snapshot.HasPort;
        }

        private void DrawAnimationClipSection()
        {
            EditorGUILayout.LabelField("Animation Clip", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (animationClipProp != null)
            {
                EditorGUILayout.PropertyField(animationClipProp, new GUIContent("Clip", "Baked Unity AnimationClip used by this playable clip."));
            }
            else
            {
                EditorGUILayout.HelpBox("Clip property not found.", MessageType.Warning);
            }

            if (footIKProp != null)
            {
                EditorGUILayout.PropertyField(footIKProp, new GUIContent("Foot IK", "Enable Animator foot IK during playback."));
            }

            if (loopProp != null)
            {
                EditorGUILayout.PropertyField(loopProp, new GUIContent("Loop", "Loop this clip when timeline playback exceeds clip duration."));
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void DrawBakeSection()
        {
            EditorGUILayout.LabelField("Animation Bake", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (autoRetargetOnBindingProp != null)
            {
                EditorGUILayout.PropertyField(autoRetargetOnBindingProp, new GUIContent("Auto Retarget On Binding", "Automatically retarget baked motion to the bound character avatar at playback/bind time."));
            }
            if (autoRetargetOnBindingProp != null && !autoRetargetOnBindingProp.boolValue && customRetargetAvatarProp != null)
            {
                EditorGUILayout.PropertyField(customRetargetAvatarProp, new GUIContent("Custom Avatar", "Humanoid avatar used for retargeting when auto retarget on binding is disabled."));
                Avatar customAvatar = clip != null ? clip.CustomRetargetAvatar : null;
                if (customAvatar == null)
                {
                    EditorGUILayout.HelpBox("Custom Avatar is required when Auto Retarget On Binding is disabled.", MessageType.Warning);
                }
                else if (!customAvatar.isValid || !customAvatar.isHuman)
                {
                    EditorGUILayout.HelpBox("Custom Avatar must be a valid Humanoid Avatar.", MessageType.Error);
                }
            }
            DrawAdvancedCurveFilterSection();

            EditorGUILayout.EndVertical();
        }

        private void DrawBridgeModelSelector()
        {
            string current = KimodoPlayableClip.NormalizeBridgeModelName(bridgeModelName.stringValue);
            string[] options = KimodoBridgeController.SupportedModelNames;
            int idx = Array.IndexOf(options, current);
            if (idx < 0)
            {
                idx = 0;
            }

            int newIdx = EditorGUILayout.Popup(new GUIContent("Bridge Model", "Installed Kimodo model package to use for bridge generation."), idx, options);
            bridgeModelName.stringValue = options[Mathf.Clamp(newIdx, 0, options.Length - 1)];
        }

        private void DrawEstimatedSetupTimeHint()
        {
            string runtimeRoot = KimodoBridgeController.GetRuntimeRootPath();
            bool highVram = clip != null && clip.bridgeVramMode == KimodoBridgeVramMode.High;
            string modelName = clip == null ? KimodoPlayableClip.DefaultBridgeModelName : KimodoPlayableClip.NormalizeBridgeModelName(clip.bridgeModelName);
            string modelsRootOverride = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim();
            if (!KimodoBridgeController.TryGetModelMissingSetupMinutes(runtimeRoot, highVram, modelName, modelsRootOverride, out int minutes))
            {
                return;
            }
            EditorGUILayout.HelpBox($"Model missing detected, update required, approximately {minutes} minutes.", MessageType.None);
        }

        private void SubscribeManagerEvents()
        {
            if (managerSubscribed)
            {
                return;
            }

            KimodoEditorCommandManager.CommandProgress += OnManagerCommandProgress;
            KimodoEditorCommandManager.CommandCompleted += OnManagerCommandCompleted;
            KimodoEditorCommandManager.CommandFailed += OnManagerCommandFailed;
            KimodoEditorCommandManager.CommandCanceled += OnManagerCommandCanceled;
            managerSubscribed = true;
        }

        private void UnsubscribeManagerEvents()
        {
            if (!managerSubscribed)
            {
                return;
            }

            KimodoEditorCommandManager.CommandProgress -= OnManagerCommandProgress;
            KimodoEditorCommandManager.CommandCompleted -= OnManagerCommandCompleted;
            KimodoEditorCommandManager.CommandFailed -= OnManagerCommandFailed;
            KimodoEditorCommandManager.CommandCanceled -= OnManagerCommandCanceled;
            managerSubscribed = false;
        }

        private void OnManagerCommandProgress(KimodoEditorCommandProgressEvent evt)
        {
            if (!IsCommandForCurrentClip(evt.Command))
            {
                return;
            }

            if (evt.Command.Kind == KimodoEditorCommandKind.GeneratePlayableClip)
            {
                isGenerating = true;
            }

            lastStatus = evt.Message;
            RequestThrottledRepaint();
        }

        private void OnManagerCommandCompleted(KimodoEditorCommandCompletedEvent evt)
        {
            if (evt.Command.Kind == KimodoEditorCommandKind.BridgeStopServer)
            {
                PullBridgeStatusSnapshot(forceRefresh: true);
                RequestThrottledRepaint();
                return;
            }

            if (!IsCommandForCurrentClip(evt.Command))
            {
                return;
            }

            if (evt.Command.Kind == KimodoEditorCommandKind.GeneratePlayableClip)
            {
                isGenerating = false;
                lastError = string.Empty;
                lastStatus = "Generation complete.";
                if (evt.Payload is KimodoEditorGenerateResult generateResult &&
                    !string.IsNullOrWhiteSpace(generateResult.ConstraintsPath))
                {
                    lastConstraintsPath = generateResult.ConstraintsPath;
                }

                lastConstraintMarkers.Clear();
                var latestMarkers = KimodoPlayableClipGenerationHostService.GetLatestConstraintMarkers();
                if (latestMarkers != null)
                {
                    for (int i = 0; i < latestMarkers.Count; i++)
                    {
                        KimodoConstraintMarkerBase marker = latestMarkers[i];
                        if (marker != null)
                        {
                            lastConstraintMarkers.Add(marker);
                        }
                    }
                }
            }

            RequestThrottledRepaint();
        }

        private void OnManagerCommandFailed(KimodoEditorCommandFailedEvent evt)
        {
            if (!IsCommandForCurrentClip(evt.Command))
            {
                return;
            }

            if (evt.Command.Kind == KimodoEditorCommandKind.GeneratePlayableClip)
            {
                isGenerating = false;
                lastError = evt.Message;
                lastStatus = "Generation failed.";
            }
            else
            {
                lastError = evt.Message;
            }

            RequestThrottledRepaint();
        }

        private void OnManagerCommandCanceled(KimodoEditorCommandCanceledEvent evt)
        {
            if (!IsCommandForCurrentClip(evt.Command))
            {
                return;
            }

            if (evt.Command.Kind == KimodoEditorCommandKind.GeneratePlayableClip ||
                evt.Command.Kind == KimodoEditorCommandKind.CancelPlayableClipGeneration)
            {
                isGenerating = false;
                lastStatus = "Generation canceled.";
            }

            RequestThrottledRepaint();
        }

        private void RequestThrottledRepaint()
        {
            if (this == null)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now - lastRepaintTime >= RepaintIntervalSeconds)
            {
                lastRepaintTime = now;
                Repaint();
                return;
            }

            if (repaintQueued)
            {
                return;
            }

            repaintQueued = true;
            EditorApplication.delayCall += FlushQueuedRepaint;
        }

        private void FlushQueuedRepaint()
        {
            repaintQueued = false;
            if (this == null)
            {
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            if (now - lastRepaintTime < RepaintIntervalSeconds)
            {
                if (!repaintQueued)
                {
                    repaintQueued = true;
                    EditorApplication.delayCall += FlushQueuedRepaint;
                }
                return;
            }

            lastRepaintTime = now;
            Repaint();
        }

        private bool IsCommandForCurrentClip(IKimodoEditorCommand command)
        {
            if (command == null || clip == null)
            {
                return false;
            }

            return string.Equals(command.TargetKey, "clip:" + clip.GetInstanceID(), StringComparison.Ordinal);
        }

        private void DrawAdvancedCurveFilterSection()
        {
            if (curveFilterOptionsProp == null)
            {
                return;
            }

            EditorGUILayout.Space(4f);
            bool newFoldout = EditorGUILayout.Foldout(showAdvancedFoldout, new GUIContent("Advanced", "Constraint normalization, motion compensation, and curve filtering options for generated animation curves."), true);
            if (newFoldout != showAdvancedFoldout)
            {
                showAdvancedFoldout = newFoldout;
                KimodoPlayableClipGenerationSettings.instance.AdvancedCurveFilterFoldout = showAdvancedFoldout;
                KimodoPlayableClipGenerationSettings.instance.SaveSettings();
            }
            if (!showAdvancedFoldout)
            {
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Constraint Options", EditorStyles.boldLabel);

            if (normalizeConstraintOrigin != null)
            {
                EditorGUILayout.PropertyField(
                    normalizeConstraintOrigin,
                    new GUIContent("Normalize Constraint Origin", "Use the first available boundary constraint as the local origin before export."));
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Curve Filter Options", EditorStyles.boldLabel);

            SerializedProperty enabledProp = curveFilterOptionsProp.FindPropertyRelative("enabled");
            SerializedProperty positionErrorProp = curveFilterOptionsProp.FindPropertyRelative("positionError");
            SerializedProperty rotationErrorProp = curveFilterOptionsProp.FindPropertyRelative("rotationError");
            SerializedProperty floatErrorProp = curveFilterOptionsProp.FindPropertyRelative("floatError");
            SerializedProperty ensureQuatProp = curveFilterOptionsProp.FindPropertyRelative("ensureQuaternionContinuity");

            if (enabledProp != null)
            {
                EditorGUILayout.PropertyField(enabledProp, new GUIContent("Reduce Keyframes", "Enable curve keyframe reduction after bake."));
            }

            bool curveFilterEnabled = enabledProp == null || enabledProp.boolValue;
            if (curveFilterEnabled)
            {
                if (positionErrorProp != null)
                {
                    positionErrorProp.floatValue = EditorGUILayout.Slider(
                        new GUIContent("Position Error", "Maximum tolerated positional error during keyframe reduction."),
                        positionErrorProp.floatValue,
                        0f,
                        1f);
                }

                if (rotationErrorProp != null)
                {
                    rotationErrorProp.floatValue = EditorGUILayout.Slider(
                        new GUIContent("Rotation Error", "Maximum tolerated rotational error during keyframe reduction."),
                        rotationErrorProp.floatValue,
                        0f,
                        1f);
                }

                if (floatErrorProp != null)
                {
                    floatErrorProp.floatValue = EditorGUILayout.Slider(
                        new GUIContent("Float Error", "Maximum tolerated scalar-property error during keyframe reduction."),
                        floatErrorProp.floatValue,
                        0f,
                        1f);
                }
            }

            if (ensureQuatProp != null)
            {
                EditorGUILayout.PropertyField(ensureQuatProp, new GUIContent("Ensure Quaternion Continuity", "Fix quaternion sign continuity to reduce rotation flips after keyframe reduction."));
            }

            EditorGUI.indentLevel--;
        }

        private void DrawErrorSection()
        {
            if (!string.IsNullOrEmpty(lastError))
            {
                EditorGUILayout.HelpBox(lastError, MessageType.Error);
            }
        }

        private void DrawGeneratedInfo()
        {
            if (!clip.isGenerated)
            {
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Generated", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            if (!string.IsNullOrWhiteSpace(clip.lastGeneratedPrompt))
            {
                EditorGUILayout.LabelField($"Prompt: {clip.lastGeneratedPrompt}", EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField(
                $"Duration: {KimodoInOutConstraintTimingUtility.FrameCountToDurationSeconds(clip.frameCount):F2}s, Frames: {clip.frameCount}, Joints: {clip.jointCount}",
                EditorStyles.miniLabel);
            if (!string.IsNullOrWhiteSpace(lastConstraintsPath))
            {
                EditorGUILayout.LabelField($"Constraints: {lastConstraintsPath}", EditorStyles.miniLabel);
            }

            if (GUILayout.Button(new GUIContent("Reset", "Clear generated metadata/state on this clip. Does not delete external assets."), GUILayout.Width(100)))
            {
                Undo.RecordObject(clip, "Reset Kimodo Clip");
                clip.ResetGeneration();
                EditorUtility.SetDirty(clip);
            }

            EditorGUILayout.EndVertical();
        }

        private void TrySyncTimelineDuration(int frames)
        {
            UnityEngine.Timeline.TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            if (timelineClip == null)
            {
                return;
            }

            float newDuration = KimodoInOutConstraintTimingUtility.FrameCountToDurationSeconds(frames);
            UndoExtensions.RegisterClip(timelineClip, L10n.Tr("Modify Clip Duration"));
            timelineClip.duration = newDuration;
        }

        private void DrawConstraintPreviewIfNeeded()
        {
            if (clip == null)
            {
                return;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForPlayableClip(clip, out PoseCacheRenderContext context, out TimelineClip timelineClip, out _))
            {
                KimodoConstraintPoseCache.DestroyEntriesForClipId(clip.GetInstanceID());
                return;
            }

            KimodoConstraintPoseCache.DestroyEntriesForClipId(clip.GetInstanceID(), context);

            if (showConstraint == null || !showConstraint.boolValue)
            {
                KimodoConstraintPoseCache.DestroyContext(context);
                return;
            }

            TrackAsset track = timelineClip != null ? timelineClip.GetParentTrack() : null;
            if (track == null)
            {
                KimodoConstraintPoseCache.DestroyContext(context);
                return;
            }

            var markers = new List<KimodoConstraintMarkerBase>();
            foreach (IMarker m in track.GetMarkers())
            {
                if (m is not KimodoConstraintMarkerBase marker)
                {
                    continue;
                }

                if (marker.time < timelineClip.start || marker.time > timelineClip.end)
                {
                    continue;
                }

                markers.Add(marker);
            }

            var renderItems = new List<PoseCacheRenderItem>(markers.Count + 2);
            for (int i = 0; i < markers.Count; i++)
            {
                KimodoConstraintMarkerBase marker = markers[i];
                if (marker == null)
                {
                    continue;
                }

                if (!KimodoMarkerSamplingUtility.TryNormalizeConstraintMarkerSample(marker, marker.SampleData, out KimodoMarkerSampleResult sample, out _))
                {
                    continue;
                }

                renderItems.Add(new PoseCacheRenderItem
                {
                    EntryId = KimodoConstraintMarkerEditorUtility.GetCachedIntString(marker.GetInstanceID()),
                    SampleData = sample,
                    ConstraintType = marker.ConstraintType,
                    HighlightJoints = KimodoMarkerSamplingUtility.BuildHighlightJointsForMarker(marker, context.ModelName),
                    Visible = true
                });
            }

            if (clip.inOutConstraintMode != KimodoInOutConstraintMode.None &&
                KimodoTimelineInOutConstraintAdapter.TryBuildBoundarySamplesForPreview(
                    timelineClip,
                    clip.inOutConstraintMode,
                    KimodoInOutConstraintTimingUtility.ClampFrameCount(clip.generationFrames),
                    out KimodoMarkerSampleResult beginBoundaryPose,
                    out KimodoMarkerSampleResult endBoundaryPose,
                    out _))
            {
                if (beginBoundaryPose != null)
                {
                    renderItems.Add(new PoseCacheRenderItem
                    {
                        EntryId = "inout_begin_boundary",
                        SampleData = beginBoundaryPose,
                        ConstraintType = "fullbody",
                        HighlightJoints = null,
                        Visible = true
                    });
                }

                if (endBoundaryPose != null)
                {
                    renderItems.Add(new PoseCacheRenderItem
                    {
                        EntryId = "inout_end_boundary",
                        SampleData = endBoundaryPose,
                        ConstraintType = "fullbody",
                        HighlightJoints = null,
                        Visible = true
                    });
                }
            }

            if (!KimodoConstraintPoseCache.RenderBatch(context, renderItems, out _))
            {
                KimodoConstraintPoseCache.DestroyContext(context);
            }
        }

        private void TryHideConstraintPreview()
        {
            if (clip == null)
            {
                return;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForPlayableClip(clip, out PoseCacheRenderContext context, out _, out _))
            {
                KimodoConstraintPoseCache.DestroyEntriesForClipId(clip.GetInstanceID());
                return;
            }

            KimodoConstraintPoseCache.DestroyContext(context);
        }

    }
}


