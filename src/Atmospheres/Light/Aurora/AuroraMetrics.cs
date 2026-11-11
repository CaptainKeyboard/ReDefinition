using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // Package D38. The three artefact metrics of the earlier aurora layer and the contribution mask they are read from, on plain float
    // arrays (row 0 at the bottom, as Unity reads a texture), so that the weather player, the series probe in the game and the unit
    // tests compute the same numbers. The mask is the positive part of (shell on minus shell off) of one luminance image, taken with an
    // identical camera, state and clock.
    //   lid    the largest step between adjacent row means of the mask over the peak row mean (a hard lid is one big step);
    //   drip   the share of lit columns whose lowest lit row hangs more than 6 % of the height below the median of the columns within
    //          10 of it (vertical drips below the base line);
    //   band   the mean mask in the rows below a view elevation (3 degrees) over the peak row mean (the horizon band).
    // A pose whose peak row mean is under PeakMin is too dim to judge: the three numbers of a dim mask are not rows of the table.
    internal static class AuroraMetrics
    {
        // The peak row mean (of the luminance) from which a pose is judged.
        internal const float PeakMin = 0.004f;

        // The share of the mask's top that counts as lit in the drip metric, and the sag in picture heights that counts as a drip.
        internal const float DripLitShare = 0.25f, DripSag = 0.06f;
        internal const int DripWindow = 10;

        internal struct Result
        {
            public double Peak;
            public float Lid, Drip, Band;
            public bool Judged;
        }

        // The positive part of the difference of two luminance images of the same size.
        internal static float[] Mask(float[] on, float[] off)
        {
            var m = new float[on.Length];
            for (int i = 0; i < m.Length; i++) m[i] = Math.Max(0f, on[i] - off[i]);
            return m;
        }

        internal static double[] RowMeans(float[] mask, int width, int height)
        {
            var rows = new double[height];
            for (int y = 0; y < height; y++)
            {
                double sum = 0;
                for (int x = 0; x < width; x++) sum += mask[y * width + x];
                rows[y] = sum / width;
            }
            return rows;
        }

        internal static double PeakRowMean(float[] mask, int width, int height)
        {
            double peak = 0;
            foreach (double r in RowMeans(mask, width, height)) peak = Math.Max(peak, r);
            return peak;
        }

        // The largest step between two adjacent rows of the row means, over the peak row mean.
        internal static float LidStep(float[] mask, int width, int height)
        {
            double[] rows = RowMeans(mask, width, height);
            double peak = 0, step = 0;
            for (int y = 0; y < height; y++) peak = Math.Max(peak, rows[y]);
            for (int y = 1; y < height; y++) step = Math.Max(step, Math.Abs(rows[y] - rows[y - 1]));
            return peak > 0 ? (float)(step / peak) : 0f;
        }

        // The share of columns (of the lit ones) whose lowest lit row hangs more than DripSag of the height below the median of the
        // columns round it.
        internal static float DripShare(float[] mask, int width, int height)
        {
            float top = 0f;
            foreach (float v in mask) top = Math.Max(top, v);
            float threshold = DripLitShare * top;
            var lowest = new int[width];
            for (int x = 0; x < width; x++)
            {
                lowest[x] = -1;
                for (int y = 0; y < height; y++)
                {
                    if (mask[y * width + x] > threshold)
                    {
                        lowest[x] = y;
                        break;
                    }
                }
            }
            int lit = 0, drips = 0;
            var window = new List<int>();
            for (int x = 0; x < width; x++)
            {
                if (lowest[x] < 0) continue;
                lit++;
                window.Clear();
                for (int k = Math.Max(0, x - DripWindow); k <= Math.Min(width - 1, x + DripWindow); k++)
                    if (lowest[k] >= 0) window.Add(lowest[k]);
                window.Sort();
                int median = window[window.Count / 2];
                if (median - lowest[x] > DripSag * height) drips++;
            }
            return lit > 0 ? drips / (float)lit : 0f;
        }

        // The view elevation (degrees over the horizontal) of a row's centre when the camera looks up by viewElevation degrees and has a
        // vertical field of view of fovY degrees (a pinhole camera without roll, the centre column).
        internal static double RowElevationDeg(int row, int height, double viewElevationDeg, double fovYDeg)
        {
            double tanHalf = Math.Tan(fovYDeg * 0.5 * Math.PI / 180.0);
            double ndc = (row - (height - 1) * 0.5) / (height * 0.5);
            return viewElevationDeg + Math.Atan(ndc * tanHalf) * 180.0 / Math.PI;
        }

        // The mean mask in the rows below the elevation limit over the peak row mean.
        internal static float BandShare(float[] mask, int width, int height, double viewElevationDeg, double fovYDeg, double limitDeg, double peak)
        {
            double sum = 0;
            int count = 0;
            for (int y = 0; y < height; y++)
            {
                if (RowElevationDeg(y, height, viewElevationDeg, fovYDeg) >= limitDeg) continue;
                for (int x = 0; x < width; x++) sum += mask[y * width + x];
                count += width;
            }
            return count > 0 && peak > 0 ? (float)(sum / count / peak) : 0f;
        }

        // All three metrics of one pose from the on and off luminance images.
        internal static Result Measure(float[] on, float[] off, int width, int height, double viewElevationDeg, double fovYDeg)
        {
            return MeasureMask(Mask(on, off), width, height, viewElevationDeg, fovYDeg);
        }

        internal static Result MeasureMask(float[] mask, int width, int height, double viewElevationDeg, double fovYDeg)
        {
            var r = new Result { Peak = PeakRowMean(mask, width, height) };
            r.Judged = r.Peak >= PeakMin;
            if (!r.Judged) return r;
            r.Lid = LidStep(mask, width, height);
            r.Drip = DripShare(mask, width, height);
            r.Band = BandShare(mask, width, height, viewElevationDeg, fovYDeg, 3.0, r.Peak);
            return r;
        }

        // The mean of the mask inside a rectangle (pixel bounds, upper ends exclusive), or outside it within the rows it spans.
        internal static double MeanMask(float[] mask, int width, int height, int x0, int y0, int x1, int y1, bool inside)
        {
            double sum = 0;
            int count = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool within = x >= x0 && x < x1 && y >= y0 && y < y1;
                    if (within != inside) continue;
                    if (!inside && (y < y0 || y >= y1)) continue;
                    sum += mask[y * width + x];
                    count++;
                }
            }
            return count > 0 ? sum / count : 0;
        }

        // The centre (pixel) of the block of the given size with the highest mean mask: where the shell is brightest, for an occluder.
        internal static void BrightestBlock(float[] mask, int width, int height, int block, out int cx, out int cy)
        {
            cx = width / 2;
            cy = height / 2;
            int step = Math.Max(1, block / 2);
            double best = -1;
            for (int y0 = 0; y0 + block <= height; y0 += step)
            {
                for (int x0 = 0; x0 + block <= width; x0 += step)
                {
                    double sum = 0;
                    for (int y = y0; y < y0 + block; y++)
                        for (int x = x0; x < x0 + block; x++) sum += mask[y * width + x];
                    if (sum > best)
                    {
                        best = sum;
                        cx = x0 + block / 2;
                        cy = y0 + block / 2;
                    }
                }
            }
        }

        // The share of pixels whose mask value is above a level (how much of the picture the shell paints).
        internal static double LitShare(float[] mask, float level)
        {
            if (mask == null || mask.Length == 0) return 0;
            int lit = 0;
            for (int i = 0; i < mask.Length; i++)
                if (mask[i] > level) lit++;
            return lit / (double)mask.Length;
        }

        // The radius in pixels of a sphere seen under an angular radius (radians) at the centre of a pinhole picture of the given height
        // and vertical field of view; a sphere off the centre is wider, so this is the smaller bound.
        internal static double SphereRadiusPx(double angularRadiusRad, double fovYDeg, int height)
        {
            double tanHalf = Math.Tan(fovYDeg * 0.5 * Math.PI / 180.0);
            return Math.Tan(angularRadiusRad) / tanHalf * height * 0.5;
        }

        // The median and the spread (largest distance of the 10th and 90th percentile from it) of a sample.
        internal static void MedianAndSpread(IList<float> values, out float median, out float spread)
        {
            median = 0f;
            spread = 0f;
            if (values == null || values.Count == 0) return;
            var sorted = new List<float>(values);
            sorted.Sort();
            median = sorted[sorted.Count / 2];
            float low = sorted[(int)(0.1 * (sorted.Count - 1))], high = sorted[(int)(0.9 * (sorted.Count - 1))];
            spread = Math.Max(Math.Abs(median - low), Math.Abs(high - median));
        }
    }
}
