using System;
using TimelineInject;
using UnityEngine;

namespace KimodoBridge.Editor
{
    internal static class KimodoConstraintExportProjector
    {
        internal static Func<KimodoMarkerSampleResult, KimodoConstraintProjectedPose> Create(
            string modelName,
            Avatar sourceAvatar = null)
        {
            return KimodoRuntimeConstraintExportProjector.Create(modelName, sourceAvatar);
        }
    }
}
