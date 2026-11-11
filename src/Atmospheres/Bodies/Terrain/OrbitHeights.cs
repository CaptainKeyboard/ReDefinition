using System;
using System.Diagnostics;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The planet's heights for the orbit snow (the snow line sinks with height): a coarse grid of OrbitMask.HeightWidth x HeightHeight
    // texels read from the PQS a few at a time, a little each frame, until it is complete. OrbitSnow gives the worker a copy of the
    // finished grid.
    internal sealed class OrbitHeights
    {
        // The time one frame spends on reading the planet's heights, in milliseconds.
        internal const long BudgetMilliseconds = 2;

        private float[] heights;
        private int done;
        private bool failedLogged;

        // Whether the grid is complete, and how often it has been completed (a new complete grid is a changed input of the mask).
        internal bool Complete { get; private set; }
        internal int Version { get; private set; }

        // A copy of the grid for the worker; null while it is not complete.
        internal float[] CopyIfComplete()
        {
            return heights != null && Complete ? (float[])heights.Clone() : null;
        }

        // Reads texels until the frame's budget is spent or the grid is complete. Nothing is read while the sphere is not started.
        internal void Sample(PQS pqs)
        {
            if (Complete) return;
            if (pqs == null || !pqs.isStarted) return;
            if (heights == null) heights = new float[OrbitMask.HeightWidth * OrbitMask.HeightHeight];
            var watch = Stopwatch.StartNew();
            try
            {
                while (done < heights.Length && watch.ElapsedMilliseconds < BudgetMilliseconds)
                {
                    int row = done / OrbitMask.HeightWidth, column = done % OrbitMask.HeightWidth;
                    double x, y, z;
                    OrbitMask.Direction((column + 0.5) / OrbitMask.HeightWidth, (row + 0.5) / OrbitMask.HeightHeight, out x, out y, out z);
                    double h = pqs.GetSurfaceHeight(new Vector3d(x, y, z)) - pqs.radius;
                    heights[done++] = double.IsNaN(h) || double.IsInfinity(h) ? 0f : (float)h;
                }
            }
            catch (Exception e)
            {
                // The planet's heights are not readable now (the sphere is being rebuilt): start the grid again later.
                done = 0;
                if (!failedLogged)
                {
                    failedLogged = true;
                    AtmospheresLog.Say("orbit snow: the planet's heights could not be read yet (" + CompatibilityLog.Reason(e) + "); the snow line has no height bonus until they can.");
                }
                return;
            }
            if (done >= heights.Length)
            {
                Complete = true;
                Version++;
                AtmospheresLog.Say("orbit snow: planet heights read, " + OrbitMask.HeightWidth + "x" + OrbitMask.HeightHeight + " samples.");
            }
        }
    }
}
