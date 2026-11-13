using System;

namespace ReDefinition.Atmospheres
{
    // How many midpoint steps the cloud shadow takes in each span of a layer (package T04, the finding of T18). A span lies inside the layer's tight
    // interval, and with the height variation (T13) that interval is the type table's widened by the variation's reach on both sides: several times the
    // cloud's own thickness, while the density is zero in most of it. A fixed count per span then puts the cloud in two or three steps. Scaled keeps the
    // step the count asked for would have in the table's own interval: the count is multiplied by (widened thickness / table thickness), up to a cap.
    // Without the variation the two thicknesses are equal, the factor is 1 and both policies are the same number of steps. The shadow shader reads the
    // same count per layer (CloudShadowFrame / layer array D.x), so the CPU twin and the shader keep one definition.
    internal enum CloudShadowSteps
    {
        // The count of every span as given (the policy before the finding).
        Fixed = 0,
        // The count scaled by the widening of the layer's interval, capped at MaxScaledSteps.
        Scaled = 1
    }

    internal static class CloudShadowStepPolicy
    {
        // The tuning key (ATMO_TUNING { shadow.stepPolicy = 0 or 1 }): 0 Fixed, 1 Scaled.
        internal const string TuneKey = "shadow.stepPolicy";
        // The most steps a span takes under Scaled (the shader's loop bound).
        internal const int MaxScaledSteps = 64;
        // The compiled default: Scaled, because the measured error of the fixed policy with the variation (p90 47 percent at 8 steps) falls to the
        // measured p90 of the scaled one (tests CloudShadowQuadratureTests).
        internal const CloudShadowSteps Default = CloudShadowSteps.Scaled;

        internal static CloudShadowSteps FromNumber(float value)
        {
            return value >= 0.5f ? CloudShadowSteps.Scaled : CloudShadowSteps.Fixed;
        }

        // The steps of a span: stepsPerSpan under Fixed, and under Scaled stepsPerSpan x wide / table, rounded up, at least stepsPerSpan and at most cap
        // (a cap below stepsPerSpan leaves stepsPerSpan). A table thickness of zero or less has nothing to compare with.
        internal static int Count(CloudShadowSteps policy, int stepsPerSpan, double tableThicknessM, double wideThicknessM, int cap = MaxScaledSteps)
        {
            if (policy != CloudShadowSteps.Scaled || tableThicknessM <= 0.0 || wideThicknessM <= tableThicknessM * (1.0 + 1e-6)) return stepsPerSpan;
            int n = (int)Math.Ceiling(stepsPerSpan * wideThicknessM / tableThicknessM - 1e-9);
            return Math.Max(stepsPerSpan, Math.Min(n, cap));
        }

        // The steps of a span of this layer under the policy.
        internal static int Count(CloudShadowSteps policy, int stepsPerSpan, CloudLayerDef layer)
        {
            if (policy != CloudShadowSteps.Scaled) return stepsPerSpan;
            float tableInner, tableOuter, wideInner, wideOuter;
            layer.TableAltitudes(out tableInner, out tableOuter);
            layer.TightAltitudes(out wideInner, out wideOuter);
            return Count(policy, stepsPerSpan, tableOuter - tableInner, wideOuter - wideInner);
        }
    }
}
