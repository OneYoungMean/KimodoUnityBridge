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
        private const string YBotPath = PackageRoot + "/Editor/Model/T-Pose.fbx";
        private const string ArcWalkPath = PackageRoot + "/Editor/Tests/Fixtures/arc_walk_left_loop.anim";

        [Test]
        public void AnimationAnalyze_ArcWalkFixture_WritesCompositePng()
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(YBotPath);
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ArcWalkPath);
            Assert.That(source, Is.Not.Null, "Missing YBot T-pose fixture.");
            Assert.That(clip, Is.Not.Null, "Missing arc walk left loop fixture.");

            GameObject character = UnityEngine.Object.Instantiate(source);
            character.name = "KimodoTest_YBot";
            try
            {
                Animator animator = character.GetComponentInChildren<Animator>(true);
                Assert.That(animator, Is.Not.Null, "YBot fixture requires an Animator.");
                Assert.That(animator.avatar, Is.Not.Null.And.Property("isHuman").True, "YBot fixture requires a Humanoid Avatar.");
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
            }
            finally
            {
                command_dispatcher.Invoke("session_close", "{}");
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        [UnityTest]
        public IEnumerator AnimationGenerate_TPoseWalkForward_WritesCompositePng()
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(YBotPath);
            Assert.That(source, Is.Not.Null, "Missing YBot T-pose fixture.");

            GameObject character = UnityEngine.Object.Instantiate(source);
            character.name = "KimodoTest_YBot";
            try
            {
                Animator animator = character.GetComponentInChildren<Animator>(true);
                Assert.That(animator, Is.Not.Null, "YBot fixture requires an Animator.");
                Assert.That(animator.avatar, Is.Not.Null.And.Property("isHuman").True, "YBot fixture requires a Humanoid Avatar.");
                animator.runtimeAnimatorController = null;
                animator.Rebind();

                JObject session = Require("session_get_or_create", new JObject
                {
                    ["name"] = "AnimationGenerate_TPoseWalkForward_" + Guid.NewGuid().ToString("N")
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
                    ["name"] = "Tpose_WalkForward",
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
                    Assert.That(EditorApplication.timeSinceStartup, Is.LessThan(deadline), "Timed out waiting for T-pose walk-forward generation.");
                    yield return null;
                    generation = Require("kimodo_get_generation", new JObject { ["request_id"] = requestId });
                    status = generation.Value<string>("status");
                }
                while (status != "completed" && status != "failed" && status != "canceled");

                Assert.That(status, Is.EqualTo("completed"), "T-pose walk-forward generation did not complete: " + generation.Value<string>("error"));
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
                Debug.Log("[Kimodo][Test] Generated T-pose walk-forward analysis screenshot: " + absolutePng);
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
    }
}
