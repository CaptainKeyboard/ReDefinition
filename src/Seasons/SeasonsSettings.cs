using System.Globalization;
using System.IO;
using ReDefinition.Core;

namespace ReDefinition.Seasons
{
    // The settings of ReDefinition Seasons, in settings.cfg in the PluginData folder of the mod's own folder. A missing or damaged file
    // leaves the defaults; a value outside its range is brought into it, and a value that is not a number is ignored. The mod writes
    // the file with the defaults when it starts and finds none (Load). The fields are in groups: season and ground, storm schedule,
    // storm look, storm cells and volume, orbit view, and diagnostics. Every field starts at its default (ResetToDefaults).
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

        // Where the cloud of a storm cell lives when it is an EVE cloud.
        // Existing: the cell is painted into the maps of the layers EVE has (EveLayerStorm).
        // Own: it is a layer of its own that the mod's config adds (EveCellLayer).
        internal enum StormCloudModes
        {
            Existing,
            Own
        }

        // The key of every setting in the file, once.
        internal static class Keys
        {
            internal const string Enabled = "enabled";
            internal const string YearLengthFactor = "yearLengthFactor";
            internal const string SnowStrength = "snowStrength";
            internal const string WinterSnowLine = "winterSnowLine";
            internal const string GroundRefreshMode = "groundRefreshMode";
            internal const string RefreshSeconds = "refreshSeconds";
            internal const string Storms = "storms";
            internal const string StormFrequency = "stormFrequency";
            internal const string StormSeed = "stormSeed";
            internal const string StormVisibility = "stormVisibility";
            internal const string StormVeil = "stormVeil";
            internal const string StormSnowfall = "stormSnowfall";
            internal const string StormSkyDarkening = "stormSkyDarkening";
            internal const string StormEveSnow = "stormEveSnow";
            internal const string StormEveRainToSnow = "stormEveRainToSnow";
            internal const string StormCellRadiusKm = "stormCellRadiusKm";
            internal const string StormVolume = "stormVolume";
            internal const string StormVolumeSteps = "stormVolumeSteps";
            internal const string StormClouds = "stormClouds";
            internal const string StormOwnFlakes = "stormOwnFlakes";
            internal const string StormCullDistance = "stormCullDistance";
            internal const string StormMaxCells = "stormMaxCells";
            internal const string OrbitSnow = "orbitSnow";
            internal const string OrbitSnowWidth = "orbitSnowWidth";
            internal const string ForceStorm = "forceStorm";
            internal const string ForceStormCell = "forceStormCell";
            internal const string ForcePhase = "forcePhase";
        }

        internal const string Mod = "ReDefinition/Seasons";
        internal const string FileName = "settings.cfg";

        // Ranges and defaults.
        internal const float YearLengthFactorMin = 0.01f;
        internal const float YearLengthFactorMax = 10f;
        internal const float StormFrequencyMax = 2f;
        internal const float StormCellRadiusMinKm = 5f, StormCellRadiusMaxKm = 25f, StormCellRadiusDefaultKm = 12f;
        internal const int StormVolumeStepsMin = 16, StormVolumeStepsMax = 48, StormVolumeStepsDefault = 28;
        internal const int StormMaxCellsMin = 1, StormMaxCellsMax = 12, StormMaxCellsDefault = 6;
        internal const int OrbitSnowWidthMin = 1024, OrbitSnowWidthMax = 4096, OrbitSnowWidthDefault = 2048;
        internal const float RefreshSecondsMin = 1f, RefreshSecondsMax = 120f, RefreshSecondsDefault = 5f;

        // --- Season and ground ---

        // Whether the seasons run at all.
        internal static bool Enabled;

        // The length of the season year against Kerbin's orbit period: 1 is the real year of 426 days, 0.1 makes it a tenth as long.
        internal static float YearLengthFactor;

        // How much of the snow cover the mask gives is shown, 0 to 1.
        internal static float SnowStrength;

        // The latitude of the snow line in the winter of a hemisphere, in degrees (SnowMask.DefaultWinterLineDegrees).
        internal static float WinterSnowLine;

        // How built ground follows the season, and the real seconds between two passes that paint the built quads again (Live).
        internal static GroundRefresh GroundRefreshMode;
        internal static float RefreshSeconds;

        // --- Storm schedule ---

        // Whether snow storms happen, and how often against the default, 0 to 2.
        internal static bool StormsEnabled;
        internal static float StormFrequency;

