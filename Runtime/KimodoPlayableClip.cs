using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;
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
        [Tooltip("Choose a text-encoder profile. Runtime platforms are selected automatically.")]
        public KimodoTextEncoderMode textEncoderMode = KimodoTextEncoderMode.HighPerformance;

        [TextArea(2, 6)]
        public string motionPrompt = string.Empty;
        public int generationFrames = KimodoMotionModelProfiles.DefaultGenerationFrames;
        public int diffusionSteps = 100;
        public bool randomSeed = false;
        public int seed = 42;
        [Tooltip("Generate a baseline motion, constrain its first pose at the end, then generate an extended motion and keep its middle section.")]
        public bool generateLoop;
        [Tooltip("When looping, copy the first frame's X root position to the loop tail.")]
        public bool loopLockPositionX;
        [Tooltip("When looping, copy the first frame's Y root position to the loop tail.")]
        public bool loopLockPositionY = true;
        [Tooltip("When looping, copy the first frame's Z root position to the loop tail.")]
        public bool loopLockPositionZ;
        [Tooltip("When looping, copy the first frame's X root rotation to the loop tail.")]
        public bool loopLockRotationX = true;
        [Tooltip("When looping, copy the first frame's Y root rotation to the loop tail.")]
        public bool loopLockRotationY;
        [Tooltip("When looping, copy the first frame's Z root rotation to the loop tail.")]
        public bool loopLockRotationZ = true;
        [Min(1), Tooltip("Number of command frames at 60 FPS retained from the loop anchor window when constructing Stage 3 loop constraints. Converted to the model frame rate before sampling.")]
        public int loopWindowFrames = 30;
        [Min(1), Tooltip("Number of evenly spaced FullBody samples retained in the Stage 3 loop window, including its endpoints.")]
        public int loopWindowSampleCount = 2;
        [Tooltip("Regenerate with the same seed and override the Root2D path using absolute begin/end Unity yaw angles.")]
        public bool overridePathAngle;
        [Tooltip("Absolute Unity yaw for the PathAngle Bezier start tangent.")]
        public float pathBeginAngleDegrees;
        [Tooltip("Absolute Unity yaw for the PathAngle Bezier end tangent.")]
        public float pathEndAngleDegrees;
        [Tooltip("When overriding Path Angle, replace the measured path length with this value.")]
        public bool overridePathDistance;
        [Min(0f)]
        public float pathDistance;
        [Tooltip("Override second-pass Root2D headings every 30 frames with one absolute Unity yaw.")]
        public bool overrideHeading;
        [Tooltip("Absolute Unity yaw in degrees. Positive turns right; negative turns left; zero faces Unity forward.")]
        public float headingDegrees;
        [Tooltip("Normalize root X/Z into the initial facing frame before Humanoid conversion, transfer initial yaw to the Timeline Clip rotation, preserve generated root height in the muscle curves, and apply the requested World-space target position as the Timeline Clip offset.")]
        public bool useRelativeOffset;
        [Tooltip("World-space target position for the generated Clip's first root pose. The final output root Y curve is preserved and used for height compensation.")]
        public Vector3 relativeOffset;
        [Tooltip("Backend analysis options serialized as JSON and applied to this generation clip.")]
        public string analysisOptionsJson = string.Empty;
        [Tooltip("Run the full animation analysis automatically after generation.")]
        public bool analyzeAfterGeneration = true;
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
        [Tooltip("Defer the In constraint until after the first trajectory generation, align it in SOMA Root2D space, then use it for the second generation pass.")]
        public bool postApplyInConstraint;
        [Tooltip("Defer the Out constraint until after the first trajectory generation, align it in SOMA Root2D space, then use it for the second generation pass.")]
        public bool postApplyOutConstraint;
        [Min(1), Tooltip("Number of model-rate frames covered by the In constraint window.")]
        public int inConstraintWindowFrames = 1;
        [Min(1), Tooltip("Number of evenly spaced FullBody samples in the In window, including its endpoints.")]
        public int inConstraintSampleCount = 1;
        [Min(1), Tooltip("Number of model-rate frames covered by the Out constraint window.")]
        public int outConstraintWindowFrames = 1;
        [Min(1), Tooltip("Number of evenly spaced FullBody samples in the Out window, including its endpoints.")]
        public int outConstraintSampleCount = 1;
        [Min(1), Tooltip("Number of model-rate overlap frames retained when a long animation is split into multiple generation windows.")]
        public int longClipWindowFrames = 30;
        [Min(1), Tooltip("Number of evenly spaced FullBody samples retained in each long-animation overlap window, including its endpoints.")]
        public int longClipWindowSampleCount = 2;
        [SerializeField, HideInInspector]
        public TrackAsset inConstraintSourceTrack;
        [SerializeField, HideInInspector]
        public PlayableAsset inConstraintSourceAsset;
        [SerializeField, HideInInspector]
        public double inConstraintSourceClipStart = -1.0;
        [SerializeField, HideInInspector]
        public int inConstraintSourceFrame = -1;
        [SerializeField, HideInInspector]
        public TrackAsset outConstraintSourceTrack;
        [SerializeField, HideInInspector]
        public PlayableAsset outConstraintSourceAsset;
        [SerializeField, HideInInspector]
        public double outConstraintSourceClipStart = -1.0;
        [SerializeField, HideInInspector]
        public int outConstraintSourceFrame = -1;
        [Tooltip("Adapt ARDY history from previous root speed: 0-1 m/s = 0.225; 1-10 m/s grows exponentially to 1; above 10 m/s = 1.")]
        public bool ardyAutoHistory = true;
        [Range(0f, 1f)]
        [Tooltip("0 uses one motion token of history; 1 uses the largest history window allowed by the model context.")]
        public float ardyHistoryWeight = 1f;
        [Tooltip("Show all constraint pose previews for this clip when selected in Timeline/Inspector.")]
        public bool showConstraint = true;
        [Tooltip("When the first second has no effective constraint anchor, use the character's original scene Hips position and heading as a Root2D frame-0 anchor.")]
        public bool autoBeginAnchor = true;
        public bool isGenerated;
        [HideInInspector]
        public bool isDebugRawClip;
        [HideInInspector]
        public string debugPassName = string.Empty;
        public string lastGeneratedPrompt;
        [SerializeField, HideInInspector]
        internal string commandGenerationStatus = string.Empty;
        [SerializeField, HideInInspector]
        internal string commandGenerationError = string.Empty;
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

