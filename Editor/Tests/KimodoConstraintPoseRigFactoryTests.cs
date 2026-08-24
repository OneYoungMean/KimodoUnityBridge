using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintPoseRigFactoryTests
    {
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
