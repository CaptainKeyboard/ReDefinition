using System.Collections.Generic;
using System.IO;
using ReDefinition.Atmospheres;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // Builds the mod's layer stack, its maps and the frame numbers from one job of jobs.json (the layer scene and the cost scene share it).
    internal static class LayersSetup
    {
        internal static LayerFrame Load(LayersJob job, string input, LayerRenderer renderer)
        {
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
            renderer.Upload(source);
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
            return new LayerFrame
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
