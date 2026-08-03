using TimelineInject;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal sealed class KimodoConstraintOverrideEditWindow : EditorWindow
    {
        private static KimodoConstraintOverrideEditWindow currentWindow;
        private static KimodoConstraintMarkerBase lastKnownMarker;
        private static UnityEngine.Object selectionBeforeOpen;
        private KimodoConstraintMarkerBase marker;
        private PoseCacheRenderContext editContext;
        private bool hasEditContext;
        private string editEntryId;
        private bool timelineLockCaptured;
        private bool previousTimelineLockState;
        private Vector2 scroll;
        private string lastError;

        internal KimodoConstraintMarkerBase TargetMarker => marker;

        internal static void ShowWindow(KimodoConstraintMarkerBase marker)
        {
            if (marker == null || !marker.constraintEnabled)
            {
                return;
            }

            if (selectionBeforeOpen == null)
            {
                selectionBeforeOpen = Selection.activeObject;
            }

            var window = GetWindow<KimodoConstraintOverrideEditWindow>(true, "Kimodo Constraint Override Edit");
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

        internal static bool IsOpenForMarker(KimodoConstraintMarkerBase marker)
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
        }

        private void OnDisable()
        {
            KimodoTimelineConstraintClipCache.Clear();
            KimodoConstraintMarkerBase restoreMarker = marker != null ? marker : lastKnownMarker;
            UnityEngine.Object restoreSelection = selectionBeforeOpen != null ? selectionBeforeOpen : restoreMarker as UnityEngine.Object;

            CommitPoseChangesFromCache();

            if (currentWindow == this)
            {
                currentWindow = null;
            }
            EditorApplication.update -= OnEditorUpdate;
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
                if (KimodoConstraintPoseCache.HasAnyTransformChanges(context, editEntryId))
                {
                    bool targetChanged = marker is KimodoEndEffectorConstraintMarker &&
                        KimodoConstraintPoseCache.HasEndEffectorTargetTransformChanges(context, editEntryId);
                    KimodoConstraintMarkerEditorUtility.LogDragMuscleSnapshot(
                        marker,
                        context,
                        editEntryId);
                    if (!KimodoConstraintPoseCache.TryBuildSampleFromContext(
                            context,
                            editEntryId,
                            marker.ConstraintType,
                            marker.time,
                            out KimodoMarkerSampleResult sample,
                            out string sampleError))
                    {
                        lastError = string.IsNullOrWhiteSpace(sampleError) ? "sample writeback failed." : sampleError;
                    }
                    else if (!KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                                 marker,
                                 sample,
                                 keepOverrideEnabled: true,
                                 out string writeError))
                    {
                        lastError = string.IsNullOrWhiteSpace(writeError) ? "marker writeback failed." : writeError;
                    }
                    else if (!targetChanged &&
                             !KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCache(marker, context, out string poseError))
                    {
                        lastError = string.IsNullOrWhiteSpace(poseError) ? "pose cache update failed." : poseError;
                    }
                    else
                    {
                        KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: true);
                        lastError = string.Empty;
                    }

                    KimodoConstraintPoseCache.ClearTransformChanges(context, editEntryId);
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
            EditorGUILayout.LabelField("Constraint Override Edit", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Edit the pose cache directly. Marker data updates immediately.", MessageType.Info);
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Marker", marker != null ? marker.name : "(null)");
            EditorGUILayout.LabelField("Override", marker.useOverride ? "Enabled" : "Disabled");
            EditorGUILayout.Space(6f);
        }

        private void DrawMarkerPayload()
        {
            if (!marker.useOverride)
            {
                EditorGUILayout.HelpBox("Override is disabled. Move the target or a preview bone to enable it.", MessageType.Info);
            }

            var so = new SerializedObject(marker);
            so.Update();

            bool targetFieldChanged = DrawEndEffectorTargetField(so);
            using (new EditorGUI.DisabledScope(!marker.useOverride && !targetFieldChanged))
            {
                DrawPropertyIfExists(so, "sampleData.sampleTime");
                DrawPropertyIfExists(so, "sampleData.kimodoRootPosition");
                DrawPropertyIfExists(so, "sampleData.localAxisAngles");
                SerializedProperty includeHeadingProp = so.FindProperty("sampleData.hasRootHeading");
                if (includeHeadingProp != null)
                {
                    EditorGUILayout.PropertyField(includeHeadingProp);
                    if (includeHeadingProp.boolValue)
                    {
                        SerializedProperty headingProp = so.FindProperty("sampleData.rootHeading");
                        EditorGUILayout.PropertyField(headingProp, true);
                        if (headingProp != null)
                        {
                            KimodoConstraintHeadingPreviewGUI.Draw(headingProp.vector2Value, enabled: true);
                        }
                    }
                }
            }

            if (so.ApplyModifiedProperties())
            {
                EditorUtility.SetDirty(marker);
                string poseError = string.Empty;
                bool rendered = TryGetEditContext(out PoseCacheRenderContext context, out poseError) &&
                    (targetFieldChanged
                        ? KimodoConstraintPoseCache.TryUpdateEndEffectorTarget(
                            context,
                            editEntryId,
                            marker.ConstraintType,
                            marker.SampleData)
                        : KimodoConstraintMarkerEditorUtility.TryRenderMarkerToPoseCache(marker, context, out poseError));
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

            EditorGUILayout.HelpBox("Pose writes back continuously while this window is open.", MessageType.None);
        }

        private bool DrawEndEffectorTargetField(SerializedObject so)
        {
            if (marker is not KimodoEndEffectorConstraintMarker endEffector ||
                string.Equals(endEffector.ConstraintType, "end-effector", System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            SerializedProperty targetProp = so.FindProperty("sampleData.endEffectorTargetPositionRootLocal");
            if (targetProp == null)
            {
                return false;
            }

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(
                targetProp,
                new GUIContent("Hand/Foot Point (Root Local)"));
            bool changed = EditorGUI.EndChangeCheck();
            if (!changed)
            {
                return false;
            }

            SerializedProperty hasTargetProp = so.FindProperty("sampleData.hasEndEffectorTargetPosition");
            if (hasTargetProp != null)
            {
                hasTargetProp.boolValue = true;
            }
            SerializedProperty overrideProp = so.FindProperty("useOverride");
            if (overrideProp != null)
            {
                overrideProp.boolValue = true;
            }
            return true;
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

        private void ConfigureEditSession(KimodoConstraintMarkerBase target)
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
                !KimodoConstraintPoseCache.HasAnyTransformChanges(context, editEntryId))
            {
                return;
            }

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
                if (!KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                        marker,
                        sample,
                        keepOverrideEnabled: true,
                        out string writeError))
                {
                    lastError = string.IsNullOrWhiteSpace(writeError) ? "marker writeback failed." : writeError;
                }
                else
                {
                    lastError = string.Empty;
                }
            }
            else if (!string.IsNullOrWhiteSpace(sampleError))
            {
                lastError = sampleError;
            }

            EditorUtility.SetDirty(marker);
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
            KimodoConstraintMarkerBase marker,
            PoseCacheRenderContext context,
            string entryId)
        {
            if (marker is KimodoEndEffectorConstraintMarker &&
                KimodoConstraintPoseCache.TryGetEndEffectorTarget(context, entryId, out GameObject target) &&
                target != null)
            {
                Selection.activeGameObject = target;
                EditorGUIUtility.PingObject(target);
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

    }
}
