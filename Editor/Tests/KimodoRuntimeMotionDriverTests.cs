#if false
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;
using UnityEngine.Playables;
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

        [Test]
        public void MotionModelProfiles_OwnSharedGenerationSemantics()
        {
            Assert.That(KimodoMotionModelProfiles.NormalizeName("  "), Is.EqualTo(KimodoMotionModelProfiles.DefaultModelName));
            Assert.That(KimodoMotionModelProfiles.NormalizeName(" model "), Is.EqualTo("model"));
            Assert.That(KimodoMotionModelProfiles.ResolveGenerationFrameRate("ardy-g1"), Is.EqualTo(25f));
            Assert.That(KimodoMotionModelProfiles.ResolveBakeSkeletonType("Kimodo-SMPLX-RP-v1"), Is.EqualTo(KimodoBakeSkeletonType.SMPLX));
            Assert.That(KimodoMotionModelProfiles.TryGetArdy("ardy-core", out KimodoMotionModelProfile profile), Is.True);
            Assert.That(profile.ModelName, Is.EqualTo(KimodoMotionModelProfiles.ArdyCoreModelName));

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

        [TestCase(-0.051, 20.0, -1)]
        [TestCase(-0.01, 20.0, 0)]
        [TestCase(0.051, 20.0, 2)]
        public void SecondsToProtocolFrameIndex_UsesSignedCeiling(
            double seconds,
            double frameRate,
            int expected)
        {
            Assert.That(
                KimodoFrameTimeUtility.SecondsToProtocolFrameIndex(seconds, frameRate),
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

        [Test]
        public void SelectedTimelineGeneration_OrdersClipsByStart()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                var clips = new List<TimelineClip>
                {
                    CreateArdyTimelineClip(track, 4.0, 1.0, 10),
                    CreateArdyTimelineClip(track, 1.0, 1.0, 10),
                    CreateArdyTimelineClip(track, 2.0, 1.0, 10)
                };

                clips.Sort(KimodoPlayableClipGenerationExecutionService.CompareTimelineClips);

                Assert.That(clips[0].start, Is.EqualTo(1.0));
                Assert.That(clips[1].start, Is.EqualTo(2.0));
                Assert.That(clips[2].start, Is.EqualTo(4.0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
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

            Assert.DoesNotThrow(() => KimodoRuntimeSegmentBuilder.ValidateArdyResult(result, profile, 42));
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
                KimodoRuntimeSegmentBuilder.ValidateArdyResult(result, profile, 42));
        }

        [Test]
        public void RuntimeSegmentBuilder_CreatesPlayableArdySegment()
        {
            var motion = new KimodoRawMotionData(
                2,
                1,
                20f,
                new[] { "Hips" },
                new[] { -1 },
                new[] { new Vector3(1f, 0f, 2f), new Vector3(3f, 0f, 4f) },
                new List<float>
                {
                    1f, 0f, 0f, 0f,
                    1f, 0f, 0f, 0f
                },
                0);
            var result = new KimodoBridgeGenerationResult
            {
                MotionData = motion,
                MotionBytes = new byte[] { 1 },
                MotionRepFingerprint = "fingerprint",
                ResolvedSeed = 7
            };

            KimodoRuntimeGeneratedSegment segment = KimodoRuntimeSegmentBuilder.BuildAsync(
                result,
                KimodoMotionModelProfiles.ArdyCoreModelName,
                "walk",
                3,
                true,
                new KimodoSegmentTrimTrailSettings(),
                false,
                System.Threading.CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(segment.Index, Is.EqualTo(3));
            Assert.That(segment.LastRootPosition, Is.EqualTo(new Vector3(3f, 0f, 4f)));
            Assert.That(segment.EffectiveLastFrameTimeSeconds, Is.EqualTo(0.1f));
            Assert.That(segment.UseRawRootPosition, Is.True);
        }

        [Test]
        public void CompletedStaleArdyResult_IsKeptToPreserveTheServerCursor()
        {
            Assert.That(
                KimodoRuntimeGenerationSession.ShouldDiscardResult(
                    isArdy: true,
                    staleRequest: true,
                    lifetimeCancelled: false),
                Is.False);
            Assert.That(
                KimodoRuntimeGenerationSession.ShouldDiscardResult(
                    isArdy: false,
                    staleRequest: true,
                    lifetimeCancelled: false),
                Is.True);
            Assert.That(
                KimodoRuntimeGenerationSession.ShouldDiscardResult(
                    isArdy: true,
                    staleRequest: false,
                    lifetimeCancelled: true),
                Is.True);
        }

        [Test]
        public void Root2DTarget_KimodoAcceptsTheAutomaticTargetApi()
        {
            var gameObject = new GameObject("KimodoRuntimeMotionDriverTests.Root2DTarget");
            gameObject.SetActive(false);
            try
            {
                var driver = gameObject.AddComponent<KimodoRuntimeMotionDriver>();

                driver.SetRoot2DTarget(1f, 2f, arrivalThresholdMeters: 3f);

                Assert.That(driver.StatusMessage, Does.Contain("already within"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void RuntimeConstraintSampler_RejectsUseBeforeDriverInitialization()
        {
            Assert.That(
                KimodoRuntimeConstraintSampler.TryCreateEndEffector(
                    null,
                    KimodoMotionModelProfiles.DefaultModelName,
                    KimodoRuntimeConstraints.LeftHandType,
                    "LeftHand",
                    Vector3.zero,
                    1f,
                    out _,
                    out string error),
                Is.False);
            Assert.That(error, Does.Contain("before the driver is initialized"));
        }

        [TestCase(0.5f, 1.25f, 1.5f, 1.1547005f)]
        [TestCase(5f, 1.25f, 1.5f, 4.8333335f)]
        public void Root2DTargetDuration_UsesAccelerationAndCruiseLimits(
            float distance,
            float maxSpeed,
            float maxAcceleration,
            float expected)
        {
            Assert.That(
                KimodoRoot2DPlanner.EstimateDuration(
                    distance,
                    maxSpeed,
                    maxAcceleration,
                    0.1f,
                    10f),
                Is.EqualTo(expected).Within(1e-5f));
        }

        [Test]
        public void Root2DWorldTarget_ConvertsSceneDeltaToModelOffset()
        {
            Vector3 currentWorldPosition = new Vector3(10f, 1f, 20f);
            Quaternion worldRotation = Quaternion.Euler(0f, 90f, 0f);
            Vector3 expectedLocalOffset = new Vector3(2f, 0f, 3f);
            Vector3 targetWorldPosition = currentWorldPosition + worldRotation * expectedLocalOffset;

            Vector2 actual = KimodoRoot2DPlanner.ToModelOffset(
                currentWorldPosition,
                worldRotation,
                targetWorldPosition);

            Assert.That(actual.x, Is.EqualTo(expectedLocalOffset.x).Within(1e-5f));
            Assert.That(actual.y, Is.EqualTo(expectedLocalOffset.z).Within(1e-5f));
        }

        [Test]
        public void Root2DWorldHeading_ConvertsIntoModelBasis()
        {
            Vector2 actual = KimodoRoot2DPlanner.ToModelHeading(
                Quaternion.Euler(0f, 90f, 0f),
                Vector2.right);

            Assert.That(actual.x, Is.Zero.Within(1e-5f));
            Assert.That(actual.y, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void Root2DPublicApi_ExposesOnlyWorldSpaceEntryPoints()
        {
            var methodNames = new HashSet<string>(
                typeof(KimodoRuntimeMotionDriver)
                    .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    .Select(method => method.Name));

            Assert.That(methodNames, Does.Contain("SetRoot2D"));
            Assert.That(methodNames, Does.Contain("QueuePromptedRoot2D"));
            Assert.That(methodNames, Does.Not.Contain("SetRoot2DWorld"));
            Assert.That(methodNames, Does.Not.Contain("SetRoot2DLocal"));
            Assert.That(methodNames, Does.Not.Contain("QueuePromptedRoot2DLocal"));
        }

        [Test]
        public void RuntimeDriverPublicApi_DoesNotKeepPromptAliases()
        {
            var methodNames = new HashSet<string>(
                typeof(KimodoRuntimeMotionDriver)
                    .GetMethods(System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.Public |
                                System.Reflection.BindingFlags.DeclaredOnly)
                    .Select(method => method.Name));

            Assert.That(methodNames, Does.Contain("SetAnimationPrompt"));
            Assert.That(methodNames, Does.Contain("GetCurrentPrompt"));
            Assert.That(methodNames, Does.Not.Contain("SetPrompt"));
            Assert.That(methodNames, Does.Not.Contain("GetAnimationPrompt"));
        }

        [Test]
        public void RuntimeConstraints_StagesOwnedSamplesAndCommitsOnePerType()
        {
            var buffer = new KimodoRuntimeConstraints();
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
            Assert.That(firstHand.sampleTime, Is.EqualTo(1.0), "Staging must not mutate the caller's pose.");
            buffer.Stage(replacementHand);
            buffer.Stage(new KimodoMarkerSampleResult
            {
                constraintType = "left-foot",
                sampleTime = 3.0
            });

            Assert.That(buffer.StagedCount, Is.EqualTo(2));
            Assert.That(buffer.Commit(), Is.True);
            Assert.That(buffer.StagedCount, Is.Zero);
            Assert.That(buffer.PendingCount, Is.EqualTo(2));

            List<KimodoMarkerSampleResult> active = buffer.BuildForGeneration(
                isArdy: false,
                playbackTime: 0.0,
                duration: 5f);
            Assert.That(active, Has.Count.EqualTo(2));
            Assert.That(active[0].sampleTime, Is.EqualTo(2.0).Within(1e-6));
            Assert.That(active[1].sampleTime, Is.EqualTo(3.0).Within(1e-6));
        }

        [Test]
        public void RuntimeConstraints_MergesDifferentChannelsAtOneFrameAndReplacesOnlyTheirChannels()
        {
            var buffer = new KimodoRuntimeConstraints();
            buffer.Stage(new KimodoMarkerSampleResult
            {
                constraintType = "left-hand",
                sampleTime = 1.0
            });
            buffer.Stage(new KimodoMarkerSampleResult
            {
                constraintType = "right-foot",
                sampleTime = 1.0
            });
            Assert.That(buffer.Commit(), Is.True);

            List<KimodoMarkerSampleResult> merged = buffer.BuildForGeneration(
                isArdy: false,
                playbackTime: 0.0,
                duration: 5f);
            Assert.That(merged, Has.Count.EqualTo(1));
            Assert.That(merged[0].mask.leftHand && merged[0].mask.rightFoot, Is.True);

            buffer.Stage(new KimodoMarkerSampleResult
            {
                constraintType = "left-hand",
                sampleTime = 2.0
            });

            Assert.That(buffer.Commit(), Is.True);
            List<KimodoMarkerSampleResult> active = buffer.BuildForGeneration(
                isArdy: false,
                playbackTime: 0.0,
                duration: 5f);

            Assert.That(active, Has.Count.EqualTo(2));
            KimodoMarkerSampleResult first = active.Single(item => Math.Abs(item.sampleTime - 1.0) <= 1e-6);
            KimodoMarkerSampleResult second = active.Single(item => Math.Abs(item.sampleTime - 2.0) <= 1e-6);
            Assert.That(first.mask.rightFoot, Is.True);
            Assert.That(first.mask.leftHand, Is.False);
            Assert.That(second.mask.leftHand, Is.True);
        }

        [Test]
        public void RuntimeConstraints_UsesArdyAbsoluteTimeAndNormalTargetTiming()
        {
            var buffer = new KimodoRuntimeConstraints();
            buffer.Stage(new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 2.0
            }, absoluteTimeOffset: 10.0);
            Assert.That(buffer.Commit(), Is.True);

            List<KimodoMarkerSampleResult> ardyActive = buffer.BuildForGeneration(
                isArdy: true,
                playbackTime: 11.0,
                duration: 5f);
            Assert.That(ardyActive, Has.Count.EqualTo(1));
            Assert.That(ardyActive[0].sampleTime, Is.EqualTo(1.0).Within(1e-6));

            buffer.CompleteGeneration(isArdy: true);
            Assert.That(buffer.PendingCount, Is.EqualTo(1));
            List<KimodoMarkerSampleResult> normalActive = buffer.BuildForGeneration(
                isArdy: false,
                playbackTime: 0.0,
                duration: 5f);
            Assert.That(normalActive, Has.Count.EqualTo(1));
            Assert.That(normalActive[0].sampleTime, Is.EqualTo(5.0).Within(1e-6));

            buffer.CompleteGeneration(isArdy: false);
            Assert.That(buffer.PendingCount, Is.Zero);
        }

        [Test]
        public void RuntimeConstraints_PreservesConstraintCommittedDuringGeneration()
        {
            var buffer = new KimodoRuntimeConstraints();
            buffer.Stage(new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 1.0
            });
            Assert.That(buffer.Commit(), Is.True);
            int consumedRevision = buffer.PendingRevision;

            buffer.Stage(new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 2.0
            });
            Assert.That(buffer.Commit(), Is.True);
            buffer.CompleteGeneration(isArdy: false, consumedRevision: consumedRevision);

            Assert.That(buffer.PendingCount, Is.EqualTo(2));
            List<KimodoMarkerSampleResult> active = buffer.BuildForGeneration(
                isArdy: false,
                playbackTime: 0.0,
                duration: 5f);
            Assert.That(active.Select(sample => sample.sampleTime), Is.EqualTo(new[] { 1.0, 2.0 }));
        }

        [Test]
        public void RuntimeConstraints_UsesSingleKimodoTerminalAnchor()
        {
            var buffer = new KimodoRuntimeConstraints();
            var terminal = new KimodoConstraintInternalData
            {
                rootPosition = new Vector3(2f, 3f, 4f),
                localJointAxisAngles = new List<Vector3> { Vector3.zero },
                sampleTime = 7.0
            };

            buffer.SetTerminal(terminal);
            terminal.rootPosition = Vector3.zero;

            KimodoConstraintInternalData anchor = buffer.BuildTerminalForGeneration(isArdy: false);
            Assert.That(anchor, Is.Not.Null);
            Assert.That(anchor.sampleTime, Is.Zero);
            Assert.That(anchor.rootPosition, Is.EqualTo(new Vector3(2f, 3f, 4f)));
            Assert.That(buffer.BuildTerminalForGeneration(isArdy: true), Is.Null);
        }

        [Test]
        public void ArdyRequestCompletion_ClearsOnlySentFields()
        {
            var session = new KimodoRuntimeGenerationSession();
            try
            {
                session.ResetArdy(1f);
                session.CompleteArdyRequest(
                    sentPrompt: true,
                    sentConstraints: false,
                    sentSettings: true,
                    stale: false);

                Assert.That(session.ArdyStarted, Is.True);
                Assert.That(session.ArdyPromptDirty, Is.False);
                Assert.That(session.ArdyConstraintsDirty, Is.True);
                Assert.That(session.ArdySettingsDirty, Is.False);
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void RuntimeMotionPlayer_StoresOnlyOneNextSegment()
        {
            var player = new KimodoRuntimeMotionPlayer();
            Assert.That(player.TrySetNextSegment(
                new KimodoRuntimeGeneratedSegment { EffectiveLastFrameTimeSeconds = 2f },
                verboseLogging: false), Is.True);
            Assert.That(player.TrySetNextSegment(
                new KimodoRuntimeGeneratedSegment { EffectiveLastFrameTimeSeconds = 3.5f },
                verboseLogging: false), Is.False);

            Assert.That(player.HasNextSegment, Is.True);
            Assert.That(player.BufferedDurationSeconds, Is.EqualTo(2f));

            player.ClearNextSegment();
            Assert.That(player.HasNextSegment, Is.False);
        }

        [Test]
        public void RuntimeGenerationSession_IgnoresFirstKimodoDurationWhenEstimatingInterruptions()
        {
            var session = new KimodoRuntimeGenerationSession();
            try
            {
                session.RecordKimodoGenerationDuration(8f);
                Assert.That(session.TryGetKimodoGenerationEstimate(out _), Is.False);

                session.RecordKimodoGenerationDuration(2f);
                Assert.That(session.TryGetKimodoGenerationEstimate(out float estimate), Is.True);
                Assert.That(estimate, Is.EqualTo(2f));

                session.RecordKimodoGenerationDuration(4f);
                Assert.That(session.EstimatedKimodoGenerationSeconds, Is.EqualTo(3f));
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void GenerationConstraintProvider_ComposesExternalSamplesOnce()
        {
            KimodoPlayableClip clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                var external = new KimodoExternalConstraintRequest
                {
                    Enabled = true,
                    IncludeTimelineConstraints = false,
                    ConstraintsJson = "{\"raw\":true}"
                };
                var provider = new KimodoEditorConstraintProvider();
                KimodoInOutConstraintResult raw = provider.BuildGenerationConstraintsOrThrow(
                    clip,
                    external,
                    runtimeFrameCount: 30,
                    runtimeLengthSeconds: 1f,
                    frameRate: 30f,
                    disableTimelineInOut: true,
                    deferNormalization: false,
                    enableAutoBeginAnchor: false,
                    sampleTimeOffsetSeconds: 0.25,
                    timelineClip: null);
                Assert.That(raw.ConstraintsJson, Is.EqualTo(external.ConstraintsJson));

                var sample = new KimodoMarkerSampleResult
                {
                    constraintType = "root2d",
                    sampleTime = 0.5,
                };
                external.ConstraintSamples.Add(sample);
                KimodoInOutConstraintResult composed = provider.BuildGenerationConstraintsOrThrow(
                    clip,
                    external,
                    runtimeFrameCount: 30,
                    runtimeLengthSeconds: 1f,
                    frameRate: 30f,
                    disableTimelineInOut: true,
                    deferNormalization: false,
                    enableAutoBeginAnchor: false,
                    sampleTimeOffsetSeconds: 0.25,
                    timelineClip: null);

                Assert.That(composed.CombinedSamples, Has.Count.EqualTo(1));
                Assert.That(composed.CombinedSamples[0], Is.Not.SameAs(sample));
                Assert.That(composed.CombinedSamples[0].sampleTime, Is.EqualTo(0.75));
                Assert.That(composed.ConstraintsJson, Does.Contain("root2d"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void EditorRuntimeRequest_UsesSharedModelAndBridgeOwnership()
        {
            var request = new KimodoEditorGenerateRequest
            {
                TargetFrameCount = 30,
                TargetFrameRate = 30f,
                RuntimeFrameCount = 30
            };

            KimodoGenerationRequestDto generation =
                KimodoEditorGeneratePipeline.CreateRuntimePipelineRequest(
                    request,
                    "walk",
                    " ").GenerationRequest;

            Assert.That(generation.model, Is.EqualTo(KimodoMotionModelProfiles.DefaultModelName));
            Assert.That(generation.force_hf_download, Is.False);
        }

        [Test]
        public void EditorGenerateRequest_OwnsSingleUseOutputLifecycle()
        {
            int createCount = 0;
            int resolveCount = 0;
            var outputPlan = new KimodoEditorGenerateOutputPlan();
            AnimationClip target = null;
            AnimationClip rawBone = null;
            AnimationClip resolvedClip = null;
            var request = new KimodoEditorGenerateRequest(
                () =>
                {
                    createCount++;
                    return new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
                },
                (clip, _) =>
                {
                    resolveCount++;
                    resolvedClip = clip;
                    return outputPlan;
                },
                outputPlan);

            try
            {
                request.CreateTargetClip();
                request.CreateTargetClip();
                target = request.TargetClip;
                rawBone = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
                request.RawBoneClip = rawBone;

                Assert.That(request.ResolveOutputPlan(KimodoMotionModelProfiles.DefaultModelName), Is.SameAs(outputPlan));
                Assert.That(request.ResolveOutputPlan(KimodoMotionModelProfiles.DefaultModelName), Is.SameAs(outputPlan));
                Assert.That(createCount, Is.EqualTo(1));
                Assert.That(resolveCount, Is.EqualTo(1));
                Assert.That(resolvedClip, Is.SameAs(target));

                request.CleanupGeneratedClips();
                Assert.That(target == null, Is.True);
                Assert.That(rawBone == null, Is.True);
                Assert.That(request.TargetClip, Is.Null);
                Assert.That(request.RawBoneClip, Is.Null);
            }
            finally
            {
                if (target != null) UnityEngine.Object.DestroyImmediate(target);
                if (rawBone != null) UnityEngine.Object.DestroyImmediate(rawBone);
            }
        }

        [Test]
        public void RuntimeGenerationSession_ResetAndDisposeOwnsArdyLifecycle()
        {
            var session = new KimodoRuntimeGenerationSession();
            try
            {
                Assert.That(session.TryBeginStart(), Is.True);
                session.Start();
                session.EndStart();
                Assert.That(
                    session.TryBeginGeneration(
                        System.Threading.CancellationToken.None,
                        out System.Threading.CancellationTokenSource generationCts,
                        out _,
                        out _),
                    Is.True);
                System.Threading.CancellationToken lifetimeToken = session.LifetimeToken;
                System.Threading.CancellationToken generationToken = generationCts.Token;
                session.ResetArdy(0f);

                Assert.That(session.ArdyStarted, Is.False);
                Assert.That(session.ArdyPromptDirty, Is.True);
                Assert.That(session.ArdyConstraintsDirty, Is.True);
                Assert.That(session.ArdySettingsDirty, Is.True);
                Assert.That(session.RefreshPending, Is.False);
                Assert.That(session.ArdyPlaybackReserveSeconds, Is.EqualTo(0.2f));

                session.Stop();
                Assert.That(lifetimeToken.IsCancellationRequested, Is.True);
                Assert.That(generationToken.IsCancellationRequested, Is.True);
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
                KimodoRuntimeGenerationSession.ShouldRequestArdyGeneration(
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
        public void TimelineConnectedSelection_AcceptsGapsUnalignedDurationAndParameterDifferences()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip first = CreateArdyTimelineClip(track, 0.0, 0.23, 10);
                TimelineClip second = CreateArdyTimelineClip(track, 1.0, 2.0, 5);
                var secondPlayable = (KimodoPlayableClip)second.asset;
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
            };

            string json = KimodoConstraintJsonExporter.ToConstraintsJson(
                new[] { sample },
                new KimodoConstraintExportContext(),
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
                KimodoMotionModelProfiles.DefaultModelName);

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
                hasRootHeading = false
            };

            JArray constraints = JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { sample },
                    new KimodoConstraintExportContext(),
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
            };

            JArray constraints = JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { sample },
                    new KimodoConstraintExportContext(),
                    clipStartSeconds: 4.0,
                    clipDurationSeconds: 2.0,
                    exportFps: 30.0));

            Assert.That(constraints[0]["frame_indices"]?[0]?.Value<int>(), Is.EqualTo(30));
        }



        [Test]
        public void TimelineConnectedSelection_AcceptsKimodoModel()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip first = CreateArdyTimelineClip(track, 0.0, 1.0, 100);
                TimelineClip second = CreateArdyTimelineClip(track, 1.0, 1.0, 100);
                ((KimodoPlayableClip)first.asset).bridgeModelName = KimodoMotionModelProfiles.DefaultModelName;
                ((KimodoPlayableClip)second.asset).bridgeModelName = KimodoMotionModelProfiles.DefaultModelName;

                Assert.That(
                    KimodoPlayableClipGenerationExecutionService.TryValidateConnectedSelection(
                        new[] { first, second },
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
        public void FootIk_RequiresAnExplicitTargetBeforeSolvingTheLeg()
        {
            Assert.That(KimodoRuntimeHumanoidRetargeter.ShouldSolveFootIk(false, null), Is.False);
            Assert.That(KimodoRuntimeHumanoidRetargeter.ShouldSolveFootIk(true, null), Is.False);
        }

        [Test]
        public void ConstraintJson_EndEffectorOmitsManualTargetPositionPendingIk()
        {
            Quaternion rootRotation = Quaternion.Euler(0f, 90f, 0f);
            var targeted = new KimodoMarkerSampleResult
            {
                constraintType = "left-hand",
                sampleTime = 1.0,
                {
                    KimodoRuntimeUtility.QuaternionToAxisAngleVector(rootRotation)
                }
            };
            JArray constraints = JArray.Parse(
                KimodoConstraintJsonExporter.ToConstraintsJson(
                    new[] { targeted },
                    new KimodoConstraintExportContext(),
                    clipDurationSeconds: 4.0,
                    exportFps: 30.0));

            JToken positions = constraints[0]["target_positions"];
            Assert.That(positions, Is.Null);
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
                KimodoRuntimeHumanoidRetargeter.SolveTwoBoneLeg(
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
                KimodoRuntimeHumanoidRetargeter.SolveTwoBoneLeg(
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
            GameObject directorRoot = new GameObject("KimodoTimelineRequestLengthTest");
            RetargetSkeleton skeleton = null;
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.duration = 12.0;
                PlayableDirector director = directorRoot.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                Assert.That(
                    KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                        KimodoMotionModelProfiles.DefaultModelName,
                        out Avatar avatar,
                        out string error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        avatar,
                        "KimodoTimelineRequestLengthSkeleton",
                        out skeleton,
                        out error),
                    Is.True,
                    error);
                director.SetGenericBinding(track, skeleton.animator);
                var playable = (KimodoPlayableClip)timelineClip.asset;
                playable.bridgeModelName = KimodoMotionModelProfiles.DefaultModelName;
                playable.inOutConstraintMode = KimodoInOutConstraintMode.None;
                playable.autoBeginAnchor = false;

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
                skeleton?.Dispose();
                UnityEngine.Object.DestroyImmediate(directorRoot);
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
                    KimodoMotionModelProfiles.DefaultModelName,
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
                    KimodoTimelineGenerationOutputPlanner.Capture(
                        playable,
                        avatar,
                        KimodoMotionModelProfiles.DefaultModelName,
                        bindingObject: null);
                UnityEngine.Object.DestroyImmediate(playable);
                playable = null;

                KimodoEditorGenerateOutputPlan resolved =
                    KimodoTimelineGenerationOutputPlanner.Resolve(
                        snapshot,
                        bindingObject: null,
                        generated,
                        KimodoMotionModelProfiles.DefaultModelName);

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
            playable.randomSeed = false;
            playable.seed = 42;
            return timelineClip;
        }
    }
}

#endif
