using System;

namespace ReDefinition.Atmospheres
{
    // The figures of the frost's A/B in the game's picture (series K-W3b): two luminance images of the same state, one with the frost
    // drawn and one without, and the interior mask. Pure; the images are rows of width w, row 0 at the bottom (a render texture read
    // back). Used by the series probe; tested without a game.
    internal static class FrostAbMetrics
    {
        // A pixel counts as changed when its luminance differs by more than this share of the brighter of the two (with a floor, so
        // that a dark pixel does not flip on noise).
        internal const float RelativeChange = 0.10f;
        internal const float Floor = 0.05f;

        internal static bool Changed(float a, float b)
        {
            float d = Math.Abs(a - b);
            return d > RelativeChange * Math.Max(Math.Max(a, b), Floor);
        }

        // The share of the pixels that changed between two images of the same size.
        internal static float ChangedShare(float[] a, float[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            if (n == 0) return float.NaN;
            int changed = 0;
            for (int i = 0; i < n; i++) if (Changed(a[i], b[i])) changed++;
            return changed / (float)n;
        }

        // The share of changed pixels among those where the mask is covered (inside) or not (outside). covered is a mask of maskW x
        // maskH (any size: it is read at the image pixel's place), values above 0.5 are covered. NaN where no pixel is in the set.
        internal static float ChangedShareIn(float[] a, float[] b, int w, int h, float[] covered, int maskW, int maskH, bool inside)
        {
            int n = 0, changed = 0;
            for (int y = 0; y < h; y++)
            {
                int my = Math.Min(maskH - 1, y * maskH / h);
                for (int x = 0; x < w; x++)
                {
                    int mx = Math.Min(maskW - 1, x * maskW / w);
                    bool isIn = covered[my * maskW + mx] > 0.5f;
                    if (isIn != inside) continue;
                    n++;
                    if (Changed(a[y * w + x], b[y * w + x])) changed++;
                }
            }
            return n == 0 ? float.NaN : changed / (float)n;
        }

        // The Pearson correlation of the size of the change (the absolute difference of the two images, relative to the brighter) with a
        // predicted opacity map of the same size, once as it is and once with the prediction turned over vertically. The frost is read
        // the right way up when normal is clearly above flipped.
        internal static void Orientation(float[] frosted, float[] clear, float[] predicted, int w, int h, out float normal, out float flipped)
        {
            var change = new float[w * h];
            for (int i = 0; i < change.Length; i++)
            {
                float hi = Math.Max(Math.Max(frosted[i], clear[i]), Floor);
                change[i] = Math.Min(1f, Math.Abs(frosted[i] - clear[i]) / hi);
            }
            var turned = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    turned[y * w + x] = predicted[(h - 1 - y) * w + x];
            normal = Correlation(change, predicted);
            flipped = Correlation(change, turned);
        }

        internal static float Correlation(float[] x, float[] y)
        {
            int n = Math.Min(x.Length, y.Length);
            if (n == 0) return float.NaN;
            double mx = 0, my = 0;
            for (int i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
            mx /= n;
            my /= n;
            double sxy = 0, sxx = 0, syy = 0;
            for (int i = 0; i < n; i++)
            {
                double dx = x[i] - mx, dy = y[i] - my;
                sxy += dx * dy;
                sxx += dx * dx;
                syy += dy * dy;
            }
            if (sxx <= 1e-12 || syy <= 1e-12) return 0f;
            return (float)(sxy / Math.Sqrt(sxx * syy));
        }

        // Luminance (Rec. 709) of a linear colour.
        internal static float Luma(float r, float g, float b)
        {
            return 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }
    }
}
