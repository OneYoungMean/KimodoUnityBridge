using UnityEngine;

namespace KimodoUnityMotionTools.ProjectEditor.RootMotionTooling
{
    internal static class FootRootMotionSolver
    {
        public static FootRootMotionResult Solve(FootRootMotionFrame[] frames, FootRootMotionSolverSettings settings)
        {
            settings = settings ?? new FootRootMotionSolverSettings();
            if (frames == null || frames.Length == 0)
            {
                return new FootRootMotionResult
                {
                    rootXZ = new Vector2[0],
                    rootDeltaXZ = new Vector2[0],
                    leftContact = new FootContactState[0],
                    rightContact = new FootContactState[0],
                    debug = new FootRootMotionDebugInfo()
                };
            }

            int count = frames.Length;
            float[] leftHeights = new float[count];
            float[] rightHeights = new float[count];
            for (int i = 0; i < count; i++)
            {
                leftHeights[i] = frames[i].leftFootWorld.y;
                rightHeights[i] = frames[i].rightFootWorld.y;
            }

            float leftBaseHeight = ComputeLowPercentile(leftHeights, 0.1f);
            float rightBaseHeight = ComputeLowPercentile(rightHeights, 0.1f);

            FootContactState[] leftStates = new FootContactState[count];
            FootContactState[] rightStates = new FootContactState[count];
            float[] leftConfidence = new float[count];
            float[] rightConfidence = new float[count];
            bool[] leftPlant = new bool[count];
            bool[] rightPlant = new bool[count];

            EvaluateFootContact(
                frames,
                true,
                leftBaseHeight,
                settings,
                leftStates,
                leftConfidence,
                leftPlant);
            EvaluateFootContact(
                frames,
                false,
                rightBaseHeight,
                settings,
                rightStates,
                rightConfidence,
                rightPlant);

            Vector2[] rootDelta = new Vector2[count];
            Vector2[] rootXZ = new Vector2[count];
            Vector2[] leftAnchors = new Vector2[count];
            Vector2[] rightAnchors = new Vector2[count];
            float[] conflict = new float[count];
            bool[] usedPrediction = new bool[count];

            Vector2 leftAnchor = ToXZ(frames[0].leftFootWorld);
            Vector2 rightAnchor = ToXZ(frames[0].rightFootWorld);
            Vector2 previousDelta = Vector2.zero;
            int predictionFrames = 0;

            for (int i = 1; i < count; i++)
            {
                bool leftPlanted = leftPlant[i];
                bool rightPlanted = rightPlant[i];

                if (leftPlanted && !leftPlant[i - 1])
                {
                    leftAnchor = ToXZ(frames[i].leftFootWorld);
                }

                if (rightPlanted && !rightPlant[i - 1])
                {
                    rightAnchor = ToXZ(frames[i].rightFootWorld);
                }

                leftAnchors[i] = leftAnchor;
                rightAnchors[i] = rightAnchor;

                Vector2 leftObservation = -(ToXZ(frames[i].leftFootWorld) - ToXZ(frames[i - 1].leftFootWorld));
                Vector2 rightObservation = -(ToXZ(frames[i].rightFootWorld) - ToXZ(frames[i - 1].rightFootWorld));

                Vector2 delta;
                if (leftPlanted && rightPlanted)
                {
                    float lw = Mathf.Max(0.0001f, leftConfidence[i]);
                    float rw = Mathf.Max(0.0001f, rightConfidence[i]);
                    conflict[i] = Vector2.Distance(leftObservation, rightObservation);
                    if (conflict[i] > settings.conflictDistanceThreshold)
                    {
                        delta = lw >= rw ? leftObservation : rightObservation;
                    }
                    else
                    {
                        delta = (leftObservation * lw + rightObservation * rw) / (lw + rw);
                    }

                    predictionFrames = 0;
                }
                else if (leftPlanted)
                {
                    delta = leftObservation;
                    predictionFrames = 0;
                }
                else if (rightPlanted)
                {
                    delta = rightObservation;
                    predictionFrames = 0;
                }
                else
                {
                    predictionFrames++;
                    usedPrediction[i] = true;
                    float dt = Mathf.Max(1e-4f, frames[i].time - frames[i - 1].time);
                    float decayDuration = Mathf.Max(dt, settings.predictionDecayTime);
                    float decay = Mathf.Clamp01(1f - predictionFrames * dt / decayDuration);
                    delta = previousDelta * decay;
                }

                delta = ApplyDirectionalDamping(delta, frames[i].rootYawRadians, settings.lateralDamping);
                rootDelta[i] = delta;
                previousDelta = delta;
            }

            SmoothDeltas(rootDelta, leftPlant, rightPlant, settings.deltaSmoothing);

            for (int i = 1; i < count; i++)
            {
                rootXZ[i] = rootXZ[i - 1] + rootDelta[i];
            }

            return new FootRootMotionResult
            {
                rootXZ = rootXZ,
                rootDeltaXZ = rootDelta,
                leftContact = leftStates,
                rightContact = rightStates,
                debug = new FootRootMotionDebugInfo
                {
                    leftAnchors = leftAnchors,
                    rightAnchors = rightAnchors,
                    leftConfidence = leftConfidence,
                    rightConfidence = rightConfidence,
                    conflictError = conflict,
                    usedPrediction = usedPrediction,
                    leftPlant = leftPlant,
                    rightPlant = rightPlant
                }
            };
        }

