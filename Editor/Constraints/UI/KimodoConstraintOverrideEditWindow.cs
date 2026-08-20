using TimelineInject;
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoConstraintOverrideEditWindow : EditorWindow
    {
        private static KimodoConstraintOverrideEditWindow currentWindow;
        private static KimodoConstraintMarker lastKnownMarker;
        private static UnityEngine.Object selectionBeforeOpen;
        private KimodoConstraintMarker marker;
        private PoseCacheRenderContext editContext;
        private bool hasEditContext;
        private string editEntryId;
        private bool timelineLockCaptured;
        private bool previousTimelineLockState;
        private bool sceneDragActive;
        private bool pendingEndEffectorWriteback;
        private bool pendingRootWriteback;
        private int sceneDragUndoGroup = -1;
        private bool collapseSceneDragUndo;
        private bool refreshSceneAfterDrag;
        private bool invalidContext;
        private string invalidContextError;
        private int editSceneHandle;
        private bool editSceneCaptured;
        [SerializeField] private HumanBodyBones selectedFullBodyTarget = HumanBodyBones.LastBone;
        private Vector2 scroll;
        private string lastError;
        private double lastRenderedMarkerTime = double.NaN;
        private bool lastRenderedAutoSample;

        internal KimodoConstraintMarker TargetMarker => marker;

        // Kept for existing editor tests and callers during the UI migration.
        // Constraint authoring no longer displays or edits these Euler values.
        internal static HumanBodyBones[] BuildMuscleEulerBones()
        {
            return CharacterPoseMuscleAdapter.UnityBodyMuscleIndices
                .Select(HumanTrait.BoneFromMuscle)
                .Where(index => index >= 0 && index < (int)HumanBodyBones.LastBone)
                .Select(index => (HumanBodyBones)index)
                .Where(bone => bone != HumanBodyBones.Hips)
                .Distinct()
                .ToArray();
        }

        internal static void ShowWindow(
            KimodoConstraintMarker marker,
            HumanBodyBones selectedTarget = HumanBodyBones.LastBone)
        {
            if (marker == null || !marker.constraintEnabled)
            {
                return;
            }

            if (selectionBeforeOpen == null)
            {
                selectionBeforeOpen = Selection.activeObject;
            }

            var window = GetWindow<KimodoConstraintOverrideEditWindow>(true, "Kimodo Constraint Edit");
            window.minSize = new Vector2(420f, 260f);
            window.marker = marker;
            window.selectedFullBodyTarget = selectedTarget;
            window.lastError = string.Empty;
            window.invalidContext = false;
            window.invalidContextError = string.Empty;
            window.editSceneCaptured = false;
            window.CaptureEditScene();
            window.ConfigureEditSession(marker);
            if (marker != null)
            {
                lastKnownMarker = marker;
            }
            window.Show();
            window.Focus();
            KimodoConstraintSelectionPreviewTool.ForceRefresh();
            if (marker != null && KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(marker, out PoseCacheRenderContext context, out _))
            {
                KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: true);
                FocusSelectionOnEditTarget(marker, context, window.editEntryId, window.selectedFullBodyTarget);
            }
        }

        internal static KimodoConstraintOverrideEditWindow GetOpenWindow()
        {
            if (currentWindow != null)
            {
                return currentWindow;
            }

            KimodoConstraintOverrideEditWindow[] windows = Resources.FindObjectsOfTypeAll<KimodoConstraintOverrideEditWindow>();
            for (int i = 0; i < windows.Length; i++)
            {
                if (windows[i] != null)
                {
                    currentWindow = windows[i];
                    return currentWindow;
                }
            }

            return null;
        }

        internal static bool IsOpenForMarker(KimodoConstraintMarker marker)
        {
            if (marker == null)
            {
                return false;
            }

            KimodoConstraintOverrideEditWindow[] windows = Resources.FindObjectsOfTypeAll<KimodoConstraintOverrideEditWindow>();
            for (int i = 0; i < windows.Length; i++)
            {
                if (windows[i] != null && windows[i].marker == marker)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool HasAnyOpenWindow()
        {
            return Resources.FindObjectsOfTypeAll<KimodoConstraintOverrideEditWindow>().Length > 0;
        }

        private void OnEnable()
        {
            currentWindow = this;
            CaptureEditScene();
            if (marker != null)
            {
                lastKnownMarker = marker;
                if (!hasEditContext)
                {
                    ConfigureEditSession(marker);
                }

                if (TryGetEditContext(out PoseCacheRenderContext context, out _))
                {
                    KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: true);
                    KimodoConstraintPoseCache.ClearTransformChanges(context, editEntryId);
                    FocusSelectionOnEditTarget(marker, context, editEntryId, selectedFullBodyTarget);
                }
            }
            LockTimelineWindow();
            EditorApplication.update += OnEditorUpdate;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
        }

        private void OnDisable()
        {
            KimodoConstraintMarker restoreMarker = marker != null ? marker : lastKnownMarker;
            UnityEngine.Object restoreSelection = selectionBeforeOpen != null ? selectionBeforeOpen : restoreMarker as UnityEngine.Object;

            if (!invalidContext)
            {
                CommitPoseChangesFromCache();
            }

            if (currentWindow == this)
            {
                currentWindow = null;
            }
            EditorApplication.update -= OnEditorUpdate;
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            DestroyEditPreview();
            if (!hasEditContext && !invalidContext &&
                restoreMarker != null &&
                KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(
                    restoreMarker,
                    out PoseCacheRenderContext restoreContext,
                    out _))
            {
                KimodoConstraintPoseCache.DestroyContext(restoreContext);
            }
            RestoreTimelineWindowLock();
            KimodoConstraintSelectionPreviewTool.ForceRefresh();
            SceneView.RepaintAll();

            if (restoreSelection != null)
            {
                EditorApplication.delayCall += () =>
                {
                    if (restoreSelection != null)
                    {
                        Selection.activeObject = restoreSelection;
                        EditorApplication.delayCall += () =>
                        {
                            if (restoreSelection != null)
                            {
                                Selection.activeObject = restoreSelection;
                            }
                        };
                    }
                };
            }

            selectionBeforeOpen = null;
            hasEditContext = false;
            editEntryId = string.Empty;
            sceneDragActive = false;
            pendingEndEffectorWriteback = false;
            pendingRootWriteback = false;
            invalidContext = false;
            invalidContextError = string.Empty;
            editSceneCaptured = false;
        }

        private void OnSceneClosing(Scene scene, bool _)
        {
            if (editSceneCaptured && scene.handle == editSceneHandle)
            {
                MarkInvalid("The scene containing the edited character was closed. Reopen the edit window.");
            }
        }

        private void OnActiveSceneChanged(Scene _, Scene next)
        {
            if (editSceneCaptured && next.handle != editSceneHandle)
            {
                MarkInvalid("The active scene changed while the edit window was open. Reopen the edit window.");
            }
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            Event current = Event.current;
            if (current == null)
            {
                return;
            }

            if (current.type == EventType.MouseDrag)
            {
                sceneDragActive = true;
            }
            else if (current.type == EventType.MouseUp || current.type == EventType.Ignore)
            {
                sceneDragActive = false;
                collapseSceneDragUndo = true;
            }
        }

        private void OnEditorUpdate()
        {
            if (invalidContext)
            {
                Repaint();
                return;
            }

            if (marker == null)
            {
                MarkInvalid("The edited constraint marker was deleted.");
                return;
            }

            if (!marker.constraintEnabled)
            {
                Close();
                return;
            }

            if (!TryGetEditContext(out PoseCacheRenderContext context, out string contextError))
            {
                MarkInvalid(string.IsNullOrWhiteSpace(contextError)
                    ? "The edited character or rig is no longer available."
                    : contextError);
                return;
            }

            bool markerTimeChanged = double.IsNaN(lastRenderedMarkerTime) ||
                Math.Abs(lastRenderedMarkerTime - marker.time) > 1e-9;
            bool autoSampleChanged = marker.autoSample != lastRenderedAutoSample;
            if (markerTimeChanged || autoSampleChanged)
            {
                string sampleError = string.Empty;
                string poseError = string.Empty;
                // A disabled AutoSample marker owns its authored payload. A
                // time edit must still refresh the preview, but must not ask
                // the timeline sampler to overwrite that payload with a
                // cached/previous frame.
                bool sampleReady = !marker.autoSample ||
                    KimodoConstraintMarkerEditorUtility.TryUpdateAutoSampleMarkerData(
                        marker, forceRefresh: true, out sampleError);
                if (sampleReady &&
                    KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCache(
                        marker, context, out poseError))
                {
                    lastRenderedMarkerTime = marker.time;
                    lastRenderedAutoSample = marker.autoSample;
                    KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: true);
                    KimodoConstraintPoseCache.ClearTransformChanges(context, editEntryId);
                    lastError = string.Empty;
                }
                else
                {
                    lastError = string.IsNullOrWhiteSpace(sampleError)
                        ? (string.IsNullOrWhiteSpace(poseError) ? "pose cache update failed." : poseError)
                        : sampleError;
                }
            }

            if (sceneDragActive &&
                KimodoConstraintPoseCache.HasIkTargetTransformChanges(context, editEntryId))
            {
                if (KimodoConstraintPoseCache.TryPreviewEndEffectorTargetPose(
                        context,
                        editEntryId,
                        marker.ConstraintType,
                        out string previewError))
                {
                    pendingEndEffectorWriteback = true;
                    lastError = string.Empty;
                }
                else
                {
                    lastError = string.IsNullOrWhiteSpace(previewError)
                        ? "end-effector preview failed."
                        : previewError;
                }
            }

            if (sceneDragActive &&
                KimodoConstraintPoseCache.HasRootTargetTransformChanges(context, editEntryId))
            {
                pendingRootWriteback = true;
            }

            if (!sceneDragActive &&
                (Tools.current == Tool.Move || Tools.current == Tool.Transform) &&
                KimodoConstraintPoseCache.IsNonRootPoseTransform(
                    context,
                    editEntryId,
                    Selection.activeTransform))
            {
                Tools.current = Tool.Rotate;
            }

            if (pendingEndEffectorWriteback || pendingRootWriteback ||
                KimodoConstraintPoseCache.HasAnyTransformChanges(context, editEntryId))
            {
                WriteBackPoseChanges(context);
            }

            if (!sceneDragActive && refreshSceneAfterDrag)
            {
                if (KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCache(marker, context, out string poseError))
                {
                    KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: true);
                    RestoreEndEffectorTargetSelection(marker, context, editEntryId, selectedFullBodyTarget);
                    lastError = string.Empty;
                }
                else
                {
                    lastError = string.IsNullOrWhiteSpace(poseError) ? "pose cache update failed." : poseError;
                }
                refreshSceneAfterDrag = false;
            }

            if (collapseSceneDragUndo)
            {
                CollapseSceneDragUndo();
            }

            Repaint();
        }

        private void OnGUI()
        {
            if (invalidContext)
            {
                DrawInvalidState();
                return;
            }

            if (marker == null)
            {
                MarkInvalid("The edited constraint marker was deleted.");
                DrawInvalidState();
                return;
            }

            KimodoConstraintMarkerEditorUtility.HandleDeleteCommand(marker);
            if (marker == null)
            {
                MarkInvalid("The edited constraint marker was deleted.");
                DrawInvalidState();
                return;
            }

            if (marker.parent == null)
            {
                MarkInvalid("The edited rig or parent track was deleted.");
                DrawInvalidState();
                return;
            }

            if (!TryGetEditContext(out _, out string contextError))
            {
                MarkInvalid(string.IsNullOrWhiteSpace(contextError)
                    ? "The edited character or rig is no longer available."
                    : contextError);
                DrawInvalidState();
                return;
            }

            DrawHeader();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawMarkerPayload();
            EditorGUILayout.EndScrollView();
            DrawFooter();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Constraint Edit", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Edit raw Muscle values or Scene targets. Marker data updates immediately; bone Euler angles are intentionally not editable.", MessageType.Info);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Marker", marker != null ? marker.name : "(null)");
            EditorGUILayout.Space(6f);
        }

        private void DrawMarkerPayload()
        {
            var so = new SerializedObject(marker);
            so.Update();

            KimodoConstraintEditorState.DrawConstraintPayload(so);

            if (KimodoConstraintEditorState.ApplyConstraintPanels(so, marker))
            {
                string poseError = string.Empty;
                bool rendered = TryGetEditContext(out PoseCacheRenderContext context, out poseError) &&
                    KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCache(marker, context, out poseError);
                if (rendered)
                {
                    KimodoConstraintPoseCache.ClearTransformChanges(context, editEntryId);
                    lastError = string.Empty;
                }
                else
                {
                    lastError = string.IsNullOrWhiteSpace(poseError) ? "pose cache update failed." : poseError;
                }
            }

        }


        private void DrawFooter()
        {
            if (!string.IsNullOrWhiteSpace(lastError))
            {
                EditorGUILayout.HelpBox(lastError, MessageType.Error);
            }

            EditorGUILayout.Space(6f);
            if (GUILayout.Button(new GUIContent("Close", "Close the edit window and keep current marker data."), GUILayout.Height(30f)))
            {
                CommitPoseChangesFromCache();
                Close();
            }
        }

        private void DrawInvalidState()
        {
            EditorGUILayout.LabelField("Constraint Edit", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                string.IsNullOrWhiteSpace(invalidContextError)
                    ? "The edit window is no longer valid."
                    : invalidContextError,
                MessageType.Error);
            EditorGUILayout.HelpBox(
                "The character, rig, marker, or Timeline scene used by this window is no longer available. Close this window and reopen it after restoring the source.",
                MessageType.Error);
            EditorGUILayout.Space(6f);
            if (GUILayout.Button("Close", GUILayout.Height(30f)))
            {
                Close();
                GUIUtility.ExitGUI();
            }
        }

        private void ConfigureEditSession(KimodoConstraintMarker target)
        {
            hasEditContext = false;
            editEntryId = string.Empty;
            if (target == null)
            {
                return;
            }

            editEntryId = KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(target);
            if (!KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(target, out editContext, out string contextError))
            {
                lastError = contextError;
                return;
            }

            hasEditContext = true;
            lastRenderedMarkerTime = target.time;
            lastRenderedAutoSample = target.autoSample;
            if (!KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCache(target, editContext, out string renderError))
            {
                lastError = renderError;
                return;
            }

            KimodoConstraintPoseCache.SetGroupState(editContext, visible: true, selectable: true);
            KimodoConstraintPoseCache.ClearTransformChanges(editContext, editEntryId);
            FocusSelectionOnEditTarget(target, editContext, editEntryId, selectedFullBodyTarget);
        }

        private bool TryGetEditContext(out PoseCacheRenderContext context, out string error)
        {
            error = string.Empty;
            if (invalidContext)
            {
                context = default;
                error = invalidContextError;
                return false;
            }

            if (!IsEditSceneStillValid(out error))
            {
                context = default;
                return false;
            }

            string contextError = string.Empty;
            if (marker == null)
            {
                context = default;
                error = "The edited constraint marker was deleted.";
                return false;
            }

            if (!KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(
                    marker,
                    out PoseCacheRenderContext resolvedContext,
                    out contextError))
            {
                context = default;
                error = contextError;
                error = string.IsNullOrWhiteSpace(error)
                    ? "The edited character or rig is no longer available."
                    : error;
                return false;
            }

            if (hasEditContext)
            {
                if (!KimodoRetargetCoreUtility.IsValidHumanoid(editContext.SourceAvatar))
                {
                    context = default;
                    error = "The edited rig Avatar was deleted or is no longer valid. Reopen the edit window.";
                    return false;
                }

                Animator sourceAnimator =
                    KimodoEditorObjectIdUtility.ObjectFromId(editContext.AnimatorId) as Animator;
                if (sourceAnimator == null ||
                    !KimodoLocalAvatarUtility.CheckAvatarValid(
                        editContext.SourceAvatar,
                        sourceAnimator.gameObject))
                {
                    context = default;
                    error = "The edited character rig was deleted or no longer matches its Avatar. Reopen the edit window.";
                    return false;
                }

                if (!KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                        editContext.ModelName,
                        out Avatar profileAvatar,
                        out _)
                    || !KimodoRetargetCoreUtility.IsValidHumanoid(profileAvatar))
                {
                    context = default;
                    error = "The edited rig profile Avatar was deleted or is no longer valid. Reopen the edit window.";
                    return false;
                }

                if (!AreSameContext(editContext, resolvedContext))
                {
                    context = default;
                    error = "The edited character or rig changed or was deleted. Reopen the edit window.";
                    return false;
                }

                if (!KimodoConstraintPoseCache.TryGetPreviewRoot(
                        editContext,
                        editEntryId,
                        out Transform previewRoot) ||
                    previewRoot == null)
                {
                    context = default;
                    error = "The edited rig preview was deleted. Reopen the edit window.";
                    return false;
                }

                context = editContext;
                return true;
            }

            editContext = resolvedContext;
            editEntryId = KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker);
            hasEditContext = true;
            context = editContext;
            return true;
        }

        private void CommitPoseChangesFromCache()
        {
            if (invalidContext ||
                marker == null ||
                !TryGetEditContext(out PoseCacheRenderContext context, out _) ||
                (!pendingEndEffectorWriteback &&
                 !pendingRootWriteback &&
                 !KimodoConstraintPoseCache.HasAnyTransformChanges(context, editEntryId)))
            {
                return;
            }

            WriteBackPoseChanges(context);
            CollapseSceneDragUndo();
        }

        private void WriteBackPoseChanges(PoseCacheRenderContext context)
        {
            if (invalidContext || marker == null) return;
            EnsureSceneDragUndo();
            KimodoConstraintPoseCache.RestoreNonRootBoneTranslations(context, editEntryId);
            KimodoConstraintMarkerEditorUtility.LogDragMuscleSnapshot(
                marker,
                context,
                editEntryId);
            string sampleError = string.Empty;
            if (KimodoConstraintPoseCache.TryBuildSampleFromContext(
                    context,
                    editEntryId,
                    marker.ConstraintType,
                    marker.time,
                    out KimodoMarkerSampleResult sample,
                    out sampleError))
            {
                KimodoConstraintPoseCache.EnableChangedConstraintChannels(context, editEntryId, sample);
                if (!KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                        marker,
                        sample,
                        out string writeError,
                        writeSampledCharacterPose: true))
                {
                    lastError = string.IsNullOrWhiteSpace(writeError) ? "marker writeback failed." : writeError;
                }
                else
                {
                    // Rebuilding the cache during an active Scene drag would
                    // recreate the target and reset the handle mid-drag.
                    string poseError = string.Empty;
                    if (sceneDragActive ||
                        KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCache(marker, context, out poseError))
                    {
                        KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: true);
                        RestoreEndEffectorTargetSelection(marker, context, editEntryId, selectedFullBodyTarget);
                        lastError = string.Empty;
                        refreshSceneAfterDrag |= sceneDragActive;
                    }
                    else
                    {
                        lastError = string.IsNullOrWhiteSpace(poseError) ? "pose cache update failed." : poseError;
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(sampleError))
            {
                lastError = sampleError;
            }

            EditorUtility.SetDirty(marker);
            KimodoConstraintPoseCache.ClearTransformChanges(context, editEntryId);
            pendingEndEffectorWriteback = false;
            pendingRootWriteback = false;
        }

        private void EnsureSceneDragUndo()
        {
            if (sceneDragUndoGroup >= 0 || marker == null) return;
            Undo.IncrementCurrentGroup();
            sceneDragUndoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Edit Kimodo Constraint");
            Undo.RecordObject(marker, "Edit Kimodo Constraint");
        }

        private void CollapseSceneDragUndo()
        {
            if (sceneDragUndoGroup >= 0) Undo.CollapseUndoOperations(sceneDragUndoGroup);
            sceneDragUndoGroup = -1;
            collapseSceneDragUndo = false;
        }

        private void LockTimelineWindow()
        {
            if (timelineLockCaptured)
            {
                return;
            }

            try
            {
                previousTimelineLockState = KimodoTimelinePreviewRefreshUtility.GetTImelineWindowLockState();
                KimodoTimelinePreviewRefreshUtility.SetTimelineWindowLockState(true);
                timelineLockCaptured = true;
            }
            catch
            {
                timelineLockCaptured = false;
            }
        }

        private void RestoreTimelineWindowLock()
        {
            if (!timelineLockCaptured)
            {
                return;
            }

            try
            {
                KimodoTimelinePreviewRefreshUtility.SetTimelineWindowLockState(previousTimelineLockState);
            }
            catch
            {
                // Timeline window may already be closed during editor shutdown.
            }

            timelineLockCaptured = false;
        }

        private void CaptureEditScene()
        {
            if (editSceneCaptured)
            {
                return;
            }

            Scene scene = SceneManager.GetActiveScene();
            var director = TimelineEditor.inspectedDirector;
            if (director != null && director.gameObject != null && director.gameObject.scene.IsValid())
            {
                scene = director.gameObject.scene;
            }

            editSceneHandle = scene.handle;
            editSceneCaptured = scene.IsValid();
        }

        private bool IsEditSceneStillValid(out string error)
        {
            error = string.Empty;
            if (!editSceneCaptured)
            {
                return true;
            }

            Scene activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || activeScene.handle != editSceneHandle)
            {
                error = "The active scene changed while the edit window was open. Reopen the edit window.";
                return false;
            }

            return true;
        }

        private void MarkInvalid(string error)
        {
            invalidContext = true;
            invalidContextError = string.IsNullOrWhiteSpace(error)
                ? "The edit window is no longer valid."
                : error;
            lastError = invalidContextError;
            sceneDragActive = false;
            CollapseSceneDragUndo();
            refreshSceneAfterDrag = false;
            pendingEndEffectorWriteback = false;
            pendingRootWriteback = false;
            DestroyEditPreview();
            Repaint();
            SceneView.RepaintAll();
        }

        private void DestroyEditPreview()
        {
            if (!hasEditContext)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(editEntryId))
            {
                KimodoConstraintPoseCache.DestroyEntry(editContext, editEntryId);
            }
            else
            {
                KimodoConstraintPoseCache.DestroyContext(editContext);
            }

            hasEditContext = false;
            editEntryId = string.Empty;
            editContext = default;
        }

        private static bool AreSameContext(PoseCacheRenderContext left, PoseCacheRenderContext right)
        {
            return left.ClipId == right.ClipId &&
                left.AnimatorId == right.AnimatorId &&
                left.TrackId == right.TrackId &&
                left.RigType == right.RigType &&
                string.Equals(left.ModelName, right.ModelName, System.StringComparison.Ordinal);
        }

        private static void DrawPropertyIfExists(SerializedObject so, string name)
        {
            if (so == null || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            SerializedProperty prop = so.FindProperty(name);
            if (prop != null)
            {
                EditorGUILayout.PropertyField(prop, true);
            }
        }

        private static void FocusSelectionOnEditTarget(
            KimodoConstraintMarker marker,
            PoseCacheRenderContext context,
            string entryId,
            HumanBodyBones selectedTarget = HumanBodyBones.LastBone)
        {
            if (selectedTarget != HumanBodyBones.LastBone &&
                KimodoConstraintPoseCache.TryGetFullBodyTarget(
                    context,
                    entryId,
                    selectedTarget,
                    out GameObject selectedTargetObject) &&
                selectedTargetObject != null)
            {
                Selection.activeGameObject = selectedTargetObject;
                EditorGUIUtility.PingObject(selectedTargetObject);
                Tools.current = Tool.Move;
                SceneView.lastActiveSceneView?.FrameSelected();
                return;
            }

            if (KimodoConstraintPoseCache.TryGetFullBodyTarget(
                    context,
                    entryId,
                    HumanBodyBones.Hips,
                    out GameObject pelvisTarget) &&
                pelvisTarget != null)
            {
                Selection.activeGameObject = pelvisTarget;
                EditorGUIUtility.PingObject(pelvisTarget);
                Tools.current = Tool.Move;
                SceneView.lastActiveSceneView?.FrameSelected();
                return;
            }

            if (marker is KimodoConstraintMarker &&
                KimodoConstraintPoseCache.TryGetEndEffectorTarget(
                    context,
                    entryId,
                    out GameObject endEffectorTarget) &&
                endEffectorTarget != null)
            {
                Selection.activeGameObject = endEffectorTarget;
                EditorGUIUtility.PingObject(endEffectorTarget);
                Tools.current = Tool.Move;
                SceneView.lastActiveSceneView?.FrameSelected();
                return;
            }

            if (!KimodoConstraintPoseCache.TryGetRootBone(context, entryId, out Transform rootBone) ||
                rootBone == null ||
                rootBone.gameObject == null)
            {
                return;
            }

            Selection.activeGameObject = rootBone.gameObject;
            EditorGUIUtility.PingObject(rootBone.gameObject);
        }

        private static void RestoreEndEffectorTargetSelection(
            KimodoConstraintMarker marker,
            PoseCacheRenderContext context,
            string entryId,
            HumanBodyBones selectedTarget = HumanBodyBones.LastBone)
        {
            if (selectedTarget != HumanBodyBones.LastBone &&
                KimodoConstraintPoseCache.TryGetFullBodyTarget(context, entryId, selectedTarget, out GameObject fullBodyTarget) &&
                fullBodyTarget != null)
            {
                Selection.activeGameObject = fullBodyTarget;
                return;
            }

            if (marker is KimodoConstraintMarker &&
                KimodoConstraintPoseCache.TryGetEndEffectorTarget(context, entryId, out GameObject target) &&
                target != null)
            {
                Selection.activeGameObject = target;
            }
        }

    }
}
