using CharacterAnimationCli.Unity;
using CharacterAnimationCli.Unity.Command;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Playables;

namespace KimodoBridge.Editor.Tests
{
    public sealed class CommandSessionIntegrationTests
    {
        private const string TPoseGuid = "46a6f19ca1178c4478955aa3a75f397a";

        private string sessionName;
        private string timelineAssetPath;
        private GameObject character;
        private bool sessionOpen;
        private readonly List<GameObject> extraSceneObjects = new List<GameObject>();
        private readonly List<string> createdAssetPaths = new List<string>();

        [SetUp]
        public void SetUp()
        {
            sessionName = $"CommandSessionTest_{Guid.NewGuid():N}";
            JObject opened = Invoke(command_session.OpenCommand, new JObject { ["session_name"] = sessionName });
            Assert.That(opened.Value<bool>("ok"), Is.True, opened.ToString());
            sessionOpen = true;

            PlayableDirector director = Resources.FindObjectsOfTypeAll<PlayableDirector>()
                .Single(item => item.name == $"Kimodo_CommandSession_{sessionName}");
            timelineAssetPath = AssetDatabase.GetAssetPath(director.playableAsset);

            string modelPath = AssetDatabase.GUIDToAssetPath(TPoseGuid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            Assert.That(prefab, Is.Not.Null, $"T-Pose model was not found for GUID {TPoseGuid}.");
            character = UnityEngine.Object.Instantiate(prefab);
            Animator animator = character.GetComponentInChildren<Animator>(true);
            Assert.That(animator, Is.Not.Null);
            Assert.That(animator.avatar, Is.Not.Null);
            Assert.That(animator.avatar.isHuman, Is.True);
            animator.gameObject.name = $"TPose_{Guid.NewGuid():N}";
        }

        [TearDown]
        public void TearDown()
        {
            if (sessionOpen)
            {
                command_dispatcher.Invoke(command_session.CloseCommand);
                sessionOpen = false;
            }
            if (character != null)
            {
                UnityEngine.Object.DestroyImmediate(character);
            }
            foreach (GameObject item in extraSceneObjects.Where(item => item != null))
            {
                UnityEngine.Object.DestroyImmediate(item);
            }
            foreach (PlayableDirector director in Resources.FindObjectsOfTypeAll<PlayableDirector>()
                         .Where(item => item != null && item.name == $"Kimodo_CommandSession_{sessionName}"))
            {
                UnityEngine.Object.DestroyImmediate(director.gameObject);
            }
            if (!string.IsNullOrEmpty(timelineAssetPath))
            {
                AssetDatabase.DeleteAsset(timelineAssetPath);
            }
            foreach (string path in createdAssetPaths.Where(path => !string.IsNullOrEmpty(path)).Distinct())
            {
                AssetDatabase.DeleteAsset(path);
            }
            AssetDatabase.SaveAssets();
        }

        [Test]
        public void SessionCommands_AddQueryCloseAndReopenTPoseCharacter()
        {
            JObject added = AddCharacter();
            string safeName = added["character"].Value<string>("name");

            Assert.That(QueryCharacters(), Does.Contain(safeName));

            JObject closed = Invoke(command_session.CloseCommand);
            Assert.That(closed.Value<bool>("ok"), Is.True, closed.ToString());
            sessionOpen = false;

            JObject reopened = Invoke(command_session.OpenCommand, new JObject { ["session_name"] = sessionName });
            Assert.That(reopened.Value<bool>("ok"), Is.True, reopened.ToString());
            Assert.That(reopened.Value<string>("session"), Is.EqualTo(sessionName));
            sessionOpen = true;

            Assert.That(QueryCharacters(), Does.Contain(safeName));
        }

        [Test]
        public void SessionCommands_RemoveTPoseCharacterUpdatesQuery()
        {
            string safeName = AddCharacter()["character"].Value<string>("name");

            JObject removed = Invoke(command_session.TryRemoveCommand, new JObject
            {
                ["kind"] = "character",
                ["character"] = safeName
            });

            Assert.That(removed.Value<bool>("ok"), Is.True, removed.ToString());
            Assert.That(QueryCharacters(), Does.Not.Contain(safeName));
        }

        [Test]
        public void PoseCommands_CreateGetSetAndCopyWritablePose()
        {
            string safeName = AddCharacter()["character"].Value<string>("name");
            JObject created = Invoke(command_context.PoseCreateCommand, new JObject
            {
                ["character"] = safeName,
                ["pose"] = CharacterPoseJson.ToJson(new CharacterPose())
            });
            Assert.That(created.Value<bool>("ok"), Is.True, created.ToString());
            JObject locator = (JObject)created["pose"];

            JObject read = Invoke(command_context.PoseGetCommand, new JObject { ["pose"] = locator.DeepClone() });
            Assert.That(read.Value<bool>("ok"), Is.True, read.ToString());
            Assert.That(read["data"]["muscles"], Has.Count.EqualTo(CharacterPose.MuscleCount));

            JObject updated = Invoke(command_context.PoseSetCommand, new JObject
            {
                ["pose"] = locator.DeepClone(),
                ["data"] = new JObject { ["root"] = new JObject { ["t"] = new JArray(0.25f, 0f, 0f) } }
            });
            Assert.That(updated.Value<bool>("ok"), Is.True, updated.ToString());
            Assert.That(updated["data"]["root"]["t"][0].Value<float>(), Is.EqualTo(0.25f));

            JObject copied = Invoke(command_context.PoseCopyCommand, new JObject
            {
                ["character"] = safeName,
                ["pose"] = locator.DeepClone()
            });
            Assert.That(copied.Value<bool>("ok"), Is.True, copied.ToString());
            Assert.That(copied["pose"].Value<int>("frame"), Is.Not.EqualTo(locator.Value<int>("frame")));
        }

        [Test]
        public void BuildRoot2DPathCommand_ReturnsSixtyFpsLine()
        {
            JObject response = Invoke(command_context.BuildRoot2DPathCommand, new JObject
            {
                ["shape"] = "line",
                ["duration_frames"] = 60
            });

            Assert.That(response.Value<bool>("ok"), Is.True, response.ToString());
            Assert.That(response.Value<int>("fps"), Is.EqualTo(60));
            Assert.That(response["points"], Has.Count.EqualTo(2));
        }

        [Test]
        public void HistoricalPoseWorkflow_SamplesCopiesAndEditsTimelinePose()
        {
            string safeName = AddCharacter()["character"].Value<string>("name");
            AddSampleClip(safeName);
            JObject sampled = Invoke(command_context.PoseGetCommand, new JObject
            {
                ["pose"] = new JObject { ["source"] = safeName, ["frame"] = 0 }
            });
            Assert.That(sampled.Value<bool>("ok"), Is.True, sampled.ToString());

            JObject copied = Invoke(command_context.PoseCopyCommand, new JObject
            {
                ["character"] = safeName,
                ["pose"] = new JObject { ["source"] = safeName, ["frame"] = 0 }
            });
            Assert.That(copied.Value<bool>("ok"), Is.True, copied.ToString());
            JObject writable = (JObject)copied["pose"];
            Assert.That(writable.Value<string>("source"), Does.EndWith(".Poses"));

            JObject edited = Invoke(command_context.PoseSetCommand, new JObject
            {
                ["pose"] = writable.DeepClone(),
                ["data"] = new JObject { ["hands"] = new JObject
                {
                    ["left"] = new JObject { ["t"] = new JArray(0.01f, 0.02f, 0.03f) }
                } }
            });
            Assert.That(edited.Value<bool>("ok"), Is.True, edited.ToString());
            Assert.That(edited["data"]["hands"]["left"]["t"].Values<float>(),
                Is.EqualTo(new[] { 0.01f, 0.02f, 0.03f }));
        }

        [Test]
        public void HistoricalBakeWorkflow_BakesQueriesAndRemovesAnimation()
        {
            string safeName = AddCharacter()["character"].Value<string>("name");
            string requestedName = $"CommandBake_{Guid.NewGuid():N}";
            JObject baked = Invoke(command_kimodo.BakeRangeCommand, new JObject
            {
                ["character"] = safeName,
                ["start_frame"] = 0,
                ["end_frame"] = 2,
                ["name"] = requestedName,
                ["output_folder"] = "Assets/KimodoGeneratedClips"
            });
            Assert.That(baked.Value<bool>("ok"), Is.True, baked.ToString());
            string animation = baked["animation"].Value<string>("name");
            string outputPath = AssetDatabase.FindAssets($"{animation} t:AnimationClip", new[] { "Assets/KimodoGeneratedClips" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Single(path => Path.GetFileNameWithoutExtension(path) == animation);
            createdAssetPaths.Add(outputPath);

            JObject animations = Invoke(command_query.CurrentSessionCommand, new JObject
            {
                ["query"] = "character_animations", ["character"] = safeName
            });
            Assert.That(animations["animations"].Values<string>("name"), Does.Contain(animation));

            JObject queried = Invoke(command_query.CurrentSessionCommand, new JObject
            {
                ["query"] = "animation", ["character"] = safeName, ["animation"] = animation
            });
            Assert.That(queried.Value<bool>("ok"), Is.True, queried.ToString());
            Assert.That(queried["animation"].Value<int>("duration_frames"), Is.GreaterThan(0));

            JObject removed = Invoke(command_session.TryRemoveCommand, new JObject
            {
                ["kind"] = "clip", ["character"] = safeName, ["animation"] = animation
            });
            Assert.That(removed.Value<bool>("ok"), Is.True, removed.ToString());
            Assert.That(QueryAnimations(safeName), Does.Not.Contain(animation));
        }

        [Test]
        public void HistoricalConstraintWorkflow_ComposesPoseAndRoot2DWithoutStartingGeneration()
        {
            string safeName = AddCharacter()["character"].Value<string>("name");
            AddSampleClip(safeName);
            JObject copied = Invoke(command_context.PoseCopyCommand, new JObject
            {
                ["character"] = safeName,
                ["pose"] = new JObject { ["source"] = safeName, ["frame"] = 0 }
            });
            JObject path = Invoke(command_context.BuildRoot2DPathCommand, new JObject
            {
                ["shape"] = "turn", ["duration_frames"] = 60, ["turn_degrees"] = 90
            });
            JObject endpoint = (JObject)path["points"].Last;

            JObject response = Invoke(command_kimodo.GenerateAnimationCommand, new JObject
            {
                ["character"] = safeName,
                ["prompt"] = "turn left",
                ["duration_frames"] = 60,
                ["constraints"] = new JArray
                {
                    new JObject { ["frame"] = 0, ["type"] = "fullbody", ["pose"] = copied["pose"].DeepClone() },
                    new JObject
                    {
                        ["frame"] = endpoint.Value<int>("frame"), ["type"] = "root2d",
                        ["position"] = endpoint["position"].DeepClone(), ["heading"] = endpoint["heading"].DeepClone()
                    },
                    new JObject { ["frame"] = 1, ["type"] = "unsupported" }
                }
            });

            Assert.That(response.Value<bool>("ok"), Is.False);
            Assert.That(response.Value<string>("error"), Does.Contain("constraints[2].type"));
        }

        [Test]
        public void HistoricalBlendTreeWorkflow_ImportsCandidatesAndLeavesBranchChoiceExplicit()
        {
            string safeName = AddCharacter()["character"].Value<string>("name");
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                AssetDatabase.GUIDToAssetPath("834c224ed4f14ad40811f5f161fbe870"));
            Assert.That(clip, Is.Not.Null, "Editor/Model/MuscleClip.anim is required by the workflow test.");

            string controllerPath = AssetDatabase.GenerateUniqueAssetPath(
                $"Assets/KimodoGeneratedClips/CommandAnimator_{Guid.NewGuid():N}.controller");
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            createdAssetPaths.Add(controllerPath);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            AnimatorStateMachine machine = controller.layers[0].stateMachine;
            AnimatorState blendState = machine.AddState("Locomotion");
            AnimatorState endState = machine.AddState("End");
            var tree = new BlendTree { name = "Locomotion", blendParameter = "Speed" };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(clip, 0f);
            tree.AddChild(clip, 1f);
            blendState.motion = tree;
            endState.motion = clip;
            blendState.AddTransition(endState);
            AssetDatabase.SaveAssets();

            GameObject source = UnityEngine.Object.Instantiate(
                AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(TPoseGuid)));
            extraSceneObjects.Add(source);
            Animator sourceAnimator = source.GetComponentInChildren<Animator>(true);
            sourceAnimator.gameObject.name = $"BlendTreeSource_{Guid.NewGuid():N}";
            sourceAnimator.runtimeAnimatorController = controller;

            JObject imported = Invoke(command_session.TryAddCommand, new JObject
            {
                ["kind"] = "animator",
                ["character"] = safeName,
                ["animator"] = sourceAnimator.gameObject.name
            });

            Assert.That(imported.Value<bool>("ok"), Is.True, imported.ToString());
            Assert.That(imported["animations"], Has.Count.EqualTo(3));
            Assert.That(imported["transitions"], Is.Empty);
            Assert.That(imported["skipped"].Values<string>("kind"), Does.Contain("blend_tree_transition"));
            Assert.That(imported["transition_analysis"], Has.Count.EqualTo(1));
            JObject plan = (JObject)imported["transition_analysis"][0];
            Assert.That(plan.Value<string>("from_motion"), Is.EqualTo("blend_tree"));
            Assert.That(plan.Value<string>("to_motion"), Is.EqualTo("clip"));
            Assert.That(plan.Value<long>("candidate_case_count"), Is.EqualTo(2));
            JObject analysis = Invoke(command_session.AnalyzeTransitionsCommand, new JObject
            {
                ["character"] = safeName,
                ["animator"] = imported.Value<string>("animator")
            });
            Assert.That(analysis.Value<bool>("ok"), Is.True, analysis.ToString());
            Assert.That(analysis["animators"][0]["transitions"], Has.Count.EqualTo(1));
            Assert.That(QueryAnimations(safeName), Has.Length.GreaterThanOrEqualTo(3));
        }

        [TestCase(command_kimodo.DebugInstallServerCommand, "Error reading JObject")]
        [TestCase(command_kimodo.AnalyzeCommand, "Provide exactly one analysis source")]
        [TestCase(command_kimodo.BakeRangeCommand, "bake range must satisfy")]
        [TestCase(command_kimodo.GenerateAnimationCommand, "duration_frames must be a positive integer")]
        [TestCase(command_kimodo.QueryPictureCommand, "Provide exactly one of poses")]
        [TestCase(command_kimodo.GetGenerationCommand, "Unknown or expired request_id")]
        [TestCase(command_kimodo.CancelGenerationCommand, "Unknown or expired request_id")]
        public void GuardedCommands_ReachTheirHandlerWithoutExternalSideEffects(string command, string expectedError)
        {
            JObject arguments = new JObject();
            if (command == command_kimodo.AnalyzeCommand || command == command_kimodo.BakeRangeCommand ||
                command == command_kimodo.GenerateAnimationCommand)
            {
                arguments["character"] = AddCharacter()["character"].Value<string>("name");
            }
            if (command == command_kimodo.BakeRangeCommand)
            {
                arguments["start_frame"] = 1;
                arguments["end_frame"] = 1;
            }
            if (command == command_kimodo.GenerateAnimationCommand)
            {
                arguments["prompt"] = "walk";
                arguments["duration_frames"] = 0;
            }
            if (command == command_kimodo.GetGenerationCommand || command == command_kimodo.CancelGenerationCommand)
            {
                arguments["request_id"] = Guid.NewGuid().ToString("D");
            }

            JObject response = command == command_kimodo.DebugInstallServerCommand
                ? JObject.Parse(command_dispatcher.Invoke(command, "{"))
                : Invoke(command, arguments);
            Assert.That(response.Value<bool>("ok"), Is.False, response.ToString());
            Assert.That(response.Value<string>("error"), Does.Contain(expectedError));
        }

        private JObject AddCharacter()
        {
            Animator animator = character.GetComponentInChildren<Animator>(true);
            JObject added = Invoke(command_session.TryAddCommand, new JObject
            {
                ["kind"] = "character",
                ["character"] = animator.gameObject.name
            });
            Assert.That(added.Value<bool>("ok"), Is.True, added.ToString());
            return added;
        }

        private string AddSampleClip(string safeName)
        {
            AnimationClip source = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                AssetDatabase.GUIDToAssetPath("834c224ed4f14ad40811f5f161fbe870"));
            string clipName = $"CommandSample_{Guid.NewGuid():N}";
            string clipPath = $"Assets/KimodoGeneratedClips/{clipName}.anim";
            AnimationClip copy = UnityEngine.Object.Instantiate(source);
            copy.name = clipName;
            AssetDatabase.CreateAsset(copy, clipPath);
            createdAssetPaths.Add(clipPath);
            JObject added = Invoke(command_session.TryAddCommand, new JObject
            {
                ["kind"] = "clip", ["character"] = safeName, ["clip"] = clipName
            });
            Assert.That(added.Value<bool>("ok"), Is.True, added.ToString());
            return added["animation"].Value<string>("name");
        }

        private static string[] QueryCharacters()
        {
            JObject response = Invoke(command_query.CurrentSessionCommand, new JObject { ["query"] = "characters" });
            Assert.That(response.Value<bool>("ok"), Is.True, response.ToString());
            return response["characters"].Values<string>().ToArray();
        }

        private static string[] QueryAnimations(string characterName)
        {
            JObject response = Invoke(command_query.CurrentSessionCommand, new JObject
            {
                ["query"] = "character_animations", ["character"] = characterName
            });
            Assert.That(response.Value<bool>("ok"), Is.True, response.ToString());
            return response["animations"].Values<string>("name").ToArray();
        }

        private static JObject Invoke(string command, JObject arguments = null) =>
            JObject.Parse(command_dispatcher.Invoke(command, (arguments ?? new JObject()).ToString()));
    }
}
