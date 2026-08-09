namespace KimodoUnityBridge.Command
{
    /// <summary>
    /// Kimodo generation, model, analysis, bake, and job commands.
    /// </summary>
    public static class command_kimodo
    {
        public const string HelpCommand = command_context.HelpCommand;
        public const string DebugInstallServerCommand = command_context.DebugInstallServerCommand;
        public const string GenerateAnimationAssetCommand = command_context.GenerateAnimationAssetCommand;
        public const string AnalyzeRangeCommand = command_context.KimodoAnalyzeRangeCommand;
        public const string BakeRangeCommand = command_context.KimodoBakeRangeCommand;
        public const string RenderPoseSheetCommand = command_context.KimodoRenderPoseSheetCommand;
        public const string RenderAnalysisSheetCommand = command_context.KimodoRenderAnalysisSheetCommand;
        public const string GetGenerationCommand = command_context.QueryGenerationCommand;
        public const string CancelGenerationCommand = command_context.QueryCancelGenerationCommand;

        public static string Help(string argumentsJson = "{}") => command_context.GetCommandHelp(argumentsJson);
        public static string DebugInstallServer(string argumentsJson = "{}") => command_context.DebugInstallServer(argumentsJson);
        public static string GenerateAnimationAsset(string argumentsJson) => command_context.GenerateAnimationAsset(argumentsJson);
        public static string AnalyzeRange(string argumentsJson) => command_context.KimodoAnalyzeTimelineRange(argumentsJson);
        public static string BakeRange(string argumentsJson) => command_context.KimodoBakeTimelineRange(argumentsJson);
        public static string RenderPoseSheet(string argumentsJson) => command_context.RenderPoseSheet(argumentsJson);
        public static string RenderAnalysisSheet(string argumentsJson) => command_context.RenderAnalysisSheet(argumentsJson);
        public static string GetGeneration(string argumentsJson) => command_context.QueryGeneration(argumentsJson);
        public static string CancelGeneration(string argumentsJson) => command_context.QueryCancelGeneration(argumentsJson);
    }
}
