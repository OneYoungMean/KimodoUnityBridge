using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KimodoBridge;
using KimodoBridge.Editor;
using KimodoUnityBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace KimodoUnityBridge.Command.Tests
{
    /// <summary>
    /// Generation-facing correspondence checks. The regular tests verify the
    /// request boundary; the real KModel test also samples the returned,
    /// retargeted AnimationClip.
    /// </summary>
    public sealed class GenerationConstraintCorrespondenceTests
    {
        private const float PositionToleranceMeters = 0.003f;
        private const float Root2DGenerationToleranceMeters = 0.1f;
        private const float EffectorRootGenerationToleranceMeters = 0.1f;

        [Test]
        public void HipsComparison_UsesTimelineEvaluationOnSourceCharacterHumanoidRig()
        {
            using (var fixture = new GenerationFixture("HipsComparison"))
            {
                const string model = KimodoMotionModelProfiles.DefaultModelName;
                KimodoMarkerSampleResult constraint = fixture.Capture("root2d", 0.5d);
                KimodoEditorGenerateRequest request = fixture.BuildTimelineComparisonRequest();
                Assert.That(KimodoConstraintPoseRigFactory.TryCreatePoseRig(
                    model,
                    fixture.Skeleton.animator,
                    fixture.Skeleton.avatar,
                    out KimodoConstraintPoseRigFactory.PoseRigInstance rig,
                    out string error), Is.True, error);

                AnimationClip clip = null;
                try
                {
                    Assert.That(KimodoConstraintPoseRigFactory.TryApplyPose(
                        rig, constraint, model, out error), Is.True, error);
                    BoneSample pose = KimodoRetargetSamplingUtility.CaptureBoneSample(rig.TargetCache);
                    var poses = new BoneSample[16];
                    for (int i = 0; i < poses.Length; i++) poses[i] = pose;
                    Assert.That(KimodoRetargetSamplingUtility.TryCreateTransientBoneClip(
                        poses, 30f, out clip, out error), Is.True, error);
                    fixture.Playable.clip = clip;

                    JObject comparison = KimodoGenerationConstraintHipsComparer.Compare(
                        clip,
                        request.TimelineClipSnapshot,
                        model,
                        new[] { constraint },
                        0,
                        30f,
                        request.TimelineContextSnapshot);

                    Assert.That(comparison.Value<string>("status"), Is.EqualTo("ok"));
                    Assert.That(comparison.Value<string>("comparison_basis"),
                        Is.EqualTo("timeline_evaluated_source_character_humanoid_rig"));
                    JObject frame = comparison["frames"].Values<JObject>().Single();
                    Assert.That(frame["constraint_hips_world"].Values<float>().Count(), Is.EqualTo(3));
                    Assert.That(frame["generated_hips_world"].Values<float>().Count(), Is.EqualTo(3));
                    Assert.That(frame["delta_xyz_m"].Values<float>().ToArray(), Is.EqualTo(new[] { 0f, 0f, 0f }));
                    Assert.That(frame.Value<float>("distance_m"), Is.EqualTo(0f).Within(0.0001f));
                }
                finally
                {
                    KimodoConstraintPoseRigFactory.DisposePoseRig(rig);
                    if (clip != null) Object.DestroyImmediate(clip);
                }
            }
        }

        [Test, Timeout(10 * 60 * 1000)]
        public async Task RealKModelGeneration_RetargetedClipMatchesRoot2DKeyframe()
        {
            using (var fixture = new GenerationFixture("RealKModelGeneration"))
            {
                const int generationTimeoutMinutes = 10;
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromMinutes(generationTimeoutMinutes));
                KimodoMarkerSampleResult root2D = fixture.Capture("root2d", 30.0 / 30.0);
                root2D.rootOverride.t = new Vector3(0f, root2D.rootOverride.t.y, 0f);
                KimodoEditorGenerateRequest request = fixture.BuildRequest(root2D);
                request.Prompt = "a person standing still";
                request.DiffusionSteps = 100;
                request.EffectiveSeed = 1701;
                request.Token = timeout.Token;

                KimodoBridgeService bridge = KimodoBridgeService.Shared;
                JObject modelStatus = null;
                try
                {
                    modelStatus = await (bridge.ListModelConfigurationsAsync(
                        KimodoMotionModelProfiles.DefaultModelName,
                        KimodoTextEncoderModeProtocol.HighPerformance,
                        request.ModelsRoot,
                        null,
                        timeout.Token));
                }
                catch (Exception exception)
                {
                    if (timeout.IsCancellationRequested)
                    {
                        Assert.Fail($"KModel availability probe timed out after {generationTimeoutMinutes} minutes.");
                    }
                    Assert.Fail($"KModel runtime is unavailable: {exception.Message}");
                }

                if (!ModelStatusContainsUsableModel(modelStatus, KimodoMotionModelProfiles.DefaultModelName))
                {
                    Assert.Fail("The configured QuickServer does not report a usable default Kimodo model.");
                }

                KimodoEditorGenerationResult result = null;
                try
                {
                    result = await KimodoEditorGeneratePipeline.ExecuteAsync(request);
                    Assert.That(result, Is.Not.Null);
                    Assert.That(result.GeneratedClip, Is.Not.Null);
                    KimodoPlayableClipGenerationHostService.FinalizeGeneration(
                        fixture.Playable,
                        request,
                        result);
                    Assert.That(result.ConstraintLoss, Is.Not.Null);
                    Assert.That(result.ConstraintLoss.Value<string>("comparison_basis"),
                        Is.EqualTo("timeline_evaluated_source_character_humanoid_rig"));

                    JObject exportedRoot2D = JArray.Parse(request.Constraints.json)
                        .OfType<JObject>()
                        .Single(item => string.Equals(item.Value<string>("type"), "root2d", StringComparison.OrdinalIgnoreCase));
                    int constrainedFrame = exportedRoot2D["frame_indices"]?.Values<int>().Single() ??
                        throw new AssertionException("Generated root2d constraint has no frame index.");
                    int outputConstraintFrame = constrainedFrame - request.RuntimeTrimStartFrame;
                    Assert.That(result.ConstraintLoss.Value<int>("compared_count"), Is.GreaterThan(0));
                    Assert.That(
                        result.ConstraintLoss["frames"].Values<JObject>().Any(item =>
                            item.Value<int>("frame") == outputConstraintFrame),
                        Is.True,
                        "The returned constraint-loss details should include the generated frame that corresponds to the Root2D constraint.");
                    Assert.That(result.ConstraintLoss["frames"].Values<JObject>().Any(item =>
                            item.Value<int>("frame") == outputConstraintFrame &&
                            item["constraint_hips_world"]?.Values<float>().Count() == 3 &&
                            item["generated_hips_world"]?.Values<float>().Count() == 3 &&
                            item["delta_xyz_m"]?.Values<float>().Count() == 3 &&
                            item.Value<float?> ("distance_m").HasValue),
                        Is.True,
                        "The Hips comparison should report both world positions, XYZ delta, and distance at the generated constraint frame.");
                    double constrainedTime = constrainedFrame / KimodoMotionModelProfiles.DefaultFrameRate;
                    Assert.That(
                        KimodoRetargetSamplingUtility.SampleBoneClipToBoneSample(
                            result.GeneratedClip,
                            fixture.Skeleton,
                            constrainedTime,
                            KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(result.GeneratedClip),
                            out BoneSample actualBone,
                            out string sampleError),
                        Is.True,
                        sampleError);
                    Assert.That(
                        KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(
                            actualBone,
                            fixture.Skeleton,
                            out sampleError),
                        Is.True,
                        sampleError);
                    var actual = new KimodoMarkerSampleResult
                    {
                        rootOverride = KimodoRigidTransform.Identity,
                        effectors = new KimodoConstraintEffectors(),
                        enableMask = new KimodoConstraintMask()
                    };
                    KimodoRetargetMarkerSamplingUtility.CaptureWorldTargets(fixture.Skeleton, actual);

                    KimodoMarkerSampleResult expected = request.ConstraintSamples.Single();
                    Vector2 actualPlanar = new Vector2(actual.rootOverride.t.x, actual.rootOverride.t.z);
                    Vector2 expectedPlanar = new Vector2(expected.rootOverride.t.x, expected.rootOverride.t.z);
                    float planarDistance = Vector2.Distance(actualPlanar, expectedPlanar);
                    float verticalDelta = Mathf.Abs(actual.rootOverride.t.y - expected.rootOverride.t.y);
                    Vector2 exportedPlanar = new Vector2(
                        exportedRoot2D["smooth_root_2d"]?[0]?[0]?.Value<float>() ?? float.NaN,
                        exportedRoot2D["smooth_root_2d"]?[0]?[1]?.Value<float>() ?? float.NaN);
                    string rawRootDescription = "raw motion unavailable";
                    if (KimodoRawMotionUtility.TryParse(
                        result.MotionJsonCompact,
                        out KimodoRawMotionData rawMotion,
                        out _))
                    {
                        int rawFrame = Mathf.Clamp(constrainedFrame, 0, rawMotion.FrameCount - 1);
                        if (rawMotion.TryReadUnityRootPosition(rawFrame, out _))
                        {
                            rawRootDescription = $"raw frames {rawFrame - 1}/{rawFrame}/{rawFrame + 1}=" +
                                $"{ReadRawRoot(rawMotion, rawFrame - 1)}/{ReadRawRoot(rawMotion, rawFrame)}/{ReadRawRoot(rawMotion, rawFrame + 1)}";
                        }
                    }
                    Assert.That(
                        planarDistance,
                        Is.LessThanOrEqualTo(Root2DGenerationToleranceMeters),
                        $"KModel retargeted root XZ differs by {planarDistance:F4}m " +
                        $"(actual={actual.rootOverride.t}, expected={expected.rootOverride.t}, " +
                        $"exported protocol smooth_root_2d={exportedPlanar}, " +
                        $"unconstrained Y delta={verticalDelta:F4}m, {rawRootDescription}).");
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    Assert.Fail($"KModel generation timed out after {generationTimeoutMinutes} minutes; the request was cancelled.");
                }
                finally
                {
                    request.CleanupGeneratedClips();
                    if (result?.GeneratedClip != null && result.GeneratedClip != request.TargetClip)
                    {
                        Object.DestroyImmediate(result.GeneratedClip);
                    }
                }
            }
        }

        [Test, Timeout(10 * 60 * 1000)]
        public async Task RealKModelGeneration_RetargetedClipMatchesFullBodyKeyframe()
        {
            await AssertRealKModelGenerationRootKeyframe(
                "RealKModelGenerationFullBody",
                "fullbody",
                "fullbody",
                30.0 / 30.0,
                1702,
                planarOnly: false);
        }

        [Test, Timeout(10 * 60 * 1000)]
        public async Task RealKModelGeneration_RetargetedClipMatchesRootEffectKeyframe()
        {
            await AssertRealKModelGenerationRootKeyframe(
                "RealKModelGenerationRootEffect",
                "effector",
                "right-hand",
                30.0 / 30.0,
                1703,
                planarOnly: false);
        }

        private async Task AssertRealKModelGenerationRootKeyframe(
            string fixtureName,
            string sampleMode,
            string exportedConstraintType,
            double sampleTime,
            int seed,
            bool planarOnly)
        {
            using (var fixture = new GenerationFixture(fixtureName))
            {
                const int generationTimeoutMinutes = 10;
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromMinutes(generationTimeoutMinutes));
                KimodoMarkerSampleResult sample = fixture.Capture(sampleMode, sampleTime);
                if (string.Equals(sampleMode, "effector", StringComparison.OrdinalIgnoreCase))
                {
                    sample.rootOverride.t = new Vector3(1f, 1f, 1f);
                    sample.enableMask = new KimodoConstraintMask
                    {
                        rootHeading = true,
                        rightHand = true
                    };
                }

                KimodoEditorGenerateRequest request = fixture.BuildRequest(sample);
                request.Prompt = "a person standing still";
                request.DiffusionSteps = 100;
                request.EffectiveSeed = seed;
                request.Token = timeout.Token;

                JObject modelStatus;
                try
                {
                    modelStatus = await KimodoBridgeService.Shared.ListModelConfigurationsAsync(
                        KimodoMotionModelProfiles.DefaultModelName,
                        KimodoTextEncoderModeProtocol.HighPerformance,
                        request.ModelsRoot,
                        null,
                        timeout.Token);
                }
                catch (Exception exception)
                {
                    if (timeout.IsCancellationRequested)
                    {
                        Assert.Fail($"KModel availability probe timed out after {generationTimeoutMinutes} minutes.");
                    }
                    Assert.Fail($"KModel runtime is unavailable: {exception.Message}");
                    return;
                }

                if (!ModelStatusContainsUsableModel(modelStatus, KimodoMotionModelProfiles.DefaultModelName))
                {
                    Assert.Fail("The configured QuickServer does not report a usable default Kimodo model.");
                }

                KimodoEditorGenerationResult result = null;
                try
                {
                    result = await KimodoEditorGeneratePipeline.ExecuteAsync(request);
                    Assert.That(result, Is.Not.Null);
                    Assert.That(result.GeneratedClip, Is.Not.Null);
                    KimodoPlayableClipGenerationHostService.FinalizeGeneration(
                        fixture.Playable,
                        request,
                        result);

                    JArray constraints = JArray.Parse(request.Constraints.json);
                    JObject exported = constraints
                        .OfType<JObject>()
                        .Single(item => string.Equals(
                            item.Value<string>("type"),
                            exportedConstraintType,
                            StringComparison.OrdinalIgnoreCase));
                    int constrainedFrame = exported["frame_indices"]?.Values<int>().Single() ??
                        throw new AssertionException($"Generated {exportedConstraintType} constraint has no frame index.");
                    double constrainedTime = constrainedFrame / KimodoMotionModelProfiles.DefaultFrameRate;

                    Assert.That(
                        KimodoRetargetSamplingUtility.SampleBoneClipToBoneSample(
                            result.GeneratedClip,
                            fixture.Skeleton,
                            constrainedTime,
                            KimodoRetargetClipSamplingUtility.ResolveClipSamplingMode(result.GeneratedClip),
                            out BoneSample actualBone,
                            out string sampleError),
                        Is.True,
                        sampleError);
                    Assert.That(
                        KimodoRetargetSamplingUtility.TryApplyBoneSampleToRetargetSkeleton(
                            actualBone,
                            fixture.Skeleton,
                            out sampleError),
                        Is.True,
                        sampleError);

                    var actual = new KimodoMarkerSampleResult
                    {
                        rootOverride = KimodoRigidTransform.Identity,
                        effectors = new KimodoConstraintEffectors(),
                        enableMask = new KimodoConstraintMask()
                    };
                    KimodoRetargetMarkerSamplingUtility.CaptureWorldTargets(fixture.Skeleton, actual);
                    KimodoMarkerSampleResult expected = request.ConstraintSamples.Single();
                    float rootDistance = planarOnly
                        ? Vector2.Distance(
                            new Vector2(actual.rootOverride.t.x, actual.rootOverride.t.z),
                            new Vector2(expected.rootOverride.t.x, expected.rootOverride.t.z))
                        : Vector3.Distance(actual.rootOverride.t, expected.rootOverride.t);
                    float rootTolerance = string.Equals(sampleMode, "effector", StringComparison.OrdinalIgnoreCase)
                        ? EffectorRootGenerationToleranceMeters
                        : PositionToleranceMeters;

                    Assert.That(
                        rootDistance,
                        Is.LessThanOrEqualTo(rootTolerance),
                        $"{exportedConstraintType} generated root differs by {rootDistance:F4}m " +
                        $"(tolerance={rootTolerance:F3}m, actual={actual.rootOverride.t}, expected={expected.rootOverride.t}, " +
                        $"frame={constrainedFrame}, time={constrainedTime:F4}s).");
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    Assert.Fail($"KModel generation timed out after {generationTimeoutMinutes} minutes; the request was cancelled.");
                }
                finally
                {
                    request.CleanupGeneratedClips();
                    if (result?.GeneratedClip != null && result.GeneratedClip != request.TargetClip)
                    {
                        Object.DestroyImmediate(result.GeneratedClip);
                    }
                }
            }
        }

        private static Vector3 ReadRawRoot(KimodoRawMotionData motion, int frame)
        {
            if (motion == null || !motion.TryReadUnityRootPosition(
                    Mathf.Clamp(frame, 0, motion.FrameCount - 1),
                    out Vector3 root))
            {
                return Vector3.zero;
            }

            return root;
        }

        [Test]
        public void GenerationRequest_DifferentConstraintFamilies_PreserveFramesAndCoordinates()
        {
            using (var fixture = new GenerationFixture("GenerationConstraintFamilies"))
            {
                KimodoMarkerSampleResult fullBody = fixture.Capture("fullbody", 10.0 / 30.0);
                KimodoMarkerSampleResult root2D = fixture.Capture("root2d", 25.0 / 30.0);
                KimodoMarkerSampleResult rootEffect = fixture.Capture("effector", 40.0 / 30.0);
                rootEffect.enableMask = new KimodoConstraintMask
                {
                    rootHeading = true,
                    rightHand = true
                };

                KimodoEditorGenerateRequest request = fixture.BuildRequest(
                    fullBody,
                    root2D,
                    rootEffect);
                JArray exported = JArray.Parse(request.Constraints.json);

                JObject fullBodyJson = Find(exported, "fullbody");
                JObject root2DJson = Find(exported, "root2d");
                JObject rightHandJson = Find(exported, "right-hand");

                CollectionAssert.AreEqual(new[] { 10 }, Frames(fullBodyJson));
                CollectionAssert.Contains(Frames(root2DJson), 25,
                    "The explicit root2d target frame must remain present.");
                CollectionAssert.Contains(Frames(rightHandJson), 40,
                    "The effector target frame must remain present alongside independent constraint samples.");

                AssertVector3(fullBodyJson["root_positions"][0],
                    ToKimodoPosition(fullBody.rootOverride.t), "fullbody root", PositionToleranceMeters);
                int root2DTargetRow = Array.IndexOf(Frames(root2DJson), 25);
                Assert.That(root2DTargetRow, Is.GreaterThanOrEqualTo(0));
                AssertVector2(root2DJson["smooth_root_2d"][root2DTargetRow],
                    new Vector2(-root2D.rootOverride.t.x, root2D.rootOverride.t.z),
                    "root2d root", PositionToleranceMeters);
                int rightHandTargetRow = Array.IndexOf(Frames(rightHandJson), 40);
                Assert.That(rightHandTargetRow, Is.GreaterThanOrEqualTo(0));
                AssertVector3(rightHandJson["target_positions"][rightHandTargetRow],
                    ToKimodoPosition(rootEffect.effectors.rightHand.t),
                    "root-effect right hand", PositionToleranceMeters);
            }
        }

        [Test]
        public void GenerateCommand_ExposesInferredPointConstraintSchema()
        {
            JObject generation = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson())
                ["tools"]
                .Values<JObject>()
                .Single(tool => tool.Value<string>("name") == "animation.generate");
            JObject constraints = (JObject)generation["inputSchema"]["properties"]["constraints"];
            JObject point = ((JArray)constraints["items"]["oneOf"])
                .Values<JObject>()
                .Single(branch => branch["properties"]?["root"] != null && branch["properties"]?["pose"] != null);

            AssertRequiredProperties(point, "frame");
            Assert.That(point["properties"]["type"], Is.Null);
            Assert.That(point["properties"]["root2d"], Is.Null);
            Assert.That(point["properties"]["effect"], Is.Null);
            Assert.That(point["properties"]["pose"], Is.Not.Null);
            Assert.That(point["properties"]["root"], Is.Not.Null);
            Assert.That(point["properties"]["effector"], Is.Not.Null);
            Assert.That(point["properties"]["root"]["properties"]["rotation"]["maxItems"].Value<int>(), Is.EqualTo(3));
            Assert.That(point["oneOf"], Has.Count.EqualTo(3));
        }

        [Test]
        public void GenerationRequest_Effectors_EmbedRootContextWithoutSeparateRoot2D()
        {
            using (var fixture = new GenerationFixture("GenerationRootEffect"))
            {
                KimodoMarkerSampleResult rootEffect = fixture.Capture("effector", 32.0 / 30.0);
                rootEffect.enableMask = new KimodoConstraintMask
                {
                    rootHeading = true,
                    leftHand = true,
                    rightHand = true
                };

                KimodoEditorGenerateRequest request = fixture.BuildRequest(rootEffect);
                JArray exported = JArray.Parse(request.Constraints.json);
                JObject leftHand = Find(exported, "left-hand");
                JObject rightHand = Find(exported, "right-hand");

                Assert.That(exported.Any(item => string.Equals(
                    item.Value<string>("type"), "root2d", StringComparison.OrdinalIgnoreCase)), Is.False,
                    "Effector records embed root context without a separate Root2D record.");
                CollectionAssert.AreEqual(new[] { 32 }, Frames(leftHand));
                CollectionAssert.AreEqual(new[] { 32 }, Frames(rightHand));
                Assert.That(request.ConstraintSamples, Has.Count.EqualTo(1));
                AssertVector3(leftHand["target_positions"][0],
                    ToKimodoPosition(rootEffect.effectors.leftHand.t),
                    "root-effect left hand", PositionToleranceMeters);
                AssertVector3(rightHand["target_positions"][0],
                    ToKimodoPosition(rootEffect.effectors.rightHand.t),
                    "root-effect right hand", PositionToleranceMeters);
            }
        }

        [Test]
        public void GenerationRequest_ConstraintSampleOrderDoesNotChangeFrameCorrespondence()
        {
            using (var fixture = new GenerationFixture("GenerationConstraintOrder"))
            {
                KimodoMarkerSampleResult fullBody = fixture.Capture("fullbody", 12.0 / 30.0);
                KimodoMarkerSampleResult root2D = fixture.Capture("root2d", 48.0 / 30.0);

                KimodoEditorGenerateRequest request = fixture.BuildRequest(root2D, fullBody);
                JArray exported = JArray.Parse(request.Constraints.json);

                CollectionAssert.AreEqual(new[] { 12 }, Frames(Find(exported, "fullbody")));
                CollectionAssert.AreEqual(new[] { 48 }, Frames(Find(exported, "root2d")));
            }
        }

        private static JObject Find(JArray values, string type)
        {
            JObject result = values
                .OfType<JObject>()
                .SingleOrDefault(value => string.Equals(value.Value<string>("type"), type, StringComparison.Ordinal));
            Assert.That(result, Is.Not.Null, $"Expected generated constraint type '{type}'. Payload: {values}");
            return result;
        }

        private static bool ModelStatusContainsUsableModel(JObject status, string modelName)
        {
            if (status == null) return false;
            foreach (JToken token in status.Descendants())
            {
                if (token.Type != JTokenType.String) continue;
                string value = token.Value<string>();
                if (string.Equals(value, modelName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "ready", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "available", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static int[] Frames(JObject value)
        {
            return value["frame_indices"]?.Values<int>().ToArray() ?? Array.Empty<int>();
        }

        private static Vector3 ToKimodoPosition(Vector3 unityPosition)
        {
            return new Vector3(-unityPosition.x, unityPosition.y, unityPosition.z);
        }

        private static void AssertRequiredProperties(JObject schema, params string[] names)
        {
            Assert.That(schema, Is.Not.Null);
            JArray required = (JArray)schema["required"];
            Assert.That(required, Is.Not.Null);
            foreach (string name in names)
                CollectionAssert.Contains(required.Values<string>(), name, $"Schema must require {name}.");
        }

        private static void AssertVector2(JToken actual, Vector2 expected, string label, float tolerance)
        {
            Assert.That(actual, Is.TypeOf<JArray>(), label);
            JArray values = (JArray)actual;
            Assert.That(values.Count, Is.GreaterThanOrEqualTo(2), label);
            Assert.That(Mathf.Abs(values[0].Value<float>() - expected.x), Is.LessThanOrEqualTo(tolerance), label + " x");
            Assert.That(Mathf.Abs(values[1].Value<float>() - expected.y), Is.LessThanOrEqualTo(tolerance), label + " y");
        }

        private static void AssertVector3(JToken actual, Vector3 expected, string label, float tolerance)
        {
            Assert.That(actual, Is.TypeOf<JArray>(), label);
            JArray values = (JArray)actual;
            Assert.That(values.Count, Is.GreaterThanOrEqualTo(3), label);
            Assert.That(Mathf.Abs(values[0].Value<float>() - expected.x), Is.LessThanOrEqualTo(tolerance), label + " x");
            Assert.That(Mathf.Abs(values[1].Value<float>() - expected.y), Is.LessThanOrEqualTo(tolerance), label + " y");
            Assert.That(Mathf.Abs(values[2].Value<float>() - expected.z), Is.LessThanOrEqualTo(tolerance), label + " z");
        }

        private sealed class GenerationFixture : IDisposable
        {
            private readonly GameObject directorObject;
            private readonly TimelineAsset timeline;
            private readonly PlayableDirector director;
            private readonly AnimationTrack track;
            private readonly TimelineClip timelineClip;
            private readonly KimodoPlayableClip playable;
            private readonly RetargetSkeleton skeleton;
            private bool disposed;

            internal GenerationFixture(string name)
            {
                try
                {
                    timeline = ScriptableObject.CreateInstance<TimelineAsset>();
                    // The production resolver only accepts directors in the active scene.
                    directorObject = new GameObject(name + "Director");
                    director = directorObject.AddComponent<PlayableDirector>();
                    director.playableAsset = timeline;
                    track = timeline.CreateTrack<AnimationTrack>(null, name + "Track");
                    timelineClip = track.CreateClip<KimodoPlayableClip>();
                    timelineClip.duration = 2.0;

                    Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                        KimodoMotionModelProfiles.DefaultModelName,
                        out Avatar avatar,
                        out string error), Is.True, error);
                    Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                        avatar,
                        name + "Skeleton",
                        out skeleton,
                        out error), Is.True, error);
                    director.SetGenericBinding(track, skeleton.animator);
                    playable = (KimodoPlayableClip)timelineClip.asset;
                    playable.bridgeModelName = KimodoMotionModelProfiles.DefaultModelName;
                    playable.inOutConstraintMode = KimodoInOutConstraintMode.None;
                    playable.generateLoop = false;
                    Assert.That(KimodoInOutConstraintAdapter.TryResolveTimelineContext(
                        timelineClip, out _, out error), Is.True, error);
                    director.RebuildGraph();
                    director.Evaluate();
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal KimodoMarkerSampleResult Capture(string mode, double sampleTime)
            {
                Assert.That(KimodoRetargetSamplingUtility.TryCaptureMuscleSample(
                    skeleton,
                    out MuscleSample pose,
                    out string error), Is.True, error);
                var sample = new KimodoMarkerSampleResult
                {
                    constraintMode = mode,
                    sampleTime = sampleTime,
                    sampleData = pose.Clone(),
                    enableMask = KimodoConstraintMask.ForType(mode),
                    effectors = new KimodoConstraintEffectors(),
                    rootOverride = KimodoRigidTransform.Identity,
                    enabled = true
                };
                KimodoRetargetMarkerSamplingUtility.CaptureWorldTargets(skeleton, sample);
                sample.enableMask = KimodoConstraintMask.ForType(mode);
                return sample;
            }

            internal KimodoEditorGenerateRequest BuildRequest(params KimodoMarkerSampleResult[] samples)
            {
                var external = new KimodoExternalConstraintRequest
                {
                    Enabled = true,
                    IncludeTimelineConstraints = false,
                    ConstraintSamples = new List<KimodoMarkerSampleResult>(samples)
                };
                return KimodoPlayableClipGenerationHostService.BuildRequest(
                    playable,
                    "constraint correspondence test",
                    external,
                    CancellationToken.None,
                    timelineClipOverride: timelineClip);
            }

            internal KimodoEditorGenerateRequest BuildTimelineComparisonRequest()
            {
                var context = new KimodoTimelineInOutConstraintContext
                {
                    SourceClip = timelineClip,
                    Track = track,
                    Director = director,
                    Animator = skeleton.animator,
                    ModelName = KimodoMotionModelProfiles.DefaultModelName
                };
                return new KimodoEditorGenerateRequest
                {
                    TimelineClipSnapshot = timelineClip,
                    TimelineDirectorSnapshot = director,
                    TimelineContextSnapshot = context
                };
            }

            internal RetargetSkeleton Skeleton => skeleton;
            internal KimodoPlayableClip Playable => playable;

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;

                skeleton?.Dispose();
                Object.DestroyImmediate(directorObject);
                Object.DestroyImmediate(timeline);
            }
        }
    }
}
