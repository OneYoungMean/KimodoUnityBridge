using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KimodoUnityBridge;
using KimodoBridge;
using TimelineInject;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace KimodoUnityBridge.Command

{
    internal static partial class command_context
    {
        private sealed class AnalysisCaptureLightScope : IDisposable
        {
            private readonly List<Tuple<Light, bool>> restoredLights = new List<Tuple<Light, bool>>();

            private void DisableSceneLights()
            {
                foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
                {
                    if (light == null || light.gameObject == null || !light.gameObject.scene.IsValid() ||
                        EditorUtility.IsPersistent(light))
                    {
                        continue;
                    }

                    if (!restoredLights.Any(item => item.Item1 == light))
                    {
                        restoredLights.Add(Tuple.Create(light, light.enabled));
                    }
                    light.enabled = false;
                }
            }

            public AnalysisCaptureLightScope()
            {
                DisableSceneLights();
            }

            public void Refresh()
            {
                DisableSceneLights();
            }

            public void Dispose()
            {
                foreach (Tuple<Light, bool> item in restoredLights)
                {
                    if (item.Item1 != null) item.Item1.enabled = item.Item2;
                }
                Shader.SetGlobalVector("_KimodoEvidenceKey", Vector4.zero);
                Shader.SetGlobalVector("_KimodoEvidenceFill", Vector4.zero);
                Shader.SetGlobalVector("_KimodoEvidenceRim", Vector4.zero);
                restoredLights.Clear();
            }
        }

        private static Texture2D RenderPictureCanvas(
            IReadOnlyList<SubjectPictureData> subjects,
            IReadOnlyList<PictureTile> tiles,
            PictureLayout layout,
            TrajectoryScale trajectoryScale,
            int tileWidth,
            int tileHeight,
            int supersample,
            out List<RectInt> imageRects)
        {
            int panelHeight = layout.TileRows * tileHeight;
            var images = new Texture2D[tiles.Count];
            using (var captureLights = new AnalysisCaptureLightScope())
            {
                try
                {
                    for (int index = 0; index < tiles.Count; index++)
                    {
                        captureLights.Refresh();
                        images[index] = RenderPictureTileSupersampled(
                            tiles[index], tileWidth, tileHeight, trajectoryScale, supersample);
                        int panel = subjects.ToList().FindIndex(item => ReferenceEquals(item, tiles[index].Subject));
                        int localIndex = tiles.Take(index).Count(item => ReferenceEquals(item.Subject, tiles[index].Subject));
                        DrawTileNumber(
                            images[index],
                            (panel + 1).ToString(CultureInfo.InvariantCulture) + "." +
                            (localIndex + 1).ToString(CultureInfo.InvariantCulture));
                        if (tiles[index].Presentation == "test_pose")
                        {
                            DrawFrameNumber(images[index], tiles[index].Frame);
                        }
                    }

                    imageRects = new List<RectInt>(tiles.Count);
                    var rowWidths = new int[subjects.Count * layout.TileRows];
                    for (int index = 0; index < tiles.Count; index++)
                    {
                        int panel = subjects.ToList().FindIndex(item => ReferenceEquals(item, tiles[index].Subject));
                        int row = layout.TileRows == 2 && IsHighFootPose(tiles[index]) ? 0 : layout.TileRows - 1;
                        int rowIndex = panel * layout.TileRows + row;
                        int x = rowWidths[rowIndex];
                        rowWidths[rowIndex] += images[index].width;
                        imageRects.Add(new RectInt(
                            x,
                            (subjects.Count - panel - 1) * panelHeight + row * tileHeight,
                            images[index].width,
                            images[index].height));
                    }
                    int canvasWidth = Math.Max(1, rowWidths.DefaultIfEmpty(1).Max());
                    if (canvasWidth > SystemInfo.maxTextureSize)
                    {
                        throw new InvalidOperationException($"Analysis picture width {canvasWidth} exceeds Unity's maximum texture width {SystemInfo.maxTextureSize}.");
                    }

                    var canvas = new Texture2D(canvasWidth, panelHeight * subjects.Count, TextureFormat.RGBA32, false);
                    Fill(canvas, new Color(.12f, .12f, .12f, 1f));
                    for (int index = 0; index < tiles.Count; index++)
                    {
                        RectInt rect = imageRects[index];
                        canvas.SetPixels(rect.x, rect.y, rect.width, rect.height, images[index].GetPixels());
                    }
                    DrawPictureGrid(canvas, imageRects, subjects.Count, panelHeight, layout.TileRows);
                    canvas.Apply(false, false);
                    return canvas;
                }
                finally
                {
                    foreach (Texture2D image in images)
                    {
                        if (image != null) UnityEngine.Object.DestroyImmediate(image);
                    }
                }
            }
        }

        private static Texture2D RenderPictureTile(PictureTile tile, int width, int height, TrajectoryScale trajectoryScale)
        {
            if (tile.Presentation == "test_empty_pose")
            {
                return CreateEmptyTestTile(width, height);
            }
            if (tile.Presentation.StartsWith("test_overview_", StringComparison.Ordinal))
            {
                string type = tile.TestTileType ?? string.Empty;
                if (type == "3d_track") return RenderRoot2DPictureTile(PictureTile.TestRoot2D(tile.Subject, tile.Direction), width, height);
                if (type == "height_time_track") return RenderTestHeightTimeTile(tile, width, height);
                if (type == "3d_ghost" || type == "3d_ghost_track") return RenderTestPictureTile(tile, width, height, trajectoryScale);
                PictureTile mapped = type == "3d_ghost"
                    ? PictureTile.TestKeyframes(tile.Subject, tile.Direction)
                    : type == "3d_ghost_track"
                        ? PictureTile.TestFootTransitions(tile.Subject, tile.Direction)
                        : PictureTile.TestRoot2D(tile.Subject, tile.Direction);
                return RenderPictureTile(mapped, width, height, trajectoryScale);
            }
            if (tile.Presentation == "test_selected_3d_ghost" || tile.Presentation == "test_selected_3d_ghost_track")
            {
                return RenderTestPictureTile(tile, width, height, trajectoryScale);
            }
            if (tile.Presentation == "test_root2d")
            {
                return RenderRoot2DPictureTile(tile, width, height);
            }
            if (tile.Presentation == "mesh_pose")
            {
                Bounds meshBounds = CalculatePreviewPoseBounds(tile.Subject, tile.Frame);
                var meshEnvironment = new List<GameObject>();
                CreatePictureEnvironment(meshEnvironment, meshBounds);
                Camera meshCamera = CreateAnalysisPictureCamera(meshBounds, tile.Direction, true);
                try
                {
                    Texture2D result = RenderCamera(meshCamera, width, height, new Color(.12f, .12f, .12f, 1f));
                    RenderPoseOnto(result, meshCamera, meshEnvironment, tile.Subject, tile.Frame, 1f);
                    return result;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(meshCamera.gameObject);
                    foreach (GameObject item in meshEnvironment)
                    {
                        if (item != null) UnityEngine.Object.DestroyImmediate(item);
                    }
                }
            }
            if (tile.Presentation == "test_foot_transitions" || tile.Presentation == "test_keyframes")
            {
                return RenderTestPictureTile(tile, width, height, trajectoryScale);
            }
            if (tile.Presentation == "test_pose")
            {
                return RenderTestPoseTile(tile, width, height);
            }

            int size = width;

            Bounds tileBounds = tile.Presentation == "key" || tile.Presentation == "foot_contact" || tile.Presentation == "foot_fallback"
                ? CalculatePreviewPoseBounds(tile.Subject, tile.Frame)
                : tile.Subject.Bounds;
            var environment = new List<GameObject>();
            CreatePictureEnvironment(environment, tileBounds);
            Camera camera = CreateAnalysisPictureCamera(tileBounds, tile.Direction, tile.Orthographic);
            try
            {
                Texture2D result = null;
                if (tile.Presentation == "ghost")
                {
                    List<int> frames = BuildGhostFrames(tile.Subject, out HashSet<int> promotedFrames);
                    bool separated = !tile.Subject.FirstBounds.Intersects(tile.Subject.LastBounds);
                    var poses = new List<TestVirtualPose>();
                    for (int index = 0; index < frames.Count; index++)
                    {
                        int frame = frames[index];
                        float alpha = GhostAlpha(index, frames.Count, separated);
                        if (promotedFrames.Contains(frame))
                        {
                            alpha = Mathf.Min(MaxPromotedGhostAlpha, alpha + StationaryTrajectoryAlphaBoost);
                        }
                        poses.Add(CreateGhostVirtualPose(tile.Subject, frame, ResolveGhostPoseTint(tile.Subject, frame), alpha));
                    }
                    try { result = RenderGpuPoseLayers(camera, environment, poses, size, size,
                        new Color(.12f, .12f, .12f, 1f), false); }
                    finally { foreach (TestVirtualPose pose in poses) pose.Dispose(); }
                }
                else if (tile.Presentation == "key" || tile.Presentation == "foot_contact" || tile.Presentation == "foot_fallback")
                {
                    result = RenderCamera(camera, size, new Color(.12f, .12f, .12f, 1f));
                    RenderPoseOnto(result, camera, environment, tile.Subject, tile.Frame, 1f);
                }
                return result;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
                foreach (GameObject item in environment)
                {
                    if (item != null) UnityEngine.Object.DestroyImmediate(item);
                }
            }
        }

        private static Texture2D RenderTestHeightTimeTile(PictureTile tile, int width, int height)
        {
            SubjectPictureData subject = tile.Subject;
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            float aspect = width / (float)Mathf.Max(1, height);
            List<int> poseFrames = subject.KeyFrameSet
                .Append(0)
                .Append(lastFrame)
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();
            float length = CalculateHeightTimeLength(subject, poseFrames, aspect);
            var poseTargets = new Dictionary<int, Vector3>();
            var curvePoints = new List<Vector3>(Math.Max(1, subject.Pelvis.Length));
            for (int frame = 0; frame <= lastFrame; frame++)
            {
                float z = lastFrame == 0 ? 0f : frame / (float)lastFrame * length;
                curvePoints.Add(new Vector3(0f, subject.Pelvis[frame].y, z));
            }

            using (TestPosePlan posePlan = BuildTestPosePlan(subject, poseFrames))
            {
                Bounds contentBounds = new Bounds(curvePoints.Count > 0 ? curvePoints[0] : Vector3.zero, Vector3.zero);
                foreach (Vector3 point in curvePoints) contentBounds.Encapsulate(point);
                for (int index = 0; index < poseFrames.Count; index++)
                {
                    int frame = poseFrames[index];
                    Vector3 target = curvePoints[Mathf.Clamp(frame, 0, curvePoints.Count - 1)];
                    // The preview root can be offset from Hips. Move the root
                    // by that stable offset so the rendered Hips lands on the
                    // same diagnostic point as the green curve.
                    poseTargets[frame] = target + posePlan.Get(frame).RootPosition - subject.Pelvis[frame];
                    Bounds poseBounds = CalculateRawPreviewPoseBounds(subject, frame);
                    poseBounds.center += poseTargets[frame] - posePlan.Get(frame).RootPosition;
                    contentBounds.Encapsulate(poseBounds.min);
                    contentBounds.Encapsulate(poseBounds.max);
                }

                float minY = contentBounds.min.y;
                float maxY = contentBounds.max.y;
                float axisX = contentBounds.min.x;
                Color gridColor = new Color(.55f, .62f, .68f, .45f);
                var environment = new List<GameObject>();
                CreateEvidenceLights(environment, contentBounds.center);
                for (int index = 0; index <= 5; index++)
                {
                    float z = length * index / 5f;
                    bool edge = index == 0 || index == 5;
                    CreateWorldLine(environment, new Vector3(axisX, minY, z), new Vector3(axisX, maxY, z), edge ? .012f : .006f, gridColor, true);
                }
                const int horizontalDivisions = 5;
                for (int index = 0; index <= horizontalDivisions; index++)
                {
                    float y = Mathf.Lerp(minY, maxY, index / (float)horizontalDivisions);
                    bool edge = index == 0 || index == horizontalDivisions;
                    CreateWorldLine(environment, new Vector3(axisX, y, 0f), new Vector3(axisX, y, length), edge ? .016f : .006f, gridColor, true);
                }
                CreateDiagnosticLine(environment, curvePoints, new Color(.15f, .9f, .25f, 1f), .025f);
                CreateWorldLine(environment, new Vector3(axisX, minY, 0f), new Vector3(axisX, minY, length), .022f, Color.white, true);
                CreateWorldLine(environment, new Vector3(axisX, minY, 0f), new Vector3(axisX, maxY, 0f), .022f, Color.white, true);
                float diagnosticMargin = Mathf.Max(.05f, contentBounds.size.magnitude * .03f);
                contentBounds.Expand(Vector3.one * diagnosticMargin);

                var poses = new List<TestVirtualPose>(poseFrames.Count);
                bool separated = !subject.FirstBounds.Intersects(subject.LastBounds);
                for (int index = 0; index < poseFrames.Count; index++)
                {
                    int frame = poseFrames[index];
                    float alpha = GhostAlpha(index, poseFrames.Count, separated);
                    poses.Add(CreateGhostVirtualPose(
                        posePlan.Get(frame),
                        ResolveGhostPoseTint(subject, frame),
                        alpha,
                        poseTargets[frame]));
                }

                Camera camera = CreateTestAnalysisPictureCamera(
                    contentBounds,
                    Vector3.right,
                    aspect,
                    0f);
                try
                {
                    return RenderTestPoseLayers(camera, environment, poses, width, height, new Color(.12f, .12f, .12f, 1f));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(camera.gameObject);
                    foreach (TestVirtualPose pose in poses) pose.Dispose();
                    foreach (GameObject item in environment) if (item != null) UnityEngine.Object.DestroyImmediate(item);
                }
            }
        }

        private static float CalculateHeightTimeLength(
            SubjectPictureData subject,
            IReadOnlyList<int> poseFrames,
            float aspect)
        {
            if (subject == null || poseFrames == null || poseFrames.Count == 0) return 1f;
            var poses = new List<Bounds>(poseFrames.Count);
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            for (int index = 0; index < poseFrames.Count; index++)
            {
                int frame = Mathf.Clamp(poseFrames[index], 0, Math.Max(0, subject.Pelvis.Length - 1));
                Bounds pose = CalculateRawPreviewPoseBounds(subject, frame);
                Vector3 hips = subject.Pelvis[frame];
                pose.center += new Vector3(-hips.x, 0f, -hips.z);
                poses.Add(pose);
                minY = Mathf.Min(minY, pose.min.y);
                maxY = Mathf.Max(maxY, pose.max.y);
            }

            if (float.IsNaN(minY) || float.IsInfinity(minY) ||
                float.IsNaN(maxY) || float.IsInfinity(maxY)) return 1f;
            float targetWidth = Mathf.Max(.001f, maxY - minY) * Mathf.Max(.001f, aspect);
            float widthAtLength = 0f;
            for (int index = 0; index < poses.Count; index++)
            {
                widthAtLength = Mathf.Max(widthAtLength, poses[index].max.z);
            }
            float minimumLength = CalculateHeightTimeSeparationLength(
                poses, poseFrames, Math.Max(1, subject.Pelvis.Length - 1), HeightTimePoseGapMeters);

            Func<float, float> horizontalWidth = length =>
            {
                float minZ = float.PositiveInfinity;
                float maxZ = float.NegativeInfinity;
                for (int index = 0; index < poses.Count; index++)
                {
                    float u = subject.Pelvis.Length <= 1 ? 0f :
                        Mathf.Clamp(poseFrames[index], 0, subject.Pelvis.Length - 1) /
                        (float)(subject.Pelvis.Length - 1);
                    minZ = Mathf.Min(minZ, u * length + poses[index].min.z);
                    maxZ = Mathf.Max(maxZ, u * length + poses[index].max.z);
                }
                return maxZ - minZ;
            };

            float upper = Mathf.Max(1f, targetWidth + widthAtLength - poses[0].min.z);
            while (horizontalWidth(upper) < targetWidth && upper < 100000f) upper *= 2f;
            if (horizontalWidth(0f) >= targetWidth) return Mathf.Max(.001f, minimumLength);
            float lower = 0f;
            for (int iteration = 0; iteration < 32; iteration++)
            {
                float middle = (lower + upper) * .5f;
                if (horizontalWidth(middle) < targetWidth) lower = middle;
                else upper = middle;
            }
            return Mathf.Max(.001f, upper, minimumLength);
        }

        private static Texture2D ComposePictureCanvasGpu(
            IReadOnlyList<Texture2D> tiles, IReadOnlyList<RectInt> rects, int width, int height)
        {
            ComputeShader compositor = Resources.Load<ComputeShader>("KimodoPictureCanvasComposite");
            if (compositor == null) throw new InvalidOperationException("KimodoPictureCanvasComposite compute shader is unavailable.");
            if (tiles == null || rects == null || tiles.Count != rects.Count || tiles.Count > 20)
                throw new ArgumentException("Picture canvas tile and rect counts must match and fit the GPU compositor.");
            int kernel = compositor.FindKernel("CompositeTiles");
            var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0)
            {
                enableRandomWrite = true,
                sRGB = QualitySettings.activeColorSpace == ColorSpace.Linear
            };
            var target = new RenderTexture(descriptor)
            {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            target.Create();
            try
            {
                compositor.SetInt("_Width", width);
                compositor.SetInt("_Height", height);
                compositor.SetInt("_TileCount", tiles.Count);
                compositor.SetInt("_HeaderHeight", 60);
                compositor.SetVectorArray("_Rects", rects.Select(rect => new Vector4(rect.x, rect.y, rect.width, rect.height)).ToArray());
                compositor.SetVectorArray("_Labels", rects.Select((_, index) => new Vector4(
                    index < 4 ? 1 : index < 12 ? 2 : 3,
                    index < 4 ? index + 1 : index < 12 ? index - 3 : index - 11, 0f, 0f)).ToArray());
                for (int index = 0; index < tiles.Count; index++)
                    compositor.SetTexture(kernel, "_Tile" + index.ToString(CultureInfo.InvariantCulture), tiles[index]);
                compositor.SetTexture(kernel, "_Canvas", target);
                compositor.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1);
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = target;
                try
                {
                    var result = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    result.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    result.Apply(false, false);
                    return result;
                }
                finally { RenderTexture.active = previous; }
            }
            finally
            {
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static float CalculateHeightTimeSeparationLength(
            IReadOnlyList<Bounds> poses, IReadOnlyList<int> frames, int lastFrame, float gap)
        {
            float length = 0f;
            for (int right = 1; right < poses.Count; right++)
            for (int left = 0; left < right; left++)
            {
                int frameGap = frames[right] - frames[left];
                if (frameGap <= 0) continue;
                length = Mathf.Max(length,
                    (poses[left].max.z - poses[right].min.z + gap) * lastFrame / frameGap);
            }
            return length;
        }

        private static void CreateDiagnosticLine(List<GameObject> objects, IReadOnlyList<Vector3> points, Color color, float width)
        {
            if (points == null || points.Count < 2) return;
            GameObject lineObject = MoveToAnalysisSessionRoot(
                new GameObject("Kimodo Height Time Hip Curve") { hideFlags = HideFlags.HideAndDontSave });
            SetLayerRecursively(lineObject, SessionCaptureLayer);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = points.Count;
            line.SetPositions(points.ToArray());
            line.startWidth = line.endWidth = width;
            line.useWorldSpace = true;
            line.sharedMaterial = MakeUnlitMaterial(color);
            line.startColor = line.endColor = color;
            objects.Add(lineObject);
        }

        private static Color TestSpeedColor(float speed)
        {
            float index = speed <= .01f ? 0f : speed >= 10f ? 1f : speed <= 2f ? .5f * (speed - .01f) / 1.99f : .5f + .5f * (speed - 2f) / 8f;
            return Color.Lerp(Color.green, Color.red, Mathf.Clamp01(index));
        }

        private static void CreateSpeedTrajectoryLine(List<GameObject> objects, IReadOnlyList<Vector3> points, float width)
        {
            if (points == null || points.Count < 2) return;
            GameObject lineObject = MoveToAnalysisSessionRoot(
                new GameObject("Kimodo Evidence Speed Trajectory") { hideFlags = HideFlags.HideAndDontSave });
            SetLayerRecursively(lineObject, SessionCaptureLayer);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = points.Count;
            line.SetPositions(points.ToArray());
            line.startWidth = line.endWidth = width;
            line.useWorldSpace = true;
            Gradient gradient = new Gradient();
            // Unity's Gradient supports at most 8 color keys; downsample the
            // per-frame speed samples onto 8 evenly spaced keys to stay in budget.
            const int MaxGradientKeys = 8;
            var colors = new GradientColorKey[MaxGradientKeys];
            var alphas = new GradientAlphaKey[MaxGradientKeys];
            for (int index = 0; index < MaxGradientKeys; index++)
            {
                int sample = Mathf.RoundToInt(index * (points.Count - 1) / (float)(MaxGradientKeys - 1));
                float speed = sample == 0
                    ? Vector3.Distance(points[1], points[0]) * (float)SessionFrameRate
                    : Vector3.Distance(points[sample], points[sample - 1]) * (float)SessionFrameRate;
                float time = sample / (float)(points.Count - 1);
                colors[index] = new GradientColorKey(TestSpeedColor(speed), time);
                alphas[index] = new GradientAlphaKey(1f, time);
            }
            gradient.SetKeys(colors, alphas);
            line.colorGradient = gradient;
            line.startColor = colors[0].color;
            line.endColor = colors[colors.Length - 1].color;
            // The speed gradient reaches the mesh as VERTEX colors; URP/Unlit
            // ignores those and would paint the line in its base color. Use a
            // vertex-color-aware shader so the gradient actually shows.
            line.sharedMaterial = new Material(Shader.Find("Sprites/Default"))
            {
                hideFlags = HideFlags.HideAndDontSave,
                color = Color.white
            };
            objects.Add(lineObject);
        }

        private static void RenderPoseOnto(
            Texture2D destination,
            Camera camera,
            IReadOnlyList<GameObject> environment,
            SubjectPictureData subject,
            int localFrame,
            float alpha)
        {
            EvaluatedPosePreview preview = CreateAnalysisPosePreview(subject, localFrame);
            var transientMaterials = new List<Material>();
            var originalMaterials = new List<Tuple<Renderer, Material[]>>();
            try
            {
                ApplyPoseMaterials(preview.Root, Color.white, false, transientMaterials, originalMaterials);
                SetEvidenceVisualsEnabled(environment, false);
                Texture2D layer = RenderCamera(camera, destination.width, new Color(0f, 0f, 0f, 0f));
                try
                {
                    Composite(destination, layer, alpha);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(layer);
                    SetEvidenceVisualsEnabled(environment, true);
                }
            }
            finally
            {
                RestoreAnalysisMaterials(originalMaterials);
                foreach (Material material in transientMaterials)
                {
                    if (material != null) UnityEngine.Object.DestroyImmediate(material);
                }
                preview.Dispose();
            }
        }

        private static Texture2D RenderTestPictureTile(PictureTile tile, int width, int height, TrajectoryScale trajectoryScale)
        {
            int lastFrame = Math.Max(0, tile.Subject.Pelvis.Length - 1);
            var requestedFrames = tile.TrajectoryFrames;
            requestedFrames = requestedFrames
                .Concat(new[] { 0, lastFrame })
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();
            // BuildSubjectPictureData already sampled every frame. Reuse those
            // canonical poses for both trajectory points and ghost snapshots so
            // the renderer never has a second AnimationClip sampling path.
            using (TestPosePlan posePlan = BuildTestPosePlan(tile.Subject, requestedFrames))
            {
                var virtualPoses = new List<TestVirtualPose>();
                bool eventOnly = tile.Presentation == "test_selected_3d_ghost_track";
                if (tile.Presentation == "test_foot_transitions" || tile.Presentation == "test_keyframes" ||
                    tile.Presentation == "test_selected_3d_ghost" || tile.Presentation == "test_selected_3d_ghost_track")
                {
                    List<int> frames = tile.TrajectoryFrames;
                    bool separated = !tile.Subject.FirstBounds.Intersects(tile.Subject.LastBounds);
                    for (int index = 0; index < frames.Count; index++)
                    {
                        int frame = frames[index];
                        if (!eventOnly && (frame == 0 || frame == lastFrame)) continue;
                        Color tint = ResolveTestPoseTint(tile, frame, out bool keyframe, out bool footTransition);
                        float alpha = Mathf.Clamp(
                            GhostAlpha(index, frames.Count, separated),
                            TestGhostAlphaMin,
                            TestGhostAlphaMax);
                        if (keyframe || footTransition) alpha = .96f;
                        if (!keyframe && !footTransition && tile.StationaryBoostFrames.Contains(frame))
                        {
                            alpha = Mathf.Min(MaxPromotedGhostAlpha, alpha + StationaryTrajectoryAlphaBoost);
                        }
                        alpha = Mathf.Clamp01(alpha);
                        virtualPoses.Add(CreateTestVirtualPose(
                            posePlan.Get(frame), tint, alpha));
                    }
                }
                if (!eventOnly)
                {
                    Color startTint = ResolveTestPoseTint(tile, 0, out _, out _);
                    Color endTint = ResolveTestPoseTint(tile, lastFrame, out _, out _);
                    virtualPoses.Add(CreateTestVirtualPose(posePlan.Get(0), startTint, 1f));
                    virtualPoses.Add(CreateTestVirtualPose(posePlan.Get(lastFrame), endTint, 1f));
                }

                Bounds contentBounds = CalculateTestContentBounds(tile.Subject);
                Bounds tileBounds = IncludeGroundInBounds(contentBounds);
                var environment = new List<GameObject>();
                CreateTestPictureEnvironment(environment, tileBounds);
                if (tile.ShowTestTrajectories)
                {
                    CreateTestBodyTrajectories(environment, tile.Subject);
                }

                Camera camera = CreateTestAnalysisPictureCamera(
                    contentBounds,
                    tile.Subject,
                    tile.Direction,
                    (float)width / Mathf.Max(1, height));
                try
                {
                    return RenderTestPoseLayers(
                        camera,
                        environment,
                        virtualPoses,
                        width,
                        height,
                        new Color(.12f, .12f, .12f, 1f));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(camera.gameObject);
                    foreach (TestVirtualPose pose in virtualPoses)
                    {
                        pose.Dispose();
                    }
                    foreach (GameObject item in environment)
                    {
                        if (item != null) UnityEngine.Object.DestroyImmediate(item);
                    }
                }
            }
        }

        private static Texture2D RenderPictureTileSupersampled(
            PictureTile tile,
            int targetWidth,
            int targetHeight,
            TrajectoryScale trajectoryScale,
            int supersample)
        {
            int scale = Mathf.Max(1, supersample);
            if (scale == 1)
            {
                return RenderPictureTile(tile, targetWidth, targetHeight, trajectoryScale);
            }

            if (tile.Presentation == "test_pose")
            {
                // The final tile rect is authoritative. Render the pose with
                // that aspect so the orthographic camera fits its OBB into the
                // target viewport; never stretch a rendered character image.
                Texture2D source = RenderTestPoseTile(tile, targetWidth * scale, targetHeight * scale);
                try
                {
                    return ResizeTexture(source, targetWidth, targetHeight);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(source);
                }
            }

            Texture2D highResolution = RenderPictureTile(
                tile,
                Mathf.Max(1, targetWidth * scale),
                Mathf.Max(1, targetHeight * scale),
                trajectoryScale);
            try
            {
                return ResizeTexture(highResolution, targetWidth, targetHeight);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(highResolution);
            }
        }

        private static Texture2D RenderTestPoseTile(PictureTile tile, int targetWidth, int targetHeight)
        {
            int frame = Mathf.Clamp(tile.Frame, 0, Math.Max(0, tile.Subject.Pelvis.Length - 1));
            Vector3[] viewPoints =
            {
                tile.Subject.Pelvis[frame],
                tile.Subject.LeftHand[frame],
                tile.Subject.RightHand[frame],
                tile.Subject.LeftElbow[frame],
                tile.Subject.RightElbow[frame],
                tile.Subject.LeftFoot[frame],
                tile.Subject.RightFoot[frame],
                tile.Subject.LeftKnee[frame],
                tile.Subject.RightKnee[frame],
                tile.Subject.Head[frame]
            };
            KimodoMarkerSampleResult sampledSample = tile.Subject.GetSample(frame);
            sampledSample.sampleData.GetRoot(out _, out Quaternion sampledRootRotation);
            viewPoints = ExpandPosePointsAwayFromHipsInCameraSpace(
                viewPoints,
                tile.Direction,
                sampledRootRotation * Vector3.forward);
            CalculateTestViewExtents(viewPoints, tile.Direction, out _, out float horizontal, out float vertical, out _);
            float aspect = targetWidth / (float)Mathf.Max(1, targetHeight);
            // Render at 2x the target tile resolution for edge quality, with a
            // reasonable upper bound that scales with the requested resolution.
            // The max is 4x the target to handle high-resolution analysis pictures
            // (e.g., 1920 → 710px tiles → 2840px max render, not capped at 1024).
            int sourceHeight = Mathf.Clamp(Mathf.Max(256, targetHeight * 2), 256, targetHeight * 4);
            int sourceWidth = Math.Max(1, Mathf.RoundToInt(sourceHeight * aspect));
            using (TestPosePlan posePlan = BuildTestPosePlan(tile.Subject, new[] { frame }))
            {
                TestVirtualPose pose = CreateTestVirtualPose(
                    posePlan.Get(frame),
                    ResolveSingleTestPoseTint(tile, frame),
                    1f);
                try
                {
                    Bounds contentBounds = new Bounds(viewPoints[0], Vector3.zero);
                    foreach (Vector3 point in viewPoints) contentBounds.Encapsulate(point);
                    Bounds tileBounds = IncludeGroundInBounds(contentBounds);
                    var environment = new List<GameObject>();
                    CreateTestPictureEnvironment(environment, tileBounds);
                    Camera camera = CreateTestAnalysisPictureCamera(viewPoints, tile.Direction, aspect);
                    try
                    {
                        Texture2D source = RenderTestPoseLayers(
                            camera,
                            environment,
                            new[] { pose },
                            sourceWidth,
                            sourceHeight,
                            new Color(.12f, .12f, .12f, 1f));
                        try
                        {
                            return ResizeTexture(source, targetWidth, targetHeight);
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(source);
                        }
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(camera.gameObject);
                        foreach (GameObject item in environment)
                        {
                            if (item != null) UnityEngine.Object.DestroyImmediate(item);
                        }
                    }
                }
                finally
                {
                    pose.Dispose();
                }
            }
        }

        private static Texture2D RenderTestPoseLayers(
            Camera camera,
            IReadOnlyList<GameObject> environment,
            IReadOnlyList<TestVirtualPose> poses,
            int width,
            int height,
            Color background)
        {
            return RenderGpuPoseLayers(camera, environment, poses, width, height, background, true);
        }

        private static Texture2D RenderGpuPoseLayers(
            Camera camera,
            IReadOnlyList<GameObject> environment,
            IReadOnlyList<TestVirtualPose> poses,
            int width,
            int height,
            Color background,
            bool includeTrajectories)
        {
            ComputeShader composite = Resources.Load<ComputeShader>("KimodoPoseDepthComposite");
            if (composite == null) throw new InvalidOperationException("KimodoPoseDepthComposite compute shader is unavailable.");
            Shader depthShader = Shader.Find("Hidden/Kimodo/PoseDepthEncode")
                ?? throw new InvalidOperationException("Pose depth encoder shader is unavailable.");
            RenderTexture accumulationColor = NewAnalysisRenderTexture(width, height, RenderTextureFormat.ARGB32, true);
            RenderTexture accumulationDepth = NewAnalysisRenderTexture(width, height, RenderTextureFormat.RFloat, true);
            RenderTexture baseLayer = null;
            RenderTexture layer = null;
            RenderTexture depth = null;
            int groupsX = (width + 7) / 8;
            int groupsY = (height + 7) / 8;
            int initKernel = composite.FindKernel("InitDepth");
            int poseKernel = composite.FindKernel("CompositePose");
            int blendKernel = composite.FindKernel("BlendLayer");
            int seedKernel = composite.FindKernel("SeedBase");
            try
            {
                SetEvidenceVisualsEnabled(environment, true);
                baseLayer = RenderCameraToTexture(camera, width, height, background, RenderTextureFormat.ARGB32, false);
                composite.SetInt("_Width", width); composite.SetInt("_Height", height);
                composite.SetInt("_ReversedZ", SystemInfo.usesReversedZBuffer ? 1 : 0);
                // Seed the accumulation from the base layer through the UAV instead
                // of Graphics.CopyTexture, which left the texture in a state where
                // every later UAV write was ignored.
                composite.SetTexture(seedKernel, "_BaseColor", baseLayer);
                composite.SetTexture(seedKernel, "_AccumColor", accumulationColor);
                composite.Dispatch(seedKernel, groupsX, groupsY, 1);
                composite.SetTexture(initKernel, "_AccumDepth", accumulationDepth);
                composite.Dispatch(initKernel, groupsX, groupsY, 1);

                SetEvidenceVisualsEnabled(environment, false);
                foreach (TestVirtualPose pose in poses)
                {
                    Vector3 previousPosition = pose.Preview != null ? pose.Preview.transform.position : Vector3.zero;
                    bool moved = pose.HasTargetPosition && pose.Preview != null;
                    if (moved) pose.Preview.transform.position = pose.TargetPosition;
                    SetPreviewRenderersEnabled(pose.Preview, true);
                    try
                    {
                        layer = RenderCameraToTexture(camera, width, height, Color.clear, RenderTextureFormat.ARGB32, false);
                        depth = RenderCameraDepthToTexture(
                            camera, depthShader, width, height, new[] { pose.Preview });
                        // Default materials stay opaque. The compositor owns
                        // ghost opacity for every pose so Lit never needs a
                        // transparent material variant.
                        composite.SetFloat("_PoseAlpha", pose.Alpha);
                        composite.SetTexture(poseKernel, "_PoseColor", layer);
                        composite.SetTexture(poseKernel, "_PoseDepth", depth);
                        composite.SetTexture(poseKernel, "_BaseColor", baseLayer);
                        composite.SetTexture(poseKernel, "_AccumColor", accumulationColor);
                        composite.SetTexture(poseKernel, "_AccumDepth", accumulationDepth);
                        composite.Dispatch(poseKernel, groupsX, groupsY, 1);
                        DestroyAnalysisRenderTexture(layer); layer = null;
                        DestroyAnalysisRenderTexture(depth); depth = null;
                    }
                    finally
                    {
                        SetPreviewRenderersEnabled(pose.Preview, false);
                        if (moved) pose.Preview.transform.position = previousPosition;
                    }
                }

                if (includeTrajectories)
                {
                    SetEvidenceVisualsEnabled(environment, false);
                    foreach (GameObject item in environment)
                    {
                        if (item == null) continue;
                        foreach (LineRenderer line in item.GetComponentsInChildren<LineRenderer>(true)) line.enabled = true;
                    }
                    layer = RenderCameraToTexture(camera, width, height, Color.clear, RenderTextureFormat.ARGB32, false);
                    // The trajectory layer needs its own depth buffer: the colour
                    // layer's alpha carries no silhouette on pipelines that clear
                    // to an opaque background.
                    depth = RenderCameraDepthToTexture(camera, depthShader, width, height, environment);
                    composite.SetTexture(blendKernel, "_LayerColor", layer);
                    composite.SetTexture(blendKernel, "_LayerDepth", depth);
                    composite.SetTexture(blendKernel, "_AccumColor", accumulationColor);
                    composite.SetInt("_UseLayerDepth", 1);
                    composite.Dispatch(blendKernel, groupsX, groupsY, 1);
                    DestroyAnalysisRenderTexture(layer); layer = null;
                    DestroyAnalysisRenderTexture(depth); depth = null;
                }
                return ReadRenderTexture(accumulationColor, width, height);
            }
            finally
            {
                DestroyAnalysisRenderTexture(layer);
                DestroyAnalysisRenderTexture(depth);
                DestroyAnalysisRenderTexture(baseLayer);
                DestroyAnalysisRenderTexture(accumulationColor);
                DestroyAnalysisRenderTexture(accumulationDepth);
                camera.targetTexture = null;
                SetEvidenceVisualsEnabled(environment, true);
            }
        }

        private static void DestroyAnalysisRenderTexture(RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
        }

        private static RenderTexture NewAnalysisRenderTexture(int width, int height, RenderTextureFormat format, bool randomWrite, int depthBitsOverride = -1)
        {
            int depthBits = depthBitsOverride >= 0 ? depthBitsOverride : (format == RenderTextureFormat.ARGB32 ? 24 : 0);
            // These textures stay alive side by side for the whole composite (base
            // layer, pose layer, depth, accumulation colour/depth), so they must not
            // come from Unity's temporary pool: RenderTexture.GetTemporary followed by
            // Release() returns the texture to the pool, and the next GetTemporary can
            // hand the very same instance back while it is still in use. That aliasing
            // made the pose layer overwrite the base layer and the compositor read and
            // write the same resource. Allocate a dedicated texture instead.
            var texture = new RenderTexture(width, height, depthBits, format)
            {
                enableRandomWrite = randomWrite,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.Create();
            return texture;
        }

        private static Type FindLoadedType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(type => type != null);
        }

        private static RenderTexture RenderCameraToTexture(Camera camera, int width, int height, Color background, RenderTextureFormat format, bool randomWrite)
        {
            // Analysis layers must be captured through a plain camera target
            // texture. The transient analysis camera must render directly into
            // this target so the compositor receives the actual color layer.
            RenderTexture target = NewAnalysisRenderTexture(width, height, format, randomWrite);
            RenderTexture previous = RenderTexture.active;
            try
            {
                // HDRP may leave transient target contents intact when rendering
                // an isolated camera. Clear explicitly, then preserve that clear
                // while the camera draws its geometry.
                RenderTexture.active = target;
                GL.Clear(true, true, background, 1f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                SetHdrpBackgroundColor(camera, background);
                camera.targetTexture = target;
                camera.Render();
                camera.targetTexture = null;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
            }
            return target;
        }

        private static RenderTexture RenderCameraDepthToTexture(
            Camera camera,
            Shader depthShader,
            int width,
            int height,
            IReadOnlyList<GameObject> renderObjects)
        {
            // Keep the hardware-depth encoding in step with the compositor's
            // _ReversedZ convention; the HDRP DepthStencil AOV is not the same
            // quantity.
            RenderTexture target = NewAnalysisRenderTexture(width, height, RenderTextureFormat.ARGBFloat, false, 24);
            var replaced = new List<Tuple<Renderer, Material[]>>();
            var replacements = new List<Material>();
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                GL.Clear(true, true, Color.clear, 1f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                SetHdrpBackgroundColor(camera, Color.clear);
                // SRP cameras do not reliably honour Camera.RenderWithShader. Replace
                // the actual materials instead, then use the normal camera.Render()
                // path so HDRP draws the pose into this auxiliary depth texture.
                if (renderObjects != null)
                {
                    foreach (GameObject root in renderObjects)
                    {
                        if (root == null) continue;
                        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                        {
                            if (renderer == null || !renderer.enabled) continue;
                            Material[] original = renderer.sharedMaterials;
                            Material[] replacement = new Material[Mathf.Max(1, original.Length)];
                            for (int index = 0; index < replacement.Length; index++)
                            {
                                Material material = new Material(depthShader) { hideFlags = HideFlags.HideAndDontSave };
                                replacement[index] = material;
                                replacements.Add(material);
                            }
                            replaced.Add(Tuple.Create(renderer, original));
                            renderer.sharedMaterials = replacement;
                        }
                    }
                }
                camera.targetTexture = target;
                camera.Render();
                camera.targetTexture = null;
                return target;
            }
            finally
            {
                foreach (Tuple<Renderer, Material[]> item in replaced)
                {
                    if (item.Item1 != null) item.Item1.sharedMaterials = item.Item2;
                }
                foreach (Material material in replacements) UnityEngine.Object.DestroyImmediate(material);
                camera.targetTexture = null;
                RenderTexture.active = previous;
            }
        }

        private static void SetHdrpBackgroundColor(Camera camera, Color color)
        {
            if (camera == null) return;
            Type type = FindLoadedType("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData");
            if (type == null) return;
            Component data = camera.GetComponent(type);
            if (data == null) return;
            type.GetField("backgroundColorHDR")?.SetValue(data, color);
            type.GetProperty("backgroundColorHDR")?.SetValue(data, color);
        }

        private static Texture2D RenderRoot2DPictureTile(PictureTile tile, int width, int height)
        {
            SubjectPictureData subject = tile.Subject;
            var groundPoints = subject.Pelvis
                .Select(point => new Vector3(point.x, 0f, point.z))
                .ToArray();
            Bounds bounds = new Bounds(groundPoints.Length > 0 ? groundPoints[0] : Vector3.zero, Vector3.zero);
            foreach (Vector3 point in groundPoints) bounds.Encapsulate(point);
            bounds.Expand(new Vector3(.8f, .2f, .8f));

            float aspect = width / (float)Mathf.Max(1, height);
            Bounds groundBounds = IncludeGroundInBounds(bounds);
            float groundWidth = Mathf.Max(.001f, groundBounds.size.x);
            float groundDepth = Mathf.Max(.001f, groundBounds.size.z);
            if (groundWidth / groundDepth < aspect)
            {
                groundBounds.Expand(new Vector3(groundDepth * aspect - groundWidth, 0f, 0f));
            }
            else
            {
                groundBounds.Expand(new Vector3(0f, 0f, groundWidth / aspect - groundDepth));
            }

            var environment = new List<GameObject>();
            CreatePictureEnvironment(environment, groundBounds);
            CreateWorldLine(environment, groundPoints, new Color(.1f, .85f, .25f, .95f), .06f);
            var keyframes = new HashSet<int>(tile.PrimaryFrames);
            foreach (int frame in tile.TrajectoryFrames.Where(frame => !keyframes.Contains(frame)))
            {
                int clamped = Mathf.Clamp(frame, 0, Math.Max(0, groundPoints.Length - 1));
                Vector3 origin = groundPoints.Length > 0 ? groundPoints[clamped] : Vector3.zero;
                CreateGroundMarker(environment, origin, .08f, Color.gray, "Kimodo Root2D Sample", .025f);
            }

            IReadOnlyList<int> orderedKeyframes = keyframes.OrderBy(frame => frame).ToArray();
            foreach (int frame in orderedKeyframes)
            {
                int clamped = Mathf.Clamp(frame, 0, Math.Max(0, groundPoints.Length - 1));
                Vector3 origin = groundPoints.Length > 0 ? groundPoints[clamped] : Vector3.zero;
                Color tint = clamped == 0 ? TestStartFrameTint :
                    clamped == groundPoints.Length - 1 ? TestEndFrameTint : TestKeyframeTint;
                CreateGroundMarker(environment, origin, .13f, tint);
                Vector3 forward = SampleRootForward(subject, clamped);
                CreateHeadingArrow(environment, origin, forward, .45f, tint);
            }

            Camera camera = CreateTestAnalysisPictureCamera(groundBounds, tile.Direction, aspect, 0f);
            try
            {
                return RenderCamera(camera, width, height, new Color(.12f, .12f, .12f, 1f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
                foreach (GameObject item in environment)
                {
                    if (item != null) UnityEngine.Object.DestroyImmediate(item);
                }
            }
        }

    }
}
