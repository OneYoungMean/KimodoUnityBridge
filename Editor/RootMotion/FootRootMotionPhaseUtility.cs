using System;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class FootRootMotionPhaseUtility
    {
        public static FootRootMotionPhaseFrame[] BuildPhaseFrames(FootRootMotionFrame[] frames, FootRootMotionSolverSettings settings)
        {
            int count = frames != null ? frames.Length : 0;
            if (count == 0)
            {
                return new FootRootMotionPhaseFrame[0];
            }

            ResolveDriveMuscleIndices(settings, out int[] leftDriveMuscleIndices, out int[] rightDriveMuscleIndices);
            int windowFrames = settings != null ? settings.PhaseWindowFrameCount : 2;
            int dominantMuscleIndex = ResolveDominantMuscleIndex(frames, leftDriveMuscleIndices, rightDriveMuscleIndices);
            float[] leftDrive = new float[count];
            float[] rightDrive = new float[count];
            float[] leftPrefix = new float[count];
            float[] rightPrefix = new float[count];
            float[] leftWindowDrive = new float[count];
            float[] rightWindowDrive = new float[count];

            for (int i = 1; i < count; i++)
            {
                leftDrive[i] = ComputeSignedDrive(frames[i - 1].muscleSample, frames[i].muscleSample, leftDriveMuscleIndices, dominantMuscleIndex);
                rightDrive[i] = ComputeSignedDrive(frames[i - 1].muscleSample, frames[i].muscleSample, rightDriveMuscleIndices, dominantMuscleIndex);
                leftPrefix[i] = leftPrefix[i - 1] + leftDrive[i];
                rightPrefix[i] = rightPrefix[i - 1] + rightDrive[i];
            }

            float averageAbsWindowDrive = 0f;
            float maxAbsWindowDrive = 0f;
            for (int i = 0; i < count; i++)
            {
                int start = Mathf.Max(1, i - windowFrames + 2);
                leftWindowDrive[i] = i > 0 ? leftPrefix[i] - leftPrefix[start - 1] : 0f;
                rightWindowDrive[i] = i > 0 ? rightPrefix[i] - rightPrefix[start - 1] : 0f;

                float currentMax = Mathf.Max(Mathf.Abs(leftWindowDrive[i]), Mathf.Abs(rightWindowDrive[i]));
                averageAbsWindowDrive += currentMax;
                maxAbsWindowDrive = Mathf.Max(maxAbsWindowDrive, currentMax);
            }

            averageAbsWindowDrive = count > 0 ? averageAbsWindowDrive / count : 0f;
            float zeroCrossThreshold = Mathf.Max(
                1e-5f,
                Mathf.Max(
                    averageAbsWindowDrive * 0.24f,
                    maxAbsWindowDrive * 0.07f));

            int[] leftSigns = new int[count];
            int[] rightSigns = new int[count];
            bool[] leftJumped = new bool[count];
            bool[] rightJumped = new bool[count];
            int leftFirstJumpFrame = -1;
            int rightFirstJumpFrame = -1;

            int previousLeftSign = 0;
            int previousRightSign = 0;
            for (int i = 0; i < count; i++)
            {
                leftSigns[i] = SignWithThreshold(leftWindowDrive[i], zeroCrossThreshold);
                rightSigns[i] = SignWithThreshold(rightWindowDrive[i], zeroCrossThreshold);

                if (leftSigns[i] != 0)
                {
                    if (previousLeftSign != 0 && leftSigns[i] != previousLeftSign)
                    {
                        leftJumped[i] = true;
                        if (leftFirstJumpFrame < 0)
                        {
                            leftFirstJumpFrame = i;
                        }
                    }

                    previousLeftSign = leftSigns[i];
                }

                if (rightSigns[i] != 0)
                {
                    if (previousRightSign != 0 && rightSigns[i] != previousRightSign)
                    {
                        rightJumped[i] = true;
                        if (rightFirstJumpFrame < 0)
                        {
                            rightFirstJumpFrame = i;
                        }
                    }

                    previousRightSign = rightSigns[i];
                }
            }

            FootRootMotionSupportState initialSupport = ResolveInitialSupportState(
                settings,
                leftFirstJumpFrame,
                rightFirstJumpFrame,
                leftWindowDrive,
                rightWindowDrive);
            bool leftGrounded = initialSupport == FootRootMotionSupportState.LeftPlant || initialSupport == FootRootMotionSupportState.DoubleSupport;
            bool rightGrounded = initialSupport == FootRootMotionSupportState.RightPlant || initialSupport == FootRootMotionSupportState.DoubleSupport;

            var phaseFrames = new FootRootMotionPhaseFrame[count];
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    if (leftJumped[i])
                    {
                        leftGrounded = !leftGrounded;
                    }

                    if (rightJumped[i])
                    {
                        rightGrounded = !rightGrounded;
                    }
                }

                float leftWindow = leftWindowDrive[i];
                float rightWindow = rightWindowDrive[i];
                float leftWindowDelta = i > 0 ? leftWindow - leftWindowDrive[i - 1] : 0f;
                float rightWindowDelta = i > 0 ? rightWindow - rightWindowDrive[i - 1] : 0f;
                float leftMagnitude = Mathf.Abs(leftWindow);
                float rightMagnitude = Mathf.Abs(rightWindow);
                float totalMagnitude = leftMagnitude + rightMagnitude;
                float confidence = totalMagnitude > 1e-5f
                    ? Mathf.Abs(leftMagnitude - rightMagnitude) / totalMagnitude
                    : 0f;

                FootRootMotionSupportState supportStateHint = ResolveSupportState(leftGrounded, rightGrounded);
                FootRootMotionPhaseHint hint = ResolvePhaseHint(supportStateHint);

                phaseFrames[i] = new FootRootMotionPhaseFrame
                {
                    leftDrive = leftDrive[i],
                    rightDrive = rightDrive[i],
                    leftWindowDrive = leftWindow,
                    rightWindowDrive = rightWindow,
                    leftWindowDelta = leftWindowDelta,
                    rightWindowDelta = rightWindowDelta,
                    leftJumped = leftJumped[i],
                    rightJumped = rightJumped[i],
                    confidence = confidence,
                    hint = hint,
                    supportStateHint = supportStateHint
                };
            }

            return phaseFrames;
        }

        private static void ResolveDriveMuscleIndices(
            FootRootMotionSolverSettings settings,
            out int[] leftDriveMuscleIndices,
            out int[] rightDriveMuscleIndices)
        {
            string leftBendMuscleName;
            string rightBendMuscleName;
            switch (settings != null ? settings.legBendChannel : FootRootMotionLegBendChannel.LowerLegStretch)
            {
                case FootRootMotionLegBendChannel.UpperLegInOut:
                    leftBendMuscleName = "Left Upper Leg In-Out";
                    rightBendMuscleName = "Right Upper Leg In-Out";
                    break;
                case FootRootMotionLegBendChannel.LowerLegTwistInOut:
                    leftBendMuscleName = "Left Lower Leg Twist In-Out";
                    rightBendMuscleName = "Right Lower Leg Twist In-Out";
                    break;
                case FootRootMotionLegBendChannel.LowerLegStretch:
                default:
                    leftBendMuscleName = "Left Lower Leg Stretch";
                    rightBendMuscleName = "Right Lower Leg Stretch";
                    break;
            }

            leftDriveMuscleIndices = ResolveMuscleIndices(new[]
            {
                "Left Upper Leg Front-Back",
                leftBendMuscleName,
                "Left Upper Leg Twist In-Out"
            });
            rightDriveMuscleIndices = ResolveMuscleIndices(new[]
            {
                "Right Upper Leg Front-Back",
                rightBendMuscleName,
                "Right Upper Leg Twist In-Out"
            });
        }

        private static int ResolveDominantMuscleIndex(
            FootRootMotionFrame[] frames,
            int[] leftDriveMuscleIndices,
            int[] rightDriveMuscleIndices)
        {
            if (frames == null || frames.Length <= 1)
            {
                return 0;
            }

            float bestTotal = float.MinValue;
            int bestIndex = 0;
            int muscleCount = Mathf.Min(
                leftDriveMuscleIndices != null ? leftDriveMuscleIndices.Length : 0,
                rightDriveMuscleIndices != null ? rightDriveMuscleIndices.Length : 0);
            for (int muscleIndex = 0; muscleIndex < muscleCount; muscleIndex++)
            {
                float total = 0f;
                for (int frameIndex = 1; frameIndex < frames.Length; frameIndex++)
                {
                    total += Mathf.Abs(ComputeSignedDrive(frames[frameIndex - 1].muscleSample, frames[frameIndex].muscleSample, leftDriveMuscleIndices, muscleIndex));
                    total += Mathf.Abs(ComputeSignedDrive(frames[frameIndex - 1].muscleSample, frames[frameIndex].muscleSample, rightDriveMuscleIndices, muscleIndex));
                }

                if (total > bestTotal)
                {
                    bestTotal = total;
                    bestIndex = muscleIndex;
                }
            }

            return bestIndex;
        }

        private static int[] ResolveMuscleIndices(string[] names)
        {
            string[] allNames = HumanTrait.MuscleName;
            if (names == null || allNames == null)
            {
                return new int[0];
            }

            int[] indices = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                indices[i] = Array.IndexOf(allNames, names[i]);
            }

            return indices;
        }

        private static float ComputeSignedDrive(MuscleSample previous, MuscleSample current, int[] indices, int muscleIndex)
        {
            if (previous == null || current == null || indices == null || indices.Length == 0)
            {
                return 0f;
            }

            float[] previousMuscles = previous.pose.muscles;
            float[] currentMuscles = current.pose.muscles;
            if (previousMuscles == null || currentMuscles == null)
            {
                return 0f;
            }

            if (muscleIndex < 0 || muscleIndex >= indices.Length)
            {
                return 0f;
            }

            int index = indices[muscleIndex];
            if (index < 0 || index >= previousMuscles.Length || index >= currentMuscles.Length)
            {
                return 0f;
            }

            return currentMuscles[index] - previousMuscles[index];
        }

        private static FootRootMotionSupportState ResolveInitialSupportState(
            FootRootMotionSolverSettings settings,
            int leftFirstJumpFrame,
            int rightFirstJumpFrame,
            float[] leftWindowDrive,
            float[] rightWindowDrive)
        {
            switch (settings != null ? settings.initialSupportMode : FootRootMotionInitialSupportMode.Auto)
            {
                case FootRootMotionInitialSupportMode.Left:
                    return FootRootMotionSupportState.LeftPlant;
                case FootRootMotionInitialSupportMode.Right:
                    return FootRootMotionSupportState.RightPlant;
            }

            if (leftFirstJumpFrame >= 0 || rightFirstJumpFrame >= 0)
            {
                if (leftFirstJumpFrame >= 0 && rightFirstJumpFrame >= 0)
                {
                    if (leftFirstJumpFrame > rightFirstJumpFrame)
                    {
                        return FootRootMotionSupportState.LeftPlant;
                    }

                    if (rightFirstJumpFrame > leftFirstJumpFrame)
                    {
                        return FootRootMotionSupportState.RightPlant;
                    }
                }
                else if (leftFirstJumpFrame >= 0)
                {
                    return FootRootMotionSupportState.LeftPlant;
                }
                else
                {
                    return FootRootMotionSupportState.RightPlant;
                }
            }

            int count = Mathf.Min(leftWindowDrive != null ? leftWindowDrive.Length : 0, rightWindowDrive != null ? rightWindowDrive.Length : 0);
            for (int i = 0; i < count; i++)
            {
                float leftMagnitude = Mathf.Abs(leftWindowDrive[i]);
                float rightMagnitude = Mathf.Abs(rightWindowDrive[i]);
                if (!Mathf.Approximately(leftMagnitude, rightMagnitude))
                {
                    return leftMagnitude <= rightMagnitude
                        ? FootRootMotionSupportState.LeftPlant
                        : FootRootMotionSupportState.RightPlant;
                }
            }

            return FootRootMotionSupportState.LeftPlant;
        }

        private static int SignWithThreshold(float value, float threshold)
        {
            if (value > threshold)
            {
                return 1;
            }

            if (value < -threshold)
            {
                return -1;
            }

            return 0;
        }

        private static FootRootMotionSupportState ResolveSupportState(bool leftGrounded, bool rightGrounded)
        {
            if (leftGrounded && rightGrounded)
            {
                return FootRootMotionSupportState.DoubleSupport;
            }

            if (!leftGrounded && !rightGrounded)
            {
                return FootRootMotionSupportState.Air;
            }

            return leftGrounded
                ? FootRootMotionSupportState.LeftPlant
                : FootRootMotionSupportState.RightPlant;
        }

        private static FootRootMotionPhaseHint ResolvePhaseHint(FootRootMotionSupportState supportState)
        {
            switch (supportState)
            {
                case FootRootMotionSupportState.LeftPlant:
                    return FootRootMotionPhaseHint.RightSwing;
                case FootRootMotionSupportState.RightPlant:
                    return FootRootMotionPhaseHint.LeftSwing;
                default:
                    return FootRootMotionPhaseHint.Balanced;
            }
        }
    }
}
