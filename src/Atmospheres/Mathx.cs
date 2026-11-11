using System;

namespace ReDefinition.Atmospheres
{
    // The small numeric helpers the atmospheres share: clamp, lerp, smooth step and fraction, for double, float and int. Pure numbers, no
    // engine type, so that the tests and the worker thread use them too.
    internal static class Mathx
    {
        // A value held between min and max. NaN passes through unchanged.
        internal static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }

        // A value held between min and max; NaN gives min.
        internal static double ClampOrMin(double value, double min, double max)
        {
            if (double.IsNaN(value)) return min;
            return value < min ? min : value > max ? max : value;
        }

        internal static float Clamp(float value, float min, float max)
        {
            return value < min ? min : value > max ? max : value;
        }

        internal static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }

        internal static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }

        internal static double Clamp01(double value)
        {
            return value < 0 ? 0 : value > 1 ? 1 : value;
        }

        internal static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        internal static float Lerp(float a, float b, float t)
        {
            return a + (b - a) * t;
        }

        // 0 below from, 1 above to, a smooth curve between (3t^2 - 2t^3).
        internal static double SmoothStep(double from, double to, double value)
        {
            double t = Clamp((value - from) / (to - from), 0, 1);
            return t * t * (3 - 2 * t);
        }

        // The part of a number after the point, 0 to 1 (below 0 counts from the floor).
        internal static float Frac(float value)
        {
            return value - (float)Math.Floor(value);
        }
    }
}
