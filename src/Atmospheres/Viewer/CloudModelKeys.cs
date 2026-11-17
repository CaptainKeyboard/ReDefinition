using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Viewer
{
    // The keys and look presets of the new cloud model (package CLOUDR1) for the viewer's tables, registered through the CloudKeySource mechanism: the heap volume (round lobes in a cascade),
    // the crisp rim, the cavity, the hidden tiling, the first-hit experiment and the looks (Previous Look for A/B, 96 and 128 Steps). This is the one place the looks are defined (the viewer's preset list shows them
    // next to Today; no cfg file holds a second copy). Every range is the clamp of the reader (CloudLookTuning); the markers say when a change shows (read at the reader named in each note).
    internal static class CloudModelKeys
    {
        // The looks: the slot of the viewer's list that the 96-point look fills, the 128-point look and the look before the new model became the default. The march grid is the setting cloudSamples
        // (the grid points of the profile); layers.gridFine (the registry's Quality group) multiplies it for a quick comparison.
        internal const string Look96 = ViewerBuiltIns.NewModelSlot, Look128 = "128 Steps", LookBefore = "Previous Look";

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
            r.Flag(CloudLookTuning.TuneNoiseBlend, CloudLookTuning.NoiseBlendDefault > 0.5f, KeySection.Shape,
                "hides the tiling of the shape and heap volumes by blending four randomly offset and rotated reads per lattice cell (the layer pass and the cloud shadow); on: the untiling displacement is not applied to them");
            // CELLS: read every frame (LayerRenderer.Apply and CloudShadowRenderer.Apply write the layers' pitch or 0; the CPU shadow twin reads it per sample).
            r.Flag(CloudLookTuning.TuneCells, CloudLookTuning.CellsDefault > 0.5f, KeySection.Shape,
                "single clouds: the map's cloud share is put down as single clouds on the lattice of each layer's cellPitch (the cumulus layer); off: the map's smoothed texels are the clouds",
                KeyLiveness.FrameHistory);
            r.Key(CloudLookTuning.TuneCellBillow, -1f, KeySection.Shape,
                "how far the billows move a single cloud's surface, as a share of its shorter semi-axis (0 the bare body; below 0 each layer's cellBillow)", KeyLiveness.FrameHistory, KeyMarker.Live, -1f, 2f);
            r.Key(CloudLookTuning.TuneCellBand, -1f, KeySection.Shape,
                "width in m over which a single cloud's share rises at its surface, narrow is a sharp outline (below 0 each layer's cellBand)", KeyLiveness.FrameHistory, KeyMarker.Live, -1f, 300f);
            r.Key(CloudLookTuning.TuneCellFine, -1f, KeySection.Shape,
                "gain of a single cloud's finer billows, the heads on the heads (0 off; below 0 each layer's cellFine)", KeyLiveness.FrameHistory, KeyMarker.Live, -1f, 4f);
            r.Key(CloudLookTuning.TuneNoiseBlendExp, 8f, KeySection.Shape, "exponent of the blend weights", min: 1f, max: 32f);
            r.Key(CloudLookTuning.TuneNoiseBlendCell, 1f, KeySection.Shape, "blend lattice cell in periods", min: 0.25f, max: 16f);
            // CLOUDR2: the first taps of the sun's light march read the eroded density (LightTau, _BlendP.w), read every frame.
            r.Key(CloudLookTuning.TuneLightErosion, CloudLookTuning.LightErosionDefault, KeySection.Light,
                "number of the first taps of the sun's light march that read the eroded density, 0..8 (the lobes shade each other), 0 the shape only", KeyLiveness.FrameHistory, KeyMarker.Live, 0f, 8f);
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

            // OPT1: the map pipeline. cloud.mapGpuSlew is read when the map store is made (OwnCloudModule.cs:642, a stack build) and when a fetch round starts (:769, a change
            // switches the mode at the next round); cloud.mapWorker is read when each steady-state round starts (:789), so a change acts within about a second.
            r.Flag(OwnCloudModule.TuneMapGpuSlew, true, KeySection.Quality,
                "the cloud maps move toward the weather's new maps on the GPU (one upload per new map) instead of a step and an upload of every map on the CPU every frame",
                KeyLiveness.Second);
            r.Flag(OwnCloudModule.TuneMapWorker, true, KeySection.Quality,
                "the preparation of the cloud maps (deck rule, meso stage, anvils) runs on a worker thread instead of the main thread", KeyLiveness.Second);

            // CLOUDR3 P7: read every frame (OwnCloudOptions.Apply(ReconSettings) -> LayerReconstructor, _ReconK.w).
            r.Flag(OwnCloudOptions.TuneCatmull, OwnCloudOptions.CatmullDefault, KeySection.Quality,
                "the reconstruction reads its history with a Catmull-Rom filter instead of the bilinear one (less blur from sub-pixel moves, bounded by the variance clip)", KeyLiveness.FrameHistory);

            // The same three per layer (the layer's name after the prefix): the layer's value wins over the global key.
            r.LayerKey(CloudLookTuning.TuneHeapLayer, "", -1f, KeySection.Shape, "heap preset of the layer (-1 the global key, 0 none, 1 billow, 2 rough)",
                min: -1f, max: presets);
            r.LayerKey(CloudLookTuning.TuneEdgePowerLayer, "", 0f, KeySection.Edge, "rim exponent of the layer (0 the global key; below 1 a crisper rim)", min: 0f, max: 6f);
            r.LayerKey(CloudLookTuning.TuneCavityLayer, "", -1f, KeySection.Shape, "cavity strength of the layer (-1 the global key, 0 off)", min: -1f, max: 1f);

            // The looks. A key a look does not name goes back to its default when the look is loaded (the viewer clears every key a set does not hold), so Today (the shipped defaults: the new model
            // on, the grid points of the Medium profile) undoes all of them; none touches ownClouds or cloudSource. The grid points are the setting cloudSamples, a look names it absolute.
            var before = new List<PresetLayerValue>
            {
                new PresetLayerValue(CloudLookTuning.TuneHeapLayer, "", "cumulus", 0f),
                new PresetLayerValue(CloudLookTuning.TuneEdgePowerLayer, "", "cumulus", 2f),
                new PresetLayerValue(CloudLookTuning.TuneCavityLayer, "", "cumulus", 0f)
            };
            r.Preset(LookBefore, "the look before the new model became the default, for A/B against Today: no heap volume, the rim exponent 2 (the layer's waterPower), no cavity, the tiling hidden by the untiling displacement, no eroded sun taps, no single clouds (the map's texels), 48 grid points",
                new Dictionary<string, float> { { CloudLookTuning.TuneNoiseBlend, 0f }, { CloudLookTuning.TuneLightErosion, 0f }, { OwnCloudOptions.TuneCatmull, 0f }, { CloudLookTuning.TuneCells, 0f } },
                new Dictionary<string, double> { { AtmospheresSettings.Keys.CloudSamples, 48 } }, before);
            r.Preset(Look96, "Today's look with 96 grid points (the Medium profile; +1.2 ms against 48 at 1080p)", null, new Dictionary<string, double> { { AtmospheresSettings.Keys.CloudSamples, 96 } });
            r.Preset(Look128, "Today's look with 128 grid points (the High profile; +1.75 ms against 48 at 1080p)", null, new Dictionary<string, double> { { AtmospheresSettings.Keys.CloudSamples, 128 } });
        }
    }
}
