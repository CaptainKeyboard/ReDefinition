using System;

namespace ReDefinition.Atmospheres
{
    // A piecewise linear curve of (x, y) pairs with ascending x, held immutable: the temperature target of a body over altitude, a
    // profile over height. Outside the keys the nearest value holds.
    internal sealed class Curve
    {
        private readonly float[] x;
        private readonly float[] y;

        internal Curve(float[] xs, float[] ys)
        {
            if (xs == null || ys == null || xs.Length != ys.Length || xs.Length == 0) throw new ArgumentException("A curve needs equally many keys and values, at least one.");
            x = (float[])xs.Clone();
            y = (float[])ys.Clone();
            for (int i = 1; i < x.Length; i++)
            {
                if (!(x[i] > x[i - 1])) throw new ArgumentException("The keys of a curve must ascend.");
            }
        }

        // The number of keys.
        internal int Count
        {
            get { return x.Length; }
        }

        // The key and the value at an index.
        internal float KeyAt(int index)
        {
            return x[index];
        }

        internal float ValueAt(int index)
        {
            return y[index];
        }

        // The value at a place on the x axis.
        internal float Evaluate(float at)
        {
            if (float.IsNaN(at) || at <= x[0]) return y[0];
            int last = x.Length - 1;
            if (at >= x[last]) return y[last];
            int i = 1;
            while (x[i] < at) i++;
            float t = (at - x[i - 1]) / (x[i] - x[i - 1]);
            return y[i - 1] + (y[i] - y[i - 1]) * t;
        }
    }
}
