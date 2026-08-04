using System.Collections.Generic;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintNormalizationUtilityTests
    {
        [Test]
        public void DeferredAutoBegin_RealConstraintBeatsSyntheticConstraint()
        {
            var synthetic = new KimodoMarkerSampleResult { constraintType = "root2d", sampleTime = 0.0 };
            var real = new KimodoMarkerSampleResult { constraintType = "fullbody", sampleTime = 0.5 };

            Assert.That(
                KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    new List<KimodoMarkerSampleResult> { synthetic, real },
                    1.0,
                    synthetic),
                Is.True);
            Assert.That(
                KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    new List<KimodoMarkerSampleResult> { synthetic },
                    1.0,
                    synthetic),
                Is.False);
        }

        [Test]
        public void ConstraintAtExactlyOneSecond_GetsFrameZeroAutoBeginConstraint()
        {
            AnimationTrack track = CreateAutoBeginTrack(new Vector3(4f, 0f, 5f), Quaternion.identity);
            try
            {
                var sample = new KimodoMarkerSampleResult
                {
                    constraintType = "root2d",
                    sampleTime = 1.0,
                    kimodoRootPosition = new Vector3(5f, 0f, 5f)
                };

                Assert.That(
                    KimodoInOutConstraintComposer.TryBuild(
                        CreateAutoBeginRequest(track, sample),
                        out KimodoInOutConstraintResult result,
                        out _,
                        out _),
                    Is.True);

                Assert.That(result.CombinedSamples, Has.Count.EqualTo(2));
                Assert.That(result.CombinedSamples[0].constraintType, Is.EqualTo("root2d"));
                Assert.That(result.CombinedSamples[0].sampleTime, Is.EqualTo(0.0).Within(1e-6));
                Assert.That(result.HasSyntheticAutoBeginConstraint, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(track);
            }
        }

        [Test]
        public void RealAnchorInsideFirstSecond_PreventsAutoBeginConstraint()
        {
            AnimationTrack track = CreateAutoBeginTrack(Vector3.zero, Quaternion.identity);
            try
            {
                var realAnchor = new KimodoMarkerSampleResult
                {
                    constraintType = "fullbody",
                    sampleTime = 0.75
                };

                Assert.That(
                    KimodoInOutConstraintComposer.TryBuild(
                        CreateAutoBeginRequest(track, realAnchor),
                        out KimodoInOutConstraintResult result,
                        out _,
                        out _),
                    Is.True);

                Assert.That(result.CombinedSamples, Has.Count.EqualTo(1));
                Assert.That(result.HasSyntheticAutoBeginConstraint, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(track);
            }
        }

        [Test]
        public void PlayableClip_InAndOutDefaultEnabled()
        {
            KimodoPlayableClip clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                Assert.That(clip.enableInConstraint, Is.True);
                Assert.That(clip.enableOutConstraint, Is.True);
                Assert.That(clip.autoBeginAnchor, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        private static AnimationTrack CreateAutoBeginTrack(Vector3 position, Quaternion rotation)
        {
            AnimationTrack track = ScriptableObject.CreateInstance<AnimationTrack>();
            track.trackOffset = TrackOffset.ApplyTransformOffsets;
            track.position = position;
            track.rotation = rotation;
            return track;
        }

        private static KimodoInOutConstraintRequest CreateAutoBeginRequest(
            AnimationTrack track,
            params KimodoMarkerSampleResult[] samples)
        {
            return new KimodoInOutConstraintRequest
            {
                Mode = KimodoInOutConstraintMode.None,
                AutoBeginAnchor = true,
                TimelineContext = new KimodoTimelineInOutConstraintContext { Track = track },
                ManualSamples = samples != null
                    ? new List<KimodoMarkerSampleResult>(samples)
                    : new List<KimodoMarkerSampleResult>()
            };
        }
    }
}
