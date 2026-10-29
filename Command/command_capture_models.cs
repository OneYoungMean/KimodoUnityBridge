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
                TimelineCharacterRecord character,
                TimelineAnimationRecord animation,
                AnalysisCacheRecord record,
                int startFrame,
                int endFrameExclusive)
            {
                Character = character;
                Animation = animation;
                Record = record;
                StartFrame = startFrame;
                EndFrameExclusive = endFrameExclusive;
            }
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
                Vector3[] head,
                float[] leftArmSwingAngles,
                float[] rightArmSwingAngles,
                bool[] leftContacts,
                bool[] rightContacts,
                Bounds[] poseBounds,
                Bounds bounds)
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
                Head = head;
                LeftArmSwingAngles = leftArmSwingAngles;
                RightArmSwingAngles = rightArmSwingAngles;
                LeftContacts = leftContacts;
                RightContacts = rightContacts;
                PoseBounds = poseBounds;
                Bounds = bounds;
                KeyFrameSet = new HashSet<int>((subject.Record.Analysis?["keyframes"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Select(item => Mathf.Clamp(
                        (item.Value<int?>("timeline_frame_60") ?? subject.StartFrame) - subject.StartFrame,
                        0,
                        Math.Max(0, pelvis.Length - 1))));
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
            public Vector3[] Head { get; }
            public float[] LeftArmSwingAngles { get; }
            public float[] RightArmSwingAngles { get; }
            public bool[] LeftContacts { get; }
            public bool[] RightContacts { get; }
            public Bounds[] PoseBounds { get; }
            public Bounds Bounds { get; }
            public HashSet<int> KeyFrameSet { get; }

            public void RefreshKeyframes()
            {
                JArray keyframes = Subject.Record.Analysis?["keyframes"] as JArray ?? new JArray();
                KeyFrameSet.Clear();
                foreach (JObject item in keyframes.OfType<JObject>())
                {
                    KeyFrameSet.Add(Mathf.Clamp(
                        (item.Value<int?>("timeline_frame_60") ?? Subject.StartFrame) - Subject.StartFrame,
                        0,
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
            public int Frame { get; private set; }
            public string PoseKind { get; private set; }
            public List<int> TrajectoryFrames { get; private set; } = new List<int>();
            public HashSet<int> PrimaryFrames { get; private set; } = new HashSet<int>();
            public bool IsEmpty { get; private set; }
            public string TileType { get; private set; }

            public static PictureTile RootTrajectory(SubjectPictureData subject, Vector3 direction)
            {
                var keyframes = subject.KeyFrameSet;
                List<int> frames = BuildAnalysisSampleFrames(
                    subject,
                    keyframes,
                    false);
                return new PictureTile(subject, "root_trajectory", new JObject
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
                    TrajectoryFrames = frames,
                    PrimaryFrames = keyframes
                };
            }

            public static PictureTile Overview(SubjectPictureData subject, string type, Vector3 direction)
            {
                // The 3d_track slot is rendered as the Root2D pelvis projection
                // by SceneAnalysisLayout, so it must describe itself as that
                // tile rather than as a plain overview.
                PictureTile root2D = type == "3d_track" ? RootTrajectory(subject, direction) : null;
                var description = root2D != null
                    ? root2D.Description
                    : new JObject
                    {
                        ["presentation"] = type,
                        ["frames"] = type == "height_time_track"
                            ? new JArray(BuildHeightTimePoseFrames(subject))
                            : new JArray()
                    };
                return new PictureTile(subject, "overview_" + type, description)
                {
                    Direction = direction,
                    TileType = type,
                    TrajectoryFrames = root2D?.TrajectoryFrames ?? new List<int>(),
                    PrimaryFrames = root2D?.PrimaryFrames ?? new HashSet<int>()
                };
            }

            public static PictureTile SelectedOverview(
                SubjectPictureData subject,
                string type,
                IEnumerable<int> frames,
                Vector3 direction)
            {
                var ordered = (frames ?? Enumerable.Empty<int>())
                    .Distinct().OrderBy(frame => frame).ToList();
                HashSet<int> primary = new HashSet<int>(ordered);
                List<int> sampled = BuildAnalysisSampleFrames(subject, ordered, true);
                return new PictureTile(subject, "selected_" + type, new JObject
                {
                    ["presentation"] = type,
                    ["frames"] = new JArray(sampled),
                    ["candidate_frames"] = new JArray(ordered),
                    ["sample_frames"] = new JArray(sampled.Where(frame => !primary.Contains(frame)))
                })
                {
                    Direction = direction,
                    TileType = type,
                    TrajectoryFrames = sampled,
                    PrimaryFrames = primary
                };
            }

            public static PictureTile PoseSlot(SubjectPictureData subject, int? frame, string row, int slot)
            {
                if (!frame.HasValue)
                {
                    return new PictureTile(subject, "empty_pose", new JObject
                    {
                        ["presentation"] = "empty_pose",
                        ["row"] = row,
                        ["slot"] = slot
                    }) { IsEmpty = true, TileType = row };
                }

                int clamped = Mathf.Clamp(frame.Value, 0, Math.Max(0, subject.Pelvis.Length - 1));
                return new PictureTile(subject, "pose", new JObject
                {
                    ["presentation"] = row == "key_pose" ? "key_pose" : "step_pose",
                    ["frame"] = clamped,
                    ["row"] = row,
                    ["slot"] = slot
                })
                {
                    Direction = new Vector3(1f, .75f, -1f),
                    Frame = clamped,
                    PoseKind = row,
                    TileType = row
                };
            }
        }

        private sealed class AnalysisPreviewPose
        {
            public AnalysisPreviewPose(GameObject preview) => Preview = preview;

            public GameObject Preview { get; }

            public void Dispose()
            {
                if (Preview != null) UnityEngine.Object.DestroyImmediate(Preview);
            }
        }

        /// <summary>
        /// Owns every transient pose object used by one 20-tile capture composite.
        /// The renderer only enables one of these objects (or a selected subset)
        /// for each tile; it never instantiates the same frame twice.
        /// </summary>
        private sealed class CapturePosePool : IDisposable
        {
            private readonly Dictionary<SubjectPictureData, AnalysisPosePlan> plans =
                new Dictionary<SubjectPictureData, AnalysisPosePlan>();
            private readonly Dictionary<SubjectPictureData, Dictionary<int, AnalysisPreviewPose>> poses =
                new Dictionary<SubjectPictureData, Dictionary<int, AnalysisPreviewPose>>();

            public CapturePosePool(IReadOnlyList<PictureTile> tiles)
            {
                var framesBySubject = new Dictionary<SubjectPictureData, HashSet<int>>();
                foreach (PictureTile tile in tiles ?? Array.Empty<PictureTile>())
                {
                    if (tile?.Subject == null) continue;
                    if (!framesBySubject.TryGetValue(tile.Subject, out HashSet<int> frames))
                    {
                        frames = new HashSet<int>();
                        framesBySubject[tile.Subject] = frames;
                    }

                    int last = Math.Max(0, tile.Subject.Pelvis.Length - 1);
                    frames.Add(0);
                    frames.Add(last);
                    foreach (int frame in tile.TrajectoryFrames ?? new List<int>()) frames.Add(Mathf.Clamp(frame, 0, last));
                    if (tile.Presentation == "pose") frames.Add(Mathf.Clamp(tile.Frame, 0, last));
                    if (tile.TileType == "height_time_track")
                    {
                        foreach (int frame in BuildHeightTimePoseFrames(tile.Subject)) frames.Add(Mathf.Clamp(frame, 0, last));
                    }
                }

                foreach (KeyValuePair<SubjectPictureData, HashSet<int>> item in framesBySubject)
                {
                    List<int> frames = item.Value.OrderBy(frame => frame).ToList();
                    AnalysisPosePlan plan = BuildAnalysisPosePlan(item.Key, frames);
                    plans[item.Key] = plan;
                    var subjectPoses = new Dictionary<int, AnalysisPreviewPose>();
                    foreach (int frame in frames)
                    {
                        // Reuse the source material and color for every pooled pose.
                        subjectPoses[frame] = CreatePooledOriginalMaterialPose(plan.Get(frame));
                    }
                    poses[item.Key] = subjectPoses;
                }
            }

            public AnalysisPosePlan GetPlan(SubjectPictureData subject)
            {
                return subject != null && plans.TryGetValue(subject, out AnalysisPosePlan plan) ? plan : null;
            }

            public AnalysisPreviewPose Get(SubjectPictureData subject, int frame)
            {
                if (subject == null || !poses.TryGetValue(subject, out Dictionary<int, AnalysisPreviewPose> subjectPoses) ||
                    !subjectPoses.TryGetValue(frame, out AnalysisPreviewPose pose))
                {
                    throw new InvalidOperationException($"Analysis pose pool is missing frame {frame}.");
                }
                return pose;
            }

            public void Dispose()
            {
                foreach (Dictionary<int, AnalysisPreviewPose> subjectPoses in poses.Values)
                    foreach (AnalysisPreviewPose pose in subjectPoses.Values)
                        pose?.Dispose();
                foreach (AnalysisPosePlan plan in plans.Values) plan?.Dispose();
                poses.Clear();
                plans.Clear();
            }
        }

        private sealed class AnalysisPosePlan : IDisposable
        {
            private readonly EvaluatedPosePreview source;
            private readonly Dictionary<int, AnalysisPoseSnapshot> snapshots;

            public AnalysisPosePlan(
                EvaluatedPosePreview source,
                Dictionary<int, AnalysisPoseSnapshot> snapshots)
            {
                this.source = source;
                this.snapshots = snapshots;
            }

            public AnalysisPoseSnapshot Get(int frame)
            {
                if (snapshots.TryGetValue(frame, out AnalysisPoseSnapshot snapshot)) return snapshot;
                return snapshots.Values.First();
            }

            public void Dispose()
            {
                source?.Dispose();
            }
        }

        private sealed class AnalysisPoseSnapshot
        {
            private readonly AnalysisTransformSnapshot[] transforms;

            private AnalysisPoseSnapshot(
                GameObject sourcePrefab,
                Vector3 rootPosition,
                Quaternion rootRotation,
                Vector3 rootScale,
                AnalysisTransformSnapshot[] transforms)
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

            public static AnalysisPoseSnapshot Capture(GameObject source)
            {
                Transform root = source.transform;
                Transform[] all = source.GetComponentsInChildren<Transform>(true);
                var values = new AnalysisTransformSnapshot[all.Length];
                for (int index = 0; index < all.Length; index++)
                {
                    Transform transform = all[index];
                    values[index] = new AnalysisTransformSnapshot(
                        GetTransformPath(root, transform),
                        transform.localPosition,
                        transform.localRotation,
                        transform.localScale);
                }
                return new AnalysisPoseSnapshot(
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
                foreach (AnalysisTransformSnapshot value in transforms)
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

        private readonly struct AnalysisTransformSnapshot
        {
            public AnalysisTransformSnapshot(
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
                Analysis = json["analysis"] as JObject ?? new JObject(),
                Pictures = json["pictures"] as JObject ?? new JObject(),
                RootTrajectory = json["root_trajectory"] as JObject ?? new JObject()
            };
        }
    }
}
