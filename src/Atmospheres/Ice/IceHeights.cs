using System;
using System.Diagnostics;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The planet's heights for the coast map: a grid of ShoreGrid.Width x ShoreGrid.Height texels (the sphere's frame, row 0 at the
    // south pole, like OrbitMask) read from the PQS a few at a time, a little each frame, until it is complete; then ShoreGrid.Build
    // makes the coast map on a worker. Once per load and body; the cost is the PQS height function, 0.5 million calls, within a budget
    // of BudgetMilliseconds a frame.
    internal sealed class IceHeights
    {
        internal const long BudgetMilliseconds = 1;
        // The tuning key of the budget; the series raises it so that the grid is complete within the start.
        internal const string BudgetKey = "ice.heightBudgetMs";

        private float[] heights;
        private int done;
        private bool failedLogged;

        internal bool Complete { get; private set; }

        // The share read, 0 to 1.
        internal double Progress
        {
            get { return Complete ? 1.0 : heights == null ? 0.0 : done / (double)heights.Length; }
        }

        // The grid; null until it is complete.
        internal float[] Grid
        {
            get { return Complete ? heights : null; }
        }

        // Reads texels until the frame's budget is spent or the grid is complete. Nothing is read while the sphere is not started.
        internal void Sample(PQS pqs)
        {
            if (Complete) return;
            if (pqs == null || !pqs.isStarted) return;
            if (heights == null) heights = new float[ShoreGrid.Width * ShoreGrid.Height];
            var watch = Stopwatch.StartNew();
            long budget = Math.Max(1L, (long)Tuning.Get(BudgetKey, BudgetMilliseconds));
            try
            {
                while (done < heights.Length && watch.ElapsedMilliseconds < budget)
                {
                    int row = done / ShoreGrid.Width, column = done % ShoreGrid.Width;
                    double x, y, z;
                    OrbitMask.Direction((column + 0.5) / ShoreGrid.Width, (row + 0.5) / ShoreGrid.Height, out x, out y, out z);
                    double h = pqs.GetSurfaceHeight(new Vector3d(x, y, z)) - pqs.radius;
                    heights[done++] = double.IsNaN(h) || double.IsInfinity(h) ? float.NaN : (float)h;
                }
            }
            catch (Exception e)
            {
                // The heights are not readable now (the sphere is being rebuilt): start the grid again later.
                done = 0;
                if (!failedLogged)
                {
                    failedLogged = true;
                    AtmospheresLog.Say("sea ice: the planet's heights could not be read yet (" + CompatibilityLog.Reason(e) + ").");
                }
                return;
            }
            if (done >= heights.Length)
            {
                Complete = true;
                AtmospheresLog.Say("sea ice: planet heights read, " + ShoreGrid.Width + "x" + ShoreGrid.Height + " samples.");
            }
        }

        // Forgets the grid (another save, another sphere).
        internal void Reset()
        {
            heights = null;
            done = 0;
            Complete = false;
        }
    }
}
