using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using KimodoUnityBridge.Command;
using TimelineInject;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class command_tests
    {
        [Test]
        public void CommandDefinitions_ExposeTheStableEntrypoints()
        {
            Assert.That(command_kimodo.GetGenerationCommand, Is.EqualTo("kimodo_get_generation"));
            Assert.That(command_kimodo.CancelGenerationCommand, Is.EqualTo("kimodo_cancel_generation"));
            Assert.That(command_kimodo.DebugInstallServerCommand, Is.EqualTo("kimodo_debug_install_server"));

            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            var names = definitions["tools"]
                .Values<JObject>()
                .Select(tool => tool.Value<string>("name"))
                .ToArray();

            Assert.That(names, Is.EqualTo(new[]
            {
                command_kimodo.HelpCommand,
                command_kimodo.DebugInstallServerCommand,
                command_session.OpenCommand,
                command_session.CloseCommand,
                command_query.CurrentSessionCommand,
                command_session.TryAddCommand,
                command_session.TryRemoveCommand,
                command_kimodo.AnalyzeCommand,
                command_kimodo.BakeRangeCommand,
                command_kimodo.GenerateAnimationCommand,
                command_kimodo.QueryPictureCommand,
                "pose_create",
                "pose_get",
                "pose_set",
                "pose_copy",
                "kimodo_build_root2d_path",
                command_kimodo.GetGenerationCommand,
                command_kimodo.CancelGenerationCommand
            }));
        }

        [Test]
        public void HelpSchema_ProvidesCommandManualAndModelsSection()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject help = definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.HelpCommand);

            Assert.That(help.Value<string>("description"), Does.Contain("manual"));
            Assert.That(help["inputSchema"]["properties"]["section"]["enum"].Values<string>(),
                Is.EqualTo(new[] { "commands", "models", "constraints" }));
            Assert.That(definitions["tools"].Values<JObject>()
                .Select(tool => tool.Value<string>("name")),
                Does.Not.Contain("kimodo_list_text_encoder_models"));
        }

        [Test]
        public void HelpInvocation_ProvidesRunnableAnimationWorkflow()
        {
            JObject response = JObject.Parse(command_dispatcher.Invoke(command_kimodo.HelpCommand));
            Assert.That(response.Value<bool>("ok"), Is.True);

            JArray workflow = (JArray)response["workflow"];
            Assert.That(workflow.Values<string>("command"), Is.EqualTo(new[]
            {
                command_session.OpenCommand,
                command_query.CurrentSessionCommand,
                command_kimodo.GenerateAnimationCommand,
                command_kimodo.GetGenerationCommand,
                command_session.CloseCommand
            }));
            Assert.That(workflow[1]["arguments"].Value<string>("query"), Is.EqualTo("characters"));
            Assert.That(workflow[2]["arguments"].Value<int>("duration_frames"), Is.EqualTo(60));
            Assert.That(workflow[3].Value<string>("repeat_until"), Does.Contain("completed"));
            Assert.That(response["constraints"].Values<JObject>().Select(item => item.Value<string>("type")),
                Is.EqualTo(new[] { "fullbody", "root2d" }));
            Assert.That(response["constraint_rules"].Values<string>().Single(), Does.Contain("root2d"));
        }

        [Test]
        public void WritablePoseMarker_HasAUnityScriptAsset()
        {
            var marker = ScriptableObject.CreateInstance<KimodoUntypedConstraintMarker>();
            try
            {
                Assert.That(MonoScript.FromScriptableObject(marker), Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void ConstraintHelp_DescribesFullBodyAndRoot2D()
        {
            JObject response = JObject.Parse(command_kimodo.Help("{\"section\":\"constraints\"}"));
            Assert.That(response.Value<bool>("ok"), Is.True);
            Assert.That(response["constraints"].Values<JObject>().Select(item => item.Value<string>("type")),
                Is.EqualTo(new[] { "fullbody", "root2d" }));
            Assert.That(response["constraints"][0].Value<string>("description"), Does.Contain("root bone"));
            Assert.That(response["constraints"][1].Value<string>("description"), Does.Contain("root-only"));
        }

        [Test]
        public void Root2DProtocol_UsesSemanticForwardRightYawFields()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject generate = definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.GenerateAnimationCommand);
            JObject itemProperties = (JObject)generate["inputSchema"]["properties"]["constraints"]["items"]["properties"];

            Assert.That(itemProperties.Property("forwardPos"), Is.Not.Null);
            Assert.That(itemProperties.Property("rightwardPos"), Is.Not.Null);
            Assert.That(itemProperties.Property("rotateY"), Is.Not.Null);
            Assert.That(itemProperties.Property("position"), Is.Null);
            Assert.That(itemProperties.Property("heading"), Is.Null);

            JObject path = JObject.Parse(command_context.BuildRoot2DPath(
                "{\"shape\":\"line\",\"duration_frames\":60}"));
            JArray points = (JArray)path["points"];
            JObject point = (JObject)points[points.Count - 1];
            Assert.That(point.Property("forwardPos"), Is.Not.Null);
            Assert.That(point.Property("rightwardPos"), Is.Not.Null);
            Assert.That(point.Property("rotateY"), Is.Not.Null);
            Assert.That(point.Property("position"), Is.Null);
            Assert.That(point.Property("heading"), Is.Null);
            Assert.That(point.Value<float>("forwardPos"), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(point.Value<float>("rightwardPos"), Is.EqualTo(path.Value<float>("distance")).Within(1e-5f));
            Assert.That(point.Value<float>("rotateY"), Is.EqualTo(90f).Within(1e-5f));
        }

        [Test]
        public void PoseRootYaw_UpdatePreservesTiltAndChangesOnlyWorldYaw()
        {
            Quaternion original = Quaternion.Euler(20f, 35f, -10f);
            float originalTilt = Vector3.Angle(original * Vector3.up, Vector3.up);

            Quaternion updated = command_context.ApplyPoseRootYaw(original, -70f);

            Assert.That(
                Mathf.Abs(Mathf.DeltaAngle(command_context.ResolvePoseRootYaw(updated), -70f)),
                Is.LessThan(1e-4f));
            Assert.That(
                Vector3.Angle(updated * Vector3.up, Vector3.up),
                Is.EqualTo(originalTilt).Within(1e-4f));
        }

        [Test]
        public void PoseConstraintFkConversion_PreservesProtocolRootOnly()
        {
            var source = new KimodoMarkerSampleResult
            {
                kimodoRootPosition = new Vector3(2f, 0.95f, 3f),
                rootHeading = new Vector2(0.6f, 0.8f),
                hasRootHeading = true,
                localAxisAngles = new List<Vector3> { new Vector3(0f, 0.7f, 0f) },
                unityRootPos = new Vector3(20f, 21f, 22f),
                unityRootRot = Quaternion.Euler(10f, 20f, 30f)
            };
            var originalUnityRootPosition = new Vector3(4f, 5f, 6f);
            var originalUnityRootRotation = Quaternion.Euler(1f, 2f, 3f);
            var destination = new KimodoMarkerSampleResult
            {
                kimodoRootPosition = Vector3.zero,
                rootHeading = Vector2.right,
                hasRootHeading = false,
                localAxisAngles = new List<Vector3> { Vector3.zero, new Vector3(0.1f, 0.2f, 0.3f) },
                unityRootPos = originalUnityRootPosition,
                unityRootRot = originalUnityRootRotation
            };

            command_context.PreservePoseConstraintRoot(source, destination);

            Assert.That(destination.kimodoRootPosition, Is.EqualTo(source.kimodoRootPosition));
            Assert.That(destination.rootHeading, Is.EqualTo(source.rootHeading));
            Assert.That(destination.hasRootHeading, Is.True);
            Assert.That(destination.localAxisAngles[0], Is.EqualTo(source.localAxisAngles[0]));
            Assert.That(destination.localAxisAngles[1], Is.EqualTo(new Vector3(0.1f, 0.2f, 0.3f)));
            Assert.That(destination.unityRootPos, Is.EqualTo(originalUnityRootPosition));
            Assert.That(destination.unityRootRot, Is.EqualTo(originalUnityRootRotation));
        }

        [Test]
        public void DebugInstallServer_IsExplicitlyMarkedAndTakesNoArguments()
        {
            JObject definition = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson())["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.DebugInstallServerCommand);

            Assert.That(definition.Value<bool>("debug_only"), Is.True);
            Assert.That(definition["inputSchema"]["required"].HasValues, Is.False);
            Assert.That(definition.Value<string>("description"), Does.Contain("debug-only"));
            Assert.That(definition.Value<string>("description"), Does.Contain("Python environment"));
        }

        [Test]
        public void OpenTimelineSessionSchema_UsesACurrentTemporaryTimelineAsset()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject openTool = definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_session.OpenCommand);
            JObject closeTool = definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_session.CloseCommand);
            JObject properties = (JObject)openTool["inputSchema"]["properties"];

            Assert.That(openTool.Value<string>("description"), Does.Not.Contain("Timeline"));
            Assert.That(properties.Property("director_ref"), Is.Null);
            Assert.That(properties.Property("character_ref"), Is.Null);
            Assert.That(properties["session_name"].Value<string>("type"), Is.EqualTo("string"));
            Assert.That(closeTool.Value<string>("description"), Does.Contain("preserving"));
            Assert.That(closeTool.Value<string>("description"), Does.Not.Contain("delete"));
        }

        [TestCase(null, "humanoid_muscle")]
        [TestCase("character_bone", "character_bone")]
        [TestCase("model_bone", "model_bone")]
        public void ParseOutputMode_UsesTheSupportedVariants(string input, string expected)
        {
            Assert.That(command_context.ParseOutputMode(input), Is.EqualTo(expected));
        }

        [Test]
        public void ParseOutputMode_RejectsUnknownMode()
        {
            Assert.Throws<System.InvalidOperationException>(() => command_context.ParseOutputMode("muscle_and_bones"));
        }

        [TestCase(null, "Assets/KimodoGeneratedClips")]
        [TestCase("Assets/My Clips", "Assets/My Clips")]
        public void NormalizeOutputFolder_StaysUnderAssets(string input, string expected)
        {
            Assert.That(command_context.NormalizeOutputFolder(input), Is.EqualTo(expected));
        }

        [TestCase("C:/outside", TestName = "RejectsOutsideAssets")]
        [TestCase("Assets/../Library", TestName = "RejectsTraversal")]
        public void NormalizeOutputFolder_RejectsUnsafePath(string input)
        {
            Assert.Throws<System.InvalidOperationException>(() => command_context.NormalizeOutputFolder(input));
        }

        [Test]
        public void InvalidInvocation_ReturnsStructuredError()
        {
            JObject response = JObject.Parse(command_dispatcher.Invoke("kimodo_unknown", "{}"));
            Assert.That(response.Value<bool>("ok"), Is.False);
            Assert.That(response.Value<string>("error"), Does.Contain("Unknown"));
        }

        [TestCase(10, 20, 15, 25, true)]
        [TestCase(10, 20, 20, 25, false)]
        [TestCase(20, 25, 10, 20, false)]
        public void GenerationRangesOverlap_UsesHalfOpenRanges(
            int firstStart,
            int firstEnd,
            int secondStart,
            int secondEnd,
            bool expected)
        {
            Assert.That(command_context.GenerationRangesOverlap(firstStart, firstEnd, secondStart, secondEnd), Is.EqualTo(expected));
        }

        [Test]
        public void ClipSafeZone_IsFourFramesAtSessionRate()
        {
            Assert.That(command_context.ClipSafeZoneFrames, Is.EqualTo(4));
            Assert.That(command_context.ClipSafeZoneSeconds, Is.EqualTo(4.0 / 60.0).Within(1e-9));
        }

        [Test]
        public void GetGeneration_UnknownRequest_ReturnsStructuredError()
        {
            JObject response = JObject.Parse(command_kimodo.GetGeneration(
                "{\"request_id\":\"00000000-0000-0000-0000-000000000001\"}"));
            Assert.That(response.Value<bool>("ok"), Is.False);
            Assert.That(response.Value<string>("error"), Does.Contain("Unknown or expired"));
        }

        [Test]
        public void GenerateCommands_ExposeAtomicConstraintObjects()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject assetProperties = (JObject)definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.GenerateAnimationCommand)["inputSchema"]["properties"];
            Assert.That(assetProperties["constraints"]["items"]["required"].Values<string>(),
                Is.EqualTo(new[] { "frame", "type" }));
            Assert.That(assetProperties["constraints"]["items"]["properties"]["type"]["enum"].Values<string>(),
                Is.EqualTo(new[] { "fullbody", "root2d", "left_hand", "right_hand", "left_foot", "right_foot" }));
            Assert.That(assetProperties["analysis_option"].Value<string>("type"), Is.EqualTo("object"));
            Assert.That(assetProperties.Property("loop"), Is.Null);
            Assert.That(assetProperties["model"].Value<string>("type"), Is.EqualTo("string"));
            Assert.That(assetProperties["text_encoder_model"]["enum"].Values<string>(),
                Is.EqualTo(new[] { "high_performance", "high_precision" }));
            Assert.That(assetProperties.Property("timeline_session_id"), Is.Null);
            Assert.That(definitions["tools"].Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.GenerateAnimationCommand)
                .Value<string>("description"), Does.Contain("KimodoPlayableClip"));
        }

        [Test]
        public void SessionSchemas_ExposeCurrentSessionQueriesAndBakeWithoutSessionId()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject query = definitions["tools"].Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_query.CurrentSessionCommand);
            JObject bake = definitions["tools"].Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.BakeRangeCommand);

            JObject queryProperties = (JObject)query["inputSchema"]["properties"];
            Assert.That(queryProperties.Properties().Select(property => property.Name),
                Is.EqualTo(new[] { "query", "character", "animation" }));
            Assert.That(queryProperties["query"]["enum"].Values<string>(), Is.EqualTo(new[]
            {
                "characters", "character_animations", "animation", "character_constraints", "animation_constraints",
                "animation_transitions", "transition"
            }));
            Assert.That(bake["inputSchema"]["properties"]["retarget_character"].Value<string>("type"),
                Is.EqualTo("string"));
            Assert.That(bake["inputSchema"]["properties"]["speed"].Value<string>("type"),
                Is.EqualTo("number"));
            Assert.That(((JObject)bake["inputSchema"]["properties"]).Property("timeline_session_id"), Is.Null);
        }

        [Test]
        public void AnalyzeAndPictureSchemas_ExposeTheUnifiedSources()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject analyze = definitions["tools"].Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.AnalyzeCommand);
            JObject picture = definitions["tools"].Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.QueryPictureCommand);

            Assert.That(analyze["inputSchema"]["required"].Values<string>(), Is.EqualTo(new[] { "character" }));
            Assert.That(analyze["inputSchema"]["properties"]["animation"].Value<string>("type"), Is.EqualTo("string"));
            Assert.That(analyze["inputSchema"]["properties"]["start_frame"].Value<string>("type"), Is.EqualTo("integer"));
            Assert.That(definitions["tools"].Values<JObject>().Select(item => item.Value<string>("name")),
                Does.Not.Contain("kimodo_analyze_range"));
            Assert.That(picture["inputSchema"]["properties"]["poses"]["items"]["required"].Values<string>(),
                Is.EqualTo(new[] { "source", "frame" }));
            Assert.That(picture["inputSchema"]["properties"]["constraints"]["items"]["required"].Values<string>(),
                Is.EqualTo(new[] { "frame", "type" }));
        }

        [Test]
        public void ResolvePoseConstraintTimes_DistributesAcrossFirstAndLastFrame()
        {
            Assert.That(
                command_context.ResolvePoseConstraintTimes(1, 4, 1f, null),
                Is.EqualTo(new[] { 0.0 }));
            Assert.That(
                command_context.ResolvePoseConstraintTimes(2, 4, 1f, null),
                Is.EqualTo(new[] { 0.0, 3.0 }));
            Assert.That(
                command_context.ResolvePoseConstraintTimes(4, 4, 1f, null),
                Is.EqualTo(new[] { 0.0, 1.0, 2.0, 3.0 }));
        }

        [Test]
        public void ResolvePoseConstraintTimes_RequiresMatchingCount()
        {
            Assert.Throws<InvalidOperationException>(() =>
                command_context.ResolvePoseConstraintTimes(2, 30, 30f, new[] { 0.0 }));
        }

        [Test]
        public void ResolvePoseConstraintTypes_DefaultsToFullBodyAndRequiresMatchingCount()
        {
            Assert.That(
                command_context.ResolvePoseConstraintTypes(2, null),
                Is.EqualTo(new[] { "fullbody", "fullbody" }));
            Assert.That(
                command_context.ResolvePoseConstraintTypes(2, new[] { "root2d", "FULLBODY" }),
                Is.EqualTo(new[] { "root2d", "fullbody" }));
            Assert.Throws<InvalidOperationException>(() =>
                command_context.ResolvePoseConstraintTypes(2, new[] { "root2d" }));
            Assert.Throws<InvalidOperationException>(() =>
                command_context.ResolvePoseConstraintTypes(1, new[] { "left-hand" }));
        }

        [TestCase("high_performance", KimodoTextEncoderMode.HighPerformance)]
        [TestCase("high-precision", KimodoTextEncoderMode.HighPrecision)]
        public void ResolveTextEncoderMode_UsesListedProfiles(string value, KimodoTextEncoderMode expected)
        {
            Assert.That(command_context.ResolveTextEncoderMode(value), Is.EqualTo(expected));
        }

        [Test]
        public void ResolveTextEncoderMode_RejectsUnknownProfile()
        {
            Assert.Throws<InvalidOperationException>(() => command_context.ResolveTextEncoderMode("fp8"));
        }
    }
}
