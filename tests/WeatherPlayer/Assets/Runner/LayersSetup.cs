using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // Builds the mod's layer stack, its maps and the frame numbers from one job of jobs.json (the layer scene and the cost scene share it).
    internal static class LayersSetup
    {
        // A hook run on the finished stack before it is uploaded (package T13 gives the layers their height maps here from <input>/heights.json).
        internal static System.Action<LayersJob, string, LayerStackSource> BeforeUpload;

        internal static LayerFrame Load(LayersJob job, string input, LayerRenderer renderer)
        {
            // The job's tuning keys (E40 Phase O): the look knobs and the coverage shift go through the game's code (CloudLayerDef.Effective*, CloudLookTuning).
            Tuning.ClearOverrides();
            if (job.tuning != null)
                foreach (LayersJobKey k in job.tuning) Tuning.SetOverride(k.key, k.value);
            var source = new LayerStackSource(0, job.planetRadius, job.hasSurface != 0);
            bool anyMap = false;
            foreach (LayersJobLayer jl in job.layers)
            {
                var types = new List<CloudTypeDef>();
                foreach (LayersJobType jt in jl.types)
                {
                    var xs = new float[jt.profile.Length];
                    for (int i = 0; i < xs.Length; i++) xs[i] = i / (float)(xs.Length - 1);
                    types.Add(new CloudTypeDef(jt.name, jt.baseShare, jt.topShare, new Curve(xs, jt.profile), jt.erosion));
                }
                var def = new CloudLayerDef(jl.name, 0, jl.inner, jl.outer, types, jl.sigma, (LayerKind)jl.kind)
                {
                    Coverage = jl.coverage,
                    EdgeSoftness = jl.softness,
                    FadeNearM = jl.fadeNear,
                    FadeFarM = jl.fadeFar > 0f ? jl.fadeFar : float.PositiveInfinity,
                    NoiseAmount = jl.noiseAmount,
                    NoiseScaleM = jl.noiseScale,
                    MapShiftDeg = jl.shift
                };
                bool hasMap = !string.IsNullOrEmpty(jl.coverageMap) || !string.IsNullOrEmpty(jl.typeMap);
                if (hasMap) { def.CoverageMapHandle = 0; def.TypeMapHandle = 0; anyMap = true; }
                source.Add(def, null, null);
            }
            if (BeforeUpload != null) BeforeUpload(job, input, source);
            renderer.Upload(source);
            ApplyNoise(job, input, renderer);
            if (anyMap)
            {
                renderer.Maps.Ensure(job.mapWidth, job.mapHeight);
                for (int l = 0; l < job.layers.Length; l++)
                {
                    LayersJobLayer jl = job.layers[l];
                    byte[] cov = string.IsNullOrEmpty(jl.coverageMap) ? Constant(job, 255) : File.ReadAllBytes(Path.Combine(input, jl.coverageMap));
                    byte[] typ = string.IsNullOrEmpty(jl.typeMap) ? Constant(job, 0) : File.ReadAllBytes(Path.Combine(input, jl.typeMap));
                    renderer.Maps.SetTargetBytes(l, cov, typ);
                }
                renderer.Maps.Step(0f, MapSlew.DefaultRate, true);
            }
            var frame = new LayerFrame
            {
                HasSurface = job.hasSurface != 0,
                TowardsSun = V(job.sun),
                MaxSteps = job.maxSteps,
                SunSteps = job.sunSteps,
                MinStepM = job.minStep,
                StepGrowth = job.stepGrowth,
                EarlyOut = job.earlyOut,
                Jitter = job.jitter,
                SunMarchM = job.sunMarch,
                RayLimitM = job.rayLimit,
                LayerMask = job.layerMask,
                UseSceneDepth = job.occluder != null && job.occluder.Length == 4 && job.occluder[3] > 0f,
                SunColour = Rgb(job.sunColour, new Color(3.0f, 2.9f, 2.7f)),
                AmbientTop = Rgb(job.ambientTop, new Color(0.35f, 0.42f, 0.55f)),
                AmbientBottom = Rgb(job.ambientBottom, new Color(0.14f, 0.15f, 0.16f))
            };
            // The cost controls keep the product's default (the LayerFrame's) where the job does not name a value.
            frame.CostDebug = job.costDebug;
            if (job.ambientLayerColumn != 0) frame.Light.AmbientColumnToTypeTop = false;
            if (job.ambientGain > 0f) frame.Light.AmbientGain = job.ambientGain;
            if (job.productCost != 0) frame.UseProductCost();
            frame.FixedConeRotation = job.fixedCone != 0;
            frame.HashDither = job.ignDither == 0;
            // The measurement jobs (aux, lum) integrate on the grid step as their CPU references do; the pictures take the product's refinement.
            if (job.mode != "color" && job.refineBudget == 0f) frame.RefineBudget = 0f;
            if (job.refineBudget != 0f) frame.RefineBudget = Mathf.Max(0f, job.refineBudget);   // negative: off
            if (job.refineFraction > 0f) frame.RefineFraction = job.refineFraction;
            if (job.edgePower > 0f) frame.EdgePower = job.edgePower;
            if (job.refineBisect > 0) frame.RefineBisect = job.refineBisect;
            if (job.refineStepBudget > 0f) frame.RefineStepBudget = job.refineStepBudget;
            frame.FrameNumber = job.frameNumber;
            if (job.costBound >= 0) frame.CoverageBound = job.costBound != 0;
            if (job.costLeap >= 0f) frame.EmptyLeap = job.costLeap;
            if (job.costCut >= 0f) frame.LightCutoff = job.costCut;
            if (job.lightStride >= 0) frame.LightStride = job.lightStride;
            if (job.far != null)
            {
                FarViewSettings far = frame.Far;
                far.Haze = job.far.haze != 0;
                far.ShapeMean = job.far.shapeMean != 0;
                far.HazeStrength = job.far.hazeStrength;
                far.MiePerM = job.far.mie;
                far.RayleighHeightM = job.far.rayleighHeight;
                far.MieHeightM = job.far.mieHeight;
                far.LodStart = job.far.lodStart;
                far.LodEnd = job.far.lodEnd;
                if (job.far.rayleigh != null && job.far.rayleigh.Length >= 3) far.RayleighPerM = V(job.far.rayleigh);
                far.Inscatter = Rgb(job.far.inscatter, far.Inscatter);
                far.Policy = job.far.policy != 0;
                far.MinSamples = job.far.minSamples;
                if (job.far.refine > 0f) far.RefineFraction = job.far.refine;
                // The march policy of the far view: the step budget, the shortest step and the entry's finest step by the camera's altitude.
                far.ApplyTo(frame, V(job.position).magnitude - job.planetRadius, job.planetRadius);
            }
            if (job.cheapShare >= 0f) frame.CheapShare = job.cheapShare;
            if (job.fogShare >= 0f) frame.FogShare = job.fogShare;
            return frame;
        }

        // The noise model of a job (package T02): the switches, the volumes (generated here, blocking), the flow and colour maps and the layers' motion.
        private static void ApplyNoise(LayersJob job, string input, LayerRenderer renderer)
        {
            LayerNoise noise = renderer.Noise;
            LayersJobNoise n = job.noise;
            noise.SetFlowMap(null);
            noise.SetColourMap(null);
            if (n != null)
            {
                NoiseSwitches sw = noise.Switches;
                sw.Enabled = n.disabled == 0;
                sw.Untile = n.noUntile == 0;
                sw.Curl = n.noCurl == 0;
                sw.DistanceLod = n.noLod == 0;
                sw.FineDetail = n.noFine == 0;
                sw.WarpAmplitude = n.warpAmplitude;
                sw.WarpCell = n.warpCell;
                sw.SphereScale = n.sphere > 0f ? n.sphere : 1f;
                sw.Seed = n.seed == 0u ? 1u : n.seed;
                sw.SizeLevel = n.sizeLevel;
                noise.FlowTimeSeconds = n.flowTime;
                if (!string.IsNullOrEmpty(n.flowMap)) noise.SetFlowMap(NoiseMaps.FromBytes(n.mapWidth, n.mapHeight, File.ReadAllBytes(Path.Combine(input, n.flowMap)), "flow map"));
                if (!string.IsNullOrEmpty(n.colourMap)) noise.SetColourMap(NoiseMaps.FromBytes(n.mapWidth, n.mapHeight, File.ReadAllBytes(Path.Combine(input, n.colourMap)), "colour map"));
            }
            for (int l = 0; l < job.layers.Length; l++)
            {
                LayersJobLayer jl = job.layers[l];
                noise.SetMotion(l, new LayerNoiseMotion { FlowAmplitudeM = jl.flowAmplitude, FlowLooped = jl.flowLooped != 0, ColourStrength = jl.colourStrength });
                Vector3 drift = jl.drift != null && jl.drift.Length >= 3 ? V(jl.drift) : Vector3.zero;
                noise.SetState(l, drift, jl.rise);
            }
            if (noise.Switches.Enabled) noise.EnsureReady();
        }

        internal static byte[] Constant(LayersJob job, byte value)
        {
            var b = new byte[job.mapWidth * job.mapHeight];
            for (int i = 0; i < b.Length; i++) b[i] = value;
            return b;
        }

        internal static Vector3 V(float[] a)
        {
            return new Vector3(a[0], a[1], a[2]);
        }

        internal static Color Rgb(float[] a, Color fallback)
        {
            return a != null && a.Length >= 3 ? new Color(a[0], a[1], a[2]) : fallback;
        }
    }
}
