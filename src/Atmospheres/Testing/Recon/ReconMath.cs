using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The arithmetic of the reconnaissance probe (series K-pre), free of the engine so that unit tests can reach it: the pixel statistics
    // of the disc and fog tests, Unity's fog formulas, the parachute drift model, and the sums over a height grid.
    internal static class ReconMath
    {
        // The width and height of the height and ocean grids of the dumps (360 x 180: one texel per degree).
        internal const int GridWidth = 360, GridHeight = 180;

        // The fraction of values within +-tolerance of their median: 1 for a flat region (an opaque disc), small for waves.
        internal static double UniformFraction(IList<float> values, float tolerance)
        {
            if (values == null || values.Count == 0) return 0;
            float median = Median(values);
            int near = 0;
            for (int i = 0; i < values.Count; i++)
            {
                if (Math.Abs(values[i] - median) <= tolerance) near++;
            }
            return near / (double)values.Count;
        }

        // The median of a list, without changing it.
        internal static float Median(IList<float> values)
        {
            var copy = new float[values.Count];
            values.CopyTo(copy, 0);
            Array.Sort(copy);
            int n = copy.Length;
            return n % 2 == 1 ? copy[n / 2] : 0.5f * (copy[n / 2 - 1] + copy[n / 2]);
        }

        // The mean of a list; 0 when it is empty.
        internal static double Mean(IList<float> values)
        {
            if (values == null || values.Count == 0) return 0;
            double sum = 0;
            for (int i = 0; i < values.Count; i++) sum += values[i];
            return sum / values.Count;
        }

        // The luminance of a linear colour (Rec. 709 weights).
        internal static float Luma(float r, float g, float b)
        {
            return 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }

        // Unity's fog factor at an eye depth: the share of the fog colour that replaces the object's colour, 0..1.
        // mode: 1 linear (start, end), 2 exp (density), 3 exp2 (density), as UnityEngine.FogMode counts.
        internal static double FogShare(int mode, double density, double start, double end, double eyeDepth)
        {
            double keep;
            switch (mode)
            {
                case 1:
                    keep = end > start ? Clamp01((end - eyeDepth) / (end - start)) : 1.0;
                    break;
                case 2:
                    keep = Math.Exp(-density * eyeDepth);
                    break;
                case 3:
                    keep = Math.Exp(-(density * eyeDepth) * (density * eyeDepth));
                    break;
                default:
                    keep = 1.0;
                    break;
            }
            return 1.0 - Clamp01(keep);
        }

        // The measured fog share of a far object from its channel value with fog on and off, corrected by the change of a near object that
        // the fog does not reach (the exposure's own drift): share = 1 - (far_on / far_off) / (ref_on / ref_off), for a black fog colour;
        // for a fog colour of channel value fogValue, the object's value is far_off*(1-share) + fogValue*share.
        internal static double MeasuredFogShare(double farOff, double farOn, double refOff, double refOn, double fogValue)
        {
            if (farOff <= 1e-6 || refOff <= 1e-6 || refOn <= 1e-6) return double.NaN;
            double exposure = refOn / refOff;
            double correctedOn = farOn / exposure;
            double denominator = farOff - fogValue;
            if (Math.Abs(denominator) < 1e-6) return double.NaN;
            return (farOff - correctedOn) / denominator;
        }

        // The horizontal drift of a parachute in a constant wind, in metres, over a time in seconds. The canopy's drag is quadratic in
        // the speed relative to the air and is balanced by gravity at the still-air descent speed: a = (g / vt^2) |u| u. Start: at rest
        // horizontally, falling at vt. The result is the distance travelled along the wind, relative to the ground.
        internal static double PredictChuteDrift(double wind, double vt, double gravity, double seconds, double step = 0.01)
        {
            return PredictChuteDriftBetween(wind, vt, gravity, 0, seconds, step);
        }

        // The same model, the distance travelled between two times after the wind began (the start state is the one at time 0).
        internal static double PredictChuteDriftBetween(double wind, double vt, double gravity, double from, double to, double step = 0.01)
        {
            if (vt <= 1e-6 || to <= from) return 0;
            double c = gravity / (vt * vt);
            double vx = 0, vz = -vt, x = 0, xFrom = 0;
            int fromStep = (int)Math.Round(from / step);
            int steps = (int)Math.Ceiling(to / step);
            for (int i = 0; i < steps; i++)
            {
                if (i == fromStep) xFrom = x;
                double ux = wind - vx, uz = -vz;
                double speed = Math.Sqrt(ux * ux + uz * uz);
                double ax = c * speed * ux;
                double az = -gravity + c * speed * uz;
                vx += ax * step;
                vz += az * step;
                x += vx * step;
            }
            return x - xFrom;
        }

        // The sum of the cell areas of a latitude-longitude grid that fall under a condition, as a fraction of the whole sphere: a row's
        // weight is the cosine of its latitude. heights are row-major, row 0 at the south pole.
        internal static double OceanFraction(float[] heights, int width, int height, float seaLevel)
        {
            if (heights == null || heights.Length != width * height) return double.NaN;
            double wet = 0, all = 0;
            for (int row = 0; row < height; row++)
            {
                double latitude = (-90.0 + (row + 0.5) * 180.0 / height) * Math.PI / 180.0;
                double weight = Math.Cos(latitude);
                for (int column = 0; column < width; column++)
                {
                    all += weight;
                    if (heights[row * width + column] < seaLevel) wet += weight;
                }
            }
            return all > 0 ? wet / all : double.NaN;
        }

        // The direction (x, y, z) of a texel centre of the grid: y is the pole axis, as OrbitMask.Direction does.
        internal static void GridDirection(int column, int row, int width, int height, out double x, out double y, out double z)
        {
            double longitude = (-180.0 + (column + 0.5) * 360.0 / width) * Math.PI / 180.0;
            double latitude = (-90.0 + (row + 0.5) * 180.0 / height) * Math.PI / 180.0;
            double c = Math.Cos(latitude);
            x = c * Math.Cos(longitude);
            y = Math.Sin(latitude);
            z = c * Math.Sin(longitude);
        }

        // Whether a monotone increasing series (strictly, within a small slack) holds.
        internal static bool IsMonotoneIncreasing(IList<double> values, double slack)
        {
            for (int i = 1; i < values.Count; i++)
            {
                if (values[i] < values[i - 1] - slack) return false;
            }
            return values.Count > 1;
        }

        // The ratio of the power in a channel with a source to the power without, as an amplitude: sqrt(max(0, with^2 - without^2)).
        internal static double AddedRms(double withSource, double without)
        {
            double power = withSource * withSource - without * without;
            return power > 0 ? Math.Sqrt(power) : 0;
        }

        // The pan index of a stereo reading: (left - right) / (left + right), 0 when both are silent.
        internal static double PanIndex(double left, double right)
        {
            double sum = left + right;
            return sum > 1e-9 ? (left - right) / sum : 0;
        }

        internal static double Clamp01(double v)
        {
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }
    }
}
