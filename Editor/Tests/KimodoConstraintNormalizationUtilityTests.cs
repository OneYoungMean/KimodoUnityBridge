using System.Collections.Generic;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintNormalizationUtilityTests
    {
        [TestCase(KimodoInOutConstraintMode.Outside, true, 23, 0)]
        [TestCase(KimodoInOutConstraintMode.Outside, false, 90, 67)]
        [TestCase(KimodoInOutConstraintMode.Inside, true, 30, 0)]
        [TestCase(KimodoInOutConstraintMode.Inside, false, 83, 67)]
        public void BoundaryWindow_UsesModelFramesAndPreservesEndpoints(
            KimodoInOutConstraintMode mode, bool begin, int sourceFirst, int exportedFirst)
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                timeline.editorSettings.frameRate = 60;
                var track = timeline.CreateTrack<AnimationTrack>();
                var previous = track.CreateDefaultClip(); previous.start = 0; previous.duration = 1;
                var current = track.CreateClip<KimodoPlayableClip>(); current.start = 1; current.duration = 2;
                var next = track.CreateDefaultClip(); next.start = 3; next.duration = 1;
                var request = new KimodoInOutConstraintRequest
                {
                    Mode = mode, EnableBegin = true, EnableEnd = true, GenerationFrames = 74,
                    BeginWindowFrames = 7, EndWindowFrames = 7, BeginSampleCount = 4, EndSampleCount = 4,
                    TimelineContext = new KimodoTimelineInOutConstraintContext
                    { SourceClip = current, PreviousTimelineClip = previous, NextTimelineClip = next, Track = track }
                };
                KimodoInOutConstraintTools.BuildBoundarySampleTimes(request, begin, out var times, out var exports);
                Assert.That(times, Has.Length.EqualTo(4));
                for (int i = 0; i < 4; i++)
                {
                    Assert.That(times[i], Is.EqualTo((sourceFirst + i * 2) / 30.0).Within(1e-6));
                    Assert.That(exports[i], Is.EqualTo((exportedFirst + i * 2) / 30.0).Within(1e-6));
                }
                request.BeginSampleCount = request.EndSampleCount = 1;
                KimodoInOutConstraintTools.BuildBoundarySampleTimes(request, begin, out times, out exports);
                int seamOffset = (mode == KimodoInOutConstraintMode.Inside) == begin ? 0 : 6;
                Assert.That(times[0], Is.EqualTo((sourceFirst + seamOffset) / 30.0).Within(1e-6));
                Assert.That(exports[0], Is.EqualTo((exportedFirst + seamOffset) / 30.0).Within(1e-6));
            }
            finally { Object.DestroyImmediate(timeline); }
        }

        [Test]
        public void OutsideWindow_ClampsShortNeighborsAndDoesNotPadMissingOrDisabledBoundaries()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                var track = timeline.CreateTrack<AnimationTrack>();
                var previous = track.CreateDefaultClip(); previous.start = 0; previous.duration = 0.1;
                var current = track.CreateClip<KimodoPlayableClip>(); current.start = 0.1; current.duration = 1;
                var playable = (KimodoPlayableClip)current.asset;
                playable.inOutConstraintMode = KimodoInOutConstraintMode.Outside;
                playable.inConstraintWindowFrames = 100;
                playable.inConstraintSampleCount = 100;
                KimodoInOutConstraintTools.ResolveOutsideContextFrames(current, out int before, out int after);
                Assert.That(before, Is.EqualTo(3));
                Assert.That(after, Is.Zero);
                playable.enableInConstraint = false;
                KimodoInOutConstraintTools.ResolveOutsideContextFrames(current, out before, out after);
                Assert.That(before, Is.Zero);
                playable.enableInConstraint = true;
                playable.inOutConstraintMode = KimodoInOutConstraintMode.None;
                KimodoInOutConstraintTools.ResolveOutsideContextFrames(current, out before, out after);
                Assert.That(before, Is.Zero);
            }
            finally { Object.DestroyImmediate(timeline); }
        }

        [Test]
        public void DeferredAutoBegin_RealConstraintBeatsSyntheticConstraint()
        {
            var synthetic = new KimodoMarkerSampleResult { constraintMode = "root2d", sampleTime = 0.0 };
            var real = new KimodoMarkerSampleResult { constraintMode = "fullbody", sampleTime = 0.5 };

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
                    constraintMode = "root2d",
                    sampleTime = 1.0,
                };

                Assert.That(
                    KimodoInOutConstraintComposer.TryBuild(
                        CreateAutoBeginRequest(track, sample),
                        out KimodoInOutConstraintResult result,
                        out _,
                        out _),
                    Is.True);

                Assert.That(result.CombinedSamples, Has.Count.EqualTo(2));
                Assert.That(result.CombinedSamples[0].constraintMode, Is.EqualTo("root2d"));
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
                    constraintMode = "fullbody",
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
