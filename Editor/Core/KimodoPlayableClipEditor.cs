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
    [CanEditMultipleObjects]
    public partial class KimodoPlayableClipEditor : UnityEditor.Editor
    {
        private SerializedProperty bridgeModelName;
        private SerializedProperty textEncoderMode;
        private SerializedProperty motionPrompt;
        private SerializedProperty generationFrames;
        private SerializedProperty diffusionSteps;
        private SerializedProperty randomProp;
        private SerializedProperty seed;
        private SerializedProperty generateLoopProp;
        private SerializedProperty loopLockPositionXProp;
        private SerializedProperty loopLockPositionYProp;
        private SerializedProperty loopLockPositionZProp;
        private SerializedProperty loopLockRotationXProp;
        private SerializedProperty loopLockRotationYProp;
        private SerializedProperty loopLockRotationZProp;
        private SerializedProperty loopLockTargetFieldsEditorProxyProp;
        private SerializedProperty loopWindowFramesProp;
        private SerializedProperty loopWindowSampleCountProp;
        private SerializedProperty overridePathAngleProp;
        private SerializedProperty pathBeginAngleDegreesProp;
        private SerializedProperty pathEndAngleDegreesProp;
        private SerializedProperty overridePathDistanceProp;
        private SerializedProperty pathDistanceProp;
        private SerializedProperty overrideHeadingProp;
        private SerializedProperty headingDegreesProp;
        private SerializedProperty useRelativeOffsetProp;
        private SerializedProperty relativeOffsetProp;
        private SerializedProperty commandGenerationStatusProp;
        private SerializedProperty commandGenerationErrorProp;
        private SerializedProperty inOutConstraintModeProp;
        private SerializedProperty enableInConstraint;
        private SerializedProperty enableOutConstraint;
        private SerializedProperty postApplyOutConstraint;
        private SerializedProperty postApplyInConstraint;
        private SerializedProperty inConstraintWindowFrames;
        private SerializedProperty inConstraintSampleCount;
        private SerializedProperty outConstraintWindowFrames;
        private SerializedProperty outConstraintSampleCount;
        private SerializedProperty longClipWindowFramesProp;
        private SerializedProperty longClipWindowSampleCountProp;
        private SerializedProperty ardyAutoHistory;
        private SerializedProperty ardyHistoryWeight;
        private SerializedProperty showConstraint;
        private SerializedProperty analyzeAfterGeneration;
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
        private bool showTrajectoryContinuityFoldout;
        private bool showAdvancedFoldout = true;
        private GUIStyle trajectoryContinuityFoldoutStyle;
        private string lastFullAnalysisJson = string.Empty;
        private string lastFullAnalysisError = string.Empty;
        private Vector2 fullAnalysisScroll;

        private void OnEnable()
        {
            InitializeSerializedBindings();
            ApplyProjectPromptDefault();
            showAdvancedFoldout = KimodoPlayableClipGenerationSettings.instance.AdvancedCurveFilterFoldout;
            PullBridgeStatusSnapshot();
            SyncRequestHandleState();
            KimodoConstraintSelectionPreviewTool.SchedulePreviewUpdate();
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
            generateLoopProp = serializedObject.FindProperty("generateLoop");
            loopLockPositionXProp = serializedObject.FindProperty("loopLockPositionX");
            loopLockPositionYProp = serializedObject.FindProperty("loopLockPositionY");
            loopLockPositionZProp = serializedObject.FindProperty("loopLockPositionZ");
            loopLockRotationXProp = serializedObject.FindProperty("loopLockRotationX");
            loopLockRotationYProp = serializedObject.FindProperty("loopLockRotationY");
            loopLockRotationZProp = serializedObject.FindProperty("loopLockRotationZ");
            loopLockTargetFieldsEditorProxyProp = serializedObject.FindProperty("loopLockTargetFieldsEditorProxy");
            loopWindowFramesProp = serializedObject.FindProperty("loopWindowFrames60");
            loopWindowSampleCountProp = serializedObject.FindProperty("loopWindowSampleCount");
            overridePathAngleProp = serializedObject.FindProperty("overridePathAngle");
            pathBeginAngleDegreesProp = serializedObject.FindProperty("pathBeginAngleDegrees");
            pathEndAngleDegreesProp = serializedObject.FindProperty("pathEndAngleDegrees");
            overridePathDistanceProp = serializedObject.FindProperty("overridePathDistance");
            pathDistanceProp = serializedObject.FindProperty("pathDistance");
            overrideHeadingProp = serializedObject.FindProperty("overrideHeading");
            headingDegreesProp = serializedObject.FindProperty("headingDegrees");
            useRelativeOffsetProp = serializedObject.FindProperty("useRelativeOffset");
            relativeOffsetProp = serializedObject.FindProperty("relativeOffset");
            commandGenerationStatusProp = serializedObject.FindProperty("commandGenerationStatus");
            commandGenerationErrorProp = serializedObject.FindProperty("commandGenerationError");
            inOutConstraintModeProp = serializedObject.FindProperty("inOutConstraintMode");
            enableInConstraint = serializedObject.FindProperty("enableInConstraint");
            enableOutConstraint = serializedObject.FindProperty("enableOutConstraint");
            postApplyOutConstraint = serializedObject.FindProperty("postApplyOutConstraint");
            postApplyInConstraint = serializedObject.FindProperty("postApplyInConstraint");
            inConstraintWindowFrames = serializedObject.FindProperty("inConstraintWindowFrames60");
            inConstraintSampleCount = serializedObject.FindProperty("inConstraintSampleCount");
            outConstraintWindowFrames = serializedObject.FindProperty("outConstraintWindowFrames60");
            outConstraintSampleCount = serializedObject.FindProperty("outConstraintSampleCount");
            longClipWindowFramesProp = serializedObject.FindProperty("longClipWindowFrames60");
            longClipWindowSampleCountProp = serializedObject.FindProperty("longClipWindowSampleCount");
            ardyAutoHistory = serializedObject.FindProperty("ardyAutoHistory");
            ardyHistoryWeight = serializedObject.FindProperty("ardyHistoryWeight");
            showConstraint = serializedObject.FindProperty("showConstraint");
            analyzeAfterGeneration = serializedObject.FindProperty("analyzeAfterGeneration");
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
            generationFrames.intValue = Mathf.Clamp(generationFramesValue, KimodoMotionModelProfiles.MinGenerationFrames, KimodoMotionModelProfiles.MaxGenerationFrames);
            diffusionSteps.intValue = Mathf.Clamp(diffusionStepsValue, 0, 1000);
            randomProp.boolValue = randomSeedEnabled;
            seed.intValue = seedValue;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        private void OnDisable()
        {
            EditorUtility.ClearProgressBar();
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
            DrawCommandGenerationStatus();
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

        private void DrawCommandGenerationStatus()
        {
            string status = commandGenerationStatusProp?.stringValue ?? string.Empty;
            if (string.IsNullOrWhiteSpace(status) || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return;

            string error = commandGenerationErrorProp?.stringValue ?? string.Empty;
            MessageType messageType = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                ? MessageType.Error
                : string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase)
                    ? MessageType.Warning
                    : MessageType.Info;
            string message = $"Command generation {status}.";
            if (!string.IsNullOrWhiteSpace(error)) message += $"\n{error}";
            EditorGUILayout.HelpBox(message, messageType);
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
                KimodoGenerationInspectorGui.DrawDurationWarning((float)timelineClip.duration);
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
                    new GUIContent("Auto History", "0-1 m/s = 0.225; 1-10 m/s grows exponentially to 1; above 10 m/s = 1."));
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
            EditorGUI.BeginChangeCheck();
            DrawTrajectoryAndContinuitySection(timelineClip, hasTimelineDuration);
            if (EditorGUI.EndChangeCheck()) KimodoConstraintSelectionPreviewTool.SchedulePreviewUpdate();
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
                            new GUIContent("Refresh", "Force constraint re-sampling."),
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
                    KimodoConstraintSelectionPreviewTool.SchedulePreviewUpdate();
                }
            }
            DrawSplinePathSection(timelineClip);

            DrawConstraintReferenceList();

            bool disableGenerate =
                isGenerating ||
                !hasTimelineDuration ||
                KimodoBridgeServerTool.IsRuntimeMaintenanceInProgress ||
                EditorCompilationStateGate.IsCompilingOrReloading;
            GUI.enabled = !disableGenerate;
            int selectedGenerateClipCount = KimodoPlayableClipGenerationExecutionService.GetSelectedPlayableClipCount(clip);
            bool generateLoop = generateLoopProp != null && generateLoopProp.boolValue;
            string generateLabel = selectedGenerateClipCount > 1
                ? $"Generate {selectedGenerateClipCount} {(generateLoop ? "Loop " : string.Empty)}Clips & Bake"
                : generateLoop ? "Generate Loop & Bake" : "Generate & Bake";
            string generateTooltip = selectedGenerateClipCount > 1
                ? "Generate the selected Timeline clips one at a time in Timeline order."
                : generateLoop
                    ? "Generate a baseline motion, then regenerate with an automatic terminal FullBody constraint and keep the middle section."
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
                    KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsModified);
                }
                else
                {
                    lastError = error;
                }
            }
            if (KimodoPlayableClipGenerationExecutionService.TryGetSelectedCompatibleClipCount(
                    clip,
                    out int connectedClipCount))
            {
                bool hasConnectedPlan = KimodoPlayableClipGenerationExecutionService.TryGetConnectedClipCount(
                    clip,
                    out _,
                    out string connectedReason);
                GUI.enabled = !disableGenerate && hasConnectedPlan;
                string connectedLabel = $"Generate {connectedClipCount} Connected Clips & Bake";
                if (GUILayout.Button(
                        new GUIContent(
                            connectedLabel,
                            hasConnectedPlan
                                ? "Generate all compatible clips in one server request, then slice and bake them in Unity."
                                : connectedReason),
                        GUILayout.Height(28)))
                {
                    serializedObject.ApplyModifiedProperties();
                    bool accepted = KimodoPlayableClipGenerationExecutionService.TryStartGenerateConnected(
                        clip,
                        out _,
                        out string error);
                    if (accepted)
                    {
                        isGenerating = true;
                        lastError = string.Empty;
                        lastStatus = "Queued connected Timeline generation...";
                        KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(RefreshReason.ContentsModified);
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
                KimodoEditorGenerationJobService.Cancel(clip);
            }
            GUI.enabled = true;


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

        public override bool RequiresConstantRepaint()
        {
            return isGenerating;
        }

        private void DrawTrajectoryAndContinuitySection(TimelineClip timelineClip, bool hasTimelineDuration)
        {
            trajectoryContinuityFoldoutStyle ??= new GUIStyle(EditorStyles.foldout)
            {
                fontStyle = FontStyle.Bold
            };
            EditorGUILayout.Space(2f);
            showTrajectoryContinuityFoldout = EditorGUILayout.Foldout(
                showTrajectoryContinuityFoldout,
                new GUIContent(
                    "Trajectory & Continuity Setting",
                    "InOut constraints, loop generation, PathAngle shaping, and heading overrides."),
                true,
                trajectoryContinuityFoldoutStyle);
            if (!showTrajectoryContinuityFoldout)
            {
                return;
            }

            EditorGUI.indentLevel++;
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

                if ((KimodoInOutConstraintMode)inOutConstraintModeProp.enumValueIndex != KimodoInOutConstraintMode.None)
                {
                    if (enableInConstraint.boolValue && postApplyInConstraint != null)
                    {
                        EditorGUILayout.PropertyField(
                            postApplyInConstraint,
                            new GUIContent(
                                "Post-Apply In",
                                "Defer In until after the first trajectory pass, align it in SOMA Root2D space, then apply it in the second pass."));
                    }
                    if (enableOutConstraint.boolValue && postApplyOutConstraint != null)
                    {
                        EditorGUILayout.PropertyField(
                            postApplyOutConstraint,
                            new GUIContent(
                                "Post-Apply Out",
                                "Defer Out until after the first trajectory pass, align it in SOMA Root2D space, then apply it in the second pass."));
                    }
                }
            }
            if ((KimodoInOutConstraintMode)inOutConstraintModeProp.enumValueIndex != KimodoInOutConstraintMode.None &&
                !KimodoMotionModelProfiles.TryGetArdy(bridgeModelName.stringValue, out _))
            {
                if (enableInConstraint.boolValue)
                    DrawBoundarySampling("In", inConstraintWindowFrames, inConstraintSampleCount);
                if (enableOutConstraint.boolValue)
                    DrawBoundarySampling("Out", outConstraintWindowFrames, outConstraintSampleCount);
                EditorGUILayout.HelpBox(
                    "Window frames use 60 FPS command time and are converted to model frames. " +
                    "Outside windows extend generation with neighboring motion, then are trimmed from the output. " +
                    "Short source clips limit the available window. Show Constraint displays the sampled poses.", MessageType.Info);
            }
            if (generateLoopProp != null)
            {
                EditorGUILayout.PropertyField(
                    generateLoopProp,
                    new GUIContent("Generate Loop", "Generate a normal baseline, constrain its first pose at the end, then generate an extended motion and keep its middle section."));
                if (!generateLoopProp.hasMultipleDifferentValues &&
                    generateLoopProp.boolValue &&
                    hasTimelineDuration &&
                    timelineClip.duration * 2.0 > 10.0)
                {
                    EditorGUILayout.HelpBox(
                        "Loop generation exceeds the 600-frame limit and will fall back to normal generation.",
                        MessageType.Warning);
                }
                if (!generateLoopProp.hasMultipleDifferentValues && generateLoopProp.boolValue)
                {
                    EditorGUI.indentLevel++;
                    if (loopWindowFramesProp != null)
                    {
                        EditorGUILayout.PropertyField(
                            loopWindowFramesProp,
                            new GUIContent("Loop Window Frames (60 FPS)", "Number of command frames at 60 FPS retained from the loop anchor window for Stage 3; converted to model frames before sampling."));
                    }
                    if (loopWindowSampleCountProp != null)
                    {
                        EditorGUILayout.PropertyField(
                            loopWindowSampleCountProp,
                            new GUIContent("Loop Window Samples", "Evenly spaced FullBody samples retained in the Stage 3 loop window, including endpoints."));
                    }
                    KimodoTimelinePreviewRefreshUtility.DrawLoopLockFields(
                        loopLockTargetFieldsEditorProxyProp,
                        loopLockPositionXProp,
                        loopLockPositionYProp,
                        loopLockPositionZProp,
                        loopLockRotationXProp,
                        loopLockRotationYProp,
                        loopLockRotationZProp);
                    EditorGUI.indentLevel--;
                }
            }
            if (overridePathAngleProp != null)
            {
                EditorGUILayout.PropertyField(
                    overridePathAngleProp,
                    new GUIContent("Override Path Angle", "Generate a baseline, then reuse its path length between absolute Root2D begin/end angles."));
                if (!overridePathAngleProp.hasMultipleDifferentValues && overridePathAngleProp.boolValue)
                {
                    EditorGUI.indentLevel++;
                    if (pathBeginAngleDegreesProp != null)
                    {
                        EditorGUILayout.PropertyField(
                            pathBeginAngleDegreesProp,
                            new GUIContent("Path Begin Angle", "Absolute Unity yaw: 0 faces +Z, 90 faces +X, and -90 faces -X."));
                    }
                    if (pathEndAngleDegreesProp != null)
                    {
                        EditorGUILayout.PropertyField(
                            pathEndAngleDegreesProp,
                            new GUIContent("Path End Angle", "Absolute Unity yaw: 0 faces +Z, 90 faces +X, and -90 faces -X."));
                    }
                    if (overridePathDistanceProp != null)
                        EditorGUILayout.PropertyField(overridePathDistanceProp, new GUIContent("Override Distance", "Use a fixed path distance instead of measuring the baseline path."));
                    if (overridePathDistanceProp != null && overridePathDistanceProp.boolValue && pathDistanceProp != null)
                        EditorGUILayout.PropertyField(pathDistanceProp, new GUIContent("Distance", "Path length in Unity units."));
                    EditorGUI.indentLevel--;
                }
            }
            if (useRelativeOffsetProp != null)
            {
                EditorGUILayout.PropertyField(
                    useRelativeOffsetProp,
                    new GUIContent("Relative Offset", "Normalize root X/Z into the initial facing frame before Humanoid conversion, transfer initial yaw to the Timeline Clip rotation, preserve root height in the muscle curves, and apply the World-space target position as the Clip offset."));
                if (useRelativeOffsetProp.boolValue && relativeOffsetProp != null)
                {
                    EditorGUILayout.PropertyField(
                        relativeOffsetProp,
                        new GUIContent("World Position", "World-space target position for the generated Clip's first root pose. The final output root Y curve is preserved and used for height compensation."));
                }
            }
            if (overrideHeadingProp != null)
            {
                EditorGUILayout.PropertyField(
                    overrideHeadingProp,
                    new GUIContent("Override Heading", "Override second-pass Root2D headings every 30 frames with one absolute Unity yaw."));
                if (!overrideHeadingProp.hasMultipleDifferentValues &&
                    overrideHeadingProp.boolValue &&
                    headingDegreesProp != null)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(
                        headingDegreesProp,
                        new GUIContent("Heading", "Absolute Unity yaw in degrees. Positive turns right; negative turns left; zero faces Unity forward."));
                    EditorGUI.indentLevel--;
                }
            }
            EditorGUI.indentLevel--;
        }

        private static void DrawBoundarySampling(string label, SerializedProperty frames, SerializedProperty samples)
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(frames, new GUIContent(
                label + " Window Frames (60 FPS)",
                "Total window duration in 60 FPS command frames. The pipeline converts this duration to model frames before sampling."));
            if (EditorGUI.EndChangeCheck())
                frames.intValue = Mathf.Clamp(frames.intValue, 1, KimodoMotionModelProfiles.MaxGenerationFrames);
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(samples, new GUIContent(label + " Sample Count",
                "Evenly spaced samples including both endpoints. One sample keeps only the seam pose."));
            if (EditorGUI.EndChangeCheck())
                samples.intValue = Mathf.Clamp(samples.intValue, 1, KimodoMotionModelProfiles.MaxGenerationFrames);
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
            List<KimodoConstraintMarker> references = CollectConstraintReferences();
            if (references.Count == 0)
            {
                EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
            }
            else
            {
                for (int i = 0; i < references.Count; i++)
                {
                    KimodoConstraintMarker marker = references[i];
                    if (marker == null)
                    {
                        continue;
                    }

                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(
                            new GUIContent($"{marker.ConstraintType} @ {marker.time:F3}s"),
                            marker,
                            typeof(KimodoConstraintMarker),
                            true);
                    }
                }
            }
        }

        private List<KimodoConstraintMarker> CollectConstraintReferences()
        {
            TimelineClip timelineClip = KimodoTimelineClipResolver.FindTimelineClipForAsset(clip);
            TrackAsset track = timelineClip != null ? timelineClip.GetParentTrack() : null;
            return track == null
                ? new List<KimodoConstraintMarker>()
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
                EditorGUILayout.PropertyField(loopProp, new GUIContent("Is Loop", "Loop this clip when timeline playback exceeds clip duration."));
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


        private void SyncRequestHandleState()
        {
            if (clip == null || !KimodoEditorGenerationJobService.TryGet(clip, out KimodoEditorGenerationJobSession handle) || handle == null)
            {
                isGenerating = false;
                return;
            }

            isGenerating = handle.IsRunning;
            switch (handle.Status)
            {
                case KimodoEditorGenerationJobStatus.Running:
                    lastStatus = string.IsNullOrWhiteSpace(handle.Message) ? "Generating and baking..." : handle.Message;
                    lastError = string.Empty;
                    break;
                case KimodoEditorGenerationJobStatus.Completed:
                    lastStatus = string.IsNullOrWhiteSpace(handle.Message) ? "Generation complete." : handle.Message;
                    lastError = string.Empty;
                    if (handle.Payload is KimodoEditorGenerationResult generateResult &&
                        !string.IsNullOrWhiteSpace(generateResult.ConstraintsPath))
                    {
                        lastConstraintsPath = generateResult.ConstraintsPath;
                    }

                    break;
                case KimodoEditorGenerationJobStatus.Failed:
                    lastStatus = "Generation failed.";
                    lastError = handle.Error;
                    break;
                case KimodoEditorGenerationJobStatus.Canceled:
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

            if (longClipWindowFramesProp != null || longClipWindowSampleCountProp != null)
            {
                EditorGUILayout.LabelField("Long Clip Window", EditorStyles.miniBoldLabel);
                if (longClipWindowFramesProp != null)
                {
                    EditorGUILayout.PropertyField(
                        longClipWindowFramesProp,
                        new GUIContent("Window Frames (60 FPS)", "Overlap duration in 60 FPS command frames; converted to model frames before splitting. Default: 60 command frames."));
                }
                if (longClipWindowSampleCountProp != null)
                {
                    EditorGUILayout.PropertyField(
                        longClipWindowSampleCountProp,
                        new GUIContent("Sample Count", "Number of evenly spaced FullBody samples retained in each long-animation overlap. Default: 2."));
                }
                EditorGUILayout.HelpBox(
                    "The overlap is shared by adjacent generation windows. Window duration is measured at 60 FPS and converted to model frames before splitting; sample count includes both overlap endpoints.",
                    MessageType.Info);
            }

            if (autoBeginAnchor != null)
            {
                EditorGUILayout.PropertyField(
                    autoBeginAnchor,
                    new GUIContent("Auto Begin Anchor", "When the first second has no effective constraint anchor, use the character's original scene Hips position and heading as a Root2D frame-0 constraint."));
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Analysis", EditorStyles.boldLabel);
            if (analyzeAfterGeneration != null)
            {
                EditorGUILayout.PropertyField(
                    analyzeAfterGeneration,
                    new GUIContent("Analyze After Generation", "Run the full animation analysis after generation; enabled by default."));
            }
            using (new EditorGUI.DisabledScope(!clip.isGenerated || clip.clip == null))
            {
                if (GUILayout.Button(new GUIContent(
                        "Run Full Animation Analysis",
                        "Analyze the baked Timeline Clip and show phases, contacts, root trajectory, endpoint comparison, motion profile, and SceneView evidence.")))
                {
                    RunFullAnimationAnalysis();
                }
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

            if (KimodoEditorGenerationJobService.TryGet(
                    clip,
                    out KimodoEditorGenerationJobSession generationSession) &&
                generationSession?.Payload is KimodoEditorGenerationResult generationResult)
            {
                if (!string.IsNullOrWhiteSpace(generationResult.PostAnalysisJson))
                {
                    lastFullAnalysisJson = generationResult.PostAnalysisJson;
                    lastFullAnalysisError = string.Empty;
                }
                else if (!string.IsNullOrWhiteSpace(generationResult.PostAnalysisError))
                {
                    lastFullAnalysisError = generationResult.PostAnalysisError;
                }
                else if (generationResult.PostAnalysisSkipped)
                {
                    EditorGUILayout.HelpBox("Full animation analysis was skipped for this generation.", MessageType.Info);
                }
            }

            if (!string.IsNullOrWhiteSpace(lastFullAnalysisError))
            {
                EditorGUILayout.HelpBox($"Full animation analysis failed: {lastFullAnalysisError}", MessageType.Warning);
            }
            if (!string.IsNullOrWhiteSpace(lastFullAnalysisJson))
            {
                EditorGUILayout.LabelField("Full Animation Analysis", EditorStyles.boldLabel);
                if (GUILayout.Button("Copy Full Analysis JSON", GUILayout.Width(180f)))
                {
                    EditorGUIUtility.systemCopyBuffer = lastFullAnalysisJson;
                }
                fullAnalysisScroll = EditorGUILayout.BeginScrollView(fullAnalysisScroll, GUILayout.MaxHeight(240f));
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.TextArea(lastFullAnalysisJson, GUILayout.MinHeight(140f));
                }
                EditorGUILayout.EndScrollView();
            }

            if (GUILayout.Button(new GUIContent("Reset", "Clear generated metadata/state on this clip. Does not delete external assets."), GUILayout.Width(100)))
            {
                Undo.RecordObject(clip, "Reset Kimodo Clip");
                clip.ResetGeneration();
                EditorUtility.SetDirty(clip);
                KimodoEditorGenerationJobService.Clear(clip);
                lastStatus = string.Empty;
                lastError = string.Empty;
            }

            EditorGUILayout.EndVertical();
        }

        private void RunFullAnimationAnalysis()
        {
            lastFullAnalysisError = string.Empty;
            if (KimodoEditorFullAnalysisBridge.TryAnalyze(clip, out string analysisJson, out string error))
            {
                lastFullAnalysisJson = analysisJson;
                return;
            }

            lastFullAnalysisError = error;
        }

    }
}


