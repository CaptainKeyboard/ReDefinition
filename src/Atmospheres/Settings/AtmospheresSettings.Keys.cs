using System;
using System.Collections.Generic;

namespace ReDefinition.Atmospheres
{
    // The settings of ReDefinition Atmospheres: the key of every setting of the whole project, once, and the table that gives each its
    // default, range, tooltip, category, greyed-out reason and profile row. Packages only READ them (AtmospheresSettings.Values.cs); keys
    // are added here and nowhere else.
    internal static partial class AtmospheresSettings
    {
        // The key of every setting in the file, once.
        internal static class Keys
        {
            // General
            internal const string Enabled = "enabled";
            internal const string WeatherEffects = "weatherEffects";
            internal const string YearLengthFactor = "yearLengthFactor";
            internal const string SeasonMode = "seasonMode";
            internal const string QualityTier = "qualityTier";
            internal const string QualityGovernor = "qualityGovernor";
            // Clouds and frequency
            internal const string CloudSource = "cloudSource";
            internal const string WeatherCompose = "weatherCompose";
            internal const string UseEveWeather = "useEveWeather";
            internal const string EveLayerCap = "eveLayerCap";
            internal const string LightWeatherFrequency = "lightWeatherFrequency";
            internal const string HeavyShare = "heavyShare";
            internal const string TypeByTemperature = "typeByTemperature";
            // Wind and gameplay
            internal const string WindSource = "windSource";
            internal const string WindInjection = "windInjection";
            internal const string WindForces = "windForces";
            internal const string WindStrength = "windStrength";
            internal const string WindOnLanded = "windOnLanded";
            internal const string GustStrength = "gustStrength";
            internal const string SolarDimming = "solarDimming";
            internal const string SolarDimmingStrength = "solarDimmingStrength";
            internal const string VisibilityExport = "visibilityExport";
            // Simulation
            internal const string SimEnabled = "simEnabled";
            internal const string SimGrid = "simGrid";
            internal const string SimLevels = "simLevels";
            internal const string SimSliceMs = "simSliceMs";
            internal const string SimSeed = "simSeed";
            // Volume
            internal const string VolumePass = "volumePass";
            internal const string VolumeResolution = "volumeResolution";
            internal const string VolumeSteps = "volumeSteps";
            internal const string VolumeCompose = "volumeCompose";
            // Precipitation and look
            internal const string PrecipDensity = "precipDensity";
            internal const string PrecipSnow = "precipSnow";
            internal const string PrecipRain = "precipRain";
            internal const string PrecipSleet = "precipSleet";
            internal const string PrecipDust = "precipDust";
            internal const string PrecipSand = "precipSand";
            internal const string PrecipAsh = "precipAsh";
            internal const string PrecipMethane = "precipMethane";
            internal const string StormCullDistance = "stormCullDistance";
            internal const string FogBrightness = "fogBrightness";
            internal const string VeilStrength = "veilStrength";
            internal const string BlizzardVisibilityM = "blizzardVisibilityM";
            internal const string SkyDimming = "skyDimming";
            // Light in the medium, aurora, frost
            internal const string LightInMedium = "lightInMedium";
            internal const string LightCount = "lightCount";
            internal const string LightInMediumStrength = "lightInMediumStrength";
            internal const string Aurora = "aurora";
            internal const string AuroraAmbientTint = "auroraAmbientTint";
            internal const string CameraFrost = "cameraFrost";
            // Audio
            internal const string WeatherAudio = "weatherAudio";
            internal const string WeatherAudioVolume = "weatherAudioVolume";
            internal const string AudioWind = "audioWind";
            internal const string AudioPrecip = "audioPrecip";
            internal const string AudioFootsteps = "audioFootsteps";
            internal const string AudioFootstepsSurface = "audioFootstepsSurface";
            internal const string AudioDistantStorm = "audioDistantStorm";
            internal const string AudioQuietSnow = "audioQuietSnow";
            internal const string AudioMuffling = "audioMuffling";
            internal const string EveSound = "eveSound";
            internal const string AudioBedSource = "audioBedSource";
            internal const string AudioProbeSolo = "audioProbeSolo";
            // Surface cover and ice
            internal const string GroundCover = "groundCover";
            internal const string CoverStrength = "coverStrength";
            internal const string VehicleCover = "vehicleCover";
            internal const string Tracks = "tracks";
            internal const string CoverDrift = "coverDrift";
            internal const string SeaIce = "seaIce";
            internal const string IceDrawDistanceKm = "iceDrawDistanceKm";
            internal const string IceCollision = "iceCollision";
            // Forecast, alerts and launch limits
            internal const string ForecastEnabled = "forecastEnabled";
            internal const string AlertsEnabled = "alertsEnabled";
            internal const string AlertLeadHours = "alertLeadHours";
            internal const string AlertMinLevel = "alertMinLevel";
            internal const string AlertWindMs = "alertWindMs";
            internal const string AlertWarpStop = "alertWarpStop";
            internal const string AlertKac = "alertKac";
            internal const string AlertMessageApp = "alertMessageApp";
            internal const string AlertSites = "alertSites";
            internal const string ForecastHorizonHours = "forecastHorizonHours";
            internal const string ForecastUncertainty = "forecastUncertainty";
            internal const string LaunchMaxLevel = "launchMaxLevel";
            internal const string LaunchMaxWindMs = "launchMaxWindMs";
            internal const string LaunchMinVisibilityM = "launchMinVisibilityM";
            internal const string LaunchLeadHours = "launchLeadHours";
            // Orbit view
            internal const string OrbitBodies = "orbitBodies";
            internal const string OrbitOverlay = "orbitOverlay";
            internal const string OrbitMapWidth = "orbitMapWidth";
            // Diagnostics
            internal const string ForceLevel = "forceLevel";
            internal const string ForceType = "forceType";
            internal const string ForceBody = "forceBody";
            internal const string ForcePhase = "forcePhase";
            internal const string AtmoDebug = "atmoDebug";
        }

