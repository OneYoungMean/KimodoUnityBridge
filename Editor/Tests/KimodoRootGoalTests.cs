using System.Collections.Generic;
using KimodoUnityBridge;
using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoRootGoalTests
    {
        [Test]
        public void InverseLossOnFirstFrame_CancelsPlaybackLoss()
        {
            Quaternion rootRotation = Quaternion.Euler(8f, 35f, -4f);
            var source = new KimodoConstraintInternalData
            {
                rootPosition = new Vector3(1f, 2f, 3f),
                localJointAxisAngles = new List<Vector3>
                {
                    KimodoConstraintRotationUtility.QuaternionToAxisAngleVector(rootRotation)
                }
            };
            var loss = new KimodoRigidTransform
            {
                t = new Vector3(0.2f, 0.5f, -0.1f),
                q = Quaternion.Euler(12f, 0f, 6f)
            };

            KimodoConstraintInternalData compensated =
                KimodoRuntimeConstraints.ApplyInverseRootGoalLoss(source, loss);
            Vector3 displayedPosition = loss.q * compensated.rootPosition + loss.t;
            Quaternion displayedRotation = loss.q *
                KimodoConstraintRotationUtility.AxisAngleVectorToQuaternion(
                    compensated.localJointAxisAngles[0]);

            Assert.That(Vector3.Distance(displayedPosition, source.rootPosition), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(displayedRotation, rootRotation), Is.LessThan(1e-4f));
        }

        [Test]
        public void RootGoalLoss_IsCommittedAndConsumedWithItsConstraintRevision()
        {
            var constraints = new KimodoRuntimeConstraints();
            constraints.Stage(new KimodoMarkerSampleResult { constraintMode = "root2d" });
            constraints.StageRootGoalLoss(new KimodoRigidTransform
            {
                t = Vector3.up,
                q = Quaternion.Euler(10f, 0f, 0f)
            });

            Assert.That(constraints.Commit(), Is.True);
            int revision = constraints.PendingRevision;
            Assert.That(constraints.BuildRootGoalLossForGeneration(isArdy: false), Is.Not.Null);
            Assert.That(constraints.BuildRootGoalLossForGeneration(isArdy: true), Is.Null);

            constraints.CompleteGeneration(isArdy: false, consumedRevision: revision);
            Assert.That(constraints.BuildRootGoalLossForGeneration(isArdy: false), Is.Null);
        }

        [Test]
        public void ComposeSameFrame_PreservesRootOverrideAfterEffectors()
        {
            var root = new KimodoMarkerSampleResult
            {
                sampleTime = 0.0,
                rootOverrideAfterEffectors = true,
                rootOverride = new KimodoRigidTransform { t = Vector3.right, q = Quaternion.identity },
                enableMask = new KimodoConstraintMask { rootPosition = true },
                validMask = new KimodoConstraintMask { rootPosition = true }
            };
            var effector = new KimodoMarkerSampleResult
            {
                sampleTime = 0.0,
                enableMask = new KimodoConstraintMask { leftHand = true },
                validMask = new KimodoConstraintMask { leftHand = true }
            };

            List<KimodoMarkerSampleResult> composed = KimodoConstraintSampleComposer.ComposeCanonicalSamples(
                new[] { root, effector }, 30.0);

            Assert.That(composed, Has.Count.EqualTo(1));
            Assert.That(composed[0].rootOverrideAfterEffectors, Is.True);
        }
    }
}
