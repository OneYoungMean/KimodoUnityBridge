using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine.Serialization;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoTextEncoderModeTests
    {
        [Test]
        public void LegacyValuesAndProtocolMappingRemainStable()
        {
            Assert.That((int)KimodoTextEncoderMode.HighPerformance, Is.EqualTo(0));
            Assert.That((int)KimodoTextEncoderMode.HighPrecision, Is.EqualTo(1));
            Assert.That(
                KimodoTextEncoderModeProtocol.ToProtocolValue(KimodoTextEncoderMode.HighPerformance),
                Is.EqualTo("high_performance"));
            Assert.That(
                KimodoTextEncoderModeProtocol.ToProtocolValue(KimodoTextEncoderMode.HighPrecision),
                Is.EqualTo("high_precision"));
        }

        [TestCase(typeof(KimodoPlayableClip), "textEncoderMode", "bridgeVramMode")]
        [TestCase(typeof(KimodoRuntimeMotionDriver), "textEncoderMode", "highVram")]
        [TestCase(typeof(KimodoPlayableClipGenerationSettings), "defaultTextEncoderMode", "defaultBridgeVramMode")]
        public void RenamedRuntimeFieldsDeclareLegacySerializedName(
            System.Type type,
            string fieldName,
            string legacyName)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            string[] oldNames = field
                .GetCustomAttributes<FormerlySerializedAsAttribute>()
                .Select(attribute => attribute.oldName)
                .ToArray();
            Assert.That(oldNames, Does.Contain(legacyName));
        }
    }
}
