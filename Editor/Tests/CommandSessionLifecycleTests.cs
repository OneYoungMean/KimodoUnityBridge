using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using KimodoBridge;
using KimodoBridge.Editor;
using KimodoUnityBridge.Command;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class CommandSessionLifecycleTests
    {
        private const string HumanoidModelPath = "Packages/com.unity.kimodo_unity_motion_tools/Editor/Model/Armature.fbx";

        [Test]
        public void ResolveSessionCharacterByReference_AddsObjectReferenceAndRejectsAmbiguousOrInactiveSceneTargets()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            Scene activeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Scene inactiveScene = default;
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            string fixtureId = Guid.NewGuid().ToString("N");
            string fixtureFolder = "Assets/Archive/KimodoCommandTests/Fixtures";
            string activeScenePath = $"{fixtureFolder}/RoleResolution_Active_{fixtureId}.unity";
            string inactiveScenePath = $"{fixtureFolder}/RoleResolution_Inactive_{fixtureId}.unity";
            string activeCharacterName = "RoleResolution_" + fixtureId;
            try
            {
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "Archive", "KimodoCommandTests", "Fixtures"));
                GameObject sceneAnchor = new GameObject("RoleResolutionSceneAnchor");
                SceneManager.MoveGameObjectToScene(sceneAnchor, activeScene);
                Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(HumanoidModelPath).OfType<Avatar>()
                    .FirstOrDefault(item => item.isValid && item.isHuman);
                Assert.That(avatar, Is.Not.Null, "Missing Humanoid Armature fixture Avatar.");
                GameObject character = CreateHumanoidCharacter(activeCharacterName, avatar);
                GameObject ambiguousA = CreateHumanoidCharacter("RoleResolution_Ambiguous", avatar);
                GameObject ambiguousB = CreateHumanoidCharacter("RoleResolution_Ambiguous", avatar);
                Assert.That(EditorSceneManager.SaveScene(activeScene, activeScenePath), Is.True,
                    "Could not save the active scene needed for stable GlobalObjectId resolution.");

                Type context = typeof(command_context);
                Type sessionType = context.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
                object session = CreateSessionRecord(sessionType, timeline, null, null);
                MethodInfo resolve = context.GetMethod("ResolveSessionCharacterByReference", BindingFlags.Static | BindingFlags.NonPublic);
                string characterId = GlobalObjectId.GetGlobalObjectIdSlow(character).ToString();
                object byId = resolve.Invoke(null, new object[] { session, characterId, true });
                Assert.That(byId.GetType().GetProperty("Root").GetValue(byId), Is.SameAs(character));
                object byName = resolve.Invoke(null, new object[] { session, activeCharacterName, true });
                Assert.That(byName, Is.SameAs(byId), "Name and object_ref should reuse the same character record.");
                Assert.That(((System.Collections.ICollection)sessionType.GetProperty("Characters").GetValue(session)).Count, Is.EqualTo(1));
                Assert.That(timeline.GetRootTracks().Count(), Is.EqualTo(1));

                TargetInvocationException ambiguous = Assert.Throws<TargetInvocationException>(() =>
                    resolve.Invoke(null, new object[] { session, "RoleResolution_Ambiguous", true }));
                Assert.That(ambiguous.InnerException, Is.TypeOf<InvalidOperationException>());
                Assert.That(ambiguous.InnerException.Message, Does.Contain("ambiguous in the active scene"));
                Assert.That(ambiguous.InnerException.Message, Does.Contain("object_ref"));
                Assert.That(((System.Collections.ICollection)sessionType.GetProperty("Characters").GetValue(session)).Count, Is.EqualTo(1));
                Assert.That(timeline.GetRootTracks().Count(), Is.EqualTo(1), "Ambiguous resolution must not add tracks.");

                inactiveScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                GameObject inactiveCharacter = new GameObject("RoleResolution_InactiveCharacter_" + fixtureId);
                inactiveCharacter.AddComponent<Animator>();
                SceneManager.MoveGameObjectToScene(inactiveCharacter, inactiveScene);
                Assert.That(EditorSceneManager.SaveScene(inactiveScene, inactiveScenePath), Is.True,
                    "Could not save the additive scene needed for stable GlobalObjectId resolution.");
                Assert.That(SceneManager.SetActiveScene(activeScene), Is.True);

                MethodInfo find = context.GetMethod("ResolveRequestedSceneCharacters", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That((GameObject[])find.Invoke(null, new object[] { inactiveCharacter.name }), Is.Empty,
                    "Name lookup must stay within the active scene.");
                string inactiveId = GlobalObjectId.GetGlobalObjectIdSlow(inactiveCharacter).ToString();
                TargetInvocationException inactive = Assert.Throws<TargetInvocationException>(() =>
                    resolve.Invoke(null, new object[] { session, inactiveId, true }));
                Assert.That(inactive.InnerException.Message, Does.Contain("in the active scene"));
                Assert.That(((System.Collections.ICollection)sessionType.GetProperty("Characters").GetValue(session)).Count, Is.EqualTo(1));
                Assert.That(timeline.GetRootTracks().Count(), Is.EqualTo(1));
            }
            finally
            {
                if (inactiveScene.IsValid() && inactiveScene.isLoaded)
                {
                    if (activeScene.IsValid() && activeScene.isLoaded) SceneManager.SetActiveScene(activeScene);
                    EditorSceneManager.CloseScene(inactiveScene, true);
                }
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                {
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                UnityEngine.Object.DestroyImmediate(timeline);
                ArchiveFixtureScene(activeScenePath, fixtureId + "_active");
                ArchiveFixtureScene(inactiveScenePath, fixtureId + "_inactive");
                AssetDatabase.Refresh();
            }
        }

        [TestCase("mesh")]
        [TestCase("missing_avatar")]
        [TestCase("generic_avatar")]
        [TestCase("mismatched_skeleton")]
        public void SceneCharacterIndex_SkipsObjectsWhoseAvatarCannotBeResolvedOrGenerated(string kind)
        {
            var root = new GameObject("CharacterIndex_" + Guid.NewGuid().ToString("N"));
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            Avatar genericAvatar = null;
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                root.AddComponent<MeshRenderer>();
                Animator animator = kind == "mesh" ? null : root.AddComponent<Animator>();
                if (kind == "generic_avatar")
                    animator.avatar = genericAvatar = AvatarBuilder.BuildGenericAvatar(root, string.Empty);
                if (kind == "mismatched_skeleton")
                {
                    Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                        "smplx", out Avatar humanoid, out string error), Is.True, error);
                    animator.avatar = humanoid;
                }

                var resolved = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
                Assert.That(resolved.IsHumanoid, Is.False);
                Assert.That(resolved.Avatar, Is.Null);
                Assert.That(resolved.Error, Does.Contain("Cannot resolve ModelImporter"));

                Type context = typeof(command_context);
                MethodInfo find = context.GetMethod("ResolveRequestedSceneCharacters", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That((GameObject[])find.Invoke(null, new object[] { root.name }), Is.Empty);
                Selection.activeGameObject = root;
                Assert.That((GameObject[])find.Invoke(null, new object[] { "@active_animator" }), Is.Empty);

                Type sessionType = context.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
                object session = CreateSessionRecord(sessionType, timeline, null, null);
                MethodInfo add = context.GetMethod("AddCharacterTrack", BindingFlags.Static | BindingFlags.NonPublic);
                object[] arguments = { session, root, animator, true, null, true };
                Assert.That((bool)add.Invoke(null, arguments), Is.False);
                Assert.That(arguments[4] as string, Does.StartWith("avatar_required:"));
                Assert.That(timeline.GetRootTracks(), Is.Empty, "Rejected objects must not create Timeline tracks.");

                var legacy = new command_context.TimelineCharacterRecord("legacy", root, animator,
                    animator != null ? animator.avatar : null, null, null, string.Empty);
                MethodInfo indexed = context.GetMethod("IsIndexableSessionCharacter", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That((bool)indexed.Invoke(null, new object[] { legacy }), Is.False);
            }
            finally
            {
                Selection.activeObject = previousSelection;
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(timeline);
                if (genericAvatar != null) UnityEngine.Object.DestroyImmediate(genericAvatar);
            }
        }

        [Test]
        public void SceneCharacterIndex_UsesExistingHumanoidAvatarAndSkipsSelectedProp()
        {
            Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryLoadAvatarByModelName(
                "smplx", out Avatar avatar, out string error), Is.True, error);
            var root = new GameObject("CharacterIndex_" + Guid.NewGuid().ToString("N"));
            var prop = new GameObject("CharacterIndexProp");
            UnityEngine.Object previousSelection = Selection.activeObject;
            try
            {
                prop.AddComponent<Animator>();
                Assert.That(KimodoRuntimeAvatarSkeletonBuilder.TryBuildHierarchyFromAvatarSkeleton(
                    avatar, root.transform, out error), Is.True, error);
                Animator animator = root.AddComponent<Animator>();
                animator.avatar = avatar;
                var resolved = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
                Assert.That(resolved.Avatar, Is.SameAs(avatar));
                Assert.That(resolved.Source, Is.EqualTo("Animator"));
                Assert.That(resolved.Error, Is.Empty);
                Type context = typeof(command_context);
                MethodInfo find = context.GetMethod("ResolveRequestedSceneCharacters", BindingFlags.Static | BindingFlags.NonPublic);
                CollectionAssert.AreEqual(new[] { root },
                    (GameObject[])find.Invoke(null, new object[] { root.name }));

                Selection.activeGameObject = root;
                MethodInfo automatic = context.GetMethod("ResolveAutomaticSceneCharacter", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(automatic.Invoke(null, null), Is.SameAs(animator));
                Selection.activeGameObject = prop;
                var fallback = (Animator)automatic.Invoke(null, null);
                Assert.That(fallback, Is.Not.Null);
                Assert.That(fallback, Is.Not.SameAs(prop.GetComponent<Animator>()));
                Assert.That(KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(
                    fallback.transform.root.gameObject).IsHumanoid, Is.True);

                var character = new command_context.TimelineCharacterRecord("humanoid", root,
                    animator, avatar, null, null, string.Empty);
                MethodInfo indexed = context.GetMethod("IsIndexableSessionCharacter", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That((bool)indexed.Invoke(null, new object[] { character }), Is.True);
            }
            finally
            {
                Selection.activeObject = previousSelection;
                UnityEngine.Object.DestroyImmediate(prop);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SceneCharacterIndex_GeneratesMissingAvatarBeforeIndexing(bool useSelection)
        {
            string folderName = "SceneCharacterIndex_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            string modelPath = folder + "/Humanoid.fbx";
            GameObject root = null;
            GameObject cacheKeyMarker = null;
            TimelineAsset timeline = null;
            string avatarCachePath = null;
            UnityEngine.Object[] previousSelection = Selection.objects;
            try
            {
                AssetDatabase.CreateFolder("Assets", folderName);
                Assert.That(AssetDatabase.CopyAsset(
                    "Packages/com.unity.kimodo_unity_motion_tools/Editor/Model/SOMA30.fbx", modelPath), Is.True);
                var importer = (ModelImporter)AssetImporter.GetAtPath(modelPath);
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.SaveAndReimport();
                root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modelPath));
                root.name = folderName;
                Animator animator = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
                cacheKeyMarker = new GameObject("KimodoAvatarCacheKey_" + Guid.NewGuid().ToString("N"));
                cacheKeyMarker.transform.SetParent(animator.transform.root, false);
                avatarCachePath = GetGeneratedAvatarCachePath(animator.transform.root.gameObject);
                ArchiveGeneratedAvatarCache(avatarCachePath);
                animator.avatar = null;
                Assert.That(AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<Avatar>()
                    .Any(avatar => avatar.isValid && avatar.isHuman), Is.False,
                    "The fixture must start without a valid Humanoid Avatar.");

                Type context = typeof(command_context);
                Selection.activeGameObject = root;
                MethodInfo automatic = context.GetMethod("ResolveAutomaticSceneCharacter", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(automatic.Invoke(null, null), Is.Null,
                    "Automatic scene discovery must not generate an Avatar or reimport the character.");
                Assert.That(((ModelImporter)AssetImporter.GetAtPath(modelPath)).animationType,
                    Is.EqualTo(ModelImporterAnimationType.Generic),
                    "Read-only character discovery must preserve the ModelImporter rig setting.");

                if (useSelection)
                {
                    var explicitlyResolved = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
                    Assert.That(explicitlyResolved.IsHumanoid, Is.True, explicitlyResolved.Error);
                    Assert.That(automatic.Invoke(null, null), Is.SameAs(animator));
                }
                else
                {
                    MethodInfo find = context.GetMethod("ResolveRequestedSceneCharacters", BindingFlags.Static | BindingFlags.NonPublic);
                    CollectionAssert.AreEqual(new[] { root },
                        (GameObject[])find.Invoke(null, new object[] { root.name }));
                }

                var resolved = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root);
                Assert.That(resolved.IsHumanoid, Is.True, resolved.Error);
                Assert.That(KimodoLocalAvatarUtility.CheckAvatarValid(resolved.Avatar, root), Is.True);
                Assert.That(((ModelImporter)AssetImporter.GetAtPath(modelPath)).animationType,
                    Is.EqualTo(ModelImporterAnimationType.Generic),
                    "Avatar generation must restore the original ModelImporter rig setting.");
                var queried = KimodoLocalAvatarUtility.ResolveAvatarFromGameObject(root, allowGeneration: false);
                Assert.That(queried.IsHumanoid, Is.True, queried.Error);
                Assert.That(queried.Source, Is.EqualTo("Cache"),
                    "A query must reuse the cached Avatar without changing the importer.");
                Assert.That(((ModelImporter)AssetImporter.GetAtPath(modelPath)).animationType,
                    Is.EqualTo(ModelImporterAnimationType.Generic));
                timeline = ScriptableObject.CreateInstance<TimelineAsset>();
                object session = CreateSessionRecord(
                    context.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic), timeline, null, null);
                MethodInfo add = context.GetMethod("AddCharacterTrack", BindingFlags.Static | BindingFlags.NonPublic);
                object[] arguments = { session, root, animator, true, null, true };
                Assert.That((bool)add.Invoke(null, arguments), Is.True, arguments[4] as string);
                Assert.That(timeline.GetRootTracks().Count(), Is.EqualTo(1));
                Assert.That(timeline.GetRootTracks().OfType<AnimationTrack>().Single().trackOffset,
                    Is.EqualTo(TrackOffset.ApplyTransformOffsets),
                    "Command-created character tracks must not apply scene offsets.");
            }
            finally
            {
                Selection.objects = previousSelection;
                if (cacheKeyMarker != null) UnityEngine.Object.DestroyImmediate(cacheKeyMarker);
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (timeline != null) UnityEngine.Object.DestroyImmediate(timeline);
                // Preserve generated fixture assets outside Assets so discovery cannot index them later.
                string project = Path.GetFullPath(Directory.GetCurrentDirectory()) + Path.DirectorySeparatorChar;
                string source = Path.GetFullPath(folder);
                string archive = Path.GetFullPath(Path.Combine("archive", folderName));
                if (!source.StartsWith(project, StringComparison.OrdinalIgnoreCase) ||
                    !archive.StartsWith(project, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fixture archive paths must remain inside the project.");
                Directory.CreateDirectory(Path.GetDirectoryName(archive));
                if (Directory.Exists(source)) Directory.Move(source, archive);
                if (File.Exists(source + ".meta")) File.Move(source + ".meta", archive + ".meta");
                ArchiveGeneratedAvatarCache(avatarCachePath);
                AssetDatabase.Refresh();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SceneContextBinding_ReadsTimelineAndPreservesItsJsonTarget(bool isAutomatic)
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string sessionId = Guid.NewGuid().ToString("D");
            string jsonPath = $"Library/KimodoData/Sessions/{Guid.NewGuid():D}/session.json";
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            GameObject root = new GameObject(KimodoRuntimeUtility.SessionRootPrefix + "BindingTest");
            KimodoSessionContextBinding binding = root.AddComponent<KimodoSessionContextBinding>();
            binding.sessionId = sessionId;
            binding.sessionName = "BindingTest";
            binding.sessionJsonPath = jsonPath;
            binding.timelineAssetPath = "Assets/BindingTest.playable";
            binding.isAutomatic = isAutomatic;
            binding.sessionRevision = 17;
            GameObject directorObject = new GameObject(KimodoRuntimeUtility.SessionDirectorPrefix + "BindingTest");
            directorObject.transform.SetParent(root.transform, false);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;

            try
            {
                Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
                MethodInfo bind = context.GetMethod("BindLoadedSceneContext", BindingFlags.Static | BindingFlags.NonPublic);
                object session = bind.Invoke(null, new object[] { scene });
                Type sessionType = session.GetType();
                MethodInfo isBound = context.GetMethod("IsSceneContextBound", BindingFlags.Static | BindingFlags.NonPublic);

                Assert.That(sessionType.GetProperty("Id").GetValue(session), Is.EqualTo(Guid.Parse(sessionId)));
                Assert.That(sessionType.GetProperty("IsAutomatic").GetValue(session), Is.EqualTo(isAutomatic));
                Assert.That(sessionType.GetProperty("SessionJsonPath").GetValue(session), Is.EqualTo(jsonPath));
                Assert.That(sessionType.GetProperty("SessionRevision").GetValue(session), Is.EqualTo(17));
                Assert.That((bool)isBound.Invoke(null, new[] { session }), Is.True,
                    "The loaded scene root and its serialized Binding identify the context.");
                binding.sessionId = Guid.NewGuid().ToString("D");
                Assert.That((bool)isBound.Invoke(null, new[] { session }), Is.False,
                    "Generation must stop if the scene Binding no longer identifies its context.");
                binding.sessionId = sessionId;
                Assert.That(director.playableAsset, Is.SameAs(timeline));
                Assert.That(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), jsonPath)), Is.False,
                    "Binding a scene context must not read or create its JSON file.");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (timeline != null) UnityEngine.Object.DestroyImmediate(timeline);
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void RuntimeTimelineProjection_ReadsClipsFromBoundSceneTimeline()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject sessionRoot = null;
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationClip firstClip = new AnimationClip { name = "ProjectionFirst" };
            AnimationClip secondClip = new AnimationClip { name = "ProjectionSecond" };
            Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
            FieldInfo currentSessionField = context.GetField("currentTimelineSession", BindingFlags.Static | BindingFlags.NonPublic);
            object previousCurrentSession = currentSessionField.GetValue(null);
            try
            {
                Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(HumanoidModelPath)
                    .OfType<Avatar>().FirstOrDefault(item => item.isValid && item.isHuman);
                Assert.That(avatar, Is.Not.Null, "Missing Humanoid Armature fixture Avatar.");
                GameObject character = CreateHumanoidCharacter("ProjectionCharacter", avatar);
                Animator animator = character.GetComponentInChildren<Animator>(true);

                sessionRoot = new GameObject(KimodoRuntimeUtility.SessionRootPrefix + "ProjectionTest");
                KimodoSessionContextBinding binding = sessionRoot.AddComponent<KimodoSessionContextBinding>();
                Guid sessionId = Guid.NewGuid();
                binding.sessionId = sessionId.ToString("D");
                binding.sessionName = "ProjectionTest";
                binding.sessionJsonPath = $"Library/KimodoData/Sessions/{sessionId:D}/session.json";
                binding.isAutomatic = true;
                GameObject directorObject = new GameObject(KimodoRuntimeUtility.SessionDirectorPrefix + "ProjectionTest");
                directorObject.transform.SetParent(sessionRoot.transform, false);
                PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, character.name);
                director.SetGenericBinding(track, animator);
                timeline.CreateTrack<AnimationTrack>(track, character.name + ".Poses");
                TimelineClip firstTimelineClip = track.CreateClip<AnimationPlayableAsset>();
                ((AnimationPlayableAsset)firstTimelineClip.asset).clip = firstClip;
                firstTimelineClip.displayName = firstClip.name;

                MethodInfo bind = context.GetMethod("BindLoadedSceneContext", BindingFlags.Static | BindingFlags.NonPublic);
                object session = bind.Invoke(null, new object[] { scene });
                Assert.That(session, Is.Not.Null);
                currentSessionField.SetValue(null, session);
                Type sessionType = session.GetType();
                Type characterType = context.GetNestedType("TimelineCharacterRecord", BindingFlags.NonPublic);
                object originalCharacter = ((IList)sessionType.GetProperty("Characters").GetValue(session))[0];
                object originalAnimation = ((IList)characterType.GetProperty("Animations").GetValue(originalCharacter))[0];
                Guid originalAnimationId = (Guid)originalAnimation.GetType().GetProperty("Id").GetValue(originalAnimation);
                JObject retainedAnalysis = new JObject { ["projection_test"] = true };
                byte[] retainedMotion = { 3, 1, 4 };
                originalAnimation.GetType().GetMethod("ApplyResult").Invoke(originalAnimation, new object[]
                {
                    firstClip, retainedAnalysis, retainedMotion, 12, 34
                });

                TimelineClip secondTimelineClip = track.CreateClip<AnimationPlayableAsset>();
                ((AnimationPlayableAsset)secondTimelineClip.asset).clip = secondClip;
                secondTimelineClip.displayName = secondClip.name;
                MethodInfo readProjection = context.GetMethod("ReadSceneTimelineProjection", BindingFlags.Static | BindingFlags.NonPublic);
                readProjection.Invoke(null, new object[] { scene });

                Assert.That(currentSessionField.GetValue(null), Is.SameAs(session),
                    "Refreshing the scene-backed projection must preserve the Session identity used by in-flight work.");
                IList refreshedCharacters = (IList)sessionType.GetProperty("Characters").GetValue(session);
                Assert.That(refreshedCharacters, Has.Count.EqualTo(1));
                IList refreshedAnimations = (IList)characterType.GetProperty("Animations").GetValue(refreshedCharacters[0]);
                Assert.That(refreshedAnimations, Has.Count.EqualTo(2));
                Assert.That(refreshedAnimations.Cast<object>().Single(item =>
                    (Guid)item.GetType().GetProperty("Id").GetValue(item) == originalAnimationId), Is.SameAs(originalAnimation),
                    "A Timeline reread must preserve the animation record carrying generated or analysis data.");
                JObject retainedAnalysisResult = (JObject)originalAnimation.GetType().GetProperty("Analysis").GetValue(originalAnimation);
                Assert.That(retainedAnalysisResult["projection_test"].Value<bool>(), Is.True);
                CollectionAssert.AreEqual(retainedMotion,
                    (byte[])originalAnimation.GetType().GetProperty("KmbBytes").GetValue(originalAnimation));
                CollectionAssert.AreEquivalent(new[] { firstClip.name, secondClip.name },
                    refreshedAnimations.Cast<object>().Select(item => item.GetType().GetProperty("Name").GetValue(item)));
                Assert.That((Guid)refreshedAnimations[0].GetType().GetProperty("Id").GetValue(refreshedAnimations[0]),
                    Is.EqualTo(originalAnimationId), "A projection refresh must keep persistent Timeline Clip identity stable.");
            }
            finally
            {
                currentSessionField.SetValue(null, previousCurrentSession);
                if (sessionRoot != null) UnityEngine.Object.DestroyImmediate(sessionRoot);
                if (timeline != null) UnityEngine.Object.DestroyImmediate(timeline);
                if (firstClip != null) UnityEngine.Object.DestroyImmediate(firstClip);
                if (secondClip != null) UnityEngine.Object.DestroyImmediate(secondClip);
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void PlayModeTransition_DisablesContextWithoutDestroyingItsTimeline()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject root = new GameObject(KimodoRuntimeUtility.SessionRootPrefix + "PlayModeBindingTest");
            root.AddComponent<KimodoSessionContextBinding>();
            GameObject directorObject = new GameObject(KimodoRuntimeUtility.SessionDirectorPrefix + "PlayModeBindingTest");
            directorObject.transform.SetParent(root.transform, false);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            director.playableAsset = timeline;
            try
            {
                Type guardType = typeof(command_dispatcher).Assembly.GetType(
                    "KimodoUnityBridge.Command.KimodoSessionRuntimeGuard");
                MethodInfo onPlayModeStateChanged = guardType.GetMethod(
                    "OnPlayModeStateChanged", BindingFlags.Static | BindingFlags.NonPublic);

                onPlayModeStateChanged.Invoke(null, new object[] { PlayModeStateChange.ExitingEditMode });
                onPlayModeStateChanged.Invoke(null, new object[] { PlayModeStateChange.EnteredPlayMode });
                Assert.That(root, Is.Not.Null);
                Assert.That(root.activeSelf, Is.False);
                Assert.That(root.GetComponent<KimodoSessionContextBinding>(), Is.Not.Null);
                Assert.That(director.playableAsset, Is.SameAs(timeline));

                onPlayModeStateChanged.Invoke(null, new object[] { PlayModeStateChange.EnteredEditMode });
                Assert.That(root, Is.Not.Null);
                Assert.That(root.activeSelf, Is.True);
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (timeline != null) UnityEngine.Object.DestroyImmediate(timeline);
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void BindTimelineCharacter_UsesOriginalAnimatorAndArchivesLegacyGhost()
        {
            Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
            Type sessionType = context.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
            GameObject sessionRoot = new GameObject("KimodoSession_Test");
            GameObject source = new GameObject("TestCharacter");
            GameObject otherSource = new GameObject("OtherCharacter");
            Animator animator = source.AddComponent<Animator>();
            Animator otherAnimator = otherSource.AddComponent<Animator>();
            animator.applyRootMotion = true;
            source.transform.position = new Vector3(3f, 1f, 7f);
            source.transform.rotation = Quaternion.Euler(0f, 31f, 0f);
            Vector3 sourcePosition = source.transform.position;
            Quaternion sourceRotation = source.transform.rotation;
            GameObject oldGhost = new GameObject("Ghost_TestCharacter");
            oldGhost.transform.SetParent(sessionRoot.transform, false);
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var directorObject = new GameObject("Kimodo_CommandSession_Test");
            directorObject.transform.SetParent(sessionRoot.transform, false);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;
            object session = CreateSessionRecord(sessionType, timeline, director, sessionRoot);
            AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, "TestCharacter");
            AnimationTrack otherTrack = timeline.CreateTrack<AnimationTrack>(null, "OtherCharacter");
            director.SetGenericBinding(otherTrack, otherAnimator);
            track.trackOffset = TrackOffset.ApplyTransformOffsets;
            MethodInfo bindMethod = context.GetMethod("BindTimelineCharacter", BindingFlags.Static | BindingFlags.NonPublic);

            try
            {
                bindMethod.Invoke(null, new object[] { session, source, source, animator, track });

                Assert.That(director.GetGenericBinding(track), Is.SameAs(animator));
                Assert.That(director.GetGenericBinding(otherTrack), Is.SameAs(otherAnimator),
                    "Binding one character must preserve other Animator bindings.");
                Assert.That(animator.applyRootMotion, Is.True);
                Assert.That(source.transform.position, Is.EqualTo(sourcePosition));
                Assert.That(source.transform.rotation, Is.EqualTo(sourceRotation));

                director.RebuildGraph();
                director.time = 0.5d;
                director.Evaluate();
                Assert.That(source.transform.position, Is.EqualTo(sourcePosition),
                    "Evaluating a newly bound Timeline must preserve the character's scene position.");
                Assert.That(Quaternion.Angle(source.transform.rotation, sourceRotation), Is.LessThan(0.01f),
                    "Evaluating a newly bound Timeline must preserve the character's scene rotation.");
                Assert.That(oldGhost.activeSelf, Is.False);
                Assert.That(oldGhost.transform.parent.name, Is.EqualTo("Archive_GhostPreviews"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(otherSource);
                UnityEngine.Object.DestroyImmediate(sessionRoot);
                UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        [Test]
        public void DestroySceneSessionRoots_DestroysTemporaryContexts()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            Scene testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject contextRoot = new GameObject("KimodoSession_DestroyTest");
            GameObject legacyArchive = new GameObject("KimodoSessionArchive");
            GameObject legacyArchivedSession = new GameObject("Archive_KimodoSession_DestroyTest");
            legacyArchivedSession.transform.SetParent(legacyArchive.transform, false);

            try
            {
                Type context = typeof(command_dispatcher).Assembly.GetType("KimodoUnityBridge.Command.command_context");
                MethodInfo destroy = context.GetMethod("DestroySceneSessionRoots", BindingFlags.Static | BindingFlags.NonPublic);

                Assert.That((int)destroy.Invoke(null, new object[] { testScene }), Is.EqualTo(2));
                Assert.That(contextRoot == null, Is.True);
                Assert.That(legacyArchive == null, Is.True,
                    "session.clear should clean up the legacy archive container and its archived transient contexts.");
                Assert.That(legacyArchivedSession == null, Is.True);
            }
            finally
            {
                if (contextRoot != null) UnityEngine.Object.DestroyImmediate(contextRoot);
                if (legacyArchive != null) UnityEngine.Object.DestroyImmediate(legacyArchive);
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void SessionHistory_DoesNotCreateContextWhenActiveSceneHasNoBinding()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            Scene activeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                Assert.That(activeScene.GetRootGameObjects(), Is.Empty);
                JObject response = JObject.Parse(command_dispatcher.Invoke("session.history", "{}"));
                Assert.That(response.Value<bool>("ok"), Is.False);
                Assert.That(response["error"]?.Value<string>("code"), Is.EqualTo("invalid_scene_context"));
                Assert.That(activeScene.GetRootGameObjects(), Is.Empty,
                    "History reads must not create or replace a scene Context.");
            }
            finally
            {
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void SessionRuntimeGuard_RestoresTheOriginalActiveState()
        {
            GameObject root = new GameObject("KimodoSession_RuntimeGuardTest");
            try
            {
                KimodoDisableOnRuntime guard = root.AddComponent<KimodoDisableOnRuntime>();

                guard.DisableForPlay();
                Assert.That(root.activeSelf, Is.False);

                guard.RestoreAfterPlay();
                Assert.That(root.activeSelf, Is.True);

                root.SetActive(false);
                guard.DisableForPlay();
                guard.RestoreAfterPlay();
                Assert.That(root.activeSelf, Is.False,
                    "A context that was inactive before Play Mode must remain inactive in Edit Mode.");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SessionRuntimeGuard_PlayModeStateChangesDisableAndRestoreSessionRoots()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject root = new GameObject(KimodoRuntimeUtility.SessionRootPrefix + "RuntimeEventGuardTest");
            try
            {
                Type guardType = typeof(command_context).Assembly.GetType(
                    "KimodoUnityBridge.Command.KimodoSessionRuntimeGuard");
                MethodInfo onPlayModeStateChanged = guardType?.GetMethod(
                    "OnPlayModeStateChanged", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(onPlayModeStateChanged, Is.Not.Null);

                onPlayModeStateChanged.Invoke(null, new object[] { PlayModeStateChange.ExitingEditMode });
                Assert.That(root.activeSelf, Is.False,
                    "The Session root must be disabled before Play Mode starts.");

                onPlayModeStateChanged.Invoke(null, new object[] { PlayModeStateChange.EnteredPlayMode });
                Assert.That(root.activeSelf, Is.False,
                    "Entering Play Mode must keep the Session root disabled without replacing its captured state.");

                onPlayModeStateChanged.Invoke(null, new object[] { PlayModeStateChange.EnteredEditMode });
                Assert.That(root.activeSelf, Is.True,
                    "The Session root must recover its Edit Mode state after Play Mode.");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        private static object CreateSessionRecord(
            Type sessionType,
            TimelineAsset timeline,
            PlayableDirector director,
            GameObject sessionRoot)
        {
            ConstructorInfo constructor = sessionType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(item => item.GetParameters().Length == 9);
            return constructor.Invoke(new object[]
            {
                Guid.NewGuid(), "Test", director, timeline, string.Empty, true, sessionRoot, null, 0
            });
        }

        private static void EnsureAssetFolder(string folderPath)
        {
            string current = "Assets";
            foreach (string segment in folderPath.Split('/').Skip(1))
            {
                string next = current + "/" + segment;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, segment);
                current = next;
            }
        }

        private static GameObject CreateHumanoidCharacter(string name, Avatar avatar)
        {
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(HumanoidModelPath);
            Assert.That(model, Is.Not.Null, "Missing Humanoid Armature fixture.");
            GameObject character = UnityEngine.Object.Instantiate(model);
            character.name = name;
            Animator animator = character.GetComponentInChildren<Animator>(true);
            Assert.That(animator, Is.Not.Null, "Humanoid Armature fixture has no Animator.");
            animator.avatar = avatar;
            animator.runtimeAnimatorController = null;
            return character;
        }

        private static void ArchiveFixtureScene(string sourcePath, string suffix)
        {
            string project = Path.GetFullPath(Directory.GetCurrentDirectory()) + Path.DirectorySeparatorChar;
            string source = Path.GetFullPath(sourcePath);
            string archive = Path.GetFullPath(Path.Combine("archive", "kimodo-command-resolution-tests", Path.GetFileNameWithoutExtension(source) + "_" + suffix + ".unity"));
            if (!source.StartsWith(project, StringComparison.OrdinalIgnoreCase) ||
                !archive.StartsWith(project, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Fixture archive paths must remain inside the project.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(archive));
            if (File.Exists(source)) File.Move(source, archive);
            if (File.Exists(source + ".meta")) File.Move(source + ".meta", archive + ".meta");
        }

        private static string GetGeneratedAvatarCachePath(GameObject root)
        {
            Type cacheUtility = typeof(KimodoLocalAvatarUtility).Assembly.GetType(
                "KimodoBridge.Editor.KimodoEditorAvatarCacheUtility");
            MethodInfo buildPath = cacheUtility?.GetMethod(
                "BuildAvatarCachePath", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(buildPath, Is.Not.Null);
            return (string)buildPath.Invoke(null, new object[] { root });
        }

        private static void ArchiveGeneratedAvatarCache(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) return;
            string project = Path.GetFullPath(Directory.GetCurrentDirectory()) + Path.DirectorySeparatorChar;
            string source = Path.GetFullPath(assetPath);
            string archive = Path.GetFullPath(Path.Combine(
                "archive", "kimodo-avatar-cache-tests",
                Path.GetFileNameWithoutExtension(source) + "_" + Guid.NewGuid().ToString("N") + Path.GetExtension(source)));
            if (!source.StartsWith(Path.Combine(project, "Assets") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !archive.StartsWith(project, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Avatar cache archive paths must remain inside the project.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(archive));
            bool moved = false;
            if (File.Exists(source))
            {
                File.Move(source, archive);
                moved = true;
            }
            if (File.Exists(source + ".meta"))
            {
                File.Move(source + ".meta", archive + ".meta");
                moved = true;
            }
            if (moved) AssetDatabase.Refresh();
        }

    }
}
