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
    }
}
