using System.Linq;
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
        public void MarkerMenu_HidesLegacyConstraintMarkers()
        {
            System.Type[] legacyTypes =
            {
                typeof(KimodoEndEffectorConstraintMarker),
                typeof(KimodoFullBodyConstraintMarker),
                typeof(KimodoRoot2DConstraintMarker),
                typeof(KimodoLeftHandConstraintMarker),
                typeof(KimodoRightHandConstraintMarker),
                typeof(KimodoLeftFootConstraintMarker),
                typeof(KimodoRightFootConstraintMarker),
                typeof(KimodoUntypedConstraintMarker)
            };
            Assert.That(
                legacyTypes.All(type => System.Attribute.IsDefined(
                    type,
                    typeof(HideInMenuAttribute),
                    inherit: false)),
                Is.True);
            Assert.That(
                System.Attribute.IsDefined(
                    typeof(KimodoConstraintMarker),
                    typeof(HideInMenuAttribute),
                    inherit: false),
                Is.False);
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
            KimodoFullBodyConstraintMarker marker = ScriptableObject.CreateInstance<KimodoFullBodyConstraintMarker>();
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
            SkeletonCache skeleton = null;
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
            SkeletonCache skeleton = null;
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
            SkeletonCache skeleton = null;
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

        private static SkeletonCache BindTestSkeleton(
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
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    rootName,
                    out SkeletonCache skeleton,
                    out error),
                Is.True,
                error);
            director.SetGenericBinding(track, skeleton.animator);
            return skeleton;
        }
    }
}
