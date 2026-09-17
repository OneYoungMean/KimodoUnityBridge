using System;
using System.Reflection;
using KimodoUnityBridge.Command;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class AnalysisCaptureRequestTests
    {
        private static Type RequestType => typeof(command_dispatcher).Assembly
            .GetType("KimodoUnityBridge.Command.command_context")
            .GetNestedType("AnalysisPictureRequest", BindingFlags.NonPublic);

        [TestCase("preserve")]
        [TestCase("isolated")]
        public void Environment_SurvivesCommandAndInspectorRoundTrips(string mode)
        {
            var value = new JObject
            {
                ["tiles"] = new JArray("key_pose"),
                ["output"] = "both",
                ["environment"] = new JObject { ["mode"] = mode }
            };
            for (int i = 0; i < 3; i++)
            {
                object request = RequestType.GetMethod("Parse").Invoke(null, new object[] { value });
                value = (JObject)RequestType.GetMethod("ToJson").Invoke(request, null);
                Assert.That(value["environment"]?["mode"]?.Value<string>(), Is.EqualTo(mode));
                Assert.That(value["tiles"]?[0]?.Value<string>(), Is.EqualTo("key_pose"));
                Assert.That(value.Value<string>("output"), Is.EqualTo("both"));
            }
        }

        [Test]
        public void Environment_DefaultRemainsIsolated()
        {
            object request = RequestType.GetMethod("Parse").Invoke(null, new object[] { null });
            Assert.That(RequestType.GetProperty("Environment").GetValue(request), Is.EqualTo("isolated"));
        }
    }
}
