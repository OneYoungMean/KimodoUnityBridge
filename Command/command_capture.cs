using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CharacterAnimationCli.Unity;
using KimodoBridge;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;

namespace CharacterAnimationCli.Unity.Command
{
    internal static partial class command_context
    {
        private static readonly Dictionary<string, AnalysisCacheRecord> AnalysisCache =
            new Dictionary<string, AnalysisCacheRecord>(StringComparer.OrdinalIgnoreCase);

        public static string PictureMotionOverlay(string argumentsJson) => RenderPicture(argumentsJson, "motion_overlay");

        public static string PictureKeyPoses(string argumentsJson) => RenderPicture(argumentsJson, "key_poses");

        public static string PictureTrajectory3D(string argumentsJson) => RenderPicture(argumentsJson, "trajectory_3d");

        private static string RenderPicture(string argumentsJson, string mode)
        {
            return Execute(argumentsJson, arguments =>
            {
                TimelineSessionRecord session = RequireTimelineSession(arguments);
                JObject captureArguments = (JObject)arguments.DeepClone();
                captureArguments.Remove("session_id");
                if (mode != "trajectory_3d") captureArguments["bones"] = new JArray();
                if (mode == "key_poses" && captureArguments["frames"] is JArray requestedFrames)
                {
                    if (requestedFrames.Count == 0 || requestedFrames.Any(item => item.Type != JTokenType.Integer))
                        throw new InvalidOperationException("frames must be a non-empty integer array.");
                    if (captureArguments["analysis_id"] != null)
                    {
                        AnalysisCacheRecord cached = GetCachedAnalysis(session, RequiredStringValue(captureArguments, "analysis_id"));
                        var selectedPoses = new JArray();
                        foreach (JToken requestedFrame in requestedFrames)
                        {
                            int frame = requestedFrame.Value<int>();
                            JObject match = (cached.Poses ?? new JArray()).OfType<JObject>().FirstOrDefault(item =>
                                item["analysis"]?.Value<int?>("frame") == frame);
                            if (match?["pose"] is not JObject pose)
                            {
                                throw new InvalidOperationException($"analysis_id does not contain keyframe {frame}.");
                            }
                            selectedPoses.Add(pose.DeepClone());
                        }
                        captureArguments["poses"] = selectedPoses;
                    }
                    else if (captureArguments["animation"] != null)
                    {
                        string name = RequiredStringValue(captureArguments, "animation");
                        TimelineAnimationRecord animation = session.Characters.SelectMany(character => character.Animations)
                            .SingleOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidOperationException($"Animation '{name}' is not loaded in the selected Session.");
                        int duration = animation.TimelineClip != null
                            ? Math.Max(1, Mathf.RoundToInt((float)(animation.TimelineDurationSeconds * SessionFrameRate)))
                            : Math.Max(1, animation.EndFrameExclusive - animation.StartFrame);
                        if (requestedFrames.Any(item => item.Value<int>() < 0 || item.Value<int>() >= duration))
                        {
                            throw new InvalidOperationException($"frames must be within animation '{name}' local range [0,{duration}).");
                        }
                        captureArguments["poses"] = new JArray(requestedFrames.Select(frame => PoseLocatorJson(name, frame.Value<int>())));
                    }
                    else
                    {
                        throw new InvalidOperationException("picture_key_poses frames require analysis_id or animation.");
                    }
                    captureArguments.Remove("analysis_id");
                    captureArguments.Remove("animation");
                }
                captureArguments.Remove("frames");
                if (captureArguments["analysis_id"] == null && captureArguments["animation"] != null)
                {
                    string animationName = RequiredStringValue(captureArguments, "animation");
                    TimelineAnimationRecord animation = session.Characters.SelectMany(item => item.Animations)
                        .SingleOrDefault(item => string.Equals(item.Name, animationName, StringComparison.OrdinalIgnoreCase));
                    if (animation == null)
                    {
                        throw new InvalidOperationException($"Animation '{animationName}' is not loaded in the current Session.");
                    }
                    int duration = animation.TimelineClip != null
                        ? Math.Max(1, Mathf.RoundToInt((float)(animation.TimelineDurationSeconds * SessionFrameRate)))
                        : Math.Max(1, animation.EndFrameExclusive - animation.StartFrame);
                    int[] frames = SelectFrames(0, duration, 5);
                    captureArguments.Remove("animation");
                    captureArguments["poses"] = new JArray(frames.Select(frame => PoseLocatorJson(animation.Name, frame)));
                }
                if (captureArguments["analysis_id"] == null && captureArguments["poses"] == null)
                {
                    throw new InvalidOperationException("Provide analysis_id, animation, or poses.");
                }

                JObject raw = JObject.Parse(RenderEvidence(session, captureArguments));
                if (raw.Value<bool?>("ok") != true)
                {
                    string message = raw["error"]?["message"]?.Value<string>() ?? "Picture rendering failed.";
                    throw new InvalidOperationException(message);
                }
                JArray allImages = raw["images"] as JArray ?? new JArray();
                IEnumerable<JObject> images = mode switch
                {
                    "motion_overlay" => allImages.OfType<JObject>().Where(item =>
                        (item.Value<string>("kind") ?? string.Empty).StartsWith("motion_overlay_", StringComparison.Ordinal)),
                    "key_poses" => allImages.OfType<JObject>().Where(item =>
                        (item.Value<string>("kind") ?? string.Empty).StartsWith("key_pose_", StringComparison.Ordinal)),
                    "trajectory_3d" => allImages.OfType<JObject>().Where(item =>
                        string.Equals(item.Value<string>("kind"), "trajectory_3d", StringComparison.Ordinal)),
                    _ => throw new InvalidOperationException("Unknown picture mode.")
                };
                return OkForSession(session, new JObject
                {
                    ["images"] = new JArray(images.Select(item => item.DeepClone())),
                    ["resolution"] = raw["resolution"]?.DeepClone(),
                    ["format"] = "motion_evidence_vNext"
                });
            });
        }

