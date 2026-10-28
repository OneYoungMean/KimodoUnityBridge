using System.Collections.Generic;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using KimodoUnityBridge;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class KimodoEditorConstraintProviderTests
    {
        [Test]
        public void RawConstraintValidation_RejectsFastRootTrajectory()
        {
            string json = new JArray
            {
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(0, 1),
                    ["root_positions"] = new JArray(
                        new JArray(0f, 0f, 0f),
                        new JArray(0f, 0f, 1f))
                }
            }.ToString();

            Assert.That(
                KimodoPostApplyOutConstraintUtility.TryValidateRawConstraintRootMotion(
                    json,
                    30f,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("30.000 m/s"));
        }

        [Test]
        public void RawConstraintValidation_RejectsAccelerationEvenWhenSpeedIsWithinLimit()
        {
            string json = new JArray
            {
                CreateRootConstraint(new[] { 0, 6, 12 }, new[] { 0f, 0f, 0.2f })
            }.ToString();

            Assert.That(
                KimodoPostApplyOutConstraintUtility.TryValidateRawConstraintRootMotion(
                    json,
                    60f,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("20.000 m/s²"));
        }

        [Test]
        public void RawConstraintValidation_UsesNonUniformLagrangeAcceleration()
        {
            string json = new JArray
            {
                CreateRootConstraint(new[] { 0, 60, 66 }, new[] { 0f, 0f, 0.2f })
            }.ToString();

            Assert.That(
                KimodoPostApplyOutConstraintUtility.TryValidateRawConstraintRootMotion(
                    json,
                    60f,
                    out string error),
                Is.True,
                error);
        }

        [Test]
        public void RawConstraintValidation_RejectsNonUniformLagrangeAcceleration()
        {
            string json = new JArray
            {
                CreateRootConstraint(new[] { 0, 6, 66 }, new[] { 0f, 0f, 6f })
            }.ToString();

            Assert.That(
                KimodoPostApplyOutConstraintUtility.TryValidateRawConstraintRootMotion(
                    json,
                    60f,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("10.909 m/s²"));
        }

        [Test]
        public void RawConstraintValidation_DoesNotAssumeZeroVelocityBeforeFirstSample()
        {
            string json = new JArray
            {
                CreateRootConstraint(new[] { 0, 6 }, new[] { 0f, 0.2f })
            }.ToString();

            Assert.That(
                KimodoPostApplyOutConstraintUtility.TryValidateRawConstraintRootMotion(
                    json,
                    60f,
                    out string error),
                Is.True,
                error);
        }

        [Test]
        public void PostApplyInOutSameFrame_GivesStartConstraintPriority()
        {
            string inJson = CreateRootConstraint(0, 1f);
            string outJson = CreateRootConstraint(0, 2f);

            JArray result = JArray.Parse(KimodoPostApplyOutConstraintUtility.ApplyInOut(
                "[]", inJson, outJson, null, KimodoMotionModelProfiles.DefaultModelName,
                30f, 0, null));

            Assert.That(result, Has.Count.EqualTo(1), result.ToString());
            Assert.That(result[0]["frame_indices"][0].Value<int>(), Is.Zero);
            Assert.That(result[0]["root_positions"][0][2].Value<float>(), Is.EqualTo(1f));
        }

        [Test]
        public void PostApplySparseWindow_PreservesImmediateConstraintsBetweenSamples()
        {
            string existing = new JArray
            {
                new JObject
                {
                    ["type"] = "effector",
                    ["frame_indices"] = new JArray(0, 5, 10),
                    ["target_positions"] = new JArray(
                        new JArray(0f, 0f, 0f), new JArray(5f, 0f, 0f), new JArray(10f, 0f, 0f))
                }
            }.ToString();
            string deferredIn = new JArray
            {
                CreateRootConstraint(new[] { 0, 10 }, new[] { 1f, 2f })
            }.ToString();

            JArray result = JArray.Parse(KimodoPostApplyOutConstraintUtility.ApplyInOut(
                existing, deferredIn, "[]", null,
                KimodoMotionModelProfiles.DefaultModelName, 30f, 0, null));
            JObject effector = result.Values<JObject>().Single(item => item.Value<string>("type") == "effector");
            CollectionAssert.AreEqual(new[] { 5 }, effector["frame_indices"].Values<int>().ToArray());
            Assert.That(effector["target_positions"].Count(), Is.EqualTo(1));
            Assert.That(effector["target_positions"][0][0].Value<float>(), Is.EqualTo(5f));
        }

        [TestCase(true, 0, 90, new[] { 0, 5, 11 }, true,
            TestName = "InsideIn_AnchorsAtWindowStart")]
        [TestCase(false, 0, 90, new[] { 78, 84, 89 }, false,
            TestName = "InsideOut_AnchorsAtWindowEnd")]
        [TestCase(true, 12, 90, new[] { 0, 5, 11 }, false,
            TestName = "OutsideIn_AnchorsAtWindowEndNearSeam")]
        [TestCase(false, 0, 90, new[] { 90, 95, 101 }, true,
            TestName = "OutsideOut_AnchorsAtWindowStartNearSeam")]
        public void DeferredWindow_ChoosesTheSampleNearestToTheOutputSeam(
            bool isIn,
            int trimStart,
            int targetFrames,
            int[] sampleFrames,
            bool expectedPreferStart)
        {
            Assert.That(
                KimodoPostApplyOutConstraintUtility.PreferStartForDeferredWindow(
                    sampleFrames, trimStart, targetFrames, isIn),
                Is.EqualTo(expectedPreferStart));
        }

        [Test]
        public void PostApplyTrajectoryPass_RequiresAnEnabledDeferredBoundary()
        {
            var clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                clip.inOutConstraintMode = KimodoInOutConstraintMode.Inside;
                clip.postApplyInConstraint = true;
                clip.enableInConstraint = false;
                clip.enableOutConstraint = true;

                Assert.That(KimodoPlayableClipGenerationExecutionService.RequiresTrajectoryPass(clip, null), Is.False);

                clip.enableInConstraint = true;
                Assert.That(KimodoPlayableClipGenerationExecutionService.RequiresTrajectoryPass(clip, null), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void GenerationStage_AppliesPassesSequentiallyToInheritedConstraintSet()
        {
            var request = new KimodoEditorGenerateRequest();
            request.Prompt = "generate one stage";
            request.Constraints.json = new JArray
            {
                new JObject { ["pass"] = "stage1" }
            }.ToString();
            var stage = new KimodoEditorGenerationStage(2, "Path overrides", request);
            stage.AddPass(new KimodoEditorGenerationPass("Path", constraints =>
                KimodoClipConstraintBakeUtility.AppendConstraintsJson(
                    constraints,
                    new JArray(new JObject { ["pass"] = "path" }).ToString())));
            stage.AddPass(new KimodoEditorGenerationPass("Heading", constraints =>
                KimodoClipConstraintBakeUtility.AppendConstraintsJson(
                    constraints,
                    new JArray(new JObject { ["pass"] = "heading" }).ToString())));

            JArray composed = JArray.Parse(stage.ApplyPasses());

            CollectionAssert.AreEqual(
                new[] { "stage1", "path", "heading" },
                composed.Children<JObject>().Select(item => item.Value<string>("pass")));
            Assert.That(stage.Passes.Count, Is.EqualTo(2));
            Assert.That(JToken.DeepEquals(JToken.Parse(request.Constraints.json), composed), Is.True);
        }

        [Test]
        public void ReplaceRoot2DConstraintsJson_ReplacesRootChannelAndPreservesOtherConstraintTypes()
        {
            string original = new JArray
            {
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(0),
                    ["smooth_root_2d"] = new JArray(new JArray(1f, 2f))
                },
                new JObject
                {
                    ["type"] = "effector",
                    ["frame_indices"] = new JArray(1),
                    ["target_positions"] = new JArray(new JArray(3f, 4f, 5f))
                }
            }.ToString();
            string replacement = new JArray
            {
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(2),
                    ["smooth_root_2d"] = new JArray(new JArray(6f, 7f))
                }
            }.ToString();

            JArray result = JArray.Parse(KimodoClipConstraintBakeUtility.ReplaceRoot2DConstraintsJson(
                original,
                replacement));

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result.Count(item => item.Value<string>("type") == "root2d"), Is.EqualTo(1));
            Assert.That(result.Count(item => item.Value<string>("type") == "effector"), Is.EqualTo(1));
            Assert.That((int)result.Single(item => item.Value<string>("type") == "root2d")["frame_indices"][0], Is.EqualTo(2));
        }

        private static string CreateRootConstraint(int frame, float rootZ)
        {
            return new JArray
            {
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(frame),
                    ["root_positions"] = new JArray { new JArray { 0f, 0f, rootZ } }
                }
            }.ToString();
        }

        private static JObject CreateRootConstraint(int[] frames, float[] rootZ)
        {
            return new JObject
            {
                ["type"] = "fullbody",
                ["frame_indices"] = new JArray(frames),
                ["root_positions"] = new JArray(rootZ.Select(value => new JArray(0f, 0f, value)))
            };
        }

    }
}
