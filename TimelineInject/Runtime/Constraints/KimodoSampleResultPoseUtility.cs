using CharacterAnimationCli.Unity;

namespace TimelineInject
{
    /// <summary>
    /// Explicit conversion boundary for APIs that still need a temporary
    /// CharacterPose (HumanPose/JSON adapters). KimodoMarkerSampleResult itself
    /// stores only sampleData, enableMask and effector channels.
    /// </summary>
    public static class KimodoSampleResultPoseUtility
    {
        public static bool TryDecode(
            KimodoMarkerSampleResult sample,
            out CharacterPose pose,
            out string error)
        {
            pose = null;
            if (sample == null)
            {
                error = "SampleResult is null.";
                return false;
            }

            if (!KimodoSampleDataLayout.TryDecodeCharacterPose(
                    sample.sampleData,
                    out pose,
                    out error))
            {
                return false;
            }

            if (sample.effectors != null)
            {
                pose.hands = sample.effectors.hands?.Clone() ?? new CharacterPoseSides();
                pose.feet = sample.effectors.feet?.Clone() ?? new CharacterPoseSides();
            }
            return true;
        }

        public static bool TryEncode(
            KimodoMarkerSampleResult sample,
            CharacterPose pose,
            out string error)
        {
            error = string.Empty;
            if (sample == null)
            {
                error = "SampleResult is null.";
                return false;
            }
            sample.sampleData ??= new KimodoBridge.MuscleSample();
            if (!KimodoSampleDataLayout.TryEncodeCharacterPose(
                    pose,
                    sample.sampleData,
                    out error))
            {
                return false;
            }

            sample.enableMask ??= new KimodoSampleChannelMask();
            sample.enableMask.muscle49 = true;
            sample.enableMask.rootTQ = true;
            sample.enableMask.leftFootTQ = true;
            sample.enableMask.rightFootTQ = true;
            sample.effectors ??= new KimodoConstraintEffectors();
            sample.effectors.hands = pose.hands?.Clone() ?? new CharacterPoseSides();
            sample.effectors.feet = pose.feet?.Clone() ?? new CharacterPoseSides();
            return true;
        }

        public static CharacterPose DecodeOrDefault(KimodoMarkerSampleResult sample)
        {
            return TryDecode(sample, out CharacterPose pose, out _)
                ? pose
                : new CharacterPose();
        }
    }
}
