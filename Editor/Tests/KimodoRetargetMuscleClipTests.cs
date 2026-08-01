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
    }
}
#endif
