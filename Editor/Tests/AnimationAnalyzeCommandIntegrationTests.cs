using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KimodoBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using UnityEngine.TestTools;

namespace KimodoUnityBridge.Command.Tests
{
    public sealed class AnimationAnalyzeCommandIntegrationTests
    {
        private const string PackageRoot = "Packages/com.unity.kimodo_unity_motion_tools";
        private const string ModelPath = PackageRoot + "/Editor/Model/Armature.fbx";
        private const string ArcWalkPath = PackageRoot + "/Editor/Tests/Fixtures/arc_walk_left_loop.anim";
        private const int TestAnalysisResolution = 1920;

        [UnityTest]
        public IEnumerator AnimationAnalyze_ArcWalkFixture_WritesCompositePng()
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ArcWalkPath);
            Assert.That(source, Is.Not.Null, "Missing Armature fixture.");
            Assert.That(clip, Is.Not.Null, "Missing arc walk left loop fixture.");

            GameObject character = UnityEngine.Object.Instantiate(source);
            character.name = "KimodoTest_Armature";
            object testSession = null;
            try
            {
                Avatar modelAvatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
                    .OfType<Avatar>()
                    .FirstOrDefault();
                Assert.That(modelAvatar, Is.Not.Null.And.Property("isHuman").True,
                    "Armature fixture requires a Humanoid Avatar.");
                Animator animator = character.GetComponentInChildren<Animator>(true) ?? character.AddComponent<Animator>();
                animator.avatar = modelAvatar;
                animator.runtimeAnimatorController = null;
                animator.Rebind();

                testSession = CreateCurrentTestContext(character, animator, clip);
                JObject analysis = Require("animation_analyze", new JObject
                {
                    ["clips"] = new JArray(new JObject
                    {
                        ["character"] = character.name,
                        ["clip"] = clip.name
                    }),
                    ["picture"] = new JObject { ["output"] = "composite" },
                    ["resolution"] = TestAnalysisResolution
                });

                Assert.That(analysis.Value<string>("analysis_schema_version"), Is.EqualTo("2-phase-track-v1"));
                Assert.That(analysis["pictures"]?.Value<string>("aspect"), Is.EqualTo("16:9"));
                Assert.That(analysis["pictures"]?.Value<int>("tile_count"), Is.EqualTo(20));
                Assert.That(analysis["pictures"]?.Value<string>("render_version"), Is.EqualTo("57-pose-spacing-event-ghosts-phase-v2"));

                JObject clipAnalysis = analysis["clips"]?.Children<JObject>().Single();
                Assert.That(clipAnalysis?.Value<string>("phase_track_version"), Is.EqualTo("1-temporal-cluster-v2-command-60"));
                JArray phases = clipAnalysis?["phase_track"] as JArray;
                Assert.That(phases, Is.Not.Null.And.Not.Empty);
                Assert.That(phases.First().Value<int>("start_frame"), Is.EqualTo(0));
                int analyzedFrameCount = clipAnalysis["root_trajectory"]?.Value<int>("frame_count") ?? 0;
                Assert.That(analyzedFrameCount, Is.GreaterThan(1));
                Assert.That(phases.Last().Value<int>("end_frame"), Is.EqualTo(analyzedFrameCount - 1));
                Assert.That(clipAnalysis["foot_contacts"]?.Count(), Is.GreaterThan(0));
                Assert.That(clipAnalysis["motion_profile"]?.Value<float>("path_length_xz"), Is.GreaterThan(0.5f));
                Assert.That(Math.Abs(clipAnalysis["motion_profile"]?.Value<float>("heading_change_degrees") ?? 0f),
                    Is.GreaterThan(45f));

                yield return null;

                string relativePng = analysis["pictures"]?.Value<string>("image_path");
                string absolutePng = string.IsNullOrWhiteSpace(relativePng)
                    ? string.Empty
                    : Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePng));
                Assert.That(relativePng, Is.Not.Null.And.EndsWith(".png"));
                Assert.That(relativePng.Replace('\\', '/'), Does.StartWith("Library/KimodoData/"));
                Assert.That(File.Exists(absolutePng), Is.True, "animation_analyze did not write its composite PNG.");
                Assert.That(new FileInfo(absolutePng).Length, Is.GreaterThan(0));
            }
            finally
            {
                ResetCurrentTestContext(testSession);
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        private static object CreateCurrentTestContext(GameObject character, Animator animator, AnimationClip clip)
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            Type sessionType = contextType.GetNestedType("TimelineSessionRecord", BindingFlags.NonPublic);
            Type metadataType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.KimodoCommandSessionMetadata");
            TimelineAsset timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.editorSettings.frameRate = 60.0;
            var metadata = ScriptableObject.CreateInstance(metadataType);
            string sessionName = "AnimationAnalyze_ArcWalkFixture_CurrentCommand";
            metadataType.GetField("schemaVersion").SetValue(metadata, "vNext.transition_clip.1");
            metadataType.GetField("sessionId").SetValue(metadata, Guid.NewGuid().ToString("D"));
            metadataType.GetField("sessionName").SetValue(metadata, sessionName);
            metadataType.GetField("isAutomatic").SetValue(metadata, true);
            GameObject sessionRoot = new GameObject("KimodoSession_ArcWalkFixture_CurrentCommand");
            sessionRoot.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            GameObject directorObject = new GameObject("Kimodo_CommandSession_ArcWalkFixture_CurrentCommand");
            directorObject.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
            directorObject.transform.SetParent(sessionRoot.transform, false);
            PlayableDirector director = directorObject.AddComponent<PlayableDirector>();
            director.playableAsset = timeline;

            ConstructorInfo sessionConstructor = sessionType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Guid), typeof(string), typeof(PlayableDirector), typeof(TimelineAsset), typeof(string), typeof(bool), metadataType, typeof(GameObject) },
                null);
            object session = sessionConstructor.Invoke(new object[]
            {
                Guid.NewGuid(), sessionName, director, timeline, string.Empty, true, metadata, sessionRoot
            });

            MethodInfo addCharacter = contextType.GetMethod("AddCharacterTrack", BindingFlags.Static | BindingFlags.NonPublic);
            object[] addArguments = { session, character, animator, true, null, true };
            Assert.That((bool)addCharacter.Invoke(null, addArguments), Is.True,
                "AddCharacterTrack failed: " + addArguments[4]);
            IList characters = (IList)sessionType.GetProperty("Characters").GetValue(session);
            object characterRecord = characters[0];
            MethodInfo appendClip = contextType.GetMethod("AppendAnimationClip", BindingFlags.Static | BindingFlags.NonPublic);
            appendClip.Invoke(null, new object[] { session, characterRecord, clip, "fixture", null, clip.name });

            FieldInfo currentSession = contextType.GetField("currentTimelineSession", BindingFlags.Static | BindingFlags.NonPublic);
            currentSession.SetValue(null, session);
            MethodInfo activate = contextType.GetMethod("ActivateTimelineSession", BindingFlags.Static | BindingFlags.NonPublic);
            activate.Invoke(null, new[] { session });
            return session;
        }

        private static void ResetCurrentTestContext(object session)
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            FieldInfo currentSession = contextType.GetField("currentTimelineSession", BindingFlags.Static | BindingFlags.NonPublic);
            currentSession.SetValue(null, null);
            if (session == null) return;
            Type sessionType = session.GetType();
            GameObject root = sessionType.GetProperty("SessionRoot")?.GetValue(session) as GameObject;
            TimelineAsset timeline = sessionType.GetProperty("TimelineAsset")?.GetValue(session) as TimelineAsset;
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
            if (timeline != null) UnityEngine.Object.DestroyImmediate(timeline);
        }

        private static JObject Require(string command, JObject arguments)
        {
            JObject response = JObject.Parse(command_dispatcher.Invoke(command, arguments.ToString()));
            Assert.That(response.Value<bool?>("ok"), Is.True, command + " failed: " + response["error"]);
            return response;
        }
    }
}
