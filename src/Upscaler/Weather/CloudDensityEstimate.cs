using System;

namespace ReDefinition.Upscaler
{
    // The vertical optical depth of EVE's cloud deck over a place, estimated on the CPU: EVE's coverage without noise (the
    // volume's SampleCoverage, which already folds in the cloud type's height curve and the coverage and density time fades),
    // times the cloud type's density as a factor (CloudDensityCalibration.TypeDensityFactor), times the fade of the whole deck,
    // summed over a coarse march from the place up to the deck's top. SunTransmission.From turns it into a share of the sun along
    // the ray (the along-ray value is composed by the frame driver; this class gives the vertical one only).
    //
    // The scale of the result is CloudDensityCalibration.TauAtFullCoverage, a [VERIFY] constant until the K-W1 fit has run (the
    // procedure is in that class). Free of Unity and KSP, for the tests. The march is Steps samples at the middle of equal slices.
    internal static class CloudDensityEstimate
    {
        // The slices of the march. Coarse on purpose: the deck is a smooth shell and the estimate is read a few times a second.
        internal const int Steps = 8;

        // The vertical optical depth of a deck of coverage 1 all through its shell, density 1 and no fade: the calibration's.
        internal static float TauAtFullCoverage { get { return CloudDensityCalibration.TauAtFullCoverage; } }

        // The deck as the march sees it.
        internal struct Deck
        {
            // The shell's lower and upper edge in metres above the surface of the body.
            public double BottomM;
            public double TopM;
            // The density factor where the slices do not give their own (1 where it is not read).
            public float Density;
            // The fade of the whole deck, 0 to 1. Not the coverage and density time fades: SampleCoverage has those in its value.
            public float TimeFade;
        }

        // What the march reads at one altitude: EVE's coverage, 0 to 1, and the density factor of the cloud type there.
        internal struct Slice
        {
            public float Coverage;
            public float Density;
        }

        // The vertical optical depth for an observer at altitudeM (metres above the surface): coverageAt gives EVE's coverage, 0 to 1,
        // at an altitude over the place. 0 for no deck, a deck below the observer, zero coverage, a bad value or a deck of no
        // thickness; never negative and never above TauAtFullCoverage times the density.
        internal static float VerticalOpticalDepth(Deck deck, double altitudeM, Func<double, float> coverageAt)
        {
            if (coverageAt == null) return 0f;
            return VerticalOpticalDepthBySlice(deck, altitudeM,
                height => new Slice { Coverage = coverageAt(height), Density = deck.Density });
        }

        // The same with the density factor of each slice.
        internal static float VerticalOpticalDepthBySlice(Deck deck, double altitudeM, Func<double, Slice> sliceAt)
        {
            return TauAtFullCoverage * Shape(deck, altitudeM, sliceAt);
        }

        // The estimate at TauAtFullCoverage 1: the coverage times the density factor over the shell, times the fade; what the fit of
        // CloudDensityCalibration multiplies by its factor.
        internal static float Shape(Deck deck, double altitudeM, Func<double, Slice> sliceAt)
        {
            if (sliceAt == null) return 0f;
            double bottom = Math.Max(deck.BottomM, altitudeM);
            double top = deck.TopM;
            double shell = deck.TopM - deck.BottomM;
            if (!(top > bottom) || !(shell > 0.0)) return 0f;
            float fade = Clamp(deck.TimeFade, 0f, 1f);
            if (fade <= 0f) return 0f;

            // Each slice carries its share of the whole shell: a coverage of 1 through the shell at density 1 gives 1.
            double slice = (top - bottom) / Steps;
            double sum = 0.0;
            for (int i = 0; i < Steps; i++)
            {
                Slice read = sliceAt(bottom + (i + 0.5) * slice);
                if (float.IsNaN(read.Coverage) || read.Coverage <= 0f) continue;
                float density = Clamp(read.Density, 0f, 4f);
                if (density <= 0f) continue;
                sum += Math.Min(read.Coverage, 1f) * density * slice;
            }
            double shape = fade * sum / shell;
            return shape <= 0.0 ? 0f : (float)shape;
        }

        private static float Clamp(float value, float low, float high)
        {
            if (float.IsNaN(value)) return low;
            return value < low ? low : value > high ? high : value;
        }
    }
}
