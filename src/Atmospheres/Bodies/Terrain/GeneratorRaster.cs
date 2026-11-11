using System;
using System.Diagnostics;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The terrain heights of a body for the weather generator's ocean and moisture-source raster: GeneratorRaster.Width x Height texels
    // (360 x 180, one degree, the grid of the B09 dumps), row 0 at the north pole, column 0 at longitude -180, texel centres. Read from the
    // PQS a few milliseconds a frame until it is complete (the sibling of OrbitHeights, which reads a coarser grid for the orbit snow).
    // The generator turns the heights and the body's ocean flag into the ocean fraction and the moisture source (GeneratorBodies).
    internal sealed class GeneratorRaster
    {
        internal const int Width = 360;
        internal const int Height = 180;
        // The time one frame spends on reading, in milliseconds.
        internal const long BudgetMilliseconds = 2;

        private float[] heights;
        private int done;
        private bool failedLogged;

        // Whether the grid is complete.
        internal bool Complete { get; private set; }

        // The heights in metres above the sea level of the body, row by row; null while the grid is not complete.
        internal float[] HeightsIfComplete
        {
            get { return Complete ? heights : null; }
        }

        // The share of the grid read so far, 0 to 1.
        internal double Progress
        {
            get { return Complete ? 1.0 : heights == null ? 0.0 : done / (double)heights.Length; }
        }

        // The latitude and longitude in degrees of a texel centre.
        internal static double LatitudeOf(int row)
        {
            return 90.0 - (row + 0.5) * 180.0 / Height;
        }

        internal static double LongitudeOf(int column)
        {
            return -180.0 + (column + 0.5) * 360.0 / Width;
        }

        // Reads texels until the frame's budget is spent or the grid is complete. Nothing is read while the sphere is not started: the
        // generator does not start a body's sphere (that would load its terrain).
        internal void Sample(CelestialBody body)
        {
            if (Complete || body == null) return;
            PQS pqs = body.pqsController;
            if (pqs == null || !pqs.isStarted) return;
            if (heights == null) heights = new float[Width * Height];
            var watch = Stopwatch.StartNew();
            try
            {
                while (done < heights.Length && watch.ElapsedMilliseconds < BudgetMilliseconds)
                {
                    int row = done / Width, column = done % Width;
                    double h = pqs.GetSurfaceHeight(body.GetRelSurfaceNVector(LatitudeOf(row), LongitudeOf(column))) - pqs.radius;
                    heights[done++] = double.IsNaN(h) || double.IsInfinity(h) ? 0f : (float)h;
                }
            }
            catch (Exception e)
            {
                // The sphere is being rebuilt: start the grid again later.
                done = 0;
                if (!failedLogged)
                {
                    failedLogged = true;
                    AtmospheresLog.Say("generator: the heights of " + body.name + " could not be read yet (" + CompatibilityLog.Reason(e) + "); the weather waits for them.");
                }
                return;
            }
            if (done >= heights.Length)
            {
                Complete = true;
                AtmospheresLog.Say("generator: heights of " + body.name + " read, " + Width + "x" + Height + " samples.");
            }
        }
    }
}
