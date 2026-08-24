using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace TimelineInject
{
    public static class AvatarRuntimeAccess
    {
        public static Quaternion GetAvatarPostRotationOrIdentity(Avatar avatar, int humanId)
        {
            if (avatar == null)
            {
                return Quaternion.identity;
            }
            return avatar.GetPostRotation(humanId);
        }



        public static float GetAvatarAxisLengthOrZero(Avatar avatar, int humanId)
        {
            if (avatar == null)
            {
                return 0f;
            }
            return avatar.GetAxisLength(humanId);

        }

        public static string GetSkeletonBoneParentNameOrEmpty(SkeletonBone bone)
        {
            return string.IsNullOrWhiteSpace(bone.parentName) ? string.Empty : bone.parentName;
        }
    }

    public static class AnimationOffsetPlayableAccess
    {
        public static Playable CreateMotionXToDeltaAndConnect(
            PlayableGraph graph,
            Playable input)
        {
            AnimationMotionXToDeltaPlayable motion =
                AnimationMotionXToDeltaPlayable.Create(graph);
            graph.Connect(input, 0, motion, 0);
            motion.SetInputWeight(0, 1f);
            motion.SetAbsoluteMotion(true);
            return motion;
        }

        public static Playable CreateAndConnect(
            PlayableGraph graph,
            Playable input,
            Vector3 position,
            Quaternion rotation)
        {
            rotation.Normalize();
            AnimationOffsetPlayable offset = AnimationOffsetPlayable.Create(
                graph,
                position,
                rotation,
                1);
            graph.Connect(input, 0, offset, 0);
            offset.SetInputWeight(0, 1f);

            return CreateMotionXToDeltaAndConnect(graph, offset);
        }
    }
}
