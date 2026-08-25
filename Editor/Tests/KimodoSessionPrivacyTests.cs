using System;
using System.Reflection;
using KimodoUnityBridge.Command;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command.Tests
{
    /// <summary>
    /// Session identifiers and generated artifact paths must not echo a caller's
    /// personal display name. These tests intentionally pin the privacy boundary
    /// before the session naming implementation is changed.
    /// </summary>
    public sealed class KimodoSessionPrivacyTests
    {
        private const string PersonalIdentifier = "lin.zhang@example.test";

        [Test]
        public void SessionDescription_DoesNotEchoCallerProvidedPersonalIdentifier()
        {
            object session = CreateSessionRecord(PersonalIdentifier);
            MethodInfo describe = PrivateMethod("DescribeSession", typeof(object));

            JObject description = (JObject)describe.Invoke(null, new[] { session });

            Assert.That(description.ToString(), Does.Not.Contain(PersonalIdentifier));
        }

        [Test]
        public void SessionGeneratedFolder_DoesNotUseCallerProvidedPersonalIdentifier()
        {
            object session = CreateSessionRecord(PersonalIdentifier);
            MethodInfo resolveFolder = PrivateMethod("GetSessionGeneratedFolder", typeof(object));

            string folder = (string)resolveFolder.Invoke(null, new[] { session });

            Assert.That(folder, Does.Not.Contain(PersonalIdentifier));
        }

        private static object CreateSessionRecord(string name)
        {
            Type context = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            Type recordType = context?.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
            Assert.That(recordType, Is.Not.Null, "TimelineSessionRecord was removed or renamed.");

            ConstructorInfo constructor = recordType.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                new[]
                {
                    typeof(Guid), typeof(string), typeof(PlayableDirector), typeof(TimelineAsset),
                    typeof(string), typeof(bool),
                    context.Assembly.GetType("KimodoUnityBridge.Command.KimodoCommandSessionMetadata")
                },
                modifiers: null);
            Assert.That(constructor, Is.Not.Null, "TimelineSessionRecord constructor changed.");

            return constructor.Invoke(new object[]
            {
                Guid.NewGuid(), name, null, null, string.Empty, false, null
            });
        }

        private static MethodInfo PrivateMethod(string name, Type parameterType)
        {
            Type context = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            MethodInfo method = context?.GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic,
                binder: null,
                new[] { parameterType },
                modifiers: null);
            Assert.That(method, Is.Not.Null, $"private session privacy method {name} was removed or renamed");
            return method;
        }
    }
}
