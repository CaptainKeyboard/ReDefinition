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
            IEnumerable<string> dustTypeNames = null, int eveLayerCap = 0, IEnumerable<CloudLayerSpec> layers = null, float sphereScale = 1f,
            float aerialPerspectiveBlue = DefaultAerialPerspectiveBlue, float aerialPerspectiveNearBlue = DefaultAerialPerspectiveNearBlue, Vector3? aerialPerspectiveTint = null, string layerSet = "",
            CloudHeightRules height = null, double scaledTroposphereM = double.NaN)
        {
            Height = height ?? CloudHeightRules.Default;
            ScaledTroposphereM = scaledTroposphereM;
            Layers = SchemaList.Of(layers);
            LayerSet = layerSet ?? "";
            SphereScale = sphereScale;
            AerialPerspectiveTint = aerialPerspectiveTint ?? Vector3.one;
            AerialPerspectiveBlue = aerialPerspectiveBlue;
            AerialPerspectiveNearBlue = aerialPerspectiveNearBlue;
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
        // The layer set of the own cloud system for this body: the LAYER sub-nodes, or where there are none the layer template sized to the body (CloudLayerTemplate);
        // empty: the body draws no own clouds (layers = none, no atmosphere, no template, no water phase).
        internal IReadOnlyList<CloudLayerSpec> Layers { get; }
        // Which set Layers is and why (E40 Q8a, CloudLayerTemplate): "authored, 5 layers", "template Earthlike, 5 layers, troposphere 7.5 km", "none (layers = none)"; empty: not resolved.
        internal string LayerSet { get; }
        // Key sphereScale: the vertical scale of the noise lattice about the layers' reference radius (NoiseSwitches.SphereScale; 1: the
        // lattice is planet-centred).
        internal float SphereScale { get; }
        // Keys aerialPerspectiveBlue and aerialPerspectiveNearBlue (E40 T1 tint): how much bluer than the sun the air's in-scattered light is in the cloud pass's aerial perspective, as the exponent of the Rayleigh
        // coefficient's ratio to its mean per channel (CloudBridgeAir.Inscatter). aerialPerspectiveBlue is the colour a cloud far away fades into (the saturated horizon colour of the body's sky; 0.35 fits
        // Scatterer's horizon on Kerbin); aerialPerspectiveNearBlue is the colour of the in-scatter over a SHORT path, which the sun's colour alone gives (the air's own blue is in the extinction
        // already, so the source of the first kilometres is the sun's grey: 0). The pass blends the two by the blue channel's extinction along the ray (FarView.cginc).
        internal float AerialPerspectiveBlue { get; }
        internal float AerialPerspectiveNearBlue { get; }
        // Keys aerialPerspectiveTintR, aerialPerspectiveTintG and aerialPerspectiveTintB (E40 FL far): a gain per channel on the colour a far cloud fades into (the far in-scatter, after aerialPerspectiveBlue; the near colour is not touched).
        // The Rayleigh power law of aerialPerspectiveBlue cannot reach the whitish-cyan horizon of a sky with aerosols: on Kerbin it leaves green 0.09 to 0.13 (display) under Scatterer's horizon, so a cloud 25 km away
        // fades into a violet and a band of far clouds reads darker than the sky behind it. The gain is the ratio of the sky's horizon radiance to the power law's colour; 1 keeps the law as it was.
        internal Vector3 AerialPerspectiveTint { get; }
        internal const float DefaultAerialPerspectiveBlue = 0.35f, DefaultAerialPerspectiveNearBlue = 0f;
        // Keys height* (E40 HKEYS): the numbers of the height variation of the layers' bases and tops (HeightTuning); never null.
        internal CloudHeightRules Height { get; }
        // The troposphere of the height scale (CloudHeightScale: the measured one times the factor) the layers were read against; NaN where the scale does not act. The
        // meso stage takes it as its depth instead of the generator's (OwnCloudModule), so its depth keys follow the layers.
        internal double ScaledTroposphereM { get; }
    }

    // The colours and limits of the body's air. Node LOOK.
    internal sealed class LookRules
    {
        internal static readonly LookRules Default = new LookRules();

        internal LookRules(Color? fogColour = null, float visibilityMinM = 60f, Color? precipExtinctionGroundColour = null, Color? precipExtinctionSkyColour = null,
            Color? flakeColour = null)
        {
            FogColour = fogColour ?? new Color(0.78f, 0.80f, 0.84f, 1f);
            VisibilityMinM = visibilityMinM;
            PrecipExtinctionGroundColour = precipExtinctionGroundColour ?? new Color(0.86f, 0.88f, 0.92f, 1f);
            PrecipExtinctionSkyColour = precipExtinctionSkyColour ?? new Color(0.70f, 0.73f, 0.78f, 1f);
            FlakeColour = flakeColour ?? Color.white;
        }

        // Linear colours.
        internal Color FogColour { get; }
        // Key visibilityMin: the visibility at the top of the scale, in metres.
        internal float VisibilityMinM { get; }
        internal Color PrecipExtinctionGroundColour { get; }
        internal Color PrecipExtinctionSkyColour { get; }
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
