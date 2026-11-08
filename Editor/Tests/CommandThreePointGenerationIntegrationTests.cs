using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using KimodoBridge;
using KimodoUnityBridge;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using TimelineInject;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;
using UnityEngine.TestTools;

namespace KimodoUnityBridge.Command.Tests
{
    /// <summary>
    /// End-to-end command contract: fullbody at the start, Root2D at the
    /// midpoint, and RootEffect at the final frame.  The request is sent
    /// through command_dispatcher, rather than through the editor pipeline.
    /// </summary>
    public sealed class CommandThreePointGenerationIntegrationTests
    {
        private const string ModelPath = "Packages/com.unity.kimodo_unity_motion_tools/Editor/Model/Armature.fbx";
        private const int DurationFrames = 120;
        private const int MidFrame = 60;
        private const int EndFrame = DurationFrames - 1;

        [UnityTest, Timeout(10 * 60 * 1000)]
        public IEnumerator CommandGeneration_ThreePointConstraints_UsesDefaultPoseRoot2DAndRootEffect()
        {
            Scene fixtureScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            Assert.That(source, Is.Not.Null, "Missing Humanoid Armature fixture.");
            GameObject character = UnityEngine.Object.Instantiate(source);
            character.name = "KimodoTest_CommandThreePoint";
            // command_context resolves source characters through GlobalObjectId. A
            // transient object in an unsaved test scene has no resolvable identity.
            string fixtureFolder = "Assets/Archive/KimodoCommandTests/Fixtures";
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Archive", "KimodoCommandTests", "Fixtures"));
            string fixtureScenePath = fixtureFolder + "/CommandThreePoint_" + Guid.NewGuid().ToString("N") + ".unity";
            Assert.That(EditorSceneManager.SaveScene(fixtureScene, fixtureScenePath), Is.True,
                "Could not save the scene fixture needed for stable scene-object identity.");
            GameObject previousSelection = Selection.activeGameObject;
            Selection.activeGameObject = character;
            string requestId = null;
            try
            {
                Animator animator = character.GetComponentInChildren<Animator>(true);
                Assert.That(animator, Is.Not.Null, "Armature fixture has no Animator.");
                Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(ModelPath)
                    .OfType<Avatar>().FirstOrDefault();
                Assert.That(avatar, Is.Not.Null.And.Property("isHuman").True);
                animator.avatar = avatar;
                animator.runtimeAnimatorController = null;
                animator.Rebind();

                // First command materializes the current/default skeleton pose.
                JObject pose = Invoke("pose.get", new JObject
                {
                    ["source"] = new JObject
                    {
                        ["character"] = character.name,
                        ["mode"] = "skeleton_rest_pose"
                    },
                    ["full_data"] = true
                });
                JObject poseRef = (JObject)pose["pose"];
                Assert.That(poseRef, Is.Not.Null, pose.ToString());
                AssertGenerationResolvesCachedCharacterByNameAndId(character);

                JObject request = new JObject
                {
                    ["character"] = character.name,
                    ["prompt"] = "stand still and breathe naturally",
                    ["duration_frames"] = DurationFrames,
                    ["loop"] = false,
                    ["output"] = new JObject
                    {
                        ["folder"] = "Assets/Archive/KimodoCommandTests",
                        ["name"] = "CommandThreePoint_" + Guid.NewGuid().ToString("N")
                    },
                    ["generation"] = new JObject { ["seed"] = 42031, ["diffusion_steps"] = 20 },
                    ["constraints"] = new JArray
                    {
                        new JObject
                        {
                            ["frame"] = 0,
                            ["pose"] = poseRef.DeepClone()
                        },
                        new JObject
                        {
                            ["frame"] = MidFrame,
                            ["pose"] = poseRef.DeepClone(),
                            ["root"] = new JObject
                            {
                                ["position"] = new JArray(5.0, 0.95, 0.0),
                                ["rotation"] = new JArray(0.0, 0.0, 0.0)
                            }
                        },
                        new JObject
                        {
                            ["frame"] = EndFrame,
                            ["pose"] = poseRef.DeepClone(),
                            ["root"] = new JObject
                            {
                                ["position"] = new JArray(10.0, 0.95, 0.0),
                                ["rotation"] = new JArray(0.0, 0.0, 0.0)
                            },
                            ["effector"] = new JArray(new JObject
                            {
                                ["joint"] = "right_hand",
                                ["position"] = new JArray(10.0, 1.0, 1.0)
                            })
                        }
                    }
                };

                JObject accepted = Invoke("animation.generate", request);
                requestId = accepted.Value<string>("request_id");
                Assert.That(requestId, Is.Not.Null.And.Not.Empty, accepted.ToString());
                Assert.That(accepted["check"], Is.Not.Null, "Generation must return shared constraint diagnostics.");

                KimodoConstraintMarker[] authored = ReadCommandConstraintMarkers();
                Assert.That(authored, Has.Length.EqualTo(3), "Command should author exactly three constraint markers.");
                CollectionAssert.AreEqual(new[] { 0, MidFrame, EndFrame }, authored
                    .Select(marker => Mathf.RoundToInt((float)(marker.time * 60.0))).ToArray());
                KimodoMarkerSampleResult start = authored[0].SampleData;
                KimodoMarkerSampleResult middle = authored[1].SampleData;
                KimodoMarkerSampleResult end = authored[2].SampleData;
                Assert.That(start.constraintMode, Is.EqualTo("fullbody"));
                Assert.That(start.sampleData, Is.Not.Null.And.Property("IsValid").True,
                    "Start point must carry the pose.get default-pose sample.");
                JArray defaultRoot = (JArray)pose["data"]?["root"]?["t"];
                Assert.That(defaultRoot, Is.Not.Null, pose.ToString());
                Assert.That(start.rootOverride.t.x, Is.EqualTo(defaultRoot[0].Value<float>()).Within(0.001f));
                Assert.That(start.rootOverride.t.y, Is.EqualTo(defaultRoot[1].Value<float>()).Within(0.001f));
                Assert.That(start.rootOverride.t.z, Is.EqualTo(defaultRoot[2].Value<float>()).Within(0.001f));
                Assert.That(middle.constraintMode, Is.EqualTo("fullbody"));
                Assert.That(middle.rootOverride.t.x, Is.EqualTo(5f).Within(0.001f));
                Assert.That(middle.rootOverride.t.y, Is.EqualTo(0.95f).Within(0.001f),
                    "The explicit World root position includes and overrides height.");
                Assert.That(end.constraintMode, Is.EqualTo("fullbody"));
                Assert.That(end.rootOverride.t.x, Is.EqualTo(10f).Within(0.001f));
                Assert.That(end.rootOverride.t.y, Is.EqualTo(0.95f).Within(0.001f));
                Assert.That(end.effectors.rightHand.t, Is.EqualTo(new Vector3(10f, 1f, 1f)));

                // Verify the command-side export before waiting on the backend.
                JObject diagnostics = (JObject)accepted["check"];
                Assert.That(diagnostics, Is.Not.Null);
                Assert.That(diagnostics.Value<bool?>("blocking") ?? false, Is.False, diagnostics.ToString());

                JObject status = null;
                DateTime deadline = DateTime.UtcNow.AddMinutes(10);
                while (DateTime.UtcNow < deadline)
                {
                    status = Invoke("generation.get", new JObject { ["request_id"] = requestId });
                    string state = status.Value<string>("status")?.ToLowerInvariant();
                    if (state == "completed" || state == "failed" || state == "canceled") break;
                    yield return new WaitForSeconds(1f);
                }

                Assert.That(status, Is.Not.Null);
                Assert.That(status.Value<string>("status"), Is.EqualTo("completed"), status.ToString());
                JObject constraintLoss = status["constraint_loss"] as JObject;
                Assert.That(constraintLoss, Is.Not.Null, status.ToString());
                Assert.That(constraintLoss.Value<int>("compared_count"), Is.GreaterThan(0), constraintLoss.ToString());
                Assert.That(constraintLoss["frames"].Values<JObject>().Any(frame =>
                    Mathf.Abs(frame.Value<float>("timeline_time_seconds") - MidFrame / 60f) < 0.001f &&
                    frame["constraint_hips_world"]?.Values<float>().Count() == 3 &&
                    Mathf.Abs(frame["constraint_hips_world"][0].Value<float>() - 5f) < 0.001f &&
                    Mathf.Abs(frame["constraint_hips_world"][1].Value<float>() - 0.95f) < 0.001f),
                    Is.True,
                    "Returned constraint loss should include the middle generated frame with its explicit World root position.");
                AssertGenerationResolvesCachedCharacterByNameAndId(character);
                string path = status["clip"]?.Value<string>("asset_path");
                Assert.That(path, Is.Not.Null.And.Not.Empty, status.ToString());
                Assert.That(AssetDatabase.LoadAssetAtPath<AnimationClip>(path), Is.Not.Null,
                    $"Generation completed but output Clip was not found at '{path}'.");
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    // Cancellation is best-effort and only applies while running.
                    JObject status = Invoke("generation.get", new JObject { ["request_id"] = requestId });
                    string state = status.Value<string>("status")?.ToLowerInvariant();
                    if (state != "completed" && state != "failed" && state != "canceled")
                    {
                        Invoke("generation.cancel", new JObject
                        { ["request_id"] = requestId, ["reason"] = "test cleanup" });
                    }
                }
                Type contextType = typeof(command_dispatcher).Assembly
                    .GetType("KimodoUnityBridge.Command.command_context");
                object session = CurrentSessionProjection();
                contextType.GetMethod("ClearRuntimeSession", BindingFlags.Static | BindingFlags.NonPublic)
                    ?.Invoke(null, new[] { session });
                Selection.activeGameObject = previousSelection;
                UnityEngine.Object.DestroyImmediate(character);
            }
        }

