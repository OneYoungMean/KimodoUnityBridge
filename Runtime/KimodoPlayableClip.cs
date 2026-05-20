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
    public class KimodoPlayableClip : AnimationPlayableAsset
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
        [Min(500f)]
        [Tooltip("Local bridge startup timeout in seconds. Minimum is 500.")]
        public float bridgeStartupTimeoutSeconds = 500f;

        [TextArea(2, 6)]
        public string motionPrompt = "";
        public int generationFrames = DEFAULT_FRAMES;
        public int numSamples = 1;
        public int diffusionSteps = 100;
        public bool randomSeed = false;
        public int seed = 42;
        [Tooltip("If empty, use Resources/kimodo-unity-workflow.json")]
        public TextAsset workflowJsonAsset;
        [Min(10f)]
        public float generationTimeoutSeconds = 120f;
        [Min(0.1f)]
        public float pollIntervalSeconds = 1f;

        public bool isGenerated;
        public string lastGeneratedPrompt;
        [Header("Bake Options")]
        [Tooltip("Use Unity built-in keyframe reduction after baking.")]
        public bool keyReduce = true;
        [Range(0f, 1f)]
        public float keyReducePositionError = 0.5f;
        [Range(0f, 1f)]
        public float keyReduceRotationError = 0.5f;
        [Range(0f, 1f)]
        public float keyReduceScaleError = 0.5f;
        [Range(0f, 1f)]
        public float keyReduceFloatError = 0.5f;
        
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

