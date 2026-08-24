#if false
using System;
using System.Reflection;
using NUnit.Framework;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoTimelineSamplingSessionTests
    {
        [Test]
        public void ClipConstraintTimelineTimes_OutsideBoundaryOverridesCurrentClipBoundary()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip previous = track.CreateClip<KimodoPlayableClip>();
                previous.start = 0.0;
                previous.duration = 1.0;
                TimelineClip current = track.CreateClip<KimodoPlayableClip>();
                current.start = 1.0;
                current.duration = 2.0;
                TimelineClip next = track.CreateClip<KimodoPlayableClip>();
                next.start = 3.0;
                next.duration = 1.0;
                var context = new KimodoTimelineInOutConstraintContext
                {
                    SourceClip = current,
                    Track = track,
                    PreviousTimelineClip = previous,
                    NextTimelineClip = next
                };

                double[] times = KimodoClipConstraintEncoder.BuildTimelineSampleTimes(
                    context,
                    frameCount: 5,
                    frameRate: 20f,
                    runtimeTrimStartFrame: 1,
                    inOutMode: KimodoInOutConstraintMode.Outside,
                    enableInConstraint: true,
                    enableOutConstraint: true);
                var boundaryRequest = new KimodoInOutConstraintRequest
                {
                    Mode = KimodoInOutConstraintMode.Outside,
                    TimelineContext = context
                };

                Assert.That(times[0], Is.EqualTo(0.95).Within(1e-9));
                Assert.That(times[1], Is.EqualTo(
                    KimodoInOutConstraintTools.ResolveTimelineBoundaryTime(boundaryRequest, isBegin: true)).Within(1e-9));
                Assert.That(times[2], Is.EqualTo(1.05).Within(1e-9));
                Assert.That(times[4], Is.EqualTo(
                    KimodoInOutConstraintTools.ResolveTimelineBoundaryTime(boundaryRequest, isBegin: false)).Within(1e-9));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ConstraintDragMuscleDiagnostics_ReportsValuesAndLargestDifference()
        {
            float[] left = { 0f, 0.25f, -0.1f };
            float[] right = { 0f, -0.5f, -0.05f };

            Assert.That(
                KimodoConstraintPoseDiagnostics.BuildMuscleValues(left),
                Is.EqualTo("[0.00000,0.25000,-0.10000]"));

            string diff = KimodoConstraintPoseDiagnostics.BuildMuscleDiff(left, right);
            Assert.That(diff, Does.Contain("changed=2"));
            Assert.That(diff, Does.Contain("absMax=0.750000"));
            Assert.That(diff, Does.Contain("maxIndex=1"));
            Assert.That(diff, Does.Contain($"maxName='{HumanTrait.MuscleName[1]}'"));
        }
        [Test]
        public void ConstraintPreviewClone_WithResolvedAvatarAndNullBindingAvatar_AppliesAndKeepsSampledPose()
        {
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
                    "KimodoConstraintAvatarlessBindingTest",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            RetargetSkeleton expectedTarget = null;
            try
            {
                Assert.That(
                    KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        KimodoMotionModelProfiles.DefaultModelName,
                        source,
                        out string[] jointNames,
                        out int[] parentIndices,
                        out Transform[] joints,
                        out error),
                    Is.True,
                    error);
                Transform sourceHips = source.animator.GetBoneTransform(HumanBodyBones.Hips);
                Transform sourceHand = source.animator.GetBoneTransform(HumanBodyBones.LeftHand);
                Transform sourceFoot = source.animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                Assert.That(sourceHips, Is.Not.Null);
                Assert.That(sourceHand, Is.Not.Null);
                Assert.That(sourceFoot, Is.Not.Null);

                source.skeletonRoot.SetPositionAndRotation(
                    new Vector3(1.25f, 0.2f, -0.75f),
                    Quaternion.Euler(0f, 32f, 0f));
                sourceHips.localRotation *= Quaternion.Euler(7f, 18f, -4f);
                sourceHand.localRotation *= Quaternion.Euler(22f, -13f, 31f);
                sourceFoot.localRotation *= Quaternion.Euler(-16f, 9f, 6f);
                Assert.That(
                    KimodoMarkerSamplingUtility.TrySampleMarkerFromProfileSkeletonRaw(
                        source.animator,
                        source.skeletonRoot,
                        KimodoMotionModelProfiles.DefaultModelName,
                        0.0,
                        "fullbody",
                        jointNames,
                        parentIndices,
                        joints,
                        out KimodoMarkerSampleResult sample,
                        out error),
                    Is.True,
                    error);

                KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(source);
                Assert.That(
                    KimodoRetargetAvatarUtility.TryApplyMarkerSampleToTransformMap(
                        sample,
                        KimodoMotionModelProfiles.DefaultModelName,
                        source.skeletonRoot,
                        source.uniqueNameMap,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        source,
                        out MuscleSample profileSample,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        avatar,
                        "KimodoConstraintAvatarlessExpectedTarget",
                        out expectedTarget,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        profileSample,
                        KimodoMotionModelProfiles.DefaultFrameRate,
                        expectedTarget,
                        out BoneSample expectedBoneSample,
                        out _,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(
                        expectedBoneSample,
                        expectedTarget,
                        out error),
                    Is.True,
                    error);
                Transform expectedHips = expectedTarget.animator.GetBoneTransform(HumanBodyBones.Hips);
                Transform expectedHand = expectedTarget.animator.GetBoneTransform(HumanBodyBones.LeftHand);
                Transform expectedFoot = expectedTarget.animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                Assert.That(expectedHips, Is.Not.Null);
                Assert.That(expectedHand, Is.Not.Null);
                Assert.That(expectedFoot, Is.Not.Null);
                Vector3[] expectedPositions =
                {
                    expectedHips.position,
                    expectedHand.position,
                    expectedFoot.position
                };
                Quaternion[] expectedRotations =
                {
                    expectedHips.rotation,
                    expectedHand.rotation,
                    expectedFoot.rotation
                };
                source.animator.avatar = null;
                var context = new PoseCacheRenderContext(
                    1,
                    KimodoUnityObjectIdUtility.IdHash(source.animator),
                    1,
                    KimodoMotionModelProfiles.DefaultModelName,
                    KimodoConstraintRigType.Soma77,
                    avatar);

                bool callbackInvoked = false;
                Avatar previewAvatar = null;
                bool previewAnimatorEnabled = true;
                bool previewBonesResolved = false;
                float[] expectedPositionErrors = null;
                float[] expectedRotationErrors = null;
                float[] retainedPositionErrors = null;
                float[] retainedRotationErrors = null;

                Assert.That(
                    KimodoConstraintPoseCache.TryResolveTargetHipsPose(
                        context,
                        sample,
                        out Vector3 rebuiltHipsPosition,
                        out Quaternion rebuiltHipsRotation,
                        out error,
                        (previewAnimator, _) =>
                        {
                            callbackInvoked = true;
                            previewAvatar = previewAnimator.avatar;
                            previewAnimatorEnabled = previewAnimator.enabled;
                            Transform[] previewBones =
                            {
                                previewAnimator.GetBoneTransform(HumanBodyBones.Hips),
                                previewAnimator.GetBoneTransform(HumanBodyBones.LeftHand),
                                previewAnimator.GetBoneTransform(HumanBodyBones.LeftFoot)
                            };
                            previewBonesResolved = Array.TrueForAll(previewBones, bone => bone != null);
                            if (!previewBonesResolved)
                            {
                                return;
                            }

                            expectedPositionErrors = new float[previewBones.Length];
                            expectedRotationErrors = new float[previewBones.Length];
                            var positionsBeforeUpdate = new Vector3[previewBones.Length];
                            var rotationsBeforeUpdate = new Quaternion[previewBones.Length];
                            for (int i = 0; i < previewBones.Length; i++)
                            {
                                positionsBeforeUpdate[i] = previewBones[i].position;
                                rotationsBeforeUpdate[i] = previewBones[i].rotation;
                                expectedPositionErrors[i] = Vector3.Distance(previewBones[i].position, expectedPositions[i]);
                                expectedRotationErrors[i] = Quaternion.Angle(previewBones[i].rotation, expectedRotations[i]);
                            }

                            previewAnimator.Update(0f);
                            retainedPositionErrors = new float[previewBones.Length];
                            retainedRotationErrors = new float[previewBones.Length];
                            for (int i = 0; i < previewBones.Length; i++)
                            {
                                retainedPositionErrors[i] = Vector3.Distance(previewBones[i].position, positionsBeforeUpdate[i]);
                                retainedRotationErrors[i] = Quaternion.Angle(previewBones[i].rotation, rotationsBeforeUpdate[i]);
                            }
                        }),
                    Is.True,
                    error);
                Assert.That(callbackInvoked, Is.True);
                Assert.That(previewAvatar, Is.SameAs(avatar));
                Assert.That(previewAnimatorEnabled, Is.False);
                Assert.That(previewBonesResolved, Is.True);
                Assert.That(Vector3.Distance(rebuiltHipsPosition, expectedPositions[0]), Is.LessThan(1e-3f));
                Assert.That(Quaternion.Angle(rebuiltHipsRotation, expectedRotations[0]), Is.LessThan(0.1f));
                for (int i = 0; i < expectedPositions.Length; i++)
                {
                    Assert.That(expectedPositionErrors[i], Is.LessThan(1e-3f), $"preview bone {i} position");
                    Assert.That(expectedRotationErrors[i], Is.LessThan(0.1f), $"preview bone {i} rotation");
                    Assert.That(retainedPositionErrors[i], Is.LessThan(1e-5f), $"preview bone {i} retained position");
                    Assert.That(retainedRotationErrors[i], Is.LessThan(1e-4f), $"preview bone {i} retained rotation");
                }
                Assert.That(source.animator.avatar, Is.Null);
            }
            finally
            {
                expectedTarget?.Dispose();
                source.Dispose();
            }
        }

        [Test]
        public void ResolveTimelineSourceAvatar_UsesFirstTrackClipCustomAvatarWhenBindingAnimatorAvatarIsNull()
        {
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
                    "KimodoConstraintCustomAvatarTest",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                source.animator.avatar = null;
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>();
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                ((KimodoPlayableClip)timelineClip.asset).CustomRetargetAvatar = avatar;

                KimodoLocalAvatarUtility.AvatarResolveResult result =
                    KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(track, source.animator);

                Assert.That(result.Avatar, Is.SameAs(avatar));
                Assert.That(result.IsHumanoid, Is.True);
                Assert.That(result.Source, Is.EqualTo("TrackFirstClip"));
                Assert.That(result.Error, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
                source.Dispose();
            }
        }

        [Test]
        public void ResolveTimelineSourceAvatar_IgnoresLaterClipCustomAvatar()
        {
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
                    "KimodoTrackFirstAvatarTest",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>();
                TimelineClip first = track.CreateClip<AnimationPlayableAsset>();
                first.start = 0.0;
                TimelineClip later = track.CreateClip<KimodoPlayableClip>();
                later.start = 1.0;
                ((KimodoPlayableClip)later.asset).CustomRetargetAvatar = avatar;

                KimodoLocalAvatarUtility.AvatarResolveResult result =
                    KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(track, source.animator);

                Assert.That(result.Avatar, Is.SameAs(avatar));
                Assert.That(result.IsHumanoid, Is.True);
                Assert.That(result.Source, Is.EqualTo("Animator"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
                source.Dispose();
            }
        }

        [Test]
        public void TransientBonePoseClip_PreservesRootTransformCurves()
        {
            var first = new BoneSample
            {
                boneNames = new[] { string.Empty },
                localPositions = new[] { new Vector3(1f, 2f, 3f) },
                localRotations = new[] { Quaternion.Euler(0f, 10f, 0f) }
            };
            var second = new BoneSample
            {
                boneNames = first.boneNames,
                localPositions = new[] { new Vector3(4f, 5f, 6f) },
                localRotations = new[] { Quaternion.Euler(0f, 40f, 0f) }
            };
            AnimationClip clip = null;
            try
            {
                Assert.That(
                    KimodoRetargetSamplingUtility.TryCreateTransientBoneClip(
                        new[] { first, second },
                        30f,
                        out clip,
                        out string error),
                    Is.True,
                    error);

                AnimationCurve x = AnimationUtility.GetEditorCurve(
                    clip,
                    EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalPosition.x"));
                AnimationCurve qy = AnimationUtility.GetEditorCurve(
                    clip,
                    EditorCurveBinding.FloatCurve(string.Empty, typeof(Transform), "m_LocalRotation.y"));
                AnimationCurve motionTx = AnimationUtility.GetEditorCurve(
                    clip,
                    EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "MotionT.x"));
                AnimationCurve motionQy = AnimationUtility.GetEditorCurve(
                    clip,
                    EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "MotionQ.y"));
                Assert.That(x, Is.Not.Null);
                Assert.That(qy, Is.Not.Null);
                Assert.That(motionTx, Is.Null);
                Assert.That(motionQy, Is.Null);
                Assert.That(x.Evaluate(0f), Is.EqualTo(first.localPositions[0].x).Within(1e-5f));
                Assert.That(x.Evaluate(1f / 30f), Is.EqualTo(second.localPositions[0].x).Within(1e-5f));
                Assert.That(qy.Evaluate(0f), Is.EqualTo(first.localRotations[0].y).Within(1e-5f));
                Assert.That(qy.Evaluate(1f / 30f), Is.EqualTo(second.localRotations[0].y).Within(1e-5f));
            }
            finally
            {
                if (clip != null)
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }

        [Test]
        public void BatchRetargetMuscleSamples_PreservesSampleOrder()
        {
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
                    "KimodoBatchRetargetTest",
                    out RetargetSkeleton cache,
                    out error),
                Is.True,
                error);

            try
            {
                var firstPose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref firstPose);
                var secondPose = new HumanPose
                {
                    bodyPosition = firstPose.bodyPosition + new Vector3(0.2f, 0f, 0.1f) / cache.humanScale,
                    bodyRotation = Quaternion.Euler(0f, 25f, 0f) * firstPose.bodyRotation,
                    muscles = (float[])firstPose.muscles.Clone()
                };
                int[] changedMuscles = { 0, 1, 2, 21, 22, 23, 37, 38 };
                for (int i = 0; i < changedMuscles.Length; i++)
                {
                    int muscle = changedMuscles[i];
                    if (muscle >= 0 && muscle < secondPose.muscles.Length)
                    {
                        secondPose.muscles[muscle] = Mathf.Clamp(firstPose.muscles[muscle] + 0.35f, -1f, 1f);
                    }
                }
                MuscleSample[] sourceSamples =
                {
                    KimodoRetargetHumanoidPoseUtility.BuildMuscleSampleFromPose(cache, firstPose),
                    KimodoRetargetHumanoidPoseUtility.BuildMuscleSampleFromPose(cache, secondPose)
                };
                int writebackCount = 0;
                string writebackLabel = string.Empty;

                Assert.That(
                    KimodoRetargetSamplingUtility.TryRetargetMuscleSamplesToBoneSamples(
                        sourceSamples,
                        30f,
                        cache,
                        out BoneSample[] samples,
                        out error,
                        (clip, label) =>
                        {
                            writebackCount++;
                            writebackLabel = label;
                            return clip != null ? string.Empty : "clip is null";
                        }),
                    Is.True,
                    error);
                Assert.That(writebackCount, Is.EqualTo(1));
                Assert.That(writebackLabel, Is.EqualTo("MuscleClip"));
                Assert.That(samples, Has.Length.EqualTo(2));
                Assert.That(samples[0].IsValid, Is.True);
                Assert.That(samples[1].IsValid, Is.True);
                Assert.That(
                    Vector3.Distance(samples[0].localPositions[0], samples[1].localPositions[0]),
                    Is.GreaterThan(0.05f));
                bool nonRootBoneChanged = false;
                for (int i = 1; i < samples[0].localRotations.Length; i++)
                {
                    if (Quaternion.Angle(samples[0].localRotations[i], samples[1].localRotations[i]) > 0.1f)
                    {
                        nonRootBoneChanged = true;
                        break;
                    }
                }
                Assert.That(nonRootBoneChanged, Is.True, "Retargeted bone clip must not collapse to root-only motion.");
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        [Category("ArdyGuardValidation")]
        public void TimelinePoseSampler_WithNullBindingAvatar_SamplesChangingBoneClipSpineMuscle()
        {
            const bool preserveSampledPoseForInspection = true;
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
                    "KimodoTimelineAvatarlessSource",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var boneClip = new AnimationClip { frameRate = 30f };
            AnimationClip ardyHistoryClip = null;
            var directorRoot = new GameObject("KimodoTimelineAvatarlessDirector");
            KimodoTimelineSamplingSession sampler = null;
            try
            {
                const int muscleIndex = 0;
                Transform spine = source.animator.GetBoneTransform(HumanBodyBones.Spine);
                Transform hips = source.animator.GetBoneTransform(HumanBodyBones.Hips);
                Assert.That(spine, Is.Not.Null);
                Assert.That(hips, Is.Not.Null);
                var baselinePose = new HumanPose();
                source.poseHandler.GetHumanPose(ref baselinePose);
                Vector3 baselineBodyPosition = baselinePose.bodyPosition * source.humanScale;
                Quaternion baselineBodyRotation = baselinePose.bodyRotation;
                string spinePath = AnimationUtility.CalculateTransformPath(spine, source.animator.transform);
                Quaternion bindRotation = spine.localRotation;
                Quaternion sampledRotation = bindRotation * Quaternion.Euler(30f, 0f, 0f);
                float[] bindValues = { bindRotation.x, bindRotation.y, bindRotation.z, bindRotation.w };
                float[] sampledValues = { sampledRotation.x, sampledRotation.y, sampledRotation.z, sampledRotation.w };
                string[] properties =
                {
                    "m_LocalRotation.x",
                    "m_LocalRotation.y",
                    "m_LocalRotation.z",
                    "m_LocalRotation.w"
                };
                for (int i = 0; i < properties.Length; i++)
                {
                    AnimationUtility.SetEditorCurve(
                        boneClip,
                        EditorCurveBinding.FloatCurve(spinePath, typeof(Transform), properties[i]),
                        new AnimationCurve(
                            new Keyframe(0f, bindValues[i]),
                            new Keyframe(1f / 30f, sampledValues[i]),
                            new Keyframe(2f / 30f, sampledValues[i])));
                }
                boneClip.EnsureQuaternionContinuity();
                string hipsPath = AnimationUtility.CalculateTransformPath(hips, source.animator.transform);
                Vector3 bindHipsPosition = hips.localPosition;
                AnimationUtility.SetEditorCurve(
                    boneClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalPosition.x"),
                    new AnimationCurve(
                        new Keyframe(0f, bindHipsPosition.x),
                        new Keyframe(1f / 30f, bindHipsPosition.x + 0.25f),
                        new Keyframe(2f / 30f, bindHipsPosition.x + 0.25f)));

                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(0.25f, 1f, -0.4f);
                track.rotation = Quaternion.Euler(0f, 35f, 0f);
                TimelineClip timelineClip = track.CreateClip<AnimationPlayableAsset>();
                ((AnimationPlayableAsset)timelineClip.asset).clip = boneClip;
                timelineClip.start = 0.0;
                timelineClip.duration = boneClip.length;

                PlayableDirector director = directorRoot.AddComponent<PlayableDirector>();
                director.timeUpdateMode = DirectorUpdateMode.Manual;
                director.playableAsset = timeline;
                director.SetGenericBinding(track, source.animator);

                source.animator.avatar = null;
                director.RebuildGraph();
                director.time = 0.0;
                director.Evaluate();

                var context = new KimodoTimelineInOutConstraintContext
                {
                    SourceClip = timelineClip,
                    Track = track,
                    Director = director,
                    Animator = source.animator,
                    SourceAvatar = avatar,
                    ModelName = KimodoMotionModelProfiles.ArdyCoreModelName
                };
                Assert.That(
                    KimodoTimelineSamplingSession.TryCreate(
                        context,
                        KimodoMotionModelProfiles.ArdyCoreModelName,
                        out sampler,
                        out error),
                    Is.True,
                    error);
                Assert.That(source.animator.avatar, Is.Null, "Timeline sampling must not mutate the binding Animator Avatar.");
                var sourceIntermediate = (RetargetSkeleton)typeof(KimodoTimelineSamplingSession)
                    .GetField("sourceSamplingCache", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(sampler);
                Assert.That(sourceIntermediate, Is.Not.Null);
                Assert.That(sourceIntermediate.avatar, Is.SameAs(avatar));
                Assert.That(
                    sampler.TryCaptureMuscleSample(
                        0.0,
                        false,
                        Vector3.zero,
                        Quaternion.identity,
                        out MuscleSample first,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    sampler.TryCaptureMuscleSample(
                        1.0 / 30.0,
                        false,
                        Vector3.zero,
                        Quaternion.identity,
                        out MuscleSample second,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    Mathf.Abs(second.pose.muscles[muscleIndex] - first.pose.muscles[muscleIndex]),
                    Is.GreaterThan(0.1f));
                Assert.That(Quaternion.Angle(spine.localRotation, sampledRotation), Is.LessThan(1f));
                Assert.That(source.animator.avatar, Is.Null, "Timeline sampling must leave the binding Animator Avatar unchanged.");

                Assert.That(
                    sampler.TryCaptureTargetBoneSamples(
                        new[] { 1.0 / 30.0 },
                        30f,
                        out BoneSample[] targetSamples,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(
                        targetSamples[0],
                        sampler.TargetCache,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    sampler.TargetCache.humanBoneTransforms.TryGetValue(HumanBodyBones.Hips, out Transform rebuiltHips),
                    Is.True);
                Assert.That(rebuiltHips, Is.Not.Null);
                Vector3 secondHipsPosition = rebuiltHips.position;
                Quaternion secondHipsRotation = rebuiltHips.rotation;
                Assert.That(
                    KimodoRetargetSamplingUtility.TryCreateTransientBoneClip(
                        new[] { targetSamples[0], targetSamples[0] },
                        30f,
                        out ardyHistoryClip,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetSamplingUtility.TryCollectMuscleSamplesFromClip(
                        ardyHistoryClip,
                        sampler.TargetCache,
                        2,
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.RawTransform,
                        out MuscleSample[] ardyMuscleSamples,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetSamplingUtility.TryRetargetMuscleSamplesToBoneSamples(
                        new[] { ardyMuscleSamples[0] },
                        30f,
                        sourceIntermediate,
                        out BoneSample[] roundTripSamples,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(
                        roundTripSamples[0],
                        sourceIntermediate,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    sourceIntermediate.humanBoneTransforms.TryGetValue(HumanBodyBones.Hips, out Transform roundTripHips),
                    Is.True);

                if (!preserveSampledPoseForInspection)
                {
                    sampler.Dispose();
                    sampler = null;
                    Assert.That(sourceIntermediate.root, Is.Null, "The virtual source skeleton must be disposed with the sampler.");
                }
                Assert.That(source.animator.avatar, Is.Null);
                Assert.That(director.GetGenericBinding(track), Is.SameAs(source.animator));
            }
            finally
            {
                if (!preserveSampledPoseForInspection)
                {
                    sampler?.Dispose();
                    source.Dispose();
                    if (ardyHistoryClip != null)
                    {
                        UnityEngine.Object.DestroyImmediate(ardyHistoryClip);
                    }
                    UnityEngine.Object.DestroyImmediate(directorRoot);
                    UnityEngine.Object.DestroyImmediate(timeline);
                    UnityEngine.Object.DestroyImmediate(boneClip);
                }
            }
        }

        [Test]
        public void EndConstraintTarget_IsWorldSpaceCubeEditableOnlyDuringEdit()
        {
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
                    "KimodoEndConstraintTargetTest",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            var context = new PoseCacheRenderContext(
                101,
                KimodoUnityObjectIdUtility.IdHash(source.animator),
                102,
                KimodoMotionModelProfiles.DefaultModelName,
                KimodoConstraintRigType.Soma77,
                avatar);
            const string entryId = "end-target-test";
            try
            {
                KimodoConstraintPoseCache.DestroyAll();
                KimodoMarkerSampleResult sample = KimodoMarkerSamplingUtility.CreateDefaultMarkerSample(
                    KimodoMotionModelProfiles.DefaultModelName,
                    source.skeletonRoot,
                    "left-hand");
                var items = new[]
                {
                    new PoseCacheRenderItem
                    {
                        EntryId = entryId,
                        SampleData = sample,
                        ConstraintType = "left-hand",
                        Visible = true
                    }
                };

                Assert.That(KimodoConstraintPoseCache.RenderBatch(context, items, out error), Is.True, error);
                Assert.That(
                    KimodoConstraintPoseCache.TryGetEndEffectorTarget(context, entryId, out GameObject target),
                    Is.True);
                Assert.That(target, Is.Not.Null);
                Assert.That(target.GetComponent<MeshFilter>()?.sharedMesh?.name, Is.EqualTo("Sphere"));
                Assert.That(target.transform.lossyScale.x, Is.EqualTo(0.05f).Within(1e-4f));
                Assert.That(target.transform.lossyScale.y, Is.EqualTo(0.05f).Within(1e-4f));
                Assert.That(target.transform.lossyScale.z, Is.EqualTo(0.05f).Within(1e-4f));
                Assert.That((target.hideFlags & HideFlags.NotEditable) != 0, Is.True);
                Assert.That(target.transform.parent, Is.Null);

                KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: true);
                Assert.That((target.hideFlags & HideFlags.NotEditable) == 0, Is.True);
                Assert.That((target.hideFlags & HideFlags.HideInHierarchy) == 0, Is.True);
            }
            finally
            {
                KimodoConstraintPoseCache.DestroyAll();
                source.Dispose();
            }
        }

        [Test]
        public void FullBodyConstraint_CreatesPelvisAndLimbIkTargetsAndWritesCanonicalPose()
        {
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
                    "KimodoFullBodyTargetsTest",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            var context = new PoseCacheRenderContext(
                103,
                KimodoUnityObjectIdUtility.IdHash(source.animator),
                104,
                KimodoMotionModelProfiles.DefaultModelName,
                KimodoConstraintRigType.Soma77,
                avatar);
            const string entryId = "fullbody-targets-test";
            try
            {
                KimodoConstraintPoseCache.DestroyAll();
                KimodoMarkerSampleResult sample = KimodoMarkerSamplingUtility.CreateDefaultMarkerSample(
                    KimodoMotionModelProfiles.DefaultModelName,
                    source.skeletonRoot,
                    "fullbody");
                sample.characterPose.muscles[0] = 0.123f;
                sample.characterPose.muscles[17] = -0.234f;
                float[] authoredMuscles = (float[])sample.characterPose.muscles.Clone();
                Assert.That(
                    KimodoConstraintPoseCache.RenderBatch(
                        context,
                        new[]
                        {
                            new PoseCacheRenderItem
                            {
                                EntryId = entryId,
                                SampleData = sample,
                                ConstraintType = "constraint",
                                Visible = true
                            }
                        },
                        out error),
                    Is.True,
                    error);

                HumanBodyBones[] bones =
                {
                    HumanBodyBones.Hips,
                    HumanBodyBones.LeftHand,
                    HumanBodyBones.RightHand,
                    HumanBodyBones.LeftFoot,
                    HumanBodyBones.RightFoot
                };
                for (int i = 0; i < bones.Length; i++)
                {
                    Assert.That(
                        KimodoConstraintPoseCache.TryGetFullBodyTarget(
                            context,
                            entryId,
                            bones[i],
                            out GameObject target),
                        Is.True,
                        $"Missing FullBody target for {bones[i]}.");
                    Assert.That(target, Is.Not.Null);
                }

                Assert.That(
                    KimodoConstraintPoseCache.TryBuildSampleFromContext(
                        context,
                        entryId,
                        "constraint",
                        0.0,
                        out KimodoMarkerSampleResult initial,
                        out error),
                    Is.True,
                    error);
                Assert.That(initial.characterPose.muscles, Is.EqualTo(authoredMuscles),
                    "Initial sampling must preserve authored muscle values.");
                Assert.That(
                    KimodoConstraintPoseCache.TryGetFullBodyTarget(
                        context,
                        entryId,
                        HumanBodyBones.LeftHand,
                        out GameObject leftHandTarget),
                    Is.True);
                leftHandTarget.transform.position += new Vector3(0.02f, 0.03f, 0.01f);
                Assert.That(
                    KimodoConstraintPoseCache.TryGetFullBodyTarget(
                        context,
                        entryId,
                        HumanBodyBones.Hips,
                        out GameObject pelvisTarget),
                    Is.True);
                Assert.That(
                    Vector3.Distance(
                        pelvisTarget.transform.position,
                        initial.characterPose.root.t * source.humanScale),
                    Is.LessThan(1e-4f),
                    "The FullBody Root target must display the solved HumanPose root, not Hips.");
                Vector3 requestedRootWorld = pelvisTarget.transform.position + new Vector3(0.04f, 0.02f, -0.03f);
                pelvisTarget.transform.position = requestedRootWorld;
                Assert.That(
                    KimodoConstraintPoseCache.TryPreviewEndEffectorTargetPose(
                        context,
                        entryId,
                        "constraint",
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoConstraintPoseCache.TryBuildSampleFromContext(
                        context,
                        entryId,
                        "constraint",
                        0.0,
                        out KimodoMarkerSampleResult edited,
                        out error),
                    Is.True,
                    error);
                Assert.That(edited.characterPose, Is.Not.Null);
                Assert.That(edited.characterPose.TryValidate(out error), Is.True, error);
                Assert.That(edited.characterPose.muscles, Is.EqualTo(authoredMuscles),
                    "Dragging IK targets must not resample post-IK muscles into the canonical pose.");
                Assert.That(edited.mask.leftHand, Is.True,
                    "Dragging a disabled FullBody hand target must enable its IK channel.");
                Assert.That(
                    Vector3.Distance(initial.characterPose.root.t, edited.characterPose.root.t),
                    Is.GreaterThan(1e-4f));
                Assert.That(
                    Vector3.Distance(edited.characterPose.root.t * source.humanScale, requestedRootWorld),
                    Is.LessThan(Vector3.Distance(initial.characterPose.root.t * source.humanScale, requestedRootWorld)));
            }
            finally
            {
                KimodoConstraintPoseCache.DestroyAll();
                source.Dispose();
            }
        }

        [Test]
        public void HumanoidIkGoalSpace_WorldConversionRoundTrips()
        {
            Vector3 bodyPosition = new Vector3(1.5f, 0.25f, -2f);
            Quaternion bodyRotation = Quaternion.Euler(12f, 47f, -6f);
            Vector3 worldPosition = new Vector3(3f, 1.2f, -0.7f);
            Quaternion worldRotation = Quaternion.Euler(-15f, 81f, 22f);
            float humanScale = 1f;

            KimodoRetargetHumanoidPoseUtility.WorldToBodyRelativeEffector(
                bodyPosition,
                bodyRotation,
                humanScale,
                worldPosition,
                worldRotation,
                out Vector3 goalPosition,
                out Quaternion goalRotation);
            KimodoRetargetHumanoidPoseUtility.BodyRelativeEffectorToWorld(
                bodyPosition,
                bodyRotation,
                humanScale,
                goalPosition,
                goalRotation,
                out Vector3 roundTripPosition,
                out Quaternion roundTripRotation);

            Assert.That(Vector3.Distance(roundTripPosition, worldPosition), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(roundTripRotation, worldRotation), Is.LessThan(1e-4f));
        }

        [TestCase(AvatarIKGoal.LeftHand)]
        [TestCase(AvatarIKGoal.RightHand)]
        [TestCase(AvatarIKGoal.LeftFoot)]
        [TestCase(AvatarIKGoal.RightFoot)]
        public void SingleMuscleSample_AppliesHumanoidIkGoalPositionAndRotation(AvatarIKGoal goal)
        {
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
                    $"KimodoTimeline{goal}IkTest",
                    out RetargetSkeleton cache,
                    out error),
                Is.True,
                error);

            try
            {
                Assert.That(
                    KimodoRetargetSamplingUtility.TryCaptureMuscleSample(cache, out MuscleSample source, out error),
                    Is.True,
                    error);
                Assert.That(TryGetIkGoal(source, goal, out Vector3 initialPosition, out Quaternion initialRotation), Is.True);

                Vector3 desiredPosition = initialPosition + new Vector3(0.02f, 0.015f, -0.01f);
                Quaternion desiredRotation = Quaternion.AngleAxis(5f, Vector3.up) * initialRotation;
                Assert.That(TrySetIkGoal(source, goal, desiredPosition, desiredRotation), Is.True);
                Assert.That(
                    KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        source,
                        KimodoMotionModelProfiles.DefaultFrameRate,
                        cache,
                        out _,
                        out MuscleSample solved,
                        out error),
                    Is.True,
                    error);
                Assert.That(solved, Is.Not.Null);
                Assert.That(TryGetIkGoal(solved, goal, out Vector3 solvedPosition, out Quaternion solvedRotation), Is.True);

                Assert.That(
                    Vector3.Distance(solvedPosition, desiredPosition),
                    Is.LessThan(Vector3.Distance(initialPosition, desiredPosition)),
                    $"{goal} position did not move toward the requested IK goal.");
                Assert.That(
                    Quaternion.Angle(solvedRotation, desiredRotation),
                    Is.LessThan(Quaternion.Angle(initialRotation, desiredRotation)),
                    $"{goal} rotation did not move toward the requested IK goal.");
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void SolvedMuscleSample_PreservesAuthoredIkCurveChannels()
        {
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
                    "KimodoTimelineSolvedIkCurveChannels",
                    out RetargetSkeleton cache,
                    out error),
                Is.True,
                error);

            try
            {
                Assert.That(
                    KimodoRetargetSamplingUtility.TryCaptureMuscleSample(cache, out MuscleSample source, out error),
                    Is.True,
                    error);

                source.leftHandPosition += new Vector3(0.02f, 0.01f, -0.01f);
                source.rightHandPosition += new Vector3(-0.02f, 0.01f, 0.01f);
                source.leftFootPosition += new Vector3(0.01f, 0.02f, -0.01f);
                source.rightFootPosition += new Vector3(-0.01f, 0.02f, 0.01f);
                source.leftHandRotation = Quaternion.Euler(3f, 5f, 7f) * source.leftHandRotation;
                source.rightHandRotation = Quaternion.Euler(-3f, -5f, -7f) * source.rightHandRotation;
                source.leftFootRotation = Quaternion.Euler(2f, 4f, 6f) * source.leftFootRotation;
                source.rightFootRotation = Quaternion.Euler(-2f, -4f, -6f) * source.rightFootRotation;

                Assert.That(
                    KimodoRetargetSamplingUtility.TryRebuildPoseFromMuscles(
                        source,
                        KimodoMotionModelProfiles.DefaultFrameRate,
                        cache,
                        out _,
                        out MuscleSample solved,
                        out error),
                    Is.True,
                    error);

                Assert.That(solved, Is.Not.Null);
                Assert.That(Vector3.Distance(solved.leftHandPosition, source.leftHandPosition), Is.LessThan(1e-6f));
                Assert.That(Vector3.Distance(solved.rightHandPosition, source.rightHandPosition), Is.LessThan(1e-6f));
                Assert.That(Vector3.Distance(solved.leftFootPosition, source.leftFootPosition), Is.LessThan(1e-6f));
                Assert.That(Vector3.Distance(solved.rightFootPosition, source.rightFootPosition), Is.LessThan(1e-6f));
                Assert.That(Quaternion.Angle(solved.leftHandRotation, source.leftHandRotation), Is.LessThan(1e-4f));
                Assert.That(Quaternion.Angle(solved.rightHandRotation, source.rightHandRotation), Is.LessThan(1e-4f));
                Assert.That(Quaternion.Angle(solved.leftFootRotation, source.leftFootRotation), Is.LessThan(1e-4f));
                Assert.That(Quaternion.Angle(solved.rightFootRotation, source.rightFootRotation), Is.LessThan(1e-4f));
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void SingleMuscleSample_WithoutAnyIk_ReportsFkFootError()
        {
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
                    "KimodoTimelineFkOnlySource",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    avatar,
                    "KimodoTimelineFkOnlyTarget",
                    out RetargetSkeleton target,
                    out error),
                Is.True,
                error);

            try
            {
                source.skeletonRoot.SetPositionAndRotation(
                    new Vector3(1.25f, 0.2f, -0.75f),
                    Quaternion.Euler(0f, 32f, 0f));
                var sourcePose = new HumanPose();
                source.poseHandler.GetHumanPose(ref sourcePose);
                sourcePose.bodyPosition += new Vector3(0.08f, 0.03f, -0.05f) / source.humanScale;
                sourcePose.bodyRotation = Quaternion.Euler(0f, 17f, 0f) * sourcePose.bodyRotation;
                source.poseHandler.SetHumanPose(ref sourcePose);
                Assert.That(
                    KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        source,
                        out MuscleSample input,
                        out error),
                    Is.True,
                    error);

                KimodoRetargetClipSamplingUtility.ResetRetargetSkeletonPose(target);
                HumanPose expectedPose = input.pose;
                target.poseHandler.SetHumanPose(ref expectedPose);
                Transform expectedLeftFoot = target.animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                Transform expectedRightFoot = target.animator.GetBoneTransform(HumanBodyBones.RightFoot);
                Assert.That(expectedLeftFoot, Is.Not.Null);
                Assert.That(expectedRightFoot, Is.Not.Null);
                Vector3 expectedLeftPosition = expectedLeftFoot.position;
                Vector3 expectedRightPosition = expectedRightFoot.position;

                Assert.That(
                    KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        input,
                        KimodoMotionModelProfiles.DefaultFrameRate,
                        target,
                        out _,
                        out MuscleSample solved,
                        out error),
                    Is.True,
                    error);

                float leftFkError = Vector3.Distance(
                    target.animator.GetBoneTransform(HumanBodyBones.LeftFoot).position,
                    expectedLeftPosition);
                float rightFkError = Vector3.Distance(
                    target.animator.GetBoneTransform(HumanBodyBones.RightFoot).position,
                    expectedRightPosition);
                KimodoRetargetHumanoidPoseUtility.BodyRelativeEffectorToWorld(
                    solved.pose.bodyPosition,
                    solved.pose.bodyRotation,
                    target.humanScale,
                    solved.leftFootPosition,
                    solved.leftFootRotation,
                    out Vector3 leftGoalWorld,
                    out _);
                KimodoRetargetHumanoidPoseUtility.BodyRelativeEffectorToWorld(
                    solved.pose.bodyPosition,
                    solved.pose.bodyRotation,
                    target.humanScale,
                    solved.rightFootPosition,
                    solved.rightFootRotation,
                    out Vector3 rightGoalWorld,
                    out _);
                float leftGoalBoneOffset = Vector3.Distance(
                    leftGoalWorld,
                    target.animator.GetBoneTransform(HumanBodyBones.LeftFoot).position);
                float rightGoalBoneOffset = Vector3.Distance(
                    rightGoalWorld,
                    target.animator.GetBoneTransform(HumanBodyBones.RightFoot).position);

                TestContext.Out.WriteLine(
                    $"FK-only foot error: left={leftFkError:F6}m, right={rightFkError:F6}m");
                TestContext.Out.WriteLine(
                    $"Foot goal-to-bone offset: left={leftGoalBoneOffset:F6}m, right={rightGoalBoneOffset:F6}m");
                Assert.That(leftFkError, Is.LessThan(0.01f));
                Assert.That(rightFkError, Is.LessThan(0.01f));
            }
            finally
            {
                target.Dispose();
                source.Dispose();
            }
        }

        [Test]
        public void SelfRetarget_PreservesProfileHandWorldPose()
        {
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
                    "KimodoSelfRetargetSource",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    avatar,
                    "KimodoSelfRetargetTarget",
                    out RetargetSkeleton target,
                    out error),
                Is.True,
                error);

            try
            {
                var pose = new HumanPose();
                source.poseHandler.GetHumanPose(ref pose);
                int[] changedMuscles = { 0, 1, 2, 21, 22, 23, 37, 38, 39, 40, 41, 42 };
                for (int i = 0; i < changedMuscles.Length; i++)
                {
                    int muscle = changedMuscles[i];
                    if (muscle < pose.muscles.Length)
                    {
                        pose.muscles[muscle] = Mathf.Clamp(pose.muscles[muscle] + 0.3f, -1f, 1f);
                    }
                }
                pose.bodyPosition += new Vector3(0.15f, 0.05f, -0.2f) / source.humanScale;
                pose.bodyRotation = Quaternion.Euler(0f, 31f, 0f) * pose.bodyRotation;
                source.poseHandler.SetHumanPose(ref pose);
                source.poseHandler.GetHumanPose(ref pose);

                MuscleSample input = KimodoRetargetHumanoidPoseUtility.BuildMuscleSampleFromPose(source, pose);
                HumanBodyBones[] handBones = { HumanBodyBones.LeftHand, HumanBodyBones.RightHand };
                var sourcePositions = new Vector3[handBones.Length];
                var sourceRotations = new Quaternion[handBones.Length];
                for (int i = 0; i < handBones.Length; i++)
                {
                    Transform hand = source.animator.GetBoneTransform(handBones[i]);
                    Assert.That(hand, Is.Not.Null);
                    sourcePositions[i] = hand.position;
                    sourceRotations[i] = hand.rotation;
                }

                Assert.That(
                    KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        input,
                        KimodoMotionModelProfiles.DefaultFrameRate,
                        target,
                        out _,
                        out _,
                        out error),
                    Is.True,
                    error);

                for (int i = 0; i < handBones.Length; i++)
                {
                    Transform hand = target.animator.GetBoneTransform(handBones[i]);
                    Assert.That(hand, Is.Not.Null);
                    Assert.That(
                        Vector3.Distance(sourcePositions[i], hand.position),
                        Is.LessThan(0.01f),
                        $"{handBones[i]} moved while retargeting the profile Avatar to itself.");
                    Assert.That(
                        Quaternion.Angle(sourceRotations[i], hand.rotation),
                        Is.LessThan(0.1f),
                        $"{handBones[i]} rotated while retargeting the profile Avatar to itself.");
                }
            }
            finally
            {
                source.Dispose();
                target.Dispose();
            }
        }

        [Test]
        public void EndConstraintTarget_UpdatesHandThroughMuscleRetarget()
        {
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
                    "KimodoEndConstraintHandIkTest",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            var context = new PoseCacheRenderContext(
                111,
                KimodoUnityObjectIdUtility.IdHash(source.animator),
                112,
                KimodoMotionModelProfiles.DefaultModelName,
                KimodoConstraintRigType.Soma77,
                avatar);
            const string entryId = "end-target-hand-ik-test";
            try
            {
                KimodoConstraintPoseCache.DestroyAll();
                KimodoMarkerSampleResult sample = KimodoMarkerSamplingUtility.CreateDefaultMarkerSample(
                    KimodoMotionModelProfiles.DefaultModelName,
                    source.skeletonRoot,
                    "left-hand");
                Assert.That(
                    KimodoConstraintPoseCache.RenderBatch(
                        context,
                        new[]
                        {
                            new PoseCacheRenderItem
                            {
                                EntryId = entryId,
                                SampleData = sample,
                                ConstraintType = "left-hand",
                                Visible = true
                            }
                        },
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoConstraintPoseCache.TryGetEndEffectorTarget(context, entryId, out GameObject target),
                    Is.True);
                Assert.That(
                    KimodoConstraintPoseCache.TryGetEndEffectorBone(
                        context,
                        entryId,
                        "left-hand",
                        out Transform hand),
                    Is.True);

                Quaternion handGoalRotation = hand.rotation *
                    AvatarRuntimeAccess.GetAvatarPostRotationOrIdentity(avatar, (int)HumanBodyBones.LeftHand);
                Vector3 handGoalPosition = KimodoRetargetHumanoidPoseUtility.BonePositionToEffectorWorldPosition(
                    avatar,
                    HumanBodyBones.LeftHand,
                    hand.position,
                    handGoalRotation);
                Assert.That(
                    Vector3.Distance(handGoalPosition, target.transform.position),
                    Is.LessThan(1e-4f),
                    "The editable hand target must use the same Humanoid IK goal as HandT/Q.");
                Assert.That(
                    KimodoConstraintPoseCache.TryPreviewEndEffectorTargetPose(
                        context,
                        entryId,
                        "left-hand",
                        out error),
                    Is.True,
                    error);
                handGoalRotation = hand.rotation *
                    AvatarRuntimeAccess.GetAvatarPostRotationOrIdentity(avatar, (int)HumanBodyBones.LeftHand);
                handGoalPosition = KimodoRetargetHumanoidPoseUtility.BonePositionToEffectorWorldPosition(
                    avatar,
                    HumanBodyBones.LeftHand,
                    hand.position,
                    handGoalRotation);
                Assert.That(
                    Vector3.Distance(handGoalPosition, target.transform.position),
                    Is.LessThan(0.01f),
                    "Applying an unchanged hand goal must preserve HandT/Q.");

                target.transform.position += new Vector3(0.04f, 0.06f, 0.03f);
                float beforeDistance = Vector3.Distance(handGoalPosition, target.transform.position);
                Assert.That(
                    KimodoConstraintPoseCache.TryPreviewEndEffectorTargetPose(
                        context,
                        entryId,
                        "left-hand",
                        out error),
                    Is.True,
                    error);
                handGoalRotation = hand.rotation *
                    AvatarRuntimeAccess.GetAvatarPostRotationOrIdentity(avatar, (int)HumanBodyBones.LeftHand);
                handGoalPosition = KimodoRetargetHumanoidPoseUtility.BonePositionToEffectorWorldPosition(
                    avatar,
                    HumanBodyBones.LeftHand,
                    hand.position,
                    handGoalRotation);
                float afterDistance = Vector3.Distance(handGoalPosition, target.transform.position);

                Assert.That(afterDistance, Is.LessThan(beforeDistance));
            }
            finally
            {
                KimodoConstraintPoseCache.DestroyAll();
                source.Dispose();
            }
        }

        [Test]
        public void ArdyHistoryRange_UsesOutsideInBoundaryFrame()
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
                var source = new ArdyEditorHistorySource
                {
                    TimelineContext = new KimodoTimelineInOutConstraintContext
                    {
                        SourceClip = current,
                        Track = track,
                        PreviousTimelineClip = previous
                    },
                    RangeStartSeconds = 0.0,
                    RangeEndSeconds = 1.0
                };

                Assert.That(
                    ArdyEditorHistoryEncoder.ResolveLatestHistorySampleTime(source),
                    Is.EqualTo(59.0 / 60.0).Within(1e-9));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void GeneratedWriteback_ClearsClipOffsetWithoutChangingRemoveStartOffset()
        {
            var destination = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                MethodInfo complete = typeof(KimodoPlayableClipGenerationHostService).GetMethod(
                    "HandleGeneratedClipWritebackCompleted",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(complete, Is.Not.Null);

                destination.position = new Vector3(1f, 2f, 3f);
                destination.rotation = Quaternion.Euler(10f, 20f, 30f);
                destination.removeStartOffset = true;

                complete.Invoke(null, new object[] { destination });

                Assert.That(Vector3.Distance(destination.position, Vector3.zero), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(destination.rotation, Quaternion.identity), Is.LessThan(1e-4f));
                Assert.That(destination.removeStartOffset, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(destination);
            }
        }

        [Test]
        public void HumanoidIkGoals_AreInvariantToSkeletonRootWorldPose()
        {
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
                    "KimodoTimelineFootIkRootSpaceTest",
                    out RetargetSkeleton cache,
                    out error),
                Is.True,
                error);

            try
            {
                var pose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref pose);
                MuscleSample before = KimodoRetargetHumanoidPoseUtility.BuildMuscleSampleFromPose(cache, pose);

                cache.skeletonRoot.SetPositionAndRotation(
                    new Vector3(7f, 2f, -3f),
                    Quaternion.Euler(0f, 73f, 0f));
                cache.poseHandler.GetHumanPose(ref pose);
                MuscleSample after = KimodoRetargetHumanoidPoseUtility.BuildMuscleSampleFromPose(cache, pose);

                Assert.That(Vector3.Distance(before.leftFootPosition, after.leftFootPosition), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(before.rightFootPosition, after.rightFootPosition), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(before.leftFootRotation, after.leftFootRotation), Is.LessThan(1e-4f));
                Assert.That(Quaternion.Angle(before.rightFootRotation, after.rightFootRotation), Is.LessThan(1e-4f));
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void SingleMuscleSample_RestoresAbsoluteTargetRootAfterHumanoidFootIkPlayable()
        {
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
                    "KimodoTimelineSingleFrameFootIkTest",
                    out RetargetSkeleton cache,
                    out error),
                Is.True,
                error);

            try
            {
                var pose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref pose);
                MuscleSample source = KimodoRetargetHumanoidPoseUtility.BuildMuscleSampleFromPose(cache, pose);
                Vector3 rootOffset = new Vector3(0.25f, 0f, -0.4f);
                source.pose.bodyPosition += rootOffset / cache.humanScale;
                HumanPose directPose = source.pose;
                cache.poseHandler.SetHumanPose(ref directPose);
                Assert.That(
                    KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        KimodoMotionModelProfiles.DefaultModelName,
                        cache,
                        out _,
                        out _,
                        out Transform[] directJoints,
                        out error),
                    Is.True,
                    error);
                Vector3 directRootPosition = directJoints[0].position;
                Quaternion directRootRotation = directJoints[0].rotation;

                Assert.That(
                    KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        source,
                        KimodoMotionModelProfiles.DefaultFrameRate,
                        cache,
                        out BoneSample target,
                        out MuscleSample targetMuscle,
                        out error),
                    Is.True,
                    error);
                Assert.That(target, Is.Not.Null);
                Assert.That(target.IsValid, Is.True);
                Assert.That(targetMuscle, Is.Not.Null);
                Assert.That(
                    KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(
                        target,
                        cache,
                        out error),
                    Is.True,
                    error);
                Transform restoredRoot = directJoints[0];
                Assert.That(Vector3.Distance(restoredRoot.position, directRootPosition), Is.LessThan(1e-3f));
                Assert.That(Quaternion.Angle(restoredRoot.rotation, directRootRotation), Is.LessThan(0.1f));
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void MuscleClipGraph_UsesMotionXAbsoluteRootWithoutManualRestore()
        {
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
                    "KimodoTimelineHumanPoseRootTest",
                    out RetargetSkeleton cache,
                    out error),
                Is.True,
                error);

            AnimationClip clip = null;
            KimodoRetargetClipSamplingUtility.ClipSamplingContext graph = null;
            try
            {
                var pose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref pose);
                pose.bodyPosition += new Vector3(0.25f, 1f, -0.4f) / cache.humanScale;
                pose.bodyRotation = Quaternion.Euler(0f, 35f, 0f) * pose.bodyRotation;
                MuscleSample sample = KimodoRetargetHumanoidPoseUtility.BuildMuscleSampleFromPose(cache, pose);

                cache.skeletonRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                HumanPose directPose = sample.pose;
                cache.poseHandler.SetHumanPose(ref directPose);
                Assert.That(
                    KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        KimodoMotionModelProfiles.DefaultModelName,
                        cache,
                        out _,
                        out _,
                        out Transform[] joints,
                        out error),
                    Is.True,
                    error);
                Vector3 directRootPosition = joints[0].position;
                Quaternion directRootRotation = joints[0].rotation;

                Assert.That(
                    KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(
                        new[] { sample, sample },
                        KimodoMotionModelProfiles.DefaultFrameRate,
                        out clip,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetClipSamplingUtility.TryBuildClipSamplingContext(
                        clip,
                        cache,
                        "KimodoTimelineHumanPoseRootGraph",
                        KimodoRetargetClipSamplingUtility.ClipSamplingMode.Humanoid,
                        out graph,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetClipSamplingUtility.TryEvaluateClipSamplingContext(graph, 0f, out error),
                    Is.True,
                    error);

                Assert.That(Vector3.Distance(joints[0].position, directRootPosition), Is.LessThan(1e-3f));
                Assert.That(Quaternion.Angle(joints[0].rotation, directRootRotation), Is.LessThan(0.1f));
            }
            finally
            {
                graph?.Dispose();
                if (clip != null)
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                }
                cache.Dispose();
            }
        }

        [Test]
        public void TrackOffset_ResolvesWorldPose()
        {
            AnimationTrack track = ScriptableObject.CreateInstance<AnimationTrack>();
            var parent = new GameObject("TrackOffsetParent");
            var character = new GameObject("TrackOffsetCharacter");
            try
            {
                parent.transform.SetPositionAndRotation(
                    new Vector3(4f, 1f, -2f),
                    Quaternion.Euler(0f, 30f, 0f));
                character.transform.SetParent(parent.transform, false);
                Animator animator = character.AddComponent<Animator>();
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(2f, 0.5f, 3f);
                track.rotation = Quaternion.Euler(0f, 40f, 0f);

                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    track,
                    animator,
                    out Vector3 offsetPosition,
                    out Quaternion offsetRotation,
                    out bool rootPoseIncludesOffset);

                Assert.That(
                    Vector3.Distance(offsetPosition, parent.transform.TransformPoint(track.position)),
                    Is.LessThan(1e-5f));
                Assert.That(
                    Quaternion.Angle(offsetRotation, parent.transform.rotation * track.rotation),
                    Is.LessThan(1e-4f));
                Assert.That(rootPoseIncludesOffset, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(track);
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void SceneOffset_UsesTimelinePreviewFields()
        {
            AnimationTrack track = ScriptableObject.CreateInstance<AnimationTrack>();
            var character = new GameObject("SceneOffsetCharacter");
            try
            {
                Animator animator = character.AddComponent<Animator>();
                track.trackOffset = TrackOffset.ApplySceneOffsets;
                character.transform.SetPositionAndRotation(
                    new Vector3(9f, 8f, 7f),
                    Quaternion.Euler(0f, 80f, 0f));
                Vector3 expectedPosition = new Vector3(-1f, 0f, 0f);
                Vector3 expectedEuler = new Vector3(0f, 35f, 0f);
                typeof(AnimationTrack).GetField(
                    "m_SceneOffsetPosition",
                    BindingFlags.Instance | BindingFlags.NonPublic).SetValue(track, expectedPosition);
                typeof(AnimationTrack).GetField(
                    "m_SceneOffsetRotation",
                    BindingFlags.Instance | BindingFlags.NonPublic).SetValue(track, expectedEuler);

                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    track,
                    animator,
                    out Vector3 offsetPosition,
                    out Quaternion offsetRotation,
                    out bool rootPoseIncludesOffset);

                Assert.That(Vector3.Distance(offsetPosition, expectedPosition), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(offsetRotation, Quaternion.Euler(expectedEuler)), Is.LessThan(1e-4f));
                Assert.That(rootPoseIncludesOffset, Is.True);

            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(track);
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        [Test]
        public void AnimationOffsetPlayable_AppliesRootOffsetExactlyOnceOnFirstFrame()
        {
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
                    "KimodoTimelineOffsetPlayableTest",
                    out RetargetSkeleton cache,
                    out error),
                Is.True,
                error);
            Assert.That(
                KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                    KimodoMotionModelProfiles.DefaultModelName,
                    cache,
                    out _,
                    out _,
                    out Transform[] joints,
                    out error),
                Is.True,
                error);
            Transform profileRoot = joints[0];

            var clip = new AnimationClip { frameRate = KimodoMotionModelProfiles.DefaultFrameRate };
            PlayableGraph graph = default;
            try
            {
                var sourcePose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref sourcePose);
                sourcePose.bodyPosition += new Vector3(0.3f, 0f, -0.2f) / cache.humanScale;
                Assert.That(
                    KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(
                        new[]
                        {
                            KimodoRetargetHumanoidPoseUtility.BuildMuscleSampleFromPose(cache, sourcePose),
                            KimodoRetargetHumanoidPoseUtility.BuildMuscleSampleFromPose(cache, sourcePose)
                        },
                        clip,
                        out error),
                    Is.True,
                    error);

                Vector3 offsetPosition = new Vector3(-1f, 0f, 0.5f);
                Quaternion offsetRotation = Quaternion.Euler(0f, 35f, 0f);
                graph = PlayableGraph.Create("KimodoAnimationOffsetPlayableBaselineGraph");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                AnimationClipPlayable baselinePlayable = AnimationClipPlayable.Create(graph, clip);
                AnimationPlayableOutput baselineOutput = AnimationPlayableOutput.Create(
                    graph,
                    "KimodoAnimationOffsetPlayableBaselineOutput",
                    cache.animator);
                baselineOutput.SetSourcePlayable(baselinePlayable);
                graph.Play();
                graph.Evaluate(0f);
                Vector3 baselinePosition = profileRoot.position;
                Quaternion baselineRotation = profileRoot.rotation;
                graph.Destroy();

                graph = PlayableGraph.Create("KimodoAnimationOffsetPlayableTestGraph");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                AnimationClipPlayable clipPlayable = AnimationClipPlayable.Create(graph, clip);
                Playable offsetPlayable = AnimationOffsetPlayableAccess.CreateAndConnect(
                    graph,
                    clipPlayable,
                    offsetPosition,
                    offsetRotation);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(
                    graph,
                    "KimodoAnimationOffsetPlayableTestOutput",
                    cache.animator);
                output.SetSourcePlayable(offsetPlayable);
                graph.Play();
                graph.Evaluate(0f);
                graph.Evaluate(1f / KimodoMotionModelProfiles.DefaultFrameRate);

                Vector3 expectedPosition = offsetPosition +
                    offsetRotation * new Vector3(0.3f, baselinePosition.y, -0.2f);
                Quaternion expectedRotation = offsetRotation;
                Vector3 doubleOffsetPosition = offsetPosition * 2f + Vector3.up * baselinePosition.y;
                Assert.That(
                    Vector3.Distance(profileRoot.position, expectedPosition),
                    Is.LessThan(1e-3f),
                    $"baseline={baselinePosition}, actual={profileRoot.position}, expected={expectedPosition}");
                Assert.That(Quaternion.Angle(profileRoot.rotation, expectedRotation), Is.LessThan(1e-3f));
                Assert.That(Vector3.Distance(profileRoot.position, doubleOffsetPosition), Is.GreaterThan(0.1f));
            }
            finally
            {
                if (graph.IsValid())
                {
                    graph.Destroy();
                }
                UnityEngine.Object.DestroyImmediate(clip);
                cache.Dispose();
            }
        }

        [Test]
        public void TimelineConstraintCache_SourceSignatureIgnoresMarkersAndTracksMotionChanges()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var sourceClip = new AnimationClip();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<AnimationPlayableAsset>();
                var playableAsset = (AnimationPlayableAsset)timelineClip.asset;
                playableAsset.clip = sourceClip;
                timelineClip.start = 0.0;
                timelineClip.duration = 2.0;

                int initial = KimodoTimelineConstraintClipCache.ComputeSamplingSourceSignature(track);
                KimodoConstraintMarker marker = track.CreateMarker<KimodoConstraintMarker>(0.5);
                marker.time = 1.0;
                EditorUtility.SetDirty(track);

                Assert.That(
                    KimodoTimelineConstraintClipCache.ComputeSamplingSourceSignature(track),
                    Is.EqualTo(initial),
                    "Constraint marker edits must not invalidate sampled motion windows.");

                timelineClip.start = 0.25;
                Assert.That(
                    KimodoTimelineConstraintClipCache.ComputeSamplingSourceSignature(track),
                    Is.Not.EqualTo(initial),
                    "Timeline motion edits must invalidate sampled motion windows.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
                UnityEngine.Object.DestroyImmediate(sourceClip);
            }
        }

        [Test]
        public void TimelineConstraintCache_UsesFixedHalfOpenFrameBuckets()
        {
            KimodoTimelineConstraintCacheRange first = KimodoTimelineConstraintClipCache.ResolveRange(
                timelineTime: 1.999,
                trackEndTime: 10.0,
                cacheTimeFrames: 60,
                frameRate: 30f);
            KimodoTimelineConstraintCacheRange second = KimodoTimelineConstraintClipCache.ResolveRange(
                timelineTime: 2.0,
                trackEndTime: 10.0,
                cacheTimeFrames: 60,
                frameRate: 30f);
            KimodoTimelineConstraintCacheRange last = KimodoTimelineConstraintClipCache.ResolveRange(
                timelineTime: 9.999,
                trackEndTime: 10.0,
                cacheTimeFrames: 60,
                frameRate: 30f);

            Assert.That(first.StartFrame, Is.EqualTo(0));
            Assert.That(first.EndFrame, Is.EqualTo(60));
            Assert.That(first.BakedStartFrame, Is.EqualTo(0));
            Assert.That(first.BakedEndFrame, Is.EqualTo(60));
            Assert.That(second.StartFrame, Is.EqualTo(60));
            Assert.That(second.EndFrame, Is.EqualTo(120));
            Assert.That(second.BakedStartFrame, Is.EqualTo(59));
            Assert.That(second.BakedEndFrame, Is.EqualTo(120));
            Assert.That(last.StartFrame, Is.EqualTo(240));
            Assert.That(last.EndFrame, Is.EqualTo(300));
            Assert.That(last.BakedStartFrame, Is.EqualTo(239));
            Assert.That(last.BakedEndFrame, Is.EqualTo(300));
        }

        [Test]
        public void TimelineConstraintSample_UsesTimelineFrameQuantization()
        {
            Assert.That(
                KimodoTimelineConstraintClipCache.ResolveTimelineSampleFrame(1.999, 30f),
                Is.EqualTo(59));
            Assert.That(
                KimodoTimelineConstraintClipCache.ResolveTimelineSampleTime(1.999, 30f),
                Is.EqualTo(59.0 / 30.0).Within(1e-9));
        }

        [Test]
        public void ConstraintMarkerTrackResolution_DoesNotRequireClipAtMarkerTime()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip clip = track.CreateClip<AnimationPlayableAsset>();
                clip.start = 0.0;
                clip.duration = 1.0;
                _ = track.end;
                typeof(TimelineClip).GetField(
                        "m_PostExtrapolationMode",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(clip, TimelineClip.ClipExtrapolation.None);
                KimodoConstraintMarker marker = track.CreateMarker<KimodoConstraintMarker>(2.0);

                Assert.That(KimodoConstraintMarkerEditorUtility.TryGetMarkerTrack(marker, out TrackAsset resolvedTrack), Is.True);
                Assert.That(resolvedTrack, Is.SameAs(track));
                Assert.That(KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out _), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ConstraintMarkerClipResolution_IncludesTimelineExtrapolation()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip clip = track.CreateClip<AnimationPlayableAsset>();
                clip.start = 0.0;
                clip.duration = 1.0;
                typeof(TimelineClip).GetField(
                        "m_PostExtrapolationMode",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(clip, TimelineClip.ClipExtrapolation.Loop);
                typeof(TimelineClip).GetMethod(
                        "SetPostExtrapolationTime",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(clip, new object[] { 2.0 });
                KimodoConstraintMarker marker = track.CreateMarker<KimodoConstraintMarker>(2.0);

                Assert.That(KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out TimelineClip resolvedClip), Is.True);
                Assert.That(resolvedClip, Is.SameAs(clip));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ConstraintMarkerClipResolution_UsesTimelineFramesAtSharedBoundary()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                timeline.editorSettings.frameRate = 60.0;
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip previous = track.CreateClip<AnimationPlayableAsset>();
                previous.start = 0.0;
                previous.duration = 5.900000063578288;
                TimelineClip next = track.CreateClip<AnimationPlayableAsset>();
                next.start = 5.900000063578288;
                next.duration = 1.0;
                KimodoConstraintMarker marker = track.CreateMarker<KimodoConstraintMarker>(5.9);

                Assert.That(KimodoConstraintMarkerEditorUtility.IsTimeInClipFrameRange(marker.time, previous), Is.False);
                Assert.That(KimodoConstraintMarkerEditorUtility.IsTimeInClipFrameRange(marker.time, next), Is.True);
                Assert.That(
                    KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out TimelineClip resolvedClip),
                    Is.True);
                Assert.That(resolvedClip, Is.SameAs(next));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ConstraintMarkerAtApproximatePlayableClipStart_IsCollected()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                timeline.editorSettings.frameRate = 60.0;
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip clip = track.CreateClip<KimodoPlayableClip>();
                double frame827Time = 827.0 / 60.0;
                clip.start = frame827Time + 1e-6;
                clip.duration = 1.0;
                KimodoConstraintMarker marker = track.CreateMarker<KimodoConstraintMarker>(frame827Time);

                Assert.That(
                    KimodoTimelinePreviewRefreshUtility.ApproximatelyTimelineTime(marker.time, clip.start),
                    Is.True);
                Assert.That(
                    KimodoConstraintMarkerEditorUtility.IsTimeInClipFrameRange(marker.time, clip),
                    Is.True);
                Assert.That(
                    KimodoTimelineConstraintMarkerSampler.CollectMarkersForClip(track, clip),
                    Does.Contain(marker));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ConstraintMarkerAtRoundedClipEnd_IsCollectedByEndingClip()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                timeline.editorSettings.frameRate = 30.0;
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip clip = track.CreateClip<KimodoPlayableClip>();
                clip.start = 0.0;
                clip.duration = 149.0 / 30.0;
                KimodoConstraintMarker marker = track.CreateMarker<KimodoConstraintMarker>(4.9667);

                Assert.That(
                    KimodoTimelineConstraintMarkerSampler.CollectMarkersForClip(track, clip),
                    Does.Contain(marker));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineConstraintCache_ExtendsPastLastClipToMarkerTime()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip clip = track.CreateClip<AnimationPlayableAsset>();
                clip.start = 0.0;
                clip.duration = 1.0;
                var context = new KimodoTimelineInOutConstraintContext
                {
                    SourceClip = null,
                    Track = track
                };

                double endTime = KimodoTimelineConstraintClipCache.ResolveSamplingEndTime(
                    context,
                    timelineTime: 2.0,
                    frameRate: 30f);
                KimodoTimelineConstraintCacheRange range = KimodoTimelineConstraintClipCache.ResolveRange(
                    timelineTime: 2.0,
                    trackEndTime: endTime,
                    cacheTimeFrames: 60,
                    frameRate: 30f);

                Assert.That(endTime, Is.GreaterThan(2.0));
                Assert.That(range.StartFrame, Is.EqualTo(60));
                Assert.That(range.ResolveLocalSampleTime(2.0), Is.EqualTo(1f / 30f).Within(1e-5f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineConstraintCache_KeepsRangesSeparateAndClearDestroysAllClips()
        {
            KimodoTimelineConstraintCacheRange firstRange = KimodoTimelineConstraintClipCache.ResolveRange(
                timelineTime: 1.0,
                trackEndTime: 10.0,
                cacheTimeFrames: 60,
                frameRate: 30f);
            KimodoTimelineConstraintCacheRange secondRange = KimodoTimelineConstraintClipCache.ResolveRange(
                timelineTime: 5.0,
                trackEndTime: 10.0,
                cacheTimeFrames: 60,
                frameRate: 30f);
            var firstClip = new AnimationClip
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = "KimodoTimelineConstraintCacheTest_First"
            };
            var secondClip = new AnimationClip
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = "KimodoTimelineConstraintCacheTest_Second"
            };
            var firstKey = new KimodoTimelineConstraintCacheKey(1, 2, 3, firstRange, "model");
            var secondKey = new KimodoTimelineConstraintCacheKey(1, 2, 3, secondRange, "model");
            FieldInfo field = typeof(KimodoTimelineConstraintClipCache).GetField(
                "Entries",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null);
            var entries = field.GetValue(null) as System.Collections.IDictionary;
            Assert.That(entries, Is.Not.Null);
            entries.Add(firstKey, new KimodoTimelineConstraintCacheEntry { Clip = firstClip });
            KimodoTimelineConstraintClipCache.RemoveStaleEntries(secondKey);
            Assert.That(entries.Contains(firstKey), Is.True);
            Assert.That(firstClip == null, Is.False);
            entries.Add(secondKey, new KimodoTimelineConstraintCacheEntry { Clip = secondClip });
            Assert.That(entries.Count, Is.EqualTo(2));

            KimodoTimelineConstraintClipCache.Clear();

            Assert.That(firstClip == null, Is.True);
            Assert.That(secondClip == null, Is.True);
            Assert.That(entries.Count, Is.Zero);
        }

        [Test]
        public void TimelineConstraintCache_SourceChangeInvalidatesAllCachedRanges()
        {
            KimodoTimelineConstraintCacheRange firstRange = KimodoTimelineConstraintClipCache.ResolveRange(1.0, 10.0, 60, 30f);
            KimodoTimelineConstraintCacheRange secondRange = KimodoTimelineConstraintClipCache.ResolveRange(5.0, 10.0, 60, 30f);
            var firstKey = new KimodoTimelineConstraintCacheKey(21, 22, 23, firstRange, "model", sourceSignature: 1);
            var secondKey = new KimodoTimelineConstraintCacheKey(21, 22, 23, secondRange, "model", sourceSignature: 1);
            var changedKey = new KimodoTimelineConstraintCacheKey(21, 22, 23, secondRange, "model", sourceSignature: 2);
            var firstClip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
            var secondClip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
            FieldInfo field = typeof(KimodoTimelineConstraintClipCache).GetField(
                "Entries",
                BindingFlags.NonPublic | BindingFlags.Static);
            var entries = field.GetValue(null) as System.Collections.IDictionary;
            entries.Add(firstKey, new KimodoTimelineConstraintCacheEntry { Clip = firstClip });
            entries.Add(secondKey, new KimodoTimelineConstraintCacheEntry { Clip = secondClip });

            KimodoTimelineConstraintClipCache.RemoveStaleEntries(changedKey);

            Assert.That(entries.Count, Is.Zero);
            Assert.That(firstClip == null, Is.True);
            Assert.That(secondClip == null, Is.True);
        }

        [Test]
        public void TimelineConstraintCache_TargetAnimatorOrAvatarChangeInvalidatesOldEntries()
        {
            KimodoTimelineConstraintCacheRange range = KimodoTimelineConstraintClipCache.ResolveRange(1.0, 10.0, 60, 30f);
            var oldAnimatorKey = new KimodoTimelineConstraintCacheKey(31, 32, 33, range, "model", avatarDirtyCount: 2);
            var oldAvatarKey = new KimodoTimelineConstraintCacheKey(31, 42, 34, range, "model", avatarDirtyCount: 2);
            var oldAvatarVersionKey = new KimodoTimelineConstraintCacheKey(31, 42, 33, range, "model", avatarDirtyCount: 1);
            var currentKey = new KimodoTimelineConstraintCacheKey(31, 42, 33, range, "model", avatarDirtyCount: 2);
            var oldAnimatorClip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
            var oldAvatarClip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
            var oldAvatarVersionClip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
            FieldInfo field = typeof(KimodoTimelineConstraintClipCache).GetField(
                "Entries",
                BindingFlags.NonPublic | BindingFlags.Static);
            var entries = field.GetValue(null) as System.Collections.IDictionary;
            entries.Add(oldAnimatorKey, new KimodoTimelineConstraintCacheEntry { Clip = oldAnimatorClip });
            entries.Add(oldAvatarKey, new KimodoTimelineConstraintCacheEntry { Clip = oldAvatarClip });
            entries.Add(oldAvatarVersionKey, new KimodoTimelineConstraintCacheEntry { Clip = oldAvatarVersionClip });

            KimodoTimelineConstraintClipCache.RemoveStaleEntries(currentKey);

            Assert.That(entries.Count, Is.Zero);
            Assert.That(oldAnimatorClip == null, Is.True);
            Assert.That(oldAvatarClip == null, Is.True);
            Assert.That(oldAvatarVersionClip == null, Is.True);
        }

        [Test]
        public void EndEffectorSampling_UsesCachedHumanBoneWhenAnimatorAvatarIsTemporarilyNull()
        {
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
                    "KimodoAvatarlessEndEffectorTest",
                    out RetargetSkeleton cache,
                    out error),
                Is.True,
                error);
            try
            {
                float expectedHumanScale = cache.animator.humanScale;
                Assert.That(cache.humanScale, Is.EqualTo(expectedHumanScale).Within(1e-6f));
                BoneSample sample = KimodoRetargetSamplingUtility.CaptureBoneSample(cache);
                cache.animator.avatar = null;
                Assert.That(cache.humanScale, Is.EqualTo(expectedHumanScale).Within(1e-6f));

                Assert.That(
                    KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                        sample,
                        cache,
                        KimodoMotionModelProfiles.DefaultModelName,
                        "left-hand",
                        0.0,
                        out KimodoMarkerSampleResult result,
                        out error),
                    Is.True,
                    error);
                Assert.That(result.characterPose != null, Is.True);
                Assert.That(result.characterPose, Is.Not.Null);
                Assert.That(result.characterPose.TryValidate(out error), Is.True, error);
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void TimelineConstraintCache_InvalidateDestroysOnlyRequestedRange()
        {
            KimodoTimelineConstraintCacheRange firstRange = KimodoTimelineConstraintClipCache.ResolveRange(1.0, 10.0, 60, 30f);
            KimodoTimelineConstraintCacheRange secondRange = KimodoTimelineConstraintClipCache.ResolveRange(5.0, 10.0, 60, 30f);
            var firstKey = new KimodoTimelineConstraintCacheKey(11, 12, 13, firstRange, "model");
            var secondKey = new KimodoTimelineConstraintCacheKey(11, 12, 13, secondRange, "model");
            var firstClip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
            var secondClip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
            FieldInfo field = typeof(KimodoTimelineConstraintClipCache).GetField(
                "Entries",
                BindingFlags.NonPublic | BindingFlags.Static);
            var entries = field.GetValue(null) as System.Collections.IDictionary;
            entries.Add(firstKey, new KimodoTimelineConstraintCacheEntry { Clip = firstClip });
            entries.Add(secondKey, new KimodoTimelineConstraintCacheEntry { Clip = secondClip });

            Assert.That(KimodoTimelineConstraintClipCache.Invalidate(firstKey), Is.True);

            Assert.That(firstClip == null, Is.True);
            Assert.That(secondClip == null, Is.False);
            Assert.That(entries.Contains(secondKey), Is.True);
            KimodoTimelineConstraintClipCache.Clear();
        }

        [Test]
        public void ConstraintPoseRenderSignature_ChangesOnlyWhenRenderedContentChanges()
        {
            var sample = new TimelineInject.KimodoMarkerSampleResult
            {
                characterPose = new KimodoUnityBridge.CharacterPose(),
                constraintType = "fullbody",
                sampleTime = 1.0,
            };
            var item = new PoseCacheRenderItem
            {
                ConstraintType = "fullbody",
                SampleData = sample,
                HighlightJoints = new System.Collections.Generic.List<string> { "Hips" },
                Visible = true
            };

            int first = KimodoConstraintPoseCache.ComputeRenderSignature(item, "model");
            int same = KimodoConstraintPoseCache.ComputeRenderSignature(item, "model");
            sample.characterPose.muscles[0] = 0.3f;
            int changed = KimodoConstraintPoseCache.ComputeRenderSignature(item, "model");

            Assert.That(same, Is.EqualTo(first));
            Assert.That(changed, Is.Not.EqualTo(first));
        }

        [Test]
        public void ConstraintSpaceConverter_MapsAndRoundTripsHumanBonePoint()
        {
            var source = new GameObject("ConstraintSpaceSource");
            var target = new GameObject("ConstraintSpaceTarget");
            try
            {
                source.transform.SetPositionAndRotation(
                    new Vector3(1f, 2f, -3f),
                    Quaternion.Euler(10f, 25f, -5f));
                target.transform.SetPositionAndRotation(
                    new Vector3(-4f, 0.5f, 6f),
                    Quaternion.Euler(-8f, 70f, 12f));
                Vector3 sourcePoint = source.transform.position +
                    source.transform.rotation * new Vector3(0.2f, -0.4f, 0.6f);

                Vector3 targetPoint = KimodoConstraintSpaceConverter.MapPoint(
                    source.transform,
                    sourceHumanScale: 1.5f,
                    target.transform,
                    targetHumanScale: 0.75f,
                    sourcePoint);
                Vector3 roundTrip = KimodoConstraintSpaceConverter.MapPoint(
                    target.transform,
                    sourceHumanScale: 0.75f,
                    source.transform,
                    targetHumanScale: 1.5f,
                    targetPoint);

                Assert.That(
                    Vector3.Distance(
                        targetPoint,
                        target.transform.position +
                            target.transform.rotation * new Vector3(0.1f, -0.2f, 0.3f)),
                    Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(roundTrip, sourcePoint), Is.LessThan(1e-5f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void ConstraintSomaConstraint_RoundTripReportsSkeletonPoseError()
        {
            const string modelName = KimodoMotionModelProfiles.DefaultModelName;
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    modelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    avatar,
                    "KimodoConstraintRoundTrip_Source",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            RetargetSkeleton somaFromConstraint = null;
            RetargetSkeleton somaFromRestoredConstraint = null;
            try
            {
                Assert.That(
                    KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        avatar,
                        "KimodoConstraintRoundTrip_FirstSoma",
                        out somaFromConstraint,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        avatar,
                        "KimodoConstraintRoundTrip_SecondSoma",
                        out somaFromRestoredConstraint,
                        out error),
                    Is.True,
                    error);

                Transform hips = source.animator.GetBoneTransform(HumanBodyBones.Hips);
                Transform leftHand = source.animator.GetBoneTransform(HumanBodyBones.LeftHand);
                Transform rightHand = source.animator.GetBoneTransform(HumanBodyBones.RightHand);
                Transform leftFoot = source.animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                Transform rightFoot = source.animator.GetBoneTransform(HumanBodyBones.RightFoot);
                Assert.That(hips, Is.Not.Null);
                Assert.That(leftHand, Is.Not.Null);
                Assert.That(rightHand, Is.Not.Null);
                Assert.That(leftFoot, Is.Not.Null);
                Assert.That(rightFoot, Is.Not.Null);

                source.skeletonRoot.SetPositionAndRotation(
                    new Vector3(1.25f, 0.2f, -0.75f),
                    Quaternion.Euler(0f, 32f, 0f));
                hips.localRotation *= Quaternion.Euler(7f, 18f, -4f);
                leftHand.localRotation *= Quaternion.Euler(22f, -13f, 31f);
                rightHand.localRotation *= Quaternion.Euler(-17f, 11f, -24f);
                leftFoot.localRotation *= Quaternion.Euler(-16f, 9f, 6f);
                rightFoot.localRotation *= Quaternion.Euler(13f, -8f, -5f);

                BoneSample originalSomaPose = KimodoRetargetSamplingUtility.CaptureBoneSample(source);
                Assert.That(
                    KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                        originalSomaPose,
                        source,
                        modelName,
                        "fullbody",
                        0.0,
                        out KimodoMarkerSampleResult originalConstraint,
                        out error),
                    Is.True,
                    error);
                originalConstraint.mask = new KimodoConstraintMask
                {
                    muscle = true,
                    rootPosition = true,
                    rootHeading = true,
                    leftHand = true,
                    rightHand = true,
                    leftFoot = true,
                    rightFoot = true
                };

                Assert.That(
                    KimodoConstraintSpaceConverter.TryApplyToTargetAvatar(
                        originalConstraint,
                        modelName,
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
                        source,
                        somaFromConstraint,
                        out error),
                    Is.True,
                    error);
                BoneSample firstSomaPose = KimodoRetargetSamplingUtility.CaptureBoneSample(somaFromConstraint);

                Assert.That(
                    KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                        firstSomaPose,
                        somaFromConstraint,
                        modelName,
                        "fullbody",
                        0.0,
                        out KimodoMarkerSampleResult restoredConstraint,
                        out error),
                    Is.True,
                    error);
                restoredConstraint.mask = originalConstraint.mask.Clone();

                Assert.That(
                    KimodoConstraintSpaceConverter.TryApplyToTargetAvatar(
                        restoredConstraint,
                        modelName,
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
                        source,
                        somaFromRestoredConstraint,
                        out error),
                    Is.True,
                    error);
                BoneSample restoredSomaPose = KimodoRetargetSamplingUtility.CaptureBoneSample(somaFromRestoredConstraint);

                MeasureBoneSampleDifference(
                    originalSomaPose,
                    firstSomaPose,
                    out float firstPositionMax,
                    out string firstPositionMaxBone,
                    out float firstPositionMean,
                    out float firstRotationMax,
                    out string firstRotationMaxBone,
                    out float firstRotationMean);
                MeasureBoneSampleDifference(
                    firstSomaPose,
                    restoredSomaPose,
                    out float roundTripPositionMax,
                    out string roundTripPositionMaxBone,
                    out float roundTripPositionMean,
                    out float roundTripRotationMax,
                    out string roundTripRotationMaxBone,
                    out float roundTripRotationMean);
                MeasureBoneSampleDifference(
                    originalSomaPose,
                    restoredSomaPose,
                    out float totalPositionMax,
                    out string totalPositionMaxBone,
                    out float totalPositionMean,
                    out float totalRotationMax,
                    out string totalRotationMaxBone,
                    out float totalRotationMean);

                Debug.Log(
                    $"[Kimodo][ConstraintSomaConstraintRoundTrip] bones={originalSomaPose.boneNames.Length} " +
                    $"firstSoma positionMax={firstPositionMax:F6}m bone={firstPositionMaxBone} positionMean={firstPositionMean:F6}m " +
                    $"rotationMax={firstRotationMax:F4}deg bone={firstRotationMaxBone} rotationMean={firstRotationMean:F4}deg; " +
                    $"somaConstraintSoma positionMax={roundTripPositionMax:F6}m bone={roundTripPositionMaxBone} positionMean={roundTripPositionMean:F6}m " +
                    $"rotationMax={roundTripRotationMax:F4}deg bone={roundTripRotationMaxBone} rotationMean={roundTripRotationMean:F4}deg; " +
                    $"total positionMax={totalPositionMax:F6}m bone={totalPositionMaxBone} positionMean={totalPositionMean:F6}m " +
                    $"rotationMax={totalRotationMax:F4}deg bone={totalRotationMaxBone} rotationMean={totalRotationMean:F4}deg");

                Assert.That(roundTripPositionMax, Is.LessThan(0.01f),
                    $"SOMA pose changed after constraint -> SOMA -> constraint -> SOMA. max={roundTripPositionMax:F6}m");
                Assert.That(roundTripRotationMax, Is.LessThan(5f),
                    $"SOMA pose changed after constraint -> SOMA -> constraint -> SOMA. max={roundTripRotationMax:F4}deg");
            }
            finally
            {
                somaFromRestoredConstraint?.Dispose();
                somaFromConstraint?.Dispose();
                source.Dispose();
            }
        }

        [Test]
        public void ConstraintPoseCache_IsolatesPreviewSessionsByTrack()
        {
            KimodoConstraintPoseCache.DestroyAll();
            try
            {
                var firstContext = new PoseCacheRenderContext(
                    clipId: 1,
                    animatorId: 2,
                    trackId: 3,
                    KimodoMotionModelProfiles.DefaultModelName,
                    KimodoConstraintRigType.Soma77);
                var secondContext = new PoseCacheRenderContext(
                    clipId: 1,
                    animatorId: 2,
                    trackId: 4,
                    KimodoMotionModelProfiles.DefaultModelName,
                    KimodoConstraintRigType.Soma77);

                Assert.That(
                    KimodoConstraintPoseCache.TryGetOrCreateSession(
                        firstContext,
                        out ConstraintPosePreviewSession first,
                        out string firstError),
                    Is.True,
                    firstError);
                Assert.That(
                    KimodoConstraintPoseCache.TryGetOrCreateSession(
                        secondContext,
                        out ConstraintPosePreviewSession second,
                        out string secondError),
                    Is.True,
                    secondError);
                Assert.That(second, Is.Not.SameAs(first));

                first.Dispose();
                Assert.That(first.IsDisposed, Is.True);
                Assert.That(second.IsDisposed, Is.False);
            }
            finally
            {
                KimodoConstraintPoseCache.DestroyAll();
            }
        }

        [Test]
        public void ConstraintPoseCache_ItemCleanupIncludesPrefixedSelectionEntry()
        {
            KimodoConstraintPoseCache.DestroyAll();
            try
            {
                var context = new PoseCacheRenderContext(
                    clipId: 1,
                    animatorId: 2,
                    trackId: 3,
                    KimodoMotionModelProfiles.DefaultModelName,
                    KimodoConstraintRigType.Soma77);
                Assert.That(
                    KimodoConstraintPoseCache.TryGetOrCreateSession(
                        context,
                        out ConstraintPosePreviewSession session,
                        out string error),
                    Is.True,
                    error);
                session.Entries.Add("marker:1", new ConstraintPosePreviewEntry { Key = "marker:1" });
                session.Entries.Add("selection:marker:1", new ConstraintPosePreviewEntry { Key = "selection:marker:1" });
                session.Entries.Add("marker:2", new ConstraintPosePreviewEntry { Key = "marker:2" });

                KimodoConstraintPoseCache.DestroyEntriesForItemId("marker:1");

                Assert.That(session.Entries.ContainsKey("marker:1"), Is.False);
                Assert.That(session.Entries.ContainsKey("selection:marker:1"), Is.False);
                Assert.That(session.Entries.ContainsKey("marker:2"), Is.True);
            }
            finally
            {
                KimodoConstraintPoseCache.DestroyAll();
            }
        }

        [Test]
        public void ConstraintPoseCache_ScopeCleanupPreservesOtherPreviewOwners()
        {
            KimodoConstraintPoseCache.DestroyAll();
            try
            {
                var context = new PoseCacheRenderContext(
                    clipId: 1,
                    animatorId: 2,
                    trackId: 3,
                    KimodoMotionModelProfiles.DefaultModelName,
                    KimodoConstraintRigType.Soma77);
                Assert.That(
                    KimodoConstraintPoseCache.TryGetOrCreateSession(
                        context,
                        out ConstraintPosePreviewSession session,
                        out string error),
                    Is.True,
                    error);
                session.Entries.Add("selection:marker:1", new ConstraintPosePreviewEntry { Key = "selection:marker:1" });
                session.Entries.Add("edit:marker:1", new ConstraintPosePreviewEntry { Key = "edit:marker:1" });

                KimodoConstraintPoseCache.DestroyEntriesInScope(context, "selection:");

                Assert.That(session.Entries.ContainsKey("selection:marker:1"), Is.False);
                Assert.That(session.Entries.ContainsKey("edit:marker:1"), Is.True);
            }
            finally
            {
                KimodoConstraintPoseCache.DestroyAll();
            }
        }

        [Test]
        public void ConstraintPoseCache_RecognizesClipRemovedFromOriginalTrack()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject previewRoot = null;
            ConstraintPosePreviewSession session = null;
            try
            {
                KimodoConstraintPoseCache.DestroyAll();
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                int clipId = KimodoUnityObjectIdUtility.IdHash((UnityEngine.Object)timelineClip.asset);
                int trackId = KimodoUnityObjectIdUtility.IdHash(track);

                Assert.That(KimodoConstraintPoseCache.IsClipStillOnTrack(clipId, trackId), Is.True);

                var context = new PoseCacheRenderContext(
                    clipId,
                    animatorId: 1,
                    trackId,
                    KimodoMotionModelProfiles.DefaultModelName,
                    KimodoConstraintRigType.Soma77);
                Assert.That(
                    KimodoConstraintPoseCache.TryGetOrCreateSession(context, out session, out string error),
                    Is.True,
                    error);
                previewRoot = new GameObject("KimodoDeletedClipPreviewTest");
                session.Entries.Add(
                    "deleted-clip-test",
                    new ConstraintPosePreviewEntry
                    {
                        Key = "deleted-clip-test",
                        Root = previewRoot.transform
                    });

                Assert.That(timeline.DeleteClip(timelineClip), Is.True);
                Assert.That(KimodoConstraintPoseCache.IsClipStillOnTrack(clipId, trackId), Is.False);

                KimodoConstraintPoseCache.DestroyInvalidContexts();

                Assert.That(session.IsDisposed, Is.True);
                Assert.That(session.Entries, Is.Empty);
                Assert.That(previewRoot == null, Is.True);
            }
            finally
            {
                KimodoConstraintPoseCache.DestroyAll();
                if (previewRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(previewRoot);
                }
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ConstraintAnchorHips_IsRebuiltOnTheBoundTargetAvatar()
        {
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
                    "KimodoConstraintAnchorTargetAvatarTest",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            try
            {
                source.skeletonRoot.SetPositionAndRotation(
                    new Vector3(2f, 0f, -3f),
                    Quaternion.Euler(0f, 35f, 0f));
                Assert.That(
                    KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        KimodoMotionModelProfiles.DefaultModelName,
                        source,
                        out string[] jointNames,
                        out int[] parentIndices,
                        out Transform[] joints,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoMarkerSamplingUtility.TrySampleMarkerFromProfileSkeletonRaw(
                        source.animator,
                        source.skeletonRoot,
                        KimodoMotionModelProfiles.DefaultModelName,
                        0.0,
                        "fullbody",
                        jointNames,
                        parentIndices,
                        joints,
                        out TimelineInject.KimodoMarkerSampleResult sample,
                        out error),
                    Is.True,
                    error);

                var context = new PoseCacheRenderContext(
                    1,
                    KimodoUnityObjectIdUtility.IdHash(source.animator),
                    1,
                    KimodoMotionModelProfiles.DefaultModelName,
                    KimodoConstraintRigType.Soma77);
                Assert.That(
                    KimodoConstraintPoseCache.TryResolveTargetHipsPose(
                        context,
                        sample,
                        out Vector3 rebuiltPosition,
                        out Quaternion rebuiltRotation,
                        out error),
                    Is.True,
                    error);

                Transform sourceHips = source.animator.GetBoneTransform(HumanBodyBones.Hips);
                Assert.That(sourceHips, Is.Not.Null);
                string positionDiagnostic =
                    $"sourceHips={sourceHips.position:F9} rebuiltHips={rebuiltPosition:F9} " +
                    $"delta={(rebuiltPosition - sourceHips.position):F9} sourceRoot={source.skeletonRoot.position:F9} " +
                    $"sampleUnityRoot={sample.characterPose.root.t:F9} sampleKimodoRoot={sample.characterPose.root.t:F9}";
                Assert.That(Vector3.Distance(rebuiltPosition, sourceHips.position), Is.LessThan(1e-3f), positionDiagnostic);
                Assert.That(Quaternion.Angle(rebuiltRotation, sourceHips.rotation), Is.LessThan(0.1f));
            }
            finally
            {
                source.Dispose();
            }
        }

        private static bool TryGetIkGoal(
            MuscleSample sample,
            AvatarIKGoal goal,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (sample == null)
            {
                return false;
            }

            switch (goal)
            {
                case AvatarIKGoal.LeftHand:
                    position = sample.leftHandPosition;
                    rotation = sample.leftHandRotation;
                    return true;
                case AvatarIKGoal.RightHand:
                    position = sample.rightHandPosition;
                    rotation = sample.rightHandRotation;
                    return true;
                case AvatarIKGoal.LeftFoot:
                    position = sample.leftFootPosition;
                    rotation = sample.leftFootRotation;
                    return true;
                case AvatarIKGoal.RightFoot:
                    position = sample.rightFootPosition;
                    rotation = sample.rightFootRotation;
                    return true;
                default:
                    return false;
            }
        }

        private static void MeasureBoneSampleDifference(
            BoneSample left,
            BoneSample right,
            out float positionMax,
            out string positionMaxBone,
            out float positionMean,
            out float rotationMax,
            out string rotationMaxBone,
            out float rotationMean)
        {
            Assert.That(left, Is.Not.Null);
            Assert.That(right, Is.Not.Null);
            Assert.That(right.boneNames.Length, Is.EqualTo(left.boneNames.Length));

            positionMax = 0f;
            positionMaxBone = string.Empty;
            positionMean = 0f;
            rotationMax = 0f;
            rotationMaxBone = string.Empty;
            rotationMean = 0f;
            for (int i = 0; i < left.boneNames.Length; i++)
            {
                Assert.That(right.boneNames[i], Is.EqualTo(left.boneNames[i]));
                float positionError = Vector3.Distance(left.localPositions[i], right.localPositions[i]);
                float rotationError = Quaternion.Angle(left.localRotations[i], right.localRotations[i]);
                if (positionError > positionMax)
                {
                    positionMax = positionError;
                    positionMaxBone = left.boneNames[i];
                }
                positionMean += positionError;
                if (rotationError > rotationMax)
                {
                    rotationMax = rotationError;
                    rotationMaxBone = left.boneNames[i];
                }
                rotationMean += rotationError;
            }

            int count = left.boneNames.Length;
            if (count > 0)
            {
                positionMean /= count;
                rotationMean /= count;
            }
        }

        private static bool TrySetIkGoal(
            MuscleSample sample,
            AvatarIKGoal goal,
            Vector3 position,
            Quaternion rotation)
        {
            if (sample == null)
            {
                return false;
            }

            switch (goal)
            {
                case AvatarIKGoal.LeftHand:
                    sample.leftHandPosition = position;
                    sample.leftHandRotation = rotation;
                    return true;
                case AvatarIKGoal.RightHand:
                    sample.rightHandPosition = position;
                    sample.rightHandRotation = rotation;
                    return true;
                case AvatarIKGoal.LeftFoot:
                    sample.leftFootPosition = position;
                    sample.leftFootRotation = rotation;
                    return true;
                case AvatarIKGoal.RightFoot:
                    sample.rightFootPosition = position;
                    sample.rightFootRotation = rotation;
                    return true;
                default:
                    return false;
            }
        }

    }
}

#endif
