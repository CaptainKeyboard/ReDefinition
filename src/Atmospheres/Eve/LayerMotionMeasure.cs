using System;

namespace ReDefinition.Atmospheres
{
    // Measures how far the picture of a layer of clouds moved between two screens (package E43, review R1): the probe cannot read the rotation back from the rate it set, so
    // it looks at the rendered pixels. Block matching on a small luminance image: the shift (in pixels) of the content of the second image against the first that minimises
    // the mean absolute difference, refined to a fraction of a pixel by a parabola through the three costs around the minimum. Pure arithmetic, tested offline with
    // synthetic shifts (MotionMeasureTests); the game only supplies the two screens (ProbeShots.Capture).
    internal static class LayerMotionMeasure
    {
        // The luminance of an RGB byte image (three bytes a pixel, row 0 at the bottom, as ProbeShots delivers it), box-filtered down to outW x outH and brought to zero mean
        // and unit spread, so the two screens of different exposure compare by their structure.
        internal static float[] Luma(byte[] rgb, int w, int h, int outW, int outH)
        {
            var o = new float[outW * outH];
            for (int y = 0; y < outH; y++)
                for (int x = 0; x < outW; x++)
                {
                    int x0 = x * w / outW, x1 = Math.Max(x0 + 1, (x + 1) * w / outW), y0 = y * h / outH, y1 = Math.Max(y0 + 1, (y + 1) * h / outH);
                    double sum = 0.0; int n = 0;
                    for (int yy = y0; yy < y1; yy++)
                        for (int xx = x0; xx < x1; xx++)
                        {
                            int i = 3 * (yy * w + xx);
                            sum += 0.2126 * rgb[i] + 0.7152 * rgb[i + 1] + 0.0722 * rgb[i + 2];
                            n++;
                        }
                    o[y * outW + x] = (float)(sum / n);
                }
            Normalise(o);
            return o;
        }

        internal static void Normalise(float[] v)
        {
            double mean = 0.0;
            for (int i = 0; i < v.Length; i++) mean += v[i];
            mean /= v.Length;
            double var = 0.0;
            for (int i = 0; i < v.Length; i++) var += (v[i] - mean) * (v[i] - mean);
            double sd = Math.Sqrt(var / v.Length);
            if (sd < 1e-6) sd = 1.0;
            for (int i = 0; i < v.Length; i++) v[i] = (float)((v[i] - mean) / sd);
        }

        // The shift (dx to the right, dy to the larger row index) of the content of b against a, searched within +-maxShift pixels. confidence in 0 .. 1: how much lower the best
        // cost is than the mean cost over all shifts (0 = the images carry no structure to match). False when the images are flat or too small.
        internal static bool Estimate(float[] a, float[] b, int w, int h, int maxShift, out double dx, out double dy, out double confidence)
        {
            dx = dy = confidence = 0.0;
            int m = maxShift;
            if (w < 4 * m + 4 || h < 4 * m + 4 || a.Length != w * h || b.Length != w * h) return false;
            int n = 2 * m + 1;
            var cost = new double[n * n];
            double total = 0.0, best = double.MaxValue; int bi = 0;
            for (int sy = -m; sy <= m; sy++)
                for (int sx = -m; sx <= m; sx++)
                {
                    double c = 0.0; int cnt = 0;
                    for (int y = m; y < h - m; y++)
                        for (int x = m; x < w - m; x++)
                        {
                            c += Math.Abs(b[(y + sy) * w + (x + sx)] - a[y * w + x]);
                            cnt++;
                        }
                    c /= cnt;
                    int idx = (sy + m) * n + (sx + m);
                    cost[idx] = c; total += c;
                    if (c < best) { best = c; bi = idx; }
                }
            double mean = total / (n * n);
            if (mean < 1e-9) return false;
            confidence = Math.Max(0.0, (mean - best) / mean);
            int bx = bi % n, by = bi / n;
            dx = bx - m; dy = by - m;
            if (bx > 0 && bx < n - 1) dx += Parabola(cost[by * n + bx - 1], cost[bi], cost[by * n + bx + 1]);
            if (by > 0 && by < n - 1) dy += Parabola(cost[(by - 1) * n + bx], cost[bi], cost[(by + 1) * n + bx]);
            return true;
        }

        // The offset of the minimum of the parabola through (-1, l), (0, c), (1, r).
        private static double Parabola(double l, double c, double r)
        {
            double den = l - 2.0 * c + r;
            if (Math.Abs(den) < 1e-12) return 0.0;
            double o = 0.5 * (l - r) / den;
            return o < -0.5 ? -0.5 : (o > 0.5 ? 0.5 : o);
        }

        // The verdict on a marker test with two opposite known rates: the shift of the picture with the positive rate minus the shift with the negative rate is twice the
        // shift of the known rate (the pattern's own motion cancels). Given the screen shift of one metre eastward on the layer (eastPx, in pixels per metre) it returns
        // the cosine between the measured motion and the east direction (1 = along east, -1 = westward, 0 = sideways or none: a wrong rotation axis) and the measured
        // eastward distance in metres.
        internal static void Verdict(double plusDx, double plusDy, double minusDx, double minusDy, double eastPxX, double eastPxY, out double cosine, out double eastMetres)
        {
            double fx = 0.5 * (plusDx - minusDx), fy = 0.5 * (plusDy - minusDy);
            double ml = Math.Sqrt(fx * fx + fy * fy), el = Math.Sqrt(eastPxX * eastPxX + eastPxY * eastPxY);
            if (ml < 1e-9 || el < 1e-12) { cosine = 0.0; eastMetres = 0.0; return; }
            cosine = (fx * eastPxX + fy * eastPxY) / (ml * el);
            eastMetres = (fx * eastPxX + fy * eastPxY) / (el * el);
        }
    }
}
