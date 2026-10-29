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
            TimelineSessionRecord session,
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
            KimodoMarkerSampleResult[] samples = CaptureSampleResults(session, character, startFrame, frameCount);
            if (!string.IsNullOrWhiteSpace(key)) AnalysisPoseSamples[key] = samples;
            return samples;
        }

        // The analysis picture is the fixed 16:9 composite: four
        // overview tiles plus eight key poses and eight step poses.
        private static JObject RenderAnalysisPictures(
            TimelineSessionRecord session,
            IReadOnlyList<AnalysisSubject> subjects,
            int requestedResolution,
            JObject cameraOptions)
        {
            if (subjects == null || subjects.Count != 1)
            {
                throw new InvalidOperationException("Analysis picture rendering accepts exactly one clip.");
            }
            JObject capture = RenderSceneAnalysisCapture(session, subjects[0], requestedResolution, cameraOptions);
            return (capture["pictures"] as JObject)?.DeepClone() as JObject ?? new JObject();
        }

        private static List<PictureTile> BuildAnalysisTiles(SubjectPictureData subject)
        {
            List<int> allKeyframes = GetAllAnalysisKeyframes(subject);
            List<int> allFootEvents = GetAllFootEventFrames(subject);
            IReadOnlyList<int?> keyframes = SelectUniformlySpacedKeyframes(subject, allKeyframes, 8);
            IReadOnlyList<int?> steps = SelectFirstNonOverlappingFrames(subject, allFootEvents, 8);
            var result = new List<PictureTile>
            {
                PictureTile.Overview(subject, "3d_track", Vector3.up),
                PictureTile.Overview(subject, "height_time_track", Vector3.up),
                PictureTile.SelectedOverview(subject, "3d_ghost", allKeyframes, new Vector3(1f, .75f, -1f)),
                PictureTile.SelectedOverview(subject, "3d_ghost_track", allFootEvents, new Vector3(1f, .75f, -1f))
            };
            for (int index = 0; index < 8; index++) result.Add(PictureTile.PoseSlot(subject, keyframes[index], "key_pose", index + 1));
            for (int index = 0; index < 8; index++) result.Add(PictureTile.PoseSlot(subject, steps[index], "step_pose", index + 1));
            // SelectedOverview records the frames that survive spatial
            // de-duplication in "frames" and preserves the complete source
            // set separately in "candidate_frames". Keep that distinction:
            // these tiles describe what was drawn, not every analysis event.
            result[2].Description["selected_frames"] = new JArray(keyframes.Where(value => value.HasValue).Select(value => value.Value));
            result[3].Description["selected_frames"] = new JArray(steps.Where(value => value.HasValue).Select(value => value.Value));
            result[2].Description["shared_with"] = "key_pose";
            result[3].Description["shared_with"] = "step_pose";
            return result;
        }

        private static List<int> BuildHeightTimePoseFrames(SubjectPictureData subject)
        {
            int lastFrame = Math.Max(0, subject?.Pelvis?.Length - 1 ?? 0);
            var frames = (subject?.KeyFrameSet ?? new HashSet<int>())
                .Append(0)
                .Append(lastFrame)
                .Select(frame => Mathf.Clamp(frame, 0, lastFrame))
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();
            if (subject == null || frames.Count <= 2) return frames;

            const float maximumWidthToHeight = 5f;
            const float layoutAspect = 4f / 3f;
            while (frames.Count > 2)
            {
                float poseHeight = CalculateHeightTimePoseHeight(subject, frames);
                float length = CalculateHeightTimeLength(subject, frames, layoutAspect);
                if (length / Mathf.Max(.001f, poseHeight) <= maximumWidthToHeight) break;

                int removeIndex = -1;
                int closestGap = int.MaxValue;
                for (int index = 0; index < frames.Count - 1; index++)
                {
                    if (frames[index + 1] == lastFrame) continue;
                    int gap = frames[index + 1] - frames[index];
                    if (gap <= closestGap)
                    {
                        closestGap = gap;
                        removeIndex = index + 1;
                    }
                }
                if (removeIndex < 0) break;
                frames.RemoveAt(removeIndex);
            }
            return frames;
        }

        private static float CalculateHeightTimePoseHeight(
            SubjectPictureData subject,
            IReadOnlyList<int> poseFrames)
        {
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            for (int index = 0; index < poseFrames.Count; index++)
            {
                int frame = Mathf.Clamp(poseFrames[index], 0, Math.Max(0, subject.Pelvis.Length - 1));
                Bounds pose = CalculateRawPreviewPoseBounds(subject, frame);
                minY = Mathf.Min(minY, pose.min.y);
                maxY = Mathf.Max(maxY, pose.max.y);
            }
            if (float.IsInfinity(minY) || float.IsInfinity(maxY)) return 1f;
            return Mathf.Max(.001f, maxY - minY);
        }

        private static float CalculateHeightTimeLength(
            SubjectPictureData subject,
            IReadOnlyList<int> poseFrames,
            float aspect)
        {
            if (subject == null || poseFrames == null || poseFrames.Count == 0) return 1f;
            var poses = new List<Bounds>(poseFrames.Count);
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            for (int index = 0; index < poseFrames.Count; index++)
            {
                int frame = Mathf.Clamp(poseFrames[index], 0, Math.Max(0, subject.Pelvis.Length - 1));
                Bounds pose = CalculateRawPreviewPoseBounds(subject, frame);
                Vector3 hips = subject.Pelvis[frame];
                pose.center += new Vector3(-hips.x, 0f, -hips.z);
                poses.Add(pose);
                minY = Mathf.Min(minY, pose.min.y);
                maxY = Mathf.Max(maxY, pose.max.y);
            }

            if (float.IsNaN(minY) || float.IsInfinity(minY) || float.IsNaN(maxY) || float.IsInfinity(maxY)) return 1f;
            float targetWidth = Mathf.Max(.001f, maxY - minY) * Mathf.Max(.001f, aspect);
            float minimumLength = 0f;
            const float poseGap = .06f;
            int lastFrame = Math.Max(1, subject.Pelvis.Length - 1);
            for (int right = 1; right < poses.Count; right++)
            for (int left = 0; left < right; left++)
            {
                int frameGap = poseFrames[right] - poseFrames[left];
                if (frameGap <= 0) continue;
                minimumLength = Mathf.Max(minimumLength,
                    (poses[left].max.z - poses[right].min.z + poseGap) * lastFrame / frameGap);
            }

            float WidthAtLength(float length)
            {
                float minZ = float.PositiveInfinity;
                float maxZ = float.NegativeInfinity;
                for (int index = 0; index < poses.Count; index++)
                {
                    float time = subject.Pelvis.Length <= 1 ? 0f :
                        Mathf.Clamp(poseFrames[index], 0, subject.Pelvis.Length - 1) / (float)(subject.Pelvis.Length - 1);
                    minZ = Mathf.Min(minZ, time * length + poses[index].min.z);
                    maxZ = Mathf.Max(maxZ, time * length + poses[index].max.z);
                }
                return maxZ - minZ;
            }

            float upper = Mathf.Max(1f, targetWidth - poses[0].min.z);
            while (WidthAtLength(upper) < targetWidth && upper < 100000f) upper *= 2f;
            if (WidthAtLength(0f) >= targetWidth) return Mathf.Max(.001f, minimumLength);
            float lower = 0f;
            for (int iteration = 0; iteration < 32; iteration++)
            {
                float middle = (lower + upper) * .5f;
                if (WidthAtLength(middle) < targetWidth) lower = middle;
                else upper = middle;
            }
            return Mathf.Max(.001f, upper, minimumLength);
        }

        private static List<int> GetAllAnalysisKeyframes(SubjectPictureData subject)
        {
            return subject.KeyFrameSet.OrderBy(frame => frame).ToList();
        }

        private static List<int> GetAllFootEventFrames(SubjectPictureData subject)
        {
            int last = Math.Max(0, subject.Pelvis.Length - 1);
            return (subject.Subject.Record.Analysis?["foot_contacts"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Where(IsFootLandingEvent)
                .Select(item => Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, last))
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();
        }

        private static bool IsFootLandingEvent(JObject item)
        {
            return item?.Value<bool?>("contact") == true;
        }

        private static IReadOnlyList<int?> SelectFirstNonOverlappingFrames(
            SubjectPictureData subject,
            IEnumerable<int> candidates,
            int count)
        {
            List<int> validFrames = GetNonOverlappingFrames(subject, candidates);
            int targetCount = Math.Max(0, count);
            return validFrames.Take(targetCount).Select(frame => (int?)frame)
                .Concat(Enumerable.Repeat<int?>(null, Math.Max(0, targetCount - validFrames.Count)))
                .ToArray();
        }

        private static List<int> GetNonOverlappingFrames(
            SubjectPictureData subject,
            IEnumerable<int> candidates)
        {
            int last = Math.Max(0, subject.Pelvis.Length - 1);
            var selected = new List<int>();
            foreach (int frame in (candidates ?? Enumerable.Empty<int>())
                .Select(item => Mathf.Clamp(item, 0, last))
                .Distinct()
                .OrderBy(item => item))
            {
                Vector3 candidate = subject.Pelvis[frame];
                candidate.y = 0f;
                if (selected.Any(previousFrame =>
                {
                    Vector3 previous = subject.Pelvis[previousFrame];
                    previous.y = 0f;
                    return Vector3.Distance(previous, candidate) < MinimumGhostGroundSpacing;
                }))
                {
                    continue;
                }

                selected.Add(frame);
            }
            return selected;
        }

        private static IReadOnlyList<int?> SelectUniformlySpacedKeyframes(
            SubjectPictureData subject,
            IEnumerable<int> candidates,
            int count)
        {
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            int targetCount = Math.Max(0, count);
            if (targetCount == 0) return Array.Empty<int?>();

            // Keep the same spatial de-duplication used by ghost views as the
            // validity filter, but evaluate every candidate before selecting
            // evenly across the resulting ordered set. Always make both clip
            // endpoints eligible.
            List<int> validFrames = GetNonOverlappingFrames(
                subject,
                (candidates ?? Enumerable.Empty<int>()).Concat(new[] { 0, lastFrame }));
            if (!validFrames.Contains(0)) validFrames.Insert(0, 0);
            if (!validFrames.Contains(lastFrame)) validFrames.Add(lastFrame);
            validFrames = validFrames.Distinct().OrderBy(frame => frame).ToList();

            if (targetCount == 1) return new int?[] { 0 };

            int selectedCount = Math.Min(targetCount, validFrames.Count);
            var selected = new int?[selectedCount];
            for (int index = 0; index < selectedCount; index++)
            {
                if (index == 0)
                {
                    selected[index] = 0;
                    continue;
                }
                if (index == selectedCount - 1)
                {
                    selected[index] = lastFrame;
                    continue;
                }

                int candidateIndex = Mathf.RoundToInt(
                    index * (validFrames.Count - 1f) / (selectedCount - 1f));
                selected[index] = validFrames[Mathf.Clamp(candidateIndex, 0, validFrames.Count - 1)];
            }
            return selected.Concat(Enumerable.Repeat<int?>(null, targetCount - selectedCount)).ToArray();
        }


        private static void CalculateSceneViewExtents(
            Bounds bounds,
            Vector3 direction,
            out Vector3 viewCenter,
            out float maxHorizontal,
            out float maxVertical,
            out float maxDepth)
        {
            CalculateSceneViewExtents(
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

        private static void CalculateSceneViewExtents(
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
            var head = new Vector3[frameCount];
            var leftArmSwingAngles = Enumerable.Repeat(float.NaN, frameCount).ToArray();
            var rightArmSwingAngles = Enumerable.Repeat(float.NaN, frameCount).ToArray();
            var poseBounds = new Bounds[frameCount];
            Bounds firstBounds = default;
            Bounds lastBounds = default;
            double originalTime = session.Director.time;
            EvaluatedPosePreview posePreview = null;
            KimodoMarkerSampleResult[] samples;
            try
            {
                samples = CaptureCachedSampleResults(session, subject.Record, subject.Character, subject.StartFrame, frameCount);
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
                    head[localFrame] = ReadHumanoidBonePosition(poseAnimator, HumanBodyBones.Head, subject.Character.Name);
                    TryReadArmSwingAngle(
                        poseAnimator,
                        HumanBodyBones.LeftShoulder,
                        HumanBodyBones.LeftUpperArm,
                        HumanBodyBones.LeftHand,
                        out leftArmSwingAngles[localFrame]);
                    TryReadArmSwingAngle(
                        poseAnimator,
                        HumanBodyBones.RightShoulder,
                        HumanBodyBones.RightUpperArm,
                        HumanBodyBones.RightHand,
                        out rightArmSwingAngles[localFrame]);
                    Bounds currentBounds = CalculateSkinnedBounds(posePreview.Root);
                    poseBounds[localFrame] = currentBounds;
                    if (localFrame == 0) firstBounds = currentBounds;
                    if (localFrame == frameCount - 1) lastBounds = currentBounds;
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

            Bounds bounds = firstBounds;
            foreach (Vector3 point in pelvis) bounds.Encapsulate(point);
            bounds.Encapsulate(lastBounds);
            bounds.Expand(new Vector3(6f, 1f, 6f));
            if (bounds.size.x < 6f) bounds.Expand(new Vector3(6f - bounds.size.x, 0f, 0f));
            if (bounds.size.z < 6f) bounds.Expand(new Vector3(0f, 0f, 6f - bounds.size.z));
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
                head,
                leftArmSwingAngles,
                rightArmSwingAngles,
                leftContacts,
                rightContacts,
                poseBounds,
                bounds);
        }

        private static SubjectPictureData BuildMeshSubjectPictureData(AnalysisSubject subject)
        {
            int frameCount = Math.Max(1, subject.EndFrameExclusive - subject.StartFrame);
            var positions = new Vector3[frameCount];
            var poseBounds = new Bounds[frameCount];
            Bounds allBounds = default;
            bool hasBounds = false;
            for (int localFrame = 0; localFrame < frameCount; localFrame++)
            {
                GameObject preview = CreateMeshPosePreview(subject, localFrame);
                try
                {
                    positions[localFrame] = preview.transform.position;
                    Bounds current = CalculateSkinnedBounds(preview);
                    poseBounds[localFrame] = current;
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
                null,
                null,
                new bool[frameCount],
                new bool[frameCount],
                poseBounds,
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

        private static bool TryReadArmSwingAngle(
            Animator animator,
            HumanBodyBones shoulderBone,
            HumanBodyBones upperArmBone,
            HumanBodyBones handBone,
            out float angleDegrees)
        {
            angleDegrees = 0f;
            if (animator == null) return false;
            Transform shoulder = animator.GetBoneTransform(shoulderBone) ?? animator.GetBoneTransform(upperArmBone);
            Transform hand = animator.GetBoneTransform(handBone);
            if (shoulder == null || hand == null) return false;

            return KimodoForceLineAnalysis.TryCalculateBodyLocalSwingAngle(
                hand.position - shoulder.position,
                animator.transform.rotation,
                out angleDegrees);
        }

        private static Bounds CalculateSkinnedBounds(GameObject root)
        {
            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) return CalculateBounds(root);
            Bounds result = renderers[0].bounds;
            for (int index = 1; index < renderers.Length; index++) result.Encapsulate(renderers[index].bounds);
            return result;
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
            if (analysis["motion_profile"] is not JObject profile)
            {
                profile = new JObject();
                analysis["motion_profile"] = profile;
            }
            profile["force_line"] = KimodoForceLineAnalysis.Build(
                subject.LeftArmSwingAngles,
                subject.RightArmSwingAngles,
                phases,
                subject.Subject.StartFrame);
            if (subject.Subject.Record.RootTrajectory?["samples"] is JArray trajectorySamples)
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
                ["timeline_frame_60"] = subject.Subject.StartFrame + item.Value<int>("anchor_frame"),
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

    internal static class KimodoForceLineAnalysis
    {
        internal const float BalanceThresholdRatio = 0.15f;
        private const float MinimumMeasuredSwingDegrees = 0.01f;

        internal static bool TryCalculateBodyLocalSwingAngle(
            Vector3 shoulderToHandWorld,
            Quaternion characterRootRotation,
            out float angleDegrees)
        {
            Vector3 local = Quaternion.Inverse(characterRootRotation) * shoulderToHandWorld;
            if (float.IsNaN(local.y) || float.IsInfinity(local.y) ||
                float.IsNaN(local.z) || float.IsInfinity(local.z) ||
                local.y * local.y + local.z * local.z <= 1e-8f)
            {
                angleDegrees = 0f;
                return false;
            }

            angleDegrees = Mathf.Atan2(local.z, -local.y) * Mathf.Rad2Deg;
            return !float.IsNaN(angleDegrees) && !float.IsInfinity(angleDegrees);
        }

        internal static JObject Build(
            float[] leftAngles,
            float[] rightAngles,
            JArray phases,
            int startTimelineFrame)
        {
            int frameCount = Math.Max(leftAngles?.Length ?? 0, rightAngles?.Length ?? 0);
            foreach (JObject phase in (phases ?? new JArray()).OfType<JObject>())
            {
                frameCount = Math.Max(frameCount, (phase.Value<int?>("end_frame") ?? -1) + 1);
            }
            JObject result = BuildInterval(leftAngles, rightAngles, 0, frameCount - 1);
            result["method"] = "body_local_shoulder_to_hand_sagittal_angle_delta_sum_v1";
            result["coordinate_space"] = "character_root_local";
            result["angle_unit"] = "degrees";
            result["balance_threshold_ratio"] = BalanceThresholdRatio;
            result["start_timeline_frame_60"] = startTimelineFrame;
            result["end_timeline_frame_60_exclusive"] = startTimelineFrame + frameCount;
            result["phases"] = new JArray((phases ?? new JArray()).OfType<JObject>().Select(phase =>
            {
                int startFrame = Mathf.Clamp(phase.Value<int?>("start_frame") ?? 0, 0, Math.Max(0, frameCount - 1));
                int endFrame = Mathf.Clamp(phase.Value<int?>("end_frame") ?? startFrame, startFrame, Math.Max(startFrame, frameCount - 1));
                JObject metric = BuildInterval(leftAngles, rightAngles, startFrame, endFrame);
                metric["phase_index"] = phase.Value<int?>("phase_index") ?? 0;
                metric["kind"] = phase.Value<string>("kind") ?? "phase";
                metric["start_timeline_frame_60"] = startTimelineFrame + startFrame;
                metric["end_timeline_frame_60_exclusive"] = startTimelineFrame + endFrame + 1;
                return metric;
            }));
            return result;
        }

        private static JObject BuildInterval(float[] leftAngles, float[] rightAngles, int startFrame, int endFrame)
        {
            float leftDeltaSum = 0f;
            float rightDeltaSum = 0f;
            int deltaSampleCount = 0;
            int expectedDeltaSampleCount = Math.Max(0, endFrame - startFrame);
            if (leftAngles != null && rightAngles != null && leftAngles.Length == rightAngles.Length &&
                startFrame >= 0 && endFrame < leftAngles.Length)
            {
                for (int frame = Math.Max(startFrame + 1, 1); frame <= endFrame; frame++)
                {
                    if (!IsFinite(leftAngles[frame - 1]) || !IsFinite(leftAngles[frame]) ||
                        !IsFinite(rightAngles[frame - 1]) || !IsFinite(rightAngles[frame]))
                    {
                        continue;
                    }

                    leftDeltaSum += Mathf.Abs(Mathf.DeltaAngle(leftAngles[frame - 1], leftAngles[frame]));
                    rightDeltaSum += Mathf.Abs(Mathf.DeltaAngle(rightAngles[frame - 1], rightAngles[frame]));
                    deltaSampleCount++;
                }
            }

            float maximumDeltaSum = Mathf.Max(leftDeltaSum, rightDeltaSum);
            bool completeSamples = deltaSampleCount == expectedDeltaSampleCount && deltaSampleCount > 0;
            bool hasEvidence = completeSamples && maximumDeltaSum >= MinimumMeasuredSwingDegrees;
            float difference = Mathf.Abs(leftDeltaSum - rightDeltaSum);
            float imbalanceRatio = hasEvidence ? difference / maximumDeltaSum : 0f;
            return new JObject
            {
                ["status"] = hasEvidence ? "ok" : "insufficient_evidence",
                ["reason"] = hasEvidence ? string.Empty : !completeSamples
                    ? deltaSampleCount == 0 ? "no_shared_valid_angle_pairs" : "incomplete_angle_samples"
                    : "no_measurable_arm_swing",
                ["left_delta_angle_sum_degrees"] = leftDeltaSum,
                ["right_delta_angle_sum_degrees"] = rightDeltaSum,
                ["absolute_difference_degrees"] = difference,
                ["imbalance_ratio"] = hasEvidence ? new JValue(imbalanceRatio) : JValue.CreateNull(),
                ["symmetry_score"] = hasEvidence ? new JValue(1f - imbalanceRatio) : JValue.CreateNull(),
                ["balanced"] = hasEvidence ? new JValue(imbalanceRatio <= BalanceThresholdRatio) : JValue.CreateNull(),
                ["delta_sample_count"] = deltaSampleCount
            };
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
