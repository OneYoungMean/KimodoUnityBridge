using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine.Splines;

namespace KimodoBridge.Editor
{
    public sealed class KimodoPlayableSplinePathTests
    {
        [Test]
        public void InsertKnotPreservingCurve_LeavesCurvePositionsUnchanged()
        {
            var spline = new Spline();
            spline.Add(new BezierKnot(
                new float3(0f, 0f, 0f),
                float3.zero,
                new float3(2f, 0f, 1f)));
            spline.Add(new BezierKnot(
                new float3(6f, 0f, 3f),
                new float3(-2f, 0f, 1f),
                float3.zero));

            float3[] before = new float3[9];
            for (int i = 0; i < before.Length; i++)
            {
                before[i] = spline.EvaluatePosition(i / (float)(before.Length - 1));
            }

            KimodoPlayableSplinePathSceneEditor.InsertKnotPreservingCurve(spline, 0.42f);

            Assert.That(spline.Count, Is.EqualTo(3));
            for (int i = 0; i < before.Length; i++)
            {
                float3 after = spline.EvaluatePosition(i / (float)(before.Length - 1));
                Assert.That(math.distance(before[i], after), Is.LessThan(0.001f));
            }
        }
    }
}
