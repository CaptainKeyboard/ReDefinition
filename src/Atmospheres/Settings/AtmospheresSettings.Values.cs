using ReDefinition.Core;

namespace ReDefinition.Atmospheres
{
    // The typed readers of the settings. Packages read these and never write a setting except through SetNumber/SetText (the settings
    // window and the profiles do). A value outside its range cannot be here: it was brought into it when it was set.
    internal static partial class AtmospheresSettings
    {
        private static bool B(string key) => Numbers[key] != 0.0;
        private static float F(string key) => (float)Numbers[key];
        private static int I(string key) => (int)Numbers[key];
        private static T E<T>(string key) where T : struct => (T)(object)(int)Numbers[key];

        // --- General ---
        internal static bool Enabled
        {
            get { return B(Keys.Enabled); }
            set { SetNumber(Keys.Enabled, value ? 1.0 : 0.0); }
        }

        internal static bool WeatherEffects => B(Keys.WeatherEffects);

        internal static float YearLengthFactor
        {
            get { return F(Keys.YearLengthFactor); }
            set { SetNumber(Keys.YearLengthFactor, value); }
        }

        internal static SeasonMode SeasonMode => E<SeasonMode>(Keys.SeasonMode);

        internal static QualityTier QualityTier => E<QualityTier>(Keys.QualityTier);
        internal static bool QualityGovernor => B(Keys.QualityGovernor);

        // --- Clouds and frequency ---
        internal static CloudSourceKind CloudSource => E<CloudSourceKind>(Keys.CloudSource);
        internal static WeatherCompose WeatherCompose => E<WeatherCompose>(Keys.WeatherCompose);
        internal static bool UseEveWeather => B(Keys.UseEveWeather);
        internal static int EveLayerCap => I(Keys.EveLayerCap);
        internal static float LightWeatherFrequency => F(Keys.LightWeatherFrequency);
        internal static float HeavyShare => F(Keys.HeavyShare);
        internal static bool TypeByTemperature => B(Keys.TypeByTemperature);

        // --- Wind and gameplay ---
        internal static WindSourceMode WindSource => E<WindSourceMode>(Keys.WindSource);
        internal static WindInjection WindInjection => E<WindInjection>(Keys.WindInjection);
        internal static bool WindForces => B(Keys.WindForces);
        internal static float WindStrength => F(Keys.WindStrength);
        internal static bool WindOnLanded => B(Keys.WindOnLanded);
        internal static float GustStrength => F(Keys.GustStrength);
        internal static bool SolarDimming => B(Keys.SolarDimming);
        internal static float SolarDimmingStrength => F(Keys.SolarDimmingStrength);
        internal static bool VisibilityExport => B(Keys.VisibilityExport);

        // --- Simulation ---
        internal static bool GenEnabled => B(Keys.GenEnabled);
        internal static SimGridChoice SimGrid => E<SimGridChoice>(Keys.SimGrid);
        internal static int SimLevels => I(Keys.SimLevels);
        internal static float SimSliceMs => F(Keys.SimSliceMs);
        internal static int GenSeed => I(Keys.GenSeed);

        // --- Volume ---
        internal static bool VolumePass => B(Keys.VolumePass);
        internal static float VolumeResolution => F(Keys.VolumeResolution);
        internal static int VolumeSteps => I(Keys.VolumeSteps);
        internal static VolumeCompose VolumeCompose => E<VolumeCompose>(Keys.VolumeCompose);

        // --- Precipitation and look ---
        internal static float PrecipDensity => F(Keys.PrecipDensity);
        internal static bool PrecipSnow => B(Keys.PrecipSnow);
        internal static bool PrecipRain => B(Keys.PrecipRain);
        internal static bool PrecipSleet => B(Keys.PrecipSleet);
        internal static bool PrecipDust => B(Keys.PrecipDust);
        internal static bool PrecipSand => B(Keys.PrecipSand);
        internal static bool PrecipAsh => B(Keys.PrecipAsh);
        internal static bool PrecipMethane => B(Keys.PrecipMethane);
        internal static bool StormCullDistance => B(Keys.StormCullDistance);
        internal static float FogBrightness => F(Keys.FogBrightness);
        internal static float VeilStrength => F(Keys.VeilStrength);
        internal static float BlizzardVisibilityM => F(Keys.BlizzardVisibilityM);
        internal static float SkyDimming => F(Keys.SkyDimming);

        // Whether a precipitation type is drawn: the per-type switch (freezing rain follows rain).
        internal static bool PrecipTypeDrawn(PrecipType type)
        {
            switch (type)
            {
                case PrecipType.Snow: return PrecipSnow;
                case PrecipType.Rain:
                case PrecipType.FreezingRain: return PrecipRain;
                case PrecipType.Sleet: return PrecipSleet;
                case PrecipType.Dust: return PrecipDust;
                case PrecipType.Sand: return PrecipSand;
                case PrecipType.Ash: return PrecipAsh;
                case PrecipType.Methane: return PrecipMethane;
                default: return false;
            }
        }

