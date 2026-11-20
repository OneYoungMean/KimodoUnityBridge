using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using KimodoBridge.Editor;
using KimodoBridge;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private static JObject BuildGenerationCheck(
            JObject arguments,
            TimelineCharacterRecord character,
            int durationFrames,
            float frameRate,
            string modelName,
            IReadOnlyList<KimodoMarkerSampleResult> generatedSamples)
        {
            return KimodoGenerationWarningCheckService.Build(
                arguments,
                durationFrames,
                frameRate,
                character.Name,
                modelName,
                generatedSamples,
                blockRootMotionLimits: true);
        }
    }
}


