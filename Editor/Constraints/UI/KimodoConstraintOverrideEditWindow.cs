using TimelineInject;
using System.Linq;
using UnityEditor;
using UnityEngine;

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
        private Vector2 scroll;
        private string lastError;

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
            window.marker = marker;
            window.lastError = string.Empty;
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
                FocusSelectionOnEditTarget(marker, context, window.editEntryId);
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
                    FocusSelectionOnEditTarget(marker, context, editEntryId);
                }
            }
            LockTimelineWindow();
            EditorApplication.update += OnEditorUpdate;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private void OnDisable()
        {
            KimodoConstraintMarker restoreMarker = marker != null ? marker : lastKnownMarker;
            UnityEngine.Object restoreSelection = selectionBeforeOpen != null ? selectionBeforeOpen : restoreMarker as UnityEngine.Object;

            CommitPoseChangesFromCache();

            if (currentWindow == this)
            {
                currentWindow = null;
            }
            EditorApplication.update -= OnEditorUpdate;
            SceneView.duringSceneGui -= OnSceneGUI;
            if (hasEditContext)
            {
                if (!string.IsNullOrWhiteSpace(editEntryId))
                {
                    KimodoConstraintPoseCache.DestroyEntry(editContext, editEntryId);
                }
                else
                {
                    KimodoConstraintPoseCache.DestroyContext(editContext);
                }
            }
            else if (restoreMarker != null && KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(restoreMarker, out PoseCacheRenderContext restoreContext, out _))
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
            if (marker == null || !marker.constraintEnabled)
            {
                Close();
                return;
            }

            if (TryGetEditContext(out PoseCacheRenderContext context, out _))
            {
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
                        RestoreEndEffectorTargetSelection(marker, context, editEntryId);
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
            }

            Repaint();
        }

        private void OnGUI()
        {
            if (marker == null)
            {
                EditorGUILayout.HelpBox("Marker is null.", MessageType.Error);
                return;
            }

            KimodoConstraintMarkerEditorUtility.HandleDeleteCommand(marker);
            if (marker == null || marker.parent == null)
            {
                Close();
                GUIUtility.ExitGUI();
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

            DrawPropertyIfExists(so, "sampleData.sampleTime");
            KimodoConstraintEditorState.DrawConstraintPanels(so);

            if (so.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(marker);
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

            EditorGUILayout.HelpBox(
                "Muscle values are the authoritative body-pose data. Scene target drags write back to the same canonical pose when the drag completes.",
                MessageType.None);
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
            if (!KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCache(target, editContext, out string renderError))
            {
                lastError = renderError;
                return;
            }

            KimodoConstraintPoseCache.SetGroupState(editContext, visible: true, selectable: true);
            KimodoConstraintPoseCache.ClearTransformChanges(editContext, editEntryId);
            FocusSelectionOnEditTarget(target, editContext, editEntryId);
        }

        private bool TryGetEditContext(out PoseCacheRenderContext context, out string error)
        {
            error = string.Empty;
            string contextError = string.Empty;
            if (hasEditContext)
            {
                context = editContext;
                return true;
            }

            if (marker != null && KimodoConstraintMarkerEditorUtility.TryBuildRenderContextForMarker(marker, out context, out contextError))
            {
                editContext = context;
                editEntryId = KimodoConstraintMarkerEditorUtility.GetMarkerEntryId(marker);
                hasEditContext = true;
                return true;
            }

            context = default;
            error = contextError;
            error = string.IsNullOrWhiteSpace(error) ? "edit context is unavailable." : error;
            return false;
        }

        private void CommitPoseChangesFromCache()
        {
            if (marker == null ||
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
            if (marker == null) return;
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
                KimodoConstraintPoseCache.GetChangedAutoSampleChannels(
                    context, editEntryId, out bool fullBodyChanged, out bool root2DChanged);
                KimodoConstraintPoseCache.EnableChangedConstraintChannels(context, editEntryId, sample);
                if (!KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                        marker,
                        sample,
                        disableFullBodyAutoSample: fullBodyChanged,
                        disableRoot2DAutoSample: root2DChanged,
                        out string writeError))
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
                        RestoreEndEffectorTargetSelection(marker, context, editEntryId);
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
            string entryId)
        {
            if ((marker is KimodoConstraintMarker || marker is KimodoConstraintMarker) &&
                KimodoConstraintPoseCache.TryGetFullBodyTarget(
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
            string entryId)
        {
            if (marker is KimodoConstraintMarker &&
                KimodoConstraintPoseCache.TryGetEndEffectorTarget(context, entryId, out GameObject target) &&
                target != null)
            {
                Selection.activeGameObject = target;
            }
        }

    }
}
