namespace CharacterAnimationCli.Unity.Command
{
    /// <summary>
    /// Framework-neutral command discovery and dispatch entry point.
    /// </summary>
    public static class command_dispatcher
    {
        public static string GetCommandDefinitionsJson()
        {
            return command_context.GetCommandDefinitionsJson();
        }

        public static string Invoke(string commandName, string argumentsJson = "{}")
        {
            return command_context.Invoke(commandName, argumentsJson);
        }
    }
}
