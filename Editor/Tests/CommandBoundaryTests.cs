using System;
using System.Linq;
using System.Reflection;
using KimodoBridge;
using KimodoUnityBridge.Command;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KimodoUnityBridge.Command.Tests
{
    /// <summary>
    /// Boundary tests that do not require a scene, Avatar, or QuickServer.
    /// Scene/asset lifecycle cases are listed in COMMAND_BOUNDARY_TEST_PLAN.md.
    /// </summary>
    public sealed class CommandBoundaryTests
    {
        private const string HelpAssetPath = "Packages/com.unity.kimodo_unity_motion_tools/Command/help.json";

        [Test]
        public void HelpJson_IsLoadableAndParseable()
        {
            TextAsset help = AssetDatabase.LoadAssetAtPath<TextAsset>(HelpAssetPath);

            Assert.That(help, Is.Not.Null, HelpAssetPath);
            Assert.DoesNotThrow(() => JObject.Parse(help.text));
        }

        [Test]
        public void Definitions_AreLoadedFromHelpJsonAndMatchDispatcherSurface()
        {
            TextAsset help = AssetDatabase.LoadAssetAtPath<TextAsset>(HelpAssetPath);
            JObject published = JObject.Parse(help.text);
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());

            Assert.That(JToken.DeepEquals(definitions, published), Is.True);
            CollectionAssert.AreEquivalent(new[]
            {
                "kimodo_help", "kimodo_install_server",
                "session_get_or_create", "session_add", "session_close",
                "kimodo_generate_animation", "kimodo_get_generation", "kimodo_cancel_generation",
                "animation_analyze", "animation_compare",
                "pose_get", "pose_create_path", "pose_contract", "pose_set_root_transform", "pose_set_muscle",
                "kimodo_record_range", "kimodo_retarget_animation"
            }, definitions["tools"].Values<JObject>().Select(tool => tool.Value<string>("name")));
        }

        [Test]
        public void Invoke_NullOrUnknownCommand_ReturnsStableFailureEnvelope()
        {
            AssertFailure(command_dispatcher.Invoke(null, "{}"), "unknown_command");
            AssertFailure(command_dispatcher.Invoke("does_not_exist", "{}"), "unknown_command");
            AssertFailure(command_dispatcher.Invoke("pose_copy", "{}"), "unknown_command");
        }

        [Test]
        public void Invoke_MalformedOrNonObjectArguments_ReturnsInvalidArgument()
        {
            AssertFailure(command_dispatcher.Invoke("kimodo_help", "{"), "invalid_argument");
            AssertFailure(command_dispatcher.Invoke("kimodo_help", "[]"), "invalid_argument");
            AssertFailure(command_dispatcher.Invoke("kimodo_help", "null"), "invalid_argument");
        }

        [Test]
        public void Help_NormalizesCommandAndSectionWhitespace()
        {
            JObject response = JObject.Parse(command_dispatcher.Invoke(
                "  kimodo_help  ",
                "{\"section\":\"  CoMmAnDs  \"}"));

            Assert.That(response.Value<bool>("ok"), Is.True);
            Assert.That(response["manual"], Is.Not.Null);
        }

        [Test]
        public void Help_RejectsUnknownCommandAndSection()
        {
            AssertFailure(
                command_dispatcher.Invoke("kimodo_help", "{\"command\":\"not_a_command\"}"),
                "invalid_argument");
            AssertFailure(
                command_dispatcher.Invoke("kimodo_help", "{\"section\":\"not_a_section\"}"),
                "invalid_argument");
        }

        [Test]
        public void Definitions_HaveClosedSchemasAndConsistentRequiredFields()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JArray tools = (JArray)definitions["tools"];

            Assert.That(tools, Is.Not.Null);
            Assert.That(tools.Values<JObject>().Select(tool => tool.Value<string>("name")).Distinct().Count(),
                Is.EqualTo(tools.Count), "command names must be unique");

            foreach (JObject tool in tools.Values<JObject>())
            {
                JObject schema = (JObject)tool["inputSchema"];
                Assert.That(schema?.Value<string>("type"), Is.EqualTo("object"), tool.Value<string>("name"));
                Assert.That(schema?.Value<bool?>("additionalProperties"), Is.False, tool.Value<string>("name"));

                JObject properties = (JObject)schema["properties"] ?? new JObject();
                foreach (string required in (schema["required"] as JArray ?? new JArray()).Values<string>())
                {
                    Assert.That(properties[required], Is.Not.Null,
                        $"{tool.Value<string>("name")} requires undeclared property {required}");
                }
            }
        }

        [Test]
        public void Definitions_ExposeTheImportantNumericBoundaries()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject Tool(string name) => definitions["tools"].Values<JObject>()
                .Single(tool => tool.Value<string>("name") == name);

            JObject analyze = (JObject)Tool("animation_analyze")["inputSchema"];
            Assert.That(analyze["properties"]["clips"]["minItems"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(analyze["properties"]["clips"]["maxItems"]?.Value<int>(), Is.EqualTo(2));
            Assert.That(analyze["properties"]["resolution"]["minimum"]?.Value<int>(), Is.EqualTo(64));
            Assert.That(analyze["properties"]["resolution"]["maximum"]?.Value<int>(), Is.EqualTo(4096));
            Assert.That(analyze["properties"]["resolution"]?.Value<string>("type"), Is.EqualTo("integer"));
            Assert.That(analyze["properties"]["level"]["enum"]?.Values<string>(),
                Is.EquivalentTo(new[] { "low", "middle", "high" }));

            JObject generate = (JObject)Tool("kimodo_generate_animation")["inputSchema"];
            Assert.That(generate["properties"]["output_mode"]["enum"]?.Values<string>(),
                Is.EquivalentTo(new[] { "humanoid_muscle", "character_bone", "model_bone" }));

            JObject path = (JObject)Tool("pose_create_path")["inputSchema"];
            Assert.That(path["properties"]["knots"]["minItems"]?.Value<int>(), Is.EqualTo(2));

            JObject poseReference = (JObject)Tool("pose_set_root_transform")["inputSchema"];
            Assert.That(poseReference["properties"]["pose"]["properties"]["index"]["minimum"]?.Value<int>(),
                Is.EqualTo(0));
        }

        [TestCase("humanoid_muscle")]
        [TestCase("character_bone")]
        [TestCase("model_bone")]
        public void ParseOutputMode_AcceptsSupportedValues(string value)
        {
            Assert.That(command_context.ParseOutputMode(value), Is.EqualTo(value));
        }

        [TestCase("unsupported")]
        [TestCase("humanoid-muscle")]
        public void ParseOutputMode_RejectsUnsupportedValues(string value)
        {
            Assert.Throws<InvalidOperationException>(() => command_context.ParseOutputMode(value));
        }

        [TestCase("high_performance", KimodoTextEncoderMode.HighPerformance)]
        [TestCase("high_precision", KimodoTextEncoderMode.HighPrecision)]
        [TestCase(" HIGH-PRECISION ", KimodoTextEncoderMode.HighPrecision)]
        public void ResolveTextEncoderMode_NormalizesSupportedValues(
            string value,
            KimodoTextEncoderMode expected)
        {
            Assert.That(command_context.ResolveTextEncoderMode(value), Is.EqualTo(expected));
        }

        [Test]
        public void ResolveTextEncoderMode_RejectsUnknownValue()
        {
            Assert.Throws<InvalidOperationException>(() => command_context.ResolveTextEncoderMode("balanced"));
        }

        [Test]
        public void AnalysisResolution_UsesDefaultAndRejectsOutsideInclusiveBounds()
        {
            MethodInfo method = PrivateMethod("ResolveAnalysisPictureResolution", typeof(JToken));
            Assert.That(method.Invoke(null, new object[] { null }), Is.EqualTo(512));
            Assert.That(method.Invoke(null, new object[] { new JValue(64) }), Is.EqualTo(64));
            Assert.That(method.Invoke(null, new object[] { new JValue(4096) }), Is.EqualTo(4096));
            AssertPrivateFailure(method, new JValue(63), "between 64 and 4096");
            AssertPrivateFailure(method, new JValue(4097), "between 64 and 4096");
            AssertPrivateFailure(method, new JValue(64.5), "positive integer");
        }

        [Test]
        public void AnalysisLevel_NormalizesSupportedValuesAndRejectsUnknownValue()
        {
            MethodInfo method = PrivateMethod("NormalizeAnalysisPictureLevel", typeof(string));
            Assert.That(method.Invoke(null, new object[] { "  HIGH " }), Is.EqualTo("high"));
            Assert.That(method.Invoke(null, new object[] { null }), Is.EqualTo("middle"));
            AssertPrivateFailure(method, "ultra", "level must be");
            AssertPrivateFailure(method, "-test", "level must be");
        }

        [Test]
        public void RequiredRequestId_OnlyAcceptsNonEmptyGuids()
        {
            MethodInfo method = PrivateMethod("RequiredRequestId", typeof(JObject));
            Guid expected = Guid.NewGuid();
            Assert.That(method.Invoke(null, new object[] { new JObject { ["request_id"] = expected.ToString("D") } }),
                Is.EqualTo(expected));
            AssertPrivateFailure(method, new JObject(), "request_id is required");
            AssertPrivateFailure(method, new JObject { ["request_id"] = "not-a-guid" }, "not a valid GUID");
        }

        [Test]
        public void RequiredVector2_RequiresExactlyTwoFiniteNumbers()
        {
            MethodInfo method = PrivateMethod("RequiredVector2", typeof(JObject), typeof(string));
            Vector2 value = (Vector2)method.Invoke(null, new object[] {
                new JObject { ["v"] = new JArray(1.25, -2) }, "v" });
            Assert.That(value.x, Is.EqualTo(1.25f).Within(0.0001f));
            Assert.That(value.y, Is.EqualTo(-2f).Within(0.0001f));
            AssertPrivateFailure(method, new object[] { new JObject { ["v"] = new JArray(1) }, "v" }, "must be [x,z]");
            AssertPrivateFailure(method, new object[] { new JObject { ["v"] = new JArray("x", 2) }, "v" }, "finite numbers");
        }

        private static MethodInfo PrivateMethod(string name, params Type[] parameterTypes)
        {
            Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
            MethodInfo method = context?.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic, null, parameterTypes, null);
            Assert.That(method, Is.Not.Null, $"private validator {name} was removed or renamed");
            return method;
        }

        private static void AssertPrivateFailure(MethodInfo method, object argument, string message)
        {
            AssertPrivateFailure(method, new[] { argument }, message);
        }

        private static void AssertPrivateFailure(MethodInfo method, object[] arguments, string message)
        {
            TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
                () => method.Invoke(null, arguments));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(exception.InnerException.Message, Does.Contain(message));
        }

        private static void AssertFailure(string json, string code)
        {
            JObject response = JObject.Parse(json);
            Assert.That(response.Value<bool>("ok"), Is.False);
            Assert.That(response["error"]?.Value<string>("code"), Is.EqualTo(code));
            Assert.That(response["error"]?.Value<string>("message"), Is.Not.Null.And.Not.Empty);
        }
    }
}
