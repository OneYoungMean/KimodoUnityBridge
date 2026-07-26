using UnityEngine;

namespace KimodoBridge
{
    public sealed partial class KimodoInfiniteMotionDemo : MonoBehaviour
    {
        public void StartDemo()
        {
            _ = StartDemoAsync();
        }

        public void StopDemo()
        {
            _ = StopDemoAsync();
        }

        public void ResetDemo()
        {
            _ = ResetDemoAsync();
        }

        public void SetAnimationDurationSeconds(float seconds)
        {
            ApplyGenerationDurationSeconds(seconds);
        }

        public void SetLoop(bool enabled)
        {
            loopHint = enabled;
            if (!loopHint)
            {
                ClearNextConstraintPoses();
            }
        }

        public bool GetLoop()
        {
            return loopHint;
        }

        public void SetOverlap(int sampleCount)
        {
            overlapConstraintSamples = Mathf.Clamp(sampleCount, 1, MaxOverlapConstraintSamples);
        }

        public int GetOverlap()
        {
            return Mathf.Clamp(overlapConstraintSamples, 1, MaxOverlapConstraintSamples);
        }

        public void SetPrompt(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return;
            }

            promptDraft = prompt.Trim();
            UpdateStatus($"Prompt updated: {promptDraft}");
        }

        public void SwitchToNextCharacter()
        {
            if (characterSwitchCoroutine != null)
            {
                return;
            }

            if (characterConstraintStates.Count < 2)
            {
                return;
            }

            int nextIndex = ResolveNextCharacterStateIndex();
            if (nextIndex < 0 || nextIndex == currentCharacterStateIndex)
            {
                return;
            }

            characterSwitchCoroutine = StartCoroutine(SwitchCharacterCoroutine(currentCharacterStateIndex, nextIndex));
        }
    }
}
