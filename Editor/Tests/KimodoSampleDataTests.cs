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
            Assert.That(composed[0].enableMask.muscle49, Is.False);
            Assert.That(composed[0].sampleData[KimodoSampleDataLayout.BodyMuscleOffset], Is.EqualTo(0f));
        }

        [Test]
        public void ChannelMask_HeadingRequiresRootPosition()
        {
            var mask = new KimodoSampleChannelMask
            {
                root2DHeading = true,
                root2DPosition = false
            };
            mask.NormalizeDependencies();
            Assert.That(mask.root2DHeading, Is.False);
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
                marker.SampleData.enableMask.root2DPosition = true;

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
        public void Composer_MixExpandsBackToProtocolFamilies()
        {
            KimodoMarkerSampleResult sample = CreateFullBody(1, 0.5f, true);
            sample.constraintMode = "mix";
            sample.enableMask.root2DPosition = true;
            sample.enableMask.root2DHeading = true;
            sample.enableMask.root2DPosition = true;
            sample.root2DOverride.t = new Vector3(3f, 0f, 4f);

            var expanded = KimodoConstraintSampleComposer.ExpandProtocolSamples(
                new[] { sample },
                60.0);

            Assert.That(expanded, Is.Not.Empty);
            Assert.That(expanded.Exists(item => item.constraintType == "fullbody"), Is.True);
            Assert.That(expanded.Exists(item => item.constraintType == "root2d"), Is.True);
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
                enableMask = new KimodoSampleChannelMask
                {
                    muscle49 = valid,
                    rootTQ = valid,
                    leftFootTQ = valid,
                    rightFootTQ = valid
                },
                constraintMode = "fullbody",
                constraintType = "fullbody",
                sampleTime = 0,
                creationOrder = creationOrder,
                enabled = true
            };
        }
    }
}
