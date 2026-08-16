using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CharacterAnimationCli.Unity.Command.Tests
{
    public sealed class CommandVNextTests
    {
        [Test]
        public void CommandDefinitions_ExposeOnlyTheVNextSurface()
        {
            JObject json = JObject.Parse(command_dispatcher.GetCommandDefinitionsJson());
            string[] names = json["tools"].Values<JObject>().Select(value => value.Value<string>("name")).ToArray();
            CollectionAssert.AreEquivalent(new[]
            {
                "kimodo_help", "kimodo_install_server",
                "session_get_or_create", "session_add", "session_close",
                "kimodo_generate_animation", "kimodo_get_generation", "kimodo_cancel_generation",
                "animation_analyze", "animation_compare",
                "pose_get", "pose_contract", "pose_set_root_transform", "pose_set_muscle",
                "picture_motion_overlay", "picture_key_poses", "picture_trajectory_3d",
                "kimodo_record_range", "kimodo_retarget_animation"
            }, names);
        }

        [Test]
        public void UnknownCommand_UsesTheVNextFailureEnvelope()
        {
            JObject response = JObject.Parse(command_dispatcher.Invoke("pose_copy", "{}"));
            Assert.That(response.Value<bool>("ok"), Is.False);
            Assert.That(response["error"]?.Value<string>("code"), Is.EqualTo("unknown_command"));
        }
    }
}