        private static void EvaluateFootContact(
            FootRootMotionFrame[] frames,
            bool leftFoot,
            float baseHeight,
            FootRootMotionSolverSettings settings,
            FootContactState[] states,
            float[] confidence,
            bool[] plantMask)
        {
            int stableCount = 0;
            int unstableCount = 0;
            states[0] = FootContactState.Air;

            for (int i = 0; i < frames.Length; i++)
            {
                Vector3 curr = leftFoot ? frames[i].leftFootWorld : frames[i].rightFootWorld;
                Vector3 prev = i > 0 ? (leftFoot ? frames[i - 1].leftFootWorld : frames[i - 1].rightFootWorld) : curr;
                float dt = i > 0 ? Mathf.Max(1e-4f, frames[i].time - frames[i - 1].time) : 1f / Mathf.Max(1f, settings.sampleRate);
                float planarVelocity = (ToXZ(curr) - ToXZ(prev)).magnitude / dt;
                float heightOverBase = Mathf.Max(0f, curr.y - baseHeight);
                float yawDelta = i > 0 ? Mathf.Abs(Mathf.DeltaAngle(frames[i - 1].rootYawRadians * Mathf.Rad2Deg, frames[i].rootYawRadians * Mathf.Rad2Deg)) : 0f;

                bool stable = planarVelocity <= settings.plantVelocityThreshold &&
                              heightOverBase <= settings.plantHeightThreshold &&
                              yawDelta <= 30f;

                if (stable)
                {
                    stableCount++;
                    unstableCount = 0;
                }
                else
                {
                    unstableCount++;
                    stableCount = 0;
                }

                FootContactState state = i > 0 ? states[i - 1] : FootContactState.Air;
                bool planted = i > 0 && plantMask[i - 1];

                if (!planted)
                {
                    if (stableCount >= Mathf.Max(1, settings.enterFrames))
                    {
                        state = FootContactState.Plant;
                        planted = true;
                    }
                    else if (stable)
                    {
                        state = FootContactState.CandidatePlant;
                    }
                    else
                    {
                        state = FootContactState.Air;
                    }
                }
                else
                {
                    if (unstableCount >= Mathf.Max(1, settings.exitFrames))
                    {
                        state = FootContactState.Air;
                        planted = false;
                    }
                    else if (!stable)
                    {
                        state = FootContactState.CandidateRelease;
                    }
                    else
                    {
                        state = FootContactState.Plant;
                    }
                }

                states[i] = state;
                plantMask[i] = planted;

                float velScore = 1f - Mathf.Clamp01(planarVelocity / Mathf.Max(0.0001f, settings.plantVelocityThreshold));
                float heightScore = 1f - Mathf.Clamp01(heightOverBase / Mathf.Max(0.0001f, settings.plantHeightThreshold));
                float yawScore = 1f - Mathf.Clamp01(yawDelta / 30f);
                confidence[i] = planted ? Mathf.Clamp01(0.5f * velScore + 0.35f * heightScore + 0.15f * yawScore) : 0f;
            }
        }

        private static void SmoothDeltas(Vector2[] deltas, bool[] leftPlant, bool[] rightPlant, float smoothing)
        {
            if (deltas == null || deltas.Length <= 2 || smoothing <= 0f)
            {
                return;
            }

            Vector2 previous = deltas[0];
            for (int i = 1; i < deltas.Length; i++)
            {
                bool planted = leftPlant[i] || rightPlant[i];
                float t = planted ? smoothing * 0.35f : smoothing;
                t = Mathf.Clamp01(t);
                previous = Vector2.Lerp(deltas[i], previous, t);
                deltas[i] = previous;
            }
        }

        private static Vector2 ApplyDirectionalDamping(Vector2 delta, float yawRadians, float lateralDamping)
        {
            Vector2 forward = new Vector2(Mathf.Sin(yawRadians), Mathf.Cos(yawRadians));
            if (forward.sqrMagnitude < 1e-6f)
            {
                forward = Vector2.up;
            }

            forward.Normalize();
            Vector2 right = new Vector2(forward.y, -forward.x);
            float forwardAmount = Vector2.Dot(delta, forward);
            float lateralAmount = Vector2.Dot(delta, right) * Mathf.Clamp01(1f - lateralDamping);
            return forward * forwardAmount + right * lateralAmount;
        }

        private static float ComputeLowPercentile(float[] values, float percentile)
        {
            float[] copy = new float[values.Length];
            values.CopyTo(copy, 0);
            System.Array.Sort(copy);
            int index = Mathf.Clamp(Mathf.RoundToInt((copy.Length - 1) * Mathf.Clamp01(percentile)), 0, copy.Length - 1);
            return copy[index];
        }

        private static Vector2 ToXZ(Vector3 value)
        {
            return new Vector2(value.x, value.z);
        }
    }
}
