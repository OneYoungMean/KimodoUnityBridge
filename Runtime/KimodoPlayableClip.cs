using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Timeline;


namespace KimodoUnityMotionTools
{
    public enum KimodoBridgeVramMode
    {
        Low = 0,
        High = 1
    }

    public enum KimodoGenerationBackend
    {
        ComfyUI = 0,
        KimodoBridge = 1
    }

    public enum KimodoBakeSkeletonType
    {
        SOMA = 0
    }

    [System.Serializable]
    public class KimodoCurveFilterSettings
    {
        [Tooltip("Whether to apply keyframe reduction when saving recorder data to clip.")]
        public bool keyframeReduction = true;

        [Range(0f, 1f)]
        [Tooltip("Allowed position curve deviation (0-1). Unity docs suggest 0.5 as light compression.")]
        public float positionError = 0.5f;

        [Range(0f, 1f)]
        [Tooltip("Allowed rotation curve deviation (0-1). Unity docs suggest 0.5 as light compression.")]
        public float rotationError = 0.5f;

        [Range(0f, 1f)]
        [Tooltip("Allowed scale curve deviation (0-1). Unity docs suggest 0.5 as light compression.")]
        public float scaleError = 0.5f;

        [Range(0f, 1f)]
        [Tooltip("Allowed float curve deviation (0-1). Unity docs suggest 0.5 as light compression.")]
        public float floatError = 0.5f;

        [Tooltip("Whether to unroll rotation if supported by this Unity version.")]
        public bool unrollRotation = true;
    }

    [System.Serializable]
    public class KimodoPlayableClip : AnimationPlayableAsset, IKimodoSampleMarker
    {
        [Header("Generation Backend")]
        public KimodoGenerationBackend generationBackend = KimodoGenerationBackend.KimodoBridge;

        [Header("ComfyUI")]
        public string comfyuiIP = "127.0.0.1";
        public int comfyuiPort = 8188;

        [Header("Kimodo Bridge")]
        [HideInInspector]
        [Tooltip("Deprecated legacy field. Local bridge startup is managed by packaged offline launcher.")]
        public string bridgePythonPath = "";
        [HideInInspector]
        [Tooltip("Deprecated legacy field. Local bridge startup is managed by packaged offline launcher.")]
        public string bridgeLauncherPath = "";
        public string bridgeModelName = "Kimodo-SOMA-RP-v1";
        [Tooltip("Low: quantized encoder (~4G). High: full encoder (~16G). Kimodo base model ~2G.")]
        public KimodoBridgeVramMode bridgeVramMode = KimodoBridgeVramMode.Low;
        [HideInInspector]
        [Tooltip("Deprecated legacy field. Local bridge startup is managed by packaged offline launcher.")]
        public string bridgeServerScriptPath = "";

        [TextArea(2, 6)]
        public string motionPrompt = "";
        public int generationFrames = DEFAULT_FRAMES;
        public int numSamples = 1;
        public int diffusionSteps = 100;
        public bool randomSeed = false;
        public int seed = 42;
        [Tooltip("Enable inbetween interpolation by constraining start/end with neighboring clips on timeline.")]
        public bool enableInbetweenInterpolation = false;
        [Tooltip("If empty, use Resources/kimodo-unity-workflow.json")]
        public TextAsset workflowJsonAsset;
        [Min(10f)]
        public float generationTimeoutSeconds = 120f;
        [Min(0.1f)]
        public float pollIntervalSeconds = 1f;

        public bool isGenerated;
        public string lastGeneratedPrompt;
        [Header("Bake Options")]
        [Tooltip("Auto retarget baked animation according to timeline binding animator.")]
        public bool autoRetargetOnBinding = true;
        [Tooltip("Bake via GameObjectRecorder.SaveToClip so CurveFilterOptions can be applied.")]
        public bool bakeUseRecorderSaveToClip = true;
        [Tooltip("Run AnimationClip.EnsureQuaternionContinuity() after bake.")]
        public bool bakeEnsureQuaternionContinuity = true;
        public KimodoCurveFilterSettings curveFilterOptions = new KimodoCurveFilterSettings();
        
        public string motionData;
        public int frameCount;
        public int jointCount;
        public int fps;
        public string[] jointNames;
        public float[] motionPositions;
        public KimodoBakeSkeletonType savedSkeletonType = KimodoBakeSkeletonType.SOMA;

        public const int MIN_FRAMES = 120;
        public const int MAX_FRAMES = 300;
        public const int DEFAULT_FRAMES = 150;
        
        public override Playable CreatePlayable(PlayableGraph graph, GameObject owner)
        {
            return base.CreatePlayable(graph, owner);
        }

        public bool TrySampleMarker(KimodoMarkerSampleRequest request, out KimodoMarkerSampleResult result, out string error)
        {
            return KimodoMarkerSamplingUtility.TrySampleMarker(request, out result, out error);
        }
        
        public void ResetGeneration()
        {
            isGenerated = false;
            lastGeneratedPrompt = "";
            motionData = "";
            frameCount = 0;
            jointCount = 0;
            fps = 0;
            jointNames = null;
            motionPositions = null;
        }

    }
}

