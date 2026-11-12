using ReDefinition.Atmospheres.Audio;

namespace ReDefinition.Atmospheres
{
    // The switches and numbers of the lightning (package T07) until the settings package (T10) carries them: lightning is off until the
    // owner accepts the look (it is a quality cost with lights and a bolt per flash), the photosensitivity limit is on at the common safety
    // threshold of three flashes in any second. The probe and the tests set the fields; the settings package will replace the reads.
    internal static class LightningSwitches
    {
        internal const bool EnabledDefault = false;
        internal const int MaxBumpsPerSecondDefault = 3;

        // Whether lightning runs at all (lightning events, bolts, light, thunder).
        internal static bool Enabled = EnabledDefault;
        // The photosensitivity limit: strokes that may begin in any one second (1 to 3).
        internal static int MaxBumpsPerSecond = MaxBumpsPerSecondDefault;
        // The brightness of the bolts and of the light 0 to 1 (a calmer setting for a player who finds the flashes too hard).
        internal static float Brightness = 1f;
        // The bolt drawn as a mesh, the light on the terrain and the vessels, the cloud flash, the thunder.
        internal static bool Bolts = true, TerrainLight = true, CloudFlash = true, Thunder = true;
        // The thunder's numbers: the realistic delay switch, the speed of sound and the volume.
        internal static readonly ThunderSettings ThunderSettings = new ThunderSettings();
        // The flashes within this distance in m get a bolt; the farther ones light the clouds only.
        internal const double BoltRangeM = 60000.0;
        // The seconds between scans of the lattice around the camera.
        internal const float ScanIntervalS = 20f;
        // The peak light of the cloud flash in the units of the sun's colour, and the reach of the terrain light in m.
        internal const float CloudPeak = 1.6f;
        internal const float TerrainLightRangeM = 9000f, TerrainLightPeak = 7f;

        internal static void Reset()
        {
            Enabled = EnabledDefault;
            MaxBumpsPerSecond = MaxBumpsPerSecondDefault;
            Brightness = 1f;
            Bolts = TerrainLight = CloudFlash = Thunder = true;
            ThunderSettings.RealisticDelay = true;
            ThunderSettings.SpeedOfSoundMs = 343.2;
            ThunderSettings.Volume = 1f;
        }
    }
}
