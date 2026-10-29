using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KimodoBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEngine.TestTools;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class AnimationAnalysisCommandIntegrationTests
    {
        private const string PackageRoot = "Packages/com.unity.kimodo_unity_motion_tools";
        private const string ModelPath = PackageRoot + "/Editor/Model/Armature.fbx";
        private const string ArcWalkPath = PackageRoot + "/Editor/Tests/Fixtures/arc_walk_left_loop.anim";
        private const int TestAnalysisResolution = 1920;

        [Test]
        public void ForceLineAnalysis_UsesRootLocalAnglesAndReportsPerPhaseBalance()
        {
            Vector3 localArmVector = new Vector3(0f, -1f, 1f);
            Vector3 worldArmVector = Quaternion.Euler(0f, 90f, 0f) * localArmVector;
            Assert.That(KimodoForceLineAnalysis.TryCalculateBodyLocalSwingAngle(
                worldArmVector,
                Quaternion.Euler(0f, 90f, 0f),
                out float bodyLocalAngle), Is.True);
            Assert.That(bodyLocalAngle, Is.EqualTo(45f).Within(0.001f));

            JArray phases = new JArray
            {
                new JObject { ["phase_index"] = 0, ["kind"] = "phase", ["start_frame"] = 0, ["end_frame"] = 2 },
                new JObject { ["phase_index"] = 1, ["kind"] = "transition", ["start_frame"] = 2, ["end_frame"] = 4 }
            };
            JObject metric = KimodoForceLineAnalysis.Build(
                new[] { 0f, 10f, 0f, 10f, 0f },
                new[] { 0f, -10f, 0f, 0f, 0f },
                phases,
                startTimelineFrame: 120);

            Assert.That(metric.Value<string>("status"), Is.EqualTo("ok"));
            Assert.That(metric.Value<float>("left_delta_angle_sum_degrees"), Is.EqualTo(40f).Within(0.001f));
            Assert.That(metric.Value<float>("right_delta_angle_sum_degrees"), Is.EqualTo(20f).Within(0.001f));
            Assert.That(metric.Value<float>("imbalance_ratio"), Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(metric.Value<bool>("balanced"), Is.False);
            Assert.That(metric["phases"]?[0]?.Value<bool?>("balanced"), Is.True);
            Assert.That(metric["phases"]?[1]?.Value<bool?>("balanced"), Is.False);
            Assert.That(metric["phases"]?[1]?.Value<int>("start_timeline_frame_60"), Is.EqualTo(122));
        }

        [Test]
        public void ForceLineAnalysis_MarksMissingOrStationarySwingAsInsufficient()
        {
            JObject stationary = KimodoForceLineAnalysis.Build(
                new[] { 0f, 0f, 0f },
                new[] { 0f, 0f, 0f },
                new JArray(),
                startTimelineFrame: 0);
            Assert.That(stationary.Value<string>("status"), Is.EqualTo("insufficient_evidence"));
            Assert.That(stationary.Value<bool?>("balanced"), Is.Null);

            JObject missingAngles = KimodoForceLineAnalysis.Build(
                null,
                null,
                new JArray { new JObject { ["phase_index"] = 0, ["kind"] = "phase", ["start_frame"] = 2, ["end_frame"] = 4 } },
                startTimelineFrame: 300);
            Assert.That(missingAngles.Value<string>("status"), Is.EqualTo("insufficient_evidence"));
            Assert.That(missingAngles.Value<int>("end_timeline_frame_60_exclusive"), Is.EqualTo(305));
            Assert.That(missingAngles["phases"]?[0]?.Value<int>("start_timeline_frame_60"), Is.EqualTo(302));
            Assert.That(missingAngles["phases"]?[0]?.Value<int>("end_timeline_frame_60_exclusive"), Is.EqualTo(305));

            Assert.That(KimodoForceLineAnalysis.TryCalculateBodyLocalSwingAngle(
                Vector3.right,
                Quaternion.identity,
                out _), Is.False);
        }

        [Test]
        public void AnalysisKeyframes_UseOnlyAbsoluteTimelineFrameAndNormalizeIdempotently()
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            MethodInfo normalize = contextType.GetMethod(
                "NormalizeAnalysisContract",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(normalize, Is.Not.Null);

            var analysis = JObject.Parse("{ 'keyframes': [{ 'frame': 6, 'time_seconds': 2.1, 'local_time_seconds': 0.1 }] }");
            normalize.Invoke(null, new object[] { analysis, 120, 240 });
            normalize.Invoke(null, new object[] { analysis, 120, 240 });

            JObject keyframe = analysis["keyframes"]?.Children<JObject>().Single();
            Assert.That(keyframe?.Value<int>("timeline_frame_60"), Is.EqualTo(126));
            Assert.That(keyframe?["time_seconds"], Is.Null);
            Assert.That(keyframe?["local_time_seconds"], Is.Null);
            Assert.That(keyframe?["local_frame_60"], Is.Null);
            Assert.That(keyframe?["frame"], Is.Null);
        }

        [UnityTest]
        public IEnumerator AnimationAnalysis_ArcWalkFixture_WritesCompositePng()
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ArcWalkPath);
            Assert.That(source, Is.Not.Null, "Missing Armature fixture.");
            Assert.That(clip, Is.Not.Null, "Missing arc walk left loop fixture.");

            GameObject character = UnityEngine.Object.Instantiate(source);
            character.name = "KimodoTest_Armature";
            object testSession = null;
            try
            {
                Avatar modelAvatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
                    .OfType<Avatar>()
                    .FirstOrDefault();
                Assert.That(modelAvatar, Is.Not.Null.And.Property("isHuman").True,
                    "Armature fixture requires a Humanoid Avatar.");
                Animator animator = character.GetComponentInChildren<Animator>(true) ?? character.AddComponent<Animator>();
                animator.avatar = modelAvatar;
                animator.runtimeAnimatorController = null;
                animator.Rebind();

                testSession = CreateCurrentTestContext(character, animator);
                EnsureTestSceneView();
                JObject analysis = Require("animation.analysis", new JObject
                {
                    ["clips"] = new JArray(new JObject
                    {
                        ["character"] = character.name,
                        ["clip"] = ArcWalkPath
                    }),
                    ["resolution"] = TestAnalysisResolution
                });

                Assert.That(analysis["request_id"], Is.Null);
                Assert.That(analysis.Value<string>("analysis_schema_version"), Is.EqualTo("6-command-60-force-line-v1"));
                Assert.That(analysis["picture"], Is.Null);
                Assert.That(analysis["clips"], Is.Null);
                Assert.That(analysis["pictures"]?.Value<string>("aspect"), Is.EqualTo("16:9"));
                Assert.That(analysis["pictures"]?.Value<int>("tile_count"), Is.EqualTo(20));
                Assert.That(analysis["pictures"]?.Value<string>("render_version"), Is.EqualTo("animation.analysis.20-sceneview-headers-metrics-ghost-spacing-05m"));
                Assert.That(analysis["pictures"]?.Value<string>("title"), Is.Not.Empty);
                Assert.That(analysis["pictures"]?.Value<int?>("header_height"), Is.GreaterThan(0));
                Assert.That(analysis["pictures"]?["images"]?.Count(), Is.EqualTo(20));

                JObject compactContext = Require("scene.context", new JObject());
                Assert.That(compactContext["history"], Is.Null, "History should be omitted from the default scene context response.");
                string sessionIdBeforeHistory = compactContext["session"]?.Value<string>("session_id");
                JObject sceneContext = Require("scene.context", new JObject { ["include_history"] = true });
                Assert.That(sceneContext["session"]?.Value<string>("session_id"), Is.Not.Empty);
                Assert.That(sceneContext["session"]?.Value<string>("session_id"), Is.EqualTo(sessionIdBeforeHistory),
                    "A history query must inspect the existing context without replacing it.");
                Assert.That(sceneContext["session"]?.Value<int>("session_revision"), Is.GreaterThan(0));
                JObject indexedAnalysis = (sceneContext["history"]?["analyses"] as JArray)?.Values<JObject>()
                    .SingleOrDefault(item => item.Value<string>("analysis_id") == analysis["clip"]?.Value<string>("analysis_id"));
                Assert.That(indexedAnalysis, Is.Not.Null);
                Assert.That(indexedAnalysis.Value<string>("animation_id"), Is.Not.Empty);
                CollectionAssert.Contains(indexedAnalysis["picture_paths"].Values<string>(), analysis["pictures"]?.Value<string>("image_path"));
                Assert.That((sceneContext["history"]?["animations"] as JArray)?.Values<JObject>()
                    .Any(item => item.Value<string>("animation_id") == indexedAnalysis.Value<string>("animation_id")), Is.True);

                JObject clipAnalysis = analysis["clip"] as JObject;
                Assert.That(clipAnalysis?.Value<int>("start_timeline_frame_60"), Is.EqualTo(0));
                Assert.That(clipAnalysis?.Value<int>("end_timeline_frame_60_exclusive"), Is.GreaterThan(1));
                Assert.That(clipAnalysis?["analysis_schema_version"], Is.Null);
                Assert.That(clipAnalysis?.Value<string>("phase_track_version"), Is.EqualTo("1-temporal-cluster-v2-command-60"));
                JArray phases = clipAnalysis?["phase_track"] as JArray;
                Assert.That(phases, Is.Not.Null.And.Not.Empty);
                JArray keyframes = clipAnalysis["keyframes"] as JArray;
                Assert.That(keyframes, Is.Not.Null.And.Not.Empty);
                foreach (JObject keyframe in keyframes.OfType<JObject>())
                {
                    Assert.That(keyframe["timeline_frame_60"], Is.Not.Null);
                    Assert.That(keyframe["time_seconds"], Is.Null);
                    Assert.That(keyframe["local_time_seconds"], Is.Null);
                    Assert.That(keyframe["local_frame_60"], Is.Null);
                    Assert.That(keyframe["frame"], Is.Null);
                }
                Assert.That(phases.First().Value<int>("start_timeline_frame_60"), Is.EqualTo(0));
                Assert.That(phases.First()["start_frame"], Is.Null);
                Assert.That(phases.First()["duration_seconds"], Is.Null);
                int analyzedFrameCount = clipAnalysis["root_trajectory"]?.Value<int>("frame_count") ?? 0;
                Assert.That(analyzedFrameCount, Is.GreaterThan(1));
                Assert.That(clipAnalysis["root_trajectory"]?.Value<string>("coordinate_space"), Is.EqualTo("world"));
                Assert.That(phases.Last().Value<int>("end_timeline_frame_60_exclusive"), Is.EqualTo(analyzedFrameCount));
                Assert.That(clipAnalysis["foot_contacts"]?.Count(), Is.GreaterThan(0));
                Assert.That(clipAnalysis["foot_contacts"]?.FirstOrDefault()?["timeline_frame_60"], Is.Not.Null);
                Assert.That(clipAnalysis["foot_contacts"]?.FirstOrDefault()?["frame"], Is.Null);
                Assert.That(clipAnalysis["root_trajectory"]?["duration_seconds"], Is.Null);
                Assert.That(clipAnalysis["root_trajectory"]?["samples"]?.FirstOrDefault()?["timeline_frame_60"], Is.Not.Null);
                Assert.That(clipAnalysis["root_trajectory"]?["samples"]?.FirstOrDefault()?["frame"], Is.Null);
                AssertGhostTileSpacing(analysis, clipAnalysis, "1.3");
                AssertGhostTileSpacing(analysis, clipAnalysis, "1.4");
                Assert.That(clipAnalysis["motion_profile"]?.Value<float>("path_length_xz"), Is.GreaterThan(0.5f));
                JObject forceLine = clipAnalysis["motion_profile"]?["force_line"] as JObject;
                Assert.That(forceLine, Is.Not.Null);
                Assert.That(forceLine.Value<string>("method"), Is.EqualTo("body_local_shoulder_to_hand_sagittal_angle_delta_sum_v1"));
                Assert.That(forceLine["phases"]?.Count(), Is.EqualTo(phases.Count));
                Assert.That(Math.Abs(clipAnalysis["motion_profile"]?.Value<float>("heading_change_degrees") ?? 0f),
                    Is.GreaterThan(45f));

                JObject poseCapture = Require("pose.analysis", new JObject
                {
                    ["character"] = character.name,
                    ["constraints"] = new JArray(
                        new JObject
                        {
                            ["frame"] = 0,
                            ["root"] = new JObject { ["position"] = new JArray(0, 0, 0) },
                            ["effector"] = new JObject
                            {
                                ["right_hand"] = new JObject { ["position"] = new JArray(0, 0, 0) }
                            }
                        },
                        new JObject
                        {
                            ["frame"] = 15,
                            ["root"] = new JObject { ["position"] = new JArray(1, 0, 0) }
                        }),
                    ["combine"] = false,
                    ["joints"] = new JArray("Hips", "RightHand"),
                    ["camera"] = new JObject { ["resolution"] = new JArray(960, 540) }
                });
                Assert.That(poseCapture["request_id"], Is.Null);
                JObject poseConstraint = (JObject)poseCapture["constraints"]?.First;
                Assert.That(poseCapture.Value<bool>("combine"), Is.False);
                Assert.That(poseCapture["constraints"]?.Count(), Is.EqualTo(2));
                Assert.That(poseCapture["constraints"]?.Values<JObject>().Select(item => item.Value<string>("image_path")).Distinct().Count(), Is.EqualTo(2));
                Assert.That(poseConstraint.Value<string>("image_path"), Does.StartWith("Assets/KimodoGeneratedClips/PoseAnalysis/"));
                Assert.That(poseConstraint["pose_analysis"]?["joints"]?.Count(), Is.EqualTo(2));
                Assert.That(poseConstraint["pose_analysis"]?["joints"]?.Values<JObject>()
                    .Single(item => item.Value<string>("joint") == "RightHand")["distance_m"]?.Type, Is.EqualTo(JTokenType.Float));

                JObject combinedPoseCapture = Require("pose.analysis", new JObject
                {
                    ["character"] = character.name,
                    ["constraints"] = new JArray(
                        new JObject
                        {
                            ["frame"] = 0,
                            ["root"] = new JObject { ["position"] = new JArray(0, 0, 0) }
                        },
                        new JObject
                        {
                            ["frame"] = 30,
                            ["root"] = new JObject { ["position"] = new JArray(1, 0, 0) }
                        }),
                    ["combine"] = true,
                    ["camera"] = new JObject { ["resolution"] = new JArray(960, 540) }
                });
                Assert.That(combinedPoseCapture.Value<bool>("combine"), Is.True);
                Assert.That(combinedPoseCapture.Value<string>("image_path"), Does.StartWith("Assets/KimodoGeneratedClips/PoseAnalysis/"));
                Assert.That(combinedPoseCapture["constraints"]?.Count(), Is.EqualTo(2));

                yield return null;

                string relativePng = analysis["pictures"]?.Value<string>("image_path");
                string absolutePng = string.IsNullOrWhiteSpace(relativePng)
                    ? string.Empty
                    : Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePng));
                Assert.That(relativePng, Is.Not.Null.And.EndsWith(".png"));
                Assert.That(relativePng.Replace('\\', '/'), Does.StartWith("Assets/KimodoGeneratedClips/SceneCaptures/"));
                Assert.That(File.Exists(absolutePng), Is.True, "animation.analysis did not write its composite PNG.");
                Assert.That(new FileInfo(absolutePng).Length, Is.GreaterThan(0));

                string posePng = Path.GetFullPath(Path.Combine(Application.dataPath, "..", poseConstraint.Value<string>("image_path")));
                Assert.That(File.Exists(posePng), Is.True, "pose.analysis did not write its frame PNG.");
                Assert.That(new FileInfo(posePng).Length, Is.GreaterThan(0));
                string combinedPng = Path.GetFullPath(Path.Combine(Application.dataPath, "..", combinedPoseCapture.Value<string>("image_path")));
                Assert.That(File.Exists(combinedPng), Is.True, "pose.analysis did not write its combined opaque PNG.");
                Assert.That(new FileInfo(combinedPng).Length, Is.GreaterThan(0));
            }
            finally
            {
                ResetCurrentTestContext(testSession);
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        private static void AssertGhostTileSpacing(JObject analysis, JObject clipAnalysis, string tileId)
        {
            JObject tile = (analysis["pictures"]?["images"] as JArray)?.Values<JObject>()
                .Single(item => item.Value<string>("id") == tileId);
            var frames = new HashSet<int>((tile["description"]?["timeline_frames_60"] as JArray ?? new JArray())
                .Values<int>());
            foreach (int frame in (tile["description"]?["sample_frames"] as JArray ?? new JArray()).Values<int>())
                frames.Add(frame);

            var positions = (clipAnalysis["root_trajectory"]?["samples"] as JArray ?? new JArray())
                .Values<JObject>()
                .ToDictionary(item => item.Value<int>("timeline_frame_60"), item =>
                {
                    JArray xz = (JArray)item["position_xz"];
                    return new Vector2(xz[0].Value<float>(), xz[1].Value<float>());
                });
            int[] ordered = frames.OrderBy(frame => frame).ToArray();
            for (int right = 1; right < ordered.Length; right++)
            for (int left = 0; left < right; left++)
                Assert.That(Vector2.Distance(positions[ordered[left]], positions[ordered[right]]),
                    Is.GreaterThanOrEqualTo(.5f - .0001f),
                    $"Ghost tile {tileId} draws frames {ordered[left]} and {ordered[right]} within 0.5m.");
        }

        [UnityTest]
        public IEnumerator PoseAnalysis_ArmatureFixture_WritesIndividualAndCombinedPng()
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            Assert.That(source, Is.Not.Null, "Missing Armature fixture.");

            GameObject character = UnityEngine.Object.Instantiate(source);
            character.name = "KimodoTest_PoseAnalysis_Armature";
            object testSession = null;
            try
            {
                Avatar modelAvatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
                    .OfType<Avatar>()
                    .FirstOrDefault();
                Assert.That(modelAvatar, Is.Not.Null.And.Property("isHuman").True,
                    "Armature fixture requires a Humanoid Avatar.");
                Animator animator = character.GetComponentInChildren<Animator>(true) ?? character.AddComponent<Animator>();
                animator.avatar = modelAvatar;
                animator.runtimeAnimatorController = null;
                animator.Rebind();

                testSession = CreateCurrentTestContext(character, animator);
                EnsureTestSceneView();

                JObject individual = Require("pose.analysis", new JObject
                {
                    ["character"] = character.name,
                    ["constraints"] = new JArray
                    {
                        new JObject
                        {
                            ["frame"] = 0,
                            ["root"] = new JObject { ["position"] = new JArray(0, 0, 0) },
                            ["effector"] = new JObject
                            {
                                ["right_hand"] = new JObject { ["position"] = new JArray(0, 0, 0) }
                            }
                        }
                    },
                    ["combine"] = false,
                    ["joints"] = new JArray("Hips", "RightHand"),
                    ["transforms"] = new JArray("@pose"),
                    ["camera"] = new JObject { ["resolution"] = new JArray(640, 360) }
                });

                Assert.That(individual.Value<bool>("combine"), Is.False);
                JArray individualRows = individual["constraints"] as JArray;
                Assert.That(individualRows, Is.Not.Null.And.Count.EqualTo(1));
                JObject individualRow = (JObject)individualRows[0];
                Assert.That(individualRow.Value<int>("index"), Is.EqualTo(0));
                Assert.That(individualRow.Value<int>("frame"), Is.EqualTo(0));
                Assert.That(individualRow.Value<int>("width"), Is.EqualTo(640));
                Assert.That(individualRow.Value<int>("height"), Is.EqualTo(360));
                Assert.That(individualRow.Value<string>("image_path"), Does.StartWith("Assets/KimodoGeneratedClips/PoseAnalysis/"));
                JObject individualSource = individualRow["source"] as JObject;
                Assert.That(individualSource, Is.Not.Null);
                Assert.That(individualSource.Value<string>("type"), Is.EqualTo("point_constraint"));
                Assert.That(individualSource.Value<string>("character"), Is.EqualTo(character.name));
                Assert.That(individualSource.Value<int>("frame"), Is.EqualTo(0));
                Assert.That(individualSource["constraint"]?.Value<int>("frame"), Is.EqualTo(0));
                JArray individualTransforms = individualRow["transforms"] as JArray;
                Assert.That(individualTransforms, Is.Not.Null.And.Count.EqualTo(1));
                Assert.That(individualTransforms[0].Value<string>("path"), Is.EqualTo("@pose"));
                Assert.That(individualTransforms[0]["world_position"]?.Count(), Is.EqualTo(3));
                JObject individualAnalysis = individualRow["pose_analysis"] as JObject;
                Assert.That(individualAnalysis, Is.Not.Null);
                Assert.That(individualAnalysis.Value<string>("overall"),
                    Is.EqualTo("passed").Or.EqualTo("failed").Or.EqualTo("not_tested"));
                Assert.That(individualAnalysis["joints"]?.Count(), Is.EqualTo(2));
                Assert.That(individualRow["pose_analysis"]?["joints"]?.Values<JObject>()
                    .Single(item => item.Value<string>("joint") == "RightHand")["distance_m"]?.Type,
                    Is.EqualTo(JTokenType.Float));

                JObject combined = Require("pose.analysis", new JObject
                {
                    ["character"] = character.name,
                    ["constraints"] = new JArray
                    {
                        new JObject
                        {
                            ["frame"] = 0,
                            ["root"] = new JObject { ["position"] = new JArray(0, 0, 0) }
                        },
                        new JObject
                        {
                            ["frame"] = 30,
                            ["root"] = new JObject { ["position"] = new JArray(1, 0, 0) }
                        }
                    },
                    ["combine"] = true,
                    ["transforms"] = new JArray("@pose"),
                    ["camera"] = new JObject { ["resolution"] = new JArray(640, 360) }
                });

                Assert.That(combined.Value<bool>("combine"), Is.True);
                Assert.That(combined.Value<string>("image_path"), Does.StartWith("Assets/KimodoGeneratedClips/PoseAnalysis/"));
                Assert.That(combined.Value<int>("width"), Is.EqualTo(640));
                Assert.That(combined.Value<int>("height"), Is.EqualTo(360));
                JArray combinedRows = combined["constraints"] as JArray;
                Assert.That(combinedRows, Is.Not.Null.And.Count.EqualTo(2));
                for (int index = 0; index < combinedRows.Count; index++)
                {
                    JObject row = (JObject)combinedRows[index];
                    Assert.That(row.Value<int>("index"), Is.EqualTo(index));
                    Assert.That(row.Value<int>("frame"), Is.EqualTo(index == 0 ? 0 : 30));
                    JObject sourceResult = row["source"] as JObject;
                    Assert.That(sourceResult, Is.Not.Null);
                    Assert.That(sourceResult.Value<string>("type"), Is.EqualTo("point_constraint"));
                    Assert.That(sourceResult.Value<string>("character"), Is.EqualTo(character.name));
                    Assert.That(sourceResult.Value<int>("frame"), Is.EqualTo(index == 0 ? 0 : 30));
                    Assert.That(sourceResult["constraint"]?.Value<int>("frame"), Is.EqualTo(index == 0 ? 0 : 30));
                    JArray transforms = row["transforms"] as JArray;
                    Assert.That(transforms, Is.Not.Null.And.Count.EqualTo(1));
                    Assert.That(transforms[0].Value<string>("path"), Is.EqualTo("@pose"));
                    Assert.That(transforms[0]["world_position"]?.Count(), Is.EqualTo(3));
                    JObject poseAnalysis = row["pose_analysis"] as JObject;
                    Assert.That(poseAnalysis, Is.Not.Null);
                    Assert.That(poseAnalysis.Value<string>("overall"), Is.EqualTo("not_tested"));
                    Assert.That(poseAnalysis["joints"]?.Count(), Is.GreaterThan(0));
                }

                yield return null;

                string individualPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", individualRow.Value<string>("image_path")));
                string combinedPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", combined.Value<string>("image_path")));
                Assert.That(File.Exists(individualPath), Is.True, "pose.analysis did not write its individual PNG.");
                Assert.That(new FileInfo(individualPath).Length, Is.GreaterThan(0));
                Assert.That(File.Exists(combinedPath), Is.True, "pose.analysis did not write its combined PNG.");
                Assert.That(new FileInfo(combinedPath).Length, Is.GreaterThan(0));
            }
            finally
            {
                ResetCurrentTestContext(testSession);
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        [Test]
        public void RootPathSamples_ConvertClipLocalPathIntoWorldAnchor()
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            MethodInfo buildPath = contextType.GetMethod(
                "BuildRootPathConstraintsWorld",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(buildPath, Is.Not.Null);

            var path = new KimodoRootPathData
            {
                length = 1f,
                sourceHumanScale = 1f,
                knots = new List<KimodoRootPathKnot>
                {
                    new KimodoRootPathKnot
                    {
                        position = Vector2.zero,
                        hasHeading = true,
                        heading = Vector2.right
                    },
                    new KimodoRootPathKnot
                    {
                        position = Vector2.right,
                        hasHeading = true,
                        heading = Vector2.right
                    }
                }
            };
            var samples = ((IEnumerable<KimodoMarkerSampleResult>)buildPath.Invoke(null, new object[]
            {
                path, 0, 0, 60, 1f,
                new Vector3(10f, 2f, 20f),
                Quaternion.Euler(0f, 90f, 0f),
                new HashSet<int>(), new HashSet<int>()
            })).ToArray();

            Assert.That(samples, Has.Length.EqualTo(2));
            Assert.That(samples[0].sampleTime, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(samples[1].sampleTime, Is.EqualTo(59f / 60f).Within(1e-5f));
            Assert.That(Vector3.Distance(samples[0].rootOverride.t, new Vector3(10f, 2f, 20f)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(samples[1].rootOverride.t, new Vector3(10f, 2f, 19f)), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(samples[0].rootOverride.q * Vector3.forward, Vector3.back), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(samples[1].rootOverride.q * Vector3.forward, Vector3.back), Is.LessThan(1e-4f));
        }

        private static object CreateCurrentTestContext(GameObject character, Animator animator)
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            Type sessionType = contextType.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
            Type metadataType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.KimodoCommandSessionMetadata");
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.editorSettings.frameRate = 60.0;
            var metadata = ScriptableObject.CreateInstance(metadataType);
            string sessionName = "AnimationAnalysis_ArcWalkFixture_CurrentCommand";
            metadataType.GetField("schemaVersion").SetValue(metadata, "vNext.transition_clip.1");
            metadataType.GetField("sessionId").SetValue(metadata, Guid.NewGuid().ToString("D"));
            metadataType.GetField("sessionName").SetValue(metadata, sessionName);
            metadataType.GetField("isAutomatic").SetValue(metadata, true);
            GameObject sessionRoot = new GameObject("KimodoSession_ArcWalkFixture_CurrentCommand");
            sessionRoot.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            GameObject directorObject = new GameObject("Kimodo_CommandSession_ArcWalkFixture_CurrentCommand");
            directorObject.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            directorObject.transform.SetParent(sessionRoot.transform, false);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;

            ConstructorInfo sessionConstructor = sessionType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Guid), typeof(string), typeof(PlayableDirector), typeof(TimelineAsset), typeof(string), typeof(bool), metadataType, typeof(GameObject) },
                null);
            object session = sessionConstructor.Invoke(new object[]
            {
                Guid.NewGuid(), sessionName, director, timeline, string.Empty, true, metadata, sessionRoot
            });

            MethodInfo addCharacter = contextType.GetMethod("AddCharacterTrack", BindingFlags.Static | BindingFlags.NonPublic);
            object[] addArguments = { session, character, animator, true, null, true };
            Assert.That((bool)addCharacter.Invoke(null, addArguments), Is.True,
                "AddCharacterTrack failed: " + addArguments[4]);
            FieldInfo currentSession = contextType.GetField("currentTimelineSession", BindingFlags.Static | BindingFlags.NonPublic);
            currentSession.SetValue(null, session);
            MethodInfo activate = contextType.GetMethod("ActivateTimelineSession", BindingFlags.Static | BindingFlags.NonPublic);
            activate.Invoke(null, new[] { session });
            return session;
        }

        private static void ResetCurrentTestContext(object session)
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            FieldInfo currentSession = contextType.GetField("currentTimelineSession", BindingFlags.Static | BindingFlags.NonPublic);
            currentSession.SetValue(null, null);
            if (session == null) return;
            Type sessionType = session.GetType();
            GameObject root = sessionType.GetProperty("SessionRoot")?.GetValue(session) as GameObject;
            TimelineAsset timeline = sessionType.GetProperty("TimelineAsset")?.GetValue(session) as TimelineAsset;
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            if (timeline != null) UnityEngine.Object.DestroyImmediate(timeline);
        }

        private static JObject Require(string command, JObject arguments)
        {
            JObject response = JObject.Parse(command_dispatcher.Invoke(command, arguments.ToString()));
            Assert.That(response.Value<bool?>("ok"), Is.True, command + " failed: " + response["error"]);
            return response;
        }

        private static SceneView EnsureTestSceneView()
        {
            SceneView sceneView = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
            sceneView.Show();
            sceneView.Focus();
            Assert.That(sceneView.camera, Is.Not.Null, "The integration test requires a SceneView camera.");
            return sceneView;
        }
    }
}
