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
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private static readonly Dictionary<string, AnalysisCacheRecord> AnalysisCache =
            new Dictionary<string, AnalysisCacheRecord>(StringComparer.OrdinalIgnoreCase);

        private const string AnalysisPictureRenderVersion = "21-humanbodybones-mesh";
        private const string TestAnalysisPictureRenderVersion = "33-root2d-pelvis-heading";
        private const int PictureSupersample = 2;
        private const int AnalysisKeyframeCount = 8;
        private const int TestPoseSupersampleHeight = 2048;
        private const float TestPoseJointCameraOffsetMeters = .2f;
        private const float TestPoseFootForwardCameraOffsetMeters = .3f;
        private const float TestPoseHeadCameraOffsetMeters = .3f;
        private const float TestCameraMarginMeters = .5f;
        private const float TestCameraFitScale = 1f;
        private const float TestGhostAlphaMin = .1f;
        private const float TestGhostAlphaMax = .5f;
        private const float StationaryTrajectoryRange = .25f;
        private const int StationaryTrajectoryMinFrames = 10;
        private const float StationaryTrajectoryAlphaBoost = .1f;
        private const float MaxPromotedGhostAlpha = .75f;
        private static readonly Color TestStartFrameTint = new Color(57f / 255f, 197f / 255f, 187f / 255f, 1f);
        private static readonly Color TestEndFrameTint = new Color(217f / 255f, 58f / 255f, 73f / 255f, 1f);
        private static Scene analysisPreviewScene;

        private static JObject RenderAnalysisPictures(
            TimelineSessionRecord session,
            IReadOnlyList<AnalysisSubject> subjects,
            string level,
            int requestedResolution)
        {
            string signature = BuildPictureSignature(subjects, level, requestedResolution);
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
            TrajectoryScale trajectoryScale = BuildTrajectoryScale(data, true);
            var tiles = new List<PictureTile>();
            foreach (SubjectPictureData subject in data)
            {
                tiles.AddRange(BuildPictureTiles(subject, level));
            }

            int maxTileCount = Math.Max(
                1,
                data.Select(subject => tiles.Count(tile => ReferenceEquals(tile.Subject, subject))).DefaultIfEmpty(1).Max());
            PictureLayout layout = PictureLayout.ForLevel(maxTileCount, level == "high", requestedResolution);

            int tileWidth = layout.TileSize;
            int tileHeight = ResolvePictureTileHeight(layout, tiles, tileWidth);
            int panelHeight = layout.TileRows * tileHeight;

            List<RectInt> imageRects;
            int imageWidth;
            int imageHeight;
            bool cached = false;
            Directory.CreateDirectory(EvidenceFolder(session));
            Scene previousPreviewScene = analysisPreviewScene;
            Scene previewScene = EditorSceneManager.NewPreviewScene();
            Texture2D canvas;
            try
            {
                analysisPreviewScene = previewScene;
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
                analysisPreviewScene = previousPreviewScene;
                if (previewScene.IsValid()) EditorSceneManager.ClosePreviewScene(previewScene);
            }
            try
            {
                imageWidth = canvas.width;
                imageHeight = canvas.height;
                File.WriteAllBytes(imagePath, canvas.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvas);
            }

            var descriptions = new JArray();
            for (int index = 0; index < tiles.Count; index++)
            {
                PictureTile tile = tiles[index];
                RectInt rect = imageRects[index];
                int panel = data.FindIndex(item => ReferenceEquals(item, tile.Subject));
                int localIndex = tiles.Take(index).Count(item => ReferenceEquals(item.Subject, tile.Subject));
                JObject description = (JObject)tile.Description.DeepClone();
                description["subject"] = tile.Subject.Subject.Role;
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
                ["level"] = level,
                ["image_path"] = projectPath,
                ["width"] = imageWidth,
                ["height"] = imageHeight,
                ["resolution"] = requestedResolution,
                ["supersample"] = PictureSupersample,
                ["images"] = descriptions,
                ["cached"] = cached
            };
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
            string level,
            int requestedResolution)
        {
            // All humanoid picture levels now use the depth-tested test renderer.
            string renderVersion = TestAnalysisPictureRenderVersion;
            string source = renderVersion + "|" + level + "|" + requestedResolution + "|" + PictureSupersample + "|" +
                string.Join("|", subjects.Select(item => item.Role + ":" + item.Record.Id));
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
            GameObject posePreview = null;
            KimodoMarkerSampleResult[] samples;
            try
            {
                samples = CaptureSampleResults(subject.Character, subject.StartFrame, frameCount);
                posePreview = CreateCanonicalPosePreview(subject.Character);
                Animator poseAnimator = posePreview.GetComponentInChildren<Animator>(true)
                    ?? throw new InvalidOperationException($"Character '{subject.Character.Name}' pose preview has no Animator.");
                for (int localFrame = 0; localFrame < frameCount; localFrame++)
                {
                    KimodoMarkerSampleResult sample = samples[localFrame];
                    ApplyCanonicalPoseToPreview(posePreview, subject.Character, sample);

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
                    Bounds currentBounds = CalculateSkinnedBounds(posePreview);
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
                if (posePreview != null) UnityEngine.Object.DestroyImmediate(posePreview);
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

        private static List<PictureTile> BuildPictureTiles(SubjectPictureData subject, string level)
        {
            if (!IsHumanoidCharacter(subject.Subject.Character))
            {
                return SelectKeyFrames(subject, AnalysisKeyframeCount)
                    .Select(frame => PictureTile.MeshPose(subject, frame, "mesh_pose"))
                    .ToList();
            }

            if (level == "low" || level == "middle" || level == "high")
            {
                var result = new List<PictureTile>
                {
                    PictureTile.TestFootTransitions(subject, new Vector3(1f, .75f, -1f)),
                    PictureTile.TestKeyframes(subject, new Vector3(1f, .75f, -1f))
                };

                if (level == "middle" || level == "high")
                {
                    result.Insert(0, PictureTile.TestRoot2D(subject, new Vector3(0f, 1f, 0f)));
                    foreach (int frame in SelectKeyFrames(subject, AnalysisKeyframeCount).OrderBy(frame => frame))
                    {
                        result.Add(PictureTile.TestPose(subject, frame, "keyframe", new Vector3(1f, .75f, -1f)));
                    }
                }

                if (level == "high")
                {
                    foreach (int frame in FootTransitionFrames(subject).OrderBy(frame => frame))
                    {
                        result.Add(PictureTile.TestPose(subject, frame, "foot_transition", new Vector3(1f, .75f, -1f)));
                    }
                }

                if (level == "middle" || level == "high")
                {
                    int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
                    result.Add(PictureTile.TestPose(subject, 0, "start", new Vector3(1f, .75f, -1f)));
                    result.Add(PictureTile.TestPose(subject, lastFrame, "end", new Vector3(1f, .75f, -1f)));
                }

                return result;
            }

            throw new InvalidOperationException($"Unsupported analysis picture level '{level}'.");
        }

        private static List<int> SelectKeyFrames(SubjectPictureData subject, int count)
        {
            var frames = (subject.Subject.Record.Analysis?["keyframes"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(item => Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, subject.Pelvis.Length - 1))
                .Distinct()
                .Take(Math.Max(0, count))
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
                Texture2D result = RenderCamera(camera, size, new Color(.12f, .12f, .12f, 1f));
                if (tile.Presentation == "ghost")
                {
                    List<int> frames = BuildGhostFrames(tile.Subject, out HashSet<int> promotedFrames);
                    bool separated = !tile.Subject.FirstBounds.Intersects(tile.Subject.LastBounds);
                    for (int index = 0; index < frames.Count; index++)
                    {
                        int frame = frames[index];
                        float alpha = GhostAlpha(index, frames.Count, separated);
                        if (promotedFrames.Contains(frame))
                        {
                            alpha = Mathf.Min(MaxPromotedGhostAlpha, alpha + StationaryTrajectoryAlphaBoost);
                        }
                        RenderPoseOnto(result, camera, environment, tile.Subject, frame, ResolveGhostPoseTint(tile.Subject, frame), alpha);
                    }
                }
                else if (tile.Presentation == "key" || tile.Presentation == "foot_contact" || tile.Presentation == "foot_fallback")
                {
                    Color tint = tile.Presentation == "key" ? Color.yellow : FootTint(tile.Subject, tile.Frame);
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
            Shader depthShader = Shader.Find("Hidden/Kimodo/PoseDepthEncode")
                ?? throw new InvalidOperationException("Pose depth encoder shader is unavailable.");
            try
            {
                // The floor and trajectories are a stable, opaque base layer.
                SetEvidenceVisualsEnabled(environment, true);
                Texture2D result = RenderCamera(camera, width, height, background);
                Color[] resultPixels = result.GetPixels();
                var nearestPoseDepth = new float[resultPixels.Length];
                var hasPoseDepth = new bool[resultPixels.Length];

                SetEvidenceVisualsEnabled(environment, false);
                foreach (TestVirtualPose pose in poses)
                {
                    SetPreviewRenderersEnabled(pose.Preview, true);
                    try
                    {
                        Texture2D color = RenderCamera(camera, width, height, Color.clear);
                        Texture2D depth = RenderCameraDepth(camera, depthShader, width, height);
                        try
                        {
                            CompositeNearestPose(
                                resultPixels,
                                nearestPoseDepth,
                                hasPoseDepth,
                                color.GetPixels(),
                                depth.GetPixels(),
                                pose.Alpha);
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(color);
                            UnityEngine.Object.DestroyImmediate(depth);
                        }
                    }
                    finally
                    {
                        SetPreviewRenderersEnabled(pose.Preview, false);
                    }
                }

                result.SetPixels(resultPixels);
                result.Apply(false, false);
                SetEvidenceVisualsEnabled(environment, false);
                foreach (GameObject item in environment)
                {
                    if (item == null) continue;
                    foreach (LineRenderer line in item.GetComponentsInChildren<LineRenderer>(true)) line.enabled = true;
                }
                Texture2D trajectoryLayer = RenderCamera(camera, width, height, Color.clear);
                try
                {
                    Composite(result, trajectoryLayer, 1f);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(trajectoryLayer);
                }
                return result;
            }
            finally
            {
                camera.targetTexture = null;
                SetEvidenceVisualsEnabled(environment, true);
            }
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
            IReadOnlyList<int> keyframes = SelectKeyFrames(subject, AnalysisKeyframeCount).OrderBy(frame => frame).ToArray();
            foreach (int frame in keyframes)
            {
                int clamped = Mathf.Clamp(frame, 0, Math.Max(0, groundPoints.Length - 1));
                Vector3 origin = groundPoints.Length > 0 ? groundPoints[clamped] : Vector3.zero;
                Color tint = clamped == 0 ? TestStartFrameTint :
                    clamped == groundPoints.Length - 1 ? TestEndFrameTint : Color.yellow;
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

        private static void CreateWorldLine(List<GameObject> objects, IReadOnlyList<Vector3> points, Color color, float width)
        {
            if (points == null || points.Count < 2) return;
            GameObject lineObject = MoveToAnalysisPreviewScene(
                new GameObject("Kimodo Root2D Pelvis Trajectory") { hideFlags = HideFlags.HideAndDontSave });
            SetLayerRecursively(lineObject, 31);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = points.Count;
            line.SetPositions(points.Select(point => point + Vector3.up * .025f).ToArray());
            line.startWidth = line.endWidth = width;
            line.useWorldSpace = true;
            line.sharedMaterial = MakeUnlitMaterial(color);
            line.startColor = line.endColor = color;
            objects.Add(lineObject);
        }

        private static void CreateGroundMarker(List<GameObject> objects, Vector3 position, float radius, Color color)
        {
            GameObject marker = MoveToAnalysisPreviewScene(GameObject.CreatePrimitive(PrimitiveType.Cylinder));
            marker.name = "Kimodo Root2D Keyframe";
            marker.hideFlags = HideFlags.HideAndDontSave;
            SetLayerRecursively(marker, 31);
            marker.transform.position = position + Vector3.up * .045f;
            marker.transform.localScale = new Vector3(radius, .04f, radius);
            marker.GetComponent<Renderer>().sharedMaterial = MakeUnlitMaterial(color);
            objects.Add(marker);
        }

        private static void CreateHeadingArrow(List<GameObject> objects, Vector3 origin, Vector3 forward, float length, Color color)
        {
            Vector3 flat = new Vector3(forward.x, 0f, forward.z);
            if (flat.sqrMagnitude < .0001f) flat = Vector3.forward;
            flat.Normalize();
            Vector3 tip = origin + flat * length;
            CreateWorldLine(objects, origin + Vector3.up * .07f, tip + Vector3.up * .07f, .035f, color, true);
            Quaternion left = Quaternion.AngleAxis(150f, Vector3.up);
            Quaternion right = Quaternion.AngleAxis(-150f, Vector3.up);
            CreateWorldLine(objects, tip + Vector3.up * .07f, tip + left * flat * .16f + Vector3.up * .07f, .035f, color, true);
            CreateWorldLine(objects, tip + Vector3.up * .07f, tip + right * flat * .16f + Vector3.up * .07f, .035f, color, true);
        }

        private static Vector3 SampleRootForward(SubjectPictureData subject, int frame)
        {
            try
            {
                subject.GetSample(frame).sampleData.GetRoot(out _, out Quaternion rotation);
                return rotation * Vector3.forward;
            }
            catch
            {
                return Vector3.forward;
            }
        }

        private static Texture2D RenderCameraDepth(Camera camera, Shader depthShader, int width, int height)
        {
            RenderTexture renderTexture = RenderTexture.GetTemporary(
                width,
                height,
                24,
                RenderTextureFormat.ARGBFloat);
            RenderTexture previous = RenderTexture.active;
            try
            {
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.targetTexture = renderTexture;
                camera.RenderWithShader(depthShader, "RenderType");
                RenderTexture.active = renderTexture;
                var image = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
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

        private static void CompositeNearestPose(
            Color[] destination,
            float[] nearestDepth,
            bool[] hasDepth,
            Color[] color,
            Color[] depth,
            float poseAlpha)
        {
            bool reversedZ = SystemInfo.usesReversedZBuffer;
            for (int index = 0; index < destination.Length; index++)
            {
                Color source = color[index];
                if (source.a <= .01f) continue;
                float sourceDepth = depth[index].r;
                bool isNearer = !hasDepth[index] ||
                    (reversedZ ? sourceDepth > nearestDepth[index] : sourceDepth < nearestDepth[index]);
                if (!isNearer) continue;

                float alpha = Mathf.Clamp01(poseAlpha * source.a);
                destination[index] = Color.Lerp(destination[index], source, alpha);
                nearestDepth[index] = sourceDepth;
                hasDepth[index] = true;
            }
        }

        private static Material CreateTestPoseCompositeMaterial()
        {
            // Use the engine-provided transparent blit shader. Custom
            // SV_Depth fullscreen passes crash Tuanjie/URP on some GPUs and
            // render as the magenta error material; pose geometry itself still
            // gets a fresh depth buffer in each camera.Render call above.
            Shader shader = Shader.Find("Sprites/Default") ??
                Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ??
                Shader.Find("Unlit/Transparent");
            if (shader == null)
            {
                throw new InvalidOperationException("No transparent pose composite shader is available.");
            }
            return new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        }

        private static void RenderTestPoseOnto(
            Texture2D destination,
            Camera camera,
            IReadOnlyList<GameObject> environment,
            TestVirtualPose pose)
        {
            // Render one sampled pose as opaque first.  A fresh depth buffer
            // resolves every mesh on the character before its whole image is
            // alpha-composited, avoiding transparent sorting between Mixamo's
            // separate body and clothing renderers.
            SetEvidenceVisualsEnabled(environment, false);
            SetPreviewRenderersEnabled(pose.Preview, true);
            Texture2D layer = RenderCamera(camera, destination.width, destination.height, new Color(0f, 0f, 0f, 0f));
            try
            {
                Composite(destination, layer, pose.UsesGhostMaterial ? 1f : pose.Alpha);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(layer);
                SetPreviewRenderersEnabled(pose.Preview, false);
                SetEvidenceVisualsEnabled(environment, true);
            }
        }

        private static TestPosePlan BuildTestPosePlan(SubjectPictureData subject, IReadOnlyList<int> frames)
        {
            GameObject source = CreateCanonicalPosePreview(subject.Subject.Character);
            var snapshots = new Dictionary<int, TestPoseSnapshot>();
            try
            {
                foreach (int frame in frames.Distinct().OrderBy(item => item))
                {
                    KimodoMarkerSampleResult sample = subject.GetSample(frame);
                    ApplyCanonicalPoseToPreview(source, subject.Subject.Character, sample);
                    snapshots[frame] = TestPoseSnapshot.Capture(source);
                }
                // The source is only a transform snapshot template. Keep it
                // out of the capture layer so each pose is rendered exactly
                // once by its dedicated virtual-pose instance.
                SetPreviewRenderersEnabled(source, false);
                return new TestPosePlan(source, snapshots);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(source);
                throw;
            }
        }

        private static TestVirtualPose CreateTestVirtualPose(
            TestPoseSnapshot snapshot,
            Color tint,
            float alpha)
        {
            GameObject preview = MoveToAnalysisPreviewScene(UnityEngine.Object.Instantiate(snapshot.SourcePrefab));
            preview.name = "Kimodo Test Virtual Pose";
            preview.hideFlags = HideFlags.HideAndDontSave;
            foreach (Animator animator in preview.GetComponentsInChildren<Animator>(true))
            {
                UnityEngine.Object.DestroyImmediate(animator);
            }
            snapshot.Apply(preview);
            var transientMaterials = new List<Material>();
            // Comparison render: keep the original/default material path.
            TintPreview(preview, tint, transientMaterials);
            SetPreviewRenderersEnabled(preview, false);
            return new TestVirtualPose(preview, transientMaterials, alpha, false);
        }

        private static void SetPreviewRenderersEnabled(GameObject preview, bool enabled)
        {
            if (preview == null) return;
            foreach (Renderer renderer in preview.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    skinned.updateWhenOffscreen = true;
                    skinned.localBounds = new Bounds(Vector3.zero, Vector3.one * 100f);
                }
                renderer.enabled = enabled;
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
            if (!IsHumanoidCharacter(subject.Subject.Character))
            {
                return CreateMeshPosePreview(subject.Subject, localFrame);
            }

            TimelineCharacterRecord character = subject.Subject.Character;
            GameObject preview = CreateCanonicalPosePreview(character);
            KimodoMarkerSampleResult sample = subject.GetSample(localFrame);
            ApplyCanonicalPoseToPreview(preview, character, sample);
            return preview;
        }

        private static GameObject CreateMeshPosePreview(AnalysisSubject subject, int localFrame)
        {
            if (subject?.Character?.Root == null)
            {
                throw new InvalidOperationException("Mesh-only analysis has no scene object to preview.");
            }
            GameObject preview = MoveToAnalysisPreviewScene(UnityEngine.Object.Instantiate(subject.Character.Root));
            preview.name = "Kimodo Mesh Pose Preview";
            preview.hideFlags = HideFlags.HideAndDontSave;
            foreach (Transform transform in preview.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = 31;
            }

            foreach (Animator animator in preview.GetComponentsInChildren<Animator>(true))
            {
                animator.enabled = false;
                animator.runtimeAnimatorController = null;
            }

            AnimationClip clip = subject.Animation?.Clip;
            if (clip != null)
            {
                double timelineTime = (subject.StartFrame + Mathf.Max(0, localFrame)) / SessionFrameRate;
                float sourceTime = (float)KimodoMarkerSamplingUtility.ResolveAnimationSourceTime(
                    subject.Animation.TimelineClip,
                    timelineTime);
                clip.SampleAnimation(preview, Mathf.Clamp(sourceTime, 0f, clip.length));
            }
            foreach (SkinnedMeshRenderer renderer in preview.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                renderer.updateWhenOffscreen = true;
                renderer.localBounds = new Bounds(Vector3.zero, Vector3.one * 100f);
            }
            return preview;
        }

        private static GameObject CreateCanonicalPosePreview(TimelineCharacterRecord character)
        {
            GameObject preview = MoveToAnalysisPreviewScene(UnityEngine.Object.Instantiate(character.Root));
            preview.name = "Kimodo Pose Preview";
            preview.hideFlags = HideFlags.HideAndDontSave;
            foreach (Transform transform in preview.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = 31;
            }

            Animator animator = preview.GetComponentInChildren<Animator>(true)
                ?? throw new InvalidOperationException($"Character '{character.Name}' preview has no Animator.");
            // The preview must use the same humanoid Avatar as the Session
            // character before applying canonical HumanPose snapshots.
            if (KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                animator.avatar = character.Avatar;
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = true;
                animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
            }
            return preview;
        }

        private static void ApplyCanonicalPoseToPreview(
            GameObject preview,
            TimelineCharacterRecord character,
            KimodoMarkerSampleResult sample)
        {
            if (sample?.sampleData == null || !sample.sampleData.IsValid) return;
            Animator animator = preview.GetComponentInChildren<Animator>(true);
            if (animator == null || !KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar)) return;
            HumanPose pose = KimodoMuscleSampleHumanPoseAdapter.ToHumanPose(sample.sampleData);
            using (var handler = new HumanPoseHandler(character.Avatar, animator.transform))
            {
                handler.SetHumanPose(ref pose);
            }
            if (TryGetRoot2DWorld(sample, out Vector3 rootPosition, out Quaternion rootRotation))
            {
                animator.transform.SetPositionAndRotation(rootPosition, rootRotation);
            }
        }

        private static void TintPreview(GameObject preview, Color tint)
        {
            foreach (Renderer renderer in preview.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.materials)
                {
                    if (material != null)
                    {
                        Color current = material.HasProperty("_BaseColor")
                            ? material.GetColor("_BaseColor")
                            : material.color;
                        Color result = Color.Lerp(current, tint, .8f);
                        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", result);
                        if (material.HasProperty("_Color")) material.SetColor("_Color", result);
                    }
                }
            }
        }

        private static List<int> BuildGhostFrames(SubjectPictureData subject, out HashSet<int> promotedFrames)
        {
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            var keyFrames = new HashSet<int>(subject.KeyFrameSet);
            var events = keyFrames
                .Concat(FootTransitionFrames(subject))
                .Append(0)
                .Append(lastFrame)
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();

            // Keep a nearby key pose over a foot transition. If neither event
            // is a key pose, keep the earlier one to preserve time ordering.
            for (int index = 1; index < events.Count;)
            {
                int previous = events[index - 1];
                int current = events[index];
                if (current - previous >= 10 || previous == 0 || current == lastFrame)
                {
                    index++;
                    continue;
                }
                if (keyFrames.Contains(current) && !keyFrames.Contains(previous))
                {
                    events.RemoveAt(index - 1);
                    if (index > 1) index--;
                }
                else
                {
                    events.RemoveAt(index);
                }
            }

            // Fill only long gaps. The rounded divisions produce evenly spaced
            // white auxiliary poses and leave no adjacent samples over 20 frames apart.
            var result = new List<int> { events[0] };
            for (int index = 1; index < events.Count; index++)
            {
                int from = events[index - 1];
                int to = events[index];
                int gap = to - from;
                // A 20-frame gap is allowed as-is. Add helpers only when the
                // gap is strictly larger, then keep each result below 20 frames.
                int divisions = gap > 20 ? Mathf.CeilToInt(gap / 19f) : 1;
                for (int part = 1; part < divisions; part++)
                {
                    result.Add(from + Mathf.RoundToInt(gap * part / (float)divisions));
                }
                result.Add(to);
            }
            var protectedFrames = new HashSet<int>(events);
            return FilterStationaryBlankFrames(subject, result, protectedFrames, out promotedFrames);
        }

        private static List<int> BuildTestSampleFrames(
            SubjectPictureData subject,
            IEnumerable<int> primaryFrames,
            bool preserveAllPrimaryFrames,
            out HashSet<int> promotedFrames)
        {
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            var events = (primaryFrames ?? Enumerable.Empty<int>())
                .Select(frame => Mathf.Clamp(frame, 0, lastFrame))
                .Append(0)
                .Append(lastFrame)
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();

            // Keyframe panels compact nearby events. Foot-transition panels keep
            // every authored transition, even when two events are within 10 frames.
            for (int index = 1; !preserveAllPrimaryFrames && index < events.Count;)
            {
                int previous = events[index - 1];
                int current = events[index];
                if (current - previous >= 10)
                {
                    index++;
                    continue;
                }
                if (current == lastFrame && previous != 0)
                {
                    events.RemoveAt(index - 1);
                    if (index > 1) index--;
                }
                else
                {
                    events.RemoveAt(index);
                }
            }

            int maximumGap = preserveAllPrimaryFrames ? 10 : 20;
            var result = new List<int> { events[0] };
            for (int index = 1; index < events.Count; index++)
            {
                int from = events[index - 1];
                int to = events[index];
                int gap = to - from;
                int divisions = gap > maximumGap ? Mathf.CeilToInt(gap / (float)maximumGap) : 1;
                for (int part = 1; part < divisions; part++)
                {
                    result.Add(from + Mathf.RoundToInt(gap * part / (float)divisions));
                }
                result.Add(to);
            }
            var protectedFrames = new HashSet<int>((primaryFrames ?? Enumerable.Empty<int>())
                .Select(frame => Mathf.Clamp(frame, 0, lastFrame)))
            {
                0,
                lastFrame
            };
            return FilterStationaryBlankFrames(subject, result, protectedFrames, out promotedFrames);
        }

        private static List<int> FilterStationaryBlankFrames(
            SubjectPictureData subject,
            IReadOnlyList<int> frames,
            ISet<int> protectedFrames,
            out HashSet<int> promotedFrames)
        {
            var ordered = (frames ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();
            var removed = new HashSet<int>();
            promotedFrames = new HashSet<int>();
            for (int index = 1; index < ordered.Count - 1;)
            {
                int frame = ordered[index];
                if (protectedFrames.Contains(frame))
                {
                    index++;
                    continue;
                }

                int anchorFrame = ordered[index - 1];
                int runEnd = index;
                while (runEnd < ordered.Count - 1 &&
                       !protectedFrames.Contains(ordered[runEnd]) &&
                       Vector3.Distance(subject.Pelvis[anchorFrame], subject.Pelvis[ordered[runEnd]]) <= StationaryTrajectoryRange)
                {
                    runEnd++;
                }

                if (runEnd > index && ordered[runEnd - 1] - anchorFrame >= StationaryTrajectoryMinFrames)
                {
                    for (int removeIndex = index; removeIndex < runEnd; removeIndex++)
                    {
                        removed.Add(ordered[removeIndex]);
                    }
                    if (runEnd < ordered.Count - 1)
                    {
                        promotedFrames.Add(ordered[runEnd]);
                    }
                    index = runEnd;
                }
                else
                {
                    index++;
                }
            }

            return ordered.Where(frame => !removed.Contains(frame)).ToList();
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
            return TryGetFootTransitionTint(subject, frame, out Color tint) ? tint : Color.white;
        }

        private static IReadOnlyList<int> FootTransitionFrames(SubjectPictureData subject)
        {
            return (subject.Subject.Record.Analysis?["foot_contacts"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(item => Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, Math.Max(0, subject.Pelvis.Length - 1)))
                .Distinct()
                .OrderBy(frame => frame)
                .ToArray();
        }

        private static bool TryGetFootTransitionTint(SubjectPictureData subject, int frame, out Color tint)
        {
            bool left = false;
            bool right = false;
            foreach (JObject item in (subject.Subject.Record.Analysis?["foot_contacts"] as JArray ?? new JArray()).OfType<JObject>())
            {
                int eventFrame = Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, Math.Max(0, subject.Pelvis.Length - 1));
                if (eventFrame != frame) continue;
                string foot = item.Value<string>("foot") ?? string.Empty;
                left |= foot.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0;
                right |= foot.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (left)
            {
                tint = new Color(.2f, .45f, 1f);
                return true;
            }
            tint = right ? new Color(1f, .2f, .2f) : Color.white;
            return right;
        }

        private static bool IsKeyframe(SubjectPictureData subject, int frame)
        {
            return subject.KeyFrameSet.Contains(frame);
        }

        private static Color ResolveGhostPoseTint(SubjectPictureData subject, int frame)
        {
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            if (frame == 0) return TestStartFrameTint;
            if (frame == lastFrame) return TestEndFrameTint;
            if (IsKeyframe(subject, frame)) return Color.yellow;
            return TryGetFootTransitionTint(subject, frame, out Color footTint) ? footTint : Color.white;
        }

        private static Color ResolveTestPoseTint(PictureTile tile, int frame, out bool keyframe, out bool footTransition)
        {
            SubjectPictureData subject = tile.Subject;
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            // The two test panels intentionally color only their own event
            // type: keyframe samples never inherit foot colors, and vice versa.
            keyframe = tile.Presentation == "test_keyframes" && tile.PrimaryFrames.Contains(frame);
            footTransition = tile.Presentation == "test_foot_transitions" &&
                tile.PrimaryFrames.Contains(frame) && TryGetFootTransitionTint(subject, frame, out _);
            if (frame == 0) return TestStartFrameTint;
            if (frame == lastFrame) return TestEndFrameTint;
            if (keyframe) return Color.yellow;
            return footTransition && TryGetFootTransitionTint(subject, frame, out Color footTint)
                ? footTint
                : Color.white;
        }

        private static Color ResolveSingleTestPoseTint(PictureTile tile, int frame)
        {
            int lastFrame = Math.Max(0, tile.Subject.Pelvis.Length - 1);
            if (frame == 0) return TestStartFrameTint;
            if (frame == lastFrame) return TestEndFrameTint;
            if (string.Equals(tile.PoseKind, "keyframe", StringComparison.Ordinal)) return Color.yellow;
            if (string.Equals(tile.PoseKind, "foot_transition", StringComparison.Ordinal) &&
                TryGetFootTransitionTint(tile.Subject, frame, out Color footTint))
            {
                return footTint;
            }
            return Color.white;
        }

        private static void CreatePictureEnvironment(List<GameObject> objects, Bounds bounds)
        {
            const int captureLayer = 31;
            float size = Mathf.Ceil(Mathf.Max(bounds.size.x, bounds.size.z) * .5f) * 2f;
            GameObject floor = MoveToAnalysisPreviewScene(GameObject.CreatePrimitive(PrimitiveType.Plane));
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

        private static Bounds IncludeGroundInBounds(Bounds bounds)
        {
            bounds.Encapsulate(new Vector3(bounds.min.x, 0f, bounds.min.z));
            bounds.Encapsulate(new Vector3(bounds.max.x, 0f, bounds.max.z));
            bounds.Expand(new Vector3(.5f, .25f, .5f));
            return bounds;
        }

        private static void CreateTestPictureEnvironment(List<GameObject> objects, Bounds bounds)
        {
            const float tileSize = 16f;
            Vector3 center = bounds.center;
            int minX = Mathf.FloorToInt(bounds.min.x / tileSize) - 1;
            int maxX = Mathf.FloorToInt(bounds.max.x / tileSize) + 1;
            int minZ = Mathf.FloorToInt(bounds.min.z / tileSize) - 1;
            int maxZ = Mathf.FloorToInt(bounds.max.z / tileSize) + 1;
            Texture2D gridTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Packages/com.unity.kimodo_unity_motion_tools/Editor/Model/UVCheckGrid.png")
                ?? AssetDatabase.LoadAssetAtPath<Texture2D>("Editor/Model/UVCheckGrid.png");

            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    GameObject floor = CreateTestGridFloor(
                        new Vector3((x + .5f) * tileSize, 0f, (z + .5f) * tileSize), tileSize, gridTexture);
                    objects.Add(floor);
                }
            }
            CreateEvidenceLights(objects, center);
        }

        private static GameObject CreateTestGridFloor(Vector3 center, float size, Texture2D gridTexture)
        {
            const int subdivisions = 16;
            const int captureLayer = 31;
            var mesh = new Mesh { name = "Kimodo Test 16x16 UV Grid", hideFlags = HideFlags.HideAndDontSave };
            int vertexSide = subdivisions + 1;
            var vertices = new Vector3[vertexSide * vertexSide];
            var uv = new Vector2[vertices.Length];
            var triangles = new int[subdivisions * subdivisions * 6];
            for (int z = 0; z < vertexSide; z++)
            {
                for (int x = 0; x < vertexSide; x++)
                {
                    int index = z * vertexSide + x;
                    vertices[index] = new Vector3(
                        (x / (float)subdivisions - .5f) * size,
                        0f,
                        (z / (float)subdivisions - .5f) * size);
                    uv[index] = new Vector2(x / (float)subdivisions, z / (float)subdivisions);
                }
            }
            int triangle = 0;
            for (int z = 0; z < subdivisions; z++)
            {
                for (int x = 0; x < subdivisions; x++)
                {
                    int a = z * vertexSide + x;
                    int b = a + 1;
                    int c = a + vertexSide;
                    int d = c + 1;
                    triangles[triangle++] = a;
                    triangles[triangle++] = c;
                    triangles[triangle++] = b;
                    triangles[triangle++] = b;
                    triangles[triangle++] = c;
                    triangles[triangle++] = d;
                }
            }
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();

            GameObject floor = MoveToAnalysisPreviewScene(
                new GameObject("Kimodo Test UV Grid") { hideFlags = HideFlags.HideAndDontSave });
            floor.transform.position = center;
            floor.layer = captureLayer;
            MeshFilter filter = floor.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = floor.AddComponent<MeshRenderer>();
            Material material = MakeMaterial(Color.white);
            if (gridTexture != null)
            {
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", gridTexture);
                if (material.HasProperty("_BaseColorMap")) material.SetTexture("_BaseColorMap", gridTexture);
                if (material.HasProperty("_UnlitColorMap")) material.SetTexture("_UnlitColorMap", gridTexture);
                if (material.HasProperty("_MainTex")) material.mainTexture = gridTexture;
            }
            renderer.sharedMaterial = material;
            return floor;
        }

        private static Camera CreateAnalysisPictureCamera(Bounds bounds, Vector3 direction, bool orthographic)
        {
            Camera camera = CreateAnalysisPictureCamera("Kimodo Analysis Picture Camera");
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

        private static Camera CreateTestAnalysisPictureCamera(
            Bounds bounds,
            SubjectPictureData subject,
            Vector3 direction,
            float aspect)
        {
            Camera camera = CreateAnalysisPictureCamera("Kimodo Test Analysis Picture Camera");
            camera.cullingMask = 1 << 31;
            camera.orthographic = true;
            camera.aspect = Mathf.Max(.1f, aspect);
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            Vector3 normalizedDirection = direction.sqrMagnitude > .0001f ? direction.normalized : new Vector3(1f, .75f, -1f).normalized;
            CalculateTestViewExtents(
                subject,
                normalizedDirection,
                out Vector3 viewCenter,
                out float maxHorizontal,
                out float maxVertical,
                out float maxDepth);
            float distance = Mathf.Max(8f, bounds.extents.magnitude * 4f);
            camera.transform.position = viewCenter + normalizedDirection * distance;
            Vector3 up = Mathf.Abs(Vector3.Dot(normalizedDirection, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            camera.transform.LookAt(viewCenter, up);

            float horizontalHalf = maxHorizontal * TestCameraFitScale + TestCameraMarginMeters;
            float verticalHalf = maxVertical * TestCameraFitScale + TestCameraMarginMeters;
            camera.orthographicSize = Mathf.Max(
                .5f,
                verticalHalf,
                horizontalHalf / camera.aspect);
            camera.farClipPlane = Mathf.Max(100f, distance + maxDepth + 10f);
            return camera;
        }

        private static Camera CreateTestAnalysisPictureCamera(
            Bounds bounds,
            Vector3 direction,
            float aspect)
        {
            Camera camera = CreateAnalysisPictureCamera("Kimodo Test Pose Camera");
            camera.cullingMask = 1 << 31;
            camera.orthographic = true;
            camera.aspect = Mathf.Max(.1f, aspect);
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            Vector3 normalizedDirection = direction.sqrMagnitude > .0001f ? direction.normalized : new Vector3(1f, .75f, -1f).normalized;
            CalculateTestViewExtents(
                bounds,
                normalizedDirection,
                out Vector3 viewCenter,
                out float maxHorizontal,
                out float maxVertical,
                out float maxDepth);
            float distance = Mathf.Max(8f, bounds.extents.magnitude * 4f);
            camera.transform.position = viewCenter + normalizedDirection * distance;
            Vector3 up = Mathf.Abs(Vector3.Dot(normalizedDirection, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            camera.transform.LookAt(viewCenter, up);

            float horizontalHalf = maxHorizontal * TestCameraFitScale + TestCameraMarginMeters;
            float verticalHalf = maxVertical * TestCameraFitScale + TestCameraMarginMeters;
            camera.orthographicSize = Mathf.Max(
                .5f,
                verticalHalf,
                horizontalHalf / camera.aspect);
            camera.farClipPlane = Mathf.Max(100f, distance + maxDepth + 10f);
            return camera;
        }

        private static Camera CreateTestAnalysisPictureCamera(
            IEnumerable<Vector3> points,
            Vector3 direction,
            float aspect)
        {
            Camera camera = CreateAnalysisPictureCamera("Kimodo Test Pose Camera");
            camera.cullingMask = 1 << 31;
            camera.orthographic = true;
            camera.aspect = Mathf.Max(.0001f, aspect);
            camera.nearClipPlane = .01f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            Vector3 normalizedDirection = direction.sqrMagnitude > .0001f ? direction.normalized : new Vector3(1f, .75f, -1f).normalized;
            CalculateTestViewExtents(points, normalizedDirection, out Vector3 viewCenter, out _, out float vertical, out float maxDepth);
            float distance = Mathf.Max(8f, maxDepth + 8f);
            Vector3 up = Mathf.Abs(Vector3.Dot(normalizedDirection, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            camera.transform.position = viewCenter + normalizedDirection * distance;
            camera.transform.LookAt(viewCenter, up);
            camera.orthographicSize = Mathf.Max(.0001f, vertical);
            camera.farClipPlane = Mathf.Max(100f, distance + maxDepth + 10f);
            return camera;
        }

        private static Camera CreateAnalysisPictureCamera(string name)
        {
            GameObject cameraObject = MoveToAnalysisPreviewScene(
                new GameObject(name) { hideFlags = HideFlags.HideAndDontSave });
            Camera camera = cameraObject.AddComponent<Camera>();
            if (analysisPreviewScene.IsValid()) camera.scene = analysisPreviewScene;
            ConfigureRenderPipelineAnalysisCamera(camera);
            return camera;
        }

        private static void ConfigureRenderPipelineAnalysisCamera(Camera camera)
        {
            if (camera == null || GraphicsSettings.currentRenderPipeline == null) return;

            string pipelineName = GraphicsSettings.currentRenderPipeline.GetType().FullName ?? string.Empty;
            string cameraDataTypeName = pipelineName.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData"
                    : pipelineName.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData"
                        : null;
            if (cameraDataTypeName == null) return;

            Type additionalCameraDataType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(cameraDataTypeName, false))
                .FirstOrDefault(type => type != null);
            if (additionalCameraDataType == null) return;

            Component additionalCameraData = camera.GetComponent(additionalCameraDataType) ??
                camera.gameObject.AddComponent(additionalCameraDataType);
            var volumeLayerMask = additionalCameraDataType.GetField("volumeLayerMask");
            if (volumeLayerMask != null)
            {
                volumeLayerMask.SetValue(additionalCameraData, (LayerMask)0);
                return;
            }
            additionalCameraDataType.GetProperty("volumeLayerMask")?.SetValue(additionalCameraData, (LayerMask)0);
        }

        private static TrajectoryScale BuildTrajectoryScale(IReadOnlyList<SubjectPictureData> subjects, bool includeEndEffectors = false)
        {
            var speeds = new List<float>();
            var accelerations = new List<float>();
            foreach (SubjectPictureData subject in subjects)
            {
                CollectTrajectoryMeasurements(subject.Pelvis, speeds, accelerations);
                if (!includeEndEffectors) continue;
                CollectTrajectoryMeasurements(subject.LeftHand, speeds, accelerations);
                CollectTrajectoryMeasurements(subject.RightHand, speeds, accelerations);
                CollectTrajectoryMeasurements(subject.LeftFoot, speeds, accelerations);
                CollectTrajectoryMeasurements(subject.RightFoot, speeds, accelerations);
            }
            return new TrajectoryScale(Percentile(speeds, .05f), Percentile(speeds, .95f), Percentile(accelerations, .05f), Percentile(accelerations, .95f));
        }

        private static void CollectTrajectoryMeasurements(Vector3[] points, List<float> speeds, List<float> accelerations)
        {
            float previousSpeed = 0f;
            for (int index = 1; index < points.Length; index++)
            {
                float speed = (points[index] - points[index - 1]).magnitude * (float)SessionFrameRate;
                speeds.Add(speed);
                accelerations.Add(Mathf.Abs(speed - previousSpeed) * (float)SessionFrameRate);
                previousSpeed = speed;
            }
        }

        private static float Percentile(List<float> values, float percent)
        {
            if (values == null || values.Count == 0) return 0f;
            values.Sort();
            return values[Mathf.Clamp(Mathf.RoundToInt((values.Count - 1) * percent), 0, values.Count - 1)];
        }

        private static void CreateTestBodyTrajectories(
            List<GameObject> objects,
            SubjectPictureData subject)
        {
            CreateTestTrajectory(objects, subject.Pelvis, new Color(.1f, .8f, .2f, .9f), .09f);
            CreateTestTrajectory(objects, subject.LeftHand, new Color(.2f, .45f, 1f, .65f), .035f);
            CreateTestTrajectory(objects, subject.LeftFoot, new Color(.2f, .45f, 1f, .8f), .05f);
            CreateTestTrajectory(objects, subject.RightHand, new Color(1f, .2f, .2f, .65f), .035f);
            CreateTestTrajectory(objects, subject.RightFoot, new Color(1f, .2f, .2f, .8f), .05f);
        }

        private static void CreateTestTrajectory(
            List<GameObject> objects,
            Vector3[] points,
            Color color,
            float lineWidth)
        {
            if (points == null || points.Length < 2) return;
            GameObject lineObject = MoveToAnalysisPreviewScene(
                new GameObject("Kimodo Test Body Trajectory") { hideFlags = HideFlags.HideAndDontSave });
            SetLayerRecursively(lineObject, 31);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = points.Length;
            line.SetPositions(points.Select(point => point + Vector3.up * .02f).ToArray());
            line.startWidth = line.endWidth = lineWidth;
            line.useWorldSpace = true;
            line.sharedMaterial = MakeUnlitMaterial(color);
            line.startColor = line.endColor = color;
            objects.Add(lineObject);
        }

        private static void Fill(Texture2D texture, Color color)
        {
            var pixels = new Color[texture.width * texture.height];
            for (int index = 0; index < pixels.Length; index++) pixels[index] = color;
            texture.SetPixels(pixels);
        }

        private static bool IsHighFootPose(PictureTile tile)
        {
            return tile != null && tile.Presentation == "test_pose" &&
                string.Equals(tile.PoseKind, "foot_transition", StringComparison.Ordinal);
        }

        private static void DrawPictureGrid(
            Texture2D texture,
            IReadOnlyList<RectInt> imageRects,
            int panels,
            int panelHeight,
            int rows)
        {
            foreach (RectInt rect in imageRects)
            {
                if (rect.xMax < texture.width)
                {
                    FillRect(texture, rect.xMax - 2, rect.y, 4, rect.height, Color.white);
                }
            }
            for (int panel = 0; panel < panels; panel++)
            {
                int origin = panel * panelHeight;
                for (int row = 1; row < rows; row++)
                {
                    FillRect(texture, 0, origin + row * (panelHeight / rows) - 2, texture.width, 4, Color.white);
                }
            }
            for (int panel = 1; panel < panels; panel++)
            {
                FillRect(texture, 0, panel * panelHeight - 2, texture.width, 4, Color.white);
            }
        }

        private static void DrawTileNumber(Texture2D texture, string value)
        {
            string text = value ?? string.Empty;
            int size = texture.width >= 256 ? 4 : 2;
            int width = 0;
            foreach (char character in text)
            {
                width += character == '.' ? size * 2 : size * 5;
            }
            width = Math.Max(1, width - size);
            int x = texture.width - width - size * 2;
            int y = texture.height - size * 8;
            foreach (char digit in text)
            {
                if (digit == '.')
                {
                    FillRect(texture, x, y, size, size, Color.white);
                    x += size * 2;
                }
                else
                {
                    DrawSevenSegmentDigit(texture, x, y, digit, size, Color.white);
                    x += size * 5;
                }
            }
            texture.Apply(false, false);
        }

        private static void DrawFrameNumber(Texture2D texture, int frame)
        {
            string text = Math.Max(0, frame).ToString(CultureInfo.InvariantCulture);
            int size = texture.width >= 256 ? 4 : 2;
            int width = 0;
            foreach (char character in text) width += size * 5;
            width = Math.Max(1, width - size);
            int x = size * 2;
            int y = size * 2;
            FillRect(texture, 0, 0, width + size * 4, size * 8, new Color(0f, 0f, 0f, .65f));
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
                Poses = poses != null ? (JArray)poses.DeepClone() : new JArray(),
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

        private static string EvidenceFolder(TimelineSessionRecord session) =>
            Path.Combine(GetSessionGeneratedFolder(session), "Pictures");

        private static GameObject CreatePosePreview(
            TimelineCharacterRecord character,
            KimodoMarkerSampleResult sample,
            bool root2DOnly)
        {
            const int captureLayer = 31;
            GameObject preview = MoveToAnalysisPreviewScene(UnityEngine.Object.Instantiate(character.Root));
            preview.name = "Kimodo Pose Preview";
            preview.hideFlags = HideFlags.HideAndDontSave;
            foreach (Transform transform in preview.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = captureLayer;
            }
            Animator animator = preview.GetComponentInChildren<Animator>(true)
                ?? throw new InvalidOperationException($"Character '{character.Name}' preview has no Animator.");
            animator.runtimeAnimatorController = null;
            bool hasPose = sample?.sampleData != null && sample.sampleData.IsValid;
            bool hasRoot2D = TryGetRoot2DWorld(sample, out Vector3 position, out Quaternion rotation);
            if (!root2DOnly && hasPose)
            {
                HumanPose pose = KimodoMuscleSampleHumanPoseAdapter.ToHumanPose(sample.sampleData);
                using (var handler = new HumanPoseHandler(character.Avatar, animator.transform))
                {
                    handler.SetHumanPose(ref pose);
                }
                if (hasRoot2D)
                {
                    animator.transform.SetPositionAndRotation(position, rotation);
                }
            }
            else
            {
                animator.transform.SetPositionAndRotation(position, rotation);
            }
            return preview;
        }

        private static bool TryGetRoot2DWorld(
            KimodoMarkerSampleResult sample,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (sample?.validMask?.rootPosition != true || sample.root2DOverride == null)
            {
                return false;
            }

            position = sample.root2DOverride.t;
            rotation = sample.root2DOverride.q.normalized;
            return true;
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
                GameObject lightObject = MoveToAnalysisPreviewScene(
                    new GameObject("Kimodo Evidence Light") { hideFlags = HideFlags.HideAndDontSave });
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = setup.intensity;
                lightObject.transform.position = center + setup.position;
                lightObject.transform.LookAt(center);
                objects.Add(lightObject);
            }
        }

        private static void CreateWorldLine(List<GameObject> objects, Vector3 from, Vector3 to, float width, Color color, bool unlit = false)
        {
            GameObject lineObject = MoveToAnalysisPreviewScene(
                new GameObject("Kimodo Evidence Line") { hideFlags = HideFlags.HideAndDontSave });
            SetLayerRecursively(lineObject, 31);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPositions(new[] { from, to });
            line.startWidth = line.endWidth = width;
            line.useWorldSpace = true;
            line.sharedMaterial = unlit ? MakeUnlitMaterial(color) : MakeMaterial(color);
            line.startColor = line.endColor = color;
            objects.Add(lineObject);
        }

        private static Material MakeMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("HDRP/Unlit") ??
                Shader.Find("Sprites/Default") ??
                Shader.Find("Standard");
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", color);
            return material;
        }

        private static Material MakeUnlitMaterial(Color color)
        {
            return MakeMaterial(color);
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = layer;
        }

        private static GameObject MoveToAnalysisPreviewScene(GameObject gameObject)
        {
            if (gameObject != null && analysisPreviewScene.IsValid())
            {
                SceneManager.MoveGameObjectToScene(gameObject, analysisPreviewScene);
            }
            return gameObject;
        }

        private static Texture2D RenderCamera(Camera camera, int size, Color background)
        {
            return RenderCamera(camera, size, size, background);
        }

        private static Texture2D RenderCamera(Camera camera, int width, int height, Color background)
        {
            RenderTexture renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            try
            {
                camera.backgroundColor = background;
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
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

        private static Texture2D ReadRenderTexture(RenderTexture source, int width, int height)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply(false, false);
                return image;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private static Texture2D ResizeTexture(Texture2D source, int width, int height)
        {
            RenderTexture target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, target);
                RenderTexture.active = target;
                var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply(false, false);
                return image;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
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

        private static void SetEvidenceVisualsEnabled(
            IReadOnlyList<GameObject> objects,
            bool enabled,
            bool preserveLineRenderers = false)
        {
            foreach (GameObject item in objects)
            {
                if (item == null) continue;
                foreach (Renderer renderer in item.GetComponentsInChildren<Renderer>(true))
                {
                    if (preserveLineRenderers && renderer is LineRenderer) continue;
                    renderer.enabled = enabled;
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
                ["contract"] = "animation_analysis_picture_v3",
                ["character_ref"] = character?.CharacterRef ?? string.Empty,
                ["rig_type"] = IsHumanoidCharacter(character) ? "humanoid" : "mesh",
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
                KimodoMarkerSampleResult[] samples,
                Vector3[] pelvis,
                Vector3[] leftHand,
                Vector3[] rightHand,
                Vector3[] leftFoot,
                Vector3[] rightFoot,
                Vector3[] leftElbow,
                Vector3[] rightElbow,
                Vector3[] leftKnee,
                Vector3[] rightKnee,
                Vector3[] head,
                bool[] leftContacts,
                bool[] rightContacts,
                Bounds firstBounds,
                Bounds lastBounds,
                Bounds bounds,
                Bounds testBounds)
            {
                Subject = subject;
                Samples = samples;
                Pelvis = pelvis;
                LeftHand = leftHand;
                RightHand = rightHand;
                LeftFoot = leftFoot;
                RightFoot = rightFoot;
                LeftElbow = leftElbow;
                RightElbow = rightElbow;
                LeftKnee = leftKnee;
                RightKnee = rightKnee;
                Head = head;
                LeftContacts = leftContacts;
                RightContacts = rightContacts;
                FirstBounds = firstBounds;
                LastBounds = lastBounds;
                Bounds = bounds;
                TestBounds = testBounds;
                KeyFrameSet = new HashSet<int>((subject.Record.Analysis?["keyframes"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Select(item => Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, Math.Max(0, pelvis.Length - 1))));
            }
            public AnalysisSubject Subject { get; }
            public KimodoMarkerSampleResult[] Samples { get; }
            public Vector3[] Pelvis { get; }
            public Vector3[] LeftHand { get; }
            public Vector3[] RightHand { get; }
            public Vector3[] LeftFoot { get; }
            public Vector3[] RightFoot { get; }
            public Vector3[] LeftElbow { get; }
            public Vector3[] RightElbow { get; }
            public Vector3[] LeftKnee { get; }
            public Vector3[] RightKnee { get; }
            public Vector3[] Head { get; }
            public bool[] LeftContacts { get; }
            public bool[] RightContacts { get; }
            public Bounds FirstBounds { get; }
            public Bounds LastBounds { get; }
            public Bounds Bounds { get; }
            public Bounds TestBounds { get; }
            public HashSet<int> KeyFrameSet { get; }

            public KimodoMarkerSampleResult GetSample(int localFrame)
            {
                if (Samples == null || Samples.Length == 0)
                {
                    throw new InvalidOperationException($"Character '{Subject.Character.Name}' has no sampled poses.");
                }
                return Samples[Mathf.Clamp(localFrame, 0, Samples.Length - 1)];
            }
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
            public string PoseKind { get; private set; }
            public List<int> TrajectoryFrames { get; private set; } = new List<int>();
            public HashSet<int> PrimaryFrames { get; private set; } = new HashSet<int>();
            public HashSet<int> StationaryBoostFrames { get; private set; } = new HashSet<int>();
            public bool ShowTestTrajectories { get; private set; }

            public static PictureTile Ghost(SubjectPictureData subject, string view, Vector3 direction, bool orthographic)
            {
                return new PictureTile(subject, "ghost", new JObject { ["presentation"] = "ghost", ["view"] = view })
                {
                    Direction = direction,
                    Orthographic = orthographic
                };
            }

            public static PictureTile TestFootTransitions(SubjectPictureData subject, Vector3 direction)
            {
                return TestFrameSet(subject, "test_foot_transitions", "foot_transitions", FootTransitionFrames(subject), direction, false);
            }

            public static PictureTile TestKeyframes(SubjectPictureData subject, Vector3 direction)
            {
                return TestFrameSet(subject, "test_keyframes", "keyframes", SelectKeyFrames(subject, AnalysisKeyframeCount), direction, true);
            }

            public static PictureTile TestRoot2D(SubjectPictureData subject, Vector3 direction)
            {
                return new PictureTile(subject, "test_root2d", new JObject
                {
                    ["presentation"] = "root2d_pelvis_projection",
                    ["keyframes"] = new JArray(SelectKeyFrames(subject, AnalysisKeyframeCount).OrderBy(frame => frame)),
                    ["pelvis_only"] = true,
                    ["heading_arrows"] = true
                })
                {
                    Direction = direction,
                    Orthographic = true
                };
            }

            public static PictureTile TestPose(
                SubjectPictureData subject,
                int frame,
                string poseKind,
                Vector3 direction)
            {
                int clampedFrame = Mathf.Clamp(frame, 0, Math.Max(0, subject.Pelvis.Length - 1));
                return new PictureTile(subject, "test_pose", new JObject
                {
                    ["presentation"] = "test_pose",
                    ["frame"] = clampedFrame,
                    ["pose_kind"] = poseKind
                })
                {
                    Direction = direction,
                    Orthographic = true,
                    Frame = clampedFrame,
                    PoseKind = poseKind
                };
            }

            public static PictureTile MeshPose(
                SubjectPictureData subject,
                int frame,
                string poseKind)
            {
                int clampedFrame = Mathf.Clamp(frame, 0, Math.Max(0, subject.Pelvis.Length - 1));
                return new PictureTile(subject, "mesh_pose", new JObject
                {
                    ["presentation"] = "mesh_pose",
                    ["frame"] = clampedFrame,
                    ["pose_kind"] = poseKind
                })
                {
                    Direction = new Vector3(1f, .75f, -1f),
                    Orthographic = true,
                    Frame = clampedFrame,
                    PoseKind = poseKind
                };
            }

            private static PictureTile TestFrameSet(
                SubjectPictureData subject,
                string presentation,
                string label,
                IEnumerable<int> primaryFrames,
                Vector3 direction,
                bool showTrajectories)
            {
                int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
                var primary = new HashSet<int>((primaryFrames ?? Enumerable.Empty<int>())
                    .Select(frame => Mathf.Clamp(frame, 0, lastFrame)));
                List<int> frames = BuildTestSampleFrames(
                    subject,
                    primary,
                    presentation == "test_foot_transitions",
                    out HashSet<int> stationaryBoostFrames);
                primary.IntersectWith(frames);
                return new PictureTile(subject, presentation, new JObject
                {
                    ["presentation"] = label,
                    ["primary_frames"] = new JArray(primary.OrderBy(frame => frame)),
                    ["frames"] = new JArray(frames),
                    ["test"] = true
                })
                {
                    Direction = direction,
                    Orthographic = true,
                    TrajectoryFrames = frames,
                    PrimaryFrames = primary,
                    StationaryBoostFrames = stationaryBoostFrames,
                    ShowTestTrajectories = showTrajectories
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

        }

        private static void TintPreview(GameObject preview, Color tint, List<Material> transientMaterials)
        {
            Color flatTint = new Color(tint.r, tint.g, tint.b, 1f);
            Shader fallbackShader = null;
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
                    Material material = sourceMaterials[index];
                    if (IsUsablePoseMaterial(material))
                    {
                        Material replacement = UnityEngine.Object.Instantiate(material);
                        replacement.hideFlags = HideFlags.HideAndDontSave;
                        SetMaterialTint(replacement, flatTint);
                        replacements[index] = replacement;
                        transientMaterials?.Add(replacement);
                        continue;
                    }

                    fallbackShader ??= FindPoseFallbackShader();
                    if (fallbackShader == null)
                    {
                        throw new InvalidOperationException("No compatible pose fallback shader is available.");
                    }

                    Material fallbackMaterial = new Material(fallbackShader) { hideFlags = HideFlags.HideAndDontSave };
                    SetMaterialTint(fallbackMaterial, flatTint);
                    replacements[index] = fallbackMaterial;
                    transientMaterials?.Add(fallbackMaterial);
                }
                renderer.sharedMaterials = replacements;
            }
        }

        private static bool IsUsablePoseMaterial(Material material)
        {
            if (material == null) return false;
            Shader shader = material.shader;
            if (shader == null || string.Equals(shader.name, "Hidden/InternalErrorShader", StringComparison.Ordinal))
            {
                return false;
            }
            if (!shader.isSupported) return false;
            return !ShaderUtil.ShaderHasError(shader);
        }

        private static Shader FindPoseFallbackShader()
        {
            string pipelineName = GraphicsSettings.currentRenderPipeline == null
                ? string.Empty
                : GraphicsSettings.currentRenderPipeline.GetType().FullName ?? string.Empty;
            bool isHdrp = pipelineName.IndexOf("HDRenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("HDRP", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isUrp = pipelineName.IndexOf("UniversalRenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("Universal RP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("URP", StringComparison.OrdinalIgnoreCase) >= 0;

            string[] preferred = isHdrp
                ? new[] { "HDRP/Lit" }
                : isUrp
                    ? new[] { "Universal Render Pipeline/Lit" }
                    : new[] { "Standard" };
            foreach (string shaderName in preferred)
            {
                Shader shader = Shader.Find(shaderName);
                if (IsUsablePoseShader(shader)) return shader;
            }

            // Unlit is retained only as the final compatibility path for a
            // missing pipeline Lit/Standard shader, never for a valid source.
            string[] lastResort = isHdrp
                ? new[] { "HDRP/Unlit", "Sprites/Default" }
                : isUrp
                    ? new[] { "Universal Render Pipeline/Unlit", "Sprites/Default" }
                    : new[] { "Sprites/Default", "Unlit/Color" };
            foreach (string shaderName in lastResort)
            {
                Shader shader = Shader.Find(shaderName);
                if (IsUsablePoseShader(shader)) return shader;
            }
            return null;
        }

        private static bool IsUsablePoseShader(Shader shader)
        {
            if (shader == null || string.Equals(shader.name, "Hidden/InternalErrorShader", StringComparison.Ordinal))
            {
                return false;
            }
            return shader.isSupported && !ShaderUtil.ShaderHasError(shader);
        }

        private static void SetMaterialTint(Material material, Color tint)
        {
            if (material == null) return;
            // Unity has no universal `mainColor` field. These are shader
            // property names: URP/HDRP Lit uses _BaseColor, HDRP Unlit uses
            // _UnlitColor, and Built-in uses _Color.
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", tint);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", tint);
            if (material.HasProperty("_Color")) material.SetColor("_Color", tint);
        }

        private static void SetPreviewMaterialRenderQueue(GameObject preview, int renderQueue)
        {
            foreach (Renderer renderer in preview.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.materials)
                {
                    if (material != null) material.renderQueue = renderQueue;
                }
            }
        }

        private sealed class TestVirtualPose
        {
            public TestVirtualPose(
                GameObject preview,
                IReadOnlyList<Material> transientMaterials,
                float alpha,
                bool usesGhostMaterial)
            {
                Preview = preview;
                TransientMaterials = transientMaterials;
                Alpha = alpha;
                UsesGhostMaterial = usesGhostMaterial;
            }

            public GameObject Preview { get; }
            public IReadOnlyList<Material> TransientMaterials { get; }
            public float Alpha { get; }
            public bool UsesGhostMaterial { get; }

            public void Dispose()
            {
                if (TransientMaterials != null)
                {
                    foreach (Material material in TransientMaterials)
                    {
                        if (material != null) UnityEngine.Object.DestroyImmediate(material);
                    }
                }
                if (Preview != null) UnityEngine.Object.DestroyImmediate(Preview);
            }
        }

        private sealed class TestPosePlan : IDisposable
        {
            private readonly GameObject source;
            private readonly Dictionary<int, TestPoseSnapshot> snapshots;

            public TestPosePlan(
                GameObject source,
                Dictionary<int, TestPoseSnapshot> snapshots)
            {
                this.source = source;
                this.snapshots = snapshots;
            }

            public TestPoseSnapshot Get(int frame)
            {
                if (snapshots.TryGetValue(frame, out TestPoseSnapshot snapshot)) return snapshot;
                return snapshots.Values.First();
            }

            public void Dispose()
            {
                if (source != null) UnityEngine.Object.DestroyImmediate(source);
            }
        }

        private sealed class TestPoseSnapshot
        {
            private readonly TestTransformSnapshot[] transforms;

            private TestPoseSnapshot(
                GameObject sourcePrefab,
                Vector3 rootPosition,
                Quaternion rootRotation,
                Vector3 rootScale,
                TestTransformSnapshot[] transforms)
            {
                SourcePrefab = sourcePrefab;
                RootPosition = rootPosition;
                RootRotation = rootRotation;
                RootScale = rootScale;
                this.transforms = transforms;
            }

            public GameObject SourcePrefab { get; }
            public Vector3 RootPosition { get; }
            public Quaternion RootRotation { get; }
            public Vector3 RootScale { get; }

            public static TestPoseSnapshot Capture(GameObject source)
            {
                Transform root = source.transform;
                Transform[] all = source.GetComponentsInChildren<Transform>(true);
                var values = new TestTransformSnapshot[all.Length];
                for (int index = 0; index < all.Length; index++)
                {
                    Transform transform = all[index];
                    values[index] = new TestTransformSnapshot(
                        GetTransformPath(root, transform),
                        transform.localPosition,
                        transform.localRotation,
                        transform.localScale);
                }
                return new TestPoseSnapshot(
                    source,
                    root.position,
                    root.rotation,
                    root.localScale,
                    values);
            }

            public void Apply(GameObject target)
            {
                target.transform.SetPositionAndRotation(RootPosition, RootRotation);
                target.transform.localScale = RootScale;
                foreach (TestTransformSnapshot value in transforms)
                {
                    Transform transform = FindTransform(target.transform, value.Path);
                    if (transform == null) continue;
                    transform.localPosition = value.LocalPosition;
                    transform.localRotation = value.LocalRotation;
                    transform.localScale = value.LocalScale;
                }
            }

            private static string GetTransformPath(Transform root, Transform transform)
            {
                if (transform == root) return string.Empty;
                var names = new List<string>();
                Transform current = transform;
                while (current != null && current != root)
                {
                    names.Add(current.name);
                    current = current.parent;
                }
                names.Reverse();
                return string.Join("/", names);
            }

            private static Transform FindTransform(Transform root, string path)
            {
                return string.IsNullOrEmpty(path) ? root : root.Find(path);
            }
        }

        private readonly struct TestTransformSnapshot
        {
            public TestTransformSnapshot(
                string path,
                Vector3 localPosition,
                Quaternion localRotation,
                Vector3 localScale)
            {
                Path = path;
                LocalPosition = localPosition;
                LocalRotation = localRotation;
                LocalScale = localScale;
            }

            public string Path { get; }
            public Vector3 LocalPosition { get; }
            public Quaternion LocalRotation { get; }
            public Vector3 LocalScale { get; }
        }

        private readonly struct PictureLayout
        {
            private PictureLayout(int tileColumns, int tileRows, int tileSize)
            {
                TileColumns = Math.Max(1, tileColumns);
                TileRows = Math.Max(1, tileRows);
                TileSize = Math.Max(1, tileSize);
            }
            public int TileColumns { get; }
            public int TileRows { get; }
            public int TileSize { get; }

            public static PictureLayout ForLevel(int tileColumns, bool splitHighRows, int requestedTileSize)
            {
                // Keep one composite within Unity's texture limit while retaining
                // the largest readable tile size for shorter animations.
                int maxTextureSize = Mathf.Max(1, SystemInfo.maxTextureSize);
                int maxTileSize = Mathf.Max(1, maxTextureSize / Math.Max(1, tileColumns));
                int tileSize = Mathf.Clamp(requestedTileSize, 1, maxTileSize);
                return new PictureLayout(tileColumns, splitHighRows ? 2 : 1, tileSize);
            }
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
