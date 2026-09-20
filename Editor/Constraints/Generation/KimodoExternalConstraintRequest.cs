using TimelineInject;
using System.Collections.Generic;

namespace KimodoBridge.Editor
{
    public sealed class KimodoExternalConstraintRequest
    {
        public string ConstraintsJson;
        public bool Enabled;
        public bool IncludeTimelineConstraints;
        public string AnalysisOptionsJson;
        // Model-rate context outside the returned clip. Sample times remain
        // relative to the returned clip (negative for preceding context).
        public int ContextBeforeFrames;
        public int ContextAfterFrames;
        public List<KimodoMarkerSampleResult> ConstraintSamples = new List<KimodoMarkerSampleResult>();
    }
}
