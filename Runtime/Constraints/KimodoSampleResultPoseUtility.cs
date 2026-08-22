using CharacterAnimationCli.Unity;
using KimodoBridge;

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

            if (!CharacterPoseMuscleAdapter.TryFromSampleData(
                    sample.sampleData,
                    out pose,
                    out error))
            {
                return false;
            }

            if (sample.effectors != null)
            {
                pose.leftHand = sample.effectors.leftHand?.Clone() ?? KimodoRigidTransform.Identity;
                pose.rightHand = sample.effectors.rightHand?.Clone() ?? KimodoRigidTransform.Identity;
                pose.leftFoot = sample.effectors.leftFoot?.Clone() ?? KimodoRigidTransform.Identity;
                pose.rightFoot = sample.effectors.rightFoot?.Clone() ?? KimodoRigidTransform.Identity;
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
            sample.sampleData ??= new MuscleSample();
            if (!CharacterPoseMuscleAdapter.TryToSampleData(
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
            sample.effectors.leftHand = pose.leftHand?.Clone() ?? KimodoRigidTransform.Identity;
            sample.effectors.rightHand = pose.rightHand?.Clone() ?? KimodoRigidTransform.Identity;
            sample.effectors.leftFoot = pose.leftFoot?.Clone() ?? KimodoRigidTransform.Identity;
            sample.effectors.rightFoot = pose.rightFoot?.Clone() ?? KimodoRigidTransform.Identity;
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
