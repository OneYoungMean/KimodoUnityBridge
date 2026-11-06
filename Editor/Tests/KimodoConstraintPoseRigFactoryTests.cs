using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using KimodoUnityBridge.Command;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintPoseRigFactoryTests
    {
        [TestCase("root2d", false)]
        [TestCase("root2d", true)]
        [TestCase("fullbody", false)]
        [TestCase("fullbody", true)]
        public void PosePipeline_RootOverrideUsesWorldUnits(string mode, bool heading)
        {
            const string model = KimodoMotionModelProfiles.DefaultModelName;
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(model, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(avatar, "RootWorldUnits", out var skeleton, out error), Is.True, error);
            try
            {
                Assert.That(KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    KimodoRetargetSamplingUtility.CaptureBoneSample(skeleton), skeleton, model, mode, 0,
                    out var sample, out error), Is.True, error);
                sample.constraintMode = mode;
                sample.enableMask = KimodoConstraintMask.ForType(mode);
                sample.enableMask.rootHeading = heading;
                sample.rootOverride.t = new Vector3(-3.011355f, 0.841578f, 12.355375f);
                sample.rootOverride.q = Quaternion.Euler(12f, 65f, -8f);
                Assert.That(KimodoConstraintPosePipeline.TryApply(sample, 30f, skeleton,
                    out _, out _, out error), Is.True, error);
                Assert.That(skeleton.GetBonePose(HumanBodyBones.Hips, out var position, out var rotation), Is.True);
                Assert.That(Vector2.Distance(new Vector2(position.x, position.z),
                    new Vector2(sample.rootOverride.t.x, sample.rootOverride.t.z)), Is.LessThan(0.001f));
                if (mode == "fullbody")
                {
                    Assert.That(Vector3.Distance(position, sample.rootOverride.t), Is.LessThan(0.001f));
                }
                if (heading)
                {
                    Quaternion expected = mode == "fullbody" ? sample.rootOverride.q : KimodoMotionMath.ResolvePlanarHeading(sample.rootOverride.q);
                    Quaternion actual = mode == "fullbody" ? rotation : KimodoMotionMath.ResolvePlanarHeading(rotation);
                    Assert.That(Quaternion.Angle(actual, expected), Is.LessThan(0.1f));
                }
            }
            finally { skeleton.Dispose(); }
        }

        [Test]
        public void PosePipeline_RootTranslationCarriesNativeFootIkWhenFootEffectorsAreDisabled()
        {
            const string model = KimodoMotionModelProfiles.DefaultModelName;
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                model, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                avatar, "RootCarriesNativeFootIk", out var baseline, out error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                avatar, "RootCarriesNativeFootIkMoved", out var moved, out error), Is.True, error);
            try
            {
                Assert.That(KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    KimodoRetargetSamplingUtility.CaptureBoneSample(baseline),
                    baseline,
                    model,
                    "fullbody",
                    0,
                    out KimodoMarkerSampleResult sample,
                    out error), Is.True, error);
                sample.enableMask = KimodoConstraintMask.ForType("fullbody");
                sample.enableMask.leftFoot = false;
                sample.enableMask.rightFoot = false;
                sample.enableMask.rootHeading = false;
                Assert.That(baseline.GetBonePose(HumanBodyBones.Hips,
                    out Vector3 originalHips, out Quaternion originalHeading), Is.True);
                sample.rootOverride.position = originalHips;
                sample.rootOverride.rotation = originalHeading;

                Assert.That(KimodoConstraintPosePipeline.TryApply(
                    sample, 30f, baseline, out _, out _, out error), Is.True, error);
                KimodoMarkerSampleResult movedSample = sample.Clone();
                movedSample.rootOverride.position += Vector3.right;
                Assert.That(KimodoConstraintPosePipeline.TryApply(
                    movedSample, 30f, moved,
                    out _, out _, out error), Is.True, error);

                Assert.That(baseline.GetBonePose(HumanBodyBones.LeftFoot,
                    out Vector3 baselineLeftFoot, out _), Is.True);
                Assert.That(moved.GetBonePose(HumanBodyBones.LeftFoot,
                    out Vector3 movedLeftFoot, out _), Is.True);
                Assert.That(baseline.GetBonePose(HumanBodyBones.RightFoot,
                    out Vector3 baselineRightFoot, out _), Is.True);
                Assert.That(moved.GetBonePose(HumanBodyBones.RightFoot,
                    out Vector3 movedRightFoot, out _), Is.True);
                Assert.That(Vector3.Distance(movedLeftFoot - baselineLeftFoot, Vector3.right),
                    Is.LessThan(0.02f));
                Assert.That(Vector3.Distance(movedRightFoot - baselineRightFoot, Vector3.right),
                    Is.LessThan(0.02f));
            }
            finally
            {
                baseline.Dispose();
                moved.Dispose();
            }
        }


        [Test]
        public void GenerateCommand_ImplicitPoseUsesAvatarRestPoseOnEmptyTrack(
            [Values(0.0, 1.25, 100.0)] double sampleTime,
            [Values] bool withEffector,
            [Values] bool withSourceCharacter)
        {
            const string model = KimodoMotionModelProfiles.DefaultModelName;
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                model, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                avatar, "ImplicitConstraintRestPose", out var skeleton, out error), Is.True, error);
            var timeline = ScriptableObject.CreateInstance<UnityEngine.Timeline.TimelineAsset>();
            try
            {
                var track = timeline.CreateTrack<UnityEngine.Timeline.AnimationTrack>(null, "EmptyCharacter");
                var character = new command_context.TimelineCharacterRecord("rest-test", skeleton.root,
                    skeleton.animator, avatar, track, null, string.Empty);
                var resolve = typeof(command_context).GetMethod("ResolveConstraintPose",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var build = typeof(command_context).GetMethod("BuildCanonicalConstraint",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(KimodoInOutConstraintTools.TryCaptureOriginalScenePose(
                    skeleton.root, skeleton.animator, avatar, true, character.Name,
                    out var expected, out error), Is.True, error);

                skeleton.root.transform.position = new Vector3(3f, -2f, 5f);
                skeleton.animator.GetBoneTransform(HumanBodyBones.RightUpperArm).localRotation *= Quaternion.Euler(0, 0, 45);
                var input = JObject.Parse(@"{'root':{'rotation':[0,35,0]}}");
                if (withEffector)
                    input["effector"] = JArray.Parse(@"[{'joint':'right_hand','position':[1,1.2,0.5]}]");
                var support = (KimodoMarkerSampleResult)resolve.Invoke(null, new object[]
                {
                    input, "point", skeleton, skeleton.root, withSourceCharacter ? character : null, new List<string>(), 0
                });
                Assert.That(support.sampleValid, Is.True);
                Assert.That(Vector3.Distance(support.rootOverride.t, expected.rootOverride.t), Is.LessThan(0.001f));
                Assert.That(Quaternion.Angle(support.rootOverride.q, expected.rootOverride.q), Is.LessThan(0.1f));
                Assert.That(JToken.DeepEquals(KimodoConstraintPoseUtility.BuildPoseJson(support),
                    KimodoConstraintPoseUtility.BuildPoseJson(expected)), Is.True);

                var anchor = typeof(command_context).GetMethod("ResolveRootPathWorldAnchor",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var anchorArguments = new object[]
                {
                    withSourceCharacter ? character : null, skeleton.root, avatar, 0f, null, null
                };
                anchor.Invoke(null, anchorArguments);
                Assert.That(Vector3.Distance((Vector3)anchorArguments[4], expected.rootOverride.t), Is.LessThan(0.001f),
                    "Root path initialization must not inherit the scene's current root transform.");
                Assert.That(Quaternion.Angle((Quaternion)anchorArguments[5],
                    KimodoMotionMath.ResolvePlanarHeading(expected.rootOverride.q)), Is.LessThan(0.1f));

                var sample = (KimodoMarkerSampleResult)build.Invoke(null, new object[]
                {
                    input, skeleton, model, 30f, sampleTime, 0, skeleton.root,
                    withSourceCharacter ? character : null, new List<string>()
                });
                Assert.That(sample.sampleTime, Is.EqualTo(sampleTime));
                Assert.That(sample.constraintMode, Is.EqualTo(withEffector ? "effector" : "root2d"));
                Assert.That(sample.rootOverride.t.y, Is.EqualTo(expected.rootOverride.t.y).Within(0.001f));
                Assert.That(Quaternion.Angle(sample.rootOverride.q, Quaternion.Euler(0, 35, 0)), Is.LessThan(0.1f));
                if (withEffector)
                    Assert.That(sample.effectors.rightHand.t, Is.EqualTo(new Vector3(1, 1.2f, 0.5f)));
                Assert.That(track.GetClips(), Is.Empty, "Implicit constraints must not create Timeline animation.");
            }
            finally
            {
                foreach (var track in timeline.GetRootTracks()) Object.DestroyImmediate(track);
                Object.DestroyImmediate(timeline);
                skeleton.Dispose();
            }
        }

        [TestCase(false), TestCase(true)]
        public void GenerateCommand_Root2DConstraintMatchesCanonicalSampleExport(bool withEffect)
        {
            const string model = KimodoMotionModelProfiles.DefaultModelName;
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                model, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                avatar, "GenerateCommandCorrespondence", out var skeleton, out error), Is.True, error);
            try
            {
                const double sampleTime = 1.25;
                Assert.That(skeleton.GetBonePose(
                    HumanBodyBones.Hips, out Vector3 supportRoot, out _), Is.True);
                var commandInput = new JObject
                {
                    ["root"] = new JObject
                    {
                        ["position"] = new JArray(2f, supportRoot.y, -3f),
                        ["rotation"] = new JArray(0f, 0f, 0f)
                    }
                };
                if (withEffect)
                {
                    Assert.That(skeleton.GetBonePose(HumanBodyBones.RightHand, out Vector3 hand, out _), Is.True);
                    Vector3 target = hand + new Vector3(2f - supportRoot.x, 0f, -3f - supportRoot.z);
                    commandInput["effector"] = new JArray(new JObject {
                        ["joint"] = "right_hand",
                        ["position"] = new JArray(target.x, target.y, target.z) });
                }
                Type context = typeof(command_dispatcher).Assembly
                    .GetType("KimodoUnityBridge.Command.command_context");
                MethodInfo buildCanonical = context?.GetMethod(
                    "BuildCanonicalConstraint",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(buildCanonical, Is.Not.Null);

                var sample = (KimodoMarkerSampleResult)buildCanonical.Invoke(null, new object[]
                {
                    commandInput, skeleton, model, 30f, sampleTime, 0, null, null, new List<string>()
                });
                Assert.That(sample, Is.Not.Null);
                Assert.That(sample.constraintMode, Is.EqualTo(withEffect ? "effector" : "root2d"));
                Assert.That(sample.sampleValid, Is.True);
                Assert.That(sample.enableMask.rootHeading, Is.True);
                Assert.That(sample.rootOverride.t.y, Is.EqualTo(supportRoot.y).Within(0.001f));

                KimodoConstraintJson exported = KimodoConstraintJsonExporter.BuildConstraint(
                    sample,
                    new KimodoConstraintExportContext
                    {
                        projectedPoseProjector = KimodoConstraintExportProjector.CreateProfileNative(model)
                    },
                    0.0,
                    null,
                    30.0);
                Assert.That(exported, Is.Not.Null);
                Assert.That(exported.type, Is.EqualTo(withEffect ? "right-hand" : "root2d"));
                CollectionAssert.AreEqual(new[] { 37 }, exported.frame_indices);
                Assert.That(exported.smooth_root_2d, Is.Not.Null);
                Assert.That(exported.smooth_root_2d, Has.Count.EqualTo(1));
                Assert.That(exported.smooth_root_2d[0][0], Is.EqualTo(-2f).Within(0.001f));
                Assert.That(exported.smooth_root_2d[0][1], Is.EqualTo(-3f).Within(0.001f));
            }
            finally
            {
                skeleton.Dispose();
            }
        }

        [TestCase(false), TestCase(true)]
        public void Command_PoseAndEffector_ExportsIkSolvedFullBody(bool withRoot)
        {
            const string model = KimodoMotionModelProfiles.DefaultModelName;
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                model, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                avatar, "FullBodyIkExport", out var skeleton, out error), Is.True, error);
            try
            {
                Assert.That(KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    KimodoRetargetSamplingUtility.CaptureBoneSample(skeleton), skeleton, model, "fullbody", 0,
                    out var pose, out error), Is.True, error);
                pose.enableMask = KimodoConstraintMask.ForType("fullbody");
                var projector = KimodoConstraintExportProjector.CreateProfileNative(model);
                var baseline = projector(pose);
                int handIndex = Array.IndexOf(baseline.jointNames, "RightHand");
                Assert.That(handIndex, Is.GreaterThanOrEqualTo(0));
                Vector3 target = Vector3.Lerp(baseline.jointPositions[handIndex], baseline.profileRootPosition, 0.2f)
                    + Vector3.forward * 0.1f;
                var input = new JObject
                {
                    ["pose"] = new JObject { ["track"] = "P", ["index"] = 0 },
                    ["effector"] = new JArray(new JObject { ["joint"] = "right_hand", ["position"] = new JArray(target.x, target.y, target.z) })
                };
                if (withRoot) input["root"] = new JObject { ["position"] = new JArray(pose.rootOverride.t.x, pose.rootOverride.t.y, pose.rootOverride.t.z) };
                KimodoUnityBridge.Command.command_context.ApplyPointConstraintData(input, pose, 0, 0);
                var solved = projector(pose);
                Assert.That(pose.constraintMode, Is.EqualTo("fullbody"));
                Assert.That(Vector3.Distance(solved.jointPositions[handIndex], target), Is.LessThan(0.03f));
                int armIndex = Array.IndexOf(solved.jointNames, "RightForeArm");
                Assert.That(armIndex, Is.GreaterThanOrEqualTo(0));
                Assert.That(Vector3.Distance(solved.localJointAngles[armIndex], baseline.localJointAngles[armIndex]),
                    Is.GreaterThan(0.01f), "IK must change the exported arm pose.");
                var exported = KimodoConstraintJsonExporter.BuildConstraint(pose,
                    new KimodoConstraintExportContext { projectedPoseProjector = projector }, 0, null, 30);
                Assert.That(exported.type, Is.EqualTo("fullbody"));
                Assert.That(exported.local_joints_rot, Has.Count.EqualTo(1));
                Assert.That(exported.target_positions, Is.Null);
            }
            finally { skeleton.Dispose(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PreviewModeSwitch_PreservesSampledPoseAndLegs(bool staleEffectorMasks)
        {
            const string model = KimodoMotionModelProfiles.DefaultModelName;
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(model, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(avatar, "PreviewModeSwitch", out var source, out error), Is.True, error);
            KimodoConstraintPoseRigFactory.PoseRigInstance rig = null;
            try
            {
                // An asymmetric bent-leg pose catches accidental fallback to the bind pose.
                source.animator.GetBoneTransform(HumanBodyBones.LeftUpperLeg).localRotation *= Quaternion.Euler(35, 0, 0);
                source.animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg).localRotation *= Quaternion.Euler(-55, 0, 0);
                source.animator.GetBoneTransform(HumanBodyBones.RightUpperLeg).localRotation *= Quaternion.Euler(-15, 0, 0);
                Assert.That(KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    KimodoRetargetSamplingUtility.CaptureBoneSample(source), source, model, "fullbody", 0,
                    out var sample, out error), Is.True, error);
                sample.enableMask = KimodoConstraintMask.ForType("fullbody");
                sample.rootOverride.t += new Vector3(2, 0.35f, -1);
                sample.rootOverride.q = Quaternion.Euler(12, 65, -8) * sample.rootOverride.q;
                Assert.That(KimodoConstraintPoseRigFactory.TryCreatePoseRig(model, source.animator, avatar, out rig, out error), Is.True, error);
                Assert.That(KimodoConstraintPoseRigFactory.TryApplyPose(rig, sample, model, out error), Is.True, error);
                var expected = KimodoRetargetSamplingUtility.CaptureBoneSample(rig.TargetCache);
                Transform expectedHips = rig.TargetCache.animator.GetBoneTransform(HumanBodyBones.Hips);
                Vector3 expectedHipsPosition = expectedHips.position;
                Quaternion expectedHipsRotation = expectedHips.rotation;
                var originalData = (float[])sample.sampleData.data.Clone();
                if (staleEffectorMasks)
                {
                    sample.effectors.leftFoot.t += Vector3.up * 3;
                    sample.effectors.rightFoot.t += Vector3.forward * 3;
                }
                for (int pass = 0; pass < 3; pass++)
                {
                    sample.constraintMode = pass == 1 ? "fullbody" : "root2d";
                    sample.enableMask = KimodoConstraintMask.ForType(sample.constraintMode);
                    if (staleEffectorMasks && sample.constraintMode == "root2d")
                    {
                        sample.enableMask.leftFoot = sample.enableMask.rightFoot = true;
                    }
                    var previewSample = sample.Clone();
                    Assert.That(KimodoConstraintPoseRigFactory.TryApplyPose(rig,
                        previewSample, model, out error), Is.True, error);
                    Assert.That(previewSample.constraintMode, Is.EqualTo(sample.constraintMode));
                    CollectionAssert.AreEqual(originalData, previewSample.sampleData.data);
                    var actual = KimodoRetargetSamplingUtility.CaptureBoneSample(rig.TargetCache);
                    Transform actualHips = rig.TargetCache.animator.GetBoneTransform(HumanBodyBones.Hips);
                    Assert.That(Vector3.Distance(actualHips.position, expectedHipsPosition), Is.LessThan(0.001f));
                    Assert.That(Quaternion.Angle(actualHips.rotation, expectedHipsRotation), Is.LessThan(0.1f));
                    for (int i = 0; i < expected.boneNames.Length; i++)
                    {
                        Assert.That(Vector3.Distance(actual.localPositions[i], expected.localPositions[i]), Is.LessThan(0.001f), expected.boneNames[i]);
                        Assert.That(Quaternion.Angle(actual.localRotations[i], expected.localRotations[i]), Is.LessThan(0.1f), expected.boneNames[i]);
                    }
                }
                Assert.That(sample.constraintMode, Is.EqualTo("root2d"));
                CollectionAssert.AreEqual(originalData, sample.sampleData.data);
                var export = KimodoConstraintJsonExporter.BuildConstraint(sample,
                    new KimodoConstraintExportContext { projectedPoseProjector = KimodoConstraintExportProjector.CreateProfileNative(model) }, 0, 1, 30);
                Assert.That(export.type, Is.EqualTo("root2d"));
            }
            finally
            {
                KimodoConstraintPoseRigFactory.DisposePoseRig(rig);
                source.Dispose();
            }
        }

        [TestCase("root2d")]
        [TestCase("fullbody")]
        public void TimelineProjection_RootPositionIsConvertedToTrackSpaceOnce(string mode)
        {
            const string model = KimodoMotionModelProfiles.DefaultModelName;
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(model, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(avatar, "TrackRootUnits", out var skeleton, out error), Is.True, error);
            try
            {
                Assert.That(KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                    KimodoRetargetSamplingUtility.CaptureBoneSample(skeleton), skeleton, model, mode, 0,
                    out var sample, out error), Is.True, error);
                sample.enableMask = KimodoConstraintMask.ForType(mode);
                sample.rootOverride.t = new Vector3(-3.011355f, 0.841578f, 12.355375f);
                sample.rootOverride.q = Quaternion.Euler(0f, 65f, 0f);
                var trackPosition = new Vector3(4f, 0.5f, -3f);
                var trackRotation = Quaternion.Euler(0f, 35f, 0f);
                var projected = KimodoConstraintExportProjector.ProjectTimelineSample(
                    sample, model, avatar, trackPosition, trackRotation);
                // Same avatar on both sides isolates the rigid track transform
                // from the character/profile retarget scale conversion.
                var restored = trackPosition + trackRotation * projected.profileRootPosition;
                Assert.That(Vector2.Distance(new Vector2(restored.x, restored.z),
                    new Vector2(sample.rootOverride.t.x, sample.rootOverride.t.z)), Is.LessThan(0.005f));
                Assert.That(Quaternion.Angle(trackRotation * projected.jointRotations[0],
                    sample.rootOverride.q), Is.LessThan(0.1f));
            }
            finally { skeleton.Dispose(); }
        }

        [Test]
        public void CaptureWorldTargets_DoesNotEnableChannels()
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

                Assert.That(sample.enableMask.IsEmpty, Is.True);
            }
            finally
            {
                skeleton.Dispose();
            }
        }

        [Test]
        public void PreviewRootRotation_UsesFullHipsRotationForAllModes()
        {
            Quaternion evaluatedHips = Quaternion.Euler(12f, 35f, -8f);
            Quaternion capturedHips = Quaternion.Euler(-16f, 70f, 11f);
            var fullBody = new KimodoMarkerSampleResult
            {
                constraintMode = "fullbody",
                rootOverride = new KimodoUnityBridge.KimodoRigidTransform { q = capturedHips },
                enableMask = new KimodoConstraintMask { rootHeading = true }
            };
            var root2D = new KimodoMarkerSampleResult
            {
                constraintMode = "root2d",
                rootOverride = new KimodoUnityBridge.KimodoRigidTransform { q = capturedHips },
                enableMask = new KimodoConstraintMask { rootHeading = true }
            };

            Assert.That(
                Quaternion.Angle(
                    KimodoConstraintPoseRigFactory.ResolvePreviewHipsRotation(fullBody, evaluatedHips),
                    capturedHips),
                Is.LessThan(0.01f));
            Assert.That(
                Quaternion.Angle(
                    KimodoConstraintPoseRigFactory.ResolvePreviewHipsRotation(root2D, evaluatedHips),
                    capturedHips),
                Is.LessThan(0.01f));
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
                    enableMask = new KimodoConstraintMask()
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
                Assert.That(result.sampleValid, Is.True);
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
                    enableMask = new KimodoConstraintMask { rootHeading = false }
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

    }
}
