using System;
using UnityEngine;

namespace ReDefinition.Atmospheres.Viewer
{
    // What a solo picture holds against the picture of the same pose without the cloud (the baseline): how much of the frame the cloud fills and how far it reaches up and
    // across it. Pure arithmetic on two pixel arrays (row 0 at the bottom, as Unity's GetPixels32 gives them), so that a test runs it on a known picture.
    internal struct MaskReading
    {
        internal int Width, Height;
        // The pixels judged (the frame without the vessel's box) and those of them that differ from the baseline.
        internal int Counted, Masked;
        // Masked / Counted: the share of the frame the cloud fills.
        internal double Share;
        // The vertical and the horizontal reach of the cloud as a share of the frame's height and width: from the lowest to the highest row (the leftmost to the rightmost column)
        // in which at least RowShare of the pixels differ.
        internal double RowSpan, ColumnSpan;
        // The lowest and the highest such row as a share of the frame's height from the bottom (0 where there is none).
        internal double RowLow, RowHigh;

        internal bool Valid { get { return Counted > 0; } }
    }

    internal static class PictureMask
    {
        // A pixel is cloud where the baseline differs from the picture by more than this in a channel (0 to 1): the cloud mask of the reference expectation (RE 0.5, 0.03).
        internal const double Threshold = 0.03;
        // The box around the vessel (shares of the width and the height) left out, as the numeric readout of K-T2d does: the vessel's lit side changes with the cloud's shadow.
        internal const double VesselHalfWidth = 0.12, VesselHalfHeight = 0.20;
        // A row (a column) belongs to the cloud's reach where this share of its pixels differs.
        internal const double RowShare = 0.02;

        // The row (as a share of the frame's height from the bottom) of the horizon: the camera looks elevationDeg above it and stands heightM over a body of the given radius, whose
        // horizon dips by sqrt(2 h / R); the vertical field of view is fovDeg. Below 0 or above 1 the horizon is outside the frame.
        internal static double HorizonRow(double elevationDeg, double fovDeg, double heightM, double radiusM)
        {
            double dip = Math.Sqrt(2.0 * Math.Max(0.0, heightM) / radiusM) * 180.0 / Math.PI;
            double below = (-dip - elevationDeg) * Math.PI / 180.0;
            return 0.5 + 0.5 * Math.Tan(below) / Math.Tan(0.5 * fovDeg * Math.PI / 180.0);
        }

        // Only the rows from skyFromV (a share of the height from the bottom) up count: the ground below the horizon (its cloud shadow, the sea, the grass) is not cloud.
        internal static MaskReading Measure(Color32[] picture, Color32[] baseline, int width, int height, double skyFromV = 0.0)
        {
            var r = new MaskReading { Width = width, Height = height };
            if (picture == null || baseline == null || width <= 0 || height <= 0 || picture.Length < width * height || baseline.Length < width * height) return r;
            int limit = (int)Math.Round(Threshold * 255.0);
            int[] rowHits = new int[height], rowCount = new int[height], columnHits = new int[width], columnCount = new int[width];
            for (int y = 0; y < height; y++)
            {
                double v = (y + 0.5) / height;
                if (v < skyFromV) continue;
                for (int x = 0; x < width; x++)
                {
                    double u = (x + 0.5) / width;
                    if (Math.Abs(u - 0.5) < VesselHalfWidth && Math.Abs(v - 0.5) < VesselHalfHeight) continue;
                    Color32 a = picture[y * width + x], b = baseline[y * width + x];
                    bool differs = Math.Abs(a.r - b.r) > limit || Math.Abs(a.g - b.g) > limit || Math.Abs(a.b - b.b) > limit;
                    r.Counted++;
                    rowCount[y]++;
                    columnCount[x]++;
                    if (!differs) continue;
                    r.Masked++;
                    rowHits[y]++;
                    columnHits[x]++;
                }
            }
            if (r.Counted == 0) return r;
            r.Share = r.Masked / (double)r.Counted;
            int low = -1, high = -1, left = -1, right = -1;
            for (int y = 0; y < height; y++)
            {
                if (rowCount[y] == 0 || rowHits[y] < RowShare * rowCount[y]) continue;
                if (low < 0) low = y;
                high = y;
            }
            for (int x = 0; x < width; x++)
            {
                if (columnCount[x] == 0 || columnHits[x] < RowShare * columnCount[x]) continue;
                if (left < 0) left = x;
                right = x;
            }
            if (low >= 0)
            {
                r.RowLow = low / (double)height;
                r.RowHigh = (high + 1) / (double)height;
                r.RowSpan = r.RowHigh - r.RowLow;
            }
            if (left >= 0) r.ColumnSpan = (right + 1 - left) / (double)width;
            return r;
        }
    }
}
