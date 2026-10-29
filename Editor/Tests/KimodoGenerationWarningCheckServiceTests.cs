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
                        ["effector"] = new JObject { ["right_hand"] = new JObject { ["position"] = new JArray(0, 1, 0) } }
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
    }
}

