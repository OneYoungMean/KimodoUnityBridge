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

        [TestCase(1.0 / 30.0, 30.0, 1)]
        [TestCase(1.00001, 30.0, 30)]
        [TestCase(0.0, 30.0, 0)]
        public void SecondsToFrameIndex_UsesTimelineFrameFloor(
            double seconds,
            double frameRate,
            int expected)
        {
            Assert.That(
                KimodoFrameTimeUtility.SecondsToFrameIndex(seconds, frameRate),
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
        public void Root2DTarget_KimodoReportsThatAutomaticTargetsRequireArdy()
        {
            var gameObject = new GameObject("KimodoRuntimeMotionDriverTests.Root2DTarget");
            gameObject.SetActive(false);
            try
            {
                var driver = gameObject.AddComponent<KimodoRuntimeMotionDriver>();

                driver.SetRoot2DTarget(1f, 2f);

                Assert.That(driver.StatusMessage, Does.Contain("automatic ARDY-only"));
                Assert.That(driver.StatusMessage, Does.Contain("SetRoot2D"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Root2DWorldTarget_ConvertsSceneDeltaToModelOffset()
        {
            Vector3 currentWorldPosition = new Vector3(10f, 1f, 20f);
            Quaternion worldRotation = Quaternion.Euler(0f, 90f, 0f);
            Vector3 expectedLocalOffset = new Vector3(2f, 0f, 3f);
            Vector3 targetWorldPosition = currentWorldPosition + worldRotation * expectedLocalOffset;

            Vector2 actual = KimodoRuntimeMotionDriver.ResolveModelRoot2DOffset(
                currentWorldPosition,
                worldRotation,
                targetWorldPosition);

            Assert.That(actual.x, Is.EqualTo(expectedLocalOffset.x).Within(1e-5f));
            Assert.That(actual.y, Is.EqualTo(expectedLocalOffset.z).Within(1e-5f));
        }

        [Test]
        public void StreamRefresh_DoesNotCancelAnActiveArdyGenerate()
        {
            Assert.That(KimodoRuntimeMotionDriver.ShouldCancelActiveGenerationForRefresh(isArdy: true), Is.False);
            Assert.That(KimodoRuntimeMotionDriver.ShouldCancelActiveGenerationForRefresh(isArdy: false), Is.True);
        }

        [Test]
        public void RuntimeConstraintBuffer_StagesAndCommitsOneSamplePerType()
        {
            var buffer = new KimodoRuntimeConstraintBuffer();
            var firstHand = new KimodoMarkerSampleResult
            {
                constraintType = "left-hand",
                sampleTime = 1.0
            };
            var replacementHand = new KimodoMarkerSampleResult
            {
                constraintType = "LEFT-HAND",
                sampleTime = 2.0
            };
            buffer.Stage(firstHand, absoluteTimeOffset: 1.0);
            buffer.Stage(replacementHand);
            buffer.Stage(new KimodoMarkerSampleResult
            {
                constraintType = "left-foot",
                sampleTime = 3.0
            });

            Assert.That(buffer.StagedCount, Is.EqualTo(2));
            Assert.That(buffer.CommitStaged(), Is.True);
            Assert.That(buffer.StagedCount, Is.Zero);
            Assert.That(buffer.PendingCount, Is.EqualTo(2));

            List<KimodoMarkerSampleResult> active = buffer.BuildActive(
                isArdy: false,
                ardyApplyTime: 0.0,
                includeOverlap: false,
                maxConstraintTime: 5f,
                fullBodyConstraintType: "fullbody",
                root2DTargetConstraintType: "root2d_target");
            Assert.That(active, Has.Count.EqualTo(2));
            Assert.That(active[0].sampleTime, Is.EqualTo(2.0).Within(1e-6));
            Assert.That(active[1].sampleTime, Is.EqualTo(3.0).Within(1e-6));
        }

        [Test]
        public void RuntimeConstraintBuffer_UsesArdyAbsoluteTimeAndNormalTargetFiltering()
        {
            var buffer = new KimodoRuntimeConstraintBuffer();
            buffer.Stage(new KimodoMarkerSampleResult
            {
                constraintType = "root2d_target",
                sampleTime = 2.0
            }, absoluteTimeOffset: 10.0);
            Assert.That(buffer.CommitStaged(), Is.True);

            List<KimodoMarkerSampleResult> ardyActive = buffer.BuildActive(
                isArdy: true,
                ardyApplyTime: 11.0,
                includeOverlap: false,
                maxConstraintTime: 5f,
                fullBodyConstraintType: "fullbody",
                root2DTargetConstraintType: "root2d_target");
            Assert.That(ardyActive, Has.Count.EqualTo(1));
            Assert.That(ardyActive[0].sampleTime, Is.EqualTo(1.0).Within(1e-6));

            buffer.CompleteGeneration(isArdy: true);
            Assert.That(buffer.PendingCount, Is.EqualTo(1));
            Assert.That(
                buffer.BuildActive(
                    isArdy: false,
                    ardyApplyTime: 0.0,
                    includeOverlap: false,
                    maxConstraintTime: 5f,
                    fullBodyConstraintType: "fullbody",
                    root2DTargetConstraintType: "root2d_target"),
                Is.Empty);
            Assert.That(buffer.PendingCount, Is.Zero);
        }

        [Test]
        public void RuntimeConstraintBuffer_OnlyAddsOverlapPosesToNormalKimodo()
        {
            var buffer = new KimodoRuntimeConstraintBuffer();
            var overlap = new KimodoMarkerSampleResult
            {
                constraintType = "left-hand",
                sampleTime = 0.5,
                kimodoRootPosition = new Vector3(4f, 2f, 3f)
            };
            buffer.SetOverlapPoses(new[] { overlap });

            List<KimodoMarkerSampleResult> normalActive = buffer.BuildActive(
                isArdy: false,
                ardyApplyTime: 0.0,
                includeOverlap: true,
                maxConstraintTime: 5f,
                fullBodyConstraintType: "fullbody",
                root2DTargetConstraintType: "root2d_target");
            Assert.That(normalActive, Has.Count.EqualTo(1));
            Assert.That(normalActive[0].constraintType, Is.EqualTo("fullbody"));
            Assert.That(normalActive[0].kimodoRootPosition, Is.EqualTo(new Vector3(0f, 2f, 0f)));

            Assert.That(
                buffer.BuildActive(
                    isArdy: true,
                    ardyApplyTime: 0.0,
                    includeOverlap: false,
                    maxConstraintTime: 5f,
                    fullBodyConstraintType: "fullbody",
                    root2DTargetConstraintType: "root2d_target"),
                Is.Empty);
        }

        [Test]
        public void RuntimeGenerationSession_ResetAndDisposeOwnsArdyLifecycle()
        {
            var session = new KimodoRuntimeGenerationSession
            {
                LifetimeCts = new System.Threading.CancellationTokenSource(),
                ActiveGenerationCts = new System.Threading.CancellationTokenSource(),
                Running = true,
                GenerationInFlight = true,
                ArdySessionStarted = true,
                ArdyPromptDirty = false,
                ArdyConstraintsDirty = false,
                ArdySettingsDirty = false,
                ArdyRefreshPending = true
            };
            try
            {
                session.ResetArdy(0f);

                Assert.That(session.ArdySessionStarted, Is.False);
                Assert.That(session.ArdyPromptDirty, Is.True);
                Assert.That(session.ArdyConstraintsDirty, Is.True);
                Assert.That(session.ArdySettingsDirty, Is.True);
                Assert.That(session.ArdyRefreshPending, Is.False);
                Assert.That(session.ArdyEffectivePlaybackReserveSeconds, Is.EqualTo(0.2f));

                var lifetimeCts = session.LifetimeCts;
                var generationCts = session.ActiveGenerationCts;
                session.Dispose();
                Assert.That(lifetimeCts.IsCancellationRequested, Is.True);
                Assert.That(generationCts.IsCancellationRequested, Is.True);
                Assert.That(session.LifetimeCts, Is.Null);
                Assert.That(session.ActiveGenerationCts, Is.Null);
                Assert.That(session.Running, Is.False);
                Assert.That(session.GenerationInFlight, Is.False);
            }
            finally
            {
                session.Dispose();
            }
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
        public void TimelineConnectedSelection_AcceptsGapsUnalignedDurationAndParameterDifferences()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip first = CreateArdyTimelineClip(track, 0.0, 0.23, 10);
                TimelineClip second = CreateArdyTimelineClip(track, 1.0, 2.0, 5);
                var secondPlayable = (KimodoPlayableClip)second.asset;
                secondPlayable.textWeight = 2f;
                secondPlayable.randomSeed = true;
                secondPlayable.seed = 99;

                Assert.That(
                    KimodoPlayableClipGenerationExecutionService.TryValidateConnectedSelection(
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
        public void TimelineConnectedSelection_RejectsDifferentTrack()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                AnimationTrack otherTrack = timeline.CreateTrack<AnimationTrack>(null, "Other");
                TimelineClip first = CreateArdyTimelineClip(track, 0.0, 2.0, 10);
                TimelineClip second = CreateArdyTimelineClip(otherTrack, 2.0, 2.0, 10);

                Assert.That(
                    KimodoPlayableClipGenerationExecutionService.TryValidateConnectedSelection(
                        new[] { first, second },
                        out string reason),
                    Is.False);
                Assert.That(reason, Does.Contain("same Timeline track"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineConnectedSelection_RejectsDifferentModel()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip first = CreateArdyTimelineClip(track, 0.0, 2.0, 10);
                TimelineClip second = CreateArdyTimelineClip(track, 2.0, 2.0, 10);
                ((KimodoPlayableClip)second.asset).bridgeModelName = KimodoMotionModelProfiles.ArdyG1ModelName;

                Assert.That(
                    KimodoPlayableClipGenerationExecutionService.TryValidateConnectedSelection(
                        new[] { first, second },
                        out string reason),
                    Is.False);
                Assert.That(reason, Does.Contain("different model/profile"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineConnectedSelection_RejectsDifferentTextEncoder()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip first = CreateArdyTimelineClip(track, 0.0, 2.0, 10);
                TimelineClip second = CreateArdyTimelineClip(track, 2.0, 2.0, 10);
                ((KimodoPlayableClip)second.asset).textEncoderMode = KimodoTextEncoderMode.HighPrecision;

                Assert.That(
                    KimodoPlayableClipGenerationExecutionService.TryValidateConnectedSelection(
                        new[] { first, second },
                        out string reason),
                    Is.False);
                Assert.That(reason, Does.Contain("Text Encoder mode"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineConnectedInOutOverride_KeepsManualConstraintsWithoutBoundaries()
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
                autoBeginAnchor: true,
                deferNormalization: true,
                enableIn: true,
                enableOut: false,
                generationFrames: 40,
                manualSamples: new List<KimodoMarkerSampleResult> { manual });

            Assert.That(request, Is.Not.Null);
            Assert.That(request.Mode, Is.EqualTo(KimodoInOutConstraintMode.None));
            Assert.That(request.EnableBegin, Is.False);
            Assert.That(request.EnableEnd, Is.False);
            Assert.That(request.AutoBeginAnchor, Is.True);
            Assert.That(request.DeferNormalization, Is.True);
            Assert.That(request.ManualSamples, Has.Count.EqualTo(1));
        }

        [Test]
        public void TimelineOutsideGuard_OffsetsManualConstraintsByRuntimeFrame()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip clip = track.CreateClip<KimodoPlayableClip>();
                clip.start = 10.0;
                clip.duration = 2.0;
                var manual = new KimodoMarkerSampleResult
                {
                    constraintType = "fullbody",
                    sampleTime = 10.0
                };

                KimodoInOutConstraintRequest request = KimodoInOutConstraintAdapter.BuildTimelineRequest(
                    new KimodoTimelineInOutConstraintContext
                    {
                        SourceClip = clip,
                        ModelName = KimodoMotionModelProfiles.ArdyCoreModelName
                    },
                    KimodoInOutConstraintMode.Outside,
                    autoBeginAnchor: false,
                    deferNormalization: true,
                    enableIn: false,
                    enableOut: false,
                    generationFrames: 61,
                    manualSamples: new List<KimodoMarkerSampleResult> { manual },
                    sampleTimeOffsetSeconds: 1.0 / 20.0);

                Assert.That(request.ManualSamples[0].sampleTime, Is.EqualTo(1.0 / 20.0).Within(1e-9));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineOutsideGuard_ArdyOutUsesModelFrameRateAndTargetsRuntimeTail()
        {
            float frameRate = KimodoMotionModelProfiles.ResolveGenerationFrameRate(
                KimodoMotionModelProfiles.ArdyCoreModelName);
            var request = new KimodoInOutConstraintRequest
            {
                GenerationFrames = 102
            };
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = KimodoInOutConstraintTools.ResolveConstraintEndSampleTimeSeconds(
                    request.GenerationFrames,
                    frameRate),
                localAxisAngles = new List<Vector3> { Vector3.zero }
            };

            string json = KimodoConstraintJsonExporter.ToConstraintsJson(
                new[] { sample },
                clipStartSeconds: 0.0,
                clipDurationSeconds: KimodoInOutConstraintTools.ResolveConstraintClipDurationSeconds(
                    request.GenerationFrames,
                    frameRate),
                exportFps: frameRate);
            JArray constraints = JArray.Parse(json);

            Assert.That(frameRate, Is.EqualTo(20f));
            Assert.That(constraints[0]["frame_indices"]?[0]?.Value<int>(), Is.EqualTo(101));
        }

        [Test]
        [Category("ArdyGuardValidation")]
        public void TimelineOutsideGuard_TrimRuntimeResultDropsLeadingFrame()
        {
            KimodoRawMotionData source = CreateMotion(4, 2, 30f);
            var request = new KimodoEditorGenerateRequest
            {
                TargetFrameCount = 3,
                TargetFrameRate = 30f,
                RuntimeFrameCount = 4,
                RuntimeTrimStartFrame = 1
            };
            var result = new KimodoBridgeCommandResult
            {
                MotionData = source,
                MotionFormat = "kmb_v1"
            };

            KimodoBridgeCommandResult trimmed = KimodoEditorGeneratePipeline.TrimRuntimeResultForOutput(
                request,
                result,
                KimodoPlayableClip.DefaultBridgeModelName);

            Assert.That(trimmed.MotionData.FrameCount, Is.EqualTo(3));
            Assert.That(trimmed.MotionData.TryReadUnityRootPosition(0, out Vector3 first), Is.True);
            Assert.That(first.x, Is.EqualTo(1f));
            Assert.That(trimmed.MotionJsonCompact, Does.Contain("\"num_frames\":3"));
            Assert.That(trimmed.MotionBytes, Is.Not.Null);
        }

        [Test]
        public void ArdyInConstraint_IsSampledAsOrdinaryFullBodyConstraint()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip previous = CreateArdyTimelineClip(track, 0.0, 2.0, 10);
                TimelineClip current = CreateArdyTimelineClip(track, 2.0, 2.0, 10);
                var context = new KimodoTimelineInOutConstraintContext
                {
                    SourceClip = current,
                    PreviousTimelineClip = previous,
                    ModelName = KimodoMotionModelProfiles.ArdyCoreModelName
                };

                KimodoInOutConstraintRequest exportRequest = KimodoInOutConstraintAdapter.BuildTimelineRequest(
                    context,
                    KimodoInOutConstraintMode.Outside,
                    autoBeginAnchor: false,
                    deferNormalization: true,
                    enableIn: true,
                    enableOut: false,
                    generationFrames: 40,
                    manualSamples: null);

                Assert.That(exportRequest, Is.Not.Null);
                Assert.That(exportRequest.EnableBegin, Is.True);
                Assert.That(exportRequest.EnableEnd, Is.False);

                KimodoInOutConstraintRequest insideRequest = KimodoInOutConstraintAdapter.BuildTimelineRequest(
                    context,
                    KimodoInOutConstraintMode.Inside,
                    autoBeginAnchor: false,
                    deferNormalization: true,
                    enableIn: true,
                    enableOut: false,
                    generationFrames: 40,
                    manualSamples: null);
                Assert.That(insideRequest, Is.Not.Null);
                Assert.That(insideRequest.EnableBegin, Is.True);
                Assert.That(insideRequest.EnableEnd, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
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
        public void ConstraintJson_TimeBetweenFramesUsesTheTimelineSampleFrame()
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

            Assert.That(constraints[0]["frame_indices"]?[0]?.Value<int>(), Is.EqualTo(30));
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
        public void ConstraintJson_RootHeadingUsesArdyCosSinFeatureOrder()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 1.0,
                kimodoRootPosition = new Vector3(2f, 0f, 3f),
                hasRootHeading = true,
                rootHeading = new Vector2(0.6f, 0.8f)
            };

            JArray constraints = JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { sample },
                    clipDurationSeconds: 8.0,
                    exportFps: 20.0));

            Assert.That(constraints[0]["global_root_heading"]?[0]?[0]?.Value<float>(), Is.EqualTo(0.8f));
            Assert.That(constraints[0]["global_root_heading"]?[0]?[1]?.Value<float>(), Is.EqualTo(-0.6f));
        }

        [Test]
        public void FootIk_RequiresAnExplicitTargetBeforeSolvingTheLeg()
        {
            Assert.That(KimodoRuntimeMotionPlayer.ShouldSolveFootIk(false, null), Is.False);
            Assert.That(KimodoRuntimeMotionPlayer.ShouldSolveFootIk(true, null), Is.False);
        }

        [Test]
        public void ConstraintJson_EndEffectorTargetUsesRootLocalPointAndKeepsLegacyFrames()
        {
            Quaternion rootRotation = Quaternion.Euler(0f, 90f, 0f);
            var targeted = new KimodoMarkerSampleResult
            {
                constraintType = "left-hand",
                sampleTime = 1.0,
                kimodoRootPosition = new Vector3(1f, 2f, 3f),
                hasEndEffectorTargetPosition = true,
                endEffectorTargetPositionRootLocal = new Vector3(1f, 0.5f, 0f),
                localAxisAngles = new List<Vector3>
                {
                    KimodoRuntimeUtility.QuaternionToAxisAngleVector(rootRotation)
                }
            };
            var legacy = targeted.Clone();
            legacy.sampleTime = 2.0;
            legacy.hasEndEffectorTargetPosition = false;

            JArray constraints = JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { targeted, legacy },
                    clipDurationSeconds: 4.0,
                    exportFps: 30.0));

            Vector3 unityTarget = targeted.kimodoRootPosition +
                rootRotation * targeted.endEffectorTargetPositionRootLocal;
            JToken positions = constraints[0]["target_positions"];
            Assert.That(positions, Is.Not.Null);
            Assert.That(((JArray)positions).Count, Is.EqualTo(2));
            Assert.That(positions[0]?[0]?.Value<float>(), Is.EqualTo(-unityTarget.x).Within(1e-5f));
            Assert.That(positions[0]?[1]?.Value<float>(), Is.EqualTo(unityTarget.y).Within(1e-5f));
            Assert.That(positions[0]?[2]?.Value<float>(), Is.EqualTo(unityTarget.z).Within(1e-5f));
            Assert.That(positions[1]?.Type, Is.EqualTo(JTokenType.Null));
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
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineGeneration_ResetsCapturedInsideConstraintTimeScale()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.timeScale = 2.0;
                var request = new KimodoEditorGenerateRequest
                {
                    TimelineClipSnapshot = timelineClip,
                    ResetTimelineTimeScaleAfterGeneration = true
                };

                Assert.That(
                    KimodoPlayableClipGenerationHostService.ResetTimelineTimeScaleAfterGeneration(request),
                    Is.True);
                Assert.That(timelineClip.timeScale, Is.EqualTo(1.0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineOutputPlan_KeepsAvatarSnapshotAfterSourceContextIsGone()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);

            KimodoPlayableClip playable = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            var generated = new AnimationClip();
            try
            {
                playable.curveFilterOptions.positionError = 0.125f;
                KimodoEditorGenerateOutputPlan snapshot =
                    KimodoPlayableClipGenerationHostService.CaptureTimelineOutputPlan(
                        playable,
                        avatar,
                        KimodoPlayableClip.DefaultBridgeModelName,
                        bindingObject: null);
                UnityEngine.Object.DestroyImmediate(playable);
                playable = null;

                KimodoEditorGenerateOutputPlan resolved =
                    KimodoPlayableClipGenerationHostService.ResolveTimelineOutputPlan(
                        snapshot,
                        bindingObject: null,
                        generated,
                        KimodoPlayableClip.DefaultBridgeModelName);

                Assert.That(resolved.TargetRetargetAvatar, Is.SameAs(avatar));
                Assert.That(KimodoRetargetCoreUtility.IsValidHumanoid(resolved.OriginRetargetAvatar), Is.True);
                Assert.That(resolved.CurveFilterOptions.positionError, Is.EqualTo(0.125f));
                Assert.That(resolved.SkipRetarget, Is.False);
            }
            finally
            {
                if (playable != null)
                {
                    UnityEngine.Object.DestroyImmediate(playable);
                }
                UnityEngine.Object.DestroyImmediate(generated);
            }
        }

        [TestCase(KimodoInOutConstraintMode.Inside, true, 1.0)]
        [TestCase(KimodoInOutConstraintMode.Inside, false, 179.0 / 60.0)]
        [TestCase(KimodoInOutConstraintMode.Outside, true, 59.0 / 60.0)]
        [TestCase(KimodoInOutConstraintMode.Outside, false, 3.0)]
        public void TimelineBoundarySampling_OffsetsOnlyTheOverlappingSideByOneTimelineFrame(
            KimodoInOutConstraintMode mode,
            bool isBegin,
            double expected)
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                timeline.editorSettings.frameRate = 60.0;
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
                    ModelName = "ARDY-Core-RP-20FPS-Horizon40",
                    TimelineContext = new KimodoTimelineInOutConstraintContext
                    {
                        SourceClip = current,
                        PreviousTimelineClip = previous,
                        NextTimelineClip = next
                    }
                };

                double sampleTime = KimodoInOutConstraintTools.ResolveTimelineBoundaryTime(request, isBegin);
                if (mode == KimodoInOutConstraintMode.Outside && !isBegin)
                {
                    Assert.That(sampleTime, Is.GreaterThan(expected));
                    Assert.That(sampleTime, Is.LessThan(expected + 1e-5));
                    Assert.That(
                        KimodoTimelineConstraintClipCache.ResolveTimelineSampleFrame(sampleTime, 60f),
                        Is.EqualTo(KimodoTimelineConstraintClipCache.ResolveTimelineSampleFrame(expected, 60f)));
                }
                else
                {
                    Assert.That(sampleTime, Is.EqualTo(expected).Within(1e-9));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineBoundarySampling_OutsideOutWithSubFrameGapUsesFrame354AndSamplesPastBoundary()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                timeline.editorSettings.frameRate = 60.0;
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip previous = track.CreateClip<AnimationPlayableAsset>();
                previous.start = 0.9666666667;
                previous.duration = 4.9333333969;
                double preciseEnd = previous.start + previous.duration;
                TimelineClip current = track.CreateClip<AnimationPlayableAsset>();
                current.start = preciseEnd;
                current.duration = 2.0;
                var request = new KimodoInOutConstraintRequest
                {
                    Mode = KimodoInOutConstraintMode.Outside,
                    TimelineContext = new KimodoTimelineInOutConstraintContext
                    {
                        SourceClip = previous,
                        NextTimelineClip = current
                    }
                };

                double sampleTime = KimodoInOutConstraintTools.ResolveTimelineBoundaryTime(request, isBegin: false);

                Assert.That(
                    KimodoTimelineConstraintClipCache.ResolveTimelineSampleFrame(sampleTime, 60f),
                    Is.EqualTo(354));
                Assert.That(sampleTime, Is.GreaterThan(preciseEnd));
                Assert.That(sampleTime, Is.LessThan(preciseEnd + 1e-5));
                Assert.That(sampleTime, Is.GreaterThan(354.0 / 60.0));
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
