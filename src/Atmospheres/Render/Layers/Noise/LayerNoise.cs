using System;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The switches of the shape model (package T02). Each is an A/B pair of the verification (curl on/off, untiling on/off, noise on/off). The
    // settings that carry them (cloudNoise, cloudNoiseUntile, cloudNoiseCurl, cloudNoiseTier and the others in AtmospheresSettings.Clouds.cs)
    // reach this object through OwnCloudOptions.Apply, and their rows are in tools/benchmark_matrix.json (group weather-5); the values below
    // are the compiled defaults.
    internal sealed class NoiseSwitches
    {
        // The volumes drive the shape and the erosion; off: T01's plain coverage threshold (no noise, no cost).
        public bool Enabled = true;
        // P11: the displacement that breaks the repetition of the volumes' tile.
        public bool Untile = true;
        // P05: the curl distortion of the erosion lookup.
        public bool Curl = true;
        // The distance LOD of the erosion detail: beyond a march spacing of about one detail cell the detail is faded out (it could only
        // shimmer there); off: the detail is read at every sample (a cost A/B).
        public bool DistanceLod = true;
        // P03: the vertical scale of the lattice about the layer's reference radius (1: the planet-centred lattice).
        public float SphereScale = 1f;
        // The volume size level: 0 Low, 1 Medium, 2 High and above (NoiseSizes.ForLevel).
        public int SizeLevel = 1;
        // The seed of the volumes and of the layers' offsets.
        public uint Seed = 1;
        // The untiling displacement's amplitude and cell in noise scales (0: the model's constants); a probe for the sweep of the warp.
        public float WarpAmplitude, WarpCell;
        // The fine octave of the erosion detail (stream T): the detail volume read again at 2.37 times the frequency.
        public bool FineDetail = true;
        // E40 soft density (an experiment of the weather player): the eroded share is not hardened (the hardness gain 0), so the share stays the water content
        // the layer's numbers describe (CloudWater). False: the edge hardness by type of package T02.
        public bool SoftEdge;
    }

    // The motion and the maps of one layer's noise: the drift of the whole noise with the local wind, the rise of the detail (P18), the
    // flow map's displacement (P16) and the colour map's strength (P15).
    internal struct LayerNoiseMotion
    {
        // The drift of the noise in world space (m/s): the wind at the camera, fed by the wind package.
        public Vector3 DriftMs;
        // P18: the speed at which the detail rises along the local up (m/s, negative: sinks), fed by E43's vertical speed.
        public float RiseMs;
        // P16: the largest displacement of the flow map in m over one loop; 0: the layer has no flow map.
        public float FlowAmplitudeM;
        // The looped flow blends two phases half a loop apart; false: one phase that grows with the loop time (no wrap).
        public bool FlowLooped;
        // P15: the strength of the colour map's tint, 0 to 1; 0: none.
        public float ColourStrength;
    }

    // The noise volumes on the GPU and the per-frame numbers of the shape model (package T02): generates the three tileable volumes at load
    // (on a worker, the layers draw without noise until they are ready), uploads them as 3D textures, keeps the layers' offsets and drifts,
    // the flow phase, and writes everything into the layer material. It uses nothing of the game, so the weather player links it as it is.
    internal sealed class LayerNoise
    {
        // The seconds a loop of the flow map takes.
        internal const double FlowLoopSeconds = 120.0;

        private static readonly int IdShape = Shader.PropertyToID("_NoiseShape"), IdDetail = Shader.PropertyToID("_NoiseDetail"),
            IdCurl = Shader.PropertyToID("_NoiseCurl"), IdFlow = Shader.PropertyToID("_NoiseFlow"), IdColour = Shader.PropertyToID("_NoiseColour"),
            IdP = Shader.PropertyToID("_NoiseP"), IdQ = Shader.PropertyToID("_NoiseQ"), IdR = Shader.PropertyToID("_NoiseR"), IdS = Shader.PropertyToID("_NoiseS"),
            IdOffset = Shader.PropertyToID("_NoiseOffset"), IdLayer = Shader.PropertyToID("_NoiseLayer"), IdStretch = Shader.PropertyToID("_NoiseStretch");

        private readonly Vector4[] offsets = new Vector4[CloudLayerDef.MaxLayers], layerData = new Vector4[CloudLayerDef.MaxLayers], stretchData = new Vector4[CloudLayerDef.MaxLayers];
        // The factor each layer's noise features are longer along the body's axis than across (CloudLayerDef.FibreStretch; 1: isotropic).
        private readonly double[] stretch = { 1.0, 1.0, 1.0, 1.0 };
        private readonly double[] driftX = new double[CloudLayerDef.MaxLayers], driftY = new double[CloudLayerDef.MaxLayers],
            driftZ = new double[CloudLayerDef.MaxLayers], rise = new double[CloudLayerDef.MaxLayers];
        private readonly LayerNoiseMotion[] motion = new LayerNoiseMotion[CloudLayerDef.MaxLayers];
        private Task<NoiseVolumeSet> task;
        private Texture3D shapeTexture, detailTexture, curlTexture;
        private Texture2D flowTexture, colourTexture, defaultFlow, defaultColour;
        private double flowTime;
        private int sizeLevelBuilt = -1;
        private uint seedBuilt;
        private double detailMean = 0.5;

        internal NoiseSwitches Switches { get; } = new NoiseSwitches();
        // The time the flow map has run, in seconds (the loop is FlowLoopSeconds); Advance adds to it, a measurement sets it.
        internal double FlowTimeSeconds { get { return flowTime; } set { flowTime = value; } }
        internal bool Ready { get; private set; }
        // The shape volume (the other volumes are null) the GPU has, for the CPU readers of the shape noise; null until the first upload.
        internal NoiseVolumeSet ShapeVolumes { get; private set; }
        // The milliseconds the generation took (the worker's time) and the bytes the volumes hold on the GPU, for the report.
        internal double GenerationMs { get; private set; }
        internal long VolumeBytes { get; private set; }
        internal string Problem { get; private set; }

        // Starts the generation on a worker; Poll uploads it when it is done. A change of the size level or of the seed starts it again.
        internal void StartGeneration()
        {
            if (task != null && sizeLevelBuilt == Switches.SizeLevel && seedBuilt == Switches.Seed) return;
            NoiseSizes sizes = NoiseSizes.ForLevel(Switches.SizeLevel);
            uint seed = Switches.Seed;
            sizeLevelBuilt = Switches.SizeLevel;
            seedBuilt = seed;
            Ready = false;
            task = Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                NoiseVolumeSet set = NoiseVolumeSet.Generate(sizes, seed);
                set.GenerationMs = watch.Elapsed.TotalMilliseconds;
                return set;
            });
        }

        // Uploads the volumes when the worker is done (true: ready). wait: blocks until it is (the player's way).
        internal bool Poll(bool wait = false)
        {
            if (Ready) return true;
            if (task == null) return false;
            if (!task.IsCompleted && !wait) return false;
            NoiseVolumeSet set;
            try
            {
                set = task.Result;
            }
            catch (Exception e)
            {
                Problem = "the noise generation failed: " + (e.InnerException != null ? e.InnerException.Message : e.Message);
                task = null;
                return false;
            }
            Upload(set);
            Ready = true;
            return true;
        }

        // Generates and uploads at once (the player; also a test of the generation time).
        internal void EnsureReady()
        {
            StartGeneration();
            Poll(true);
        }

        private void Upload(NoiseVolumeSet set)
        {
            Destroy(shapeTexture);
            Destroy(detailTexture);
            Destroy(curlTexture);
            shapeTexture = Make3D(set.ShapeN, TextureFormat.R8, set.Shape, "cloud noise shape");
            detailTexture = Make3D(set.DetailN, TextureFormat.R8, set.Detail, "cloud noise detail");
            curlTexture = Make3D(set.CurlN, TextureFormat.RGBA32, set.Curl, "cloud noise curl");
            GenerationMs = set.GenerationMs;
            detailMean = set.DetailMean;
            // The water content at the outline of the soft density reads the detail's mean (CloudSoftDensity); a stack uploaded before the volumes were ready is uploaded again.
            CloudSoftDensity.DetailMean = detailMean;
            // The shape volume alone stays on the CPU for the readers of the shape noise (CloudShadowShapeNoise: the cloud shadow's CPU twin, the sun dimming); the detail and curl
            // volumes are on the GPU only.
            ShapeVolumes = new NoiseVolumeSet { ShapeN = set.ShapeN, Shape = set.Shape, DetailMean = set.DetailMean };
            VolumeBytes = (long)set.Shape.Length + set.Detail.Length + set.Curl.Length;
        }

        private static Texture3D Make3D(int n, TextureFormat format, byte[] data, string name)
        {
            var texture = new Texture3D(n, n, n, format, false)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixelData(data, 0);
            texture.Apply(false, true);
            return texture;
        }

        // The flow map (P16: red = east, green = north, 0.5 = still; an equirectangular map like the coverage maps) and the colour map (P15:
        // the tint, white = none) of the body; null: none.
        internal void SetFlowMap(Texture2D map)
        {
            flowTexture = map;
        }

        internal void SetColourMap(Texture2D map)
        {
            colourTexture = map;
        }

        // The motion of a layer; takes effect at the next Advance.
        internal void SetMotion(int layer, LayerNoiseMotion value)
        {
            if (layer >= 0 && layer < motion.Length) motion[layer] = value;
        }

        // How much longer a layer's noise features are along the body's axis than across it (1 and above; 1: isotropic): the lookup is compressed along the axis by 1 / k. The axis itself is
        // the shader's: the third row of the world-to-map matrix, the body's pole. The uniform holds 1 - 1 / k.
        internal void SetStretch(int layer, double k)
        {
            if (layer >= 0 && layer < stretch.Length) stretch[layer] = k < 1.0 || double.IsNaN(k) ? 1.0 : k;
        }

        // Sets the distance the pattern has drifted (m, world space) and the detail has risen (m) of a layer directly (a measurement, a restore).
        internal void SetState(int layer, Vector3 driftDistanceM, float riseDistanceM)
        {
            if (layer < 0 || layer >= motion.Length) return;
            driftX[layer] = driftDistanceM.x;
            driftY[layer] = driftDistanceM.y;
            driftZ[layer] = driftDistanceM.z;
            rise[layer] = riseDistanceM;
        }

        // Advances the drift, the rise and the flow time by a frame.
        internal void Advance(double dt)
        {
            if (dt <= 0.0) return;
            flowTime += dt;
            for (int l = 0; l < motion.Length; l++)
            {
                driftX[l] += motion[l].DriftMs.x * dt;
                driftY[l] += motion[l].DriftMs.y * dt;
                driftZ[l] += motion[l].DriftMs.z * dt;
                rise[l] += motion[l].RiseMs * dt;
            }
        }

        // The model parameters of a layer as the shader gets them (the CPU twin of the uniforms), for the tests and the CPU consumers.
        internal NoiseLayerParams LayerParams(int layer, double scaleM, double referenceRadiusM)
        {
            double bx, by, bz;
            NoiseSeeds.BaseOffset(Switches.Seed, layer, out bx, out by, out bz);
            return new NoiseLayerParams
            {
                ScaleM = scaleM,
                Untile = Switches.Untile,
                Curl = Switches.Curl,
                OffsetX = bx - driftX[layer],
                OffsetY = by - driftY[layer],
                OffsetZ = bz - driftZ[layer],
                Seed = NoiseSeeds.LayerSeed(Switches.Seed, layer),
                SphereScale = Switches.SphereScale,
                NoDistanceLod = !Switches.DistanceLod,
                NoFineDetail = !Switches.FineDetail,
                DensityRemap = CloudLookTuning.DensityRemap,
                ReferenceRadiusM = referenceRadiusM,
                UpDistanceM = rise[layer],
                Stretch = 1.0 - 1.0 / stretch[layer],
                AxisZ = 1.0
            };
        }

        // Writes the volumes, the switches and the layers' numbers into the layer material.
        internal void Bind(Material material, int layerCount)
        {
            Poll();
            EnsureDefaults();
            bool on = Switches.Enabled && Ready;
            material.SetVector(IdP, new Vector4(on ? 1f : 0f, Switches.Untile ? 1f : 0f, Switches.Curl ? 1f : 0f, Switches.SphereScale));
            material.SetVector(IdQ, new Vector4(Switches.WarpAmplitude > 0f ? Switches.WarpAmplitude : (float)NoiseModel.WarpAmplitudeScales,
                                                Switches.WarpCell > 0f ? Switches.WarpCell : (float)NoiseModel.WarpCellScales, (float)NoiseModel.CurlAmplitudeScales,
                                                (float)((flowTime / FlowLoopSeconds) % 1.0)));
            material.SetVector(IdR, new Vector4((float)NoiseModel.HardnessBase, Switches.SoftEdge ? 0f : (float)NoiseModel.HardnessGain, (float)NoiseModel.ShapePeriodScales,
                                                (float)NoiseModel.DetailPeriodScales));
            material.SetVector(IdS, new Vector4((float)NoiseModel.DetailFinestCellScales, Switches.DistanceLod ? (float)NoiseModel.DetailLodFullFootCells : 0f,
                                                Switches.DistanceLod ? (float)NoiseModel.DetailLodOffFootCells : 0f, (float)detailMean));
            material.SetVector(Shader.PropertyToID("_NoiseT"), new Vector4(Switches.FineDetail ? (float)NoiseModel.FineDetailWeight : 0f, (float)NoiseModel.FineDetailFrequency, CloudLookTuning.DensityRemap, 0f));
            material.SetTexture(IdShape, shapeTexture);
            material.SetTexture(IdDetail, detailTexture);
            material.SetTexture(IdCurl, curlTexture);
            material.SetTexture(IdFlow, flowTexture != null ? flowTexture : defaultFlow);
            material.SetTexture(IdColour, colourTexture != null ? colourTexture : defaultColour);
            for (int l = 0; l < CloudLayerDef.MaxLayers; l++)
            {
                double bx, by, bz;
                NoiseSeeds.BaseOffset(Switches.Seed, l, out bx, out by, out bz);
                // The pattern moves along the drift, so the lookup moves against it.
                offsets[l] = new Vector4((float)(bx - driftX[l]), (float)(by - driftY[l]), (float)(bz - driftZ[l]), NoiseSeeds.LayerSeed(Switches.Seed, l));
                LayerNoiseMotion m = motion[l];
                layerData[l] = new Vector4(flowTexture != null ? m.FlowAmplitudeM : 0f, m.FlowLooped ? 1f : 0f, colourTexture != null ? m.ColourStrength : 0f, (float)rise[l]);
                stretchData[l] = new Vector4((float)(1.0 - 1.0 / stretch[l]), 0f, 0f, 0f);
            }
            material.SetVectorArray(IdOffset, offsets);
            material.SetVectorArray(IdLayer, layerData);
            material.SetVectorArray(IdStretch, stretchData);
        }

        private void EnsureDefaults()
        {
            if (defaultFlow == null)
            {
                defaultFlow = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { name = "cloud noise flow default", hideFlags = HideFlags.HideAndDontSave };
                defaultFlow.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1f));
                defaultFlow.Apply(false);
            }
            if (defaultColour == null)
            {
                defaultColour = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { name = "cloud noise colour default", hideFlags = HideFlags.HideAndDontSave };
                defaultColour.SetPixel(0, 0, Color.white);
                defaultColour.Apply(false);
            }
            // The 3D textures must exist for the shader to compile its samplers: 1x1x1 stand-ins until the real ones are there.
            if (!Ready && shapeTexture == null)
            {
                shapeTexture = Make3D(1, TextureFormat.R8, new byte[] { 128 }, "cloud noise shape (stand-in)");
                detailTexture = Make3D(1, TextureFormat.R8, new byte[] { 128 }, "cloud noise detail (stand-in)");
                curlTexture = Make3D(1, TextureFormat.RGBA32, new byte[] { 128, 128, 128, 255 }, "cloud noise curl (stand-in)");
            }
        }

        private static void Destroy(UnityEngine.Object o)
        {
            if (o != null) UnityEngine.Object.Destroy(o);
        }

        internal void Dispose()
        {
            Destroy(shapeTexture);
            Destroy(detailTexture);
            Destroy(curlTexture);
            Destroy(defaultFlow);
            Destroy(defaultColour);
            shapeTexture = detailTexture = curlTexture = null;
            defaultFlow = defaultColour = null;
            Ready = false;
            task = null;
        }
    }
}
