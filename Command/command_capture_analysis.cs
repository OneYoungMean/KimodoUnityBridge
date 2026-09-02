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

namespace KimodoUnityBridge.Command

{
    internal static partial class command_context
    {
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

    }
}
