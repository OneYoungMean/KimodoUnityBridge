using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor
{
    public sealed class KimodoPlayableSplinePathTests
    {
        [Test]
        public void ResolveSplineCurveTime_UsesStoredKnotTimes()
        {
            var clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            clip.SetSplinePathData(new[]
            {
                new KimodoSplineKnotData { time = 0f },
                new KimodoSplineKnotData { time = 0.5f },
                new KimodoSplineKnotData { time = 1f }
            });

            Assert.That(
                clip.TryResolveSplineCurveTime(0.5f, out int firstCurve, out float firstCurveTime),
                Is.True);
            Assert.That(firstCurve, Is.Zero);
            Assert.That(firstCurveTime, Is.EqualTo(1f));

            Assert.That(
                clip.TryResolveSplineCurveTime(0.75f, out int secondCurve, out float secondCurveTime),
                Is.True);
            Assert.That(secondCurve, Is.EqualTo(1));
            Assert.That(secondCurveTime, Is.EqualTo(0.5f));

            Object.DestroyImmediate(clip);
        }
    }
}
