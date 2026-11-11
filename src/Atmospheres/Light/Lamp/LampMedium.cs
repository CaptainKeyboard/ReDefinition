using System;
using System.Collections.Generic;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The density of the air at a place, as the lamp scatter needs it: the fog Unity draws, the precipitation of the weather view, the
    // cloud (the volume pass's voxels and EVE's layers) and the body's dust. Each source is separate and can be switched off; the sum
    // is LampMath.Sigma. The cloud lookups are the costly part (EVE's SampleCoverage goes through reflection), so the caller evaluates
    // a lamp at 10 Hz and slews the result.
    //
    // EVE's layers are found through EveLayerAccess (the painter's reflective link; Atmosphere.dll is not referenced). A layer that
    // shines by itself (an aurora: "aurora" in its name, or a negative skylightTintMultiplier on its material) is not a medium and is
    // skipped, as CloudLayerKinds does for the raytracing. The extinction of a layer is its coverage at the place times
    // LampTuning.EveCoverageDensity, inside the layer's shell only [VERIFY: the mapping from EVE's density curves to extinction is an
    // empirical constant until the K-W3b rows are read].
    internal sealed class LampMedium
    {
        private const float ShellEdgeShare = 0.1f;
        private const double LayerRescanSeconds = 1.0;

        // Looked up on first use, so that the maths of this class (ShellFactor) runs without the engine in the unit tests.
        private static int tintId = -1;

        private static int SkylightTintId
        {
            get
            {
                if (tintId < 0) tintId = Shader.PropertyToID("skylightTintMultiplier");
                return tintId;
            }
        }

        private List<EveLayer> layers = new List<EveLayer>();
        private readonly List<EveLayer> scratch = new List<EveLayer>();
        private string layersBody = "";
        private double lastScan = double.NegativeInfinity;

        // The number of non-emissive EVE layers the last scan found (for the probe).
        internal int EveLayerCount { get; private set; }

        // Whether a layer shines by itself and so takes no part in the medium.
        internal static bool IsEmissive(EveLayer layer)
        {
            if (layer == null) return true;
            if (!string.IsNullOrEmpty(layer.Name) && layer.Name.IndexOf("aurora", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            Material main = layer.Main;
            if (main != null && main.HasProperty(SkylightTintId) && main.GetFloat(SkylightTintId) < 0f) return true;
            return false;
        }

        // The factor of a layer's shell: 1 inside, falling to 0 over the outer and the inner tenth of the shell's depth, 0 outside.
        internal static float ShellFactor(float radiusM, float innerM, float outerM)
        {
            if (!(outerM > innerM)) return 0f;
            if (radiusM < innerM || radiusM > outerM) return 0f;
            float edge = Math.Max((outerM - innerM) * ShellEdgeShare, 1f);
            float fromInner = Math.Min((radiusM - innerM) / edge, 1f);
            float fromOuter = Math.Min((outerM - radiusM) / edge, 1f);
            float f = Math.Min(fromInner, fromOuter);
            return f * f * (3f - 2f * f);
        }

        // The fog density Unity's ExponentialSquared (or Exponential, as its extinction) has now; 0 with the fog off.
        internal static float FogDensity()
        {
            if (!RenderSettings.fog) return 0f;
            switch (RenderSettings.fogMode)
            {
                case FogMode.ExponentialSquared: return RenderSettings.fogDensity;
                // The exponential fog transmits exp(-d r): its extinction is d itself; as an equal-visibility ExponentialSquared
                // density it is d / sqrt(3.912).
                case FogMode.Exponential: return RenderSettings.fogDensity / LampMath.FogSigmaFactor;
                default: return 0f;
            }
        }

        // The cloud density at a world position, 0 to 1: the larger of the volume pass's and EVE's.
        internal float CloudDensity(Vector3 world, CelestialBody body, ref LampTuning t)
        {
            float own = VolumePass.DensityAt(world);
            float eve = EveDensity(world, body, ref t);
            return Math.Max(own, eve);
        }

        private float EveDensity(Vector3 world, CelestialBody body, ref LampTuning t)
        {
            if (body == null || !EveLayerAccess.Available) return 0f;
            double now = Time.realtimeSinceStartup;
            if (!string.Equals(layersBody, body.bodyName, StringComparison.Ordinal) || now - lastScan > LayerRescanSeconds)
            {
                Rescan(body, now);
            }
            float radius = (float)((Vector3d)world - body.position).magnitude;
            float best = 0f;
            for (int i = 0; i < layers.Count; i++)
            {
                EveLayer layer = layers[i];
                float shell = ShellFactor(radius, layer.InnerRadiusM, layer.OuterRadiusM);
                if (shell <= 0f) continue;
                float type;
                float coverage;
                try
                {
                    coverage = EveLayerAccess.SampleCoverage(layer, world, out type);
                }
                catch (Exception e)
                {
                    AtmospheresLog.Failed("lamp-eve-coverage", "reading EVE's coverage for the lamp scatter failed", e);
                    return 0f;
                }
                float density = Mathf.Clamp01(coverage) * shell * t.EveCoverageDensity;
                if (density > best) best = density;
            }
            return best;
        }

        private void Rescan(CelestialBody body, double now)
        {
            lastScan = now;
            layersBody = body.bodyName;
            List<EveLayer> found;
            try
            {
                found = EveLayerAccess.Scan(body.bodyName, null, 0, layers);
            }
            catch (Exception e)
            {
                AtmospheresLog.Failed("lamp-eve-scan", "listing EVE's cloud layers for the lamp scatter failed", e);
                layers = new List<EveLayer>();
                EveLayerCount = 0;
                return;
            }
            // The list the sampling reads is replaced whole, once a second (the scan itself allocates its result).
            scratch.Clear();
            foreach (EveLayer layer in found)
            {
                EveLayerAccess.ReadMaterials(layer);
                if (!IsEmissive(layer)) scratch.Add(layer);
            }
            layers = new List<EveLayer>(scratch);
            EveLayerCount = layers.Count;
        }

        // The sigma at a world position from all sources in the order of LampMath.Sigma.
        internal LampSigma Evaluate(Vector3 world, CelestialBody body, WeatherView view, LampSources on, ref LampTuning t, float bodyScale, float dustSigma)
        {
            bool weather = view != null && !view.IsStale(Time.frameCount);
            PrecipType type = weather ? view.Type : PrecipType.None;
            float level = weather ? view.Level : 0f;
            float cloud = on.Cloud ? CloudDensity(world, body, ref t) : 0f;
            // The body's dust haze needs air: its share follows the pressure at the lamp.
            float haze = 0f;
            if (on.Dust && dustSigma > 0f && body != null && body.atmosphere)
            {
                double altitude = ((Vector3d)world - body.position).magnitude - body.Radius;
                haze = dustSigma * LampMath.AirShare(body.GetPressure(Math.Max(altitude, 0.0)), body.GetPressure(0.0));
            }
            return LampMath.Sigma(ref t, on, on.Fog ? FogDensity() : 0f, type, level, cloud, haze, bodyScale);
        }
    }
}
