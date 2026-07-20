using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using System.Collections.Generic;
using UnityEngine.Serialization;

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
    public class KimodoPlayableClip : AnimationPlayableAsset
    {
        [Header("Kimodo Bridge")]
        public string bridgeModelName = DefaultBridgeModelName;
        [FormerlySerializedAs("bridgeVramMode")]
        [Tooltip("High Performance uses NF4/INT8. High Precision uses FP16. Device placement is automatic.")]
        public KimodoTextEncoderMode textEncoderMode = KimodoTextEncoderMode.HighPrecision;

        [TextArea(2, 6)]
        public string motionPrompt = "a man walk and say hello";
        public int generationFrames = DEFAULT_FRAMES;
        public int diffusionSteps = 100;
        [Range(0f, 4f)] public float textWeight = 1f;
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
        [Tooltip("Show all constraint pose previews for this clip when selected in Timeline/Inspector.")]
        public bool showConstraint = true;
        [Tooltip("Normalize constraint root positions around the first available first-frame constraint anchor before export.")]
        public bool normalizeConstraintOrigin = true;
        public bool isGenerated;
        public string lastGeneratedPrompt;
        [SerializeField, HideInInspector] public string ardyMotionCachePath;
        [SerializeField, HideInInspector] public List<string> ardyClipHandles = new List<string>();
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

        public const float FIXED_FRAME_RATE = 30f;
        public const int MIN_FRAMES = 1;
        public const int MAX_FRAMES = 300;
        public const int DEFAULT_FRAMES = 150;
        public const string DefaultBridgeModelName = "Kimodo-SOMA-RP-v1";

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
            ardyClipHandles.Clear();
            ardyMotionRepFingerprint = string.Empty;
            ardyResolvedSeeds.Clear();
        }

    }
}

