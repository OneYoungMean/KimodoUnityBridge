using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private static SceneAnalysisLayout CreateSceneAnalysisLayout(
            TimelineSessionRecord session,
            AnalysisSubject subject,
            int width,
            JObject cameraOptions)
        {
            SubjectPictureData data = BuildSubjectPictureData(session, subject);
            EnsurePhaseTrack(data);
            GameObject previousRoot = captureSessionRoot;
            var layout = new SceneAnalysisLayout();
            try
            {
                captureSessionRoot = session.SessionRoot;
                layout.Initialize(
                    data,
                    width,
                    KimodoAnimationAnalysisResponseBuilder.BuildClipResult(
                        CreateAnalysisResponseData(subject),
                        IsHumanoidCharacter(subject.Character)),
                    cameraOptions);
                return layout;
            }
            catch { layout.Dispose(); throw; }
            finally { captureSessionRoot = previousRoot; }
        }

        // Owns layout objects across real Editor repaints; no camera or render pass is created here.
        private sealed class SceneAnalysisLayout : IDisposable
        {
            public List<PictureTile> Tiles;
            public Texture2D Canvas;
            public JObject Clip;
            public JObject Metadata;
            public int Index;
            private readonly List<RectInt> rects = new List<RectInt>();
            private readonly List<List<GameObject>> lines = new List<List<GameObject>>();
            private readonly List<Tuple<Renderer, bool>> hidden = new List<Tuple<Renderer, bool>>();
            private readonly List<AnalysisPreviewPose> visible = new List<AnalysisPreviewPose>();
            private readonly Dictionary<int, Vector3> heightTargets = new Dictionary<int, Vector3>();
            private readonly List<Material> ownedMaterials = new List<Material>();
            private readonly List<Mesh> ownedMeshes = new List<Mesh>();
            private CapturePosePool pool;
            private SubjectPictureData data;
            private List<int> heightFrames;
            private string title;
            private string distanceText;
            private string durationText;
            private bool includeScene;
            private JObject cameraOptions;
            private bool disposed;

            public void Initialize(SubjectPictureData subject, int width, JObject clip, JObject cameraOptions)
            {
                data = subject;
                Clip = clip;
                this.cameraOptions = cameraOptions ?? new JObject();
                includeScene = string.Equals(this.cameraOptions.Value<string>("scope"), "scene", StringComparison.Ordinal);
                DateTimeOffset capturedAt = DateTimeOffset.Now;
                title = "Motion Analysis  |  " + (data.Subject.Animation.Clip != null ? data.Subject.Animation.Clip.name : data.Subject.Animation.Name)
                    + "  |  " + capturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
                double? distance = Clip["root_trajectory"]?.Value<double?>("path_length_xz");
                distanceText = distance.HasValue ? distance.Value.ToString("0.00", CultureInfo.InvariantCulture) + " m" : "N/A";
                // Duration counts all clip frames, rather than the shorter interval between endpoint samples.
                int durationFrames = Clip["root_trajectory"]?.Value<int?>("duration_frames_60")
                    ?? Clip.Value<int>("end_timeline_frame_60_exclusive") - Clip.Value<int>("start_timeline_frame_60");
                durationText = (durationFrames / SessionFrameRate).ToString("0.00", CultureInfo.InvariantCulture) + " s";
                Tiles = BuildAnalysisTiles(data);
                // Preserve authored visibility, including renderers that were already hidden.
                // In the default isolated mode every scene renderer is hidden so
                // unrelated geometry cannot change framing or contaminate a tile.
                // include_scene keeps environment geometry (for example a floor)
                // in the real SceneView while hiding the authored source character.
                foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (!renderer.gameObject.scene.IsValid() || EditorUtility.IsPersistent(renderer)) continue;
                    if (includeScene && data.Subject.Character.Root != null &&
                        !renderer.transform.IsChildOf(data.Subject.Character.Root.transform)) continue;
                    hidden.Add(Tuple.Create(renderer, renderer.forceRenderingOff));
                    renderer.forceRenderingOff = true;
                }
                pool = new CapturePosePool(Tiles);
                int height = Mathf.Max(36, Mathf.RoundToInt(width * 9f / 16f));
                Canvas = new Texture2D(width, height, TextureFormat.RGBA32, false);
                Fill(Canvas, new Color(.12f, .12f, .12f, 1));
                // Scale the original 1920x1080 rectangles so low resolutions still retain all 20 slots.
                for (int row = 0; row < 3; row++)
                {
                    int count = row == 0 ? 4 : 8;
                    int tileWidth = row == 0 ? 474 : 233;
                    int tileHeight = row == 0 ? 356 : 332;
                    int x = (1920 - count * tileWidth - 8 * (count - 1)) / 2;
                    int y = row == 0 ? 664 : row == 1 ? 332 : 0;
                    for (int col = 0; col < count; col++)
                    {
                        int left = Mathf.RoundToInt(x * width / 1920f), bottom = Mathf.RoundToInt(y * height / 1080f);
                        rects.Add(new RectInt(left, bottom, Mathf.Max(1, Mathf.RoundToInt((x + tileWidth) * width / 1920f) - left),
                            Mathf.Max(1, Mathf.RoundToInt((y + tileHeight) * height / 1080f) - bottom)));
                        x += tileWidth + 8;
                    }
                }
                for (int index = 0; index < Tiles.Count; index++) lines.Add(new List<GameObject>());
                Vector3[] ground = data.Pelvis.Select(point => new Vector3(point.x, 0, point.z)).ToArray();
                CreateMetricLine(lines[0], ground, BuildGroundSpeeds(ground), 7f, .06f);
                foreach (int frame in Tiles[0].TrajectoryFrames.Union(Tiles[0].PrimaryFrames))
                {
                    bool primary = Tiles[0].PrimaryFrames.Contains(frame);
                    Color color = !primary ? Color.gray : frame == 0 ? AnalysisStartFrameTint : frame == ground.Length - 1 ? AnalysisEndFrameTint : AnalysisKeyframeTint;
                    CreateGroundMarker(lines[0], ground[frame], primary ? .13f : .08f);
                    if (primary) CreateHeadingArrow(lines[0], ground[frame], SampleRootForward(data, frame), .45f, color);
                }
                heightFrames = BuildHeightTimePoseFrames(data);
                float length = CalculateHeightTimeLength(data, heightFrames, rects[1].width / (float)rects[1].height);
                var curve = data.Pelvis.Select((point, frame) => new Vector3(0, point.y, frame / (float)Mathf.Max(1, data.Pelvis.Length - 1) * length)).ToArray();
                foreach (int frame in heightFrames)
                    heightTargets[frame] = curve[frame] + pool.GetPlan(data).Get(frame).RootPosition - data.Pelvis[frame];
                CreateMetricLine(lines[1], curve, BuildPelvisAccelerations(data.Pelvis), 10f, .035f);
                Bounds heightBounds = new Bounds(curve[0], Vector3.zero);
                foreach (int frame in heightFrames)
                {
                    Bounds bounds = CalculateRawPreviewPoseBounds(data, frame);
                    bounds.center += heightTargets[frame] - pool.GetPlan(data).Get(frame).RootPosition;
                    heightBounds.Encapsulate(bounds);
                }
                for (int grid = 0; grid <= 5; grid++)
                {
                    float z = grid * length / 5;
                    float y = Mathf.Lerp(heightBounds.min.y, heightBounds.max.y, grid / 5f);
                    CreateWorldLine(lines[1], new Vector3(0, heightBounds.min.y, z), new Vector3(0, heightBounds.max.y, z), .015f, Color.gray);
                    CreateWorldLine(lines[1], new Vector3(0, y, 0), new Vector3(0, y, length), .015f, Color.gray);
                }
                CreateAnalysisBodyTrajectories(lines[3], data);
                CollectOwnedResources();
                foreach (GameObject item in lines.SelectMany(group => group)) item.SetActive(false);
                var descriptions = new JArray();
                for (int index = 0; index < Tiles.Count; index++)
                {
                    RectInt rect = rects[index];
                    descriptions.Add(new JObject
                    {
                        ["id"] = index < 4 ? $"1.{index + 1}" : index < 12 ? $"2.{index - 3}" : $"3.{index - 11}",
                        ["tile"] = Tiles[index].TileType,
                        ["header"] = BuildHeader(index),
                        ["rect"] = new JObject { ["x"] = rect.x, ["y"] = rect.y, ["width"] = rect.width, ["height"] = rect.height },
                        ["description"] = Tiles[index].Description.DeepClone()
                    });
                }
                Metadata = KimodoAnimationAnalysisResponseBuilder.BuildPictureMetadata(new JObject
                {
                    ["tile_count"] = Tiles.Count, ["images"] = descriptions, ["width"] = width, ["height"] = height,
                    ["render_version"] = "animation.analysis.20-sceneview-headers-metrics-ghost-spacing-05m", ["aspect"] = "16:9",
                    ["header_height"] = height - rects[0].yMax
                }, CreateAnalysisResponseData(data.Subject));
                Metadata["title"] = title;
                Metadata["screenshot_generated_at"] = capturedAt.ToString("o", CultureInfo.InvariantCulture);
            }

            private void CollectOwnedResources()
            {
                foreach (GameObject item in lines.SelectMany(group => group))
                {
                    if (item == null) continue;
                    foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>())
                    {
                        ownedMaterials.AddRange(renderer.sharedMaterials
                            .Where(material => material != null && !EditorUtility.IsPersistent(material)));
                    }
                    foreach (MeshFilter filter in item.GetComponentsInChildren<MeshFilter>())
                        if (filter.sharedMesh != null && !EditorUtility.IsPersistent(filter.sharedMesh)) ownedMeshes.Add(filter.sharedMesh);
                }
            }

            private static float[] BuildGroundSpeeds(IReadOnlyList<Vector3> points)
            {
                int count = points?.Count ?? 0;
                var result = new float[count];
                if (count < 2) return result;
                result[0] = Vector3.Distance(points[1], points[0]) * (float)SessionFrameRate;
                for (int index = 1; index < count; index++)
                    result[index] = Vector3.Distance(points[index], points[index - 1]) * (float)SessionFrameRate;
                return result;
            }

            private static float[] BuildPelvisAccelerations(IReadOnlyList<Vector3> points)
            {
                int count = points?.Count ?? 0;
                var result = new float[count];
                if (count < 3) return result;
                float fpsSquared = (float)(SessionFrameRate * SessionFrameRate);
                for (int index = 1; index < count - 1; index++)
                    result[index] = (points[index + 1] - points[index] * 2f + points[index - 1]).magnitude * fpsSquared;
                result[0] = result[1];
                result[count - 1] = result[count - 2];
                return result;
            }

            private IEnumerable<int> FramesFor(int index)
            {
                PictureTile tile = Tiles[index];
                return (tile.IsEmpty || index == 0 ? Enumerable.Empty<int>()
                    : index == 1 ? heightFrames
                    : index >= 4 ? new[] { tile.Frame }
                    : index == 2 ? tile.TrajectoryFrames.Concat(new[] { 0, data.Pelvis.Length - 1 }) : tile.TrajectoryFrames).Distinct();
            }

            private JObject BuildHeader(int index)
            {
                PictureTile tile = Tiles[index];
                string text = index == 0 ? "Ground Path - " + distanceText : index == 1 ? "Height over Time - " + durationText
                    : index == 2 ? $"Key Poses - {FramesFor(index).Count()}"
                    : index == 3 ? $"Step Poses - {FramesFor(index).Count()}"
                    : tile.IsEmpty ? "No Frame" : $"Frame {data.Subject.StartFrame + tile.Frame}";
                Color color = index < 4 || tile.IsEmpty ? new Color(.18f, .22f, .27f, 1)
                    : ResolveAnalysisPoseTint(tile, tile.Frame);
                RectInt rect = rects[index];
                return new JObject
                {
                    ["text"] = text,
                    ["color"] = new JArray(color.r, color.g, color.b, 1f),
                    ["height"] = Mathf.Max(1, Mathf.RoundToInt(rect.height / 8f)),
                    ["character_count"] = FramesFor(index).Count()
                };
            }

            private void DrawHeaders()
            {
                using (var labels = new SceneCaptureLabels())
                {
                    for (int index = 0; index < Tiles.Count; index++)
                    {
                        JObject header = BuildHeader(index);
                        var rgba = (JArray)header["color"];
                        var color = new Color(rgba[0].Value<float>(), rgba[1].Value<float>(), rgba[2].Value<float>(), 1);
                        RectInt tile = rects[index];
                        var strip = new RectInt(tile.x, tile.yMax - header.Value<int>("height"), tile.width, header.Value<int>("height"));
                        FillRect(Canvas, strip.x, strip.y, strip.width, strip.height, color);
                        Color textColor = color.grayscale > .55f ? new Color(.035f, .045f, .055f, 1) : Color.white;
                        labels.Draw(Canvas, strip, header.Value<string>("text"), textColor);
                    }
                    var main = new RectInt(0, rects[0].yMax, Canvas.width, Canvas.height - rects[0].yMax);
                    FillRect(Canvas, main.x, main.y, main.width, main.height, new Color(.025f, .03f, .045f, 1));
                    labels.Draw(Canvas, main, title, Color.white);
                }
            }

            public void ShowNext(SceneView view)
            {
                foreach (AnalysisPreviewPose pose in visible) SetPreviewRenderersEnabled(pose.Preview, false);
                visible.Clear();
                foreach (GameObject item in lines.SelectMany(group => group)) item.SetActive(false);
                foreach (GameObject item in lines[Index]) item.SetActive(true);
                PictureTile tile = Tiles[Index];
                foreach (int frame in FramesFor(Index))
                {
                    AnalysisPreviewPose pose = pool.Get(data, frame);
                    var snapshot = pool.GetPlan(data).Get(frame);
                    pose.Preview.transform.SetPositionAndRotation(Index == 1 ? heightTargets[frame] : snapshot.RootPosition, snapshot.RootRotation);
                    SetPreviewRenderersEnabled(pose.Preview, true);
                    visible.Add(pose);
                }
                Bounds bounds = default;
                bool hasBounds = false;
                if (Index == 0)
                {
                    bounds = new Bounds(new Vector3(data.Pelvis[0].x, 0, data.Pelvis[0].z), Vector3.zero);
                    hasBounds = true;
                    foreach (Vector3 point in data.Pelvis) bounds.Encapsulate(new Vector3(point.x, 0, point.z));
                }
                foreach (AnalysisPreviewPose pose in visible)
                {
                    Bounds poseBounds = CalculateSceneTargetBounds(pose.Preview.transform);
                    if (!hasBounds) { bounds = poseBounds; hasBounds = true; }
                    else bounds.Encapsulate(poseBounds);
                }
                foreach (GameObject item in lines[Index])
                    foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>())
                    {
                        if (!hasBounds) { bounds = renderer.bounds; hasBounds = true; }
                        else bounds.Encapsulate(renderer.bounds);
                    }
                if (!hasBounds) bounds = data.Bounds;
                bounds.Expand(Index == 0 ? new Vector3(.8f, .2f, .8f) : Vector3.one * .1f);
                Vector3 direction = cameraOptions?["direction"] != null
                    ? ReadSceneVector(cameraOptions["direction"], "camera.direction", tile.Direction)
                    : Index == 1 ? Vector3.right : tile.Direction;
                if (direction.sqrMagnitude < .001f) direction = new Vector3(1, .75f, -1);
                FrameSceneView(view, bounds, direction, cameraOptions, rects[Index].width / (float)rects[Index].height);
            }

            public bool AddTile(Texture2D image)
            {
                RectInt rect = rects[Index];
                if (!Tiles[Index].IsEmpty)
                {
                    float aspect = rect.width / (float)rect.height;
                    float cropWidth = Mathf.Min(image.width, image.height * aspect);
                    float cropHeight = Mathf.Min(image.height, image.width / aspect);
                    var pixels = new Color[rect.width * rect.height];
                    for (int y = 0; y < rect.height; y++)
                        for (int x = 0; x < rect.width; x++)
                            pixels[y * rect.width + x] = image.GetPixelBilinear(
                                ((image.width - cropWidth) * .5f + (x + .5f) * cropWidth / rect.width) / image.width,
                                ((image.height - cropHeight) * .5f + (y + .5f) * cropHeight / rect.height) / image.height);
                    Canvas.SetPixels(rect.x, rect.y, rect.width, rect.height, pixels);
                }
                Index++;
                if (Index < Tiles.Count) return false;
                DrawHeaders();
                Canvas.Apply(false, false);
                return true;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                pool?.Dispose();
                CollectOwnedResources();
                foreach (GameObject item in lines.SelectMany(group => group))
                    if (item != null) UnityEngine.Object.DestroyImmediate(item);
                foreach (Material material in ownedMaterials.Distinct()) if (material != null) UnityEngine.Object.DestroyImmediate(material);
                foreach (Mesh mesh in ownedMeshes.Distinct()) if (mesh != null) UnityEngine.Object.DestroyImmediate(mesh);
                foreach (var item in hidden) if (item.Item1 != null) item.Item1.forceRenderingOff = item.Item2;
                if (Canvas != null) UnityEngine.Object.DestroyImmediate(Canvas);
            }
        }
    }
}
