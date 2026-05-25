using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;
using KimodoUnityMotionTools.ProjectEditor.GenerationPipeline;
using KimodoUnityMotionTools.ProjectEditor.Manager;
using UnityEditor.Timeline;

namespace KimodoUnityMotionTools.ProjectEditor
{
    [CustomEditor(typeof(KimodoPlayableClip))]
    public class KimodoPlayableClipEditor : UnityEditor.Editor
    {
        private const float TargetFps = 30f;

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
        private SerializedProperty enableInbetweenInterpolation;
        private SerializedProperty workflowJsonAsset;

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
            enableInbetweenInterpolation = serializedObject.FindProperty("enableInbetweenInterpolation");
            workflowJsonAsset = serializedObject.FindProperty("workflowJsonAsset");

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
            EditorUtility.ClearProgressBar();
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

            motionPrompt.stringValue = EditorGUILayout.TextArea(motionPrompt.stringValue, GUILayout.Height(60));

            int oldFrames = generationFrames.intValue;
            int newFrames = EditorGUILayout.IntSlider("Duration (frames)", oldFrames, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES);
            if (newFrames != oldFrames)
            {
                generationFrames.intValue = newFrames;
                TrySyncTimelineDuration(newFrames);
            }

            diffusionSteps.intValue = Mathf.Clamp(EditorGUILayout.IntField("Diffusion Steps", diffusionSteps.intValue), 1, 1000);

            EditorGUILayout.BeginHorizontal();
            randomProp.boolValue = EditorGUILayout.ToggleLeft("Random", randomProp.boolValue, GUILayout.Width(110f));
            EditorGUI.BeginDisabledGroup(randomProp.boolValue);
            seed.intValue = EditorGUILayout.IntField("Seed", seed.intValue);
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            if (enableInbetweenInterpolation != null)
            {
                EditorGUILayout.PropertyField(
                    enableInbetweenInterpolation,
                    new GUIContent("In-between Interpolation", "Use neighboring clip boundary poses as constraints to generate in-between motion."));
            }

            float seconds = generationFrames.intValue / TargetFps;
            EditorGUILayout.LabelField($"Duration: {seconds:F2}s", EditorStyles.miniLabel);
            DrawConstraintReferenceList();

            bool disableGenerate = isGenerating || KimodoBridgeController.IsRuntimeMaintenanceInProgress;
            GUI.enabled = !disableGenerate;
            if (GUILayout.Button("Generate & Bake", GUILayout.Height(32)))
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
            if (GUILayout.Button("Cancel", GUILayout.Height(24)))
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

                bool closeAllowed = bridgeRunningCached || isGenerating;
                EditorGUI.BeginDisabledGroup(!closeAllowed);
                if (GUILayout.Button("Close Bridge Server", GUILayout.Height(22)))
                {
                    KimodoEditorCommandManager.Dispatch(
                        new BridgeControlCommand(
                            KimodoBridgeOperation.Stop,
                            runtimeRoot: KimodoBridgeController.GetRuntimeRootPath(),
                            modelName: clip.bridgeModelName,
                            vramMode: clip.bridgeVramMode,
                            modelsRootOverride: KimodoPlayableClipGenerationSettings.instance.LocalModelsPath));
                }
                EditorGUI.EndDisabledGroup();

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

            KimodoBridgeController.RequestServerStateRefresh(forceRefresh);
            KimodoBridgeController.ServerStatusSnapshot snapshot = KimodoBridgeController.GetServerStatusSnapshot();
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
                EditorGUILayout.PropertyField(animationClipProp, new GUIContent("Clip"));
            }
            else
            {
                EditorGUILayout.HelpBox("Clip property not found.", MessageType.Warning);
            }

            if (footIKProp != null)
            {
                EditorGUILayout.PropertyField(footIKProp, new GUIContent("Foot IK"));
            }

