using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoGenerationInspectorGuiTests
    {
        [Test]
        public void LightweightAnalysisToggle_EnablesKeyframesAndCanBeDisabled()
        {
            string enabledJson = KimodoGenerationInspectorGui.SetLightweightAnalysisEnabled(
                "{\"keyframes\":{\"max_count\":12}}",
                enabled: true);
            JObject enabled = JObject.Parse(enabledJson);
            Assert.That(enabled["keyframes"]?.Value<bool>("enabled"), Is.True);
            Assert.That(enabled["keyframes"]?.Value<int>("max_count"), Is.EqualTo(12));

            Assert.That(
                KimodoGenerationInspectorGui.SetLightweightAnalysisEnabled(enabledJson, enabled: false),
                Is.Empty);
        }

        [Test]
        public void LightweightAnalysisSummary_ReportsReturnedDataCounts()
        {
            string summary = KimodoGenerationInspectorGui.GetLightweightAnalysisSummary(
                "{\"keyframes\":[{},{}],\"foot_contact_changes\":[{},{},{}]}");

            Assert.That(summary, Is.EqualTo("2 keyframes, 3 foot-contact changes"));
            Assert.That(KimodoGenerationInspectorGui.GetLightweightAnalysisSummary("invalid"), Is.Empty);
        }

        [Test]
        public void FootContactTrackDetectionRequiresAllGeneratedChannels()
        {
            AnimationClip clip = new AnimationClip { name = "FootContactTrackDetection" };
            try
            {
                Assert.That(KimodoTimelineFootContactSampler.HasAuthoredTrack(clip), Is.False);

                for (int channel = 0; channel < KimodoFootContactTrackUtility.ChannelCount; channel++)
                {
                    AnimationCurve curve = AnimationCurve.Constant(0f, 1f, channel % 2);
                    AnimationUtility.SetEditorCurve(
                        clip,
                        EditorCurveBinding.FloatCurve(
                            string.Empty,
                            typeof(Animator),
                            KimodoFootContactTrackUtility.GetPropertyName(channel)),
                        curve);

                    Assert.That(
                        KimodoTimelineFootContactSampler.HasAuthoredTrack(clip),
                        Is.EqualTo(channel == KimodoFootContactTrackUtility.ChannelCount - 1));
                }

                AnimationUtility.SetEditorCurve(
                    clip,
                    EditorCurveBinding.FloatCurve(
                        string.Empty,
                        typeof(Animator),
                        KimodoFootContactTrackUtility.GetPropertyName(0)),
                    null);
                Assert.That(KimodoTimelineFootContactSampler.HasAuthoredTrack(clip), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void BakeIntoClip_UsesRealCoverageSampleInsteadOfHoldingPreviousFrame()
        {
            string motionJson = new JObject
            {
                ["num_frames"] = 3,
                ["num_joints"] = 1,
                ["fps"] = 30,
                ["joint_names"] = new JArray("Hips"),
                ["joint_parents"] = new JArray(-1),
                ["joints"] = new JArray(0, 0, 0, 1, 0, 0, 2, 0, 0),
                ["local_rot_quats"] = new JArray(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0)
            }.ToString(Newtonsoft.Json.Formatting.None);
            var clip = new AnimationClip { name = "RealCoverageSample", frameRate = 30f };
            try
            {
                Assert.That(
                    KimodoRetargetToolsEditor.BakeIntoClip(
                        clip,
                        motionJson,
                        KimodoBakeSkeletonType.SOMA,
                        KimodoMotionModelProfiles.DefaultModelName,
                        null,
                        out string error,
                        hasRealCoverageSample: true),
                    Is.True,
                    error);

                EditorCurveBinding binding = EditorCurveBinding.FloatCurve(
                    "Hips", typeof(Transform), "m_LocalPosition.x");
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                Assert.That(curve, Is.Not.Null);
                Assert.That(curve.length, Is.EqualTo(3));
                Assert.That(curve.keys[2].time, Is.EqualTo(2f / 30f).Within(1e-6f));
                Assert.That(curve.keys[2].value, Is.EqualTo(-2f).Within(1e-6f));
                Assert.That(curve.keys[2].value, Is.Not.EqualTo(curve.keys[1].value));
                Assert.That(clip.length, Is.EqualTo(2f / 30f).Within(1e-6f));
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void RelativeOffset_NormalizesRootBeforeBakeAndCarriesWorldPlacementOnRequest()
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var root = new GameObject("RelativeOffsetRequestTest");
            RetargetSkeleton skeleton = null;
            AnimationClip clip = null;
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>();
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(3f, 0.5f, -2f);
                track.rotation = Quaternion.Euler(0f, 35f, 0f);
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.duration = 2.0;
                var director = root.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                skeleton = BindTestSkeleton(director, track, "RelativeOffsetRequestSkeleton");
                var playable = (KimodoPlayableClip)timelineClip.asset;
                playable.bridgeModelName = KimodoMotionModelProfiles.DefaultModelName;
                playable.useRelativeOffset = true;
                playable.relativeOffset = new Vector3(4f, 1f, -2f);
                playable.autoBeginAnchor = false;
                playable.inOutConstraintMode = KimodoInOutConstraintMode.None;

                KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    playable, "walk", null, default, timelineClipOverride: timelineClip);

                Assert.That(request.UseRelativeOffset, Is.True);
                Assert.That(request.RelativeOffset, Is.EqualTo(playable.relativeOffset));
                request.RelativeRootOriginY = 0.98f;
                KimodoTimelineTrackOffsetUtility.CaptureWorldOffset(
                    track, skeleton.animator, out Vector3 trackPosition, out Quaternion trackRotation, out _);
                KimodoTimelineTrackOffsetUtility.WorldToTrackPose(
                    playable.relativeOffset, Quaternion.identity, trackPosition, trackRotation,
                    out Vector3 expectedTrackPosition, out _);
                Vector3 actualTrackPosition = KimodoPlayableClipGenerationHostService.ResolveRelativeOffsetInTrackSpace(
                    playable, request);
                Assert.That(Vector3.Distance(
                    actualTrackPosition,
                    expectedTrackPosition - Vector3.up * request.RelativeRootOriginY),
                    Is.LessThan(0.0001f));
                KimodoTimelineTrackOffsetUtility.TrackToWorldPose(
                    actualTrackPosition + Vector3.up * request.RelativeRootOriginY,
                    Quaternion.identity, trackPosition, trackRotation,
                    out Vector3 resolvedWorldPosition, out _);
                Assert.That(Vector3.Distance(resolvedWorldPosition, playable.relativeOffset), Is.LessThan(0.0001f));

                clip = new AnimationClip { frameRate = 30f };
                clip.SetCurve(string.Empty, typeof(Animator), "RootT.x",
                    new AnimationCurve(new Keyframe(0f, 2f), new Keyframe(1f, 5f)));
                clip.SetCurve(string.Empty, typeof(Animator), "RootT.y",
                    new AnimationCurve(new Keyframe(0f, 3f), new Keyframe(1f, 4f)));
                clip.SetCurve(string.Empty, typeof(Animator), "RootT.z",
                    new AnimationCurve(new Keyframe(0f, -1f), new Keyframe(1f, 2f)));

                KimodoPlayableClipGenerationHostService.NormalizeRootPathToFrameZero(
                    clip, out float rootOriginY, out Quaternion rootOriginRotation);
                Assert.That(AnimationUtility.GetCurveBindings(clip).Length, Is.GreaterThanOrEqualTo(3));
                Assert.That(rootOriginY, Is.EqualTo(3f).Within(0.0001f));
                Assert.That(Quaternion.Angle(rootOriginRotation, Quaternion.identity), Is.LessThan(0.001f));
                Assert.That(Vector3.Distance(ReadRootTranslation(clip, 0f), new Vector3(0f, 3f, 0f)), Is.LessThan(0.0001f));
                Assert.That(Vector3.Distance(ReadRootTranslation(clip, 1f), new Vector3(3f, 4f, 3f)), Is.LessThan(0.0001f));
                Assert.That(KimodoPlayableClipGenerationHostService.TryReadRootOriginY(clip, out float finalRootY), Is.True);
                Assert.That(finalRootY, Is.EqualTo(3f).Within(0.0001f));
            }
            finally
            {
                skeleton?.Dispose();
                if (clip != null) Object.DestroyImmediate(clip);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void RelativeOffset_NormalizesRetargetRootTransformCurvesBeforeHumanoidBake()
        {
            var clip = new AnimationClip { frameRate = 30f };
            try
            {
                // BakeMotionCurvesDirect writes root translation as Transform local-position
                // curves on the sanitized root-joint path, before the Humanoid RootT curves
                // are produced. This is the representation relative-offset normalization
                // must handle in the generation pipeline.
                const string rootJointPath = "Hips";
                clip.SetCurve(rootJointPath, typeof(Transform), "m_LocalPosition.x",
                    new AnimationCurve(new Keyframe(0f, 2f), new Keyframe(1f, 5f)));
                clip.SetCurve(rootJointPath, typeof(Transform), "m_LocalPosition.y",
                    new AnimationCurve(new Keyframe(0f, 3f), new Keyframe(1f, 4f)));
                clip.SetCurve(rootJointPath, typeof(Transform), "m_LocalPosition.z",
                    new AnimationCurve(new Keyframe(0f, -1f), new Keyframe(1f, 2f)));
                // A complete child position triplet must not be mistaken for the
                // animated root when the clip contains additional transform curves.
                const string childJointPath = "Hips/Spine";
                clip.SetCurve(childJointPath, typeof(Transform), "m_LocalPosition.x",
                    new AnimationCurve(new Keyframe(0f, 100f), new Keyframe(1f, 101f)));
                clip.SetCurve(childJointPath, typeof(Transform), "m_LocalPosition.y",
                    new AnimationCurve(new Keyframe(0f, 200f), new Keyframe(1f, 201f)));
                clip.SetCurve(childJointPath, typeof(Transform), "m_LocalPosition.z",
                    new AnimationCurve(new Keyframe(0f, 300f), new Keyframe(1f, 301f)));
                Quaternion initialYaw = Quaternion.Euler(0f, 45f, 0f);
                Quaternion finalYaw = Quaternion.Euler(0f, 90f, 0f);
                clip.SetCurve(rootJointPath, typeof(Transform), "m_LocalRotation.x",
                    new AnimationCurve(new Keyframe(0f, initialYaw.x), new Keyframe(1f, finalYaw.x)));
                clip.SetCurve(rootJointPath, typeof(Transform), "m_LocalRotation.y",
                    new AnimationCurve(new Keyframe(0f, initialYaw.y), new Keyframe(1f, finalYaw.y)));
                clip.SetCurve(rootJointPath, typeof(Transform), "m_LocalRotation.z",
                    new AnimationCurve(new Keyframe(0f, initialYaw.z), new Keyframe(1f, finalYaw.z)));
                clip.SetCurve(rootJointPath, typeof(Transform), "m_LocalRotation.w",
                    new AnimationCurve(new Keyframe(0f, initialYaw.w), new Keyframe(1f, finalYaw.w)));

                KimodoPlayableClipGenerationHostService.NormalizeRootPathToFrameZero(
                    clip, out float rootOriginY, out Quaternion rootOriginRotation);

                Assert.That(rootOriginY, Is.EqualTo(3f).Within(0.0001f));
                Assert.That(Quaternion.Angle(rootOriginRotation, Quaternion.Euler(0f, 45f, 0f)), Is.LessThan(0.001f));
                Assert.That(KimodoPlayableClipGenerationHostService.TryReadRootOriginY(clip, out float finalRootY), Is.True);
                Assert.That(finalRootY, Is.EqualTo(3f).Within(0.0001f));
                Assert.That(ReadTransformPosition(clip, rootJointPath, 0f).x, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(ReadTransformPosition(clip, rootJointPath, 0f).y, Is.EqualTo(3f).Within(0.0001f));
                Assert.That(ReadTransformPosition(clip, rootJointPath, 0f).z, Is.EqualTo(0f).Within(0.0001f));
                Vector3 expectedFinalPath = Quaternion.Euler(0f, -45f, 0f) * new Vector3(3f, 0f, 3f);
                Assert.That(Vector3.Distance(
                    ReadTransformPosition(clip, rootJointPath, 1f),
                    new Vector3(expectedFinalPath.x, 4f, expectedFinalPath.z)),
                    Is.LessThan(0.0001f),
                    "Horizontal path is normalized into the initial facing frame while vertical root motion is preserved.");
                Assert.That(Vector3.Distance(ReadTransformPosition(clip, childJointPath, 0f), new Vector3(100f, 200f, 300f)),
                    Is.LessThan(0.0001f), "Child local-position curves must not be normalized as root translation.");
                Assert.That(Quaternion.Angle(ReadTransformRotation(clip, rootJointPath, 0f), Quaternion.identity),
                    Is.LessThan(0.001f), "Root yaw is normalized to the Clip origin.");
                Assert.That(Quaternion.Angle(ReadTransformRotation(clip, rootJointPath, 1f), Quaternion.Euler(0f, 45f, 0f)),
                    Is.LessThan(0.001f), "The root's relative turn is preserved.");
                Assert.That(AnimationUtility.GetCurveBindings(clip).Any(binding =>
                    binding.type == typeof(Animator) && binding.propertyName.StartsWith("RootT.")), Is.False,
                    "The pre-Humanoid bake clip should retain root Transform curves without synthesizing Animator RootT curves.");
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void MultiFrameOutsideRequest_ExportsFullBodyWindowsAndPadsOnlyEnabledEnds(bool enableIn, bool enableOut)
        {
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var root = new GameObject("MultiFrameOutsideRequestTest");
            RetargetSkeleton skeleton = null;
            AnimationClip animation = null;
            try
            {
                var track = timeline.CreateTrack<AnimationTrack>();
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(4, 0, 6);
                track.rotation = Quaternion.Euler(0, 35, 0);
                var previous = track.CreateClip<AnimationPlayableAsset>(); previous.start = 0; previous.duration = 1;
                var current = track.CreateClip<KimodoPlayableClip>(); current.start = 1; current.duration = 2;
                var next = track.CreateClip<KimodoPlayableClip>(); next.start = 3; next.duration = 1;
                var director = root.AddComponent<PlayableDirector>(); director.playableAsset = timeline;
                skeleton = BindTestSkeleton(director, track, "MultiFrameOutsideSkeleton");
                Assert.That(KimodoRetargetSamplingUtility.TryCaptureMuscleSample(skeleton, out var pose, out string poseError), Is.True, poseError);
                pose.GetRoot(out var rootPosition, out var rootRotation);
                var poses = new MuscleSample[31];
                for (int i = 0; i < poses.Length; i++)
                {
                    poses[i] = pose.Clone();
                    poses[i].SetRoot(rootPosition + Vector3.right * (i / 240f), rootRotation);
                }
                Assert.That(KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(poses, 30,
                    out animation, out poseError), Is.True, poseError);
                ((AnimationPlayableAsset)previous.asset).clip = animation;
                ((AnimationPlayableAsset)next.asset).clip = animation;
                var playable = (KimodoPlayableClip)current.asset;
                playable.clip = animation;
                playable.inOutConstraintMode = KimodoInOutConstraintMode.Outside;
                playable.enableInConstraint = enableIn; playable.enableOutConstraint = enableOut;
                playable.inConstraintWindowFrames60 = 14; playable.inConstraintSampleCount = 3;
                playable.outConstraintWindowFrames60 = 8; playable.outConstraintSampleCount = 3;
                playable.autoBeginAnchor = false;
                director.time = 1.25;
                // Newly authored transient clips initialize root-curve flags on first evaluation.
                // Rebuild afterward so Timeline selects its absolute motion/offset playables.
                director.RebuildGraph();
                director.Evaluate();
                director.RebuildGraph();
                director.Evaluate();
                Assert.That(skeleton.animator.GetBoneTransform(HumanBodyBones.Hips).position.x, Is.GreaterThan(3f),
                    "The test Timeline must apply its track offset before sampling.");
                var request = KimodoPlayableClipGenerationHostService.BuildRequest(playable, "walk", null, default,
                    timelineClipOverride: current);
                int prefix = enableIn ? 7 : 0;
                int suffix = enableOut ? 4 : 0;
                Assert.That(request.TargetFrameCount, Is.EqualTo(60));
                Assert.That(request.RuntimeFrameCount, Is.EqualTo(60 + prefix + suffix));
                Assert.That(request.RuntimeTrimStartFrame, Is.EqualTo(prefix));
                var stage3Request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    playable,
                    "walk",
                    null,
                    default,
                    timelineClipOverride: current,
                    generateLoopOverride: false,
                    targetDurationSecondsOverride: 1.9,
                    disableOutsideContextExpansion: true);
                Assert.That(stage3Request.TargetFrameCount, Is.EqualTo(57));
                Assert.That(stage3Request.RuntimeFrameCount, Is.EqualTo(stage3Request.TargetFrameCount),
                    "Stage 3 already includes its In/Out windows in the target duration and must not expand them again as outside context.");
                Assert.That(request.Constraints.clips, Is.Empty, "Kimodo boundaries must use native FullBody constraints.");
                Assert.That(request.ConstraintSamples.Count, Is.EqualTo((enableIn ? 3 : 0) + (enableOut ? 3 : 0)));
                var json = Newtonsoft.Json.Linq.JArray.Parse(request.Constraints.Serialize(playable.bridgeModelName,
                    new System.Collections.Generic.List<byte[]>()));
                var frames = json.Where(x => x["type"]?.Value<string>() == "fullbody")
                    .SelectMany(x => x["frame_indices"].Values<int>()).ToArray();
                var expected = new System.Collections.Generic.List<int>();
                if (enableIn) expected.AddRange(new[] { 0, 3, 6 });
                if (enableOut) expected.AddRange(new[] { 60 + prefix, 62 + prefix, 63 + prefix });
                CollectionAssert.AreEqual(expected, frames);
                Assert.That(director.time, Is.EqualTo(1.25).Within(1e-6), "Sampling must restore the playhead.");
                Assert.That(current.duration, Is.EqualTo(2));
                Assert.That(KimodoInOutConstraintAdapter.TryBuildBoundarySamplesForPreview(current,
                    playable.inOutConstraintMode, enableIn, enableOut, 60, out var begins, out var ends, out var warning), Is.True, warning);
                var preview = begins.Concat(ends).ToArray();
                Assert.That(preview.Length, Is.EqualTo(request.ConstraintSamples.Count));
                for (int i = 0; i < preview.Length; i++)
                {
                    Assert.That(preview[i].sampleTime, Is.EqualTo(request.ConstraintSamples[i].sampleTime).Within(1e-6));
                    Assert.That(Vector3.Distance(preview[i].rootOverride.t, request.ConstraintSamples[i].rootOverride.t), Is.LessThan(0.002f),
                        $"Preview {preview[i].rootOverride.t} differs from request {request.ConstraintSamples[i].rootOverride.t}.");
                }
                track.position = Vector3.zero;
                track.rotation = Quaternion.identity;
                director.RebuildGraph();
                director.Evaluate();
                var unshifted = KimodoPlayableClipGenerationHostService.BuildRequest(playable, "walk", null, default,
                    timelineClipOverride: current);
                var unshiftedJson = JArray.Parse(unshifted.Constraints.json);
                var shiftedPositions = json.Where(x => (string)x["type"] == "fullbody")
                    .SelectMany(x => x["root_positions"]).ToArray();
                var localPositions = unshiftedJson.Where(x => (string)x["type"] == "fullbody")
                    .SelectMany(x => x["root_positions"]).ToArray();
                for (int i = 0; i < localPositions.Length; i++)
                    for (int axis = 0; axis < 3; axis++)
                        Assert.That((float)shiftedPositions[i][axis], Is.EqualTo((float)localPositions[i][axis]).Within(0.003f),
                            "All samples must share one world-to-track transform.");
                Assert.That(System.Math.Abs((float)localPositions[2][0] - (float)localPositions[0][0]), Is.GreaterThan(0.01f),
                    "Export must preserve movement across the window, not normalize every pose independently.");

                if (enableIn && enableOut)
                {
                    var nextPlayable = (KimodoPlayableClip)next.asset;
                    nextPlayable.inOutConstraintMode = KimodoInOutConstraintMode.Outside;
                    nextPlayable.inConstraintWindowFrames60 = 14;
                    nextPlayable.inConstraintSampleCount = 3;
                    nextPlayable.autoBeginAnchor = false;
                    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                    var service = typeof(KimodoPlayableClipGenerationExecutionService);
                    object[] planArgs = { new[] { current, next }, null, null, null };
                    Assert.That((bool)service.GetMethod("TryCreateConnectedPlanEntries", flags).Invoke(null, planArgs), Is.True, planArgs[3]?.ToString());
                    object[] buildArgs = { planArgs[1], planArgs[2], 42, null, default(System.Threading.CancellationToken), 0, 0 };
                    service.GetMethod("BuildConnectedRequests", flags).Invoke(null, buildArgs);
                    Assert.That(buildArgs[5], Is.EqualTo(7));
                    Assert.That(buildArgs[6], Is.EqualTo(0));
                    var entries = (System.Collections.IList)planArgs[1];
                    var aggregate = (KimodoEditorGenerateRequest)entries[0].GetType().GetField("Request").GetValue(entries[0]);
                    var connectedJson = JArray.Parse(aggregate.Constraints.json);
                    CollectionAssert.AreEqual(new[] { 0, 3, 6, 60, 63, 66 },
                        connectedJson.Where(x => (string)x["type"] == "fullbody").SelectMany(x => x["frame_indices"].Values<int>()));
                }
            }
            finally
            {
                skeleton?.Dispose();
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(timeline);
                Object.DestroyImmediate(animation);
            }
        }

        [Test]
        public void ModelOptions_SeparateKimodoAndArdy()
        {
            string[] kimodo = KimodoGenerationInspectorGui.GetModelOptions(false);
            string[] ardy = KimodoGenerationInspectorGui.GetModelOptions(true);

            Assert.That(kimodo, Is.Not.Empty);
            Assert.That(ardy, Is.Not.Empty);
            Assert.That(kimodo.All(name => !KimodoGenerationInspectorGui.IsArdy(name)), Is.True);
            Assert.That(ardy.All(KimodoGenerationInspectorGui.IsArdy), Is.True);
            Assert.That(kimodo.Intersect(ardy), Is.Empty);
            Assert.That(ardy, Does.Contain(KimodoMotionModelProfiles.ArdyCore8ModelName));
            Assert.That(ardy, Does.Contain(KimodoMotionModelProfiles.ArdyG18ModelName));
        }

        [Test]
        public void PromptEdit_PreservesMixedValuesUntilTheUserChangesTheField()
        {
            KimodoPlayableClip first = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            KimodoPlayableClip second = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                first.motionPrompt = "walk forward";
                second.motionPrompt = "wave hello";
                var serializedClips = new SerializedObject(new UnityEngine.Object[] { first, second });
                SerializedProperty prompt = serializedClips.FindProperty("motionPrompt");

                Assert.That(prompt.hasMultipleDifferentValues, Is.True);
                KimodoGenerationInspectorGui.ApplyPromptEdit(prompt, prompt.stringValue, changed: false);
                serializedClips.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(first.motionPrompt, Is.EqualTo("walk forward"));
                Assert.That(second.motionPrompt, Is.EqualTo("wave hello"));

                KimodoGenerationInspectorGui.ApplyPromptEdit(prompt, "run", changed: true);
                serializedClips.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(first.motionPrompt, Is.EqualTo("run"));
                Assert.That(second.motionPrompt, Is.EqualTo("run"));
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [TestCase(KimodoMotionModelProfiles.DefaultModelName, "Kimodo_Playable_20260730_120000_123")]
        [TestCase(KimodoMotionModelProfiles.ArdyCoreModelName, "ARDY_Playable_20260730_120000_123")]
        public void TimelineGeneratedClipName_IdentifiesModelFamily(string modelName, string expected)
        {
            Assert.That(
                KimodoTimelineGenerationOutputPlanner.BuildTargetClipName(
                    modelName,
                    new System.DateTime(2026, 7, 30, 12, 0, 0, 123)),
                Is.EqualTo(expected));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RawBoneWriteback_PersistsOnlyWhenEnabled(bool persist)
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            bool previous = settings.WriteResampledTimelineCacheClips;
            var source = new AnimationClip
            {
                name = $"RawBoneWritebackTest_{System.Guid.NewGuid():N}",
                frameRate = 24f
            };
            AnimationClip rawBone = null;
            try
            {
                settings.WriteResampledTimelineCacheClips = persist;
                rawBone = KimodoEditorClipWritebackService.CreateRawBoneWritebackClip(source);

                string assetPath = AssetDatabase.GetAssetPath(rawBone);
                Assert.That(string.IsNullOrWhiteSpace(assetPath), Is.EqualTo(!persist));
                if (persist)
                {
                    Assert.That(assetPath, Does.StartWith(KimodoEditorClipWritebackService.CacheClipFolder + "/"));
                }
                else
                {
                    Assert.That(rawBone.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
                }
            }
            finally
            {
                settings.WriteResampledTimelineCacheClips = previous;
                if (rawBone != null && !KimodoEditorClipWritebackService.TryDeleteGeneratedAnimationClipAsset(rawBone))
                {
                    Object.DestroyImmediate(rawBone);
                }
                Object.DestroyImmediate(source);
            }
        }

        [Test]
        public void DisabledConstraint_IsIgnoredByNormalization()
        {
            KimodoConstraintMarker marker = ScriptableObject.CreateInstance<KimodoConstraintMarker>();
            try
            {
                Assert.That(marker.constraintEnabled, Is.True);
                marker.constraintEnabled = false;
                Assert.That(
                    KimodoMarkerSamplingUtility.NormalizeConstraintMarkerSample(marker, marker.SampleData),
                    Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(marker);
            }
        }

        [Test]
        public void ArdyRequest_UsesAutoHistoryWithoutRetiredMotionLimits()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject directorRoot = new GameObject("KimodoArdyAutoHistoryRequestTest");
            RetargetSkeleton skeleton = null;
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.duration = 4.0;
                PlayableDirector director = directorRoot.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                skeleton = BindTestSkeleton(director, track, "KimodoArdyAutoHistoryRequestSkeleton");
                var clip = (KimodoPlayableClip)timelineClip.asset;
                clip.bridgeModelName = KimodoMotionModelProfiles.ArdyCoreModelName;
                clip.inOutConstraintMode = KimodoInOutConstraintMode.None;
                clip.autoBeginAnchor = false;

                KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    "walk",
                    externalConstraint: null,
                    default);
                KimodoGenerationRequestDto generation = KimodoEditorGeneratePipeline.CreateRuntimePipelineRequest(
                    request,
                    "walk",
                    clip.bridgeModelName).GenerationRequest;

                Assert.That(request.ConstraintSamples, Is.Empty);
                Assert.That(generation.ardy_history_weight, Is.Null);
            }
            finally
            {
                skeleton?.Dispose();
                Object.DestroyImmediate(directorRoot);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ArdyRequest_UsesManualHistoryWeightWhenAutoHistoryIsDisabled()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject directorRoot = new GameObject("KimodoArdyManualHistoryRequestTest");
            RetargetSkeleton skeleton = null;
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.duration = 4.0;
                PlayableDirector director = directorRoot.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                skeleton = BindTestSkeleton(director, track, "KimodoArdyManualHistoryRequestSkeleton");
                var clip = (KimodoPlayableClip)timelineClip.asset;
                clip.bridgeModelName = KimodoMotionModelProfiles.ArdyCoreModelName;
                clip.inOutConstraintMode = KimodoInOutConstraintMode.None;
                clip.autoBeginAnchor = false;
                clip.ardyAutoHistory = false;
                clip.ardyHistoryWeight = 0.25f;

                KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    "walk",
                    externalConstraint: null,
                    default);
                KimodoGenerationRequestDto generation = KimodoEditorGeneratePipeline.CreateRuntimePipelineRequest(
                    request,
                    "walk",
                    clip.bridgeModelName).GenerationRequest;

                Assert.That(generation.ardy_history_weight, Is.EqualTo(0.25));
            }
            finally
            {
                skeleton?.Dispose();
                Object.DestroyImmediate(directorRoot);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineRequest_IgnoresLegacyLightweightAnalysisOptions()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject directorRoot = new GameObject("KimodoGenerationRequestOptionsTest");
            RetargetSkeleton skeleton = null;
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.duration = 2.0;
                PlayableDirector director = directorRoot.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                skeleton = BindTestSkeleton(director, track, "KimodoGenerationRequestOptionsSkeleton");
                var clip = (KimodoPlayableClip)timelineClip.asset;
                clip.inOutConstraintMode = KimodoInOutConstraintMode.None;
                clip.autoBeginAnchor = false;
                clip.analysisOptionsJson = "{\"keyframes\":{\"enabled\":true}}";

                KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    "walk",
                    externalConstraint: null,
                    default);
                KimodoGenerationRequestDto generation = KimodoEditorGeneratePipeline.CreateRuntimePipelineRequest(
                    request,
                    "walk",
                    clip.bridgeModelName).GenerationRequest;

                Assert.That(request.AnalysisOptionsJson, Is.Empty);
                Assert.That(generation.analysis_option_json, Is.Empty);

                clip.generationOutputMode = KimodoGenerationOutputMode.ModelBone;
                Assert.That(
                    KimodoTimelineGenerationOutputPlanner.Capture(
                        clip,
                        explicitRetargetAvatar: null,
                        modelName: clip.bridgeModelName,
                        bindingObject: null).SkipRetarget,
                    Is.True);

                clip.generationOutputMode = KimodoGenerationOutputMode.CharacterBone;
                Assert.That(
                    KimodoTimelineGenerationOutputPlanner.Capture(
                        clip,
                        explicitRetargetAvatar: null,
                        modelName: clip.bridgeModelName,
                        bindingObject: null).ExportMuscleClip,
                    Is.False);
            }
            finally
            {
                skeleton?.Dispose();
                Object.DestroyImmediate(directorRoot);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void LoopRequest_ExtendsRuntimeAndKeepsTimelineDuration()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject directorRoot = new GameObject("KimodoLoopGenerationRequestTest");
            RetargetSkeleton skeleton = null;
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.duration = 2.0;
                PlayableDirector director = directorRoot.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                skeleton = BindTestSkeleton(director, track, "KimodoLoopGenerationRequestSkeleton");
                var clip = (KimodoPlayableClip)timelineClip.asset;
                clip.inOutConstraintMode = KimodoInOutConstraintMode.None;
                clip.autoBeginAnchor = false;
                clip.generateLoop = true;

                KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    "walk",
                    externalConstraint: null,
                    default);

                Assert.That(request.RuntimeFrameCount, Is.EqualTo(request.TargetFrameCount * 2));
                Assert.That(request.RuntimeTrimStartFrame, Is.EqualTo(request.TargetFrameCount / 2));
                Assert.That(timelineClip.duration, Is.EqualTo(2.0));

                KimodoEditorGenerateRequest firstPass = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    "walk",
                    externalConstraint: null,
                    default,
                    generateLoopOverride: false);
                Assert.That(firstPass.RuntimeFrameCount, Is.EqualTo(firstPass.TargetFrameCount));
                Assert.That(firstPass.RuntimeTrimStartFrame, Is.Zero);

                KimodoEditorGenerateRequest loopTrajectoryPass =
                    KimodoPlayableClipGenerationHostService.BuildRequest(
                        clip,
                        "walk",
                        externalConstraint: null,
                        default,
                        targetDurationSecondsOverride: 3.0,
                        generateLoopOverride: false);
                Assert.That(
                    loopTrajectoryPass.TargetFrameCount,
                    Is.EqualTo(KimodoFrameTimeUtility.SecondsToFrameCount(
                        3.0,
                        loopTrajectoryPass.TargetFrameRate)));
                Assert.That(
                    loopTrajectoryPass.RuntimeFrameCount,
                    Is.EqualTo(loopTrajectoryPass.TargetFrameCount));
            }
            finally
            {
                skeleton?.Dispose();
                Object.DestroyImmediate(directorRoot);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ShortClip_DoesNotExtendRuntimeToMinimumGenerationDuration()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject directorRoot = new GameObject("KimodoShortGenerationRequestTest");
            RetargetSkeleton skeleton = null;
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.duration = 1.0;
                PlayableDirector director = directorRoot.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                skeleton = BindTestSkeleton(director, track, "KimodoShortGenerationRequestSkeleton");
                var clip = (KimodoPlayableClip)timelineClip.asset;
                clip.inOutConstraintMode = KimodoInOutConstraintMode.None;
                clip.autoBeginAnchor = false;

                KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    "walk",
                    externalConstraint: null,
                    default,
                    generateLoopOverride: false);

                int expectedFrames = KimodoFrameTimeUtility.SecondsToFrameCount(
                    timelineClip.duration,
                    request.TargetFrameRate);
                Assert.That(request.TargetFrameCount, Is.EqualTo(expectedFrames));
                Assert.That(request.RuntimeFrameCount, Is.EqualTo(expectedFrames));
            }
            finally
            {
                skeleton?.Dispose();
                Object.DestroyImmediate(directorRoot);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ConstraintPreviewBinding_WalksParentTracks()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject directorRoot = new GameObject("KimodoParentTrackBindingTest");
            GameObject animatorRoot = new GameObject("KimodoParentTrackBindingAnimator");
            try
            {
                GroupTrack parentTrack = timeline.CreateTrack<GroupTrack>(null, "Character");
                AnimationTrack childTrack = timeline.CreateTrack<AnimationTrack>(parentTrack, "Pose");
                PlayableDirector director = directorRoot.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                Animator expected = animatorRoot.AddComponent<Animator>();
                director.SetGenericBinding(parentTrack, expected);

                Assert.That(
                    KimodoConstraintMarkerEditorUtility.TryGetTrackAnimatorBinding(
                        director,
                        childTrack,
                        out Animator resolved),
                    Is.True);
                Assert.That(resolved, Is.SameAs(expected));
            }
            finally
            {
                Object.DestroyImmediate(animatorRoot);
                Object.DestroyImmediate(directorRoot);
                Object.DestroyImmediate(timeline);
            }
        }

        private static RetargetSkeleton BindTestSkeleton(
            PlayableDirector director,
            AnimationTrack track,
            string rootName)
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoMotionModelProfiles.DefaultModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                    avatar,
                    rootName,
                    out RetargetSkeleton skeleton,
                    out error),
                Is.True,
                error);
            director.SetGenericBinding(track, skeleton.animator);
            return skeleton;
        }

        private static Vector3 ReadRootTranslation(AnimationClip clip, float time)
        {
            Vector3 value = Vector3.zero;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(Animator) || binding.path != string.Empty)
                    continue;
                float component = AnimationUtility.GetEditorCurve(clip, binding)?.Evaluate(time) ?? 0f;
                if (binding.propertyName == "RootT.x") value.x = component;
                else if (binding.propertyName == "RootT.y") value.y = component;
                else if (binding.propertyName == "RootT.z") value.z = component;
            }
            return value;
        }

        private static Vector3 ReadTransformPosition(AnimationClip clip, string path, float time)
        {
            Vector3 value = Vector3.zero;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(Transform) || binding.path != path)
                    continue;
                float component = AnimationUtility.GetEditorCurve(clip, binding)?.Evaluate(time) ?? 0f;
                if (binding.propertyName == "m_LocalPosition.x") value.x = component;
                else if (binding.propertyName == "m_LocalPosition.y") value.y = component;
                else if (binding.propertyName == "m_LocalPosition.z") value.z = component;
            }
            return value;
        }

        private static Quaternion ReadTransformRotation(AnimationClip clip, string path, float time)
        {
            Vector4 value = new Vector4(0f, 0f, 0f, 1f);
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (binding.type != typeof(Transform) || binding.path != path)
                    continue;
                float component = AnimationUtility.GetEditorCurve(clip, binding)?.Evaluate(time) ?? 0f;
                if (binding.propertyName == "m_LocalRotation.x") value.x = component;
                else if (binding.propertyName == "m_LocalRotation.y") value.y = component;
                else if (binding.propertyName == "m_LocalRotation.z") value.z = component;
                else if (binding.propertyName == "m_LocalRotation.w") value.w = component;
            }
            return new Quaternion(value.x, value.y, value.z, value.w).normalized;
        }
    }
}
