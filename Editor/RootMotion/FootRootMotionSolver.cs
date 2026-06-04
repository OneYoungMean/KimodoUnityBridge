using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class FootRootMotionSolver
    {
        private enum SupportFoot
        {
            None = 0,
            Left = 1,
            Right = 2
        }

        public static FootRootMotionResult Solve(FootRootMotionFrame[] frames, FootRootMotionSolverSettings settings)
        {
            settings = settings ?? FootRootMotionSolverSettings.CreateZero();
            if (frames == null || frames.Length == 0)
            {
                return new FootRootMotionResult
                {
                    rootXZ = new Vector2[0]
                };
            }

            int count = frames.Length;

            SupportFoot[] supportFeet = new SupportFoot[count];
            EvaluateSupportState(frames, settings, supportFeet);
            Vector2[] rootXZ = new Vector2[count];

            rootXZ[0] = ToXZ(frames[0].sampledRootWorld);
            Vector2 sampledRoot0 = ToXZ(frames[0].sampledRootWorld);
            Vector2 leftOffset0 = ToXZ(frames[0].leftFootWorld) - sampledRoot0;
            Vector2 rightOffset0 = ToXZ(frames[0].rightFootWorld) - sampledRoot0;
            Vector2 leftAnchor = rootXZ[0] + leftOffset0;
            Vector2 rightAnchor = rootXZ[0] + rightOffset0;
            Vector2 previousSolvedRoot = rootXZ[0];
            Vector2 previousSolvedDelta = Vector2.zero;
            SupportFoot previousSupport = supportFeet[0];
            int predictionFrames = 0;

            for (int i = 1; i < count; i++)
            {
                Vector2 sampledRoot = ToXZ(frames[i].sampledRootWorld);
                Vector2 leftOffset = ToXZ(frames[i].leftFootWorld) - sampledRoot;
                Vector2 rightOffset = ToXZ(frames[i].rightFootWorld) - sampledRoot;
                SupportFoot support = supportFeet[i];
                Vector2 candidateRoot;

                if (previousSupport == SupportFoot.Left)
                {
                    candidateRoot = leftAnchor - leftOffset;
                }
                else if (previousSupport == SupportFoot.Right)
                {
                    candidateRoot = rightAnchor - rightOffset;
                }
                else
                {
                    predictionFrames++;
                    float dt = Mathf.Max(1e-4f, frames[i].time - frames[i - 1].time);
                    float decayDuration = Mathf.Max(dt, settings.PredictionDecayTime);
                    float decay = Mathf.Clamp01(1f - predictionFrames * dt / decayDuration);
                    Vector2 predictedDelta = previousSolvedDelta * decay;
                    candidateRoot = previousSolvedRoot + predictedDelta;
                }

                if (support != previousSupport && support != SupportFoot.None)
                {
                    if (support == SupportFoot.Left)
                    {
                        leftAnchor = candidateRoot + leftOffset;
                    }
                    else
                    {
                        rightAnchor = candidateRoot + rightOffset;
                    }
                }

                if (support == SupportFoot.Left)
                {
                    candidateRoot = leftAnchor - leftOffset;
                    predictionFrames = 0;
                }
                else if (support == SupportFoot.Right)
                {
                    candidateRoot = rightAnchor - rightOffset;
                    predictionFrames = 0;
                }
                else if (settings.DeltaSmoothing > 0f)
                {
                    Vector2 rawDelta = candidateRoot - previousSolvedRoot;
                    Vector2 smoothedDelta = Vector2.Lerp(rawDelta, previousSolvedDelta, Mathf.Clamp01(settings.DeltaSmoothing));
                    candidateRoot = previousSolvedRoot + smoothedDelta;
                }

                Vector2 solvedDelta = candidateRoot - previousSolvedRoot;
                rootXZ[i] = candidateRoot;
                previousSolvedRoot = candidateRoot;
                previousSolvedDelta = solvedDelta;
                previousSupport = support;
            }

            return new FootRootMotionResult
            {
                rootXZ = rootXZ
            };
        }

        private static void EvaluateSupportState(
            FootRootMotionFrame[] frames,
            FootRootMotionSolverSettings settings,
            SupportFoot[] supportFeet)
        {
            SupportFoot support = SupportFoot.None;
            float switchAccumulator = 0f;
            float keepTime = settings.PlantKeepTimeSeconds;

            for (int i = 0; i < frames.Length; i++)
            {
                Vector3 left = frames[i].leftFootWorld;
                Vector3 right = frames[i].rightFootWorld;
                float dt = i > 0 ? Mathf.Max(1e-4f, frames[i].time - frames[i - 1].time) : FootRootMotionSolverSettings.FixedSamplingStepSeconds;
                bool preferLeft = left.y <= right.y;

                if (keepTime <= 1e-4f)
                {
                    support = preferLeft ? SupportFoot.Left : SupportFoot.Right;
                    switchAccumulator = preferLeft ? keepTime : -keepTime;
                }
                else
                {
                    switchAccumulator += preferLeft ? dt : -dt;
                    switchAccumulator = Mathf.Clamp(switchAccumulator, -keepTime, keepTime);
                    if (support == SupportFoot.None)
                    {
                        if (switchAccumulator >= keepTime)
                        {
                            support = SupportFoot.Left;
                        }
                        else if (switchAccumulator <= -keepTime)
                        {
                            support = SupportFoot.Right;
                        }
                    }
                    else if (support == SupportFoot.Left && switchAccumulator <= -keepTime)
                    {
                        support = SupportFoot.Right;
                    }
                    else if (support == SupportFoot.Right && switchAccumulator >= keepTime)
                    {
                        support = SupportFoot.Left;
                    }
                }

                supportFeet[i] = support;
            }
        }

        private static Vector2 ToXZ(Vector3 value)
        {
            return new Vector2(value.x, value.z);
        }
    }
}
