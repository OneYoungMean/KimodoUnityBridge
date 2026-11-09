using System.Reflection;
using KimodoBridge;
using KimodoBridge.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class KimodoEditorConstraintProviderTests
    {
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
