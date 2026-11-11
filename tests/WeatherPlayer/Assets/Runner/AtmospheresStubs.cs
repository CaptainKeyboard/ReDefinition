using System;

namespace ReDefinition.Atmospheres
{
    // What src/Atmospheres/StormVolumeMath.cs asks of the rest of the Atmospheres mod: the storm volume's step range and the conversion to
    // radians. The player links StormVolumeMath and StormNoise as they are.
    internal static class AtmospheresSettings
    {
        internal const int StormVolumeStepsMin = 16, StormVolumeStepsMax = 48;
    }

    internal static class Geo
    {
        internal static double ToRad(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }
    }
}
