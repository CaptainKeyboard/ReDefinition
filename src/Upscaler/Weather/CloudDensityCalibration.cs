using System;
using System.Collections.Generic;
using ReDefinition.Core;

namespace ReDefinition.Upscaler
{
    // The two numbers of CloudDensityEstimate that nothing in EVE's code gives: the vertical optical depth of a deck of coverage 1
    // through its whole shell, and the density of an EVE cloud type that counts as 1. Both are [VERIFY] constants of the estimate
    // until the K-W1 series has run; this class holds them, the procedure that sets them and the arithmetic of the fit.
    //
    // The procedure (series K-W1, rows C24.tau_estimate_error_max and C24.tau_estimate_monotone in tests/series/rows/C24.json):
    //  1. In flight over one place with EVE's deck, the probe (LightProbe, CloudLightBlend, RtEveClouds) sets the coverage map to five or
    //     more uniform values (for instance 0.1, 0.25, 0.5, 0.75, 1) and, at each, measures the sun's light on the ground with the
    //     clouds on against off (the ratio is the transmission T of the deck along the sun ray) and notes the sun's elevation.
    //  2. At each value the estimate's shape is read: CloudDensityEstimate.Shape, the estimate with TauAtFullCoverage 1 (the coverage
    //     times the type density over the shell, times the fades), so that tau = TauAtFullCoverage x shape.
    //  3. TryFit takes the points (shape, T, elevation). The measured vertical depth of a point is -ln(T) / air mass
    //     (SunTransmission.AirMass); the fit is the least-squares factor through the origin, tau = k x shape. The worst relative
    //     error of the transmissions the factor gives against the measured ones must be 0.15 or less (the brief's bound), and the
    //     measured depths must rise with the coverage (the monotone row).
    //  4. The fit table goes to tests/data/c24-tau-fit.json (not there yet), Apply (or the constants here) takes the factor, and the
    //     [VERIFY] markers on the constants go.
    // With a type density the same fit is run once with TypeDensityReference at the density of the type in view (the probe logs the
    // densities of the body's cloud types, RtEveClouds): the reference is what makes density / reference equal 1 at the fitted point.
    internal static class CloudDensityCalibration
    {
        // [VERIFY] A guess, kept until the fit above has run: 3 means 5 % of the sun overhead through a full deck.
        internal const float ProvisionalTauAtFullCoverage = 3f;

        // The vertical optical depth of a deck of coverage 1 through its whole shell, density counted as 1 and no fade. [VERIFY]
        internal static float TauAtFullCoverage = ProvisionalTauAtFullCoverage;

        // The cloud-type density that counts as 1; 0 while the type densities are not calibrated: the density then counts as 1
        // everywhere, as the estimate did before it read them. [VERIFY]
        internal static float TypeDensityReference;

        // The bound on the transmission error of the fit (the brief's 15 %).
        internal const float ErrorBound = 0.15f;

        // One measurement of the fit: the estimate's shape at TauAtFullCoverage 1, the measured transmission along the sun ray (0 to 1)
        // and the sun's elevation in radians.
        internal struct Point
        {
            public float Shape;
            public float MeasuredTransmission;
            public float SunElevationRad;
        }

        // The result of a fit.
        internal struct Fit
        {
            // The factor on the shape: the new TauAtFullCoverage.
            public float TauAtFullCoverage;
            // The worst relative error of the transmissions the factor gives against the measured ones, and the points used.
            public float WorstRelativeError;
            public int Points;
            // Whether the measured vertical depths rise with the shape (a point may equal the one before it).
            public bool Monotone;
            public bool WithinBound { get { return Points >= 5 && Monotone && WorstRelativeError <= ErrorBound; } }
        }

        // The least-squares factor through the origin over the points with a usable shape (above 0) and transmission (above 0 and
        // below 1): tau = k x shape against the measured vertical depths. False for fewer than two usable points.
        internal static bool TryFit(IList<Point> points, out Fit fit)
        {
            fit = new Fit { TauAtFullCoverage = ProvisionalTauAtFullCoverage };
            if (points == null) return false;
            var shapes = new List<double>();
            var depths = new List<double>();
            var elevations = new List<float>();
            var measured = new List<double>();
            for (int i = 0; i < points.Count; i++)
            {
                Point p = points[i];
                if (!(p.Shape > 0f) || !(p.MeasuredTransmission > 0f) || !(p.MeasuredTransmission < 1f)) continue;
                shapes.Add(p.Shape);
                depths.Add(-Math.Log(p.MeasuredTransmission) / SunTransmission.AirMass(p.SunElevationRad));
                elevations.Add(p.SunElevationRad);
                measured.Add(p.MeasuredTransmission);
            }
            if (shapes.Count < 2) return false;
            double numerator = 0.0, denominator = 0.0;
            for (int i = 0; i < shapes.Count; i++)
            {
                numerator += shapes[i] * depths[i];
                denominator += shapes[i] * shapes[i];
            }
            double k = numerator / denominator;
            double worst = 0.0;
            bool monotone = true;
            // Ordered by shape, the depths must not fall.
            var order = new List<int>();
            for (int i = 0; i < shapes.Count; i++) order.Add(i);
            order.Sort((a, b) => shapes[a].CompareTo(shapes[b]));
            for (int i = 0; i < order.Count; i++)
            {
                int at = order[i];
                float modelled = SunTransmission.From((float)(k * shapes[at]), elevations[at]);
                worst = Math.Max(worst, Math.Abs(modelled - measured[at]) / measured[at]);
                if (i > 0 && depths[at] < depths[order[i - 1]] - 1e-9) monotone = false;
            }
            fit = new Fit { TauAtFullCoverage = (float)k, WorstRelativeError = (float)worst, Points = shapes.Count, Monotone = monotone };
            return true;
        }

        // The density factor of a cloud type: its density over TypeDensityReference, 1 while the reference is not set or the density
        // is not a usable number.
        internal static float TypeDensityFactor(float typeDensity)
        {
            if (!(TypeDensityReference > 0f) || float.IsNaN(typeDensity) || typeDensity < 0f) return 1f;
            return typeDensity / TypeDensityReference;
        }

        // Sets both numbers, for a series variant that tries a candidate and for the code that takes the fitted value.
        internal static void Apply(float tauAtFullCoverage, float typeDensityReference)
        {
            TauAtFullCoverage = tauAtFullCoverage > 0f ? tauAtFullCoverage : ProvisionalTauAtFullCoverage;
            TypeDensityReference = typeDensityReference > 0f ? typeDensityReference : 0f;
        }

        // Back to the provisional state.
        internal static void Reset()
        {
            TauAtFullCoverage = ProvisionalTauAtFullCoverage;
            TypeDensityReference = 0f;
        }
    }
}
