using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
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
        public void SingleMuscleSample_CanRunThroughHumanoidFootIkPlayable()
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
            }
            finally
            {
                cache.Dispose();
            }
        }

        [Test]
        public void TrackOffset_ResolvesAndAppliesWorldPose()
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
                    out Quaternion offsetRotation);

                Vector3 originalPosition = new Vector3(1f, 2f, -3f);
                Quaternion originalRotation = Quaternion.Euler(5f, 17f, -3f);
                Vector3 transformedPosition = originalPosition;
                Quaternion transformedRotation = originalRotation;
                KimodoTimelineTrackOffsetUtility.ApplyToRootPose(
                    offsetPosition,
                    offsetRotation,
                    ref transformedPosition,
                    ref transformedRotation);
                Assert.That(
                    Vector3.Distance(transformedPosition, offsetPosition + offsetRotation * originalPosition),
                    Is.LessThan(1e-5f));
                Assert.That(
                    Quaternion.Angle(transformedRotation, offsetRotation * originalRotation),
                    Is.LessThan(1e-4f));
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
                Vector3 expectedPosition = new Vector3(2f, 3f, 4f);
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
                    out Quaternion offsetRotation);

                Assert.That(Vector3.Distance(offsetPosition, expectedPosition), Is.LessThan(1e-5f));
                Assert.That(Quaternion.Angle(offsetRotation, Quaternion.Euler(expectedEuler)), Is.LessThan(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(track);
                UnityEngine.Object.DestroyImmediate(character);
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
        public void TimelineConstraintCache_RestoresInterpolatedSourceHipsWorldPose()
        {
            var positions = new[]
            {
                new Vector3(2f, 1f, 4f),
                new Vector3(6f, 3f, 8f)
            };
            var rotations = new[]
            {
                Quaternion.Euler(0f, 30f, 0f),
                Quaternion.Euler(0f, 90f, 0f)
            };

            Assert.That(
                KimodoTimelineConstraintClipCache.TryInterpolateSourceHipsPose(
                    positions,
                    rotations,
                    0.5f,
                    out Vector3 sourcePosition,
                    out Quaternion sourceRotation),
                Is.True);

            var sample = new TimelineInject.KimodoMarkerSampleResult
            {
                kimodoRootPosition = new Vector3(0f, 1.25f, 0f)
            };
            KimodoTimelinePoseSampler.ApplySourceHipsPose(
                sample,
                sourcePosition,
                sourceRotation,
                exportedSampleTime: 2.5);

            Assert.That(Vector3.Distance(sourcePosition, new Vector3(4f, 2f, 6f)), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(sample.kimodoRootPosition, new Vector3(4f, 1.25f, 6f)), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(sample.unityRootPos, sourcePosition), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(sample.unityRootRot, Quaternion.Euler(0f, 60f, 0f)), Is.LessThan(1e-3f));
            Assert.That(sample.sampleTime, Is.EqualTo(2.5));
        }

        [Test]
        public void ApplySourceHipsPose_PreservesRootToHipsRelativeRotation()
        {
            Quaternion sampledRoot = new Quaternion(0f, 0.575372f, 0f, 0.817892f).normalized;
            Quaternion sampledHips = new Quaternion(-0.065226f, 0.563530f, -0.092168f, 0.818342f).normalized;
            Quaternion sourceHips = new Quaternion(-0.062781f, 0.866679f, -0.112737f, 0.481888f).normalized;
            var sample = new TimelineInject.KimodoMarkerSampleResult
            {
                unityRootRot = sampledRoot,
                localAxisAngles = new System.Collections.Generic.List<Vector3>
                {
                    KimodoRuntimeUtility.QuaternionToAxisAngleVector(sampledHips)
                }
            };

            KimodoTimelinePoseSampler.ApplySourceHipsPose(
                sample,
                Vector3.zero,
                sourceHips,
                exportedSampleTime: 3.0);

            Vector3 restoredAxisAngle = sample.localAxisAngles[0];
            Quaternion restoredHips = Quaternion.AngleAxis(
                restoredAxisAngle.magnitude * Mathf.Rad2Deg,
                restoredAxisAngle.normalized);
            Quaternion relativeBefore = Quaternion.Inverse(sampledRoot) * sampledHips;
            Quaternion relativeAfter = Quaternion.Inverse(sample.unityRootRot) * restoredHips;
            Vector3 sourceForward = Vector3.ProjectOnPlane(sourceHips * Vector3.forward, Vector3.up).normalized;
            Quaternion expectedRoot = Quaternion.LookRotation(sourceForward, Vector3.up);

            Assert.That(Quaternion.Angle(relativeBefore, relativeAfter), Is.LessThan(1e-3f));
            Assert.That(Quaternion.Angle(sample.unityRootRot, expectedRoot), Is.LessThan(1e-3f));
            Assert.That(Quaternion.Angle(sampledHips, restoredHips), Is.GreaterThan(50f));
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
    }
}