        private static JObject Invoke(string command, JObject arguments)
        {
            JObject response = JObject.Parse(command_dispatcher.Invoke(command, arguments.ToString()));
            Assert.That(response.Value<bool?>("ok"), Is.True, $"{command} failed: {response["error"]}");
            return response;
        }

        private static KimodoConstraintMarker[] ReadCommandConstraintMarkers()
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            object session = CurrentSessionProjection();
            Assert.That(session, Is.Not.Null, "Command did not create a Timeline session.");
            object character = ((System.Collections.IEnumerable)session.GetType()
                    .GetProperty("Characters").GetValue(session))
                .Cast<object>().Single();
            var track = character.GetType().GetProperty("Track").GetValue(character) as UnityEngine.Timeline.TrackAsset;
            Assert.That(track, Is.Not.Null);
            return track.GetMarkers().OfType<KimodoConstraintMarker>()
                .Where(marker => marker.constraintEnabled && marker.ParticipatesInGeneration)
                .OrderBy(marker => marker.time).ToArray();
        }

        private static object CurrentSessionProjection()
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            MethodInfo buildView = contextType.GetMethod("BuildTimelineSessionView", BindingFlags.Static | BindingFlags.NonPublic);
            return buildView.Invoke(null, new object[] { SceneManager.GetActiveScene() });
        }

        private static void AssertGenerationResolvesCachedCharacterByNameAndId(GameObject source)
        {
            Type contextType = typeof(command_dispatcher).Assembly
                .GetType("KimodoUnityBridge.Command.command_context");
            object session = CurrentSessionProjection();
            MethodInfo resolve = contextType.GetMethod("ResolveCharacter", BindingFlags.Static | BindingFlags.NonPublic);
            object byName = resolve.Invoke(null, new object[] { session, source.name });
            string sourceId = GlobalObjectId.GetGlobalObjectIdSlow(source).ToString();
            object byId = resolve.Invoke(null, new object[] { session, sourceId });
            GameObject nameRoot = (GameObject)byName.GetType().GetProperty("Root").GetValue(byName);
            GameObject idRoot = (GameObject)byId.GetType().GetProperty("Root").GetValue(byId);
            Assert.That(idRoot, Is.SameAs(nameRoot), "Name and stable source id must resolve the same cached Ghost.");
            object characters = session.GetType().GetProperty("Characters").GetValue(session);
            Assert.That(((System.Collections.ICollection)characters).Count, Is.EqualTo(1),
                "Resolving a known source identity must not create another character clone.");
        }

    }
}
