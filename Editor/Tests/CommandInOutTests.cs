using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using KimodoBridge;
using KimodoBridge.Editor;
using UnityEditor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class CommandInOutTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        [Test]
        public void CommandWindowFrameConversion_UsesSharedSixtyFpsUtility()
        {
            Assert.That(KimodoFrameTimeUtility.CommandFramesToSeconds(30), Is.EqualTo(0.5).Within(1e-9));
            Assert.That(KimodoFrameTimeUtility.CommandFramesToModelFrameCount(30, 30f), Is.EqualTo(15));
            Assert.That(KimodoFrameTimeUtility.CommandFrameIndexToSeconds(60), Is.EqualTo(1.0).Within(1e-9));
            Assert.That(KimodoFrameTimeUtility.ConvertFrameIndex(30, 30, 60), Is.EqualTo(60));
            Assert.That(KimodoFrameTimeUtility.ConvertFrameCount(12, 30, 60), Is.EqualTo(24));
        }

        [Test]
        public void IncompleteGenerationClip_ReservesNextAppendPositionAcrossProjectionReload()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "IncompleteGenerationCharacter");
            TimelineClip incomplete = track.CreateClip<KimodoPlayableClip>();
            incomplete.start = 12.0;
            incomplete.duration = 2.0;
            incomplete.displayName = "CanceledAttempt";
            var character = new command_context.TimelineCharacterRecord(
                "test", null, null, null, track, track, string.Empty);

            try
            {
                MethodInfo refreshAppendPosition = typeof(command_context).GetMethod(
                    "RefreshCharacterTimelineAppendPosition", PrivateStatic);
                refreshAppendPosition.Invoke(null, new object[] { character });
                Assert.That(character.NextStartSeconds,
                    Is.EqualTo(incomplete.end + command_context.ClipSafeZoneSeconds).Within(1e-8));

                character.NextStartSeconds = 0.0;
                MethodInfo populateProjection = typeof(command_context).GetMethod(
                    "PopulateTimelineCharacterAnimations", PrivateStatic);
                populateProjection.Invoke(null, new object[] { character });
                Assert.That(character.NextStartSeconds,
                    Is.EqualTo(incomplete.end + command_context.ClipSafeZoneSeconds).Within(1e-8));
            }
            finally
            {
                foreach (TimelineClip clip in track.GetClips())
                {
                    if (clip.asset != null) Object.DestroyImmediate(clip.asset);
                }
                Object.DestroyImmediate(track);
                Object.DestroyImmediate(timeline);
            }
        }

        [TestCase(true, -15, -1, 1.5)]
        [TestCase(false, 60, 74, 0.0)]
        public void Windows_ConvertOutsideFramesAndPreserveSampleOrder(bool isIn, int first, int last, double sourceStart)
        {
            var side = JObject.Parse(@"{'source':{'clip':'Walk'},'window_frames':30,'sample_count':5}");
            var window = command_context.ResolveCommandInOutWindow(side, isIn, 2, 60, 30);
            Assert.That(window.CommandFrameCount60, Is.EqualTo(30));
            Assert.That(window.ModelFrameCount, Is.EqualTo(15));
            Assert.That(window.OutputFrames.First(), Is.EqualTo(first));
            Assert.That(window.OutputFrames.Last(), Is.EqualTo(last));
            Assert.That(window.OutputFrames.Distinct().Count(), Is.EqualTo(5));
            Assert.That(window.SourceTimes.First(), Is.EqualTo(sourceStart).Within(1e-6));
            Assert.That(window.SourceTimes.Last() - window.SourceTimes.First(), Is.EqualTo(14.0 / 30).Within(1e-6));
        }

        [TestCase(0, 1, 2, "positive")]
        [TestCase(30, 16, 2, "duplicate")]
        [TestCase(30, 0, 2, "sample_count")]
        [TestCase(30, 5, 0.2, "too short")]
        public void InvalidWindows_AreRejected(int frames, int count, double duration, string error)
        {
            var side = JObject.Parse(@"{'source':{'clip':'Walk'}}");
            side["window_frames"] = frames; side["sample_count"] = count;
            Assert.That(() => command_context.ResolveCommandInOutWindow(side, true, duration, 60, 30),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains(error));
        }

        [Test]
        public void SingleSample_UsesExplicitBoundaryAndDefaultsToOneModelFrame()
        {
            var side = JObject.Parse(@"{'source':{'clip':'Walk','frame':60}}");
            var window = command_context.ResolveCommandInOutWindow(side, true, 2, 60, 30);
            CollectionAssert.AreEqual(new[] { 1.0 }, window.SourceTimes);
            CollectionAssert.AreEqual(new[] { -1 }, window.OutputFrames);
            side["window_frames"] = 30;
            window = command_context.ResolveCommandInOutWindow(side, true, 2, 60, 30);
            CollectionAssert.AreEqual(new[] { 1.0 }, window.SourceTimes);
            CollectionAssert.AreEqual(new[] { -1 }, window.OutputFrames);
        }

        [TestCase("{'constraints':[{'inout':{}}]}", false, "requires")]
        [TestCase("{'constraints':[{'inout':{'mode':'typo'}}]}", false, "mode")]
        [TestCase("{'constraints':[{'inout':{},'frame':0}]}", false, "standalone")]
        [TestCase("{'constraints':[{'inout':{}},{'inout':{}}]}", false, "standalone")]
        [TestCase("{'constraints':[{'inout':{}}]}", true, "requires")]
        public void InvalidInOut_IsRejectedBeforeSampling(string json, bool loop, string error)
        {
            var configure = typeof(command_context).GetMethod("ConfigureCommandInOutClip", PrivateStatic);
            var exception = Assert.Throws<TargetInvocationException>(() => configure.Invoke(null,
                new object[] { JObject.Parse(json), null, null, KimodoMotionModelProfiles.DefaultModelName, 60, 30f, loop, new List<KimodoMarkerSampleResult>() }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(exception.InnerException.Message, Does.Contain(error));
        }

        [TestCase(KimodoMotionModelProfiles.DefaultModelName)]
        [TestCase(KimodoMotionModelProfiles.ArdySomaModelName)]
        public void CommandSampling_UsesExplicitOutsideClipsAndBuildsPaddedRequest(string model)
        {
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(model, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(avatar, "CommandInOutFixture", out var skeleton, out error), Is.True, error);
            var directorObject = new GameObject("CommandInOutDirector");
            var director = directorObject.AddComponent<PlayableDirector>();
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationClip sourceClip = null;
            try
            {
                Assert.That(KimodoRetargetSamplingUtility.TryCaptureMuscleSample(skeleton, out var pose, out error), Is.True, error);
                pose.GetRoot(out var position, out var rotation);
                var frames = Enumerable.Range(0, 121).Select(i =>
                {
                    var frame = pose.Clone(); frame.SetRoot(position + Vector3.forward * (i / 240f), rotation); return frame;
                }).ToArray();
                Assert.That(KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(frames, 30, out sourceClip, out error), Is.True, error);
                // Initialize native clip bindings before Timeline detects root curves.
                sourceClip.SampleAnimation(skeleton.root, 0);
                director.playableAsset = timeline;
                // Match command Session character setup: Timeline owns root motion.
                skeleton.animator.applyRootMotion = false;
                skeleton.animator.Rebind();
                var track = timeline.CreateTrack<AnimationTrack>(null, "TestCharacter");
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(3, 0.5f, -2); track.rotation = Quaternion.Euler(0, 35, 0);
                director.SetGenericBinding(track, skeleton.animator);
                var character = new command_context.TimelineCharacterRecord("test", skeleton.root, skeleton.animator, avatar, track, null, "");
                foreach (var name in new[] { "Previous", "Next" })
                {
                    var source = track.CreateClip<AnimationPlayableAsset>();
                    ((AnimationPlayableAsset)source.asset).clip = sourceClip;
                    ((AnimationPlayableAsset)source.asset).removeStartOffset = false;
                    source.start = name == "Previous" ? 0 : 4; source.duration = 2;
                    source.clipIn = 0.25; source.timeScale = 1.2;
                    character.Animations.Add(new command_context.TimelineAnimationRecord(Guid.NewGuid(), name, "added",
                        sourceClip, source, null, null, 0, 120));
                }
                // This clip is intentionally absent from character.Animations to
                // reproduce a user-moved or manually-created Timeline clip that
                // the cached command cursor does not know about.
                var unindexedOccupant = track.CreateClip<AnimationPlayableAsset>();
                ((AnimationPlayableAsset)unindexedOccupant.asset).clip = sourceClip;
                unindexedOccupant.start = 8.2;
                unindexedOccupant.duration = 0.8;
                unindexedOccupant.displayName = "UnindexedOccupant";
                var sessionType = typeof(command_context).GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
                var session = Activator.CreateInstance(sessionType, new object[] {
                    Guid.NewGuid(), "InOutTest", director, timeline, "", directorObject
                });
                ((List<command_context.TimelineCharacterRecord>)sessionType.GetProperty("Characters").GetValue(session)).Add(character);
                character.NextStartSeconds = 8.0;
                typeof(command_context).GetMethod("RefreshCharacterTimelineAppendPosition", PrivateStatic)
                    .Invoke(null, new object[] { character });
                var traceType = typeof(command_context).GetNestedType("TimelineGenerationTrace", BindingFlags.NonPublic);
                var trace = Activator.CreateInstance(traceType, new object[] { session, character, character.NextStartSeconds, 2.0 });
                var arguments = JObject.Parse(@"{'constraints':[{'inout':{'mode':'outside','in':{'source':{'clip':'Previous'},'window_frames':30,'sample_count':5},'out':{'source':{'clip':'Next'},'window_frames':12,'sample_count':3}}}]}");
                var configure = typeof(command_context).GetMethod("ConfigureCommandInOutClip", PrivateStatic);
                Assert.That(configure, Is.Not.Null);
                director.RebuildGraph();
                director.Evaluate();
                Assert.That((bool)configure.Invoke(null,
                    new object[] { arguments.DeepClone(), trace, null, model, 60, 30f, false, new List<KimodoMarkerSampleResult>() }), Is.True);
                var diagnostics = (JObject)traceType.GetProperty("InOutSampling").GetValue(trace);
                arguments["constraints"][0]["inout"]["in"]["post_apply"] = false;
                Assert.That((bool)configure.Invoke(null,
                    new object[] { arguments, trace, null, model, 600, 30f, false, new List<KimodoMarkerSampleResult>() }), Is.True,
                    "Long requests are divided into windows by the backend.");
                Assert.That(diagnostics["in"]["source_timeline_frames_60"], Is.TypeOf<JArray>());
                Assert.That(diagnostics["runtime_model_frame_count"], Is.Not.Null);
                Assert.That(diagnostics["crop_start_model_frame"], Is.Not.Null);

                var playable = (KimodoPlayableClip)typeof(command_context).GetMethod("CreateGenerationPlayableClip", PrivateStatic)
                    .Invoke(null, new object[] { trace, "CommandOutput", true });
                var output = (TimelineClip)traceType.GetProperty("TimelineClip").GetValue(trace);
                TimelineClip[] evidenceClips = track.GetClips()
                    .Where(clip => clip.asset is AnimationPlayableAsset && !(clip.asset is KimodoPlayableClip))
                    .Where(clip => (clip.displayName ?? string.Empty).StartsWith("InOut ", StringComparison.Ordinal))
                    .OrderBy(clip => clip.start)
                    .ToArray();
                Assert.That(evidenceClips, Is.Empty);
                Assert.That(output.start, Is.EqualTo(9.0 + command_context.ClipSafeZoneSeconds).Within(1e-8));
                Assert.That(character.Animations[0].TimelineClip.start, Is.EqualTo(0));
                Assert.That(character.Animations[0].TimelineClip.duration, Is.EqualTo(2));
                Assert.That(character.Animations[1].TimelineClip.start, Is.EqualTo(4));
                Assert.That(character.Animations[1].TimelineClip.duration, Is.EqualTo(2));
                Assert.That(output.start, Is.GreaterThanOrEqualTo(
                    unindexedOccupant.end + command_context.ClipSafeZoneSeconds - 1e-8),
                    "The generated block must start after every actual Timeline clip.");
                playable.bridgeModelName = model;
                Assert.That(playable.enableInConstraint || playable.enableOutConstraint, Is.False,
                    "Explicit sources must not mix with automatically inferred Session boundaries.");
                Assert.That(playable.postApplyOutConstraint, Is.True,
                    "Command-created clips enable Postapply Out without exposing a command option.");
                Assert.That(output.duration, Is.EqualTo(2));

                configure.Invoke(null,
                    new object[] { arguments, trace, playable, model, 60, 30f, false, new List<KimodoMarkerSampleResult>() });
                Assert.That(playable.inOutConstraintMode, Is.EqualTo(KimodoInOutConstraintMode.Outside));
                Assert.That(playable.inConstraintSourceAsset, Is.SameAs(character.Animations[0].TimelineClip.asset));
                Assert.That(playable.outConstraintSourceAsset, Is.SameAs(character.Animations[1].TimelineClip.asset));
                arguments["constraints"][0]["inout"]["in"]["post_apply"] = false;
                configure.Invoke(null,
                    new object[] { arguments, trace, playable, model, 60, 30f, false, new List<KimodoMarkerSampleResult>() });
                Assert.That(playable.postApplyInConstraint, Is.False,
                    "The command's explicit post_apply option must be copied to the shared clip configuration.");
                arguments["constraints"][0]["inout"]["in"]["post_apply"] = true;
                configure.Invoke(null,
                    new object[] { arguments, trace, playable, model, 60, 30f, false, new List<KimodoMarkerSampleResult>() });
                Assert.That(playable.postApplyInConstraint, Is.True,
                    "In constraint post-application defaults to enabled.");
                var sharedRequest = KimodoPlayableClipGenerationHostService.BuildRequest(
                    playable, "stand", externalConstraint: null, token: CancellationToken.None,
                    timelineClipOverride: output);
                Assert.That(sharedRequest.TargetFrameCount, Is.EqualTo(60));
                Assert.That(sharedRequest.RuntimeFrameCount, Is.EqualTo(81));
                Assert.That(sharedRequest.RuntimeTrimStartFrame, Is.EqualTo(15));
                KimodoClipConstraint inWindow = sharedRequest.Constraints.clips.Single(item => item.purpose == "outside_in");
                KimodoClipConstraint outWindow = sharedRequest.Constraints.clips.Single(item => item.purpose == "outside_out");
                Assert.That(inWindow.postApply, Is.True);
                Assert.That(outWindow.postApply, Is.True);
                CollectionAssert.AreEqual(Enumerable.Range(0, 15), inWindow.targetFrameIndices);
                CollectionAssert.AreEqual(Enumerable.Range(75, 6), outWindow.targetFrameIndices);
                Assert.That(inWindow.sampleCount, Is.EqualTo(5));
                Assert.That(outWindow.sampleCount, Is.EqualTo(3));
                CaptureDiagnosticRequest("unity_" + model + "_outside", sharedRequest);
                if (KimodoMotionModelProfiles.TryGetArdy(model, out var profile))
                {
                    byte[] history = KimodoEditorGeneratePipeline.BuildInitialArdyHistoryPayload(sharedRequest, profile);
                    Assert.That(history, Is.Not.Null);
                    sharedRequest.Constraints = new KimodoConstraintPayload { json = "[]" };
                    KimodoEditorGeneratePipeline.PrependArdyHistoryConstraint(sharedRequest.Constraints.clips, history, profile);
                    Assert.That(sharedRequest.Constraints.clips, Has.Count.EqualTo(1));
                    Assert.That(KimodoRawMotionUtility.TryParseFlatBuffer(history, out var historyMotion, out error), Is.True, error);
                    Assert.That(historyMotion.FrameCount, Is.EqualTo(profile.MaxContextFrames - profile.HorizonFrames));
                    CaptureDiagnosticRequest("unity_ardy_history_only", sharedRequest);
                }
            }
            finally
            {
                director.Stop();
                foreach (var track in timeline.GetOutputTracks().ToArray())
                {
                    foreach (var clip in track.GetClips()) Object.DestroyImmediate(clip.asset);
                    Object.DestroyImmediate(track);
                }
                Object.DestroyImmediate(timeline); Object.DestroyImmediate(directorObject);
                if (sourceClip != null) Object.DestroyImmediate(sourceClip);
                skeleton.Dispose();
            }
        }

        private static void CaptureDiagnosticRequest(string name, KimodoEditorGenerateRequest request)
        {
            string root = Environment.GetEnvironmentVariable("KIMODO_DIAGNOSTIC_CAPTURE_DIR");
            if (string.IsNullOrWhiteSpace(root)) return;
            string folder = Path.Combine(root, name);
            Directory.CreateDirectory(folder);
            var attachments = new List<byte[]>();
            string constraints = request.Constraints.Serialize(request.ModelName, attachments);
            var payload = new JObject
            {
                ["cmd"] = "generate", ["model"] = request.ModelName, ["prompt"] = request.Prompt,
                ["duration"] = request.EffectiveBackendRuntimeDurationSeconds,
                ["seed"] = 1729, ["diffusion_steps"] = 10, ["output_format"] = "kmb_v1",
                ["constraints_json"] = constraints, ["stage_plan"] = JObject.Parse(request.StagePlanJson),
                ["window_constraints"] = JObject.Parse(request.WindowConstraintsJson), ["debug"] = true
            };
            File.WriteAllText(Path.Combine(folder, "request.json"), payload.ToString());
            for (int index = 0; index < attachments.Count; index++)
                File.WriteAllBytes(Path.Combine(folder, $"reference_{index}.kmb"), attachments[index]);
        }

        [Test]
        public void PoseSampling_UsesFollowingClipAtSharedBoundaryAndKeepsFinalEndpoint()
        {
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                KimodoMotionModelProfiles.DefaultModelName, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(
                avatar, "SharedBoundaryFixture", out var skeleton, out error), Is.True, error);
            var directorObject = new GameObject("SharedBoundaryDirector");
            var director = directorObject.AddComponent<PlayableDirector>();
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationClip sourceClip = null;
            try
            {
                Assert.That(KimodoRetargetSamplingUtility.TryCaptureMuscleSample(skeleton, out var pose, out error), Is.True, error);
                pose.GetRoot(out var position, out var rotation);
                var frames = Enumerable.Range(0, 121).Select(i =>
                {
                    var frame = pose.Clone();
                    frame.SetRoot(position + Vector3.forward * (i / 100f), rotation);
                    return frame;
                }).ToArray();
                Assert.That(KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(
                    frames, 30, out sourceClip, out error), Is.True, error);
                sourceClip.SampleAnimation(skeleton.root, 0);
                director.playableAsset = timeline;
                skeleton.animator.applyRootMotion = false;
                skeleton.animator.Rebind();
                var track = timeline.CreateTrack<AnimationTrack>(null, "SharedBoundaryCharacter");
                director.SetGenericBinding(track, skeleton.animator);
                var clips = new TimelineClip[2];
                for (int index = 0; index < clips.Length; index++)
                {
                    clips[index] = track.CreateClip<AnimationPlayableAsset>();
                    var asset = (AnimationPlayableAsset)clips[index].asset;
                    asset.clip = sourceClip;
                    asset.removeStartOffset = false;
                    clips[index].start = index;
                    clips[index].duration = 1;
                    clips[index].clipIn = index == 0 ? 1 : 0;
                }
                var character = new command_context.TimelineCharacterRecord(
                    "boundary", skeleton.root, skeleton.animator, avatar, track, null, "");
                var session = new command_context.TimelineSessionRecord(
                    Guid.NewGuid(), "BoundaryTest", director, timeline, "", directorObject);
                director.RebuildGraph();
                director.Evaluate();
                foreach (int frame in new[] { 59, 60, 61, 120 })
                {
                    double time = frame / 60d;
                    var expected = (KimodoMarkerSampleResult[])typeof(command_context)
                        .GetMethod("CaptureSampleResultsFromSourceClip", PrivateStatic)
                        .Invoke(null, new object[] { character, clips[frame < 60 ? 0 : 1], sourceClip, new[] { time } });
                    var single = (KimodoMarkerSampleResult)typeof(command_context)
                        .GetMethod("CaptureSampleResult", PrivateStatic)
                        .Invoke(null, new object[] { session, character, time });
                    var batch = (KimodoMarkerSampleResult[])typeof(command_context)
                        .GetMethod("CaptureSampleResults", PrivateStatic)
                        .Invoke(null, new object[] { session, character, frame, 1 });
                    Assert.That(Vector3.Distance(single.rootOverride.t, expected[0].rootOverride.t),
                        Is.LessThan(1e-5f), $"pose.get frame {frame}");
                    Assert.That(Vector3.Distance(batch[0].rootOverride.t, expected[0].rootOverride.t),
                        Is.LessThan(1e-5f), $"animation.analysis frame {frame}");
                }
            }
            finally
            {
                director.Stop();
                Object.DestroyImmediate(directorObject);
                Object.DestroyImmediate(timeline);
                if (sourceClip != null) Object.DestroyImmediate(sourceClip);
                skeleton.Dispose();
            }
        }

        [Test]
        public void PoseGet_MatchesAnalysisWorldCoordinatesAtSameTimelineTime()
        {
            const string model = KimodoMotionModelProfiles.DefaultModelName;
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(model, out var avatar, out var error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(avatar, "PoseGetWorldCoordinateFixture", out var skeleton, out error), Is.True, error);
            foreach (Transform transform in skeleton.root.GetComponentsInChildren<Transform>(true))
                transform.gameObject.hideFlags = HideFlags.None;
            SceneManager.MoveGameObjectToScene(skeleton.root, SceneManager.GetActiveScene());
            var sessionRoot = new GameObject(KimodoRuntimeUtility.SessionRootPrefix + "PoseGetWorldCoordinateTest");
            KimodoDisableOnRuntime.Ensure(sessionRoot);
            var director = sessionRoot.AddComponent<PlayableDirector>();
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            string timelineFolder = "Assets/Archive/KimodoPoseGetTests/Timelines";
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Archive", "KimodoPoseGetTests", "Timelines"));
            string timelinePath = AssetDatabase.GenerateUniqueAssetPath($"{timelineFolder}/PoseGetWorldCoordinate_{Guid.NewGuid():N}.playable");
            AssetDatabase.CreateAsset(timeline, timelinePath);
            AssetDatabase.SaveAssets();
            AnimationClip sourceClip = null;
            try
            {
                Assert.That(KimodoRetargetSamplingUtility.TryCaptureMuscleSample(skeleton, out var pose, out error), Is.True, error);
                pose.GetRoot(out var position, out var rotation);
                var frames = Enumerable.Range(0, 121).Select(i =>
                {
                    var frame = pose.Clone();
                    frame.SetRoot(position + Vector3.forward * (i / 240f), rotation);
                    return frame;
                }).ToArray();
                Assert.That(KimodoRetargetSamplingUtility.TryCreateTransientMuscleClip(frames, 30, out sourceClip, out error), Is.True, error);
                sourceClip.SampleAnimation(skeleton.root, 0);

                director.playableAsset = timeline;
                skeleton.animator.applyRootMotion = false;
                skeleton.animator.Rebind();
                var track = timeline.CreateTrack<AnimationTrack>(null, "PoseGetCharacter");
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(3f, 0.5f, -2f);
                track.rotation = Quaternion.Euler(0f, 35f, 0f);
                director.SetGenericBinding(track, skeleton.animator);
                var poseTrack = timeline.CreateTrack<AnimationTrack>(track, "PoseGetCharacter.Poses");
                var timelineClip = track.CreateClip<AnimationPlayableAsset>();
                ((AnimationPlayableAsset)timelineClip.asset).clip = sourceClip;
                ((AnimationPlayableAsset)timelineClip.asset).removeStartOffset = false;
                timelineClip.start = 0.0;
                timelineClip.duration = 2.0;
                timelineClip.clipIn = 0.25;
                timelineClip.timeScale = 1.2;
                EditorUtility.SetDirty(timeline);
                AssetDatabase.SaveAssets();

                director.RebuildGraph();
                director.Evaluate();
                const double timelineTime = 1.0;
                JObject timelineResponse = JObject.Parse(command_context.PoseGet(
                    "{'source':{'character':'PoseGetCharacter','timeline_frame_60':48},'full_data':true}"));
                Assert.That(timelineResponse.Value<bool>("ok"), Is.True, timelineResponse.ToString());
                JObject middleResponse = JObject.Parse(command_context.PoseGet(
                    "{'source':{'character':'PoseGetCharacter','timeline_frame_60':60},'full_data':true}"));
                Assert.That(middleResponse.Value<bool>("ok"), Is.True, middleResponse.ToString());
                JObject tailResponse = JObject.Parse(command_context.PoseGet(
                    "{'source':{'character':'PoseGetCharacter','timeline_frame_60':72},'full_data':true}"));
                Assert.That(tailResponse.Value<bool>("ok"), Is.True, tailResponse.ToString());

                var expectedContext = new KimodoTimelineInOutConstraintContext
                {
                    Director = director,
                    Track = track,
                    Animator = skeleton.animator,
                    SourceClip = timelineClip,
                    ModelName = model
                };
                Assert.That(KimodoTimelineConstraintSampler.TrySampleMarker(
                    expectedContext, timelineTime, 0, "fullbody", model, out var expected, out error), Is.True, error);
                Assert.That(KimodoTimelineConstraintSampler.TrySampleMarker(
                    expectedContext, 0.8, 0, "fullbody", model, out var expectedTimeline, out error), Is.True, error);
                Assert.That(KimodoTimelineConstraintSampler.TrySampleMarker(
                    expectedContext, 1.2, 0, "fullbody", model, out var expectedTail, out error), Is.True, error);
                KimodoConstraintMarker[] actualMarkers = poseTrack.GetMarkers()
                    .OfType<KimodoConstraintMarker>().OrderBy(marker => marker.time).ToArray();
                Assert.That(actualMarkers, Has.Length.EqualTo(3));
                KimodoMarkerSampleResult actual = actualMarkers[1].SampleData;
                KimodoMarkerSampleResult timelineActual = actualMarkers[0].SampleData;
                KimodoMarkerSampleResult tailActual = actualMarkers[2].SampleData;
                // Muscle/Root2D decoding can introduce up to 10 cm of world-space pose error.
                Assert.That(Vector3.Distance(timelineActual.rootOverride.t, expectedTimeline.rootOverride.t), Is.LessThan(0.1f),
                    "timeline_frame_60=48 pose root world position");
                Assert.That(Vector3.Distance(actual.rootOverride.t, expected.rootOverride.t), Is.LessThan(0.1f),
                    "timeline_frame_60=60 pose root world position");
                Assert.That(Vector3.Distance(tailActual.rootOverride.t, expectedTail.rootOverride.t), Is.LessThan(0.1f),
                    "timeline_frame_60=72 pose root world position");
                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    track, skeleton.animator, out Vector3 resolvedTrackPosition,
                    out _, out bool resolvedSceneOffset);

                Assert.That(Vector3.Distance(actual.rootOverride.t, expected.rootOverride.t), Is.LessThan(0.1f),
                    $"hips world position actual={actual.rootOverride.t} expected={expected.rootOverride.t} " +
                    $"track={resolvedTrackPosition} sceneOffset={resolvedSceneOffset} " +
                    $"characterRoot={skeleton.root.transform.position} " +
                    $"sourceTime={KimodoMarkerSamplingUtility.ResolveAnimationSourceTime(timelineClip, timelineTime):F6}");
                foreach (var pair in new[]
                {
                    (actual.effectors.leftHand, expected.effectors.leftHand, "left hand"),
                    (actual.effectors.rightHand, expected.effectors.rightHand, "right hand"),
                    (actual.effectors.leftFoot, expected.effectors.leftFoot, "left foot"),
                    (actual.effectors.rightFoot, expected.effectors.rightFoot, "right foot")
                })
                {
                    Assert.That(Vector3.Distance(pair.Item1.t, pair.Item2.t), Is.LessThan(0.1f), pair.Item3 + " world position");
                }

                Vector3 sceneRootPosition = new Vector3(8f, 1.5f, -4f);
                Quaternion sceneRootRotation = Quaternion.Euler(0f, 27f, 0f);
                skeleton.root.transform.SetPositionAndRotation(sceneRootPosition, sceneRootRotation);
                Vector3 expectedScenePoseRoot = skeleton.animator.GetBoneTransform(HumanBodyBones.Hips).position;
                JObject scenePoseResponse = JObject.Parse(command_context.PoseGet(
                    "{'source':{'character':'PoseGetCharacter','timeline_frame_60':60}}"));
                Assert.That(scenePoseResponse.Value<bool>("ok"), Is.True, scenePoseResponse.ToString());
                JToken characterRoot = scenePoseResponse["character_root"];
                Assert.That(characterRoot["space"]?.Value<string>(), Is.EqualTo("world"));
                Assert.That(characterRoot["position"].Values<float>().ToArray(), Is.EqualTo(new[]
                {
                    sceneRootPosition.x, sceneRootPosition.y, sceneRootPosition.z
                }));
                Assert.That(characterRoot["rotation"].Values<float>().ToArray(), Is.EqualTo(new[]
                {
                    sceneRootRotation.x, sceneRootRotation.y, sceneRootRotation.z, sceneRootRotation.w
                }));

                JObject scenePoseCapture = JObject.Parse(command_context.PoseGet(
                    "{'source':{'character':'PoseGetCharacter','mode':'scene_pose'},'full_data':true}"));
                Assert.That(scenePoseCapture.Value<bool>("ok"), Is.True, scenePoseCapture.ToString());
                Assert.That(scenePoseCapture["source"]?["mode"]?.Value<string>(), Is.EqualTo("scene_pose"));
                Assert.That(scenePoseCapture["pose"]?["track"]?.Value<string>(), Is.EqualTo(poseTrack.name));
                JToken capturedRoot = scenePoseCapture["character_root"];
                Assert.That(capturedRoot["position"].Values<float>().ToArray(), Is.EqualTo(new[]
                {
                    sceneRootPosition.x, sceneRootPosition.y, sceneRootPosition.z
                }));
                Assert.That(capturedRoot["rotation"].Values<float>().ToArray(), Is.EqualTo(new[]
                {
                    sceneRootRotation.x, sceneRootRotation.y, sceneRootRotation.z, sceneRootRotation.w
                }));
                JArray sampledRootPosition = (JArray)scenePoseCapture["data"]?["root"]?["t"];
                Assert.That(sampledRootPosition[0].Value<float>(), Is.EqualTo(expectedScenePoseRoot.x).Within(0.1f));
                Assert.That(sampledRootPosition[1].Value<float>(), Is.EqualTo(expectedScenePoseRoot.y).Within(0.1f));
                Assert.That(sampledRootPosition[2].Value<float>(), Is.EqualTo(expectedScenePoseRoot.z).Within(0.1f));
            }
            finally
            {
                if (director != null) director.Stop();
                Object.DestroyImmediate(sessionRoot);
                if (sourceClip != null) Object.DestroyImmediate(sourceClip);
                skeleton.Dispose();
            }
        }

        [Test]
        public void EveryCommand_HasExamplesInStaticAndLiveHelp()
        {
            var definitions = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            var built = JObject.Parse((string)typeof(command_context).GetMethod("BuildCommandDefinitionsJson", PrivateStatic).Invoke(null, null));
            Assert.That(JToken.DeepEquals(built, definitions), Is.True, "Regenerate Command/help.json from its definition source.");
            var overview = JObject.Parse(command_dispatcher.Invoke("kimodo.help", "{}"));
            var sharedRuleIds = (overview["shared_rules"] as JArray)?
                .Values<JObject>()
                .Select(rule => rule.Value<string>("id"))
                .ToArray();
            Assert.That(sharedRuleIds, Is.Not.Null.And.Not.Empty);
            foreach (var command in definitions["tools"].Values<JObject>())
            {
                string name = command.Value<string>("name");
                Assert.That(command["examples"] is JArray examples && examples.Count > 0, Is.True, name);
                JObject documentation = command["documentation"] as JObject;
                Assert.That(documentation, Is.Not.Null, name);
                Assert.That(documentation.Value<string>("when_to_use"), Is.Not.Null.And.Not.Empty, name);
                foreach (string section in new[] { "behavior", "result_notes", "scene_effects", "limits", "related_commands" })
                    Assert.That(documentation[section] is JArray values && values.Count > 0, Is.True, $"{name}.{section}");
                string[] ruleRefs = documentation["shared_rule_refs"]?.Values<string>().ToArray();
                Assert.That(ruleRefs, Is.Not.Null.And.Not.Empty, $"{name}.shared_rule_refs");
                CollectionAssert.IsSubsetOf(ruleRefs, sharedRuleIds, $"{name} references an unknown shared rule.");
                var detail = JObject.Parse(command_dispatcher.Invoke("kimodo.help", new JObject { ["command"] = name }.ToString()));
                Assert.That(JToken.DeepEquals(detail["manual"]?["documentation"], documentation), Is.True, name);
                Assert.That(JToken.DeepEquals(detail["manual"]["examples"], command["examples"]), Is.True, name);
                CollectionAssert.AreEqual(ruleRefs, detail["shared_rules"]?.Values<JObject>().Select(rule => rule.Value<string>("id")).ToArray(), name);
                JObject overviewCommand = overview["commands"].Values<JObject>().Single(c => c.Value<string>("name") == name);
                Assert.That(overviewCommand["examples"].Count(), Is.GreaterThan(0), name);
                Assert.That(JToken.DeepEquals(overviewCommand["documentation"], documentation), Is.True, name);
            }

            JObject generate = definitions["tools"].Values<JObject>().Single(c => c.Value<string>("name") == "animation.generate");
            Assert.That(generate["inputSchema"]?["properties"]?["prompt"]?.Value<string>("description"), Does.Contain("25 words or fewer"));

            var constraintManual = JObject.Parse(command_dispatcher.Invoke("kimodo.help", "{\"section\":\"constraints\"}"));
            var constraintTypes = constraintManual["constraints"].Values<JObject>().ToArray();
            JObject inside = constraintTypes.Single(item => item.Value<string>("type") == "inside");
            Assert.That(inside["shape"]?["properties"]?["inside"]?["properties"]?["post_apply"], Is.Not.Null);
            JObject inout = constraintTypes.Single(item => item.Value<string>("type") == "inout");
            CollectionAssert.AreEqual(new[] { "outside" }, inout["shape"]?["properties"]?["inout"]?["properties"]?["mode"]?["enum"]?.Values<string>());
        }

        [Test]
        public void PoseAnalysis_HelpDocumentsSynchronousMeasurement()
        {
            JObject manual = JObject.Parse(command_dispatcher.Invoke(
                "kimodo.help", new JObject { ["command"] = "pose.analysis" }.ToString()))["manual"] as JObject;
            Assert.That(manual, Is.Not.Null);
            Assert.That(manual["inputSchema"]?["required"]?.Values<string>(), Does.Contain("constraints"));
            Assert.That(manual["inputSchema"]?["properties"]?["constraints"]?["minItems"]?.Value<int>(), Is.EqualTo(1));
            Assert.That(manual["inputSchema"]?["properties"]?["combine"]?["default"]?.Value<bool>(), Is.False);
            Assert.That(manual["outputSchema"]?["properties"]?["request_id"], Is.Null);
            Assert.That(string.Join(" ", manual["documentation"]?["result_notes"]?.Values<string>() ?? Enumerable.Empty<string>()), Does.Contain("synchronous").IgnoreCase);
            Assert.That(string.Join(" ", manual["documentation"]?["result_notes"]?.Values<string>() ?? Enumerable.Empty<string>()), Does.Contain("joint").IgnoreCase);
        }

        [TestCase("{'pose':{'track':'poses','index':0},'effector':[{'joint':'right_hand','position':[1,2,3]}]}", true)]
        [TestCase("{'effector':[{'joint':'right_hand','position':[1,2,3]}]}", false)]
        [TestCase("{'pose':{'track':'poses','index':0}}", false)]
        [TestCase("{'root':{'position':[0,1,0],'rotation':[0,0,0]},'effector':[{'joint':'right_hand','position':[1,2,3]}]}", false)]
        public void PoseEffectorSafetyPreflight_RequiresExplicitPoseAndEffector(string json, bool expected)
        {
            Assert.That(command_context.HasPoseAndEffector(JObject.Parse(json)), Is.EqualTo(expected));
        }
    }
}
