using System;

namespace ReDefinition.Atmospheres
{
    // The sampling patterns of the temporal reconstruction (package T05), without any engine type so that the tests can run them.
    internal static class ReconPattern
    {
        // The cell of an N x N block that frame `index` marches: a stride through the block's cells that visits every cell once in N * N frames
        // (the stride is coprime with N * N), and neighbouring frames land far apart.
        internal static void BlockOffset(int n, int index, out int x, out int y)
        {
            int cells = n * n;
            int stride = n == 2 ? 3 : (n == 3 ? 4 : (n == 4 ? 7 : 1));
            int cell = (index % cells) * stride % cells;
            x = cell % n;
            y = cell / n;
        }

        // The Halton sequence of base b at an index (1, 2, ..): the sub-pixel offsets of the upscalers' jitter.
        internal static float Halton(int index, int b)
        {
            float f = 1f, r = 0f;
            while (index > 0)
            {
                f /= b;
                r += f * (index % b);
                index /= b;
            }
            return r;
        }

        // The levels of the resolution slider (T14): 1 is the plain pass (its half-size march target, LayerFrame.Resolution), N in 2 to MaxLevel the N x N block of the full-size picture. BlockOffset has a stride for these only
        // (a stride coprime with N * N that sends neighbouring frames far apart); a larger block would walk in raster order and is not offered.
        internal const int MaxLevel = 4;

        internal static int ClampLevel(int level)
        {
            return Math.Max(1, Math.Min(MaxLevel, level));
        }

        // The mean number of frames until a pixel gets a new sample of its own: N * N.
        internal static int Cycle(int n)
        {
            return n * n;
        }
    }
}
