using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using System.Collections.Generic;
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
        public string bridgeModelName = DefaultBridgeModelName;
        [FormerlySerializedAs("bridgeVramMode")]
        [Tooltip("Choose a text-encoder profile. Runtime platforms are selected automatically.")]
        public KimodoTextEncoderMode textEncoderMode = KimodoTextEncoderMode.HighPerformance;

        [TextArea(2, 6)]
        public string motionPrompt = string.Empty;
        public int generationFrames = DEFAULT_FRAMES;
        public int diffusionSteps = 100;
        [HideInInspector, Range(0f, 4f)] public float textWeight = 1f;
        public bool randomSeed = false;
        public int seed = 42;
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
        [SerializeField, HideInInspector] public string ardyMotionCachePath;
        [SerializeField, HideInInspector] public string ardyMotionRepFingerprint;
        [SerializeField, HideInInspector] public List<int> ardyResolvedSeeds = new List<int>();
        [Header("Bake Options")]
        [Tooltip("Auto retarget baked animation according to timeline binding animator.")]
        public bool autoRetargetOnBinding = true;
        [SerializeField]
        public KimodoCurveFilterOptions curveFilterOptions = new KimodoCurveFilterOptions();

        public int frameCount;
        public int jointCount;
        [HideInInspector]
        public int fps = Mathf.RoundToInt(FIXED_FRAME_RATE);

        public KimodoBakeSkeletonType InferredSkeletonType
        {
            get
            {
                return ResolveBakeSkeletonTypeFromModelName(bridgeModelName);
            }
        }

        public static KimodoBakeSkeletonType ResolveBakeSkeletonTypeFromModelName(string modelName)
        {
            string normalized = NormalizeBridgeModelName(modelName).ToLowerInvariant();
            if (normalized.Contains("smplx"))
            {
                return KimodoBakeSkeletonType.SMPLX;
            }

            if (normalized.Contains("g1"))
            {
                return KimodoBakeSkeletonType.G1;
            }

            return KimodoBakeSkeletonType.SOMA;
        }

        public static string NormalizeBridgeModelName(string modelName)
        {
            return string.IsNullOrWhiteSpace(modelName)
                ? DefaultBridgeModelName
                : modelName.Trim();
        }

        public Avatar CustomRetargetAvatar
        {
            get => customRetargetAvatar;
            set => customRetargetAvatar = value;
        }

        public bool ConstraintPreviewEnabled => showConstraint;
        public int ConstraintPreviewPriority => 1;
        public string ConstraintPreviewName => "Clip";

        public const float FIXED_FRAME_RATE = 30f;
        public const int MIN_FRAMES = 1;
        public const int MAX_FRAMES = 300;
        public const int DEFAULT_FRAMES = 150;
        public const string DefaultBridgeModelName = "Kimodo-SOMA-RP-v1";
        public const float DefaultArdyTargetMaxSpeed = 1.25f;
        public const float DefaultArdyTargetMaxAcceleration = 1.5f;

        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            return base.CreatePlayable(graph, owner);
        }

        public void ResetGeneration()
        {
            isGenerated = false;
            lastGeneratedPrompt = "";
            frameCount = 0;
            jointCount = 0;
            fps = Mathf.RoundToInt(FIXED_FRAME_RATE);
            ardyMotionCachePath = string.Empty;
            ardyMotionRepFingerprint = string.Empty;
            ardyResolvedSeeds.Clear();
        }

    }
}

