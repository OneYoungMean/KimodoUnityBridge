using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class AnalysisPictureSpacingTests
    {
        [TestCase(9, 2)]
        [TestCase(10, 2)]
        [TestCase(20, 2)]
        [TestCase(21, 3)]
        [TestCase(45, 4)]
        public void GhostIntervals_KeepAnchorsAndFillLongGaps(int lastFrame, int count)
        {
            var frames = Invoke<List<int>>("InsertTestGhostFrames", new[] { 0, lastFrame });
            Assert.That(frames.Count, Is.EqualTo(count));
            Assert.That(frames.First(), Is.Zero);
            Assert.That(frames.Last(), Is.EqualTo(lastFrame));
            if (count > 2)
                for (int index = 1; index < frames.Count; index++)
                    Assert.That(frames[index] - frames[index - 1], Is.InRange(10, 20));
        }

        [Test]
        public void GhostIntervals_AreUniformWithinEachEventGap()
        {
            var frames = Invoke<List<int>>("InsertTestGhostFrames", new[] { 0, 9, 54, 60 });
            Assert.That(frames, Is.EqualTo(new[] { 0, 9, 24, 39, 54, 60 }));
        }

        [Test]
        public void GhostOverlap_UsesGroundDistanceAndProtectsAllEvents()
        {
            var pelvis = new[]
            {
                Vector3.zero, new Vector3(.3f, 8f, 0f), new Vector3(1f, 0f, 0f),
                new Vector3(1.3f, 0f, 0f), new Vector3(3.8f, 0f, 0f),
                new Vector3(4f, 0f, 0f), new Vector3(.1f, 0f, 0f)
            };
            var frames = Invoke<List<int>>("FilterOverlappingGhostFrames", pelvis,
                Enumerable.Range(0, pelvis.Length).ToArray(), new HashSet<int> { 0, 5, 6 });
            Assert.That(frames, Is.EqualTo(new[] { 0, 2, 5, 6 }),
                "Auxiliary ghosts must avoid earlier ghosts and later events; coincident events remain.");
        }

        [Test]
        public void GhostOverlap_KeepsHalfMeterBoundary()
        {
            var frames = Invoke<List<int>>("FilterOverlappingGhostFrames",
                new[] { Vector3.zero, new Vector3(.5f, 0f, 0f) },
                new[] { 0, 1 }, new HashSet<int> { 0 });
            Assert.That(frames, Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void HeightTimeSeparation_FitsAllSilhouettesWithOneTimeScale()
        {
            var poses = new[]
            {
                new Bounds(new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 2f)),
                new Bounds(new Vector3(0f, 1.2f, 2f), Vector3.one),
                new Bounds(new Vector3(0f, 1.1f, -2f), new Vector3(1f, 2f, 3f))
            };
            int[] frames = { 0, 12, 60 };
            const float gap = .06f;
            float length = Invoke<float>("CalculateHeightTimeSeparationLength", poses, frames, 60, gap);
            float tightestGap = float.PositiveInfinity;
            for (int right = 1; right < poses.Length; right++)
            for (int left = 0; left < right; left++)
            {
                float separation = frames[right] / 60f * length + poses[right].min.z
                    - (frames[left] / 60f * length + poses[left].max.z);
                Assert.That(separation, Is.GreaterThanOrEqualTo(gap - .00001f));
                tightestGap = Mathf.Min(tightestGap, separation);
            }
            Assert.That(tightestGap, Is.EqualTo(gap).Within(.00001f), "Do not stretch more than necessary.");
        }

        [Test]
        public void HeightTimeSeparation_OnePoseNeedsNoStretch()
        {
            Assert.That(Invoke<float>("CalculateHeightTimeSeparationLength",
                new[] { new Bounds(Vector3.zero, Vector3.one) }, new[] { 0 }, 1, .05f), Is.Zero);
        }

        private static T Invoke<T>(string methodName, params object[] arguments)
        {
            Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
            MethodInfo method = context.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (T)method.Invoke(null, arguments);
        }
    }
}
