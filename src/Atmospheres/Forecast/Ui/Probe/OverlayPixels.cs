using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Forecast
{
    // What the pixels say about the overlay: the screen with the overlay on and the same screen with it off differ where the overlay is
    // drawn, and that difference is compared with the places the scene says it drew its rings at. This is independent of the maths that
    // placed the rings: a canvas drawn at a wrong place or a wrong scale puts the difference somewhere else and the hit share falls.
    // Pure over RGB byte arrays (three bytes a pixel, row 0 at the bottom as Unity's ReadPixels gives it), so it is tested without the game.
    internal sealed class OverlayPixelResult
    {
        // The sample points of the rings that lie on the screen, and those with a changed pixel within the reach.
        internal int Samples;
        internal int Hits;
        // The pixels that differ between the two screens (the contribution of the overlay, with whatever else moved).
        internal int ChangedPixels;
        // A white pixel where the two screens differ, row 0 at the bottom: the aid view of the overlay.
        internal byte[] Mask;

        internal double HitPct
        {
            get { return Samples > 0 ? 100.0 * Hits / Samples : 0.0; }
        }
    }

    internal static class OverlayPixels
    {
        // The summed difference of the three channels from which a pixel counts as changed (the rings are drawn with an alpha of 0.95 in
        // saturated colours, a cloud that moved between two frames changes far less).
        internal const int Threshold = 90;
        // A sample is a hit when a changed pixel lies within this many pixels of it (the ring is 2 pixels wide).
        internal const int Reach = 3;

        internal static OverlayPixelResult Measure(byte[] on, byte[] off, int width, int height, IList<IList<P2>> ringLines)
        {
            if (on == null || off == null || on.Length != off.Length || on.Length < width * height * 3) throw new ArgumentException("two screens of the same size");
            var result = new OverlayPixelResult { Mask = new byte[width * height * 3] };
            var changed = new bool[width * height];
            for (int p = 0; p < width * height; p++)
            {
                int d = Math.Abs(on[p * 3] - off[p * 3]) + Math.Abs(on[p * 3 + 1] - off[p * 3 + 1]) + Math.Abs(on[p * 3 + 2] - off[p * 3 + 2]);
                if (d < Threshold) continue;
                changed[p] = true;
                result.ChangedPixels++;
                result.Mask[p * 3] = result.Mask[p * 3 + 1] = result.Mask[p * 3 + 2] = 255;
            }
            if (ringLines == null) return result;
            for (int l = 0; l < ringLines.Count; l++)
            {
                IList<P2> points = ringLines[l];
                for (int i = 0; i < points.Count; i++)
                {
                    int x = (int)Math.Round(points[i].X), y = (int)Math.Round(points[i].Y);
                    if (x < 0 || y < 0 || x >= width || y >= height) continue;
                    result.Samples++;
                    if (AnyChanged(changed, width, height, x, y)) result.Hits++;
                }
            }
            return result;
        }

        private static bool AnyChanged(bool[] changed, int width, int height, int x, int y)
        {
            for (int dy = -Reach; dy <= Reach; dy++)
            {
                int yy = y + dy;
                if (yy < 0 || yy >= height) continue;
                for (int dx = -Reach; dx <= Reach; dx++)
                {
                    int xx = x + dx;
                    if (xx >= 0 && xx < width && changed[yy * width + xx]) return true;
                }
            }
            return false;
        }
    }
}
