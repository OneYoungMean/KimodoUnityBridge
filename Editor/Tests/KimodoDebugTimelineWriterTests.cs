using NUnit.Framework;
using Newtonsoft.Json.Linq;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoDebugTimelineWriterTests
    {
        [Test]
        public void TryResolveWindowPlaybackTiming_MapsFrameCountThroughTimeScale()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationTrack track = timeline.CreateTrack<AnimationTrack>();
            TimelineClip source = track.CreateClip<AnimationPlayableAsset>();
            source.start = 3.0;
            source.duration = 10.0;
            source.clipIn = 0.5;
            source.timeScale = 2.0;
            try
            {
                Assert.That(
                    KimodoDebugTimelineWriter.TryResolveWindowPlaybackTiming(
                        source,
                        requestFrameCount: 60,
                        frameRate: 30f,
                        out double duration,
                        out double timeScale),
                    Is.True);
                Assert.That(duration, Is.EqualTo(1.0));
                Assert.That(timeScale, Is.EqualTo(2.0));
            }
            finally
            {
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TryPrepare_AppendsBoundPlaceholderOnPerGenerationDebugTrackAndMutesPreviousDebugTracks()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            bool previousSetting = settings.WriteResampledTimelineCacheClips;
            Scene testScene = SceneManager.GetActiveScene();
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            TimelineClip sourceClip = null;
            KimodoEditorGenerateRequest request = null;
            KimodoEditorGenerateRequest secondRequest = null;
            KimodoEditorGenerateRequest independentRequest = null;
            GameObject directorObject = null;
            GameObject profileBinding = null;
            GameObject sourceBinding = null;
            try
            {
                settings.WriteResampledTimelineCacheClips = true;
                directorObject = new GameObject("Debug Timeline Test Director");
                SceneManager.MoveGameObjectToScene(directorObject, testScene);
                PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;

                AnimationTrack oldDebugTrack = timeline.CreateTrack<AnimationTrack>(null, "debug track");
                oldDebugTrack.muted = false;
                AnimationTrack oldRollTrack = timeline.CreateTrack<AnimationTrack>(null, "DebugRoll_legacy");
                oldRollTrack.muted = false;
                AnimationTrack sourceTrack = timeline.CreateTrack<AnimationTrack>(null, "Source");
                sourceBinding = new GameObject("Formal Source Character");
                SceneManager.MoveGameObjectToScene(sourceBinding, testScene);
                Animator sourceAnimator = sourceBinding.AddComponent<Animator>();
                director.SetGenericBinding(sourceTrack, sourceAnimator);
                profileBinding = CreateProfileBinding(
                    KimodoMotionModelProfiles.DefaultModelName,
                    testScene,
                    out Animator profileAnimator);
                sourceClip = sourceTrack.CreateClip<KimodoPlayableClip>();
                sourceClip.start = 4.0;
                sourceClip.duration = 2.0;
                sourceClip.clipIn = 0.25;
                sourceClip.timeScale = 1.25;
                KimodoPlayableClip sourceAsset = (KimodoPlayableClip)sourceClip.asset;
                request = new KimodoEditorGenerateRequest
                {
                    ModelName = KimodoMotionModelProfiles.DefaultModelName,
                    TargetFrameCount = 60,
                    RuntimeFrameCount = 90,
                    TargetFrameRate = 30f,
                    TimelineClipSnapshot = sourceClip
                };

                KimodoDebugTimelineWriter.BeginGeneration(sourceClip);
                sourceAsset.generationOutputMode = KimodoGenerationOutputMode.ModelBone;
                KimodoEditorGenerateOutputPlan outputPlanSnapshot = KimodoTimelineGenerationOutputPlanner.Capture(
                    sourceAsset,
                    explicitRetargetAvatar: null,
                    KimodoMotionModelProfiles.DefaultModelName,
                    profileBinding);
                AnimationClip generatedProfileClip = new AnimationClip();
                try
                {
                    KimodoEditorGenerateOutputPlan outputPlan = KimodoTimelineGenerationOutputPlanner.Resolve(
                        outputPlanSnapshot,
                        profileBinding,
                        generatedProfileClip,
                        KimodoMotionModelProfiles.DefaultModelName);
                    Assert.That(outputPlan.SkipRetarget, Is.True,
                        "The profile Animator binding must be captured before the formal output plan is resolved.");
                }
                finally
                {
                    Object.DestroyImmediate(generatedProfileClip);
                }
                KimodoDebugTimelineWriter.TryPrepare(sourceAsset, request, "Generation Pass");

                Assert.That(oldDebugTrack.muted, Is.True);
                Assert.That(oldRollTrack.muted, Is.True);
                Assert.That(request.DebugTimelineReservation, Is.Not.Null);
                KimodoDebugTimelineReservation reservation = request.DebugTimelineReservation;
                Assert.That(request.DebugTimelineGroup.Timeline, Is.SameAs(timeline));
                Assert.That(request.DebugTimelineGroup.Track, Is.Not.SameAs(sourceTrack));
                Assert.That(request.DebugTimelineGroup.Track.name, Does.StartWith("debug track"));
                Assert.That(timeline.GetOutputTracks().Count(), Is.EqualTo(4));
                Assert.That(timeline.GetOutputTracks().Count(item =>
                    item.name.StartsWith("DebugRoll_", System.StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1),
                    "The legacy DebugRoll track may be muted, but generation must not create another one.");
                Assert.That(director.GetGenericBinding(request.DebugTimelineGroup.Track), Is.SameAs(request.DebugTimelineGroup.Animator));
                Assert.That(director.GetGenericBinding(sourceTrack), Is.SameAs(sourceAnimator),
                    "The formal animation track binding must remain unchanged.");
                Assert.That(request.DebugTimelineGroup.ProfileInstance.GetComponentsInChildren<Animator>(true)
                    .All(item => item.avatar == null), Is.True,
                    "Debug profile bindings must stay generic and must not use a Humanoid Avatar.");
                Assert.That(request.DebugTimelineGroup.ProfileInstance.transform.position, Is.EqualTo(Vector3.zero));
                Assert.That(reservation.TimelineClip.asset, Is.TypeOf<KimodoPlayableClip>());
                Assert.That(((KimodoPlayableClip)reservation.TimelineClip.asset).clip, Is.Null);
                Assert.That(((KimodoPlayableClip)reservation.TimelineClip.asset).removeStartOffset, Is.False);
                KimodoDebugTimelineReservation overview = request.DebugTimelineGroup.OverviewReservations.Single();
                Assert.That(overview.IsOverview, Is.True);
                Assert.That(overview.TimelineClip.start, Is.EqualTo(sourceClip.start));
                Assert.That(overview.TimelineClip.duration, Is.EqualTo(sourceClip.duration));
                Assert.That(((KimodoPlayableClip)overview.TimelineClip.asset).debugPassName,
                    Is.EqualTo("Raw Overview"));
                Assert.That(reservation.TimelineClip.start,
                    Is.EqualTo(overview.TimelineClip.end + 5.0 / timeline.editorSettings.frameRate));
                Assert.That(reservation.TimelineClip.duration, Is.EqualTo(2.4).Within(1e-6),
                    "The raw placeholder duration must include all 90 backend frames at the source playback speed.");
                Assert.That(reservation.TimelineClip.clipIn, Is.EqualTo(0.0));
                Assert.That(reservation.TimelineClip.timeScale, Is.EqualTo(sourceClip.timeScale));
                Assert.That(((KimodoPlayableClip)reservation.TimelineClip.asset).isDebugRawClip, Is.True);
                Assert.That(testScene.GetRootGameObjects().Any(item => item.name.StartsWith("KimodoSession_")), Is.False);

                secondRequest = new KimodoEditorGenerateRequest
                {
                    ModelName = KimodoMotionModelProfiles.DefaultModelName,
                    TargetFrameCount = 60,
                    TargetFrameRate = 30f,
                    TimelineClipSnapshot = sourceClip
                };
                secondRequest.DebugTimelineGroup = request.DebugTimelineGroup;
                KimodoDebugTimelineWriter.TryPrepare(sourceAsset, secondRequest, "Trajectory Pass 2");
                Assert.That(secondRequest.DebugTimelineReservation, Is.Not.Null);
                Assert.That(secondRequest.DebugTimelineGroup.Track, Is.SameAs(request.DebugTimelineGroup.Track));
                Assert.That(secondRequest.DebugTimelineGroup.ProfileInstance,
                    Is.SameAs(request.DebugTimelineGroup.ProfileInstance));
                Assert.That(
                    secondRequest.DebugTimelineReservation.TimelineClip.start,
                    Is.EqualTo(reservation.TimelineClip.end + 5.0 / timeline.editorSettings.frameRate));
                Assert.That(request.DebugTimelineGroup.OverviewReservations.Single().TimelineClip,
                    Is.SameAs(overview.TimelineClip));
                Assert.That(request.DebugTimelineGroup.Track.GetClips().Count(), Is.EqualTo(3),
                    "The shared Debug track should contain one overview and one placeholder per pass.");
                Assert.That(timeline.GetOutputTracks().Count(), Is.EqualTo(4));

                independentRequest = new KimodoEditorGenerateRequest
                {
                    ModelName = KimodoMotionModelProfiles.DefaultModelName,
                    TargetFrameCount = 60,
                    TargetFrameRate = 30f,
                    TimelineClipSnapshot = sourceClip
                };
                KimodoDebugTimelineWriter.TryPrepare(sourceAsset, independentRequest, "Separate Generation");
                Assert.That(independentRequest.DebugTimelineReservation, Is.Not.Null);
                Assert.That(independentRequest.DebugTimelineGroup.Track,
                    Is.Not.SameAs(request.DebugTimelineGroup.Track));
                Assert.That(independentRequest.DebugTimelineGroup.ProfileInstance,
                    Is.SameAs(request.DebugTimelineGroup.ProfileInstance));
                Assert.That(independentRequest.DebugTimelineGroup.Animator,
                    Is.SameAs(request.DebugTimelineGroup.Animator));
                Assert.That(timeline.GetOutputTracks().Count(), Is.EqualTo(5));
            }
            finally
            {
                settings.WriteResampledTimelineCacheClips = previousSetting;
                if (profileBinding != null)
                    Object.DestroyImmediate(profileBinding);
                if (request?.DebugTimelineGroup?.ProfileInstance != null)
                    Object.DestroyImmediate(request.DebugTimelineGroup.ProfileInstance);
                if (independentRequest?.DebugTimelineGroup?.ProfileInstance != null)
                    Object.DestroyImmediate(independentRequest.DebugTimelineGroup.ProfileInstance);
                if (sourceBinding != null)
                    Object.DestroyImmediate(sourceBinding);
                if (directorObject != null)
                    Object.DestroyImmediate(directorObject);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TryPrepare_CreatesSeparateDebugBindingWithoutChangingUnboundFormalTrack()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            bool previousSetting = settings.WriteResampledTimelineCacheClips;
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject directorObject = new GameObject("Unbound Debug Timeline Test Director");
            KimodoEditorGenerateRequest request = null;
            try
            {
                settings.WriteResampledTimelineCacheClips = true;
                Scene testScene = SceneManager.GetActiveScene();
                SceneManager.MoveGameObjectToScene(directorObject, testScene);
                PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                AnimationTrack sourceTrack = timeline.CreateTrack<AnimationTrack>(null, "Unbound Source");
                TimelineClip sourceTimelineClip = sourceTrack.CreateClip<KimodoPlayableClip>();
                KimodoPlayableClip sourceAsset = (KimodoPlayableClip)sourceTimelineClip.asset;
                request = new KimodoEditorGenerateRequest
                {
                    ModelName = KimodoMotionModelProfiles.DefaultModelName,
                    TargetFrameCount = 60,
                    TargetFrameRate = 30f,
                    TimelineClipSnapshot = sourceTimelineClip
                };

                KimodoDebugTimelineWriter.TryPrepare(sourceAsset, request, "Generation Pass");

                Assert.That(request.DebugTimelineReservation, Is.Not.Null);
                Assert.That(director.GetGenericBinding(sourceTrack), Is.Null);
                Assert.That(request.DebugTimelineGroup.Track, Is.Not.SameAs(sourceTrack));
                Assert.That(director.GetGenericBinding(request.DebugTimelineGroup.Track),
                    Is.SameAs(request.DebugTimelineGroup.Animator));
                Assert.That(request.DebugTimelineGroup.ProfileInstance.GetComponentsInChildren<Animator>(true)
                    .All(item => item.avatar == null), Is.True);
            }
            finally
            {
                settings.WriteResampledTimelineCacheClips = previousSetting;
                if (request?.DebugTimelineGroup?.ProfileInstance != null)
                    Object.DestroyImmediate(request.DebugTimelineGroup.ProfileInstance);
                Object.DestroyImmediate(directorObject);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TryPrepare_WhenDisabledDoesNotCreateProfileOrDebugTrack()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            bool previousSetting = settings.WriteResampledTimelineCacheClips;
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject directorObject = new GameObject("Disabled Debug Timeline Test Director");
            try
            {
                settings.WriteResampledTimelineCacheClips = false;
                Scene testScene = SceneManager.GetActiveScene();
                SceneManager.MoveGameObjectToScene(directorObject, testScene);
                PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                AnimationTrack sourceTrack = timeline.CreateTrack<AnimationTrack>(null, "Source");
                TimelineClip sourceTimelineClip = sourceTrack.CreateClip<KimodoPlayableClip>();
                KimodoPlayableClip sourceAsset = (KimodoPlayableClip)sourceTimelineClip.asset;
                var request = new KimodoEditorGenerateRequest
                {
                    ModelName = KimodoMotionModelProfiles.DefaultModelName,
                    TargetFrameCount = 60,
                    TargetFrameRate = 30f,
                    TimelineClipSnapshot = sourceTimelineClip
                };
                int trackCount = timeline.GetOutputTracks().Count();

                KimodoDebugTimelineWriter.TryPrepare(sourceAsset, request, "Generation Pass");

                Assert.That(request.DebugTimelineGroup, Is.Null);
                Assert.That(request.DebugTimelineReservation, Is.Null);
                Assert.That(timeline.GetOutputTracks().Count(), Is.EqualTo(trackCount));
                Assert.That(testScene.GetRootGameObjects().Any(item =>
                    item.name.StartsWith("KimodoDebugProfile_", System.StringComparison.Ordinal)), Is.False);
            }
            finally
            {
                settings.WriteResampledTimelineCacheClips = previousSetting;
                Object.DestroyImmediate(directorObject);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TryWrite_BackendSegmentsUseLocalFramesAndConstraintTimes()
        {
            var settings = KimodoPlayableClipGenerationSettings.instance;
            bool previous = settings.WriteResampledTimelineCacheClips;
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var directorObject = new GameObject("Backend Debug Segment Test");
            var director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            var track = timeline.CreateTrack<AnimationTrack>(null, "Source");
            var source = track.CreateClip<KimodoPlayableClip>();
            source.start = 4;
            source.duration = 6.0 / 30;
            KimodoEditorGenerateRequest request = null;
            try
            {
                settings.WriteResampledTimelineCacheClips = true;
                string model = KimodoMotionModelProfiles.DefaultModelName;
                string[] names = KimodoRigProfileDatabase.GetJointNamesForModel(model);
                int[] parents = KimodoRigProfileDatabase.GetParentIndicesForModel(model);
                var roots = Enumerable.Range(0, 6).Select(frame => new Vector3(frame, 1, 0)).ToArray();
                var rotations = Enumerable.Range(0, 6 * names.Length)
                    .SelectMany(_ => new[] { 1f, 0f, 0f, 0f }).ToList();
                request = new KimodoEditorGenerateRequest
                {
                    ModelName = model, TargetFrameCount = 6, TargetFrameRate = 30,
                    TimelineClipSnapshot = source,
                    DebugRuntimeMotion = new KimodoRawMotionData(6, names.Length, 30,
                        names, parents, roots, rotations, 0),
                    DebugSegmentsJson = "[{\"name\":\"Stage 2\",\"debug_start_frame\":0,\"debug_end_frame_exclusive\":2}," +
                        "{\"name\":\"Stage 3\",\"debug_start_frame\":2,\"debug_end_frame_exclusive\":6}]",
                    DebugConstraintsJson = "[{\"artifact\":\"Stage 2\",\"constraints\":[{\"type\":\"root2d\",\"frame_indices\":[1],\"smooth_root_2d\":[[1,0]]}]}," +
                        "{\"artifact\":\"Stage 3\",\"constraints\":[{\"type\":\"root2d\",\"frame_indices\":[3],\"smooth_root_2d\":[[5,0]]}]}]"
                };
                var asset = (KimodoPlayableClip)source.asset;
                KimodoDebugTimelineWriter.TryPrepare(asset, request, "Generation");
                var result = new KimodoEditorGenerationResult();
                KimodoDebugTimelineWriter.TryWrite(asset, request, result);

                var segments = request.DebugTimelineGroup.Track.GetClips()
                    .Where(clip => result.DebugConstraintPasses.Any(pass => pass.DebugClipTarget == clip.asset))
                    .OrderBy(clip => clip.start).ToArray();
                Assert.That(segments, Has.Length.EqualTo(2));
                Assert.That(segments[0].duration, Is.EqualTo(2.0 / 30).Within(1e-6));
                Assert.That(segments[1].duration, Is.EqualTo(4.0 / 30).Within(1e-6));
                Assert.That(segments.All(clip => ((KimodoPlayableClip)clip.asset).clip != null), Is.True);
                Assert.That(result.DebugConstraintPasses.Select(pass => pass.FrameCount), Is.EqualTo(new[] { 2, 4 }));
                var markers = request.DebugTimelineGroup.Track.GetMarkers()
                    .OfType<KimodoDebugConstraintJsonMarker>().OrderBy(marker => marker.time).ToArray();
                Assert.That(markers, Has.Length.EqualTo(2));
                Assert.That(markers[0].time, Is.EqualTo(segments[0].start + 1.0 / 30).Within(1e-6));
                Assert.That(markers[1].time, Is.EqualTo(segments[1].start + 3.0 / 30).Within(1e-6));
            }
            finally
            {
                settings.WriteResampledTimelineCacheClips = previous;
                if (request?.DebugTimelineGroup != null)
                {
                    string folder = KimodoDebugTimelineWriter.DebugFolder;
                    string archive = folder + "/archive";
                    foreach (var clip in request.DebugTimelineGroup.Track.GetClips())
                    {
                        var animation = (clip.asset as KimodoPlayableClip)?.clip;
                        string path = AssetDatabase.GetAssetPath(animation);
                        if (string.IsNullOrEmpty(path)) continue;
                        if (!AssetDatabase.IsValidFolder(archive)) AssetDatabase.CreateFolder(folder, "archive");
                        string moved = AssetDatabase.MoveAsset(path,
                            AssetDatabase.GenerateUniqueAssetPath(archive + "/" + System.IO.Path.GetFileName(path)));
                        Assert.That(moved, Is.Empty);
                    }
                    if (request.DebugTimelineGroup.ProfileInstance != null)
                        Object.DestroyImmediate(request.DebugTimelineGroup.ProfileInstance);
                }
                Object.DestroyImmediate(directorObject);
                Object.DestroyImmediate(timeline);
            }
        }

        private static GameObject CreateProfileBinding(string modelName, Scene scene, out Animator animator)
        {
            string modelPath = AssetDatabase.GUIDToAssetPath(
                KimodoDebugTimelineWriter.ResolveDebugProfileModelGuid(modelName));
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            Assert.That(model, Is.Not.Null, $"Profile model asset was not found at '{modelPath}'.");
            GameObject instance = Object.Instantiate(model);
            instance.name = $"KimodoDebugProfile_Test_{modelName}";
            SceneManager.MoveGameObjectToScene(instance, scene);
            instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (Animator profileAnimator in instance.GetComponentsInChildren<Animator>(true))
                profileAnimator.avatar = null;
            animator = instance.GetComponentInChildren<Animator>(true);
            Assert.That(animator, Is.Not.Null, "Profile model must contain an Animator for AnimationTrack binding.");
            return instance;
        }

        [Test]
        public void TryAttachConstraintMarkers_CreatesOneMarkerPerRawConstraint()
        {
            KimodoPlayableClipGenerationSettings settings = KimodoPlayableClipGenerationSettings.instance;
            bool previousSetting = settings.WriteResampledTimelineCacheClips;
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Source");
            TimelineClip sourceClip = track.CreateClip<KimodoPlayableClip>();
            GameObject directorObject = new GameObject("Debug Constraint Test Director");
            KimodoEditorGenerateRequest request = null;
            Scene testScene = SceneManager.GetActiveScene();
            SceneManager.MoveGameObjectToScene(directorObject, testScene);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            try
            {
                settings.WriteResampledTimelineCacheClips = true;
                request = new KimodoEditorGenerateRequest
                {
                    ModelName = "SOMA30",
                    TargetFrameCount = 60,
                    TargetFrameRate = 30f,
                    TimelineClipSnapshot = sourceClip
                };
                KimodoDebugTimelineWriter.TryPrepare(
                    sourceClip.asset as KimodoPlayableClip,
                    request,
                    "Trajectory Pass 1");
                var pass = new KimodoDebugConstraintPass
                {
                    Name = "Trajectory Path Constraints",
                    AddedConstraintsJson = "[{\"type\":\"Root2D\",\"frame_indices\":[0,7,15,22,29],\"root_positions\":[[0,0],[1,0],[2,0],[3,0],[4,0]]}]",
                    CombinedConstraintsJson = "[{\"type\":\"Root2D\"},{\"type\":\"Effector\"}]",
                    ModelName = "SOMA30",
                    FrameRate = 30,
                    FrameCount = 60,
                };
                KimodoEditorGenerateRequest secondRequest = new KimodoEditorGenerateRequest
                {
                    ModelName = "SOMA30",
                    TargetFrameCount = 60,
                    TargetFrameRate = 30f,
                    TimelineClipSnapshot = sourceClip,
                    DebugTimelineGroup = request.DebugTimelineGroup
                };
                KimodoDebugTimelineWriter.TryPrepare(
                    sourceClip.asset as KimodoPlayableClip,
                    secondRequest,
                    "Trajectory Pass 2");
                var secondPass = new KimodoDebugConstraintPass
                {
                    Name = "Trajectory Final Constraints",
                    AddedConstraintsJson = "[{\"type\":\"Effector\",\"frame_indices\":[3]}]",
                    CombinedConstraintsJson = "[{\"type\":\"Root2D\"},{\"type\":\"Effector\"}]",
                    ModelName = "SOMA30",
                    FrameRate = 30,
                    FrameCount = 60,
                    DebugClipTarget = secondRequest.DebugTimelineReservation.TimelineClip.asset as KimodoPlayableClip
                };
                pass.DebugClipTarget = request.DebugTimelineReservation.TimelineClip.asset as KimodoPlayableClip;

                int trackCountBeforeAttach = timeline.GetOutputTracks().Count();
                KimodoDebugTimelineWriter.TryAttachConstraintMarkers(secondRequest, new[] { pass, secondPass });

                KimodoDebugTimelineGroup group = request.DebugTimelineGroup;
                KimodoPlayableClip rawClip = request.DebugTimelineReservation.TimelineClip.asset as KimodoPlayableClip;
                KimodoPlayableClip secondRawClip = secondRequest.DebugTimelineReservation.TimelineClip.asset as KimodoPlayableClip;
                var markers = group.Track.GetMarkers()
                    .OfType<KimodoDebugConstraintJsonMarker>()
                    .OrderBy(marker => marker.time)
                    .ToArray();
                Assert.That(markers, Has.Length.EqualTo(6));
                Assert.That(markers.Count(marker => marker.PassName == pass.Name), Is.EqualTo(5));
                Assert.That(markers.Count(marker => marker.PassName == secondPass.Name), Is.EqualTo(1));
                Assert.That(markers.Take(5).All(marker => marker.AddedConstraintsJson.Contains("Root2D")), Is.True);
                Assert.That(markers.Last().AddedConstraintsJson, Does.Contain("Effector"));
                Assert.That(markers.All(marker => marker.CombinedConstraintsJson == marker.AddedConstraintsJson), Is.True,
                    "Each debug marker must retain one atomic raw constraint rather than the pass-level merged payload.");
                Assert.That(markers.Take(5).All(marker =>
                {
                    JArray json = JArray.Parse(marker.AddedConstraintsJson);
                    return json.Count == 1 &&
                           json[0]["frame_indices"] is JArray frames && frames.Count == 1;
                }), Is.True);
                Assert.That(markers.Take(5).Select(marker => marker.time).ToArray(),
                    Is.EqualTo(new[]
                    {
                        request.DebugTimelineReservation.TimelineClip.start + 0.0,
                        request.DebugTimelineReservation.TimelineClip.start + 7.0 / 30.0,
                        request.DebugTimelineReservation.TimelineClip.start + 15.0 / 30.0,
                        request.DebugTimelineReservation.TimelineClip.start + 22.0 / 30.0,
                        request.DebugTimelineReservation.TimelineClip.start + 29.0 / 30.0
                    }));
                Assert.That(markers.Last().time, Is.EqualTo(
                    secondRequest.DebugTimelineReservation.TimelineClip.start + 3.0 / 30.0).Within(1e-6));
                Assert.That(rawClip, Is.Not.Null);
                Assert.That(secondRawClip, Is.Not.Null);
                Assert.That(timeline.GetOutputTracks().Count(), Is.EqualTo(trackCountBeforeAttach));
                Assert.That(timeline.GetOutputTracks().Any(item =>
                    item.name.StartsWith("DebugRoll_", System.StringComparison.OrdinalIgnoreCase)), Is.False);
            }
            finally
            {
                settings.WriteResampledTimelineCacheClips = previousSetting;
                if (request?.DebugTimelineGroup?.ProfileInstance != null)
                    Object.DestroyImmediate(request.DebugTimelineGroup.ProfileInstance);
                Object.DestroyImmediate(directorObject);
                Object.DestroyImmediate(timeline);
            }
        }

    }
}
