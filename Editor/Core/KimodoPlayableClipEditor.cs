using System;
using System.Collections.Generic;
using KimodoUnityBridge.Command;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    [CustomEditor(typeof(KimodoPlayableClip))]
    [CanEditMultipleObjects]
    public partial class KimodoPlayableClipEditor : UnityEditor.Editor
    {
        private const double RepaintIntervalSeconds = 0.2d;
        private SerializedProperty bridgeModelName;
        private SerializedProperty textEncoderMode;
        private SerializedProperty motionPrompt;
        private SerializedProperty generationFrames;
        private SerializedProperty diffusionSteps;
        private SerializedProperty randomProp;
        private SerializedProperty seed;
        private SerializedProperty inOutConstraintModeProp;
        private SerializedProperty enableInConstraint;
        private SerializedProperty enableOutConstraint;
        private SerializedProperty enableClipConstraint;
        private SerializedProperty clipConstraintAvatarMask;
        private SerializedProperty ardyAutoHistory;
        private SerializedProperty ardyHistoryWeight;
        private SerializedProperty ardyTargetMaxSpeed;
        private SerializedProperty ardyTargetMaxAcceleration;
        private SerializedProperty showConstraint;
        private SerializedProperty splinePathEnabled;
        private SerializedProperty splineWaypointCount;
        private SerializedProperty splineDensePath;
        private SerializedProperty splineIncludeHeading;
        private SerializedProperty autoBeginAnchor;

        private SerializedProperty animationClipProp;
        private SerializedProperty footIKProp;
        private SerializedProperty loopProp;
        private SerializedProperty clipTransformOffsetPositionProp;
        private SerializedProperty clipTransformOffsetRotationProp;
        private SerializedProperty useTrackMatchFieldsProp;
        private SerializedProperty matchTargetFieldsProp;
        private SerializedProperty removeStartOffsetProp;
        private SerializedProperty autoRetargetOnBindingProp;
        private SerializedProperty customRetargetAvatarProp;
        private SerializedProperty curveFilterOptionsProp;

        private KimodoPlayableClip clip;
        private bool isGenerating;
        private string lastStatus;
        private string lastError;
        private string lastConstraintsPath = string.Empty;
        private bool bridgeConnectedCached;
        private bool showAdvancedFoldout = true;
        private double lastRepaintTime;
        private bool repaintQueued;

        private void OnEnable()
        {
            InitializeSerializedBindings();
            ApplyProjectPromptDefault();
            showAdvancedFoldout = KimodoPlayableClipGenerationSettings.instance.AdvancedCurveFilterFoldout;
            PullBridgeStatusSnapshot();
            SyncRequestHandleState();
        }

        private void ApplyProjectPromptDefault()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            string defaultPrompt = settings.DefaultPrompt;
            foreach (UnityEngine.Object selectedTarget in targets)
            {
                if (selectedTarget is not KimodoPlayableClip playableClip)
                {
                    continue;
                }

                string currentPrompt = playableClip.motionPrompt?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(currentPrompt) &&
                    !string.Equals(
                        currentPrompt,
                        KimodoPlayableClipGenerationSettings.DefaultPromptFallback,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.Equals(playableClip.motionPrompt, defaultPrompt, StringComparison.Ordinal))
                {
                    playableClip.motionPrompt = defaultPrompt;
                    EditorUtility.SetDirty(playableClip);
                }
            }

            serializedObject.UpdateIfRequiredOrScript();
        }

        private void InitializeSerializedBindings()
        {
            clip = (KimodoPlayableClip)target;
            bridgeModelName = serializedObject.FindProperty("bridgeModelName");
            textEncoderMode = serializedObject.FindProperty("textEncoderMode");
            motionPrompt = serializedObject.FindProperty("motionPrompt");
            generationFrames = serializedObject.FindProperty("generationFrames");
            diffusionSteps = serializedObject.FindProperty("diffusionSteps");
            randomProp = serializedObject.FindProperty("randomSeed");
            seed = serializedObject.FindProperty("seed");
            inOutConstraintModeProp = serializedObject.FindProperty("inOutConstraintMode");
            enableInConstraint = serializedObject.FindProperty("enableInConstraint");
            enableOutConstraint = serializedObject.FindProperty("enableOutConstraint");
            enableClipConstraint = serializedObject.FindProperty("enableClipConstraint");
            clipConstraintAvatarMask = serializedObject.FindProperty("clipConstraintAvatarMask");
            ardyAutoHistory = serializedObject.FindProperty("ardyAutoHistory");
            ardyHistoryWeight = serializedObject.FindProperty("ardyHistoryWeight");
            ardyTargetMaxSpeed = serializedObject.FindProperty("ardyTargetMaxSpeed");
            ardyTargetMaxAcceleration = serializedObject.FindProperty("ardyTargetMaxAcceleration");
            showConstraint = serializedObject.FindProperty("showConstraint");
            splinePathEnabled = serializedObject.FindProperty("splinePathEnabled");
            splineWaypointCount = serializedObject.FindProperty("splineWaypointCount");
            splineDensePath = serializedObject.FindProperty("splineDensePath");
            splineIncludeHeading = serializedObject.FindProperty("splineIncludeHeading");
            autoBeginAnchor = serializedObject.FindProperty("autoBeginAnchor");

            animationClipProp = serializedObject.FindProperty("m_Clip");
            footIKProp = serializedObject.FindProperty("m_ApplyFootIK");
            loopProp = serializedObject.FindProperty("m_Loop");
            clipTransformOffsetPositionProp = serializedObject.FindProperty("m_Position");
            clipTransformOffsetRotationProp = serializedObject.FindProperty("m_EulerAngles");
            useTrackMatchFieldsProp = serializedObject.FindProperty("m_UseTrackMatchFields");
            matchTargetFieldsProp = serializedObject.FindProperty("m_MatchTargetFields");
            removeStartOffsetProp = serializedObject.FindProperty("m_RemoveStartOffset");
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
            motionPrompt.stringValue = prompt ?? string.Empty;
            generationFrames.intValue = Mathf.Clamp(generationFramesValue, KimodoPlayableClip.MIN_FRAMES, KimodoPlayableClip.MAX_FRAMES);
            diffusionSteps.intValue = Mathf.Clamp(diffusionStepsValue, 0, 1000);
            randomProp.boolValue = randomSeedEnabled;
            seed.intValue = seedValue;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private void OnDisable()
        {
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

            PullBridgeStatusSnapshot();
            SyncRequestHandleState();
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
            bool isArdy = KimodoGenerationInspectorGui.IsArdy(bridgeModelName?.stringValue);
            if (bridgeModelName != null)
            {
                isArdy = KimodoGenerationInspectorGui.DrawModelSelector(bridgeModelName, diffusionSteps, textEncoderMode);
            }
            if (textEncoderMode != null)
            {
                KimodoGenerationInspectorGui.DrawTextEncoderMode(textEncoderMode, isArdy);
                KimodoGenerationInspectorGui.DrawResolvedTextEncoderStatus();
            }
            KimodoGenerationInspectorGui.DrawPrompt(motionPrompt);

            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            bool hasTimelineDuration = timelineClip != null && timelineClip.duration > 0.0;
            if (hasTimelineDuration)
            {
                EditorGUILayout.LabelField("Timeline Duration", $"{timelineClip.duration:F2}s");
            }
            else
            {
                EditorGUILayout.HelpBox("Generation length is read from its Timeline clip.", MessageType.Error);
            }

            KimodoGenerationInspectorGui.DrawDiffusionSteps(diffusionSteps, bridgeModelName);
            if (isArdy && ardyAutoHistory != null)
            {
                EditorGUILayout.PropertyField(
                    ardyAutoHistory,
                    new GUIContent("Auto History", "Adapt the history window from upcoming motion constraints."));
                if (!ardyAutoHistory.hasMultipleDifferentValues &&
                    !ardyAutoHistory.boolValue &&
                    ardyHistoryWeight != null)
                {
                    EditorGUILayout.PropertyField(
                        ardyHistoryWeight,
                        new GUIContent("ARDY History Weight", "0 uses one motion token; 1 uses the maximum history window."));
                }
            }
            KimodoGenerationInspectorGui.DrawSeed(randomProp, seed);
            int previousInOutMode = inOutConstraintModeProp?.enumValueIndex ?? 0;
            bool previousInEnabled = enableInConstraint?.boolValue ?? false;
            bool previousOutEnabled = enableOutConstraint?.boolValue ?? false;
            if (inOutConstraintModeProp != null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PropertyField(
                        inOutConstraintModeProp,
                        new GUIContent("InOut Constraint", "None disables boundary constraints. Inside uses this clip's own start/end poses. Outside uses neighboring clip boundary poses."));
                    if ((KimodoInOutConstraintMode)inOutConstraintModeProp.enumValueIndex != KimodoInOutConstraintMode.None)
                    {
                        float previousLabelWidth = EditorGUIUtility.labelWidth;
                        EditorGUIUtility.labelWidth = 28f;
                        EditorGUILayout.PropertyField(enableInConstraint, new GUIContent("In"), GUILayout.Width(60f));
                        EditorGUIUtility.labelWidth = 36f;
                        EditorGUILayout.PropertyField(enableOutConstraint, new GUIContent("Out"), GUILayout.Width(60f));
                        EditorGUIUtility.labelWidth = previousLabelWidth;
                    }
                }
            }
            if (enableClipConstraint != null)
            {
                EditorGUILayout.Space(2f);
                EditorGUILayout.PropertyField(
                    enableClipConstraint,
                    new GUIContent("Clip Constraint", "Use this clip's current animation as a per-frame masked pose condition while regenerating it."));
                if (enableClipConstraint.boolValue && clipConstraintAvatarMask != null)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(
                        clipConstraintAvatarMask,
                        new GUIContent("Avatar Mask", "Selected bones retain position and rotation. Hips also retains root position and orientation."));
                    EditorGUI.indentLevel--;
                }
            }
            if (showConstraint != null)
            {
                bool wasShown = showConstraint.boolValue;
                bool refreshClicked = false;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PropertyField(
                        showConstraint,
                        new GUIContent("Show Constraint", "Show constraint previews for this clip when selected."));
                    if (showConstraint.boolValue && !showConstraint.hasMultipleDifferentValues)
                    {
                        refreshClicked = GUILayout.Button(
                            new GUIContent("Refresh", "Clear cached poses and force constraint re-sampling."),
                            EditorStyles.miniButton,
                            GUILayout.Width(54f));
                    }
                }

                bool wasReEnabled =
                    (!wasShown && showConstraint.boolValue) ||
                    (showConstraint.boolValue &&
                     ((!previousInEnabled && enableInConstraint.boolValue) ||
                      (!previousOutEnabled && enableOutConstraint.boolValue) ||
                      previousInOutMode != inOutConstraintModeProp.enumValueIndex));
                if (refreshClicked || wasReEnabled)
                {
                    KimodoConstraintSelectionPreviewTool.ForceRefresh();
                }
            }
            KimodoConstraintSelectionPreviewTool.ScheduleRefresh();

            DrawSplinePathSection(timelineClip);

            DrawConstraintReferenceList();

            bool disableGenerate =
                isGenerating ||
                !hasTimelineDuration ||
                KimodoBridgeServerTool.IsRuntimeMaintenanceInProgress ||
                EditorCompilationStateGate.IsCompilingOrReloading;
            GUI.enabled = !disableGenerate;
            int selectedGenerateClipCount = KimodoPlayableClipGenerationExecutionService.GetSelectedPlayableClipCount(clip);
            string generateLabel = selectedGenerateClipCount > 1
                ? $"Generate {selectedGenerateClipCount} Clips & Bake"
                : "Generate & Bake";
            string generateTooltip = selectedGenerateClipCount > 1
                ? "Generate the selected Timeline clips one at a time in Timeline order."
                : "Generate only this Timeline clip.";
            if (GUILayout.Button(new GUIContent(generateLabel, generateTooltip), GUILayout.Height(32)))
            {
                serializedObject.ApplyModifiedProperties();
                bool accepted = KimodoPlayableClipGenerationExecutionService.TryStartGenerate(
                    clip,
                    out _,
                    out string error);
                if (accepted)
                {
                    isGenerating = true;
                    lastError = string.Empty;
                    lastStatus = string.IsNullOrWhiteSpace(error) ? "Queued generation..." : error;
                }
                else
                {
                    lastError = error;
                }
            }
            if (isArdy &&
                KimodoPlayableClipGenerationExecutionService.TryGetSelectedArdyClipCount(
                    clip,
                    out int connectedClipCount))
            {
                bool hasConnectedPlan = KimodoPlayableClipGenerationExecutionService.TryGetConnectedArdyClipCount(
                    clip,
                    out _,
                    out string connectedReason);
                GUI.enabled = !disableGenerate && hasConnectedPlan;
                string connectedLabel = $"Generate {connectedClipCount} Connected Clips & Bake";
                if (GUILayout.Button(
                        new GUIContent(
                            connectedLabel,
                            hasConnectedPlan
                                ? "Generate all compatible head-to-tail ARDY clips in one server request, then slice and bake them in Unity."
                                : connectedReason),
                        GUILayout.Height(28)))
                {
                    serializedObject.ApplyModifiedProperties();
                    bool accepted = KimodoPlayableClipGenerationExecutionService.TryStartGenerateConnectedArdy(
                        clip,
                        out _,
                        out string error);
                    if (accepted)
                    {
                        isGenerating = true;
                        lastError = string.Empty;
                        lastStatus = "Queued connected ARDY generation...";
                    }
                    else
                    {
                        lastError = error;
                    }
                }
            }
            GUI.enabled = isGenerating;
            if (GUILayout.Button(new GUIContent("Cancel", "Cancel the current generation command for this clip."), GUILayout.Height(24)))
            {
                command_generation_runner.Cancel(clip);
            }
            GUI.enabled = true;

            DrawEstimatedSetupTimeHint();

            EditorGUILayout.LabelField(
                "Bridge status: " + (bridgeConnectedCached ? "connected" : "disconnected"),
                EditorStyles.miniLabel);

            if (!string.IsNullOrWhiteSpace(lastStatus))
            {
                EditorGUILayout.LabelField(lastStatus, EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space();
        }

        private void DrawSplinePathSection(TimelineClip timelineClip)
        {
            if (splinePathEnabled == null ||
                !KimodoPlayableClipGenerationSettings.instance.EnableSplineExperimental)
            {
                return;
            }

            if (targets.Length != 1)
            {
                EditorGUILayout.HelpBox("Spline Path can only be edited for one Kimodo Playable clip at a time.", MessageType.Info);
                return;
            }

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(
                splinePathEnabled,
                new GUIContent("Spline Path", "Store an editable spline on this clip and export its Root2D waypoints when generating."));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
                if (KimodoSplinePathEditorBridge.TrySetEnabled(
                        clip,
                        splinePathEnabled.boolValue,
                        out string pathError))
                {
                    lastError = string.Empty;
                    lastStatus = splinePathEnabled.boolValue
                        ? "Spline Path enabled. Select Edit Spline to change its knots."
                        : "Spline Path hidden.";
                }
                else
                {
                    lastError = pathError;
                }
            }

            if (!splinePathEnabled.boolValue)
            {
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(
                splineWaypointCount,
                new GUIContent("Root2D Samples", "Number of evenly timed Root2D samples exported from the spline."));
            EditorGUILayout.PropertyField(
                splineDensePath,
                new GUIContent("Dense Path", "Ask Kimodo to expand the Root2D samples into a dense path."));
            EditorGUILayout.PropertyField(
                splineIncludeHeading,
                new GUIContent("Include Heading", "Export the planar spline tangent as Root2D heading."));
            EditorGUI.indentLevel--;

            if (!KimodoSplinePathEditorBridge.IsAvailable)
            {
                EditorGUILayout.HelpBox(
                    "Install com.unity.splines to use this experimental editor integration. The main Kimodo package does not install it automatically.",
                    MessageType.Warning);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Edit Spline", "Create a temporary hidden SplineContainer and enter Unity's spline editing mode.")))
                {
                    if (KimodoSplinePathEditorBridge.TryBeginEditing(clip, timelineClip, out string editError))
                    {
                        lastError = string.Empty;
                        lastStatus = "Editing Spline Path.";
                    }
                    else
                    {
                        lastError = editError;
                    }
                }

                if (GUILayout.Button(new GUIContent("Reset Spline", "Rebuild from the current animation root motion, or from duration at 1 m/s when no animation is assigned.")))
                {
                    if (KimodoSplinePathEditorBridge.TryResetPath(clip, out string resetError))
                    {
                        lastError = string.Empty;
                        lastStatus = "Spline Path reset from the current clip.";
                    }
                    else
                    {
                        lastError = resetError;
                    }
                }

                EditorGUILayout.LabelField(
                    $"{splineWaypointCount.intValue} Root2D samples",
                    EditorStyles.miniLabel,
                    GUILayout.Width(132f));
            }
            EditorGUILayout.HelpBox(
                "Spline data is stored on this PlayableAsset. Unity's temporary editor proxy is created only while the clip is selected or edited. Only XZ is exported to Root2D.",
                MessageType.None);
        }

        private void DrawConstraintReferenceList()
        {
            EditorGUILayout.LabelField("Constraint References", EditorStyles.miniBoldLabel);
            List<KimodoConstraintMarkerBase> references = CollectConstraintReferences();
            if (references.Count == 0)
            {
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            }
            else
            {
                for (int i = 0; i < references.Count; i++)
                {
                    KimodoConstraintMarkerBase marker = references[i];
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

        private List<KimodoConstraintMarkerBase> CollectConstraintReferences()
        {
            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            TrackAsset track = timelineClip != null ? timelineClip.GetParentTrack() : null;
            return track == null
                ? new List<KimodoConstraintMarkerBase>()
                : KimodoTimelineConstraintMarkerSampler.CollectMarkersForClip(track, timelineClip);
        }

        private void PullBridgeStatusSnapshot()
        {
            if (clip == null)
            {
                return;
            }

            bridgeConnectedCached = KimodoBridgeService.Shared.IsConnected;
        }

        private static string SummarizeForUi(string message, int maxLength = 320)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            string normalized = string.Join(" ", message.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (normalized.Length <= maxLength)
            {
                return normalized;
            }

            return normalized.Substring(0, maxLength) + "...";
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

            KimodoTimelinePreviewRefreshUtility.DrawAnimationPlayableAssetClipOffsetSettings(
                clipTransformOffsetPositionProp,
                clipTransformOffsetRotationProp,
                useTrackMatchFieldsProp,
                matchTargetFieldsProp,
                removeStartOffsetProp);

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

        private void DrawEstimatedSetupTimeHint()
        {
            string runtimeRoot = KimodoBridgeServerTool.GetRuntimeRootPath();
            KimodoTextEncoderMode encoderMode = clip != null
                ? clip.textEncoderMode
                : KimodoTextEncoderMode.HighPerformance;
            string modelName = clip == null ? KimodoPlayableClip.DefaultBridgeModelName : KimodoPlayableClip.NormalizeBridgeModelName(clip.bridgeModelName);
            string modelsRootOverride = KimodoPlayableClipGenerationSettings.instance.LocalModelsPath?.Trim();
            if (!KimodoBridgeServerTool.TryGetModelMissingSetupMinutes(runtimeRoot, encoderMode, modelName, modelsRootOverride, out int minutes))
            {
                return;
            }
            EditorGUILayout.HelpBox($"Model missing detected, update required, approximately {minutes} minutes.", MessageType.None);
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

        private void SyncRequestHandleState()
        {
            if (clip == null || !command_generation_runner.TryGet(clip, out command_generation_session handle) || handle == null)
            {
                isGenerating = false;
                return;
            }

            isGenerating = handle.IsRunning;
            switch (handle.Status)
            {
                case command_status.Running:
                    lastStatus = string.IsNullOrWhiteSpace(handle.Message) ? "Generating and baking..." : handle.Message;
                    lastError = string.Empty;
                    break;
                case command_status.Completed:
                    lastStatus = string.IsNullOrWhiteSpace(handle.Message) ? "Generation complete." : handle.Message;
                    lastError = string.Empty;
                    if (handle.Payload is command_generate_result generateResult &&
                        !string.IsNullOrWhiteSpace(generateResult.ConstraintsPath))
                    {
                        lastConstraintsPath = generateResult.ConstraintsPath;
                    }

                    break;
                case command_status.Failed:
                    lastStatus = "Generation failed.";
                    lastError = handle.Error;
                    break;
                case command_status.Canceled:
                    lastStatus = string.IsNullOrWhiteSpace(handle.Message) ? "Generation canceled." : handle.Message;
                    lastError = string.Empty;
                    break;
            }
        }

        private void DrawAdvancedCurveFilterSection()
        {
            if (curveFilterOptionsProp == null)
            {
                return;
            }

            EditorGUILayout.Space(4f);
            bool newFoldout = EditorGUILayout.Foldout(showAdvancedFoldout, new GUIContent("Advanced", "Auto begin anchoring, motion compensation, and curve filtering options for generated animation curves."), true);
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

            if (autoBeginAnchor != null)
            {
                EditorGUILayout.PropertyField(
                    autoBeginAnchor,
                    new GUIContent("Auto Begin Anchor", "When the first second has no effective constraint anchor, add a frame-0 Root2D constraint at the Timeline start pose."));
            }

            if (KimodoGenerationInspectorGui.IsArdy(bridgeModelName?.stringValue) &&
                ardyAutoHistory != null &&
                !ardyAutoHistory.hasMultipleDifferentValues &&
                ardyAutoHistory.boolValue &&
                ardyTargetMaxSpeed != null &&
                ardyTargetMaxAcceleration != null)
            {
                EditorGUILayout.Space(4f);
                EditorGUILayout.LabelField("ARDY Motion Limits", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(
                    ardyTargetMaxSpeed,
                    new GUIContent("Max Speed", "Maximum root speed used by ARDY Auto History for a future Full-Body target."));
                EditorGUILayout.PropertyField(
                    ardyTargetMaxAcceleration,
                    new GUIContent("Max Acceleration", "Maximum root acceleration used by ARDY Auto History for a future Full-Body target."));
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
                $"Duration: {KimodoInOutConstraintTools.FrameCountToDurationSeconds(clip.frameCount):F2}s, Frames: {clip.frameCount}, Joints: {clip.jointCount}",
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
                command_generation_runner.Clear(clip);
                lastStatus = string.Empty;
                lastError = string.Empty;
            }

            EditorGUILayout.EndVertical();
        }

    }
}


