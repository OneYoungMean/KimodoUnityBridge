using TimelineInject;
using UnityEngine;
using System.Collections.Generic;

namespace KimodoBridge.Editor
{
    public sealed class KimodoExternalConstraintRequest
    {
        public string ConstraintsJson;
        public bool Enabled;
        public bool IncludeTimelineConstraints;
        public Avatar RetargetAvatar;
        public List<KimodoMarkerSampleResult> ConstraintSamples = new List<KimodoMarkerSampleResult>();
    }
}
