using System;
using System.Reflection;
using KimodoUnityBridge.Command;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintPoseRigFactoryTests
    {
        [Test]
        public void CaptureWorldTargets_ReportsValidityWithoutEnablingChannels()
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
                    "KimodoCaptureAvailabilityTest",
                    out RetargetSkeleton skeleton,
                    out error),
                Is.True,
                error);

            try
            {
                var sample = new KimodoMarkerSampleResult();

                KimodoRetargetMarkerSamplingUtility.CaptureWorldTargets(skeleton, sample);

                Assert.That(sample.validMask.rootPosition, Is.True);
                Assert.That(sample.validMask.rootHeading, Is.True);
                Assert.That(sample.validMask.leftHand, Is.True);
                Assert.That(sample.validMask.rightHand, Is.True);
                Assert.That(sample.validMask.leftFoot, Is.True);
                Assert.That(sample.validMask.rightFoot, Is.True);
                Assert.That(sample.enableMask.IsEmpty, Is.True);
            }
            finally
            {
                skeleton.Dispose();
            }
        }

        [Test]
        public void CaptureWorldTargets_DoesNotAdvertiseMissingSkeletonChannels()
        {
            var sample = new KimodoMarkerSampleResult
            {
                enableMask = new KimodoConstraintMask(),
                validMask = new KimodoConstraintMask
                {
                    rootPosition = true,
                    rootHeading = true,
                    leftHand = true,
                    rightHand = true,
                    leftFoot = true,
                    rightFoot = true
                }
            };

            KimodoRetargetMarkerSamplingUtility.CaptureWorldTargets(null, sample);

            Assert.That(sample.validMask.rootPosition, Is.False);
            Assert.That(sample.validMask.rootHeading, Is.False);
            Assert.That(sample.validMask.AnyEndEffector, Is.False);
        }

        [Test]
        public void ModelNativeConstraintPipeline_AppliesExplicitRootAndCommandMask()
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
                    "KimodoCommandConstraintPipelineTest",
                    out RetargetSkeleton skeleton,
                    out error),
                Is.True,
                error);

            try
            {
                Assert.That(
                    KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        skeleton,
                        out MuscleSample pose,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    skeleton.GetBonePose(
                        HumanBodyBones.Hips,
                        out Vector3 hipsPosition,
                        out Quaternion hipsRotation),
                    Is.True);
                Vector3 expectedRoot = hipsPosition + new Vector3(1.5f, 2f, -0.75f);
                var source = new KimodoMarkerSampleResult
                {
                    sampleData = pose,
                    rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = expectedRoot,
                        q = hipsRotation
                    },
                    enableMask = new KimodoConstraintMask(),
                    validMask = new KimodoConstraintMask
                    {
                        muscle = true,
                        rootTQ = true,
                        leftFootTQ = true,
                        rightFootTQ = true,
                        rootPosition = true,
                        rootHeading = true
                    }
                };
                Type context = typeof(command_dispatcher).Assembly
                    .GetType("KimodoUnityBridge.Command.command_context");
                MethodInfo method = context?.GetMethod(
                    "BuildModelNativeConstraintSample",
                    BindingFlags.Static | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(KimodoMarkerSampleResult),
                        typeof(string),
                        typeof(RetargetSkeleton),
                        typeof(string),
                        typeof(float),
                        typeof(double)
                    },
                    null);

                Assert.That(method, Is.Not.Null);
                var result = (KimodoMarkerSampleResult)method.Invoke(null, new object[]
                {
                    source,
                    "fullbody",
                    skeleton,
                    modelName,
                    KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
                    2.0
                });

                Assert.That(Vector3.Distance(result.rootOverride.t, expectedRoot), Is.LessThan(0.01f));
                Assert.That(result.constraintMode, Is.EqualTo("fullbody"));
                Assert.That(result.enableMask.muscle, Is.True);
                Assert.That(result.enableMask.rootPosition, Is.True);
                Assert.That(result.enableMask.rootHeading, Is.True);
                Assert.That(result.enableMask.AnyEndEffector, Is.False);
            }
            finally
            {
                skeleton.Dispose();
            }
        }

        [Test]
        public void PosePipeline_PositionOnlyRootOverridePreservesFkHeading()
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
                    "KimodoPositionOnlyRootTest",
                    out RetargetSkeleton skeleton,
                    out error),
                Is.True,
                error);

            try
            {
                Assert.That(
                    KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        skeleton,
                        out MuscleSample pose,
                        out error),
                    Is.True,
                    error);
                pose.GetRoot(out Vector3 sourceRoot, out _);
                pose.SetRoot(sourceRoot, Quaternion.Euler(0f, 40f, 0f));
                Assert.That(
                    KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        pose,
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
                        skeleton,
                        out _,
                        out _,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    skeleton.GetBonePose(
                        HumanBodyBones.Hips,
                        out Vector3 baselinePosition,
                        out Quaternion baselineRotation),
                    Is.True);
                var sample = new KimodoMarkerSampleResult
                {
                    sampleData = pose,
                    rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = baselinePosition + Vector3.up * 2f,
                        q = Quaternion.identity
                    },
                    enableMask = KimodoConstraintMask.ForType("fullbody"),
                    validMask = new KimodoConstraintMask
                    {
                        muscle = true,
                        rootTQ = true,
                        leftFootTQ = true,
                        rightFootTQ = true,
                        rootPosition = true,
                        rootHeading = false
                    }
                };

                Assert.That(
                    KimodoConstraintPosePipeline.TryApply(
                        sample,
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
                        skeleton,
                        out _,
                        out _,
                        out error),
                    Is.True,
                    error);
                Assert.That(
                    skeleton.GetBonePose(
                        HumanBodyBones.Hips,
                        out Vector3 solvedPosition,
                        out Quaternion solvedRotation),
                    Is.True);

                Assert.That(Vector3.Distance(solvedPosition, sample.rootOverride.t), Is.LessThan(0.01f));
                Assert.That(Quaternion.Angle(solvedRotation, baselineRotation), Is.LessThan(0.5f));
            }
            finally
            {
                skeleton.Dispose();
            }
        }

        [Test]
        public void PoseRigClone_CopiesStaticMeshRenderer()
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
                    "KimodoStaticMeshPreviewTest",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            KimodoConstraintPoseRigFactory.PoseRigInstance rig = null;
            try
            {
                GameObject sourceVisual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sourceVisual.name = "StaticVisual";
                sourceVisual.transform.SetParent(source.animator.transform, false);
                Mesh sourceMesh = sourceVisual.GetComponent<MeshFilter>().sharedMesh;
                sourceVisual.GetComponent<MeshRenderer>().sortingOrder = 7;

                Assert.That(
                    KimodoConstraintPoseRigFactory.TryCreatePoseRig(
                        KimodoMotionModelProfiles.DefaultModelName,
                        clipId: 1,
                        animatorId: KimodoUnityObjectIdUtility.IdHash(source.animator),
                        sourceAvatar: avatar,
                        out rig,
                        out error),
                    Is.True,
                    error);

                Transform cloneVisual = rig.Root.transform.Find("StaticVisual");
                Assert.That(cloneVisual, Is.Not.Null);
                MeshRenderer cloneRenderer = cloneVisual.GetComponent<MeshRenderer>();
                Assert.That(cloneRenderer, Is.Not.Null);
                Assert.That(cloneVisual.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(sourceMesh));
                Assert.That(cloneRenderer.sortingOrder, Is.EqualTo(7));
            }
            finally
            {
                rig?.TargetCache?.Dispose();
                if (rig?.GeneratedMaterials != null)
                {
                    for (int i = 0; i < rig.GeneratedMaterials.Count; i++)
                    {
                        Object.DestroyImmediate(rig.GeneratedMaterials[i]);
                    }
                }
                source.Dispose();
            }
        }

        [Test]
        public void TimelineProjection_SolvesCharacterBeforeRemovingTrackOffset()
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
                    "KimodoTimelineProjectionSourceTest",
                    out RetargetSkeleton source,
                    out error),
                Is.True,
                error);

            try
            {
                Assert.That(
                    KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                        source,
                        out MuscleSample bindPose,
                        out error),
                    Is.True,
                    error);

                Vector3 trackPosition = new Vector3(4f, 0.5f, -3f);
                Quaternion trackRotation = Quaternion.Euler(0f, 35f, 0f);
                Vector3 expectedRoot = new Vector3(1.25f, 1.1f, 2.5f);
                Quaternion expectedRotation = Quaternion.Euler(5f, 20f, -3f);
                KimodoTimelineTrackOffsetUtility.TrackToWorldPose(
                    expectedRoot,
                    expectedRotation,
                    trackPosition,
                    trackRotation,
                    out Vector3 worldRoot,
                    out Quaternion worldRotation);

                Transform sourceHips = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
                    source,
                    HumanBodyBones.Hips);
                Transform sourceLeftHand = KimodoRetargetHumanoidPoseUtility.ResolveHumanBoneTransform(
                    source,
                    HumanBodyBones.LeftHand);
                Assert.That(sourceHips, Is.Not.Null);
                Assert.That(sourceLeftHand, Is.Not.Null);
                sourceHips.SetPositionAndRotation(expectedRoot, expectedRotation);
                Vector3 expectedLeftHand = sourceLeftHand.position + new Vector3(0.08f, 0.04f, 0.03f);
                Quaternion expectedLeftHandRotation = sourceLeftHand.rotation;
                KimodoTimelineTrackOffsetUtility.TrackToWorldPose(
                    expectedLeftHand,
                    expectedLeftHandRotation,
                    trackPosition,
                    trackRotation,
                    out Vector3 worldLeftHand,
                    out Quaternion worldLeftHandRotation);

                var sample = new KimodoMarkerSampleResult
                {
                    sampleData = bindPose,
                    rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = worldRoot,
                        q = worldRotation
                    },
                    effectors = new KimodoConstraintEffectors
                    {
                        leftHand = new KimodoUnityBridge.KimodoRigidTransform
                        {
                            t = worldLeftHand,
                            q = worldLeftHandRotation
                        }
                    },
                    constraintMode = "fullbody",
                    enabled = true,
                    enableMask = new KimodoConstraintMask
                    {
                        muscle = true,
                        rootTQ = true,
                        leftFootTQ = true,
                        rightFootTQ = true,
                        rootPosition = true,
                        rootHeading = true,
                        leftHand = true
                    },
                    validMask = new KimodoConstraintMask
                    {
                        muscle = true,
                        rootTQ = true,
                        leftFootTQ = true,
                        rightFootTQ = true,
                        rootPosition = true,
                        rootHeading = true,
                        leftHand = true
                    }
                };

                KimodoConstraintProjectedPose projected =
                    KimodoConstraintExportProjector.ProjectTimelineSample(
                        sample,
                        modelName,
                        avatar,
                        trackPosition,
                        trackRotation);

                Assert.That(
                    Vector3.Distance(projected.profileRootPosition, expectedRoot),
                    Is.LessThan(0.01f));
                Assert.That(
                    Quaternion.Angle(projected.jointRotations[0], expectedRotation),
                    Is.LessThan(0.5f));
                Assert.That(
                    Vector3.Distance(projected.profileRootPosition, worldRoot),
                    Is.GreaterThan(0.5f));
                int leftHandIndex = System.Array.IndexOf(projected.jointNames, "LeftHand");
                Assert.That(leftHandIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(
                    Vector3.Distance(projected.jointPositions[leftHandIndex], expectedLeftHand),
                    Is.LessThan(0.03f));

                Assert.That(
                    KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        bindPose,
                        KimodoMotionModelProfiles.ResolveGenerationFrameRate(modelName),
                        source,
                        out _,
                        out _,
                        out error),
                    Is.True,
                    error);
                Vector3 expectedEffectorOnlyHand = sourceLeftHand.position +
                    new Vector3(0.05f, 0.02f, 0.02f);
                KimodoTimelineTrackOffsetUtility.TrackToWorldPose(
                    expectedEffectorOnlyHand,
                    sourceLeftHand.rotation,
                    trackPosition,
                    trackRotation,
                    out Vector3 worldEffectorOnlyHand,
                    out Quaternion worldEffectorOnlyRotation);
                var effectorOnly = new KimodoMarkerSampleResult
                {
                    sampleData = bindPose,
                    effectors = new KimodoConstraintEffectors
                    {
                        leftHand = new KimodoUnityBridge.KimodoRigidTransform
                        {
                            t = worldEffectorOnlyHand,
                            q = worldEffectorOnlyRotation
                        }
                    },
                    constraintMode = "effector",
                    enabled = true,
                    enableMask = new KimodoConstraintMask
                    {
                        rootTQ = true,
                        leftFootTQ = true,
                        rightFootTQ = true,
                        leftHand = true
                    },
                    validMask = new KimodoConstraintMask
                    {
                        muscle = true,
                        rootTQ = true,
                        leftFootTQ = true,
                        rightFootTQ = true,
                        leftHand = true
                    }
                };

                KimodoConstraintProjectedPose effectorOnlyProjected =
                    KimodoConstraintExportProjector.ProjectTimelineSample(
                        effectorOnly,
                        modelName,
                        avatar,
                        trackPosition,
                        trackRotation);
                int effectorOnlyHandIndex = System.Array.IndexOf(
                    effectorOnlyProjected.jointNames,
                    "LeftHand");
                Assert.That(effectorOnlyHandIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(
                    Vector3.Distance(
                        effectorOnlyProjected.jointPositions[effectorOnlyHandIndex],
                        expectedEffectorOnlyHand),
                    Is.LessThan(0.03f));
            }
            finally
            {
                source.Dispose();
            }
        }
    }
}
