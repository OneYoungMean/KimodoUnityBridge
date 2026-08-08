using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System;
using System.Linq;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoMcpToolsTests
    {
        [Test]
        public void ToolDefinitions_ExposeTheStableEntrypoints()
        {
            JObject definitions = JObject.Parse(KimodoMcpTools.GetToolDefinitionsJson());
            var names = definitions["tools"]
                .Values<JObject>()
                .Select(tool => tool.Value<string>("name"))
                .ToArray();

            Assert.That(names, Is.EqualTo(new[]
            {
                KimodoMcpTools.ListCharactersTool,
                KimodoMcpTools.ListModelsTool,
                KimodoMcpTools.HelpTool,
                KimodoMcpTools.ReinstallServerTool,
                KimodoMcpTools.OpenTimelineSessionTool,
                KimodoMcpTools.CloseTimelineSessionTool,
                KimodoMcpTools.GenerateAnimationAssetTool,
                KimodoMcpTools.GenerateTimelineAnimationTool,
                KimodoMcpTools.GetGenerationTool,
                KimodoMcpTools.CancelGenerationTool
            }));
        }

        [Test]
        public void ModelListAndHelpSchemas_UseTheServerAsTheSourceOfTruth()
        {
            JObject definitions = JObject.Parse(KimodoMcpTools.GetToolDefinitionsJson());
            JObject modelList = definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == KimodoMcpTools.ListModelsTool);
            JObject help = definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == KimodoMcpTools.HelpTool);

            Assert.That(modelList.Value<string>("description"), Does.Contain("QuickServer"));
            Assert.That(help.Value<string>("description"), Does.Contain("protocol"));
            Assert.That(definitions["tools"].Values<JObject>()
                .Select(tool => tool.Value<string>("name")),
                Does.Not.Contain("kimodo_list_text_encoder_models"));
        }

        [Test]
        public void OpenTimelineSessionSchema_UsesATemporaryTimelineAsset()
        {
            JObject definitions = JObject.Parse(KimodoMcpTools.GetToolDefinitionsJson());
            JObject openTool = definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == KimodoMcpTools.OpenTimelineSessionTool);
            JObject closeTool = definitions["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == KimodoMcpTools.CloseTimelineSessionTool);
            JObject properties = (JObject)openTool["inputSchema"]["properties"];

            Assert.That(openTool.Value<string>("description"), Does.Contain("temporary TimelineAsset"));
            Assert.That(properties.Property("track_ref"), Is.Null);
            Assert.That(properties["start_seconds"].Value<string>("description"), Does.Contain("defaults to 0"));
            Assert.That(closeTool.Value<string>("description"), Does.Contain("delete its temporary TimelineAsset"));
        }

        [TestCase(null, "humanoid_muscle")]
        [TestCase("character_bone", "character_bone")]
        [TestCase("model_bone", "model_bone")]
        public void ParseOutputMode_UsesTheSupportedVariants(string input, string expected)
        {
            Assert.That(KimodoMcpTools.ParseOutputMode(input), Is.EqualTo(expected));
        }

        [Test]
        public void ParseOutputMode_RejectsUnknownMode()
        {
            Assert.Throws<System.InvalidOperationException>(() => KimodoMcpTools.ParseOutputMode("muscle_and_bones"));
        }

        [TestCase(null, "Assets/KimodoGeneratedClips")]
        [TestCase("Assets/My Clips", "Assets/My Clips")]
        public void NormalizeOutputFolder_StaysUnderAssets(string input, string expected)
        {
            Assert.That(KimodoMcpTools.NormalizeOutputFolder(input), Is.EqualTo(expected));
        }

        [TestCase("C:/outside", TestName = "RejectsOutsideAssets")]
        [TestCase("Assets/../Library", TestName = "RejectsTraversal")]
        public void NormalizeOutputFolder_RejectsUnsafePath(string input)
        {
            Assert.Throws<System.InvalidOperationException>(() => KimodoMcpTools.NormalizeOutputFolder(input));
        }

        [Test]
        public void InvalidInvocation_ReturnsStructuredError()
        {
            JObject response = JObject.Parse(KimodoMcpTools.Invoke("kimodo_unknown", "{}"));
            Assert.That(response.Value<bool>("ok"), Is.False);
            Assert.That(response.Value<string>("error"), Does.Contain("Unknown"));
        }

        [Test]
        public void GetGeneration_UnknownRequest_ReturnsStructuredError()
        {
            JObject response = JObject.Parse(KimodoMcpTools.GetGeneration(
                "{\"request_id\":\"00000000-0000-0000-0000-000000000001\"}"));
            Assert.That(response.Value<bool>("ok"), Is.False);
            Assert.That(response.Value<string>("error"), Does.Contain("Unknown or expired"));
        }

        [Test]
        public void GenerateTools_ExposePoseConstraintArrays()
        {
            JObject definitions = JObject.Parse(KimodoMcpTools.GetToolDefinitionsJson());
            JObject[] generateTools = definitions["tools"]
                .Values<JObject>()
                .Where(tool => tool.Value<string>("name") == KimodoMcpTools.GenerateAnimationAssetTool ||
                    tool.Value<string>("name") == KimodoMcpTools.GenerateTimelineAnimationTool)
                .ToArray();

            Assert.That(generateTools, Has.Length.EqualTo(2));
            foreach (JObject tool in generateTools)
            {
                JObject properties = (JObject)tool["inputSchema"]["properties"];
                Assert.That(properties["pose_refs"]["items"].Value<string>("type"), Is.EqualTo("string"));
                Assert.That(properties["times"]["items"].Value<string>("type"), Is.EqualTo("number"));
                Assert.That(
                    properties["constraint_types"]["items"]["enum"].Values<string>(),
                    Is.EqualTo(new[] { "fullbody", "root2d" }));
                Assert.That(properties["analysis_options"].Value<string>("type"), Is.EqualTo("object"));
                Assert.That(properties["model"].Value<string>("type"), Is.EqualTo("string"));
                Assert.That(properties["text_encoder_model"]["enum"].Values<string>(),
                    Is.EqualTo(new[] { "high_performance", "high_precision" }));
            }
            JObject assetProperties = (JObject)generateTools
                .Single(tool => tool.Value<string>("name") == KimodoMcpTools.GenerateAnimationAssetTool)["inputSchema"]["properties"];
            Assert.That(assetProperties["timeline_session_id"].Value<string>("type"), Is.EqualTo("string"));
        }

        [Test]
        public void ResolvePoseConstraintTimes_DistributesAcrossFirstAndLastFrame()
        {
            Assert.That(
                KimodoMcpTools.ResolvePoseConstraintTimes(1, 4, 1f, null),
                Is.EqualTo(new[] { 0.0 }));
            Assert.That(
                KimodoMcpTools.ResolvePoseConstraintTimes(2, 4, 1f, null),
                Is.EqualTo(new[] { 0.0, 3.0 }));
            Assert.That(
                KimodoMcpTools.ResolvePoseConstraintTimes(4, 4, 1f, null),
                Is.EqualTo(new[] { 0.0, 1.0, 2.0, 3.0 }));
        }

        [Test]
        public void ResolvePoseConstraintTimes_RequiresMatchingCount()
        {
            Assert.Throws<InvalidOperationException>(() =>
                KimodoMcpTools.ResolvePoseConstraintTimes(2, 30, 30f, new[] { 0.0 }));
        }

        [Test]
        public void ResolvePoseConstraintTypes_DefaultsToFullBodyAndRequiresMatchingCount()
        {
            Assert.That(
                KimodoMcpTools.ResolvePoseConstraintTypes(2, null),
                Is.EqualTo(new[] { "fullbody", "fullbody" }));
            Assert.That(
                KimodoMcpTools.ResolvePoseConstraintTypes(2, new[] { "root2d", "FULLBODY" }),
                Is.EqualTo(new[] { "root2d", "fullbody" }));
            Assert.Throws<InvalidOperationException>(() =>
                KimodoMcpTools.ResolvePoseConstraintTypes(2, new[] { "root2d" }));
            Assert.Throws<InvalidOperationException>(() =>
                KimodoMcpTools.ResolvePoseConstraintTypes(1, new[] { "left-hand" }));
        }

        [TestCase("high_performance", KimodoTextEncoderMode.HighPerformance)]
        [TestCase("high-precision", KimodoTextEncoderMode.HighPrecision)]
        public void ResolveTextEncoderMode_UsesListedProfiles(string value, KimodoTextEncoderMode expected)
        {
            Assert.That(KimodoMcpTools.ResolveTextEncoderMode(value), Is.EqualTo(expected));
        }

        [Test]
        public void ResolveTextEncoderMode_RejectsUnknownProfile()
        {
            Assert.Throws<InvalidOperationException>(() => KimodoMcpTools.ResolveTextEncoderMode("fp8"));
        }
    }
}
