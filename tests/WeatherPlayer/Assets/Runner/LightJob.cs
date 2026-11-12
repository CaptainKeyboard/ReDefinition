using System;

namespace ReDefinition.WeatherPlayer
{
    // The jobs of the light scene (package T03, -mode scene -scene light -in <folder>): a jobs.json that tools/weather_metrics/light.py writes.
    // A job is one layer-pass job (the T01 job: camera, stack, sun) plus the light's numbers; the scene draws the march into a float target and
    // writes its four channels as arrays. A value of 0 in a number the job may leave out keeps the compiled default of CloudLightSettings,
    // except where the field says otherwise.
    [Serializable]
    public sealed class LightJobFile
    {
        public LightJob[] jobs;
    }

    [Serializable]
    public sealed class LightJob
    {
        public string id;
        // "lum": the march (rgb, alpha); the aid number picks what the channels hold (0 natural, 3 phase over mu, 4 direct | steps | ambient, 5 step map).
        public string mode = "lum";
        public int aid;
        // 1: the light's numbers are the compiled defaults of CloudLightSettings (the cell, the extra lights and the cache still come from the job).
        public int useDefaults;
        public LayersJob layerJob;
        // The phase function and the albedo.
        public float forwardG = 0.85f, backG = -0.3f, forwardWeight = 0.92f, albedo = 0.999f;
        // The octaves.
        public int octaves = 4;
        public float octaveA = 0.5f, octaveB = 0.5f, octaveC = 0.5f, msBrightness = 1f, diffusion, diffusionTau = 8f;
        // Powder, ambient, tint.
        public float powder, powderLength = 150f, ambientFloor = 0.03f, ambientOcclusion, skyTint = 0.7f, ambientGain = 1f;
        // The light march: taps towards the sun, the cone, the step ratio, taps for the moons and straight up.
        public int sunSteps = 3, moonSteps = 2, upSteps = 2;
        public float coneSpread, tapRatio = 2f;
        // Extra lights: direction towards and colour (rgb) of up to two.
        public float[] moonDir0, moonCol0, moonDir1, moonCol1;
        // The bolts: positions (xyz each, flat), ranges and colours (rgb each, flat).
        public float[] boltPos, boltRange, boltCol;
        // The test cell: centre (planet frame), horizontal radius, vertical radius, extinction; radius 0: none.
        public float[] cellCentre;
        public float cellRadius, cellVertical, cellSigma;
        // The light cache: on, its box (minimum corner and size in the planet frame) and the taps of its fill.
        public int cache;
        public float[] cacheMin, cacheSize;
        public int cacheFillSteps = 12;
        // A per-layer multiple scattering brightness, powder and ambient scale (x, y, z of layer 0).
        public float[] layerScale;
    }
}
