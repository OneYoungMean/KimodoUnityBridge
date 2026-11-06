using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using TimelineInject;
using UnityEditor;
using UnityEditor.Timeline;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private sealed class PoseAnalysisTarget
        {
            public string Path;
            public Transform Transform;
        }

        public static string PoseAnalysis(string argumentsJson) => Execute(argumentsJson, PoseAnalysis);

        private static string PoseAnalysis(JObject arguments)
        {
            TimelineSessionRecord session = RequireCurrentTimelineSession();
            JArray constraintsArg = arguments["constraints"] as JArray
                ?? throw new InvalidOperationException("constraints must be an array.");
            bool combine = arguments.Value<bool?>("combine") ?? false;
            if (constraintsArg.Count == 0)
                throw new InvalidOperationException("constraints must contain at least one point constraint.");

            JObject cameraOptions = arguments["camera"] as JObject ?? new JObject();
            GetOrOpenSceneView();

            TimelineCharacterRecord character = ResolveSessionCharacterByReference(
                session, RequiredStringValue(arguments, "character"), addIfMissing: true);
            string modelName = ResolveModelName(null);
            foreach (JToken token in constraintsArg)
            {
                if (token is not JObject constraint)
                    throw new InvalidOperationException("constraints entries must be objects.");
                if (constraint["root_path"] != null || constraint["inout"] != null)
                    throw new InvalidOperationException("pose.analysis accepts point constraints only.");
            }
            int durationFrames = constraintsArg.OfType<JObject>()
                .Select(item => RequiredNonNegativeFrame(item, "frame"))
                .DefaultIfEmpty(0).Max() + 1;
            if (durationFrames <= 0)
                throw new InvalidOperationException("constraints frame is too large.");
            Avatar originAvatar = KimodoTimelineGenerationOutputPlanner.ResolveOriginRetargetAvatar(modelName);
            if (!KimodoRetargetCoreUtility.IsValidHumanoid(originAvatar))
                throw new InvalidOperationException($"Model '{modelName}' does not provide a valid humanoid origin Avatar.");
            var constraintArguments = new JObject { ["constraints"] = constraintsArg.DeepClone() };
            List<KimodoMarkerSampleResult> samples = BuildPoseConstraints(
                constraintArguments,
                modelName,
                originAvatar,
                Math.Max(1, KimodoFrameTimeUtility.SecondsToFrameCount(durationFrames / SessionFrameRate, ResolveFrameRate(modelName, null))),
                ResolveFrameRate(modelName, null),
                durationFrames,
                character.Root,
                character,
                out _);
            if (samples.Count != constraintsArg.Count)
                throw new InvalidOperationException("pose.analysis requires one point constraint per entry.");

            var entries = new List<Tuple<TimelineCharacterRecord, KimodoMarkerSampleResult, JObject>>();
            for (int index = 0; index < constraintsArg.Count; index++)
            {
                JObject constraint = (JObject)constraintsArg[index];
                entries.Add(Tuple.Create(character, samples[index], new JObject
                {
                    ["type"] = "point_constraint",
                    ["character"] = character.Name,
                    ["frame"] = constraint["frame"]?.DeepClone(),
                    ["constraint"] = constraint.DeepClone()
                }));
            }

            string folder = Path.Combine(Application.dataPath, "KimodoGeneratedClips", "PoseAnalysis");
            Directory.CreateDirectory(folder);

            if (combine)
            {
                JObject combined = CaptureCombinedPoseAnalysisEntries(arguments, cameraOptions, entries, folder);
                combined["ok"] = true;
                combined["combine"] = true;
                return Ok(combined);
            }

            var captured = new List<JObject>();
            for (int index = 0; index < entries.Count; index++)
                captured.Add(CapturePoseAnalysisEntry(arguments, cameraOptions, entries[index].Item1,
                    entries[index].Item2, entries[index].Item3, folder, index));

            var response = new JObject
            {
                ["ok"] = true,
                ["combine"] = false,
                ["constraints"] = new JArray(captured)
            };
            return Ok(response);

        }

        private static JObject CapturePoseAnalysisEntry(
            JObject arguments,
            JObject cameraOptions,
            TimelineCharacterRecord character,
            KimodoMarkerSampleResult sample,
            JObject sourceResult,
            string folder,
            int index)
        {
            using (EvaluatedPosePreview preview = CreatePipelinePosePreview(character, sample))
            {
                if (preview.Animator == null || !preview.Animator.isHuman)
                    throw new InvalidOperationException("pose.analysis requires a valid Humanoid pose rig.");

                JObject rowArguments = ConstraintAnalysisArguments(arguments, sourceResult);
                List<HumanBodyBones> joints = ResolvePoseAnalysisJoints(rowArguments);
                JObject poseAnalysis = MeasurePoseAnalysis(preview.Animator, joints, rowArguments["targets"] as JArray);
                List<PoseAnalysisTarget> transforms = ResolvePoseAnalysisTransforms(
                    arguments["transforms"] as JArray,
                    character,
                    preview.Root.transform);
                Bounds bounds = CalculatePoseAnalysisBounds(transforms);

                SceneView sceneView = GetOrOpenSceneView();
                List<Tuple<Renderer, bool>> visibility = HidePoseAnalysisRenderers(transforms, character, cameraOptions);
                try
                {
                    Vector3 direction = ReadSceneVector(
                        cameraOptions["direction"],
                        "camera.direction",
                        new Vector3(1f, .75f, -1f));
                    FrameSceneView(sceneView, bounds, direction, cameraOptions);

                    string fileName = "pose_analysis_" + index + "_" + Guid.NewGuid().ToString("N") + ".png";
                    string filePath = Path.Combine(folder, fileName);
                    int width;
                    int height;
                    ResolveSceneViewResolution(cameraOptions, sceneView, out width, out height);
                    Texture2D image = SceneViewCaptureBridge.Render(sceneView, width, height);
                    try
                    {
                        File.WriteAllBytes(filePath, image.EncodeToPNG());
                        width = image.width;
                        height = image.height;
                    }
                    finally
                    {
                        UnityEngine.Object.DestroyImmediate(image);
                    }
                    AssetDatabase.Refresh();

                    return new JObject
                    {
                        ["index"] = index,
                        ["frame"] = sourceResult["frame"]?.DeepClone(),
                        ["image_path"] = "Assets/KimodoGeneratedClips/PoseAnalysis/" + fileName,
                        ["width"] = width,
                        ["height"] = height,
                        ["source"] = sourceResult,
                        ["transforms"] = new JArray(transforms.Select(target => new JObject
                        {
                            ["path"] = target.Path,
                            ["world_position"] = PositionJson(target.Transform.position)
                        })),
                        ["pose_analysis"] = poseAnalysis,
                        ["ok"] = true
                    };
                }
                finally
                {
                    foreach (Tuple<Renderer, bool> entry in visibility)
                        if (entry.Item1 != null) entry.Item1.forceRenderingOff = entry.Item2;
                }
            }
        }

        private static JObject CaptureCombinedPoseAnalysisEntries(
            JObject arguments,
            JObject cameraOptions,
            IReadOnlyList<Tuple<TimelineCharacterRecord, KimodoMarkerSampleResult, JObject>> entries,
            string folder)
        {
            var previews = new List<EvaluatedPosePreview>();
            var targets = new List<PoseAnalysisTarget>();
            var measurements = new JArray();
            var visibility = new List<Tuple<Renderer, bool>>();
            try
            {
                Bounds bounds = default;
                bool hasBounds = false;
                for (int index = 0; index < entries.Count; index++)
                {
                    var item = entries[index];
                    EvaluatedPosePreview preview = CreatePipelinePosePreview(item.Item1, item.Item2);
                    previews.Add(preview);
                    if (preview.Animator == null || !preview.Animator.isHuman)
                        throw new InvalidOperationException("pose.analysis requires a valid Humanoid pose rig.");

                    List<PoseAnalysisTarget> poseTargets = ResolvePoseAnalysisTransforms(
                        arguments["transforms"] as JArray, item.Item1, preview.Root.transform);
                    targets.AddRange(poseTargets);
                    Bounds current = CalculatePoseAnalysisBounds(poseTargets);
                    if (!hasBounds) { bounds = current; hasBounds = true; }
                    else bounds.Encapsulate(current);

                    measurements.Add(new JObject
                    {
                        ["index"] = index,
                        ["frame"] = item.Item3["frame"]?.DeepClone(),
                        ["source"] = item.Item3.DeepClone(),
                        ["transforms"] = new JArray(poseTargets.Select(target => new JObject
                        {
                            ["path"] = target.Path,
                            ["world_position"] = PositionJson(target.Transform.position)
                        })),
                        ["pose_analysis"] = MeasureCombinedEntry(preview.Animator, arguments, item.Item3)
                    });
                }

                if (!hasBounds) throw new InvalidOperationException("constraints must contain at least one entry.");
                bounds.Expand(Vector3.one * .1f);
                SceneView sceneView = GetOrOpenSceneView();
                visibility = HidePoseAnalysisRenderers(targets, entries[0].Item1, cameraOptions);
                Vector3 direction = ReadSceneVector(
                    cameraOptions["direction"], "camera.direction", new Vector3(1f, .75f, -1f));
                FrameSceneView(sceneView, bounds, direction, cameraOptions);

                int width;
                int height;
                ResolveSceneViewResolution(cameraOptions, sceneView, out width, out height);
                Texture2D image = SceneViewCaptureBridge.Render(sceneView, width, height);
                try
                {
                    string fileName = "pose_analysis_combined_" + Guid.NewGuid().ToString("N") + ".png";
                    File.WriteAllBytes(Path.Combine(folder, fileName), image.EncodeToPNG());
                    width = image.width;
                    height = image.height;
                    AssetDatabase.Refresh();
                    return new JObject
                    {
                        ["image_path"] = "Assets/KimodoGeneratedClips/PoseAnalysis/" + fileName,
                        ["width"] = width,
                        ["height"] = height,
                        ["constraints"] = measurements
                    };
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(image);
                }
            }
            finally
            {
                foreach (Tuple<Renderer, bool> entry in visibility)
                    if (entry.Item1 != null) entry.Item1.forceRenderingOff = entry.Item2;
                foreach (EvaluatedPosePreview preview in previews) preview.Dispose();
            }
        }

        private static JObject ConstraintAnalysisArguments(JObject arguments, JObject sourceResult)
        {
            JObject result = (JObject)arguments.DeepClone();
            var targets = new JArray();
            JArray effectors = sourceResult?["constraint"]?["effector"] as JArray;
            if (effectors != null)
            {
                foreach (JObject effector in effectors.OfType<JObject>())
                {
                    string joint = effector.Value<string>("joint") switch
                    {
                        "left_hand" => "LeftHand",
                        "right_hand" => "RightHand",
                        "left_foot" => "LeftFoot",
                        "right_foot" => "RightFoot",
                        _ => null
                    };
                    JArray position = effector["position"] as JArray;
                    if (joint == null || position == null) continue;
                    targets.Add(new JObject
                    {
                        ["joint"] = joint,
                        ["position"] = position.DeepClone(),
                        ["tolerance_m"] = 0.05f
                    });
                }
            }
            result["targets"] = targets;
            return result;
        }

        private static JObject MeasureCombinedEntry(
            Animator animator,
            JObject arguments,
            JObject sourceResult)
        {
            JObject rowArguments = ConstraintAnalysisArguments(arguments, sourceResult);
            return MeasurePoseAnalysis(
                animator,
                ResolvePoseAnalysisJoints(rowArguments),
                rowArguments["targets"] as JArray);
        }

        private static List<HumanBodyBones> ResolvePoseAnalysisJoints(JObject arguments)
        {
            var joints = new List<HumanBodyBones>();
            if (arguments["joints"] is JArray requested)
            {
                foreach (string name in requested.Values<string>())
                {
                    if (!System.Enum.TryParse(name, false, out HumanBodyBones bone) ||
                        !System.Enum.IsDefined(typeof(HumanBodyBones), bone) || bone == HumanBodyBones.LastBone)
                        throw new InvalidOperationException("Unsupported Humanoid joint '" + name + "'.");
                    if (!joints.Contains(bone)) joints.Add(bone);
                }
            }
            else
            {
                joints.AddRange(new[]
                {
                    HumanBodyBones.Hips, HumanBodyBones.Head, HumanBodyBones.LeftHand,
                    HumanBodyBones.RightHand, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot
                });
            }

            foreach (JObject target in (arguments["targets"] as JArray ?? new JArray()).OfType<JObject>())
            {
                HumanBodyBones bone = ParsePoseAnalysisBone(RequiredStringValue(target, "joint"));
                if (!joints.Contains(bone)) joints.Add(bone);
            }
            if (joints.Count == 0)
                throw new InvalidOperationException("joints or effector constraints must select at least one Humanoid joint.");
            return joints;
        }

        private static JObject MeasurePoseAnalysis(
            Animator animator,
            IReadOnlyList<HumanBodyBones> joints,
            JArray targetValues)
        {
            var targets = new Dictionary<HumanBodyBones, Tuple<Vector3, float>>();
            foreach (JObject target in (targetValues ?? new JArray()).OfType<JObject>())
            {
                HumanBodyBones bone = ParsePoseAnalysisBone(RequiredStringValue(target, "joint"));
                if (targets.ContainsKey(bone))
                    throw new InvalidOperationException("targets contains duplicate joint '" + bone + "'.");
                Vector3 position = ReadVector3(target["position"] as JArray, "targets." + bone + ".position");
                float tolerance = target.Value<float?>("tolerance_m") ?? 0.05f;
                if (float.IsNaN(tolerance) || float.IsInfinity(tolerance) || tolerance < 0f)
                    throw new InvalidOperationException("targets." + bone + ".tolerance_m must be finite and non-negative.");
                targets.Add(bone, Tuple.Create(position, tolerance));
            }

            var results = new JArray();
            bool passed = true;
            foreach (HumanBodyBones bone in joints)
            {
                Transform joint = animator.GetBoneTransform(bone);
                if (joint == null)
                    throw new InvalidOperationException("Humanoid joint '" + bone + "' is not available on the sampled rig.");
                var result = new JObject
                {
                    ["joint"] = bone.ToString(),
                    ["world_position"] = PositionJson(joint.position),
                    ["target_position"] = JValue.CreateNull(),
                    ["distance_m"] = JValue.CreateNull(),
                    ["tolerance_m"] = JValue.CreateNull(),
                    ["status"] = "not_tested"
                };
                if (targets.TryGetValue(bone, out Tuple<Vector3, float> target))
                {
                    float distance = Vector3.Distance(joint.position, target.Item1);
                    bool withinTolerance = distance <= target.Item2;
                    result["target_position"] = PositionJson(target.Item1);
                    result["distance_m"] = distance;
                    result["tolerance_m"] = target.Item2;
                    result["status"] = withinTolerance ? "passed" : "failed";
                    passed &= withinTolerance;
                }
                results.Add(result);
            }

            int testedCount = targets.Count;
            return new JObject
            {
                ["overall"] = testedCount == 0 ? "not_tested" : passed ? "passed" : "failed",
                ["joints"] = results
            };
        }

        private static HumanBodyBones ParsePoseAnalysisBone(string name)
        {
            if (!System.Enum.TryParse(name, false, out HumanBodyBones bone) ||
                !System.Enum.IsDefined(typeof(HumanBodyBones), bone) || bone == HumanBodyBones.LastBone)
                throw new InvalidOperationException("Unsupported Humanoid joint '" + name + "'.");
            return bone;
        }

        private static List<PoseAnalysisTarget> ResolvePoseAnalysisTransforms(
            JArray requested,
            TimelineCharacterRecord character,
            Transform poseRoot)
        {
            var paths = requested == null
                ? new List<string> { "@pose" }
                : requested.Values<string>().Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim('/')).ToList();
            if (paths.Count == 0)
                throw new InvalidOperationException("transforms must contain at least one scene hierarchy path.");

            var result = new List<PoseAnalysisTarget>();
            foreach (string path in paths.Distinct(StringComparer.Ordinal))
            {
                bool isPose = path == "@pose" || path.StartsWith("@pose/", StringComparison.Ordinal);
                Transform transform;
                if (isPose)
                {
                    transform = path == "@pose" ? poseRoot : poseRoot.Find(path.Substring(6));
                }
                else
                {
                    transform = character?.Root != null ? character.Root.transform.Find(path) : null;
                    transform = transform ?? FindPoseAnalysisSceneTransform(path);
                }
                if (transform == null)
                    throw new InvalidOperationException("Transform path '" + path + "' was not found in the pose or active scene.");
                result.Add(new PoseAnalysisTarget { Path = path, Transform = transform });
            }
            return result;
        }

        private static Transform FindPoseAnalysisSceneTransform(string path)
        {
            Scene scene = SceneManager.GetActiveScene();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (string.Equals(path, root.name, StringComparison.Ordinal))
                    return root.transform;
                if (path.StartsWith(root.name + "/", StringComparison.Ordinal))
                    return root.transform.Find(path.Substring(root.name.Length + 1));
                Transform relative = root.transform.Find(path);
                if (relative != null) return relative;
            }
            GameObject exact = GameObject.Find(path);
            return exact != null && exact.scene == scene ? exact.transform : null;
        }

        private static Bounds CalculatePoseAnalysisBounds(IReadOnlyList<PoseAnalysisTarget> targets)
        {
            bool hasBounds = false;
            Bounds bounds = default;
            foreach (PoseAnalysisTarget target in targets)
            {
                Bounds current = CalculateSceneTargetBounds(target.Transform);
                if (!hasBounds) { bounds = current; hasBounds = true; }
                else bounds.Encapsulate(current);
            }
            if (!hasBounds)
                throw new InvalidOperationException("No renderable geometry is available in transforms.");
            bounds.Expand(Vector3.one * .1f);
            return bounds;
        }

        private static List<Tuple<Renderer, bool>> HidePoseAnalysisRenderers(
            IReadOnlyList<PoseAnalysisTarget> targets,
            TimelineCharacterRecord character,
            JObject cameraOptions)
        {
            bool sceneScope = string.Equals(
                cameraOptions?.Value<string>("scope") ?? "targets",
                "scene",
                StringComparison.Ordinal);
            Transform sourceRoot = character?.Root != null ? character.Root.transform : null;
            var visibility = new List<Tuple<Renderer, bool>>();
            foreach (Renderer renderer in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (renderer == null || !renderer.gameObject.scene.IsValid() || EditorUtility.IsPersistent(renderer))
                    continue;
                bool isSourceCharacter = sourceRoot != null && renderer.transform.IsChildOf(sourceRoot);
                bool requested = targets.Any(target => renderer.transform.IsChildOf(target.Transform));
                bool hide = sceneScope ? isSourceCharacter : !requested;
                if (renderer.forceRenderingOff != hide)
                {
                    visibility.Add(Tuple.Create(renderer, renderer.forceRenderingOff));
                    renderer.forceRenderingOff = hide;
                }
            }
            return visibility;
        }

        private static JArray PositionJson(Vector3 position) =>
            new JArray(position.x, position.y, position.z);
    }
}
