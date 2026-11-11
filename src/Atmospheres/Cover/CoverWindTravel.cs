using System;

namespace ReDefinition.Atmospheres
{
    // How far the drift pattern of a body has been carried by the wind, in metres east and north: the integral of the reference wind over
    // game time, scaled by CoverWind.AdvectionPerSecond. The pattern is looked up at (place - travel), so a change of the wind changes
    // the speed the pattern moves at from then on and never moves it as a whole (an absolute time times the current wind would jump by
    // dWind x 0.01 x UT metres at every change). In-session state only: nothing is saved, the pattern starts at rest with the session
    // (the cover itself comes from the field). Package D34, review finding R6.
    internal sealed class CoverWindTravel
    {
        // The longest span of game time one step integrates; a longer warp counts as this much, so that a jump does not throw the pattern.
        internal const double MaxStepSeconds = 21600.0;

        // The wind is sampled at most this often in game time (the integral is a sum of such steps).
        internal const double MinStepSeconds = 60.0;

        // The quads are built on worker threads as well as on the main thread.
        private readonly object gate = new object();
        private double lastUt = double.NaN;

        internal double EastM { get; private set; }
        internal double NorthM { get; private set; }

        // Whether a step at this game time is due (the first one always is).
        internal bool Due(double ut)
        {
            return double.IsNaN(lastUt) || ut - lastUt >= MinStepSeconds || ut < lastUt;
        }

        // Adds the travel of the span since the last step for a wind in m/s east and north. The first step only sets the time; a game time
        // that went back (a revert) sets the time and keeps the travel.
        internal void Advance(double ut, double windEastMs, double windNorthMs)
        {
            if (double.IsNaN(ut)) return;
            lock (gate)
            {
                if (!double.IsNaN(lastUt) && ut > lastUt)
                {
                    double dt = Math.Min(ut - lastUt, MaxStepSeconds);
                    EastM += windEastMs * CoverWind.AdvectionPerSecond * dt;
                    NorthM += windNorthMs * CoverWind.AdvectionPerSecond * dt;
                }
                lastUt = ut;
            }
        }

        internal void Reset()
        {
            lock (gate)
            {
                lastUt = double.NaN;
                EastM = 0;
                NorthM = 0;
            }
        }
    }
}
