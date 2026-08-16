using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using CharacterAnimationCli.Unity;
using KimodoBridge;
using TimelineInject;
using UnityEditor;
using UnityEngine;

namespace CharacterAnimationCli.Unity.Command
{
    internal static partial class command_context
    {
        private static readonly Dictionary<string, AnalysisCacheRecord> AnalysisCache =
            new Dictionary<string, AnalysisCacheRecord>(StringComparer.OrdinalIgnoreCase);

        private const string AnalysisPictureRenderVersion = "5";

        private static JObject RenderAnalysisPictures(
            TimelineSessionRecord session,
            IReadOnlyList<AnalysisSubject> subjects,
            string level)
        {
            PictureLayout layout = PictureLayout.ForLevel(level);
            string signature = BuildPictureSignature(subjects, level);
            string imagePath = Path.Combine(EvidenceFolder(session), $"analysis_picture_{signature}.png");
            string projectPath = ToProjectRelativePath(imagePath);
            JObject persisted = subjects[0].Record.Pictures;
            if (persisted != null &&
                string.Equals(persisted.Value<string>("level"), level, StringComparison.Ordinal) &&
                string.Equals(persisted.Value<string>("image_path"), projectPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(imagePath))
            {
                var cachedResult = (JObject)persisted.DeepClone();
                cachedResult["cached"] = true;
                return cachedResult;
            }

            var data = subjects.Select(subject => BuildSubjectPictureData(session, subject)).ToList();
            TrajectoryScale trajectoryScale = BuildTrajectoryScale(data);
            var tiles = new List<PictureTile>();
            foreach (SubjectPictureData subject in data)
            {
                tiles.AddRange(BuildPictureTiles(subject, level));
            }

            bool cached = File.Exists(imagePath);
            if (!cached)
            {
                Directory.CreateDirectory(EvidenceFolder(session));
                Texture2D canvas = RenderPictureCanvas(data, tiles, layout, trajectoryScale);
                try
                {
                    File.WriteAllBytes(imagePath, canvas.EncodeToPNG());
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(canvas);
                }
            }

            var descriptions = new JArray();
            for (int index = 0; index < tiles.Count; index++)
            {
                PictureTile tile = tiles[index];
                int panel = data.FindIndex(item => ReferenceEquals(item, tile.Subject));
                int localIndex = tiles.Take(index).Count(item => ReferenceEquals(item.Subject, tile.Subject));
                int x = (localIndex % layout.Columns) * layout.CellSize;
                int y = (data.Count - panel - 1) * layout.Height +
                    (layout.Rows - 1 - localIndex / layout.Columns) * layout.CellSize;
                JObject description = (JObject)tile.Description.DeepClone();
                description["subject"] = tile.Subject.Subject.Role;
                descriptions.Add(new JObject
                {
                    ["id"] = tile.Subject.Subject.Role + "." + (localIndex + 1).ToString(CultureInfo.InvariantCulture),
                    ["rect"] = new JObject { ["x"] = x, ["y"] = y, ["width"] = layout.CellSize, ["height"] = layout.CellSize },
                    ["description"] = description
                });
            }

            var result = new JObject
            {
                ["level"] = level,
                ["image_path"] = projectPath,
                ["width"] = layout.Width,
                ["height"] = layout.Height * data.Count,
                ["images"] = descriptions,
                ["cached"] = cached
            };
            PersistPictureSummary(session, subjects[0].Record, result);
            return result;
        }

        private static void PersistPictureSummary(TimelineSessionRecord session, AnalysisCacheRecord record, JObject pictures)
        {
            if (record == null || session == null) return;
            record.Pictures = pictures != null ? (JObject)pictures.DeepClone() : new JObject();
            AnalysisCache[record.Id] = record;
            WriteJsonAtomically(AnalysisCachePath(session, record.Id), record.ToJson());
        }

        private static string BuildPictureSignature(IReadOnlyList<AnalysisSubject> subjects, string level)
        {
            string source = AnalysisPictureRenderVersion + "|" + level + "|" + string.Join("|", subjects.Select(item => item.Role + ":" + item.Record.Id));
            using (SHA256 hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(source))).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
            }
        }

