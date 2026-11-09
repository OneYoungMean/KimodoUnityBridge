using System.Reflection;
using KimodoBridge;
using KimodoBridge.Editor;
using NUnit.Framework;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintNormalizationUtilityTests
    {
        [Test]
        public void GenerationTypesHaveNoSyntheticBeginOrRelativeOffsetMembers()
        {
            const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            Assert.That(typeof(KimodoPlayableClip).GetField("autoBeginAnchor", members), Is.Null);
            Assert.That(typeof(KimodoPlayableClip).GetField("useRelativeOffset", members), Is.Null);
            Assert.That(typeof(KimodoPlayableClip).GetField("relativeOffset", members), Is.Null);
            Assert.That(typeof(KimodoEditorGenerateRequest).GetField("UseRelativeOffset", members), Is.Null);
            Assert.That(typeof(KimodoEditorGenerateRequest).GetField("RelativeOffset", members), Is.Null);
            Assert.That(typeof(KimodoTimelineClipConstraintBuilder).GetMethod("BuildBegin", members), Is.Null);
        }
    }
}
