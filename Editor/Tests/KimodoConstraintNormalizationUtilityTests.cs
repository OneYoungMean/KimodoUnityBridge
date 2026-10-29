using System.Collections.Generic;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;
using UnityEngine.Playables;
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
                    BeginWindowModelFrameCount = 7, EndWindowModelFrameCount = 7, BeginSampleCount = 4, EndSampleCount = 4,
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
                playable.inConstraintWindowFrames60 = 100;
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
        public void DeferredAutoBegin_GenericEffectorWithinOneSecondBeatsSyntheticConstraint()
        {
            var synthetic = new KimodoMarkerSampleResult { constraintMode = "root2d", sampleTime = 0.0 };
            var effector = new KimodoMarkerSampleResult { constraintMode = "effector", sampleTime = 0.999 };

            Assert.That(
                KimodoConstraintNormalizationUtility.HasNormalizationAnchor(
                    new List<KimodoMarkerSampleResult> { synthetic, effector },
                    1.0,
                    synthetic),
                Is.True);
        }

        [Test]
        public void ConstraintAtExactlyOneSecond_GetsFrameZeroAutoBeginRoot2DConstraint()
        {
            using (AutoBeginSceneFixture fixture = CreateAutoBeginSceneFixture())
            {
                Transform sceneHips = fixture.Skeleton.animator.GetBoneTransform(HumanBodyBones.Hips);
                Assert.That(sceneHips, Is.Not.Null);
                Vector3 expectedRootPosition = sceneHips.position;
                Quaternion expectedRootRotation = sceneHips.rotation;
                var sample = new KimodoMarkerSampleResult
                {
                    sampleValid = true,
                    constraintMode = "root2d",
                    sampleTime = 1.0,
                    rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = expectedRootPosition,
                        q = expectedRootRotation
                    },
                    enableMask = KimodoConstraintMask.ForType("root2d")
                };

                Assert.That(
                    KimodoInOutConstraintComposer.TryBuild(
                        CreateAutoBeginRequest(fixture, sample),
                        out KimodoInOutConstraintResult result,
                        out _,
                        out _),
                    Is.True);

                Assert.That(result.CombinedSamples, Has.Count.EqualTo(2));
                Assert.That(result.CombinedSamples[0].constraintMode, Is.EqualTo("root2d"));
                Assert.That(result.CombinedSamples[0].sampleTime, Is.EqualTo(0.0).Within(1e-6));
                Assert.That(result.CombinedSamples[0].sampleData.IsValid, Is.True);
                Assert.That(
                    Vector3.Distance(result.CombinedSamples[0].rootOverride.t, expectedRootPosition),
                    Is.LessThan(0.01f),
                    "AutoBegin must use the original scene Hips position.");
                Assert.That(
                    Quaternion.Angle(result.CombinedSamples[0].rootOverride.q, expectedRootRotation),
                    Is.LessThan(0.01f),
                    "AutoBegin must use the original scene Hips heading.");
                Assert.That(result.ConstraintsJson, Does.Contain("\"type\": \"root2d\""));
                Assert.That(result.ConstraintsJson, Does.Not.Contain("\"type\": \"fullbody\""));
                Assert.That(result.HasSyntheticAutoBeginConstraint, Is.True);
            }
        }

        [Test]
        public void DeferredBeginStillGetsSyntheticAutoBeginAnchor()
        {
            using (AutoBeginSceneFixture fixture = CreateAutoBeginSceneFixture())
            {
                Transform sceneHips = fixture.Skeleton.animator.GetBoneTransform(HumanBodyBones.Hips);
                KimodoInOutConstraintRequest request = CreateAutoBeginRequest(
                    fixture,
                    new KimodoMarkerSampleResult
                    {
                        sampleValid = true,
                        constraintMode = "root2d",
                        sampleTime = 1.0,
                        rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                        {
                            t = sceneHips.position,
                            q = sceneHips.rotation
                        },
                        enableMask = KimodoConstraintMask.ForType("root2d")
                    });
                request.DeferBeginConstraint = true;

                Assert.That(
                    KimodoInOutConstraintComposer.TryBuild(
                        request,
                        out KimodoInOutConstraintResult result,
                        out _,
                        out string error),
                    Is.True,
                    error);
                Assert.That(result.HasSyntheticAutoBeginConstraint, Is.True);
                Assert.That(result.CombinedSamples[0].constraintMode, Is.EqualTo("root2d"));
                Assert.That(result.CombinedSamples[0].sampleTime, Is.EqualTo(0.0).Within(1e-6));
            }
        }

        [Test]
        public void AutoBegin_CachesSceneHipsSnapshotForSubsequentBuilds()
        {
            using (AutoBeginSceneFixture fixture = CreateAutoBeginSceneFixture())
            {
                TimelineClip sourceClip = fixture.Track.CreateClip<KimodoPlayableClip>();
                Transform sceneHips = fixture.Skeleton.animator.GetBoneTransform(HumanBodyBones.Hips);
                Vector3 originalPosition = sceneHips.position;
                Quaternion originalRotation = sceneHips.rotation;

                Assert.That(
                    KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                        sourceClip, out var context, out var contextError),
                    Is.True,
                    contextError);
                Assert.That(context.HasOriginalRootSnapshot, Is.False);

                var request = new KimodoInOutConstraintRequest
                {
                    Mode = KimodoInOutConstraintMode.None,
                    AutoBeginAnchor = true,
                    GenerationFrames = 90,
                    TimelineContext = context
                };
                Assert.That(
                    KimodoInOutConstraintComposer.TryBuild(
                        request, out var initialResult, out _, out var initialError),
                    Is.True,
                    initialError);
                Assert.That(initialResult.HasSyntheticAutoBeginConstraint, Is.True);
                Assert.That(context.HasOriginalRootSnapshot, Is.True);

                // Simulate a Timeline preview changing the bound pose after
                // AutoBegin captured the scene's original Root2D anchor.
                sceneHips.SetPositionAndRotation(
                    originalPosition + new Vector3(3f, 0f, -2f),
                    Quaternion.Euler(0f, 70f, 0f));

                Assert.That(
                    KimodoInOutConstraintComposer.TryBuild(
                        request, out var result, out _, out var error),
                    Is.True,
                    error);
                Assert.That(result.CombinedSamples, Has.Count.EqualTo(1));
                Assert.That(result.CombinedSamples[0].constraintMode, Is.EqualTo("root2d"));
                Assert.That(
                    Vector3.Distance(result.CombinedSamples[0].rootOverride.t, originalPosition),
                    Is.LessThan(0.01f));
                Assert.That(
                    Quaternion.Angle(result.CombinedSamples[0].rootOverride.q, originalRotation),
                    Is.LessThan(0.01f));
            }
        }

        [Test]
        public void AutoBegin_IsSkippedWhenItWouldExceedRootMotionLimits()
        {
            using (AutoBeginSceneFixture fixture = CreateAutoBeginSceneFixture())
            {
                var fastBoundary = new KimodoMarkerSampleResult
                {
                    sampleValid = true,
                    constraintMode = "root2d",
                    sampleTime = 1.1,
                    rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = new Vector3(100f, 0.8879f, 0f),
                        q = Quaternion.identity
                    },
                    enableMask = KimodoConstraintMask.ForType("root2d")
                };

                Assert.That(KimodoInOutConstraintComposer.TryBuild(
                    CreateAutoBeginRequest(fixture, fastBoundary),
                    out var result, out _, out var error), Is.True, error);
                Assert.That(result.HasSyntheticAutoBeginConstraint, Is.False);
                Assert.That(result.CombinedSamples, Has.Count.EqualTo(1));
                Assert.That(result.CombinedSamples[0].sampleTime, Is.EqualTo(1.1));
            }
        }

        [Test]
        public void ConstraintComposer_RejectsBoundaryMotionAboveSpeedLimit()
        {
            using (AutoBeginSceneFixture fixture = CreateAutoBeginSceneFixture())
            {
                var request = CreateAutoBeginRequest(fixture,
                    new KimodoMarkerSampleResult
                    {
                        sampleValid = true, sampleTime = 0.0,
                        constraintMode = "root2d",
                        enableMask = KimodoConstraintMask.ForType("root2d"),
                        rootOverride = new KimodoUnityBridge.KimodoRigidTransform { t = Vector3.zero }
                    },
                    new KimodoMarkerSampleResult
                    {
                        sampleValid = true, sampleTime = 0.5,
                        constraintMode = "root2d",
                        enableMask = KimodoConstraintMask.ForType("root2d"),
                        rootOverride = new KimodoUnityBridge.KimodoRigidTransform { t = new Vector3(5f, 0f, 0f) }
                    });
                request.AutoBeginAnchor = false;

                Assert.That(KimodoInOutConstraintComposer.TryBuild(request,
                    out _, out _, out string error), Is.False);
                StringAssert.Contains("Boundary constraints", error);
                StringAssert.Contains("7.0 m/s", error);
            }
        }

        [Test]
        public void BoundaryConstraints_AreRejectedWhenTheyExceedAccelerationLimit()
        {
            using (AutoBeginSceneFixture fixture = CreateAutoBeginSceneFixture())
            {
                var request = CreateAutoBeginRequest(fixture,
                    CreateRoot2DSample(0.0, Vector3.zero),
                    CreateRoot2DSample(0.5, Vector3.zero),
                    CreateRoot2DSample(1.0, new Vector3(3.5f, 0f, 0f)));

                Assert.That(KimodoInOutConstraintComposer.TryBuild(request,
                    out _, out _, out string error), Is.False);
                StringAssert.Contains("10.0 m/s²", error);
            }
        }

        private static KimodoMarkerSampleResult CreateRoot2DSample(double time, Vector3 position)
        {
            return new KimodoMarkerSampleResult
            {
                sampleValid = true,
                sampleTime = time,
                constraintMode = "root2d",
                enableMask = KimodoConstraintMask.ForType("root2d"),
                rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                {
                    t = position,
                    q = Quaternion.identity
                }
            };
        }

        [TestCase(KimodoConstraintMode.FullBody)]
        [TestCase(KimodoConstraintMode.Root2D)]
        [TestCase(KimodoConstraintMode.Effector)]
        public void ConstraintInitialization_UsesTimelinePoseForPreviewAndExport(KimodoConstraintMode mode)
        {
            using (AutoBeginSceneFixture fixture = CreateAutoBeginSceneFixture())
            {
                var context = CreateAutoBeginRequest(fixture).TimelineContext;
                var marker = fixture.Track.CreateMarker<KimodoConstraintMarker>(100.0);
                marker.ConstraintMode = mode;
                if (mode == KimodoConstraintMode.Effector) marker.SampleData.enableMask.rightHand = true;
                fixture.Skeleton.animator.GetBoneTransform(HumanBodyBones.RightUpperArm).localRotation *= Quaternion.Euler(0, 0, 45);
                fixture.Director.time = 50.0;

                Assert.That(KimodoTimelineConstraintSampler.TrySampleMarker(
                    context, marker.time, marker.time, "fullbody",
                    KimodoMotionModelProfiles.DefaultModelName,
                    out var expected, out var error), Is.True, error);

                Assert.That(KimodoConstraintMarkerSampling.TryUpdateAutoSampleMarkerData(marker, out error), Is.True, error);
                Assert.That(Newtonsoft.Json.Linq.JToken.DeepEquals(
                    KimodoConstraintPoseUtility.BuildPoseJson(marker.SampleData),
                    KimodoConstraintPoseUtility.BuildPoseJson(expected)), Is.True,
                    "Auto Sample should capture the Timeline pose at the marker time.");

                Assert.That(KimodoTimelineConstraintMarkerSampler.TryBuildMarkerSamplesForExport(
                    context, out var exported, out error), Is.True, error);
                Assert.That(exported, Has.Count.EqualTo(1));
                Assert.That(exported[0].constraintMode, Is.EqualTo(marker.SampleData.constraintMode));
                Assert.That(exported[0].sampleTime, Is.EqualTo(100.0));
                Assert.That(Newtonsoft.Json.Linq.JToken.DeepEquals(
                    KimodoConstraintPoseUtility.BuildPoseJson(exported[0]),
                    KimodoConstraintPoseUtility.BuildPoseJson(expected)), Is.True);
                Assert.That(fixture.Director.time, Is.EqualTo(50.0), "Sampling must not leave Timeline time changed.");

                marker.autoSample = false;
                marker.SampleData.rootOverride.t += Vector3.up;
                marker.SampleData.effectors.rightHand.t += Vector3.forward;
                Assert.That(KimodoTimelineConstraintMarkerSampler.TryBuildMarkerSamplesForExport(
                    context, out exported, out error), Is.True, error);
                Assert.That(exported[0].rootOverride.t, Is.EqualTo(marker.SampleData.rootOverride.t));
                Assert.That(exported[0].effectors.rightHand.t, Is.EqualTo(marker.SampleData.effectors.rightHand.t));
            }
        }

        [TestCase("fullbody")]
        [TestCase("root2d")]
        public void RealAnchorInsideFirstSecond_PreventsAutoBeginConstraint(string constraintType)
        {
            using (AutoBeginSceneFixture fixture = CreateAutoBeginSceneFixture())
            {
                var realPose = new MuscleSample();
                realPose.SetRoot(Vector3.zero, Quaternion.identity);
                var realAnchor = new KimodoMarkerSampleResult
                {
                    sampleData = realPose,
                    rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = Vector3.zero,
                        q = Quaternion.identity
                    },
                    enableMask = KimodoConstraintMask.ForType(constraintType),
                    constraintMode = constraintType,
                    sampleValid = true,
                    sampleTime = 0.75
                };

                Assert.That(
                    KimodoInOutConstraintComposer.TryBuild(
                        CreateAutoBeginRequest(fixture, realAnchor),
                        out KimodoInOutConstraintResult result,
                        out _,
                        out _),
                    Is.True);

                Assert.That(result.CombinedSamples, Has.Count.EqualTo(1));
                Assert.That(result.HasSyntheticAutoBeginConstraint, Is.False);
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

        [Test]
        public void TrackOffset_UsesEffectiveTrackOrSceneOffset()
        {
            AnimationTrack track = CreateAutoBeginTrack(
                new Vector3(4f, 0f, 5f),
                Quaternion.Euler(0f, 35f, 0f));
            var animatorObject = new GameObject("TrackOffsetAnimator");
            Animator animator = animatorObject.AddComponent<Animator>();
            animator.transform.position = new Vector3(9f, 0f, -3f);
            animator.transform.rotation = Quaternion.Euler(0f, -20f, 0f);
            try
            {
                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    track, animator, out Vector3 transformPosition, out Quaternion transformRotation);
                Assert.That(transformPosition, Is.EqualTo(new Vector3(4f, 0f, 5f)));
                Assert.That(Quaternion.Angle(transformRotation, Quaternion.Euler(0f, 35f, 0f)), Is.LessThan(0.001f));

                track.trackOffset = TrackOffset.ApplySceneOffsets;
                animator.transform.position = new Vector3(2f, 0f, 7f);
                animator.transform.rotation = Quaternion.Euler(0f, -20f, 0f);
                KimodoTimelinePreviewRefreshUtility.InitializeSceneOffset(track, animator);
                animator.transform.position = new Vector3(9f, 0f, -3f);
                animator.transform.rotation = Quaternion.Euler(0f, 15f, 0f);
                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    track, animator, out Vector3 scenePosition, out Quaternion sceneRotation);
                Assert.That(scenePosition, Is.EqualTo(new Vector3(2f, 0f, 7f)));
                Assert.That(
                    Quaternion.Angle(sceneRotation, Quaternion.Euler(0f, -20f, 0f)),
                    Is.LessThan(0.001f));
            }
            finally
            {
                Object.DestroyImmediate(animatorObject);
                Object.DestroyImmediate(track);
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
            AutoBeginSceneFixture fixture,
            params KimodoMarkerSampleResult[] samples)
        {
            return new KimodoInOutConstraintRequest
            {
                Mode = KimodoInOutConstraintMode.None,
                AutoBeginAnchor = true,
                GenerationFrames = 90,
                TimelineContext = new KimodoTimelineInOutConstraintContext
                {
                    Director = fixture.Director,
                    Track = fixture.Track,
                    Animator = fixture.Skeleton.animator,
                    ModelName = KimodoMotionModelProfiles.DefaultModelName
                },
                ManualSamples = samples != null
                    ? new List<KimodoMarkerSampleResult>(samples)
                    : new List<KimodoMarkerSampleResult>()
            };
        }

        private static AutoBeginSceneFixture CreateAutoBeginSceneFixture()
        {
            var fixture = new AutoBeginSceneFixture
            {
                Timeline = ScriptableObject.CreateInstance<TimelineAsset>(),
                DirectorObject = new GameObject("AutoBeginScenePoseDirector")
            };
            fixture.Director = fixture.DirectorObject.AddComponent<PlayableDirector>();
            fixture.Director.playableAsset = fixture.Timeline;
            fixture.Track = fixture.Timeline.CreateTrack<AnimationTrack>();
            fixture.Track.trackOffset = TrackOffset.ApplyTransformOffsets;
            fixture.Track.position = new Vector3(4f, 0f, 5f);
            fixture.Track.rotation = Quaternion.Euler(0f, 35f, 0f);

            fixture.SceneRoot = new GameObject("AutoBeginSceneCharacterRoot");
            fixture.SceneRoot.transform.SetPositionAndRotation(
                new Vector3(9f, 0f, -3f),
                Quaternion.Euler(0f, -20f, 0f));

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
                    "AutoBeginScenePoseCharacter",
                    out fixture.Skeleton,
                    out error),
                Is.True,
                error);
            fixture.Skeleton.root.transform.SetParent(fixture.SceneRoot.transform, false);
            fixture.Skeleton.root.transform.localPosition = new Vector3(2f, 0f, -1f);
            fixture.Skeleton.root.transform.localRotation = Quaternion.Euler(0f, 15f, 0f);
            fixture.Director.SetGenericBinding(fixture.Track, fixture.Skeleton.animator);
            return fixture;
        }

        private sealed class AutoBeginSceneFixture : System.IDisposable
        {
            public TimelineAsset Timeline;
            public GameObject DirectorObject;
            public GameObject SceneRoot;
            public PlayableDirector Director;
            public AnimationTrack Track;
            public RetargetSkeleton Skeleton;

            public void Dispose()
            {
                Director?.Stop();
                Skeleton?.Dispose();
                if (SceneRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(SceneRoot);
                }
                if (DirectorObject != null)
                {
                    UnityEngine.Object.DestroyImmediate(DirectorObject);
                }
                if (Timeline != null)
                {
                    UnityEngine.Object.DestroyImmediate(Timeline);
                }
            }
        }
    }
}
