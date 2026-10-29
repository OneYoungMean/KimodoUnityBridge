using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private static void CreateAnalysisBodyTrajectories(
            List<GameObject> objects,
            SubjectPictureData subject)
        {
            CreateAnalysisTrajectory(objects, subject.Pelvis, new Color(.1f, .8f, .2f, .9f), .09f);
            CreateAnalysisTrajectory(objects, subject.LeftHand, new Color(.2f, .45f, 1f, .65f), .035f);
            CreateAnalysisTrajectory(objects, subject.LeftFoot, new Color(.2f, .45f, 1f, .8f), .05f);
            CreateAnalysisTrajectory(objects, subject.RightHand, new Color(1f, .2f, .2f, .65f), .035f);
            CreateAnalysisTrajectory(objects, subject.RightFoot, new Color(1f, .2f, .2f, .8f), .05f);
        }

        private static void CreateAnalysisTrajectory(
            List<GameObject> objects,
            Vector3[] points,
            Color color,
            float lineWidth)
        {
            if (points == null || points.Length < 2) return;
            GameObject lineObject = MoveToAnalysisSessionRoot(
                new GameObject("Kimodo Analysis Body Trajectory") { hideFlags = HideFlags.HideAndDontSave });
            SetLayerRecursively(lineObject, SessionCaptureLayer);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = points.Length;
            line.SetPositions(points.Select(point => point + Vector3.up * .02f).ToArray());
            line.startWidth = line.endWidth = lineWidth;
            line.useWorldSpace = true;
            color.a = 1f;
            line.sharedMaterial = CreatePipelineUnlitMaterial(color);
            line.startColor = line.endColor = color;
            objects.Add(lineObject);
        }

        private static void Fill(Texture2D texture, Color color)
        {
            var pixels = new Color[texture.width * texture.height];
            for (int index = 0; index < pixels.Length; index++) pixels[index] = color;
            texture.SetPixels(pixels);
        }

        private static void FillRect(Texture2D texture, int x, int y, int width, int height, Color color)
        {
            int minX = Mathf.Clamp(x, 0, texture.width);
            int maxX = Mathf.Clamp(x + width, 0, texture.width);
            int minY = Mathf.Clamp(y, 0, texture.height);
            int maxY = Mathf.Clamp(y + height, 0, texture.height);
            for (int row = minY; row < maxY; row++)
            {
                for (int column = minX; column < maxX; column++) texture.SetPixel(column, row, color);
            }
        }

        private static string CacheAnalysisResult(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            double start,
            double end,
            JObject analysis,
            byte[] motionBytes,
            TimelineAnimationRecord animation = null,
            string inputSignature = null)
        {
            string id = Guid.NewGuid().ToString("D");
            string motionPath = AnalysisMotionCachePath(session, id);
            Directory.CreateDirectory(Path.GetDirectoryName(motionPath));
            if (motionBytes != null && motionBytes.Length > 0)
            {
                File.WriteAllBytes(motionPath, motionBytes);
            }
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
                Analysis = analysis != null ? (JObject)analysis.DeepClone() : new JObject(),
                MotionPath = motionBytes != null && motionBytes.Length > 0
                    ? ToProjectRelativePath(motionPath)
                    : string.Empty,
                AnimationId = animation?.Id.ToString("D") ?? string.Empty,
                AnimationName = animation?.Name ?? string.Empty,
                InputSignature = inputSignature ?? string.Empty
            };
            AnalysisCache[id] = record;
            WriteJsonAtomically(AnalysisCachePath(session, id), record.ToJson());
            return id;
        }

        private static string AnalysisCachePath(TimelineSessionRecord session, string id) =>
            Path.Combine(GetSessionGeneratedFolder(session), "Analyses", $"analysis_{id}.json");

        private static string AnalysisMotionCachePath(TimelineSessionRecord session, string id) =>
            Path.Combine(GetSessionGeneratedFolder(session), "Analyses", $"analysis_{id}.kmb");

        private static bool TryGetRoot2DWorld(
            KimodoMarkerSampleResult sample,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (sample == null || !sample.sampleValid || sample.rootOverride == null)
            {
                return false;
            }

            position = sample.rootOverride.t;
            rotation = sample.rootOverride.q.normalized;
            return true;
        }

        private static void CreateWorldLine(List<GameObject> objects, Vector3 from, Vector3 to, float width, Color color)
        {
            GameObject lineObject = MoveToAnalysisSessionRoot(
                new GameObject("Kimodo Evidence Line") { hideFlags = HideFlags.HideAndDontSave });
            SetLayerRecursively(lineObject, SessionCaptureLayer);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPositions(new[] { from, to });
            line.startWidth = line.endWidth = width;
            line.useWorldSpace = true;
            color.a = 1f;
            line.sharedMaterial = CreatePipelineUnlitMaterial(color);
            line.startColor = line.endColor = color;
            objects.Add(lineObject);
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = layer;
        }

        private static GameObject MoveToAnalysisSessionRoot(GameObject gameObject)
        {
            if (gameObject != null && captureSessionRoot != null)
            {
                gameObject.transform.SetParent(captureSessionRoot.transform, true);
                SetLayerRecursively(gameObject, SessionCaptureLayer);
            }
            return gameObject;
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

        private static AnalysisCacheRecord GetCachedAnalysis(TimelineSessionRecord session, string id)
        {
            if (!Guid.TryParse(id, out _))
            {
                throw new InvalidOperationException("analysis_id is not a valid GUID.");
            }
            if (AnalysisCache.TryGetValue(id, out AnalysisCacheRecord cached))
            {
                if (string.Equals(cached.SessionId, session.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)) return cached;
                throw new InvalidOperationException("analysis_id belongs to a different Session.");
            }
            string path = AnalysisCachePath(session, id);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Unknown analysis_id '{id}' in the current scene context.");
            }
            cached = AnalysisCacheRecord.FromJson(JObject.Parse(File.ReadAllText(path)));
            if (!string.Equals(cached.SessionId, session.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("analysis_id belongs to a different Session.");
            }
            AnalysisCache[id] = cached;
            return cached;
        }

        private static bool TryFindCachedAnimationAnalysis(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            TimelineAnimationRecord animation,
            string inputSignature,
            out AnalysisCacheRecord cached)
        {
            cached = null;
            if (session == null || character == null || animation == null || string.IsNullOrWhiteSpace(inputSignature))
            {
                return false;
            }

            string animationId = animation.Id.ToString("D");
            IEnumerable<AnalysisCacheRecord> records = AnalysisCache.Values
                .Concat(EnumerateAnalysisCacheRecords(session))
                .GroupBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());
            cached = records
                .Where(record => record != null &&
                    string.Equals(record.SessionId, session.Id.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(record.CharacterRef, character.CharacterRef, StringComparison.Ordinal) &&
                    string.Equals(record.AnimationId, animationId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(record.InputSignature, inputSignature, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(record.MotionPath) &&
                    File.Exists(ProjectRelativePathToAbsolute(record.MotionPath)))
                .OrderByDescending(record => record.CreatedAtUtc)
                .FirstOrDefault();
            if (cached == null)
            {
                return false;
            }

            AnalysisCache[cached.Id] = cached;
            return true;
        }

        private static IEnumerable<AnalysisCacheRecord> EnumerateAnalysisCacheRecords(TimelineSessionRecord session)
        {
            string folder = Path.Combine(GetSessionGeneratedFolder(session), "Analyses");
            if (!Directory.Exists(folder))
            {
                yield break;
            }
            foreach (string path in Directory.GetFiles(folder, "analysis_*.json"))
            {
                AnalysisCacheRecord record = null;
                try
                {
                    record = AnalysisCacheRecord.FromJson(JObject.Parse(File.ReadAllText(path)));
                }
                catch
                {
                    // A malformed cache entry is not a valid analysis result and must never be reused.
                }
                if (record != null)
                {
                    yield return record;
                }
            }
        }

        private static string BuildAnimationAnalysisSignature(
            TimelineCharacterRecord character,
            TimelineAnimationRecord animation,
            JObject effectiveOptions)
        {
            var signature = new JObject
            {
                // v5 aligns native KMB analysis frames to the fixed 60 FPS command
                // time base before keyframe/contact extraction. Do not reuse
                // v3 caches, whose markers are in the model's native FPS.
                ["contract"] = "animation_analysis_picture_v7_force_line_command_60",
                ["character_ref"] = character?.CharacterRef ?? string.Empty,
                ["rig_type"] = IsHumanoidCharacter(character) ? "humanoid" : "mesh",
                ["animation_id"] = animation?.Id.ToString("D") ?? string.Empty,
                ["start_timeline_frame_60"] = animation?.StartFrame ?? 0,
                ["end_frame_exclusive"] = animation?.EndFrameExclusive ?? 0,
                ["authored_foot_contact_track"] = KimodoTimelineFootContactSampler.HasAuthoredTrack(animation?.Clip),
                ["options"] = CanonicalizeJson(effectiveOptions ?? new JObject())
            };
            return signature.ToString(Formatting.None);
        }

        private static JToken CanonicalizeJson(JToken value)
        {
            if (value is JObject source)
            {
                var result = new JObject();
                foreach (JProperty property in source.Properties().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    result[property.Name] = CanonicalizeJson(property.Value);
                }
                return result;
            }
            if (value is JArray array)
            {
                return new JArray(array.Select(CanonicalizeJson));
            }
            return value?.DeepClone() ?? JValue.CreateNull();
        }

        private static string ProjectRelativePathToAbsolute(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar)));
        }
    }
}
