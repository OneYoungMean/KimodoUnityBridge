using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    public sealed class KimodoExternalConstraintRequest
    {
        public string ConstraintsJson;
        public bool Enabled;
        public Avatar RetargetAvatar;
        public bool NormalizeConstraintOriginApplied;
        public KimodoConstraintNormalizationAnchorKind NormalizationAnchorKind;
        public KimodoMarkerSampleResult NormalizationAnchorSample;
    }
}
