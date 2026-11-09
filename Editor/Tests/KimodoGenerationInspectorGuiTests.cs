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
        public void ModelOptionsUseCurrentKimodoDefaultAndHideLegacySomaRpVersions()
        {
            string[] options = KimodoGenerationInspectorGui.GetModelOptions(ardy: false);

            Assert.That(KimodoMotionModelProfiles.DefaultModelName, Is.EqualTo("Kimodo-SOMA-RP-v1.2"));
            Assert.That(options, Does.Contain("Kimodo-SOMA-RP-v1.2"));
            Assert.That(options, Does.Not.Contain("Kimodo-SOMA-RP-v1"));
            Assert.That(options, Does.Not.Contain("Kimodo-SOMA-RP-v1.1"));
            Assert.That(KimodoMotionModelProfiles.TryGet("Kimodo-SOMA-RP-v1", out _), Is.True);
            Assert.That(KimodoMotionModelProfiles.TryGet("Kimodo-SOMA-RP-v1.1", out _), Is.True);
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

                KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    "walk",
                    externalConstraint: null,
                    default);
                KimodoGenerationRequestDto generation = KimodoEditorGeneratePipeline.CreateRuntimePipelineRequest(
                    request,
                    "walk",
                    clip.bridgeModelName);

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
                    clip.bridgeModelName);

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
        public void GenerationRequest_DoesNotForwardDisconnectedAnalysisOptions()
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
                KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    "walk",
                    externalConstraint: null,
                    default);
                KimodoGenerationRequestDto generation = KimodoEditorGeneratePipeline.CreateRuntimePipelineRequest(
                    request,
                    "walk",
                    clip.bridgeModelName);

                Assert.That(string.IsNullOrEmpty(generation.analysis_option_json), Is.True);

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
        public void LoopRequest_SubmitsSettingsAtAuthoredDuration()
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
                clip.generateLoop = true;

                KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    "walk",
                    externalConstraint: null,
                    default);

                Assert.That(request.RuntimeFrameCount, Is.EqualTo(request.TargetFrameCount));
                Assert.That(request.RuntimeTrimStartFrame, Is.Zero);
                Assert.That(timelineClip.duration, Is.EqualTo(2.0));
                JObject plan = JObject.Parse(request.StagePlanJson);
                Assert.That(plan["loop"]["enabled"].Value<bool>(), Is.True);
                Assert.That(plan["target_frame_count"].Value<int>(), Is.EqualTo(request.TargetFrameCount));
                Assert.That(plan["stage1"], Is.Null);
                Assert.That(request.TargetClip, Is.Null, "The request must not create intermediate assets.");

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

                KimodoEditorGenerateRequest request = KimodoPlayableClipGenerationHostService.BuildRequest(
                    clip,
                    "walk",
                    externalConstraint: null,
                    default);

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
