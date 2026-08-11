using System.Collections.Generic;
using UnityEngine;

namespace KimodoBridge
{
    internal static class KimodoFootContactTrackUtility
    {
        internal const int ChannelCount = 4;
        private static readonly string[] ChannelNames =
        {
            "LeftHeel",
            "LeftToe",
            "RightHeel",
            "RightToe"
        };

        internal static void Apply(AnimationClip clip, KimodoRawMotionData motion)
        {
            if (clip == null || motion == null || !motion.HasFootContacts)
            {
                return;
            }

            var contacts = new List<float>(motion.FrameCount * ChannelCount);
            for (int frame = 0; frame < motion.FrameCount; frame++)
            {
                for (int channel = 0; channel < ChannelCount; channel++)
                {
                    motion.TryReadFootContact(frame, channel, out float value);
                    contacts.Add(value);
                }
            }
            Apply(clip, contacts, motion.FrameCount, motion.FrameRate);
        }

        internal static void Apply(AnimationClip clip, IReadOnlyList<float> contacts, int frameCount, float frameRate)
        {
            if (clip == null || contacts == null || contacts.Count != frameCount * ChannelCount || frameCount <= 0)
            {
                return;
            }

            float fps = Mathf.Max(1e-6f, frameRate);
            for (int channel = 0; channel < ChannelCount; channel++)
            {
                var curve = new AnimationCurve();
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float value = contacts[frame * ChannelCount + channel] >= 0.5f ? 1f : 0f;
                    curve.AddKey(CreateStepKey(frame / fps, value));
                }

                float lastValue = contacts[(frameCount - 1) * ChannelCount + channel] >= 0.5f ? 1f : 0f;
                curve.AddKey(CreateStepKey(frameCount / fps, lastValue));
                clip.SetCurve(string.Empty, typeof(Animator), GetPropertyName(channel), curve);
            }
        }

        internal static string GetPropertyName(int channel)
        {
            return channel >= 0 && channel < ChannelNames.Length
                ? $"KimodoFootContact.{ChannelNames[channel]}"
                : string.Empty;
        }

        private static Keyframe CreateStepKey(float time, float value)
        {
            return new Keyframe(time, value, float.PositiveInfinity, float.PositiveInfinity);
        }
    }
}
