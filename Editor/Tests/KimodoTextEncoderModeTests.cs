using NUnit.Framework;

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

        [Test]
        public void NewKimodoRequestsAndClipsDefaultToHighPerformance()
        {
            KimodoPlayableClip clip = UnityEngine.ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                Assert.That(clip.textEncoderMode, Is.EqualTo(KimodoTextEncoderMode.HighPerformance));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void NewKimodoClipsKeepStartOffsetByDefault()
        {
            KimodoPlayableClip clip = UnityEngine.ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                Assert.That(clip.removeStartOffset, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

    }
}
