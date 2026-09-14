#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using TimelineInject;
using Object = UnityEngine.Object;

namespace KimodoBridge.Editor.Tests
{
    public sealed class KimodoHandEffectorGoalTests
    {
        private const string ArmaturePath =
            "Packages/com.unity.kimodo_unity_motion_tools/Editor/Model/Armature.fbx";

        [Test]
        public void ArmatureAvatar_HandGoalFormulaMatchesAnimationHumanStream()
        {
            Object asset = AssetDatabase.LoadMainAssetAtPath(ArmaturePath);
            Assert.That(asset, Is.Not.Null, $"Missing test model at {ArmaturePath}");
            GameObject source = asset as GameObject;
            Assert.That(source, Is.Not.Null, "Armature.fbx main asset is not a GameObject.");
            Animator sourceAnimator = source.GetComponentInChildren<Animator>(true);
            Assert.That(sourceAnimator, Is.Not.Null, "Armature.fbx has no Animator.");
            Assert.That(sourceAnimator.avatar != null && sourceAnimator.avatar.isHuman, Is.True,
                "Armature.fbx Animator has no valid humanoid Avatar.");

            GameObject go = Object.Instantiate(source);
            go.name = "KimodoHandGoalTestInstance";
            Animator animator = go.GetComponentInChildren<Animator>(true);
            animator.runtimeAnimatorController = null;
            animator.avatar = sourceAnimator.avatar;
            animator.Rebind();
            animator.Update(0f);

            PlayableGraph graph = default;
            try
            {
                graph = PlayableGraph.Create("KimodoHandGoalFormulaTest");
                AnimationPlayableOutput output = AnimationPlayableOutput.Create(graph, "output", animator);
                AnimationClip sourceClip = null;
                foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(ArmaturePath))
                {
                    if (sub is AnimationClip candidate && candidate.humanMotion) { sourceClip = candidate; break; }
                }
                if (sourceClip == null) sourceClip = new AnimationClip();
                AnimationClipPlayable clipPlayable = AnimationClipPlayable.Create(graph, sourceClip);
                AnimationScriptPlayable jobPlayable = AnimationScriptPlayable.Create(
                    graph, new GoalReadJob(), 1);
                graph.Connect(clipPlayable, 0, jobPlayable, 0);
                jobPlayable.SetInputWeight(0, 1f);
                output.SetSourcePlayable(jobPlayable);
                graph.Play();
                graph.Evaluate(0f);

                GoalReadJob job = jobPlayable.GetJobData<GoalReadJob>();
                Quaternion expectedLeft = ComputeGoal(animator, HumanBodyBones.LeftHand);
                Quaternion expectedRight = ComputeGoal(animator, HumanBodyBones.RightHand);
                Assert.That(Quaternion.Angle(job.leftGoal, expectedLeft), Is.LessThan(0.01f));
                Assert.That(Quaternion.Angle(job.rightGoal, expectedRight), Is.LessThan(0.01f));
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                Object.DestroyImmediate(go);
            }
        }

        private static Quaternion ComputeGoal(Animator animator, HumanBodyBones bone)
        {
            Transform hand = animator.GetBoneTransform(bone);
            Quaternion post = AvatarRuntimeAccess.GetAvatarPostRotationOrIdentity(animator.avatar, (int)bone);
            Quaternion offset = bone == HumanBodyBones.LeftHand
                ? new Quaternion(0.707107f, 0f, 0.707107f, 0f)
                : new Quaternion(0f, 0.707107f, 0f, 0.707107f);
            return (hand.rotation * post * offset).normalized;
        }

        private struct GoalReadJob : IAnimationJob
        {
            public Quaternion leftGoal;
            public Quaternion rightGoal;

            public void ProcessRootMotion(AnimationStream stream) { }

            public void ProcessAnimation(AnimationStream stream)
            {
                if (!stream.isHumanStream) return;
                AnimationHumanStream human = stream.AsHuman();
                leftGoal = human.GetGoalRotationFromPose(AvatarIKGoal.LeftHand);
                rightGoal = human.GetGoalRotationFromPose(AvatarIKGoal.RightHand);
            }
        }
    }
}
#endif
