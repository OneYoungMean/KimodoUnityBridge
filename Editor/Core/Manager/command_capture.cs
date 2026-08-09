using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        private static string CacheAnalysisResult(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            double start,
            double end,
            JObject analysis)
        {
            string id = Guid.NewGuid().ToString("D");
            var record = new AnalysisCacheRecord
            {
                Id = id,
                SessionId = session.Id.ToString("D"),
                SessionName = session.Name,
                CharacterRef = character.CharacterRef,
                CharacterName = character.Name,
                Start = start,
                End = end,
                CreatedAtUtc = DateTime.UtcNow,
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

        public static string RenderPoseSheet(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                if (arguments["samples"] is not JArray samples || samples.Count == 0)
                {
                    throw new InvalidOperationException("samples must be a non-empty array.");
                }
                var requests = new List<CaptureRequest>(samples.Count);
                for (int i = 0; i < samples.Count; i++)
                {
                    if (samples[i] is not JObject item)
                    {
                        throw new InvalidOperationException($"samples[{i}] must be an object.");
                    }
                    TimelineCharacterRecord character = ResolveSessionCharacterByReference(
                        session, RequiredStringValue(item, "character"), addIfMissing: false);
                    double time = RequiredFiniteDouble(item, "time");
                    if (time < 0.0)
                    {
                        throw new InvalidOperationException($"samples[{i}].time must be non-negative.");
                    }
                    requests.Add(new CaptureRequest(character, time, null));
                }
                return Ok(RenderContactSheet(session, requests, arguments));
            });
        }

        public static string RenderAnalysisSheet(string argumentsJson)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireCurrentTimelineSession();
                string id = RequiredStringValue(arguments, "analysis_id");
                AnalysisCacheRecord cached = GetCachedAnalysis(id);
                if (!string.Equals(cached.SessionId, session.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("analysis_source_expired: the analysis belongs to a different Session.");
                }
                TimelineCharacterRecord character = ResolveSessionCharacterByReference(
                    session, cached.CharacterRef, addIfMissing: false);
                JArray keyframes = cached.Analysis?["keyframes"] as JArray ?? new JArray();
                if (keyframes.Count == 0)
                {
                    throw new InvalidOperationException("The cached analysis contains no keyframes to render.");
                }
                var requests = new List<CaptureRequest>(keyframes.Count);
                foreach (JToken token in keyframes)
                {
                    double reported = token.Value<double?>("session_time") ?? token.Value<double?>("time") ?? cached.Start;
                    double time = reported >= cached.Start && reported <= cached.End
                        ? reported
                        : Math.Max(cached.Start, Math.Min(cached.End, cached.Start + reported));
                    requests.Add(new CaptureRequest(character, time, token as JObject));
                }
                JObject result = RenderContactSheet(session, requests, arguments);
                result["analysis_id"] = id;
                return Ok(result);
            });
        }

        internal static int ResolveSquareGridSize(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            return Mathf.CeilToInt(Mathf.Sqrt(count));
        }

        private static JObject RenderContactSheet(
            TimelineSessionRecord session,
            IReadOnlyList<CaptureRequest> requests,
            JObject arguments)
        {
            int resolution = Mathf.Clamp(arguments.Value<int?>("resolution") ?? 1024, 128, 4096);
            float scale = arguments.Value<float?>("scale") ?? 1f;
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0.25f || scale > 4f)
            {
                throw new InvalidOperationException("scale must be between 0.25 and 4.0.");
            }
            int grid = ResolveSquareGridSize(requests.Count);
            int cellSize = resolution / grid;
            int used = cellSize * grid;
            int offset = (resolution - used) / 2;
            var sheet = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
            Color background = new Color(0.12f, 0.12f, 0.12f, 1f);
            Color[] clear = Enumerable.Repeat(background, resolution * resolution).ToArray();
            sheet.SetPixels(clear);
            double originalTime = session.Director.time;
            var metadata = new JArray();
            try
            {
                for (int index = 0; index < requests.Count; index++)
                {
                    CaptureRequest request = requests[index];
                    session.Director.time = request.Time;
                    session.Director.Evaluate();
                    TimelineEditor.Refresh(RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
                    Texture2D image = CaptureCharacter(request.Character, cellSize, scale);
                    int row = index / grid;
                    int column = index % grid;
                    int x = offset + column * cellSize;
                    int y = resolution - offset - (row + 1) * cellSize;
                    sheet.SetPixels(x, y, cellSize, cellSize, image.GetPixels());
                    UnityEngine.Object.DestroyImmediate(image);
                    string label = $"{row + 1}.{column + 1}";
                    DrawGridLabel(sheet, x + cellSize - 8, y + cellSize - 8, label);
                    var item = new JObject
                    {
                        ["label"] = label,
                        ["character"] = request.Character.Name,
                        ["character_ref"] = request.Character.CharacterRef,
                        ["time"] = request.Time
                    };
                    if (request.AnalysisFrame != null)
                    {
                        item["analysis"] = request.AnalysisFrame.DeepClone();
                    }
                    metadata.Add(item);
                }
                sheet.Apply(false, false);
                Directory.CreateDirectory(CommandCacheFolder);
                string path = Path.Combine(CommandCacheFolder, $"contact_sheet_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
                File.WriteAllBytes(path, sheet.EncodeToPNG());
                return new JObject
                {
                    ["image_path"] = path.Replace('\\', '/'),
                    ["resolution"] = new JArray(resolution, resolution),
                    ["grid"] = new JArray(grid, grid),
                    ["cell_size"] = cellSize,
                    ["scale"] = scale,
                    ["frames"] = metadata
                };
            }
            finally
            {
                session.Director.time = originalTime;
                session.Director.Evaluate();
                TimelineEditor.Refresh(RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
                UnityEngine.Object.DestroyImmediate(sheet);
            }
        }

        private static Texture2D CaptureCharacter(TimelineCharacterRecord character, int size, float scale)
        {
            const int captureLayer = 31;
            Transform[] transforms = character.Root.GetComponentsInChildren<Transform>(true);
            int[] originalLayers = transforms.Select(transform => transform.gameObject.layer).ToArray();
            var targetRenderers = new HashSet<Renderer>(character.Root.GetComponentsInChildren<Renderer>(true));
            SkinnedMeshRenderer[] skinnedRenderers = character.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            bool[] originalUpdateWhenOffscreen = skinnedRenderers.Select(renderer => renderer.updateWhenOffscreen).ToArray();
            bool[] originalForceMatrixRecalculation = skinnedRenderers.Select(renderer => renderer.forceMatrixRecalculationPerRender).ToArray();
            Renderer[] conflictingRenderers = Resources.FindObjectsOfTypeAll<Renderer>()
                .Where(renderer => renderer != null && !targetRenderers.Contains(renderer) &&
                    renderer.gameObject.layer == captureLayer && renderer.gameObject.scene.IsValid())
                .ToArray();
            bool[] conflictingEnabled = conflictingRenderers.Select(renderer => renderer.enabled).ToArray();
            GameObject cameraObject = null;
            RenderTexture renderTexture = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                foreach (Transform transform in transforms)
                {
                    transform.gameObject.layer = captureLayer;
                }
                foreach (Renderer renderer in conflictingRenderers)
                {
                    renderer.enabled = false;
                }
                foreach (SkinnedMeshRenderer renderer in skinnedRenderers)
                {
                    renderer.updateWhenOffscreen = true;
                    renderer.forceMatrixRecalculationPerRender = true;
                }

                Bounds bounds = CalculateBounds(character.Root);
                float radius = Mathf.Max(0.1f, bounds.extents.magnitude);
                Vector3 direction = Quaternion.Euler(0f, 270f, 0f) * new Vector3(1f, 0.55f, -1f).normalized;
                cameraObject = new GameObject("Kimodo Command Capture Camera")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.cullingMask = 1 << captureLayer;
                camera.fieldOfView = 30f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = radius * 20f + 10f;
                float distance = radius / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                distance /= scale;
                camera.transform.position = bounds.center + direction * distance;
                camera.transform.LookAt(bounds.center);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.18f, 0.18f, 0.18f, 1f);
                renderTexture = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                var result = new Texture2D(size, size, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                result.Apply();
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                if (renderTexture != null)
                {
                    RenderTexture.ReleaseTemporary(renderTexture);
                }
                if (cameraObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(cameraObject);
                }
                for (int i = 0; i < transforms.Length; i++)
                {
                    transforms[i].gameObject.layer = originalLayers[i];
                }
                for (int i = 0; i < conflictingRenderers.Length; i++)
                {
                    conflictingRenderers[i].enabled = conflictingEnabled[i];
                }
                for (int i = 0; i < skinnedRenderers.Length; i++)
                {
                    skinnedRenderers[i].updateWhenOffscreen = originalUpdateWhenOffscreen[i];
                    skinnedRenderers[i].forceMatrixRecalculationPerRender = originalForceMatrixRecalculation[i];
                }
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
            public CaptureRequest(TimelineCharacterRecord character, double time, JObject analysisFrame)
            {
                Character = character;
                Time = time;
                AnalysisFrame = analysisFrame;
            }
            public TimelineCharacterRecord Character { get; }
            public double Time { get; }
            public JObject AnalysisFrame { get; }
        }

        private sealed class AnalysisCacheRecord
        {
            public string Id;
            public string SessionId;
            public string SessionName;
            public string CharacterRef;
            public string CharacterName;
            public double Start;
            public double End;
            public DateTime CreatedAtUtc;
            public JObject Analysis;

            public JObject ToJson() => new JObject
            {
                ["analysis_id"] = Id, ["session_id"] = SessionId, ["session_name"] = SessionName,
                ["character_ref"] = CharacterRef, ["character"] = CharacterName,
                ["start"] = Start, ["end"] = End, ["created_at_utc"] = CreatedAtUtc,
                ["analysis"] = Analysis?.DeepClone() ?? new JObject()
            };

            public static AnalysisCacheRecord FromJson(JObject json) => new AnalysisCacheRecord
            {
                Id = json.Value<string>("analysis_id"), SessionId = json.Value<string>("session_id"),
                SessionName = json.Value<string>("session_name"), CharacterRef = json.Value<string>("character_ref"),
                CharacterName = json.Value<string>("character"), Start = json.Value<double>("start"),
                End = json.Value<double>("end"), CreatedAtUtc = json.Value<DateTime>("created_at_utc"),
                Analysis = json["analysis"] as JObject ?? new JObject()
            };
        }
    }
}
