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
                var keyframes = new HashSet<int>(SelectKeyFrames(subject, AnalysisKeyframeCount));
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
            foreach (Renderer renderer in preview.GetComponentsInChildren<Renderer>(true))
            {
                Material[] sourceMaterials = renderer.sharedMaterials;
                if (sourceMaterials == null)
                {
                    continue;
                }

                Material[] replacements = null;
                for (int index = 0; index < sourceMaterials.Length; index++)
                {
                    Material material = sourceMaterials[index];
                    if (material == null) continue;

                    Shader fallbackShader = ResolvePoseFallbackShader(material.shader);
                    if (fallbackShader != null)
                    {
                        replacements ??= (Material[])sourceMaterials.Clone();
                        Material replacement = new Material(fallbackShader)
                        {
                            hideFlags = HideFlags.HideAndDontSave
                        };
                        CopyPoseMaterialProperties(material, replacement);
                        replacements[index] = replacement;
                        transientMaterials?.Add(replacement);
                    }
                }
                if (replacements != null)
                {
                    renderer.sharedMaterials = replacements;
                }

                Material[] materials = renderer.sharedMaterials;
                for (int index = 0; index < materials.Length; index++)
                {
                    Material material = materials[index];
                    if (material == null) continue;
                    MaterialPropertyBlock block = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(block, index);
                    Color source = material.HasProperty("_BaseColor")
                        ? material.GetColor("_BaseColor")
                        : material.HasProperty("_Color")
                            ? material.GetColor("_Color")
                            : material.HasProperty("_TintColor")
                                ? material.GetColor("_TintColor")
                                : Color.white;
                    Color blended = Color.Lerp(source, tint, .9f);
                    if (material.HasProperty("_BaseColor")) block.SetColor("_BaseColor", blended);
                    else if (material.HasProperty("_Color")) block.SetColor("_Color", blended);
                    else if (material.HasProperty("_TintColor")) block.SetColor("_TintColor", blended);
                    else continue;
                    renderer.SetPropertyBlock(block, index);
                }
            }
        }

        private static Shader ResolvePoseFallbackShader(Shader sourceShader)
        {
            if (sourceShader == null) return null;
            string sourceName = sourceShader.name ?? string.Empty;
            bool isStandard = string.Equals(sourceName, "Standard", StringComparison.Ordinal) ||
                string.Equals(sourceName, "Standard (Specular setup)", StringComparison.Ordinal);
            bool isUrpLit = string.Equals(sourceName, "Universal Render Pipeline/Lit", StringComparison.Ordinal);
            bool isHdrpLit = string.Equals(sourceName, "HDRP/Lit", StringComparison.Ordinal);

            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            string pipelineName = pipeline?.GetType().FullName ?? string.Empty;
            bool isHdrp = pipelineName.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ||
                pipelineName.IndexOf("HDRenderPipeline", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isUrp = pipelineName.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0;

            string targetName = null;
            if (isHdrp && (isStandard || isUrpLit)) targetName = "HDRP/Lit";
            else if (isUrp && isStandard) targetName = "Universal Render Pipeline/Lit";
            else if (!isHdrp && !isUrp && (isHdrpLit || isUrpLit)) targetName = "Standard";
            if (targetName == null) return null;

            Shader target = Shader.Find(targetName);
            return target != null && target.isSupported ? target : null;
        }

        private static void CopyPoseMaterialProperties(Material source, Material target)
        {
            if (source == null || target == null) return;
            Texture texture = null;
            if (source.HasProperty("_BaseColorMap")) texture = source.GetTexture("_BaseColorMap");
            if (texture == null && source.HasProperty("_BaseMap")) texture = source.GetTexture("_BaseMap");
            if (texture == null && source.HasProperty("_MainTex")) texture = source.GetTexture("_MainTex");
            if (texture != null)
            {
                if (target.HasProperty("_BaseColorMap")) target.SetTexture("_BaseColorMap", texture);
                if (target.HasProperty("_BaseMap")) target.SetTexture("_BaseMap", texture);
                if (target.HasProperty("_MainTex")) target.SetTexture("_MainTex", texture);
            }

            Color color = source.HasProperty("_BaseColor")
                ? source.GetColor("_BaseColor")
                : source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
            if (target.HasProperty("_BaseColor")) target.SetColor("_BaseColor", color);
            if (target.HasProperty("_Color")) target.SetColor("_Color", color);
            if (source.HasProperty("_Cutoff") && target.HasProperty("_Cutoff"))
            {
                target.SetFloat("_Cutoff", source.GetFloat("_Cutoff"));
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

            public TestVirtualPose(
                EvaluatedPosePreview preview,
                IReadOnlyList<Material> transientMaterials,
                float alpha,
                bool usesGhostMaterial)
            {
                EvaluatedPreview = preview;
                Preview = preview?.Root;
                TransientMaterials = transientMaterials;
                Alpha = alpha;
                UsesGhostMaterial = usesGhostMaterial;
            }

            public GameObject Preview { get; }
            private EvaluatedPosePreview EvaluatedPreview { get; }
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
