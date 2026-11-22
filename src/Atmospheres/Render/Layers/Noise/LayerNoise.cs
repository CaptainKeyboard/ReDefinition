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
            IdOffset = Shader.PropertyToID("_NoiseOffset"), IdLayer = Shader.PropertyToID("_NoiseLayer"), IdStretch = Shader.PropertyToID("_NoiseStretch"), IdAxis = Shader.PropertyToID("_NoiseAxis"),
            IdBillow = Shader.PropertyToID("_BillowP"), IdHeapP = Shader.PropertyToID("_HeapP"), IdBlendP = Shader.PropertyToID("_BlendP");

        private readonly Vector4[] offsets = new Vector4[CloudLayerDef.MaxLayers], layerData = new Vector4[CloudLayerDef.MaxLayers], stretchData = new Vector4[CloudLayerDef.MaxLayers];
        // The factor each layer's noise features are longer along the body's axis than across (CloudLayerDef.FibreStretch; 1: isotropic).
        private readonly double[] stretch = { 1.0, 1.0, 1.0, 1.0 };
        // The vertical scale of each layer's lattice (CloudLayerDef.VerticalScale; 0: the switch SphereScale, the body's key).
        private readonly double[] vertical = new double[CloudLayerDef.MaxLayers];
        // How far each layer's shape noise is smoothed, in noise scales (CloudLayerDef.ShapeSmoothing; 0: not at all).
        private readonly double[] smoothing = new double[CloudLayerDef.MaxLayers];
        // Each layer's fibre axis numbers (CloudLayerDef.FibreMinWindMs and FibreTurnSeconds).
        private readonly float[] minWind = { FibreAxis.DefaultMinWindMs, FibreAxis.DefaultMinWindMs, FibreAxis.DefaultMinWindMs, FibreAxis.DefaultMinWindMs };
        private readonly double[] turnSeconds = { FibreAxis.DefaultTurnSeconds, FibreAxis.DefaultTurnSeconds, FibreAxis.DefaultTurnSeconds, FibreAxis.DefaultTurnSeconds };
        // The unit axis of each layer's fibre stretch: the layer's wind (the horizontal direction of the drift), smoothed in time, or the body's pole where there is no wind.
        private readonly Vector3[] axis = { Vector3.forward, Vector3.forward, Vector3.forward, Vector3.forward };
        private Vector3 pole = Vector3.forward;
        private bool poleKnown;
        private readonly Vector4[] axisData = new Vector4[CloudLayerDef.MaxLayers];
        private readonly double[] driftX = new double[CloudLayerDef.MaxLayers], driftY = new double[CloudLayerDef.MaxLayers],
            driftZ = new double[CloudLayerDef.MaxLayers], rise = new double[CloudLayerDef.MaxLayers];
        private readonly LayerNoiseMotion[] motion = new LayerNoiseMotion[CloudLayerDef.MaxLayers];
        private Task<NoiseVolumeSet> task;
        // CLOUDR1 P2: the heap volume is built on its own worker the first time a type asks for a preset (RequestHeap), so a stack without heap types pays nothing.
        private Task<HeapVolumeSet> heapTask;
        private HeapVolumeSet heapSet;
        private bool heapWanted;
        internal const int HeapSeedOffset = 3;
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
            DropHeap();
            if (heapWanted) StartHeap();
            task = Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                NoiseVolumeSet set = NoiseVolumeSet.Generate(sizes, seed);
                set.GenerationMs = watch.Elapsed.TotalMilliseconds;
                return set;
            });
        }

        // Asks for the heap volume (CLOUDR1 P2): starts its worker once; the GPU texture and the CPU twin's data appear when it is done (HeapReady).
        internal void RequestHeap()
        {
            if (heapWanted) return;
            heapWanted = true;
            if (task != null) StartHeap();
        }

        // True where a type of the uploaded stack has a heap preset, an edge exponent or a cavity: the shader then reads the second type info texture (set by LayerRenderer.Upload).
        internal bool ExtrasUsed;

        // True (the weather player): Bind waits for the heap volume's worker, so no frame is drawn with the shape volume in its place.
        internal bool BlockOnHeap;

        internal bool HeapReady { get { return heapSet != null; } }
        internal HeapVolumeSet HeapVolume { get { return heapSet; } }

        private void StartHeap()
        {
            int n = NoiseSizes.ForLevel(Switches.SizeLevel).Shape;
            uint seed = Switches.Seed + HeapSeedOffset;
            heapTask = Task.Run(() => HeapVolumeSet.Generate(n, seed));
        }

        private void DropHeap()
        {
            heapTask = null;
            heapSet = null;
            shapeHasHeap = false;
            if (ShapeVolumes != null) ShapeVolumes.Heap = null;
        }

        // Uploads the heap volume when its worker is done.
        private void PollHeap(bool wait)
        {
            if (heapTask == null || heapSet != null) return;
            if (!heapTask.IsCompleted && !wait) return;
            try
            {
                heapSet = heapTask.Result;
            }
            catch (Exception e)
            {
                Problem = "the heap volume generation failed: " + (e.InnerException != null ? e.InnerException.Message : e.Message);
                heapTask = null;
                return;
            }
            if (ShapeVolumes != null)
            {
                ShapeVolumes.Heap = heapSet;
                RebuildShapeTexture();
            }
        }

        // The shape texture once the heap volume is there: the shape volume's red channel with the heap's two presets and the cavity of the first in green, blue and alpha, the same mip chain
        // (HeapVolumes.MergeWithShape). Replaces the R8 texture; the shader reads the red channel for a type without a heap, as before.
        private void RebuildShapeTexture()
        {
            if (heapSet == null || ShapeVolumes == null || ShapeVolumes.Shape == null || ShapeVolumes.ShapeN != heapSet.N) return;
            Destroy(shapeTexture);
            byte[][] levels = HeapVolumes.MergeWithShape(ShapeVolumes.Shape, ShapeVolumes.ShapeMips, heapSet);
            var texture = new Texture3D(heapSet.N, heapSet.N, heapSet.N, TextureFormat.RGBA32, true)
            {
                name = "cloud noise shape + heap",
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave
            };
            for (int m = 0; m < levels.Length; m++) texture.SetPixelData(levels[m], m);
            texture.Apply(false, true);
            shapeTexture = texture;
            shapeHasHeap = true;
        }

        private bool shapeHasHeap;

        // Uploads the volumes when the worker is done (true: ready). wait: blocks until it is (the player's way).
        internal bool Poll(bool wait = false)
        {
            PollHeap(wait);
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
            if (heapSet != null && ShapeVolumes != null)
            {
                ShapeVolumes.Heap = heapSet;
                RebuildShapeTexture();
            }
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
            shapeTexture = MakeShape3D(set);
            shapeHasHeap = false;
            detailTexture = Make3D(set.DetailN, TextureFormat.R8, set.Detail, "cloud noise detail");
            curlTexture = Make3D(set.CurlN, TextureFormat.RGBA32, set.Curl, "cloud noise curl");
            GenerationMs = set.GenerationMs;
            detailMean = set.DetailMean;
            // The water content at the outline of the soft density reads the detail's mean (CloudSoftDensity); a stack uploaded before the volumes were ready is uploaded again.
            CloudSoftDensity.DetailMean = detailMean;
            // The shape volume alone stays on the CPU for the readers of the shape noise (CloudShadowShapeNoise: the cloud shadow's CPU twin, the sun dimming); the detail and curl
            // volumes are on the GPU only.
            ShapeVolumes = new NoiseVolumeSet { ShapeN = set.ShapeN, Shape = set.Shape, ShapeMips = set.ShapeMips, DetailMean = set.DetailMean };
            VolumeBytes = (long)set.Shape.Length + set.Detail.Length + set.Curl.Length;
        }

        // The shape volume with its mip chain (the levels NoiseVolumeSet.ShapeMips holds, each equalised again), read trilinear so that a layer's smoothing (shapeSmoothing) can ask for a level
        // between two.
        private static Texture3D MakeShape3D(NoiseVolumeSet set)
        {
            int levels = set.ShapeMips != null ? set.ShapeMips.Length + 1 : 1;
            var texture = new Texture3D(set.ShapeN, set.ShapeN, set.ShapeN, TextureFormat.R8, levels > 1)
            {
                name = "cloud noise shape",
                filterMode = levels > 1 ? FilterMode.Trilinear : FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.SetPixelData(set.Shape, 0);
            for (int m = 1; m < levels; m++) texture.SetPixelData(set.ShapeMips[m - 1], m);
            texture.Apply(false, true);
            return texture;
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

        // The drift of a layer's pattern in world space (m/s) as the last SetMotion gave it (zero for a layer that has none).
        internal Vector3 DriftOf(int layer)
        {
            return layer >= 0 && layer < motion.Length ? motion[layer].DriftMs : Vector3.zero;
        }

        // How much longer a layer's noise features are along the body's axis than across it (1 and above; 1: isotropic): the lookup is compressed along the axis by 1 / k. The axis itself is
        // the shader's: the third row of the world-to-map matrix, the body's pole. The uniform holds 1 - 1 / k.
        internal void SetStretch(int layer, double k)
        {
            if (layer >= 0 && layer < stretch.Length) stretch[layer] = k < 1.0 || double.IsNaN(k) ? 1.0 : k;
        }

        // The vertical scale of a layer's lattice (0 or less: the switch SphereScale; the cfg key verticalScale).
        internal void SetVertical(int layer, double scale)
        {
            if (layer >= 0 && layer < vertical.Length) vertical[layer] = scale > 0.0 && !double.IsNaN(scale) ? scale : 0.0;
        }

        // How far a layer's shape noise is smoothed, in noise scales (0 or less: not at all; the cfg key shapeSmoothing).
        internal void SetSmoothing(int layer, double scales)
        {
            if (layer >= 0 && layer < smoothing.Length) smoothing[layer] = scales > 0.0 && !double.IsNaN(scales) ? scales : 0.0;
        }

        // A layer's fibre axis numbers: the wind below which the fibres keep to the pole (m/s) and the time the axis takes to turn to a new wind (s; the cfg keys fibreMinWindMs and fibreTurnSeconds).
        internal void SetFibre(int layer, float minWindMs, double turnSeconds)
        {
            if (layer < 0 || layer >= minWind.Length) return;
            minWind[layer] = minWindMs >= 0f && !float.IsNaN(minWindMs) ? minWindMs : FibreAxis.DefaultMinWindMs;
            this.turnSeconds[layer] = turnSeconds >= 0.0 && !double.IsNaN(turnSeconds) ? turnSeconds : FibreAxis.DefaultTurnSeconds;
        }

        // The body's pole in world space (the third row of the world-to-map matrix): the axis of a layer's fibres where there is no wind. The first time it is known every axis takes it
        // (a layer's axis then turns from it to the wind in Advance).
        internal void SetPole(Vector3 value)
        {
            if (value.sqrMagnitude < 1e-12f) return;
            pole = value.normalized;
            if (poleKnown) return;
            poleKnown = true;
            for (int l = 0; l < axis.Length; l++) axis[l] = FibreAxis.Target(motion[l].DriftMs, pole, minWind[l]);
        }

        // Puts a layer's axis at the direction of its wind at once (a reading, the player; the game turns it over the layer's turn time in Advance).
        internal void SnapAxis(int layer)
        {
            if (layer >= 0 && layer < axis.Length) axis[layer] = FibreAxis.Target(motion[layer].DriftMs, pole, minWind[layer]);
        }

        // Puts a layer's axis at a given direction (a reading of the game's axis, the player; the game's axis may still be turning towards its wind). The pole counts as known, so the first
        // SetPole does not put the axis back at the wind's direction.
        internal void SetAxis(int layer, Vector3 value)
        {
            if (layer < 0 || layer >= axis.Length || value.sqrMagnitude < 1e-12f) return;
            axis[layer] = value.normalized;
            poleKnown = true;
        }

        // The axis of a layer as the shader gets it.
        internal Vector3 AxisOf(int layer)
        {
            return layer >= 0 && layer < axis.Length ? axis[layer] : pole;
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

        // A layer's state as SetState takes it and its wind (E40 PP: the probe's dump, so that the weather player starts from the game's pattern).
        internal void StateOf(int layer, double[] driftDistanceM, out double riseDistanceM, out Vector3 windMs)
        {
            bool ok = layer >= 0 && layer < motion.Length;
            driftDistanceM[0] = ok ? driftX[layer] : 0.0;
            driftDistanceM[1] = ok ? driftY[layer] : 0.0;
            driftDistanceM[2] = ok ? driftZ[layer] : 0.0;
            riseDistanceM = ok ? rise[layer] : 0.0;
            windMs = ok ? motion[layer].DriftMs : Vector3.zero;
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
                // The fibres turn to the layer's wind, slowly (a camera that moves over the planet turns the wind's world direction too).
                axis[l] = FibreAxis.Turn(axis[l], FibreAxis.Target(motion[l].DriftMs, pole, minWind[l]), dt, turnSeconds[l]);
            }
        }

        private int ShapeSize()
        {
            return ShapeVolumes != null ? ShapeVolumes.ShapeN : NoiseSizes.ForLevel(Switches.SizeLevel).Shape;
        }

        // The mip level of the shape volume a layer reads without a type of its own (the CPU readers that do not know the type, the cloud shadow's twin): the volume's size where it is
        // there, else the size of the switch's level.
        private double ShapeLodOf(int layer)
        {
            return CloudShapeModel.SmoothingLod(smoothing[layer], ShapeSize());
        }

        // log2 of the voxels per noise scale of the shape volume (its size over the ShapePeriodScales scales it spans): the shader adds the log2 of a type's smoothing in scales to get the
        // mip level (_NoiseT.w; CloudShapeModel.SmoothingLod is the same in double precision).
        private float ShapeVoxelsLog2()
        {
            return (float)Math.Log(ShapeSize() / NoiseModel.ShapePeriodScales, 2.0);
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
                SphereScale = vertical[layer] > 0.0 ? vertical[layer] : Switches.SphereScale,
                ShapeLod = ShapeLodOf(layer),
                SmoothingScales = smoothing[layer],
                NoDistanceLod = !Switches.DistanceLod,
                NoFineDetail = !Switches.FineDetail,
                HeapFootLod = CloudLookTuning.HeapFootLod,
                HeapIso = CloudLookTuning.HeapIso,
                HeapWarpShare = HeapModel.WarpShare,
                Blend = CloudLookTuning.NoiseBlend,
                BlendExponent = CloudLookTuning.NoiseBlendExp,
                BlendCell = CloudLookTuning.NoiseBlendCell,
                BillowContrastDelta = CloudLookTuning.BillowContrast - 1.0,
                FineWeight = CloudLookTuning.BillowFine(-1f) >= 0f ? CloudLookTuning.BillowFine(-1f) : (double?)null,
                FineFrequency = CloudLookTuning.BillowFineFreq(0f),
                ReferenceRadiusM = referenceRadiusM,
                UpDistanceM = rise[layer],
                Stretch = 1.0 - 1.0 / stretch[layer],
                AxisX = axis[layer].x,
                AxisY = axis[layer].y,
                AxisZ = axis[layer].z
            };
        }

        // Writes the volumes, the switches and the layers' numbers into the layer material.
        internal void Bind(Material material, int layerCount)
        {
            PollHeap(BlockOnHeap);
            Poll();
            EnsureDefaults();
            bool on = Switches.Enabled && Ready;
            material.SetVector(IdP, new Vector4(on ? 1f : 0f, Switches.Untile ? 1f : 0f, Switches.Curl ? 1f : 0f, 0f));
            material.SetVector(IdQ, new Vector4(Switches.WarpAmplitude > 0f ? Switches.WarpAmplitude : (float)NoiseModel.WarpAmplitudeScales,
                                                Switches.WarpCell > 0f ? Switches.WarpCell : (float)NoiseModel.WarpCellScales, (float)NoiseModel.CurlAmplitudeScales,
                                                (float)((flowTime / FlowLoopSeconds) % 1.0)));
            material.SetVector(IdR, new Vector4((float)NoiseModel.ShapePeriodScales, (float)NoiseModel.DetailPeriodScales, (float)HeapModel.PeriodScales, (float)HeapModel.WarpShare));
            material.SetVector(IdS, new Vector4((float)NoiseModel.DetailFinestCellScales, Switches.DistanceLod ? (float)NoiseModel.DetailLodFullFootCells : 0f,
                                                Switches.DistanceLod ? (float)NoiseModel.DetailLodOffFootCells : 0f, (float)detailMean));
            material.SetVector(Shader.PropertyToID("_NoiseT"), new Vector4(Switches.FineDetail ? CloudLookTuning.BillowFine((float)NoiseModel.FineDetailWeight) : 0f,
                                                                           CloudLookTuning.BillowFineFreq((float)NoiseModel.FineDetailFrequency), 0f, ShapeVoxelsLog2()));
            // PUFF: the billow detail's keys (all neutral at 0: the light tap, the contrast minus 1, the gain toward the top).
            material.SetVector(IdBillow, new Vector4(CloudLookTuning.BillowLight,CloudLookTuning.BillowContrast - 1f, CloudLookTuning.BillowTop, 0f));
            // CLOUDR1: the heap volume (x: it is built, y: bit 0 the march's spacing raises its mip level, bit 1 the heap is read at the lattice's true vertical scale, z: log2 of its voxels per noise scale like _NoiseT.w).
            material.SetVector(IdBlendP, new Vector4(CloudLookTuning.NoiseBlend ? 1f : 0f, CloudLookTuning.NoiseBlendExp, CloudLookTuning.NoiseBlendCell, CloudLookTuning.LightErosionTaps));
            material.SetVector(IdHeapP, new Vector4(shapeHasHeap ? 1f : 0f, (CloudLookTuning.HeapFootLod ? 1f : 0f) + (CloudLookTuning.HeapIso ? 2f : 0f), (float)Math.Log(ShapeSize() / HeapModel.PeriodScales, 2.0), ExtrasUsed ? 1f : 0f));
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
                stretchData[l] = new Vector4((float)(1.0 - 1.0 / stretch[l]), vertical[l] > 0.0 ? (float)vertical[l] : Switches.SphereScale, 0f, 0f);
                axisData[l] = new Vector4(axis[l].x, axis[l].y, axis[l].z, 0f);
            }
            material.SetVectorArray(IdAxis, axisData);
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
            heapTask = null;
            heapSet = null;
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
