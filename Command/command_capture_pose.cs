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
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KimodoUnityBridge.Command

{
    internal static partial class command_context
    {
        private const float MinimumGhostGroundSpacing = .5f;

        private static void CreateWorldLine(List<GameObject> objects, IReadOnlyList<Vector3> points, Color color, float width)
        {
            CreateWorldLineCore(
                objects,
                points,
                "Kimodo Root2D Pelvis Trajectory",
                width,
                color,
                Vector3.up * .025f);
        }

        private static Color MetricColor(float value, float redThreshold)
        {
            float yellowThreshold = redThreshold * .5f;
            if (value >= redThreshold) return new Color(.95f, .08f, .08f, 1f);
            if (value >= yellowThreshold) return new Color(1f, .9f, .04f, 1f);
            return new Color(.1f, .85f, .25f, 1f);
        }

        private static void CreateMetricLine(
            List<GameObject> objects,
            IReadOnlyList<Vector3> points,
            IReadOnlyList<float> metrics,
            float redThreshold,
            float width)
        {
            if (points == null || metrics == null || points.Count < 2) return;
            int segmentCount = Mathf.Min(points.Count - 1, metrics.Count);
            if (segmentCount < 1) return;

            var run = new List<Vector3> { points[0] };
            Color runColor = MetricColor(Mathf.Abs(metrics[0]), redThreshold);
            for (int segment = 0; segment < segmentCount; segment++)
            {
                Color color = MetricColor(Mathf.Abs(metrics[segment]), redThreshold);
                if (segment > 0 && color != runColor)
                {
                    CreateWorldLine(objects, run, runColor, width);
                    run = new List<Vector3> { points[segment] };
                    runColor = color;
                }
                run.Add(points[segment + 1]);
            }
            if (run.Count >= 2) CreateWorldLine(objects, run, runColor, width);
        }

        private static void CreateGroundMarker(
            List<GameObject> objects,
            Vector3 position,
            float radius,
            string name = "Kimodo Root2D Keyframe",
            float height = .045f)
        {
            GameObject marker = MoveToAnalysisSessionRoot(GameObject.CreatePrimitive(PrimitiveType.Cylinder));
            marker.name = name;
            marker.hideFlags = HideFlags.HideAndDontSave;
            SetLayerRecursively(marker, SessionCaptureLayer);
            marker.transform.position = position + Vector3.up * height;
            marker.transform.localScale = new Vector3(radius, .04f, radius);
            objects.Add(marker);
        }

        private static void CreateHeadingArrow(List<GameObject> objects, Vector3 origin, Vector3 forward, float length, Color color)
        {
            Vector3 flat = KimodoMotionMath.FromPlanar(
                KimodoMotionMath.ToPlanarHeadingVector(forward), 0f);
            if (flat.sqrMagnitude < .0001f) flat = Vector3.forward;
            flat.Normalize();
            Vector3 tip = origin + flat * length;
            CreateWorldLine(objects, origin + Vector3.up * .07f, tip + Vector3.up * .07f, .035f, color);
            Quaternion left = Quaternion.AngleAxis(150f, Vector3.up);
            Quaternion right = Quaternion.AngleAxis(-150f, Vector3.up);
            CreateWorldLine(objects, tip + Vector3.up * .07f, tip + left * flat * .16f + Vector3.up * .07f, .035f, color);
            CreateWorldLine(objects, tip + Vector3.up * .07f, tip + right * flat * .16f + Vector3.up * .07f, .035f, color);
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

        private static AnalysisPosePlan BuildAnalysisPosePlan(SubjectPictureData subject, IReadOnlyList<int> frames)
        {
            EvaluatedPosePreview source = CreatePipelinePosePreview(subject.Subject.Character, subject.GetSample(0));
            var snapshots = new Dictionary<int, AnalysisPoseSnapshot>();
            try
            {
                // The retarget preview factory owns material copies; analysis poses use the source references.
                Transform originalRoot = subject.Subject.Character.Animator.transform;
                foreach (Renderer original in originalRoot.GetComponentsInChildren<Renderer>(true))
                {
                    string path = AnimationUtility.CalculateTransformPath(original.transform, originalRoot);
                    Transform target = string.IsNullOrEmpty(path) ? source.Root.transform : source.Root.transform.Find(path);
                    Renderer renderer = target != null ? target.GetComponent(original.GetType()) as Renderer : null;
                    if (renderer != null) renderer.sharedMaterials = original.sharedMaterials;
                }
                foreach (int frame in frames.Distinct().OrderBy(item => item))
                {
                    KimodoMarkerSampleResult sample = subject.GetSample(frame);
                    source.Apply(sample);
                    snapshots[frame] = AnalysisPoseSnapshot.Capture(source.Root);
                }
                // The source is only a transform snapshot template. Keep it
                // out of the capture layer so each pose is rendered exactly
                // once by its dedicated virtual-pose instance.
                SetPreviewRenderersEnabled(source.Root, false);
                return new AnalysisPosePlan(source, snapshots);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        private static AnalysisPreviewPose CreatePooledOriginalMaterialPose(AnalysisPoseSnapshot snapshot)
        {
            GameObject preview = MoveToAnalysisSessionRoot(UnityEngine.Object.Instantiate(snapshot.SourcePrefab));
            preview.name = "Kimodo Test Virtual Pose";
            preview.hideFlags = HideFlags.HideAndDontSave;
            foreach (Animator animator in preview.GetComponentsInChildren<Animator>(true))
            {
                // Keep the component (disabled) so bone lookups such as
                // GetBoneTransform(HumanBodyBones.Hips) still resolve for
                // downstream anchoring; a destroyed Animator loses them.
                animator.enabled = false;
            }
            snapshot.Apply(preview);
            SetPreviewRenderersEnabled(preview, false);
            return new AnalysisPreviewPose(preview);
        }

        private static void SetPreviewRenderersEnabled(GameObject preview, bool enabled)
        {
            if (preview == null) return;
            foreach (Renderer renderer in preview.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    skinned.updateWhenOffscreen = true;
                }
                renderer.enabled = enabled;
            }
        }


        private static Bounds CalculateRawPreviewPoseBounds(SubjectPictureData subject, int localFrame)
        {
            // Bounds were sampled with these same poses before layout began.
            return subject.PoseBounds[Mathf.Clamp(localFrame, 0, subject.PoseBounds.Length - 1)];
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
            }
            return preview;
        }

        private static EvaluatedPosePreview CreatePipelinePosePreview(
            TimelineCharacterRecord character,
            KimodoMarkerSampleResult sample)
        {
            string error = string.Empty;
            if (character?.Animator == null ||
                !KimodoConstraintPoseRigFactory.TryCreatePoseRig(
                    ResolveModelName(null),
                    character.Animator,
                    character.Avatar,
                    out KimodoConstraintPoseRigFactory.PoseRigInstance poseRig,
                    out error))
            {
                throw new InvalidOperationException($"Character '{character?.Name}' preview rig creation failed: {error}");
            }

            if (!KimodoConstraintPoseRigFactory.TryApplyPose(poseRig, sample, ResolveModelName(null), out error))
            {
                KimodoConstraintPoseRigFactory.DisposePoseRig(poseRig);
                throw new InvalidOperationException($"Character '{character.Name}' preview pose evaluation failed: {error}");
            }

            GameObject preview = MoveToAnalysisSessionRoot(poseRig.Root);
            preview.name = "Kimodo Pose Preview";
            preview.hideFlags = HideFlags.HideAndDontSave;
            foreach (Transform transform in preview.GetComponentsInChildren<Transform>(true))
            {
                transform.gameObject.layer = SessionCaptureLayer;
            }

            return new EvaluatedPosePreview(preview, poseRig, ResolveModelName(null));
        }

        private static List<int> BuildAnalysisSampleFrames(
            SubjectPictureData subject,
            IEnumerable<int> primaryFrames,
            bool preserveAllSelectedFrames)
        {
            int lastFrame = Math.Max(0, subject.Pelvis.Length - 1);
            var events = (primaryFrames ?? Enumerable.Empty<int>())
                .Select(frame => Mathf.Clamp(frame, 0, lastFrame))
                .Append(0)
                .Append(lastFrame)
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();

            List<int> result = InsertIntermediateFrames(events);
            var protectedFrames = new HashSet<int>((primaryFrames ?? Enumerable.Empty<int>())
                .Select(frame => Mathf.Clamp(frame, 0, lastFrame)))
            {
                0,
                lastFrame
            };
            return preserveAllSelectedFrames
                ? FilterOverlappingFrames(subject.Pelvis, result)
                : FilterStationaryFrames(subject, result, protectedFrames);
        }

        private static List<int> InsertIntermediateFrames(IReadOnlyList<int> events)
        {
            var result = new List<int>();
            if (events == null || events.Count == 0) return result;
            result.Add(events[0]);
            for (int index = 1; index < events.Count; index++)
            {
                int from = events[index - 1];
                int to = events[index];
                int gap = to - from;
                int divisions = gap > 20 ? Mathf.CeilToInt(gap / 20f) : 1;
                for (int part = 1; part < divisions; part++) result.Add(from + Mathf.RoundToInt(gap * part / (float)divisions));
                result.Add(to);
            }
            return result;
        }

        private static List<int> FilterOverlappingFrames(Vector3[] pelvis, IReadOnlyList<int> frames)
        {
            var result = new List<int>();
            foreach (int frame in (frames ?? Array.Empty<int>()).Distinct().OrderBy(item => item))
            {
                Vector3 candidate = pelvis[frame];
                candidate.y = 0f;
                bool overlaps = result.Any(previousFrame =>
                {
                    Vector3 previous = pelvis[previousFrame];
                    previous.y = 0f;
                    return Vector3.Distance(previous, candidate) < MinimumGhostGroundSpacing;
                });
                if (!overlaps) result.Add(frame);
            }
            return result;
        }

        private static List<int> FilterStationaryFrames(
            SubjectPictureData subject,
            IReadOnlyList<int> frames,
            ISet<int> protectedFrames)
        {
            var ordered = (frames ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(frame => frame)
                .ToList();
            var removed = new HashSet<int>();
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
                    index = runEnd;
                }
                else
                {
                    index++;
                }
            }

            return ordered.Where(frame => !removed.Contains(frame)).ToList();
        }

        private static bool TryGetContactTint(SubjectPictureData subject, int frame, out Color tint)
        {
            bool left = false;
            bool right = false;
            foreach (JObject item in (subject.Subject.Record.Analysis?["foot_contacts"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if (!IsFootLandingEvent(item)) continue;
                int eventFrame = Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0, Math.Max(0, subject.Pelvis.Length - 1));
                if (eventFrame != frame) continue;
                string foot = item.Value<string>("foot") ?? string.Empty;
                left |= foot.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0;
                right |= foot.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            // When both feet share one frame, keep the event visibly distinct
            // from the left-foot blue marker by giving the right-foot red
            // marker precedence.
            if (right)
            {
                tint = new Color(1f, .2f, .2f);
                return true;
            }
            tint = left ? new Color(.2f, .45f, 1f) : Color.white;
            return left;
        }

        private static Color ResolveAnalysisPoseTint(PictureTile tile, int frame)
        {
            int lastFrame = Math.Max(0, tile.Subject.Pelvis.Length - 1);
            if (frame == 0) return AnalysisStartFrameTint;
            if (frame == lastFrame) return AnalysisEndFrameTint;
            if (string.Equals(tile.PoseKind, "key_pose", StringComparison.Ordinal)) return AnalysisKeyframeTint;
            if (string.Equals(tile.PoseKind, "step_pose", StringComparison.Ordinal) &&
                TryGetContactTint(tile.Subject, frame, out Color footTint))
            {
                return footTint;
            }
            return Color.white;
        }

    }
}
