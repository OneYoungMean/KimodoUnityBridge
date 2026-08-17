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
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace CharacterAnimationCli.Unity.Command
{
    internal static partial class command_context
    {
        private static readonly Dictionary<string, AnalysisCacheRecord> AnalysisCache =
            new Dictionary<string, AnalysisCacheRecord>(StringComparer.OrdinalIgnoreCase);

        private const string AnalysisPictureRenderVersion = "5";
        private const string TestAnalysisPictureRenderVersion = "15-test";
        private const float TestCameraMarginMeters = .5f;
        private const float TestCameraFitScale = .5f;
        private const float TestGhostAlphaMin = .1f;
        private const float TestGhostAlphaMax = .5f;

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
            TrajectoryScale trajectoryScale = BuildTrajectoryScale(data, level == "-test");
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
                int x = (localIndex % layout.TileColumns) * layout.TileSize;
                int y = (data.Count - panel - 1) * layout.Height +
                    (layout.TileRows - 1 - localIndex / layout.TileColumns) * layout.TileSize;
                JObject description = (JObject)tile.Description.DeepClone();
                description["subject"] = tile.Subject.Subject.Role;
                descriptions.Add(new JObject
                {
                    ["id"] = tile.Subject.Subject.Role + "." + (localIndex + 1).ToString(CultureInfo.InvariantCulture),
                    ["rect"] = new JObject { ["x"] = x, ["y"] = y, ["width"] = layout.TileSize, ["height"] = layout.TileSize },
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
            string renderVersion = level == "-test" ? TestAnalysisPictureRenderVersion : AnalysisPictureRenderVersion;
            string source = renderVersion + "|" + level + "|" + string.Join("|", subjects.Select(item => item.Role + ":" + item.Record.Id));
            using (SHA256 hash = SHA256.Create())
            {
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(source))).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
            }
        }

        private static SubjectPictureData BuildSubjectPictureData(TimelineSessionRecord session, AnalysisSubject subject)
        {
            int frameCount = Math.Max(1, subject.EndFrameExclusive - subject.StartFrame);
            var pelvis = new Vector3[frameCount];
            var leftHand = new Vector3[frameCount];
            var rightHand = new Vector3[frameCount];
            var leftFoot = new Vector3[frameCount];
            var rightFoot = new Vector3[frameCount];
            Bounds firstBounds = default;
            Bounds lastBounds = default;
            Bounds allPoseBounds = default;
            bool hasPoseBounds = false;
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
                    leftHand[localFrame] = ReadHumanoidBonePosition(subject.Character.Animator, HumanBodyBones.LeftHand, pelvis[localFrame]);
                    rightHand[localFrame] = ReadHumanoidBonePosition(subject.Character.Animator, HumanBodyBones.RightHand, pelvis[localFrame]);
                    leftFoot[localFrame] = ReadHumanoidBonePosition(subject.Character.Animator, HumanBodyBones.LeftFoot, pelvis[localFrame]);
                    rightFoot[localFrame] = ReadHumanoidBonePosition(subject.Character.Animator, HumanBodyBones.RightFoot, pelvis[localFrame]);
                    Bounds currentBounds = CalculateSkinnedBounds(subject.Character.Root);
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

            // Keep the legacy bounds for low/middle/high.  The complete pose
            // bounds are exposed separately and consumed only by -test.
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
                pelvis,
                leftHand,
                rightHand,
                leftFoot,
                rightFoot,
                leftContacts,
                rightContacts,
                firstBounds,
                lastBounds,
                bounds,
                testBounds);
        }

        private static Vector3 ReadHumanoidBonePosition(Animator animator, HumanBodyBones bone, Vector3 fallback)
        {
            Transform transform = animator != null ? animator.GetBoneTransform(bone) : null;
            return transform != null ? transform.position : fallback;
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
            if (level == "-test")
            {
                return new List<PictureTile>
                {
                    PictureTile.TestFootTransitions(subject, new Vector3(1f, .75f, -1f)),
                    PictureTile.TestKeyframes(subject, new Vector3(1f, .75f, -1f))
                };
            }

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
                Texture2D image = RenderPictureTile(tile, layout.TileSize, trajectoryScale);
                try
                {
                    DrawTileNumber(image, localIndex + 1);
                    int x = (localIndex % layout.TileColumns) * layout.TileSize;
                    int y = (subjects.Count - panel - 1) * layout.Height +
                        (layout.TileRows - 1 - localIndex / layout.TileColumns) * layout.TileSize;
                    canvas.SetPixels(x, y, layout.TileSize, layout.TileSize, image.GetPixels());
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
            if (tile.Presentation == "test_foot_transitions" || tile.Presentation == "test_keyframes")
            {
                return RenderTestPictureTile(tile, size, trajectoryScale);
            }

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
                        float alpha = GhostAlpha(index, frames.Count, separated);
                        RenderPoseOnto(result, camera, environment, tile.Subject, frame, ResolveGhostPoseTint(tile.Subject, frame), alpha);
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

        private static Texture2D RenderTestPictureTile(PictureTile tile, int size, TrajectoryScale trajectoryScale)
        {
            int lastFrame = Math.Max(0, tile.Subject.Pelvis.Length - 1);
            var requestedFrames = tile.TrajectoryFrames;
            requestedFrames = requestedFrames
                .Concat(new[] { 0, lastFrame })
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();
            // Sample once with a real Avatar, then render all poses from the
            // captured skeleton snapshots. Virtual poses never run an
            // Animator/PlayableGraph and therefore cannot fall back to T-pose.
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
                        alpha = Mathf.Clamp01(alpha);
                        virtualPoses.Add(CreateTestVirtualPose(
                            posePlan.Get(frame), tint, alpha));
                    }
                }
                Color startTint = ResolveTestPoseTint(tile, 0, out bool startIsKeyframe, out bool startIsFootTransition);
                Color endTint = ResolveTestPoseTint(tile, lastFrame, out bool endIsKeyframe, out bool endIsFootTransition);
                if (!startIsKeyframe && !startIsFootTransition) startTint = new Color(1f, .65f, .05f, 1f);
                if (!endIsKeyframe && !endIsFootTransition) endTint = new Color(1f, .35f, 0f, 1f);
                virtualPoses.Add(CreateTestVirtualPose(posePlan.Get(0), startTint, 1f));
                virtualPoses.Add(CreateTestVirtualPose(posePlan.Get(lastFrame), endTint, 1f));

                Bounds contentBounds = CalculateVirtualPoseBounds(virtualPoses);
                Bounds tileBounds = IncludeGroundInBounds(contentBounds);
                var environment = new List<GameObject>();
                CreateTestPictureEnvironment(environment, tileBounds);
                if (tile.ShowTestTrajectories)
                {
                    CreateTestBodyTrajectories(environment, tile.Subject);
                }

                Camera camera = CreateTestAnalysisPictureCamera(tileBounds, tile.Direction);
                try
                {
                    Texture2D result = RenderCamera(camera, size, new Color(.12f, .12f, .12f, 1f));
                    foreach (TestVirtualPose pose in virtualPoses)
                    {
                        RenderTestPoseOnto(result, camera, environment, pose);
                    }
                    return result;
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
            Texture2D layer = RenderCamera(camera, destination.width, new Color(0f, 0f, 0f, 0f));
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

        private static Bounds CalculateVirtualPoseBounds(IReadOnlyList<TestVirtualPose> poses)
        {
            Bounds result = default;
            bool initialized = false;
            foreach (TestVirtualPose pose in poses)
            {
                if (pose?.Preview == null) continue;
                Bounds bounds = CalculateSkinnedBounds(pose.Preview);
                bounds.Encapsulate(pose.Preview.transform.position);
                if (!initialized)
                {
                    result = bounds;
                    initialized = true;
                }
                else
                {
                    result.Encapsulate(bounds);
                }
            }
            return initialized ? result : new Bounds(Vector3.up, Vector3.one);
        }

        private static TestPosePlan BuildTestPosePlan(SubjectPictureData subject, IReadOnlyList<int> frames)
        {
            var source = UnityEngine.Object.Instantiate(subject.Subject.Character.Root);
            source.name = "Kimodo Test Pose Sampler";
            source.hideFlags = HideFlags.HideAndDontSave;
            foreach (Transform transform in source.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = 31;
            }

            Animator animator = source.GetComponentInChildren<Animator>(true);
            PlayableGraph graph = default;
            AnimationClipPlayable playable = default;
            AnimationClip clip = subject.Subject.Animation?.Clip;
            if (animator != null && KimodoRetargetCoreUtility.IsValidHumanoid(subject.Subject.Character.Avatar))
            {
                animator.avatar = subject.Subject.Character.Avatar;
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = true;
                animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                if (clip != null)
                {
                    graph = PlayableGraph.Create("KimodoTestPoseSampler");
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    playable = AnimationClipPlayable.Create(graph, clip);
                    playable.SetApplyFootIK(true);
                    playable.SetApplyPlayableIK(true);
                    AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "KimodoTestPoseOutput", animator);
                    output.SetSourcePlayable(playable);
                    graph.Play();
                }
            }

            var snapshots = new Dictionary<int, TestPoseSnapshot>();
            try
            {
                foreach (int frame in frames.Distinct().OrderBy(item => item))
                {
                    if (playable.IsValid())
                    {
                        playable.SetTime(ResolveAnimationClipSampleTime(subject.Subject.Animation, frame, clip));
                        graph.Evaluate(0f);
                    }
                    else
                    {
                        CharacterPose pose = CaptureCharacterPose(subject.Subject.Character, subject.Subject.StartFrame + frame);
                        var sample = new KimodoMarkerSampleResult { sampleTime = (subject.Subject.StartFrame + frame) / SessionFrameRate };
                        SetCanonicalPose(sample, pose, subject.Subject.Character);
                        ApplyCanonicalPoseToPreview(source, subject.Subject.Character, sample);
                    }
                    snapshots[frame] = TestPoseSnapshot.Capture(source);
                }
                return new TestPosePlan(source, graph, snapshots);
            }
            catch
            {
                if (graph.IsValid()) graph.Destroy();
                UnityEngine.Object.DestroyImmediate(source);
                throw;
            }
        }

        private static TestVirtualPose CreateTestVirtualPose(
            TestPoseSnapshot snapshot,
            Color tint,
            float alpha)
        {
            GameObject preview = UnityEngine.Object.Instantiate(snapshot.SourcePrefab);
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
            TimelineCharacterRecord character = subject.Subject.Character;
            GameObject preview = UnityEngine.Object.Instantiate(character.Root);
            preview.name = "Kimodo Pose Preview";
            preview.hideFlags = HideFlags.HideAndDontSave;
            foreach (Transform transform in preview.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = 31;
            }

            Animator animator = preview.GetComponentInChildren<Animator>(true)
                ?? throw new InvalidOperationException($"Character '{character.Name}' preview has no Animator.");
            // The preview must use the same humanoid Avatar as the Session
            // character. Without this assignment AnimationClipPlayable leaves
            // the instantiated model in its imported T-pose.
            if (KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                animator.avatar = character.Avatar;
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = true;
                animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
            }

            AnimationClip clip = subject.Subject.Animation?.Clip;
            if (clip != null && KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar))
            {
                PlayableGraph graph = default;
                try
                {
                    graph = PlayableGraph.Create("KimodoAnalysisPoseGraph");
                    graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                    AnimationClipPlayable playable = AnimationClipPlayable.Create(graph, clip);
                    playable.SetApplyFootIK(true);
                    playable.SetApplyPlayableIK(true);
                    AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "KimodoAnalysisPoseOutput", animator);
                    output.SetSourcePlayable(playable);
                    float sampleTime = ResolveAnimationClipSampleTime(subject.Subject.Animation, localFrame, clip);
                    playable.SetTime(sampleTime);
                    graph.Play();
                    graph.Evaluate(0f);
                }
                finally
                {
                    if (graph.IsValid()) graph.Destroy();
                }
                return preview;
            }

            // Keep the canonical-pose fallback for non-clip/legacy records.
            CharacterPose pose = CaptureCharacterPose(character, subject.Subject.StartFrame + localFrame);
            var sample = new KimodoMarkerSampleResult { sampleTime = (subject.Subject.StartFrame + localFrame) / SessionFrameRate };
            SetCanonicalPose(sample, pose, character);
            ApplyCanonicalPoseToPreview(preview, character, sample);
            return preview;
        }

        private static float ResolveAnimationClipSampleTime(
            TimelineAnimationRecord animation,
            int localFrame,
            AnimationClip clip)
        {
            float clipIn = animation?.TimelineClip != null
                ? (float)Math.Max(0.0, animation.TimelineClip.clipIn)
                : 0f;
            float time = (float)(localFrame / SessionFrameRate) + clipIn;
            return clip.length > 0f ? Mathf.Clamp(time, 0f, clip.length) : 0f;
        }

        private static void ApplyCanonicalPoseToPreview(
            GameObject preview,
            TimelineCharacterRecord character,
            KimodoMarkerSampleResult sample)
        {
            if (sample?.characterPose == null || !sample.characterPose.TryValidate(out _)) return;
            Animator animator = preview.GetComponentInChildren<Animator>(true);
            if (animator == null || !KimodoRetargetCoreUtility.IsValidHumanoid(character.Avatar)) return;
            HumanPose pose = CharacterPoseMuscleAdapter.ToMuscleSample(sample.characterPose).pose;
            using (var handler = new HumanPoseHandler(character.Avatar, animator.transform))
            {
                handler.SetHumanPose(ref pose);
            }
            animator.transform.SetPositionAndRotation(sample.characterPose.root.t, sample.characterPose.root.q);
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
            return result.Distinct().OrderBy(frame => frame).ToList();
        }

        private static List<int> BuildTestSampleFrames(SubjectPictureData subject, IEnumerable<int> primaryFrames)
        {
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            var events = (primaryFrames ?? Enumerable.Empty<int>())
                .Select(frame => Mathf.Clamp(frame, 0, lastFrame))
                .Append(0)
                .Append(lastFrame)
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();

            // Keep the endpoints and discard any event closer than 10 frames
            // to its predecessor. This leaves one sample per short interval.
            for (int index = 1; index < events.Count;)
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

            var result = new List<int> { events[0] };
            for (int index = 1; index < events.Count; index++)
            {
                int from = events[index - 1];
                int to = events[index];
                int gap = to - from;
                int divisions = gap > 20 ? Mathf.CeilToInt(gap / 20f) : 1;
                for (int part = 1; part < divisions; part++)
                {
                    result.Add(from + Mathf.RoundToInt(gap * part / (float)divisions));
                }
                result.Add(to);
            }
            return result.Distinct().OrderBy(frame => frame).ToList();
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
            if (left == right)
            {
                tint = Color.gray;
                return left || right;
            }
            tint = left ? new Color(.2f, .45f, 1f) : new Color(1f, .2f, .2f);
            return true;
        }

        private static bool IsKeyframe(SubjectPictureData subject, int frame)
        {
            return subject.KeyFrameSet.Contains(frame);
        }

        private static Color ResolveGhostPoseTint(SubjectPictureData subject, int frame)
        {
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            if (frame == 0) return Color.yellow;
            if (IsKeyframe(subject, frame)) return Color.yellow;
            if (frame == lastFrame) return new Color(1f, .35f, 0f, 1f);
            return TryGetFootTransitionTint(subject, frame, out Color footTint) ? footTint : Color.white;
        }

        private static Color ResolveTestPoseTint(PictureTile tile, int frame, out bool keyframe, out bool footTransition)
        {
            SubjectPictureData subject = tile.Subject;
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            keyframe = tile.Presentation == "test_keyframes" && tile.PrimaryFrames.Contains(frame);
            footTransition = tile.Presentation == "test_foot_transitions" &&
                tile.PrimaryFrames.Contains(frame) && TryGetFootTransitionTint(subject, frame, out _);
            if (keyframe || frame == 0) return Color.yellow;
            if (frame == lastFrame) return new Color(1f, .35f, 0f, 1f);
            return footTransition && TryGetFootTransitionTint(subject, frame, out Color footTint)
                ? footTint
                : Color.white;
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
                "Packages/com.nvlab.character-animation-cli-unity/Editor/Model/UVCheckGrid.png")
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

            GameObject floor = new GameObject("Kimodo Test UV Grid") { hideFlags = HideFlags.HideAndDontSave };
            floor.transform.position = center;
            floor.layer = captureLayer;
            MeshFilter filter = floor.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = floor.AddComponent<MeshRenderer>();
            Material material = MakeMaterial(Color.white);
            if (gridTexture != null && material.HasProperty("_MainTex")) material.mainTexture = gridTexture;
            renderer.sharedMaterial = material;
            return floor;
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

        private static Camera CreateTestAnalysisPictureCamera(Bounds bounds, Vector3 direction)
        {
            GameObject cameraObject = new GameObject("Kimodo Test Analysis Picture Camera") { hideFlags = HideFlags.HideAndDontSave };
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.cullingMask = 1 << 31;
            camera.orthographic = true;
            camera.aspect = 1f;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 1000f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            Vector3 normalizedDirection = direction.sqrMagnitude > .0001f ? direction.normalized : new Vector3(1f, .75f, -1f).normalized;
            float distance = Mathf.Max(8f, bounds.extents.magnitude * 4f);
            camera.transform.position = bounds.center + normalizedDirection * distance;
            Vector3 up = Mathf.Abs(Vector3.Dot(normalizedDirection, Vector3.up)) > .95f ? Vector3.forward : Vector3.up;
            camera.transform.LookAt(bounds.center, up);

            float maxHorizontal = 0f;
            float maxVertical = 0f;
            float maxDepth = 0f;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            for (int x = 0; x <= 1; x++)
            {
                for (int y = 0; y <= 1; y++)
                {
                    for (int z = 0; z <= 1; z++)
                    {
                        Vector3 local = camera.transform.InverseTransformPoint(new Vector3(
                            x == 0 ? min.x : max.x,
                            y == 0 ? min.y : max.y,
                            z == 0 ? min.z : max.z));
                        maxHorizontal = Mathf.Max(maxHorizontal, Mathf.Abs(local.x));
                        maxVertical = Mathf.Max(maxVertical, Mathf.Abs(local.y));
                        maxDepth = Mathf.Max(maxDepth, Mathf.Abs(local.z));
                    }
                }
            }
            // -test prioritizes readable pose detail over showing every edge of
            // the aggregate ghost bounds.  The view volume is deliberately
            // half-sized, with a fixed half-meter breathing margin.
            camera.orthographicSize = Mathf.Max(
                .5f,
                Mathf.Max(maxHorizontal, maxVertical) * TestCameraFitScale + TestCameraMarginMeters);
            camera.farClipPlane = Mathf.Max(100f, distance + maxDepth + 10f);
            return camera;
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

        private static void CreatePelvisTrajectory(
            List<GameObject> objects,
            Vector3[] points,
            TrajectoryScale scale,
            float lineWidth = .045f,
            bool unlit = false)
        {
            CreateMotionTrajectory(objects, points, scale, lineWidth, unlit, 1f);
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
            GameObject lineObject = new GameObject("Kimodo Test Body Trajectory") { hideFlags = HideFlags.HideAndDontSave };
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

        private static void CreateMotionTrajectory(
            List<GameObject> objects,
            Vector3[] points,
            TrajectoryScale scale,
            float lineWidth,
            bool unlit,
            float alphaMultiplier)
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
                color.a = Mathf.Lerp(1f, .2f, accelerationWeight) * alphaMultiplier;
                CreateWorldLine(objects, points[index - 1] + Vector3.up * .02f, points[index] + Vector3.up * .02f, lineWidth, color, unlit);
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
            for (int column = 1; column < layout.TileColumns; column++)
            {
                int x = column * layout.TileSize - 2;
                for (int panel = 0; panel < panels; panel++)
                {
                    FillRect(texture, x, panel * layout.Height, 4, layout.Height, Color.white);
                }
            }
            for (int panel = 1; panel < panels; panel++)
            {
                int y = panel * layout.Height - 2;
                FillRect(texture, 0, y, texture.width, 4, Color.white);
            }
            for (int panel = 0; panel < panels; panel++)
            {
                int originY = panel * layout.Height;
                for (int row = 1; row < layout.TileRows; row++)
                {
                    int y = originY + row * layout.TileSize - 2;
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

        private static void CreateWorldLine(List<GameObject> objects, Vector3 from, Vector3 to, float width, Color color, bool unlit = false)
        {
            GameObject lineObject = new GameObject("Kimodo Evidence Line") { hideFlags = HideFlags.HideAndDontSave };
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
            Shader shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
            return new Material(shader) { hideFlags = HideFlags.HideAndDontSave, color = color };
        }

        private static Material MakeUnlitMaterial(Color color)
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
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
                Vector3[] leftHand,
                Vector3[] rightHand,
                Vector3[] leftFoot,
                Vector3[] rightFoot,
                bool[] leftContacts,
                bool[] rightContacts,
                Bounds firstBounds,
                Bounds lastBounds,
                Bounds bounds,
                Bounds testBounds)
            {
                Subject = subject;
                Pelvis = pelvis;
                LeftHand = leftHand;
                RightHand = rightHand;
                LeftFoot = leftFoot;
                RightFoot = rightFoot;
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
            public Vector3[] Pelvis { get; }
            public Vector3[] LeftHand { get; }
            public Vector3[] RightHand { get; }
            public Vector3[] LeftFoot { get; }
            public Vector3[] RightFoot { get; }
            public bool[] LeftContacts { get; }
            public bool[] RightContacts { get; }
            public Bounds FirstBounds { get; }
            public Bounds LastBounds { get; }
            public Bounds Bounds { get; }
            public Bounds TestBounds { get; }
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
            public HashSet<int> PrimaryFrames { get; private set; } = new HashSet<int>();
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
                return TestFrameSet(subject, "test_keyframes", "keyframes", subject.KeyFrameSet, direction, true);
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
                List<int> frames = BuildTestSampleFrames(subject, primary);
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

        private static void TintPreview(GameObject preview, Color tint, List<Material> transientMaterials)
        {
            foreach (Renderer renderer in preview.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.materials;
                foreach (Material material in materials)
                {
                    if (material == null) continue;
                    transientMaterials?.Add(material);
                    if (material.HasProperty("_Color"))
                    {
                        material.color = Color.Lerp(material.color, tint, .8f);
                    }
                }
            }
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
            private readonly PlayableGraph graph;
            private readonly Dictionary<int, TestPoseSnapshot> snapshots;

            public TestPosePlan(
                GameObject source,
                PlayableGraph graph,
                Dictionary<int, TestPoseSnapshot> snapshots)
            {
                this.source = source;
                this.graph = graph;
                this.snapshots = snapshots;
            }

            public TestPoseSnapshot Get(int frame)
            {
                if (snapshots.TryGetValue(frame, out TestPoseSnapshot snapshot)) return snapshot;
                return snapshots.Values.First();
            }

            public void Dispose()
            {
                if (graph.IsValid()) graph.Destroy();
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
            private PictureLayout(int columns, int rows, int cellSize, int tileSpan)
            {
                Columns = columns;
                Rows = rows;
                CellSize = cellSize;
                TileSpan = tileSpan;
            }
            public int Columns { get; }
            public int Rows { get; }
            public int CellSize { get; }
            public int TileSpan { get; }
            public int TileColumns => Columns / TileSpan;
            public int TileRows => Rows / TileSpan;
            public int TileSize => CellSize * TileSpan;
            public int Width => Columns * CellSize;
            public int Height => Rows * CellSize;
            public static PictureLayout ForLevel(string level) => level == "high"
                ? new PictureLayout(8, 2, 256, 1)
                : level == "middle"
                    ? new PictureLayout(4, 2, 256, 1)
                    : level == "-test"
                        ? new PictureLayout(4, 2, 256, 2)
                        : new PictureLayout(2, 1, 128, 1);
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
