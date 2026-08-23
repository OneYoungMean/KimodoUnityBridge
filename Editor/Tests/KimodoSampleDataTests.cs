using System;
using System.Collections.Generic;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoSampleDataTests
    {
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
        public void Composer_LastCreatedInvalidChannelWinsWithoutFallback()
        {
            KimodoMarkerSampleResult first = CreateFullBody(1, 0.25f, true);
            KimodoMarkerSampleResult lastInvalid = CreateFullBody(2, 9f, false);

            var composed = KimodoConstraintSampleComposer.ComposeCanonicalSamples(
                new[] { first, lastInvalid },
                60.0);

            Assert.That(composed, Has.Count.EqualTo(1));
            Assert.That(composed[0].enableMask.muscle, Is.False);
            Assert.That(composed[0].sampleData.data[KimodoSampleDataLayout.BodyMuscleOffset], Is.EqualTo(0f));
        }

        [Test]
        public void MissingValidMask_DoesNotFallBackToEnableMask()
        {
            var sample = new KimodoMarkerSampleResult
            {
                enableMask = new KimodoConstraintMask { muscle = true },
                validMask = null,
                constraintMode = "fullbody"
            };
            Assert.That(KimodoConstraintMask.FromSample(sample).muscle, Is.False);
            Assert.That(ResolveTypes(sample), Is.Empty);
        }

        [Test]
        public void ConstraintWriteback_PreservesDraggedRoot2DOverride()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                marker.autoSample = false;
                marker.ConstraintMode = KimodoConstraintMode.Root2D;
                marker.SampleData.root2DOverride = new CharacterAnimationCli.Unity.KimodoRigidTransform
                {
                    position = new Vector3(1f, 2f, 3f),
                    rotation = Quaternion.Euler(0f, 15f, 0f)
                };
                marker.SampleData.enableMask.rootPosition = true;
                marker.SampleData.validMask.rootPosition = true;

                KimodoMarkerSampleResult dragged = marker.SampleData.Clone();
                dragged.root2DOverride.position = new Vector3(8f, 9f, 10f);
                dragged.root2DOverride.rotation = Quaternion.Euler(10f, 25f, 30f);

                Assert.That(
                    KimodoBridge.Editor.KimodoMarkerSamplingEditorUtility.TryWriteConstraintMarkerSample(
                        marker, dragged, out string error),
                    Is.True,
                    error);
                Assert.That(marker.SampleData.root2DOverride.position, Is.EqualTo(new Vector3(8f, 9f, 10f)));
                Assert.That(
                    Quaternion.Angle(marker.SampleData.root2DOverride.rotation, Quaternion.Euler(10f, 25f, 30f)),
                    Is.LessThan(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void Internal_MixFullBodyWinsAndDoesNotMutateEffectorMask()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.constraintMode = "mix";
            sample.enableMask.rootPosition = true;
            sample.enableMask.rootHeading = true;
            sample.validMask.rootPosition = true;
            sample.validMask.rootHeading = true;
            EnableAllEffectors(sample);
            sample.root2DOverride.t = new Vector3(3f, 0f, 4f);
            sample = KimodoConstraintSampleComposer.ComposeCanonicalSamples(
                new[] { sample }, 30.0)[0];

            CollectionAssert.AreEqual(new[] { "fullbody" }, ResolveTypes(sample));
            Assert.That(sample.enableMask.leftHand, Is.True);
            Assert.That(sample.enableMask.rightHand, Is.True);
            Assert.That(sample.enableMask.leftFoot, Is.True);
            Assert.That(sample.enableMask.rightFoot, Is.True);
        }

        [Test]
        public void Internal_MixEmitsAllEffectorsBeforeRoot2DInFixedOrder()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.constraintMode = "mix";
            sample.validMask.muscle = false;
            sample.enableMask.rootPosition = true;
            sample.validMask.rootPosition = true;
            EnableAllEffectors(sample);

            CollectionAssert.AreEqual(
                new[] { "left-hand", "right-hand", "left-foot", "right-foot" },
                ResolveTypes(sample));
        }

        [Test]
        public void Internal_MixFallsThroughToRoot2DWhenHigherFamiliesAreInvalid()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.constraintMode = "mix";
            sample.validMask.muscle = false;
            sample.enableMask.rootPosition = true;
            sample.validMask.rootPosition = true;

            CollectionAssert.AreEqual(new[] { "root2d" }, ResolveTypes(sample));
        }

        [Test]
        public void Internal_ValidMaskIsNotOverriddenByPayloadHeuristics()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.constraintMode = "effector";
            sample.enableMask.leftHand = true;
            sample.validMask.leftHand = true;
            sample.effectors.leftHand.t = new Vector3(float.NaN, float.PositiveInfinity, 0f);

            CollectionAssert.AreEqual(new[] { "left-hand" }, ResolveTypes(sample));

            sample.validMask.leftHand = false;
            Assert.That(ResolveTypes(sample), Is.Empty);
        }

        [Test]
        public void Composer_KeepsEffectorSupportPoseValidWithoutEnablingFullBody()
        {
            KimodoMarkerSampleResult effector = CreateFullBody(1, 0.75f, true);
            effector.constraintMode = "left-hand";
            effector.enableMask.leftHand = true;
            effector.validMask.leftHand = true;
            var root = new KimodoMarkerSampleResult
            {
                constraintMode = "root2d",
                creationOrder = 2,
                enableMask = new KimodoConstraintMask { rootPosition = true },
                validMask = new KimodoConstraintMask { rootPosition = true }
            };

            KimodoMarkerSampleResult composed = KimodoConstraintSampleComposer.ComposeCanonicalSamples(
                new[] { effector, root }, 30.0)[0];

            Assert.That(composed.enableMask.muscle, Is.False);
            Assert.That(composed.validMask.muscle, Is.True);
            Assert.That(
                composed.sampleData.data[KimodoSampleDataLayout.BodyMuscleOffset],
                Is.EqualTo(0.75f));
            CollectionAssert.AreEqual(new[] { "left-hand" }, ResolveTypes(composed));
        }

        [Test]
        public void Internal_ModeLimitsFamiliesAndRoot2DRequiresPosition()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.enableMask.rootPosition = true;
            sample.validMask.rootPosition = true;
            sample.enableMask.leftHand = true;
            sample.validMask.leftHand = true;

            sample.constraintMode = "effector";
            CollectionAssert.AreEqual(new[] { "left-hand" }, ResolveTypes(sample));

            sample.constraintMode = "ik";
            CollectionAssert.AreEqual(new[] { "left-hand" }, ResolveTypes(sample));

            sample.constraintMode = "root2d";
            CollectionAssert.AreEqual(new[] { "root2d" }, ResolveTypes(sample));

            sample.enableMask.rootPosition = false;
            sample.validMask.rootPosition = false;
            sample.enableMask.rootHeading = true;
            sample.validMask.rootHeading = true;
            Assert.That(ResolveTypes(sample), Is.Empty);

            KimodoMarkerSampleResult defaultMode = CreateFullBody(1, 0.5f, true);
            defaultMode.constraintMode = string.Empty;
            CollectionAssert.AreEqual(new[] { "fullbody" }, ResolveTypes(defaultMode));
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
                enableMask = new KimodoConstraintMask
                {
                    muscle = valid,
                    rootTQ = valid,
                    leftFootTQ = valid,
                    rightFootTQ = valid
                },
                constraintMode = "fullbody",
                validMask = new KimodoConstraintMask
                {
                    muscle = valid,
                    rootTQ = valid,
                    leftFootTQ = valid,
                    rightFootTQ = valid
                },
                sampleTime = 0,
                creationOrder = creationOrder,
                enabled = true
            };
        }

        private static void EnableAllEffectors(KimodoMarkerSampleResult sample)
        {
            sample.enableMask.leftHand = sample.validMask.leftHand = true;
            sample.enableMask.rightHand = sample.validMask.rightHand = true;
            sample.enableMask.leftFoot = sample.validMask.leftFoot = true;
            sample.enableMask.rightFoot = sample.validMask.rightFoot = true;
        }

        private static string[] ResolveTypes(KimodoMarkerSampleResult sample)
        {
            var context = new KimodoConstraintExportContext
            {
                localJointAngleProjector = _ => new List<Vector3> { Vector3.zero }
            };
            KimodoConstraintInternal[] internals = KimodoConstraintInternal.GetConstraintInternal(
                sample,
                KimodoConstraintRigType.Unknown,
                context);
            return Array.ConvertAll(
                internals,
                item => item.ToJsonObject(0.0, null, 30.0).type);
        }
    }
}
