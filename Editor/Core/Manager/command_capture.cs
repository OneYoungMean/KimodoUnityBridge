using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KimodoBridge;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private const int MaxCachedAnalyses = 128;
        private static readonly Dictionary<string, AnalysisCacheRecord> AnalysisCache =
            new Dictionary<string, AnalysisCacheRecord>(StringComparer.OrdinalIgnoreCase);
        private static readonly Queue<string> AnalysisCacheOrder = new Queue<string>();

        private static string CommandCacheFolder => Path.Combine(
            Directory.GetParent(Application.dataPath)?.FullName ?? Application.temporaryCachePath,
            "Library", "KimodoCache", "Commands");

        public static string Capture(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                var requests = new List<CaptureRequest>();
                int selected = (arguments["poses"] != null ? 1 : 0) +
                    (arguments["analysis_id"] != null ? 1 : 0) +
                    (arguments["constraints"] != null ? 1 : 0);
                if (selected != 1)
                {
                    throw new InvalidOperationException("Provide exactly one of poses, analysis_id, or constraints.");
                }
                if (arguments["poses"] is JArray poses)
                {
                    foreach (JObject pose in poses.OfType<JObject>()) requests.Add(BuildCaptureRequest(RequirePoseLocator(pose), null));
                    if (requests.Count != poses.Count) throw new InvalidOperationException("Every poses item must be a {source,frame} object.");
                }
                else if (arguments["analysis_id"] != null)
                {
                    string id = RequiredStringValue(arguments, "analysis_id");
                    AnalysisCacheRecord cached = GetCachedAnalysis(id);
                    string timelineGuid = AssetDatabase.AssetPathToGUID(session.TimelineAssetPath);
                    bool sameTimeline = !string.IsNullOrWhiteSpace(cached.TimelineAssetGuid)
                        ? string.Equals(cached.TimelineAssetGuid, timelineGuid, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(cached.SessionId, session.Id.ToString("D"), StringComparison.OrdinalIgnoreCase);
                    if (!sameTimeline)
                        throw new InvalidOperationException("analysis_source_expired: the analysis belongs to a different Session.");
                    JArray analyzedPoses = cached.Poses ?? new JArray();
                    foreach (JObject item in analyzedPoses.OfType<JObject>())
                        requests.Add(BuildCaptureRequest(RequirePoseLocator(item["pose"] as JObject), item["analysis"] as JObject));
                }
                else if (arguments["constraints"] is JArray constraints)
                {
                    for (int i = 0; i < constraints.Count; i++)
                    {
                        if (constraints[i] is not JObject item)
                        {
                            throw new InvalidOperationException($"constraints[{i}] must be an object.");
                        }
                        int frame = RequiredNonNegativeFrame(item, "frame");
                        string type = RequiredStringValue(item, "type").ToLowerInvariant();
                        if (type != "fullbody" && type != "root2d" && type != "left_hand" &&
                            type != "right_hand" && type != "left_foot" && type != "right_foot")
                        {
                            throw new InvalidOperationException($"constraints[{i}].type is not supported.");
                        }
                        JObject annotation = new JObject { ["constraint_type"] = type, ["frame"] = frame };
                        if (item["pose"] is JObject pose)
                        {
                            requests.Add(BuildCaptureRequest(RequirePoseLocator(pose), annotation));
                        }
                        else if (type == "root2d")
                        {
                            string characterRef = arguments.Value<string>("character")?.Trim();
                            TimelineCharacterRecord character = !string.IsNullOrWhiteSpace(characterRef)
                                ? ResolveSessionCharacterByReference(session, characterRef, false)
                                : session.Characters.Count == 1
                                    ? session.Characters[0]
                                    : throw new InvalidOperationException("character is required to picture a position-only root2d constraint when the Session has multiple characters.");
                            Vector2 position = RequiredVector2(item, "position");
                            Vector2 heading = RequiredVector2(item, "heading");
                            if (heading.sqrMagnitude < 1e-8f) throw new InvalidOperationException($"constraints[{i}].heading must be non-zero.");
                            var sample = new KimodoMarkerSampleResult
                            {
                                constraintType = type,
                                kimodoRootPosition = new Vector3(position.x, 0f, position.y),
                                rootHeading = heading.normalized,
                                hasRootHeading = true
                            };
                            requests.Add(new CaptureRequest(character, session.Director.time, annotation, sample, frame, true));
                        }
                        else
                        {
                            throw new InvalidOperationException($"constraints[{i}].pose is required unless type is root2d with position and heading.");
                        }
                    }
                }
                if (requests.Count == 0) throw new InvalidOperationException("The selected capture source contains no poses.");
                JObject result = RenderContactSheet(session, requests, arguments);
                if (arguments["analysis_id"] != null) result["analysis_id"] = arguments.Value<string>("analysis_id");
                return Ok(result);
            });
        }

        private static CaptureRequest BuildCaptureRequest(PoseLocator locator, JObject annotation)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            TimelineCharacterRecord character = session.Characters.FirstOrDefault(item =>
                string.Equals(item.Name, locator.Source, StringComparison.OrdinalIgnoreCase));
            if (character != null)
            {
                ThrowIfGenerationRangeLocked(session, character, locator.Frame, locator.Frame + 1, QueryPictureCommand);
                return new CaptureRequest(character, locator.Frame / SessionFrameRate, annotation, null, locator.Frame);
            }
            character = ResolvePoseCacheOwner(locator.Source);
            if (character != null)
            {
                KimodoUntypedConstraintMarker marker = FindUntypedPose(character.PoseCacheTrack, locator.Frame)
                    ?? throw new InvalidOperationException("Writable pose source does not contain a pose at the requested frame.");
                return new CaptureRequest(character, session.Director.time, annotation, marker.SampleData.Clone(), locator.Frame);
            }
            TimelineCharacterRecord owner = session.Characters.FirstOrDefault(item => item.Track.GetMarkers()
                .OfType<KimodoConstraintMarkerBase>().Any(marker => marker is not KimodoUntypedConstraintMarker &&
                    string.Equals(marker.name, locator.Source, StringComparison.OrdinalIgnoreCase) &&
                    Mathf.RoundToInt((float)(marker.time * SessionFrameRate)) == locator.Frame));
            KimodoConstraintMarkerBase constraint = owner?.Track.GetMarkers().OfType<KimodoConstraintMarkerBase>()
                .FirstOrDefault(marker => marker is not KimodoUntypedConstraintMarker &&
                    string.Equals(marker.name, locator.Source, StringComparison.OrdinalIgnoreCase) &&
                    Mathf.RoundToInt((float)(marker.time * SessionFrameRate)) == locator.Frame)
                ?? throw new InvalidOperationException($"Pose source '{locator.Source}' was not found.");
            return new CaptureRequest(owner, session.Director.time, annotation, constraint.SampleData.Clone(), locator.Frame);
        }

        private static string CacheAnalysisResult(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            double start,
            double end,
            JArray poses,
            JObject analysis)
        {
            string id = Guid.NewGuid().ToString("D");
            var record = new AnalysisCacheRecord
            {
                Id = id,
                SessionId = session.Id.ToString("D"),
                TimelineAssetGuid = AssetDatabase.AssetPathToGUID(session.TimelineAssetPath),
                SessionName = session.Name,
                CharacterRef = character.CharacterRef,
                CharacterName = character.Name,
                Start = start,
                End = end,
                CreatedAtUtc = DateTime.UtcNow,
                Poses = poses != null ? (JArray)poses.DeepClone() : new JArray(),
                Analysis = analysis != null ? (JObject)analysis.DeepClone() : new JObject()
            };
            AnalysisCache[id] = record;
            AnalysisCacheOrder.Enqueue(id);
            Directory.CreateDirectory(CommandCacheFolder);
            File.WriteAllText(AnalysisCachePath(id), record.ToJson().ToString(Formatting.Indented));
            while (AnalysisCacheOrder.Count > MaxCachedAnalyses)
            {
                string expired = AnalysisCacheOrder.Dequeue();
                AnalysisCache.Remove(expired);
                string path = AnalysisCachePath(expired);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            foreach (FileInfo expired in new DirectoryInfo(CommandCacheFolder)
                .GetFiles("analysis_*.json")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(MaxCachedAnalyses))
            {
                AnalysisCache.Remove(Path.GetFileNameWithoutExtension(expired.Name).Substring("analysis_".Length));
                expired.Delete();
            }
            return id;
        }

        private static JObject RenderContactSheet(
            TimelineSessionRecord session,
            IReadOnlyList<CaptureRequest> requests,
            JObject arguments)
        {
            int resolution = Mathf.Clamp(arguments.Value<int?>("resolution") ?? 512, 128, 4096);
            float scale = arguments.Value<float?>("scale") ?? 1f;
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0.25f || scale > 4f)
            {
                throw new InvalidOperationException("scale must be between 0.25 and 4.0.");
            }
            int cellSize = resolution / 2;
            var sheet = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            Color background = new Color(0.12f, 0.12f, 0.12f, 1f);
            Color[] clear = Enumerable.Repeat(background, resolution * resolution).ToArray();
            sheet.SetPixels(clear);
            double originalTime = session.Director.time;
            var metadata = new JArray();
            var previews = new List<GameObject>(requests.Count);
            try
            {
                for (int index = 0; index < requests.Count; index++)
                {
                    CaptureRequest request = requests[index];
                    KimodoMarkerSampleResult pose = request.PoseData?.Clone() ?? SampleCapturePose(session, request);
                    previews.Add(CreatePosePreview(request.Character, pose, request.Root2DOnly));
                    string label = (index + 1).ToString(CultureInfo.InvariantCulture);
                    var item = new JObject
                    {
                        ["label"] = label,
                        ["character"] = request.Character.Name,
                        ["frame"] = request.Frame >= 0
                            ? request.Frame
                            : Mathf.RoundToInt((float)(request.Time * SessionFrameRate))
                    };
                    if (request.AnalysisFrame != null)
                    {
                        item["analysis"] = request.AnalysisFrame.DeepClone();
                    }
                    metadata.Add(item);
                }

                Bounds bounds = CalculateBounds(previews);
                Vector3[] directions = { Vector3.right, Vector3.up, Vector3.back, new Vector3(1f, 0.65f, -1f).normalized };
                for (int index = 0; index < directions.Length; index++)
                {
                    Texture2D image = CapturePreviewView(previews, bounds, directions[index], cellSize, scale);
                    int x = index % 2 * cellSize;
                    int y = (1 - index / 2) * cellSize;
                    sheet.SetPixels(x, y, cellSize, cellSize, image.GetPixels());
                    UnityEngine.Object.DestroyImmediate(image);
                }
                sheet.Apply(false, false);
                Directory.CreateDirectory(CommandCacheFolder);
                string path = Path.Combine(CommandCacheFolder, $"contact_sheet_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
                File.WriteAllBytes(path, sheet.EncodeToPNG());
                return new JObject
                {
                    ["image_path"] = path.Replace('\\', '/'),
                    ["resolution"] = new JArray(resolution, resolution),
                    ["views"] = new JArray("right", "top", "front", "3d"),
                    ["grid"] = new JArray(2, 2),
                    ["cell_size"] = cellSize,
                    ["scale"] = scale,
                    ["frames"] = metadata
                };
            }
            finally
            {
                foreach (GameObject preview in previews)
                {
                    if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
                }
                session.Director.time = originalTime;
                session.Director.Evaluate();
                TimelineEditor.Refresh(RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
                UnityEngine.Object.DestroyImmediate(sheet);
            }
        }

        private static KimodoMarkerSampleResult SampleCapturePose(TimelineSessionRecord session, CaptureRequest request)
        {
            RuntimeAnimatorController savedController = request.Character.Animator.runtimeAnimatorController;
            try
            {
                request.Character.Animator.runtimeAnimatorController = null;
                session.Director.time = request.Time;
                session.Director.Evaluate();
                TimelineEditor.Refresh(RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
                var pose = new HumanPose();
                using (var handler = new HumanPoseHandler(request.Character.Avatar, request.Character.Animator.transform))
                {
                    handler.GetHumanPose(ref pose);
                }
                KimodoRetargetClipWriter.EnsureHumanPoseMuscles(ref pose);
                return new KimodoMarkerSampleResult
                {
                    sampleTime = request.Time,
                    unityRootPos = request.Character.Animator.transform.position,
                    unityRootRot = request.Character.Animator.transform.rotation,
                    muscles = pose.muscles.ToList()
                };
            }
            finally
            {
                request.Character.Animator.runtimeAnimatorController = savedController;
            }
        }

        private static GameObject CreatePosePreview(
            TimelineCharacterRecord character,
            KimodoMarkerSampleResult sample,
            bool root2DOnly)
        {
            const int captureLayer = 31;
            GameObject preview = UnityEngine.Object.Instantiate(character.Root);
            preview.name = "Kimodo Pose Preview";
            preview.hideFlags = HideFlags.HideAndDontSave;
            foreach (Transform transform in preview.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = captureLayer;
            }
            Animator animator = preview.GetComponentInChildren<Animator>(true)
                ?? throw new InvalidOperationException($"Character '{character.Name}' preview has no Animator.");
            animator.runtimeAnimatorController = null;
            Vector3 position = root2DOnly ? sample.kimodoRootPosition : sample.unityRootPos;
            Quaternion rotation = root2DOnly && sample.hasRootHeading
                ? Quaternion.LookRotation(new Vector3(sample.rootHeading.x, 0f, sample.rootHeading.y), Vector3.up)
                : sample.unityRootRot;
            if (!root2DOnly && sample.muscles != null && sample.muscles.Count == HumanTrait.MuscleCount)
            {
                var pose = new HumanPose { muscles = sample.muscles.ToArray() };
                using (var handler = new HumanPoseHandler(character.Avatar, animator.transform))
                {
                    handler.GetHumanPose(ref pose);
                    pose.muscles = sample.muscles.ToArray();
                    handler.SetHumanPose(ref pose);
                }
            }
            animator.transform.SetPositionAndRotation(position, rotation);
            return preview;
        }

        private static Texture2D CapturePreviewView(
            IReadOnlyList<GameObject> previews,
            Bounds bounds,
            Vector3 direction,
            int size,
            float scale)
        {
            const int captureLayer = 31;
            GameObject cameraObject = new GameObject("Kimodo Pose Preview Camera") { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture renderTexture = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.cullingMask = 1 << captureLayer;
                camera.orthographic = true;
                camera.orthographicSize = Mathf.Max(0.25f, bounds.extents.magnitude * 1.15f / scale);
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = Mathf.Max(10f, bounds.extents.magnitude * 6f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);
                camera.transform.position = bounds.center + direction * Mathf.Max(5f, bounds.extents.magnitude * 3f);
                camera.transform.LookAt(bounds.center, direction == Vector3.up ? Vector3.forward : Vector3.up);
                renderTexture = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                var result = new Texture2D(size, size, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                for (int index = 1; index < previews.Count; index++)
                {
                    Vector3 from = camera.WorldToScreenPoint(PreviewRootPosition(previews[index - 1]));
                    Vector3 to = camera.WorldToScreenPoint(PreviewRootPosition(previews[index]));
                    DrawLine(result, Mathf.RoundToInt(from.x), Mathf.RoundToInt(from.y), Mathf.RoundToInt(to.x), Mathf.RoundToInt(to.y), new Color(0.1f, 0.85f, 1f, 1f));
                }
                for (int index = 0; index < previews.Count; index++)
                {
                    Vector3 point = camera.WorldToScreenPoint(PreviewRootPosition(previews[index]));
                    DrawGridLabel(result, Mathf.RoundToInt(point.x) + 12, Mathf.RoundToInt(point.y) - 4, (index + 1).ToString(CultureInfo.InvariantCulture));
                }
                result.Apply(false, false);
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                if (renderTexture != null) RenderTexture.ReleaseTemporary(renderTexture);
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        private static Vector3 PreviewRootPosition(GameObject preview)
        {
            Animator animator = preview.GetComponentInChildren<Animator>(true);
            return animator != null ? animator.transform.position : preview.transform.position;
        }

        private static Bounds CalculateBounds(IReadOnlyList<GameObject> roots)
        {
            Bounds bounds = CalculateBounds(roots[0]);
            for (int index = 1; index < roots.Count; index++)
            {
                Renderer[] renderers = roots[index].GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) bounds.Encapsulate(roots[index].transform.position);
                foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
            }
            return bounds;
        }

        private static void DrawLine(Texture2D texture, int x0, int y0, int x1, int y1, Color color)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int error = dx + dy;
            while (true)
            {
                FillRect(texture, x0 - 1, y0 - 1, 3, 3, color);
                if (x0 == x1 && y0 == y1) break;
                int twice = 2 * error;
                if (twice >= dy) { error += dy; x0 += sx; }
                if (twice <= dx) { error += dx; y0 += sy; }
            }
        }

        private static Bounds CalculateBounds(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return new Bounds(root.transform.position + Vector3.up, new Vector3(1f, 2f, 1f));
            }
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
            return bounds;
        }

        private static void DrawGridLabel(Texture2D texture, int right, int top, string label)
        {
            const int pixel = 3;
            int width = label.Length * 4 * pixel + 4;
            int height = 5 * pixel + 4;
            int startX = right - width;
            int startY = top - height;
            FillRect(texture, startX, startY, width, height, new Color(0f, 0f, 0f, 0.75f));
            for (int i = 0; i < label.Length; i++)
            {
                string glyph = Glyph(label[i]);
                for (int gy = 0; gy < 5; gy++)
                for (int gx = 0; gx < 3; gx++)
                {
                    if (glyph[gy * 3 + gx] == '1')
                    {
                        FillRect(texture, startX + 2 + (i * 4 + gx) * pixel,
                            startY + 2 + (4 - gy) * pixel, pixel, pixel, Color.white);
                    }
                }
            }
        }

        private static string Glyph(char value)
        {
            return value switch
            {
                '0' => "111101101101111", '1' => "010110010010111", '2' => "111001111100111",
                '3' => "111001111001111", '4' => "101101111001001", '5' => "111100111001111",
                '6' => "111100111101111", '7' => "111001001001001", '8' => "111101111101111",
                '9' => "111101111001111", '.' => "000000000000010", _ => "000000000000000"
            };
        }

        private static void FillRect(Texture2D texture, int x, int y, int width, int height, Color color)
        {
            for (int py = Mathf.Max(0, y); py < Mathf.Min(texture.height, y + height); py++)
            for (int px = Mathf.Max(0, x); px < Mathf.Min(texture.width, x + width); px++)
            {
                texture.SetPixel(px, py, color);
            }
        }

        private static AnalysisCacheRecord GetCachedAnalysis(string id)
        {
            if (AnalysisCache.TryGetValue(id, out AnalysisCacheRecord cached))
            {
                return cached;
            }
            string path = AnalysisCachePath(id);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Unknown or expired analysis_id '{id}'.");
            }
            cached = AnalysisCacheRecord.FromJson(JObject.Parse(File.ReadAllText(path)));
            AnalysisCache[id] = cached;
            return cached;
        }

        private static string AnalysisCachePath(string id) => Path.Combine(CommandCacheFolder, $"analysis_{id}.json");

        private sealed class CaptureRequest
        {
            public CaptureRequest(
                TimelineCharacterRecord character,
                double time,
                JObject analysisFrame,
                KimodoMarkerSampleResult poseData = null,
                int frame = -1,
                bool root2DOnly = false)
            {
                Character = character;
                Time = time;
                AnalysisFrame = analysisFrame;
                PoseData = poseData;
                Frame = frame;
                Root2DOnly = root2DOnly;
            }
            public TimelineCharacterRecord Character { get; }
            public double Time { get; }
            public JObject AnalysisFrame { get; }
            public KimodoMarkerSampleResult PoseData { get; }
            public int Frame { get; }
            public bool Root2DOnly { get; }
        }

        private sealed class AnalysisCacheRecord
        {
            public string Id;
            public string SessionId;
            public string TimelineAssetGuid;
            public string SessionName;
            public string CharacterRef;
            public string CharacterName;
            public double Start;
            public double End;
            public DateTime CreatedAtUtc;
            public JObject Analysis;
            public JArray Poses;

            public JObject ToJson() => new JObject
            {
                ["analysis_id"] = Id, ["session_id"] = SessionId, ["timeline_asset_guid"] = TimelineAssetGuid,
                ["session_name"] = SessionName,
                ["character_ref"] = CharacterRef, ["character"] = CharacterName,
                ["start"] = Start, ["end"] = End, ["created_at_utc"] = CreatedAtUtc,
                ["poses"] = Poses?.DeepClone() ?? new JArray(),
                ["analysis"] = Analysis?.DeepClone() ?? new JObject()
            };

            public static AnalysisCacheRecord FromJson(JObject json) => new AnalysisCacheRecord
            {
                Id = json.Value<string>("analysis_id"), SessionId = json.Value<string>("session_id"),
                TimelineAssetGuid = json.Value<string>("timeline_asset_guid"),
                SessionName = json.Value<string>("session_name"), CharacterRef = json.Value<string>("character_ref"),
                CharacterName = json.Value<string>("character"), Start = json.Value<double>("start"),
                End = json.Value<double>("end"), CreatedAtUtc = json.Value<DateTime>("created_at_utc"),
                Poses = json["poses"] as JArray ?? json["analysis"]?["poses"] as JArray ?? new JArray(),
                Analysis = json["analysis"] as JObject ?? new JObject()
            };
        }
    }
}
