using System;

namespace ReDefinition.Upscaler
{
    // The vertical optical depth of EVE's cloud deck over a place, estimated on the CPU: EVE's coverage without noise (the
    // volume's SampleCoverage, which already folds in the cloud type's height curve), times the type's density, times the deck's
    // time fade, summed over a coarse march from the place up to the deck's top. SunTransmission.From turns it into a share of the sun
    // along the ray (the along-ray value is composed by the frame driver; this class gives the vertical one only).
    //
    // Free of Unity and KSP, for the tests. The march is Steps samples at the middle of equal slices.
    internal static class CloudDensityEstimate
    {
        // The slices of the march. Coarse on purpose: the deck is a smooth shell and the estimate is read a few times a second.
        internal const int Steps = 8;

        // The vertical optical depth of a deck of coverage 1 all through its shell, density 1 and no fade. [VERIFY] a provisional value:
        // the calibration against the light probe (a fit table of five or more coverage values, series row K-W1 / C24) has not run;
        // 3 means 5 % of the sun overhead through a full deck.
        internal const float TauAtFullCoverage = 3f;

        // The deck as the march sees it.
        internal struct Deck
        {
            // The shell's lower and upper edge in metres above the surface of the body.
            public double BottomM;
            public double TopM;
            // The cloud type's density (1 where it is not read) and the deck's time fade, 0 to 1.
            public float Density;
            public float TimeFade;
        }

        // The vertical optical depth for an observer at altitudeM (metres above the surface): coverageAt gives EVE's coverage, 0 to 1,
        // at an altitude over the place. 0 for no deck, a deck below the observer, zero coverage, a bad value or a deck of no
        // thickness; never negative and never above TauAtFullCoverage times the density.
        internal static float VerticalOpticalDepth(Deck deck, double altitudeM, Func<double, float> coverageAt)
        {
            if (coverageAt == null) return 0f;
            double bottom = Math.Max(deck.BottomM, altitudeM);
            double top = deck.TopM;
            double shell = deck.TopM - deck.BottomM;
            if (!(top > bottom) || !(shell > 0.0)) return 0f;
            float density = Clamp(deck.Density, 0f, 4f);
            float fade = Clamp(deck.TimeFade, 0f, 1f);
            if (density <= 0f || fade <= 0f) return 0f;

            // Each slice carries its share of the whole shell: a coverage of 1 through the shell gives TauAtFullCoverage.
            double slice = (top - bottom) / Steps;
            double sum = 0.0;
            for (int i = 0; i < Steps; i++)
            {
                float coverage = coverageAt(bottom + (i + 0.5) * slice);
                if (float.IsNaN(coverage) || coverage <= 0f) continue;
                sum += Math.Min(coverage, 1f) * slice;
            }
            double tau = TauAtFullCoverage * density * fade * sum / shell;
            return tau <= 0.0 ? 0f : (float)tau;
        }

        private static float Clamp(float value, float low, float high)
        {
            if (float.IsNaN(value)) return low;
            return value < low ? low : value > high ? high : value;
        }
    }
}
