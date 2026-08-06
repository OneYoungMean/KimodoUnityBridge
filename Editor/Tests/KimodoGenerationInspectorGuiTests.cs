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
        public void MarkerMenu_HidesGenericEndEffectorOnly()
        {
            Assert.That(
                System.Attribute.IsDefined(
                    typeof(KimodoEndEffectorConstraintMarker),
                    typeof(HideInMenuAttribute),
                    inherit: false),
                Is.True);
            Assert.That(
                System.Attribute.IsDefined(
                    typeof(KimodoLeftHandConstraintMarker),
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

        [TestCase(KimodoPlayableClip.DefaultBridgeModelName, "Kimodo_Playable_20260730_120000_123")]
        [TestCase(KimodoMotionModelProfiles.ArdyCoreModelName, "ARDY_Playable_20260730_120000_123")]
        public void TimelineGeneratedClipName_IdentifiesModelFamily(string modelName, string expected)
        {
            Assert.That(
                KimodoPlayableClipGenerationHostService.BuildTimelineTargetClipName(
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
                rawBone = KimodoEditorGeneratePipeline.CreateRawBoneWritebackClip(source);

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
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.duration = 4.0;
                directorRoot.AddComponent<PlayableDirector>().playableAsset = timeline;
                var clip = (KimodoPlayableClip)timelineClip.asset;
                clip.bridgeModelName = KimodoMotionModelProfiles.ArdyCoreModelName;
                clip.inOutConstraintMode = KimodoInOutConstraintMode.None;
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

                Assert.That(
                    request.ConstraintSamples.Any(
                        sample => sample != null && sample.constraintType == "root2d_target"),
                    Is.False);
                Assert.That(generation.ardy_history_crop_seconds, Is.Zero);
                Assert.That(generation.ardy_max_speed, Is.EqualTo(2.25));
                Assert.That(generation.ardy_max_acceleration, Is.EqualTo(3.5));
                Assert.That(generation.ardy_history_transition_weight, Is.EqualTo(0.5));
            }
            finally
            {
                Object.DestroyImmediate(directorRoot);
                Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ArdyRequest_UsesManualHistoryWeightWhenAutoHistoryIsDisabled()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject directorRoot = new GameObject("KimodoArdyManualHistoryRequestTest");
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                timelineClip.duration = 4.0;
                directorRoot.AddComponent<PlayableDirector>().playableAsset = timeline;
                var clip = (KimodoPlayableClip)timelineClip.asset;
                clip.bridgeModelName = KimodoMotionModelProfiles.ArdyCoreModelName;
                clip.inOutConstraintMode = KimodoInOutConstraintMode.None;
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

                Assert.That(generation.text_weight, Is.EqualTo(1f));
                Assert.That(generation.ardy_history_crop_seconds, Is.Null);
                Assert.That(generation.ardy_history_weight, Is.EqualTo(0.25));
                Assert.That(generation.ardy_max_speed, Is.Null);
                Assert.That(generation.ardy_max_acceleration, Is.Null);
                Assert.That(generation.ardy_history_transition_weight, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(directorRoot);
                Object.DestroyImmediate(timeline);
            }
        }
    }
}
