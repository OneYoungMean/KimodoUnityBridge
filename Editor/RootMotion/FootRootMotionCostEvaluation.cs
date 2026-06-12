using UnityEngine;

namespace KimodoBridge.Editor
{
    internal interface IFootRootMotionCostEvaluator
    {
        string Name { get; }

        FootRootMotionCostBreakdown Evaluate(FootRootMotionCostContext context);
    }

    internal sealed class FootRootMotionDefaultCostEvaluator : IFootRootMotionCostEvaluator
    {
        public string Name => "Default";

        public FootRootMotionCostBreakdown Evaluate(FootRootMotionCostContext context)
        {
            FootRootMotionSolverSettings settings = context.settings ?? new FootRootMotionSolverSettings();

            FootRootMotionCostBreakdown breakdown = new FootRootMotionCostBreakdown
            {
                supportSlipXZCost = settings.supportSlipXZWeight * (context.supportSlipXZ / settings.SupportSlipXZReference),
                supportSlipYCost = settings.supportSlipYWeight * (context.supportSlipY / settings.SupportSlipYReference),
                hipResidualCost = settings.hipResidualWeight * (context.hipResidual / settings.HipResidualReference),
                rootDeltaCost = settings.rootDeltaWeight * (context.rootDeltaDeviation / settings.RootDeltaReference),
                rootYawCost = settings.rootYawWeight * (context.rootYawDeviationRadians / settings.RootYawReferenceRadians),
                phaseCost = settings.phaseWeight * ComputePhasePenalty(context)
            };
            breakdown.totalCost =
                breakdown.supportSlipXZCost +
                breakdown.supportSlipYCost +
                breakdown.hipResidualCost +
                breakdown.rootDeltaCost +
                breakdown.rootYawCost +
                breakdown.phaseCost;
            return breakdown;
        }

        private static float ComputePhasePenalty(FootRootMotionCostContext context)
        {
            float confidence = Mathf.Clamp01(context.phaseConfidence);
            switch (context.phaseHint)
            {
                case FootRootMotionPhaseHint.LeftSwing:
                    return context.supportState == FootRootMotionSupportState.LeftPlant ? confidence : 0f;
                case FootRootMotionPhaseHint.RightSwing:
                    return context.supportState == FootRootMotionSupportState.RightPlant ? confidence : 0f;
                default:
                    return 0f;
            }
        }
    }

    internal static class FootRootMotionCostEvaluatorRegistry
    {
        private static readonly IFootRootMotionCostEvaluator defaultEvaluator = new FootRootMotionDefaultCostEvaluator();

        public static IFootRootMotionCostEvaluator Resolve(FootRootMotionSolverSettings settings, IFootRootMotionCostEvaluator overrideEvaluator)
        {
            if (overrideEvaluator != null)
            {
                return overrideEvaluator;
            }

            switch (settings != null ? settings.costEvaluator : FootRootMotionCostEvaluatorKind.Default)
            {
                default:
                    return defaultEvaluator;
            }
        }
    }
}
