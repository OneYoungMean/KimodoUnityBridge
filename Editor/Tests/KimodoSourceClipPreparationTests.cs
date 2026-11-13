using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using KimodoBridge;
using KimodoBridge.Editor;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class KimodoSourceClipPreparationTests
    {
        private string folder;
        private string modelPath;
        private GameObject sourceRoot;
        private bool previousPersistence;
        private string clipName;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/ClipPreparation_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            modelPath = folder + "/Source.fbx";
            Assert.That(AssetDatabase.CopyAsset(
                "Packages/com.unity.kimodo_unity_motion_tools/Editor/Model/Armature.fbx", modelPath), Is.True);
            var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
            importer.animationType = ModelImporterAnimationType.Generic;
            var take = importer.defaultClipAnimations.First();
            clipName = "GenericSource_" + Guid.NewGuid().ToString("N");
            take.name = clipName;
            take.firstFrame = 300;
            take.lastFrame = 330;
            importer.clipAnimations = new[] { take };
            importer.SaveAndReimport();
            sourceRoot = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modelPath));
            sourceRoot.name = "SourceFixture_" + Guid.NewGuid().ToString("N");
            sourceRoot.GetComponent<Animator>().avatar = null;
            previousPersistence = KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips;
            KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips = false;
        }

        [TearDown]
        public void TearDown()
        {
            KimodoPlayableClipGenerationSettings.instance.WriteResampledTimelineCacheClips = previousPersistence;
            string modelGuid = AssetDatabase.AssetPathToGUID(modelPath);
            if (AssetDatabase.IsValidFolder("Assets/KimodoGeneratedClips"))
            {
                foreach (string path in AssetDatabase.FindAssets("t:Avatar", new[] { "Assets/KimodoGeneratedClips" })
                             .Select(AssetDatabase.GUIDToAssetPath).Where(path =>
                                 Path.GetFileName(path).StartsWith("Source_" + modelGuid + "_", StringComparison.Ordinal) ||
                                 (sourceRoot != null && Path.GetFileName(path).StartsWith(sourceRoot.name + "_", StringComparison.Ordinal))))
                    Archive(path);
                foreach (string path in AssetDatabase.FindAssets("t:AnimationClip", new[] { "Assets/KimodoGeneratedClips" })
                             .Select(AssetDatabase.GUIDToAssetPath).Where(path => Path.GetFileName(path).StartsWith(clipName, StringComparison.Ordinal)))
                    Archive(path);
            }
            if (sourceRoot != null) Object.DestroyImmediate(sourceRoot);
            Archive(folder);
            AssetDatabase.Refresh();
        }

        [Test]
        public void OriginalEncoding_UsesImportedGenericSourceRigInsteadOfTargetAvatar()
        {
            AnimationClip source = LoadSource();
            string originalImporter = EditorJsonUtility.ToJson(AssetImporter.GetAtPath(modelPath));
            Assert.That(source.isHumanMotion, Is.False);
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                KimodoMotionModelProfiles.DefaultModelName, out Avatar target, out string error), Is.True, error);
            double[] times = { 0.0, 0.25, 0.5, 0.75 };
            byte[] actual = KimodoClipConstraintEncoder.EncodeOriginalAnimationAtTimes(
                source, target, KimodoMotionModelProfiles.DefaultModelName, times, 30, CancellationToken.None);

            var resolved = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(sourceRoot);
            Assert.That(resolved.IsHumanoid, Is.True, resolved.Error);
            var detached = Object.Instantiate(LoadSource());
            try
            {
                byte[] expected = KimodoClipConstraintEncoder.EncodeOriginalAnimationAtTimes(
                    detached, resolved.Avatar, KimodoMotionModelProfiles.DefaultModelName, times, 30, CancellationToken.None);
                AssertMotionEqual(expected, actual);
            }
            finally { Object.DestroyImmediate(detached); }
            Assert.That(((ModelImporter)AssetImporter.GetAtPath(modelPath)).animationType,
                Is.EqualTo(ModelImporterAnimationType.Generic));
            Assert.That(LoadSource().isHumanMotion, Is.False);
            Assert.That(EditorJsonUtility.ToJson(AssetImporter.GetAtPath(modelPath)), Is.EqualTo(originalImporter),
                "Automatic Avatar generation must restore all model import settings.");
        }

        [Test]
        public void CommandReferences_PrepareOutsideInsideAndReuseDerivedClip()
        {
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                KimodoMotionModelProfiles.DefaultModelName, out Avatar target, out string error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(target, "ClipPreparationTarget", out var rig, out error), Is.True, error);
            var directorObject = new GameObject("ClipPreparationDirector");
            var director = directorObject.AddComponent<PlayableDirector>();
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var track = timeline.CreateTrack<AnimationTrack>(null, "Target");
            director.playableAsset = timeline;
            director.SetGenericBinding(track, rig.animator);
            var character = new command_context.TimelineCharacterRecord("test", rig.root, rig.animator, target, track, null, "");
            var session = new command_context.TimelineSessionRecord(Guid.NewGuid(), "SourcePreparation", director, timeline, "", directorObject);
            session.Characters.Add(character);
            var trace = Activator.CreateInstance(
                typeof(command_context).GetNestedType("TimelineGenerationTrace", BindingFlags.NonPublic),
                new object[] { session, character, 2.0, 1.0 });
            TimelineClip output = track.CreateClip<KimodoPlayableClip>();
            output.start = 2; output.duration = 1;
            var playable = (KimodoPlayableClip)output.asset;
            try
            {
                var configureOutside = typeof(command_context).GetMethod("ConfigureCommandInOutClip", BindingFlags.NonPublic | BindingFlags.Static);
                var args = JObject.Parse(@"{'constraints':[{'inout':{'in':{'source':{},'window_frames':12,'sample_count':3}}}]}");
                args["constraints"][0]["inout"]["in"]["source"]["clip"] = modelPath;
                configureOutside.Invoke(null, new object[] { args, trace, playable, KimodoMotionModelProfiles.DefaultModelName, 30, 30f, false, new System.Collections.Generic.List<KimodoMarkerSampleResult>() });
                AnimationClip outside = playable.inConstraintSourceClip;
                Assert.That(outside, Is.Not.SameAs(LoadSource()));
                Assert.That(EditorUtility.IsPersistent(outside), Is.True, "Command references must survive a domain reload.");
                Assert.That(outside.length, Is.EqualTo(LoadSource().length).Within(1.0 / 30));
                string outsidePath = AssetDatabase.GetAssetPath(outside);
                AssetDatabase.ImportAsset(outsidePath, ImportAssetOptions.ForceUpdate);
                outside = AssetDatabase.LoadAssetAtPath<AnimationClip>(outsidePath);
                Assert.That(outside.length, Is.EqualTo(LoadSource().length).Within(1.0 / 30),
                    "The persisted derivative must contain the baked curves after reimport.");
                var configureInside = typeof(command_context).GetMethod("ConfigureCommandInsideClip", BindingFlags.NonPublic | BindingFlags.Static);
                args = JObject.Parse(@"{'constraints':[{'inside':{'source':{}}}]}");
                args["constraints"][0]["inside"]["source"]["clip"] = GlobalObjectId.GetGlobalObjectIdSlow(LoadSource()).ToString();
                configureInside.Invoke(null, new object[] { args, trace, playable });
                Assert.That(playable.clip, Is.SameAs(outside), "Inside and outside should reuse the same prepared source.");
                Assert.That(LoadSource().isHumanMotion, Is.False);
            }
            finally
            {
                director.Stop();
                foreach (TimelineClip clip in track.GetClips()) Object.DestroyImmediate(clip.asset);
                Object.DestroyImmediate(track); Object.DestroyImmediate(timeline); Object.DestroyImmediate(directorObject);
                rig.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HistorySampling_PreparesGenericClipAndRestoresOriginalTimeline(bool genericBinding)
        {
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                KimodoMotionModelProfiles.DefaultModelName, out Avatar target, out string error), Is.True, error);
            Assert.That(KimodoRetargetAvatarUtility.TryBuildRetargetSkeleton(target, "HistoryPreparationTarget", out var rig, out error), Is.True, error);
            var directorObject = new GameObject("HistoryPreparationDirector");
            var director = directorObject.AddComponent<PlayableDirector>();
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var track = timeline.CreateTrack<AnimationTrack>(null, "Target");
            track.trackOffset = TrackOffset.ApplyTransformOffsets;
            track.position = new Vector3(2, 0.25f, -3);
            director.playableAsset = timeline;
            Animator boundAnimator = genericBinding ? sourceRoot.GetComponent<Animator>() : rig.animator;
            director.SetGenericBinding(track, boundAnimator);
            TimelineClip clip = track.CreateClip<AnimationPlayableAsset>();
            var asset = (AnimationPlayableAsset)clip.asset;
            asset.clip = LoadSource(); asset.removeStartOffset = false;
            clip.start = 0; clip.duration = 0.6; clip.clipIn = 0.1; clip.timeScale = 1.2;
            TimelineClip output = track.CreateClip<KimodoPlayableClip>();
            output.start = 0.6; output.duration = 1;
            var unrelated = new AnimationClip();
            unrelated.SetCurve("OtherRig/Hips", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
            TimelineClip future = track.CreateClip<AnimationPlayableAsset>();
            ((AnimationPlayableAsset)future.asset).clip = unrelated;
            future.start = 10; future.duration = 1;
            AnimationClip prepared = null;
            try
            {
                Assert.That(KimodoMotionModelProfiles.TryGetArdy(KimodoMotionModelProfiles.ArdySomaModelName, out var profile), Is.True);
                Assert.That(KimodoInOutConstraintAdapter.TryResolveTimelineContext(output, out var context, out error), Is.True, error);
                var history = new ArdyEditorHistorySource { TimelineContext = context, RangeStartSeconds = 0, RangeEndSeconds = 0.6 };
                Assert.That(ArdyEditorHistoryEncoder.TryEncode(history, profile, out byte[] actual, out error), Is.True, error);
                Assert.That(asset.clip, Is.SameAs(LoadSource()), "History sampling must restore the original Clip reference.");
                Assert.That(clip.clipIn, Is.EqualTo(0.1)); Assert.That(clip.timeScale, Is.EqualTo(1.2));
                Assert.That(track.position, Is.EqualTo(new Vector3(2, 0.25f, -3)));
                Avatar bindingAvatar = KimodoLocalAvatarUtility.ResolveTimelineSourceAvatar(track, boundAnimator).Avatar;
                Assert.That(KimodoSourceClipPreparationUtility.TryPrepareForAvatar(LoadSource(), bindingAvatar, out prepared, out error), Is.True, error);
                asset.clip = prepared;
                Assert.That(ArdyEditorHistoryEncoder.TryEncode(history, profile, out byte[] expected, out error), Is.True, error);
                AssertMotionEqual(expected, actual);
            }
            finally
            {
                director.Stop();
                Object.DestroyImmediate(future.asset); Object.DestroyImmediate(unrelated); Object.DestroyImmediate(output.asset); Object.DestroyImmediate(asset); Object.DestroyImmediate(track); Object.DestroyImmediate(timeline); Object.DestroyImmediate(directorObject);
                if (prepared != null && !EditorUtility.IsPersistent(prepared)) Object.DestroyImmediate(prepared);
                rig.Dispose();
            }
        }

        [Test]
        public void UnknownStandaloneBoneClip_IsRejectedInsteadOfSamplingRestPose()
        {
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                KimodoMotionModelProfiles.DefaultModelName, out Avatar target, out string error), Is.True, error);
            var clip = new AnimationClip();
            try
            {
                clip.SetCurve("UnknownRig/Pelvis", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
                Assert.That(KimodoSourceClipPreparationUtility.TryPrepareForAvatar(clip, target, out _, out error), Is.False);
                Assert.That(error, Does.Contain("source rig").And.Contain("UnknownRig/Pelvis"));
            }
            finally { Object.DestroyImmediate(clip); }
        }

        [Test]
        public void CacheIdentity_DistinguishesSameNamesAndChangedContent()
        {
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                KimodoMotionModelProfiles.DefaultModelName, out Avatar target, out string error), Is.True, error);
            var a = new AnimationClip { name = "SameName" };
            var b = new AnimationClip { name = "SameName" };
            try
            {
                a.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
                b.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 2));
                string original = KimodoRetargetEditorCacheUtility.BuildNamedCacheName(a, "bone", target, target);
                Assert.That(KimodoRetargetEditorCacheUtility.BuildNamedCacheName(b, "bone", target, target), Is.Not.EqualTo(original));
                a.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 3));
                Assert.That(KimodoRetargetEditorCacheUtility.BuildNamedCacheName(a, "bone", target, target), Is.Not.EqualTo(original));
                Assert.That(KimodoRetargetEditorCacheUtility.BuildNamedCacheName(b, "bone", target, null),
                    Is.Not.EqualTo(KimodoRetargetEditorCacheUtility.BuildNamedCacheName(b, "bone", target, target)));
            }
            finally { Object.DestroyImmediate(a); Object.DestroyImmediate(b); }
        }

        private AnimationClip LoadSource() => AssetDatabase.LoadAllAssetsAtPath(modelPath)
            .OfType<AnimationClip>().First(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal));

        private static void AssertMotionEqual(byte[] expectedBytes, byte[] actualBytes)
        {
            Assert.That(KimodoRawMotionUtility.TryParseFlatBuffer(expectedBytes, out var expected, out string error), Is.True, error);
            Assert.That(KimodoRawMotionUtility.TryParseFlatBuffer(actualBytes, out var actual, out error), Is.True, error);
            Assert.That(actual.FrameCount, Is.EqualTo(expected.FrameCount));
            float maxError = 0;
            for (int frame = 0; frame < expected.FrameCount; frame++)
            {
                Assert.That(Vector3.Distance(expected.rootPositions[frame], actual.rootPositions[frame]), Is.LessThan(0.001f));
                for (int joint = 0; joint < expected.JointCount; joint++)
                {
                    Assert.That(expected.TryReadUnityLocalRotation(frame, joint, expected.JointCount, out var expectedRotation), Is.True);
                    Assert.That(actual.TryReadUnityLocalRotation(frame, joint, actual.JointCount, out var actualRotation), Is.True);
                    maxError = Mathf.Max(maxError, Quaternion.Angle(expectedRotation, actualRotation));
                }
            }
            Assert.That(maxError, Is.LessThan(0.1f), "Automatic source preparation must match an explicit source Avatar.");
        }

        private static void Archive(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string destination = Path.Combine("archive", "ClipPreparationTests", Guid.NewGuid().ToString("N"), Path.GetFileName(path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (Directory.Exists(path)) Directory.Move(path, destination);
            else if (File.Exists(path)) File.Move(path, destination);
            if (File.Exists(path + ".meta")) File.Move(path + ".meta", destination + ".meta");
        }
    }
}