        // The seed of the storm schedule (StormModel).
        internal static int StormSeed;

        // --- Storm look ---

        // The visibility at full storm intensity in metres, 20 to 500.
        internal static float StormVisibilityM;

        // The alpha of the whiteout veil at full intensity, 0 to 0.9.
        internal static float StormVeil;

        // Whether falling snow is drawn in a storm.
        internal static bool StormSnowfall;

        // How dark the sky and the ambient light get in the strongest storm, 0 to 1: 0 keeps both as they are.
        internal static float StormSkyDarkening;

        // Whether a storm raises EVE's own snow particles where EVE has them.
        internal static bool StormEveSnow;

        // Whether EVE's rain (particle field, droplets, wet surfaces, lightning, rain sound) is replaced by its snow where the ground is
        // snow or ice (EveRainLink).
        internal static bool StormEveRainToSnow;

        // --- Storm cells and volume ---

        // The radius of a storm cell in km (StormCells): the setting is the typical radius, each cell has 0.6 to 1.4 times it,
        // always between the limits.
        internal static float StormCellRadiusKm;

        // Whether a storm cell is drawn as a snow curtain under its cloud base that can be seen from afar (StormVolume).
        internal static bool StormVolume;

        // The steps a ray takes through a raymarched cell, 16 to 48: more is smoother and costs more.
        internal static int StormVolumeSteps;

        // The snow cloud of a cell is always an EVE volumetric cloud; without EVE's volumetric clouds there are no cells. The cloud is
        // painted into the layers EVE has (Existing) or is a layer of the mod's own (Own).
        internal static StormCloudModes StormClouds;

        // Whether the storm draws its own falling snow (StormFlakes) also where EVE's snow particle field is drawing (the cell's layer or
        // the raised stock fields). Off, the own flakes are the fallback for when EVE has no snow field.
        internal static bool StormOwnFlakes;

        // Whether the terrain layer beyond what the fog shows is not drawn while a storm runs (StormCull).
        internal static bool StormCullDistance;

        // The most cells drawn at once: the nearest ones.
        internal static int StormMaxCells;

        // --- Orbit view ---

        // Whether Kerbin's scaled-space planet (orbit and map view) is tinted with the season, and the width in pixels of the colour
        // map it draws with, 1024 to 4096 (the original's width if that is smaller).
        internal static bool OrbitSnow;
        internal static int OrbitSnowWidth;

        // --- Diagnostics, for screenshots ---

        // A storm intensity from 0 to 1 at the camera, whatever the place and season, or a negative number for the schedule.
        internal static float ForceStorm;

        // One cell at a place at its peak, whatever the schedule and the storms switch.
        internal static bool ForceCellOn;
        internal static double ForceCellLatitude, ForceCellLongitude;
        internal static float ForceCellRadiusKm;

        // A season phase from 0 to 1 that replaces the clock, or a negative number for the clock.
        internal static float ForcePhase;

        private static bool loaded;

        static SeasonsSettings()
        {
            ResetToDefaults();
        }

        // Every setting to its default.
        internal static void ResetToDefaults()
        {
            Enabled = true;
            YearLengthFactor = 1f;
            SnowStrength = 1f;
            WinterSnowLine = (float)SnowMask.DefaultWinterLineDegrees;
            GroundRefreshMode = GroundRefresh.Live;
            RefreshSeconds = RefreshSecondsDefault;
            StormsEnabled = true;
            StormFrequency = 1f;
            StormSeed = 1;
            StormVisibilityM = StormLook.MinVisibilityDefault;
            StormVeil = StormLook.VeilDefault;
            StormSnowfall = true;
            StormSkyDarkening = StormLook.SkyDarkeningDefault;
            StormEveSnow = true;
            StormEveRainToSnow = true;
            StormCellRadiusKm = StormCellRadiusDefaultKm;
            StormVolume = true;
            StormVolumeSteps = StormVolumeStepsDefault;
            StormClouds = StormCloudModes.Existing;
            StormOwnFlakes = false;
            StormCullDistance = false;
            StormMaxCells = StormMaxCellsDefault;
            OrbitSnow = true;
            OrbitSnowWidth = OrbitSnowWidthDefault;
            ForceStorm = -1f;
            ForceCellOn = false;
            ForceCellLatitude = 0;
            ForceCellLongitude = 0;
            ForceCellRadiusKm = StormCellRadiusDefaultKm;
            ForcePhase = -1f;
        }

