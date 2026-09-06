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
        public void PreviewRootRotation_UsesFullHipsForFullBodyAndHeadingForRoot2D()
        {
            Quaternion evaluatedHips = Quaternion.Euler(12f, 35f, -8f);
            Quaternion capturedHips = Quaternion.Euler(-16f, 70f, 11f);
            var fullBody = new KimodoMarkerSampleResult
            {
                constraintMode = "fullbody",
                rootOverride = new KimodoUnityBridge.KimodoRigidTransform { q = capturedHips },
                validMask = new KimodoConstraintMask { rootHeading = true }
            };
            var root2D = new KimodoMarkerSampleResult
            {
                constraintMode = "root2d",
                rootOverride = new KimodoUnityBridge.KimodoRigidTransform { q = capturedHips },
                validMask = new KimodoConstraintMask { rootHeading = true }
            };

            Assert.That(
                Quaternion.Angle(
                    KimodoConstraintPoseRigFactory.ResolvePreviewHipsRotation(fullBody, evaluatedHips),
                    capturedHips),
                Is.LessThan(0.01f));
            Assert.That(
                Quaternion.Angle(
                    KimodoConstraintPoseRigFactory.ResolvePreviewHipsRotation(root2D, evaluatedHips),
                    KimodoMotionMath.ApplyPlanarHeading(evaluatedHips, capturedHips)),
                Is.LessThan(0.01f));
        }

        [Test]
        public void CarryEffectorsWithRoot_AppliesTheRootRigidTransformDelta()
        {
            var sample = new KimodoMarkerSampleResult
            {
                carryEffectorsWithRoot = true,
                rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                {
                    t = new Vector3(1f, 0f, 2f),
                    q = Quaternion.identity
                },
                effectors = new KimodoConstraintEffectors
                {
                    leftHand = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = new Vector3(2f, 0f, 2f),
                        q = Quaternion.identity
                    },
                    rightFoot = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = new Vector3(1f, 0f, 1f),
                        q = Quaternion.Euler(10f, 20f, 30f)
                    }
                },
                enableMask = new KimodoConstraintMask { leftHand = true, rightFoot = true },
                validMask = new KimodoConstraintMask { leftHand = true, rightFoot = true }
            };
            var entry = new ConstraintPreviewInstance
            {
                ConstraintMode = KimodoConstraintMode.Mix,
                SampleData = sample
            };
            Vector3 nextRoot = new Vector3(4f, 0f, 5f);
            Quaternion nextRotation = Quaternion.Euler(0f, 90f, 0f);

            KimodoConstraintPreviewRenderer.CarryEffectorsWithRoot(
                entry,
                sample.rootOverride.t,
                sample.rootOverride.q,
                nextRoot,
                nextRotation);

            Assert.That(
                Vector3.Distance(sample.effectors.leftHand.t, nextRoot + Vector3.back),
                Is.LessThan(0.001f));
            Assert.That(
                Quaternion.Angle(sample.effectors.leftHand.q, nextRotation),
                Is.LessThan(0.001f));
            Assert.That(
                Quaternion.Angle(
                    sample.effectors.rightFoot.q,
                    nextRotation * Quaternion.Euler(10f, 20f, 30f)),
                Is.LessThan(0.001f));
        }

        [Test]
        public void CarryEffectorsWithRoot_FullBodyPromotesFootGoalsForSolveIk()
        {
            var sample = new KimodoMarkerSampleResult
            {
                carryEffectorsWithRoot = true,
                rootOverride = KimodoUnityBridge.KimodoRigidTransform.Identity,
                effectors = new KimodoConstraintEffectors
                {
                    leftHand = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = new Vector3(0.5f, 1f, 0.5f),
                        q = Quaternion.identity
                    },
                    leftFoot = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = new Vector3(-0.1f, 0f, 0f),
                        q = Quaternion.identity
                    },
                    rightFoot = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = new Vector3(0.1f, 0f, 0f),
                        q = Quaternion.identity
                    }
                },
                enableMask = KimodoConstraintMask.ForType("fullbody"),
                validMask = new KimodoConstraintMask()
            };
            var entry = new ConstraintPreviewInstance
            {
                ConstraintMode = KimodoConstraintMode.FullBody,
                SampleData = sample
            };
            Vector3 originalHandPosition = sample.effectors.leftHand.t;

            KimodoConstraintPreviewRenderer.CarryEffectorsWithRoot(
                entry,
                Vector3.zero,
                Quaternion.identity,
                Vector3.forward,
                Quaternion.identity);

            Assert.That(sample.effectors.leftFoot.t.z, Is.EqualTo(1f).Within(0.001f));
            Assert.That(sample.effectors.rightFoot.t.z, Is.EqualTo(1f).Within(0.001f));
            Assert.That(sample.effectors.leftHand.t, Is.EqualTo(originalHandPosition));
            Assert.That(sample.enableMask.leftHand, Is.False);
            Assert.That(KimodoConstraintMask.IsActive(sample, "leftfoot"), Is.True);
            Assert.That(KimodoConstraintMask.IsActive(sample, "rightfoot"), Is.True);
        }

        [Test]
        public void CarryEffectorsWithRoot_Root2DIgnoresEffectors()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintMode = "root2d",
                carryEffectorsWithRoot = true,
                rootOverride = KimodoUnityBridge.KimodoRigidTransform.Identity,
                effectors = new KimodoConstraintEffectors
                {
                    leftHand = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = new Vector3(1f, 2f, 3f),
                        q = Quaternion.Euler(10f, 20f, 30f)
                    }
                },
                enableMask = new KimodoConstraintMask
                {
                    rootPosition = true,
                    leftHand = true
                },
                validMask = new KimodoConstraintMask
                {
                    rootPosition = true,
                    leftHand = true
                }
            };
            var entry = new ConstraintPreviewInstance
            {
                ConstraintMode = KimodoConstraintMode.Root2D,
                SampleData = sample
            };
            Vector3 originalPosition = sample.effectors.leftHand.t;
            Quaternion originalRotation = sample.effectors.leftHand.q;

            KimodoConstraintPreviewRenderer.CarryEffectorsWithRoot(
                entry,
                Vector3.zero,
                Quaternion.identity,
                new Vector3(4f, 0f, 5f),
                Quaternion.Euler(0f, 90f, 0f));

            Assert.That(sample.effectors.leftHand.t, Is.EqualTo(originalPosition));
            Assert.That(Quaternion.Angle(sample.effectors.leftHand.q, originalRotation), Is.LessThan(0.001f));
            Assert.That(sample.enableMask.leftHand, Is.True);
        }

        [Test]
        public void CarryEffectorsWithRoot_DisabledLeavesTargetsUnchanged()
        {
            var sample = new KimodoMarkerSampleResult
            {
                carryEffectorsWithRoot = false,
                rootOverride = KimodoUnityBridge.KimodoRigidTransform.Identity,
                effectors = new KimodoConstraintEffectors
                {
                    leftHand = new KimodoUnityBridge.KimodoRigidTransform
                    {
                        t = new Vector3(1f, 2f, 3f),
                        q = Quaternion.Euler(10f, 20f, 30f)
                    }
                },
                enableMask = new KimodoConstraintMask { leftHand = true },
                validMask = new KimodoConstraintMask { leftHand = true }
            };
            var entry = new ConstraintPreviewInstance
            {
                ConstraintMode = KimodoConstraintMode.Mix,
                SampleData = sample
            };
            Vector3 originalPosition = sample.effectors.leftHand.t;
            Quaternion originalRotation = sample.effectors.leftHand.q;

            KimodoConstraintPreviewRenderer.CarryEffectorsWithRoot(
                entry,
                Vector3.zero,
                Quaternion.identity,
                Vector3.one,
                Quaternion.Euler(0f, 90f, 0f));

            Assert.That(sample.effectors.leftHand.t, Is.EqualTo(originalPosition));
            Assert.That(
                Quaternion.Angle(sample.effectors.leftHand.q, originalRotation),
                Is.LessThan(0.001f));
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
                MeshRenderer sourceRenderer = sourceVisual.GetComponent<MeshRenderer>();
                sourceRenderer.sortingOrder = 7;
                Material sourceMaterial = sourceRenderer.sharedMaterial;

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
                if (sourceMaterial != null)
                {
                    Assert.That(cloneRenderer.sharedMaterial, Is.Not.SameAs(sourceMaterial));
                    Assert.That(cloneRenderer.sharedMaterial.shader, Is.SameAs(sourceMaterial.shader));
                }
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
