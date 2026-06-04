using System;
using UnityEngine;

namespace KimodoBridge.Editor
{
    [Serializable]
    internal sealed class FootRootMotionSolverSettings
    {
        public const float FixedSamplingStepSeconds = 1f / 30f;

        public float keepTime = 0f;
        public float airPrediction = 0f;
        public float smoothing = 0f;

        public float PlantKeepTimeSeconds => Mathf.Clamp(keepTime, 0f, 0.3f);
        public float PredictionDecayTime => Mathf.Lerp(0.04f, 0.20f, Mathf.Clamp01(airPrediction));
        public float DeltaSmoothing => Mathf.Clamp01(smoothing);

        public static FootRootMotionSolverSettings CreateZero()
        {
            return new FootRootMotionSolverSettings
            {
                keepTime = 0f,
                airPrediction = 0f,
                smoothing = 0f
            };
        }
    }

    internal struct FootRootMotionFrame
    {
        public float time;
        public Vector3 leftFootWorld;
        public Vector3 rightFootWorld;
        public Vector3 sampledRootWorld;
        public Quaternion sampledRootRotation;
    }

    internal sealed class FootRootMotionResult
    {
        public Vector2[] rootXZ;
    }
}
