using System;
using System.Collections.Generic;
using System.Linq;
using KimodoUnityBridge.Command;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace KimodoUnityBridge.Command.Tests
{
    /// <summary>
    /// Contract checks for the public command registry, help output, and
    /// compatibility facades. These cases intentionally avoid creating a
    /// Session or starting QuickServer.
    /// </summary>
    public sealed class CommandContractTests
    {
        [TestCase("")]
        [TestCase(" ")]
        [TestCase("\t\n")]
        public void Invoke_EmptyOrWhitespaceCommand_ReturnsStableFailureEnvelope(string command)
        {
            AssertFailure(command_dispatcher.Invoke(command, "{}"), "unknown_command");
        }

        [Test]
        public void Help_CommandsSectionMirrorsDefinitions()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject response = JObject.Parse(command_dispatcher.Invoke(
                "kimodo_help",
                "{\"section\":\" commands \"}"));

            Assert.That(response.Value<bool>("ok"), Is.True);
            JArray commands = response["commands"] as JArray;
            Assert.That(commands, Is.Not.Null);

            var definitionByName = definitions["tools"].Values<JObject>()
                .ToDictionary(tool => tool.Value<string>("name"), StringComparer.Ordinal);
            Assert.That(commands.Count, Is.EqualTo(definitionByName.Count));
            foreach (JObject command in commands.Values<JObject>())
            {
                string name = command.Value<string>("name");
                Assert.That(definitionByName.ContainsKey(name), Is.True, name);
                CollectionAssert.AreEqual(
                    definitionByName[name]["inputSchema"]?["required"]?.Values<string>() ?? Enumerable.Empty<string>(),
                    command["required"]?.Values<string>() ?? Enumerable.Empty<string>(),
                    name);
                Assert.That(command.Value<string>("description"), Is.Not.Null.And.Not.Empty, name);
            }
        }

        [Test]
        public void Help_ConstraintsSectionExposesStableManualShape()
        {
            JObject response = JObject.Parse(command_dispatcher.Invoke(
                "kimodo_help",
                "{\"section\":\"constraints\"}"));

            Assert.That(response.Value<bool>("ok"), Is.True);
            Assert.That(response["manual"]?.Value<string>(), Is.EqualTo("Kimodo generation constraint reference"));
            Assert.That(response["constraints"], Is.TypeOf<JArray>());
            Assert.That(response["rules"], Is.TypeOf<JArray>());
            Assert.That(response["constraints"].Values<JObject>().Select(item => item.Value<string>("type")),
                Is.EquivalentTo(new[] { "fullbody", "root2d", "root_path" }));
            Assert.That(response["rules"].Values<string>(), Has.All.Not.Empty);
        }

        [Test]
        public void Help_CommandManualIsThePublishedDefinition()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            JObject expected = definitions["tools"].Values<JObject>()
                .Single(tool => tool.Value<string>("name") == "pose_get");
            JObject response = JObject.Parse(command_dispatcher.Invoke(
                "kimodo_help",
                "{\"command\":\" pose_get \"}"));

            Assert.That(response.Value<bool>("ok"), Is.True);
            Assert.That(response.Value<string>("usage"), Is.EqualTo("pose_get(<arguments matching inputSchema>)"));
            Assert.That(JToken.DeepEquals(response["manual"], expected), Is.True);
        }

        [Test]
        public void Definitions_AndDispatcherRecognizeTheSameCommandNames()
        {
            JObject definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            var sideEffecting = new HashSet<string>(StringComparer.Ordinal)
            {
                "kimodo_install_server",
                "session_get_or_create",
                "session_close"
            };

            foreach (string name in definitions["tools"].Values<JObject>()
                .Select(tool => tool.Value<string>("name")))
            {
                if (sideEffecting.Contains(name))
                {
                    continue;
                }

                JObject response = JObject.Parse(command_dispatcher.Invoke($"  {name}  ", "{}"));
                Assert.That(response["error"]?.Value<string>("code"), Is.Not.EqualTo("unknown_command"), name);
                Assert.That(response.Value<bool?>("ok"), Is.Not.Null, name);
            }
        }

        [Test]
        public void PublicFacadesRemainCallableWithoutBypassingTheErrorEnvelope()
        {
            AssertFailure(command_kimodo.Help("{"), "invalid_argument");
            AssertFailure(command_kimodo.InstallServer("{"), "invalid_argument");
            AssertFailure(command_kimodo.GenerateAnimation("{"), "invalid_argument");
            AssertFailure(command_kimodo.Analyze("{"), "invalid_argument");
            AssertFailure(command_kimodo.Compare("{"), "invalid_argument");
            AssertFailure(command_kimodo.RecordRange("{"), "invalid_argument");
            AssertFailure(command_kimodo.RetargetAnimation("{"), "invalid_argument");
            AssertFailure(command_kimodo.GetGeneration("{"), "invalid_argument");
            AssertFailure(command_kimodo.CancelGeneration("{"), "invalid_argument");
            AssertFailure(command_session.GetOrCreate("{"), "invalid_argument");
            AssertFailure(command_session.Add("{"), "invalid_argument");
            AssertFailure(command_session.Close("{"), "invalid_argument");
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
