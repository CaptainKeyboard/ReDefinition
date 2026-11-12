using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // How the clouds of the body are found in EVE. Node CLOUDS.
    internal enum CloudDiscovery
    {
        // The layers of the body, ordered by altitude, with roles by type names.
        Auto,
        // Only the layers named in the node.
        Explicit,
        // No EVE layers are used.
        Off
    }

    // The clouds of the body. Node CLOUDS.
    internal sealed class CloudRules
    {
        internal static readonly CloudRules Default = new CloudRules();

        // Source is null where the player's cloudSource setting decides.
        internal CloudRules(CloudSourceKind? source = null, CloudDiscovery discover = CloudDiscovery.Auto, string baseLayer = "",
            IEnumerable<string> weatherLayers = null, IEnumerable<string> snowTypeNames = null, IEnumerable<string> stratusTypeNames = null,
            IEnumerable<string> dustTypeNames = null, int eveLayerCap = 0, IEnumerable<CloudLayerSpec> layers = null, float sphereScale = 1f)
        {
            Layers = SchemaList.Of(layers);
            SphereScale = sphereScale;
            Source = source;
            Discover = discover;
            BaseLayer = baseLayer ?? "";
            WeatherLayers = SchemaList.Of(weatherLayers);
            SnowTypeNames = SchemaList.Of(snowTypeNames ?? new[] { "Polar-snow", "Snow" });
            StratusTypeNames = SchemaList.Of(stratusTypeNames ?? new[] { "Stratus" });
            DustTypeNames = SchemaList.Of(dustTypeNames);
            EveLayerCap = eveLayerCap;
        }

        // Key cloudSource of the node, or null for the setting.
        internal CloudSourceKind? Source { get; }
        // Key discover.
        internal CloudDiscovery Discover { get; }
        // Keys baseLayer and weatherLayers: EVE layer names.
        internal string BaseLayer { get; }
        internal IReadOnlyList<string> WeatherLayers { get; }
        // Keys snowTypeNames, stratusTypeNames, dustTypeNames: which cloud types of a layer play which role.
        internal IReadOnlyList<string> SnowTypeNames { get; }
        internal IReadOnlyList<string> StratusTypeNames { get; }
        internal IReadOnlyList<string> DustTypeNames { get; }
        // Key eveLayerCap: the most EVE layers the painter drives for this body; 0 follows the player's setting.
        internal int EveLayerCap { get; }
        // The LAYER sub-nodes: the layer set of the own cloud system for this body (empty: the body has none of its own). A body with
        // atmosphere and no LAYER nodes draws no own clouds.
        internal IReadOnlyList<CloudLayerSpec> Layers { get; }
        // Key sphereScale: the vertical scale of the noise lattice about the layers' reference radius (NoiseSwitches.SphereScale; 1: the
        // lattice is planet-centred).
        internal float SphereScale { get; }
    }

    // The colours and limits of the body's air. Node LOOK.
    internal sealed class LookRules
    {
        internal static readonly LookRules Default = new LookRules();

        internal LookRules(Color? fogColour = null, float visibilityMinM = 60f, Color? veilGroundColour = null, Color? veilSkyColour = null,
            Color? flakeColour = null)
        {
            FogColour = fogColour ?? new Color(0.78f, 0.80f, 0.84f, 1f);
            VisibilityMinM = visibilityMinM;
            VeilGroundColour = veilGroundColour ?? new Color(0.86f, 0.88f, 0.92f, 1f);
            VeilSkyColour = veilSkyColour ?? new Color(0.70f, 0.73f, 0.78f, 1f);
            FlakeColour = flakeColour ?? Color.white;
        }

        // Linear colours.
        internal Color FogColour { get; }
        // Key visibilityMin: the visibility at the top of the scale, in metres.
        internal float VisibilityMinM { get; }
        internal Color VeilGroundColour { get; }
        internal Color VeilSkyColour { get; }
        internal Color FlakeColour { get; }
    }

    // The clips of the body's weather sound; empty names use the procedural sound. Node SOUND.
    internal sealed class SoundRules
    {
        internal static readonly SoundRules Default = new SoundRules();

        internal SoundRules(string windBed = "", string precipBed = "", string footstepSurface = "", bool grit = false)
        {
            WindBed = windBed ?? "";
            PrecipBed = precipBed ?? "";
            FootstepSurface = footstepSurface ?? "";
            Grit = grit;
        }

        // Paths of clips in the GameDatabase.
        internal string WindBed { get; }
        internal string PrecipBed { get; }
        internal string FootstepSurface { get; }
        // True where sand and dust use the grit variant of the hiss.
        internal bool Grit { get; }
    }

    // The wind of the body as a force on vehicles. Node WIND.
    internal sealed class WindRules
    {
        internal static readonly WindRules Default = new WindRules();

        internal WindRules(float maxWindMs = 12f, float violentWindMs = 25f, bool windOnLanded = true, float groundFactor = 0.1f, float fullHeightM = 50f,
            float maxAccelMs2 = 1.5f, float maxSlewMsPerS = 8f)
        {
            MaxWindMs = maxWindMs;
            ViolentWindMs = violentWindMs;
            WindOnLanded = windOnLanded;
            GroundFactor = groundFactor;
            FullHeightM = fullHeightM;
            MaxAccelMs2 = maxAccelMs2;
            MaxSlewMsPerS = maxSlewMsPerS;
        }

        // Keys maxWind and violent: the wind a vehicle feels at most, and the wind named violent. [UNKNOWN start values until B10.]
        internal float MaxWindMs { get; }
        internal float ViolentWindMs { get; }
        // Key windOnLanded.
        internal bool WindOnLanded { get; }
        // The height profile: the share of the wind on the ground, and the height above it at which the wind is full.
        internal float GroundFactor { get; }
        internal float FullHeightM { get; }
        // Keys maxWindAccel and maxWindSlew.
        internal float MaxAccelMs2 { get; }
        internal float MaxSlewMsPerS { get; }
    }

    // What the weather does to play. Node GAMEPLAY.
    internal sealed class GameplayRules
    {
        internal static readonly GameplayRules Default = new GameplayRules();

        internal GameplayRules(float solarLoss = 1f)
        {
            SolarLoss = solarLoss;
        }

        // Key solarLoss: scales how much of the sun a cloud takes from solar panels.
        internal float SolarLoss { get; }
    }

    // The aurora of the body. Node AURORA.
    internal sealed class AuroraRules
    {
        internal static readonly AuroraRules Default = new AuroraRules();

        internal AuroraRules(bool enabled = false, float minLatDeg = 60f, Color? colour = null, float strength = 1f)
        {
            Enabled = enabled;
            MinLatDeg = minLatDeg;
            Colour = colour ?? new Color(0.2f, 1f, 0.5f, 1f);
            Strength = strength;
        }

        internal bool Enabled { get; }
        internal float MinLatDeg { get; }
        internal Color Colour { get; }
        internal float Strength { get; }
    }

    // How light scatters in the air around lamps. Node LIGHT_MEDIUM.
    internal sealed class LightMediumRules
    {
        internal static readonly LightMediumRules Default = new LightMediumRules();

        internal LightMediumRules(float density = 1f, Color? scatterColour = null, float dustScatter = 0f)
        {
            Density = density;
            ScatterColour = scatterColour ?? Color.white;
            DustScatter = dustScatter;
        }

        // The scale of the scatter against the default, its colour, and the share added by dust.
        internal float Density { get; }
        internal Color ScatterColour { get; }
        internal float DustScatter { get; }
    }

    // Frost on the camera. Node FROST.
    internal sealed class FrostRules
    {
        internal static readonly FrostRules Default = new FrostRules();

        internal FrostRules(bool enabled = true, float maxTemperatureK = 268f, float strength = 1f)
        {
            Enabled = enabled;
            MaxTemperatureK = maxTemperatureK;
            Strength = strength;
        }

        internal bool Enabled { get; }
        // Frost forms below this air temperature in kelvin.
        internal float MaxTemperatureK { get; }
        internal float Strength { get; }
    }

    // The forecast of the body. Node FORECAST.
    internal sealed class ForecastRules
    {
        internal static readonly ForecastRules Default = new ForecastRules();

        internal ForecastRules(double stepSeconds = 3600.0, double horizonHours = 24.0)
        {
            StepSeconds = stepSeconds;
            HorizonHours = horizonHours;
        }

        // The step of a site's forecast in seconds of game time, and the horizon in hours; the player's forecastHorizonHours caps it.
        internal double StepSeconds { get; }
        internal double HorizonHours { get; }
    }
}
