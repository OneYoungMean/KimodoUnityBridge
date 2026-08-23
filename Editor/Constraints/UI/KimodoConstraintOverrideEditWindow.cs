using TimelineInject;
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoConstraintOverrideEditWindow : EditorWindow
    {
        private static KimodoConstraintOverrideEditWindow currentWindow;
        private static KimodoConstraintMarker lastKnownMarker;
        private static UnityEngine.Object selectionBeforeOpen;
        private KimodoConstraintMarker marker;
        internal KimodoConstraintMarker selectedConstraint
        {
            get => marker;
            private set => marker = value;
        }
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
        private ulong editSceneHandle;
        private bool editSceneCaptured;
        private Vector2 scroll;
        private string lastError;
        private double lastRenderedMarkerTime = double.NaN;
        private bool lastRenderedAutoSample;
        private KimodoConstraintInspectorEditor constraintInspectorEditor;

        internal static void ShowWindow(KimodoConstraintMarker marker)
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
            window.selectedConstraint = marker;
            window.CreateConstraintInspectorEditor();
            window.lastError = string.Empty;
            window.invalidContext = false;
            window.invalidContextError = string.Empty;
            window.editSceneCaptured = false;
            window.CaptureEditScene();
            window.ConfigureEditSession(window.selectedConstraint);
            if (marker != null)
            {
                lastKnownMarker = marker;
            }
            window.Show();
            window.Focus();
            KimodoConstraintSelectionPreviewTool.SchedulePreviewUpdate();
            if (window.hasEditContext)
            {
                KimodoConstraintPoseCache.SetGroupState(window.editContext, visible: true, selectable: true);
                FocusSelectionOnEditTarget(window.editContext, window.editEntryId);
            }
            QueuePreviewFocus(window, window.selectedConstraint);
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
                if (windows[i] != null && windows[i].selectedConstraint == marker)
                {
                    return true;
                }
            }

            return false;
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
                    FocusSelectionOnEditTarget(context, editEntryId);
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
                CommitPoseChangesFromPreview();
            }

            if (currentWindow == this)
            {
                currentWindow = null;
            }
            EditorApplication.update -= OnEditorUpdate;
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            // Hide and deselect the edit preview before destroying its rig.
            if (hasEditContext)
            {
                KimodoConstraintPoseCache.SetGroupState(editContext, visible: false, selectable: false);
            }
            DestroyEditPreview();
            DestroyConstraintInspectorEditor();
            RestoreTimelineWindowLock();
            // The Timeline preview must remain enabled after the override
            // window closes so the authored result stays visible in the scene.
            KimodoTimelinePreviewRefreshUtility.TryEnablePreview();
            KimodoTimelinePreviewRefreshUtility.RefreshEditorWorkflow(
                RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
            KimodoConstraintSelectionPreviewTool.SchedulePreviewUpdate();
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
            if (editSceneCaptured && KimodoUnityObjectIdUtility.GetSceneHandle(scene) == editSceneHandle)
            {
                MarkInvalid("The scene containing the edited character was closed. Reopen the edit window.");
            }
        }

        private void OnActiveSceneChanged(Scene _, Scene next)
        {
            if (editSceneCaptured && KimodoUnityObjectIdUtility.GetSceneHandle(next) != editSceneHandle)
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
                string previewError = string.Empty;
                if (KimodoConstraintSelectionPreviewTool.TryUpdateMarkerPreview(
                        marker,
                        context,
                        true,
                        out previewError))
                {
                    lastRenderedMarkerTime = marker.time;
                    lastRenderedAutoSample = marker.autoSample;
                    KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: true);
                    KimodoConstraintPoseCache.ClearTransformChanges(context, editEntryId);
                    lastError = string.Empty;
                }
                else
                {
                    lastError = string.IsNullOrWhiteSpace(previewError)
                        ? "pose preview update failed."
                        : previewError;
                }
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

            if (!sceneDragActive && refreshSceneAfterDrag)
            {
                if (KimodoConstraintSelectionPreviewTool.TryRenderEditPreview(marker, context, out string poseError))
                {
                    KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: true);
                    RestoreEndEffectorTargetSelection(context, editEntryId);
                    lastError = string.Empty;
                }
                else
                {
                    lastError = string.IsNullOrWhiteSpace(poseError) ? "pose preview update failed." : poseError;
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
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Marker", marker != null ? marker.name : "(null)");
            EditorGUILayout.Space(6f);
        }

        private void DrawMarkerPayload()
        {
            CreateConstraintInspectorEditor();
            if (constraintInspectorEditor != null && constraintInspectorEditor.DrawGUI(isWindow: true))
            {
                string poseError = string.Empty;
                bool rendered = TryGetEditContext(out PoseCacheRenderContext context, out poseError) &&
                    KimodoConstraintSelectionPreviewTool.TryRenderEditPreview(marker, context, out poseError);
                if (rendered)
                {
                    KimodoConstraintPoseCache.ClearTransformChanges(context, editEntryId);
                    lastError = string.Empty;
                }
                else
                {
                    lastError = string.IsNullOrWhiteSpace(poseError) ? "pose preview update failed." : poseError;
                }
            }

        }

        private void CreateConstraintInspectorEditor()
        {
            if (marker == null)
            {
                DestroyConstraintInspectorEditor();
                return;
            }
            if (constraintInspectorEditor != null && constraintInspectorEditor.target == marker)
            {
                return;
            }
            DestroyConstraintInspectorEditor();
            constraintInspectorEditor = UnityEditor.Editor.CreateEditor(
                marker,
                typeof(KimodoConstraintInspectorEditor)) as KimodoConstraintInspectorEditor;
        }

        private void DestroyConstraintInspectorEditor()
        {
            if (constraintInspectorEditor == null) return;
            DestroyImmediate(constraintInspectorEditor);
            constraintInspectorEditor = null;
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
                CommitPoseChangesFromPreview();
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

            lastRenderedMarkerTime = target.time;
            lastRenderedAutoSample = target.autoSample;
            if (!KimodoConstraintSelectionPreviewTool.TryBeginEditPreview(
                    target,
                    out editContext,
                    out editEntryId,
                    out string renderError))
            {
                lastError = renderError;
                return;
            }

            hasEditContext = true;
            KimodoConstraintPoseCache.SetGroupState(editContext, visible: true, selectable: true);
            KimodoConstraintPoseCache.ClearTransformChanges(editContext, editEntryId);
            FocusSelectionOnEditTarget(editContext, editEntryId);
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
                Animator sourceAnimator =
                    KimodoEditorObjectIdUtility.ObjectFromId(editContext.AnimatorId) as Animator;
                if (sourceAnimator == null || !KimodoConstraintMarkerEditorUtility.TryGetMarkerTrack(
                        marker,
                        out _))
                {
                    context = default;
                    error = "The edited character rig was deleted or no longer matches its Avatar. Reopen the edit window.";
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

        private void CommitPoseChangesFromPreview()
        {
            // Handles write the marker's SampleResult directly. There is no
            // Transform-cache commit phase anymore.
            pendingEndEffectorWriteback = false;
            pendingRootWriteback = false;
            CollapseSceneDragUndo();
        }

        private void WriteBackPoseChanges(PoseCacheRenderContext context)
        {
            return;
#pragma warning disable CS0162
            // AutoSample is a read-only projection: Timeline -> SampleResult ->
            // target skeleton -> rig. Only authored (non-auto) markers may
            // write world-space rig edits back into SampleResult.
            if (invalidContext || marker == null) return;
            if (marker.autoSample)
            {
                pendingEndEffectorWriteback = false;
                pendingRootWriteback = false;
                if (TryGetEditContext(out PoseCacheRenderContext readOnlyContext, out _))
                {
                    KimodoConstraintPoseCache.ClearTransformChanges(readOnlyContext, editEntryId);
                }
                return;
            }
            if (!TryGetEditContext(out PoseCacheRenderContext resolvedContext, out _)) return;
            EnsureSceneDragUndo();
            string sampleError = string.Empty;
            if (KimodoConstraintPoseCache.TryBuildSampleFromContext(
                    resolvedContext,
                    editEntryId,
                    marker.ConstraintType,
                    marker.time,
                    out KimodoMarkerSampleResult sample,
                    out sampleError))
            {
                if (sample.effectors != null)
                {
                    selectedConstraint.SetEffectors(sample.effectors);
                }
                KimodoConstraintPoseCache.EnableChangedConstraintChannels(resolvedContext, editEntryId, sample);
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
                        KimodoConstraintSelectionPreviewTool.TryRenderEditPreview(marker, resolvedContext, out poseError))
                    {
                        KimodoConstraintPoseCache.SetGroupState(resolvedContext, visible: true, selectable: true);
                        RestoreEndEffectorTargetSelection(resolvedContext, editEntryId);
                        lastError = string.Empty;
                        refreshSceneAfterDrag |= sceneDragActive;
                    }
                    else
                    {
                        lastError = string.IsNullOrWhiteSpace(poseError) ? "pose preview update failed." : poseError;
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(sampleError))
            {
                lastError = sampleError;
            }

            EditorUtility.SetDirty(marker);
            KimodoConstraintPoseCache.ClearTransformChanges(resolvedContext, editEntryId);
            pendingEndEffectorWriteback = false;
            pendingRootWriteback = false;
#pragma warning restore CS0162
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

            editSceneHandle = KimodoUnityObjectIdUtility.GetSceneHandle(scene);
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
            if (!activeScene.IsValid() ||
                KimodoUnityObjectIdUtility.GetSceneHandle(activeScene) != editSceneHandle)
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

            if (hasEditContext && !string.IsNullOrWhiteSpace(editEntryId))
            {
                KimodoConstraintSelectionPreviewTool.EndEditPreview(editContext, editEntryId);
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

        private static void FocusSelectionOnEditTarget(
            PoseCacheRenderContext context,
            string entryId)
        {
            // The controller gizmos have no renderable bounds, so framing one
            // can zoom the Scene view to an unusably large scale. Focus the
            // actual Preview character instead; its hierarchy gives SceneView
            // meaningful bounds.
            if (KimodoConstraintPoseCache.TryGetPreviewRoot(context, entryId, out Transform previewRoot) &&
                previewRoot != null &&
                previewRoot.gameObject != null)
            {
                Selection.activeGameObject = previewRoot.gameObject;
                EditorGUIUtility.PingObject(previewRoot.gameObject);
                Tools.current = Tool.Move;
                FramePreviewSceneView(previewRoot);
                return;
            }

        }

        private static void FramePreviewSceneView(Transform previewRoot)
        {
            if (Application.isBatchMode)
            {
                return;
            }

            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
            {
                return;
            }

            try
            {
                Renderer[] renderers = previewRoot.GetComponentsInChildren<Renderer>(true);
                if (renderers == null || renderers.Length == 0)
                {
                    sceneView.FrameSelected();
                    return;
                }
                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    if (renderers[i] != null) bounds.Encapsulate(renderers[i].bounds);
                }
                sceneView.Frame(bounds, false);
            }
            catch (NullReferenceException)
            {
                // Unity can expose a SceneView before its Inspector editors
                // exist during domain reload; selection remains valid.
            }
        }

        private static void QueuePreviewFocus(
            KimodoConstraintOverrideEditWindow window,
            KimodoConstraintMarker marker)
        {
            EditorApplication.delayCall += () =>
            {
                if (window == null || window.marker != marker || marker == null ||
                    !marker.constraintEnabled || !window.hasEditContext)
                {
                    return;
                }

                KimodoConstraintPoseCache.SetGroupState(window.editContext, visible: true, selectable: true);
                FocusSelectionOnEditTarget(window.editContext, window.editEntryId);
            };
        }

        private static void RestoreEndEffectorTargetSelection(
            PoseCacheRenderContext context,
            string entryId)
        {
            if (KimodoConstraintPoseCache.TryGetEndEffectorTarget(context, entryId, out GameObject target) &&
                target != null)
            {
                Selection.activeGameObject = target;
            }
        }

    }
}
