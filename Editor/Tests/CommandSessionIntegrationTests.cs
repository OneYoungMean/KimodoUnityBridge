using CharacterAnimationCli.Unity.Command;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System;
using System.Linq;
using UnityEditor;
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
            foreach (PlayableDirector director in Resources.FindObjectsOfTypeAll<PlayableDirector>()
                         .Where(item => item != null && item.name == $"Kimodo_CommandSession_{sessionName}"))
            {
                UnityEngine.Object.DestroyImmediate(director.gameObject);
            }
            if (!string.IsNullOrEmpty(timelineAssetPath))
            {
                AssetDatabase.DeleteAsset(timelineAssetPath);
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

        private static string[] QueryCharacters()
        {
            JObject response = Invoke(command_query.CurrentSessionCommand, new JObject { ["query"] = "characters" });
            Assert.That(response.Value<bool>("ok"), Is.True, response.ToString());
            return response["characters"].Values<string>().ToArray();
        }

        private static JObject Invoke(string command, JObject arguments = null) =>
            JObject.Parse(command_dispatcher.Invoke(command, (arguments ?? new JObject()).ToString()));
    }
}
