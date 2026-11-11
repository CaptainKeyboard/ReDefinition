using System;

namespace ReDefinition.Atmospheres
{
    // The two numbers of the legacy storm effect that remain here: how far the terrain is drawn in a storm (StormCull) and the step range of the
    // legacy StormVolume. The curtain's numbers are CurtainMath's. Both callers go with StormEffect (C25).
    internal static class StormVolumeMath
    {
        // How far the terrain is drawn in a storm, m: 5 times the visibility, where the fog leaves nothing of the contrast, at least 300 m.
        internal const double CullVisibilityFactor = 5, CullMinM = 300;

        internal static double CullDistanceM(double visibilityM)
        {
            return Math.Max(CullVisibilityFactor * visibilityM, CullMinM);
        }

        // The steps of a ray, within the range the setting allows.
        internal static int Steps(int wanted)
        {
            return wanted < AtmospheresSettings.StormVolumeStepsMin ? AtmospheresSettings.StormVolumeStepsMin
                : wanted > AtmospheresSettings.StormVolumeStepsMax ? AtmospheresSettings.StormVolumeStepsMax : wanted;
        }
    }
}
