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
        [Test, Category("BridgeRegression")]
        public void SessionClose_SchemaAndHelpExposeKeepObject()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject definition = definitions["tools"].Values<JObject>().Single(x => x.Value<string>("name") == "session_close");
            Assert.That(definition["inputSchema"]["properties"]["keepObject"].Value<string>("type"), Is.EqualTo("boolean"));
            JObject help = JObject.Parse(command_dispatcher.Invoke("kimodo_help", "{\"command\":\"session_close\"}"));
            Assert.That(help.Value<bool>("ok"), Is.True);
            Assert.That(help.ToString(), Does.Contain("keepObject"));
        }

        [TestCase("{}", true)]
        [TestCase("{\"keepObject\":true}", true)]
        [TestCase("{\"keepObject\":false}", false)]
        [Category("BridgeRegression")]
        public void SessionClose_ControlsOnlySessionSceneObjects(string arguments, bool keepObject)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            System.Type context = typeof(command_context);
            FieldInfo current = context.GetField("currentTimelineSession", flags);
            FieldInfo restored = context.GetField("timelineSessionsRestored", flags);
            var sessions = (System.Collections.IDictionary)context.GetField("TimelineSessions", flags).GetValue(null);
            object previous = current.GetValue(null);
            object wasRestored = restored.GetValue(null);
            string name = "CloseRegression_" + System.Guid.NewGuid().ToString("N");
            var root = new GameObject(name);
            var child = new GameObject("Director");
            child.transform.SetParent(root.transform);
            var director = child.AddComponent<UnityEngine.Playables.PlayableDirector>();
            var timeline = ScriptableObject.CreateInstance<UnityEngine.Timeline.TimelineAsset>();
            director.playableAsset = timeline;
            var unrelated = new GameObject("UnrelatedCloseRegression");
            try
            {
                System.Type recordType = context.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
                object record = System.Activator.CreateInstance(recordType, new object[]
                {
                    System.Guid.NewGuid(), name, director, timeline, string.Empty, false, null, root
                });
                sessions[name] = record;
                current.SetValue(null, record);
                restored.SetValue(null, true);
                JObject result = JObject.Parse(command_dispatcher.Invoke("session_close", arguments));
                Assert.That(result.Value<bool>("ok"), Is.True, result.ToString());
                Assert.That(result.Value<bool>("keepObject"), Is.EqualTo(keepObject));
                Assert.That(root != null, Is.EqualTo(keepObject));
                Assert.That(director != null, Is.EqualTo(keepObject));
                Assert.That(sessions.Contains(name), Is.EqualTo(keepObject));
                Assert.That(current.GetValue(null), Is.Null);
                Assert.That(timeline != null && unrelated != null, Is.True);
                if (keepObject)
                {
                    Assert.That(root.activeSelf, Is.False);
                    Assert.That(director.enabled, Is.False);
                }
            }
            finally
            {
                sessions.Remove(name);
                current.SetValue(null, previous);
                restored.SetValue(null, wasRestored);
                if (root != null) Object.DestroyImmediate(root);
                Object.DestroyImmediate(timeline);
                Object.DestroyImmediate(unrelated);
            }
        }

        [Test]
        public void CommandDefinitions_ExposeOnlyTheVNextSurface()
        {
            JObject json = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            string[] names = json["tools"].Values<JObject>().Select(value => value.Value<string>("name")).ToArray();
            CollectionAssert.AreEquivalent(new[]
            {
                "kimodo_help", "kimodo_install_server",
                "session_get_or_create", "session_get_raw", "session_add", "session_close",
                "kimodo_generate_animation", "kimodo_get_generation", "kimodo_cancel_generation",
                "animation_analyze",
                "pose_get", "pose_set", "pose_set_root_transform", "pose_set_muscle",
                "kimodo_record_range", "kimodo_retarget_animation"
            }, names);
        }

        [Test]
        public void AnimationAnalyzeSchema_UsesExplicitClipsAndPictureObject()
        {
            JObject json = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject schema = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "animation_analyze")["inputSchema"] as JObject;

            Assert.That(schema?["required"]?.Values<string>(), Does.Contain("clips"));
            Assert.That(schema?["properties"]?["clips"]?["minItems"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(schema?["properties"]?["clips"]?["maxItems"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(schema?["properties"]?["picture"]?.Value<string>("type"), Is.EqualTo("object"));
            JObject definition = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "animation_analyze");
            Assert.That(definition.Value<string>("description"), Does.Contain("phase_track_version"));
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
            Assert.That(generate?["properties"]?["loop"]?["oneOf"], Is.Not.Null);
            Assert.That(generate?["properties"]?["loop"]?["oneOf"]?.ToString(), Does.Contain("lock_pos"));
            Assert.That(generate?["properties"]?["loop"]?["oneOf"]?.ToString(), Does.Contain("lock_rot"));
            Assert.That(generate?["properties"]?["path"]?["properties"]?["distance"]?["minimum"]?.Value<float>(), Is.EqualTo(0f));
            Assert.That(generate?["properties"]?["generation"]?["properties"]?["text_encoder"], Is.Not.Null);
            Assert.That(generate?["properties"]?["output"]?["properties"]?["mode"]?["enum"]?.Values<string>(),
                Does.Contain("humanoid_muscle"));
            Assert.That(generate?["properties"]?["loop_lock_position_x"], Is.Null);
            Assert.That(generate?["properties"]?["path_begin_angle_degrees"], Is.Null);
            Assert.That(generate?["properties"]?["output_mode"], Is.Null);
            Assert.That(generate?["properties"]?["model"], Is.Null);
            JObject analyze = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "animation_analyze")["inputSchema"] as JObject;
            Assert.That(analyze?["properties"]?["analysis_option"], Is.Null);
            Assert.That(generate?["properties"]?["constraints"]?.ToString(), Does.Not.Contain("knots"));
            Assert.That(generate?["properties"]?["override_path_angle_degrees"], Is.Null);
            Assert.That(generate?["properties"]?["path_begin_angle_degrees"]?["type"]?.Value<string>(),
                Is.EqualTo("number"));
            Assert.That(generate?["properties"]?["path_end_angle_degrees"]?["type"]?.Value<string>(),
                Is.EqualTo("number"));
            Assert.That(generate?["properties"]?["override_heading_degrees"]?["type"]?.Value<string>(),
                Is.EqualTo("number"));
        }

        [Test]
        public void SessionGetRawSchema_ResolvesNamedSessionObjects()
        {
            JObject json = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject schema = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "session_get_raw")["inputSchema"] as JObject;

            Assert.That(schema?["required"]?.Values<string>(), Is.EquivalentTo(new[] { "kind", "name" }));
            Assert.That(schema?["properties"]?["kind"]?["enum"]?.Values<string>(),
                Is.EquivalentTo(new[] { "character", "track", "clip", "constraint" }));
            Assert.That(schema?["properties"]?["character"], Is.Not.Null);
        }

        [Test]
        public void GenerationResultManualDocumentsProjectRelativePath()
        {
            JObject json = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject definition = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "kimodo_get_generation");

            Assert.That(definition.Value<string>("description"), Does.Contain("asset path"));
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
                ?.GetMethod("BuildRootPathConstraintsSparse", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);
            var samples = ((IEnumerable<KimodoMarkerSampleResult>)method.Invoke(null, new object[]
            {
                path, 0, 0, 3, 1f, 1f, new HashSet<int>(), new HashSet<int>(), new HashSet<int>()
            })).ToList();

            Assert.That(samples, Has.Count.EqualTo(3));
            Assert.That(samples[0].rootOverride.t.z, Is.EqualTo(expectedStart).Within(0.001f));
            Assert.That(samples[2].rootOverride.t.z, Is.EqualTo(expectedEnd).Within(0.001f));
            Assert.That((samples[0].rootOverride.q * Vector3.forward).z,
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
                ?.GetMethod("BuildRootPathConstraintsSparse", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            var samples = ((IEnumerable<KimodoMarkerSampleResult>)method.Invoke(null, new object[]
            {
                path, 0, 0, 3, 1f, 4f, new HashSet<int>(), new HashSet<int>(), new HashSet<int>()
            })).ToList();

            Assert.That(samples[2].rootOverride.t.z, Is.EqualTo(4f).Within(0.001f));
            Assert.That((samples[1].rootOverride.q * Vector3.forward).x, Is.EqualTo(1f).Within(0.001f));
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
                ?.GetMethod("BuildRootPathConstraintsSparse", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            var samples = ((IEnumerable<KimodoMarkerSampleResult>)method.Invoke(null, new object[]
            {
                path, 0, 0, 2, 1f, 1f, new HashSet<int>(), new HashSet<int>(), new HashSet<int>()
            })).ToList();

            Assert.That(samples, Has.Count.EqualTo(2));
            Assert.That(samples.All(sample => sample.rootOverride.t == new Vector3(0f, 1f, 0f)), Is.True);
        }

        [Test]
        public void PoseSchema_UsesMaterializedTrackAndIndexReferences()
        {
            JObject json = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject poseGet = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "pose_get")["inputSchema"] as JObject;
            JObject poseSet = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "pose_set_root_transform")["inputSchema"] as JObject;
            JObject poseSetUnified = json["tools"].Values<JObject>()
                .Single(value => value.Value<string>("name") == "pose_set")["inputSchema"] as JObject;

            CollectionAssert.AreEquivalent(
                new[] { "character", "clip", "frame" },
                poseGet?["properties"]?["source"]?["required"]?.Values<string>());
            CollectionAssert.AreEquivalent(
                new[] { "track", "index" },
                poseSet?["properties"]?["pose"]?["required"]?.Values<string>());
            Assert.That(poseSet?["properties"]?["pose"]?["properties"]?["marker_id"], Is.Null);
            Assert.That(poseSetUnified?["properties"]?["root"], Is.Not.Null);
            Assert.That(poseSetUnified?["properties"]?["muscles"], Is.Not.Null);
            JObject effector = poseSetUnified?["properties"]?["effector"] as JObject;
            Assert.That(effector, Is.Not.Null);
            CollectionAssert.AreEquivalent(
                new[] { "left_hand", "right_hand", "left_foot", "right_foot" },
                effector?["properties"]?.Children<JProperty>().Select(property => property.Name));
        }

    }
}