        private static int[] SelectFrames(int startFrame, int endFrameExclusive, int count)
        {
            int start = Math.Max(0, startFrame);
            int end = Math.Max(start + 1, endFrameExclusive);
            return Enumerable.Range(0, Math.Max(1, count))
                .Select(index => Mathf.RoundToInt(Mathf.Lerp(start, end - 1, count <= 1 ? 0f : index / (float)(count - 1))))
                .Distinct()
                .ToArray();
        }

        private static string RenderEvidence(TimelineSessionRecord session, JObject arguments)
        {
            try
            {
                var requests = new List<CaptureRequest>();
                int selected = (arguments["poses"] != null ? 1 : 0) +
                    (arguments["analysis_id"] != null ? 1 : 0) +
                    (arguments["constraints"] != null ? 1 : 0);
                if (selected != 1)
                {
                    throw new InvalidOperationException("Provide exactly one of poses, analysis_id, or constraints.");
                }
                if (arguments["poses"] is JArray poses)
                {
                    foreach (JObject pose in poses.OfType<JObject>()) requests.Add(BuildCaptureRequest(session, RequirePoseLocator(pose), null));
                    if (requests.Count != poses.Count) throw new InvalidOperationException("Every poses item must be a {source,frame} object.");
                }
                else if (arguments["analysis_id"] != null)
                {
                    string id = RequiredStringValue(arguments, "analysis_id");
                    AnalysisCacheRecord cached = GetCachedAnalysis(session, id);
                    string timelineGuid = AssetDatabase.AssetPathToGUID(session.TimelineAssetPath);
                    bool sameTimeline = !string.IsNullOrWhiteSpace(cached.TimelineAssetGuid)
                        ? string.Equals(cached.TimelineAssetGuid, timelineGuid, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(cached.SessionId, session.Id.ToString("D"), StringComparison.OrdinalIgnoreCase);
                    if (!sameTimeline)
                        throw new InvalidOperationException("analysis_source_expired: the analysis belongs to a different Session.");
                    JArray analyzedPoses = cached.Poses ?? new JArray();
                    foreach (JObject item in analyzedPoses.OfType<JObject>())
                        requests.Add(BuildCaptureRequest(session, RequirePoseLocator(item["pose"] as JObject), item["analysis"] as JObject));
                }
                else if (arguments["constraints"] is JArray constraints)
                {
                    NormalizeConstraintObjects(constraints);
                    for (int i = 0; i < constraints.Count; i++)
                    {
                        if (constraints[i] is not JObject item)
                        {
                            throw new InvalidOperationException($"constraints[{i}] must be an object.");
                        }
                        int frame = RequiredNonNegativeFrame(item, "frame");
                        JObject fullBody = item["fullbody"] as JObject;
                        JObject root2D = item["root2d"] as JObject;
                        string[] endEffectorFields = { "left_hand", "right_hand", "left_foot", "right_foot" };
                        if (fullBody == null && root2D == null &&
                            endEffectorFields.All(field => item[field] is not JObject))
                        {
                            throw new InvalidOperationException($"constraints[{i}] must contain at least one constraint field.");
                        }

                        if (fullBody != null)
                        {
                            if (fullBody["pose"] is not JObject fullBodyPose)
                            {
                                throw new InvalidOperationException($"constraints[{i}].fullbody.pose is required.");
                            }
                            requests.Add(BuildCaptureRequest(session,
                                RequirePoseLocator(fullBodyPose),
                                new JObject { ["constraint_type"] = "fullbody", ["frame"] = frame }));
                        }
                        if (root2D != null)
                        {
                            bool hasPosition = root2D["position"] != null;
                            bool hasHeading = root2D["heading"] != null;
                            if (hasPosition != hasHeading)
                            {
                                throw new InvalidOperationException($"constraints[{i}].root2d requires position and heading together.");
                            }
                            if (root2D["pose"] is JObject rootPose && !hasPosition)
                            {
                                requests.Add(BuildCaptureRequest(session,
                                    RequirePoseLocator(rootPose),
                                    new JObject { ["constraint_type"] = "root2d", ["frame"] = frame }));
                            }
                            else if (hasPosition)
                            {
                                string characterRef = arguments.Value<string>("character")?.Trim();
                                TimelineCharacterRecord character = !string.IsNullOrWhiteSpace(characterRef)
                                    ? ResolveSessionCharacterByReference(session, characterRef, false)
                                    : session.Characters.Count == 1
                                        ? session.Characters[0]
                                        : throw new InvalidOperationException("character is required to picture a position-only root2d constraint when the Session has multiple characters.");
                                Vector2 position = RequiredVector2(root2D, "position");
                                Vector2 heading = RequiredVector2(root2D, "heading");
                                if (heading.sqrMagnitude <= 1e-8f)
                                {
                                    throw new InvalidOperationException($"constraints[{i}].root2d.heading must be non-zero.");
                                }
                                var sample = new KimodoMarkerSampleResult
                                {
                                    characterPose = new CharacterAnimationCli.Unity.CharacterPose
                                    {
                                        root = new CharacterAnimationCli.Unity.CharacterPoseTransform
                                        {
                                            t = new Vector3(position.x, 0f, position.y),
                                            q = Quaternion.LookRotation(new Vector3(heading.x, 0f, heading.y), Vector3.up)
                                        }
                                    },
                                    constraintType = "constraint",
                                    mask = KimodoConstraintMask.ForType("root2d"),
                                    hasRootHeading = true
                                };
                                requests.Add(new CaptureRequest(character, session.Director.time,
                                    new JObject { ["constraint_type"] = "root2d", ["frame"] = frame }, sample, frame, true));
                            }
                            else
                            {
                                throw new InvalidOperationException($"constraints[{i}].root2d requires pose or position plus heading.");
                            }
                        }

                        foreach (string field in endEffectorFields)
                        {
                            if (item[field] is not JObject value)
                            {
                                continue;
                            }
                            if (value["pose"] is not JObject endEffectorPose)
                            {
                                throw new InvalidOperationException($"constraints[{i}].{field}.pose is required.");
                            }
                            requests.Add(BuildCaptureRequest(session,
                                RequirePoseLocator(endEffectorPose),
                                new JObject { ["constraint_type"] = field, ["frame"] = frame }));
                        }
                    }
                }
                if (requests.Count == 0) throw new InvalidOperationException("The selected capture source contains no poses.");
                JObject result = RenderMotionEvidence(session, requests, arguments);
                if (arguments["analysis_id"] != null) result["analysis_id"] = arguments.Value<string>("analysis_id");
                return OkForSession(session, result);
            }
            catch (Exception ex)
            {
                return Error(ex is CommandException command ? command.Code : "invalid_argument", ex.Message);
            }
        }

        private static CaptureRequest BuildCaptureRequest(
            TimelineSessionRecord session,
            PoseLocator locator,
            JObject annotation)
        {
            if (TryResolveAnimationPoseSource(session, locator, out TimelineCharacterRecord animationCharacter, out int absoluteFrame))
            {
                ThrowIfGenerationRangeLocked(session, animationCharacter, absoluteFrame, absoluteFrame + 1, PictureMotionOverlayCommand);
                return new CaptureRequest(animationCharacter, absoluteFrame / SessionFrameRate, annotation, null, locator.Frame);
            }
            TimelineCharacterRecord cacheOwner = ResolvePoseCacheOwner(locator.Source);
            if (cacheOwner != null)
            {
                KimodoConstraintMarker marker = FindUntypedPose(cacheOwner.PoseCacheTrack, locator.Frame)
                    ?? throw new InvalidOperationException("Writable pose source does not contain a pose at the requested frame.");
                return new CaptureRequest(cacheOwner, session.Director.time, annotation, marker.SampleData.Clone(), locator.Frame);
            }
            TimelineCharacterRecord owner = session.Characters.FirstOrDefault(item => item.Track.GetMarkers()
                .OfType<KimodoConstraintMarker>().Any(marker =>
                    string.Equals(marker.name, locator.Source, StringComparison.OrdinalIgnoreCase) &&
                    Mathf.RoundToInt((float)(marker.time * SessionFrameRate)) == locator.Frame));
            KimodoConstraintMarker constraint = owner?.Track.GetMarkers().OfType<KimodoConstraintMarker>()
                .FirstOrDefault(marker =>
                    string.Equals(marker.name, locator.Source, StringComparison.OrdinalIgnoreCase) &&
                    Mathf.RoundToInt((float)(marker.time * SessionFrameRate)) == locator.Frame)
                ?? throw new InvalidOperationException($"Pose source '{locator.Source}' was not found.");
            return new CaptureRequest(owner, session.Director.time, annotation, constraint.SampleData.Clone(), locator.Frame);
        }

        private static string CacheAnalysisResult(
            TimelineSessionRecord session,
            TimelineCharacterRecord character,
            double start,
            double end,
            JArray poses,
            JObject analysis,
            byte[] motionBytes)
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
                MotionPath = ToProjectRelativePath(motionPath)
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

        private static JObject RenderMotionEvidence(
            TimelineSessionRecord session,
            IReadOnlyList<CaptureRequest> requests,
            JObject arguments)
        {
            int resolution = Mathf.Clamp(arguments.Value<int?>("resolution") ?? 512, 128, 4096);
            float scale = arguments.Value<float?>("scale") ?? 1f;
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0.25f || scale > 4f)
            {
                throw new InvalidOperationException("scale must be between 0.25 and 4.0.");
            }
            int ghostFrames = Mathf.Clamp(arguments.Value<int?>("ghost_frames") ?? 5, 2, 8);
            List<CaptureRequest> selected = SelectEvidenceRequests(requests, ghostFrames);
            double originalTime = session.Director.time;
            var metadata = new JArray();
            var previews = new List<GameObject>(selected.Count);
            var environment = new List<GameObject>();
            var images = new JArray();
            try
            {
                for (int index = 0; index < selected.Count; index++)
                {
                    CaptureRequest request = selected[index];
                    KimodoMarkerSampleResult pose = request.PoseData?.Clone() ?? SampleCapturePose(request);
                    previews.Add(CreatePosePreview(request.Character, pose, request.Root2DOnly));
                    var item = new JObject
                    {
                        ["index"] = index,
                        ["character"] = request.Character.Name,
                        ["frame"] = request.Frame >= 0
                            ? request.Frame
                            : Mathf.RoundToInt((float)(request.Time * SessionFrameRate))
                    };
                    if (request.AnalysisFrame != null)
                    {
                        item["analysis"] = request.AnalysisFrame.DeepClone();
                    }
                    metadata.Add(item);
                }

                Bounds motionBounds = CalculateMotionBounds(previews, CalculateBounds(previews));
                CreateEvidenceEnvironment(environment, motionBounds, previews, arguments["bones"] as JArray);
                Directory.CreateDirectory(EvidenceFolder(session));
                (string kind, Vector3 direction)[] views =
                {
                    ("motion_overlay_front", Vector3.forward),
                    ("motion_overlay_back", Vector3.back),
                    ("motion_overlay_left", Vector3.left),
                    ("motion_overlay_right", Vector3.right)
                };
                foreach (var view in views)
                {
                    Texture2D image = RenderGhostComposite(
                        previews, environment, motionBounds, view.direction, resolution, scale,
                        orthographic: true, renderPoses: true);
                    images.Add(WriteEvidenceImage(session, view.kind, image));
                    UnityEngine.Object.DestroyImmediate(image);
                }

                for (int index = 1; index <= 3; index++)
                {
                    Texture2D image = RenderSinglePose(
                        previews, environment, index, CalculateBounds(previews[index]), resolution, scale);
                    images.Add(WriteEvidenceImage(session, "key_pose_" + index.ToString(CultureInfo.InvariantCulture), image));
                    UnityEngine.Object.DestroyImmediate(image);
                }

                Texture2D trajectory = RenderGhostComposite(
                    previews, environment, motionBounds, new Vector3(1f, .75f, -1f).normalized,
                    resolution, scale, orthographic: false, renderPoses: false);
                images.Add(WriteEvidenceImage(session, "trajectory_3d", trajectory));
                UnityEngine.Object.DestroyImmediate(trajectory);

                return new JObject
                {
                    ["image_path"] = ((JObject)images[images.Count - 1])["path"],
                    ["format"] = "motion_evidence_v2",
                    ["images"] = images,
                    ["resolution"] = new JArray(resolution, resolution),
                    ["scale"] = scale,
                    ["frames"] = metadata
                };
            }
            finally
            {
                foreach (GameObject item in environment)
                {
                    if (item != null) UnityEngine.Object.DestroyImmediate(item);
                }
                foreach (GameObject preview in previews)
                {
                    if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
                }
                session.Director.time = originalTime;
                session.Director.Evaluate();
                TimelineEditor.Refresh(RefreshReason.SceneNeedsUpdate | RefreshReason.WindowNeedsRedraw);
            }
        }

