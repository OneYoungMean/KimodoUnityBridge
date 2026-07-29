using System;
using System.Reflection;
using NUnit.Framework;
using TimelineInject;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Timeline;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoTimelinePoseSamplerTests
    {
        [Test]
        public void ResolveSourceHumanBone_UsesSourceAvatarWhenAnimatorAvatarIsNull()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);

            HumanBone hips = Array.Find(
                avatar.humanDescription.human,
                bone => bone.humanName == HumanBodyBones.Hips.ToString());
            var root = new GameObject("Root");
            try
            {
                Animator animator = root.AddComponent<Animator>();
                Transform expected = new GameObject(hips.boneName).transform;
                expected.SetParent(root.transform);

                Assert.That(animator.avatar, Is.Null);
                Assert.That(
                    KimodoTimelinePoseSampler.ResolveSourceHumanBone(animator, avatar, HumanBodyBones.Hips),
                    Is.SameAs(expected));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void TimelineMatchPrevious_UsesHumanoidHipsAsMatchPoint()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoTimelineMatchHipsTest",
                    out SkeletonCache cache,
                    out error),
                Is.True,
                error);

            try
            {
                MethodInfo resolve = typeof(KimodoTimelinePreviewRefreshUtility).GetMethod(
                    "ResolveHumanoidHipsMatchPoint",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(resolve, Is.Not.Null);
                Transform hips = resolve.Invoke(null, new object[] { cache.animator.gameObject }) as Transform;
                Assert.That(hips, Is.SameAs(cache.animator.GetBoneTransform(HumanBodyBones.Hips)));
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void ArdyHistoryRoot_UsesSamePlanarAnchorAsConstraints()
        {
            var source = new ArdyEditorHistorySource
            {
                NormalizeRootToAnchor = true,
                AnchorRootPosition = new Vector3(10f, 0f, 20f),
                AnchorRootRotation = Quaternion.Euler(0f, 90f, 0f)
            };
            Vector3 position = new Vector3(12f, 3f, 25f);
            Quaternion rotation = Quaternion.Euler(0f, 120f, 0f);
            Quaternion inverseAnchor = Quaternion.Inverse(source.AnchorRootRotation);

            KimodoConstraintNormalizationUtility.NormalizeRootPose(
                source.AnchorRootPosition,
                source.AnchorRootRotation,
                ref position,
                ref rotation);

            Assert.That(
                Vector3.Distance(position, inverseAnchor * new Vector3(2f, 3f, 5f)),
                Is.LessThan(1e-5f));
            Assert.That(
                Quaternion.Angle(rotation, inverseAnchor * Quaternion.Euler(0f, 120f, 0f)),
                Is.LessThan(1e-4f));
        }

        [Test]
        public void HumanoidIkGoals_AreInvariantToSkeletonRootWorldPose()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoTimelineFootIkRootSpaceTest",
                    out SkeletonCache cache,
                    out error),
                Is.True,
                error);

            try
            {
                var pose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref pose);
                MuscleSample before = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, pose);

                cache.skeletonRoot.SetPositionAndRotation(
                    new Vector3(7f, 2f, -3f),
                    Quaternion.Euler(0f, 73f, 0f));
                cache.poseHandler.GetHumanPose(ref pose);
                MuscleSample after = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, pose);

                Assert.That(Vector3.Distance(before.leftFootPosition, after.leftFootPosition), Is.LessThan(1e-5f));
                Assert.That(Vector3.Distance(before.rightFootPosition, after.rightFootPosition), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(before.leftFootRotation, after.leftFootRotation), Is.LessThan(1e-4f));
                Assert.That(Quaternion.Angle(before.rightFootRotation, after.rightFootRotation), Is.LessThan(1e-4f));
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void SingleMuscleSample_RestoresAbsoluteTargetRootAfterHumanoidFootIkPlayable()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoTimelineSingleFrameFootIkTest",
                    out SkeletonCache cache,
                    out error),
                Is.True,
                error);

            try
            {
                var pose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref pose);
                MuscleSample source = KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, pose);
                Vector3 rootOffset = new Vector3(0.25f, 0f, -0.4f);
                source.pose.bodyPosition += rootOffset / cache.humanScale;
                HumanPose directPose = source.pose;
                cache.poseHandler.SetHumanPose(ref directPose);
                Assert.That(
                    KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                        KimodoPlayableClip.DefaultBridgeModelName,
                        cache,
                        out _,
                        out _,
                        out Transform[] directJoints,
                        out error),
                    Is.True,
                    error);
                Vector3 directRootPosition = directJoints[0].position;
                Quaternion directRootRotation = directJoints[0].rotation;

                Assert.That(
                    KimodoRetargetSamplingUtility.TrySampleTargetFromSingleMuscleSample(
                        source,
                        KimodoPlayableClip.FIXED_FRAME_RATE,
                        cache,
                        out BoneSample target,
                        out MuscleSample targetMuscle,
                        out error),
                    Is.True,
                    error);
                Assert.That(target, Is.Not.Null);
                Assert.That(target.IsValid, Is.True);
                Assert.That(targetMuscle, Is.Not.Null);
                Assert.That(
                    KimodoRetargetMarkerSamplingUtility.TryBuildMarkerSampleResultFromBoneSample(
                        target,
                        cache,
                        KimodoPlayableClip.DefaultBridgeModelName,
                        "fullbody",
                        0.0,
                        out TimelineInject.KimodoMarkerSampleResult markerSample,
                        out error),
                    Is.True,
                    error);
                float sampledRootY = markerSample.kimodoRootPosition.y;
                KimodoTimelineConstraintClipCache.ApplyTargetRootPose(
                    markerSample,
                    directRootPosition,
                    directRootRotation,
                    exportedSampleTime: 0.0);
                Assert.That(
                    Vector2.Distance(
                        new Vector2(markerSample.kimodoRootPosition.x, markerSample.kimodoRootPosition.z),
                        new Vector2(directRootPosition.x, directRootPosition.z)),
                    Is.LessThan(1e-3f));
                Assert.That(markerSample.kimodoRootPosition.y, Is.EqualTo(sampledRootY).Within(1e-5f));
                Vector3 restoredAxisAngle = markerSample.localAxisAngles[0];
                Quaternion restoredRootRotation = restoredAxisAngle.sqrMagnitude > 1e-12f
                    ? Quaternion.AngleAxis(
                        restoredAxisAngle.magnitude * Mathf.Rad2Deg,
                        restoredAxisAngle.normalized)
                    : Quaternion.identity;
                Assert.That(Quaternion.Angle(restoredRootRotation, directRootRotation), Is.LessThan(1e-3f));
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void TrackOffset_ResolvesWorldPose()
        {
            AnimationTrack track = ScriptableObject.CreateInstance<AnimationTrack>();
            var parent = new GameObject("TrackOffsetParent");
            var character = new GameObject("TrackOffsetCharacter");
            try
            {
                parent.transform.SetPositionAndRotation(
                    new Vector3(4f, 1f, -2f),
                    Quaternion.Euler(0f, 30f, 0f));
                character.transform.SetParent(parent.transform, false);
                Animator animator = character.AddComponent<Animator>();
                track.trackOffset = TrackOffset.ApplyTransformOffsets;
                track.position = new Vector3(2f, 0.5f, 3f);
                track.rotation = Quaternion.Euler(0f, 40f, 0f);

                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    track,
                    animator,
                    out Vector3 offsetPosition,
                    out Quaternion offsetRotation,
                    out bool rootPoseIncludesOffset);

                Assert.That(
                    Vector3.Distance(offsetPosition, parent.transform.TransformPoint(track.position)),
                    Is.LessThan(1e-5f));
                Assert.That(
                    Quaternion.Angle(offsetRotation, parent.transform.rotation * track.rotation),
                    Is.LessThan(1e-4f));
                Assert.That(rootPoseIncludesOffset, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(track);
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void SceneOffset_UsesTimelinePreviewFields()
        {
            AnimationTrack track = ScriptableObject.CreateInstance<AnimationTrack>();
            var character = new GameObject("SceneOffsetCharacter");
            try
            {
                Animator animator = character.AddComponent<Animator>();
                track.trackOffset = TrackOffset.ApplySceneOffsets;
                character.transform.SetPositionAndRotation(
                    new Vector3(9f, 8f, 7f),
                    Quaternion.Euler(0f, 80f, 0f));
                Vector3 expectedPosition = new Vector3(-1f, 0f, 0f);
                Vector3 expectedEuler = new Vector3(0f, 35f, 0f);
                typeof(AnimationTrack).GetField(
                    "m_SceneOffsetPosition",
                    BindingFlags.Instance | BindingFlags.NonPublic).SetValue(track, expectedPosition);
                typeof(AnimationTrack).GetField(
                    "m_SceneOffsetRotation",
                    BindingFlags.Instance | BindingFlags.NonPublic).SetValue(track, expectedEuler);

                KimodoTimelineTrackOffsetUtility.ResolveWorldOffset(
                    track,
                    animator,
                    out Vector3 offsetPosition,
                    out Quaternion offsetRotation,
                    out bool rootPoseIncludesOffset);

                Assert.That(Vector3.Distance(offsetPosition, expectedPosition), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(offsetRotation, Quaternion.Euler(expectedEuler)), Is.LessThan(1e-4f));
                Assert.That(rootPoseIncludesOffset, Is.True);

            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(track);
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        [Test]
        public void AnimationOffsetPlayable_AppliesRootOffsetExactlyOnceOnFirstFrame()
        {
            Assert.That(
                KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    out Avatar avatar,
                    out string error),
                Is.True,
                error);
            Assert.That(
                KimodoRetargetAvatarUtility.TryBuildSkeletonCache(
                    avatar,
                    "KimodoTimelineOffsetPlayableTest",
                    out SkeletonCache cache,
                    out error),
                Is.True,
                error);
            Assert.That(
                KimodoProfileSkeletonUtility.TryResolveProfileSkeleton(
                    KimodoPlayableClip.DefaultBridgeModelName,
                    cache,
                    out _,
                    out _,
                    out Transform[] joints,
                    out error),
                Is.True,
                error);
            Transform profileRoot = joints[0];

            var clip = new AnimationClip { frameRate = KimodoPlayableClip.FIXED_FRAME_RATE };
            PlayableGraph graph = default;
            try
            {
                var sourcePose = new HumanPose();
                cache.poseHandler.GetHumanPose(ref sourcePose);
                sourcePose.bodyPosition += new Vector3(0.3f, 0f, -0.2f) / cache.humanScale;
                Assert.That(
                    KimodoRetargetCoreUtility.WriteMuscleSampleToMuscleClip(
                        new[]
                        {
                            KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, sourcePose),
                            KimodoRetargetHumanoidIkUtility.BuildMuscleSampleFromPose(cache, sourcePose)
                        },
                        clip,
                        out error),
                    Is.True,
                    error);

                Vector3 offsetPosition = new Vector3(-1f, 0f, 0.5f);
                Quaternion offsetRotation = Quaternion.Euler(0f, 35f, 0f);
                graph = PlayableGraph.Create("KimodoAnimationOffsetPlayableBaselineGraph");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                AnimationClipPlayable baselinePlayable = AnimationClipPlayable.Create(graph, clip);
                AnimationPlayableOutput baselineOutput = AnimationPlayableOutput.Create(
                    graph,
                    "KimodoAnimationOffsetPlayableBaselineOutput",
                    cache.animator);
                baselineOutput.SetSourcePlayable(baselinePlayable);
                graph.Play();
                graph.Evaluate(0f);
                Vector3 baselinePosition = profileRoot.position;
                Quaternion baselineRotation = profileRoot.rotation;
                graph.Destroy();

                graph = PlayableGraph.Create("KimodoAnimationOffsetPlayableTestGraph");
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                AnimationClipPlayable clipPlayable = AnimationClipPlayable.Create(graph, clip);
                Playable offsetPlayable = AnimationOffsetPlayableAccess.CreateAndConnect(
                    graph,
                    clipPlayable,
                    offsetPosition,
                    offsetRotation);
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(
                    graph,
                    "KimodoAnimationOffsetPlayableTestOutput",
                    cache.animator);
                output.SetSourcePlayable(offsetPlayable);
                graph.Play();
                graph.Evaluate(0f);
                graph.Evaluate(1f / KimodoPlayableClip.FIXED_FRAME_RATE);

                Vector3 expectedPosition = offsetPosition +
                    offsetRotation * new Vector3(0.3f, baselinePosition.y, -0.2f);
                Quaternion expectedRotation = offsetRotation;
                Vector3 doubleOffsetPosition = offsetPosition * 2f + Vector3.up * baselinePosition.y;
                Assert.That(
                    Vector3.Distance(profileRoot.position, expectedPosition),
                    Is.LessThan(1e-3f),
                    $"baseline={baselinePosition}, actual={profileRoot.position}, expected={expectedPosition}");
                Assert.That(Quaternion.Angle(profileRoot.rotation, expectedRotation), Is.LessThan(1e-3f));
                Assert.That(Vector3.Distance(profileRoot.position, doubleOffsetPosition), Is.GreaterThan(0.1f));
            }
            finally
            {
                if (graph.IsValid())
                {
                    graph.Destroy();
                }
                UnityEngine.Object.DestroyImmediate(clip);
                cache.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResetClipOffset_UsesIdentityAndConfiguresStartOffset(bool removeStartOffset)
        {
            var asset = ScriptableObject.CreateInstance<KimodoPlayableClip>();
            try
            {
                asset.position = new Vector3(1f, 2f, 3f);
                asset.rotation = Quaternion.Euler(10f, 20f, 30f);
                MethodInfo reset = typeof(KimodoPlayableClipGenerationHostService).GetMethod(
                    "ResetClipOffset",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(reset, Is.Not.Null);

                reset.Invoke(null, new object[] { asset, removeStartOffset });

                Assert.That(Vector3.Distance(asset.position, Vector3.zero), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(asset.rotation, Quaternion.identity), Is.LessThan(1e-4f));
                Assert.That(asset.removeStartOffset, Is.EqualTo(removeStartOffset));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void TimelineConstraintCache_UsesFixedHalfOpenFrameBuckets()
        {
            KimodoTimelineConstraintCacheRange first = KimodoTimelineConstraintClipCache.ResolveRange(
                timelineTime: 1.999,
                trackEndTime: 10.0,
                cacheTimeFrames: 60,
                frameRate: 30f);
            KimodoTimelineConstraintCacheRange second = KimodoTimelineConstraintClipCache.ResolveRange(
                timelineTime: 2.0,
                trackEndTime: 10.0,
                cacheTimeFrames: 60,
                frameRate: 30f);
            KimodoTimelineConstraintCacheRange last = KimodoTimelineConstraintClipCache.ResolveRange(
                timelineTime: 9.999,
                trackEndTime: 10.0,
                cacheTimeFrames: 60,
                frameRate: 30f);

            Assert.That(first.StartFrame, Is.EqualTo(0));
            Assert.That(first.EndFrame, Is.EqualTo(60));
            Assert.That(first.BakedStartFrame, Is.EqualTo(0));
            Assert.That(first.BakedEndFrame, Is.EqualTo(60));
            Assert.That(second.StartFrame, Is.EqualTo(60));
            Assert.That(second.EndFrame, Is.EqualTo(120));
            Assert.That(second.BakedStartFrame, Is.EqualTo(59));
            Assert.That(second.BakedEndFrame, Is.EqualTo(120));
            Assert.That(last.StartFrame, Is.EqualTo(240));
            Assert.That(last.EndFrame, Is.EqualTo(300));
            Assert.That(last.BakedStartFrame, Is.EqualTo(239));
            Assert.That(last.BakedEndFrame, Is.EqualTo(300));
        }

        [Test]
        public void TimelineConstraintSample_UsesTimelineFrameQuantization()
        {
            Assert.That(
                KimodoTimelineConstraintClipCache.ResolveTimelineSampleFrame(1.999, 30f),
                Is.EqualTo(59));
            Assert.That(
                KimodoTimelineConstraintClipCache.ResolveTimelineSampleTime(1.999, 30f),
                Is.EqualTo(59.0 / 30.0).Within(1e-9));
        }

        [Test]
        public void ConstraintMarkerTrackResolution_DoesNotRequireClipAtMarkerTime()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip clip = track.CreateClip<AnimationPlayableAsset>();
                clip.start = 0.0;
                clip.duration = 1.0;
                _ = track.end;
                typeof(TimelineClip).GetField(
                        "m_PostExtrapolationMode",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(clip, TimelineClip.ClipExtrapolation.None);
                KimodoFullBodyConstraintMarker marker = track.CreateMarker<KimodoFullBodyConstraintMarker>(2.0);

                Assert.That(KimodoConstraintMarkerEditorUtility.TryGetMarkerTrack(marker, out TrackAsset resolvedTrack), Is.True);
                Assert.That(resolvedTrack, Is.SameAs(track));
                Assert.That(KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out _), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ConstraintMarkerClipResolution_IncludesTimelineExtrapolation()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip clip = track.CreateClip<AnimationPlayableAsset>();
                clip.start = 0.0;
                clip.duration = 1.0;
                typeof(TimelineClip).GetField(
                        "m_PostExtrapolationMode",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.SetValue(clip, TimelineClip.ClipExtrapolation.Loop);
                typeof(TimelineClip).GetMethod(
                        "SetPostExtrapolationTime",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(clip, new object[] { 2.0 });
                KimodoFullBodyConstraintMarker marker = track.CreateMarker<KimodoFullBodyConstraintMarker>(2.0);

                Assert.That(KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out TimelineClip resolvedClip), Is.True);
                Assert.That(resolvedClip, Is.SameAs(clip));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void ConstraintMarkerClipResolution_UsesTimelineFramesAtSharedBoundary()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                timeline.editorSettings.frameRate = 60.0;
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip previous = track.CreateClip<AnimationPlayableAsset>();
                previous.start = 0.0;
                previous.duration = 5.900000063578288;
                TimelineClip next = track.CreateClip<AnimationPlayableAsset>();
                next.start = 5.900000063578288;
                next.duration = 1.0;
                KimodoFullBodyConstraintMarker marker = track.CreateMarker<KimodoFullBodyConstraintMarker>(5.9);

                Assert.That(KimodoConstraintMarkerEditorUtility.IsTimeInClipFrameRange(marker.time, previous), Is.False);
                Assert.That(KimodoConstraintMarkerEditorUtility.IsTimeInClipFrameRange(marker.time, next), Is.True);
                Assert.That(
                    KimodoConstraintMarkerEditorUtility.TryGetClipRangeForMarker(marker, out TimelineClip resolvedClip),
                    Is.True);
                Assert.That(resolvedClip, Is.SameAs(next));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineConstraintCache_ExtendsPastLastClipToMarkerTime()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip clip = track.CreateClip<AnimationPlayableAsset>();
                clip.start = 0.0;
                clip.duration = 1.0;
                var context = new KimodoTimelineInOutConstraintContext
                {
                    SourceClip = null,
                    Track = track
                };

                double endTime = KimodoTimelineConstraintClipCache.ResolveSamplingEndTime(
                    context,
                    timelineTime: 2.0,
                    frameRate: 30f);
                KimodoTimelineConstraintCacheRange range = KimodoTimelineConstraintClipCache.ResolveRange(
                    timelineTime: 2.0,
                    trackEndTime: endTime,
                    cacheTimeFrames: 60,
                    frameRate: 30f);

                Assert.That(endTime, Is.GreaterThan(2.0));
                Assert.That(range.StartFrame, Is.EqualTo(60));
                Assert.That(range.ResolveLocalSampleTime(2.0), Is.EqualTo(1f / 30f).Within(1e-5f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineConstraintCache_KeepsRangesSeparateAndClearDestroysAllClips()
        {
            KimodoTimelineConstraintCacheRange firstRange = KimodoTimelineConstraintClipCache.ResolveRange(
                timelineTime: 1.0,
                trackEndTime: 10.0,
                cacheTimeFrames: 60,
                frameRate: 30f);
            KimodoTimelineConstraintCacheRange secondRange = KimodoTimelineConstraintClipCache.ResolveRange(
                timelineTime: 5.0,
                trackEndTime: 10.0,
                cacheTimeFrames: 60,
                frameRate: 30f);
            var firstClip = new AnimationClip
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = "KimodoTimelineConstraintCacheTest_First"
            };
            var secondClip = new AnimationClip
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = "KimodoTimelineConstraintCacheTest_Second"
            };
            var firstKey = new KimodoTimelineConstraintCacheKey(1, 2, 3, firstRange, "model");
            var secondKey = new KimodoTimelineConstraintCacheKey(1, 2, 3, secondRange, "model");
            FieldInfo field = typeof(KimodoTimelineConstraintClipCache).GetField(
                "Entries",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null);
            var entries = field.GetValue(null) as System.Collections.IDictionary;
            Assert.That(entries, Is.Not.Null);
            entries.Add(firstKey, new KimodoTimelineConstraintCacheEntry { Clip = firstClip });
            entries.Add(secondKey, new KimodoTimelineConstraintCacheEntry { Clip = secondClip });
            Assert.That(entries.Count, Is.EqualTo(2));

            KimodoTimelineConstraintClipCache.Clear();

            Assert.That(firstClip == null, Is.True);
            Assert.That(secondClip == null, Is.True);
            Assert.That(entries.Count, Is.Zero);
        }

        [Test]
        public void TimelineConstraintCache_InvalidateDestroysOnlyRequestedRange()
        {
            KimodoTimelineConstraintCacheRange firstRange = KimodoTimelineConstraintClipCache.ResolveRange(1.0, 10.0, 60, 30f);
            KimodoTimelineConstraintCacheRange secondRange = KimodoTimelineConstraintClipCache.ResolveRange(5.0, 10.0, 60, 30f);
            var firstKey = new KimodoTimelineConstraintCacheKey(11, 12, 13, firstRange, "model");
            var secondKey = new KimodoTimelineConstraintCacheKey(11, 12, 13, secondRange, "model");
            var firstClip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
            var secondClip = new AnimationClip { hideFlags = HideFlags.HideAndDontSave };
            FieldInfo field = typeof(KimodoTimelineConstraintClipCache).GetField(
                "Entries",
                BindingFlags.NonPublic | BindingFlags.Static);
            var entries = field.GetValue(null) as System.Collections.IDictionary;
            entries.Add(firstKey, new KimodoTimelineConstraintCacheEntry { Clip = firstClip });
            entries.Add(secondKey, new KimodoTimelineConstraintCacheEntry { Clip = secondClip });

            Assert.That(KimodoTimelineConstraintClipCache.Invalidate(firstKey), Is.True);

            Assert.That(firstClip == null, Is.True);
            Assert.That(secondClip == null, Is.False);
            Assert.That(entries.Contains(secondKey), Is.True);
            KimodoTimelineConstraintClipCache.Clear();
        }

        [Test]
        public void ConstraintPoseRenderSignature_ChangesOnlyWhenRenderedContentChanges()
        {
            var sample = new TimelineInject.KimodoMarkerSampleResult
            {
                constraintType = "fullbody",
                sampleTime = 1.0,
                kimodoRootPosition = new Vector3(1f, 2f, 3f),
                unityRootPos = new Vector3(4f, 5f, 6f),
                unityRootRot = Quaternion.Euler(0f, 30f, 0f),
                jointNames = new System.Collections.Generic.List<string> { "Hips" },
                localAxisAngles = new System.Collections.Generic.List<Vector3> { new Vector3(0f, 0.2f, 0f) }
            };
            var item = new PoseCacheRenderItem
            {
                ConstraintType = "fullbody",
                SampleData = sample,
                HighlightJoints = new System.Collections.Generic.List<string> { "Hips" },
                Visible = true
            };

            int first = KimodoConstraintPoseCache.ComputeRenderSignature(item, "model");
            int same = KimodoConstraintPoseCache.ComputeRenderSignature(item, "model");
            sample.localAxisAngles[0] = new Vector3(0f, 0.3f, 0f);
            int changed = KimodoConstraintPoseCache.ComputeRenderSignature(item, "model");

            Assert.That(same, Is.EqualTo(first));
            Assert.That(changed, Is.Not.EqualTo(first));
        }

        [Test]
        public void ConstraintPoseCache_RecognizesClipRemovedFromOriginalTrack()
        {
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject previewRoot = null;
            try
            {
                KimodoConstraintPoseCache.DestroyAll();
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "Motion");
                TimelineClip timelineClip = track.CreateClip<KimodoPlayableClip>();
                int clipId = ((UnityEngine.Object)timelineClip.asset).GetInstanceID();
                int trackId = track.GetInstanceID();

                Assert.That(KimodoConstraintPoseCache.IsClipStillOnTrack(clipId, trackId), Is.True);

                Type entryType = typeof(KimodoConstraintPoseCache).GetNestedType(
                    "PoseCacheEntry",
                    BindingFlags.NonPublic);
                object entry = Activator.CreateInstance(entryType, nonPublic: true);
                previewRoot = new GameObject("KimodoDeletedClipPreviewTest");
                entryType.GetField("Key").SetValue(entry, "deleted-clip-test");
                entryType.GetField("ContextKey").SetValue(entry, "deleted-clip-context");
                entryType.GetField("ClipId").SetValue(entry, clipId);
                entryType.GetField("TrackId").SetValue(entry, trackId);
                entryType.GetField("Root").SetValue(entry, previewRoot.transform);
                FieldInfo entriesField = typeof(KimodoConstraintPoseCache).GetField(
                    "Entries",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var entries = entriesField.GetValue(null) as System.Collections.IDictionary;
                entries.Add("deleted-clip-test", entry);

                Assert.That(timeline.DeleteClip(timelineClip), Is.True);
                Assert.That(KimodoConstraintPoseCache.IsClipStillOnTrack(clipId, trackId), Is.False);

                typeof(KimodoConstraintPoseCache).GetMethod(
                        "DestroyInvalidContexts",
                        BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, null);

                Assert.That(entries.Count, Is.EqualTo(0));
                Assert.That(previewRoot == null, Is.True);
            }
            finally
            {
                KimodoConstraintPoseCache.DestroyAll();
                if (previewRoot != null)
                {
                    UnityEngine.Object.DestroyImmediate(previewRoot);
                }
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void TimelineClipOffset_IsAnchorRelativeToTrackOffset()
        {
            Vector3 basePosition = new Vector3(4f, 0f, -2f);
            Quaternion baseRotation = Quaternion.Euler(0f, 30f, 0f);
            Vector3 expectedClipPosition = new Vector3(1f, 0f, 3f);
            Quaternion expectedClipRotation = Quaternion.Euler(0f, 20f, 0f);
            Vector3 anchorPosition = basePosition + baseRotation * expectedClipPosition;
            Quaternion anchorRotation = baseRotation * expectedClipRotation;

            KimodoPlayableClipGenerationHostService.ResolveClipOffsetForAnchor(
                basePosition,
                baseRotation,
                anchorPosition,
                anchorRotation,
                planarOnly: true,
                out Vector3 clipPosition,
                out Quaternion clipRotation);

            Vector3 resolvedPosition = basePosition + baseRotation * clipPosition;
            Quaternion resolvedRotation = baseRotation * clipRotation;
            Assert.That(Vector3.Distance(resolvedPosition, anchorPosition), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(resolvedRotation, anchorRotation), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(clipPosition, expectedClipPosition), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(clipRotation, expectedClipRotation), Is.LessThan(1e-4f));
        }

        [Test]
        public void TimelineClipOffset_IsIdentityWhenTrackOffsetIsTheAnchor()
        {
            Vector3 basePosition = new Vector3(4f, 0f, -2f);
            Quaternion baseRotation = Quaternion.Euler(0f, 30f, 0f);

            KimodoPlayableClipGenerationHostService.ResolveClipOffsetForAnchor(
                basePosition,
                baseRotation,
                basePosition,
                baseRotation,
                planarOnly: true,
                out Vector3 clipPosition,
                out Quaternion clipRotation);

            Assert.That(Vector3.Distance(clipPosition, Vector3.zero), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(clipRotation, Quaternion.identity), Is.LessThan(1e-4f));
        }

        [Test]
        public void WorldConstraint_RoundTripsThroughKimodoAndTrackSpaces()
        {
            Vector3 trackPosition = new Vector3(-1f, 0f, 2f);
            Quaternion trackRotation = Quaternion.Euler(0f, 35f, 0f);
            Vector3 secondKimodoPosition = new Vector3(2f, 1f, 0.5f);
            Quaternion secondKimodoRotation = Quaternion.Euler(0f, 20f, 0f);
            Vector3 secondWorldPosition = trackPosition + trackRotation * secondKimodoPosition;
            Quaternion secondWorldRotation = trackRotation * secondKimodoRotation;
            var samples = new System.Collections.Generic.List<TimelineInject.KimodoMarkerSampleResult>
            {
                new TimelineInject.KimodoMarkerSampleResult
                {
                    constraintType = "fullbody",
                    sampleTime = 0.0,
                    kimodoRootPosition = trackPosition + Vector3.up,
                    unityRootPos = trackPosition,
                    unityRootRot = trackRotation,
                    hasRootHeading = true,
                    rootHeading = new Vector2(
                        (trackRotation * Vector3.forward).x,
                        (trackRotation * Vector3.forward).z),
                    localAxisAngles = new System.Collections.Generic.List<Vector3>
                    {
                        KimodoRuntimeUtility.QuaternionToAxisAngleVector(trackRotation)
                    }
                },
                new TimelineInject.KimodoMarkerSampleResult
                {
                    constraintType = "fullbody",
                    sampleTime = 1.0,
                    kimodoRootPosition = secondWorldPosition,
                    unityRootPos = secondWorldPosition,
                    unityRootRot = secondWorldRotation,
                    hasRootHeading = true,
                    rootHeading = new Vector2(
                        (secondWorldRotation * Vector3.forward).x,
                        (secondWorldRotation * Vector3.forward).z),
                    localAxisAngles = new System.Collections.Generic.List<Vector3>
                    {
                        KimodoRuntimeUtility.QuaternionToAxisAngleVector(secondWorldRotation)
                    }
                }
            };

            KimodoConstraintNormalizationUtility.NormalizeConstraintOrigin(
                samples,
                out KimodoConstraintNormalizationInfo normalization,
                out string warning);

            Assert.That(warning, Is.Empty);
            Assert.That(normalization.Applied, Is.True);
            Assert.That(Vector3.Distance(normalization.AnchorSample.unityRootPos, trackPosition), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(samples[0].kimodoRootPosition, Vector3.up), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(samples[1].kimodoRootPosition, secondKimodoPosition), Is.LessThan(1e-5f));

            KimodoPlayableClipGenerationHostService.ResolveClipOffsetForAnchor(
                trackPosition,
                trackRotation,
                normalization.AnchorSample.unityRootPos,
                normalization.AnchorSample.unityRootRot,
                planarOnly: true,
                out Vector3 clipPosition,
                out Quaternion clipRotation);

            Assert.That(clipPosition, Is.EqualTo(Vector3.zero));
            Assert.That(Quaternion.Angle(clipRotation, Quaternion.identity), Is.LessThan(1e-4f));
            Assert.That(
                Vector3.Distance(
                    trackPosition + trackRotation * (clipPosition + secondKimodoPosition),
                    secondWorldPosition),
                Is.LessThan(1e-5f));
        }
    }
}