        private static SubjectPictureData BuildSubjectPictureData(TimelineSessionRecord session, AnalysisSubject subject)
        {
            int frameCount = Math.Max(1, subject.EndFrameExclusive - subject.StartFrame);
            var pelvis = new Vector3[frameCount];
            Bounds firstBounds = default;
            Bounds lastBounds = default;
            double originalTime = session.Director.time;
            try
            {
                for (int localFrame = 0; localFrame < frameCount; localFrame++)
                {
                    session.Director.time = (subject.StartFrame + localFrame) / SessionFrameRate;
                    session.Director.Evaluate();
                    Transform hips = subject.Character.Animator != null
                        ? subject.Character.Animator.GetBoneTransform(HumanBodyBones.Hips)
                        : null;
                    if (hips == null)
                    {
                        throw new InvalidOperationException($"Character '{subject.Character.Name}' has no Humanoid Hips transform.");
                    }
                    pelvis[localFrame] = hips.position;
                    Bounds currentBounds = CalculateSkinnedBounds(subject.Character.Root);
                    if (localFrame == 0) firstBounds = currentBounds;
                    if (localFrame == frameCount - 1) lastBounds = currentBounds;
                }
            }
            finally
            {
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

            Bounds bounds = firstBounds;
            foreach (Vector3 point in pelvis) bounds.Encapsulate(point);
            bounds.Encapsulate(lastBounds);
            bounds.Expand(new Vector3(6f, 1f, 6f));
            if (bounds.size.x < 6f) bounds.Expand(new Vector3(6f - bounds.size.x, 0f, 0f));
            if (bounds.size.z < 6f) bounds.Expand(new Vector3(0f, 0f, 6f - bounds.size.z));
            return new SubjectPictureData(subject, pelvis, leftContacts, rightContacts, firstBounds, lastBounds, bounds);
        }

        private static Bounds CalculateSkinnedBounds(GameObject root)
        {
            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) return CalculateBounds(root);
            Bounds result = renderers[0].bounds;
            for (int index = 1; index < renderers.Length; index++) result.Encapsulate(renderers[index].bounds);
            return result;
        }

