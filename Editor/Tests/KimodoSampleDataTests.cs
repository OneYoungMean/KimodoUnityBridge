using System;
using System.Collections.Generic;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using TimelineInject;
using UnityEngine;
using KimodoUnityBridge;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoSampleDataTests
    {
        [TestCase(true, false, false, "root2d")]
        [TestCase(true, true, false, "fullbody")]
        [TestCase(true, false, true, "effector")]
        [TestCase(true, true, true, "fullbody")]
        [TestCase(false, true, false, "fullbody")]
        [TestCase(false, true, true, "fullbody")]
        public void CommandPoint_InfersTypeOnlyFromExplicitInputs(bool root, bool pose, bool effector, string mode)
        {
            var input = new JObject { ["frame"] = 30 };
            if (root) input["root"] = JObject.Parse("{position:[3,2,1]}");
            if (pose) input["pose"] = JObject.Parse("{track:'Pose',index:0}");
            if (effector) input["effector"] = JArray.Parse("[{joint:'right_hand',position:[4,3,2]}]");
            var sample = CreateFullBody(1, 0.75f, true);
            EnableAllEffectors(sample);

            KimodoUnityBridge.Command.command_context.ApplyPointConstraintData(input, sample, 0.5, 0);

            Assert.That(sample.constraintMode, Is.EqualTo(mode));
            Assert.That(sample.sampleTime, Is.EqualTo(0.5));
            Assert.That(sample.enableMask.rightHand, Is.EqualTo(effector));
            Assert.That(sample.enableMask.leftHand, Is.False);
            Assert.That(sample.sampleData.data[KimodoSampleDataLayout.BodyMuscleOffset], Is.EqualTo(0.75f));
            CollectionAssert.AreEqual(new[] { mode == "effector" ? "right-hand" : mode }, ResolveTypes(sample));
        }

        [TestCase("{}")]
        [TestCase("{effector:[{joint:'right_hand',position:[1,2,3]}]}")]
        [TestCase("{root:{}}")]
        [TestCase("{root:{position:[1,2,3]},effector:[]}")]
        [TestCase("{root:{position:[1,2,3]},effector:[{joint:'right_hand'}]}")]
        [TestCase("{root:{rotation:[0,0,0,1]}}")]
        [TestCase("{root:{position:[1,2]}}")]
        [TestCase("{root:{position:[1,2,3]},effector:[{joint:'head',position:[1,2,3]}]}")]
        [TestCase("{root2d:{position:[1,2]}}")]
        [TestCase("{root:{position:[1,2,3]},effect:{right_hand:{position:[1,2,3]}}}")]
        [TestCase("{root:{position:[1,2,3]},type:'root2d'}")]
        public void CommandPoint_RejectsInvalidAndObsoleteInputs(string json)
        {
            Assert.Throws<InvalidOperationException>(() => KimodoUnityBridge.Command.command_context
                .ApplyPointConstraintData(JObject.Parse(json), CreateFullBody(1, 0, true), 0, 0));
        }

        [Test]
        public void CommandPoint_RotationOnlyRetainsSampledWorldPositionsAndEnablesRotation()
        {
            var sample = CreateFullBody(1, 0.75f, true);
            sample.rootOverride.t = new Vector3(2, 1, 3);
            sample.rootOverride.q = Quaternion.Euler(0, 45, 0);
            sample.effectors.rightHand.t = new Vector3(1, 2, 3);
            sample.effectors.rightHand.space = KimodoConstraintSpace.Local;
            Vector3 handWorld = KimodoConstraintSpaceUtility.ToWorldPosition(
                sample.effectors.rightHand.t, sample.effectors.rightHand.space, sample.rootOverride);
            var input = JObject.Parse("{root:{rotation:[10,80,30]},effector:[{joint:'right_hand',rotation:[20,40,60]}]}");

            KimodoUnityBridge.Command.command_context.ApplyPointConstraintData(input, sample, 0, 0);

            Assert.That(sample.rootOverride.t, Is.EqualTo(new Vector3(2, 1, 3)));
            Assert.That(Quaternion.Angle(sample.rootOverride.q, Quaternion.Euler(10, 80, 30)), Is.LessThan(0.001f));
            Assert.That(Vector3.Distance(sample.effectors.rightHand.t, handWorld), Is.LessThan(0.001f));
            Assert.That(Quaternion.Angle(sample.effectors.rightHand.q, Quaternion.Euler(20, 40, 60)), Is.LessThan(0.001f));
            Assert.That(sample.enableMask.rootHeading, Is.True);
            Assert.That(sample.effectors.rightHand.enableRotation, Is.True);
        }

        [Test]
        public void CommandPoint_PositionOnlyRetainsSampledRotations()
        {
            var sample = CreateFullBody(1, 0.75f, true);
            sample.rootOverride.q = Quaternion.Euler(10, 30, 20);
            sample.effectors.rightHand.q = Quaternion.Euler(40, 20, 10);
            sample.effectors.rightHand.space = KimodoConstraintSpace.World;
            var input = JObject.Parse("{root:{position:[4,5,6]},effector:[{joint:'right_hand',position:[7,8,9]}]}");

            KimodoUnityBridge.Command.command_context.ApplyPointConstraintData(input, sample, 0, 0);

            Assert.That(sample.rootOverride.t, Is.EqualTo(new Vector3(4, 5, 6)));
            Assert.That(sample.effectors.rightHand.t, Is.EqualTo(new Vector3(7, 8, 9)));
            Assert.That(Quaternion.Angle(sample.rootOverride.q, Quaternion.Euler(10, 30, 20)), Is.LessThan(0.001f));
            Assert.That(Quaternion.Angle(sample.effectors.rightHand.q, Quaternion.Euler(40, 20, 10)), Is.LessThan(0.001f));
            Assert.That(sample.effectors.rightHand.enableRotation, Is.False);
        }
        [Test, Category("BridgeRegression")]
        public void SampleValid_BlocksOuterConstraintConstruction()
        {
            KimodoMarkerSampleResult invalid = CreateFullBody(1, 0.5f, true);
            invalid.sampleValid = false;

            Assert.Throws<InvalidOperationException>(() => KimodoConstraintInternal.GetConstraintInternal(
                invalid, KimodoConstraintRigType.Soma77, null));
            Assert.Throws<InvalidOperationException>(() =>
                KimodoConstraintInternal.ValidateSample(invalid));
        }

        [Test, Category("BridgeRegression")]
        public void CommandConstraintFrames_RejectDuplicatesAndPrioritizeRootPathCollision()
        {
            var duplicatePoints = new JArray
            {
                new JObject { ["frame"] = 2, ["root"] = JObject.Parse("{position:[0,1,0]}") },
                new JObject { ["frame"] = 2, ["pose"] = JObject.Parse("{track:'Pose',index:0}") }
            };
            StringAssert.Contains("undefined", KimodoUnityBridge.Command.command_context
                .ValidateUniqueConstraintFrames(duplicatePoints, 10));

            var rootPathWinsFrameCheck = new JArray
            {
                new JObject { ["frame"] = 0, ["root_path"] = new JObject() },
                new JObject { ["frame"] = 0, ["root"] = JObject.Parse("{position:[0,1,0]}") }
            };
            StringAssert.Contains("root_path has priority", KimodoUnityBridge.Command.command_context
                .ValidateUniqueConstraintFrames(rootPathWinsFrameCheck, 10));

            var pointBeforeRootPath = new JArray
            {
                new JObject { ["frame"] = 0, ["root"] = JObject.Parse("{position:[0,1,0]}") },
                new JObject { ["frame"] = 0, ["root_path"] = new JObject() }
            };
            StringAssert.Contains("root_path has priority", KimodoUnityBridge.Command.command_context
                .ValidateUniqueConstraintFrames(pointBeforeRootPath, 10));

            var endpointCollision = new JArray
            {
                new JObject { ["frame"] = 9, ["root"] = JObject.Parse("{position:[0,1,0]}") },
                new JObject { ["frame"] = 0, ["root_path"] = new JObject() }
            };
            StringAssert.Contains("root_path has priority", KimodoUnityBridge.Command.command_context
                .ValidateUniqueConstraintFrames(endpointCollision, 10));
        }

        [Test]
        public void Export_SameFrameSamplesKeepTheirConcreteFamiliesAndRoots()
        {
            var root = CreateFullBody(1, 0, true);
            root.constraintMode = "root2d";
            root.rootOverride.t = new Vector3(1, 2, 3);
            var pose = CreateFullBody(2, 0.5f, true);
            pose.rootOverride.t = new Vector3(4, 5, 6);
            var effector = CreateFullBody(3, 0, true);
            effector.constraintMode = "effector";
            effector.rootOverride.t = new Vector3(7, 8, 9);
            effector.enableMask.rightHand = true;
            var context = new KimodoConstraintExportContext
            {
                projectedPoseProjector = CreateProjectedTestPose
            };

            var output = KimodoConstraintJsonExporter.BuildConstraints(
                new[] { root, pose, effector }, context);

            CollectionAssert.AreEqual(new[] { "root2d", "fullbody", "right-hand" },
                output.ConvertAll(item => item.type));
            CollectionAssert.AreEqual(new[] { -1f, 3f }, output[0].smooth_root_2d[0]);
            CollectionAssert.AreEqual(new[] { -4f, 5f, 6f }, output[1].root_positions[0]);
            CollectionAssert.AreEqual(new[] { -7f, 8f, 9f }, output[2].root_positions[0]);
        }

        [Test]
        public void SampleDataLayout_Uses70ValuesAndRoundTripsTransforms()
        {
            float[] data = KimodoSampleDataLayout.CreateBuffer();
            Assert.That(data, Has.Length.EqualTo(70));
            KimodoSampleDataLayout.SetTransform(
                data,
                KimodoSampleDataLayout.RootTqOffset,
                new Vector3(1f, 2f, 3f),
                Quaternion.Euler(0f, 45f, 0f));

            KimodoSampleDataLayout.GetTransform(
                data,
                KimodoSampleDataLayout.RootTqOffset,
                out Vector3 position,
                out Quaternion rotation);

            Assert.That(position, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(Quaternion.Angle(rotation, Quaternion.Euler(0f, 45f, 0f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void AutoSample_UsesMarkerEnableIntentAndSampleValidity()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.autoSample = true;
                marker.ConstraintMode = KimodoConstraintMode.FullBody;
                marker.SampleData.enableMask = new KimodoConstraintMask
                {
                    rootHeading = true
                };
                var sampled = new KimodoMarkerSampleResult
                {
                    sampleData = new MuscleSample(),
                    enableMask = new KimodoConstraintMask()
                };

                KimodoMarkerSampleResult normalized =
                    KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, sampled);

                Assert.That(normalized.enableMask.rootHeading, Is.True);
                Assert.That(normalized.enableMask.leftHand, Is.False);
                Assert.That(normalized.sampleValid, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void AutoSample_KeepsNewEffectorTargetsInLocalSpace()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.autoSample = true;
                marker.ConstraintMode = KimodoConstraintMode.Effector;
                KimodoRigidTransform authoredRoot = marker.SampleData.rootOverride;
                authoredRoot.position = new Vector3(2f, 1f, -3f);
                authoredRoot.rotation = Quaternion.Euler(0f, 40f, 0f);
                marker.SampleData.rootOverride = authoredRoot;

                var sampled = new KimodoMarkerSampleResult
                {
                    sampleValid = true,
                    constraintMode = "effector",
                    rootOverride = new KimodoRigidTransform
                    {
                        position = authoredRoot.position,
                        rotation = authoredRoot.rotation
                    },
                    effectors = new KimodoConstraintEffectors
                    {
                        leftHand = new KimodoRigidTransform
                        {
                            position = new Vector3(3f, 2f, -1f),
                            rotation = Quaternion.Euler(10f, 20f, 30f),
                            space = KimodoConstraintSpace.World
                        }
                    }
                };
                Vector3 sampledWorldPosition = sampled.effectors.leftHand.position;
                Quaternion sampledWorldRotation = sampled.effectors.leftHand.rotation;

                Assert.That(KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                    marker, sampled, out string error), Is.True, error);

                KimodoRigidTransform stored = marker.SampleData.effectors.leftHand;
                Assert.That(stored.space, Is.EqualTo(KimodoConstraintSpace.Local));
                Assert.That(Vector3.Distance(
                    KimodoConstraintSpaceUtility.ToWorldPosition(stored.position, stored.space,
                        marker.SampleData.rootOverride), sampledWorldPosition), Is.LessThan(0.001f));
                Assert.That(Quaternion.Angle(
                    KimodoConstraintSpaceUtility.ToWorldRotation(stored.rotation, stored.space,
                        marker.SampleData.rootOverride), sampledWorldRotation), Is.LessThan(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void EffectorSpace_LocalWorldRoundTripPreservesPose()
        {
            KimodoRigidTransform root = new KimodoRigidTransform
            {
                position = new Vector3(3f, 1f, -2f),
                rotation = Quaternion.Euler(0f, 45f, 0f)
            };
            KimodoRigidTransform value = new KimodoRigidTransform
            {
                position = new Vector3(1f, 2f, 3f),
                rotation = Quaternion.Euler(10f, 20f, 30f),
                enableRotation = true,
                space = KimodoConstraintSpace.Local
            };
            Vector3 originalPosition = value.position;
            Quaternion originalRotation = value.rotation;
            KimodoConstraintSpaceUtility.ConvertSpace(value, root, KimodoConstraintSpace.World);
            Vector3 worldPosition = value.position;
            Quaternion worldRotation = value.rotation;
            KimodoConstraintSpaceUtility.ConvertSpace(value, root, KimodoConstraintSpace.Local);
            Assert.That(Vector3.Distance(value.position, originalPosition), Is.LessThan(0.001f));
            Assert.That(Quaternion.Angle(value.rotation, originalRotation), Is.LessThan(0.001f));
            Assert.That(Vector3.Distance(KimodoConstraintSpaceUtility.ToWorldPosition(value.position, value.space, root), worldPosition), Is.LessThan(0.001f));
            Assert.That(Quaternion.Angle(KimodoConstraintSpaceUtility.ToWorldRotation(value.rotation, value.space, root), worldRotation), Is.LessThan(0.001f));
        }

        [Test]
        public void RootEditDoesNotModifyEffectorPayload()
        {
            KimodoMarkerSampleResult sample = new KimodoMarkerSampleResult
            {
                rootOverride = new KimodoRigidTransform { position = Vector3.zero, rotation = Quaternion.identity },
                effectors = new KimodoConstraintEffectors
                {
                    leftHand = new KimodoRigidTransform { position = new Vector3(1f, 2f, 3f), rotation = Quaternion.Euler(1f, 2f, 3f) }
                }
            };
            Vector3 position = sample.effectors.leftHand.position;
            Quaternion rotation = sample.effectors.leftHand.rotation;
            sample.rootOverride.position = new Vector3(4f, 0f, 5f);
            sample.rootOverride.rotation = Quaternion.Euler(0f, 90f, 0f);
            Assert.That(sample.effectors.leftHand.position, Is.EqualTo(position));
            Assert.That(Quaternion.Angle(sample.effectors.leftHand.rotation, rotation), Is.LessThan(0.001f));
        }

        [Test]
        public void CommandAndUiCanonicalSamplesPreserveConcreteIntent()
        {
            var commandSample = CreateFullBody(1, 0.75f, true);
            commandSample.constraintMode = "effector";
            commandSample.rootOverride = new KimodoUnityBridge.KimodoRigidTransform
            {
                t = new Vector3(1f, 2f, 3f),
                q = Quaternion.Euler(0f, 35f, 0f)
            };
            commandSample.effectors.leftHand.t = new Vector3(2f, 3f, 4f);
            commandSample.enableMask = new KimodoConstraintMask
            {
                rootHeading = true,
                leftHand = true
            };

            var marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.autoSample = false;
                marker.ConstraintMode = KimodoConstraintMode.Effector;
                Assert.That(
                    KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                        marker, commandSample.Clone(), out string error),
                    Is.True, error);

                KimodoMarkerSampleResult uiSample = marker.SampleData;
                KimodoMarkerSampleResult commandCanonical =
                    commandSample.Clone();

                Assert.That(uiSample.sampleValid, Is.EqualTo(commandCanonical.sampleValid));
                Assert.That(uiSample.constraintMode, Is.EqualTo(commandCanonical.constraintMode));
                Assert.That(uiSample.enableMask.leftHand, Is.EqualTo(commandCanonical.enableMask.leftHand));
                Assert.That(uiSample.enableMask.rootHeading, Is.EqualTo(commandCanonical.enableMask.rootHeading));
                Assert.That(uiSample.effectors.leftHand.t, Is.EqualTo(commandCanonical.effectors.leftHand.t));
                Assert.That(uiSample.rootOverride.t, Is.EqualTo(commandCanonical.rootOverride.t));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void NewConstraintMarker_DefaultsOnlyItsFullBodyIntent()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                Assert.That(marker.SampleData.sampleValid, Is.True);
                Assert.That(marker.SampleData.enableMask.rootHeading, Is.True);
                Assert.That(marker.SampleData.enableMask.AnyEndEffector, Is.False);
                Assert.That(marker.SampleData.effectors.leftHand.space, Is.EqualTo(KimodoConstraintSpace.Local));
                Assert.That(marker.SampleData.effectors.rightHand.space, Is.EqualTo(KimodoConstraintSpace.Local));
                Assert.That(marker.SampleData.effectors.leftFoot.space, Is.EqualTo(KimodoConstraintSpace.Local));
                Assert.That(marker.SampleData.effectors.rightFoot.space, Is.EqualTo(KimodoConstraintSpace.Local));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void ConstraintWriteback_PreservesDraggedRoot2DOverride()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.autoSample = false;
                marker.ConstraintMode = KimodoConstraintMode.Root2D;
                marker.SampleData.rootOverride = new KimodoUnityBridge.KimodoRigidTransform
                {
                    position = new Vector3(1f, 2f, 3f),
                    rotation = Quaternion.Euler(0f, 15f, 0f)
                };

                KimodoMarkerSampleResult dragged = marker.SampleData.Clone();
                dragged.rootOverride.position = new Vector3(8f, 9f, 10f);
                dragged.rootOverride.rotation = Quaternion.Euler(10f, 25f, 30f);

                Assert.That(
                    KimodoBridge.Editor.KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                        marker, dragged, out string error),
                    Is.True,
                    error);
                Assert.That(marker.SampleData.rootOverride.position, Is.EqualTo(new Vector3(8f, 9f, 10f)));
                Assert.That(
                    Quaternion.Angle(marker.SampleData.rootOverride.rotation, Quaternion.Euler(10f, 25f, 30f)),
                    Is.LessThan(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void Root2DMode_IgnoresStaleEffectorMasks()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintMode = "root2d",
                rootOverride = KimodoUnityBridge.KimodoRigidTransform.Identity,
                enableMask = new KimodoConstraintMask
                {
                    leftHand = true,
                    rightHand = true,
                    leftFoot = true,
                    rightFoot = true
                },
            };

            Assert.That(KimodoConstraintPosePipeline.IsRootOnlySample(sample), Is.True);
        }

        [TestCase(false), TestCase(true)]
        public void RootAndEffector_SurviveMarkerSerializationAndClone(bool withPose)
        {
            var support = CreateFullBody(1, 0.75f, true);
            support.constraintMode = withPose ? "fullbody" : "effector";
            support.rootOverride.t = new Vector3(1f, 0.9f, 2f);
            support.enableMask = new KimodoConstraintMask { rightHand = true };
            support.effectors.rightHand = new KimodoRigidTransform { t = new Vector3(4f, 1.1f, 6f), space = KimodoConstraintSpace.World };
            var input = new JObject
            {
                ["root"] = new JObject { ["position"] = new JArray(4f, 0.9f, 5f), ["rotation"] = new JArray(0f, 90f, 0f) },
                ["effector"] = new JArray(new JObject { ["joint"] = "right_hand", ["position"] = new JArray(4f, 1.1f, 6f) }),
            };
            if (withPose) input["pose"] = new JObject { ["track"] = "P", ["index"] = 0 };
            KimodoUnityBridge.Command.command_context.ApplyPointConstraintData(input, support, 0, 0);
            var marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            var restored = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.SampleData = support;
                UnityEditor.EditorJsonUtility.FromJsonOverwrite(UnityEditor.EditorJsonUtility.ToJson(marker), restored);
                KimodoMarkerSampleResult roundTrip = restored.SampleData.Clone();
                Assert.That(restored.ConstraintMode, Is.EqualTo(withPose ? KimodoConstraintMode.FullBody : KimodoConstraintMode.Effector));
                Assert.That(roundTrip.constraintMode, Is.EqualTo(withPose ? "fullbody" : "effector"));
                Assert.That(roundTrip.rootOverride.t, Is.EqualTo(new Vector3(4f, 0.9f, 5f)));
                Assert.That(roundTrip.effectors.rightHand.t, Is.EqualTo(support.effectors.rightHand.t));
                Assert.That(roundTrip.sampleData.data[KimodoSampleDataLayout.BodyMuscleOffset], Is.EqualTo(0.75f));
                CollectionAssert.AreEqual(new[] { withPose ? "fullbody" : "right-hand" }, ResolveTypes(roundTrip));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
                UnityEngine.Object.DestroyImmediate(restored);
            }
        }

        [TestCase("mix"), TestCase("unknown"), TestCase("")]
        public void UnsupportedMode_IsNeverGuessedFromMasksOrMarkerDefault(string mode)
        {
            var sample = CreateFullBody(1, 0f, true);
            sample.constraintMode = mode;
            EnableAllEffectors(sample);
            Assert.Throws<InvalidOperationException>(() =>
                KimodoConstraintInternal.ValidateSample(sample));
            Assert.Throws<InvalidOperationException>(() => ResolveTypes(sample));
            var marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                Assert.Throws<InvalidOperationException>(() => marker.SampleData = sample);
                Assert.That(marker.ConstraintMode, Is.EqualTo(KimodoConstraintMode.FullBody));
                Assert.That(marker.SampleData.constraintMode, Is.EqualTo("fullbody"));
                Assert.Throws<InvalidOperationException>(() => marker.ConstraintMode = (KimodoConstraintMode)3);
                marker.SampleData.constraintMode = mode;
                Assert.DoesNotThrow(() => marker.CommitSampleData());
                Assert.That(marker.SampleData.constraintMode, Is.EqualTo("fullbody"));
                marker.ConstraintMode = KimodoConstraintMode.FullBody;
            }
            finally { UnityEngine.Object.DestroyImmediate(marker); }
        }

        [Test]
        public void MarkerMode_RepairsAnUnsupportedLegacySampleDataMode()
        {
            var marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.SampleData.constraintMode = "mix";
                Assert.DoesNotThrow(() => marker.CommitSampleData());
                Assert.That(marker.SampleData.constraintMode, Is.EqualTo("fullbody"));

                marker.SampleData.constraintMode = "root2d";
                Assert.DoesNotThrow(() => marker.CommitSampleData());
                Assert.That(marker.SampleData.constraintMode, Is.EqualTo("fullbody"));
            }
            finally { UnityEngine.Object.DestroyImmediate(marker); }
        }

        [Test]
        public void MissingRoot_IsNotReplacedByIdentityDuringValidation()
        {
            var sample = CreateFullBody(1, 0f, true);
            sample.constraintMode = "effector";
            sample.enableMask.rightHand = true;
            sample.rootOverride = null;
            Assert.Throws<InvalidOperationException>(() =>
                KimodoConstraintInternal.ValidateSample(sample));
            Assert.Throws<InvalidOperationException>(() => ResolveTypes(sample));
        }

        private static KimodoMarkerSampleResult CreateFullBody(
            long creationOrder,
            float firstMuscle,
            bool valid)
        {
            float[] data = KimodoSampleDataLayout.CreateBuffer();
            data[KimodoSampleDataLayout.BodyMuscleOffset] = firstMuscle;
            return new KimodoMarkerSampleResult
            {
                sampleData = KimodoSampleDataLayout.FromBuffer(data),
                enableMask = new KimodoConstraintMask(),
                constraintMode = "fullbody",
                sampleValid = valid,
                sampleTime = 0,
                creationOrder = creationOrder,
                enabled = true
            };
        }

        private static void EnableAllEffectors(KimodoMarkerSampleResult sample)
        {
            sample.enableMask.leftHand = true;
            sample.enableMask.rightHand = true;
            sample.enableMask.leftFoot = true;
            sample.enableMask.rightFoot = true;
        }

        private static string[] ResolveTypes(KimodoMarkerSampleResult sample)
        {
            var context = new KimodoConstraintExportContext
            {
                projectedPoseProjector = sample => CreateProjectedTestPose(sample)
            };
            KimodoConstraintInternal[] internals = KimodoConstraintInternal.GetConstraintInternal(
                sample,
                KimodoConstraintRigType.Unknown,
                context);
            return Array.ConvertAll(
                internals,
                item => item.ToJsonObject(0.0, null, 30.0).type);
        }

        private static KimodoConstraintProjectedPose CreateProjectedTestPose(
            KimodoMarkerSampleResult sample)
        {
            return new KimodoConstraintProjectedPose
            {
                profileRootPosition = sample.rootOverride?.t ?? Vector3.zero,
                jointNames = new[] { "Hips", "LeftHand", "RightHand", "LeftFoot", "RightFoot" },
                jointPositions = new[]
                {
                    sample.rootOverride?.t ?? Vector3.zero,
                    sample.effectors?.leftHand?.t ?? Vector3.zero,
                    sample.effectors?.rightHand?.t ?? Vector3.zero,
                    sample.effectors?.leftFoot?.t ?? Vector3.zero,
                    sample.effectors?.rightFoot?.t ?? Vector3.zero
                },
                jointRotations = new[]
                {
                    Quaternion.identity,
                    Quaternion.identity,
                    Quaternion.identity,
                    Quaternion.identity,
                    Quaternion.identity
                },
                localJointAngles = new List<Vector3> { Vector3.zero }
            };
        }
    }
}
