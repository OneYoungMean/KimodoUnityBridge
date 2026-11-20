using System;
using System.Reflection;
using KimodoBridge;
using KimodoBridge.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class KimodoEditorConstraintProviderTests
    {
        [Test]
        public void UiConstraintBuild_DoesNotRejectRootMotionLimit()
        {
            var provider = new KimodoEditorConstraintProvider();
            var external = new KimodoExternalConstraintRequest
            {
                Enabled = true,
                IncludeTimelineConstraints = false,
                ConstraintsJson = new JArray
                {
                    new JObject
                    {
                        ["type"] = "root2d",
                        ["frame_indices"] = new JArray(0, 60),
                        ["smooth_root_2d"] = new JArray
                        {
                            new JArray(0f, 0f),
                            new JArray(8f, 0f)
                        }
                    }
                }.ToString()
            };

            KimodoEditorConstraintProvider.ConstraintData result =
                provider.BuildGenerationConstraintsOrThrow(
                    null, external, 120, 2f, 60f, 0.0, null);

            Assert.That(result.Json, Is.EqualTo(external.ConstraintsJson));
            Assert.Throws<InvalidOperationException>(() => provider.BuildGenerationConstraintsOrThrow(
                null, external, 120, 2f, 60f, 0.0, null, enforceRootMotionLimits: true));
        }

        [Test]
        public void StagePlanKeepsInsidePostApplyWhenLoopIsEnabled()
        {
            KimodoPlayableClip clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                clip.insideConstraintEnabled = true;
                clip.postApplyInsideConstraint = true;
                JObject plan = BuildPlan(clip, loop: true, trajectory: false);

                Assert.That(plan["stage1"], Is.Null);
                Assert.That(plan["stage2"], Is.Null);
                Assert.That(plan["stage3"], Is.Null);
                Assert.That(plan["loop"]["enabled"].Value<bool>(), Is.True);
                Assert.That(clip.insideConstraintEnabled, Is.True);
                Assert.That(clip.postApplyInsideConstraint, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void StagePlanSendsTrajectorySettingsWithoutChoosingStages()
        {
            KimodoPlayableClip clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                clip.overridePathAngle = true;
                JObject plan = BuildPlan(clip, loop: false, trajectory: true);
                Assert.That(plan["stage1"], Is.Null);
                Assert.That(plan["trajectory"]["override_path_angle"].Value<bool>(), Is.True);
                Assert.That(plan["trajectory"]["override_distance"].Value<bool>(), Is.False);

                clip.overridePathDistance = true;
                plan = BuildPlan(clip, loop: false, trajectory: true);
                Assert.That(plan["stage1"], Is.Null);
                Assert.That(plan["trajectory"]["override_distance"].Value<bool>(), Is.True);

                clip.overridePathAngle = false;
                clip.overridePathDistance = false;
                clip.splinePathEnabled = true;
                plan = BuildPlan(clip, loop: false, trajectory: true);
                Assert.That(plan["stage1"], Is.Null);
                Assert.That(plan["trajectory"]["enabled"].Value<bool>(), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        private static JObject BuildPlan(
            KimodoPlayableClip clip,
            bool loop,
            bool trajectory)
        {
            MethodInfo method = typeof(KimodoPlayableClipGenerationHostService).GetMethod(
                "BuildBackendStagePlanJson", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            string json = (string)method.Invoke(null, new object[]
            {
                clip, 90, 0, 90, 30f, loop, trajectory
            });
            return JObject.Parse(json);
        }
    }
}
