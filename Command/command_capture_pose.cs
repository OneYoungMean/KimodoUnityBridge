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
        private static void CreateWorldLine(List<GameObject> objects, IReadOnlyList<Vector3> points, Color color, float width)
        {
            if (points == null || points.Count < 2) return;
            GameObject lineObject = MoveToAnalysisSessionRoot(
                new GameObject("Kimodo Root2D Pelvis Trajectory") { hideFlags = HideFlags.HideAndDontSave });
            SetLayerRecursively(lineObject, SessionCaptureLayer);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.positionCount = points.Count;
            line.SetPositions(points.Select(point => point + Vector3.up * .025f).ToArray());
            line.startWidth = line.endWidth = width;
            line.useWorldSpace = true;
            line.sharedMaterial = MakeUnlitMaterial(color);
            line.startColor = line.endColor = color;
            objects.Add(lineObject);
        }

        private static void CreateGroundMarker(
            List<GameObject> objects,
            Vector3 position,
            float radius,
            Color color,
            string name = "Kimodo Root2D Keyframe",
            float height = .045f)
        {
            GameObject marker = MoveToAnalysisSessionRoot(GameObject.CreatePrimitive(PrimitiveType.Cylinder));
            marker.name = name;
            marker.hideFlags = HideFlags.HideAndDontSave;
            SetLayerRecursively(marker, SessionCaptureLayer);
            marker.transform.position = position + Vector3.up * height;
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
            GameObject preview = MoveToAnalysisSessionRoot(UnityEngine.Object.Instantiate(snapshot.SourcePrefab));
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

        private static TestVirtualPose CreateGhostVirtualPose(
            SubjectPictureData subject,
            int frame,
            Color tint,
            float alpha)
        {
            GameObject preview = CreateAnalysisPosePreview(subject, frame);
            var transientMaterials = new List<Material>();
            bool usesGhostMaterial = ConfigureTestGhostMaterial(preview, tint, alpha, transientMaterials);
            if (!usesGhostMaterial) TintPreview(preview, tint, transientMaterials);
            SetPreviewRenderersEnabled(preview, false);
            return new TestVirtualPose(preview, transientMaterials, alpha, usesGhostMaterial);
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
            GameObject preview = MoveToAnalysisSessionRoot(UnityEngine.Object.Instantiate(subject.Character.Root));
            preview.name = "Kimodo Mesh Pose Preview";
            preview.hideFlags = HideFlags.HideAndDontSave;
            foreach (Transform transform in preview.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = SessionCaptureLayer;
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
            GameObject preview = MoveToAnalysisSessionRoot(UnityEngine.Object.Instantiate(character.Root));
            preview.name = "Kimodo Pose Preview";
            preview.hideFlags = HideFlags.HideAndDontSave;
            foreach (Transform transform in preview.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = SessionCaptureLayer;
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
                // The root2D override is body(hips)-anchored: its t equals the sampled
                // hips world pose. Re-anchor the preview so the hips land on the
                // override pose. Placing the root at the override instead stacked it on
                // top of the muscle root translation and double-counted the travel,
                // bending the rendered root2d trajectory away from the real motion.
                Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips == null)
                {
                    animator.transform.position = KimodoMotionMath.ApplyPlanarPosition(
                        animator.transform.position,
                        rootPosition);
                    animator.transform.rotation = KimodoMotionMath.ApplyPlanarHeading(
                        animator.transform.rotation,
                        rootRotation);
                    return;
                }

                var rootPose = new Pose(animator.transform.position, animator.transform.rotation);
                var hipsPose = new Pose(hips.position, hips.rotation);
                Quaternion relativeRotation = Quaternion.Inverse(rootPose.rotation) * hipsPose.rotation;
                Vector3 relativePosition = Quaternion.Inverse(rootPose.rotation) * (hipsPose.position - rootPose.position);
                // Preserve the muscle pelvis tilt; the override only supplies planar heading.
                Quaternion planarHipsHeading = KimodoMotionMath.ResolvePlanarHeading(hipsPose.rotation);
                Quaternion pelvisTilt = Quaternion.Inverse(planarHipsHeading) * hipsPose.rotation;
                Quaternion desiredHipsRotation = rootRotation * pelvisTilt;
                Quaternion newRootRotation = desiredHipsRotation * Quaternion.Inverse(relativeRotation);
                Vector3 newRootPosition = rootPosition - newRootRotation * relativePosition;
                animator.transform.SetPositionAndRotation(newRootPosition, newRootRotation);
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
                        Color result = Color.Lerp(current, tint, .9f);
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
            // Auxiliary samples are intentionally neutral.  Using white here
            // makes every non-event pose wash out the source material when
            // several poses are composited into a ghost/trajectory tile.
            return TryGetFootTransitionTint(subject, frame, out Color tint) ? tint : Color.gray;
        }

        private static IReadOnlyList<int> FootTransitionFrames(SubjectPictureData subject)
        {
            // Use only QuickServer events here. The KMB contact channel has a
            // different timebase in some clips and must not be used as a
            // fallback or merged into the rendered markers.
            return (subject.Subject.Record.Analysis?["foot_contacts"] as JArray ?? new JArray())
                .OfType<JObject>()
                .Select(item => Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, Math.Max(0, subject.Pelvis.Length - 1)))
                .Distinct()
                .OrderBy(frame => frame)
                .ToArray();
        }

        private static bool TryGetFootTransitionTint(SubjectPictureData subject, int frame, out Color tint)
        {
            // Keep marker colors on the same QuickServer analysis timebase as
            // FootTransitionFrames; do not fall back to KMB contact samples.
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
            if (IsKeyframe(subject, frame)) return TestKeyframeTint;
            return TryGetFootTransitionTint(subject, frame, out Color footTint) ? footTint : Color.gray;
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
            if (keyframe) return TestKeyframeTint;
            return footTransition && TryGetFootTransitionTint(subject, frame, out Color footTint)
                ? footTint
                : Color.gray;
        }

        private static Color ResolveSingleTestPoseTint(PictureTile tile, int frame)
        {
            int lastFrame = Math.Max(0, tile.Subject.Pelvis.Length - 1);
            if (frame == 0) return TestStartFrameTint;
            if (frame == lastFrame) return TestEndFrameTint;
            if (string.Equals(tile.PoseKind, "keyframe", StringComparison.Ordinal)) return TestKeyframeTint;
            if (string.Equals(tile.PoseKind, "foot_transition", StringComparison.Ordinal) &&
                TryGetFootTransitionTint(tile.Subject, frame, out Color footTint))
            {
                return footTint;
            }
            return Color.white;
        }

    }
}
