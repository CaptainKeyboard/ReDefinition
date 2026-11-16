using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Viewer
{
    // The keys and look presets of the new cloud model (package CLOUDR1) for the viewer's tables, registered through the CloudKeySource mechanism: the heap volume (round lobes in a cascade),
    // the crisp rim, the cavity, the hidden tiling, the first-hit experiment and the two 'New Model' looks. This is the one place the looks are defined (the viewer's preset list shows them
    // next to Today; no cfg file holds a second copy). Every range is the clamp of the reader (CloudLookTuning); the markers say when a change shows (read at the reader named in each note).
    internal static class CloudModelKeys
    {
        // The march grid of the two looks (layers.gridFine, in the registry's Quality group): 96 points are 2, 128 are 2.6667 (the product's 48 are 1).
        internal const float Grid96 = 2f, Grid128 = 8f / 3f;
        internal const string Look96 = ViewerBuiltIns.NewModelSlot, Look128 = "New Model 128 Steps";

        [CloudKeySource]
        internal static void Register(CloudKeyRegistrations r)
        {
            float presets = HeapModel.PresetCount;
            // Read into the type tables when the layers are uploaded (LayerRenderer.Upload); a change of the tuning table uploads them again within a frame (CloudLookTuning.Stamp, LayerRenderer.cs:272).
            r.Key(CloudLookTuning.TuneHeap, -1f, KeySection.Shape,
                "heap preset for every type: -1 each type's cfg key, 0 none (today's shape volume), 1 billow, 2 rough; the first use builds the heap volume on a worker (about a second at Medium, the shape volume shows meanwhile)",
                KeyLiveness.Change, KeyMarker.Live, -1f, presets);
            r.Key(CloudLookTuning.TuneEdgePower, 0f, KeySection.Edge,
                "exponent of the density rim for every type of a soft layer: 0 each type's key, below 1 a crisper rim",
                KeyLiveness.Change, KeyMarker.Live, 0f, 6f);
            r.Key(CloudLookTuning.TuneCavity, -1f, KeySection.Shape,
                "cavity strength (darkens the creases between lobes): -1 each type's key, 0 off; acts only on a type that reads a heap",
                KeyLiveness.Change, KeyMarker.Live, -1f, 1f);
            // Read every frame (LayerNoise.Bind: _HeapP and _NoiseR).
            r.Flag(CloudLookTuning.TuneHeapFootLod, true, KeySection.Shape,
                "the march's sample spacing raises the heap volume's mip level; off: only the smoothing does", KeyLiveness.FrameHistory);
            r.Flag(CloudLookTuning.TuneHeapIso, true, KeySection.Shape,
                "the heap is read at the lattice's true vertical scale, so a layer's vertical scale does not stretch the lobes into columns; off: as the shape volume is read", KeyLiveness.FrameHistory);
            r.Key(CloudLookTuning.TuneHeapWarp, (float)HeapModel.WarpShare, KeySection.Shape,
                "share of the untiling displacement's horizontal part the heap lookup keeps, 0 to 1; below 0 the whole displacement (the pulled lookup)", KeyLiveness.FrameHistory,
                KeyMarker.Live, -1f, 1f);
            // CloudLookTuning.NoiseBlend and the two numbers are read by LayerNoise.LayerParams every frame (CLOUDR2: the layer pass has the blended lookup, one call site with a four-tap loop; the CPU
            // twin follows for the cloud shadow).
            r.Flag(CloudLookTuning.TuneNoiseBlend, false, KeySection.Shape,
                "hides the tiling of the shape and heap volumes by blending four randomly offset and rotated reads per lattice cell (the layer pass and the cloud shadow)");
            r.Key(CloudLookTuning.TuneNoiseBlendExp, 8f, KeySection.Shape, "exponent of the blend weights", min: 1f, max: 32f);
            r.Key(CloudLookTuning.TuneNoiseBlendCell, 1f, KeySection.Shape, "blend lattice cell in periods", min: 0.25f, max: 16f);
            // CLOUDR2: the first taps of the sun's light march read the eroded density (LightTau, _BlendP.w), read every frame.
            r.Key(CloudLookTuning.TuneLightErosion, 0f, KeySection.Light,
                "number of the first taps of the sun's light march that read the eroded density, 0..8, 0 the shape only", KeyLiveness.FrameHistory, KeyMarker.Live, 0f, 8f);
            // Read by NoiseVolumeSet.Generate (CloudShapeModel.cs:56) when LayerNoise.StartGeneration makes the volumes: a scene change, a change of cloudNoiseTier or the seed.
            r.Key(CloudLookTuning.TuneHeapDetail, 0f, KeySection.Shape,
                "Hurst exponent of the erosion detail octaves, 0 the compiled weights; read only when the noise volumes are made (a scene change or a change of Cloud Noise Size)",
                KeyLiveness.Volume, KeyMarker.AfterVolumeRebuild, 0f, 2f);
            // P6, a measured dead end: smooth look, +3.9 ms. Read every frame (LayerRenderer.Apply, _FirstHitP).
            r.Key(CloudLookTuning.TuneFirstHit, 0f, KeySection.Quality,
                "experimental, a measured dead end (smooth look, +3.9 ms): bisection evaluations on the entered span, 0 off", KeyLiveness.FrameHistory, KeyMarker.Live, 0f, 8f);
            r.Key(CloudLookTuning.TuneFirstHitThin, 0.25f, KeySection.Quality,
                "experimental: the thin steps inside the cloud as a share of the grid step", KeyLiveness.FrameHistory, KeyMarker.Live, 0.02f, 1f);
            r.Key(CloudLookTuning.TuneFirstHitShell, 32f, KeySection.Quality,
                "experimental: how many thin steps follow the entry", KeyLiveness.FrameHistory, KeyMarker.Live, 0f, 512f);
            r.Key(CloudLookTuning.TuneFirstHitNear, 0f, KeySection.Quality,
                "experimental: distance in m up to which the thin steps are taken, 0 everywhere (the reader clamps to 1e7; the slider stops at 20000)", KeyLiveness.FrameHistory,
                KeyMarker.Live, 0f, 20000f);

            // The same three per layer (the layer's name after the prefix): the layer's value wins over the global key.
            r.LayerKey(CloudLookTuning.TuneHeapLayer, "", -1f, KeySection.Shape, "heap preset of the layer (-1 the global key, 0 none, 1 billow, 2 rough)",
                min: -1f, max: presets);
            r.LayerKey(CloudLookTuning.TuneEdgePowerLayer, "", 0f, KeySection.Edge, "rim exponent of the layer (0 the global key; below 1 a crisper rim)", min: 0f, max: 6f);
            r.LayerKey(CloudLookTuning.TuneCavityLayer, "", -1f, KeySection.Shape, "cavity strength of the layer (-1 the global key, 0 off)", min: -1f, max: 1f);

            // The looks. A key a look does not name goes back to its default when the look is loaded (the viewer clears every key a set does not hold), so Today undoes all of them; none touches
            // ownClouds or cloudSource.
            var cumulus = new List<PresetLayerValue>
            {
                new PresetLayerValue(CloudLookTuning.TuneHeapLayer, "", "cumulus", 1f),
                new PresetLayerValue(CloudLookTuning.TuneEdgePowerLayer, "", "cumulus", 0.5f),
                new PresetLayerValue(CloudLookTuning.TuneCavityLayer, "", "cumulus", 0.35f)
            };
            r.Preset(Look96, "heap volume, crisp rim and cavity on the cumulus layer, 96 grid points (about +2.5 ms at 1080p)",
                new Dictionary<string, float> { { OwnCloudOptions.TuneGridFine, Grid96 } }, null, cumulus);
            r.Preset(Look128, "the same with 128 grid points (about +4 ms at 1080p)",
                new Dictionary<string, float> { { OwnCloudOptions.TuneGridFine, Grid128 } }, null, cumulus);
        }
    }
}
