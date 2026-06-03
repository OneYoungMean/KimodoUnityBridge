using KimodoUnityMotionTools.ProjectEditor.AnimatorTooling;
using System;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor.RootMotionTooling
{
    public sealed class FootRootMotionPreviewWindow : EditorWindow
    {
        private const string MenuPath = "Kimodo/Foot Root Motion Preview";

        private enum PreviewMode
        {
            Original,
            Solved
        }

        private AnimationClip clip;
        private GameObject humanoidPrefab;
        private FootRootMotionSolverSettings settings;
        private FootRootMotionFrame[] sampledFrames;
        private FootRootMotionResult solvedResult;
        private string lastError = string.Empty;
        private string lastStatus = string.Empty;
        private bool showSettings = true;
        private bool showDebug = true;
        private PreviewMode previewMode;
        private double lastRepaintAt;

        private GameObject previewInstance;
        private Animator previewAnimator;
        private KimodoAvatarPreview avatarPreview;

        [MenuItem(MenuPath, priority = 111)]
        private static void OpenWindow()
        {
            FootRootMotionPreviewWindow window = GetWindow<FootRootMotionPreviewWindow>("Foot Root Motion");
            window.minSize = new Vector2(1180f, 700f);
            window.Show();
        }

        private void OnEnable()
        {
            settings ??= new FootRootMotionSolverSettings();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            CleanupPreview();
        }

        private void OnEditorUpdate()
        {
            avatarPreview?.timeControl?.Update();
            ApplySolvedPreviewPose();

            double now = EditorApplication.timeSinceStartup;
            if (now - lastRepaintAt > 0.05d)
            {
                lastRepaintAt = now;
                Repaint();
            }
        }

        private void OnGUI()
        {
            DrawToolbar();

            using (new EditorGUILayout.HorizontalScope())
            {
                DrawLeftPanel();
                DrawPreviewPanel();
            }

            DrawStatus();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUI.BeginChangeCheck();
                AnimationClip nextClip = (AnimationClip)EditorGUILayout.ObjectField(clip, typeof(AnimationClip), false, GUILayout.Width(260f));
                GameObject nextPrefab = (GameObject)EditorGUILayout.ObjectField(humanoidPrefab, typeof(GameObject), false, GUILayout.Width(260f));
                if (EditorGUI.EndChangeCheck())
                {
                    clip = nextClip;
                    humanoidPrefab = nextPrefab;
                    sampledFrames = null;
                    solvedResult = null;
                    lastError = string.Empty;
                    lastStatus = "Input changed. Resample to refresh data.";
                    RebuildPreview();
                }

                if (GUILayout.Button("Sample", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                {
                    Sample();
                }

                if (GUILayout.Button("Solve", EditorStyles.toolbarButton, GUILayout.Width(70f)))
                {
                    Solve();
                }

                if (GUILayout.Button("Resample + Solve", EditorStyles.toolbarButton, GUILayout.Width(120f)))
                {
                    ResampleAndSolve();
                }

                if (GUILayout.Button("Reset Preview", EditorStyles.toolbarButton, GUILayout.Width(95f)))
                {
                    ResetPreviewTime();
                }

                GUILayout.FlexibleSpace();
                previewMode = GUILayout.Toggle(previewMode == PreviewMode.Original, "Original", EditorStyles.toolbarButton)
                    ? PreviewMode.Original
                    : previewMode;
                previewMode = GUILayout.Toggle(previewMode == PreviewMode.Solved, "Solved", EditorStyles.toolbarButton)
                    ? PreviewMode.Solved
                    : previewMode;
            }
        }

        private void DrawLeftPanel()
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.Width(340f)))
            {
                showSettings = EditorGUILayout.Foldout(showSettings, "Solver Settings", true);
                if (showSettings)
                {
                    settings.sampleRate = EditorGUILayout.FloatField("Sample Rate", settings.sampleRate);
                    settings.plantVelocityThreshold = EditorGUILayout.FloatField("Plant Velocity", settings.plantVelocityThreshold);
                    settings.plantHeightThreshold = EditorGUILayout.FloatField("Plant Height", settings.plantHeightThreshold);
                    settings.enterFrames = EditorGUILayout.IntField("Enter Frames", settings.enterFrames);
                    settings.exitFrames = EditorGUILayout.IntField("Exit Frames", settings.exitFrames);
                    settings.predictionDecayTime = EditorGUILayout.FloatField("Prediction Decay", settings.predictionDecayTime);
                    settings.lateralDamping = EditorGUILayout.Slider("Lateral Damping", settings.lateralDamping, 0f, 1f);
                    settings.conflictDistanceThreshold = EditorGUILayout.FloatField("Conflict Distance", settings.conflictDistanceThreshold);
                    settings.deltaSmoothing = EditorGUILayout.Slider("Delta Smoothing", settings.deltaSmoothing, 0f, 1f);
                }

                showDebug = EditorGUILayout.Foldout(showDebug, "Debug Metrics", true);
                if (showDebug)
                {
                    if (sampledFrames == null)
                    {
                        EditorGUILayout.HelpBox("No sampled frames.", MessageType.Info);
                    }
                    else
                    {
                        float duration = sampledFrames.Length > 0 ? sampledFrames[sampledFrames.Length - 1].time : 0f;
                        EditorGUILayout.LabelField("Frames", sampledFrames.Length.ToString());
                        EditorGUILayout.LabelField("Duration", duration.ToString("F3") + " s");
                        EditorGUILayout.LabelField("Sample Rate", settings.sampleRate.ToString("F1") + " FPS");
                    }

                    if (solvedResult != null && solvedResult.rootXZ != null && solvedResult.rootXZ.Length > 0)
                    {
                        Vector2 total = solvedResult.rootXZ[solvedResult.rootXZ.Length - 1];
                        float duration = sampledFrames != null && sampledFrames.Length > 1 ? sampledFrames[sampledFrames.Length - 1].time : 0f;
                        float avgSpeed = duration > 1e-4f ? total.magnitude / duration : 0f;
                        int leftPlants = CountPlants(solvedResult.debug.leftPlant);
                        int rightPlants = CountPlants(solvedResult.debug.rightPlant);
                        float predictionRatio = ComputePredictionRatio(solvedResult.debug.usedPrediction);

                        EditorGUILayout.Space(8f);
                        EditorGUILayout.LabelField("Total Root XZ", total.ToString("F3"));
                        EditorGUILayout.LabelField("Average Speed", avgSpeed.ToString("F3") + " m/s");
                        EditorGUILayout.LabelField("Left Plant Frames", leftPlants.ToString());
                        EditorGUILayout.LabelField("Right Plant Frames", rightPlants.ToString());
                        EditorGUILayout.LabelField("Prediction Ratio", (predictionRatio * 100f).ToString("F1") + " %");
                    }
                }

                Rect chartRect = GUILayoutUtility.GetRect(320f, 220f, GUILayout.ExpandWidth(true));
                DrawTopDownChart(chartRect);
            }
        }

        private void DrawPreviewPanel()
        {
            using (new EditorGUILayout.VerticalScope())
            {
                EnsurePreview();
                Rect previewRect = GUILayoutUtility.GetRect(10f, 10000f, 10f, 10000f);
                if (Event.current.type == EventType.Repaint)
                {
                    EditorGUI.DrawRect(previewRect, new Color(0.15f, 0.15f, 0.15f, 1f));
                }

                if (avatarPreview == null)
                {
                    EditorGUI.DropShadowLabel(previewRect, "Assign a clip and humanoid prefab to preview.");
                    return;
                }

                avatarPreview.DoAvatarPreview(previewRect, KimodoPreviewConstants.PreviewBackgroundSolid);
                DrawOverlayGizmos(previewRect);
            }
        }

        private void DrawOverlayGizmos(Rect rect)
        {
            if (Event.current.type != EventType.Repaint || solvedResult == null || sampledFrames == null)
            {
                return;
            }

            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 10f, 240f, 90f), EditorStyles.helpBox);
            GUILayout.Label("Preview Mode: " + previewMode);
            GUILayout.Label("Time: " + GetCurrentTime().ToString("F3"));
            int frameIndex = GetCurrentFrameIndex();
            GUILayout.Label("Frame: " + frameIndex + " / " + Mathf.Max(0, sampledFrames.Length - 1));
            if (frameIndex >= 0 && frameIndex < solvedResult.rootXZ.Length)
            {
                GUILayout.Label("Solved Root XZ: " + solvedResult.rootXZ[frameIndex].ToString("F3"));
            }
            GUILayout.EndArea();
            Handles.EndGUI();
        }

        private void DrawStatus()
        {
            if (!string.IsNullOrWhiteSpace(lastError))
            {
                EditorGUILayout.HelpBox(lastError, MessageType.Error);
            }
            else if (!string.IsNullOrWhiteSpace(lastStatus))
            {
                EditorGUILayout.HelpBox(lastStatus, MessageType.Info);
            }
        }

        private void DrawTopDownChart(Rect rect)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, new Color(0.11f, 0.11f, 0.11f, 1f));
            }

            GUI.Box(rect, GUIContent.none, EditorStyles.helpBox);
            if (sampledFrames == null || sampledFrames.Length == 0)
            {
                EditorGUI.DropShadowLabel(rect, "Top-down XZ chart appears after sampling.");
                return;
            }

            Vector2 min = ToXZ(sampledFrames[0].leftFootWorld);
            Vector2 max = min;
            ExpandBounds(ref min, ref max, sampledFrames[0].rightFootWorld);

            for (int i = 0; i < sampledFrames.Length; i++)
            {
                ExpandBounds(ref min, ref max, sampledFrames[i].leftFootWorld);
                ExpandBounds(ref min, ref max, sampledFrames[i].rightFootWorld);
                if (solvedResult != null && solvedResult.rootXZ != null && i < solvedResult.rootXZ.Length)
                {
                    Vector2 root = solvedResult.rootXZ[i];
                    min = Vector2.Min(min, root);
                    max = Vector2.Max(max, root);
                }
            }

            Vector2 size = max - min;
            if (size.x < 0.001f) size.x = 0.001f;
            if (size.y < 0.001f) size.y = 0.001f;

            Handles.BeginGUI();
            DrawPolyline(rect, sampledFrames, frame => ToXZ(frame.leftFootWorld), min, size, new Color(0.3f, 0.85f, 1f, 1f), 2f);
            DrawPolyline(rect, sampledFrames, frame => ToXZ(frame.rightFootWorld), min, size, new Color(1f, 0.55f, 0.3f, 1f), 2f);

            if (solvedResult != null && solvedResult.rootXZ != null && solvedResult.rootXZ.Length > 0)
            {
                DrawPolyline(rect, solvedResult.rootXZ, point => point, min, size, new Color(0.5f, 1f, 0.45f, 1f), 2.5f);
                DrawPlantAnchors(rect, solvedResult.debug.leftAnchors, solvedResult.debug.leftPlant, min, size, new Color(0.2f, 0.7f, 1f, 0.95f));
                DrawPlantAnchors(rect, solvedResult.debug.rightAnchors, solvedResult.debug.rightPlant, min, size, new Color(1f, 0.45f, 0.25f, 0.95f));
            }

            Handles.color = new Color(1f, 1f, 1f, 0.4f);
            Handles.DrawLine(new Vector3(rect.x + 8f, rect.center.y), new Vector3(rect.xMax - 8f, rect.center.y));
            Handles.DrawLine(new Vector3(rect.center.x, rect.y + 8f), new Vector3(rect.center.x, rect.yMax - 8f));
            Handles.EndGUI();

            GUI.Label(new Rect(rect.x + 8f, rect.y + 6f, 180f, 18f), "XZ Trajectories", EditorStyles.boldLabel);
        }

        private void Sample()
        {
            lastError = string.Empty;
            if (!FootRootMotionSamplingUtility.TrySampleClip(clip, humanoidPrefab, settings, out sampledFrames, out string error))
            {
                lastError = error;
                lastStatus = string.Empty;
                return;
            }

            lastStatus = $"Sampled {sampledFrames.Length} frames.";
            solvedResult = null;
            RebuildPreview();
        }

        private void Solve()
        {
            lastError = string.Empty;
            if (sampledFrames == null || sampledFrames.Length == 0)
            {
                lastError = "No sampled frames available. Run Sample first.";
                return;
            }

            solvedResult = FootRootMotionSolver.Solve(sampledFrames, settings);
            lastStatus = "Solve completed.";
            ApplySolvedPreviewPose();
        }

        private void ResampleAndSolve()
        {
            Sample();
            if (string.IsNullOrEmpty(lastError))
            {
                Solve();
            }
        }

        private void ResetPreviewTime()
        {
            if (avatarPreview?.timeControl != null)
            {
                avatarPreview.timeControl.currentTime = 0f;
                avatarPreview.timeControl.playing = false;
            }

            ApplySolvedPreviewPose();
        }

        private void EnsurePreview()
        {
            if (avatarPreview != null || clip == null || humanoidPrefab == null)
            {
                return;
            }

            RebuildPreview();
        }

        private void RebuildPreview()
        {
            CleanupPreview();
            if (clip == null || humanoidPrefab == null)
            {
                return;
            }

            previewInstance = UnityEngine.Object.Instantiate(humanoidPrefab);
            previewInstance.hideFlags = HideFlags.HideAndDontSave;
            previewAnimator = previewInstance.GetComponentInChildren<Animator>(true);
            if (previewAnimator == null)
            {
                lastError = "Prefab does not contain an Animator.";
                CleanupPreview();
                return;
            }

            avatarPreview = new KimodoAvatarPreview(previewAnimator, clip);
            if (avatarPreview.timeControl != null)
            {
                avatarPreview.timeControl.startTime = 0f;
                avatarPreview.timeControl.stopTime = Mathf.Max(0.001f, clip.length);
                avatarPreview.timeControl.currentTime = 0f;
                avatarPreview.timeControl.playing = false;
            }

            ApplySolvedPreviewPose();
        }

        private void CleanupPreview()
        {
            if (avatarPreview != null)
            {
                avatarPreview.OnDisable();
                avatarPreview.OnDestroy();
                avatarPreview = null;
            }

            if (previewInstance != null)
            {
                UnityEngine.Object.DestroyImmediate(previewInstance);
                previewInstance = null;
            }

            previewAnimator = null;
        }

        private void ApplySolvedPreviewPose()
        {
            if (previewInstance == null || clip == null)
            {
                return;
            }

            float t = GetCurrentTime();
            clip.SampleAnimation(previewInstance, Mathf.Clamp(t, 0f, clip.length));

            if (previewMode != PreviewMode.Solved || solvedResult == null || sampledFrames == null || solvedResult.rootXZ == null || solvedResult.rootXZ.Length == 0)
            {
                return;
            }

            int frameIndex = GetCurrentFrameIndex();
            if (frameIndex < 0 || frameIndex >= solvedResult.rootXZ.Length)
            {
                return;
            }

            Vector3 root = previewInstance.transform.position;
            Vector2 solvedXZ = solvedResult.rootXZ[frameIndex];
            root.x = solvedXZ.x;
            root.z = solvedXZ.y;
            previewInstance.transform.position = root;
        }

        private float GetCurrentTime()
        {
            return avatarPreview?.timeControl != null
                ? Mathf.Clamp(avatarPreview.timeControl.currentTime, 0f, clip != null ? clip.length : 0f)
                : 0f;
        }

        private int GetCurrentFrameIndex()
        {
            if (sampledFrames == null || sampledFrames.Length == 0)
            {
                return 0;
            }

            float t = GetCurrentTime();
            int bestIndex = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < sampledFrames.Length; i++)
            {
                float d = Mathf.Abs(sampledFrames[i].time - t);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static int CountPlants(bool[] mask)
        {
            if (mask == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i])
                {
                    count++;
                }
            }

            return count;
        }

        private static float ComputePredictionRatio(bool[] mask)
        {
            if (mask == null || mask.Length == 0)
            {
                return 0f;
            }

            int count = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i])
                {
                    count++;
                }
            }

            return (float)count / mask.Length;
        }

        private static void ExpandBounds(ref Vector2 min, ref Vector2 max, Vector3 point)
        {
            Vector2 p = ToXZ(point);
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        private static Vector2 ToXZ(Vector3 point)
        {
            return new Vector2(point.x, point.z);
        }

        private static Vector2 ChartPoint(Rect rect, Vector2 point, Vector2 min, Vector2 size)
        {
            const float padding = 16f;
            float x = rect.x + padding + ((point.x - min.x) / size.x) * (rect.width - padding * 2f);
            float y = rect.yMax - padding - ((point.y - min.y) / size.y) * (rect.height - padding * 2f);
            return new Vector2(x, y);
        }

        private static void DrawPolyline(Rect rect, FootRootMotionFrame[] frames, Func<FootRootMotionFrame, Vector2> selector, Vector2 min, Vector2 size, Color color, float width)
        {
            if (frames == null || frames.Length < 2)
            {
                return;
            }

            Handles.color = color;
            Vector3[] points = new Vector3[frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                Vector2 p = ChartPoint(rect, selector(frames[i]), min, size);
                points[i] = new Vector3(p.x, p.y, 0f);
            }
            Handles.DrawAAPolyLine(width, points);
        }

        private static void DrawPolyline(Rect rect, Vector2[] pointsSource, Func<Vector2, Vector2> selector, Vector2 min, Vector2 size, Color color, float width)
        {
            if (pointsSource == null || pointsSource.Length < 2)
            {
                return;
            }

            Handles.color = color;
            Vector3[] points = new Vector3[pointsSource.Length];
            for (int i = 0; i < pointsSource.Length; i++)
            {
                Vector2 p = ChartPoint(rect, selector(pointsSource[i]), min, size);
                points[i] = new Vector3(p.x, p.y, 0f);
            }
            Handles.DrawAAPolyLine(width, points);
        }

        private static void DrawPlantAnchors(Rect rect, Vector2[] anchors, bool[] plantMask, Vector2 min, Vector2 size, Color color)
        {
            if (anchors == null || plantMask == null)
            {
                return;
            }

            Handles.color = color;
            for (int i = 0; i < anchors.Length && i < plantMask.Length; i++)
            {
                if (!plantMask[i])
                {
                    continue;
                }

                Vector2 p = ChartPoint(rect, anchors[i], min, size);
                Rect marker = new Rect(p.x - 2f, p.y - 2f, 4f, 4f);
                EditorGUI.DrawRect(marker, color);
            }
        }
    }
}
