using System;

namespace ReDefinition.WeatherPlayer
{
    // The jobs of the layer scene (package T01, -mode scene -scene layers -in <folder>): a jobs.json that tools/weather_metrics/layers.py
    // writes next to the raw R8 maps the layers name. A job is one camera, one layer stack and one output; the scene draws it with the
    // mod's own LayerRenderer and writes the arrays or the pictures. The camera arrives as a position and a basis (the Python side builds
    // it with the reference renderer's camera, so that both columns look along the same rays).
    [Serializable]
    public sealed class LayersJobFile
    {
        public LayersJob[] jobs;
    }

    [Serializable]
    public sealed class LayersJobType
    {
        public string name;
        public float baseShare, topShare, erosion;
        // The density over the type's height (0 at its base, 1 at its top), sampled evenly, at least two values.
        public float[] profile;
    }

    [Serializable]
    public sealed class LayersJobLayer
    {
        public string name;
        // Altitudes above sea level in m, the extinction per m, the coverage, the edge softness, the kind (0 weather, 1 cheap, 2 fog, 3 emissive).
        public float inner, outer, sigma, coverage, softness;
        public int kind;
        public float fadeNear, fadeFar;
        // The raw R8 maps (mapWidth x mapHeight, row 0 = north), empty: none.
        public string coverageMap, typeMap;
        public float noiseAmount, noiseScale, shift;
        public float[] emission;
        public LayersJobType[] types;
        // The noise model's per-layer numbers (package T02): the flow map's largest displacement in m (0: none) and whether it loops, the
        // colour map's strength, the distance the detail has risen in m, the drift of the pattern in world space (m, applied as one step).
        public float flowAmplitude, colourStrength, rise;
        public int flowLooped;
        public float[] drift;
    }

    // The switches and maps of the noise model for a job (package T02). The booleans are inverted so that a job without this object has the
    // production defaults.
    [Serializable]
    public sealed class LayersJobNoise
    {
        public int disabled, noUntile, noCurl;
        // The vertical scale of the spherical lattice (0 or 1: the planet-centred lattice).
        public float sphere;
        public uint seed;
        public int sizeLevel = 1;
        // Raw RGBA8 maps (mapWidth x mapHeight, row 0 = north) next to jobs.json; empty: none.
        public string flowMap, colourMap;
        public int mapWidth, mapHeight;
        // The flow map's time in seconds (the loop is 120 s).
        public float flowTime;
    }

    [Serializable]
    public sealed class LayersJob
    {
        public string id;
        // "aux": alpha, tau, steps and first-hit arrays; "lum": the march's radiance and alpha; "color": the natural and the aid pictures.
        public string mode;
        // The size of the raw maps the layers name.
        public int mapWidth, mapHeight;
        public int width, height;
        // The camera in the planet frame (centre at the origin, z north): position, forward, right and up (unit vectors), the vertical field of view in degrees.
        public float[] position, forward, right, up;
        public float fovV;
        public float planetRadius;
        // 1: the body has a surface (the ray ends on the sea-level sphere), 0: a gas giant.
        public int hasSurface = 1;
        public float[] sun;
        public float rayLimit;
        public int maxSteps = 256, sunSteps = 3;
        public float earlyOut, minStep = 1f, stepGrowth, jitter, sunMarch = 4000f, resolution = 1f;
        public int layerMask;
        // The cost controls of the march (LayerFrame): coverage bound, leap, sun cut-off, cheap/fog step shares, lit steps per sun march; a
        // negative value leaves the product's default. resolution < 1 marches into a smaller target and composites up (colour jobs).
        public int costBound = -1, lightStride = -1;
        public float costLeap = -1f, costCut = -1f, cheapShare = -1f, fogShare = -1f;
        // An occluder sphere (centre, radius) whose depth is the scene's; radius 0: none.
        public float[] occluder;
        public LayersJobLayer[] layers;
        // The noise model's switches and maps (package T02); null: the defaults.
        public LayersJobNoise noise;
        // The light of the pictures: sun colour, ambient above and below (rgb each).
        public float[] sunColour, ambientTop, ambientBottom;
        // The backdrop of the colour pictures (the look sheet): sky colour at the horizon and at the zenith, the lit ground's albedo colour, the
        // exposure of the tone map (0: the default 1.2) and space (1: rays that miss the planet are black with a thin blue limb glow). Missing: the
        // fixed daylight backdrop.
        public float[] skyHorizon, skyZenith, groundColour;
        public float exposure;
        public int space;
    }
}
