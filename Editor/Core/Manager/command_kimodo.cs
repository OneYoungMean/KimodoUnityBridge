namespace CharacterAnimationCli.Unity.Command
{
    /// <summary>vNext Kimodo command facade. command_dispatcher remains the public discovery endpoint.</summary>
    public static class command_kimodo
    {
        public const string HelpCommand = command_context.HelpCommand;
        public const string InstallServerCommand = command_context.InstallServerCommand;
        public const string GenerateAnimationCommand = command_context.GenerateAnimationCommand;
        public const string AnalyzeCommand = command_context.AnimationAnalyzeCommand;
        public const string CompareCommand = command_context.AnimationCompareCommand;
        public const string RecordRangeCommand = command_context.RecordRangeCommand;
        public const string RetargetAnimationCommand = command_context.RetargetAnimationCommand;
        public const string GetGenerationCommand = command_context.GetGenerationCommand;
        public const string CancelGenerationCommand = command_context.CancelGenerationCommand;

        public static string Help(string argumentsJson = "{}") => command_context.GetCommandHelp(argumentsJson);
        public static string InstallServer(string argumentsJson = "{}") => command_context.InstallServer(argumentsJson);
        public static string GenerateAnimation(string argumentsJson) => command_context.GenerateAnimationAsset(argumentsJson);
        public static string Analyze(string argumentsJson) => command_context.AnimationAnalyze(argumentsJson);
        public static string Compare(string argumentsJson) => command_context.AnimationCompare(argumentsJson);
        public static string RecordRange(string argumentsJson) => command_context.RecordRange(argumentsJson);
        public static string RetargetAnimation(string argumentsJson) => command_context.RetargetAnimation(argumentsJson);
        public static string GetGeneration(string argumentsJson) => command_context.GetGeneration(argumentsJson);
        public static string CancelGeneration(string argumentsJson) => command_context.CancelGeneration(argumentsJson);
    }
}
