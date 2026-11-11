using System;

namespace ReDefinition.Atmospheres
{
    // Package D38. The decisions around the shell that need the setting's enum: whether it is drawn, and the ambient share. Pure.
    internal static class AuroraPolicy
    {
        // The share of the snow albedo that the aurora's faint green ambient light adds, at most 2 %.
        internal const float AmbientShareCap = 0.02f;

        // Whether the shell is drawn for the setting. Auto draws it unless a foreign aurora layer is found; Shell2D draws it regardless;
        // Volumetric asks for the EVE variant, which is not built (the shell is drawn instead, flagged by volumetricFallback); Off draws
        // nothing.
        internal static bool ShellWanted(AuroraMode setting, bool foreignFound, out bool volumetricFallback)
        {
            volumetricFallback = setting == AuroraMode.Volumetric;
            switch (setting)
            {
                case AuroraMode.Off: return false;
                case AuroraMode.Auto: return !foreignFound;
                default: return true;
            }
        }

        // The ambient share for the owner's slider (0 to 1, AtmospheresSettings.AuroraAmbientTint) and the shell's intensity (0 to 1). The
        // switch is the tuning flag aurora.ambientTint (default off): with it off the share is 0. Published for the ambient owner; nothing
        // here writes it.
        internal static float AmbientShare(bool switchedOn, float setting, float intensity)
        {
            if (!switchedOn) return 0f;
            return AmbientShareCap * Math.Max(0f, Math.Min(1f, setting)) * Math.Max(0f, Math.Min(1f, intensity));
        }
    }
}
