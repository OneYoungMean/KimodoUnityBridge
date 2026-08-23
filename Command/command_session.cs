namespace KimodoUnityBridge.Command
{
    /// <summary>vNext Session lifecycle and content commands.</summary>
    public static class command_session
    {
        public const string GetOrCreateCommand = command_context.SessionGetOrCreateCommand;
        public const string AddCommand = command_context.SessionAddCommand;
        public const string CloseCommand = command_context.SessionCloseCommand;

        public static string GetOrCreate(string argumentsJson = "{}") => command_context.SessionGetOrCreate(argumentsJson);
        public static string Add(string argumentsJson) => command_context.SessionAdd(argumentsJson);
        public static string Close(string argumentsJson = "{}") => command_context.SessionClose(argumentsJson);
    }
}
