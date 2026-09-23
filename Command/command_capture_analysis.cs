using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KimodoUnityBridge;
using KimodoBridge;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KimodoUnityBridge.Command

{
    internal static partial class command_context
    {
        private const string PhaseTrackVersion = "1-temporal-cluster-v2-command-60";

        private static KimodoMarkerSampleResult[] CaptureCachedSampleResults(
            AnalysisCacheRecord record,
            TimelineCharacterRecord character,
            int startFrame,
            int frameCount)
        {
            string key = record?.Id ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(key) && AnalysisPoseSamples.TryGetValue(key, out KimodoMarkerSampleResult[] cached) &&
                cached != null && cached.Length >= frameCount)
            {
                return cached;
            }
            KimodoMarkerSampleResult[] samples = CaptureSampleResults(character, startFrame, frameCount);
            if (!string.IsNullOrWhiteSpace(key)) AnalysisPoseSamples[key] = samples;
            return samples;
        }

        // The analysis picture is the fixed 16:9 test-layout composite: four
        // overview tiles plus eight key poses and eight step poses.
        private static JObject RenderAnalysisPictures(
            TimelineSessionRecord session,
            IReadOnlyList<AnalysisSubject> subjects,
            JObject picture,
            int requestedResolution)
        {
            if (subjects == null || subjects.Count != 1)
            {
                throw new InvalidOperationException("Analysis picture rendering accepts exactly one clip.");
            }
            AnalysisPictureRequest request = AnalysisPictureRequest.Parse(picture);
            if (request.WritesComposite && request.TileTypes.Count == 6 && request.Includes("3d_track") && request.Includes("height_time_track") &&
                request.Includes("3d_ghost") && request.Includes("3d_ghost_track") &&
                request.Includes("key_pose") && request.Includes("step_pose"))
            {
                return RenderAnalysisPictures16By9(session, subjects[0], request, "middle", requestedResolution);
            }
            return RenderTestAnalysisPictures(session, subjects[0], requestedResolution);
        }

        private static JObject RenderAnalysisPictures16By9(
            TimelineSessionRecord session,
            AnalysisSubject subject,
            AnalysisPictureRequest request,
            string level,
            int requestedResolution)
        {
            SubjectPictureData data = BuildSubjectPictureData(session, subject);
            EnsurePhaseTrack(data);
            AnalysisCache[data.Subject.Record.Id] = data.Subject.Record;
            WriteJsonAtomically(AnalysisCachePath(session, data.Subject.Record.Id), data.Subject.Record.ToJson());
            var tiles = BuildTestAnalysisTiles(data);
            DateTime capturedAt = DateTime.Now;
            int width = Math.Max(64, requestedResolution);
            int height = Math.Max(36, Mathf.RoundToInt(width * 9f / 16f));
            const int renderWidth = TestAnalysisCanvasWidth;
            const int renderHeight = TestAnalysisCanvasHeight;
            const int gap = 8;
            const int header = 60;
            int overviewWidth = Math.Max(1, (renderWidth - gap * 3) / 4);
            int poseWidth = Math.Max(1, (renderWidth - gap * 7) / 8);
            int overviewHeight = Mathf.Max(1, Mathf.RoundToInt(overviewWidth * .75f));
            int poseHeight = Math.Max(1, (renderHeight - header - overviewHeight) / 2);
            var images = new List<Texture2D>(tiles.Count);
            var rects = new List<RectInt>(tiles.Count);
            var hiddenRenderers = new List<(Renderer Renderer, bool Enabled)>();
            GameObject previousCaptureRoot = captureSessionRoot;
            bool previousFog = RenderSettings.fog;
            Texture2D canvas = null;
            try
            {
                captureSessionRoot = session?.SessionRoot;
                if (subject.Character?.Root != null)
                {
                    foreach (Renderer renderer in subject.Character.Root.GetComponentsInChildren<Renderer>(true))
                    {
                        hiddenRenderers.Add((renderer, renderer.enabled));
                        renderer.enabled = false;
                    }
                }
                RenderSettings.fog = false;
                TrajectoryScale scale = BuildTrajectoryScale(new[] { data }, true);
                for (int index = 0; index < tiles.Count; index++)
                {
                    int tileWidth = index < 4 ? overviewWidth : poseWidth;
                    int tileHeight = index < 4 ? overviewHeight : poseHeight;
                    images.Add(tiles[index].IsEmpty
                        ? CreateEmptyTestTile(tileWidth, tileHeight)
                        : RenderPictureTileSupersampled(tiles[index], tileWidth, tileHeight, scale, 1));
                }
                for (int row = 0; row < 3; row++)
                {
                    int count = row == 0 ? 4 : 8;
                    int tileWidth = row == 0 ? overviewWidth : poseWidth;
                    int tileHeight = row == 0 ? overviewHeight : poseHeight;
                    int x = Math.Max(0, (renderWidth - count * tileWidth - gap * (count - 1)) / 2);
                    int y = row == 0 ? renderHeight - header - overviewHeight : renderHeight - header - overviewHeight - poseHeight * row;
                    for (int column = 0; column < count; column++)
                    {
                        rects.Add(new RectInt(x, y, tileWidth, tileHeight));
                        x += tileWidth + gap;
                    }
                }
                canvas = ComposePictureCanvasGpu(images, rects, renderWidth, renderHeight);
                DrawTestAnalysisHeader(canvas, subject.Animation?.Name, (float)(data.Pelvis.Length / SessionFrameRate), capturedAt);
                canvas.Apply(false, false);
                List<RectInt> outputRects = rects;
                if (width != renderWidth || height != renderHeight)
                {
                    outputRects = rects.Select(rect => new RectInt(
                        Mathf.RoundToInt(rect.x * width / (float)renderWidth),
                        Mathf.RoundToInt(rect.y * height / (float)renderHeight),
                        Mathf.Max(1, Mathf.RoundToInt(rect.width * width / (float)renderWidth)),
                        Mathf.Max(1, Mathf.RoundToInt(rect.height * height / (float)renderHeight)))).ToList();
                    Texture2D resized = ResizeTexture(canvas, width, height);
                    UnityEngine.Object.DestroyImmediate(canvas);
                    canvas = resized;
                }
            string signature = BuildPictureSignature(new[] { subject }, "16:9-layout", requestedResolution);
                string imagePath = Path.Combine(EvidenceFolder(session), $"analysis_picture_{signature}.png");
                Directory.CreateDirectory(EvidenceFolder(session));
                File.WriteAllBytes(imagePath, canvas.EncodeToPNG());
                var descriptions = new JArray();
                for (int index = 0; index < tiles.Count; index++)
                {
                    PictureTile tile = tiles[index];
                    JObject description = (JObject)tile.Description.DeepClone();
                    description["subject"] = subject.Role;
                    if (tile.Presentation == "test_pose") description["frame"] = tile.Frame;
                    descriptions.Add(new JObject
                    {
                        ["id"] = index < 4 ? $"1.{index + 1}" : index < 12 ? $"2.{index - 3}" : $"3.{index - 11}",
                        ["rect"] = new JObject { ["x"] = outputRects[index].x, ["y"] = outputRects[index].y, ["width"] = outputRects[index].width, ["height"] = outputRects[index].height },
                        ["description"] = description
                    });
                }
                string capturedText = capturedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                double duration = data.Pelvis.Length / SessionFrameRate;
                var result = new JObject
                {
                    ["picture"] = request.ToJson(), ["level"] = level,
                    ["render_version"] = TestAnalysisPicture20TileRenderVersion,
                    ["image_path"] = ToProjectRelativePath(imagePath), ["width"] = width, ["height"] = height,
                    ["resolution"] = requestedResolution, ["aspect"] = "16:9",
                    ["header_height"] = Mathf.Max(1, Mathf.RoundToInt(header * height / (float)renderHeight)),
                    ["screenshot_generated_at"] = capturedText, ["supersample"] = 1, ["tile_count"] = tiles.Count,
                    ["title"] = new JObject { ["animation_name"] = subject.Animation?.Name ?? string.Empty, ["duration_seconds"] = duration, ["guid"] = subject.Animation?.Id.ToString("D") ?? string.Empty },
                    ["header"] = new JObject { ["clip"] = subject.Animation?.Name ?? string.Empty, ["frame_count"] = data.Pelvis.Length, ["fps"] = SessionFrameRate, ["duration_seconds"] = duration, ["tile_type"] = "analysis-test", ["screenshot_generated_at"] = capturedText },
                    ["headers"] = new JArray(new JObject { ["subject"] = subject.Role, ["clip"] = subject.Animation?.Name ?? string.Empty, ["total_frames"] = data.Pelvis.Length, ["fps"] = SessionFrameRate, ["duration_seconds"] = duration, ["screenshot_generated_at"] = capturedText, ["tile_types"] = new JArray("3d_track", "height_time_track", "3d_ghost", "3d_ghost_track", "test_pose"), ["rect"] = new JObject { ["x"] = 0, ["y"] = 0, ["width"] = width, ["height"] = height } }),
                    ["images"] = descriptions, ["cached"] = false
                };
            PersistPictureSummary(session, subject.Record, result);
            return result;
            }
            finally
            {
                foreach (Texture2D image in images) if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (canvas != null) UnityEngine.Object.DestroyImmediate(canvas);
                foreach ((Renderer renderer, bool enabled) in hiddenRenderers) if (renderer != null) renderer.enabled = enabled;
                RenderSettings.fog = previousFog;
                captureSessionRoot = previousCaptureRoot;
            }
        }

        private static List<PictureTile> BuildTestAnalysisTiles(SubjectPictureData subject)
        {
            IReadOnlyList<int?> keyframes = NormalizeTestKeyframes(subject, 8);
            IReadOnlyList<int?> steps = NormalizeTestStepFrames(subject, 8);
            var result = new List<PictureTile>
            {
                PictureTile.TestOverview(subject, "3d_track", Vector3.up),
                PictureTile.TestOverview(subject, "height_time_track", Vector3.up),
                PictureTile.TestSelectedOverview(subject, "3d_ghost", keyframes.Where(value => value.HasValue).Select(value => value.Value), new Vector3(1f, .75f, -1f)),
                PictureTile.TestSelectedOverview(subject, "3d_ghost_track", steps.Where(value => value.HasValue).Select(value => value.Value), new Vector3(1f, .75f, -1f))
            };
            for (int index = 0; index < 8; index++) result.Add(PictureTile.TestPoseSlot(subject, keyframes[index], "key_pose", index + 1));
            for (int index = 0; index < 8; index++) result.Add(PictureTile.TestPoseSlot(subject, steps[index], "step_pose", index + 1));
            result[2].Description["frames"] = new JArray(keyframes.Where(value => value.HasValue).Select(value => value.Value));
            result[3].Description["frames"] = new JArray(steps.Where(value => value.HasValue).Select(value => value.Value));
            result[2].Description["shared_with"] = "key_pose";
            result[3].Description["shared_with"] = "step_pose";
            return result;
        }

        private static IReadOnlyList<int?> NormalizeTestKeyframes(SubjectPictureData subject, int count)
        {
            int last = Math.Max(0, subject.Pelvis.Length - 1);
            var candidates = (subject.Subject.Record.Analysis?["keyframes"] as JArray ?? new JArray())
                .OfType<JObject>().Select(item => Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, last))
                .Distinct().OrderBy(frame => frame).ToList();
            var selected = new List<int>();
            if (count > 0) selected.Add(0);
            if (count > 1 && last != 0) selected.Add(last);
            int middleCount = Math.Max(0, count - selected.Count);
            var middle = candidates.Where(frame => frame != 0 && frame != last).ToList();
            if (middle.Count > middleCount && middleCount > 0)
                middle = Enumerable.Range(0, middleCount).Select(index => middle[Mathf.RoundToInt(index * (middle.Count - 1) / (float)Math.Max(1, middleCount - 1))]).ToList();
            selected = selected.Take(1).Concat(middle).Concat(selected.Skip(1)).Distinct().Take(count).ToList();
            return selected.Cast<int?>().Concat(Enumerable.Repeat<int?>(null, Math.Max(0, count - selected.Count))).ToArray();
        }

        private static IReadOnlyList<int?> NormalizeTestStepFrames(SubjectPictureData subject, int count)
        {
            int last = Math.Max(0, subject.Pelvis.Length - 1);
            var contacts = (subject.Subject.Record.Analysis?["foot_contacts"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(item => new { Frame = Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, last), Foot = item.Value<string>("foot") ?? string.Empty })
                .GroupBy(item => item.Frame).Select(group => group.First()).OrderBy(item => item.Frame).ToList();
            var selected = new HashSet<int>();
            if (count > 0 && contacts.Count > 0)
            {
                foreach (string foot in new[] { "left", "right" })
                {
                    var first = contacts.FirstOrDefault(item => item.Foot.IndexOf(foot, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (first != null) selected.Add(first.Frame);
                }
                int target = Math.Min(count, contacts.Count);
                for (int rank = 0; rank < target && selected.Count < target; rank++)
                    selected.Add(contacts[target <= 1 ? 0 : Mathf.RoundToInt(rank * (contacts.Count - 1) / (float)(target - 1))].Frame);
                for (int index = 0; index < contacts.Count && selected.Count < target; index++) selected.Add(contacts[index].Frame);
            }
            var frames = selected.OrderBy(frame => frame).Take(Math.Max(0, count)).Cast<int?>().ToList();
            while (frames.Count < count) frames.Add(null);
            return frames;
        }

        private static JObject RenderUnifiedAnalysisPictures(
            TimelineSessionRecord session,
            IReadOnlyList<AnalysisSubject> subjects,
            JObject picture,
            int requestedResolution)
        {
            AnalysisPictureRequest pictureRequest = AnalysisPictureRequest.Parse(picture);
            string pictureKey = pictureRequest.ToJson().ToString(Formatting.None);
            string signature = BuildPictureSignature(subjects, pictureKey, requestedResolution);
            string imagePath = Path.Combine(EvidenceFolder(session), $"analysis_picture_{signature}.png");
            string projectPath = ToProjectRelativePath(imagePath);
            JObject persisted = subjects[0].Record.Pictures;
            bool phaseTrackReady = subjects.All(item =>
                string.Equals(item.Record.Analysis?.Value<string>("phase_track_version"), PhaseTrackVersion, StringComparison.Ordinal));
            if (phaseTrackReady && persisted != null &&
                string.Equals(persisted.Value<string>("picture"), pictureKey, StringComparison.Ordinal) &&
                string.Equals(persisted.Value<string>("image_path"), projectPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(imagePath))
            {
                var cachedResult = (JObject)persisted.DeepClone();
                cachedResult["render_version"] = UnifiedAnalysisPictureRenderVersion;
                cachedResult["cached"] = true;
                return cachedResult;
            }

            var data = subjects.Select(subject => BuildSubjectPictureData(session, subject)).ToList();
            foreach (SubjectPictureData subject in data)
            {
                EnsurePhaseTrack(subject);
                AnalysisCache[subject.Subject.Record.Id] = subject.Subject.Record;
                WriteJsonAtomically(
                    AnalysisCachePath(session, subject.Subject.Record.Id),
                    subject.Subject.Record.ToJson());
            }
            TrajectoryScale trajectoryScale = BuildTrajectoryScale(data, true);
            var tiles = new List<PictureTile>();
            foreach (SubjectPictureData subject in data)
            {
                tiles.AddRange(BuildPictureTiles(subject, pictureRequest));
            }

            int maxTileCount = Math.Max(
                1,
                data.Select(subject => tiles.Count(tile => ReferenceEquals(tile.Subject, subject))).DefaultIfEmpty(1).Max());
            PictureLayout layout = PictureLayout.ForLevel(maxTileCount, true, requestedResolution);

            int tileWidth = layout.TileSize;
            int tileHeight = ResolvePictureTileHeight(layout, tiles, tileWidth);
            int panelHeight = layout.TileRows * tileHeight;

            List<RectInt> imageRects;
            int imageWidth;
            int imageHeight;
            bool cached = false;
            Directory.CreateDirectory(EvidenceFolder(session));
            GameObject previousCaptureRoot = captureSessionRoot;
            bool previousFogEnabled = RenderSettings.fog;
            var hiddenCharacterRenderers = new List<(Renderer Renderer, bool Enabled)>();
            Texture2D canvas;
            try
            {
                captureSessionRoot = session?.SessionRoot;
                foreach (GameObject characterRoot in subjects
                    .Select(subject => subject.Character?.Root)
                    .Where(root => root != null)
                    .Distinct())
                {
                    foreach (Renderer renderer in characterRoot.GetComponentsInChildren<Renderer>(true))
                    {
                        hiddenCharacterRenderers.Add((renderer, renderer.enabled));
                        renderer.enabled = false;
                    }
                }
                // Analysis evidence must not inherit distance-based project fog.
                RenderSettings.fog = false;
                canvas = RenderPictureCanvas(
                    data,
                    tiles,
                    layout,
                    trajectoryScale,
                    tileWidth,
                    tileHeight,
                    PictureSupersample,
                    out imageRects);
            }
            finally
            {
                foreach ((Renderer renderer, bool enabled) in hiddenCharacterRenderers)
                {
                    if (renderer != null) renderer.enabled = enabled;
                }
                RenderSettings.fog = previousFogEnabled;
                captureSessionRoot = previousCaptureRoot;
            }
            imageWidth = canvas.width;
            imageHeight = canvas.height;
            if (pictureRequest.WritesComposite)
            {
                File.WriteAllBytes(imagePath, canvas.EncodeToPNG());
            }

            var descriptions = new JArray();
            var tilePaths = new JArray();
            for (int index = 0; index < tiles.Count; index++)
            {
                PictureTile tile = tiles[index];
                RectInt rect = imageRects[index];
                int panel = data.FindIndex(item => ReferenceEquals(item, tile.Subject));
                int localIndex = tiles.Take(index).Count(item => ReferenceEquals(item.Subject, tile.Subject));
                JObject description = (JObject)tile.Description.DeepClone();
                description["subject"] = tile.Subject.Subject.Character.Name;
                if (pictureRequest.WritesTiles)
                {
                    string tilePath = Path.Combine(
                        EvidenceFolder(session),
                        $"analysis_picture_{signature}_tile_{index + 1:00}.png");
                    Texture2D tileImage = new Texture2D(rect.width, rect.height, TextureFormat.RGBA32, false);
                    tileImage.SetPixels(canvas.GetPixels(rect.x, rect.y, rect.width, rect.height));
                    tileImage.Apply(false, false);
                    try { File.WriteAllBytes(tilePath, tileImage.EncodeToPNG()); }
                    finally { UnityEngine.Object.DestroyImmediate(tileImage); }
                    tilePaths.Add(ToProjectRelativePath(tilePath));
                }
                descriptions.Add(new JObject
                {
                    ["id"] = (panel + 1).ToString(CultureInfo.InvariantCulture) + "." +
                        (localIndex + 1).ToString(CultureInfo.InvariantCulture),
                    ["rect"] = new JObject { ["x"] = rect.x, ["y"] = rect.y, ["width"] = rect.width, ["height"] = rect.height },
                    ["description"] = description
                });
            }

            var result = new JObject
            {
                ["picture"] = pictureKey,
                ["level"] = "middle",
                ["aspect"] = "16:9",
                ["tile_count"] = tiles.Count,
                ["render_version"] = pictureRequest.Includes("key_pose") && pictureRequest.Includes("step_pose")
                    ? TestAnalysisPicture20TileRenderVersion
                    : UnifiedAnalysisPictureRenderVersion,
                ["image_path"] = pictureRequest.WritesComposite ? projectPath : string.Empty,
                ["width"] = imageWidth,
                ["height"] = imageHeight,
                ["resolution"] = requestedResolution,
                ["supersample"] = PictureSupersample,
                ["images"] = descriptions,
                ["tile_paths"] = tilePaths,
                ["cached"] = cached
            };
            if (!pictureRequest.WritesTiles) result.Remove("tile_paths");
            UnityEngine.Object.DestroyImmediate(canvas);
            PersistPictureSummary(session, subjects[0].Record, result);
            return result;
        }

        private static int ResolvePictureTileHeight(
            PictureLayout layout,
            IReadOnlyList<PictureTile> tiles,
            int tileWidth)
        {
            if (layout.TileSize != tileWidth || tiles == null ||
                !tiles.Any(tile => tile.Presentation == "test_foot_transitions" || tile.Presentation == "test_keyframes"))
            {
                return layout.TileSize;
            }

            float widestAspect = 1f;
            foreach (PictureTile tile in tiles)
            {
                if (tile.Presentation != "test_foot_transitions" && tile.Presentation != "test_keyframes") continue;
                CalculateTestViewExtents(tile.Subject, tile.Direction, out _, out float horizontal, out float vertical, out _);
                widestAspect = Mathf.Max(
                    widestAspect,
                    (horizontal + TestCameraMarginMeters) / Mathf.Max(.01f, vertical + TestCameraMarginMeters));
            }

            int minimumHeight = Mathf.Max(1, Mathf.Min(160, tileWidth / 3));
            return Mathf.Clamp(Mathf.RoundToInt(tileWidth / widestAspect), minimumHeight, tileWidth);
        }

        private static JObject RenderTestAnalysisPictures(
            TimelineSessionRecord session,
            AnalysisSubject subject,
            int requestedResolution)
        {
            if (subject == null) throw new InvalidOperationException("-test analysis requires one clip.");
            SubjectPictureData data = BuildSubjectPictureData(session, subject);
            EnsurePhaseTrack(data);
                AnalysisCache[data.Subject.Record.Id] = data.Subject.Record;
                WriteJsonAtomically(
                AnalysisCachePath(session, data.Subject.Record.Id),
                data.Subject.Record.ToJson());
            var tiles = BuildTestAnalysisTiles(data);
            TestPictureLayout layout = TestPictureLayout.ForResolution(requestedResolution);
            DateTime capturedAt = DateTime.Now;
            TrajectoryScale trajectoryScale = BuildTrajectoryScale(new[] { data }, true);
            var images = new List<Texture2D>(tiles.Count);
            var rects = new List<RectInt>(tiles.Count);
            using var captureLights = new AnalysisCaptureLightScope();
            Texture2D canvas = null;
            var hiddenCharacterRenderers = new List<(Renderer Renderer, bool Enabled)>();
            GameObject previousCaptureRoot = captureSessionRoot;
            bool previousFogEnabled = RenderSettings.fog;
            try
            {
                captureSessionRoot = session?.SessionRoot;
                if (subject.Character?.Root != null)
                {
                    foreach (Renderer renderer in subject.Character.Root.GetComponentsInChildren<Renderer>(true))
                    {
                        hiddenCharacterRenderers.Add((renderer, renderer.enabled));
                        renderer.enabled = false;
                    }
                }
                RenderSettings.fog = false;
                foreach (PictureTile tile in tiles)
                {
                    int width = layout.WidthFor(tile);
                    int height = layout.HeightFor(tile);
                    Texture2D image = tile.IsEmpty
                        ? CreateEmptyTestTile(width, height)
                        : RenderPictureTileSupersampled(tile, width, height, trajectoryScale, 1);
                    if (image.width != width || image.height != height)
                    {
                        Texture2D resized = ResizeTexture(image, width, height);
                        UnityEngine.Object.DestroyImmediate(image);
                        image = resized;
                    }
                    images.Add(image);
                }

                canvas = new Texture2D(layout.CanvasWidth, layout.CanvasHeight, TextureFormat.RGBA32, false);
                Fill(canvas, new Color(.12f, .12f, .12f, 1f));
                canvas.Apply(false, false);
                int tileIndex = 0;
                for (int row = 0; row < 3; row++)
                {
                    int rowCount = row == 0 ? 4 : 8;
                    int rowWidth = Enumerable.Range(0, rowCount)
                        .Sum(column => layout.WidthFor(tiles[tileIndex + column])) +
                        TestPictureLayout.TileGapPixels * (rowCount - 1);
                    int x = Mathf.Max(0, (layout.CanvasWidth - rowWidth) / 2);
                    int y = layout.CanvasHeight - layout.HeaderHeight - layout.RowHeights.Take(row + 1).Sum();
                    int count = rowCount;
                    for (int column = 0; column < count; column++)
                    {
                        PictureTile tile = tiles[tileIndex];
                        int width = layout.WidthFor(tile);
                        int height = layout.HeightFor(tile);
                        RectInt rect = new RectInt(x, y, width, height);
                        rects.Add(rect);
                        if (!tile.IsEmpty)
                        {
                            DrawTestTileNumber(images[tileIndex], $"{row + 1}-{column + 1}");
                            if (row >= 1 && tile.Presentation == "test_pose") DrawFrameNumber(images[tileIndex], tile.Frame);
                        }
                        CopyTileIntoCanvas(images[tileIndex], canvas, rect);
                        tileIndex++;
                        x += width + TestPictureLayout.TileGapPixels;
                    }
                }
                DrawTestAnalysisHeader(
                    canvas,
                    subject.Animation?.Name,
                    (float)(data.Pelvis.Length / SessionFrameRate),
                    capturedAt);
                canvas.Apply(false, false);
                    string signature = BuildPictureSignature(new[] { subject }, "legacy-diagnostic", requestedResolution);
                string imagePath = Path.Combine(EvidenceFolder(session), $"analysis_picture_{signature}.png");
                Directory.CreateDirectory(EvidenceFolder(session));
                File.WriteAllBytes(imagePath, canvas.EncodeToPNG());

                var descriptions = new JArray();
                for (int index = 0; index < tiles.Count; index++)
                {
                    PictureTile tile = tiles[index];
                    JObject description = (JObject)tile.Description.DeepClone();
                    if (tile.Presentation == "test_pose") description["frame"] = tile.Frame;
                    descriptions.Add(new JObject
                    {
                        ["id"] = $"{(index < 4 ? 1 : index < 12 ? 2 : 3)}-{(index < 4 ? index + 1 : index < 12 ? index - 3 : index - 11)}",
                        ["rect"] = new JObject { ["x"] = rects[index].x, ["y"] = rects[index].y, ["width"] = rects[index].width, ["height"] = rects[index].height },
                        ["description"] = description
                    });
                }
                var result = new JObject
                {
                    ["picture"] = new JObject { ["output"] = "composite" },
                    ["render_version"] = UnifiedAnalysisPictureRenderVersion,
                    ["image_path"] = ToProjectRelativePath(imagePath),
                    ["width"] = layout.CanvasWidth,
                    ["height"] = layout.CanvasHeight,
                    ["resolution"] = requestedResolution,
                    ["aspect"] = "16:9",
                    ["header_height"] = layout.HeaderHeight,
                    ["screenshot_generated_at"] = capturedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    ["tile_count"] = tiles.Count,
                    ["title"] = new JObject
                    {
                        ["animation_name"] = subject.Animation?.Name ?? string.Empty,
                        ["duration_seconds"] = data.Pelvis.Length / SessionFrameRate,
                        ["guid"] = subject.Animation?.Id.ToString("D") ?? string.Empty
                    },
                    ["header"] = new JObject
                    {
                        ["clip"] = subject.Animation?.Name ?? string.Empty,
                        ["frame_count"] = data.Pelvis.Length,
                        ["fps"] = SessionFrameRate,
                        ["duration_seconds"] = data.Pelvis.Length / SessionFrameRate,
                        ["tile_type"] = "analysis-test",
                        ["screenshot_generated_at"] = capturedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    },
                    ["headers"] = new JArray
                    {
                        new JObject
                        {
                            ["subject"] = subject.Character.Name,
                            ["clip"] = subject.Animation?.Name ?? string.Empty,
                            ["total_frames"] = data.Pelvis.Length,
                            ["fps"] = SessionFrameRate,
                            ["duration_seconds"] = data.Pelvis.Length / SessionFrameRate,
                            ["screenshot_generated_at"] = capturedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                            ["tile_types"] = new JArray("3d_track", "height_time_track", "3d_ghost", "3d_ghost_track", "test_pose"),
                            ["rect"] = new JObject { ["x"] = 0, ["y"] = 0, ["width"] = layout.CanvasWidth, ["height"] = layout.CanvasHeight }
                        }
                    },
                    ["images"] = descriptions,
                    ["cached"] = false
                };
                PersistPictureSummary(session, subject.Record, result);
                return result;
            }
            finally
            {
                foreach (Texture2D image in images) if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (canvas != null) UnityEngine.Object.DestroyImmediate(canvas);
                foreach ((Renderer renderer, bool enabled) in hiddenCharacterRenderers)
                {
                    if (renderer != null) renderer.enabled = enabled;
                }
                RenderSettings.fog = previousFogEnabled;
                captureSessionRoot = previousCaptureRoot;
            }
        }

        private static Texture2D CreateEmptyTestTile(int width, int height)
        {
            var texture = new Texture2D(Math.Max(1, width), Math.Max(1, height), TextureFormat.RGBA32, false);
            Fill(texture, new Color(.04f, .05f, .07f, 1f));
            texture.Apply(false, false);
            return texture;
        }

        private static void CopyTileIntoCanvas(Texture2D source, Texture2D canvas, RectInt rect)
        {
            if (source == null || canvas == null) return;
            bool canCopy = SystemInfo.copyTextureSupport != CopyTextureSupport.None &&
                source.format == canvas.format && source.width == rect.width && source.height == rect.height;
            if (canCopy)
            {
                Graphics.CopyTexture(source, 0, 0, 0, 0, rect.width, rect.height,
                    canvas, 0, 0, rect.x, rect.y);
                return;
            }
            // Fallback for older Unity graphics backends.
            canvas.SetPixels(rect.x, rect.y, rect.width, rect.height, source.GetPixels());
        }

        private static Bounds CalculateTestContentBounds(SubjectPictureData subject)
        {
            if (subject == null || subject.Pelvis == null || subject.Pelvis.Length == 0)
            {
                return new Bounds(Vector3.up, Vector3.one);
            }

            Bounds bounds = new Bounds(subject.Pelvis[0], Vector3.zero);
            // The viewport follows the sampled trajectory markers directly.
            // Do not extrapolate a body envelope from first/last mesh bounds;
            // that mixes a world-space mesh box with every Hips sample.
            foreach (Vector3 point in subject.Pelvis) bounds.Encapsulate(point);
            foreach (Vector3 point in subject.LeftHand) bounds.Encapsulate(point);
            foreach (Vector3 point in subject.RightHand) bounds.Encapsulate(point);
            foreach (Vector3 point in subject.LeftFoot) bounds.Encapsulate(point);
            foreach (Vector3 point in subject.RightFoot) bounds.Encapsulate(point);
            foreach (Vector3 point in subject.Head) bounds.Encapsulate(point);

            return bounds;
        }

        private static void CalculateTestViewExtents(
            SubjectPictureData subject,
            Vector3 direction,
            out Vector3 viewCenter,
            out float maxHorizontal,
            out float maxVertical,
            out float maxDepth)
        {
            CalculateTestViewExtents(
                subject.Pelvis.Concat(subject.LeftHand)
                    .Concat(subject.RightHand).Concat(subject.LeftFoot).Concat(subject.RightFoot)
                    .Concat(subject.Head),
                direction,
                out viewCenter,
                out maxHorizontal,
                out maxVertical,
                out maxDepth);
        }

        private static void CalculateTestViewExtents(
            Bounds bounds,
            Vector3 direction,
            out Vector3 viewCenter,
            out float maxHorizontal,
            out float maxVertical,
            out float maxDepth)
        {
            CalculateTestViewExtents(
                new[]
                {
                    new Vector3(bounds.min.x, bounds.min.y, bounds.min.z),
                    new Vector3(bounds.min.x, bounds.min.y, bounds.max.z),
                    new Vector3(bounds.min.x, bounds.max.y, bounds.min.z),
                    new Vector3(bounds.min.x, bounds.max.y, bounds.max.z),
                    new Vector3(bounds.max.x, bounds.min.y, bounds.min.z),
                    new Vector3(bounds.max.x, bounds.min.y, bounds.max.z),
                    new Vector3(bounds.max.x, bounds.max.y, bounds.min.z),
                    new Vector3(bounds.max.x, bounds.max.y, bounds.max.z)
                },
                direction,
                out viewCenter,
                out maxHorizontal,
                out maxVertical,
                out maxDepth);
        }

        private static void CalculateTestViewExtents(
            IEnumerable<Vector3> points,
            Vector3 direction,
            out Vector3 viewCenter,
            out float maxHorizontal,
            out float maxVertical,
            out float maxDepth)
        {
            Vector3 normalizedDirection = direction.sqrMagnitude > .0001f
                ? direction.normalized
                : new Vector3(1f, .75f, -1f).normalized;
            Vector3 up = Mathf.Abs(Vector3.Dot(normalizedDirection, Vector3.up)) > .95f
                ? Vector3.forward
                : Vector3.up;
            Quaternion inverseView = Quaternion.Inverse(Quaternion.LookRotation(-normalizedDirection, up));
            float minHorizontal = float.PositiveInfinity;
            float maxHorizontalValue = float.NegativeInfinity;
            float minVertical = float.PositiveInfinity;
            float maxVerticalValue = float.NegativeInfinity;
            float minDepth = float.PositiveInfinity;
            float maxDepthValue = float.NegativeInfinity;
            foreach (Vector3 point in points)
            {
                Vector3 local = inverseView * point;
                minHorizontal = Mathf.Min(minHorizontal, local.x);
                maxHorizontalValue = Mathf.Max(maxHorizontalValue, local.x);
                minVertical = Mathf.Min(minVertical, local.y);
                maxVerticalValue = Mathf.Max(maxVerticalValue, local.y);
                minDepth = Mathf.Min(minDepth, local.z);
                maxDepthValue = Mathf.Max(maxDepthValue, local.z);
            }

            if (float.IsPositiveInfinity(minHorizontal))
            {
                viewCenter = Vector3.zero;
                maxHorizontal = maxVertical = maxDepth = 0f;
                return;
            }

            Vector3 localCenter = new Vector3(
                (minHorizontal + maxHorizontalValue) * .5f,
                (minVertical + maxVerticalValue) * .5f,
                (minDepth + maxDepthValue) * .5f);
            viewCenter = Quaternion.Inverse(inverseView) * localCenter;
            maxHorizontal = (maxHorizontalValue - minHorizontal) * .5f;
            maxVertical = (maxVerticalValue - minVertical) * .5f;
            maxDepth = (maxDepthValue - minDepth) * .5f;
        }

        private static Vector3[] ExpandPosePointsAwayFromHipsInCameraSpace(
            IReadOnlyList<Vector3> points,
            Vector3 direction,
            Vector3 characterForward)
        {
            if (points == null || points.Count == 0) return Array.Empty<Vector3>();
            Vector3 normalizedDirection = direction.sqrMagnitude > .0001f
                ? direction.normalized
                : new Vector3(1f, .75f, -1f).normalized;
            Vector3 up = Mathf.Abs(Vector3.Dot(normalizedDirection, Vector3.up)) > .95f
                ? Vector3.forward
                : Vector3.up;
            Quaternion toCameraSpace = Quaternion.Inverse(Quaternion.LookRotation(-normalizedDirection, up));
            Quaternion toWorldSpace = Quaternion.Inverse(toCameraSpace);
            Vector3 hips = toCameraSpace * points[0];
            Vector3 forward = toCameraSpace * characterForward;
            var expanded = new List<Vector3>(points.Count * 2 + 2);
            expanded.AddRange(points);
            for (int index = 1; index < points.Count; index++)
            {
                Vector3 joint = toCameraSpace * points[index];
                Vector3 fromHips = joint - hips;
                if (fromHips.sqrMagnitude > .0001f)
                {
                    float offset = index == points.Count - 1 ? TestPoseHeadCameraOffsetMeters : TestPoseJointCameraOffsetMeters;
                    joint += fromHips.normalized * offset;
                }
                expanded.Add(toWorldSpace * joint);
                if ((index == 5 || index == 6) && forward.sqrMagnitude > .0001f)
                {
                    expanded.Add(toWorldSpace * (joint + forward.normalized * TestPoseFootForwardCameraOffsetMeters));
                }
            }
            return expanded.ToArray();
        }

        private static void PersistPictureSummary(TimelineSessionRecord session, AnalysisCacheRecord record, JObject pictures)
        {
            if (record == null || session == null) return;
            record.Pictures = pictures != null ? (JObject)pictures.DeepClone() : new JObject();
            AnalysisCache[record.Id] = record;
            WriteJsonAtomically(AnalysisCachePath(session, record.Id), record.ToJson());
        }

        private static string BuildPictureSignature(
            IReadOnlyList<AnalysisSubject> subjects,
            string pictureKey,
            int requestedResolution)
        {
            string source = UnifiedAnalysisPictureRenderVersion + "|" + pictureKey + "|" + requestedResolution + "|" + PictureSupersample + "|" +
                string.Join("|", subjects.Select(item => item.Record.Id));
            using (SHA256 hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(source))).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
            }
        }

        private static SubjectPictureData BuildSubjectPictureData(TimelineSessionRecord session, AnalysisSubject subject)
        {
            if (!IsHumanoidCharacter(subject.Character))
            {
                return BuildMeshSubjectPictureData(subject);
            }

            int frameCount = Math.Max(1, subject.EndFrameExclusive - subject.StartFrame);
            var pelvis = new Vector3[frameCount];
            var leftHand = new Vector3[frameCount];
            var rightHand = new Vector3[frameCount];
            var leftFoot = new Vector3[frameCount];
            var rightFoot = new Vector3[frameCount];
            var leftElbow = new Vector3[frameCount];
            var rightElbow = new Vector3[frameCount];
            var leftKnee = new Vector3[frameCount];
            var rightKnee = new Vector3[frameCount];
            var head = new Vector3[frameCount];
            Bounds firstBounds = default;
            Bounds lastBounds = default;
            Bounds allPoseBounds = default;
            bool hasPoseBounds = false;
            double originalTime = session.Director.time;
            EvaluatedPosePreview posePreview = null;
            KimodoMarkerSampleResult[] samples;
            try
            {
                samples = CaptureCachedSampleResults(subject.Record, subject.Character, subject.StartFrame, frameCount);
                posePreview = CreatePipelinePosePreview(subject.Character, samples[0]);
                Animator poseAnimator = posePreview.Animator
                    ?? throw new InvalidOperationException($"Character '{subject.Character.Name}' pose preview has no Animator.");
                for (int localFrame = 0; localFrame < frameCount; localFrame++)
                {
                    KimodoMarkerSampleResult sample = samples[localFrame];
                    posePreview.Apply(sample);

                    Transform hips = poseAnimator.GetBoneTransform(HumanBodyBones.Hips);
                    if (hips == null)
                    {
                        throw new InvalidOperationException($"Character '{subject.Character.Name}' has no Humanoid Hips transform.");
                    }
                    pelvis[localFrame] = hips.position;
                    leftHand[localFrame] = ReadHumanoidBonePosition(poseAnimator, HumanBodyBones.LeftHand, subject.Character.Name);
                    rightHand[localFrame] = ReadHumanoidBonePosition(poseAnimator, HumanBodyBones.RightHand, subject.Character.Name);
                    leftFoot[localFrame] = ReadHumanoidBonePosition(poseAnimator, HumanBodyBones.LeftFoot, subject.Character.Name);
                    rightFoot[localFrame] = ReadHumanoidBonePosition(poseAnimator, HumanBodyBones.RightFoot, subject.Character.Name);
                    leftElbow[localFrame] = ReadHumanoidBonePosition(poseAnimator, HumanBodyBones.LeftLowerArm, subject.Character.Name);
                    rightElbow[localFrame] = ReadHumanoidBonePosition(poseAnimator, HumanBodyBones.RightLowerArm, subject.Character.Name);
                    leftKnee[localFrame] = ReadHumanoidBonePosition(poseAnimator, HumanBodyBones.LeftLowerLeg, subject.Character.Name);
                    rightKnee[localFrame] = ReadHumanoidBonePosition(poseAnimator, HumanBodyBones.RightLowerLeg, subject.Character.Name);
                    head[localFrame] = ReadHumanoidBonePosition(poseAnimator, HumanBodyBones.Head, subject.Character.Name);
                    Bounds currentBounds = CalculateSkinnedBounds(posePreview.Root);
                    if (localFrame == 0) firstBounds = currentBounds;
                    if (localFrame == frameCount - 1) lastBounds = currentBounds;
                    if (!hasPoseBounds)
                    {
                        allPoseBounds = currentBounds;
                        hasPoseBounds = true;
                    }
                    else
                    {
                        allPoseBounds.Encapsulate(currentBounds);
                    }
                }
            }
            finally
            {
                posePreview?.Dispose();
                session.Director.time = originalTime;
                session.Director.Evaluate();
            }

            bool[] leftContacts = new bool[frameCount];
            bool[] rightContacts = new bool[frameCount];
            string motionPath = ProjectRelativePathToAbsolute(subject.Record.MotionPath);
            if (File.Exists(motionPath) && KimodoRawMotionUtility.TryParseFlatBuffer(File.ReadAllBytes(motionPath), out KimodoRawMotionData motion, out _))
            {
                int count = Math.Min(frameCount, motion.FrameCount);
                for (int frame = 0; frame < count; frame++)
                {
                    motion.TryReadFootContact(frame, 0, out float leftHeel);
                    motion.TryReadFootContact(frame, 1, out float leftToe);
                    motion.TryReadFootContact(frame, 2, out float rightHeel);
                    motion.TryReadFootContact(frame, 3, out float rightToe);
                    leftContacts[frame] = leftHeel >= .5f || leftToe >= .5f;
                    rightContacts[frame] = rightHeel >= .5f || rightToe >= .5f;
                }
            }

            // Keep the legacy bounds for non-test mesh evidence. The complete
            // pose bounds are exposed separately for the test renderer.
            Bounds bounds = firstBounds;
            foreach (Vector3 point in pelvis) bounds.Encapsulate(point);
            bounds.Encapsulate(lastBounds);
            bounds.Expand(new Vector3(6f, 1f, 6f));
            if (bounds.size.x < 6f) bounds.Expand(new Vector3(6f - bounds.size.x, 0f, 0f));
            if (bounds.size.z < 6f) bounds.Expand(new Vector3(0f, 0f, 6f - bounds.size.z));
            Bounds testBounds = hasPoseBounds ? allPoseBounds : bounds;
            foreach (Vector3 point in pelvis) testBounds.Encapsulate(point);
            return new SubjectPictureData(
                subject,
                samples,
                pelvis,
                leftHand,
                rightHand,
                leftFoot,
                rightFoot,
                leftElbow,
                rightElbow,
                leftKnee,
                rightKnee,
                head,
                leftContacts,
                rightContacts,
                firstBounds,
                lastBounds,
                bounds,
                testBounds);
        }

        private static SubjectPictureData BuildMeshSubjectPictureData(AnalysisSubject subject)
        {
            int frameCount = Math.Max(1, subject.EndFrameExclusive - subject.StartFrame);
            var positions = new Vector3[frameCount];
            Bounds firstBounds = default;
            Bounds lastBounds = default;
            Bounds allBounds = default;
            bool hasBounds = false;
            for (int localFrame = 0; localFrame < frameCount; localFrame++)
            {
                GameObject preview = CreateMeshPosePreview(subject, localFrame);
                try
                {
                    positions[localFrame] = preview.transform.position;
                    Bounds current = CalculateSkinnedBounds(preview);
                    if (localFrame == 0) firstBounds = current;
                    if (localFrame == frameCount - 1) lastBounds = current;
                    if (!hasBounds)
                    {
                        allBounds = current;
                        hasBounds = true;
                    }
                    else
                    {
                        allBounds.Encapsulate(current);
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(preview);
                }
            }

            Bounds bounds = hasBounds ? allBounds : CalculateBounds(subject.Character.Root);
            bounds.Expand(new Vector3(1f, .5f, 1f));
            return new SubjectPictureData(
                subject,
                null,
                positions,
                positions,
                positions,
                positions,
                positions,
                positions,
                positions,
                positions,
                positions,
                positions,
                new bool[frameCount],
                new bool[frameCount],
                firstBounds,
                lastBounds,
                bounds,
                bounds);
        }

        private static Vector3 ReadHumanoidBonePosition(Animator animator, HumanBodyBones bone, string characterName)
        {
            if (animator == null)
            {
                throw new InvalidOperationException($"Character '{characterName}' preview has no Animator while reading {bone}.");
            }
            Transform transform = animator.GetBoneTransform(bone);
            if (transform == null)
            {
                throw new InvalidOperationException($"Character '{characterName}' has no Humanoid {bone} transform.");
            }
            return transform.position;
        }

        private static Bounds CalculateSkinnedBounds(GameObject root)
        {
            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) return CalculateBounds(root);
            Bounds result = renderers[0].bounds;
            for (int index = 1; index < renderers.Length; index++) result.Encapsulate(renderers[index].bounds);
            return result;
        }

        private static List<PictureTile> BuildPictureTiles(SubjectPictureData subject, AnalysisPictureRequest request)
        {
            if (!IsHumanoidCharacter(subject.Subject.Character))
            {
                return SelectKeyFrames(subject, subject.KeyframeCount)
                    .Select(frame => PictureTile.MeshPose(subject, frame, "mesh_pose"))
                    .ToList();
            }

            var result = new List<PictureTile>();
            if (request.Includes("3d_track")) result.Add(PictureTile.TestOverview(subject, "3d_track", Vector3.up));
            if (request.Includes("height_time_track")) result.Add(PictureTile.TestOverview(subject, "height_time_track", Vector3.up));
            IReadOnlyList<int?> keyframes = NormalizeTestKeyframes(subject, 8);
            IReadOnlyList<int?> steps = NormalizeTestStepFrames(subject, 8);
            if (request.Includes("3d_ghost"))
            {
                result.Add(PictureTile.TestSelectedOverview(subject, "3d_ghost", keyframes.Where(value => value.HasValue).Select(value => value.Value), new Vector3(1f, .75f, -1f)));
            }
            if (request.Includes("3d_ghost_track"))
            {
                result.Add(PictureTile.TestSelectedOverview(subject, "3d_ghost_track", steps.Where(value => value.HasValue).Select(value => value.Value), new Vector3(1f, .75f, -1f)));
            }
            if (request.Includes("key_pose"))
            {
                for (int index = 0; index < keyframes.Count; index++) result.Add(PictureTile.TestPoseSlot(subject, keyframes[index], "key_pose", index + 1));
            }
            if (request.Includes("step_pose"))
            {
                for (int index = 0; index < steps.Count; index++) result.Add(PictureTile.TestPoseSlot(subject, steps[index], "step_pose", index + 1));
            }
            return result;
        }

        private static List<int> SelectKeyFrames(SubjectPictureData subject, int count)
        {
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            return (subject.Subject.Record.Analysis?["keyframes"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(item => Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, lastFrame))
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();
        }

        private sealed class PhaseSegment
        {
            public int StartFrame;
            public int EndFrame;
            public int AnchorFrame;
            public float MeanChange;
            public float UpperBodyActivity;
            public float RootSpeed;
            public int FootEventCount;
            public bool Transition;
        }

        private sealed class PhaseFeature
        {
            public Vector3[] Points;
            public float Speed;
            public float UpperBodySpeed;
            public bool LeftContact;
            public bool RightContact;
        }

        private static void EnsurePhaseTrack(SubjectPictureData subject)
        {
            if (subject == null || subject.Pelvis == null || subject.Pelvis.Length == 0 ||
                !IsHumanoidCharacter(subject.Subject.Character)) return;

            JArray phases = BuildPhaseTrack(subject);
            JObject analysis = subject.Subject.Record.Analysis ?? new JObject();
            analysis["phase_track_version"] = PhaseTrackVersion;
            analysis["phase_track"] = phases;
            if (analysis["motion_profile"] is JObject profile &&
                subject.Subject.Record.RootTrajectory?["samples"] is JArray trajectorySamples)
            {
                var headings = phases.OfType<JObject>()
                    .Select(phase => phase.Value<int?>("anchor_frame") ?? 0)
                    .Select(frame => trajectorySamples.OfType<JObject>().FirstOrDefault(sample =>
                        sample.Value<int?>("frame") == frame)?["heading_xz"] as JArray)
                    .Where(value => value != null && value.Count >= 2)
                    .Select(value => Mathf.Atan2(value[0].Value<float>(), value[1].Value<float>()) * Mathf.Rad2Deg)
                    .ToArray();
                float firstHeading = headings.Length > 0 ? headings[0] : 0f;
                bool consistent = headings.Length > 0 && headings.All(value => Mathf.Abs(Mathf.DeltaAngle(firstHeading, value)) <= 8f);
                profile["phase_anchor_heading_consistent"] = consistent;
                profile["phase_anchor_heading_sample_count"] = headings.Length;
            }
            analysis["keyframes"] = new JArray(phases.OfType<JObject>().Select(item => new JObject
            {
                ["frame"] = item.Value<int>("anchor_frame"),
                ["local_frame_60"] = item.Value<int>("anchor_frame"),
                ["timeline_frame_60"] = subject.Subject.StartFrame + item.Value<int>("anchor_frame"),
                ["local_time_seconds"] = item.Value<int>("anchor_frame") / SessionFrameRate,
                ["time_seconds"] = (subject.Subject.StartFrame + item.Value<int>("anchor_frame")) / SessionFrameRate,
                ["phase_index"] = item.Value<int>("phase_index"),
                ["kind"] = item.Value<string>("kind")
            }));
            subject.Subject.Record.Analysis = analysis;
            subject.RefreshKeyframes();
        }

        private static JArray BuildPhaseTrack(SubjectPictureData subject)
        {
            int frameCount = subject.Pelvis.Length;
            PhaseFeature[] features = Enumerable.Range(0, frameCount)
                .Select(frame => BuildPhaseFeature(subject, frame)).ToArray();
            var changes = new float[Math.Max(0, frameCount - 1)];
            for (int frame = 1; frame < frameCount; frame++) changes[frame - 1] =
                PhaseFeatureDistance(features[frame - 1], features[frame]);
            float threshold = Mathf.Clamp(Mathf.Max(.06f, Median(changes) * 3f), .08f, .8f);
            int minimumFrames = Mathf.Clamp(Mathf.RoundToInt((float)SessionFrameRate * .2f), 6, 18);
            var segments = new List<PhaseSegment>();
            int start = 0;
            for (int frame = 1; frame < frameCount; frame++)
            {
                bool contactChanged = features[frame].LeftContact != features[frame - 1].LeftContact ||
                    features[frame].RightContact != features[frame - 1].RightContact;
                bool boundary = frame - start >= minimumFrames &&
                    (changes[frame - 1] > threshold || (contactChanged && changes[frame - 1] > threshold * .65f));
                if (!boundary) continue;
                segments.Add(CreatePhaseSegment(features, changes, start, frame - 1, threshold));
                start = frame;
            }
            segments.Add(CreatePhaseSegment(features, changes, start, frameCount - 1, threshold));
            MergeShortPhaseSegments(features, changes, segments, minimumFrames, threshold);
            for (int index = 0; index < segments.Count; index++)
            {
                if (segments[index].StartFrame != (index == 0 ? 0 : segments[index - 1].EndFrame + 1) ||
                    segments[index].EndFrame < segments[index].StartFrame ||
                    segments[index].AnchorFrame < segments[index].StartFrame ||
                    segments[index].AnchorFrame > segments[index].EndFrame)
                {
                    throw new InvalidOperationException("Phase-track clustering produced non-contiguous intervals.");
                }
            }
            if (segments[segments.Count - 1].EndFrame != frameCount - 1)
            {
                throw new InvalidOperationException("Phase-track clustering did not cover the complete clip.");
            }

            var result = new JArray();
            for (int index = 0; index < segments.Count; index++)
            {
                PhaseSegment segment = segments[index];
                int duration = segment.EndFrame - segment.StartFrame + 1;
                result.Add(new JObject
                {
                    ["phase_index"] = index,
                    ["start_frame"] = segment.StartFrame,
                    ["end_frame"] = segment.EndFrame,
                    ["start_frame_60"] = subject.Subject.StartFrame + segment.StartFrame,
                    ["end_frame_60"] = subject.Subject.StartFrame + segment.EndFrame,
                    ["duration_frames"] = duration,
                    ["duration_seconds"] = duration / SessionFrameRate,
                    ["anchor_frame"] = segment.AnchorFrame,
                    ["anchor_frame_60"] = subject.Subject.StartFrame + segment.AnchorFrame,
                    ["anchor_time_seconds"] = (subject.Subject.StartFrame + segment.AnchorFrame) / SessionFrameRate,
                    ["kind"] = segment.Transition ? "transition" : "phase",
                    ["confidence"] = segment.MeanChange <= threshold ? "high" : "medium",
                    ["mean_feature_change"] = segment.MeanChange,
                    ["mean_root_speed"] = segment.RootSpeed,
                    ["upper_body_activity"] = segment.UpperBodyActivity,
                    ["foot_event_count"] = segment.FootEventCount
                });
            }
            return result;
        }

        private static PhaseFeature BuildPhaseFeature(SubjectPictureData subject, int frame)
        {
            int previous = Mathf.Max(0, frame - 1);
            Vector3 pelvis = subject.Pelvis[frame];
            return new PhaseFeature
            {
                Points = new[]
                {
                    subject.LeftHand[frame] - pelvis,
                    subject.RightHand[frame] - pelvis,
                    subject.LeftElbow[frame] - pelvis,
                    subject.RightElbow[frame] - pelvis,
                    subject.LeftFoot[frame] - pelvis,
                    subject.RightFoot[frame] - pelvis,
                    subject.Head[frame] - pelvis
                },
                Speed = Vector3.Distance(subject.Pelvis[frame], subject.Pelvis[previous]) * (float)SessionFrameRate,
                UpperBodySpeed = (
                    Vector3.Distance(subject.LeftHand[frame], subject.LeftHand[previous]) +
                    Vector3.Distance(subject.RightHand[frame], subject.RightHand[previous]) +
                    Vector3.Distance(subject.LeftElbow[frame], subject.LeftElbow[previous]) +
                    Vector3.Distance(subject.RightElbow[frame], subject.RightElbow[previous])) *
                    (float)SessionFrameRate,
                LeftContact = subject.LeftContacts[frame],
                RightContact = subject.RightContacts[frame]
            };
        }

        private static float PhaseFeatureDistance(PhaseFeature first, PhaseFeature second)
        {
            float distance = 0f;
            for (int index = 0; index < first.Points.Length; index++)
                distance += Vector3.Distance(first.Points[index], second.Points[index]);
            distance += Mathf.Abs(first.Speed - second.Speed) * .05f;
            if (first.LeftContact != second.LeftContact) distance += .6f;
            if (first.RightContact != second.RightContact) distance += .6f;
            return distance / (first.Points.Length + 1f);
        }

        private static PhaseSegment CreatePhaseSegment(
            PhaseFeature[] features,
            float[] changes,
            int start,
            int end,
            float threshold)
        {
            int anchor = start;
            float best = float.PositiveInfinity;
            float meanChange = 0f;
            float rootSpeed = 0f;
            float upperBodyActivity = 0f;
            int footEvents = 0;
            int changeCount = 0;
            for (int frame = start; frame <= end; frame++)
            {
                rootSpeed += features[frame].Speed;
                upperBodyActivity += features[frame].UpperBodySpeed;
                if (frame > start &&
                    (features[frame].LeftContact != features[frame - 1].LeftContact ||
                     features[frame].RightContact != features[frame - 1].RightContact)) footEvents++;
                float distance = 0f;
                if (frame > start) { distance += changes[frame - 1]; meanChange += changes[frame - 1]; changeCount++; }
                if (frame < end) distance += changes[frame];
                if (distance < best) { best = distance; anchor = frame; }
            }
            meanChange /= Mathf.Max(1, changeCount);
            return new PhaseSegment
            {
                StartFrame = start,
                EndFrame = end,
                AnchorFrame = anchor,
                MeanChange = meanChange,
                RootSpeed = rootSpeed / Mathf.Max(1, end - start + 1),
                UpperBodyActivity = upperBodyActivity / Mathf.Max(1, end - start + 1),
                FootEventCount = footEvents,
                Transition = meanChange > threshold * 1.25f
            };
        }

        private static void MergeShortPhaseSegments(
            PhaseFeature[] features,
            float[] changes,
            List<PhaseSegment> segments,
            int minimumFrames,
            float threshold)
        {
            for (int index = 0; index < segments.Count && segments.Count > 1; index++)
            {
                PhaseSegment segment = segments[index];
                if (segment.EndFrame - segment.StartFrame + 1 >= minimumFrames) continue;
                int neighbor = index == 0 ? 1 : index - 1;
                int mergedStart = Mathf.Min(segments[neighbor].StartFrame, segment.StartFrame);
                int mergedEnd = Mathf.Max(segments[neighbor].EndFrame, segment.EndFrame);
                PhaseSegment merged = CreatePhaseSegment(features, changes, mergedStart, mergedEnd, threshold);
                int insertAt = Mathf.Min(index, neighbor);
                segments.RemoveAt(Mathf.Max(index, neighbor));
                segments.RemoveAt(Mathf.Min(index, neighbor));
                segments.Insert(insertAt, merged);
                index = Mathf.Max(-1, index - 2);
            }
        }

        private static float Median(float[] values)
        {
            if (values == null || values.Length == 0) return 0f;
            float[] ordered = values.OrderBy(value => value).ToArray();
            int middle = ordered.Length / 2;
            return ordered.Length % 2 == 0 ? (ordered[middle - 1] + ordered[middle]) * .5f : ordered[middle];
        }

    }
}
