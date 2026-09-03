using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
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
            try
            {
                for (int index = 0; index < tiles.Count; index++)
                {
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

        private static Texture2D RenderPictureTile(PictureTile tile, int width, int height, TrajectoryScale trajectoryScale)
        {
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
                    RenderPoseOnto(result, meshCamera, meshEnvironment, tile.Subject, tile.Frame, Color.white, 1f);
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
                return RenderTestPoseTile(tile, height);
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
                    Color tint = tile.Presentation == "key" ? TestKeyframeTint : FootTint(tile.Subject, tile.Frame);
                    RenderPoseOnto(result, camera, environment, tile.Subject, tile.Frame, tint, 1f);
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

        private static void RenderPoseOnto(
            Texture2D destination,
            Camera camera,
            IReadOnlyList<GameObject> environment,
            SubjectPictureData subject,
            int localFrame,
            Color tint,
            float alpha,
            bool useTestGhostMaterial = false)
        {
            GameObject preview = CreateAnalysisPosePreview(subject, localFrame);
            var transientMaterials = new List<Material>();
            try
            {
                if (useTestGhostMaterial)
                {
                    ConfigureTestGhostMaterial(preview, tint, alpha, transientMaterials);
                }
                else
                {
                    TintPreview(preview, tint);
                }
                SetEvidenceVisualsEnabled(environment, false);
                Texture2D layer = RenderCamera(camera, destination.width, new Color(0f, 0f, 0f, 0f));
                try
                {
                    // GhostAlpha is already encoded in the transparent shader;
                    // applying it again here would square the opacity.
                    Composite(destination, layer, useTestGhostMaterial ? 1f : alpha);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(layer);
                    SetEvidenceVisualsEnabled(environment, true);
                }
            }
            finally
            {
                foreach (Material material in transientMaterials)
                {
                    if (material != null) UnityEngine.Object.DestroyImmediate(material);
                }
                UnityEngine.Object.DestroyImmediate(preview);
            }
        }

        private static bool ConfigureTestGhostMaterial(
            GameObject preview,
            Color tint,
            float alpha,
            List<Material> transientMaterials)
        {
            Shader shader = Shader.Find("Kimodo/GhostFront");
            if (shader == null)
            {
                return false;
            }

            foreach (Renderer renderer in preview.GetComponentsInChildren<Renderer>(true))
            {
                Material[] sourceMaterials = renderer.sharedMaterials;
                if (sourceMaterials == null || sourceMaterials.Length == 0)
                {
                    sourceMaterials = new[] { (Material)null };
                }
                var replacements = new Material[sourceMaterials.Length];
                for (int index = 0; index < sourceMaterials.Length; index++)
                {
                    Material source = sourceMaterials[index];
                    Material replacement = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    if (source != null)
                    {
                        if (source.HasProperty("_MainTex")) replacement.mainTexture = source.mainTexture;
                        if (source.HasProperty("_Color")) replacement.SetColor("_Color", source.color);
                    }
                    replacement.SetColor("_GhostTint", tint);
                    replacement.SetFloat("_GhostAlpha", alpha);
                    replacements[index] = replacement;
                    transientMaterials.Add(replacement);
                }
                renderer.sharedMaterials = replacements;
            }
            return true;
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
                if (tile.Presentation == "test_foot_transitions" || tile.Presentation == "test_keyframes")
                {
                    List<int> frames = tile.TrajectoryFrames;
                    bool separated = !tile.Subject.FirstBounds.Intersects(tile.Subject.LastBounds);
                    for (int index = 0; index < frames.Count; index++)
                    {
                        int frame = frames[index];
                        if (frame == 0 || frame == lastFrame) continue;
                        Color tint = ResolveTestPoseTint(tile, frame, out bool keyframe, out bool footTransition);
                        float alpha = Mathf.Clamp(
                            GhostAlpha(index, frames.Count, separated),
                            TestGhostAlphaMin,
                            TestGhostAlphaMax);
                        if (keyframe) alpha += .3f;
                        if (footTransition) alpha += .2f;
                        if (tile.StationaryBoostFrames.Contains(frame))
                        {
                            alpha = Mathf.Min(MaxPromotedGhostAlpha, alpha + StationaryTrajectoryAlphaBoost);
                        }
                        alpha = Mathf.Clamp01(alpha);
                        virtualPoses.Add(CreateTestVirtualPose(
                            posePlan.Get(frame), tint, alpha));
                    }
                }
                Color startTint = ResolveTestPoseTint(tile, 0, out _, out _);
                Color endTint = ResolveTestPoseTint(tile, lastFrame, out _, out _);
                virtualPoses.Add(CreateTestVirtualPose(posePlan.Get(0), startTint, 1f));
                virtualPoses.Add(CreateTestVirtualPose(posePlan.Get(lastFrame), endTint, 1f));

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
                // Test pose tiles already choose their width from the pose
                // aspect. Render at the larger height, then preserve that
                // aspect while reducing to the requested output resolution.
                Texture2D source = RenderTestPoseTile(tile, targetHeight * scale);
                try
                {
                    int outputWidth = Mathf.Max(1, Mathf.RoundToInt(source.width / (float)scale));
                    return ResizeTexture(source, outputWidth, targetHeight);
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

        private static Texture2D RenderTestPoseTile(PictureTile tile, int targetHeight)
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
            float aspect = horizontal / Mathf.Max(.0001f, vertical);
            int sourceHeight = TestPoseSupersampleHeight;
            int sourceWidth = Math.Max(1, Mathf.CeilToInt(sourceHeight * aspect));
            int targetWidth = Math.Max(1, Mathf.RoundToInt(targetHeight * aspect));
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
            try
            {
                SetEvidenceVisualsEnabled(environment, true);
                baseLayer = RenderCameraToTexture(camera, width, height, background, RenderTextureFormat.ARGB32, false);
                Graphics.CopyTexture(baseLayer, accumulationColor);
                composite.SetInt("_Width", width); composite.SetInt("_Height", height);
                composite.SetInt("_ReversedZ", SystemInfo.usesReversedZBuffer ? 1 : 0);
                composite.SetTexture(initKernel, "_AccumDepth", accumulationDepth);
                composite.Dispatch(initKernel, groupsX, groupsY, 1);

                SetEvidenceVisualsEnabled(environment, false);
                foreach (TestVirtualPose pose in poses)
                {
                    SetPreviewRenderersEnabled(pose.Preview, true);
                    try
                    {
                        layer = RenderCameraToTexture(camera, width, height, Color.clear, RenderTextureFormat.ARGB32, false);
                        depth = RenderCameraDepthToTexture(camera, depthShader, width, height);
                        composite.SetFloat("_PoseAlpha", pose.UsesGhostMaterial ? 1f : pose.Alpha);
                        composite.SetTexture(poseKernel, "_PoseColor", layer);
                        composite.SetTexture(poseKernel, "_PoseDepth", depth);
                        composite.SetTexture(poseKernel, "_BaseColor", baseLayer);
                        composite.SetTexture(poseKernel, "_AccumColor", accumulationColor);
                        composite.SetTexture(poseKernel, "_AccumDepth", accumulationDepth);
                        composite.Dispatch(poseKernel, groupsX, groupsY, 1);
                        RenderTexture.ReleaseTemporary(layer); layer = null;
                        RenderTexture.ReleaseTemporary(depth); depth = null;
                    }
                    finally { SetPreviewRenderersEnabled(pose.Preview, false); }
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
                    composite.SetTexture(blendKernel, "_LayerColor", layer);
                    composite.SetTexture(blendKernel, "_AccumColor", accumulationColor);
                    composite.Dispatch(blendKernel, groupsX, groupsY, 1);
                    RenderTexture.ReleaseTemporary(layer); layer = null;
                }
                return ReadRenderTexture(accumulationColor, width, height);
            }
            finally
            {
                if (layer != null) RenderTexture.ReleaseTemporary(layer);
                if (depth != null) RenderTexture.ReleaseTemporary(depth);
                if (baseLayer != null) RenderTexture.ReleaseTemporary(baseLayer);
                RenderTexture.ReleaseTemporary(accumulationColor);
                RenderTexture.ReleaseTemporary(accumulationDepth);
                camera.targetTexture = null;
                SetEvidenceVisualsEnabled(environment, true);
            }
        }

        private static RenderTexture NewAnalysisRenderTexture(int width, int height, RenderTextureFormat format, bool randomWrite, int depthBitsOverride = -1)
        {
            int depthBits = depthBitsOverride >= 0 ? depthBitsOverride : (format == RenderTextureFormat.ARGB32 ? 24 : 0);
            var texture = RenderTexture.GetTemporary(width, height, depthBits, format);
            texture.Release();
            texture.enableRandomWrite = randomWrite;
            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.Create();
            return texture;
        }

        private sealed class HdrpAovState
        {
            public readonly Dictionary<string, object> Handles = new Dictionary<string, object>(StringComparer.Ordinal);
            public bool Completed;
        }

        private static bool IsHdrpCapturePipeline()
        {
            string pipelineName = GraphicsSettings.currentRenderPipeline == null
                ? string.Empty
                : GraphicsSettings.currentRenderPipeline.GetType().FullName ?? string.Empty;
            return pipelineName.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Type FindLoadedType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(type => type != null);
        }

        private static object ResolveHdrpAovTarget(object stateObject, object bufferId)
        {
            HdrpAovState state = (HdrpAovState)stateObject;
            string name = bufferId == null ? string.Empty : bufferId.ToString();
            if (state.Handles.TryGetValue(name, out object handle)) return handle;
            // HDRP invokes the allocator for every requested buffer. Returning
            // the color target for an unknown enum keeps this path compatible
            // with minor AOV enum additions while preserving one allocation.
            return state.Handles.TryGetValue("Color", out handle) ? handle : null;
        }

        private static void CompleteHdrpAov(object stateObject, object commandBuffer, object buffers)
        {
            ((HdrpAovState)stateObject).Completed = true;
        }

        private static Delegate CreateHdrpAllocator(Type delegateType, HdrpAovState state)
        {
            MethodInfo invoke = delegateType.GetMethod("Invoke");
            ParameterInfo parameter = invoke.GetParameters()[0];
            ParameterExpression parameterExpression = Expression.Parameter(parameter.ParameterType, "bufferId");
            MethodInfo resolver = typeof(command_context).GetMethod(
                nameof(ResolveHdrpAovTarget), BindingFlags.NonPublic | BindingFlags.Static);
            Expression body = Expression.Call(
                resolver,
                Expression.Constant(state, typeof(object)),
                Expression.Convert(parameterExpression, typeof(object)));
            body = Expression.Convert(body, invoke.ReturnType);
            return Expression.Lambda(delegateType, body, parameterExpression).Compile();
        }

        private static Delegate CreateHdrpCallback(Type delegateType, HdrpAovState state)
        {
            MethodInfo invoke = delegateType.GetMethod("Invoke");
            ParameterExpression[] parameters = invoke.GetParameters()
                .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
                .ToArray();
            MethodInfo complete = typeof(command_context).GetMethod(
                nameof(CompleteHdrpAov), BindingFlags.NonPublic | BindingFlags.Static);
            Expression body = Expression.Call(
                complete,
                Expression.Constant(state, typeof(object)),
                Expression.Convert(parameters[0], typeof(object)),
                Expression.Convert(parameters[1], typeof(object)));
            return Expression.Lambda(delegateType, body, parameters).Compile();
        }

        private static RenderTexture RenderHdrpAovToTexture(
            Camera camera,
            int width,
            int height,
            RenderTextureFormat format,
            string bufferName)
        {
            Type additionalCameraDataType = FindLoadedType("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData");
            Type builderType = FindLoadedType("UnityEngine.Rendering.HighDefinition.AOVRequestBuilder");
            Type requestType = FindLoadedType("UnityEngine.Rendering.HighDefinition.AOVRequest");
            Type buffersType = FindLoadedType("UnityEngine.Rendering.HighDefinition.AOVBuffers");
            Type allocatorType = FindLoadedType("UnityEngine.Rendering.HighDefinition.AOVRequestBufferAllocator");
            Type callbackType = FindLoadedType("UnityEngine.Rendering.HighDefinition.FramePassCallback");
            Type rtHandlesType = FindLoadedType("UnityEngine.Rendering.RTHandles");
            if (additionalCameraDataType == null || builderType == null || requestType == null ||
                buffersType == null || allocatorType == null || callbackType == null || rtHandlesType == null)
            {
                throw new InvalidOperationException("HDRP AOV capture types are unavailable.");
            }

            RenderTexture target = NewAnalysisRenderTexture(width, height, format, false,
                format == RenderTextureFormat.ARGB32 ? 24 : 0);
            RenderTexture result = NewAnalysisRenderTexture(width, height, format, false,
                format == RenderTextureFormat.ARGB32 ? 24 : 0);
            var state = new HdrpAovState();
            Rect previousPixelRect = camera.pixelRect;
            MethodInfo alloc = rtHandlesType.GetMethod(
                "Alloc", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(RenderTexture) }, null);
            try
            {
                if (alloc == null) throw new InvalidOperationException("HDRP RTHandle allocator is unavailable.");
                object handle = alloc.Invoke(null, new object[] { target });
                state.Handles[bufferName] = handle;

                Component additionalCameraData = camera.GetComponent(additionalCameraDataType) ??
                    camera.gameObject.AddComponent(additionalCameraDataType);
                object request = requestType.GetMethod("NewDefault", BindingFlags.Public | BindingFlags.Static)
                    .Invoke(null, null);
                object builder = Activator.CreateInstance(builderType);
                MethodInfo add = builderType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(method => method.Name == "Add")
                    .First(method => method.GetParameters().Length == 5);
                object buffer = System.Enum.Parse(buffersType, bufferName);
                Delegate allocator = CreateHdrpAllocator(allocatorType, state);
                Delegate callback = CreateHdrpCallback(callbackType, state);
                Array requestedBuffers = Array.CreateInstance(buffersType, 1);
                requestedBuffers.SetValue(buffer, 0);
                add.Invoke(builder, new object[] { request, allocator, null, requestedBuffers, callback });
                object collection = builderType.GetMethod("Build", BindingFlags.Public | BindingFlags.Instance)
                    .Invoke(builder, null);
                additionalCameraDataType.GetMethod("SetAOVRequests", BindingFlags.Public | BindingFlags.Instance)
                    .Invoke(additionalCameraData, new object[] { collection });
                additionalCameraDataType.GetField("backgroundColorHDR")?.SetValue(additionalCameraData, camera.backgroundColor);
                additionalCameraDataType.GetProperty("backgroundColorHDR")?.SetValue(additionalCameraData, camera.backgroundColor);
                camera.targetTexture = null;
                camera.pixelRect = new Rect(0f, 0f, width, height);
                camera.Render();
                camera.pixelRect = previousPixelRect;
                Graphics.CopyTexture(target, result);
                additionalCameraDataType.GetMethod("SetAOVRequests", BindingFlags.Public | BindingFlags.Instance)
                    .Invoke(additionalCameraData, new object[] { null });
                RenderTexture.ReleaseTemporary(target);
                target = null;
                return result;
            }
            catch
            {
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (result != null) RenderTexture.ReleaseTemporary(result);
                throw;
            }
            finally
            {
                camera.pixelRect = previousPixelRect;
            }
        }

        private static RenderTexture RenderCameraToTexture(Camera camera, int width, int height, Color background, RenderTextureFormat format, bool randomWrite)
        {
            if (IsHdrpCapturePipeline())
            {
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = background;
                return RenderHdrpAovToTexture(camera, width, height, format, "Color");
            }
            RenderTexture target = NewAnalysisRenderTexture(width, height, format, randomWrite);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
            camera.targetTexture = target;
            camera.Render();
            camera.targetTexture = null;
            return target;
        }

        private static RenderTexture RenderCameraDepthToTexture(Camera camera, Shader depthShader, int width, int height)
        {
            if (IsHdrpCapturePipeline())
            {
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                return RenderHdrpAovToTexture(camera, width, height, RenderTextureFormat.ARGBFloat, "DepthStencil");
            }
            RenderTexture target = NewAnalysisRenderTexture(width, height, RenderTextureFormat.ARGBFloat, false, 24);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.targetTexture = target;
            // Replace every pose renderer, including transparent ghost materials.
            // Matching on RenderType would skip Kimodo/GhostFront (Transparent),
            // leaving its depth at the clear value and making submission order
            // determine which ghost survives the GPU composite.
            camera.RenderWithShader(depthShader, null);
            camera.targetTexture = null;
            return target;
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

            var environment = new List<GameObject>();
            CreatePictureEnvironment(environment, IncludeGroundInBounds(bounds));
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

            Camera camera = CreateTestAnalysisPictureCamera(bounds, tile.Direction, (float)width / Mathf.Max(1, height));
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
