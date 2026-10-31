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
                30f, 0));

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
                KimodoMotionModelProfiles.DefaultModelName, 30f, 0));
            JObject effector = result.Values<JObject>().Single(item => item.Value<string>("type") == "effector");
            CollectionAssert.AreEqual(new[] { 5 }, effector["frame_indices"].Values<int>().ToArray());
            Assert.That(effector["target_positions"].Count(), Is.EqualTo(1));
            Assert.That(effector["target_positions"][0][0].Value<float>(), Is.EqualTo(5f));
        }

        [TestCase(new[] { 0, 5, 11 }, 0)]
        [TestCase(new[] { 78, 84, 89 }, 78)]
        [TestCase(new[] { 0, 5, 11 }, 0)]
        [TestCase(new[] { 90, 95, 101 }, 90)]
        public void DeferredWindow_UsesEarliestWindowSampleAsAnchor(
            int[] sampleFrames,
            int expectedAnchor)
        {
            Assert.That(
                KimodoPostApplyOutConstraintUtility.ResolveDeferredWindowAnchorFrame(sampleFrames),
                Is.EqualTo(expectedAnchor));
        }

        [Test]
        public void Stage3ImmediateWindow_PreservesSampledPositionWithoutReanchoring()
        {
            string existing = CreateRootConstraint(0, 7f);
            string window = new JArray
            {
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(0, 2),
                    ["root_positions"] = new JArray(
                        new JArray(1f, 2f, 3f), new JArray(1.1f, 2.1f, 3.1f))
                }
            }.ToString();

            JArray result = JArray.Parse(KimodoPostApplyOutConstraintUtility.ApplyInOut(
                existing,
                window,
                string.Empty,
                null,
                KimodoMotionModelProfiles.DefaultModelName,
                30f,
                runtimeTrimStartFrame: 0,
                reanchorInWindow: false));

            JObject fullBody = result.Children<JObject>().Single(item =>
                item.Value<string>("type") == "fullbody");
            CollectionAssert.AreEqual(new[] { 0, 2 }, fullBody["frame_indices"].Values<int>().ToArray());
            Assert.That((float)fullBody["root_positions"][0][0], Is.EqualTo(1f));
            Assert.That((float)fullBody["root_positions"][1][2], Is.EqualTo(3.1f).Within(0.001f));
            Assert.That(result.Children<JObject>().Single(item =>
                item.Value<string>("type") == "fullbody")["root_positions"].Count(), Is.EqualTo(2));
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

                Assert.That(KimodoPlayableClipGenerationExecutionService.RequiresStage2Generation(clip, null), Is.False);

                clip.enableInConstraint = true;
                Assert.That(KimodoPlayableClipGenerationExecutionService.RequiresStage2Generation(clip, null), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void StageRouting_UsesStage3ForDeferredWindowsOrBodyConstraints()
        {
            Assert.That(
                KimodoPlayableClipGenerationExecutionService.RequiresStage3Generation(
                    generateLoop: false,
                    hasStage3BoundaryWindow: true,
                    hasStage3BodyComposition: false),
                Is.True);
            Assert.That(
                KimodoPlayableClipGenerationExecutionService.RequiresStage3Generation(
                    generateLoop: false,
                    hasStage3BoundaryWindow: false,
                    hasStage3BodyComposition: true),
                Is.True);
            Assert.That(
                KimodoPlayableClipGenerationExecutionService.RequiresStage3Generation(
                    generateLoop: false,
                    hasStage3BoundaryWindow: false,
                    hasStage3BodyComposition: false),
                Is.False);
        }

        [Test]
        public void StageRouting_NoStage2ConditionKeepsDirectGeneration()
        {
            var clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                Assert.That(
                    KimodoPlayableClipGenerationExecutionService.RequiresStage2Generation(clip, null),
                    Is.False);

                clip.overridePathDistance = true;
                clip.pathDistance = 0f;
                Assert.That(
                    KimodoPlayableClipGenerationExecutionService.RequiresStage2Generation(clip, null),
                    Is.True,
                    "An explicit zero distance still requests Stage 2.");
                clip.overridePathDistance = false;
                clip.overrideHeading = true;
                Assert.That(
                    KimodoPlayableClipGenerationExecutionService.RequiresStage2Generation(clip, null),
                    Is.True);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void StageRouting_RunsStage1OnlyWhenAPathPassNeedsBaselineMotion()
        {
            var clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                Assert.That(KimodoPlayableClipGenerationExecutionService.RequiresStage1Baseline(clip), Is.False);

                clip.overridePathDistance = true;
                Assert.That(KimodoPlayableClipGenerationExecutionService.RequiresStage1Baseline(clip), Is.True,
                    "Distance-only uses the baseline Root2D trajectory as its scaling source.");

                clip.overridePathDistance = false;
                clip.overridePathAngle = true;
                Assert.That(KimodoPlayableClipGenerationExecutionService.RequiresStage1Baseline(clip), Is.True);

                clip.overridePathAngle = false;
                clip.overrideHeading = true;
                Assert.That(KimodoPlayableClipGenerationExecutionService.RequiresStage1Baseline(clip), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void Stage2PathOrigin_PrefersRelativeOffsetThenAutoBeginSnapshot()
        {
            Vector3 relativeOffset = new Vector3(8f, 2f, -3f);
            Vector3 autoBegin = new Vector3(1f, 4f, 7f);
            var request = new KimodoEditorGenerateRequest
            {
                UseRelativeOffset = true,
                RelativeOffset = relativeOffset,
                HasSyntheticAutoBeginConstraint = true,
                ConstraintSamples = new List<KimodoMarkerSampleResult>
                {
                    new KimodoMarkerSampleResult
                    {
                        constraintMode = "root2d",
                        rootOverride = new KimodoUnityBridge.KimodoRigidTransform { t = autoBegin }
                    }
                }
            };

            Assert.That(
                Vector3.Distance(
                    KimodoPlayableClipGenerationExecutionService.ResolveStage1PathRootOrigin(request).Value,
                    relativeOffset),
                Is.LessThan(0.001f));

            request.UseRelativeOffset = false;
            Assert.That(
                Vector3.Distance(
                    KimodoPlayableClipGenerationExecutionService.ResolveStage1PathRootOrigin(request).Value,
                    autoBegin),
                Is.LessThan(0.001f));

            request.HasSyntheticAutoBeginConstraint = false;
            Vector3 capturedRoot = new Vector3(-2f, 1f, 6f);
            request.TimelineContextSnapshot = new KimodoTimelineInOutConstraintContext
            {
                HasOriginalRootSnapshot = true,
                OriginalRootPosition = capturedRoot
            };
            Assert.That(
                Vector3.Distance(
                    KimodoPlayableClipGenerationExecutionService.ResolveStage1PathRootOrigin(request).Value,
                    capturedRoot),
                Is.LessThan(0.001f));
        }

        [Test]
        public void StageRouting_ResolvesFullBodyConstraintWindowSpan()
        {
            const string json = "[{\"type\":\"left-hand\",\"frame_indices\":[12,18,21]},{\"type\":\"fullbody\",\"frame_indices\":[14,20]}]";
            int span = KimodoPlayableClipGenerationExecutionService.ResolveConstraintFrameSpan(
                json, out int firstFrame, out int lastFrame);

            Assert.That(firstFrame, Is.EqualTo(12));
            Assert.That(lastFrame, Is.EqualTo(21));
            Assert.That(span, Is.EqualTo(10));
        }

        [Test]
        public void StageRouting_PreservesConfiguredWindowWhenOnlyOneSampleIsExported()
        {
            const string json = "[{\"type\":\"fullbody\",\"frame_indices\":[12]}]";
            int span = KimodoPlayableClipGenerationExecutionService.ResolveStage3WindowFrameCount(
                windowModelFrameCount: 15,
                constraintsJson: json,
                out int firstFrame,
                out int lastFrame);

            Assert.That(firstFrame, Is.EqualTo(12));
            Assert.That(lastFrame, Is.EqualTo(12));
            Assert.That(span, Is.EqualTo(15));

            int inferredSpan = KimodoPlayableClipGenerationExecutionService.ResolveStage3WindowFrameCount(
                windowModelFrameCount: 0,
                constraintsJson: json,
                out _,
                out _);
            Assert.That(inferredSpan, Is.EqualTo(1));
        }

        [Test]
        public void StageRouting_Stage3WindowWidthIsAddedOnceToCoreDuration()
        {
            const int coreFrames = 45; // 1.5 seconds at the default 30 FPS model rate.
            const int inWindowFrames = 6; // 12 command frames at 60 FPS.
            const int outWindowFrames = 6;

            int stage3Frames = coreFrames + inWindowFrames + outWindowFrames;

            Assert.That(stage3Frames, Is.EqualTo(57));
            Assert.That(stage3Frames / 30.0, Is.EqualTo(1.9).Within(1e-6));
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
        public void AppendConstraintsReplacingFullBodyFrameJson_ReplacesOnlyTheSharedSeamRow()
        {
            string existing = new JArray
            {
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(1, 2, 3),
                    ["root_positions"] = new JArray(
                        new JArray(1f, 1f, 1f),
                        new JArray(2f, 2f, 2f),
                        new JArray(3f, 3f, 3f)),
                    ["local_joints_rot"] = new JArray(
                        new JArray(new JArray(1f, 0f, 0f)),
                        new JArray(new JArray(2f, 0f, 0f)),
                        new JArray(new JArray(3f, 0f, 0f)))
                }
            }.ToString();
            string loop = new JArray
            {
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(2, 4),
                    ["root_positions"] = new JArray(
                        new JArray(20f, 20f, 20f),
                        new JArray(40f, 40f, 40f)),
                    ["local_joints_rot"] = new JArray(
                        new JArray(new JArray(20f, 0f, 0f)),
                        new JArray(new JArray(40f, 0f, 0f)))
                }
            }.ToString();

            JArray result = JArray.Parse(KimodoClipConstraintBakeUtility.AppendConstraintsReplacingFullBodyFrameJson(
                existing,
                loop,
                replacedFrame: 2));

            CollectionAssert.AreEqual(new[] { 1, 3 }, result[0]["frame_indices"].Values<int>());
            Assert.That(JToken.DeepEquals(
                new JArray(new JArray(1f, 1f, 1f), new JArray(3f, 3f, 3f)),
                result[0]["root_positions"]), Is.True);
            Assert.That(JToken.DeepEquals(
                new JArray(
                    new JArray(new JArray(1f, 0f, 0f)),
                    new JArray(new JArray(3f, 0f, 0f))),
                result[0]["local_joints_rot"]), Is.True);
            CollectionAssert.AreEqual(new[] { 2, 4 }, result[1]["frame_indices"].Values<int>());
            Assert.That((float)result[1]["root_positions"][0][0], Is.EqualTo(20f));
        }

        [Test]
        public void GenerationStage_Root2DOverridesSameFrameFullBodyRoot()
        {
            var request = new KimodoEditorGenerateRequest();
            request.Prompt = "merge root channels";
            request.Constraints.json = new JArray
            {
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(0),
                    ["root_positions"] = new JArray { new JArray(9f, 1f, 8f) },
                    ["local_joints_rot"] = new JArray
                    {
                        new JArray
                        {
                            new JArray(0f, 0f, 0f),
                            new JArray(0.1f, 0.2f, 0.3f)
                        }
                    }
                },
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(0),
                    ["smooth_root_2d"] = new JArray { new JArray(-2f, 3f) },
                    ["global_root_heading"] = new JArray { new JArray(0f, -1f) }
                }
            }.ToString();

            var stage = new KimodoEditorGenerationStage(3, "Root composition", request);
            JArray composed = JArray.Parse(stage.ApplyPasses());

            Assert.That(composed.Count, Is.EqualTo(1));
            Assert.That(composed[0].Value<string>("type"), Is.EqualTo("fullbody"));
            Assert.That(composed.Count(item => item.Value<string>("type") == "root2d"), Is.Zero);
            Assert.That((float)composed[0]["root_positions"][0][0], Is.EqualTo(-2f));
            Assert.That((float)composed[0]["root_positions"][0][1], Is.EqualTo(1f));
            Assert.That((float)composed[0]["root_positions"][0][2], Is.EqualTo(3f));
            Assert.That(
                Mathf.Abs((float)composed[0]["local_joints_rot"][0][0][1]),
                Is.EqualTo(Mathf.PI / 2f).Within(0.001f));
            Assert.That(
                JToken.DeepEquals(
                    composed[0]["local_joints_rot"][0][1],
                    new JArray(0.1f, 0.2f, 0.3f)),
                Is.True);
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
