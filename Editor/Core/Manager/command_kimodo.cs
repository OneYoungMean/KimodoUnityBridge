namespace KimodoUnityBridge.Command
{
    /// <summary>
    /// Kimodo generation, model, analysis, bake, and job commands.
    /// </summary>
    public static class command_kimodo
    {
        public const string ListCharactersCommand = command_context.ListCharactersCommand;
        public const string ListModelsCommand = command_context.ListModelsCommand;
        public const string HelpCommand = command_context.HelpCommand;
        public const string DebugInstallServerCommand = command_context.DebugInstallServerCommand;
        public const string GenerateAnimationAssetCommand = command_context.GenerateAnimationAssetCommand;
        public const string AnalyzeTimelineRangeCommand = command_context.KimodoAnalyzeTimelineRangeCommand;
        public const string BakeTimelineRangeCommand = command_context.KimodoBakeTimelineRangeCommand;
        public const string GetGenerationCommand = command_context.QueryGenerationCommand;
        public const string CancelGenerationCommand = command_context.QueryCancelGenerationCommand;

        public static string ListCharacters(string argumentsJson = "{}") => command_context.ListCharacters(argumentsJson);
        public static string ListModels(string argumentsJson = "{}") => command_context.ListModels(argumentsJson);
        public static string Help(string argumentsJson = "{}") => command_context.GetServerHelp(argumentsJson);
        public static string DebugInstallServer(string argumentsJson = "{}") => command_context.DebugInstallServer(argumentsJson);
        public static string GenerateAnimationAsset(string argumentsJson) => command_context.GenerateAnimationAsset(argumentsJson);
        public static string AnalyzeTimelineRange(string argumentsJson) => command_context.KimodoAnalyzeTimelineRange(argumentsJson);
        public static string BakeTimelineRange(string argumentsJson) => command_context.KimodoBakeTimelineRange(argumentsJson);
        public static string GetGeneration(string argumentsJson) => command_context.QueryGeneration(argumentsJson);
        public static string CancelGeneration(string argumentsJson) => command_context.QueryCancelGeneration(argumentsJson);
    }
}
