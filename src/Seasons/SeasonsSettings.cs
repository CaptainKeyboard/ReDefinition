using ReDefinition.Core;

namespace ReDefinition.Seasons
{
    // The settings of ReDefinition Seasons, in settings.cfg in the PluginData folder of the mod's own folder. A missing or damaged file
    // leaves the defaults; a value outside its range is brought into it, and a value that is not a number is ignored.
    internal static class SeasonsSettings
    {
        // How the snow on ground that is already built follows the season.
        internal enum GroundRefresh
        {
            // The built quads are painted again every few seconds (SnowRefresher).
            Live,
            // Kerbin's terrain is built anew when the season has moved a lot and no vessel needs the ground.
            Rebuild,
            // Only quads that are built later get the season's snow.
            Off
        }

        internal const string Mod = "ReDefinition/Seasons";
        private const string FileName = "settings.cfg";

        internal const float YearLengthFactorMin = 0.01f;
        internal const float YearLengthFactorMax = 10f;
        internal const float StormFrequencyMax = 2f;

        // Whether the seasons run at all.
        internal static bool Enabled = true;

        // The length of the season year against Kerbin's orbit period: 1 is the real year of 426 days, 0.1 makes it a tenth as long.
        internal static float YearLengthFactor = 1f;

        // How much of the snow cover the mask gives is shown, 0 to 1.
        internal static float SnowStrength = 1f;

        // Whether snow storms happen, and how often against the default, 0 to 2.
        internal static bool StormsEnabled = true;
        internal static float StormFrequency = 1f;

        // The latitude of the snow line in the winter of a hemisphere, in degrees (SnowMask.DefaultWinterLineDegrees).
        internal static float WinterSnowLine = (float)SnowMask.DefaultWinterLineDegrees;

        // How built ground follows the season.
        internal static GroundRefresh GroundRefreshMode = GroundRefresh.Live;

        // Real seconds between two passes that paint the built quads again (Live).
        internal const float RefreshSecondsMin = 1f;
        internal const float RefreshSecondsMax = 120f;
        internal static float RefreshSeconds = 5f;

        // A diagnostics setting: a season phase from 0 to 1 that replaces the clock, or a negative number for the clock. For screenshots.
        internal static float ForcePhase = -1f;

        private static bool loaded;

        // Reads the file once.
        internal static void Load()
        {
            if (loaded) return;
            loaded = true;
            Read(ModFolder.PluginFile(Mod, FileName));
        }

        internal static void Save()
        {
            Write(ModFolder.PluginFile(Mod, FileName));
        }

        // The settings from that file; they stay as they are where it is missing or damaged.
        internal static void Read(string path)
        {
            ModSettingsFile.Read(path, Apply, false);
        }

        // All settings into that file.
        internal static void Write(string path)
        {
            ModSettingsFile.Write(path, Fill);
        }

        private static bool Apply(ConfigNode root)
        {
            bool flag;
            float number;
            if (ModSettingsFile.TryBool(root, "enabled", out flag)) Enabled = flag;
            if (TryNumber(root, "yearLengthFactor", out number)) YearLengthFactor = Limit(number, YearLengthFactorMin, YearLengthFactorMax);
            if (TryNumber(root, "snowStrength", out number)) SnowStrength = Limit(number, 0f, 1f);
            if (ModSettingsFile.TryBool(root, "storms", out flag)) StormsEnabled = flag;
            if (TryNumber(root, "stormFrequency", out number)) StormFrequency = Limit(number, 0f, StormFrequencyMax);
            if (TryNumber(root, "winterSnowLine", out number))
                WinterSnowLine = Limit(number, (float)SnowMask.LowestWinterLineDegrees, (float)SnowMask.HighestWinterLineDegrees);
            string text;
            GroundRefresh mode;
            if (ModSettingsFile.TryString(root, "groundRefreshMode", out text) && EnumNames<GroundRefresh>.TryParse(text, out mode)) GroundRefreshMode = mode;
            if (TryNumber(root, "refreshSeconds", out number)) RefreshSeconds = Limit(number, RefreshSecondsMin, RefreshSecondsMax);
            if (TryNumber(root, "forcePhase", out number)) ForcePhase = number < 0 ? -1f : (float)SeasonClock.Wrap(number);
            return true;
        }

        private static void Fill(ConfigNode root)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            root.AddValue("enabled", Enabled.ToString());
            root.AddValue("yearLengthFactor", YearLengthFactor.ToString("R", culture));
            root.AddValue("snowStrength", SnowStrength.ToString("R", culture));
            root.AddValue("storms", StormsEnabled.ToString());
            root.AddValue("stormFrequency", StormFrequency.ToString("R", culture));
            root.AddValue("winterSnowLine", WinterSnowLine.ToString("R", culture));
            root.AddValue("groundRefreshMode", EnumNames<GroundRefresh>.Name(GroundRefreshMode));
            root.AddValue("refreshSeconds", RefreshSeconds.ToString("R", culture));
            root.AddValue("forcePhase", ForcePhase.ToString("R", culture));
        }

        // A finite number; NaN and infinity are not settings.
        private static bool TryNumber(ConfigNode root, string key, out float value)
        {
            return ModSettingsFile.TryFloat(root, key, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static float Limit(float value, float min, float max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
