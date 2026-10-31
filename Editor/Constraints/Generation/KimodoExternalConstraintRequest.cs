using TimelineInject;
using System.Collections.Generic;

namespace KimodoBridge.Editor
{
    public sealed class KimodoExternalConstraintRequest
    {
        public string ConstraintsJson;
        public bool Enabled;
        public bool IncludeTimelineConstraints;
        // Model-rate context outside the returned clip. Sample times remain
        // relative to the returned clip (negative for preceding context).
        public int ContextBeforeFrames;
        public int ContextAfterFrames;
        public List<KimodoMarkerSampleResult> ConstraintSamples = new List<KimodoMarkerSampleResult>();
        public bool DeferInConstraint;
        public bool DeferOutConstraint;
        // In samples are calibrated against the first generated trajectory before
        // they are added to the final generation pass.
        public List<KimodoMarkerSampleResult> DeferredInConstraintSamples = new List<KimodoMarkerSampleResult>();
        // Out samples are calibrated against the first generated trajectory before
        // they are added to the final generation pass.
        public List<KimodoMarkerSampleResult> DeferredOutConstraintSamples = new List<KimodoMarkerSampleResult>();
    }
}