        private static List<PictureTile> BuildPictureTiles(SubjectPictureData subject, string level)
        {
            int keyCount = level == "high" ? 5 : level == "middle" ? 3 : 0;
            var result = new List<PictureTile>();
            if (level != "low")
            {
                result.Add(PictureTile.Ghost(subject, "three_quarter", new Vector3(1f, .75f, -1f), false));
                result.Add(PictureTile.Ghost(subject, "front", Vector3.forward, true));
                result.Add(PictureTile.Ghost(subject, "left", Vector3.left, true));
                result.Add(PictureTile.Ghost(subject, "bottom", Vector3.down, true));
            }
            else
            {
                result.Add(PictureTile.Ghost(subject, "three_quarter", new Vector3(1f, .75f, -1f), false));
            }

            List<int> keyFrames = SelectKeyFrames(subject, Math.Max(keyCount, 6));
            for (int index = 0; index < keyCount; index++)
            {
                result.Add(PictureTile.Key(subject, keyFrames[index % keyFrames.Count]));
            }
            if (level == "high")
            {
                List<JObject> contacts = (subject.Subject.Record.Analysis?["foot_contacts"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .OrderBy(item => item.Value<int?>("duration_frames") ?? int.MaxValue)
                    .Take(6)
                    .Select(item => (JObject)item.DeepClone())
                    .ToList();
                List<int> fallbackFrames = keyFrames.Skip(keyCount).Concat(keyFrames).ToList();
                for (int index = 0; index < 6; index++)
                {
                    if (index < contacts.Count)
                    {
                        int frame = Mathf.Clamp(contacts[index].Value<int?>("frame") ?? 0, 0, subject.Pelvis.Length - 1);
                        result.Add(PictureTile.FootContact(subject, frame, contacts[index]));
                    }
                    else
                    {
                        int fallbackIndex = (index - contacts.Count) % fallbackFrames.Count;
                        result.Add(PictureTile.FootFallback(subject, fallbackFrames[fallbackIndex]));
                    }
                }
            }
            result.Add(PictureTile.Trajectory(subject, keyFrames));
            return result;
        }

        private static List<int> SelectKeyFrames(SubjectPictureData subject, int count)
        {
            var frames = (subject.Subject.Record.Analysis?["keyframes"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(item => Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, subject.Pelvis.Length - 1))
                .Distinct()
                .ToList();
            for (int index = 0; frames.Count < count && index < count; index++)
            {
                frames.Add(Mathf.RoundToInt(Mathf.Lerp(0, subject.Pelvis.Length - 1, count <= 1 ? 0f : index / (float)(count - 1))));
                frames = frames.Distinct().ToList();
            }
            return frames.Count > 0 ? frames : new List<int> { 0 };
        }

        private static Texture2D RenderPictureCanvas(
            IReadOnlyList<SubjectPictureData> subjects,
            IReadOnlyList<PictureTile> tiles,
            PictureLayout layout,
            TrajectoryScale trajectoryScale)
        {
            var canvas = new Texture2D(layout.Width, layout.Height * subjects.Count, TextureFormat.RGBA32, false);
            Fill(canvas, Color.white);
            for (int index = 0; index < tiles.Count; index++)
            {
                PictureTile tile = tiles[index];
                int panel = subjects.ToList().FindIndex(item => ReferenceEquals(item, tile.Subject));
                int localIndex = tiles.Take(index).Count(item => ReferenceEquals(item.Subject, tile.Subject));
                Texture2D image = RenderPictureTile(tile, layout.CellSize, trajectoryScale);
                try
                {
                    DrawTileNumber(image, localIndex + 1);
                    int x = (localIndex % layout.Columns) * layout.CellSize;
                    int y = (subjects.Count - panel - 1) * layout.Height +
                        (layout.Rows - 1 - localIndex / layout.Columns) * layout.CellSize;
                    canvas.SetPixels(x, y, layout.CellSize, layout.CellSize, image.GetPixels());
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(image);
                }
            }
            DrawInternalGrid(canvas, layout, subjects.Count);
            canvas.Apply(false, false);
            return canvas;
        }

        private static Texture2D RenderPictureTile(PictureTile tile, int size, TrajectoryScale trajectoryScale)
        {
            Bounds tileBounds = tile.Presentation == "key" || tile.Presentation == "foot_contact" || tile.Presentation == "foot_fallback"
                ? CalculatePreviewPoseBounds(tile.Subject, tile.Frame)
                : tile.Subject.Bounds;
            var environment = new List<GameObject>();
            CreatePictureEnvironment(environment, tileBounds);
            if (tile.Presentation == "trajectory")
            {
                CreatePelvisTrajectory(environment, tile.Subject.Pelvis, trajectoryScale);
            }
            Camera camera = CreateAnalysisPictureCamera(tileBounds, tile.Direction, tile.Orthographic);
            try
            {
                Texture2D result = RenderCamera(camera, size, new Color(.12f, .12f, .12f, 1f));
                if (tile.Presentation == "ghost")
                {
                    List<int> frames = BuildGhostFrames(tile.Subject);
                    bool separated = !tile.Subject.FirstBounds.Intersects(tile.Subject.LastBounds);
                    for (int index = 0; index < frames.Count; index++)
                    {
                        int frame = frames[index];
                        bool key = tile.Subject.KeyFrameSet.Contains(frame);
                        float alpha = GhostAlpha(index, frames.Count, separated);
                        RenderPoseOnto(result, camera, environment, tile.Subject, frame, key ? Color.yellow : FootTint(tile.Subject, frame), alpha);
                    }
                }
                else if (tile.Presentation == "key" || tile.Presentation == "foot_contact" || tile.Presentation == "foot_fallback")
                {
                    Color tint = tile.Presentation == "key" ? Color.yellow : FootTint(tile.Subject, tile.Frame);
                    RenderPoseOnto(result, camera, environment, tile.Subject, tile.Frame, tint, 1f);
                }
                else if (tile.Presentation == "trajectory")
                {
                    foreach (int frame in tile.TrajectoryFrames)
                    {
                        bool key = tile.Subject.KeyFrameSet.Contains(frame);
                        RenderPoseOnto(result, camera, environment, tile.Subject, frame, key ? Color.yellow : Color.gray, 1f);
                    }
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
            float alpha)
        {
            GameObject preview = CreateAnalysisPosePreview(subject, localFrame);
            try
            {
                TintPreview(preview, tint);
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
                UnityEngine.Object.DestroyImmediate(preview);
            }
        }

        private static Bounds CalculatePreviewPoseBounds(SubjectPictureData subject, int localFrame)
        {
            GameObject preview = CreateAnalysisPosePreview(subject, localFrame);
            try
            {
                Bounds bounds = CalculateSkinnedBounds(preview);
                bounds.Encapsulate(PreviewRootPosition(preview));
                bounds.Expand(new Vector3(1.5f, .5f, 1.5f));
                if (bounds.size.x < 3f) bounds.Expand(new Vector3(3f - bounds.size.x, 0f, 0f));
                if (bounds.size.z < 3f) bounds.Expand(new Vector3(0f, 0f, 3f - bounds.size.z));
                return bounds;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(preview);
            }
        }

        private static GameObject CreateAnalysisPosePreview(SubjectPictureData subject, int localFrame)
        {
            CharacterPose pose = CaptureCharacterPose(subject.Subject.Character, subject.Subject.StartFrame + localFrame);
            var sample = new KimodoMarkerSampleResult { sampleTime = (subject.Subject.StartFrame + localFrame) / SessionFrameRate };
            SetCanonicalPose(sample, pose, subject.Subject.Character);
            return CreatePosePreview(subject.Subject.Character, sample, false);
        }

        private static void TintPreview(GameObject preview, Color tint)
        {
            foreach (Renderer renderer in preview.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.materials)
                {
                    if (material != null && material.HasProperty("_Color"))
                    {
                        material.color = Color.Lerp(material.color, tint, .8f);
                    }
                }
            }
        }

        private static List<int> BuildGhostFrames(SubjectPictureData subject)
        {
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            var frames = new List<int> { 0 };
            var keys = subject.KeyFrameSet.Where(frame => frame > 0 && frame < lastFrame).OrderBy(frame => frame).ToList();
            int frame = 0;
            while (frame < lastFrame)
            {
                int windowEnd = Math.Min(lastFrame, frame + 5);
                int key = keys.FirstOrDefault(candidate => candidate > frame && candidate <= windowEnd);
                if (key > frame)
                {
                    frames.Add(key);
                    // A highlighted key pose restarts the five-frame ghost cadence.
                    frame = key;
                }
                else
                {
                    frame = windowEnd;
                    if (frame < lastFrame) frames.Add(frame);
                }
            }
            if (frames[frames.Count - 1] != lastFrame) frames.Add(lastFrame);
            return frames.ToList();
        }

        private static float GhostAlpha(int index, int count, bool separated)
        {
            if (count <= 1) return 1f;
            if (index == 0) return separated ? 1f : .3f;
            if (index == count - 2) return .7f;
            if (index == count - 1) return 1f;
            return Mathf.Lerp(separated ? 1f : .3f, 1f, index / (float)(count - 1));
        }

        private static Color FootTint(SubjectPictureData subject, int frame)
        {
            bool left = subject.LeftContacts[Mathf.Clamp(frame, 0, subject.LeftContacts.Length - 1)];
            bool right = subject.RightContacts[Mathf.Clamp(frame, 0, subject.RightContacts.Length - 1)];
            return left == right ? Color.gray : left ? new Color(.2f, .45f, 1f) : new Color(1f, .2f, .2f);
        }

        private static void CreatePictureEnvironment(List<GameObject> objects, Bounds bounds)
        {
            const int captureLayer = 31;
            float size = Mathf.Ceil(Mathf.Max(bounds.size.x, bounds.size.z) * .5f) * 2f;
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.hideFlags = HideFlags.HideAndDontSave;
            floor.transform.position = new Vector3(bounds.center.x, 0f, bounds.center.z);
            floor.transform.localScale = Vector3.one * (size / 10f);
            SetLayerRecursively(floor, captureLayer);
            floor.GetComponent<Renderer>().sharedMaterial = MakeMaterial(new Color(.31f, .31f, .31f, 1f));
            objects.Add(floor);
            for (float x = bounds.min.x; x <= bounds.max.x; x += .25f)
            {
                CreateWorldLine(objects, new Vector3(x, .006f, bounds.min.z), new Vector3(x, .006f, bounds.max.z),
                    Mathf.Abs(x % 1f) < .01f ? .010f : .003f, new Color(.65f, .65f, .65f, .25f));
            }
            for (float z = bounds.min.z; z <= bounds.max.z; z += .25f)
            {
                CreateWorldLine(objects, new Vector3(bounds.min.x, .006f, z), new Vector3(bounds.max.x, .006f, z),
                    Mathf.Abs(z % 1f) < .01f ? .010f : .003f, new Color(.65f, .65f, .65f, .25f));
            }
            CreateEvidenceLights(objects, bounds.center);
        }

        private static Camera CreateAnalysisPictureCamera(Bounds bounds, Vector3 direction, bool orthographic)
        {
            GameObject cameraObject = new GameObject("Kimodo Analysis Picture Camera") { hideFlags = HideFlags.HideAndDontSave };
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.cullingMask = 1 << 31;
            camera.orthographic = orthographic;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.orthographicSize = Mathf.Max(2.5f, bounds.extents.magnitude * 1.05f);
            camera.fieldOfView = 35f;
            camera.transform.position = bounds.center + direction.normalized * Mathf.Max(7f, bounds.extents.magnitude * 3.2f);
            Vector3 up = Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            camera.transform.LookAt(bounds.center + Vector3.up, up);
            return camera;
        }

        private static TrajectoryScale BuildTrajectoryScale(IReadOnlyList<SubjectPictureData> subjects)
        {
            var speeds = new List<float>();
            var accelerations = new List<float>();
            foreach (SubjectPictureData subject in subjects)
            {
                float previousSpeed = 0f;
                for (int index = 1; index < subject.Pelvis.Length; index++)
                {
                    float speed = (subject.Pelvis[index] - subject.Pelvis[index - 1]).magnitude * (float)SessionFrameRate;
                    speeds.Add(speed);
                    accelerations.Add(Mathf.Abs(speed - previousSpeed) * (float)SessionFrameRate);
                    previousSpeed = speed;
                }
            }
            return new TrajectoryScale(Percentile(speeds, .05f), Percentile(speeds, .95f), Percentile(accelerations, .05f), Percentile(accelerations, .95f));
        }

        private static float Percentile(List<float> values, float percent)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            return values[Mathf.Clamp(Mathf.RoundToInt((values.Count - 1) * percent), 0, values.Count - 1)];
        }

        private static void CreatePelvisTrajectory(List<GameObject> objects, Vector3[] points, TrajectoryScale scale)
        {
            float previousSpeed = 0f;
            for (int index = 1; index < points.Length; index++)
            {
                float speed = (points[index] - points[index - 1]).magnitude * (float)SessionFrameRate;
                float acceleration = Mathf.Abs(speed - previousSpeed) * (float)SessionFrameRate;
                previousSpeed = speed;
                float speedWeight = Mathf.InverseLerp(scale.MinSpeed, Mathf.Max(scale.MinSpeed + .0001f, scale.MaxSpeed), speed);
                float accelerationWeight = Mathf.InverseLerp(scale.MinAcceleration, Mathf.Max(scale.MinAcceleration + .0001f, scale.MaxAcceleration), acceleration);
                Color color = Color.Lerp(Color.red, Color.green, speedWeight);
                color.a = Mathf.Lerp(1f, .2f, accelerationWeight);
                CreateWorldLine(objects, points[index - 1] + Vector3.up * .02f, points[index] + Vector3.up * .02f, .045f, color);
            }
        }

        private static void Fill(Texture2D texture, Color color)
        {
            var pixels = new Color[texture.width * texture.height];
            for (int index = 0; index < pixels.Length; index++) pixels[index] = color;
            texture.SetPixels(pixels);
        }

        private static void DrawInternalGrid(Texture2D texture, PictureLayout layout, int panels)
        {
            for (int column = 1; column < layout.Columns; column++)
            {
                int x = column * layout.CellSize - 2;
                FillRect(texture, x, 0, 4, texture.height, Color.white);
            }
            for (int panel = 1; panel < panels; panel++)
            {
                int y = panel * layout.Height - 2;
                FillRect(texture, 0, y, texture.width, 4, Color.white);
            }
            for (int panel = 0; panel < panels; panel++)
            {
                int originY = panel * layout.Height;
                for (int row = 1; row < layout.Rows; row++)
                {
                    int y = originY + row * layout.CellSize - 2;
                    FillRect(texture, 0, y, texture.width, 4, Color.white);
                }
            }
        }

        private static void DrawTileNumber(Texture2D texture, int value)
        {
            string text = value.ToString(CultureInfo.InvariantCulture);
            int size = texture.width >= 256 ? 4 : 2;
            int width = (size * 4 + size) * text.Length;
            int x = texture.width - width - size * 2;
            int y = texture.height - size * 8;
            foreach (char digit in text)
            {
                DrawSevenSegmentDigit(texture, x, y, digit, size, Color.white);
                x += size * 5;
            }
            texture.Apply(false, false);
        }

        private static void DrawSevenSegmentDigit(Texture2D texture, int x, int y, char digit, int size, Color color)
        {
            bool[] map = digit switch
            {
                '0' => new[] { true, true, true, true, true, true, false },
                '1' => new[] { false, true, true, false, false, false, false },
                '2' => new[] { true, true, false, true, true, false, true },
                '3' => new[] { true, true, true, true, false, false, true },
                '4' => new[] { false, true, true, false, false, true, true },
                '5' => new[] { true, false, true, true, false, true, true },
                '6' => new[] { true, false, true, true, true, true, true },
                '7' => new[] { true, true, true, false, false, false, false },
                '8' => new[] { true, true, true, true, true, true, true },
                '9' => new[] { true, true, true, true, false, true, true },
                _ => new bool[7]
            };
            int w = size * 3;
            int h = size * 6;
            if (map[0]) FillRect(texture, x + size, y + h - size, w, size, color);
            if (map[1]) FillRect(texture, x + w + size, y + h / 2, size, h / 2, color);
            if (map[2]) FillRect(texture, x + w + size, y, size, h / 2, color);
            if (map[3]) FillRect(texture, x + size, y, w, size, color);
            if (map[4]) FillRect(texture, x, y, size, h / 2, color);
            if (map[5]) FillRect(texture, x, y + h / 2, size, h / 2, color);
            if (map[6]) FillRect(texture, x + size, y + h / 2 - size / 2, w, size, color);
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
            JArray poses,
            JObject analysis,
            byte[] motionBytes,
            TimelineAnimationRecord animation = null,
            string inputSignature = null)
        {
            if (motionBytes == null || motionBytes.Length == 0)
            {
                throw new InvalidOperationException("Analysis cannot be cached without dense KMB motion.");
            }
            string id = Guid.NewGuid().ToString("D");
            string motionPath = AnalysisMotionCachePath(session, id);
            Directory.CreateDirectory(Path.GetDirectoryName(motionPath));
            File.WriteAllBytes(motionPath, motionBytes);
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
                Analysis = analysis != null ? (JObject)analysis.DeepClone() : new JObject(),
                MotionPath = ToProjectRelativePath(motionPath),
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

        private static string EvidenceFolder(TimelineSessionRecord session) =>
            Path.Combine(GetSessionGeneratedFolder(session), "Pictures");

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
            Vector3 position = sample.characterPose != null ? sample.characterPose.root.t : Vector3.zero;
            Quaternion rotation = sample.characterPose != null ? sample.characterPose.root.q : Quaternion.identity;
            if (!root2DOnly && sample.characterPose != null && sample.characterPose.TryValidate(out _))
            {
                HumanPose pose = CharacterPoseMuscleAdapter.ToMuscleSample(sample.characterPose).pose;
                using (var handler = new HumanPoseHandler(character.Avatar, animator.transform))
                {
                    handler.SetHumanPose(ref pose);
                }
            }
            animator.transform.SetPositionAndRotation(position, rotation);
            return preview;
        }

        private static Vector3 PreviewRootPosition(GameObject preview)
        {
            Animator animator = preview.GetComponentInChildren<Animator>(true);
            return animator != null ? animator.transform.position : preview.transform.position;
        }

        private static void CreateEvidenceLights(List<GameObject> objects, Vector3 center)
        {
            foreach (var setup in new[]
            {
                (position: new Vector3(-4f, 6f, -4f), intensity: 1.1f),
                (position: new Vector3(4f, 3f, -2f), intensity: .55f),
                (position: new Vector3(0f, 5f, 5f), intensity: .35f)
            })
            {
                GameObject lightObject = new GameObject("Kimodo Evidence Light") { hideFlags = HideFlags.HideAndDontSave };
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = setup.intensity;
                lightObject.transform.position = center + setup.position;
                lightObject.transform.LookAt(center);
                objects.Add(lightObject);
            }
        }

        private static void CreateWorldLine(List<GameObject> objects, Vector3 from, Vector3 to, float width, Color color)
        {
            GameObject lineObject = new GameObject("Kimodo Evidence Line") { hideFlags = HideFlags.HideAndDontSave };
            SetLayerRecursively(lineObject, 31);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPositions(new[] { from, to });
            line.startWidth = line.endWidth = width;
            line.useWorldSpace = true;
            line.sharedMaterial = MakeMaterial(color);
            line.startColor = line.endColor = color;
            objects.Add(lineObject);
        }

        private static Material MakeMaterial(Color color)
        {
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            return new Material(shader) { hideFlags = HideFlags.HideAndDontSave, color = color };
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = layer;
        }

        private static Texture2D RenderCamera(Camera camera, int size, Color background)
        {
            RenderTexture renderTexture = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            try
            {
                camera.backgroundColor = background;
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                var image = new Texture2D(size, size, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                image.Apply(false, false);
                return image;
            }
            finally
            {
                RenderTexture.active = previous;
                camera.targetTexture = null;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static void Composite(Texture2D destination, Texture2D source, float alpha)
        {
            Color[] destinationPixels = destination.GetPixels();
            Color[] sourcePixels = source.GetPixels();
            for (int index = 0; index < destinationPixels.Length; index++)
            {
                if (sourcePixels[index].a > .01f)
                {
                    destinationPixels[index] = Color.Lerp(
                        destinationPixels[index], sourcePixels[index], alpha * sourcePixels[index].a);
                }
            }
            destination.SetPixels(destinationPixels);
            destination.Apply(false, false);
        }

        private static void SetEvidenceVisualsEnabled(IReadOnlyList<GameObject> objects, bool enabled)
        {
            foreach (GameObject item in objects)
            {
                if (item == null) continue;
                foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>(true)) renderer.enabled = enabled;
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
                throw new InvalidOperationException($"Unknown analysis_id '{id}' in the selected Session.");
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
                ["contract"] = "animation_analysis_picture_v2",
                ["character_ref"] = character?.CharacterRef ?? string.Empty,
                ["animation_id"] = animation?.Id.ToString("D") ?? string.Empty,
                ["start_frame"] = animation?.StartFrame ?? 0,
                ["end_frame_exclusive"] = animation?.EndFrameExclusive ?? 0,
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

        private sealed class AnalysisSubject
        {
            public AnalysisSubject(
                string role,
                TimelineCharacterRecord character,
                TimelineAnimationRecord animation,
                AnalysisCacheRecord record,
                int startFrame,
                int endFrameExclusive)
            {
                Role = role;
                Character = character;
                Animation = animation;
                Record = record;
                StartFrame = startFrame;
                EndFrameExclusive = endFrameExclusive;
            }
            public string Role { get; }
            public TimelineCharacterRecord Character { get; }
            public TimelineAnimationRecord Animation { get; }
            public AnalysisCacheRecord Record { get; }
            public int StartFrame { get; }
            public int EndFrameExclusive { get; }
        }

        private sealed class SubjectPictureData
        {
            public SubjectPictureData(
                AnalysisSubject subject,
                Vector3[] pelvis,
                bool[] leftContacts,
                bool[] rightContacts,
                Bounds firstBounds,
                Bounds lastBounds,
                Bounds bounds)
            {
                Subject = subject;
                Pelvis = pelvis;
                LeftContacts = leftContacts;
                RightContacts = rightContacts;
                FirstBounds = firstBounds;
                LastBounds = lastBounds;
                Bounds = bounds;
                KeyFrameSet = new HashSet<int>((subject.Record.Analysis?["keyframes"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Select(item => Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, Math.Max(0, pelvis.Length - 1))));
            }
            public AnalysisSubject Subject { get; }
            public Vector3[] Pelvis { get; }
            public bool[] LeftContacts { get; }
            public bool[] RightContacts { get; }
            public Bounds FirstBounds { get; }
            public Bounds LastBounds { get; }
            public Bounds Bounds { get; }
            public HashSet<int> KeyFrameSet { get; }
        }

        private sealed class PictureTile
        {
            private PictureTile(SubjectPictureData subject, string presentation, JObject description)
            {
                Subject = subject;
                Presentation = presentation;
                Description = description;
                Direction = new Vector3(1f, .75f, -1f);
            }
            public SubjectPictureData Subject { get; }
            public string Presentation { get; }
            public JObject Description { get; }
            public Vector3 Direction { get; private set; }
            public bool Orthographic { get; private set; }
            public int Frame { get; private set; }
            public List<int> TrajectoryFrames { get; private set; } = new List<int>();

            public static PictureTile Ghost(SubjectPictureData subject, string view, Vector3 direction, bool orthographic)
            {
                return new PictureTile(subject, "ghost", new JObject { ["presentation"] = "ghost", ["view"] = view })
                {
                    Direction = direction,
                    Orthographic = orthographic
                };
            }

            public static PictureTile Key(SubjectPictureData subject, int frame) =>
                new PictureTile(subject, "key", new JObject { ["presentation"] = "key_pose", ["frame"] = frame }) { Frame = frame };

            public static PictureTile FootContact(SubjectPictureData subject, int frame, JObject contact) =>
                new PictureTile(subject, "foot_contact", new JObject
                {
                    ["presentation"] = "foot_contact",
                    ["frame"] = frame,
                    ["foot_contact"] = contact.DeepClone()
                }) { Frame = frame };

            public static PictureTile FootFallback(SubjectPictureData subject, int frame) =>
                new PictureTile(subject, "foot_fallback", new JObject
                {
                    ["presentation"] = "key_pose_fallback_for_foot_contact",
                    ["frame"] = frame
                }) { Frame = frame };

            public static PictureTile Trajectory(SubjectPictureData subject, IEnumerable<int> keyFrames)
            {
                var frames = new SortedSet<int> { 0, Math.Max(0, subject.Pelvis.Length - 1) };
                foreach (int frame in keyFrames) frames.Add(frame);
                return new PictureTile(subject, "trajectory", new JObject
                {
                    ["presentation"] = "pelvis_trajectory",
                    ["bone"] = "hips",
                    ["frames"] = new JArray(frames)
                }) { TrajectoryFrames = frames.ToList() };
            }
        }

        private readonly struct PictureLayout
        {
            private PictureLayout(int columns, int rows, int cellSize)
            {
                Columns = columns;
                Rows = rows;
                CellSize = cellSize;
            }
            public int Columns { get; }
            public int Rows { get; }
            public int CellSize { get; }
            public int Width => Columns * CellSize;
            public int Height => Rows * CellSize;
            public static PictureLayout ForLevel(string level) => level == "high"
                ? new PictureLayout(8, 2, 256)
                : level == "middle"
                    ? new PictureLayout(4, 2, 256)
                    : new PictureLayout(2, 1, 128);
        }

        private readonly struct TrajectoryScale
        {
            public TrajectoryScale(float minSpeed, float maxSpeed, float minAcceleration, float maxAcceleration)
            {
                MinSpeed = minSpeed;
                MaxSpeed = maxSpeed;
                MinAcceleration = minAcceleration;
                MaxAcceleration = maxAcceleration;
            }
            public float MinSpeed { get; }
            public float MaxSpeed { get; }
            public float MinAcceleration { get; }
            public float MaxAcceleration { get; }
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
            public string MotionPath;
            public string AnimationId;
            public string AnimationName;
            public string InputSignature;
            public JObject Pictures;

            public JObject ToJson() => new JObject
            {
                ["analysis_id"] = Id, ["session_id"] = SessionId, ["timeline_asset_guid"] = TimelineAssetGuid,
                ["session_name"] = SessionName,
                ["character_ref"] = CharacterRef, ["character"] = CharacterName,
                ["start"] = Start, ["end"] = End, ["created_at_utc"] = CreatedAtUtc,
                ["motion_path"] = MotionPath ?? string.Empty,
                ["animation_id"] = AnimationId ?? string.Empty,
                ["animation_name"] = AnimationName ?? string.Empty,
                ["input_signature"] = InputSignature ?? string.Empty,
                ["poses"] = Poses?.DeepClone() ?? new JArray(),
                ["analysis"] = Analysis?.DeepClone() ?? new JObject(),
                ["pictures"] = Pictures?.DeepClone() ?? new JObject()
            };

            public static AnalysisCacheRecord FromJson(JObject json) => new AnalysisCacheRecord
            {
                Id = json.Value<string>("analysis_id"), SessionId = json.Value<string>("session_id"),
                TimelineAssetGuid = json.Value<string>("timeline_asset_guid"),
                SessionName = json.Value<string>("session_name"), CharacterRef = json.Value<string>("character_ref"),
                CharacterName = json.Value<string>("character"), Start = json.Value<double>("start"),
                End = json.Value<double>("end"), CreatedAtUtc = json.Value<DateTime>("created_at_utc"),
                MotionPath = json.Value<string>("motion_path"),
                AnimationId = json.Value<string>("animation_id"),
                AnimationName = json.Value<string>("animation_name"),
                InputSignature = json.Value<string>("input_signature"),
                Poses = json["poses"] as JArray ?? json["analysis"]?["poses"] as JArray ?? new JArray(),
                Analysis = json["analysis"] as JObject ?? new JObject(),
                Pictures = json["pictures"] as JObject ?? new JObject()
            };
        }
    }
}
