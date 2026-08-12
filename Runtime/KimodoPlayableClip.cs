using UnityEngine;
using UnityEngine.Timeline;
using UnityEngine.Serialization;
using TimelineInject;

namespace KimodoBridge
{
    public enum KimodoTextEncoderMode
    {
        HighPerformance = 0,
        HighPrecision = 1
    }

    public enum KimodoBakeSkeletonType
    {
        SOMA = 0,
        G1 = 1,
        SMPLX = 2
    }

    public enum KimodoInOutConstraintMode
    {
        None = 0,
        Inside = 1,
        Outside = 2
    }

    public enum KimodoGenerationOutputMode
    {
        Auto = 0,
        HumanoidMuscle = 1,
        CharacterBone = 2,
        ModelBone = 3
    }

    [System.Serializable]
    public class KimodoCurveFilterOptions
    {
        [Tooltip("Enable curve keyframe reduction.")]
        public bool enabled = true;

        [Range(0f, 1f)]
        [Tooltip("CurveFilterOptions.positionError (0-1).")]
        public float positionError = 0.25f;
        [Range(0f, 1f)]
        [Tooltip("CurveFilterOptions.rotationError (0-1).")]
        public float rotationError = 0.25f;
        [Range(0f, 1f)]
        [Tooltip("CurveFilterOptions.floatError (0-1).")]
        public float floatError = 0.25f;
        [Tooltip("Run AnimationClip.EnsureQuaternionContinuity() after bake/reduction.")]
        public bool ensureQuaternionContinuity = true;
    }

    [System.Serializable]
    public partial class KimodoPlayableClip : AnimationPlayableAsset, IKimodoConstraintPreviewSelectable
    {
        [Header("Kimodo Bridge")]
        public string bridgeModelName = KimodoMotionModelProfiles.DefaultModelName;
        [FormerlySerializedAs("bridgeVramMode")]
        [Tooltip("Choose a text-encoder profile. Runtime platforms are selected automatically.")]
        public KimodoTextEncoderMode textEncoderMode = KimodoTextEncoderMode.HighPerformance;

        [TextArea(2, 6)]
        public string motionPrompt = string.Empty;
        public int generationFrames = KimodoMotionModelProfiles.DefaultGenerationFrames;
        public int diffusionSteps = 100;
        public bool randomSeed = false;
        public int seed = 42;
        [Header("Clip Constraint")]
        [Tooltip("Sample this playable clip's current animation as a per-frame pose condition before regenerating it.")]
        public bool enableClipConstraint;
        [Tooltip("Bones preserved by Clip Constraint. Hips preserves root position and orientation.")]
        public AvatarMask clipConstraintAvatarMask;
        [Tooltip("Backend analysis options serialized as JSON and applied to this generation clip.")]
        public string analysisOptionsJson = string.Empty;
        [Tooltip("Optional generated AnimationClip asset name without extension.")]
        public string generatedAssetName = string.Empty;
        [Tooltip("Optional generated AnimationClip folder under Assets.")]
        public string generatedOutputFolder = string.Empty;
        public KimodoGenerationOutputMode generationOutputMode = KimodoGenerationOutputMode.Auto;
        [SerializeField, HideInInspector]
        private Avatar customRetargetAvatar;
        [Tooltip("Choose whether to disable InOutConstraint, use this clip's own start/end poses, or use neighboring clip boundary poses.")]
        public KimodoInOutConstraintMode inOutConstraintMode = KimodoInOutConstraintMode.None;
        [Tooltip("Generate the In boundary constraint when InOut Constraint is Inside or Outside.")]
        public bool enableInConstraint = true;
        [Tooltip("Generate the Out boundary constraint when InOut Constraint is Inside or Outside.")]
        public bool enableOutConstraint = true;
        [Tooltip("Adapt the ARDY history window from upcoming motion constraints.")]
        public bool ardyAutoHistory = true;
        [Range(0f, 1f)]
        [Tooltip("0 uses one motion token of history; 1 uses the largest history window allowed by the model context.")]
        public float ardyHistoryWeight = 1f;
        [Min(0.01f)]
        [Tooltip("Maximum root speed used by ARDY Auto History for a future Full-Body target.")]
        public float ardyTargetMaxSpeed = DefaultArdyTargetMaxSpeed;
        [Min(0.01f)]
        [Tooltip("Maximum root acceleration used by ARDY Auto History for a future Full-Body target.")]
        public float ardyTargetMaxAcceleration = DefaultArdyTargetMaxAcceleration;
        [Tooltip("Show all constraint pose previews for this clip when selected in Timeline/Inspector.")]
        public bool showConstraint = true;
        [Tooltip("When the first second has no effective constraint anchor, use the Timeline start pose as the anchor.")]
        public bool autoBeginAnchor = true;
        public bool isGenerated;
        public string lastGeneratedPrompt;
        [Header("Bake Options")]
        [Tooltip("Auto retarget baked animation according to timeline binding animator.")]
        public bool autoRetargetOnBinding = true;
        [SerializeField]
        public KimodoCurveFilterOptions curveFilterOptions = new KimodoCurveFilterOptions();

        public int frameCount;
        public int jointCount;
        [HideInInspector]
        public int fps = Mathf.RoundToInt(KimodoMotionModelProfiles.DefaultFrameRate);

        public Avatar CustomRetargetAvatar
        {
            get => customRetargetAvatar;
            set => customRetargetAvatar = value;
        }

        public bool ConstraintPreviewEnabled => showConstraint;
        public int ConstraintPreviewPriority => 1;
        public string ConstraintPreviewName => "Clip";

        public const float DefaultArdyTargetMaxSpeed = 1.25f;
        public const float DefaultArdyTargetMaxAcceleration = 1.5f;

        public void ResetGeneration()
        {
            isGenerated = false;
            lastGeneratedPrompt = "";
            frameCount = 0;
            jointCount = 0;
            fps = Mathf.RoundToInt(KimodoMotionModelProfiles.DefaultFrameRate);
        }

    }
}

