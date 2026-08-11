namespace CharacterAnimationCli.Unity.Command
{
    /// <summary>
    /// Read-only queries over the current Kimodo editing environment.
    /// </summary>
    public static class command_query
    {
        public const string CurrentSessionCommand = command_context.QueryCurrentSessionCommand;

        public static string CurrentSession(string argumentsJson = "{}")
        {
            return command_context.QueryCurrentSession(argumentsJson);
        }
    }
}
