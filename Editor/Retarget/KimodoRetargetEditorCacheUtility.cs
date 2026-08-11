using System;
using UnityEditor;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoRetargetEditorCacheUtility
    {
        internal const string MuscleCacheType = "muscle";
        internal const string BoneCacheType = "bone";

        internal static bool ClipHasContent(AnimationClip clip)
        {
            if (clip == null)
            {
                return false;
            }
            return clip.length > 0;

            //return AnimationUtility.GetCurveBindings(clip).Length > 0 ||
              //  AnimationUtility.GetObjectReferenceCurveBindings(clip).Length > 0;
        }

        internal static bool TryLoadStrictNamedCache(
            string cacheName,
            out AnimationClip cachedClip,
            out float frameRate,
            out string error)
        {
            cachedClip = null;
            frameRate = 0f;
            error = string.Empty;

            if (!KimodoEditorClipWritebackService.TryLoadNamedClipCache(cacheName, out cachedClip, out error))
            {
                error = string.Empty;
                return false;
            }

            if (!ClipHasContent(cachedClip))
            {
                cachedClip = null;
                return false;
            }

            frameRate = cachedClip.frameRate > 0f ? cachedClip.frameRate : KimodoPlayableClip.FIXED_FRAME_RATE;
            return true;
        }

        internal static string BuildNamedCacheName(AnimationClip sourceClip, string clipType, Avatar targetAvatar)
        {
            string sourceName = SanitizeCacheToken(sourceClip != null ? sourceClip.name : "Clip", "Clip");
            string typeName = SanitizeCacheToken(clipType, "cache");
            if (string.Equals(typeName, MuscleCacheType, StringComparison.OrdinalIgnoreCase))
            {
                return $"{sourceName}-49d-{MuscleCacheType}-cache";
            }

            string avatarName = SanitizeCacheToken(targetAvatar != null ? targetAvatar.name : "Avatar", "Avatar");
            return $"{sourceName}-{BoneCacheType}-{avatarName}-cache";
        }

        private static string SanitizeCacheToken(string value, string defaultValue)
        {
            return KimodoRuntimeUtility.SanitizeName(string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim(), defaultValue)
                .Replace(" ", "_");
        }
    }
}
