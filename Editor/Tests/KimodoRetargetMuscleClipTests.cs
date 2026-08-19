#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using NUnit.Framework;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoRetargetMuscleClipTests
    {
        private static readonly int[] ExpectedMuscleIndices =
        {
            0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14,
            21, 22, 23, 24, 25, 26, 27, 28,
            29, 30, 31, 32, 33, 34, 35, 36,
            37, 38, 39, 40, 41, 42, 43, 44, 45,
            46, 47, 48, 49, 50, 51, 52, 53, 54
        };

        [Test]
        public void WriteMuscleClip_ExportsOnly49BodyMuscles()
        {
            var clip = new AnimationClip { frameRate = 30f };
            try
            {
                var pose = new HumanPose
                {
                    bodyPosition = Vector3.zero,
                    bodyRotation = Quaternion.identity,
                    muscles = new float[HumanTrait.MuscleCount]
                };
                for (int i = 0; i < pose.muscles.Length; i++)
                {
                    pose.muscles[i] = i;
                }

                var samples = new List<MuscleSample>
                {
                    new MuscleSample
                    {
                        pose = pose,
                        leftFootRotation = Quaternion.identity,
                        rightFootRotation = Quaternion.identity,
                        leftHandRotation = Quaternion.identity,
                        rightHandRotation = Quaternion.identity
                    }
                };

                Assert.That(
                    KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(samples, clip, out string error),
                    Is.True,
                    error);

                EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
                var actual = new Dictionary<string, float>(StringComparer.Ordinal);
                for (int i = 0; i < bindings.Length; i++)
                {
                    EditorCurveBinding binding = bindings[i];
                    if (binding.type == typeof(Animator) &&
                        !binding.propertyName.StartsWith("Root", StringComparison.Ordinal) &&
                        !binding.propertyName.StartsWith("LeftFoot", StringComparison.Ordinal) &&
                        !binding.propertyName.StartsWith("RightFoot", StringComparison.Ordinal))
                    {
                        actual[binding.propertyName] = AnimationUtility.GetEditorCurve(clip, binding).Evaluate(0f);
                    }
                }

                string[] muscleNames = HumanTrait.MuscleName;
                Assert.That(actual, Has.Count.EqualTo(ExpectedMuscleIndices.Length));
                for (int i = 0; i < ExpectedMuscleIndices.Length; i++)
                {
                    int unityIndex = ExpectedMuscleIndices[i];
                    string propertyName = KimodoRetargetClipWriter.GetAnimatorMusclePropertyName(muscleNames[unityIndex]);
                    Assert.That(actual.ContainsKey(propertyName), Is.True, propertyName);
                    Assert.That(actual[propertyName], Is.EqualTo(unityIndex).Within(1e-5f), propertyName);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void WriteMuscleClip_DoesNotExportHandIkGoals()
        {
            var clip = new AnimationClip { frameRate = 30f };
            try
            {
                MuscleSample sample = CreateRootRotationSample(Quaternion.identity);
                sample.leftHandPosition = new Vector3(1f, 2f, 3f);
                sample.leftHandRotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
                sample.rightHandPosition = new Vector3(4f, 5f, 6f);
                sample.rightHandRotation = new Quaternion(0.4f, 0.5f, 0.6f, 0.7f);

                Assert.That(
                    KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(new[] { sample }, clip, out string error),
                    Is.True,
                    error);

                Assert.That(HasAnimatorCurve(clip, "LeftHandT.x"), Is.False);
                Assert.That(HasAnimatorCurve(clip, "LeftHandQ.w"), Is.False);
                Assert.That(HasAnimatorCurve(clip, "RightHandT.z"), Is.False);
                Assert.That(HasAnimatorCurve(clip, "RightHandQ.y"), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void WriteMuscleClip_ExportsFootIkGoals()
        {
            var clip = new AnimationClip { frameRate = 30f };
            try
            {
                MuscleSample sample = CreateRootRotationSample(Quaternion.identity);
                sample.leftFootPosition = new Vector3(1f, 2f, 3f);
                sample.leftFootRotation = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
                sample.rightFootPosition = new Vector3(4f, 5f, 6f);
                sample.rightFootRotation = new Quaternion(0.4f, 0.5f, 0.6f, 0.7f);

                Assert.That(
                    KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(new[] { sample }, clip, out string error),
                    Is.True,
                    error);

                Assert.That(ReadAnimatorKey(clip, "LeftFootT.x"), Is.EqualTo(1f).Within(1e-5f));
                Assert.That(ReadAnimatorKey(clip, "LeftFootQ.w"), Is.EqualTo(0.9f).Within(1e-5f));
                Assert.That(ReadAnimatorKey(clip, "RightFootT.z"), Is.EqualTo(6f).Within(1e-5f));
                Assert.That(ReadAnimatorKey(clip, "RightFootQ.y"), Is.EqualTo(0.5f).Within(1e-5f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void WriteMuscleClip_AlignsRootQuaternionHemisphere()
        {
            var clip = new AnimationClip { frameRate = 30f };
            try
            {
                var samples = new List<MuscleSample>
                {
                    CreateRootRotationSample(new Quaternion(-0.7f, 0f, 0f, -0.7f)),
                    CreateRootRotationSample(new Quaternion(0.7f, 0f, 0f, 0.7f))
                };

                Assert.That(
                    KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(samples, clip, out string error),
                    Is.True,
                    error);

                Quaternion first = ReadRootQuaternion(clip, 0);
                Quaternion second = ReadRootQuaternion(clip, 1);
                Assert.That(Quaternion.Dot(first, second), Is.GreaterThan(0f));
                Assert.That(second.x, Is.LessThan(0f));
                Assert.That(second.w, Is.LessThan(0f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void RemoveTimelinePlanarOffset_ConvertsTargetOffsetToSourceScale()
        {
            MuscleSample sample = CreateRootRotationSample(Quaternion.identity);
            sample.pose.bodyPosition = new Vector3(100f, 0f, 100f);

            KimodoRetargetToolsEditor.RemoveTimelinePlanarOffsetFromMuscleSamples(
                new[] { sample },
                new Vector3(100f, 0f, 100f),
                Quaternion.identity,
                sourceHumanScale: 2f,
                targetHumanScale: 1f);

            Assert.That(Vector3.Distance(sample.pose.bodyPosition, Vector3.zero), Is.LessThan(1e-5f));
        }

        [Test]
        public void RemoveTimelinePlanarOffset_SubtractsOffsetBeforeInverseYaw()
        {
            Quaternion targetYaw = Quaternion.Euler(0f, 90f, 0f);
            Vector3 expectedPosition = new Vector3(3f, 2f, 4f);
            MuscleSample sample = CreateRootRotationSample(targetYaw * Quaternion.Euler(0f, 25f, 0f));
            sample.pose.bodyPosition = new Vector3(10f, 0f, 20f) + targetYaw * expectedPosition;

            KimodoRetargetToolsEditor.RemoveTimelinePlanarOffsetFromMuscleSamples(
                new[] { sample },
                new Vector3(10f, 50f, 20f),
                targetYaw,
                sourceHumanScale: 1f,
                targetHumanScale: 1f);

            Assert.That(Vector3.Distance(sample.pose.bodyPosition, expectedPosition), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(sample.pose.bodyRotation, Quaternion.Euler(0f, 25f, 0f)), Is.LessThan(1e-4f));
        }

        private static MuscleSample CreateRootRotationSample(Quaternion rootRotation)
        {
            return new MuscleSample
            {
                pose = new HumanPose
                {
                    bodyPosition = Vector3.zero,
                    bodyRotation = rootRotation,
                    muscles = new float[HumanTrait.MuscleCount]
                },
                leftFootRotation = Quaternion.identity,
                rightFootRotation = Quaternion.identity,
                leftHandRotation = Quaternion.identity,
                rightHandRotation = Quaternion.identity
            };
        }

        private static Quaternion ReadRootQuaternion(AnimationClip clip, int keyIndex)
        {
            return new Quaternion(
                ReadRootKey(clip, "RootQ.x", keyIndex),
                ReadRootKey(clip, "RootQ.y", keyIndex),
                ReadRootKey(clip, "RootQ.z", keyIndex),
                ReadRootKey(clip, "RootQ.w", keyIndex));
        }

        private static float ReadRootKey(AnimationClip clip, string propertyName, int keyIndex)
        {
            AnimationCurve curve = AnimationUtility.GetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), propertyName));
            Assert.That(curve, Is.Not.Null, propertyName);
            Assert.That(curve.length, Is.GreaterThan(keyIndex), propertyName);
            return curve.keys[keyIndex].value;
        }

        private static float ReadAnimatorKey(AnimationClip clip, string propertyName)
        {
            return ReadRootKey(clip, propertyName, 0);
        }

        private static bool HasAnimatorCurve(AnimationClip clip, string propertyName)
        {
            return AnimationUtility.GetEditorCurve(
                clip,
                EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), propertyName)) != null;
        }
    }
}
#endif
