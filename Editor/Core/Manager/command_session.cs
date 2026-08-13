namespace CharacterAnimationCli.Unity.Command
{
    /// <summary>
    /// Timeline Session lifecycle and editing commands.
    /// </summary>
    public static class command_session
    {
        public const string OpenCommand = command_context.SessionOpenCommand;
        public const string CloseCommand = command_context.SessionCloseCommand;
        public const string TryAddCommand = command_context.SessionTryAddCommand;
        public const string AnalyzeTransitionsCommand = command_context.SessionAnalyzeTransitionsCommand;
        public const string TryRemoveCommand = command_context.SessionTryRemoveCommand;

        public static string OpenTimeline(string argumentsJson = "{}") => command_context.SessionOpenTimeline(argumentsJson);
        public static string CloseTimeline(string argumentsJson = "{}") => command_context.SessionCloseTimeline(argumentsJson);
        public static string TryAdd(string argumentsJson) => command_context.SessionTryAdd(argumentsJson);
        public static string AnalyzeTransitions(string argumentsJson) => command_context.AnalyzeSessionTransitions(argumentsJson);
        public static string TryRemove(string argumentsJson) => command_context.SessionTryRemove(argumentsJson);
    }
}
