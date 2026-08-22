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
        public string AnalysisOptionsJson;
        public List<KimodoMarkerSampleResult> ConstraintSamples = new List<KimodoMarkerSampleResult>();
    }
}