        private static List<CaptureRequest> SelectEvidenceRequests(
            IReadOnlyList<CaptureRequest> requests,
            int count)
        {
            var result = new List<CaptureRequest>(count);
            for (int index = 0; index < count; index++)
            {
                result.Add(requests[Mathf.RoundToInt(index * (requests.Count - 1f) / (count - 1f))]);
            }
            return result;
        }

        private static JObject WriteEvidenceImage(TimelineSessionRecord session, string kind, Texture2D image)
        {
            string path = Path.Combine(EvidenceFolder(session),
                $"picture_{kind}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            return new JObject { ["kind"] = kind, ["path"] = path.Replace('\\', '/') };
        }

        private static KimodoMarkerSampleResult SampleCapturePose(CaptureRequest request)
        {
            CharacterPose pose = CaptureCharacterPose(
                request.Character,
                Mathf.RoundToInt((float)(request.Time * SessionFrameRate)));
            var sample = new KimodoMarkerSampleResult { sampleTime = request.Time };
            SetCanonicalPose(sample, pose, request.Character);
            return sample;
        }

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

        private static Bounds CalculateMotionBounds(IReadOnlyList<GameObject> previews, Bounds characterBounds)
        {
            Bounds result = characterBounds;
            foreach (GameObject preview in previews) result.Encapsulate(PreviewRootPosition(preview));
            result.Expand(new Vector3(6f, 1f, 6f));
            if (result.size.x < 6f) result.Expand(new Vector3(6f - result.size.x, 0f, 0f));
            if (result.size.z < 6f) result.Expand(new Vector3(0f, 0f, 6f - result.size.z));
            return result;
        }

        private static void CreateEvidenceEnvironment(
            List<GameObject> objects,
            Bounds bounds,
            IReadOnlyList<GameObject> previews,
            JArray requestedBones)
        {
            const int captureLayer = 31;
            float size = Mathf.Ceil(Mathf.Max(bounds.size.x, bounds.size.z) * .5f) * 2f;
            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Kimodo Evidence Floor";
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
            Vector3[] roots = previews.Select(PreviewRootPosition).ToArray();
            CreateTrajectory(objects, roots, new Color(.1f, .85f, 1f, 1f), .045f, speedColor: false);
            CreateMarker(objects, roots[0], Color.green);
            CreateMarker(objects, roots[roots.Length - 1], Color.red);
            CreateBoneTrajectories(objects, previews, requestedBones);
            CreateEvidenceLights(objects, bounds.center);
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

        private static void CreateBoneTrajectories(
            List<GameObject> objects,
            IReadOnlyList<GameObject> previews,
            JArray requestedBones)
        {
            var bones = new[]
            {
                (name: "hips", bone: HumanBodyBones.Hips),
                (name: "left_hand", bone: HumanBodyBones.LeftHand),
                (name: "right_hand", bone: HumanBodyBones.RightHand),
                (name: "left_foot", bone: HumanBodyBones.LeftFoot),
                (name: "right_foot", bone: HumanBodyBones.RightFoot)
            };
            var selected = requestedBones == null
                ? new HashSet<string>(bones.Select(item => item.name), StringComparer.Ordinal)
                : new HashSet<string>(requestedBones.Values<string>(), StringComparer.Ordinal);
            foreach (var entry in bones)
            {
                if (!selected.Contains(entry.name)) continue;
                HumanBodyBones bone = entry.bone;
                Vector3[] points = previews.Select(preview =>
                {
                    Animator animator = preview.GetComponentInChildren<Animator>(true);
                    Transform transform = animator != null ? animator.GetBoneTransform(bone) : null;
                    return transform != null ? transform.position : PreviewRootPosition(preview);
                }).ToArray();
                CreateTrajectory(objects, points, Color.white, .025f, speedColor: true);
            }
        }

        private static void CreateTrajectory(
            List<GameObject> objects,
            Vector3[] points,
            Color color,
            float width,
            bool speedColor)
        {
            for (int index = 1; index < points.Length; index++)
            {
                float speed = (points[index] - points[index - 1]).magnitude;
                Color segment = speedColor
                    ? Color.Lerp(Color.red, Color.green, Mathf.Clamp01(speed / .4f))
                    : color;
                CreateWorldLine(objects, points[index - 1] + Vector3.up * .02f,
                    points[index] + Vector3.up * .02f, width, segment);
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

        private static void CreateMarker(List<GameObject> objects, Vector3 point, Color color)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.hideFlags = HideFlags.HideAndDontSave;
            marker.transform.position = point + Vector3.up * .1f;
            marker.transform.localScale = Vector3.one * .18f;
            SetLayerRecursively(marker, 31);
            marker.GetComponent<Renderer>().sharedMaterial = MakeMaterial(color);
            objects.Add(marker);
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

        private static Texture2D RenderGhostComposite(
            IReadOnlyList<GameObject> previews,
            IReadOnlyList<GameObject> environment,
            Bounds bounds,
            Vector3 direction,
            int size,
            float scale,
            bool orthographic,
            bool renderPoses)
        {
            Camera camera = CreateEvidenceCamera(bounds, direction, scale, orthographic);
            try
            {
                foreach (GameObject preview in previews) preview.SetActive(false);
                Texture2D result = RenderCamera(camera, size, new Color(.12f, .12f, .12f, 1f));
                if (!renderPoses) return result;

                SetEvidenceVisualsEnabled(environment, false);
                float[] alpha = { .30f, .48f, .65f, .82f, 1f };
                for (int index = 0; index < previews.Count; index++)
                {
                    previews[index].SetActive(true);
                    Texture2D pose = RenderCamera(camera, size, new Color(0f, 0f, 0f, 0f));
                    Composite(result, pose, alpha[index]);
                    UnityEngine.Object.DestroyImmediate(pose);
                    previews[index].SetActive(false);
                }
                return result;
            }
            finally
            {
                SetEvidenceVisualsEnabled(environment, true);
                foreach (GameObject preview in previews) preview.SetActive(true);
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
            }
        }

        private static Texture2D RenderSinglePose(
            IReadOnlyList<GameObject> previews,
            IReadOnlyList<GameObject> environment,
            int selected,
            Bounds bounds,
            int size,
            float scale)
        {
            for (int index = 0; index < previews.Count; index++) previews[index].SetActive(index == selected);
            SetEvidenceVisualsEnabled(environment, false);
            Camera camera = CreateEvidenceCamera(bounds, new Vector3(1f, .65f, -1f).normalized, scale, orthographic: false);
            try
            {
                return RenderCamera(camera, size, new Color(.12f, .12f, .12f, 1f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
                SetEvidenceVisualsEnabled(environment, true);
                foreach (GameObject preview in previews) preview.SetActive(true);
            }
        }

        private static Camera CreateEvidenceCamera(Bounds bounds, Vector3 direction, float scale, bool orthographic)
        {
            GameObject cameraObject = new GameObject("Kimodo Evidence Camera") { hideFlags = HideFlags.HideAndDontSave };
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.cullingMask = 1 << 31;
            camera.orthographic = orthographic;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.orthographicSize = Mathf.Max(2.5f, bounds.extents.magnitude * 1.05f / scale);
            camera.fieldOfView = 35f;
            camera.transform.position = bounds.center + direction * Mathf.Max(7f, bounds.extents.magnitude * 3.2f);
            camera.transform.LookAt(bounds.center + Vector3.up, Vector3.up);
            return camera;
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

        private static Bounds CalculateBounds(IReadOnlyList<GameObject> roots)
        {
            Bounds bounds = CalculateBounds(roots[0]);
            for (int index = 1; index < roots.Count; index++)
            {
                Renderer[] renderers = roots[index].GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) bounds.Encapsulate(roots[index].transform.position);
                foreach (Renderer renderer in renderers) bounds.Encapsulate(renderer.bounds);
            }
            return bounds;
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

        private sealed class CaptureRequest
        {
            public CaptureRequest(
                TimelineCharacterRecord character,
                double time,
                JObject analysisFrame,
                KimodoMarkerSampleResult poseData = null,
                int frame = -1,
                bool root2DOnly = false)
            {
                Character = character;
                Time = time;
                AnalysisFrame = analysisFrame;
                PoseData = poseData;
                Frame = frame;
                Root2DOnly = root2DOnly;
            }
            public TimelineCharacterRecord Character { get; }
            public double Time { get; }
            public JObject AnalysisFrame { get; }
            public KimodoMarkerSampleResult PoseData { get; }
            public int Frame { get; }
            public bool Root2DOnly { get; }
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

            public JObject ToJson() => new JObject
            {
                ["analysis_id"] = Id, ["session_id"] = SessionId, ["timeline_asset_guid"] = TimelineAssetGuid,
                ["session_name"] = SessionName,
                ["character_ref"] = CharacterRef, ["character"] = CharacterName,
                ["start"] = Start, ["end"] = End, ["created_at_utc"] = CreatedAtUtc,
                ["motion_path"] = MotionPath ?? string.Empty,
                ["poses"] = Poses?.DeepClone() ?? new JArray(),
                ["analysis"] = Analysis?.DeepClone() ?? new JObject()
            };

            public static AnalysisCacheRecord FromJson(JObject json) => new AnalysisCacheRecord
            {
                Id = json.Value<string>("analysis_id"), SessionId = json.Value<string>("session_id"),
                TimelineAssetGuid = json.Value<string>("timeline_asset_guid"),
                SessionName = json.Value<string>("session_name"), CharacterRef = json.Value<string>("character_ref"),
                CharacterName = json.Value<string>("character"), Start = json.Value<double>("start"),
                End = json.Value<double>("end"), CreatedAtUtc = json.Value<DateTime>("created_at_utc"),
                MotionPath = json.Value<string>("motion_path"),
                Poses = json["poses"] as JArray ?? json["analysis"]?["poses"] as JArray ?? new JArray(),
                Analysis = json["analysis"] as JObject ?? new JObject()
            };
        }
    }
}
