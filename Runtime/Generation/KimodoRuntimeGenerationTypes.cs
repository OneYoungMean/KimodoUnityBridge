using System;
using KimodoUnityMotionTools.Bridge;
using UnityEngine;

namespace KimodoUnityMotionTools.Generation
{
    public enum KimodoBackendType
    {
        Bridge = 0,
        ComfyUi = 1
    }

    [Serializable]
    public sealed class KimodoGenerationRequestDto
    {
        public string prompt;
        public float duration;
        public int? seed;
        public int steps;
        public string constraints_json;
        // Optional serialized boundary pose payload for segment stitching.
        public string boundary_pose_json;
        // Optional hint to backend that this request is for loop/infinite continuation.
        public bool loop_hint;
        // Optional segment sequence index for observability on backend side.
        public int segment_index;
        // Optional desired transition overlap in seconds.
        public float transition_duration;
    }

    [Serializable]
    public sealed class KimodoBoundaryPoseDto
    {
        public Vector3 rootPosition;
        public Quaternion rootRotation;
    }

    [Serializable]
    public sealed class KimodoGenerationResultDto
    {
        public string motionJsonCompact;
        public KimodoBackendType backendType;
        public string rawStatus;
        public string message;
    }

    [Serializable]
    public sealed class KimodoRuntimeGenerationSettings
    {
        public BridgeRuntimeSettings bridgeSettings;
        public string comfyHost = "127.0.0.1";
        public int comfyPort = 8188;
        public float comfyTimeoutSeconds = 120f;
        public string comfyWorkflowResourceName = "kimodo-unity-workflow";
    }
}
