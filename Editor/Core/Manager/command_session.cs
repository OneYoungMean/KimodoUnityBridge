namespace KimodoUnityBridge.Command
{
    /// <summary>
    /// Timeline Session lifecycle and editing commands.
    /// </summary>
    public static class command_session
    {
        public const string OpenTimelineCommand = command_context.SessionOpenTimelineCommand;
        public const string CloseTimelineCommand = command_context.SessionCloseTimelineCommand;
        public const string LocateAnimationCommand = command_context.SessionLocateAnimationCommand;
        public const string SamplePoseCommand = command_context.SessionSamplePoseCommand;
        public const string TryAddCommand = command_context.SessionTryAddCommand;
        public const string TryRemoveCommand = command_context.SessionTryRemoveCommand;

        public static string OpenTimeline(string argumentsJson = "{}") => command_context.SessionOpenTimeline(argumentsJson);
        public static string CloseTimeline(string argumentsJson = "{}") => command_context.SessionCloseTimeline(argumentsJson);
        public static string LocateAnimation(string argumentsJson) => command_context.SessionLocateAnimation(argumentsJson);
        public static string SamplePose(string argumentsJson) => command_context.SessionSamplePose(argumentsJson);
        public static string TryAdd(string argumentsJson) => command_context.SessionTryAdd(argumentsJson);
        public static string TryRemove(string argumentsJson) => command_context.SessionTryRemove(argumentsJson);
    }
}
