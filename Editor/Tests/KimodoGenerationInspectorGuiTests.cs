using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoGenerationInspectorGuiTests
    {
        [Test]
        public void ModelOptions_SeparateKimodoAndArdy()
        {
            string[] kimodo = KimodoGenerationInspectorGui.GetModelOptions(false);
            string[] ardy = KimodoGenerationInspectorGui.GetModelOptions(true);

            Assert.That(kimodo, Is.Not.Empty);
            Assert.That(ardy, Is.Not.Empty);
            Assert.That(kimodo.All(name => !KimodoGenerationInspectorGui.IsArdy(name)), Is.True);
            Assert.That(ardy.All(KimodoGenerationInspectorGui.IsArdy), Is.True);
            Assert.That(kimodo.Intersect(ardy), Is.Empty);
            Assert.That(ardy, Does.Contain(KimodoMotionModelProfiles.ArdyCore8ModelName));
            Assert.That(ardy, Does.Contain(KimodoMotionModelProfiles.ArdyG18ModelName));
        }

        [Test]
        public void PromptEdit_PreservesMixedValuesUntilTheUserChangesTheField()
        {
            KimodoPlayableClip first = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            KimodoPlayableClip second = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                first.motionPrompt = "walk forward";
                second.motionPrompt = "wave hello";
                var serializedClips = new SerializedObject(new UnityEngine.Object[] { first, second });
                SerializedProperty prompt = serializedClips.FindProperty("motionPrompt");

                Assert.That(prompt.hasMultipleDifferentValues, Is.True);
                KimodoGenerationInspectorGui.ApplyPromptEdit(prompt, prompt.stringValue, changed: false);
                serializedClips.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(first.motionPrompt, Is.EqualTo("walk forward"));
                Assert.That(second.motionPrompt, Is.EqualTo("wave hello"));

                KimodoGenerationInspectorGui.ApplyPromptEdit(prompt, "run", changed: true);
                serializedClips.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(first.motionPrompt, Is.EqualTo("run"));
                Assert.That(second.motionPrompt, Is.EqualTo("run"));
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [TestCase(KimodoPlayableClip.DefaultBridgeModelName, "Kimodo_Playable_20260730_120000_123")]
        [TestCase(KimodoMotionModelProfiles.ArdyCoreModelName, "ARDY_Playable_20260730_120000_123")]
        public void TimelineGeneratedClipName_IdentifiesModelFamily(string modelName, string expected)
        {
            Assert.That(
                KimodoPlayableClipGenerationHostService.BuildTimelineTargetClipName(
                    modelName,
                    new System.DateTime(2026, 7, 30, 12, 0, 0, 123)),
                Is.EqualTo(expected));
        }

        [Test]
        public void DisabledConstraint_IsIgnoredByNormalization()
        {
            KimodoFullBodyConstraintMarker marker = ScriptableObject.CreateInstance<KimodoFullBodyConstraintMarker>();
            try
            {
                Assert.That(marker.constraintEnabled, Is.True);
                marker.constraintEnabled = false;
                Assert.That(
                    KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, marker.SampleData),
                    Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void ArdyOutsideOut_AddsTimedRootTargetWithClipParameters()
        {
            KimodoPlayableClip clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                clip.inOutConstraintMode = KimodoInOutConstraintMode.Outside;
                clip.enableOutConstraint = true;
                clip.ardyTargetMaxSpeed = 2.25f;
                clip.ardyTargetMaxAcceleration = 3.5f;
                var samples = new List<KimodoMarkerSampleResult>
                {
                    new KimodoMarkerSampleResult
                    {
                        constraintType = "fullbody",
                        sampleTime = 2.0,
                        kimodoRootPosition = new Vector3(2f, 1f, 4f)
                    }
                };

                Assert.That(
                    KimodoPlayableClipGenerationHostService.AppendArdyOutsideOutRootTarget(
                        clip,
                        samples),
                    Is.False);

                clip.bridgeModelName = KimodoMotionModelProfiles.ArdyCoreModelName;
                Assert.That(
                    KimodoPlayableClipGenerationHostService.AppendArdyOutsideOutRootTarget(
                        clip,
                        samples),
                    Is.True);
                Assert.That(samples, Has.Count.EqualTo(2));

                KimodoConstraintJson target = KimodoConstraintJsonExporter.BuildConstraint(samples[1], 0.0, 2.0, 20.0);
                Assert.That(target.type, Is.EqualTo("root2d_target"));
                Assert.That(target.target_root_2d, Is.EqualTo(new[] { -2f, 4f }));
                Assert.That(target.target_frame, Is.EqualTo(39));
                Assert.That(target.max_speed, Is.EqualTo(2.25f));
                Assert.That(target.max_acceleration, Is.EqualTo(3.5f));
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }
    }
}
