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
    public sealed class KimodoTimelinePoseSamplerTests
    {
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
        public void ResolveSourceHumanBone_UsesSourceAvatarWhenAnimatorAvatarIsNull()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);

            HumanBone hips = Array.Find(
                avatar.humanDescription.human,
                bone => bone.humanName == HumanBodyBones.Hips.ToString());
            var root = new GameObject("Root");
            try
            {
                Animator animator = root.AddComponent<Animator>();
                Transform expected = new GameObject(hips.boneName).transform;
                expected.SetParent(root.transform);

                Assert.That(animator.avatar, Is.Null);
                Assert.That(
                    KimodoTimelinePoseSampler.ResolveSourceHumanBone(animator, avatar, HumanBodyBones.Hips),
                    Is.SameAs(expected));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ConstraintPreviewClone_WithResolvedAvatarAndNullBindingAvatar_AppliesAndKeepsSampledPose()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoConstraintAvatarlessBindingTest",
                    out SkeletonCache source,
                    out error),
                Is.True,
                error);

            SkeletonCache expectedTarget = null;
            try
            {
                Assert.That(
                    KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        KimodoPlayableClip.DefaultBridgeModelName,
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
                        KimodoPlayableClip.DefaultBridgeModelName,
                        0.0,
                        "fullbody",
                        jointNames,
                        parentIndices,
                        joints,
                        out KimodoMarkerSampleResult sample,
                        out error),
                    Is.True,
                    error);

                KimodoRetargetClipSamplingUtility.ResetSkeletonCachePose(source);
                Assert.That(
                    KimodoRetargetAvatarUtility.TryApplyMarkerSampleToTransformMap(
                        sample,
                        KimodoPlayableClip.DefaultBridgeModelName,
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
                    KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        avatar,
                        "KimodoConstraintAvatarlessExpectedTarget",
                        out expectedTarget,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        profileSample,
                        KimodoPlayableClip.FIXED_FRAME_RATE,
                        expectedTarget,
                        out BoneSample expectedBoneSample,
                        out _,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetSamplingUtility.TryApplyBoneSampleToSkeletonCache(
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
                    source.animator.GetInstanceID(),
                    1,
                    KimodoPlayableClip.DefaultBridgeModelName,
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
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoConstraintCustomAvatarTest",
                    out SkeletonCache source,
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
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoTrackFirstAvatarTest",
                    out SkeletonCache source,
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
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoBatchRetargetTest",
                    out SkeletonCache cache,
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
                secondPose.muscles[0] = Mathf.Clamp(firstPose.muscles[0] + 0.35f, -1f, 1f);
                MuscleSample[] sourceSamples =
                {
                    KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, firstPose),
                    KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, secondPose)
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
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void TimelinePoseSampler_WithNullBindingAvatar_SamplesChangingBoneClipSpineMuscle()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoTimelineAvatarlessSource",
                    out SkeletonCache source,
                    out error),
                Is.True,
                error);

            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var boneClip = new AnimationClip { frameRate = 30f };
            var directorRoot = new GameObject("KimodoTimelineAvatarlessDirector");
            KimodoTimelinePoseSampler sampler = null;
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
                    ModelName = KimodoPlayableClip.DefaultBridgeModelName
                };
                Assert.That(
                    KimodoTimelinePoseSampler.TryCreate(
                        context,
                        KimodoPlayableClip.DefaultBridgeModelName,
                        out sampler,
                        out error),
                    Is.True,
                    error);
                Assert.That(source.animator.avatar, Is.Null, "Timeline sampling must not mutate the binding Animator Avatar.");
                var sourceIntermediate = (SkeletonCache)typeof(KimodoTimelinePoseSampler)
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
                Transform sourceHips = KimodoTimelinePoseSampler.ResolveSourceHumanBone(
                    source.animator,
                    avatar,
                    HumanBodyBones.Hips);
                Assert.That(sourceHips, Is.Not.Null);
                Vector3 firstHipsPosition = sourceHips.position;
                Quaternion firstHipsRotation = sourceHips.rotation;
                Vector3 expectedBodyPosition = track.position + track.rotation * baselineBodyPosition;
                Quaternion expectedBodyRotation = track.rotation * baselineBodyRotation;
                Assert.That(
                    Vector3.Distance(first.pose.bodyPosition * sampler.SourceHumanScale, expectedBodyPosition),
                    Is.LessThan(1e-3f));
                Assert.That(Quaternion.Angle(first.pose.bodyRotation, expectedBodyRotation), Is.LessThan(0.1f));
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
                Vector3 secondHipsPosition = sourceHips.position;
                Quaternion secondHipsRotation = sourceHips.rotation;
                Assert.That(Vector3.Distance(firstHipsPosition, secondHipsPosition), Is.GreaterThan(0.1f));
                Assert.That(
                    sampler.TryGetSourceHipsPose(
                        0.0,
                        out Vector3 sampledFirstHipsPosition,
                        out Quaternion sampledFirstHipsRotation,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    sampler.TryGetSourceHipsPose(
                        1.0 / 30.0,
                        out Vector3 sampledSecondHipsPosition,
                        out Quaternion sampledSecondHipsRotation,
                        out error),
                    Is.True,
                    error);
                Assert.That(Vector3.Distance(sampledFirstHipsPosition, firstHipsPosition), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(sampledSecondHipsPosition, secondHipsPosition), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(sampledFirstHipsRotation, firstHipsRotation), Is.LessThan(1e-3f));
                Assert.That(Quaternion.Angle(sampledSecondHipsRotation, secondHipsRotation), Is.LessThan(1e-3f));

                sampler.Dispose();
                sampler = null;
                Assert.That(sourceIntermediate.root, Is.Null, "The virtual source skeleton must be disposed with the sampler.");
                Assert.That(source.animator.avatar, Is.Null);
                Assert.That(director.GetGenericBinding(track), Is.SameAs(source.animator));
            }
            finally
            {
                sampler?.Dispose();
                source.Dispose();
                UnityEngine.Object.DestroyImmediate(directorRoot);
                UnityEngine.Object.DestroyImmediate(timeline);
                UnityEngine.Object.DestroyImmediate(boneClip);
            }
        }

        [Test]
        public void EndConstraintTarget_IsPointOneMetersAndEditableOnlyDuringEdit()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoEndConstraintTargetTest",
                    out SkeletonCache source,
                    out error),
                Is.True,
                error);

            var context = new PoseCacheRenderContext(
                101,
                source.animator.GetInstanceID(),
                102,
                KimodoPlayableClip.DefaultBridgeModelName,
                KimodoConstraintRigType.Soma77,
                avatar);
            const string entryId = "end-target-test";
            try
            {
                KimodoConstraintPoseCache.DestroyAll();
                KimodoMarkerSampleResult sample = KimodoMarkerSamplingUtility.CreateDefaultMarkerSample(
                    KimodoPlayableClip.DefaultBridgeModelName,
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
                Assert.That(target.transform.lossyScale.x, Is.EqualTo(0.1f).Within(1e-4f));
                Assert.That(target.transform.lossyScale.y, Is.EqualTo(0.1f).Within(1e-4f));
                Assert.That(target.transform.lossyScale.z, Is.EqualTo(0.1f).Within(1e-4f));
                Assert.That((target.hideFlags & HideFlags.NotEditable) != 0, Is.True);
                Assert.That(target.transform.parent, Is.Not.Null);
                Assert.That(target.transform.localPosition, Is.EqualTo(Vector3.zero));

                KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: true);
                Assert.That((target.hideFlags & HideFlags.NotEditable) == 0, Is.True);
                KimodoConstraintPoseCache.ClearTransformChanges(context, entryId);
                target.transform.position += Vector3.right * 0.05f;
                Assert.That(KimodoConstraintPoseCache.HasAnyTransformChanges(context, entryId), Is.True);

                Vector3 draggedWorldPosition = target.transform.position;
                Assert.That(
                    KimodoConstraintPoseCache.TryBuildSampleFromContext(
                        context,
                        entryId,
                        "left-hand",
                        0.0,
                        out KimodoMarkerSampleResult draggedSample,
                        out error),
                    Is.True,
                    error);
                Assert.That(draggedSample.hasEndEffectorTargetPosition, Is.True);
                Vector3 rootAxisAngle = draggedSample.localAxisAngles[0];
                Quaternion rootRotation = rootAxisAngle.sqrMagnitude > 1e-12f
                    ? Quaternion.AngleAxis(
                        rootAxisAngle.magnitude * Mathf.Rad2Deg,
                        rootAxisAngle.normalized)
                    : Quaternion.identity;
                Vector3 rebuiltWorldPosition = draggedSample.kimodoRootPosition +
                    rootRotation * draggedSample.endEffectorTargetPositionRootLocal;
                Assert.That(Vector3.Distance(rebuiltWorldPosition, draggedWorldPosition), Is.LessThan(1e-4f));

                Assert.That(
                    KimodoConstraintPoseCache.TryUpdateEndEffectorTarget(
                        context,
                        entryId,
                        "left-hand",
                        draggedSample),
                    Is.True);
                Assert.That(
                    KimodoConstraintPoseCache.TryGetEndEffectorTarget(context, entryId, out GameObject rebuiltTarget),
                    Is.True);
                Assert.That(rebuiltTarget, Is.SameAs(target));
                Assert.That(Vector3.Distance(rebuiltTarget.transform.position, draggedWorldPosition), Is.LessThan(1e-4f));

                KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: false);
                Assert.That((target.hideFlags & HideFlags.NotEditable) != 0, Is.True);
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
        public void GeneratedWriteback_PreservesClipOffsetExceptForContinuousCopy()
        {
            var source = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            var destination = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                MethodInfo apply = typeof(KimodoPlayableClipGenerationHostService).GetMethod(
                    "ApplyTimelineOffsets",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(apply, Is.Not.Null);

                Vector3 originalPosition = new Vector3(1f, 2f, 3f);
                Quaternion originalRotation = Quaternion.Euler(10f, 20f, 30f);
                destination.position = originalPosition;
                destination.rotation = originalRotation;
                destination.removeStartOffset = true;

                apply.Invoke(null, new object[] { destination, new KimodoEditorGenerateRequest() });

                Assert.That(Vector3.Distance(destination.position, originalPosition), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(destination.rotation, originalRotation), Is.LessThan(1e-4f));
                Assert.That(destination.removeStartOffset, Is.True);

                source.position = new Vector3(-4f, 5f, 6f);
                source.rotation = Quaternion.Euler(-15f, 40f, 5f);
                source.removeStartOffset = false;
                apply.Invoke(
                    null,
                    new object[]
                    {
                        destination,
                        new KimodoEditorGenerateRequest { AnchorOffsetSourceClip = source }
                    });

                Assert.That(Vector3.Distance(destination.position, source.position), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(destination.rotation, source.rotation), Is.LessThan(1e-4f));
                Assert.That(destination.removeStartOffset, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(destination);
            }
        }

        [Test]
        public void GeneratedWriteback_AppliesNormalizedAnchorRelativeToTrackOffset()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(10f, 1f, 20f);
                track.rotation = Quaternion.Euler(0f, 30f, 0f);
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                var playable = (KimodoPlayableClip)timelineClip.asset;
                playable.position = new Vector3(0f, 4f, 0f);
                playable.removeStartOffset = true;

                Vector3 expectedLocalPosition = new Vector3(2f, 0f, 3f);
                Quaternion anchorRotation = Quaternion.Euler(0f, 70f, 0f);
                Vector3 anchorPosition = track.position + track.rotation * expectedLocalPosition;
                var request = new KimodoEditorGenerateRequest
                {
                    NormalizationInfo = new KimodoConstraintNormalizationInfo
                    {
                        Applied = true,
                        AnchorKind = KimodoConstraintNormalizationAnchorKind.FullBody,
                        AnchorSample = new KimodoMarkerSampleResult
                        {
                            constraintType = "fullbody",
                            kimodoRootPosition = Vector3.zero,
                            unityRootPos = anchorPosition,
                            unityRootRot = anchorRotation,
                            localAxisAngles = new System.Collections.Generic.List<Vector3>
                            {
                                KimodoRuntimeUtility.QuaternionToAxisAngleVector(Quaternion.identity)
                            }
                        }
                    },
                    TimelineClipSnapshot = timelineClip
                };
                MethodInfo apply = typeof(KimodoPlayableClipGenerationHostService).GetMethod(
                    "ApplyTimelineOffsets",
                    BindingFlags.NonPublic | BindingFlags.Static);

                Assert.That(apply, Is.Not.Null);
                apply.Invoke(null, new object[] { playable, request });

                Assert.That(playable.position.x, Is.EqualTo(expectedLocalPosition.x).Within(1e-5f));
                Assert.That(playable.position.y, Is.EqualTo(4f).Within(1e-5f));
                Assert.That(playable.position.z, Is.EqualTo(expectedLocalPosition.z).Within(1e-5f));
                Assert.That(Quaternion.Angle(playable.rotation, Quaternion.Euler(0f, 40f, 0f)), Is.LessThan(1e-4f));
                Assert.That(playable.removeStartOffset, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void GeneratedWriteback_NormalizedConstraintAnchorOverridesArdyHistoryAnchor()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(10f, 1f, 20f);
                track.rotation = Quaternion.Euler(0f, 30f, 0f);
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                var playable = (KimodoPlayableClip)timelineClip.asset;
                playable.position = new Vector3(0f, 4f, 0f);

                var request = new KimodoEditorGenerateRequest
                {
                    NormalizationInfo = new KimodoConstraintNormalizationInfo
                    {
                        Applied = true,
                        AnchorKind = KimodoConstraintNormalizationAnchorKind.FullBody,
                        AnchorSample = new KimodoMarkerSampleResult
                        {
                            kimodoRootPosition = Vector3.zero,
                            unityRootPos = new Vector3(4f, 0f, 6f),
                            unityRootRot = Quaternion.Euler(0f, 70f, 0f),
                            localAxisAngles = new System.Collections.Generic.List<Vector3>
                            {
                                KimodoRuntimeUtility.QuaternionToAxisAngleVector(Quaternion.identity)
                            }
                        }
                    },
                    InitialArdyHistorySource = new ArdyEditorHistorySource
                    {
                        HasTimelineWorldAnchor = true,
                        TimelineWorldAnchorPosition = new Vector3(100f, 0f, 200f),
                        TimelineWorldAnchorRotation = Quaternion.Euler(0f, 150f, 0f)
                    },
                    TimelineClipSnapshot = timelineClip
                };
                MethodInfo apply = typeof(KimodoPlayableClipGenerationHostService).GetMethod(
                    "ApplyTimelineOffsets",
                    BindingFlags.NonPublic | BindingFlags.Static);

                Assert.That(apply, Is.Not.Null);
                apply.Invoke(null, new object[] { playable, request });

                Vector3 expectedLocal = Quaternion.Inverse(track.rotation) *
                    (new Vector3(4f, 0f, 6f) - new Vector3(10f, 0f, 20f));
                Assert.That(Vector3.Distance(playable.position, new Vector3(expectedLocal.x, 4f, expectedLocal.z)), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(playable.rotation, Quaternion.Euler(0f, 40f, 0f)), Is.LessThan(1e-4f));
                Assert.That(playable.removeStartOffset, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void GeneratedWriteback_ArdyHistoryWorldAnchorUsesHipsWhenNoConstraintAnchor()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationClip generatedClip = null;
            SkeletonCache cache = null;
            try
            {
                Assert.That(
                    KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                        KimodoPlayableClip.DefaultBridgeModelName,
                        out Avatar avatar,
                        out string error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        avatar,
                        "KimodoGeneratedHipsOffsetTest",
                        out cache,
                        out error),
                    Is.True,
                    error);
                Assert.That(cache.humanBoneTransforms.TryGetValue(HumanBodyBones.Hips, out Transform hips), Is.True);
                string hipsPath = AnimationUtility.CalculateTransformPath(hips, cache.skeletonRoot);
                generatedClip = new AnimationClip { frameRate = 30f };
                Vector3 generatedHipsPosition = new Vector3(1f, 2f, 3f);
                Quaternion generatedHipsRotation = Quaternion.Euler(0f, 15f, 0f);
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalPosition.x"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsPosition.x), new Keyframe(1f / 30f, generatedHipsPosition.x)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalPosition.y"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsPosition.y), new Keyframe(1f / 30f, generatedHipsPosition.y)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalPosition.z"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsPosition.z), new Keyframe(1f / 30f, generatedHipsPosition.z)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalRotation.x"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsRotation.x), new Keyframe(1f / 30f, generatedHipsRotation.x)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalRotation.y"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsRotation.y), new Keyframe(1f / 30f, generatedHipsRotation.y)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalRotation.z"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsRotation.z), new Keyframe(1f / 30f, generatedHipsRotation.z)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalRotation.w"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsRotation.w), new Keyframe(1f / 30f, generatedHipsRotation.w)));

                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(10f, 4f, 20f);
                track.rotation = Quaternion.Euler(0f, 30f, 0f);
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                var playable = (KimodoPlayableClip)timelineClip.asset;
                playable.clip = generatedClip;
                playable.position = new Vector3(0f, -9f, 0f);

                Vector3 sourceHipsLocal = new Vector3(5f, 6f, 7f);
                Quaternion sourceHipsLocalYaw = Quaternion.Euler(0f, 70f, 0f);
                Quaternion worldAnchorRotation = track.rotation * sourceHipsLocalYaw;
                Vector3 worldAnchorPosition = track.position + track.rotation * sourceHipsLocal;
                Quaternion expectedClipRotation = (sourceHipsLocalYaw * Quaternion.Inverse(generatedHipsRotation)).normalized;
                Vector3 expectedPlanarPosition = sourceHipsLocal -
                    (expectedClipRotation * new Vector3(generatedHipsPosition.x, 0f, generatedHipsPosition.z));
                Vector3 expectedLocalPosition = new Vector3(
                    expectedPlanarPosition.x,
                    playable.position.y,
                    expectedPlanarPosition.z);
                var request = new KimodoEditorGenerateRequest
                {
                    InitialArdyHistorySource = new ArdyEditorHistorySource
                    {
                        HasTimelineWorldAnchor = true,
                        TimelineWorldAnchorPosition = worldAnchorPosition,
                        TimelineWorldAnchorRotation = worldAnchorRotation
                    },
                    OutputPlan = new KimodoEditorGenerateOutputPlan
                    {
                        TargetRetargetAvatar = avatar
                    },
                    TargetClip = generatedClip,
                    TimelineClipSnapshot = timelineClip
                };
                MethodInfo apply = typeof(KimodoPlayableClipGenerationHostService).GetMethod(
                    "ApplyTimelineOffsets",
                    BindingFlags.NonPublic | BindingFlags.Static);

                Assert.That(apply, Is.Not.Null);
                apply.Invoke(null, new object[] { playable, request });

                Assert.That(Vector3.Distance(playable.position, expectedLocalPosition), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(playable.rotation, expectedClipRotation), Is.LessThan(1e-4f));
                Assert.That(playable.removeStartOffset, Is.False);
            }
            finally
            {
                cache?.Dispose();
                if (generatedClip != null)
                {
                    UnityEngine.Object.DestroyImmediate(generatedClip);
                }
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void GeneratedWriteback_NormalizedConstraintAnchorUsesHipsWhenAvailable()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationClip generatedClip = null;
            SkeletonCache cache = null;
            try
            {
                Assert.That(
                    KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                        KimodoPlayableClip.DefaultBridgeModelName,
                        out Avatar avatar,
                        out string error),
                    Is.True,
                    error);
                Assert.That(
                    KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                        avatar,
                        "KimodoNormalizedHipsOffsetTest",
                        out cache,
                        out error),
                    Is.True,
                    error);
                Assert.That(cache.humanBoneTransforms.TryGetValue(HumanBodyBones.Hips, out Transform hips), Is.True);
                string hipsPath = AnimationUtility.CalculateTransformPath(hips, cache.skeletonRoot);
                generatedClip = new AnimationClip { frameRate = 30f };
                Vector3 generatedHipsPosition = new Vector3(1f, 2f, 3f);
                Quaternion generatedHipsRotation = Quaternion.Euler(0f, 15f, 0f);
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalPosition.x"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsPosition.x), new Keyframe(1f / 30f, generatedHipsPosition.x)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalPosition.y"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsPosition.y), new Keyframe(1f / 30f, generatedHipsPosition.y)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalPosition.z"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsPosition.z), new Keyframe(1f / 30f, generatedHipsPosition.z)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalRotation.x"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsRotation.x), new Keyframe(1f / 30f, generatedHipsRotation.x)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalRotation.y"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsRotation.y), new Keyframe(1f / 30f, generatedHipsRotation.y)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalRotation.z"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsRotation.z), new Keyframe(1f / 30f, generatedHipsRotation.z)));
                AnimationUtility.SetEditorCurve(
                    generatedClip,
                    EditorCurveBinding.FloatCurve(hipsPath, typeof(Transform), "m_LocalRotation.w"),
                    new AnimationCurve(new Keyframe(0f, generatedHipsRotation.w), new Keyframe(1f / 30f, generatedHipsRotation.w)));

                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(10f, 4f, 20f);
                track.rotation = Quaternion.Euler(0f, 30f, 0f);
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                var playable = (KimodoPlayableClip)timelineClip.asset;
                playable.clip = generatedClip;
                playable.position = new Vector3(0f, -9f, 0f);

                Vector3 sourceHipsLocal = new Vector3(5f, 6f, 7f);
                Quaternion sourceHipsLocalYaw = Quaternion.Euler(0f, 70f, 0f);
                Quaternion worldAnchorRotation = track.rotation * sourceHipsLocalYaw;
                Vector3 worldAnchorPosition = track.position + track.rotation * sourceHipsLocal;
                Quaternion expectedClipRotation = (sourceHipsLocalYaw * Quaternion.Inverse(generatedHipsRotation)).normalized;
                Vector3 expectedPlanarPosition = sourceHipsLocal -
                    (expectedClipRotation * new Vector3(generatedHipsPosition.x, 0f, generatedHipsPosition.z));
                Vector3 expectedLocalPosition = new Vector3(
                    expectedPlanarPosition.x,
                    playable.position.y,
                    expectedPlanarPosition.z);
                var request = new KimodoEditorGenerateRequest
                {
                    NormalizationInfo = new KimodoConstraintNormalizationInfo
                    {
                        Applied = true,
                        AnchorKind = KimodoConstraintNormalizationAnchorKind.FullBody,
                        AnchorSample = new KimodoMarkerSampleResult
                        {
                            constraintType = "fullbody",
                            unityRootPos = Vector3.one * 999f,
                            unityRootRot = Quaternion.identity,
                            hasUnityHipsPose = true,
                            unityHipsPos = worldAnchorPosition,
                            unityHipsRot = worldAnchorRotation
                        }
                    },
                    OutputPlan = new KimodoEditorGenerateOutputPlan
                    {
                        TargetRetargetAvatar = avatar
                    },
                    TargetClip = generatedClip,
                    TimelineClipSnapshot = timelineClip
                };
                MethodInfo apply = typeof(KimodoPlayableClipGenerationHostService).GetMethod(
                    "ApplyTimelineOffsets",
                    BindingFlags.NonPublic | BindingFlags.Static);

                Assert.That(apply, Is.Not.Null);
                apply.Invoke(null, new object[] { playable, request });

                Assert.That(Vector3.Distance(playable.position, expectedLocalPosition), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(playable.rotation, expectedClipRotation), Is.LessThan(1e-4f));
                Assert.That(playable.removeStartOffset, Is.False);
            }
            finally
            {
                cache?.Dispose();
                if (generatedClip != null)
                {
                    UnityEngine.Object.DestroyImmediate(generatedClip);
                }
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void GeneratedWriteback_UsesSampledUnityAnchorWorldSpace()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(10f, 0f, 20f);
                track.rotation = Quaternion.Euler(0f, 30f, 0f);
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                var playable = (KimodoPlayableClip)timelineClip.asset;
                playable.position = Vector3.zero;

                var request = new KimodoEditorGenerateRequest
                {
                    NormalizationInfo = new KimodoConstraintNormalizationInfo
                    {
                        Applied = true,
                        AnchorKind = KimodoConstraintNormalizationAnchorKind.FullBody,
                        AnchorSample = new KimodoMarkerSampleResult
                        {
                            constraintType = "fullbody",
                            kimodoRootPosition = new Vector3(4f, 0f, 6f),
                            unityRootPos = new Vector3(100f, 0f, 200f),
                            unityRootRot = Quaternion.Euler(0f, 80f, 0f),
                            localAxisAngles = new System.Collections.Generic.List<Vector3>
                            {
                                KimodoRuntimeUtility.QuaternionToAxisAngleVector(Quaternion.Euler(0f, 70f, 0f))
                            }
                        }
                    },
                    TimelineClipSnapshot = timelineClip
                };
                MethodInfo apply = typeof(KimodoPlayableClipGenerationHostService).GetMethod(
                    "ApplyTimelineOffsets",
                    BindingFlags.NonPublic | BindingFlags.Static);

                apply.Invoke(null, new object[] { playable, request });

                Vector3 expectedLocal = Quaternion.Inverse(track.rotation) *
                    (new Vector3(100f, 0f, 200f) - new Vector3(10f, 0f, 20f));
                Assert.That(Vector3.Distance(playable.position, expectedLocal), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(playable.rotation, Quaternion.Euler(0f, 50f, 0f)), Is.LessThan(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void HumanoidIkGoals_AreInvariantToSkeletonRootWorldPose()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoTimelineFootIkRootSpaceTest",
                    out SkeletonCache cache,
                    out error),
                Is.True,
                error);

            try
            {
                var pose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref pose);
                MuscleSample before = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, pose);

                cache.skeletonRoot.SetPositionAndRotation(
                    new Vector3(7f, 2f, -3f),
                    Quaternion.Euler(0f, 73f, 0f));
                cache.poseHandler.GetHumanPose(ref pose);
                MuscleSample after = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, pose);

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
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoTimelineSingleFrameFootIkTest",
                    out SkeletonCache cache,
                    out error),
                Is.True,
                error);

            try
            {
                var pose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref pose);
                MuscleSample source = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, pose);
                Vector3 rootOffset = new Vector3(0.25f, 0f, -0.4f);
                source.pose.bodyPosition += rootOffset / cache.humanScale;
                HumanPose directPose = source.pose;
                cache.poseHandler.SetHumanPose(ref directPose);
                Assert.That(
                    KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        KimodoPlayableClip.DefaultBridgeModelName,
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
                        KimodoPlayableClip.FIXED_FRAME_RATE,
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
                    KimodoRetargetSamplingUtility.TryApplyBoneSampleToSkeletonCache(
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
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoTimelineHumanPoseRootTest",
                    out SkeletonCache cache,
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
                MuscleSample sample = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, pose);

                cache.skeletonRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                HumanPose directPose = sample.pose;
                cache.poseHandler.SetHumanPose(ref directPose);
                Assert.That(
                    KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        KimodoPlayableClip.DefaultBridgeModelName,
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
                        KimodoPlayableClip.FIXED_FRAME_RATE,
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
                KimodoRetargetClipSamplingUtility.DestroyClipSamplingContext(graph);
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
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoTimelineOffsetPlayableTest",
                    out SkeletonCache cache,
                    out error),
                Is.True,
                error);
            Assert.That(
                KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    cache,
                    out _,
                    out _,
                    out Transform[] joints,
                    out error),
                Is.True,
                error);
            Transform profileRoot = joints[0];

            var clip = new AnimationClip { frameRate = KimodoPlayableClip.FIXED_FRAME_RATE };
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
                            KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, sourcePose),
                            KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, sourcePose)
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
                graph.Evaluate(1f / KimodoPlayableClip.FIXED_FRAME_RATE);

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
                KimodoFullBodyConstraintMarker marker = track.CreateMarker<KimodoFullBodyConstraintMarker>(2.0);

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
                KimodoFullBodyConstraintMarker marker = track.CreateMarker<KimodoFullBodyConstraintMarker>(2.0);

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
                KimodoFullBodyConstraintMarker marker = track.CreateMarker<KimodoFullBodyConstraintMarker>(5.9);

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
            entries.Add(secondKey, new KimodoTimelineConstraintCacheEntry { Clip = secondClip });
            Assert.That(entries.Count, Is.EqualTo(2));

            KimodoTimelineConstraintClipCache.Clear();

            Assert.That(firstClip == null, Is.True);
            Assert.That(secondClip == null, Is.True);
            Assert.That(entries.Count, Is.Zero);
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
                constraintType = "fullbody",
                sampleTime = 1.0,
                kimodoRootPosition = new Vector3(1f, 2f, 3f),
                unityRootPos = new Vector3(4f, 5f, 6f),
                unityRootRot = Quaternion.Euler(0f, 30f, 0f),
                jointNames = new System.Collections.Generic.List<string> { "Hips" },
                localAxisAngles = new System.Collections.Generic.List<Vector3> { new Vector3(0f, 0.2f, 0f) }
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
            sample.localAxisAngles[0] = new Vector3(0f, 0.3f, 0f);
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
        public void ConstraintPoseCache_RecognizesClipRemovedFromOriginalTrack()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject previewRoot = null;
            try
            {
                KimodoConstraintPoseCache.DestroyAll();
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                int clipId = ((UnityEngine.Object)timelineClip.asset).GetInstanceID();
                int trackId = track.GetInstanceID();

                Assert.That(KimodoConstraintPoseCache.IsClipStillOnTrack(clipId, trackId), Is.True);

                Type entryType = typeof(KimodoConstraintPoseCache).GetNestedType(
                    "PoseCacheEntry",
                    BindingFlags.NonPublic);
                object entry = Activator.CreateInstance(entryType, nonPublic: true);
                previewRoot = new GameObject("KimodoDeletedClipPreviewTest");
                entryType.GetField("Key").SetValue(entry, "deleted-clip-test");
                entryType.GetField("ContextKey").SetValue(entry, "deleted-clip-context");
                entryType.GetField("ClipId").SetValue(entry, clipId);
                entryType.GetField("TrackId").SetValue(entry, trackId);
                entryType.GetField("Root").SetValue(entry, previewRoot.transform);
                FieldInfo entriesField = typeof(KimodoConstraintPoseCache).GetField(
                    "Entries",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var entries = entriesField.GetValue(null) as System.Collections.IDictionary;
                entries.Add("deleted-clip-test", entry);

                Assert.That(timeline.DeleteClip(timelineClip), Is.True);
                Assert.That(KimodoConstraintPoseCache.IsClipStillOnTrack(clipId, trackId), Is.False);

                typeof(KimodoConstraintPoseCache).GetMethod(
                        "DestroyInvalidContexts",
                        BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, null);

                Assert.That(entries.Count, Is.EqualTo(0));
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
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoConstraintAnchorTargetAvatarTest",
                    out SkeletonCache source,
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
                        KimodoPlayableClip.DefaultBridgeModelName,
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
                        KimodoPlayableClip.DefaultBridgeModelName,
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
                    source.animator.GetInstanceID(),
                    1,
                    KimodoPlayableClip.DefaultBridgeModelName,
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
                    $"sampleUnityRoot={sample.unityRootPos:F9} sampleKimodoRoot={sample.kimodoRootPosition:F9}";
                Assert.That(Vector3.Distance(rebuiltPosition, sourceHips.position), Is.LessThan(1e-3f), positionDiagnostic);
                Assert.That(Quaternion.Angle(rebuiltRotation, sourceHips.rotation), Is.LessThan(0.1f));
            }
            finally
            {
                source.Dispose();
            }
        }

        [Test]
        public void WorldConstraint_RoundTripsThroughKimodoAndTrackSpaces()
        {
            Vector3 trackPosition = new Vector3(-1f, 0f, 2f);
            Quaternion trackRotation = Quaternion.Euler(0f, 35f, 0f);
            Vector3 secondKimodoPosition = new Vector3(2f, 1f, 0.5f);
            Quaternion secondKimodoRotation = Quaternion.Euler(0f, 20f, 0f);
            Vector3 secondWorldPosition = trackPosition + trackRotation * secondKimodoPosition;
            Quaternion secondWorldRotation = trackRotation * secondKimodoRotation;
            var samples = new System.Collections.Generic.List<TimelineInject.KimodoMarkerSampleResult>
            {
                new TimelineInject.KimodoMarkerSampleResult
                {
                    constraintType = "fullbody",
                    sampleTime = 0.0,
                    kimodoRootPosition = trackPosition + Vector3.up,
                    unityRootPos = trackPosition,
                    unityRootRot = trackRotation,
                    hasRootHeading = true,
                    rootHeading = new Vector2(
                        (trackRotation * Vector3.forward).x,
                        (trackRotation * Vector3.forward).z),
                    localAxisAngles = new System.Collections.Generic.List<Vector3>
                    {
                        KimodoRuntimeUtility.QuaternionToAxisAngleVector(trackRotation)
                    }
                },
                new TimelineInject.KimodoMarkerSampleResult
                {
                    constraintType = "fullbody",
                    sampleTime = 1.0,
                    kimodoRootPosition = secondWorldPosition,
                    unityRootPos = secondWorldPosition,
                    unityRootRot = secondWorldRotation,
                    hasRootHeading = true,
                    rootHeading = new Vector2(
                        (secondWorldRotation * Vector3.forward).x,
                        (secondWorldRotation * Vector3.forward).z),
                    localAxisAngles = new System.Collections.Generic.List<Vector3>
                    {
                        KimodoRuntimeUtility.QuaternionToAxisAngleVector(secondWorldRotation)
                    }
                }
            };

            KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                samples,
                out KimodoConstraintNormalizationInfo normalization,
                out string warning);

            Assert.That(warning, Is.Empty);
            Assert.That(normalization.Applied, Is.True);
            Assert.That(Vector3.Distance(normalization.AnchorSample.unityRootPos, trackPosition), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(samples[0].kimodoRootPosition, Vector3.up), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(samples[1].kimodoRootPosition, secondKimodoPosition), Is.LessThan(1e-5f));

            Assert.That(
                Vector3.Distance(
                    trackPosition + trackRotation * secondKimodoPosition,
                    secondWorldPosition),
                Is.LessThan(1e-5f));
        }
    }
}
