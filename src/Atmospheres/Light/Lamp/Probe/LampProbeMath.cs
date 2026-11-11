using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The arithmetic of the lamp probe's frame rows, apart from the engine so that the unit tests run it: luminance, the paired on/off
    // difference along a beam, the noise and control it is read against, the reach of a beam, the frames a smear or a ghost lasts and
    // the choice of the densest cloud altitude. The thresholds of the rows are in tests/series/rows/D32.json; these functions only
    // compute the figures.
    internal static class LampProbeMath
    {
        // Rec. 709 luminance of a colour.
        internal static double Luma(double r, double g, double b)
        {
            return 0.2126 * r + 0.7152 * g + 0.0722 * b;
        }

        // The mean of on[i] - off[i]; NaN for lists of different length or none.
        internal static double MeanDelta(IList<double> on, IList<double> off)
        {
            if (on == null || off == null || on.Count == 0 || on.Count != off.Count) return double.NaN;
            double sum = 0;
            for (int i = 0; i < on.Count; i++) sum += on[i] - off[i];
            return sum / on.Count;
        }

        // The mean of |a[i] - b[i]|; NaN for lists of different length or none.
        internal static double MeanAbsDelta(IList<double> a, IList<double> b)
        {
            if (a == null || b == null || a.Count == 0 || a.Count != b.Count) return double.NaN;
            double sum = 0;
            for (int i = 0; i < a.Count; i++) sum += Math.Abs(a[i] - b[i]);
            return sum / a.Count;
        }

        // The point `index` of `count` on a segment from (ax, ay) to (bx, by), spread over the fractions from `from` to `to` of it.
        internal static void SegmentPoint(double ax, double ay, double bx, double by, int index, int count, double from, double to, out double x, out double y)
        {
            double f = count <= 1 ? 0.5 * (from + to) : from + (to - from) * index / (count - 1);
            x = ax + (bx - ax) * f;
            y = ay + (by - ay) * f;
        }

        // Whether a point lies inside a width x height frame with a margin on every side.
        internal static bool InView(double x, double y, double width, double height, double margin)
        {
            return x >= margin && y >= margin && x <= width - margin && y <= height - margin;
        }

        // The beam's signal against what a measurement without a beam reads: beam / max(control, noise, floor). The control is the same
        // difference taken where no beam is; the noise is the difference between two frames of the same state.
        internal static double SignalRatio(double beam, double control, double noise, double floor)
        {
            double reference = Math.Max(Math.Max(Math.Abs(control), Math.Abs(noise)), floor);
            return beam / reference;
        }

        // The share of the samples, counted from the first (the lamp), over which the beam stays at or above `fraction` of its peak
        // without a gap; 0 for no beam. The reach of a beam along its axis in units of the sampled length.
        internal static double Reach(IList<double> deltas, double fraction)
        {
            if (deltas == null || deltas.Count == 0) return 0;
            double peak = 0;
            for (int i = 0; i < deltas.Count; i++) peak = Math.Max(peak, deltas[i]);
            if (!(peak > 0)) return 0;
            int n = 0;
            while (n < deltas.Count && deltas[n] >= fraction * peak) n++;
            return n / (double)deltas.Count;
        }

        // The figure of a frame count that could not be measured (no list, lists of unequal length): far above any limit of a row, so
        // that a missing measurement never passes.
        internal const int NoMeasurement = 1000;

        // The 1-based index of the first value at or below the floor; Count + 1 when none is (the figure is "never within the window").
        internal static int FramesUntil(IList<double> values, double floor)
        {
            if (values == null) return NoMeasurement;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] <= floor) return i + 1;
            }
            return values.Count + 1;
        }

        // The frames a smear lasts: the 1-based index of the first frame from which, to the end of the window, the error of the frame with
        // the beam exceeds the error of the same frame without it by no more than the allowance (`fraction` of the beam's own
        // contribution, but never below minAllowed, the noise of the reading). A frame that is within the allowance by chance and
        // leaves it again does not end the smear. Count + 1 when the last frame is outside the allowance; a NaN counts as outside.
        internal static int SmearFrames(IList<double> errorWith, IList<double> errorWithout, double beamDelta, double fraction, double minAllowed = 0.0)
        {
            if (errorWith == null || errorWithout == null || errorWith.Count != errorWithout.Count) return NoMeasurement;
            double allowed = Math.Max(Math.Abs(beamDelta) * fraction, minAllowed);
            int lastOutside = -1;
            for (int i = 0; i < errorWith.Count; i++)
            {
                if (!(errorWith[i] - errorWithout[i] <= allowed)) lastOutside = i;
            }
            return lastOutside + 2;
        }

        // The index of the largest value when it is at least `min`; -1 otherwise (no cloud dense enough to put a lamp in).
        internal static int BestIndex(IList<double> values, double min)
        {
            int best = -1;
            double top = double.NegativeInfinity;
            if (values == null) return -1;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] > top)
                {
                    top = values[i];
                    best = i;
                }
            }
            return best >= 0 && top >= min ? best : -1;
        }

        // The mean of the values; NaN for none.
        internal static double Mean(IList<double> values)
        {
            if (values == null || values.Count == 0) return double.NaN;
            double sum = 0;
            for (int i = 0; i < values.Count; i++) sum += values[i];
            return sum / values.Count;
        }
    }
}
