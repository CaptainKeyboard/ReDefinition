using System;
using System.Globalization;
using System.Text;

namespace ReDefinition.Atmospheres
{
    // The physics warp of the wind probe's soak, chosen from the game's own table of rates instead of a fixed index (K-W3a: a fixed
    // index 2 gave a rate of 8, so the row for 3x failed on the index, not on the wind).
    internal static class SoakWarp
    {
        // The rate the soak asks for.
        internal const float RequestedRate = 3f;

        // The index of the rate nearest to the wanted one; the first of two equally near; 0 for an empty or missing table.
        internal static int IndexOfRate(float[] rates, float wanted)
        {
            if (rates == null || rates.Length == 0) return 0;
            int best = 0;
            float bestGap = Math.Abs(rates[0] - wanted);
            for (int i = 1; i < rates.Length; i++)
            {
                float gap = Math.Abs(rates[i] - wanted);
                if (gap < bestGap)
                {
                    best = i;
                    bestGap = gap;
                }
            }
            return best;
        }

        // The rate at an index; NaN outside the table.
        internal static float RateAt(float[] rates, int index)
        {
            return rates != null && index >= 0 && index < rates.Length ? rates[index] : float.NaN;
        }

        // The table for the log: "1, 2, 3, 4".
        internal static string Describe(float[] rates)
        {
            if (rates == null) return "none";
            var sb = new StringBuilder();
            for (int i = 0; i < rates.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(rates[i].ToString("0.##", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}
