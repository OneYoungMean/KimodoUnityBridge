using System;
using System.Collections.Generic;
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

        [TestCase(false, true, -15, -1, 1.5)]
        [TestCase(false, false, 60, 74, 0.0)]
        [TestCase(true, true, 0, 14, 0.0)]
        [TestCase(true, false, 45, 59, 1.5)]
        public void Windows_ConvertCommandFramesAndPreserveSampleOrder(bool inside, bool isIn, int first, int last, double sourceStart)
        {
            var side = JObject.Parse(@"{'source':{'clip':'Walk'},'window_frames':30,'sample_count':5}");
            var window = command_context.ResolveCommandInOutWindow(side, inside, isIn, 2, 60, 30);
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
            Assert.That(() => command_context.ResolveCommandInOutWindow(side, false, true, duration, 60, 30),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains(error));
        }

        [Test]
        public void SingleSample_UsesExplicitBoundaryAndDefaultsToOneModelFrame()
        {
            var side = JObject.Parse(@"{'source':{'clip':'Walk','frame':60}}");
            var window = command_context.ResolveCommandInOutWindow(side, false, true, 2, 60, 30);
            CollectionAssert.AreEqual(new[] { 1.0 }, window.SourceTimes);
            CollectionAssert.AreEqual(new[] { -1 }, window.OutputFrames);
            side["window_frames"] = 30;
            window = command_context.ResolveCommandInOutWindow(side, false, true, 2, 60, 30);
            CollectionAssert.AreEqual(new[] { 1.0 }, window.SourceTimes);
            CollectionAssert.AreEqual(new[] { -1 }, window.OutputFrames);
        }

        [TestCase("{'constraints':[{'inout':{}}]}", false, "requires")]
        [TestCase("{'constraints':[{'inout':{'mode':'typo'}}]}", false, "mode")]
        [TestCase("{'constraints':[{'inout':{'typo':1}}]}", false, "Unknown")]
        [TestCase("{'constraints':[{'inout':{},'frame':0}]}", false, "standalone")]
        [TestCase("{'constraints':[{'inout':{}},{'inout':{}}]}", false, "standalone")]
        [TestCase("{'constraints':[{'inout':{}}]}", true, "loop")]
        public void InvalidInOut_IsRejectedBeforeSampling(string json, bool loop, string error)
        {
            var build = typeof(command_context).GetMethod("BuildCommandInOutConstraints", PrivateStatic);
            var exception = Assert.Throws<TargetInvocationException>(() => build.Invoke(null,
                new object[] { JObject.Parse(json), null, KimodoMotionModelProfiles.DefaultModelName, 60, 30f, loop, new List<KimodoMarkerSampleResult>() }));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(exception.InnerException.Message, Does.Contain(error));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CommandSampling_UsesExplicitClipsAndBuildsPaddedRequest(bool inside)
        {
            const string model = KimodoMotionModelProfiles.DefaultModelName;
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
                var session = Activator.CreateInstance(sessionType, new object[] { Guid.NewGuid(), "InOutTest", director, timeline, "", false, null, directorObject });
                ((List<command_context.TimelineCharacterRecord>)sessionType.GetProperty("Characters").GetValue(session)).Add(character);
                character.NextStartSeconds = 8.0;
                var traceType = typeof(command_context).GetNestedType("TimelineGenerationTrace", BindingFlags.NonPublic);
                var trace = Activator.CreateInstance(traceType, new object[] { session, character, 8.0, 2.0 });
                var arguments = JObject.Parse(@"{'constraints':[{'inout':{'in':{'source':{'clip':'Previous'},'window_frames':30,'sample_count':5},'out':{'source':{'clip':'Next'},'window_frames':12,'sample_count':3}}}]}");
                arguments["constraints"][0]["inout"]["mode"] = inside ? "inside" : "outside";
                var build = typeof(command_context).GetMethod("BuildCommandInOutConstraints", PrivateStatic);
                director.RebuildGraph();
                director.Evaluate();
                var defaults = (KimodoExternalConstraintRequest)build.Invoke(null,
                    new object[] { arguments.DeepClone(), trace, model, 60, 30f, false, new List<KimodoMarkerSampleResult>() });
                Assert.That(defaults.DeferInConstraint, Is.True,
                    "CLI In post_apply defaults to deferred application.");
                Assert.That(defaults.DeferOutConstraint, Is.True,
                    "CLI Out post_apply defaults to deferred application.");
                arguments["constraints"][0]["inout"]["in"]["post_apply"] = false;
                var explicitPostApply = (KimodoExternalConstraintRequest)build.Invoke(null,
                    new object[] { arguments.DeepClone(), trace, model, 60, 30f, false, new List<KimodoMarkerSampleResult>() });
                Assert.That(explicitPostApply.DeferInConstraint, Is.False,
                    "CLI callers can opt out of deferred In application.");
                Assert.That(explicitPostApply.DeferOutConstraint, Is.True);
                arguments["constraints"][0]["inout"]["in"]["post_apply"] = true;
                arguments["constraints"][0]["inout"]["out"]["post_apply"] = true;
                void Reject(int targetFrames, List<KimodoMarkerSampleResult> points, string message)
                {
                    var exception = Assert.Throws<TargetInvocationException>(() => build.Invoke(null,
                        new object[] { arguments, trace, model, targetFrames, 30f, false, points }));
                    Assert.That(exception.InnerException.Message, Does.Contain(message));
                }
                Reject(inside ? 20 : 300, new List<KimodoMarkerSampleResult>(), inside ? "overlap" : "exceeding");
                if (inside)
                {
                    Reject(60, new List<KimodoMarkerSampleResult> { new KimodoMarkerSampleResult { sampleTime = 0 } }, "conflicts");
                    arguments["constraints"][0]["inout"]["in"]["sample_count"] = 1;
                    arguments["constraints"][0]["inout"]["out"]["sample_count"] = 1;
                    var endpoints = (KimodoExternalConstraintRequest)build.Invoke(null,
                        new object[] { arguments.DeepClone(), trace, model, 20, 30f, false, new List<KimodoMarkerSampleResult>() });
                    Assert.That(endpoints.ConstraintSamples, Has.Count.EqualTo(2),
                        "One sample per side keeps the two requested endpoints when their windows overlap.");
                    CollectionAssert.AreEqual(new[] { 0, 19 }, endpoints.ConstraintSamples
                        .Select(sample => Mathf.RoundToInt((float)(sample.sampleTime * 30f))).OrderBy(frame => frame).ToArray());
                    arguments["constraints"][0]["inout"]["in"]["sample_count"] = 5;
                    arguments["constraints"][0]["inout"]["out"]["sample_count"] = 3;
                }
                var external = (KimodoExternalConstraintRequest)build.Invoke(null,
                    new object[] { arguments, trace, model, 60, 30f, false, new List<KimodoMarkerSampleResult>() });
                Assert.That(external.ConstraintSamples.Count, Is.EqualTo(8),
                    "Command diagnostics retain both explicit In and Out samples.");
                Assert.That(external.DeferredOutConstraintSamples.Count, Is.EqualTo(3),
                    "Explicit Out samples are deferred for postapply calibration.");
                Assert.That(external.DeferredInConstraintSamples.Count, Is.EqualTo(5),
                    "Explicit In samples are deferred for postapply calibration.");
                Assert.That(external.DeferInConstraint, Is.True,
                    "CLI In post_apply defaults to deferred application.");
                Assert.That(external.DeferOutConstraint, Is.True,
                    "CLI Out post_apply defaults to deferred application.");
                Assert.That(director.time, Is.EqualTo(0).Within(1e-8), "Sampling must restore Timeline time.");
                var diagnostics = (JObject)traceType.GetProperty("InOutSampling").GetValue(trace);
                Assert.That(diagnostics["in"]["source_times_seconds"], Is.Null);
                Assert.That(diagnostics["in"]["source_timeline_times_seconds"], Is.Null);
                Assert.That(diagnostics["in"]["source_timeline_frames_60"], Is.TypeOf<JArray>());
                Assert.That(diagnostics["runtime_model_frame_count"], Is.Not.Null);
                Assert.That(diagnostics["crop_start_model_frame"], Is.Not.Null);
                var firstTime = diagnostics["in"]["source_timeline_frames_60"][0].Value<int>() / 60.0;
                var sampleContext = new KimodoTimelineInOutConstraintContext
                { Director = director, Track = track, Animator = skeleton.animator, SourceClip = character.Animations[0].TimelineClip, ModelName = model };
                Assert.That(KimodoTimelineConstraintSampler.TrySampleMarker(sampleContext, firstTime, 0, "fullbody", model, out var expected, out error), Is.True, error);
                Assert.That(Vector3.Distance(external.ConstraintSamples[0].rootOverride.t, expected.rootOverride.t), Is.LessThan(0.001f), "Batch and single-frame sampling must agree.");
                Assert.That(Math.Abs(external.ConstraintSamples[0].rootOverride.t.x), Is.GreaterThan(1f), "Track offset must be retained in Character world samples.");

                typeof(command_context).GetMethod("PrepareCommandOutsideEvidencePosition", PrivateStatic)
                    .Invoke(null, new object[] { trace });
                typeof(command_context).GetMethod("ValidateCommandTimelineRange", PrivateStatic)
                    .Invoke(null, new object[] { trace });
                var playable = (KimodoPlayableClip)typeof(command_context).GetMethod("CreateGenerationPlayableClip", PrivateStatic)
                    .Invoke(null, new object[] { trace, "CommandOutput", true });
                var output = (TimelineClip)traceType.GetProperty("TimelineClip").GetValue(trace);
                typeof(command_context).GetMethod("CreateCommandOutsideEvidenceClips", PrivateStatic)
                    .Invoke(null, new object[] { trace });
                TimelineClip[] evidenceClips = track.GetClips()
                    .Where(clip => clip.asset is AnimationPlayableAsset && !(clip.asset is KimodoPlayableClip))
                    .Where(clip => (clip.displayName ?? string.Empty).StartsWith("InOut ", StringComparison.Ordinal))
                    .OrderBy(clip => clip.start)
                    .ToArray();
                Assert.That(evidenceClips, Has.Length.EqualTo(inside ? 0 : 2));
                if (!inside)
                {
                    Assert.That(output.start, Is.EqualTo(9.0 + command_context.ClipSafeZoneSeconds + 0.5).Within(1e-8),
                        "The evidence In clip gets a non-overlapping Timeline window after an unindexed clip.");
                    Assert.That(evidenceClips[0].end, Is.EqualTo(output.start).Within(1e-8));
                    Assert.That(evidenceClips[1].start, Is.EqualTo(output.end).Within(1e-8));
                    Assert.That(((AnimationPlayableAsset)evidenceClips[0].asset).clip, Is.SameAs(sourceClip));
                    Assert.That(((AnimationPlayableAsset)evidenceClips[1].asset).clip, Is.SameAs(sourceClip));
                    Assert.That(evidenceClips[0].duration, Is.EqualTo(0.5).Within(1e-8));
                    Assert.That(evidenceClips[1].duration, Is.EqualTo(0.2).Within(1e-8));
                    Assert.That(evidenceClips[0].timeScale, Is.EqualTo(1.2).Within(1e-8));
                    Assert.That(evidenceClips[1].timeScale, Is.EqualTo(1.2).Within(1e-8));
                    Assert.That(evidenceClips[0].clipIn, Is.EqualTo(character.Animations[0].TimelineClip.ToLocalTime(1.5)).Within(1e-8));
                    Assert.That(evidenceClips[1].clipIn, Is.EqualTo(character.Animations[1].TimelineClip.ToLocalTime(4.0)).Within(1e-8));
                    Assert.That(character.Animations[0].TimelineClip.start, Is.EqualTo(0));
                    Assert.That(character.Animations[0].TimelineClip.duration, Is.EqualTo(2));
                    Assert.That(character.Animations[1].TimelineClip.start, Is.EqualTo(4));
                    Assert.That(character.Animations[1].TimelineClip.duration, Is.EqualTo(2));
                }
                Assert.That(output.start, Is.GreaterThanOrEqualTo(
                    unindexedOccupant.end + command_context.ClipSafeZoneSeconds - 1e-8),
                    "The generated block must start after every actual Timeline clip.");
                playable.bridgeModelName = model;
                Assert.That(playable.enableInConstraint || playable.enableOutConstraint, Is.False,
                    "Explicit sources must not mix with automatically inferred Session boundaries.");
                Assert.That(playable.postApplyOutConstraint, Is.True,
                    "Command-created clips enable Postapply Out without exposing a command option.");
                Assert.That(playable.autoBeginAnchor, Is.True,
                    "Command-created clips must retain the automatic begin anchor; real explicit samples take precedence during normalization.");
                var request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    playable, "stand", external, CancellationToken.None,
                    timelineClipOverride: output,
                    deferOutConstraint: true,
                    deferInConstraint: true);
                Assert.That(request.TargetFrameCount, Is.EqualTo(60));
                Assert.That(request.RuntimeFrameCount, Is.EqualTo(inside ? 60 : 81));
                Assert.That(request.RuntimeTrimStartFrame, Is.EqualTo(inside ? 0 : 15));
                var deferredIn = JArray.Parse(request.DeferredInConstraintJson)
                    .Values<JObject>().Single(c => c.Value<string>("type") == "fullbody");
                CollectionAssert.AreEqual(
                    diagnostics["in"]["runtime_model_frames"].Values<int>().ToArray(),
                    deferredIn["frame_indices"].Values<int>().ToArray());
                var deferredOut = JArray.Parse(request.DeferredOutConstraintJson)
                    .Values<JObject>().Single(c => c.Value<string>("type") == "fullbody");
                CollectionAssert.AreEqual(
                    diagnostics["out"]["runtime_model_frames"].Values<int>().ToArray(),
                    deferredOut["frame_indices"].Values<int>().ToArray());
                Assert.That(output.duration, Is.EqualTo(2));
                var before = external.ConstraintSamples[0].sampleTime;
                Assert.That(before, Is.EqualTo(inside ? 0 : -0.5).Within(1e-8), "Provider must not mutate source snapshots.");

                var configure = typeof(command_context).GetMethod("ConfigureCommandInOutClip", PrivateStatic);
                Assert.That(configure, Is.Not.Null);
                configure.Invoke(null,
                    new object[] { arguments, trace, playable, model, 60, 30f, false, new List<KimodoMarkerSampleResult>() });
                Assert.That(playable.inOutConstraintMode, Is.EqualTo(inside
                    ? KimodoInOutConstraintMode.Inside
                    : KimodoInOutConstraintMode.Outside));
                Assert.That(playable.inConstraintSourceAsset, Is.SameAs(character.Animations[0].TimelineClip.asset));
                Assert.That(playable.outConstraintSourceAsset, Is.SameAs(character.Animations[1].TimelineClip.asset));
                var sharedRequest = KimodoPlayableClipGenerationHostService.BuildRequest(
                    playable, "stand", externalConstraint: null, token: CancellationToken.None,
                    timelineClipOverride: output,
                    deferInConstraint: playable.postApplyInConstraint,
                    deferOutConstraint: playable.postApplyOutConstraint);
                Assert.That(sharedRequest.RuntimeFrameCount, Is.EqualTo(inside ? 60 : 81));
                Assert.That(sharedRequest.DeferredInConstraintJson, Is.Not.Empty);
                Assert.That(sharedRequest.DeferredOutConstraintJson, Is.Not.Empty);
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
            var directorObject = new GameObject("PoseGetWorldCoordinateDirector");
            directorObject.transform.SetParent(sessionRoot.transform, false);
            var director = directorObject.AddComponent<PlayableDirector>();
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationClip sourceClip = null;
            FieldInfo currentSessionField = typeof(command_context).GetField("currentTimelineSession", PrivateStatic);
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
                string sourceObjectRef = GlobalObjectId.GetGlobalObjectIdSlow(skeleton.root).ToString();
                var character = new command_context.TimelineCharacterRecord(
                    sourceObjectRef, skeleton.root, skeleton.animator, avatar, track, poseTrack, "");
                character.Animations.Add(new command_context.TimelineAnimationRecord(
                    Guid.NewGuid(), "Sample", "test", sourceClip, timelineClip, null, null, 0, 120));

                var sessionType = typeof(command_context).GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
                var session = Activator.CreateInstance(sessionType,
                    new object[] { Guid.NewGuid(), "PoseGetWorldCoordinateTest", director, timeline, "", false, null, sessionRoot });
                ((List<command_context.TimelineCharacterRecord>)sessionType.GetProperty("Characters").GetValue(session)).Add(character);
                currentSessionField.SetValue(null, session);

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
                currentSessionField?.SetValue(null, null);
                if (director != null) director.Stop();
                foreach (var track in timeline.GetOutputTracks().ToArray())
                {
                    foreach (var clip in track.GetClips().ToArray()) Object.DestroyImmediate(clip.asset);
                    Object.DestroyImmediate(track);
                }
                Object.DestroyImmediate(timeline);
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
            foreach (var command in definitions["tools"].Values<JObject>())
            {
                string name = command.Value<string>("name");
                Assert.That(command["examples"] is JArray examples && examples.Count > 0, Is.True, name);
                JObject documentation = command["documentation"] as JObject;
                Assert.That(documentation, Is.Not.Null, name);
                Assert.That(documentation.Value<string>("when_to_use"), Is.Not.Null.And.Not.Empty, name);
                foreach (string section in new[] { "behavior", "result_notes", "scene_effects", "limits", "related_commands" })
                    Assert.That(documentation[section] is JArray values && values.Count > 0, Is.True, $"{name}.{section}");
                var detail = JObject.Parse(command_dispatcher.Invoke("kimodo.help", new JObject { ["command"] = name }.ToString()));
                Assert.That(JToken.DeepEquals(detail["manual"]?["documentation"], documentation), Is.True, name);
                Assert.That(JToken.DeepEquals(detail["manual"]["examples"], command["examples"]), Is.True, name);
                JObject overviewCommand = overview["commands"].Values<JObject>().Single(c => c.Value<string>("name") == name);
                Assert.That(overviewCommand["examples"].Count(), Is.GreaterThan(0), name);
                Assert.That(JToken.DeepEquals(overviewCommand["documentation"], documentation), Is.True, name);
            }

            JObject generate = definitions["tools"].Values<JObject>().Single(c => c.Value<string>("name") == "animation.generate");
            Assert.That(generate["inputSchema"]?["properties"]?["prompt"]?.Value<string>("description"), Does.Contain("25 words or fewer"));
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

        [TestCase("{'pose':{'track':'poses','index':0},'effector':{'right_hand':{'position':[1,2,3]}}}", true)]
        [TestCase("{'effector':{'right_hand':{'position':[1,2,3]}}}", false)]
        [TestCase("{'pose':{'track':'poses','index':0}}", false)]
        [TestCase("{'root':{'position':[0,1,0],'rotation':[0,0,0]},'effector':{'right_hand':{'position':[1,2,3]}}}", false)]
        public void PoseEffectorSafetyPreflight_RequiresExplicitPoseAndEffector(string json, bool expected)
        {
            Assert.That(command_context.HasPoseAndEffector(JObject.Parse(json)), Is.EqualTo(expected));
        }
    }
}