        // The groups of the settings window.
        internal static class Categories
        {
            internal const string General = "General";
            internal const string Clouds = "Clouds";
            internal const string Wind = "Wind";
            internal const string Simulation = "Simulation";
            internal const string Volume = "Volume";
            internal const string Precipitation = "Precipitation";
            internal const string Light = "Light";
            internal const string Audio = "Audio";
            internal const string Cover = "Cover";
            internal const string Ice = "Ice";
            internal const string Forecast = "Forecast";
            internal const string Orbit = "Orbit";
            internal const string Diagnostics = "Diagnostics";

            internal static readonly string[] All =
            {
                General, Clouds, Wind, Simulation, Volume, Precipitation, Light, Audio, Cover, Ice, Forecast, Orbit, Diagnostics
            };
        }

        // The ids of the reasons a setting is greyed out, with the prerequisite each names. The window shows the text of its own for each.
        internal static class GreyReasons
        {
            internal const string NeedsMaster = "needs-weather-effects";
            internal const string NeedsEve = "needs-eve";
            internal const string NeedsHost = "needs-host-api";
            internal const string NeedsSim = "needs-simulation";
            internal const string NeedsVolume = "needs-volume-pass";
            internal const string NeedsAudio = "needs-weather-audio";
            internal const string NeedsForecast = "needs-forecast";
            internal const string NeedsAlerts = "needs-alerts";
            internal const string NeedsWindForces = "needs-wind-forces";
            internal const string NeedsGroundCover = "needs-ground-cover";
            internal const string NeedsSeaIce = "needs-sea-ice";
            internal const string NeedsLightInMedium = "needs-light-in-medium";
            internal const string NeedsPrecip = "needs-precipitation";
            internal const string NeedsSolarDimming = "needs-solar-dimming";

