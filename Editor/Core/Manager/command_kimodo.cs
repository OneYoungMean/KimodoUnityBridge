namespace KimodoUnityBridge.Command
{
    /// <summary>
    /// Kimodo generation, model, analysis, recording, retargeting, and job commands.
    /// </summary>
    public static class command_kimodo
    {
        public const string HelpCommand = command_context.HelpCommand;
        public const string DebugInstallServerCommand = command_context.DebugInstallServerCommand;
        public const string GenerateAnimationCommand = command_context.GenerateAnimationCommand;
        public const string AnalyzeCommand = command_context.KimodoAnalyzeCommand;
        public const string RecordRangeCommand = command_context.KimodoRecordRangeCommand;
        public const string RetargetAnimationCommand = command_context.KimodoRetargetAnimationCommand;
        public const string QueryPictureCommand = command_context.QueryPictureCommand;
        public const string GetGenerationCommand = command_context.QueryGenerationCommand;
        public const string CancelGenerationCommand = command_context.QueryCancelGenerationCommand;

        public static string Help(string argumentsJson = "{}") => command_context.GetCommandHelp(argumentsJson);
        public static string DebugInstallServer(string argumentsJson = "{}") => command_context.DebugInstallServer(argumentsJson);
        public static string GenerateAnimation(string argumentsJson) => command_context.GenerateAnimationAsset(argumentsJson);
        public static string Analyze(string argumentsJson) => command_context.KimodoAnalyzeTimelineRange(argumentsJson);
        public static string RecordRange(string argumentsJson) => command_context.KimodoRecordTimelineRange(argumentsJson);
        public static string RetargetAnimation(string argumentsJson) => command_context.KimodoRetargetAnimation(argumentsJson);
        public static string QueryPicture(string argumentsJson) => command_context.Capture(argumentsJson);
        public static string GetGeneration(string argumentsJson) => command_context.QueryGeneration(argumentsJson);
        public static string CancelGeneration(string argumentsJson) => command_context.QueryCancelGeneration(argumentsJson);
    }
}