        // --- Light in the medium, aurora, frost ---
        internal static bool LightInMedium => B(Keys.LightInMedium);
        internal static int LightCount => I(Keys.LightCount);
        internal static float LightInMediumStrength => F(Keys.LightInMediumStrength);
        internal static AuroraMode Aurora => E<AuroraMode>(Keys.Aurora);
        internal static float AuroraAmbientTint => F(Keys.AuroraAmbientTint);
        internal static bool CameraFrost => B(Keys.CameraFrost);

        // --- Audio ---
        internal static bool WeatherAudio => B(Keys.WeatherAudio);
        internal static float WeatherAudioVolume => F(Keys.WeatherAudioVolume);
        internal static bool AudioWind => B(Keys.AudioWind);
        internal static bool AudioPrecip => B(Keys.AudioPrecip);
        internal static bool AudioFootsteps => B(Keys.AudioFootsteps);
        internal static bool AudioFootstepsSurface => B(Keys.AudioFootstepsSurface);
        internal static bool AudioDistantStorm => B(Keys.AudioDistantStorm);
        internal static bool AudioQuietSnow => B(Keys.AudioQuietSnow);
        internal static AudioMuffling AudioMuffling => E<AudioMuffling>(Keys.AudioMuffling);
        internal static EveSoundPolicy EveSound => E<EveSoundPolicy>(Keys.EveSound);
        internal static AudioBedSource AudioBedSource => E<AudioBedSource>(Keys.AudioBedSource);
        internal static bool AudioProbeSolo => B(Keys.AudioProbeSolo);

        // --- Surface cover and ice ---
        internal static bool GroundCover => B(Keys.GroundCover);
        internal static float CoverStrength => F(Keys.CoverStrength);
        internal static VehicleCoverMode VehicleCover => E<VehicleCoverMode>(Keys.VehicleCover);
        internal static bool Tracks => B(Keys.Tracks);
        internal static float CoverDrift => F(Keys.CoverDrift);
        internal static SeaIceMode SeaIce => E<SeaIceMode>(Keys.SeaIce);
        internal static float IceDrawDistanceKm => F(Keys.IceDrawDistanceKm);
        internal static bool IceCollision => B(Keys.IceCollision);

        // --- Forecast, alerts and launch limits ---
        internal static bool ForecastEnabled => B(Keys.ForecastEnabled);
        internal static bool AlertsEnabled => B(Keys.AlertsEnabled);
        internal static float AlertLeadHours => F(Keys.AlertLeadHours);
        internal static LevelChoice AlertMinLevel => E<LevelChoice>(Keys.AlertMinLevel);
        internal static float AlertWindMs => F(Keys.AlertWindMs);
        internal static bool AlertWarpStop => B(Keys.AlertWarpStop);
        internal static bool AlertKac => B(Keys.AlertKac);
        internal static bool AlertMessageApp => B(Keys.AlertMessageApp);
        internal static bool AlertScreenMessage => B(Keys.AlertScreenMessage);
        internal static bool ForecastOverlay => B(Keys.ForecastOverlay);
        internal static string AlertSites => Text(Keys.AlertSites);
        internal static float ForecastHorizonHours => F(Keys.ForecastHorizonHours);
        internal static float ForecastUncertainty => F(Keys.ForecastUncertainty);
        internal static LevelChoice LaunchMaxLevel => E<LevelChoice>(Keys.LaunchMaxLevel);
        internal static float LaunchMaxWindMs => F(Keys.LaunchMaxWindMs);
        internal static float LaunchMinVisibilityM => F(Keys.LaunchMinVisibilityM);
        internal static float LaunchLeadHours => F(Keys.LaunchLeadHours);

        // The band a level choice stands for.
        internal static PrecipBand BandOf(LevelChoice choice)
        {
            return (PrecipBand)((int)choice + 1);
        }

        // --- Orbit view ---
        internal static OrbitBodiesMode OrbitBodies => E<OrbitBodiesMode>(Keys.OrbitBodies);
        internal static bool OrbitOverlay => B(Keys.OrbitOverlay);
        internal static int OrbitMapWidth => I(Keys.OrbitMapWidth);

        // --- Diagnostics, for screenshots and probes ---
        // A level from 0 to 1 at the camera whatever the field says, or a negative number for the field's own.
        internal static float ForceLevel => F(Keys.ForceLevel);
        internal static ForceType ForceType => E<ForceType>(Keys.ForceType);
        internal static string ForceBody => Text(Keys.ForceBody);
        // A season phase from 0 to 1 that replaces the clock, or a negative number for the clock.
        internal static float ForcePhase
        {
            get { return F(Keys.ForcePhase); }
            set { SetNumber(Keys.ForcePhase, value); }
        }

        internal static AtmoDebugMode AtmoDebug => E<AtmoDebugMode>(Keys.AtmoDebug);
    }
}