            if (loopProp != null)
            {
                EditorGUILayout.PropertyField(loopProp, new GUIContent("Loop"));
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
                EditorGUILayout.PropertyField(autoRetargetOnBindingProp, new GUIContent("Auto Retarget On Binding"));
            }
            if (autoRetargetOnBindingProp != null && !autoRetargetOnBindingProp.boolValue && customRetargetAvatarProp != null)
            {
                EditorGUILayout.PropertyField(customRetargetAvatarProp, new GUIContent("Custom Avatar"));
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
            string current = string.IsNullOrWhiteSpace(bridgeModelName.stringValue) ? "Kimodo-SOMA-RP-v1" : bridgeModelName.stringValue.Trim();
            string[] options = KimodoBridgeController.SupportedModelNames;
            int idx = Array.IndexOf(options, current);
            if (idx < 0)
            {
                idx = 0;
            }

            int newIdx = EditorGUILayout.Popup(new GUIContent("Bridge Model"), idx, options);
            bridgeModelName.stringValue = options[Mathf.Clamp(newIdx, 0, options.Length - 1)];
        }

        private void DrawEstimatedSetupTimeHint()
        {
            string runtimeRoot = KimodoBridgeController.GetRuntimeRootPath();
            bool highVram = clip != null && clip.bridgeVramMode == KimodoBridgeVramMode.High;
            string modelName = clip == null || string.IsNullOrWhiteSpace(clip.bridgeModelName) ? "Kimodo-SOMA-RP-v1" : clip.bridgeModelName.Trim();
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
            Repaint();
        }

        private void OnManagerCommandCompleted(KimodoEditorCommandCompletedEvent evt)
        {
            if (evt.Command.Kind == KimodoEditorCommandKind.BridgeStopServer)
            {
                PullBridgeStatusSnapshot(forceRefresh: true);
                Repaint();
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
                var latestMarkers = KimodoEditorGeneratePipelineOrchestrator.GetLatestConstraintMarkers();
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

            Repaint();
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

            Repaint();
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
            bool newFoldout = EditorGUILayout.Foldout(showAdvancedFoldout, "Advanced", true);
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
            EditorGUILayout.LabelField("Curve Filter Options", EditorStyles.boldLabel);

            SerializedProperty enabledProp = curveFilterOptionsProp.FindPropertyRelative("enabled");
            SerializedProperty positionErrorProp = curveFilterOptionsProp.FindPropertyRelative("positionError");
            SerializedProperty rotationErrorProp = curveFilterOptionsProp.FindPropertyRelative("rotationError");
            SerializedProperty floatErrorProp = curveFilterOptionsProp.FindPropertyRelative("floatError");
            SerializedProperty ensureQuatProp = curveFilterOptionsProp.FindPropertyRelative("ensureQuaternionContinuity");

            if (enabledProp != null)
            {
                EditorGUILayout.PropertyField(enabledProp, new GUIContent("Reduce Keyframes"));
            }

            bool curveFilterEnabled = enabledProp == null || enabledProp.boolValue;
            if (curveFilterEnabled)
            {
                if (positionErrorProp != null)
                {
                    positionErrorProp.floatValue = EditorGUILayout.Slider(
                        new GUIContent("Position Error"),
                        positionErrorProp.floatValue,
                        0f,
                        1f);
                }

                if (rotationErrorProp != null)
                {
                    rotationErrorProp.floatValue = EditorGUILayout.Slider(
                        new GUIContent("Rotation Error"),
                        rotationErrorProp.floatValue,
                        0f,
                        1f);
                }

                if (floatErrorProp != null)
                {
                    floatErrorProp.floatValue = EditorGUILayout.Slider(
                        new GUIContent("Float Error"),
                        floatErrorProp.floatValue,
                        0f,
                        1f);
                }
            }

            if (ensureQuatProp != null)
            {
                EditorGUILayout.PropertyField(ensureQuatProp, new GUIContent("Ensure Quaternion Continuity"));
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
            EditorGUILayout.LabelField($"Frames: {clip.frameCount}, Joints: {clip.jointCount}, FPS: {clip.fps}", EditorStyles.miniLabel);
            if (!string.IsNullOrWhiteSpace(lastConstraintsPath))
            {
                EditorGUILayout.LabelField($"Constraints: {lastConstraintsPath}", EditorStyles.miniLabel);
            }

            if (GUILayout.Button("Reset", GUILayout.Width(100)))
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

            float newDuration = frames / TargetFps;
            UndoExtensions.RegisterClip(timelineClip, L10n.Tr("Modify Clip Duration"));
            timelineClip.duration = newDuration;
        }

    }
}


