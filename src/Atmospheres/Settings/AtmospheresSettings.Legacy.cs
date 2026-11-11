using System;

namespace ReDefinition.Atmospheres
{
    // The members of the settings of the storm system before the contract: names that the code of the packages still to come uses. The
    // aliases forward to the settings that replaced them; the plain fields belong to features that are deleted and are not written to the
    // file. The package that deletes the last caller of a member deletes its line here (the table is in CONTRACT.md, "Settings audit").
    internal static partial class AtmospheresSettings
    {
        // Where the cloud of a storm cell lived when it was an EVE cloud (stormClouds; deleted with the cell layers).
        internal enum StormCloudModes
        {
            Existing,
            Own
        }

        // Ranges of the old settings, used by the code of the old storm system.
        internal const float YearLengthFactorMin = 0.01f;
        internal const float YearLengthFactorMax = 10f;
        internal const float StormFrequencyMax = 2f;
        internal const float StormCellRadiusMinKm = 5f, StormCellRadiusMaxKm = 25f, StormCellRadiusDefaultKm = 12f;
        internal const int StormVolumeStepsMin = 16, StormVolumeStepsMax = 48, StormVolumeStepsDefault = 28;
        internal const int StormMaxCellsMin = 1, StormMaxCellsMax = 12, StormMaxCellsDefault = 6;

        // --- Aliases of settings that were renamed (key in the comment) ---

        // storms -> weatherEffects.
        [Obsolete("Replaced by WeatherEffects.")]
        internal static bool StormsEnabled
        {
            get { return WeatherEffects; }
            set { SetNumber(Keys.WeatherEffects, value ? 1.0 : 0.0); }
        }

        // stormFrequency -> heavyShare.
        [Obsolete("Renamed to HeavyShare.")]
        internal static float StormFrequency
        {
            get { return HeavyShare; }
            set { SetNumber(Keys.HeavyShare, value); }
        }

        // stormSeed -> genSeed.
        [Obsolete("Renamed to GenSeed.")]
        internal static int StormSeed
        {
            get { return GenSeed; }
            set { SetNumber(Keys.GenSeed, value); }
        }

        // stormVisibility -> blizzardVisibilityM.
        [Obsolete("Renamed to BlizzardVisibilityM.")]
        internal static float StormVisibilityM
        {
            get { return BlizzardVisibilityM; }
            set { SetNumber(Keys.BlizzardVisibilityM, value); }
        }

        // stormVeil -> veilStrength.
        [Obsolete("Renamed to VeilStrength.")]
        internal static float StormVeil
        {
            get { return VeilStrength; }
            set { SetNumber(Keys.VeilStrength, value); }
        }

        // stormSkyDarkening -> skyDimming.
        [Obsolete("Renamed to SkyDimming.")]
        internal static float StormSkyDarkening
        {
            get { return SkyDimming; }
            set { SetNumber(Keys.SkyDimming, value); }
        }

        // stormEveRainToSnow -> typeByTemperature.
        [Obsolete("Renamed to TypeByTemperature.")]
        internal static bool StormEveRainToSnow
        {
            get { return TypeByTemperature; }
            set { SetNumber(Keys.TypeByTemperature, value ? 1.0 : 0.0); }
        }

        // stormVolume -> volumePass.
        [Obsolete("Renamed to VolumePass.")]
        internal static bool StormVolume
        {
            get { return VolumePass; }
            set { SetNumber(Keys.VolumePass, value ? 1.0 : 0.0); }
        }

        // stormVolumeSteps -> volumeSteps.
        [Obsolete("Renamed to VolumeSteps.")]
        internal static int StormVolumeSteps
        {
            get { return VolumeSteps; }
            set { SetNumber(Keys.VolumeSteps, value); }
        }

        // forceStorm -> forceLevel.
        [Obsolete("Renamed to ForceLevel.")]
        internal static float ForceStorm
        {
            get { return ForceLevel; }
            set { SetNumber(Keys.ForceLevel, value); }
        }

        // --- Members of deleted features: not in the file ---

        // winterSnowLine: the snow line follows the field's temperature. Only StormEffect (package C20) still reads it, together with
        // the SnowMask shim; both go with StormEffect.
        [Obsolete("Deleted with SnowMask: the cold is the field's.")]
        internal static float WinterSnowLine;

        // stormEveSnow (cloudSource and useEveWeather decide).
        [Obsolete("Deleted: see cloudSource and useEveWeather.")]
        internal static bool StormEveSnow;

        // stormCellRadiusKm and stormMaxCells: cells emerge from the simulation.
        [Obsolete("Deleted: weather systems emerge from the simulation.")]
        internal static float StormCellRadiusKm;
        [Obsolete("Deleted: weather systems emerge from the simulation.")]
        internal static int StormMaxCells;

        // stormClouds: cloudSource replaced the cell layers.
        [Obsolete("Deleted: see cloudSource.")]
        internal static StormCloudModes StormClouds;

        // forceStormCell: a forced cell is a test forcing of the field (ISurfaceForcing).
        [Obsolete("Deleted: see ISurfaceForcing.")]
        internal static bool ForceCellOn;
        [Obsolete("Deleted: see ISurfaceForcing.")]
        internal static double ForceCellLatitude, ForceCellLongitude;
        [Obsolete("Deleted: see ISurfaceForcing.")]
        internal static float ForceCellRadiusKm;

        // The defaults of the plain fields.
        private static void ResetLegacy()
        {
#pragma warning disable 618
            WinterSnowLine = (float)SnowMask.DefaultWinterLineDegrees;
            StormEveSnow = true;
            StormCellRadiusKm = StormCellRadiusDefaultKm;
            StormMaxCells = StormMaxCellsDefault;
            StormClouds = StormCloudModes.Existing;
            ForceCellOn = false;
            ForceCellLatitude = 0;
            ForceCellLongitude = 0;
            ForceCellRadiusKm = StormCellRadiusDefaultKm;
#pragma warning restore 618
        }
    }
}
