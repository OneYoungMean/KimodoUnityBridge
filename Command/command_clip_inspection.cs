using Newtonsoft.Json.Linq;
using System;
using KimodoBridge.Editor;
using UnityEngine;

namespace KimodoUnityBridge.Command
{
    internal static partial class command_context
    {
        private static string ClipInspect(JObject arguments)
        {
            AnimationClip clip = ResolveAnimationClip(RequiredStringValue(arguments, "clip"));
            return Ok(AnimationClipInspector.InspectJson(clip));
        }

        private static string ClipSettingsSet(JObject arguments)
        {
            AnimationClip clip = ResolveAnimationClip(RequiredStringValue(arguments, "clip"));
            JObject settings = arguments["settings"] as JObject;
            if (settings == null)
                throw new InvalidOperationException("settings is required and must be an object.");
            return Ok(AnimationClipInspector.SetSettingsJson(clip, settings));
        }
    }
}
