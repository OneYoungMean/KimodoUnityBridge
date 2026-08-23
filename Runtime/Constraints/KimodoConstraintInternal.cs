using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace KimodoBridge
{
    /// <summary>
    /// Internal protocol constraint created from one canonical SampleResult.
    /// Avatar/retarget details stay behind this boundary; callers only handle
    /// the returned constraint objects and their JSON.
    /// </summary>
    internal abstract class KimodoConstraintInternal
    {
        protected readonly KimodoMarkerSampleResult Sample;
        protected readonly KimodoConstraintExportContext ExportContext;
        protected readonly KimodoConstraintRigType ModelType;

        protected KimodoConstraintInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
        {
            Sample = sample;
            ModelType = modelType;
            ExportContext = exportContext;
        }

        internal abstract KimodoConstraintJson ToJsonObject(
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps);

        internal string ToJson(
            double clipStartSeconds = 0.0,
            double? clipDurationSeconds = null,
            double exportFps = 30.0)
        {
            KimodoConstraintJson value = ToJsonObject(
                clipStartSeconds,
                clipDurationSeconds,
                exportFps);
            return value == null
                ? string.Empty
                : JsonConvert.SerializeObject(
                    value,
                    Formatting.Indented,
                    new JsonSerializerSettings
                    {
                        NullValueHandling = NullValueHandling.Ignore
                    });
        }

        /// <summary>
        /// Selects the protocol constraints for one canonical sample. The
        /// returned order is the protocol application order: fullbody, root2d,
        /// then end-effectors.
        /// </summary>
        internal static KimodoConstraintInternal[] GetConstraintInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
        {
            if (sample == null || !sample.enabled)
            {
                return Array.Empty<KimodoConstraintInternal>();
            }

            string mode = ResolveMode(sample);
            var result = new List<KimodoConstraintInternal>(3);
            if (string.Equals(mode, "mix", StringComparison.OrdinalIgnoreCase))
            {
                if (KimodoConstraintMask.IsActive(sample, "muscle"))
                {
                    result.Add(new KimodoFullBodyConstraintInternal(sample, modelType, exportContext));
                }
                if (KimodoConstraintMask.IsActive(sample, "rootposition") ||
                    KimodoConstraintMask.IsActive(sample, "rootheading"))
                {
                    result.Add(new KimodoRoot2DConstraintInternal(sample, modelType, exportContext));
                }
                AddEffectors(result, sample, modelType, exportContext);
                return result.ToArray();
            }

            if (string.Equals(mode, "root2d", StringComparison.OrdinalIgnoreCase))
            {
                if (KimodoConstraintMask.IsActive(sample, "rootposition") ||
                    KimodoConstraintMask.IsActive(sample, "rootheading"))
                {
                    result.Add(new KimodoRoot2DConstraintInternal(sample, modelType, exportContext));
                }
            }
            else if (string.Equals(mode, "fullbody", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(mode, "constraint", StringComparison.OrdinalIgnoreCase))
            {
                if (KimodoConstraintMask.IsActive(sample, "muscle"))
                {
                    result.Add(new KimodoFullBodyConstraintInternal(sample, modelType, exportContext));
                }
                AddEffectors(result, sample, modelType, exportContext);
            }
            else
            {
                AddEffectors(result, sample, modelType, exportContext);
            }
            return result.ToArray();
        }

        internal static KimodoConstraintInternal[] Build(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
        {
            return GetConstraintInternal(sample, modelType, exportContext);
        }

        private static void AddEffectors(
            List<KimodoConstraintInternal> result,
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
        {
            if (KimodoConstraintMask.IsActive(sample, "lefthand"))
                result.Add(new KimodoEndEffectorConstraintInternal(sample, modelType, exportContext, "left-hand"));
            if (KimodoConstraintMask.IsActive(sample, "righthand"))
                result.Add(new KimodoEndEffectorConstraintInternal(sample, modelType, exportContext, "right-hand"));
            if (KimodoConstraintMask.IsActive(sample, "leftfoot"))
                result.Add(new KimodoEndEffectorConstraintInternal(sample, modelType, exportContext, "left-foot"));
            if (KimodoConstraintMask.IsActive(sample, "rightfoot"))
                result.Add(new KimodoEndEffectorConstraintInternal(sample, modelType, exportContext, "right-foot"));
        }

        private static string ResolveMode(KimodoMarkerSampleResult sample)
        {
            string protocol = sample.constraintMode;
            bool specificProtocol = string.Equals(protocol, "root2d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(protocol, "fullbody", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(protocol, "left-hand", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(protocol, "right-hand", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(protocol, "left-foot", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(protocol, "right-foot", StringComparison.OrdinalIgnoreCase);
            string mode = specificProtocol ? protocol : sample.constraintMode;
            if (string.IsNullOrWhiteSpace(mode) || string.Equals(mode, "constraint", StringComparison.OrdinalIgnoreCase))
            {
                mode = sample.constraintMode;
            }
            return string.IsNullOrWhiteSpace(mode) ? "fullbody" : mode.Trim().ToLowerInvariant().Replace('_', '-');
        }
    }

    internal sealed class KimodoFullBodyConstraintInternal : KimodoConstraintInternal
    {
        internal KimodoFullBodyConstraintInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
            : base(sample, modelType, exportContext) { }

        internal override KimodoConstraintJson ToJsonObject(
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            return KimodoConstraintJsonExporter.BuildFullBodyInternal(
                Sample, ExportContext, clipStartSeconds, clipDurationSeconds, exportFps);
        }
    }

    internal sealed class KimodoRoot2DConstraintInternal : KimodoConstraintInternal
    {
        internal KimodoRoot2DConstraintInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext)
            : base(sample, modelType, exportContext) { }

        internal override KimodoConstraintJson ToJsonObject(
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            return KimodoConstraintJsonExporter.BuildRoot2DInternal(
                Sample, ExportContext, clipStartSeconds, clipDurationSeconds, exportFps);
        }
    }

    internal sealed class KimodoEndEffectorConstraintInternal : KimodoConstraintInternal
    {
        private readonly string jointType;

        internal KimodoEndEffectorConstraintInternal(
            KimodoMarkerSampleResult sample,
            KimodoConstraintRigType modelType,
            KimodoConstraintExportContext exportContext,
            string jointType)
            : base(sample, modelType, exportContext)
        {
            this.jointType = jointType;
        }

        internal override KimodoConstraintJson ToJsonObject(
            double clipStartSeconds,
            double? clipDurationSeconds,
            double exportFps)
        {
            return KimodoConstraintJsonExporter.BuildEndEffectorInternal(
                Sample, ExportContext, jointType, clipStartSeconds, clipDurationSeconds, exportFps);
        }
    }
}
