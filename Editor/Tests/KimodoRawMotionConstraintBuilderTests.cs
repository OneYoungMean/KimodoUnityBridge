using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoRawMotionConstraintBuilderTests
    {
        [Test]
        public void CommandFramesToModelFrameCount_UsesSixtyFpsInputAndCeiling()
        {
            Assert.That(
                KimodoFrameTimeUtility.CommandFramesToModelFrameCount(30, 30f),
                Is.EqualTo(15));
            Assert.That(
                KimodoFrameTimeUtility.CommandFramesToModelFrameCount(30, 25f),
                Is.EqualTo(13));
            Assert.That(
                KimodoFrameTimeUtility.CommandFramesToModelFrameCount(30, 20f),
                Is.EqualTo(10));
            Assert.That(
                KimodoFrameTimeUtility.CommandFramesToModelFrameCount(30, 60f),
                Is.EqualTo(30));
        }

        [Test]
        public void KimodoFrameWindow_ConvertsDurationAtSixtyFps()
        {
            var halfSecond = new KimodoFrameWindow(30, 2);
            Assert.That(halfSecond.DurationSeconds, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(halfSecond.ResolveModelFrameCount(30f), Is.EqualTo(15));
            Assert.That(halfSecond.ResolveModelFrameCount(25f), Is.EqualTo(13));
            Assert.That(KimodoFrameWindow.ModelFrameCountToCommandFrameCount60(30, 30f), Is.EqualTo(60));
            Assert.That(KimodoFrameWindow.ModelFrameCountToCommandFrameCount60(1, 30f), Is.EqualTo(2));
        }

        [Test]
        public void MigrateLegacyWindowFrameCounts_ConvertsLegacyModelFramesOnceButKeepsLoopUnits()
        {
            KimodoPlayableClip clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(KimodoPlayableClip).GetField("legacyInConstraintWindowModelFrames", flags).SetValue(clip, 7);
                typeof(KimodoPlayableClip).GetField("legacyOutConstraintWindowModelFrames", flags).SetValue(clip, 4);
                typeof(KimodoPlayableClip).GetField("legacyLongClipWindowModelFrames", flags).SetValue(clip, 30);
                clip.loopWindowFrames60 = 30;

                Assert.That(clip.MigrateLegacyWindowFrameCounts(), Is.True);
                Assert.That(clip.inConstraintWindowFrames60, Is.EqualTo(14));
                Assert.That(clip.outConstraintWindowFrames60, Is.EqualTo(8));
                Assert.That(clip.longClipWindowFrames60, Is.EqualTo(60));
                Assert.That(clip.loopWindowFrames60, Is.EqualTo(30));
                Assert.That(clip.MigrateLegacyWindowFrameCounts(), Is.False);
                Assert.That(clip.longClipWindowFrames60, Is.EqualTo(60));
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void MigrateLegacyWindowFrameCounts_LeavesNewAssetDefaultsUnchanged()
        {
            KimodoPlayableClip clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                Assert.That(clip.MigrateLegacyWindowFrameCounts(), Is.False);
                Assert.That(clip.inConstraintWindowFrames60, Is.EqualTo(1));
                Assert.That(clip.outConstraintWindowFrames60, Is.EqualTo(1));
                Assert.That(clip.longClipWindowFrames60, Is.EqualTo(60));
                Assert.That(clip.loopWindowFrames60, Is.EqualTo(30));
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void KimodoBezierUtility_EvaluatesCubicMidpoint()
        {
            Vector2 midpoint = KimodoBezierUtility.EvaluateCubic(
                Vector2.zero,
                new Vector2(4f, 0f),
                new Vector2(4f, 2f),
                new Vector2(0f, 2f),
                0.5f);

            Assert.That(midpoint.x, Is.EqualTo(3f).Within(1e-5f));
            Assert.That(midpoint.y, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void BuildLoopConstraintJson_UsesFirstFrameAnchorAndEstimatedVelocity()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            const int frameCount = 4;
            const float frameRate = 20f;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new[]
            {
                new Vector3(2f, 0f, 3f),
                new Vector3(4f, 0f, 5f),
                new Vector3(6f, 0f, 7f),
                new Vector3(8f, 0f, 9f)
            };
            Quaternion firstRootRotation = Quaternion.Euler(20f, 10f, 30f);
            Quaternion tailRootRotation = Quaternion.Euler(-40f, 70f, 80f);
            var rotations = new List<float>(frameCount * names.Length * 4);
            for (int frame = 0; frame < frameCount; frame++)
            {
                for (int joint = 0; joint < names.Length; joint++)
                {
                    Quaternion rotation = joint == 0 && frame == 0 ? firstRootRotation :
                        joint == 0 && frame == frameCount - 1 ? tailRootRotation : Quaternion.identity;
                    rotations.Add(rotation.w);
                    rotations.Add(rotation.x);
                    rotations.Add(-rotation.y);
                    rotations.Add(-rotation.z);
                }
            }
            var footContacts = new byte[frameCount * 4];
            footContacts[0] = 1; // Left foot fully planted before lift.
            footContacts[1] = 1;
            footContacts[12 + 2] = 1; // Right foot fully planted after left lifts.
            footContacts[12 + 3] = 1;

            var motion = new KimodoRawMotionData(
                frameCount,
                names.Length,
                frameRate,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0,
                footContacts: footContacts);

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopConstraintJson(
                motion, model, runtimeTrimStartFrame: 4, targetFrameCount: 8,
                runtimeFrameCount: 16, frameRate: frameRate));

            Assert.That(constraints, Has.Count.EqualTo(2));
            Assert.That((string)constraints[0]["type"], Is.EqualTo("root2d"));
            Assert.That((int)constraints[1]["frame_indices"][0], Is.EqualTo(4));
            Assert.That((float)constraints[1]["root_positions"][0][0], Is.EqualTo(-2f));
            Assert.That((int)constraints[1]["frame_indices"][1], Is.EqualTo(11));
            Assert.That((float)constraints[1]["root_positions"][1][0], Is.EqualTo(-18f));
            Assert.That((int)constraints[0]["frame_indices"][1], Is.EqualTo(15));
            Assert.That((float)constraints[0]["smooth_root_2d"][0][0], Is.EqualTo(6f));
            Assert.That((float)constraints[0]["smooth_root_2d"][0][1], Is.EqualTo(-5f));
            Assert.That((float)constraints[0]["smooth_root_2d"][1][0], Is.EqualTo(-26f));
            Assert.That((float)constraints[0]["smooth_root_2d"][1][1], Is.EqualTo(27f));
            JArray anchorRoot = (JArray)constraints[1]["local_joints_rot"][0][0];
            Quaternion anchorProtocolRotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(
                new Vector3((float)anchorRoot[0], (float)anchorRoot[1], (float)anchorRoot[2]));
            Quaternion anchorRotation = new Quaternion(
                anchorProtocolRotation.x,
                -anchorProtocolRotation.y,
                -anchorProtocolRotation.z,
                anchorProtocolRotation.w);
            Assert.That(motion.TryReadUnityLocalRotation(0, 0, names.Length, out Quaternion expectedAnchorRotation), Is.True);
            Assert.That(Vector3.Distance(anchorRotation.eulerAngles, expectedAnchorRotation.eulerAngles), Is.LessThan(1e-3f));
            Assert.That(
                ((JArray)constraints[1]["smooth_root_2d"]).Count,
                Is.EqualTo(((JArray)constraints[1]["frame_indices"]).Count));
            JArray terminalRoot = (JArray)constraints[1]["local_joints_rot"][1][0];
            Quaternion protocolRotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(new Vector3(
                (float)terminalRoot[0], (float)terminalRoot[1], (float)terminalRoot[2]));
            Quaternion terminalRotation = new Quaternion(
                protocolRotation.x, -protocolRotation.y, -protocolRotation.z, protocolRotation.w);
            Quaternion firstYawRotation = Quaternion.Euler(
                0f,
                KimodoMotionMath.ResolvePlanarYawDegrees(firstRootRotation),
                0f);
            Quaternion firstTilt = Quaternion.Inverse(firstYawRotation) * firstRootRotation;
            Vector3 firstTiltEuler = KimodoMotionMath.SignedEulerDegrees(firstTilt);
            Quaternion expectedTerminalRotation = Quaternion.Euler(
                0f,
                KimodoMotionMath.ResolvePlanarYawDegrees(tailRootRotation),
                0f) * Quaternion.Euler(firstTiltEuler.x, 0f, firstTiltEuler.z);
            Assert.That(Quaternion.Angle(terminalRotation, expectedTerminalRotation), Is.LessThan(1e-3f));

            JArray root2D = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildRoot2DConstraintsJson(
                motion, model, new[] { 0, 2 }));
            Assert.That((float)root2D[0]["smooth_root_2d"][1][0], Is.EqualTo(-6f));
        }

        [Test]
        public void BuildLoopConstraintJson_FallsBackToFrameZeroWithoutCompleteContactExchange()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new[] { new Vector3(0f, 1f, 2f), new Vector3(0f, 1f, 3f) };
            var rotations = new List<float>(2 * names.Length * 4);
            for (int frame = 0; frame < 2; frame++)
            {
                for (int joint = 0; joint < names.Length; joint++)
                {
                    rotations.Add(1f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                }
            }
            var motion = new KimodoRawMotionData(
                2,
                names.Length,
                20f,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0,
                footContacts: new byte[8]);

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopConstraintJson(
                motion,
                model,
                runtimeTrimStartFrame: 0,
                targetFrameCount: 2,
                runtimeFrameCount: 4,
                frameRate: 20f));

            Assert.That((float)constraints[1]["root_positions"][0][2], Is.EqualTo(2f).Within(0.001f));
        }

        [Test]
        public void ResolveLoopAnchorFrame_IgnoresLiftAlreadyPresentAtFrameZero()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            const int frameCount = 3;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new[] { Vector3.zero, Vector3.one, Vector3.one * 2f };
            var rotations = new List<float>(frameCount * names.Length * 4);
            for (int i = 0; i < frameCount * names.Length; i++)
            {
                rotations.Add(1f);
                rotations.Add(0f);
                rotations.Add(0f);
                rotations.Add(0f);
            }
            var footContacts = new byte[frameCount * 4];
            footContacts[2] = 1; // Right heel contact; the left foot starts lifted.
            footContacts[4 + 2] = 1;
            footContacts[8 + 2] = 1;
            footContacts[8 + 3] = 1; // Right foot lands later, without a left lift event.
            var motion = new KimodoRawMotionData(
                frameCount,
                names.Length,
                20f,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0,
                footContacts: footContacts);

            Assert.That(
                KimodoRawMotionConstraintBuilder.ResolveLoopAnchorFrameForGeneration(motion),
                Is.EqualTo(0));
        }

        [Test]
        public void BuildLoopWindowConstraintJson_StartsTailOnCoreTerminalFrame()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            const int frameCount = 4;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new[]
            {
                new Vector3(0f, 1f, 0f),
                new Vector3(1f, 1f, 1f),
                new Vector3(2f, 1f, 2f),
                new Vector3(3f, 1f, 3f)
            };
            var rotations = new List<float>(frameCount * names.Length * 4);
            for (int i = 0; i < frameCount * names.Length; i++)
            {
                rotations.Add(1f);
                rotations.Add(0f);
                rotations.Add(0f);
                rotations.Add(0f);
            }
            var motion = new KimodoRawMotionData(
                frameCount,
                names.Length,
                20f,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0);

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopWindowConstraintJson(
                motion,
                model,
                sourceStartFrame: 1,
                windowFrameCount: 3,
                sampleCount: 2,
                sourceOutputStartFrame: 0,
                tailStartFrame: 3,
                runtimeFrameCount: 6,
                frameRate: 20f));

            Assert.That(constraints, Has.Count.EqualTo(1));
            JArray frameIndices = (JArray)constraints[0]["frame_indices"];
            CollectionAssert.AreEqual(new[] { 0, 2, 3, 5 }, frameIndices.ToObject<int[]>());
            JArray rootsInConstraint = (JArray)constraints[0]["root_positions"];
            CollectionAssert.AreEqual(new[] { -1f, 1f, 1f }, rootsInConstraint[0].ToObject<float[]>());
            CollectionAssert.AreEqual(new[] { -3f, 1f, 3f }, rootsInConstraint[1].ToObject<float[]>());
            CollectionAssert.AreEqual(new[] { -3f, 1f, 3f }, rootsInConstraint[2].ToObject<float[]>());
            CollectionAssert.AreEqual(new[] { -5f, 1f, 5f }, rootsInConstraint[3].ToObject<float[]>());

            JArray locked = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopWindowConstraintJson(
                motion,
                model,
                sourceStartFrame: 1,
                windowFrameCount: 3,
                sampleCount: 2,
                sourceOutputStartFrame: 0,
                tailStartFrame: 3,
                runtimeFrameCount: 6,
                frameRate: 20f,
                lockPositionX: true,
                lockPositionY: true));
            Assert.That((float)locked[0]["root_positions"][3][0], Is.EqualTo(-3f).Within(0.001f));
            Assert.That((float)locked[0]["root_positions"][3][1], Is.EqualTo(1f).Within(0.001f));
            Assert.That((float)locked[0]["root_positions"][3][2], Is.EqualTo(5f).Within(0.001f));

            JArray pathAngleAnchored = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopWindowConstraintJson(
                motion,
                model,
                sourceStartFrame: 0,
                windowFrameCount: 3,
                sampleCount: 2,
                sourceOutputStartFrame: 0,
                tailStartFrame: 3,
                runtimeFrameCount: 6,
                frameRate: 20f,
                targetAnchorPositionOverride: new Vector3(10f, 0f, 20f),
                targetAnchorHeadingOverride: Quaternion.Euler(0f, 90f, 0f)));
            Assert.That((float)pathAngleAnchored[0]["root_positions"][2][0], Is.EqualTo(-10f).Within(0.001f));
            Assert.That((float)pathAngleAnchored[0]["root_positions"][2][2], Is.EqualTo(20f).Within(0.001f));
            Assert.That((float)pathAngleAnchored[0]["root_positions"][3][0], Is.EqualTo(-12f).Within(0.001f));
            Assert.That((float)pathAngleAnchored[0]["root_positions"][3][2], Is.EqualTo(18f).Within(0.001f));

            JArray lockedPathAngleAnchor = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopWindowConstraintJson(
                motion,
                model,
                sourceStartFrame: 0,
                windowFrameCount: 3,
                sampleCount: 2,
                sourceOutputStartFrame: 0,
                tailStartFrame: 3,
                runtimeFrameCount: 6,
                frameRate: 20f,
                lockPositionX: true,
                lockRotationY: true,
                targetAnchorPositionOverride: new Vector3(10f, 0f, 20f),
                targetAnchorHeadingOverride: Quaternion.Euler(0f, 90f, 0f)));
            Assert.That((float)lockedPathAngleAnchor[0]["root_positions"][2][0], Is.EqualTo(0f).Within(0.001f));
            Assert.That((float)lockedPathAngleAnchor[0]["root_positions"][2][2], Is.EqualTo(20f).Within(0.001f));
            Assert.That((float)lockedPathAngleAnchor[0]["root_positions"][3][0], Is.EqualTo(-2f).Within(0.001f));
            Assert.That((float)lockedPathAngleAnchor[0]["root_positions"][3][2], Is.EqualTo(22f).Within(0.001f));
            Assert.That(
                Quaternion.Angle(
                    ReadUnityRootRotation(lockedPathAngleAnchor[0]["local_joints_rot"][2][0]),
                    Quaternion.identity),
                Is.LessThan(0.01f));
            Assert.That(
                Quaternion.Angle(
                    ReadUnityRootRotation(lockedPathAngleAnchor[0]["local_joints_rot"][3][0]),
                    Quaternion.identity),
                Is.LessThan(0.01f));

            JArray overlap = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopWindowConstraintJson(
                motion,
                model,
                sourceStartFrame: 1,
                windowFrameCount: 3,
                sampleCount: 3,
                sourceOutputStartFrame: 0,
                tailStartFrame: 2,
                runtimeFrameCount: 5,
                frameRate: 20f,
                targetAnchorPositionOverride: new Vector3(10f, 0f, 20f),
                targetAnchorHeadingOverride: Quaternion.Euler(0f, 90f, 0f)));
            CollectionAssert.AreEqual(
                new[] { 0, 1, 2, 3, 4 },
                overlap[0]["frame_indices"].Values<int>());
            CollectionAssert.AreEqual(
                new[] { -10f, 1f, 20f },
                overlap[0]["root_positions"][2].Values<float>());

            JArray singleSample = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopWindowConstraintJson(
                motion,
                model,
                sourceStartFrame: 0,
                windowFrameCount: 3,
                sampleCount: 1,
                sourceOutputStartFrame: 0,
                tailStartFrame: 2,
                runtimeFrameCount: 5,
                frameRate: 20f));
            CollectionAssert.AreEqual(new[] { 0, 2 }, singleSample[0]["frame_indices"].Values<int>());

            JArray oneFrameWindow = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopWindowConstraintJson(
                motion,
                model,
                sourceStartFrame: 3,
                windowFrameCount: 1,
                sampleCount: 1,
                sourceOutputStartFrame: 0,
                tailStartFrame: 0,
                runtimeFrameCount: 1,
                frameRate: 20f));
            CollectionAssert.AreEqual(new[] { 0 }, oneFrameWindow[0]["frame_indices"].Values<int>());

            Assert.Throws<System.InvalidOperationException>(() =>
                KimodoRawMotionConstraintBuilder.BuildLoopWindowConstraintJson(
                    motion,
                    model,
                    sourceStartFrame: 1,
                    windowFrameCount: 3,
                    sampleCount: 2,
                    sourceOutputStartFrame: 0,
                    tailStartFrame: 1,
                    runtimeFrameCount: 5,
                    frameRate: 20f));
        }

        [Test]
        public void BuildLoopWindowConstraintJson_AppliesLocksToAnchorBeforeUniformWindowOffset()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            const int frameCount = 4;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new[]
            {
                new Vector3(0f, 1f, 0f),
                new Vector3(1f, 1.2f, 1f),
                new Vector3(2f, 1.3f, 2f),
                new Vector3(3f, 1.4f, 3f)
            };
            Quaternion[] rootRotations =
            {
                Quaternion.identity,
                Quaternion.Euler(20f, 10f, 30f),
                Quaternion.Euler(-5f, 30f, 10f),
                Quaternion.Euler(-10f, 50f, -45f)
            };
            var rotations = new List<float>(frameCount * names.Length * 4);
            for (int frame = 0; frame < frameCount; frame++)
            {
                for (int joint = 0; joint < names.Length; joint++)
                {
                    Quaternion rotation = joint == 0 ? rootRotations[frame] : Quaternion.identity;
                    rotations.Add(rotation.w);
                    rotations.Add(rotation.x);
                    rotations.Add(-rotation.y);
                    rotations.Add(-rotation.z);
                }
            }
            var motion = new KimodoRawMotionData(
                frameCount,
                names.Length,
                20f,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0);

            JArray unlocked = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopWindowConstraintJson(
                motion,
                model,
                sourceStartFrame: 1,
                windowFrameCount: 3,
                sampleCount: 3,
                sourceOutputStartFrame: 0,
                tailStartFrame: 3,
                runtimeFrameCount: 6,
                frameRate: 20f));
            JArray locked = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopWindowConstraintJson(
                motion,
                model,
                sourceStartFrame: 1,
                windowFrameCount: 3,
                sampleCount: 3,
                sourceOutputStartFrame: 0,
                tailStartFrame: 3,
                runtimeFrameCount: 6,
                frameRate: 20f,
                lockPositionX: true,
                lockPositionY: true,
                lockPositionZ: true,
                lockRotationX: true,
                lockRotationY: false,
                lockRotationZ: false));

            // Locking changes the target anchor; source samples remain untouched.
            for (int sample = 0; sample < 3; sample++)
            {
                Assert.That(JToken.DeepEquals(
                    unlocked[0]["root_positions"][sample],
                    locked[0]["root_positions"][sample]), Is.True);
                Assert.That(JToken.DeepEquals(
                    unlocked[0]["local_joints_rot"][sample][0],
                    locked[0]["local_joints_rot"][sample][0]), Is.True);
            }

            Quaternion sourceAnchorRotation = rootRotations[1];
            float sourceYawDegrees = KimodoMotionMath.ResolvePlanarYawDegrees(sourceAnchorRotation);
            float targetYawDegrees = KimodoMotionMath.ResolvePlanarYawDegrees(rootRotations[3]);
            Quaternion sourceYawRotation = Quaternion.Euler(0f, sourceYawDegrees, 0f);
            Quaternion targetYawRotation = Quaternion.Euler(0f, targetYawDegrees, 0f);
            Vector3 sourceTiltEuler = KimodoMotionMath.SignedEulerDegrees(
                Quaternion.Inverse(sourceYawRotation) * sourceAnchorRotation);
            Vector3 targetTiltEuler = KimodoMotionMath.SignedEulerDegrees(
                Quaternion.Inverse(targetYawRotation) * rootRotations[3]);
            Quaternion lockedTargetAnchorHeading = targetYawRotation * Quaternion.Euler(
                sourceTiltEuler.x,
                0f,
                targetTiltEuler.z);
            Quaternion lockedRotationOffset = lockedTargetAnchorHeading * Quaternion.Inverse(sourceAnchorRotation);

            Vector3 sourceAnchorPosition = roots[1];
            Vector3 targetAnchorPosition = sourceAnchorPosition;
            Vector3 lockedTranslation = targetAnchorPosition - lockedRotationOffset * sourceAnchorPosition;

            Quaternion unlockedRotationOffset = rootRotations[3] * Quaternion.Inverse(sourceAnchorRotation);
            Vector3 unlockedTranslation = roots[3] - unlockedRotationOffset * sourceAnchorPosition;

            // Each copied sample follows one full anchor-derived rigid transform;
            // no copied constraint receives a separate per-axis overwrite.
            for (int sample = 0; sample < 3; sample++)
            {
                int outputIndex = sample + 3;
                AssertFullBodySampleMatchesRigidTransform(
                    (JObject)locked[0], outputIndex, roots[sample + 1], rootRotations[sample + 1],
                    lockedRotationOffset, lockedTranslation);
                AssertFullBodySampleMatchesRigidTransform(
                    (JObject)unlocked[0], outputIndex, roots[sample + 1], rootRotations[sample + 1],
                    unlockedRotationOffset, unlockedTranslation);
            }
        }

        private static void AssertFullBodySampleMatchesRigidTransform(
            JObject constraint,
            int sampleIndex,
            Vector3 sourcePosition,
            Quaternion sourceRotation,
            Quaternion rotationOffset,
            Vector3 translation)
        {
            Vector3 expectedPosition = rotationOffset * sourcePosition + translation;
            JArray actualPositionJson = (JArray)constraint["root_positions"][sampleIndex];
            Vector3 actualPosition = new Vector3(
                -actualPositionJson[0].Value<float>(),
                actualPositionJson[1].Value<float>(),
                actualPositionJson[2].Value<float>());
            Assert.That(Vector3.Distance(actualPosition, expectedPosition), Is.LessThan(0.001f));

            Quaternion expectedRotation = rotationOffset * sourceRotation;
            Quaternion actualRotation = ReadUnityRootRotation(constraint["local_joints_rot"][sampleIndex][0]);
            Assert.That(Quaternion.Angle(actualRotation, expectedRotation), Is.LessThan(0.01f));
        }

        private static Quaternion ReadUnityRootRotation(JToken protocolAxisAngle)
        {
            Quaternion protocol = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(
                new Vector3(
                    protocolAxisAngle[0].Value<float>(),
                    protocolAxisAngle[1].Value<float>(),
                    protocolAxisAngle[2].Value<float>()));
            return new Quaternion(protocol.x, -protocol.y, -protocol.z, protocol.w);
        }

        [Test]
        public void ApplyLoopRoot2DHeadingOverridesJson_AnchorsCopiedEndpointAndSamplesMissingEndpoint()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            const int frameCount = 4;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new[]
            {
                new Vector3(0f, 1f, 0f),
                new Vector3(1f, 1f, 1f),
                new Vector3(2f, 1f, 2f),
                new Vector3(3f, 1f, 3f)
            };
            var rotations = new List<float>(frameCount * names.Length * 4);
            for (int frame = 0; frame < frameCount; frame++)
            {
                for (int joint = 0; joint < names.Length; joint++)
                {
                    Quaternion rotation = joint == 0 ? Quaternion.Euler(0f, frame * 20f, 0f) : Quaternion.identity;
                    rotations.Add(rotation.w);
                    rotations.Add(rotation.x);
                    rotations.Add(-rotation.y);
                    rotations.Add(-rotation.z);
                }
            }
            var motion = new KimodoRawMotionData(
                frameCount,
                names.Length,
                20f,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0);
            var existingConstraint = new JObject
            {
                ["type"] = "root2d",
                ["frame_indices"] = new JArray(0),
                ["smooth_root_2d"] = new JArray { new JArray(9f, 10f) },
                ["global_root_heading"] = new JArray { new JArray(0f, 1f) }
            };
            string existing = new JArray { existingConstraint }.ToString();

            JArray result = JArray.Parse(KimodoRawMotionConstraintBuilder.ApplyLoopRoot2DHeadingOverridesJson(
                motion,
                model,
                existing,
                sourceStartFrame: 1,
                windowFrameCount: 3,
                sourceOutputStartFrame: 0,
                tailStartFrame: 3,
                runtimeFrameCount: 6));
            JArray sampledStart = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildRoot2DConstraintsJson(
                motion, model, new[] { 1 }));

            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That((float)result[0]["smooth_root_2d"][0][0], Is.EqualTo(9f).Within(0.001f));
            Assert.That((float)result[0]["smooth_root_2d"][0][1], Is.EqualTo(10f).Within(0.001f));
            Assert.That(
                JToken.DeepEquals(result[0]["global_root_heading"][0], sampledStart[0]["global_root_heading"][0]),
                Is.True);
            Assert.That((int)result[1]["frame_indices"][0], Is.EqualTo(5));
            Vector3 rotatedWindowDelta = Quaternion.Euler(0f, 40f, 0f) * new Vector3(2f, 0f, 2f);
            Vector3 expectedTailPosition = new Vector3(3f, 1f, 3f) + rotatedWindowDelta;
            Assert.That((float)result[1]["smooth_root_2d"][0][0], Is.EqualTo(-expectedTailPosition.x).Within(0.001f));
            Assert.That((float)result[1]["smooth_root_2d"][0][1], Is.EqualTo(expectedTailPosition.z).Within(0.001f));
            Vector3 expectedTailForward = Quaternion.Euler(0f, 100f, 0f) * Vector3.forward;
            Assert.That((float)result[1]["global_root_heading"][0][0], Is.EqualTo(expectedTailForward.z).Within(0.001f));
            Assert.That((float)result[1]["global_root_heading"][0][1], Is.EqualTo(-expectedTailForward.x).Within(0.001f));

            JArray locked = JArray.Parse(KimodoRawMotionConstraintBuilder.ApplyLoopRoot2DHeadingOverridesJson(
                motion,
                model,
                string.Empty,
                sourceStartFrame: 1,
                windowFrameCount: 3,
                sourceOutputStartFrame: 0,
                tailStartFrame: 3,
                runtimeFrameCount: 6,
                lockPositionX: true,
                lockPositionY: true,
                lockRotationY: true));
            Assert.That((int)locked[0]["frame_indices"][1], Is.EqualTo(5));
            Assert.That((float)locked[0]["smooth_root_2d"][1][0], Is.EqualTo(-3f).Within(0.001f));
            Assert.That(
                (float)locked[0]["smooth_root_2d"][1][1],
                Is.EqualTo(5f).Within(0.001f));
            JArray sampledTail = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildRoot2DConstraintsJson(
                motion, model, new[] { 3 }));
            Assert.That(
                JToken.DeepEquals(locked[0]["global_root_heading"][0], sampledStart[0]["global_root_heading"][0]),
                Is.True);
            Assert.That(
                JToken.DeepEquals(locked[0]["global_root_heading"][1], sampledTail[0]["global_root_heading"][0]),
                Is.True);
        }

        [Test]
        public void Root2DAnchorHelpers_ReadAndRemovePathAngleTerminalSample()
        {
            string constraintsJson = new JArray
            {
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(0, 4),
                    ["smooth_root_2d"] = new JArray(
                        new JArray(1f, 2f),
                        new JArray(5f, 7f)),
                    ["global_root_heading"] = new JArray(
                        new JArray(1f, 0f),
                        new JArray(0f, -1f))
                }
            }.ToString();

            Assert.That(KimodoRawMotionConstraintBuilder.TryReadRoot2DAnchorJson(
                constraintsJson,
                frame: 4,
                out Vector3 anchorPosition,
                out Quaternion anchorHeading), Is.True);
            Assert.That(anchorPosition, Is.EqualTo(new Vector3(-5f, 0f, 7f)));
            Assert.That(anchorHeading.eulerAngles.y, Is.EqualTo(90f).Within(0.001f));

            JArray remaining = JArray.Parse(KimodoRawMotionConstraintBuilder.RemoveRoot2DFrameJson(
                constraintsJson,
                frame: 4));
            CollectionAssert.AreEqual(new[] { 0 }, remaining[0]["frame_indices"].Values<int>());
            CollectionAssert.AreEqual(new[] { 1f, 2f }, remaining[0]["smooth_root_2d"][0].Values<float>());
            CollectionAssert.AreEqual(new[] { 1f, 0f }, remaining[0]["global_root_heading"][0].Values<float>());
        }

        [Test]
        public void BuildPathDistanceConstraintJson_ScalesInheritedRoot2DPath()
        {
            KimodoRawMotionData motion = CreateMotion(new[]
            {
                Vector3.zero,
                new Vector3(0f, 0f, 1f),
                new Vector3(0f, 0f, 2f),
                new Vector3(0f, 0f, 3f)
            });
            string existing = new JArray
            {
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(0, 1, 2, 3),
                    ["smooth_root_2d"] = new JArray(
                        new JArray(0f, 0f), new JArray(0f, 1f),
                        new JArray(0f, 2f), new JArray(0f, 3f)),
                    ["global_root_heading"] = new JArray(
                        new JArray(0f, 1f), new JArray(0f, 1f),
                        new JArray(0f, 1f), new JArray(0f, 1f))
                }
            }.ToString();

            JArray result = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildPathDistanceConstraintJson(
                motion,
                KimodoMotionModelProfiles.ArdyCoreModelName,
                distanceOverride: 6f,
                runtimeTrimStartFrame: 0,
                targetFrameCount: 4,
                runtimeFrameCount: 4,
                frameRate: 20f,
                existingConstraintsJson: existing));

            Assert.That((float)result[0]["smooth_root_2d"][3][1], Is.EqualTo(6f).Within(0.001f));
            Assert.That(JToken.DeepEquals(
                result[0]["global_root_heading"][0],
                ((JArray)JToken.Parse(existing))[0]["global_root_heading"][0]), Is.True);
        }

        [Test]
        public void BuildPathDistanceConstraintJson_UsesCumulativeRawPathAndSupportsZeroDistance()
        {
            KimodoRawMotionData motion = CreateMotion(new[]
            {
                Vector3.zero,
                new Vector3(0f, 0f, 1f),
                new Vector3(1f, 0f, 1f),
                new Vector3(1f, 0f, 2f)
            });

            JArray scaled = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildPathDistanceConstraintJson(
                motion,
                KimodoMotionModelProfiles.ArdyCoreModelName,
                distanceOverride: 6f,
                runtimeTrimStartFrame: 1,
                targetFrameCount: 4,
                runtimeFrameCount: 6,
                frameRate: 20f,
                existingConstraintsJson: string.Empty));
            JArray scaledFrames = (JArray)scaled[0]["frame_indices"];
            JArray scaledPositions = (JArray)scaled[0]["smooth_root_2d"];
            float scaledPathLength = 0f;
            for (int i = 1; i < scaledPositions.Count; i++)
            {
                Vector2 previous = new Vector2(
                    scaledPositions[i - 1][0].Value<float>(),
                    scaledPositions[i - 1][1].Value<float>());
                Vector2 current = new Vector2(
                    scaledPositions[i][0].Value<float>(),
                    scaledPositions[i][1].Value<float>());
                scaledPathLength += Vector2.Distance(previous, current);
            }
            Assert.That(scaledPathLength, Is.EqualTo(6f).Within(0.001f));
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, scaledFrames.Values<int>().ToArray());

            JArray stationary = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildPathDistanceConstraintJson(
                motion,
                KimodoMotionModelProfiles.ArdyCoreModelName,
                distanceOverride: 0f,
                runtimeTrimStartFrame: 1,
                targetFrameCount: 4,
                runtimeFrameCount: 6,
                frameRate: 20f,
                existingConstraintsJson: string.Empty));
            JArray stationaryPositions = (JArray)stationary[0]["smooth_root_2d"];
            for (int i = 1; i < stationaryPositions.Count; i++)
            {
                Assert.That(stationaryPositions[i][0].Value<float>(), Is.EqualTo(stationaryPositions[0][0].Value<float>()).Within(0.001f));
                Assert.That(stationaryPositions[i][1].Value<float>(), Is.EqualTo(stationaryPositions[0][1].Value<float>()).Within(0.001f));
            }
        }

        [Test]
        public void BuildLoopConstraintJson_RespectsEndpointLockOptions()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new[] { new Vector3(0f, 1f, 0f), new Vector3(0f, 2f, 1f) };
            var rotations = new List<float>(2 * names.Length * 4);
            for (int frame = 0; frame < 2; frame++)
            {
                for (int joint = 0; joint < names.Length; joint++)
                {
                    Quaternion rotation = joint == 0 && frame == 0
                        ? Quaternion.Euler(20f, 10f, 30f)
                        : joint == 0 ? Quaternion.Euler(-40f, 70f, 80f) : Quaternion.identity;
                    rotations.Add(rotation.w);
                    rotations.Add(rotation.x);
                    rotations.Add(-rotation.y);
                    rotations.Add(-rotation.z);
                }
            }
            var motion = new KimodoRawMotionData(
                2, names.Length, 20f, names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model), roots, rotations, rootJointIndex: 0);
            JArray locked = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopConstraintJson(
                motion, model, 1, 2, 4, 20f,
                lockPositionX: false, lockPositionY: true, lockPositionZ: false,
                lockRotationX: true, lockRotationY: false, lockRotationZ: true));
            JArray unlocked = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildLoopConstraintJson(
                motion, model, 1, 2, 4, 20f,
                lockPositionX: false, lockPositionY: false, lockPositionZ: false,
                lockRotationX: false, lockRotationY: false, lockRotationZ: false));
            Assert.That((float)locked[1]["root_positions"][1][1], Is.EqualTo(1f).Within(0.001f));
            Assert.That((float)unlocked[1]["root_positions"][1][1], Is.EqualTo(2f).Within(0.001f));
            Assert.That(JToken.DeepEquals(locked[1]["local_joints_rot"][1][0], unlocked[1]["local_joints_rot"][1][0]), Is.False);
        }

        [Test]
        public void BuildPathAngleConstraintJson_SamplesBoundariesAndExistingFullBodyFrames()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            const int frameCount = 5;
            const float frameRate = 20f;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new Vector3[frameCount];
            var rotations = new List<float>(frameCount * names.Length * 4);
            for (int frame = 0; frame < frameCount; frame++)
            {
                roots[frame] = new Vector3(0f, 1f, frame);
                for (int joint = 0; joint < names.Length; joint++)
                {
                    rotations.Add(1f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                }
            }
            var motion = new KimodoRawMotionData(
                frameCount,
                names.Length,
                frameRate,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0);
            string existing = new JArray(new JObject
            {
                ["type"] = "fullbody",
                ["frame_indices"] = new JArray(4),
                ["root_positions"] = new JArray(new JArray(9f, 8f, 7f)),
                ["local_joints_rot"] = new JArray(new JArray())
            }).ToString();

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildPathAngleConstraintJson(
                motion,
                model,
                pathBeginAngleDegrees: 0f,
                pathEndAngleDegrees: 90f,
                runtimeTrimStartFrame: 2,
                targetFrameCount: frameCount,
                runtimeFrameCount: 9,
                frameRate: frameRate,
                existingConstraintsJson: existing,
                regularFrameInterval: 3));

            CollectionAssert.AreEqual(
                new[] { 0, 2, 3, 4, 6, 8 },
                constraints[0]["frame_indices"].Values<int>());
            float radius = 4f / (Mathf.PI * 0.5f);
            Assert.That((float)constraints[0]["smooth_root_2d"][4][0], Is.EqualTo(-radius).Within(0.001f));
            Assert.That((float)constraints[0]["smooth_root_2d"][4][1], Is.EqualTo(radius).Within(0.001f));
            Assert.That((float)constraints[0]["global_root_heading"][4][0], Is.EqualTo(1f).Within(0.001f));
            Assert.That((float)constraints[0]["global_root_heading"][4][1], Is.EqualTo(0f).Within(0.001f));

            JArray shaped = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildPathAngleConstraintJson(
                motion,
                model,
                pathBeginAngleDegrees: 90f,
                pathEndAngleDegrees: 180f,
                runtimeTrimStartFrame: 2,
                targetFrameCount: frameCount,
                runtimeFrameCount: 9,
                frameRate: frameRate,
                existingConstraintsJson: existing,
                regularFrameInterval: 3));
            Assert.That((float)shaped[0]["global_root_heading"][1][0], Is.EqualTo(1f).Within(0.001f));
            Assert.That((float)shaped[0]["global_root_heading"][1][1], Is.EqualTo(0f).Within(0.001f));
            Assert.That((float)shaped[0]["global_root_heading"][4][0], Is.EqualTo(1f).Within(0.001f));
            Assert.That((float)shaped[0]["global_root_heading"][4][1], Is.EqualTo(0f).Within(0.001f));
            Assert.That(
                JToken.DeepEquals(shaped[0]["global_root_heading"], constraints[0]["global_root_heading"]),
                Is.True);
            Assert.That(
                JToken.DeepEquals(shaped[0]["smooth_root_2d"], constraints[0]["smooth_root_2d"]),
                Is.False);

            JToken pathPositions = constraints[0]["smooth_root_2d"].DeepClone();
            JArray overridden = JArray.Parse(KimodoRawMotionConstraintBuilder.OverrideRoot2DHeadingsJson(
                constraints.ToString(),
                headingDegrees: 0f));
            Assert.That(overridden, Has.Count.EqualTo(1));
            Assert.That(JToken.DeepEquals(overridden[0]["smooth_root_2d"], pathPositions), Is.True);
            foreach (JArray heading in overridden[0]["global_root_heading"].Children<JArray>())
            {
                Assert.That((float)heading[0], Is.EqualTo(1f).Within(0.001f));
                Assert.That((float)heading[1], Is.EqualTo(0f).Within(0.001f));
            }
            JArray fixedDistance = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildPathAngleConstraintJson(
                motion, model, 0f, 90f, 2, frameCount, 9, frameRate, existing,
                regularFrameInterval: 3, distanceOverride: 10f));
            float fixedRadius = 10f / (Mathf.PI * 0.5f);
            Assert.That((float)fixedDistance[0]["smooth_root_2d"][4][0], Is.EqualTo(-fixedRadius).Within(0.001f));
            Assert.That((float)fixedDistance[0]["smooth_root_2d"][4][1], Is.EqualTo(fixedRadius).Within(0.001f));
        }

        [Test]
        public void BuildPathAngleConstraintJson_InterpolatesStageOneRootHeading()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new[] { Vector3.zero, Vector3.forward, Vector3.forward * 2f };
            var rotations = new List<float>(3 * names.Length * 4);
            for (int frame = 0; frame < 3; frame++)
            {
                for (int joint = 0; joint < names.Length; joint++)
                {
                    Quaternion rotation = joint == 0 ? Quaternion.Euler(0f, frame * 90f, 0f) : Quaternion.identity;
                    rotations.Add(rotation.w);
                    rotations.Add(rotation.x);
                    rotations.Add(-rotation.y);
                    rotations.Add(-rotation.z);
                }
            }
            var motion = new KimodoRawMotionData(
                3, names.Length, 20f, names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model), roots, rotations, rootJointIndex: 0);

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildPathAngleConstraintJson(
                motion, model, 0f, 90f, 0, 3, 3, 20f, "[]", regularFrameInterval: 1));

            Assert.That((float)constraints[0]["global_root_heading"][0][0], Is.EqualTo(1f).Within(0.001f));
            Assert.That((float)constraints[0]["global_root_heading"][1][0], Is.EqualTo(0f).Within(0.001f));
            Assert.That((float)constraints[0]["global_root_heading"][1][1], Is.EqualTo(-1f).Within(0.001f));
            Assert.That((float)constraints[0]["global_root_heading"][2][0], Is.EqualTo(-1f).Within(0.001f));
            Assert.That((float)constraints[0]["global_root_heading"][2][1], Is.EqualTo(0f).Within(0.001f));
        }

        [Test]
        public void BuildPathAngleConstraintJson_UsesRelativeOffsetAsSplineOrigin()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new[] { Vector3.zero, Vector3.forward };
            var rotations = new List<float>(2 * names.Length * 4);
            for (int i = 0; i < 2 * names.Length; i++)
            {
                rotations.Add(1f);
                rotations.Add(0f);
                rotations.Add(0f);
                rotations.Add(0f);
            }
            var motion = new KimodoRawMotionData(
                2, names.Length, 20f, names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model), roots, rotations, rootJointIndex: 0);

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildPathAngleConstraintJson(
                motion, model, 0f, 0f, 0, 2, 2, 20f, "[]", rootOriginOverride: new Vector3(4f, 2f, 6f)));

            Assert.That((float)constraints[0]["smooth_root_2d"][0][0], Is.EqualTo(-4f).Within(0.001f));
            Assert.That((float)constraints[0]["smooth_root_2d"][0][1], Is.EqualTo(6f).Within(0.001f));
        }

        [Test]
        public void BuildHeadingOverrideConstraintJson_PreservesLastRootPositionsAndReplacesHeadings()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            const int frameCount = 62;
            const float frameRate = 20f;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new Vector3[frameCount];
            var rotations = new List<float>(frameCount * names.Length * 4);
            Quaternion sourceHeading = Quaternion.Euler(0f, 45f, 0f);
            for (int frame = 0; frame < frameCount; frame++)
            {
                roots[frame] = Vector3.forward * frame;
                for (int joint = 0; joint < names.Length; joint++)
                {
                    Quaternion rotation = joint == 0 ? sourceHeading : Quaternion.identity;
                    rotations.Add(rotation.w);
                    rotations.Add(rotation.x);
                    rotations.Add(-rotation.y);
                    rotations.Add(-rotation.z);
                }
            }
            var motion = new KimodoRawMotionData(
                frameCount,
                names.Length,
                frameRate,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0);
            string existing = new JArray(
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(1),
                    ["root_positions"] = new JArray { new JArray(100f, 0f, 200f) }
                },
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(1),
                    ["smooth_root_2d"] = new JArray { new JArray(-9f, 11f) },
                    ["global_root_heading"] = new JArray { new JArray(1f, 0f) }
                }).ToString();

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildHeadingOverrideConstraintJson(
                motion,
                headingDegrees: 0f,
                runtimeTrimStartFrame: 0,
                targetFrameCount: frameCount,
                runtimeFrameCount: frameCount,
                frameRate: frameRate,
                existingConstraintsJson: existing));

            CollectionAssert.AreEqual(
                new[] { 0, 1, 30, 60, 61 },
                constraints[0]["frame_indices"].Values<int>());
            Assert.That((float)constraints[0]["smooth_root_2d"][1][0], Is.EqualTo(-9f));
            Assert.That((float)constraints[0]["smooth_root_2d"][1][1], Is.EqualTo(11f));
            foreach (JArray heading in constraints[0]["global_root_heading"].Children<JArray>())
            {
                Assert.That((float)heading[0], Is.EqualTo(1f).Within(0.001f));
                Assert.That((float)heading[1], Is.EqualTo(0f).Within(0.001f));
            }
        }

        [Test]
        public void InterpolateFullBodyRootPositionsJson_UsesStage2FrameOffsetAndPreservesPose()
        {
            KimodoRawMotionData motion = CreateMotion(new[]
            {
                new Vector3(1f, 2f, 3f),
                new Vector3(2f, 4f, 6f),
                new Vector3(3f, 6f, 9f)
            });
            var original = new JArray
            {
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(10, 12),
                    ["root_positions"] = new JArray(new JArray(90f, 91f, 92f), new JArray(93f, 94f, 95f)),
                    ["smooth_root_2d"] = new JArray(new JArray(80f, 81f), new JArray(82f, 83f)),
                    ["local_joints_rot"] = new JArray(new JArray(new JArray(0f, 0f, 0f)), new JArray(new JArray(1f, 2f, 3f)))
                },
                new JObject
                {
                    ["type"] = "effector",
                    ["frame_indices"] = new JArray(11),
                    ["root_positions"] = new JArray(new JArray(70f, 71f, 72f)),
                    ["smooth_root_2d"] = new JArray(new JArray(60f, 61f)),
                    ["target_positions"] = new JArray(new JArray(5f, 6f, 7f))
                },
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(10),
                    ["smooth_root_2d"] = new JArray(new JArray(4f, 5f))
                }
            };

            JArray result = JArray.Parse(KimodoRawMotionConstraintBuilder.InterpolateFullBodyRootPositionsJson(
                original.ToString(), motion, runtimeTrimStartFrame: 10));
            JObject fullBody = result.Children<JObject>().Single(item => item.Value<string>("type") == "fullbody");
            JObject effector = result.Children<JObject>().Single(item => item.Value<string>("type") == "effector");
            JObject root2D = result.Children<JObject>().Single(item => item.Value<string>("type") == "root2d");

            Assert.That(JToken.DeepEquals(fullBody["root_positions"][0], new JArray(-1f, 2f, 3f)), Is.True);
            Assert.That(JToken.DeepEquals(fullBody["root_positions"][1], new JArray(-3f, 6f, 9f)), Is.True);
            Assert.That(JToken.DeepEquals(fullBody["smooth_root_2d"][0], new JArray(-1f, 3f)), Is.True);
            Assert.That(JToken.DeepEquals(effector["root_positions"][0], new JArray(-2f, 4f, 6f)), Is.True);
            Assert.That(JToken.DeepEquals(effector["smooth_root_2d"][0], new JArray(-2f, 6f)), Is.True);
            Assert.That(JToken.DeepEquals(fullBody["local_joints_rot"], original[0]["local_joints_rot"]), Is.True);
            Assert.That(JToken.DeepEquals(effector["target_positions"], original[1]["target_positions"]), Is.True);
            Assert.That(JToken.DeepEquals(root2D, original[2]), Is.True);
        }

        [Test]
        public void OverrideEffectiveConstraintHeadingsJson_UpdatesRootYawAndPreservesBodyTilt()
        {
            const float targetHeading = 120f;
            Quaternion sourceUnityRoot = Quaternion.Euler(12f, 35f, -8f);
            JArray rootAxis = EncodeProtocolAxisAngle(sourceUnityRoot);
            var otherJointAxis = new JArray(0.1f, -0.2f, 0.3f);
            var original = new JArray
            {
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(2, 4),
                    ["smooth_root_2d"] = new JArray(new JArray(7f, 8f), new JArray(9f, 10f)),
                    ["global_root_heading"] = new JArray(new JArray(0f, 1f), new JArray(0f, 1f))
                },
                CreateBodyRootConstraint("fullbody", rootAxis, otherJointAxis),
                CreateBodyRootConstraint("effector", rootAxis, otherJointAxis)
            };

            JArray expectedRoot2D = JArray.Parse(KimodoRawMotionConstraintBuilder.OverrideRoot2DHeadingsJson(
                new JArray((JObject)original[0].DeepClone()).ToString(), targetHeading));
            JArray result = JArray.Parse(KimodoRawMotionConstraintBuilder.OverrideEffectiveConstraintHeadingsJson(
                original.ToString(), targetHeading));

            Assert.That(JToken.DeepEquals(result[0]["smooth_root_2d"], original[0]["smooth_root_2d"]), Is.True);
            Assert.That(JToken.DeepEquals(result[0]["global_root_heading"], expectedRoot2D[0]["global_root_heading"]), Is.True);
            for (int i = 1; i < result.Count; i++)
            {
                JObject before = (JObject)original[i];
                JObject after = (JObject)result[i];
                Assert.That(JToken.DeepEquals(after["root_positions"], before["root_positions"]), Is.True);
                for (int axis = 0; axis < 3; axis++)
                {
                    Assert.That(
                        after["local_joints_rot"][0][1][axis].Value<float>(),
                        Is.EqualTo(otherJointAxis[axis].Value<float>()).Within(1e-6f));
                }
                Quaternion actualRoot = DecodeProtocolAxisAngle(after["local_joints_rot"][0][0]);
                float sourceYaw = KimodoMotionMath.ResolvePlanarYawDegrees(sourceUnityRoot);
                Quaternion sourceTilt = Quaternion.Inverse(Quaternion.Euler(0f, sourceYaw, 0f)) * sourceUnityRoot;
                Quaternion expectedRoot = Quaternion.Euler(0f, targetHeading, 0f) * sourceTilt;
                Assert.That(Quaternion.Angle(actualRoot, expectedRoot), Is.LessThan(0.05f));
            }
        }

        private static KimodoRawMotionData CreateMotion(Vector3[] roots)
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var rotations = new List<float>(roots.Length * names.Length * 4);
            for (int i = 0; i < roots.Length * names.Length; i++)
            {
                rotations.Add(1f);
                rotations.Add(0f);
                rotations.Add(0f);
                rotations.Add(0f);
            }
            return new KimodoRawMotionData(
                roots.Length,
                names.Length,
                20f,
                names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots,
                rotations,
                rootJointIndex: 0);
        }

        private static JObject CreateBodyRootConstraint(string type, JArray rootAxis, JArray otherJointAxis)
        {
            return new JObject
            {
                ["type"] = type,
                ["frame_indices"] = new JArray(3),
                ["root_positions"] = new JArray(new JArray(1f, 2f, 3f)),
                ["local_joints_rot"] = new JArray
                {
                    new JArray(rootAxis.DeepClone(), otherJointAxis.DeepClone())
                }
            };
        }

        private static JArray EncodeProtocolAxisAngle(Quaternion unityRotation)
        {
            Quaternion protocolRotation = new Quaternion(
                unityRotation.x, -unityRotation.y, -unityRotation.z, unityRotation.w);
            Vector3 axisAngle = KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(protocolRotation);
            return new JArray(axisAngle.x, axisAngle.y, axisAngle.z);
        }

        private static Quaternion DecodeProtocolAxisAngle(JToken token)
        {
            Quaternion protocolRotation = KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(
                new Vector3(token[0].Value<float>(), token[1].Value<float>(), token[2].Value<float>()));
            return new Quaternion(
                protocolRotation.x, -protocolRotation.y, -protocolRotation.z, protocolRotation.w);
        }

        private static Quaternion Planar(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }
    }
}
