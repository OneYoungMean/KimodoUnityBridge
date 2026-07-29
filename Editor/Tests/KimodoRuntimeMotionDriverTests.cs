using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoRuntimeMotionDriverTests
    {
        [Serializable]
        private sealed class MotionJsonData
        {
            public List<float> foot_contacts;
        }

        [TestCase(4.5666666, 30.0, 137)]
        [TestCase(5.0, 30.0, 150)]
        [TestCase(1.00001, 30.0, 31)]
        [TestCase(0.0, 30.0, 0)]
        public void SecondsToFrameCount_UsesToleranceProtectedCeiling(
            double seconds,
            double frameRate,
            int expected)
        {
            Assert.That(
                KimodoFrameTimeUtility.SecondsToFrameCount(seconds, frameRate),
                Is.EqualTo(expected));
        }

        [TestCase(KimodoMotionModelProfiles.ArdyCoreModelName, 4)]
        [TestCase(KimodoMotionModelProfiles.ArdyG1ModelName, 5)]
        public void ValidateArdyResult_AcceptsPlaybackReserveSizedDownload(string modelName, int frameCount)
        {
            Assert.That(KimodoMotionModelProfiles.TryGetArdy(modelName, out KimodoMotionModelProfile profile), Is.True);
            var motion = new KimodoRawMotionData(
                frameCount,
                profile.JointCount,
                profile.SourceFps,
                new string[profile.JointCount],
                new int[profile.JointCount],
                new Vector3[frameCount],
                new List<float>(new float[frameCount * profile.JointCount * 4]),
                0);
            var result = new KimodoBridgeGenerationResult
            {
                MotionData = motion,
                MotionBytes = new byte[] { 1 },
                MotionFormat = "kmb_v1",
                MotionRepFingerprint = profile.MotionRepFingerprint,
                ResolvedSeed = 42,
                StartFrame = 0,
                EndFrameExclusive = frameCount
            };

            Assert.DoesNotThrow(() => KimodoRuntimeMotionDriver.ValidateArdyResult(result, profile, 42));
        }

        [Test]
        public void ValidateArdyResult_AcceptsVariableLengthDirectKmb()
        {
            Assert.That(
                KimodoMotionModelProfiles.TryGetArdy(
                    KimodoMotionModelProfiles.ArdyCoreModelName,
                    out KimodoMotionModelProfile profile),
                Is.True);
            int frameCount = profile.HorizonFrames + 1;
            var motion = new KimodoRawMotionData(
                frameCount,
                profile.JointCount,
                profile.SourceFps,
                new string[profile.JointCount],
                new int[profile.JointCount],
                new Vector3[frameCount],
                new List<float>(new float[frameCount * profile.JointCount * 4]),
                0);
            var result = new KimodoBridgeGenerationResult
            {
                MotionData = motion,
                MotionBytes = new byte[] { 1 },
                MotionFormat = "kmb_v1",
                MotionRepFingerprint = profile.MotionRepFingerprint,
                ResolvedSeed = 42,
                StartFrame = 0,
                EndFrameExclusive = frameCount
            };

            Assert.DoesNotThrow(() =>
                KimodoRuntimeMotionDriver.ValidateArdyResult(result, profile, 42));
        }

        [Test]
        public void CompletedStaleArdyResult_IsKeptToPreserveTheServerCursor()
        {
            Assert.That(
                KimodoRuntimeMotionDriver.ShouldDiscardCompletedGenerationResult(
                    isArdy: true,
                    staleRequest: true,
                    lifetimeCancelled: false),
                Is.False);
            Assert.That(
                KimodoRuntimeMotionDriver.ShouldDiscardCompletedGenerationResult(
                    isArdy: false,
                    staleRequest: true,
                    lifetimeCancelled: false),
                Is.True);
            Assert.That(
                KimodoRuntimeMotionDriver.ShouldDiscardCompletedGenerationResult(
                    isArdy: true,
                    staleRequest: false,
                    lifetimeCancelled: true),
                Is.True);
        }

        [Test]
        public void StreamRefresh_DoesNotCancelAnActiveArdyGenerate()
        {
            Assert.That(KimodoRuntimeMotionDriver.ShouldCancelActiveGenerationForRefresh(isArdy: true), Is.False);
            Assert.That(KimodoRuntimeMotionDriver.ShouldCancelActiveGenerationForRefresh(isArdy: false), Is.True);
        }

        [TestCase(1.01f, 1f, false, false)]
        [TestCase(1f, 1f, false, true)]
        [TestCase(0.5f, 1f, false, true)]
        [TestCase(10f, 1f, true, true)]
        public void PlaybackReserve_TriggersOnlyAtLowWaterOrForPendingRefresh(
            float bufferedSeconds,
            float reserveSeconds,
            bool refreshPending,
            bool expected)
        {
            Assert.That(
                KimodoRuntimeMotionDriver.ShouldRequestArdyGeneration(
                    bufferedSeconds,
                    reserveSeconds,
                    refreshPending),
                Is.EqualTo(expected));
        }

        [TestCase(false, false, false, false, false, false)]
        [TestCase(true, false, false, false, false, true)]
        [TestCase(false, true, false, false, false, true)]
        [TestCase(false, false, false, true, true, false)]
        [TestCase(false, false, true, true, false, true)]
        [TestCase(false, false, true, false, true, true)]
        public void RuntimeSettings_RestartOnlyForTargetRuntimeSignatureOrArdySeed(
            bool targetChanged,
            bool runtimeSignatureChanged,
            bool isArdy,
            bool randomSeedModeChanged,
            bool deterministicSeedChanged,
            bool expected)
        {
            Assert.That(
                KimodoRuntimeMotionDriver.RequiresNewGenerationSession(
                    targetChanged,
                    runtimeSignatureChanged,
                    isArdy,
                    randomSeedModeChanged,
                    deterministicSeedChanged),
                Is.EqualTo(expected));
        }

        [Test]
        public void RawMotionAppend_GrowsOneContinuousTimeline()
        {
            KimodoRawMotionData first = CreateMotion(4, 2, 20f);
            KimodoRawMotionData second = CreateMotion(3, 2, 20f);

            Assert.That(first.TryAppend(second, 4, out string error), Is.True, error);
            Assert.That(first.FrameCount, Is.EqualTo(7));
        }

        [Test]
        public void RawMotionAppend_ReportsMissingCommittedArdyRange()
        {
            KimodoRawMotionData timeline = CreateMotion(40, 2, 20f);
            KimodoRawMotionData segment = CreateMotion(20, 2, 20f);

            Assert.That(timeline.TryAppend(segment, 60, out string error), Is.False);
            Assert.That(error, Does.Contain("starts at frame 60"));
            Assert.That(error, Does.Contain("has 40 frames"));
        }

        [Test]
        public void RawMotionAppend_CompactJsonDoesNotSerializeSpareContactCapacity()
        {
            KimodoRawMotionData first = CreateMotion(4, 2, 20f, withContacts: true);
            KimodoRawMotionData second = CreateMotion(3, 2, 20f, withContacts: true);

            Assert.That(first.TryAppend(second, 4, out string error), Is.True, error);
            MotionJsonData json = JsonUtility.FromJson<MotionJsonData>(
                KimodoRawMotionUtility.ToCompactJson(first));
            Assert.That(
                json.foot_contacts.Count,
                Is.EqualTo(7 * KimodoFootContactTrackUtility.ChannelCount));
        }

        [Test]
        public void TimelineBatchRange_ReplacesPreviouslyGeneratedFuture()
        {
            KimodoRawMotionData initial = CreateMotion(8, 2, 20f, absoluteStartFrame: 0);
            KimodoRawMotionData replacement = CreateMotion(6, 2, 20f, absoluteStartFrame: 4);

            KimodoRawMotionData merged = KimodoPlayableClipGenerationExecutionService.MergeRange(
                initial,
                new KimodoBridgeCommandResult
                {
                    MotionData = replacement,
                    StartFrame = 4,
                    EndFrameExclusive = 10
                });

            Assert.That(merged.FrameCount, Is.EqualTo(10));
            Assert.That(merged.TryReadUnityRootPosition(3, out Vector3 prefix), Is.True);
            Assert.That(merged.TryReadUnityRootPosition(9, out Vector3 tail), Is.True);
            Assert.That(prefix.x, Is.EqualTo(3f));
            Assert.That(tail.x, Is.EqualTo(9f));
        }

        [Test]
        public void TimelineBatchSelection_AcceptsConnectedCompatibleArdyClips()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip first = CreateArdyTimelineClip(track, 0.0, 2.0, 10);
                TimelineClip second = CreateArdyTimelineClip(track, 2.0, 2.0, 10);

                Assert.That(
                    KimodoPlayableClipGenerationExecutionService.TryValidateContinuousSelection(
                        new[] { second, first },
                        out string reason),
                    Is.True,
                    reason);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineBatchSelection_ReportsParameterDifferenceBeforeSerialFallback()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip first = CreateArdyTimelineClip(track, 0.0, 2.0, 10);
                TimelineClip second = CreateArdyTimelineClip(track, 2.0, 2.0, 5);

                Assert.That(
                    KimodoPlayableClipGenerationExecutionService.TryValidateContinuousSelection(
                        new[] { first, second },
                        out string reason),
                    Is.False);
                Assert.That(reason, Does.Contain("diffusion steps"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineBatchSelection_RejectsUnalignedPromptBoundary()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip first = CreateArdyTimelineClip(track, 0.0, 0.25, 10);
                TimelineClip second = CreateArdyTimelineClip(track, 0.25, 2.0, 10);

                Assert.That(
                    KimodoPlayableClipGenerationExecutionService.TryValidateContinuousSelection(
                        new[] { first, second },
                        out string reason),
                    Is.False);
                Assert.That(reason, Does.Contain("motion token"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineBatchInOutOverride_KeepsManualConstraintsWithoutBoundaries()
        {
            var manual = new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 1.0,
                kimodoRootPosition = new Vector3(2f, 0f, 3f)
            };
            KimodoInOutConstraintRequest request = KimodoInOutConstraintAdapter.BuildTimelineRequest(
                new KimodoTimelineInOutConstraintContext
                {
                    ModelName = KimodoMotionModelProfiles.ArdyCoreModelName
                },
                KimodoInOutConstraintMode.None,
                normalizeConstraintOrigin: true,
                enableIn: true,
                enableOut: false,
                generationFrames: 40,
                manualSamples: new List<KimodoMarkerSampleResult> { manual });

            Assert.That(request, Is.Not.Null);
            Assert.That(request.Mode, Is.EqualTo(KimodoInOutConstraintMode.None));
            Assert.That(request.EnableBegin, Is.False);
            Assert.That(request.EnableEnd, Is.False);
            Assert.That(request.AllowNormalizeConstraintOrigin, Is.True);
            Assert.That(request.ManualSamples, Has.Count.EqualTo(1));
        }

        [Test]
        public void ArdyRingBuffer_ReplacesAcrossWrapWithoutTouchingProtectedFrames()
        {
            KimodoRawMotionData first = CreateMotion(6, 2, 20f, absoluteStartFrame: 0);
            KimodoRawMotionData replacement = CreateMotion(6, 2, 20f, absoluteStartFrame: 4);
            using var buffer = new KimodoArdyMotionBuffer(first, capacityFrames: 8);

            Assert.That(buffer.TryReplace(first, 0, 0, out _, out string firstError), Is.True, firstError);
            Assert.That(
                buffer.TryReplace(replacement, 4, 6, out int writtenStart, out string replaceError),
                Is.True,
                replaceError);

            Assert.That(writtenStart, Is.EqualTo(6));
            Assert.That(buffer.StartFrame, Is.EqualTo(2));
            Assert.That(buffer.EndFrameExclusive, Is.EqualTo(10));
            Assert.That(buffer.TryReadRootPosition(5, out Vector3 protectedRoot), Is.True);
            Assert.That(buffer.TryReadRootPosition(9, out Vector3 replacedRoot), Is.True);
            Assert.That(protectedRoot.x, Is.EqualTo(5f));
            Assert.That(replacedRoot.x, Is.EqualTo(9f));
        }

        [Test]
        public void ArdyRingBuffer_LateReplacementDoesNotShrinkBufferedFuture()
        {
            KimodoRawMotionData first = CreateMotion(6, 2, 20f, absoluteStartFrame: 0);
            KimodoRawMotionData late = CreateMotion(4, 2, 20f, absoluteStartFrame: 0);
            using var buffer = new KimodoArdyMotionBuffer(first, capacityFrames: 8);

            Assert.That(buffer.TryReplace(first, 0, 0, out _, out string firstError), Is.True, firstError);
            Assert.That(buffer.TryReplace(late, 0, 6, out _, out string lateError), Is.True, lateError);

            Assert.That(buffer.EndFrameExclusive, Is.EqualTo(6));
            Assert.That(buffer.TryReadRootPosition(5, out Vector3 tail), Is.True);
            Assert.That(tail.x, Is.EqualTo(5f));
        }

        [Test]
        public void ConstraintJson_ARDYRootWaypointRequestsOfficialDensePath()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 2.0,
                kimodoRootPosition = new Vector3(3f, 0f, 4f),
                hasRootHeading = false
            };

            JArray constraints = JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { sample },
                    clipDurationSeconds: 8.0,
                    exportFps: 20.0,
                    denseRootPath: true));

            Assert.That(constraints[0].Value<bool>("dense_path"), Is.True);
            Assert.That(constraints[0]["frame_indices"]?[0]?.Value<int>(), Is.EqualTo(40));
        }

        [Test]
        public void ConstraintJson_TimeBetweenFramesUsesTheNextFrame()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 5.00001,
                kimodoRootPosition = Vector3.zero
            };

            JArray constraints = JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { sample },
                    clipStartSeconds: 4.0,
                    clipDurationSeconds: 2.0,
                    exportFps: 30.0));

            Assert.That(constraints[0]["frame_indices"]?[0]?.Value<int>(), Is.EqualTo(31));
        }

        [Test]
        public void ConstraintJson_ARDYRootTargetKeepsTimingInTheBackend()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "root2d_target",
                kimodoRootPosition = new Vector3(2f, 0f, 3f),
                rootTargetMaxSpeed = 1.25f,
                rootTargetMaxAcceleration = 1.5f,
                rootTargetArrivalThreshold = 0.1f,
                rootTargetIncludeHeading = true
            };

            JObject target = (JObject)JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { sample },
                    clipDurationSeconds: 8.0,
                    exportFps: 20.0))[0];

            Assert.That(target.Value<string>("type"), Is.EqualTo("root2d_target"));
            Assert.That(target["target_root_2d"]?[0]?.Value<float>(), Is.EqualTo(-2f));
            Assert.That(target["target_root_2d"]?[1]?.Value<float>(), Is.EqualTo(3f));
            Assert.That(target.Value<float>("max_speed"), Is.EqualTo(1.25f));
            Assert.That(target.Value<float>("max_acceleration"), Is.EqualTo(1.5f));
            Assert.That(target.Value<bool>("include_heading"), Is.True);
            Assert.That(target["frame_indices"], Is.Null);
        }

        [Test]
        public void ConstraintJson_ARDYRootHeadingUsesBridgeCoordinates()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 1.0,
                kimodoRootPosition = new Vector3(2f, 0f, 3f),
                hasRootHeading = true,
                rootHeading = new Vector2(-1f, 0f)
            };

            JArray constraints = JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { sample },
                    clipDurationSeconds: 8.0,
                    exportFps: 20.0));

            Assert.That(constraints[0]["global_root_heading"]?[0]?[0]?.Value<float>(), Is.EqualTo(1f));
            Assert.That(constraints[0]["global_root_heading"]?[0]?[1]?.Value<float>(), Is.EqualTo(0f));
        }

        [Test]
        public void FootIk_UsesSourceKneePoleAndKeepsItWhenTheSourceLegIsStraight()
        {
            var sourceHipsObject = new GameObject("SourceHips");
            var targetHipsObject = new GameObject("TargetHips");
            try
            {
                Transform sourceHips = sourceHipsObject.transform;
                Transform sourceUpper = new GameObject("SourceUpper").transform;
                Transform sourceKnee = new GameObject("SourceKnee").transform;
                Transform sourceFoot = new GameObject("SourceFoot").transform;
                sourceUpper.SetParent(sourceHips);
                sourceKnee.SetParent(sourceUpper);
                sourceFoot.SetParent(sourceKnee);
                sourceUpper.position = new Vector3(0f, 1f, 0f);
                sourceKnee.position = new Vector3(0f, 0.5f, 0.2f);
                sourceFoot.position = Vector3.zero;

                Transform targetHips = targetHipsObject.transform;
                targetHips.rotation = Quaternion.Euler(0f, 90f, 0f);
                Transform targetUpper = new GameObject("TargetUpper").transform;
                Transform targetKnee = new GameObject("TargetKnee").transform;
                Transform targetFoot = new GameObject("TargetFoot").transform;
                targetUpper.SetParent(targetHips);
                targetKnee.SetParent(targetUpper);
                targetFoot.SetParent(targetKnee);
                targetUpper.position = new Vector3(0f, 1f, 0f);
                targetKnee.position = new Vector3(-0.2f, 0.5f, 0f);
                targetFoot.position = Vector3.zero;
                Quaternion originalFootRotation = targetFoot.rotation;

                Vector3 previousPole = Vector3.zero;
                bool poleInitialized = false;
                KimodoRuntimeMotionPlayer.SolveTwoBoneLeg(
                    targetHips,
                    targetUpper,
                    targetKnee,
                    targetFoot,
                    sourceHips,
                    sourceUpper,
                    sourceKnee,
                    sourceFoot,
                    ref previousPole,
                    ref poleInitialized);

                Assert.That(targetKnee.position.x, Is.GreaterThan(0f));
                Assert.That(Quaternion.Angle(targetFoot.rotation, originalFootRotation), Is.LessThan(0.001f));

                sourceKnee.position = new Vector3(0f, 0.5f, 0f);
                sourceFoot.position = Vector3.zero;
                targetKnee.position = new Vector3(-0.2f, 0.5f, 0f);
                targetFoot.position = Vector3.zero;
                KimodoRuntimeMotionPlayer.SolveTwoBoneLeg(
                    targetHips,
                    targetUpper,
                    targetKnee,
                    targetFoot,
                    sourceHips,
                    sourceUpper,
                    sourceKnee,
                    sourceFoot,
                    ref previousPole,
                    ref poleInitialized);

                Assert.That(targetKnee.position.x, Is.GreaterThan(0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sourceHipsObject);
                UnityEngine.Object.DestroyImmediate(targetHipsObject);
            }
        }

        [TestCase(300, new[] { 300 })]
        [TestCase(301, new[] { 151, 150 })]
        [TestCase(600, new[] { 300, 300 })]
        [TestCase(601, new[] { 201, 200, 200 })]
        public void KimodoSegmentation_DistributesFramesWithoutAShortTail(int totalFrames, int[] expected)
        {
            Assert.That(KimodoEditorGeneratePipeline.PlanKimodoSegmentFrames(totalFrames), Is.EqualTo(expected));
        }

        [Test]
        public void KimodoSegmentation_UsesPreviousRawTailAsInAndKeepsFinalOutOnLastSegment()
        {
            var first = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(1f, 0f, 0f)
            };
            var finalOut = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 20.0,
                kimodoRootPosition = new Vector3(3f, 0f, 0f)
            };
            var tail = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 9.9,
                kimodoRootPosition = new Vector3(2f, 0f, 0f)
            };
            var source = new[] { first, finalOut };

            List<KimodoMarkerSampleResult> segment0 = KimodoEditorGeneratePipeline.BuildKimodoSegmentConstraintSamples(
                source,
                previousTail: null,
                totalFrameCount: 601,
                segmentStartFrame: 0,
                segmentFrameCount: 201,
                frameRate: 30f);
            List<KimodoMarkerSampleResult> segment1 = KimodoEditorGeneratePipeline.BuildKimodoSegmentConstraintSamples(
                source,
                tail,
                totalFrameCount: 601,
                segmentStartFrame: 201,
                segmentFrameCount: 200,
                frameRate: 30f);
            List<KimodoMarkerSampleResult> segment2 = KimodoEditorGeneratePipeline.BuildKimodoSegmentConstraintSamples(
                source,
                tail,
                totalFrameCount: 601,
                segmentStartFrame: 401,
                segmentFrameCount: 200,
                frameRate: 30f);

            Assert.That(segment0.Count, Is.EqualTo(1));
            Assert.That(segment0[0].kimodoRootPosition.x, Is.EqualTo(1f));
            Assert.That(segment1.Count, Is.EqualTo(1));
            Assert.That(segment1[0].kimodoRootPosition.x, Is.EqualTo(2f));
            Assert.That(segment1[0].sampleTime, Is.EqualTo(0.0));
            Assert.That(segment2.Count, Is.EqualTo(2));
            Assert.That(segment2[0].kimodoRootPosition.x, Is.EqualTo(2f));
            Assert.That(segment2[1].kimodoRootPosition.x, Is.EqualTo(3f));
            Assert.That(segment2[1].sampleTime, Is.EqualTo(199.0 / 30.0).Within(1e-9));
        }

        [Test]
        public void TimelineRequest_DerivesKimodoLengthBeyondTenSecondsFromTimeline()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.duration = 12.0;
                var playable = (KimodoPlayableClip)timelineClip.asset;
                playable.bridgeModelName = KimodoPlayableClip.DefaultBridgeModelName;
                playable.inOutConstraintMode = KimodoInOutConstraintMode.None;

                KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    playable,
                    "walk",
                    externalConstraint: null,
                    default);

                Assert.That(request.TargetFrameRate, Is.EqualTo(30f));
                Assert.That(request.TargetFrameCount, Is.EqualTo(360));
                Assert.That(KimodoEditorGeneratePipeline.PlanKimodoSegmentFrames(request.TargetFrameCount), Is.EqualTo(new[] { 180, 180 }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [TestCase(KimodoInOutConstraintMode.Inside, true, 1.0)]
        [TestCase(KimodoInOutConstraintMode.Inside, false, 89.0 / 30.0)]
        [TestCase(KimodoInOutConstraintMode.Outside, true, 29.0 / 30.0)]
        [TestCase(KimodoInOutConstraintMode.Outside, false, 3.0)]
        public void TimelineBoundarySampling_OffsetsOnlyTheOverlappingSideByOneFrame(
            KimodoInOutConstraintMode mode,
            bool isBegin,
            double expected)
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip previous = track.CreateClip<AnimationPlayableAsset>();
                previous.start = 0.0;
                previous.duration = 1.0;
                TimelineClip current = track.CreateClip<AnimationPlayableAsset>();
                current.start = 1.0;
                current.duration = 2.0;
                TimelineClip next = track.CreateClip<AnimationPlayableAsset>();
                next.start = 3.0;
                next.duration = 1.0;
                var request = new KimodoInOutConstraintRequest
                {
                    Mode = mode,
                    ModelName = KimodoPlayableClip.DefaultBridgeModelName,
                    TimelineContext = new KimodoTimelineInOutConstraintContext
                    {
                        SourceClip = current,
                        PreviousTimelineClip = previous,
                        NextTimelineClip = next
                    }
                };

                Assert.That(
                    KimodoInOutConstraintTools.ResolveTimelineBoundaryTime(request, isBegin),
                    Is.EqualTo(expected).Within(1e-9));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        private static KimodoRawMotionData CreateMotion(
            int frames,
            int joints,
            float fps,
            bool withContacts = false,
            int absoluteStartFrame = 0)
        {
            var rotations = new List<float>(frames * joints * 4);
            for (int frame = 0; frame < frames; frame++)
            {
                for (int joint = 0; joint < joints; joint++)
                {
                    rotations.Add(1f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                }
            }
            var roots = new Vector3[frames];
            for (int frame = 0; frame < frames; frame++)
            {
                roots[frame] = new Vector3(absoluteStartFrame + frame, 0f, 0f);
            }
            return new KimodoRawMotionData(
                frames,
                joints,
                fps,
                new[] { "Root", "Child" },
                new[] { -1, 0 },
                roots,
                rotations,
                0,
                withContacts ? new byte[frames * KimodoFootContactTrackUtility.ChannelCount] : null);
        }

        private static TimelineClip CreateArdyTimelineClip(
            AnimationTrack track,
            double start,
            double duration,
            int diffusionSteps)
        {
            TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
            timelineClip.start = start;
            timelineClip.duration = duration;
            var playable = (KimodoPlayableClip)timelineClip.asset;
            playable.bridgeModelName = KimodoMotionModelProfiles.ArdyCoreModelName;
            playable.diffusionSteps = diffusionSteps;
            playable.textWeight = 1f;
            playable.randomSeed = false;
            playable.seed = 42;
            return timelineClip;
        }
    }
}
