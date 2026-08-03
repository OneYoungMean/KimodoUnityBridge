using TimelineInject;
using UnityEngine;
using System.Collections.Generic;

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
        public List<KimodoMarkerSampleResult> ConstraintSamples = new List<KimodoMarkerSampleResult>();

        internal KimodoConstraintNormalizationInfo BuildNormalizationInfo()
        {
            return new KimodoConstraintNormalizationInfo
            {
                Applied = NormalizeConstraintOriginApplied,
                AnchorKind = NormalizationAnchorKind,
                AnchorSample = NormalizationAnchorSample?.Clone()
            };
        }
    }
}
