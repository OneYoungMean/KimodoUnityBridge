using UnityEngine;
using UnityEngine.Animations;
using KimodoUnityMotionTools;

namespace KimodoUnityMotionTools.ProjectEditor
{
    public sealed class KimodoRetargetDebugContext
    {
        public IGetMuscleData sourceMuscleData;
        public KimodoRetargetStageCache originBone;
        public KimodoRetargetStageCache originMuscle;
        public KimodoRetargetStageCache targetMuscle;
        public KimodoRetargetStageCache targetBone;
    }

    public sealed class KimodoRetargetStageCache
    {
        public Avatar avatar;
        public GameObject root;
        public HumanPoseHandler poseHandler;
        public string[] bonePaths;
        public Transform[] boneTransforms;
        public HumanPose pose;
        public KimodoRetargetTools.BoneFrame boneFrame;
    }
}
