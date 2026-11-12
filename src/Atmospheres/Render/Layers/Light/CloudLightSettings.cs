using System;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // A moon or a planet's shine as a light of the cloud pass: the direction towards it and its colour times intensity in the units of the
    // sun colour. A light with a zero colour is skipped by the shader (the caller zeroes a light that is below the horizon).
    internal struct CloudLight
    {
        public Vector3 Towards;
        public Color Colour;
    }

    // A lightning bolt as a point light of the clouds (the hook of P37, used by the lightning package T07): position in the planet frame,
    // the range in m at which the light has fallen to half, colour times intensity.
    internal struct CloudBolt
    {
        public Vector3 Position;
        public float RangeM;
        public Color Colour;
    }

    // The numbers of the cloud light (package T03): the phase, the octaves of the multiple scattering, the powder, the ambient, the light
    // march, the extra lights and the light cache switch. Plain data the caller keeps and rewrites; Upload writes it into the layer pass's
    // material (called from LayerRenderer.Apply). Defaults are the fitted values of the parity run (tools/weather_metrics/light.py).
    internal sealed class CloudLightSettings
    {
        internal const int MaxLights = 2, MaxBolts = 4;

        // The dual-lobe phase function: forward g, back g, forward weight, and the single-scattering albedo.
        public float ForwardG = 0.85f, BackG = -0.3f, ForwardWeight = 0.92f, Albedo = 0.999f;
        // The octaves of the multiple scattering: count (1 = single scattering only), the thinning of the light per octave (a), the weight (b)
        // and the eccentricity factor (c) per octave, and the brightness of the octaves above the first.
        public int Octaves = 4;
        public float OctaveA = 0.3081f, OctaveB = 0.5482f, OctaveC = 0.898f, MsBrightness = 2.9358f;
        // The diffusion term: the isotropic light of the deep core as a share of the sun's isotropic light, and the optical depth over which it builds up.
        public float Diffusion = 0.8732f, DiffusionTau = 35.807f;
        // The isotropic share of the phase of the octaves above the first (0..1; light that scattered several times has lost its direction).
        public float MsIsotropy = 0.7552f;
        // Powder: strength 0..1 and the length in m over which the density is judged.
        public float PowderStrength = 0.8075f, PowderLengthM = 460.05f;
        // The ambient: the floor as a share of the sun's isotropic share (never below 3 %), the occlusion by what lies above (0: the
        // gradient of the sky colour alone), the tint (0 grey, 1 the sky's own hue) and the gain.
        public float AmbientFloor = 0.05f, AmbientOcclusion = 1.2765f, SkyTint = 0.7f, AmbientGain = 0.2447f;
        // How the occlusion reads what lies above: false (default) from the local density and the layer's remaining height, with no taps; true: UpSteps taps
        // straight up (the earlier path, kept as a switch for the cost table; 0.9 to 2.4 ms more at 1707 x 960).
        public bool AmbientOcclusionTaps;
        // The sun's colour through the air (FX-T03): extinction per channel along the zenith path at sea level (Rayleigh scattering of the red, green and
        // blue band plus a grey aerosol share) and the scale height in m; a zero height uses the frame's sun colour unchanged (the caller that already
        // supplies a reddened colour, e.g. from Scatterer, sets it to zero).
        public Vector3 SunAirTau = new Vector3(0.0764f, 0.138f, 0.295f);
        public float SunAirHeightM = 8000f;
        // The light march: taps towards the sun (0: the frame's SunSteps), the spread of the taps over a cone (tangent of the half angle),
        // the growth of the step from tap to tap, taps for each extra light and for the ambient's tap straight up.
        public int SunStepsOverride;
        public float ConeSpread, TapRatio = 2f;
        public int MoonSteps = 2, UpSteps = 2;
        // With AmbientOcclusionTaps (package T05): 0 the taps march at every lit sample, 1 the optical depth of the last march of the span is
        // reused with the sun march's reuse, 2 it is reused and moved by what the ray has crossed since (look against the per-sample taps: mean
        // tone-mapped difference at most 0.0091 with the step growth, 0.0071 alone, on the six views of the table).
        public int UpReuse;
        // Per layer (index = layer): multiple scattering brightness, powder scale, ambient scale.
        public readonly Vector3[] LayerScale = { Vector3.one, Vector3.one, Vector3.one, Vector3.one };
        // The extra lights and the lightning bolts.
        public readonly CloudLight[] Lights = new CloudLight[MaxLights];
        public readonly CloudBolt[] Bolts = new CloudBolt[MaxBolts];
        public int BoltCount;
        // The light cache (prototype B): the atlas to read and the steps its fill uses; null or Cache.Enabled false: per-sample marches.
        public LightCache Cache;
        public int CacheFillSteps = 12;
        // The test cell of the parity harness: centre in the planet frame, horizontal and vertical radius, extinction (radius 0: off).
        public Vector3 CellCentre;
        public float CellRadiusM, CellVerticalM, CellSigma, CellEdge = 0.35f;
        public bool CellFlatBase = true;

        // The shader property ids, read on the first upload (a static field here would need Unity's native code when the settings are only built).
        private static class Ids
        {
            internal static readonly int LightP = Shader.PropertyToID("_LightP"), LightQ = Shader.PropertyToID("_LightQ"),
                LightR = Shader.PropertyToID("_LightR"), LightS = Shader.PropertyToID("_LightS"), LightT = Shader.PropertyToID("_LightT"),
                LightL = Shader.PropertyToID("_LightL"), MoonDir = Shader.PropertyToID("_MoonDir"), MoonCol = Shader.PropertyToID("_MoonCol"),
                BoltPos = Shader.PropertyToID("_BoltPos"), BoltCol = Shader.PropertyToID("_BoltCol"), CellA = Shader.PropertyToID("_CellA"),
                CellB = Shader.PropertyToID("_CellB"), LightU = Shader.PropertyToID("_LightU"), SunAir = Shader.PropertyToID("_SunAir"), PhaseP = Shader.PropertyToID("_PhaseP");
        }

        private readonly Vector4[] layerL = new Vector4[CloudLayerDef.MaxLayers], moonDir = new Vector4[MaxLights], moonCol = new Vector4[MaxLights],
            boltPos = new Vector4[MaxBolts], boltCol = new Vector4[MaxBolts];

        // The quality levels of the light march (0 low, 1 medium, 2 high): taps towards the sun and octaves.
        internal static CloudLightSettings ForQuality(int level)
        {
            var s = new CloudLightSettings();
            switch (level)
            {
                case 0: s.SunStepsOverride = 2; s.Octaves = 2; s.MoonSteps = 1; s.UpSteps = 1; break;
                case 1: s.SunStepsOverride = 3; s.Octaves = 3; break;
                default: s.SunStepsOverride = 4; s.Octaves = 4; s.MoonSteps = 3; break;
            }
            return s;
        }

        // The floor never drops below 3 % of the sun's isotropic share (the parity gate R08-ambient-floor) whatever the caller sets; the 0.1 %
        // above it keeps the measured share, a sum of many float steps, from reading just under the gate.
        internal const float MinimumFloor = 0.0301f;
        internal float EffectiveFloor { get { return Mathf.Max(AmbientFloor, MinimumFloor); } }

        // Writes the numbers into the material. The phase vector of the frame is replaced by this object's.
        internal void Upload(Material m)
        {
            m.SetVector(Ids.PhaseP, new Vector4(ForwardG, BackG, ForwardWeight, Albedo));
            m.SetVector(Ids.LightP, new Vector4(Mathf.Clamp(Octaves, 1, 4), OctaveA, OctaveB, OctaveC));
            m.SetVector(Ids.LightQ, new Vector4(Mathf.Clamp01(PowderStrength), Mathf.Max(PowderLengthM, 1f), EffectiveFloor, Mathf.Max(AmbientOcclusion, 0f)));
            m.SetVector(Ids.LightR, new Vector4(Mathf.Max(ConeSpread, 0f), Mathf.Max(TapRatio, 1f), Mathf.Clamp01(SkyTint), MsBrightness));
            bool cache = Cache != null && Cache.Enabled;
            m.SetVector(Ids.LightS, new Vector4(Mathf.Clamp(MoonSteps, 1, 8), Mathf.Clamp(UpSteps, 1, 8), AmbientGain, cache ? 1f : 0f));
            // The up term's mode: 0 the local density (no taps), 1 taps at every lit sample, 2 taps reused with the sun march, 3 reused and moved by the ray's progress.
            float upMode = AmbientOcclusionTaps ? 1f + Mathf.Clamp(UpReuse, 0, 2) : 0f;
            m.SetVector(Ids.LightT, new Vector4(Mathf.Clamp(BoltCount, 0, MaxBolts), Mathf.Clamp(SunStepsOverride, 0, 64), Mathf.Clamp(CacheFillSteps, 1, 64), upMode));
            m.SetVector(Ids.SunAir, new Vector4(SunAirTau.x, SunAirTau.y, SunAirTau.z, Mathf.Max(SunAirHeightM, 0f)));
            m.SetVector(Ids.LightU, new Vector4(Mathf.Max(Diffusion, 0f), Mathf.Max(DiffusionTau, 0.01f), Mathf.Clamp01(MsIsotropy), 0f));
            for (int i = 0; i < layerL.Length; i++)
            {
                Vector3 s = i < LayerScale.Length ? LayerScale[i] : Vector3.one;
                layerL[i] = new Vector4(s.x, s.y, s.z, 0f);
            }
            m.SetVectorArray(Ids.LightL, layerL);
            for (int i = 0; i < MaxLights; i++)
            {
                moonDir[i] = new Vector4(Lights[i].Towards.x, Lights[i].Towards.y, Lights[i].Towards.z, 0f);
                moonCol[i] = new Vector4(Lights[i].Colour.r, Lights[i].Colour.g, Lights[i].Colour.b, 0f);
            }
            m.SetVectorArray(Ids.MoonDir, moonDir);
            m.SetVectorArray(Ids.MoonCol, moonCol);
            for (int i = 0; i < MaxBolts; i++)
            {
                boltPos[i] = new Vector4(Bolts[i].Position.x, Bolts[i].Position.y, Bolts[i].Position.z, Bolts[i].RangeM);
                boltCol[i] = new Vector4(Bolts[i].Colour.r, Bolts[i].Colour.g, Bolts[i].Colour.b, 0f);
            }
            m.SetVectorArray(Ids.BoltPos, boltPos);
            m.SetVectorArray(Ids.BoltCol, boltCol);
            m.SetVector(Ids.CellA, new Vector4(CellCentre.x, CellCentre.y, CellCentre.z, CellRadiusM));
            m.SetVector(Ids.CellB, new Vector4(CellVerticalM, CellEdge, CellFlatBase ? 1f : 0f, CellSigma));
            if (Cache != null) Cache.Bind(m);
        }
    }
}
