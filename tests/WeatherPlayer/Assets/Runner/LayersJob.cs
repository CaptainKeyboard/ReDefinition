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
        // The light of the pictures: sun colour, ambient above and below (rgb each).
        public float[] sunColour, ambientTop, ambientBottom;
    }
}
