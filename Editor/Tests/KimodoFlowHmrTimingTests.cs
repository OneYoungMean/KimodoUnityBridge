#if UNITY_EDITOR
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using KimodoBridge;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoFlowHmrTimingTests
    {
        [Test]
        public void TryRead_UsesGlobalTimeTrackAndIgnoresDiagnosticJsonTimestamps()
        {
            var motion = CreateMotion(
                3,
                globalTimeSeconds: new[] { 0f, 0.04f, 0.0733333f });
            var analysis = new JObject
            {
                ["frame_timestamps_seconds"] = new JArray(0.0, 1.0 / 30.0),
                ["source_duration_seconds"] = 0.2,
            };

            Assert.That(
                KimodoFlowHmrTiming.TryRead(
                    motion, analysis,
                    out float[] frameTimes, out float duration, out string error),
                Is.True,
                error);
            Assert.That(frameTimes, Has.Length.EqualTo(3));
            Assert.That(frameTimes[1], Is.EqualTo(0.04f).Within(1e-6f));
            Assert.That(frameTimes[2], Is.EqualTo(0.0733333f).Within(1e-6f));
            Assert.That(duration, Is.EqualTo(0.2f).Within(1e-6f));
        }

        [Test]
        public void TryRead_RejectsKmbWithoutGlobalTimeTrack()
        {
            Assert.That(
                KimodoFlowHmrTiming.TryRead(
                    CreateMotion(2), new JObject
                    {
                        ["frame_timestamps_seconds"] = new JArray(0.0, 0.04),
                        ["source_duration_seconds"] = 0.1
                    },
                    out _, out _, out string error),
                Is.False);
            Assert.That(error, Does.Contain("global_time_seconds"));
        }

        [Test]
        public void TryRead_RejectsDurationBeforeLastGlobalTime()
        {
            Assert.That(
                KimodoFlowHmrTiming.TryRead(
                    CreateMotion(2, globalTimeSeconds: new[] { 0f, 0.08f }),
                    new JObject { ["source_duration_seconds"] = 0.07 },
                    out _, out _, out string error),
                Is.False);
            Assert.That(error, Does.Contain("source duration"));
        }

        [Test]
        public void HumanDetectionTrack_ClampsInvalidSampleToNearestValidPose()
        {
            var motion = CreateMotion(
                5,
                humanDetection: new byte[] { 1, 0, 0, 1, 0 });

            Assert.That(motion.TryResolveHumanDetectionFrame(2, out int resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(0));
            Assert.That(motion.TryResolveHumanDetectionFrame(4, out resolved), Is.True);
            Assert.That(resolved, Is.EqualTo(3));
        }

        [Test]
        public void HumanDetectionTrack_ReportsNoPoseWhenAllFramesAreInvalid()
        {
            var motion = CreateMotion(
                2,
                humanDetection: new byte[] { 0, 0 });

            Assert.That(motion.HasHumanDetectionTrack, Is.True);
            Assert.That(motion.HasAnyHumanDetection, Is.False);
            Assert.That(motion.TryResolveHumanDetectionFrame(0, out _), Is.False);
        }

        [Test]
        public void MuscleImporter_RejectsMissingDetectionTrack()
        {
            Assert.That(
                KimodoFlowHmrMuscleClipImporter.TryResolvePoseFrame(
                    CreateMotion(2), 0, 2, out _, out string error),
                Is.False);
            Assert.That(error, Does.Contain("human_detection"));
        }

        [Test]
        public void MuscleImporter_UsesDetectionMaskForPoseClamping()
        {
            var motion = CreateMotion(
                5,
                humanDetection: new byte[] { 1, 0, 1, 1, 0 });

            Assert.That(
                KimodoFlowHmrMuscleClipImporter.TryResolvePoseFrame(
                    motion, 2, 5, out int poseFrame, out string error),
                Is.True,
                error);
            Assert.That(poseFrame, Is.EqualTo(2));
        }

        [Test]
        public void MuscleImporter_UsesRawSampleWhenDetectionMaskHasNoHits()
        {
            var motion = CreateMotion(
                3,
                humanDetection: new byte[] { 0, 0, 0 });

            Assert.That(
                KimodoFlowHmrMuscleClipImporter.TryResolvePoseFrame(
                    motion, 1, 3, out int poseFrame, out string error),
                Is.True,
                error);
            Assert.That(poseFrame, Is.EqualTo(1));
        }

        [Test]
        public void CameraImporter_ReusesOneTimelineTrackForTheBoundCamera()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var directorObject = new GameObject("CameraTrackReuseDirector_Test");
            var cameraObject = new GameObject("CameraTrackReuseCamera_Test");
            try
            {
                PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                Animator animator = cameraObject.AddComponent<Animator>();

                AnimationTrack first = KimodoFlowHmrCameraTrackImporter.GetOrCreateCameraTrack(
                    timeline, director, animator, out string firstError);
                AnimationTrack second = KimodoFlowHmrCameraTrackImporter.GetOrCreateCameraTrack(
                    timeline, director, animator, out string secondError);

                Assert.That(firstError, Is.Empty);
                Assert.That(secondError, Is.Empty);
                Assert.That(second, Is.SameAs(first));
                Assert.That(timeline.GetOutputTracks().OfType<AnimationTrack>().Count(), Is.EqualTo(1));
                Assert.That(director.GetGenericBinding(first), Is.SameAs(animator));
            }
            finally
            {
                Object.DestroyImmediate(directorObject);
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void RawMotionFlatBuffer_RoundTripsRequiredFlowHmrTracks()
        {
            var motion = CreateMotion(
                3,
                globalTimeSeconds: new[] { 0f, 0.04f, 0.0733333f },
                humanDetection: new byte[] { 1, 0, 1 });
            byte[] bytes = KimodoRawMotionUtility.ToFlatBuffer(motion, "smplh");

            Assert.That(
                KimodoRawMotionUtility.TryParseFlatBuffer(bytes, out KimodoRawMotionData parsed, out string error),
                Is.True,
                error);
            Assert.That(parsed.HasGlobalTimeTrack, Is.True);
            Assert.That(parsed.HasHumanDetectionTrack, Is.True);
            Assert.That(parsed.FrameRate, Is.EqualTo(30f));
            Assert.That(parsed.FrameCount, Is.EqualTo(3));
            Assert.That(parsed.TryReadGlobalTime(1, out float time), Is.True);
            Assert.That(time, Is.EqualTo(0.04f).Within(1e-6f));
            CollectionAssert.AreEqual(new byte[] { 1, 0, 1 }, parsed.humanDetection);
        }

        private static KimodoRawMotionData CreateMotion(
            int frameCount,
            float[] globalTimeSeconds = null,
            byte[] humanDetection = null)
        {
            var rotations = new List<float>(frameCount * 4);
            for (int frame = 0; frame < frameCount; frame++)
                rotations.AddRange(new[] { 1f, 0f, 0f, 0f });
            var motion = new KimodoRawMotionData(
                frameCount, 1, 30f,
                new[] { "pelvis" }, new[] { -1 },
                new Vector3[frameCount], rotations, 0,
                globalTimeSeconds: globalTimeSeconds,
                humanDetection: humanDetection);
            return motion;
        }
    }
}
#endif
