using System.Collections.Generic;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoConstraintNormalizationUtilityTests
    {
        [Test]
        public void NormalizeConstraintOrigin_RoundTripsTargetAvatarRootThroughKimodoAnchor()
        {
            Quaternion targetRootRotation = Quaternion.Euler(8f, 52f, -3f);
            var anchor = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                kimodoRootPosition = new Vector3(13f, 1f, 24f),
                unityRootPos = new Vector3(10f, 7f, 20f),
                unityRootRot = Quaternion.Euler(0f, 35f, 0f),
                hasRootHeading = false,
                localAxisAngles = new List<Vector3>
                {
                    KimodoRuntimeUtility.QuaternionToAxisAngleVector(targetRootRotation)
                }
            };
            KimodoMarkerSampleResult rawAnchor = anchor.Clone();

            KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                new List<KimodoMarkerSampleResult> { anchor },
                out KimodoConstraintNormalizationInfo info,
                out _);

            Quaternion kimodoAnchorRotation = KimodoConstraintNormalizationUtility.ResolveKimodoPlanarRootRotation(rawAnchor);
            Vector3 kimodoAnchorPosition = new Vector3(rawAnchor.kimodoRootPosition.x, 0f, rawAnchor.kimodoRootPosition.z);
            Vector3 rebuiltRootPosition = kimodoAnchorPosition + kimodoAnchorRotation * anchor.kimodoRootPosition;
            Quaternion rebuiltRootRotation = kimodoAnchorRotation *
                KimodoConstraintNormalizationUtility.AxisAngleToQuaternion(anchor.localAxisAngles[0]);

            Assert.That(info.AnchorSample.kimodoRootPosition, Is.EqualTo(rawAnchor.kimodoRootPosition));
            Assert.That(info.AnchorSample.unityRootPos, Is.EqualTo(rawAnchor.unityRootPos));
            Assert.That(Vector3.Distance(rebuiltRootPosition, rawAnchor.kimodoRootPosition), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(rebuiltRootRotation, targetRootRotation), Is.LessThan(1e-4f));
        }

        [Test]
        public void InOutComposer_NormalizesManualConstraintWhenInIsDisabled()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(10f, 1f, 20f),
                unityRootPos = new Vector3(10f, 0f, 20f),
                unityRootRot = Quaternion.identity
            };
            var request = new KimodoInOutConstraintRequest
            {
                Mode = KimodoInOutConstraintMode.None,
                ManualSamples = new List<KimodoMarkerSampleResult> { sample }
            };

            Assert.That(KimodoInOutConstraintComposer.TryBuild(request, out KimodoInOutConstraintResult result, out _, out _), Is.True);
            Assert.That(result.NormalizationInfo.Applied, Is.True);
            Assert.That(result.CombinedSamples[0].kimodoRootPosition, Is.EqualTo(new Vector3(0f, 1f, 0f)));
        }

        [Test]
        public void InOutComposer_NormalizesHandConstraintAndTargetPosition()
        {
            Quaternion rootRotation = Quaternion.Euler(0f, 90f, 0f);
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "right-hand",
                sampleTime = 0.5,
                kimodoRootPosition = new Vector3(10f, 2f, 20f),
                unityRootPos = new Vector3(10f, 2f, 20f),
                unityRootRot = rootRotation,
                hasEndEffectorTargetPosition = true,
                endEffectorTargetPositionRootLocal = Vector3.right,
                localAxisAngles = new List<Vector3>
                {
                    KimodoRuntimeUtility.QuaternionToAxisAngleVector(rootRotation)
                }
            };
            var request = new KimodoInOutConstraintRequest
            {
                Mode = KimodoInOutConstraintMode.None,
                ManualSamples = new List<KimodoMarkerSampleResult> { sample }
            };

            Assert.That(
                KimodoInOutConstraintComposer.TryBuild(request, out KimodoInOutConstraintResult result, out _, out _),
                Is.True);
            Assert.That(result.NormalizationInfo.Applied, Is.True);
            Assert.That(result.NormalizationInfo.AnchorKind, Is.EqualTo(KimodoConstraintNormalizationAnchorKind.EndEffector));
            Assert.That(
                Vector3.Distance(result.CombinedSamples[0].kimodoRootPosition, new Vector3(0f, 2f, 0f)),
                Is.LessThan(1e-5f));

            List<KimodoConstraintJson> constraints = KimodoConstraintJsonExporter.BuildConstraints(result.CombinedSamples);
            Assert.That(constraints, Has.Count.EqualTo(1));
            Assert.That(constraints[0].target_positions, Has.Count.EqualTo(1));
            Assert.That(constraints[0].target_positions[0][0], Is.EqualTo(-1f).Within(1e-5f));
            Assert.That(constraints[0].target_positions[0][1], Is.EqualTo(2f).Within(1e-5f));
            Assert.That(constraints[0].target_positions[0][2], Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void AutoBegin_IsUsedWhenFirstConstraintIsAfterFirstSecond()
        {
            var samples = new List<KimodoMarkerSampleResult>
            {
                new KimodoMarkerSampleResult
                {
                    constraintType = "root2d",
                    sampleTime = 1.25,
                    kimodoRootPosition = new Vector3(10f, 1f, 22f)
                }
            };
            var autoBegin = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(10f, 0f, 20f),
                rootHeading = Vector2.right,
                hasRootHeading = true,
                unityRootPos = new Vector3(10f, 4f, 20f),
                unityRootRot = Quaternion.Euler(0f, 15f, 0f)
            };

            KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                samples,
                autoBegin,
                1.0,
                out KimodoConstraintNormalizationInfo info,
                out _);

            Assert.That(info.Applied, Is.True);
            Assert.That(info.AnchorKind, Is.EqualTo(KimodoConstraintNormalizationAnchorKind.AutoBegin));
            Assert.That(samples, Has.Count.EqualTo(1));
            Vector3 expected = Quaternion.Inverse(Quaternion.Euler(0f, 15f, 0f)) * new Vector3(0f, 1f, 2f);
            Assert.That(Vector3.Distance(samples[0].kimodoRootPosition, expected), Is.LessThan(1e-5f));
        }

        [Test]
        public void RealAnchorInsideFirstSecond_BeatsAutoBegin()
        {
            var realAnchor = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 0.75,
                kimodoRootPosition = new Vector3(2f, 1f, 3f),
                unityRootPos = new Vector3(2f, 1f, 3f),
                unityRootRot = Quaternion.identity
            };
            var autoBegin = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                unityRootPos = new Vector3(10f, 0f, 20f),
                unityRootRot = Quaternion.identity
            };

            KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                new List<KimodoMarkerSampleResult> { realAnchor },
                autoBegin,
                1.0,
                out KimodoConstraintNormalizationInfo info,
                out _);

            Assert.That(info.AnchorKind, Is.EqualTo(KimodoConstraintNormalizationAnchorKind.FullBody));
            Assert.That(info.AnchorSample.sampleTime, Is.EqualTo(0.75).Within(1e-6));
        }

        [Test]
        public void SameFrameFullBodyAnchor_UsesFirstSample()
        {
            var previousBoundary = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(1f, 0f, 0f),
                unityRootPos = new Vector3(1f, 0f, 0f),
                unityRootRot = Quaternion.identity
            };
            var beginMarker = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(5f, 0f, 0f),
                unityRootPos = new Vector3(5f, 0f, 0f),
                unityRootRot = Quaternion.identity
            };

            KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                new List<KimodoMarkerSampleResult> { beginMarker, previousBoundary },
                autoBeginAnchorSample: null,
                anchorWindowSeconds: 1.0,
                out KimodoConstraintNormalizationInfo info,
                out _);

            Assert.That(info.AnchorKind, Is.EqualTo(KimodoConstraintNormalizationAnchorKind.FullBody));
            Assert.That(info.AnchorSample.kimodoRootPosition, Is.EqualTo(beginMarker.kimodoRootPosition));
        }

        [Test]
        public void SameFrameAnchorPriority_IsFullBodyThenEndThenRoot2D()
        {
            var root2d = new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(1f, 0f, 0f)
            };
            var end = new KimodoMarkerSampleResult
            {
                constraintType = "right-hand",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(2f, 0f, 0f)
            };
            var fullbody = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(3f, 0f, 0f)
            };

            KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                new List<KimodoMarkerSampleResult> { root2d, end, fullbody },
                autoBeginAnchorSample: null,
                anchorWindowSeconds: 1.0,
                out KimodoConstraintNormalizationInfo info,
                out _);

            Assert.That(info.AnchorKind, Is.EqualTo(KimodoConstraintNormalizationAnchorKind.FullBody));
            Assert.That(info.AnchorSample.kimodoRootPosition, Is.EqualTo(fullbody.kimodoRootPosition));
        }

        [Test]
        public void SameFrameEndAnchor_BeatsRoot2DAndUsesFirstEnd()
        {
            var root2d = new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(1f, 0f, 0f)
            };
            var firstEnd = new KimodoMarkerSampleResult
            {
                constraintType = "right-hand",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(2f, 0f, 0f)
            };
            var secondEnd = new KimodoMarkerSampleResult
            {
                constraintType = "left-hand",
                sampleTime = 0.0,
                kimodoRootPosition = new Vector3(3f, 0f, 0f)
            };

            KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                new List<KimodoMarkerSampleResult> { root2d, firstEnd, secondEnd },
                autoBeginAnchorSample: null,
                anchorWindowSeconds: 1.0,
                out KimodoConstraintNormalizationInfo info,
                out _);

            Assert.That(info.AnchorKind, Is.EqualTo(KimodoConstraintNormalizationAnchorKind.EndEffector));
            Assert.That(info.AnchorSample.kimodoRootPosition, Is.EqualTo(firstEnd.kimodoRootPosition));
        }

        [Test]
        public void ConstraintAtExactlyOneSecond_DoesNotBeatAutoBegin()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 1.0,
                kimodoRootPosition = new Vector3(4f, 0f, 5f)
            };
            var autoBegin = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                unityRootPos = new Vector3(4f, 0f, 5f),
                unityRootRot = Quaternion.identity
            };

            KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                new List<KimodoMarkerSampleResult> { sample },
                autoBegin,
                1.0,
                out KimodoConstraintNormalizationInfo info,
                out _);

            Assert.That(info.AnchorKind, Is.EqualTo(KimodoConstraintNormalizationAnchorKind.AutoBegin));
        }

        [Test]
        public void AutoBegin_AppliesWithoutAddingConstraintSamples()
        {
            var samples = new List<KimodoMarkerSampleResult>();
            var autoBegin = new KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                unityRootPos = new Vector3(4f, 0f, 5f),
                unityRootRot = Quaternion.identity
            };

            KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                samples,
                autoBegin,
                1.0,
                out KimodoConstraintNormalizationInfo info,
                out _);

            Assert.That(info.Applied, Is.True);
            Assert.That(info.AnchorKind, Is.EqualTo(KimodoConstraintNormalizationAnchorKind.AutoBegin));
            Assert.That(samples, Is.Empty);
            Assert.That(KimodoConstraintJsonExporter.BuildConstraints(samples), Is.Empty);
        }

        [Test]
        public void AutoBeginDisabled_UsesFirstConstraintAfterFirstSecond()
        {
            var sample = new KimodoMarkerSampleResult
            {
                constraintType = "root2d",
                sampleTime = 1.25,
                kimodoRootPosition = new Vector3(4f, 2f, 5f),
                unityRootPos = new Vector3(4f, 0f, 5f),
                unityRootRot = Quaternion.identity
            };
            var request = new KimodoInOutConstraintRequest
            {
                Mode = KimodoInOutConstraintMode.None,
                AutoBeginAnchor = false,
                ManualSamples = new List<KimodoMarkerSampleResult> { sample }
            };

            Assert.That(
                KimodoInOutConstraintComposer.TryBuild(request, out KimodoInOutConstraintResult result, out _, out _),
                Is.True);

            Assert.That(result.NormalizationInfo.Applied, Is.True);
            Assert.That(result.NormalizationInfo.AnchorKind, Is.EqualTo(KimodoConstraintNormalizationAnchorKind.Root2D));
            Assert.That(result.NormalizationInfo.AnchorSample.sampleTime, Is.EqualTo(1.25).Within(1e-6));
            Assert.That(result.CombinedSamples[0].kimodoRootPosition, Is.EqualTo(new Vector3(0f, 2f, 0f)));
        }

        [Test]
        public void PlayableClip_InAndOutDefaultEnabled()
        {
            KimodoPlayableClip clip = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                Assert.That(clip.enableInConstraint, Is.True);
                Assert.That(clip.enableOutConstraint, Is.True);
                Assert.That(clip.autoBeginAnchor, Is.True);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

    }
}
