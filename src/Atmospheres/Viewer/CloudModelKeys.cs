using System.Collections.Generic;

namespace ReDefinition.Atmospheres.Viewer
{
    // The keys and look presets of the new cloud model (package CLOUDR1) for the viewer's tables, registered through the CloudKeySource mechanism: the cumuliform volume (round lobes in a cascade),
    // the crisp rim, the cavity, the hidden tiling, the first-hit experiment and the looks (Model Parts Off for A/B, 96 and 128 Steps). This is the one place the looks are defined (the viewer's preset list shows them
    // next to Today; no cfg file holds a second copy). Every range is the clamp of the reader (CloudLookTuning); the markers say when a change shows (read at the reader named in each note).
    internal static class CloudModelKeys
    {
        // The looks: the slot of the viewer's list that the 96-point look fills, the 128-point look and the look with the model's parts off (until master plan 47.U stage 1 it also drew the map's texels as the clouds and was the look before the
        // model; the clouds now stand where the field puts them in every look). The march grid is the setting cloudSamples
        // (the grid points of the profile); layers.gridFine (the registry's Quality group) multiplies it for a quick comparison.
        internal const string Look96 = ViewerBuiltIns.NewModelSlot, Look128 = "128 Steps", LookPartsOff = "Model Parts Off";

