using System;
using System.IO;
using KimodoBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

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
                JObject addedCharacter = Require("session_add", new JObject
                {
                    ["kind"] = "character", ["character"] = character.name, ["session_id"] = session.Value<string>("session_id")
                })["character"] as JObject;
                JObject addedClip = Require("session_add", new JObject
                {
                    ["kind"] = "clip", ["character"] = addedCharacter.Value<string>("name"), ["clip"] = ArcWalkPath
                })["animation"] as JObject;
                JObject analysis = Require("animation_analyze", new JObject
                {
                    ["clips"] = new JArray(new JObject
                    {
                        ["character"] = addedCharacter.Value<string>("name"), ["clip"] = addedClip.Value<string>("name")
                    }),
                    ["level"] = "-test", ["resolution"] = 512
                });

                string relativePng = analysis["pictures"]?.Value<string>("image_path");
                string absolutePng = string.IsNullOrWhiteSpace(relativePng)
                    ? string.Empty
                    : Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePng));
                Assert.That(relativePng, Is.Not.Null.And.EndsWith(".png"));
                Assert.That(File.Exists(absolutePng), Is.True, "animation_analyze did not write its composite PNG.");
                Assert.That(new FileInfo(absolutePng).Length, Is.GreaterThan(0));
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
