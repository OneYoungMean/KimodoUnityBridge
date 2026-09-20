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
                    poses[i].SetRoot(rootPosition + Vector3.right * (i / 30f), rootRotation);
                }
                Assert.That(KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(poses, 30,
                    out animation, out poseError), Is.True, poseError);
                ((AnimationPlayableAsset)previous.asset).clip = animation;
                ((AnimationPlayableAsset)next.asset).clip = animation;
                var playable = (KimodoPlayableClip)current.asset;
                playable.clip = animation;
                playable.inOutConstraintMode = KimodoInOutConstraintMode.Outside;
                playable.enableInConstraint = enableIn; playable.enableOutConstraint = enableOut;
                playable.inConstraintWindowFrames = 7; playable.inConstraintSampleCount = 3;
                playable.outConstraintWindowFrames = 4; playable.outConstraintSampleCount = 3;
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
                    nextPlayable.inConstraintWindowFrames = 7;
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
        public void ArdyRequest_UsesAutoHistoryAndClipMotionLimits()
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
                clip.ardyTargetMaxSpeed = 2.25f;
                clip.ardyTargetMaxAcceleration = 3.5f;

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
                Assert.That(generation.ardy_max_speed, Is.EqualTo(2.25));
                Assert.That(generation.ardy_max_acceleration, Is.EqualTo(3.5));
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
                Assert.That(generation.ardy_max_speed, Is.EqualTo(1.25));
                Assert.That(generation.ardy_max_acceleration, Is.EqualTo(1.5));
            }
            finally
            {
                skeleton?.Dispose();
                Object.DestroyImmediate(directorRoot);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineRequest_UsesGenerationClipAnalysisOptions()
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

                Assert.That(request.AnalysisOptionsJson, Is.EqualTo(clip.analysisOptionsJson));
                Assert.That(generation.analysis_option_json, Is.EqualTo(clip.analysisOptionsJson));

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
    }
}
