using System.Collections.Generic;
using System.Reflection;
using KimodoBridge;
using KimodoBridge.Editor;
using NUnit.Framework;
using KimodoUnityBridge;

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

        [Test]
        public void ValidConstraintInFirstSecondSuppressesAutoBegin()
        {
            KimodoMarkerSampleResult sample = MakeSample("root2d", 0.5, enabled: true, valid: true);

            Assert.That(
                KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    new List<KimodoMarkerSampleResult> { sample }, 1.0),
                Is.True);
        }

        [Test]
        public void ConstraintAtOneSecondDoesNotSuppressAutoBegin()
        {
            KimodoMarkerSampleResult sample = MakeSample("root2d", 1.0, enabled: true, valid: true);

            Assert.That(
                KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    new List<KimodoMarkerSampleResult> { sample }, 1.0),
                Is.False);
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void DisabledOrInvalidConstraintDoesNotSuppressAutoBegin(bool enabled, bool valid)
        {
            KimodoMarkerSampleResult sample = MakeSample("root2d", 0.5, enabled, valid);

            Assert.That(
                KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    new List<KimodoMarkerSampleResult> { sample }, 1.0),
                Is.False);
        }

        [Test]
        public void MissingRootDoesNotSuppressAutoBegin()
        {
            KimodoMarkerSampleResult sample = MakeSample("root2d", 0.5, enabled: true, valid: true);
            sample.rootOverride = null;

            Assert.That(
                KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    new List<KimodoMarkerSampleResult> { sample }, 1.0),
                Is.False);
        }

        [Test]
        public void EffectorWithoutEnabledChannelDoesNotSuppressAutoBegin()
        {
            KimodoMarkerSampleResult sample = MakeSample("effector", 0.5, enabled: true, valid: true);
            sample.enableMask = new KimodoConstraintMask();

            Assert.That(
                KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    new List<KimodoMarkerSampleResult> { sample }, 1.0),
                Is.False);
        }

        [Test]
        public void EffectorWithMissingEnabledTargetDoesNotSuppressAutoBegin()
        {
            KimodoMarkerSampleResult sample = MakeSample("effector", 0.5, enabled: true, valid: true);
            sample.enableMask = new KimodoConstraintMask { rightHand = true };
            sample.effectors.rightHand = null;

            Assert.That(
                KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    new List<KimodoMarkerSampleResult> { sample }, 1.0),
                Is.False);
        }

        [TestCase("fullbody")]
        [TestCase("root2d")]
        [TestCase("effector")]
        public void ValidConstraintFamiliesSuppressAutoBegin(string mode)
        {
            KimodoMarkerSampleResult sample = MakeSample(mode, 0.5, enabled: true, valid: true);
            if (mode == "effector")
            {
                sample.enableMask.rightHand = true;
            }

            Assert.That(
                KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    new List<KimodoMarkerSampleResult> { sample }, 1.0),
                Is.True);
        }

        private static KimodoMarkerSampleResult MakeSample(
            string mode,
            double sampleTime,
            bool enabled,
            bool valid)
        {
            return new KimodoMarkerSampleResult
            {
                constraintMode = mode,
                sampleTime = sampleTime,
                enabled = enabled,
                sampleValid = valid,
                sampleData = new MuscleSample(),
                enableMask = KimodoConstraintMask.ForType(mode),
                rootOverride = KimodoRigidTransform.Identity,
                effectors = new KimodoConstraintEffectors()
            };
        }
    }
}
