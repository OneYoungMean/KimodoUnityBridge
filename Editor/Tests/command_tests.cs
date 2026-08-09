using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System;
using System.Linq;
using KimodoUnityBridge.Command;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class command_tests
    {
        [Test]
        public void CommandDefinitions_ExposeTheStableEntrypoints()
        {
            Assert.That(command_kimodo.ListModelsCommand, Is.EqualTo("Kimodo_list_models"));
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
                command_kimodo.ListCharactersCommand,
                command_kimodo.ListModelsCommand,
                command_kimodo.HelpCommand,
                command_kimodo.DebugInstallServerCommand,
                command_session.OpenTimelineCommand,
                command_session.CloseTimelineCommand,
                command_query.CurrentSessionCommand,
                command_session.LocateAnimationCommand,
                command_session.SamplePoseCommand,
                command_session.TryAddCommand,
                command_session.TryRemoveCommand,
                command_kimodo.AnalyzeTimelineRangeCommand,
                command_kimodo.BakeTimelineRangeCommand,
                command_kimodo.GenerateAnimationAssetCommand,
                command_kimodo.GetGenerationCommand,
                command_kimodo.CancelGenerationCommand
            }));
        }

        [Test]
        public void ModelListAndHelpSchemas_UseTheServerAsTheSourceOfTruth()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject modelList = definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.ListModelsCommand);
            JObject help = definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.HelpCommand);

            Assert.That(modelList.Value<string>("description"), Does.Contain("QuickServer"));
            Assert.That(help.Value<string>("description"), Does.Contain("protocol"));
            Assert.That(definitions["tools"].Values<JObject>()
                .Select(tool => tool.Value<string>("name")),
                Does.Not.Contain("kimodo_list_text_encoder_models"));
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
                .Single(tool => tool.Value<string>("name") == command_session.OpenTimelineCommand);
            JObject closeTool = definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_session.CloseTimelineCommand);
            JObject properties = (JObject)openTool["inputSchema"]["properties"];

            Assert.That(openTool.Value<string>("description"), Does.Contain("temporary TimelineAsset"));
            Assert.That(properties.Property("director_ref"), Is.Null);
            Assert.That(properties.Property("character_ref"), Is.Null);
            Assert.That(properties["session_name"].Value<string>("type"), Is.EqualTo("string"));
            Assert.That(closeTool.Value<string>("description"), Does.Contain("preserve the Session"));
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

        [Test]
        public void GetGeneration_UnknownRequest_ReturnsStructuredError()
        {
            JObject response = JObject.Parse(command_kimodo.GetGeneration(
                "{\"request_id\":\"00000000-0000-0000-0000-000000000001\"}"));
            Assert.That(response.Value<bool>("ok"), Is.False);
            Assert.That(response.Value<string>("error"), Does.Contain("Unknown or expired"));
        }

        [Test]
        public void GenerateCommands_ExposePoseConstraintArrays()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject assetProperties = (JObject)definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.GenerateAnimationAssetCommand)["inputSchema"]["properties"];
            Assert.That(assetProperties["pose_refs"]["items"].Value<string>("type"), Is.EqualTo("string"));
            Assert.That(assetProperties["times"]["items"].Value<string>("type"), Is.EqualTo("number"));
            Assert.That(assetProperties["constraint_types"]["items"]["enum"].Values<string>(),
                Is.EqualTo(new[] { "fullbody", "root2d" }));
            Assert.That(assetProperties["analysis_option"].Value<string>("type"), Is.EqualTo("object"));
            Assert.That(assetProperties["loop"].Value<string>("type"), Is.EqualTo("boolean"));
            Assert.That(assetProperties["model"].Value<string>("type"), Is.EqualTo("string"));
            Assert.That(assetProperties["text_encoder_model"]["enum"].Values<string>(),
                Is.EqualTo(new[] { "high_performance", "high_precision" }));
            Assert.That(assetProperties.Property("timeline_session_id"), Is.Null);
        }

        [Test]
        public void SessionSchemas_ExposeCurrentSessionQueriesAndBakeWithoutSessionId()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject query = definitions["tools"].Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_query.CurrentSessionCommand);
            JObject bake = definitions["tools"].Values<JObject>()
                .Single(tool => tool.Value<string>("name") == command_kimodo.BakeTimelineRangeCommand);

            Assert.That(query["inputSchema"]["properties"]["operation"]["enum"].Values<string>(),
                Does.Contain("animations"));
            Assert.That(query["inputSchema"]["properties"]["type"]["enum"].Values<string>(),
                Is.EqualTo(new[] { "session", "character", "animation" }));
            Assert.That(query["inputSchema"]["properties"]["pattern"].Value<string>("type"),
                Is.EqualTo("string"));
            Assert.That(query["inputSchema"]["properties"]["objects"]["items"].Value<string>("type"),
                Is.EqualTo("string"));
            Assert.That(query["inputSchema"]["properties"]["head"].Value<string>("type"),
                Is.EqualTo("integer"));
            Assert.That(bake["inputSchema"]["properties"]["retarget_character_ref"].Value<string>("type"),
                Is.EqualTo("string"));
            Assert.That(bake["inputSchema"]["properties"].Property("timeline_session_id"), Is.Null);
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
