using TimelineInject;
using UnityEngine;

namespace KimodoBridge
{
    public sealed class KimodoRawMotionProfilePlayer : MonoBehaviour
    {
        [SerializeField] private string modelName = "Kimodo-SOMA-RP-v1";
        [SerializeField] private Transform profileSkeletonRoot;
        [SerializeField] private bool loop;
        [SerializeField] private bool applyRootPosition = true;
        [SerializeField] private bool allowPartialJoints;
        [SerializeField] private float playbackSpeed = 1f;

        private KimodoRawMotionData motion;
        private KimodoRawMotionPlaybackBinding binding;
        private float timeSeconds;
        private bool playing;

        public bool IsPlaying => playing;
        public float TimeSeconds => timeSeconds;
        public float DurationSeconds => motion != null ? motion.DurationSeconds : 0f;
        public KimodoRawMotionData Motion => motion;

        private void Reset()
        {
            profileSkeletonRoot = transform;
        }

        private void Update()
        {
            if (!playing || binding == null)
            {
                return;
            }

            timeSeconds += Time.deltaTime * Mathf.Max(0f, playbackSpeed);
            if (!loop && motion != null && timeSeconds >= motion.LastFrameTimeSeconds)
            {
                timeSeconds = motion.LastFrameTimeSeconds;
                playing = false;
            }

            if (!KimodoRawMotionUtility.TryApplyTime(binding, timeSeconds, out string error, loop, applyRootPosition))
            {
                Debug.LogWarning($"[KimodoRawMotionProfilePlayer] Playback failed: {error}", this);
                playing = false;
            }
        }

        public bool Play(string motionJson, out string error)
        {
            if (!KimodoRawMotionUtility.TryParse(motionJson, out KimodoRawMotionData parsed, out error))
            {
                Stop();
                return false;
            }

            return Play(parsed, out error);
        }

        public bool Play(KimodoRawMotionData rawMotion, out string error)
        {
            Transform root = profileSkeletonRoot != null ? profileSkeletonRoot : transform;
            if (!KimodoRawMotionUtility.TryCreatePlaybackBinding(
                    rawMotion,
                    modelName,
                    root,
                    out KimodoRawMotionPlaybackBinding rawBinding,
                    out error,
                    allowPartialJoints))
            {
                Stop();
                return false;
            }

            motion = rawMotion;
            binding = rawBinding;
            timeSeconds = 0f;
            playing = true;
            return KimodoRawMotionUtility.TryApplyFrame(binding, 0, out error, applyRootPosition);
        }

        public void Stop()
        {
            playing = false;
            timeSeconds = 0f;
            motion = null;
            binding = null;
        }

        public void Pause()
        {
            playing = false;
        }

        public void Resume()
        {
            if (binding != null)
            {
                playing = true;
            }
        }

        public bool TryExtractTailMarkerSample(out KimodoMarkerSampleResult sample, out string error, string constraintType = "fullbody")
        {
            return KimodoRawMotionUtility.TryExtractTailMarkerSample(
                motion,
                modelName,
                out sample,
                out error,
                constraintType);
        }
    }
}
