using System;

namespace ReDefinition.Atmospheres
{
    // A curve over the precipitation level (0 to 1) with a 256-entry lookup table. The key points are the six levels of the look table
    // plus the value at level 0; between them the curve is a monotone cubic Hermite spline (slopes by the Fritsch-Carlson rule used by
    // PCHIP), so a curve that rises through its keys never overshoots or dips, and a flat stretch stays flat. Pure, no allocation after
    // the constructor.
    internal sealed class LookCurve
    {
        internal const int Size = 256;

        // The levels the table gives values at, after the implicit key at 0.
        internal static readonly float[] KeyLevels = { 0.1f, 0.25f, 0.4f, 0.6f, 0.8f, 1.0f };

        private readonly float[] lut = new float[Size];

        // atZero is the value at level 0; values are the six values at KeyLevels.
        internal LookCurve(float atZero, params float[] values)
        {
            if (values == null || values.Length != KeyLevels.Length) throw new ArgumentException("Six values are needed.", "values");
            var x = new float[KeyLevels.Length + 1];
            var y = new float[x.Length];
            x[0] = 0f;
            y[0] = atZero;
            for (int i = 0; i < KeyLevels.Length; i++)
            {
                x[i + 1] = KeyLevels[i];
                y[i + 1] = values[i];
            }
            float[] m = Slopes(x, y);
            for (int i = 0; i < Size; i++) lut[i] = Hermite(x, y, m, i / (float)(Size - 1));
        }

        // The value at a level; the level is clamped to 0 to 1, a NaN counts as 0. Linear between the table entries.
        internal float Evaluate(float level)
        {
            if (!(level > 0f)) return lut[0];
            if (level >= 1f) return lut[Size - 1];
            float p = level * (Size - 1);
            int i = (int)p;
            float f = p - i;
            return lut[i] + (lut[i + 1] - lut[i]) * f;
        }

        // The steepest change per unit of level between two table entries.
        internal float MaxSlope()
        {
            float max = 0f;
            for (int i = 0; i < Size - 1; i++) max = Math.Max(max, Math.Abs(lut[i + 1] - lut[i]) * (Size - 1));
            return max;
        }

        // The table entry i, for tests.
        internal float Entry(int i)
        {
            return lut[i];
        }

        private static float[] Slopes(float[] x, float[] y)
        {
            int n = x.Length;
            var h = new float[n - 1];
            var d = new float[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                h[i] = x[i + 1] - x[i];
                d[i] = (y[i + 1] - y[i]) / h[i];
            }
            var m = new float[n];
            for (int i = 1; i < n - 1; i++)
            {
                if (d[i - 1] * d[i] <= 0f)
                {
                    m[i] = 0f;
                }
                else
                {
                    float w1 = 2f * h[i] + h[i - 1], w2 = h[i] + 2f * h[i - 1];
                    m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
                }
            }
            m[0] = EndSlope(h[0], h[1], d[0], d[1]);
            m[n - 1] = EndSlope(h[n - 2], h[n - 3], d[n - 2], d[n - 3]);
            return m;
        }

        // The shape-preserving three-point slope at an end of the curve.
        private static float EndSlope(float h0, float h1, float d0, float d1)
        {
            float s = ((2f * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
            if (Math.Sign(s) != Math.Sign(d0)) return 0f;
            if (Math.Sign(d0) != Math.Sign(d1) && Math.Abs(s) > 3f * Math.Abs(d0)) return 3f * d0;
            return s;
        }

        private static float Hermite(float[] x, float[] y, float[] m, float t)
        {
            int k = x.Length - 2;
            for (int i = 0; i < x.Length - 1; i++)
            {
                if (t <= x[i + 1])
                {
                    k = i;
                    break;
                }
            }
            float h = x[k + 1] - x[k];
            float u = (t - x[k]) / h;
            float u2 = u * u, u3 = u2 * u;
            return (2f * u3 - 3f * u2 + 1f) * y[k] + (u3 - 2f * u2 + u) * h * m[k] + (-2f * u3 + 3f * u2) * y[k + 1] + (u3 - u2) * h * m[k + 1];
        }
    }
}