            internal static readonly string[] All =
            {
                NeedsMaster, NeedsEve, NeedsHost, NeedsSim, NeedsVolume, NeedsAudio, NeedsForecast, NeedsAlerts, NeedsWindForces, NeedsGroundCover,
                NeedsSeaIce, NeedsLightInMedium, NeedsPrecip, NeedsSolarDimming
            };
        }

        // The ids of the rows of the quality profiles (the settings that a profile sets).
        internal static class ProfileRows
        {
            internal const string Quality = "quality";
            internal const string Simulation = "simulation";
            internal const string Volume = "volume";
            internal const string Precipitation = "precipitation";
            internal const string Light = "light";
            internal const string Audio = "audio";
            internal const string Cover = "cover";
            internal const string Ice = "ice";
            internal const string Forecast = "forecast";
            internal const string Wind = "wind";

            internal static readonly string[] All =
            {
                Quality, Simulation, Volume, Precipitation, Light, Audio, Cover, Ice, Forecast, Wind
            };
        }
    }

    // The table of every setting, in the order the window lists them. Built once on first use.
    internal static class SettingsTable
    {
        internal static readonly IReadOnlyList<SettingDef> All = Build();

        private static IReadOnlyList<SettingDef> Build()
        {
            const string G = AtmospheresSettings.Categories.General;
            const string CL = AtmospheresSettings.Categories.Clouds;
            const string W = AtmospheresSettings.Categories.Wind;
            const string S = AtmospheresSettings.Categories.Simulation;
            const string V = AtmospheresSettings.Categories.Volume;
            const string P = AtmospheresSettings.Categories.Precipitation;
            const string L = AtmospheresSettings.Categories.Light;
            const string A = AtmospheresSettings.Categories.Audio;
            const string C = AtmospheresSettings.Categories.Cover;
            const string I = AtmospheresSettings.Categories.Ice;
            const string F = AtmospheresSettings.Categories.Forecast;
            const string O = AtmospheresSettings.Categories.Orbit;
            const string D = AtmospheresSettings.Categories.Diagnostics;
            const string MASTER = AtmospheresSettings.GreyReasons.NeedsMaster;
            const string EVE = AtmospheresSettings.GreyReasons.NeedsEve;
            const string SIM = AtmospheresSettings.GreyReasons.NeedsSim;
            const string VOL = AtmospheresSettings.GreyReasons.NeedsVolume;
            const string AUD = AtmospheresSettings.GreyReasons.NeedsAudio;
            const string FC = AtmospheresSettings.GreyReasons.NeedsForecast;
            const string AL = AtmospheresSettings.GreyReasons.NeedsAlerts;
            const string WF = AtmospheresSettings.GreyReasons.NeedsWindForces;
            const string GC = AtmospheresSettings.GreyReasons.NeedsGroundCover;
            const string SI = AtmospheresSettings.GreyReasons.NeedsSeaIce;
            const string LM = AtmospheresSettings.GreyReasons.NeedsLightInMedium;
            const string PR = AtmospheresSettings.GreyReasons.NeedsPrecip;
            const string RQ = AtmospheresSettings.ProfileRows.Quality;
            const string RS = AtmospheresSettings.ProfileRows.Simulation;
            const string RV = AtmospheresSettings.ProfileRows.Volume;
            const string RP = AtmospheresSettings.ProfileRows.Precipitation;
            const string RL = AtmospheresSettings.ProfileRows.Light;
            const string RA = AtmospheresSettings.ProfileRows.Audio;
            const string RC = AtmospheresSettings.ProfileRows.Cover;
            const string RI = AtmospheresSettings.ProfileRows.Ice;
            const string RF = AtmospheresSettings.ProfileRows.Forecast;
            const string RW = AtmospheresSettings.ProfileRows.Wind;
            const string SOLAR = AtmospheresSettings.GreyReasons.NeedsSolarDimming;

            var t = new List<SettingDef>
            {
                // General
                SettingDef.Bool("enabled", true, G, "Atmospheres"),
                SettingDef.Bool("weatherEffects", false, G, "Weather Effects"),
                SettingDef.Float("yearLengthFactor", 1.0, 0.01, 10.0, G, "Season Year Length"),
                SettingDef.Choice("seasonMode", typeof(SeasonMode), SeasonMode.RealSeasons, G, "Season Mode"),
                SettingDef.Choice("qualityTier", typeof(ReDefinition.Core.QualityTier), ReDefinition.Core.QualityTier.Auto, G, "Quality Tier", "", RQ),
                SettingDef.Bool("qualityGovernor", false, G, "Quality Governor", MASTER, RQ),

                // Clouds and frequency
                SettingDef.Choice("cloudSource", typeof(CloudSourceKind), CloudSourceKind.Eve, CL, "Cloud Source", MASTER, RV),
                SettingDef.Choice("weatherCompose", typeof(WeatherCompose), WeatherCompose.Auto, CL, "Weather Composition", MASTER, RV),
                SettingDef.Bool("useEveWeather", true, CL, "Use EVE Weather", EVE),
                SettingDef.Int("eveLayerCap", 4, 1, 8, CL, "EVE Layer Cap", EVE, RV),
                SettingDef.Float("lightWeatherFrequency", 1.0, 0.0, 2.0, CL, "Light Weather Frequency", SIM),
                SettingDef.Float("heavyShare", 1.0, 0.0, 2.0, CL, "Heavy Weather Share", SIM),
                SettingDef.Bool("typeByTemperature", true, CL, "Precipitation Type By Temperature", MASTER),

                // Wind and gameplay
                SettingDef.Choice("windSource", typeof(WindSourceMode), WindSourceMode.Auto, W, "Wind Source", MASTER, RW),
                SettingDef.Choice("windInjection", typeof(WindInjection), WindInjection.Auto, W, "Wind Injection", WF, RW),
                SettingDef.Bool("windForces", true, W, "Wind Forces On Vehicles", MASTER, RW),
                SettingDef.Float("windStrength", 1.0, 0.0, 2.0, W, "Wind Strength", WF, RW),
                SettingDef.Bool("windOnLanded", true, W, "Wind On Landed Vehicles", WF),
                SettingDef.Float("gustStrength", 1.0, 0.0, 2.0, W, "Gust Strength", MASTER),
                SettingDef.Bool("solarDimming", false, W, "Solar Panel Dimming", MASTER),
                SettingDef.Float("solarDimmingStrength", 1.0, 0.0, 1.0, W, "Solar Dimming Strength", SOLAR),
                SettingDef.Bool("visibilityExport", true, W, "Export Visibility", MASTER),

                // Simulation
                SettingDef.Bool("simEnabled", true, S, "Weather Simulation", MASTER, RS),
                SettingDef.Choice("simGrid", typeof(SimGridChoice), SimGridChoice.Auto, S, "Simulation Grid", SIM, RS),
                SettingDef.Int("simLevels", 0, 0, 32, S, "Simulation Levels", SIM, RS),
                SettingDef.Float("simSliceMs", 0.5, 0.1, 4.0, S, "Simulation Slice", SIM, RS),
                SettingDef.Int("simSeed", 1, int.MinValue, int.MaxValue, S, "Weather Seed", SIM),

                // Volume
                SettingDef.Bool("volumePass", true, V, "Volumetric Clouds And Curtains", MASTER, RV),
                SettingDef.Float("volumeResolution", 1.0, 0.25, 1.0, V, "Volume Resolution", VOL, RV),
                SettingDef.Int("volumeSteps", 28, 16, 48, V, "Volume Steps", VOL, RV),
                SettingDef.Choice("volumeCompose", typeof(VolumeCompose), VolumeCompose.Scene, V, "Volume Composition", VOL, RV),

                // Precipitation and look
                SettingDef.Float("precipDensity", 1.0, 0.0, 2.0, P, "Precipitation Density", MASTER, RP),
                SettingDef.Bool("precipSnow", true, P, "Draw Snow", PR),
                SettingDef.Bool("precipRain", true, P, "Draw Rain", PR),
                SettingDef.Bool("precipSleet", true, P, "Draw Sleet", PR),
                SettingDef.Bool("precipDust", true, P, "Draw Dust", PR),
                SettingDef.Bool("precipSand", true, P, "Draw Sand", PR),
                SettingDef.Bool("precipAsh", true, P, "Draw Ash", PR),
                SettingDef.Bool("precipMethane", true, P, "Draw Methane", PR),
                SettingDef.Bool("stormCullDistance", false, P, "Cull Distant Terrain In Storms", MASTER, RP),
                SettingDef.Float("fogBrightness", 1.0, 0.25, 2.0, P, "Fog Brightness", MASTER),
                SettingDef.Float("veilStrength", 0.85, 0.0, 0.95, P, "Veil Strength", MASTER),
                SettingDef.Float("blizzardVisibilityM", 60.0, 20.0, 500.0, P, "Blizzard Visibility", MASTER),
                SettingDef.Float("skyDimming", 0.6, 0.0, 1.0, P, "Sky Darkening", MASTER),

                // Light in the medium, aurora, frost
                SettingDef.Bool("lightInMedium", true, L, "Light In Fog And Precipitation", MASTER, RL),
                SettingDef.Int("lightCount", 4, 0, 8, L, "Lamp Count", LM, RL),
                SettingDef.Float("lightInMediumStrength", 1.0, 0.0, 2.0, L, "Lamp Scatter Strength", LM),
                SettingDef.Choice("aurora", typeof(AuroraMode), AuroraMode.Auto, L, "Aurora", MASTER, RL),
                SettingDef.Float("auroraAmbientTint", 0.3, 0.0, 1.0, L, "Aurora Ambient Tint", MASTER),
                SettingDef.Bool("cameraFrost", true, L, "Camera Frost", MASTER, RL),

                // Audio
                SettingDef.Bool("weatherAudio", true, A, "Weather Audio", MASTER, RA),
                SettingDef.Float("weatherAudioVolume", 0.6, 0.0, 1.0, A, "Weather Audio Volume", AUD),
                SettingDef.Bool("audioWind", true, A, "Wind Sound", AUD),
                SettingDef.Bool("audioPrecip", true, A, "Precipitation Sound", AUD),
                SettingDef.Bool("audioFootsteps", true, A, "Footstep Sound", AUD),
                SettingDef.Bool("audioFootstepsSurface", true, A, "Footsteps By Surface", AUD),
                SettingDef.Bool("audioDistantStorm", false, A, "Distant Storm Sound", AUD),
                SettingDef.Bool("audioQuietSnow", true, A, "Quiet Snow", AUD),
                SettingDef.Choice("audioMuffling", typeof(AudioMuffling), AudioMuffling.Auto, A, "Sound Muffling", AUD),
                SettingDef.Choice("eveSound", typeof(EveSoundPolicy), EveSoundPolicy.Auto, A, "EVE Sound", AUD),
                SettingDef.Choice("audioBedSource", typeof(AudioBedSource), AudioBedSource.Synth, A, "Sound Bed Source", AUD),
                SettingDef.Bool("audioProbeSolo", false, A, "Solo Weather Audio For Probes", AUD),

                // Surface cover and ice
                SettingDef.Bool("groundCover", true, C, "Ground Cover", MASTER, RC),
                SettingDef.Float("coverStrength", 1.0, 0.0, 1.0, C, "Cover Strength", GC),
                SettingDef.Choice("vehicleCover", typeof(VehicleCoverMode), VehicleCoverMode.Landed, C, "Vehicle Cover", GC, RC),
                SettingDef.Bool("tracks", true, C, "Tracks In Cover", GC, RC),
                SettingDef.Float("coverDrift", 0.3, 0.0, 1.0, C, "Cover Drift", GC),
                SettingDef.Choice("seaIce", typeof(SeaIceMode), SeaIceMode.OrbitAndGround, I, "Sea Ice", MASTER, RI),
                SettingDef.Float("iceDrawDistanceKm", 5.0, 1.0, 50.0, I, "Ice Draw Distance", SI, RI),
                SettingDef.Bool("iceCollision", false, I, "Ice Collision", SI),

                // Forecast, alerts and launch limits
                SettingDef.Bool("forecastEnabled", true, F, "Forecast", MASTER, RF),
                SettingDef.Bool("alertsEnabled", true, F, "Weather Alerts", FC),
                SettingDef.Float("alertLeadHours", 3.0, 0.5, 24.0, F, "Alert Lead Time", AL),
                SettingDef.Choice("alertMinLevel", typeof(LevelChoice), LevelChoice.Heavy, F, "Alert Minimum Level", AL),
                SettingDef.Float("alertWindMs", 15.0, 0.0, 60.0, F, "Alert Wind Speed", AL),
                SettingDef.Bool("alertWarpStop", false, F, "Stop Time Warp On Alert", AL),
                SettingDef.Bool("alertKac", false, F, "Kerbal Alarm Clock Alarms", AL),
                SettingDef.Bool("alertMessageApp", true, F, "Message App Alerts", AL),
                SettingDef.Text("alertSites", "", F, "Alert Sites", AL),
                SettingDef.Float("forecastHorizonHours", 24.0, 6.0, 96.0, F, "Forecast Horizon", FC, RF),
                SettingDef.Float("forecastUncertainty", 0.0, 0.0, 1.0, F, "Forecast Uncertainty", FC),
                SettingDef.Choice("launchMaxLevel", typeof(LevelChoice), LevelChoice.Heavy, F, "Launch Maximum Level", FC),
                SettingDef.Float("launchMaxWindMs", 10.0, 0.0, 60.0, F, "Launch Maximum Wind", FC),
                SettingDef.Float("launchMinVisibilityM", 1000.0, 0.0, 20000.0, F, "Launch Minimum Visibility", FC),
                SettingDef.Float("launchLeadHours", 2.0, 0.0, 24.0, F, "Launch Lead Time", FC),

                // Orbit view
                SettingDef.Choice("orbitBodies", typeof(OrbitBodiesMode), OrbitBodiesMode.Focused, O, "Orbit View Bodies", MASTER),
                SettingDef.Bool("orbitOverlay", true, O, "Weather In Orbit View", MASTER),
                SettingDef.Int("orbitMapWidth", 2048, 1024, 4096, O, "Orbit Map Width", MASTER),

                // Diagnostics
                SettingDef.Float("forceLevel", -1.0, -1.0, 1.0, D, "Force Precipitation Level", "", "", ForceNegativeOff),
                SettingDef.Choice("forceType", typeof(ForceType), ForceType.Auto, D, "Force Precipitation Type"),
                SettingDef.Text("forceBody", "", D, "Force Body"),
                SettingDef.Float("forcePhase", -1.0, -1.0, 1.0, D, "Force Season Phase", "", "", ForcePhaseRule),
                SettingDef.Choice("atmoDebug", typeof(AtmoDebugMode), AtmoDebugMode.Off, D, "Debug Overlay")
            };
            return t.AsReadOnly();
        }

        // A negative number means "off" and is stored as -1; anything else is a level from 0 to 1.
        private static double ForceNegativeOff(double value)
        {
            return value < 0.0 ? -1.0 : value;
        }

        // A negative number means the clock; anything else is a phase wrapped into 0 to 1.
        private static double ForcePhaseRule(double value)
        {
            if (value < 0.0) return -1.0;
            double wrapped = value - Math.Floor(value);
            return wrapped;
        }
    }
}
