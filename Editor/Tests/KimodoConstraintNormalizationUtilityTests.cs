using System.Collections.Generic;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintNormalizationUtilityTests
    {
        [Test]
        public void NormalizeConstraintOrigin_UsesHipsXZAndHeading()
        {
            var anchor = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                kimodoRootPosition = new Vector3(10f, 1f, 20f),
                unityRootPos = new Vector3(10f, 7f, 20f),
                unityRootRot = Quaternion.Euler(0f, 90f, 0f),
                rootHeading = Vector2.right,
                hasRootHeading = true
            };

            KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                new List<KimodoMarkerSampleResult> { anchor },
                out _,
                out _);

            Assert.That(anchor.kimodoRootPosition.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(anchor.kimodoRootPosition.y, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(anchor.kimodoRootPosition.z, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(Vector2.Distance(anchor.rootHeading, Vector2.up), Is.LessThan(1e-5f));
        }

        [Test]
        public void InOutComposer_DoesNotNormalizeWhenInIsDisabled()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(10f, 1f, 20f),
                unityRootPos = new Vector3(10f, 0f, 20f),
                unityRootRot = Quaternion.identity
            };
            var request = new KimodoInOutConstraintRequest
            {
                Mode = KimodoInOutConstraintMode.None,
                NormalizeConstraintOrigin = true,
                AllowNormalizeConstraintOrigin = false,
                ManualSamples = new List<KimodoMarkerSampleResult> { sample }
            };

            Assert.That(KimodoInOutConstraintComposer.TryBuild(request, out KimodoInOutConstraintResult result, out _, out _), Is.True);
            Assert.That(result.NormalizationInfo.Applied, Is.False);
            Assert.That(result.CombinedSamples[0].kimodoRootPosition, Is.EqualTo(new Vector3(10f, 1f, 20f)));
        }

        [Test]
        public void PlayableClip_InAndOutDefaultEnabled()
        {
            KimodoPlayableClip clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                Assert.That(clip.enableInConstraint, Is.True);
                Assert.That(clip.enableOutConstraint, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }
    }
}
