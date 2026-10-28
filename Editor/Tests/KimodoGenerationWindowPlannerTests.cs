using System.Collections.Generic;
using System.Linq;
using KimodoBridge;
using KimodoBridge.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Newtonsoft.Json;
using UnityEngine;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoGenerationWindowPlannerTests
    {
        [Test]
        public void FifteenSecondPlan_PreservesConstraintsAndInterpolatesBothOverlapRoots()
        {
            const int fps = 30;
            var constraints = new List<KimodoGlobalRawConstraint>
            {
                Anchor(3 * fps, KimodoRawConstraintKind.Root2D,
                    new Vector2(0f, 0f), Vector2.up,
                    new JObject { ["root2d"] = new JObject { ["position"] = new JArray(0, 0) } }),
                Anchor(6 * fps, KimodoRawConstraintKind.FullBody,
                    new Vector2(6f, 0f), Vector2.up,
                    new JObject { ["pose"] = new JObject { ["track"] = "test.Poses", ["index"] = 0 } }),
                Anchor(9 * fps, KimodoRawConstraintKind.Root2D,
                    new Vector2(9f, 3f), Vector2.right,
                    new JObject { ["root2d"] = new JObject { ["position"] = new JArray(9, 3) } }),
                Anchor(12 * fps, KimodoRawConstraintKind.Effector,
                    new Vector2(12f, 3f), Vector2.right,
                    new JObject { ["effect"] = new JObject { ["right_hand"] = new JObject { ["position"] = new JArray(1, 2, 3) } } })
            };

            List<KimodoGenerationWindow> windows = KimodoGenerationWindowPlanner.Plan(
                totalFrames: 15 * fps,
                frameRate: fps,
                maxRequestFrames: 10 * fps,
                overlapFrames: fps,
                minimumCoreFrames: 3 * fps,
                maximumCoreFrames: 8 * fps,
                preferredCoreFrames: 6 * fps,
                constraints: constraints,
                constraintGuardFrames: fps / 2);

            Assert.That(windows, Has.Count.EqualTo(2));
            Assert.That(windows.All(window => window.CoreFrameCount >= 3 * fps && window.CoreFrameCount <= 8 * fps), Is.True);
            Assert.That(windows.All(window => window.RequestFrameCount <= 10 * fps), Is.True);
            Assert.That(windows[0].RequestEndFrame - windows[1].RequestStartFrame, Is.EqualTo(fps));
            Assert.That(constraints.Any(item =>
                Mathf.Abs(item.Frame - windows[0].CoreEndFrame) <= fps / 2), Is.False,
                "The selected cut must avoid the authored constraint guard bands.");

            List<KimodoWindowConstraint> left = KimodoGenerationWindowPlanner.MapConstraints(windows[0], constraints);
            List<KimodoWindowConstraint> right = KimodoGenerationWindowPlanner.MapConstraints(windows[1], constraints);
            CollectionAssert.AreEqual(
                new[] { KimodoRawConstraintKind.Root2D, KimodoRawConstraintKind.FullBody },
                left.Select(item => item.Kind));
            CollectionAssert.AreEqual(
                new[] { KimodoRawConstraintKind.Root2D, KimodoRawConstraintKind.Effector },
                right.Select(item => item.Kind));
            CollectionAssert.AreEqual(
                constraints.Where(item => item.Frame >= windows[0].RequestStartFrame && item.Frame < windows[0].RequestEndFrame)
                    .Select(item => item.Frame - windows[0].RequestStartFrame),
                left.Select(item => item.LocalFrame));
            CollectionAssert.AreEqual(
                constraints.Where(item => item.Frame >= windows[1].RequestStartFrame && item.Frame < windows[1].RequestEndFrame)
                    .Select(item => item.Frame - windows[1].RequestStartFrame),
                right.Select(item => item.LocalFrame));

            Assert.That(KimodoGenerationWindowPlanner.TryGetOverlapBoundaryFrames(
                windows[0], windows[1], out int overlapStart, out int overlapEnd), Is.True);
            Assert.That(overlapEnd - overlapStart, Is.EqualTo(fps - 1));
            Assert.That(overlapStart, Is.GreaterThan(constraints[1].Frame));
            Assert.That(overlapEnd, Is.LessThan(constraints[2].Frame));
            Assert.That(KimodoGenerationWindowPlanner.TryInterpolateRoot2DForOverlap(
                constraints,
                overlapStart,
                overlapEnd,
                out KimodoGlobalRawConstraint startRoot,
                out KimodoGlobalRawConstraint endRoot), Is.True);
            Assert.That(startRoot.Kind, Is.EqualTo(KimodoRawConstraintKind.Root2D));
            Assert.That(endRoot.Kind, Is.EqualTo(KimodoRawConstraintKind.Root2D));
            Assert.That(startRoot.RootPosition.x, Is.EqualTo(overlapStart / (float)fps).Within(0.001f));
            Assert.That(startRoot.RootPosition.y, Is.EqualTo((overlapStart - 180) / 30f).Within(0.001f));
            Assert.That(endRoot.RootPosition.x, Is.EqualTo(overlapEnd / (float)fps).Within(0.001f));
            Assert.That(endRoot.RootPosition.y, Is.EqualTo((overlapEnd - 180) / 30f).Within(0.001f));

            List<KimodoWindowConstraint> leftWithOverlapRoots = KimodoGenerationWindowPlanner.MapConstraints(
                windows[0], constraints.Append(startRoot).Append(endRoot));
            Assert.That(leftWithOverlapRoots.Any(item =>
                item.LocalFrame == overlapStart - windows[0].RequestStartFrame &&
                item.Kind == KimodoRawConstraintKind.Root2D), Is.True);
            Assert.That(leftWithOverlapRoots.Any(item =>
                item.LocalFrame == overlapEnd - windows[0].RequestStartFrame &&
                item.Kind == KimodoRawConstraintKind.Root2D), Is.True);
        }

        [Test]
        public void PreviousWindowOverlapFullBody_ConstrainsBothOverlapEndpoints()
        {
            const string model = "SOMA30";
            const int frameRate = 30;
            const int frameCount = 60;
            string[] jointNames = KimodoRigProfileDatabase.GetJointNamesForModel(model);
            int[] jointParents = KimodoRigProfileDatabase.GetParentIndicesForModel(model);
            var roots = new Vector3[frameCount];
            var rotations = new List<float>(frameCount * jointNames.Length * 4);
            for (int frame = 0; frame < frameCount; frame++)
            {
                roots[frame] = new Vector3(frame, 0f, frame * 2f);
                for (int joint = 0; joint < jointNames.Length; joint++)
                {
                    rotations.Add(1f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                    rotations.Add(0f);
                }
            }
            var previousMotion = new KimodoRawMotionData(
                frameCount,
                jointNames.Length,
                frameRate,
                jointNames,
                jointParents,
                roots,
                rotations,
                rootJointIndex: 0);
            var previousWindow = new KimodoGenerationWindow
            {
                CoreStartFrame = 100,
                CoreEndFrame = 145,
                RequestStartFrame = 100,
                RequestEndFrame = 160
            };
            var currentWindow = new KimodoGenerationWindow
            {
                CoreStartFrame = 145,
                CoreEndFrame = 205,
                RequestStartFrame = 130,
                RequestEndFrame = 220
            };

            Assert.That(KimodoGenerationWindowPlanner.TryGetOverlapBoundaryFrames(
                previousWindow, currentWindow, out int overlapStart, out int overlapEnd), Is.True);
            string json = KimodoPlayableClipGenerationExecutionService.BuildPreviousWindowOverlapFullBodyConstraintsJson(
                previousMotion,
                model,
                previousWindow,
                currentWindow,
                overlapStart,
                overlapEnd,
                frameRate);
            JArray constraints = JArray.Parse(json);

            Assert.That(constraints, Has.Count.EqualTo(1));
            Assert.That(constraints[0].Value<string>("type"), Is.EqualTo("fullbody"));
            CollectionAssert.AreEqual(new[] { 0, 29 }, constraints[0]["frame_indices"].Values<int>());
            Assert.That(constraints[0]["root_positions"], Has.Count.EqualTo(2));
            Assert.That(constraints[0]["root_positions"][0][0].Value<float>(), Is.EqualTo(-30f));
            Assert.That(constraints[0]["root_positions"][1][0].Value<float>(), Is.EqualTo(-59f));
            Assert.That(constraints[0]["local_joints_rot"], Has.Count.EqualTo(2));

            string fourSampleJson = KimodoPlayableClipGenerationExecutionService.BuildPreviousWindowOverlapFullBodyConstraintsJson(
                previousMotion,
                model,
                previousWindow,
                currentWindow,
                overlapStart,
                overlapEnd,
                frameRate,
                sampleCount: 4);
            JObject fourSampleConstraint = JArray.Parse(fourSampleJson)[0] as JObject;
            CollectionAssert.AreEqual(new[] { 0, 10, 19, 29 }, fourSampleConstraint["frame_indices"].Values<int>());
            Assert.That(fourSampleConstraint["root_positions"], Has.Count.EqualTo(4));
            Assert.That(fourSampleConstraint["local_joints_rot"], Has.Count.EqualTo(4));
        }

        [Test]
        public void WindowConstraintsJson_MapsFourAuthoredConstraintTypesToLocalFrames()
        {
            const int fps = 30;
            string rawConstraints = new JArray
            {
                Constraint("root2d", 3 * fps, "smooth_root_2d", new JArray(1, 2)),
                Constraint("fullbody", 6 * fps, "root_positions", new JArray(3, 4, 5)),
                Constraint("root2d", 9 * fps, "smooth_root_2d", new JArray(6, 7)),
                Constraint("right-hand", 12 * fps, "target_positions", new JArray(8, 9, 10))
            }.ToString(Formatting.None);

            List<KimodoGenerationWindow> windows = KimodoGenerationWindowPlanner.Plan(
                totalFrames: 15 * fps,
                frameRate: fps,
                maxRequestFrames: 10 * fps,
                overlapFrames: fps,
                minimumCoreFrames: 3 * fps,
                maximumCoreFrames: 8 * fps,
                preferredCoreFrames: 6 * fps,
                constraints: new List<KimodoGlobalRawConstraint>(),
                constraintGuardFrames: 0);

            var first = JArray.Parse(KimodoGenerationWindowPlanner.BuildWindowConstraintsJson(windows[0], rawConstraints));
            var second = JArray.Parse(KimodoGenerationWindowPlanner.BuildWindowConstraintsJson(windows[1], rawConstraints));

            CollectionAssert.AreEqual(new[] { "root2d", "fullbody" }, first.Values<JObject>().Select(x => x.Value<string>("type")));
            CollectionAssert.AreEqual(new[] { "root2d", "right-hand" }, second.Values<JObject>().Select(x => x.Value<string>("type")));
            CollectionAssert.AreEqual(new[] { 90, 180 }.Select(frame => frame - windows[0].RequestStartFrame),
                first.SelectMany(x => x["frame_indices"].Values<int>()));
            CollectionAssert.AreEqual(new[] { 270, 360 }.Select(frame => frame - windows[1].RequestStartFrame),
                second.SelectMany(x => x["frame_indices"].Values<int>()));
            Assert.That(first[1]["root_positions"]?[0]?[0]?.Value<int>(), Is.EqualTo(3));
            Assert.That(second[1]["target_positions"]?[0]?[2]?.Value<int>(), Is.EqualTo(10));

            JObject interpolatedRoot = KimodoGenerationWindowPlanner.CreateRoot2DConstraint(
                localFrame: 225 - windows[0].RequestStartFrame,
                position: new Vector2(7.5f, 1.5f),
                heading: new Vector2(Mathf.Sqrt(0.5f), Mathf.Sqrt(0.5f)));
            Assert.That(interpolatedRoot.Value<string>("type"), Is.EqualTo("root2d"));
            Assert.That(interpolatedRoot["frame_indices"]?[0]?.Value<int>(), Is.EqualTo(225));
            Assert.That(interpolatedRoot["smooth_root_2d"], Is.TypeOf<JArray>());
            Assert.That(((JArray)interpolatedRoot["smooth_root_2d"]).Count, Is.EqualTo(1));
            Assert.That(interpolatedRoot["smooth_root_2d"]?[0], Is.TypeOf<JArray>());
            Assert.That(interpolatedRoot["smooth_root_2d"]?[0]?[0]?.Value<float>(), Is.EqualTo(7.5f).Within(0.001f));
            Assert.That(((JArray)interpolatedRoot["global_root_heading"]).Count, Is.EqualTo(1));
            Assert.That(interpolatedRoot["global_root_heading"]?[0], Is.TypeOf<JArray>());
            Assert.That(interpolatedRoot["global_root_heading"]?[0]?[0]?.Value<float>(), Is.EqualTo(Mathf.Sqrt(0.5f)).Within(0.001f));
        }

        [Test]
        public void ReadRootAnchors_RecognizesRootFullBodyRootEffectorAtThreeSixNineTwelveSeconds()
        {
            const int fps = 30;
            string rawConstraints = new JArray
            {
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(3 * fps),
                    ["smooth_root_2d"] = new JArray { new JArray(0, 0) },
                    ["global_root_heading"] = new JArray { new JArray(0, 1) }
                },
                new JObject
                {
                    ["type"] = "fullbody",
                    ["frame_indices"] = new JArray(6 * fps),
                    ["root_positions"] = new JArray { new JArray(6, 0, 0) },
                    ["local_joints_rot"] = new JArray { new JArray { new JArray(0, 0, 0) } }
                },
                new JObject
                {
                    ["type"] = "root2d",
                    ["frame_indices"] = new JArray(9 * fps),
                    ["smooth_root_2d"] = new JArray { new JArray(9, 3) },
                    ["global_root_heading"] = new JArray { new JArray(1, 0) }
                },
                new JObject
                {
                    ["type"] = "right-hand",
                    ["frame_indices"] = new JArray(12 * fps),
                    ["root_positions"] = new JArray { new JArray(12, 0, 3) },
                    ["global_root_heading"] = new JArray { new JArray(1, 0) },
                    ["target_positions"] = new JArray { new JArray(1, 2, 3) }
                }
            }.ToString(Formatting.None);

            List<KimodoGlobalRawConstraint> anchors = KimodoGenerationWindowPlanner.ReadRootAnchors(rawConstraints);
            CollectionAssert.AreEqual(
                new[] { 90, 180, 270, 360 },
                anchors.Select(anchor => anchor.Frame).ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    KimodoRawConstraintKind.Root2D,
                    KimodoRawConstraintKind.FullBody,
                    KimodoRawConstraintKind.Root2D,
                    KimodoRawConstraintKind.Effector
                },
                anchors.Select(anchor => anchor.Kind).ToArray());
            Assert.That(anchors[1].RootPosition, Is.EqualTo(new Vector2(6f, 0f)));
            Assert.That(anchors[2].RootPosition, Is.EqualTo(new Vector2(9f, 3f)));
        }

        private static JObject Constraint(string type, int frame, string valuesProperty, JArray values)
        {
            return new JObject
            {
                ["type"] = type,
                ["frame_indices"] = new JArray(frame),
                [valuesProperty] = new JArray { values }
            };
        }

        private static KimodoGlobalRawConstraint Anchor(
            int frame,
            KimodoRawConstraintKind kind,
            Vector2 rootPosition,
            Vector2 heading,
            JObject payload)
        {
            return new KimodoGlobalRawConstraint
            {
                Frame = frame,
                Kind = kind,
                RootPosition = rootPosition,
                Heading = heading,
                Payload = payload
            };
        }
    }
}
