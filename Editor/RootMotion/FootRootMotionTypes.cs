using System;
using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor.RootMotionTooling
{
    internal enum FootContactState
    {
        Air = 0,
        CandidatePlant = 1,
        Plant = 2,
        CandidateRelease = 3
    }

    [Serializable]
    internal sealed class FootRootMotionSolverSettings
    {
        public float sampleRate = 60f;
        public float plantVelocityThreshold = 0.08f;
        public float plantHeightThreshold = 0.03f;
        public int enterFrames = 2;
        public int exitFrames = 3;
        public float predictionDecayTime = 0.12f;
        public float lateralDamping = 0.35f;
        public float conflictDistanceThreshold = 0.08f;
        public float deltaSmoothing = 0.2f;
    }

    internal struct FootRootMotionFrame
    {
        public float time;
        public Vector3 leftFootWorld;
        public Vector3 rightFootWorld;
        public Vector3 hipWorld;
        public float rootYawRadians;
        public Vector3 sampledRootWorld;
        public Quaternion sampledRootRotation;
    }

    internal sealed class FootRootMotionDebugInfo
    {
        public Vector2[] leftAnchors;
        public Vector2[] rightAnchors;
        public float[] leftConfidence;
        public float[] rightConfidence;
        public float[] conflictError;
        public bool[] usedPrediction;
        public bool[] leftPlant;
        public bool[] rightPlant;
    }

    internal sealed class FootRootMotionResult
    {
        public Vector2[] rootXZ;
        public Vector2[] rootDeltaXZ;
        public FootContactState[] leftContact;
        public FootContactState[] rightContact;
        public FootRootMotionDebugInfo debug;
    }
}
