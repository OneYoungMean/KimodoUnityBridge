using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System.Linq;
using KimodoBridge.Editor;
using KimodoBridge;
using KimodoUnityBridge;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoGenerationWarningCheckServiceTests
    {
        [Test]
        public void RootTrajectory_ReportsSevenMetersPerSecondWarningWithoutBlocking()
        {
            JObject args = new JObject
            {
                ["constraints"] = new JArray
                {
                    new JObject { ["frame"] = 0, ["root"] = Root(0f, 0f) },
                    new JObject { ["frame"] = 60, ["root"] = Root(8f, 0f) }
                }
            };

            JObject result = KimodoGenerationWarningCheckService.Build(args, 120, 60f);

            Assert.That(result.Value<bool>("blocking"), Is.False);
            Assert.That(result.Value<string>("overall"), Is.EqualTo("warning"));
            Assert.That(result["root_trajectory"].Value<float>("max_speed_mps"), Is.EqualTo(8f).Within(0.001f));
            Assert.That(result["root_trajectory"]["issues"].ToObject<JArray>().Count, Is.GreaterThan(0));
        }

        [Test]
        public void MovementIntent_WithNearlyStationaryRootEndpointsWarnsWithoutBlocking()
        {
            JObject args = MovementArgs("Walk forward", Root(0f, 0f), Root(0.05f, 0.02f));

            JObject result = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            Assert.That(result.Value<bool>("blocking"), Is.False);
            Assert.That(result.Value<string>("overall"), Is.EqualTo("warning"));
            Assert.That(result["movement_intent"].Value<string>("status"), Is.EqualTo("warning"));
            Assert.That(result["movement_intent"]["issues"].ToObject<JArray>()
                .Any(issue => issue.Value<string>("code") == "movement_intent_without_root_travel"), Is.True);
        }

        [Test]
        public void MovementIntent_DetectsCommonChineseTravelPrompts()
        {
            JObject args = MovementArgs("往前走", Root(0f, 0f), Root(0.02f, 0.01f));

            JObject result = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            Assert.That(result.Value<bool>("blocking"), Is.False);
            Assert.That(result["movement_intent"].Value<string>("status"), Is.EqualTo("warning"));
        }

        [TestCase("Walk in place")]
        [TestCase("Look around")]
        [TestCase("Move the arm")]
        [TestCase("Cross arms")]
        [TestCase("移动手臂")]
        public void MovementIntent_DoesNotWarnForStationaryOrNonLocomotionPrompts(string prompt)
        {
            JObject args = MovementArgs(prompt, Root(0f, 0f), Root(0f, 0f));

            JObject result = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            Assert.That(result.Value<bool>("blocking"), Is.False);
            Assert.That(result["movement_intent"].Value<string>("status"), Is.EqualTo("not_applicable"));
        }

        [Test]
        public void MovementIntent_DoesNotWarnWhenRootEndpointsAreIncomplete()
        {
            JObject args = new JObject
            {
                ["prompt"] = "Walk forward",
                ["constraints"] = new JArray { RootConstraint(0, Root(0f, 0f)) }
            };

            JObject result = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            Assert.That(result.Value<bool>("blocking"), Is.False);
            Assert.That(result["movement_intent"].Value<string>("status"), Is.EqualTo("not_applicable"));
        }

        [Test]
        public void MovementIntent_DoesNotWarnWhenRootEndpointsOrIntermediateTargetsShowTravel()
        {
            JObject args = MovementArgs("Walk forward", Root(0f, 0f), Root(1f, 0f));
            JObject intermediateArgs = new JObject
            {
                ["prompt"] = "Walk forward and return",
                ["constraints"] = new JArray
                {
                    RootConstraint(0, Root(0f, 0f)),
                    RootConstraint(90, Root(0.5f, 0f)),
                    RootConstraint(179, Root(0f, 0f))
                }
            };

            JObject endpointResult = KimodoGenerationWarningCheckService.Build(args, 180, 60f);
            JObject intermediateResult = KimodoGenerationWarningCheckService.Build(intermediateArgs, 180, 60f);

            Assert.That(endpointResult["movement_intent"].Value<string>("status"), Is.EqualTo("ok"));
            Assert.That(intermediateResult["movement_intent"].Value<string>("status"), Is.EqualTo("ok"));
        }

        [Test]
        public void MovementIntent_DoesNotWarnWhenRouteConstraintIsPresent()
        {
            JObject args = MovementArgs("Walk forward", Root(0f, 0f), Root(0f, 0f));
            ((JArray)args["constraints"]).Insert(1, new JObject
            {
                ["frame"] = 60,
                ["root_path"] = new JObject { ["path"] = new JObject { ["track"] = "Paths", ["index"] = 0 } }
            });

            JObject result = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            Assert.That(result["movement_intent"].Value<string>("status"), Is.EqualTo("ok"));
        }

        [Test]
        public void MovementIntent_OnlyTreatsPathAngleOverridesAsRouteCoverage()
        {
            JObject args = MovementArgs("Walk forward", Root(0f, 0f), Root(0f, 0f));
            args["path"] = new JObject { ["heading"] = 90f };
            JObject headingOnlyResult = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            args["path"] = new JObject();
            JObject emptyPathResult = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            args["path"] = new JObject { ["start_angle"] = 0f, ["end_angle"] = 0f };
            JObject pathAngleResult = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            args["path"] = new JObject { ["start_angle"] = 0f, ["end_angle"] = 0f, ["distance"] = 0f };
            JObject zeroDistanceResult = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            args["path"] = new JObject { ["start_angle"] = 0f, ["end_angle"] = 0f, ["distance"] = 1f };
            JObject nonzeroDistanceResult = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            Assert.That(headingOnlyResult["movement_intent"].Value<string>("status"), Is.EqualTo("warning"));
            Assert.That(emptyPathResult["movement_intent"].Value<string>("status"), Is.EqualTo("warning"));
            Assert.That(pathAngleResult["movement_intent"].Value<string>("status"), Is.EqualTo("ok"));
            Assert.That(zeroDistanceResult["movement_intent"].Value<string>("status"), Is.EqualTo("warning"));
            Assert.That(nonzeroDistanceResult["movement_intent"].Value<string>("status"), Is.EqualTo("ok"));
        }

        [Test]
        public void RootTrajectory_AboveTenMetersPerSecondIsReportedButRawValidationOwnsRejection()
        {
            JObject args = new JObject
            {
                ["constraints"] = new JArray
                {
                    new JObject { ["frame"] = 0, ["root"] = Root(0f, 0f) },
                    new JObject { ["frame"] = 60, ["root"] = Root(11f, 0f) }
                }
            };

            JObject result = KimodoGenerationWarningCheckService.Build(args, 120, 60f);

            Assert.That(result.Value<bool>("blocking"), Is.False);
            Assert.That(result.Value<string>("overall"), Is.EqualTo("warning"));
            Assert.That(result["root_trajectory"]["issues"].ToObject<JArray>()
                .Any(issue => issue.Value<string>("code") == "speed_hard_limit"), Is.True);
        }

        [TestCase(8f, "speed_limit")]
        [TestCase(11f, "speed_hard_limit")]
        public void CommandPreflight_BlocksRootSpeedLimitViolations(float distance, string issueCode)
        {
            JObject args = new JObject
            {
                ["constraints"] = new JArray
                {
                    new JObject { ["frame"] = 0, ["root"] = Root(0f, 0f) },
                    new JObject { ["frame"] = 60, ["root"] = Root(distance, 0f) }
                }
            };

            JObject result = KimodoGenerationWarningCheckService.Build(
                args, 120, 60f, blockRootMotionLimits: true);

            Assert.That(result.Value<bool>("blocking"), Is.True);
            Assert.That(result["root_trajectory"]["issues"].ToObject<JArray>()
                .Any(issue => issue.Value<string>("code") == issueCode), Is.True);
        }

        [Test]
        public void CommandPreflight_BlocksRootAccelerationLimitViolations()
        {
            JObject args = new JObject
            {
                ["constraints"] = new JArray
                {
                    new JObject { ["frame"] = 0, ["root"] = Root(0f, 0f) },
                    new JObject { ["frame"] = 30, ["root"] = Root(2.75f, 0f) },
                    new JObject { ["frame"] = 60, ["root"] = Root(11f, 0f) }
                }
            };

            JObject result = KimodoGenerationWarningCheckService.Build(
                args, 120, 60f, blockRootMotionLimits: true);

            Assert.That(result.Value<bool>("blocking"), Is.True);
            Assert.That(result["root_trajectory"]["issues"].ToObject<JArray>()
                .Any(issue => issue.Value<string>("code") == "acceleration_limit"), Is.True);
        }

        [TestCase(9.99f, false)]
        [TestCase(10f, false)]
        [TestCase(10.01f, true)]
        [TestCase(14.99f, true)]
        [TestCase(15f, true)]
        [TestCase(15.01f, true)]
        [TestCase(16f, true)]
        public void RootTrajectory_AccelerationDiagnosticsDoNotBlockBeforeRawValidation(float acceleration, bool warning)
        {
            JObject args = new JObject
            {
                ["constraints"] = new JArray
                {
                    new JObject { ["frame"] = 0, ["root"] = Root(0f, 0f) },
                    new JObject { ["frame"] = 30, ["root"] = Root(acceleration * 0.25f, 0f) }
                }
            };

            JObject result = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            Assert.That(result.Value<bool>("blocking"), Is.False);
            Assert.That(result.Value<string>("overall"), Is.EqualTo(warning ? "warning" : "ok"));
            Assert.That(result["root_trajectory"].Value<float>("max_acceleration_mps2"), Is.EqualTo(acceleration).Within(0.001f));
            JArray issues = result["root_trajectory"]["issues"].ToObject<JArray>();
            Assert.That(issues.Any(issue => issue.Value<string>("code") == "acceleration_limit"), Is.EqualTo(warning));
            Assert.That(issues.Any(issue => issue.Value<string>("code") == "acceleration_hard_limit"), Is.EqualTo(acceleration > 15f));
        }

        [Test]
        public void RootAndEffector_AreNotReportedAsConflictingConstraints()
        {
            JObject args = new JObject
            {
                ["constraints"] = new JArray
                {
                    new JObject
                    {
                        ["frame"] = 10,
                        ["root"] = Root(1f, 0f),
                        ["effector"] = new JArray(new JObject { ["joint"] = "right_hand", ["position"] = new JArray(0, 1, 0) })
                    }
                }
            };

            JObject result = KimodoGenerationWarningCheckService.Build(args, 180, 60f);

            Assert.That(result["constraint_overlap"].Value<string>("status"), Is.EqualTo("ok"));
            Assert.That(result["constraint_overlap"]["issues"].ToObject<JArray>().Count, Is.EqualTo(0));
        }

        [TestCase(89, false, "warning", "duration_short_warning")]
        [TestCase(90, false, "warning", "duration_short_warning")]
        [TestCase(179, false, "warning", "duration_short_warning")]
        [TestCase(180, false, "ok", null)]
        public void Duration_ReportsMinimumAndShortMotionThresholds(
            int durationFrames,
            bool blocking,
            string overall,
            string issueCode)
        {
            JObject result = KimodoGenerationWarningCheckService.Build(new JObject(), durationFrames, 60f);

            Assert.That(result.Value<bool>("blocking"), Is.EqualTo(blocking));
            Assert.That(result.Value<string>("overall"), Is.EqualTo(overall));
            JArray issues = result["duration"]["issues"].ToObject<JArray>();
            if (issueCode == null)
                Assert.That(issues.Count, Is.EqualTo(0));
            else
                Assert.That(issues.Any(issue => issue.Value<string>("code") == issueCode), Is.True);
        }

        [Test]
        public void FullBodyEffector_IsReportedAsNonBlockingDiagnostic()
        {
            JObject args = new JObject
            {
                ["constraints"] = new JArray
                {
                    new JObject { ["frame"] = 0, ["pose"] = new JObject { ["track"] = "Pose", ["index"] = 0 } }
                }
            };
            var sample = new KimodoMarkerSampleResult
            {
                sampleTime = 0,
                sampleValid = true,
                effectors = new KimodoConstraintEffectors
                {
                    leftHand = new KimodoRigidTransform { t = Vector3.one },
                    rightHand = null, leftFoot = null, rightFoot = null
                }
            };

            JObject result = KimodoGenerationWarningCheckService.Build(args, 180, 60f, generatedSamples: new[] { sample });

            Assert.That(result.Value<bool>("blocking"), Is.False);
            Assert.That(result["effectors"].Value<string>("status"), Is.EqualTo("warning"));
            Assert.That(result["effectors"]["checked_count"].Value<int>(), Is.EqualTo(1));
        }

        private static JObject Root(float x, float z)
        {
            return new JObject
            {
                ["position"] = new JArray(x, 0f, z),
                ["rotation"] = new JArray(0, 0, 0)
            };
        }

        private static JObject MovementArgs(string prompt, JObject start, JObject end) => new JObject
        {
            ["prompt"] = prompt,
            ["constraints"] = new JArray
            {
                RootConstraint(0, start),
                RootConstraint(179, end)
            }
        };

        private static JObject RootConstraint(int frame, JObject root) => new JObject
        {
            ["frame"] = frame,
            ["root"] = root
        };
    }
}

