using System.Collections.Generic;
using KimodoBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoRawMotionConstraintBuilderTests
    {
        [Test]
        public void FullBodyBuilderExportsRequestedFrames()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            KimodoRawMotionData motion = CreateMotion(model);

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildFullBodyConstraintsJson(
                motion, model, new[] { 0, 1 }));

            Assert.That(constraints, Has.Count.EqualTo(1));
            Assert.That(constraints[0].Value<string>("type"), Is.EqualTo("fullbody"));
            CollectionAssert.AreEqual(new[] { 0, 1 }, constraints[0]["frame_indices"].ToObject<int[]>());
        }

        [Test]
        public void Root2DBuilderExportsRequestedFramesAndPlanarHeading()
        {
            const string model = KimodoMotionModelProfiles.ArdyCoreModelName;
            KimodoRawMotionData motion = CreateMotion(model);

            JArray constraints = JArray.Parse(KimodoRawMotionConstraintBuilder.BuildRoot2DConstraintsJson(
                motion, model, new[] { 0, 1 }));

            Assert.That(constraints, Has.Count.EqualTo(1));
            Assert.That(constraints[0].Value<string>("type"), Is.EqualTo("root2d"));
            CollectionAssert.AreEqual(new[] { 0, 1 }, constraints[0]["frame_indices"].ToObject<int[]>());
            Assert.That(constraints[0]["global_root_heading"], Is.Not.Null);
        }

        private static KimodoRawMotionData CreateMotion(string model)
        {
            string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            var roots = new[] { Vector3.zero, Vector3.forward };
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

            return new KimodoRawMotionData(
                2, names.Length, 20f, names,
                KimodoRigProfileDatabase.GetParentIndicesForModel(model),
                roots, rotations, rootJointIndex: 0);
        }
    }
}
