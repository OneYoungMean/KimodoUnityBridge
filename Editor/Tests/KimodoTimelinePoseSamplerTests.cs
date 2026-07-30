using System;
using System.Reflection;
using NUnit.Framework;
using TimelineInject;
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
        public void ResolveTimelineSourceAvatar_PrefersClipCustomAvatarWhenBindingAnimatorAvatarIsNull()
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
                    KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(timelineClip, source.animator);

                Assert.That(result.Avatar, Is.SameAs(avatar));
                Assert.That(result.IsHumanoid, Is.True);
                Assert.That(result.Source, Is.EqualTo("Clip"));
                Assert.That(result.Error, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
                source.Dispose();
            }
        }

        [Test]
        public void TimelinePoseSampler_WithNullBindingAvatar_SamplesChangingTimelineMuscles()
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
            var muscleClip = new AnimationClip { frameRate = 30f };
            var directorRoot = new GameObject("KimodoTimelineAvatarlessDirector");
            KimodoTimelinePoseSampler sampler = null;
            try
            {
                const int muscleIndex = 21;
                var poses = new HumanPose[3];
                for (int i = 0; i < poses.Length; i++)
                {
                    poses[i] = new HumanPose
                    {
                        bodyPosition = Vector3.zero,
                        bodyRotation = Quaternion.identity,
                        muscles = new float[HumanTrait.MuscleCount]
                    };
                }
                poses[1].muscles[muscleIndex] = 0.75f;
                var samples = new MuscleSample[poses.Length];
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = new MuscleSample
                    {
                        pose = poses[i],
                        leftFootRotation = Quaternion.identity,
                        rightFootRotation = Quaternion.identity,
                        leftHandRotation = Quaternion.identity,
                        rightHandRotation = Quaternion.identity
                    };
                }
                Assert.That(
                    KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(samples, muscleClip, out error),
                    Is.True,
                    error);

                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<AnimationPlayableAsset>();
                ((AnimationPlayableAsset)timelineClip.asset).clip = muscleClip;
                timelineClip.start = 0.0;
                timelineClip.duration = muscleClip.length;

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
                    ModelName = KimodoPlayableClip.DefaultBridgeModelName,
                    CurrentClip = muscleClip
                };
                Assert.That(
                    KimodoTimelinePoseSampler.TryCreate(
                        context,
                        KimodoPlayableClip.DefaultBridgeModelName,
                        out sampler,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    sampler.TryCaptureMuscleSample(
                        0.0,
                        false,
                        Vector3.zero,
                        Quaternion.identity,
                        out MuscleSample first,
                        out error,
                        logDiagnostics: false),
                    Is.True,
                    error);
                Assert.That(
                    sampler.TryCaptureMuscleSample(
                        1.0 / 30.0,
                        false,
                        Vector3.zero,
                        Quaternion.identity,
                        out MuscleSample second,
                        out error,
                        logDiagnostics: false),
                    Is.True,
                    error);
                Assert.That(
                    Mathf.Abs(second.pose.muscles[muscleIndex] - first.pose.muscles[muscleIndex]),
                    Is.GreaterThan(0.25f));

                sampler.Dispose();
                sampler = null;
                Assert.That(source.animator.avatar, Is.Null);
            }
            finally
            {
                sampler?.Dispose();
                source.Dispose();
                UnityEngine.Object.DestroyImmediate(directorRoot);
                UnityEngine.Object.DestroyImmediate(timeline);
                UnityEngine.Object.DestroyImmediate(muscleClip);
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

                KimodoConstraintPoseCache.SetGroupState(context, visible: true, selectable: true);
                Assert.That((target.hideFlags & HideFlags.NotEditable) == 0, Is.True);
                KimodoConstraintPoseCache.ClearTransformChanges(context, entryId);
                target.transform.position += Vector3.right * 0.05f;
                Assert.That(KimodoConstraintPoseCache.HasAnyTransformChanges(context, entryId), Is.True);

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
        public void TimelineMatchPrevious_UsesHumanoidHipsAsMatchPoint()
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
                    "KimodoTimelineMatchHipsTest",
                    out SkeletonCache cache,
                    out error),
                Is.True,
                error);

            try
            {
                MethodInfo resolve = typeof(KimodoTimelinePreviewRefreshUtility).GetMethod(
                    "ResolveHumanoidHipsMatchPoint",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(resolve, Is.Not.Null);
                Transform hips = resolve.Invoke(null, new object[] { cache.animator.gameObject }) as Transform;
                Assert.That(hips, Is.SameAs(cache.animator.GetBoneTransform(HumanBodyBones.Hips)));
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void ArdyHistoryRange_UsesHalfOpenTimelineSamples()
        {
            double latest = ArdyEditorHistoryEncoder.ResolveLatestHistorySampleTime(2.0, 10.0, 20.0);
            Assert.That(
                latest,
                Is.EqualTo(9.95).Within(1e-9));
            Assert.That(latest - 159.0 / 20.0, Is.EqualTo(2.0).Within(1e-9));
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

        [TestCase(false)]
        [TestCase(true)]
        public void ResetClipOffset_UsesIdentityAndConfiguresStartOffset(bool removeStartOffset)
        {
            var asset = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                asset.position = new Vector3(1f, 2f, 3f);
                asset.rotation = Quaternion.Euler(10f, 20f, 30f);
                MethodInfo reset = typeof(KimodoPlayableClipGenerationHostService).GetMethod(
                    "ResetClipOffset",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(reset, Is.Not.Null);

                reset.Invoke(null, new object[] { asset, removeStartOffset });

                Assert.That(Vector3.Distance(asset.position, Vector3.zero), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(asset.rotation, Quaternion.identity), Is.LessThan(1e-4f));
                Assert.That(asset.removeStartOffset, Is.EqualTo(removeStartOffset));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
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
