using System;
using System.Collections;
using System.IO;
using System.Linq;
using KimodoBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class AnimationAnalyzeCommandIntegrationTests
    {
        private const string PackageRoot = "Packages/com.unity.kimodo_unity_motion_tools";
        private const string ModelPath = PackageRoot + "/Editor/Model/Armature.fbx";
        private const string ArcWalkPath = PackageRoot + "/Editor/Tests/Fixtures/arc_walk_left_loop.anim";

        [UnityTest]
        public IEnumerator AnimationAnalyze_ArcWalkFixture_WritesCompositePng()
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ArcWalkPath);
            Assert.That(source, Is.Not.Null, "Missing Armature fixture.");
            Assert.That(clip, Is.Not.Null, "Missing arc walk left loop fixture.");

            GameObject character = UnityEngine.Object.Instantiate(source);
            character.name = "KimodoTest_Armature";
            try
            {
                Avatar modelAvatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
                    .OfType<Avatar>()
                    .FirstOrDefault();
                Assert.That(modelAvatar, Is.Not.Null.And.Property("isHuman").True, "Armature fixture requires a Humanoid Avatar.");
                Animator animator = character.GetComponentInChildren<Animator>(true) ?? character.AddComponent<Animator>();
                animator.avatar = modelAvatar;
                animator.runtimeAnimatorController = null;
                animator.Rebind();

                JObject session = Require("session_get_or_create", new JObject
                {
                    ["name"] = "AnimationAnalyze_ArcWalkFixture_" + Guid.NewGuid().ToString("N")
                });
                string sessionJsonPath = session.Value<string>("session_json_path");
                Assert.That(sessionJsonPath?.Replace('\\', '/'), Does.StartWith("Library/KimodoData/"));
                JObject addedCharacter = Require("session_add", new JObject
                {
                    ["kind"] = "character", ["character"] = character.name, ["session_id"] = session.Value<string>("session_id")
                })["character"] as JObject;
                JObject addedClip = Require("session_add", new JObject
                {
                    ["kind"] = "clip", ["character"] = addedCharacter.Value<string>("name"), ["clip"] = ArcWalkPath
                })["animation"] as JObject;
                int previewSceneCount = EditorSceneManager.previewSceneCount;
                JObject analysis = Require("animation_analyze", new JObject
                {
                    ["clips"] = new JArray(new JObject
                    {
                        ["character"] = addedCharacter.Value<string>("name"), ["clip"] = addedClip.Value<string>("name")
                    }),
                    ["level"] = "middle", ["resolution"] = 512
                });
                // HDRP AOV/readback and editor asset writes can finish on the
                // next editor tick even though the command returned.
                yield return null;
                Assert.That(EditorSceneManager.previewSceneCount, Is.EqualTo(previewSceneCount),
                    "animation_analyze leaked its isolated preview Scene.");

                string relativePng = analysis["pictures"]?.Value<string>("image_path");
                string absolutePng = string.IsNullOrWhiteSpace(relativePng)
                    ? string.Empty
                    : Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePng));
                Assert.That(relativePng, Is.Not.Null.And.EndsWith(".png"));
                Assert.That(relativePng.Replace('\\', '/'), Does.StartWith("Library/KimodoData/"));
                Assert.That(File.Exists(absolutePng), Is.True, "animation_analyze did not write its composite PNG.");
                Assert.That(new FileInfo(absolutePng).Length, Is.GreaterThan(0));

                JObject persistedSession = JObject.Parse(File.ReadAllText(
                    Path.GetFullPath(Path.Combine(Application.dataPath, "..", sessionJsonPath))));
                string analysisPath = persistedSession["analyses"]?[0]?.Value<string>("analysis_path");
                Assert.That(analysisPath?.Replace('\\', '/'), Does.StartWith("Library/KimodoData/"));

                JObject root2D = analysis["pictures"]?["images"]?
                    .Children<JObject>()
                    .Select(item => item["description"] as JObject)
                    .SingleOrDefault(description => description?.Value<string>("presentation") == "root2d_pelvis_projection");
                Assert.That(root2D, Is.Not.Null, "Middle analysis did not describe its Root2D tile.");
                int[] frames = root2D["frames"]?.Values<int>().ToArray();
                int[] primaryFrames = root2D["primary_frames"]?.Values<int>().ToArray();
                int[] sampleFrames = root2D["sample_frames"]?.Values<int>().ToArray();
                Assert.That(frames, Is.Not.Null.And.Not.Empty, "Root2D tile did not expose its sampled frames.");
                Assert.That(primaryFrames, Is.Not.Null.And.Not.Empty, "Root2D tile did not expose its primary frames.");
                Assert.That(sampleFrames, Is.Not.Null, "Root2D tile did not expose its gray sample frames.");
                Assert.That(sampleFrames.All(frame => frames.Contains(frame) && !primaryFrames.Contains(frame)), Is.True,
                    "Root2D gray samples must come from the shared sample set and exclude colored keyframes.");

                LogRootTrajectoryContinuity(analysis, addedClip.Value<string>("name"));
            }
            finally
            {
                command_dispatcher.Invoke("session_close", "{}");
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        [UnityTest]
        public IEnumerator AnimationGenerate_ArmatureWalkForward_WritesCompositePng()
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            Assert.That(source, Is.Not.Null, "Missing Armature fixture.");

            GameObject character = UnityEngine.Object.Instantiate(source);
            character.name = "KimodoTest_Armature";
            try
            {
                Avatar modelAvatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
                    .OfType<Avatar>()
                    .FirstOrDefault();
                Assert.That(modelAvatar, Is.Not.Null.And.Property("isHuman").True, "Armature fixture requires a Humanoid Avatar.");
                Animator animator = character.GetComponentInChildren<Animator>(true) ?? character.AddComponent<Animator>();
                animator.avatar = modelAvatar;
                animator.runtimeAnimatorController = null;
                animator.Rebind();

                JObject session = Require("session_get_or_create", new JObject
                {
                    ["name"] = "AnimationGenerate_ArmatureWalkForward_" + Guid.NewGuid().ToString("N")
                });
                JObject addedCharacter = Require("session_add", new JObject
                {
                    ["kind"] = "character", ["character"] = character.name, ["session_id"] = session.Value<string>("session_id")
                })["character"] as JObject;
                JObject started = Require("kimodo_generate_animation", new JObject
                {
                    ["character"] = addedCharacter.Value<string>("name"),
                    ["prompt"] = "walk forward in a straight line at a natural relaxed pace",
                    ["duration_frames"] = 120,
                    ["name"] = "Armature_WalkForward",
                    ["analysis_option"] = new JObject
                    {
                        ["keyframes"] = new JObject { ["enabled"] = true },
                        ["keyframe_count"] = 8
                    }
                });

                string requestId = started.Value<string>("request_id");
                Assert.That(requestId, Is.Not.Null.And.Not.Empty);
                JObject generation = started;
                double deadline = EditorApplication.timeSinceStartup + 600d;
                string status;
                do
                {
                    Assert.That(EditorApplication.timeSinceStartup, Is.LessThan(deadline), "Timed out waiting for Armature walk-forward generation.");
                    yield return null;
                    generation = Require("kimodo_get_generation", new JObject { ["request_id"] = requestId });
                    status = generation.Value<string>("status");
                }
                while (status != "completed" && status != "failed" && status != "canceled");

                // A terminal status is published before the test runner gets
                // another editor tick. Drain that tick and read the terminal
                // result again so -quit cannot race the async continuation.
                yield return null;
                generation = Require("kimodo_get_generation", new JObject { ["request_id"] = requestId });
                status = generation.Value<string>("status");

                Assert.That(status, Is.EqualTo("completed"), "Armature walk-forward generation did not complete: " + generation.Value<string>("error"));
                string animationName = generation.Value<string>("animation") ?? started.Value<string>("animation");
                Assert.That(animationName, Is.Not.Null.And.Not.Empty);

                JObject analysis = Require("animation_analyze", new JObject
                {
                    ["clips"] = new JArray(new JObject
                    {
                        ["character"] = addedCharacter.Value<string>("name"), ["clip"] = animationName
                    }),
                    ["level"] = "middle", ["resolution"] = 512
                });
                string relativePng = analysis["pictures"]?.Value<string>("image_path");
                string absolutePng = string.IsNullOrWhiteSpace(relativePng)
                    ? string.Empty
                    : Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePng));
                Assert.That(relativePng, Is.Not.Null.And.EndsWith(".png"));
                Assert.That(relativePng.Replace('\\', '/'), Does.StartWith("Library/KimodoData/"));
                Assert.That(File.Exists(absolutePng), Is.True, "animation_analyze did not write its composite PNG.");
                Assert.That(new FileInfo(absolutePng).Length, Is.GreaterThan(0));
                Debug.Log("[Kimodo][Test] Generated Armature walk-forward analysis screenshot: " + absolutePng);
                LogRootTrajectoryContinuity(analysis, animationName);
            }
            finally
            {
                command_dispatcher.Invoke("session_close", "{}");
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        private static JObject Require(string command, JObject arguments)
        {
            JObject response = JObject.Parse(command_dispatcher.Invoke(command, arguments.ToString()));
            Assert.That(response.Value<bool?>("ok"), Is.True, command + " failed: " + response["error"]);
            return response;
        }

        private static void LogRootTrajectoryContinuity(JObject analysis, string clipName)
        {
            JArray samples = analysis["clips"]?.Children<JObject>()
                .FirstOrDefault(item => string.Equals(item.Value<string>("clip"), clipName, StringComparison.OrdinalIgnoreCase))?
                ["root_trajectory"]?["samples"] as JArray;
            if (samples == null || samples.Count < 3)
            {
                Assert.Fail("Root trajectory did not contain enough XZ heading samples for continuity logging.");
            }

            int middleStart = Mathf.Max(1, samples.Count / 4);
            int middleEnd = Mathf.Min(samples.Count - 1, (samples.Count * 3) / 4);
            float previousYaw = ReadHeadingYaw(samples[middleStart - 1]);
            Vector3 previousEuler = ReadRootEuler(samples[middleStart - 1]);
            float maxStep = 0f;
            int maxStepFrame = middleStart;
            float maxPitchStep = 0f;
            float maxRollStep = 0f;
            for (int index = middleStart; index <= middleEnd; index++)
            {
                JObject sample = samples[index] as JObject;
                float yaw = ReadHeadingYaw(sample);
                float step = Mathf.Abs(Mathf.DeltaAngle(previousYaw, yaw));
                Vector3 euler = ReadRootEuler(sample);
                float pitchStep = Mathf.Abs(Mathf.DeltaAngle(previousEuler.x, euler.x));
                float rollStep = Mathf.Abs(Mathf.DeltaAngle(previousEuler.z, euler.z));
                if (step > maxStep)
                {
                    maxStep = step;
                    maxStepFrame = sample?.Value<int?>("frame") ?? index;
                }
                maxPitchStep = Mathf.Max(maxPitchStep, pitchStep);
                maxRollStep = Mathf.Max(maxRollStep, rollStep);
                if (index == middleStart || index == middleEnd || index % 30 == 0)
                {
                    JArray heading = sample?["heading_xz"] as JArray;
                    float headingX = heading?[0]?.Value<float>() ?? 0f;
                    float headingZ = heading?[1]?.Value<float>() ?? 0f;
                    Debug.Log($"[Kimodo][RootXZ] clip={clipName} frame={sample?.Value<int?>("frame") ?? index} " +
                        $"heading=({headingX:F4},{headingZ:F4}) " +
                        $"yaw={yaw:F3}deg step={step:F3}deg " +
                        $"euler_xz=({euler.x:F3},{euler.z:F3})deg");
                }
                previousYaw = yaw;
                previousEuler = euler;
            }

            Debug.Log($"[Kimodo][RootXZ] clip={clipName} middle=[{middleStart},{middleEnd}] " +
                $"samples={samples.Count} max_single_frame_yaw_step={maxStep:F3}deg at_frame={maxStepFrame} " +
                $"max_pitch_step={maxPitchStep:F3}deg max_roll_step={maxRollStep:F3}deg");
            if (maxStep > 45f || maxPitchStep > 45f || maxRollStep > 45f)
            {
                Debug.LogWarning($"[Kimodo][RootXZ] Large middle-segment rotation step: " +
                    $"yaw={maxStep:F3}deg (frame {maxStepFrame}), pitch={maxPitchStep:F3}deg, roll={maxRollStep:F3}deg.");
            }
            Assert.That(maxStep, Is.LessThan(90f),
                $"Root XZ heading is discontinuous in the middle segment: {maxStep:F3}deg at frame {maxStepFrame}.");
        }

        private static float ReadHeadingYaw(JToken sample)
        {
            JArray heading = sample?["heading_xz"] as JArray;
            float x = heading?[0]?.Value<float>() ?? 0f;
            float z = heading?[1]?.Value<float>() ?? 1f;
            return Mathf.Atan2(x, z) * Mathf.Rad2Deg;
        }

        private static Vector3 ReadRootEuler(JToken sample)
        {
            JArray euler = sample?["root_rotation_delta_euler_degrees"] as JArray;
            return new Vector3(
                euler?[0]?.Value<float>() ?? 0f,
                euler?[1]?.Value<float>() ?? 0f,
                euler?[2]?.Value<float>() ?? 0f);
        }
    }
}
