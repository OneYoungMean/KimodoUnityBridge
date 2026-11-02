using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KimodoBridge.Editor
{
    /// <summary>
    /// Compatibility adapter for the command and generation result mappers.
    /// All Clip facts and writes are implemented by AnimationClipInspector.
    /// </summary>
    internal static class KimodoClipInspector
    {
        internal static JObject Inspect(AnimationClip clip) => AnimationClipInspector.InspectJson(clip);
        internal static JObject Inspect(AnimationClip clip, bool includeConsumers) => AnimationClipInspector.InspectJson(clip, includeConsumers);
        internal static JObject SetSettings(AnimationClip clip, JObject requested) => AnimationClipInspector.SetSettingsJson(clip, requested);
    }
}
