namespace KimodoUnityBridge.Command
{
    [System.Obsolete("Use CharacterAnimationCli.Unity.Command.command_dispatcher.")]
    public static class command_dispatcher
    {
        public static string GetCommandDefinitionsJson() =>
            CharacterAnimationCli.Unity.Command.command_dispatcher.GetCommandDefinitionsJson();

        public static string Invoke(string commandName, string argumentsJson = "{}") =>
            CharacterAnimationCli.Unity.Command.command_dispatcher.Invoke(commandName, argumentsJson);
    }
}
