using System;

namespace ReDefinition.Atmospheres
{
    // What the linked Atmospheres code (StormNoise, Mathx) asks of the rest of the Atmospheres mod: the conversion to radians.
    internal static class Geo
    {
        internal static double ToRad(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }
    }
}