        // All settings as a node, to be put back with Restore (the tests keep the settings they change this way).
        internal static ConfigNode Snapshot()
        {
            var root = new ConfigNode();
            Fill(root);
            return root;
        }

        // The settings of a Snapshot: the defaults first, then every value of the node.
        internal static void Restore(ConfigNode snapshot)
        {
            ResetToDefaults();
            Apply(snapshot);
        }

        // Reads the file once. A file that is not there is made with the defaults, so that the player finds it and can edit it.
        internal static void Load()
        {
            if (loaded) return;
            loaded = true;
            LoadFrom(ModFolder.PluginFile(Mod, FileName));
        }

        // The settings of that file; where it does not exist it is written with the current settings. True if it was written.
        internal static bool LoadFrom(string path)
        {
            if (File.Exists(path))
            {
                Read(path);
                return false;
            }
            return Write(path);
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
        internal static bool Write(string path)
        {
            return ModSettingsFile.Write(path, Fill);
        }

        private static bool Apply(ConfigNode root)
        {
            bool flag;
            float number;
            int integer;
            string text;
            if (ModSettingsFile.TryBool(root, Keys.Enabled, out flag)) Enabled = flag;
            if (TryNumber(root, Keys.YearLengthFactor, out number)) YearLengthFactor = Limit(number, YearLengthFactorMin, YearLengthFactorMax);
            if (TryNumber(root, Keys.SnowStrength, out number)) SnowStrength = Limit(number, 0f, 1f);
            if (TryNumber(root, Keys.WinterSnowLine, out number))
                WinterSnowLine = Limit(number, (float)SnowMask.LowestWinterLineDegrees, (float)SnowMask.HighestWinterLineDegrees);
            GroundRefresh mode;
            if (ModSettingsFile.TryString(root, Keys.GroundRefreshMode, out text) && EnumNames<GroundRefresh>.TryParse(text, out mode)) GroundRefreshMode = mode;
            if (TryNumber(root, Keys.RefreshSeconds, out number)) RefreshSeconds = Limit(number, RefreshSecondsMin, RefreshSecondsMax);

            if (ModSettingsFile.TryBool(root, Keys.Storms, out flag)) StormsEnabled = flag;
            if (TryNumber(root, Keys.StormFrequency, out number)) StormFrequency = Limit(number, 0f, StormFrequencyMax);
            if (ModSettingsFile.TryInt(root, Keys.StormSeed, out integer)) StormSeed = integer;

            if (TryNumber(root, Keys.StormVisibility, out number)) StormVisibilityM = Limit(number, StormLook.MinVisibilityLow, StormLook.MinVisibilityHigh);
            if (TryNumber(root, Keys.StormVeil, out number)) StormVeil = Limit(number, 0f, StormLook.VeilMax);
            if (ModSettingsFile.TryBool(root, Keys.StormSnowfall, out flag)) StormSnowfall = flag;
            if (TryNumber(root, Keys.StormSkyDarkening, out number)) StormSkyDarkening = Limit(number, 0f, 1f);
            if (ModSettingsFile.TryBool(root, Keys.StormEveSnow, out flag)) StormEveSnow = flag;
            if (ModSettingsFile.TryBool(root, Keys.StormEveRainToSnow, out flag)) StormEveRainToSnow = flag;

            if (TryNumber(root, Keys.StormCellRadiusKm, out number)) StormCellRadiusKm = Limit(number, StormCellRadiusMinKm, StormCellRadiusMaxKm);
            if (ModSettingsFile.TryBool(root, Keys.StormVolume, out flag)) StormVolume = flag;
            if (ModSettingsFile.TryInt(root, Keys.StormVolumeSteps, out integer)) StormVolumeSteps = Limit(integer, StormVolumeStepsMin, StormVolumeStepsMax);
            StormCloudModes cloudMode;
            if (ModSettingsFile.TryString(root, Keys.StormClouds, out text) && EnumNames<StormCloudModes>.TryParse(text, out cloudMode)) StormClouds = cloudMode;
            if (ModSettingsFile.TryBool(root, Keys.StormOwnFlakes, out flag)) StormOwnFlakes = flag;
            if (ModSettingsFile.TryBool(root, Keys.StormCullDistance, out flag)) StormCullDistance = flag;
            if (ModSettingsFile.TryInt(root, Keys.StormMaxCells, out integer)) StormMaxCells = Limit(integer, StormMaxCellsMin, StormMaxCellsMax);

            if (ModSettingsFile.TryBool(root, Keys.OrbitSnow, out flag)) OrbitSnow = flag;
            if (ModSettingsFile.TryInt(root, Keys.OrbitSnowWidth, out integer)) OrbitSnowWidth = Limit(integer, OrbitSnowWidthMin, OrbitSnowWidthMax);

            if (TryNumber(root, Keys.ForceStorm, out number)) ForceStorm = number < 0 ? -1f : Limit(number, 0f, 1f);
            if (ModSettingsFile.TryString(root, Keys.ForceStormCell, out text)) ApplyForcedCell(text);
            if (TryNumber(root, Keys.ForcePhase, out number)) ForcePhase = number < 0 ? -1f : (float)SeasonClock.Wrap(number);
            return true;
        }

        private static void Fill(ConfigNode root)
        {
            CultureInfo culture = CultureInfo.InvariantCulture;
            root.AddValue(Keys.Enabled, Enabled.ToString());
            root.AddValue(Keys.YearLengthFactor, YearLengthFactor.ToString("R", culture));
            root.AddValue(Keys.SnowStrength, SnowStrength.ToString("R", culture));
            root.AddValue(Keys.WinterSnowLine, WinterSnowLine.ToString("R", culture));
            root.AddValue(Keys.GroundRefreshMode, EnumNames<GroundRefresh>.Name(GroundRefreshMode));
            root.AddValue(Keys.RefreshSeconds, RefreshSeconds.ToString("R", culture));
            root.AddValue(Keys.Storms, StormsEnabled.ToString());
            root.AddValue(Keys.StormFrequency, StormFrequency.ToString("R", culture));
            root.AddValue(Keys.StormSeed, StormSeed.ToString(culture));
            root.AddValue(Keys.StormVisibility, StormVisibilityM.ToString("R", culture));
            root.AddValue(Keys.StormVeil, StormVeil.ToString("R", culture));
            root.AddValue(Keys.StormSnowfall, StormSnowfall.ToString());
            root.AddValue(Keys.StormSkyDarkening, StormSkyDarkening.ToString("R", culture));
            root.AddValue(Keys.StormEveSnow, StormEveSnow.ToString());
            root.AddValue(Keys.StormEveRainToSnow, StormEveRainToSnow.ToString());
            root.AddValue(Keys.StormCellRadiusKm, StormCellRadiusKm.ToString("R", culture));
            root.AddValue(Keys.StormVolume, StormVolume.ToString());
            root.AddValue(Keys.StormVolumeSteps, StormVolumeSteps.ToString(culture));
            root.AddValue(Keys.StormClouds, EnumNames<StormCloudModes>.Name(StormClouds));
            root.AddValue(Keys.StormOwnFlakes, StormOwnFlakes.ToString());
            root.AddValue(Keys.StormCullDistance, StormCullDistance.ToString());
            root.AddValue(Keys.StormMaxCells,StormMaxCells.ToString(culture));
            root.AddValue(Keys.OrbitSnow, OrbitSnow.ToString());
            root.AddValue(Keys.OrbitSnowWidth, OrbitSnowWidth.ToString(culture));
            root.AddValue(Keys.ForceStorm, ForceStorm.ToString("R", culture));
            root.AddValue(Keys.ForceStormCell, ForceCellOn
                ? ForceCellLatitude.ToString("R", culture) + "," + ForceCellLongitude.ToString("R", culture) + "," + ForceCellRadiusKm.ToString("R", culture)
                : "off");
            root.AddValue(Keys.ForcePhase, ForcePhase.ToString("R", culture));
        }

        // The forceStormCell text "latitude,longitude,radiusKm" into the fields; anything else (such as "off") switches the cell off.
        internal static void ApplyForcedCell(string text)
        {
            double latitude, longitude, radius;
            if (StormCells.TryParseForced(text, out latitude, out longitude, out radius))
            {
                ForceCellOn = true;
                ForceCellLatitude = latitude;
                ForceCellLongitude = longitude;
                ForceCellRadiusKm = (float)radius;
            }
            else
            {
                ForceCellOn = false;
            }
        }

        // A finite number; NaN and infinity are not settings (ModSettingsFile.TryFloat takes both).
        private static bool TryNumber(ConfigNode root, string key, out float value)
        {
            return ModSettingsFile.TryFloat(root, key, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        internal static float Limit(float value, float min, float max)
        {
            return Mathx.Clamp(value, min, max);
        }

        internal static int Limit(int value, int min, int max)
        {
            return Mathx.Clamp(value, min, max);
        }
    }
}
