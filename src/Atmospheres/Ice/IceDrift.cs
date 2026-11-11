using System;

namespace ReDefinition.Atmospheres
{
    // The drift of the floe pattern: the pattern's noise domain is moved by a small share of the field's surface wind, so that floes and
    // leads creep with the wind (about 2 percent of the wind speed is the ice's, a rule of thumb of sea-ice drift). Pure numbers.
    //
    // The offset is the sum of (game seconds x speed) steps, taken at most once per game minute, so it is continuous: a jump in the game
    // time (a time warp over many hours, a revert, a load) moves the offset by nothing and the pattern does not leap. The wind is read at
    // one reference place of the body; the offset is a translation of the 3D domain, so the drift is exact at that place and a projection
    // of the same translation elsewhere [VERIFY by eye in the series: far from the reference the direction may look wrong; the drift is
    // a few kilometres a game day, slower than anything the eye follows].
    internal sealed class IceDrift
    {
        // The share of the wind speed that the ice moves with.
        internal const double DriftFactor = 0.02;
        // The least game time between two steps and the longest gap that still counts as running time.
        internal const double MinStepSeconds = 60.0;
        internal const double MaxStepSeconds = 6.0 * 3600.0;

        private double lastUt = double.NaN;

        // The offset in cell units, in the sphere's frame.
        internal double X { get; private set; }
        internal double Y { get; private set; }
        internal double Z { get; private set; }

        // The game time of the last step.
        internal double LastUt { get { return lastUt; } }

        // Moves the offset for the game time since the last step. wind is the surface wind at the reference place in m/s east and north;
        // east and north are the unit vectors of those directions there, in the sphere's frame; cellMetres is the floe cell's size.
        // Returns true where the offset changed.
        internal bool Advance(double ut, double windEast, double windNorth, double ex, double ey, double ez, double nx, double ny, double nz, double cellMetres)
        {
            if (double.IsNaN(ut) || double.IsInfinity(ut)) return false;
            if (double.IsNaN(lastUt))
            {
                lastUt = ut;
                return false;
            }
            double dt = ut - lastUt;
            if (dt < MinStepSeconds && dt >= 0.0) return false;
            lastUt = ut;
            if (dt < 0.0 || dt > MaxStepSeconds) return false;
            if (double.IsNaN(windEast) || double.IsNaN(windNorth) || cellMetres <= 0.0) return false;
            // The ice moves with the wind; the domain moves the other way.
            double k = -DriftFactor * dt / cellMetres;
            X += (k * (windEast * ex + windNorth * nx));
            Y += (k * (windEast * ey + windNorth * ny));
            Z += (k * (windEast * ez + windNorth * nz));
            return true;
        }

        // Puts the offset back to zero and forgets the time (another save, another body).
        internal void Reset()
        {
            X = 0.0;
            Y = 0.0;
            Z = 0.0;
            lastUt = double.NaN;
        }
    }
}
