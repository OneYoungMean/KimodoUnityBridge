using System;
using TimelineInject;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintExportProjector
    {
        internal static Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> Create(
            string modelName)
        {
            return KimodoRuntimeConstraintExportProjector.Create(modelName);
        }
    }
}