        [CloudKeySource]
        internal static void Register(CloudKeyRegistrations r)
        {
            float presets = CumuliformModel.PresetCount;
            // Read into the type tables when the layers are uploaded (LayerRenderer.Upload); a change of the tuning table uploads them again within a frame (CloudLookTuning.Stamp, LayerRenderer.cs:272).
            r.Key(CloudLookTuning.TuneCumuliform, -1f, KeySection.Shape,
                "cumuliform preset for every type: -1 each type's cfg key, 0 none (today's shape volume), 1 bulge, 2 rough; the first use builds the cumuliform volume on a worker (about a second at Medium, the shape volume shows meanwhile)",
                KeyLiveness.Change, KeyMarker.Live, -1f, presets);
            r.Key(CloudLookTuning.TuneEdgePower, 0f, KeySection.Edge,
                "exponent of the density rim for every type of a soft layer: 0 each type's key, below 1 a crisper rim",
                KeyLiveness.Change, KeyMarker.Live, 0f, 6f);
            r.Key(CloudLookTuning.TuneCavity, -1f, KeySection.Shape,
                "cavity strength (darkens the creases between lobes): -1 each type's key, 0 off; acts only on a type that reads a cumuliform",
                KeyLiveness.Change, KeyMarker.Live, -1f, 1f);
            // Read every frame (LayerNoise.Bind: _CumuliformP and _NoiseR).
            r.Flag(CloudLookTuning.TuneCumuliformFootLod, true, KeySection.Shape,
                "the march's sample spacing raises the cumuliform volume's mip level; off: only the smoothing does", KeyLiveness.FrameHistory);
            r.Flag(CloudLookTuning.TuneCumuliformIso, true, KeySection.Shape,
                "the cumuliform is read at the lattice's true vertical scale, so a layer's vertical scale does not stretch the lobes into columns; off: as the shape volume is read", KeyLiveness.FrameHistory);
            // CloudLookTuning.NoiseBlend and the two numbers are read by LayerNoise.LayerParams every frame (CLOUDR2: the layer pass has the blended lookup, one call site with a four-tap loop; the CPU
            // twin follows for the cloud shadow).
            r.Flag(CloudLookTuning.TuneNoiseBlend, CloudLookTuning.NoiseBlendDefault > 0.5f, KeySection.Shape,
                "hides the tiling of the shape and cumuliform volumes by blending four randomly offset and rotated reads per lattice cell (the layer pass and the cloud shadow); on: the untiling displacement is not applied to them");
            r.Key(CloudLookTuning.TuneFieldWind, 0f, KeySection.Shape,
                "direction of the cloud streets in degrees from east, counter-clockwise in the map's frame", KeyLiveness.FrameHistory, KeyMarker.Live, -360f, 360f);
            r.Key(CloudLookTuning.TuneCellBulge, -1f, KeySection.Shape,
                "how far the bulges move a cloud's surface, as a share of its shorter semi-axis (0 the bare body; below 0 each layer's cellBulge)", KeyLiveness.FrameHistory, KeyMarker.Live, -1f, 2f);
            r.Key(CloudLookTuning.TuneCellBand, -1f, KeySection.Shape,
                "width in m over which a cloud's share rises at its surface, narrow is a sharp outline (below 0 each layer's cellBand)", KeyLiveness.FrameHistory, KeyMarker.Live, -1f, 300f);
            r.Key(CloudLookTuning.TuneCellFine, -1f, KeySection.Shape,
                "gain of a cloud's finer bulges, the heads on the heads (0 off; below 0 each layer's cellFine)", KeyLiveness.FrameHistory, KeyMarker.Live, -1f, 4f);
            r.Key(CloudHeightScale.TuneOn, CloudHeightScale.OnDefault, KeySection.Shape,
                "height scale: 1 every cloud height a share of the body's measured troposphere (the sizes follow), 0 the layers as written", KeyLiveness.Second, KeyMarker.Live, 0f, 1f);
            r.Key(CloudHeightScale.TuneFactor, CloudHeightScale.FactorDefault, KeySection.Shape,
                "height scale: the factor on the measured troposphere (with the body's heightFactor)", KeyLiveness.Second, KeyMarker.Live, CloudHeightScale.FactorMin, CloudHeightScale.FactorMax);
            r.Key(CloudHeightScale.TuneHorizontal, CloudHeightScale.HorizontalDefault, KeySection.Shape,
                "height scale: 1 the horizontal lengths (noise, cells, patches, meso) follow too, 0 only the heights scale", KeyLiveness.Second, KeyMarker.Live, 0f, 1f);
            r.Key(CloudLookTuning.TuneNoiseBlendExp, 8f, KeySection.Shape, "exponent of the blend weights", min: 1f, max: 32f);
            r.Key(CloudLookTuning.TuneNoiseBlendCell, 1f, KeySection.Shape, "blend lattice cell in periods", min: 0.25f, max: 16f);
            // CLOUDR2: the first taps of the sun's light march read the eroded density (LightTau, _BlendP.w), read every frame.
            r.Key(CloudLookTuning.TuneLightErosion, CloudLookTuning.LightErosionDefault, KeySection.Light,
                "number of the first taps of the sun's light march that read the eroded density, 0..8 (the lobes shade each other), 0 the shape only", KeyLiveness.FrameHistory, KeyMarker.Live, 0f, 8f);
            // Read by NoiseVolumeSet.Generate (CloudShapeModel.cs:56) when LayerNoise.StartGeneration makes the volumes: a scene change, a change of cloudNoiseTier or the seed.
            r.Key(CloudLookTuning.TuneCumuliformDetail, 0f, KeySection.Shape,
                "Hurst exponent of the erosion detail octaves, 0 the compiled weights; read only when the noise volumes are made (a scene change or a change of Cloud Noise Size)",
                KeyLiveness.Volume, KeyMarker.AfterVolumeRebuild, 0f, 2f);

            // OPT1: the map pipeline (the maps always slew on the GPU since cleanup 2 E). cloud.mapWorker is read when each steady-state round starts, so a change acts
            // within about a second.
            r.Flag(OwnCloudModule.TuneMapWorker, true, KeySection.Quality,
                "the preparation of the cloud maps (low stratiform rule, meso stage, anvils) runs on a worker thread instead of the main thread", KeyLiveness.Second);

            // CLOUDR3 P7: read every frame (OwnCloudOptions.Apply(ReconSettings) -> LayerReconstructor, _ReconK.w).
            r.Flag(OwnCloudOptions.TuneCatmull, OwnCloudOptions.CatmullDefault, KeySection.Quality,
                "the reconstruction reads its history with a Catmull-Rom filter instead of the bilinear one (less blur from sub-pixel moves, bounded by the variance clip)", KeyLiveness.FrameHistory);

            // Read every frame (OwnCloudOptions.Apply(ReconSettings) -> LayerReconstructor, _ReconT.x).
            r.Flag(OwnCloudOptions.TuneTrueDistance, OwnCloudOptions.TrueDistanceDefault, KeySection.Quality,
                "the block reconstruction moves its history by the distance the march measured for each pixel's cloud instead of by the layers' middle spheres", KeyLiveness.FrameHistory,
                effective: () => OwnCloudOptions.TrueDistanceDefaultNow);

            r.Flag(OwnCloudOptions.TuneAge, OwnCloudOptions.AgeDefault, KeySection.Quality,
                "the block reconstruction counts the marched samples each pixel's history holds: a new area is sharp after its first samples instead of after the fixed weight's settling time",
                KeyLiveness.FrameHistory, effective: () => OwnCloudOptions.AgeDefaultNow);
            r.Flag(OwnCloudOptions.TuneMarchTriangle, OwnCloudOptions.MarchTriangleDefault, KeySection.Quality,
                "the march is drawn as one procedural triangle instead of the Blit's quad: the same picture, less march time", KeyLiveness.FrameHistory,
                effective: () => OwnCloudOptions.MarchTriangleDefaultNow);
            r.Flag(OwnCloudOptions.TuneSceneDepth, OwnCloudOptions.SceneDepthDefault, KeySection.Quality,
                "the block reconstruction takes the scene's depth into its upsample, its clip box and its history test: no cloud carried onto the craft or the terrain in front of it",
                KeyLiveness.FrameHistory);
            r.Key(OwnCloudOptions.TuneDistanceReject, OwnCloudOptions.DistanceRejectDefault, KeySection.Quality,
                "with recon.trueDistance: the history is dropped where the cloud distance it stored is more than this many octaves from the frame's (a disocclusion); 0 off",
                KeyLiveness.FrameHistory, KeyMarker.Live, 0f, 4f, () => OwnCloudOptions.DistanceRejectDefaultNow);
            r.Key(OwnCloudOptions.TuneClipSigma, OwnCloudOptions.ClipSigmaDefault, KeySection.Quality,
                "width of the history's colour clip in standard deviations of the new samples around the pixel (999: no clip)",
                KeyLiveness.FrameHistory, KeyMarker.Live, 0f, 999f, () => OwnCloudOptions.ClipSigmaDefaultNow);

            // The same three per layer (the layer's name after the prefix): the layer's value wins over the global key.
            r.LayerKey(CloudLookTuning.TuneCumuliformLayer, "", -1f, KeySection.Shape, "cumuliform preset of the layer (-1 the global key, 0 none, 1 bulge, 2 rough)",
                min: -1f, max: presets);
            r.LayerKey(CloudLookTuning.TuneEdgePowerLayer, "", 0f, KeySection.Edge, "rim exponent of the layer (0 the global key; below 1 a crisper rim)", min: 0f, max: 6f);
            r.LayerKey(CloudLookTuning.TuneCavityLayer, "", -1f, KeySection.Shape, "cavity strength of the layer (-1 the global key, 0 off)", min: -1f, max: 1f);

            // The looks. A key a look does not name goes back to its default when the look is loaded (the viewer clears every key a set does not hold), so Today (the shipped defaults: the new model
            // on, the grid points of the Medium profile) undoes all of them; none touches ownClouds or cloudSource. The grid points are the setting cloudSamples, a look names it absolute.
            var before = new List<PresetLayerValue>
            {
                new PresetLayerValue(CloudLookTuning.TuneCumuliformLayer, "", "cumulus", 0f),
                new PresetLayerValue(CloudLookTuning.TuneEdgePowerLayer, "", "cumulus", 2f),
                new PresetLayerValue(CloudLookTuning.TuneCavityLayer, "", "cumulus", 0f)
            };
            r.Preset(LookPartsOff, "the cloud model's parts off, for A/B against Today (the clouds stay where the field puts them): no cumuliform volume, the rim exponent 2 (the layer's waterPower), no cavity, the tiling hidden by the untiling displacement, no eroded sun taps, 48 grid points",
                new Dictionary<string, float> { { CloudLookTuning.TuneNoiseBlend, 0f }, { CloudLookTuning.TuneLightErosion, 0f }, { OwnCloudOptions.TuneCatmull, 0f } },
                new Dictionary<string, double> { { AtmospheresSettings.Keys.CloudSamples, 48 } }, before);
            r.Preset(Look96, "Today's look with 96 grid points (the Medium profile; +1.2 ms against 48 at 1080p)", null, new Dictionary<string, double> { { AtmospheresSettings.Keys.CloudSamples, 96 } });
            r.Preset(Look128, "Today's look with 128 grid points (the High profile; +1.75 ms against 48 at 1080p)", null, new Dictionary<string, double> { { AtmospheresSettings.Keys.CloudSamples, 128 } });
        }
    }
}
