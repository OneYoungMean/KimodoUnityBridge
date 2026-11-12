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

        [Test]
        public void SessionRoot_UsesTimelineGuidAndDirectDirectorWithoutBindingOrChildDirector()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string timelinePath = $"Assets/Archive/KimodoCommandTests/Timelines/SessionRoot_{Guid.NewGuid():N}.playable";
            EnsureAssetFolder("Assets/Archive/KimodoCommandTests/Timelines");
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(timeline, timelinePath);
            GameObject root = new GameObject(KimodoRuntimeUtility.SessionRootPrefix + "RootTest");
            KimodoDisableOnRuntime.Ensure(root);
            PlayableDirector director = root.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;

            try
            {
                command_context.TimelineSessionRecord session = command_context.BuildTimelineSessionView(scene);
                Assert.That(session, Is.Not.Null);
                Assert.That(session.Id, Is.EqualTo(command_context.GetTimelineAssetGuid(timeline)));
                Assert.That(session.TimelineAssetPath, Is.EqualTo(timelinePath));
                Assert.That(root.GetComponent<PlayableDirector>(), Is.SameAs(director));
                Assert.That(director.transform, Is.SameAs(root.transform));
                Assert.That(root.transform.childCount, Is.Zero);
                Assert.That(root.GetComponent<KimodoSessionContextBinding>(), Is.Null);
                Assert.That(root.GetComponent<KimodoDisableOnRuntime>(), Is.Not.Null);
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

        [Test]
        public void SessionContext_RebuildsTimelineProjectionAfterExternalClipChanges()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject sessionRoot = null;
            string timelinePath = $"Assets/Archive/KimodoCommandTests/Timelines/Projection_{Guid.NewGuid():N}.playable";
            EnsureAssetFolder("Assets/Archive/KimodoCommandTests/Timelines");
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            Guid sessionId = Guid.Empty;
            AnimationClip firstClip = new AnimationClip { name = "ProjectionFirst" };
            AnimationClip secondClip = new AnimationClip { name = "ProjectionSecond" };
            AssetDatabase.CreateAsset(timeline, timelinePath);
            sessionId = Guid.Parse(AssetDatabase.AssetPathToGUID(timelinePath));
            try
            {
                sessionRoot = new GameObject(KimodoRuntimeUtility.SessionRootPrefix + "ProjectionTest");
                KimodoDisableOnRuntime.Ensure(sessionRoot);
                PlayableDirector director = sessionRoot.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                GameObject character = new GameObject("ProjectionCharacter");
                Animator animator = character.AddComponent<Animator>();
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, character.name);
                director.SetGenericBinding(track, animator);
                timeline.CreateTrack<AnimationTrack>(track, character.name + ".Poses");
                TimelineClip firstTimelineClip = track.CreateClip<AnimationPlayableAsset>();
                AssetDatabase.AddObjectToAsset(firstClip, timeline);
                ((AnimationPlayableAsset)firstTimelineClip.asset).clip = firstClip;
                firstTimelineClip.displayName = "ProjectionFirst";
                AssetDatabase.SaveAssets();
                command_context.TimelineSessionRecord firstProjection = command_context.BuildTimelineSessionView(scene);
                Assert.That(firstProjection, Is.Not.Null);
                Guid firstClipId = firstProjection.Characters.Single().Animations.Single().Id;
                TimelineClip secondTimelineClip = track.CreateClip<AnimationPlayableAsset>();
                AssetDatabase.AddObjectToAsset(secondClip, timeline);
                ((AnimationPlayableAsset)secondTimelineClip.asset).clip = secondClip;
                secondTimelineClip.displayName = "ProjectionSecond";
                AssetDatabase.SaveAssets();
                command_context.TimelineSessionRecord refreshed = command_context.BuildTimelineSessionView(scene);
                Assert.That(refreshed, Is.Not.Null);
                Assert.That(refreshed.Characters.Single().Animations, Has.Count.EqualTo(2));
                Assert.That(refreshed.Characters.Single().Animations.Select(item => item.Name),
                    Is.EquivalentTo(new[] { "ProjectionFirst", "ProjectionSecond" }));
                Assert.That(refreshed.Characters.Single().Animations[0].Id, Is.EqualTo(firstClipId),
                    "A fresh Timeline projection retains stable IDs for unchanged Timeline clips.");

                JObject compact = JObject.Parse(command_dispatcher.Invoke("session.context", "{}"));
                Assert.That(compact.Value<bool>("ok"), Is.True, compact.ToString());
                Assert.That(compact["session"]?.Value<int>("clip_count"), Is.EqualTo(2));
                Assert.That(compact["details"], Is.Null, "Detail groups are omitted unless requested.");

                JObject clipDetails = JObject.Parse(command_dispatcher.Invoke(
                    "session.context", "{'details':['clips']}"));
                Assert.That(clipDetails["details"]?["clips"]?.Count(), Is.EqualTo(2));
                Assert.That(clipDetails["details"]?["characters"], Is.Null);
                Assert.That(clipDetails["details"]?["timeline"], Is.Null);
                Assert.That(clipDetails["details"]?["tasks"], Is.Null);

                JObject timelineDetails = JObject.Parse(command_dispatcher.Invoke(
                    "session.context", "{'details':['timeline']}"));
                Assert.That(timelineDetails["details"]?["timeline"]?["tracks"]?.Count(), Is.EqualTo(2));
                JObject unknownDetail = JObject.Parse(command_dispatcher.Invoke(
                    "session.context", "{'details':['animations']}"));
                Assert.That(unknownDetail["ok"]?.Value<bool>(), Is.False);
                Assert.That(unknownDetail["error"]?["code"]?.Value<string>(), Is.EqualTo("invalid_argument"));
            }
            finally
            {
                if (sessionRoot != null) UnityEngine.Object.DestroyImmediate(sessionRoot);
                if (sessionId != Guid.Empty) ArchiveSessionData(sessionId);
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void SessionContext_ReportsKimodoPlayableClipAssetType()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject sessionRoot = null;
            string timelinePath = $"Assets/Archive/KimodoCommandTests/Timelines/ClipKind_{Guid.NewGuid():N}.playable";
            EnsureAssetFolder("Assets/Archive/KimodoCommandTests/Timelines");
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AnimationClip ordinarySource = new AnimationClip { name = "ClipKindOrdinarySource" };
            Guid sessionId = Guid.Empty;
            try
            {
                AssetDatabase.CreateAsset(timeline, timelinePath);
                sessionId = Guid.Parse(AssetDatabase.AssetPathToGUID(timelinePath));
                sessionRoot = new GameObject(KimodoRuntimeUtility.SessionRootPrefix + "ClipKindTest");
                KimodoDisableOnRuntime.Ensure(sessionRoot);
                PlayableDirector director = sessionRoot.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                GameObject character = new GameObject("ClipKindCharacter");
                Animator animator = character.AddComponent<Animator>();
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, character.name);
                director.SetGenericBinding(track, animator);
                timeline.CreateTrack<AnimationTrack>(track, character.name + ".Poses");

                TimelineClip ordinaryTimelineClip = track.CreateClip<AnimationPlayableAsset>();
                AssetDatabase.AddObjectToAsset(ordinarySource, timeline);
                ((AnimationPlayableAsset)ordinaryTimelineClip.asset).clip = ordinarySource;
                ordinaryTimelineClip.displayName = "ClipKindOrdinary";
                ordinaryTimelineClip.start = 0d;
                ordinaryTimelineClip.duration = 1d;

                TimelineClip kimodoTimelineClip = track.CreateClip<KimodoPlayableClip>();
                kimodoTimelineClip.displayName = "ClipKindGenerated";
                kimodoTimelineClip.start = 2d;
                kimodoTimelineClip.duration = 1d;
                AssetDatabase.SaveAssets();

                JObject context = JObject.Parse(command_dispatcher.Invoke("session.context", "{'details':['clips']}"));
                Assert.That(context.Value<bool>("ok"), Is.True, context.ToString());
                JArray clips = context["details"]?["clips"] as JArray;
                Assert.That(clips?.Count, Is.EqualTo(2));
                JObject ordinary = clips.Values<JObject>().Single(item => item.Value<string>("name") == "ClipKindOrdinarySource");
                JObject kimodo = clips.Values<JObject>().Single(item => item.Value<string>("name") == "ClipKindGenerated");
                Assert.That(ordinary.Value<string>("kind"), Is.EqualTo("animation_clip"));
                Assert.That(ordinary.Value<string>("asset_type"), Is.EqualTo(nameof(AnimationPlayableAsset)));
                Assert.That(kimodo.Value<string>("kind"), Is.EqualTo("kimodo_clip"));
                Assert.That(kimodo.Value<string>("asset_type"), Is.EqualTo(nameof(KimodoPlayableClip)));
                Assert.That(kimodo.Value<string>("animation_id"), Is.Empty,
                    "A generated placeholder without an AnimationClip is still projected by its Timeline asset type.");
            }
            finally
            {
                if (sessionRoot != null) UnityEngine.Object.DestroyImmediate(sessionRoot);
                if (sessionId != Guid.Empty) ArchiveSessionData(sessionId);
                ArchiveTimelineAsset(timelinePath);
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void PrepareGenerationTrace_UsesResolvedSessionAndCurrentTimelineEnd()
        {
            Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(HumanoidModelPath).OfType<Avatar>()
                .First(item => item.isValid && item.isHuman);
            GameObject root = CreateHumanoidCharacter("GenerationTraceCharacter", avatar);
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            try
            {
                var session = (command_context.TimelineSessionRecord)CreateSessionRecord(
                    typeof(command_context.TimelineSessionRecord), timeline, null, null);
                AnimationTrack track = timeline.CreateTrack<AnimationTrack>(null, root.name);
                var character = new command_context.TimelineCharacterRecord(
                    "generation-trace-test", root, root.GetComponentInChildren<Animator>(),
                    avatar, track, track, string.Empty);
                session.Characters.Add(character);
                TimelineClip existing = track.CreateClip<AnimationPlayableAsset>();
                existing.start = 1d;
                existing.duration = 2d;
                MethodInfo prepare = typeof(command_context).GetMethod(
                    "PrepareGenerationTrace", BindingFlags.Static | BindingFlags.NonPublic);

                object trace = prepare.Invoke(null, new object[] { session, character, 2d });
                Type traceType = trace.GetType();
                Assert.That(traceType.GetProperty("Session").GetValue(trace), Is.SameAs(session));
                Assert.That(traceType.GetProperty("Character").GetValue(trace), Is.SameAs(character));
                Assert.That((double)traceType.GetProperty("StartSeconds").GetValue(trace),
                    Is.EqualTo(existing.end + command_context.ClipSafeZoneSeconds).Within(1e-6));
                Assert.That((double)traceType.GetProperty("DurationSeconds").GetValue(trace), Is.EqualTo(2d));

                var foreignSession = (command_context.TimelineSessionRecord)CreateSessionRecord(
                    typeof(command_context.TimelineSessionRecord), timeline, null, null);
                TargetInvocationException rejected = Assert.Throws<TargetInvocationException>(() =>
                    prepare.Invoke(null, new object[] { foreignSession, character, 2d }));
                Assert.That(rejected.InnerException.Message, Does.Contain("not in the active scene context"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(timeline);
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
        public void SessionContext_AutoCreatesOnceAndHistoryListsTheEmptySession()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            Scene activeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string createdTimelinePath = null;
            Guid createdSessionId = Guid.Empty;
            try
            {
                Assert.That(activeScene.GetRootGameObjects(), Is.Empty);
                JObject context = JObject.Parse(command_dispatcher.Invoke("session.context", "{}"));
                Assert.That(context.Value<bool>("ok"), Is.True, context.ToString());
                createdSessionId = Guid.Parse(context["session"]?.Value<string>("session_id"));
                createdTimelinePath = context["timeline"]?.Value<string>("path");
                GameObject[] roots = activeScene.GetRootGameObjects().Where(root =>
                    root.name.StartsWith(KimodoRuntimeUtility.SessionRootPrefix, System.StringComparison.Ordinal)).ToArray();
                Assert.That(roots, Has.Length.EqualTo(1));
                Assert.That(roots[0].GetComponent<PlayableDirector>(), Is.Not.Null);
                Assert.That(roots[0].GetComponent<KimodoSessionContextBinding>(), Is.Null);
                Assert.That(roots[0].transform.childCount, Is.Zero);

                JObject repeated = JObject.Parse(command_dispatcher.Invoke("session.context", "{}"));
                Assert.That(repeated["session"]?.Value<string>("session_id"), Is.EqualTo(createdSessionId.ToString("D")));
                Assert.That(activeScene.GetRootGameObjects().Count(root =>
                    root.name.StartsWith(KimodoRuntimeUtility.SessionRootPrefix, System.StringComparison.Ordinal)), Is.EqualTo(1));

                JObject details = JObject.Parse(command_dispatcher.Invoke(
                    "session.context", "{'details':['characters','clips','timeline','tasks']}"));
                Assert.That(details["details"]?["characters"], Is.TypeOf<JArray>());
                Assert.That(details["details"]?["clips"], Is.TypeOf<JArray>());
                Assert.That(details["details"]?["timeline"], Is.TypeOf<JObject>());
                Assert.That(details["details"]?["tasks"], Is.TypeOf<JArray>());

                JObject currentHistory = JObject.Parse(command_dispatcher.Invoke("session.history", "{}"));
                Assert.That(currentHistory.Value<bool>("ok"), Is.True, currentHistory.ToString());
                Assert.That(currentHistory["history"]?["operations"], Is.TypeOf<JArray>().And.Empty);
                JObject globalHistory = JObject.Parse(command_dispatcher.Invoke("session.history", "{'scope':'global'}"));
                JObject directoryEntry = (globalHistory["history"]?["sessions"] as JArray)?.Values<JObject>()
                    .SingleOrDefault(item => item.Value<string>("session_id") == createdSessionId.ToString("D"));
                Assert.That(directoryEntry, Is.Not.Null, globalHistory.ToString());
                Assert.That(directoryEntry.Value<int>("operation_count"), Is.Zero);
            }
            finally
            {
                if (createdSessionId != Guid.Empty)
                {
                    command_dispatcher.Invoke("session.clear", "{}");
                    ArchiveSessionData(createdSessionId);
                    ArchiveTimelineAsset(createdTimelinePath);
                }
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void SessionContext_TimelinePathSwitchesTheDirectTimelineRoot()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EnsureAssetFolder("Assets/Archive/KimodoCommandTests/Timelines");
            string firstPath = AssetDatabase.GenerateUniqueAssetPath(
                "Assets/Archive/KimodoCommandTests/Timelines/ContextSwitch_First.playable");
            string secondPath = AssetDatabase.GenerateUniqueAssetPath(
                "Assets/Archive/KimodoCommandTests/Timelines/ContextSwitch_Second.playable");
            var firstTimeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var secondTimeline = ScriptableObject.CreateInstance<TimelineAsset>();
            AssetDatabase.CreateAsset(firstTimeline, firstPath);
            AssetDatabase.CreateAsset(secondTimeline, secondPath);
            Guid automaticSessionId = Guid.Empty;
            try
            {
                JObject automatic = JObject.Parse(command_dispatcher.Invoke("session.context", "{}"));
                Assert.That(automatic["ok"]?.Value<bool>(), Is.True, automatic.ToString());
                automaticSessionId = Guid.Parse(automatic["session"]?.Value<string>("session_id"));

                JObject first = JObject.Parse(command_dispatcher.Invoke(
                    "session.context", new JObject { ["timeline_path"] = firstPath }.ToString()));
                Assert.That(first["ok"]?.Value<bool>(), Is.True, first.ToString());
                Assert.That(first["session"]?["session_id"]?.Value<string>(), Is.EqualTo(AssetDatabase.AssetPathToGUID(firstPath)));
                Assert.That(first["timeline"]?["path"]?.Value<string>(), Is.EqualTo(firstPath));
                Assert.That(first["opened_timeline"]?["timeline_asset_path"]?.Value<string>(), Is.EqualTo(firstPath));

                JObject second = JObject.Parse(command_dispatcher.Invoke(
                    "session.context", new JObject { ["timeline_path"] = secondPath }.ToString()));
                Assert.That(second["ok"]?.Value<bool>(), Is.True, second.ToString());
                Assert.That(second["session"]?["session_id"]?.Value<string>(), Is.EqualTo(AssetDatabase.AssetPathToGUID(secondPath)));
                GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects()
                    .Where(root => root.name.StartsWith(KimodoRuntimeUtility.SessionRootPrefix, StringComparison.Ordinal))
                    .ToArray();
                Assert.That(roots, Has.Length.EqualTo(1));
                Assert.That(roots[0].GetComponent<PlayableDirector>()?.playableAsset, Is.SameAs(secondTimeline));
                Assert.That(roots[0].GetComponent<KimodoSessionContextBinding>(), Is.Null);
                Assert.That(roots[0].transform.childCount, Is.Zero);
            }
            finally
            {
                command_dispatcher.Invoke("session.clear", "{}");
                if (automaticSessionId != Guid.Empty) ArchiveSessionData(automaticSessionId);
                ArchiveSessionData(Guid.Parse(AssetDatabase.AssetPathToGUID(firstPath)));
                ArchiveSessionData(Guid.Parse(AssetDatabase.AssetPathToGUID(secondPath)));
                ArchiveTimelineAsset(firstPath);
                ArchiveTimelineAsset(secondPath);
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void SessionHistory_RecordsFailuresAndAggregatesAsyncOperationsBySequence()
        {
            SceneSetup[] previousSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Guid sessionId = Guid.Empty;
            string historyPath = null;
            string timelinePath = null;
            string originalHistory = null;
            try
            {
                JObject context = JObject.Parse(command_dispatcher.Invoke("session.context", "{}"));
                Assert.That(context["ok"]?.Value<bool>(), Is.True, context.ToString());
                sessionId = Guid.Parse(context["session"]?["session_id"]?.Value<string>());
                timelinePath = context["timeline"]?.Value<string>("path");
                historyPath = Path.Combine(Directory.GetCurrentDirectory(), "Library", "KimodoData", "Sessions",
                    sessionId.ToString("D"), "history.jsonl");

                for (int index = 1; index <= 23; index++)
                {
                    command_context.RecordCommandHistory(command_context.AnimationAnalysisCommand,
                        new JObject { ["fixture_index"] = index },
                        index == 2
                            ? new JObject { ["ok"] = false, ["error"] = new JObject { ["code"] = "fixture_failure" } }
                            : new JObject { ["ok"] = true, ["fixture_result"] = index });
                }

                const string asyncRequestId = "async-history-fixture";
                command_context.RecordCommandHistory(command_context.GenerateAnimationCommand,
                    new JObject { ["prompt"] = "fixture async generation" },
                    new JObject { ["ok"] = true, ["request_id"] = asyncRequestId, ["status"] = "accepted" });
                Type contextType = typeof(command_context);
                MethodInfo buildView = contextType.GetMethod("BuildTimelineSessionView", BindingFlags.Static | BindingFlags.NonPublic);
                object currentSession = buildView.Invoke(null, new object[] { SceneManager.GetActiveScene() });
                MethodInfo recordTerminal = contextType.GetMethod("RecordGenerationTerminalHistory", BindingFlags.Static | BindingFlags.NonPublic);
                recordTerminal.Invoke(null, new object[] { currentSession,
                    new JObject { ["request_id"] = asyncRequestId, ["status"] = "completed", ["clip"] = new JObject { ["name"] = "fixture" } } });

                JObject latest = JObject.Parse(command_dispatcher.Invoke("session.history", "{}"));
                JArray latestOperations = latest["history"]?["operations"] as JArray;
                Assert.That(latestOperations, Has.Count.EqualTo(20));
                Assert.That(latestOperations.First?.Value<long>("operation_seq"), Is.EqualTo(24));
                Assert.That(latestOperations.Last?.Value<long>("operation_seq"), Is.EqualTo(5));
                JObject failed = JObject.Parse(command_dispatcher.Invoke(
                    "session.history", "{'historybegin':2,'historyend':2}"))["history"]?["operations"]?.First as JObject;
                Assert.That(failed, Is.Not.Null);
                Assert.That(failed.Value<string>("status"), Is.EqualTo("failed"));
                Assert.That(failed["input"]?["fixture_index"]?.Value<int>(), Is.EqualTo(2));
                Assert.That(failed["response"]?["error"]?["code"]?.Value<string>(), Is.EqualTo("fixture_failure"));

                JObject ranged = JObject.Parse(command_dispatcher.Invoke(
                    "session.history", "{'historybegin':3,'historyend':5}"));
                CollectionAssert.AreEqual(new long[] { 5, 4, 3 },
                    ranged["history"]?["operations"]?.Values<JObject>().Select(item => item.Value<long>("operation_seq")));
                JObject partialRange = JObject.Parse(command_dispatcher.Invoke("session.history", "{'historybegin':3}"));
                Assert.That(partialRange["error"]?["code"]?.Value<string>(), Is.EqualTo("invalid_argument"));

                JObject asyncOperation = JObject.Parse(command_dispatcher.Invoke(
                    "session.history", "{'historybegin':24,'historyend':24}"))["history"]?["operations"]?.First as JObject;
                Assert.That(asyncOperation?.Value<string>("status"), Is.EqualTo("completed"));
                Assert.That(asyncOperation?["input"]?["prompt"]?.Value<string>(), Is.EqualTo("fixture async generation"));
                Assert.That(asyncOperation?["accepted"]?["status"]?.Value<string>(), Is.EqualTo("accepted"));
                Assert.That(asyncOperation?["response"]?["status"]?.Value<string>(), Is.EqualTo("completed"));

                JObject byId = JObject.Parse(command_dispatcher.Invoke("session.history",
                    new JObject { ["session_id"] = sessionId.ToString("D") }.ToString()));
                Assert.That(byId["session"]?["session_id"]?.Value<string>(), Is.EqualTo(sessionId.ToString("D")));

                originalHistory = File.ReadAllText(historyPath);
                string[] lines = originalHistory.Split('\n');
                JObject header = JObject.Parse(lines[0]);
                header["package_version"] = "3.0.12";
                lines[0] = header.ToString(Newtonsoft.Json.Formatting.None);
                File.WriteAllText(historyPath, string.Join("\n", lines));
                Assert.That(JObject.Parse(command_dispatcher.Invoke("session.history", "{}"))["ok"]?.Value<bool>(), Is.True,
                    "Patch-level package versions within the same MAJOR.MINOR are compatible.");
                header["package_version"] = "3.1.0";
                lines[0] = header.ToString(Newtonsoft.Json.Formatting.None);
                File.WriteAllText(historyPath, string.Join("\n", lines));
                JObject incompatible = JObject.Parse(command_dispatcher.Invoke("session.history", "{}"));
                Assert.That(incompatible["error"]?["code"]?.Value<string>(), Is.EqualTo("history_incompatible"));
                header["package_version"] = "3.0.invalid";
                lines[0] = header.ToString(Newtonsoft.Json.Formatting.None);
                File.WriteAllText(historyPath, string.Join("\n", lines));
                JObject malformedVersion = JObject.Parse(command_dispatcher.Invoke("session.history", "{}"));
                Assert.That(malformedVersion["error"]?["code"]?.Value<string>(), Is.EqualTo("history_incompatible"));
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(originalHistory) && !string.IsNullOrWhiteSpace(historyPath))
                    File.WriteAllText(historyPath, originalHistory);
                if (sessionId != Guid.Empty)
                {
                    command_dispatcher.Invoke("session.clear", "{}");
                    ArchiveSessionData(sessionId);
                    ArchiveTimelineAsset(timelinePath);
                }
                if (previousSceneSetup.Any(setup => setup.isLoaded))
                    EditorSceneManager.RestoreSceneManagerSetup(previousSceneSetup);
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void SessionRuntimeGuard_DisablesTheRootWithoutTrackingExitState()
        {
            GameObject root = new GameObject("KimodoSession_RuntimeGuardTest");
            try
            {
                KimodoDisableOnRuntime guard = root.AddComponent<KimodoDisableOnRuntime>();

                guard.DisableForPlay();
                Assert.That(root.activeSelf, Is.False);

                guard.DisableForPlay();
                Assert.That(root.activeSelf, Is.False,
                    "Disabling an already inactive Session root must remain safe.");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SessionRuntimeGuard_DisablesDirectRootAndHasNoPlayModeExitCallback()
        {
            GameObject root = new GameObject(KimodoRuntimeUtility.SessionRootPrefix + "RuntimeEventGuardTest");
            try
            {
                KimodoDisableOnRuntime guard = KimodoDisableOnRuntime.Ensure(root);
                Assert.That(guard, Is.Not.Null);
                guard.DisableForPlay();
                Assert.That(root.activeSelf, Is.False);
                Assert.That(typeof(KimodoSessionRuntimeGuard).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                    .Any(method => method.Name.Contains("PlayMode")), Is.False,
                    "No Play Mode callback should restore the Session root on exit.");
            }
            finally
            {
                if (root != null) UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static object CreateSessionRecord(
            Type sessionType,
            TimelineAsset timeline,
            PlayableDirector director,
            GameObject sessionRoot)
        {
            ConstructorInfo constructor = sessionType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(item => item.GetParameters().Length == 6);
            return constructor.Invoke(new object[]
            {
                Guid.NewGuid(), "Test", director, timeline, string.Empty, sessionRoot
            });
        }

        private static void ArchiveSessionData(Guid sessionId)
        {
            string source = Path.Combine("Library", "KimodoData", "Sessions", sessionId.ToString("D"));
            if (!Directory.Exists(source)) return;
            string archive = Path.Combine("Library", "Archive", "KimodoData", "Sessions", sessionId.ToString("D"));
            Directory.CreateDirectory(Path.GetDirectoryName(archive));
            if (Directory.Exists(archive)) archive += "_" + Guid.NewGuid().ToString("N");
            Directory.Move(source, archive);
        }

        private static void ArchiveTimelineAsset(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal)) return;
            string fileName = Path.GetFileName(assetPath);
            string archiveFolder = "Assets/Archive/KimodoCommandTests/Timelines";
            EnsureAssetFolder(archiveFolder);
            string destination = AssetDatabase.GenerateUniqueAssetPath(archiveFolder + "/" + fileName);
            string error = AssetDatabase.MoveAsset(assetPath, destination);
            Assert.That(error, Is.Empty, $"Could not archive Timeline asset '{assetPath}': {error}");
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
