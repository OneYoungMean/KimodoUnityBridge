using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KimodoBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class CommandVNextTests
    {
        [Test]
        public void CommandDefinitions_ExposeOnlyTheVNextSurface()
        {
            JObject json = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            string[] names = json["tools"].Values<JObject>().Select(value => value.Value<string>("name")).ToArray();
            CollectionAssert.AreEquivalent(new[]
            {
                "kimodo_help", "kimodo_install_server",
                "session_get_or_create", "session_add", "session_close",
                "kimodo_generate_animation", "kimodo_get_generation", "kimodo_cancel_generation",
                "animation_analyze", "animation_compare",
                "pose_get", "pose_contract", "pose_set_root_transform", "pose_set_muscle",
                "kimodo_record_range", "kimodo_retarget_animation"
            }, names);
        }

        [Test]
        public void AnimationAnalyzeSchema_UsesExplicitClipsAndMiddleByDefault()
        {
            JObject json = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject schema = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "animation_analyze")["inputSchema"] as JObject;

            Assert.That(schema?["required"]?.Values<string>(), Does.Contain("clips"));
            Assert.That(schema?["properties"]?["clips"]?["minItems"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(schema?["properties"]?["clips"]?["maxItems"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(schema?["properties"]?["level"]?.Value<string>("default"), Is.EqualTo("middle"));
        }

        [Test]
        public void GenerationSchema_UsesCurrentSessionAndSupportsRootPath()
        {
            JObject json = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject generate = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "kimodo_generate_animation")["inputSchema"] as JObject;
            JObject get = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "kimodo_get_generation")["inputSchema"] as JObject;

            Assert.That(generate?["properties"]?["session_id"], Is.Null);
            Assert.That(get?["properties"]?["session_id"], Is.Null);
            Assert.That(generate?["properties"]?["constraints"]?.ToString(), Does.Contain("root_path"));
            Assert.That(generate?["properties"]?["constraints"]?.ToString(), Does.Contain("path"));
            Assert.That(generate?["properties"]?["constraints"]?.ToString(), Does.Not.Contain("knots"));
            Assert.That(generate?["properties"]?["override_path_angle_degrees"], Is.Null);
            Assert.That(generate?["properties"]?["path_begin_angle_degrees"]?["type"]?.Value<string>(),
                Is.EqualTo("number"));
            Assert.That(generate?["properties"]?["path_end_angle_degrees"]?["type"]?.Value<string>(),
                Is.EqualTo("number"));
            Assert.That(generate?["properties"]?["override_heading_degrees"]?["type"]?.Value<string>(),
                Is.EqualTo("number"));
        }

        [TestCase(false, 0f, 5f, 1f)]
        [TestCase(true, 5f, 0f, -1f)]
        public void RootPathCompilation_AppliesLengthAndInverse(
            bool inverse,
            float expectedStart,
            float expectedEnd,
            float expectedHeading)
        {
            var path = new KimodoRootPathData
            {
                type = "forward",
                length = 5f,
                inverse = inverse,
                knots = new List<KimodoRootPathKnot>
                {
                    new KimodoRootPathKnot { position = Vector2.zero },
                    new KimodoRootPathKnot { position = Vector2.up }
                }
            };
            MethodInfo method = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context")
                ?.GetMethod("BuildRootPathConstraints", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);
            var samples = ((IEnumerable<KimodoMarkerSampleResult>)method.Invoke(null, new object[]
            {
                path, 0, 0, 3, 1f, 1f, new HashSet<int>(), new HashSet<int>()
            })).ToList();

            Assert.That(samples, Has.Count.EqualTo(3));
            Assert.That(samples[0].root2DOverride.t.z, Is.EqualTo(expectedStart).Within(0.001f));
            Assert.That(samples[2].root2DOverride.t.z, Is.EqualTo(expectedEnd).Within(0.001f));
            Assert.That((samples[0].root2DOverride.q * Vector3.forward).z,
                Is.EqualTo(expectedHeading).Within(0.001f));
        }

        [Test]
        public void RootPathCompilation_RetargetsCharacterScaleAndUsesStoredHeading()
        {
            var path = new KimodoRootPathData
            {
                type = "analyzed",
                length = 2f,
                sourceHumanScale = 2f,
                knots = new List<KimodoRootPathKnot>
                {
                    new KimodoRootPathKnot { position = Vector2.zero, hasHeading = true, heading = Vector2.right },
                    new KimodoRootPathKnot { position = Vector2.up * 2f, hasHeading = true, heading = Vector2.right }
                }
            };
            MethodInfo method = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context")
                ?.GetMethod("BuildRootPathConstraints", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            var samples = ((IEnumerable<KimodoMarkerSampleResult>)method.Invoke(null, new object[]
            {
                path, 0, 0, 3, 1f, 4f, new HashSet<int>(), new HashSet<int>()
            })).ToList();

            Assert.That(samples[2].root2DOverride.t.z, Is.EqualTo(4f).Within(0.001f));
            Assert.That((samples[1].root2DOverride.q * Vector3.forward).x, Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void RootPathCompilation_AllowsStationaryAnalyzedTrajectory()
        {
            var path = new KimodoRootPathData
            {
                type = "analyzed",
                length = 0f,
                sourceHumanScale = 1f,
                knots = new List<KimodoRootPathKnot>
                {
                    new KimodoRootPathKnot { position = Vector2.zero, hasHeading = true, heading = Vector2.up }
                }
            };
            MethodInfo method = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context")
                ?.GetMethod("BuildRootPathConstraints", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            var samples = ((IEnumerable<KimodoMarkerSampleResult>)method.Invoke(null, new object[]
            {
                path, 0, 0, 2, 1f, 1f, new HashSet<int>(), new HashSet<int>()
            })).ToList();

            Assert.That(samples, Has.Count.EqualTo(2));
            Assert.That(samples.All(sample => sample.root2DOverride.t == new Vector3(0f, 1f, 0f)), Is.True);
        }

        [Test]
        public void PoseSchema_UsesMaterializedTrackAndIndexReferences()
        {
            JObject json = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject poseGet = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "pose_get")["inputSchema"] as JObject;
            JObject poseSet = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "pose_set_root_transform")["inputSchema"] as JObject;

            CollectionAssert.AreEquivalent(
                new[] { "character", "clip", "frame" },
                poseGet?["properties"]?["source"]?["required"]?.Values<string>());
            CollectionAssert.AreEquivalent(
                new[] { "track", "index" },
                poseSet?["properties"]?["pose"]?["required"]?.Values<string>());
            Assert.That(poseSet?["properties"]?["pose"]?["properties"]?["marker_id"], Is.Null);
        }

    }
}
