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
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace KimodoUnityBridge.Command

{
    internal static partial class command_context
    {
        private sealed class EvaluatedPosePreview : IDisposable
        {
            private KimodoConstraintPoseRigFactory.PoseRigInstance poseRig;

            public EvaluatedPosePreview(
                GameObject root,
                KimodoConstraintPoseRigFactory.PoseRigInstance poseRig = null,
                string modelName = null)
            {
                Root = root;
                this.poseRig = poseRig;
                ModelName = modelName;
            }

            public GameObject Root { get; }
            public Animator Animator => poseRig?.TargetCache?.animator ?? Root?.GetComponentInChildren<Animator>(true);
            private string ModelName { get; }

            public void Apply(KimodoMarkerSampleResult sample)
            {
                if (poseRig == null) throw new InvalidOperationException("Pose preview is not pipeline-backed.");
                if (!KimodoConstraintPoseRigFactory.TryApplyPose(poseRig, sample, ModelName, out string error))
                {
                    throw new InvalidOperationException($"Preview pose evaluation failed: {error}");
                }
            }

            public void Dispose()
            {
                if (poseRig != null)
                {
                    KimodoConstraintPoseRigFactory.DisposePoseRig(poseRig);
                    poseRig = null;
                }
                else if (Root != null)
                {
                    UnityEngine.Object.DestroyImmediate(Root);
                }
            }
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
                KeyframeCount = Mathf.Max(1,
                    (subject.Record.Analysis?["keyframes"] as JArray)?.Count ?? 1);
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
            public int KeyframeCount { get; private set; }
            public HashSet<int> KeyFrameSet { get; }

            public void RefreshKeyframes()
            {
                JArray keyframes = Subject.Record.Analysis?["keyframes"] as JArray ?? new JArray();
                KeyframeCount = Mathf.Max(1, keyframes.Count);
                KeyFrameSet.Clear();
                foreach (JObject item in keyframes.OfType<JObject>())
                {
                    KeyFrameSet.Add(Mathf.Clamp(item.Value<int?>("frame") ?? 0, 0,
                        Math.Max(0, Pelvis.Length - 1)));
                }
            }

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
            public bool IsEmpty { get; private set; }
            public string TestTileType { get; private set; }

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
                return TestFrameSet(subject, "test_keyframes", "keyframes", SelectKeyFrames(subject, subject.KeyframeCount), direction, true);
            }

            public static PictureTile TestRoot2D(SubjectPictureData subject, Vector3 direction)
            {
                var keyframes = new HashSet<int>(SelectKeyFrames(subject, subject.KeyframeCount));
                List<int> frames = BuildTestSampleFrames(
                    subject,
                    keyframes,
                    false,
                    out HashSet<int> stationaryBoostFrames);
                return new PictureTile(subject, "test_root2d", new JObject
                {
                    ["presentation"] = "root2d_pelvis_projection",
                    ["keyframes"] = new JArray(keyframes.OrderBy(frame => frame)),
                    ["primary_frames"] = new JArray(keyframes.OrderBy(frame => frame)),
                    ["frames"] = new JArray(frames),
                    ["sample_frames"] = new JArray(frames.Where(frame => !keyframes.Contains(frame))),
                    ["pelvis_only"] = true,
                    ["heading_arrows"] = true
                })
                {
                    Direction = direction,
                    Orthographic = true,
                    TrajectoryFrames = frames,
                    PrimaryFrames = keyframes,
                    StationaryBoostFrames = stationaryBoostFrames
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

            public static PictureTile TestOverview(SubjectPictureData subject, string type, Vector3 direction)
            {
                // The 3d_track slot is rendered as the Root2D pelvis projection
                // (see RenderPictureTile), so it must describe itself as that
                // tile rather than as a plain overview.
                PictureTile root2D = type == "3d_track" ? TestRoot2D(subject, direction) : null;
                var description = root2D != null
                    ? root2D.Description
                    : new JObject
                    {
                        ["presentation"] = type,
                        ["frames"] = type == "height_time_track"
                            ? new JArray(subject.KeyFrameSet.Append(0).Append(Math.Max(0, subject.Pelvis.Length - 1)).Distinct().OrderBy(frame => frame))
                            : new JArray(),
                        ["test"] = true
                    };
                return new PictureTile(subject, "test_overview_" + type, description)
                {
                    Direction = direction,
                    Orthographic = true,
                    TestTileType = type,
                    ShowTestTrajectories = type == "3d_ghost_track",
                    TrajectoryFrames = root2D?.TrajectoryFrames ?? new List<int>(),
                    PrimaryFrames = root2D?.PrimaryFrames ?? new HashSet<int>(),
                    StationaryBoostFrames = root2D?.StationaryBoostFrames ?? new HashSet<int>()
                };
            }

            public static PictureTile TestEmptyOverview(SubjectPictureData subject, string slot)
            {
                return new PictureTile(subject, "test_empty_overview", new JObject
                {
                    ["presentation"] = "empty_overview",
                    ["slot"] = slot,
                    ["test"] = true
                }) { IsEmpty = true, TestTileType = "overview" };
            }

            public static PictureTile TestSelectedOverview(
                SubjectPictureData subject,
                string type,
                IEnumerable<int> frames,
                Vector3 direction)
            {
                var ordered = (frames ?? Enumerable.Empty<int>())
                    .Distinct().OrderBy(frame => frame).ToList();
                HashSet<int> primary = new HashSet<int>(ordered);
                HashSet<int> promoted;
                List<int> sampled = BuildTestSampleFrames(subject, ordered, true, out promoted);
                return new PictureTile(subject, "test_selected_" + type, new JObject
                {
                    ["presentation"] = type,
                    ["frames"] = new JArray(ordered),
                    ["sample_frames"] = new JArray(sampled.Where(frame => !primary.Contains(frame))),
                    ["test"] = true
                })
                {
                    Direction = direction,
                    Orthographic = true,
                    TestTileType = type,
                    TrajectoryFrames = sampled,
                    PrimaryFrames = primary,
                    StationaryBoostFrames = promoted,
                    ShowTestTrajectories = type == "3d_ghost_track"
                };
            }

            public static PictureTile TestPoseSlot(SubjectPictureData subject, int? frame, string row, int slot)
            {
                if (!frame.HasValue)
                {
                    return new PictureTile(subject, "test_empty_pose", new JObject
                    {
                        ["presentation"] = "empty_pose",
                        ["row"] = row,
                        ["slot"] = slot,
                        ["test"] = true
                    }) { IsEmpty = true, TestTileType = row };
                }

                int clamped = Mathf.Clamp(frame.Value, 0, Math.Max(0, subject.Pelvis.Length - 1));
                return new PictureTile(subject, "test_pose", new JObject
                {
                    ["presentation"] = row == "key_pose" ? "key_pose" : "step_pose",
                    ["frame"] = clamped,
                    ["row"] = row,
                    ["slot"] = slot,
                    ["test"] = true
                })
                {
                    Direction = new Vector3(1f, .75f, -1f),
                    Orthographic = true,
                    Frame = clamped,
                    PoseKind = row,
                    TestTileType = row
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

        private static Material ClonePoseMaterial(Material source, Color tint, bool applyTint)
        {
            Shader fallback = FindAnalysisShader(
                "HDRP/Unlit",
                "Universal Render Pipeline/Unlit",
                "Unlit/Color");
            if (fallback == null)
            {
                throw new InvalidOperationException("No default character material shader is available.");
            }

            // Analysis poses are evidence layers, not scene lighting previews.
            // Always use the active pipeline's Unlit shader so HDRP and URP
            // produce the same flat, role-coded colors regardless of scene
            // lights or the source model's original material.
            var material = new Material(fallback);
            material.hideFlags = HideFlags.HideAndDontSave;
            material.name = "Kimodo Analysis Pose (Unlit)";
            Color evidenceColor = applyTint ? tint : Color.white;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", evidenceColor);
            if (material.HasProperty("_Color")) material.SetColor("_Color", evidenceColor);
            if (material.HasProperty("_TintColor")) material.SetColor("_TintColor", evidenceColor);
            // HDRP/Unlit exposes its actual albedo as _UnlitColor. Keep this
            // write last; Material.color targets the legacy _Color alias and
            // can reset the HDRP property back to white.
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", evidenceColor);
            return material;
        }

        private static Texture GetPoseTexture(Material material, string propertyName) =>
            material != null && material.HasProperty(propertyName) ? material.GetTexture(propertyName) : null;

        private static Vector2 GetPoseTextureScale(Material material, string primary, string fallback)
        {
            string property = material != null && material.HasProperty(primary) ? primary : fallback;
            return material != null && material.HasProperty(property) ? material.GetTextureScale(property) : Vector2.one;
        }

        private static Vector2 GetPoseTextureOffset(Material material, string primary, string fallback)
        {
            string property = material != null && material.HasProperty(primary) ? primary : fallback;
            return material != null && material.HasProperty(property) ? material.GetTextureOffset(property) : Vector2.zero;
        }

        private static Color GetPoseColor(Material material, string primary, string fallback)
        {
            if (material != null && material.HasProperty(primary)) return material.GetColor(primary);
            if (material != null && material.HasProperty(fallback)) return material.GetColor(fallback);
            return Color.white;
        }

        private static bool IsPoseMaterialCompatible(Shader shader)
        {
            if (shader == null) return false;
            string name = shader.name ?? string.Empty;
            switch (GetCapturePipeline())
            {
                case CapturePipeline.Hdrp:
                    return name.IndexOf("HDRP/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("High Definition Render Pipeline", StringComparison.OrdinalIgnoreCase) >= 0;
                case CapturePipeline.Urp:
                    return name.IndexOf("Universal Render Pipeline/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.StartsWith("Sprites/", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("Unlit/", StringComparison.OrdinalIgnoreCase);
                default:
                    return name.IndexOf("HDRP/", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("Universal Render Pipeline/", StringComparison.OrdinalIgnoreCase) < 0;
            }
        }

        private static void CopyPoseMaterialProperties(Material source, Material target)
        {
            CopyPoseTexture(source, target, "_BaseColorMap", "_BaseMap");
            CopyPoseTexture(source, target, "_BaseColorMap", "_MainTex");
            CopyPoseTexture(source, target, "_MainTex", "_BaseMap");
            CopyPoseTexture(source, target, "_MainTex", "_MainTex");
            CopyPoseTexture(source, target, "_EmissiveColorMap", "_EmissionMap");
            CopyPoseTexture(source, target, "_EmissiveColorMap", "_EmissionColorMap");

            Color color = Color.white;
            if (source.HasProperty("_BaseColor")) color = source.GetColor("_BaseColor");
            else if (source.HasProperty("_Color")) color = source.GetColor("_Color");
            if (target.HasProperty("_BaseColor")) target.SetColor("_BaseColor", color);
            if (target.HasProperty("_Color")) target.SetColor("_Color", color);

            Color emission = Color.black;
            if (source.HasProperty("_EmissiveColor")) emission = source.GetColor("_EmissiveColor");
            else if (source.HasProperty("_EmissionColor")) emission = source.GetColor("_EmissionColor");
            if (target.HasProperty("_EmissionColor")) target.SetColor("_EmissionColor", emission);
        }

        private static void CopyPoseTexture(Material source, Material target, string sourceName, string targetName)
        {
            if (!source.HasProperty(sourceName) || !target.HasProperty(targetName)) return;
            Texture texture = source.GetTexture(sourceName);
            if (texture == null) return;
            target.SetTexture(targetName, texture);
            target.SetTextureScale(targetName, source.GetTextureScale(sourceName));
            target.SetTextureOffset(targetName, source.GetTextureOffset(sourceName));
        }

        private static void ApplyPoseMaterials(
            GameObject preview,
            Color tint,
            bool applyTint,
            List<Material> transientMaterials,
            List<Tuple<Renderer, Material[]>> originalMaterials = null)
        {
            if (preview == null) return;
            foreach (Renderer renderer in preview.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                Material[] sourceMaterials = renderer.sharedMaterials;
                if (sourceMaterials == null || sourceMaterials.Length == 0) sourceMaterials = new[] { (Material)null };
                originalMaterials?.Add(Tuple.Create(renderer, sourceMaterials));
                var replacements = new Material[sourceMaterials.Length];
                for (int index = 0; index < sourceMaterials.Length; index++)
                {
                    Material replacement = ClonePoseMaterial(sourceMaterials[index], tint, applyTint);
                    replacements[index] = replacement;
                    transientMaterials?.Add(replacement);
                }
                renderer.sharedMaterials = replacements;
            }
        }

        private static void RestoreAnalysisMaterials(List<Tuple<Renderer, Material[]>> originalMaterials)
        {
            if (originalMaterials == null) return;
            foreach (Tuple<Renderer, Material[]> item in originalMaterials)
            {
                if (item.Item1 != null) item.Item1.sharedMaterials = item.Item2;
            }
            originalMaterials.Clear();
        }

        private sealed class TestVirtualPose
        {
            public TestVirtualPose(
                GameObject preview,
                IReadOnlyList<Material> transientMaterials,
                float alpha)
                : this(preview, transientMaterials, alpha, Vector3.zero, false)
            {
            }

            public TestVirtualPose(
                GameObject preview,
                IReadOnlyList<Material> transientMaterials,
                float alpha,
                Vector3 targetPosition)
                : this(preview, transientMaterials, alpha, targetPosition, true)
            {
            }

            private TestVirtualPose(
                GameObject preview,
                IReadOnlyList<Material> transientMaterials,
                float alpha,
                Vector3 targetPosition,
                bool hasTargetPosition)
            {
                Preview = preview;
                TransientMaterials = transientMaterials;
                Alpha = alpha;
                TargetPosition = targetPosition;
                HasTargetPosition = hasTargetPosition;
            }

            public TestVirtualPose(
                EvaluatedPosePreview preview,
                IReadOnlyList<Material> transientMaterials,
                float alpha)
                : this(preview, transientMaterials, alpha, Vector3.zero, false)
            {
            }

            public TestVirtualPose(
                EvaluatedPosePreview preview,
                IReadOnlyList<Material> transientMaterials,
                float alpha,
                Vector3 targetPosition)
                : this(preview, transientMaterials, alpha, targetPosition, true)
            {
            }

            private TestVirtualPose(
                EvaluatedPosePreview preview,
                IReadOnlyList<Material> transientMaterials,
                float alpha,
                Vector3 targetPosition,
                bool hasTargetPosition)
            {
                EvaluatedPreview = preview;
                Preview = preview?.Root;
                TransientMaterials = transientMaterials;
                Alpha = alpha;
                TargetPosition = targetPosition;
                HasTargetPosition = hasTargetPosition;
            }

            public GameObject Preview { get; }
            private EvaluatedPosePreview EvaluatedPreview { get; }
            public IReadOnlyList<Material> TransientMaterials { get; }
            public float Alpha { get; }
            public Vector3 TargetPosition { get; }
            public bool HasTargetPosition { get; }

            private readonly List<Tuple<Renderer, Material[]>> originalMaterials =
                new List<Tuple<Renderer, Material[]>>();

            public void SetOriginalMaterials(List<Tuple<Renderer, Material[]>> originals)
            {
                if (originals == null) return;
                originalMaterials.AddRange(originals);
            }

            public void Dispose()
            {
                RestoreAnalysisMaterials(originalMaterials);
                if (TransientMaterials != null)
                {
                    foreach (Material material in TransientMaterials)
                    {
                        if (material != null) UnityEngine.Object.DestroyImmediate(material);
                    }
                }
                if (EvaluatedPreview != null) EvaluatedPreview.Dispose();
                else if (Preview != null) UnityEngine.Object.DestroyImmediate(Preview);
            }
        }

        private sealed class TestPosePlan : IDisposable
        {
            private readonly EvaluatedPosePreview source;
            private readonly Dictionary<int, TestPoseSnapshot> snapshots;

            public TestPosePlan(
                EvaluatedPosePreview source,
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
                source?.Dispose();
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

        private sealed class AnalysisPictureRequest
        {
            private static readonly string[] AllTileTypes =
            {
                "3d_track", "height_time_track", "3d_ghost", "3d_ghost_track", "key_pose", "step_pose"
            };

            private AnalysisPictureRequest(IReadOnlyList<string> tileTypes, string output, string environment)
            {
                TileTypes = tileTypes;
                Output = output;
                Environment = environment;
            }

            public IReadOnlyList<string> TileTypes { get; }
            public string Output { get; }
            public string Environment { get; }
            public bool WritesComposite => Output == "composite" || Output == "both";
            public bool WritesTiles => Output == "tiles" || Output == "both";

            public bool Includes(string tileType) => TileTypes.Contains(tileType, StringComparer.Ordinal);

            public JObject ToJson()
            {
                return new JObject
                {
                    ["tiles"] = new JArray(TileTypes),
                    ["output"] = Output,
                    ["environment"] = new JObject { ["mode"] = Environment }
                };
            }

            public static AnalysisPictureRequest Parse(JObject value)
            {
                string output = (value?.Value<string>("output") ?? "composite").Trim().ToLowerInvariant();
                if (output != "composite" && output != "tiles" && output != "both")
                {
                    throw new InvalidOperationException("picture.output must be composite, tiles, or both.");
                }

                var requested = value?["tiles"] as JArray;
                var types = new List<string>();
                foreach (string raw in (requested ?? new JArray(AllTileTypes)).Values<string>())
                {
                    string tile = (raw ?? string.Empty).Trim().ToLowerInvariant();
                    if (!AllTileTypes.Contains(tile, StringComparer.Ordinal))
                    {
                        throw new InvalidOperationException($"Unsupported picture tile type '{raw}'.");
                    }
                    if (!types.Contains(tile, StringComparer.Ordinal)) types.Add(tile);
                }
                if (types.Count == 0) throw new InvalidOperationException("picture.tiles must contain at least one tile type.");
                return new AnalysisPictureRequest(types, output, ParseEnvironmentMode(value?["environment"] as JObject));
            }

            private static string ParseEnvironmentMode(JObject environment)
            {
                string mode = (environment?.Value<string>("mode") ?? "isolated").Trim().ToLowerInvariant();
                if (mode != "preserve" && mode != "isolated")
                {
                    throw new InvalidOperationException("picture.environment.mode must be preserve or isolated.");
                }
                return mode;
            }
        }

        private readonly struct TestPictureLayout
        {
            public const int TileGapPixels = 8;
            private readonly int overviewWidth;
            private readonly int overviewHeight;
            private readonly int poseWidth;
            private readonly int poseHeight;

            private TestPictureLayout(int width, int height)
            {
                CanvasWidth = width;
                CanvasHeight = height;
                HeaderHeight = Mathf.Clamp(Mathf.RoundToInt(width * 60f / 1920f), 48, 96);
                overviewWidth = Mathf.Max(1, (width - TileGapPixels * 3) / 4);
                overviewHeight = Mathf.Max(1, Mathf.RoundToInt(overviewWidth * .75f));
                poseWidth = Mathf.Max(1, (width - TileGapPixels * 7) / 8);
                poseHeight = Mathf.Max(1, (height - HeaderHeight - overviewHeight) / 2);
                RowHeights = new[] { overviewHeight, poseHeight, poseHeight };
            }

            public int CanvasWidth { get; }
            public int CanvasHeight { get; }
            public int HeaderHeight { get; }
            public IReadOnlyList<int> RowHeights { get; }

            public static TestPictureLayout ForResolution(int requestedResolution)
            {
                int width = Mathf.Max(64, requestedResolution);
                return new TestPictureLayout(width, Mathf.Max(36, Mathf.RoundToInt(width * 9f / 16f)));
            }

            public int WidthFor(PictureTile tile) => IsOverview(tile) ? overviewWidth : poseWidth;
            public int HeightFor(PictureTile tile) => IsOverview(tile) ? overviewHeight : poseHeight;

            private static bool IsOverview(PictureTile tile)
            {
                return tile != null && (tile.Presentation.StartsWith("test_overview_", StringComparison.Ordinal) ||
                    tile.Presentation.StartsWith("test_selected_", StringComparison.Ordinal));
            }
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
            public JObject RootTrajectory;

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
                ["pictures"] = Pictures?.DeepClone() ?? new JObject(),
                ["root_trajectory"] = RootTrajectory?.DeepClone() ?? new JObject()
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
                Pictures = json["pictures"] as JObject ?? new JObject(),
                RootTrajectory = json["root_trajectory"] as JObject ?? new JObject()
            };
        }
    }
}
