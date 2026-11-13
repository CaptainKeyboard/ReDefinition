// The shape and erosion model of the own cloud system's layer core (package T02): the base shape from a tileable Perlin-Worley volume, the
// erosion from a Worley detail volume, the curl distortion, the untiling displacement, the edge hardness by type, the flow map, the
// upward drift and the colour map. Included by ReDefinitionAtmospheresLayers.shader after MapUv (it uses MapUv, _LayerA, _LayerD, _PlanetR
// and _WorldToMap of that file). The CPU twin in double precision is src/Atmospheres/Render/Layers/Noise/CloudShapeModel.cs and the
// NumPy twin tools/weather_metrics/noise_model.py; the integer hash and the lookups are the same in the three.
//
// The model, in lengths of the layer's noise scale s (= 1 / _LayerD.y, the lattice cell of the shape volume's finest octave):
//   q = p + layer offset - flow (the pattern moves along the flow);  q += A_w s W(q / (C_w s))          the untiling displacement (W: vector value noise of the integer hash)
//   n = shape(q / (16 s));  the coverage threshold moves by (n - 0.5) * amount; the soft edge turns it into the share s0
//   d = detail((q + curl * A_c s - up * rise) / (4 s));  the share is eaten into by the erosion: c = (s0 - e (1 - d)) / (1 - e (1 - d)),
//   then hardened by (hb + hg e) and clamped: a ragged type has a hard edge, a smooth one a soft edge.
// The detail is read only where the share is between 0 and 1 (the edge band) and not on the light path (gNoiseLod).
//
// Techniques (no code taken): Schneider, "The real-time volumetric cloudscapes of Horizon Zero Dawn", SIGGRAPH 2015 (Perlin-Worley shape,
// Worley detail, curl distortion, the remap that erodes); the two-phase flow map is the common technique of looped flow maps. The integer
// hash is lowbias32 (Chris Wellons, public domain).
#ifndef REDEFINITION_ATMOSPHERES_NOISE
#define REDEFINITION_ATMOSPHERES_NOISE

sampler3D _NoiseShape;
sampler3D _NoiseDetail;
sampler3D _NoiseCurl;
sampler2D _NoiseFlow;
sampler2D _NoiseColour;
float4 _NoiseP;          // x: volumes ready (1), y: untiling, z: curl, w: vertical scale of the spherical lattice (1: the planet-centred lattice)
float4 _NoiseQ;          // warp amplitude, warp cell, curl amplitude (in noise scales), flow phase 0..1
float4 _NoiseR;          // hardness base, hardness gain, shape period, detail period (in noise scales)
float4 _NoiseOffset[4];  // per layer: xyz the offset of the lookup in m (seed and drift), w the layer's seed
float4 _NoiseLayer[4];   // per layer: x flow amplitude in m (0: no flow map), y flow looped (1) or one phase (0), z colour map strength, w rise of the detail in m

float4 _NoiseS;          // distance LOD of the erosion detail: x the detail's finest cell (noise scales), y/z the march spacing in cells up to which the detail is
                         // read in full / from which it is dropped (z = 0: LOD off), w the detail volume's mean (what the erosion reads once dropped)

float4 _NoiseT;          // fine octave of the erosion detail (stream T): x its weight (0: off), y its frequency over the first read, z layers.densityRemap (0: the erosion acts only in the soft edge),
                         // w layers.profileShape (0: off): the lift of the coverage threshold where the type's height profile is below 1 (read by LayerDensity of the layer shader)

// 1 on the light path: no erosion there (the light march reads the shape only).
static int gNoiseLod = 0;
// The march's sample spacing in m at the current sample (set by the march per layer span; 0: unknown, no distance LOD).
static float gNoiseFoot = 0;

uint NzLow(uint x)
{
    x ^= x >> 16;
    x *= 0x7FEB352Du;
    x ^= x >> 15;
    x *= 0x846CA68Bu;
    x ^= x >> 16;
    return x;
}

uint NzHash(int3 i, uint seed)
{
    uint3 u = asuint(i);
    uint h = (seed * 0x9E3779B1u) ^ (u.x * 0x85EBCA6Bu) ^ (u.y * 0xC2B2AE35u) ^ (u.z * 0x27D4EB2Fu);
    return NzLow(h);
}

float NzUnit(uint h)
{
    return (h >> 8) * (1.0 / 16777216.0);
}

float3 NzVec(uint h)
{
    return float3(NzUnit(h), NzUnit(NzLow(h ^ 0xA511E9B3u)), NzUnit(NzLow(h ^ 0x63D83595u)));
}

float3 NzFade(float3 t)
{
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

// Smooth vector value noise at a point in lattice units, each component -1..1; no period.
float3 NoiseValueVec(float3 p, uint seed)
{
    float3 f0 = floor(p);
    int3 i0 = (int3)f0;
    float3 u = NzFade(p - f0);
    float3 acc = 0;
    [unroll] for (int k = 0; k < 8; k++)
    {
        int3 o = int3(k & 1, (k >> 1) & 1, (k >> 2) & 1);
        float w = (o.x ? u.x : 1.0 - u.x) * (o.y ? u.y : 1.0 - u.y) * (o.z ? u.z : 1.0 - u.z);
        acc += w * (NzVec(NzHash(i0 + o, seed)) * 2.0 - 1.0);
    }
    return acc;
}

// The point the volumes are read at (see the header).
float3 NoiseLookup(int li, float3 pc, float scale, float3 flow)
{
    float3 p = pc;
    if (_NoiseP.w != 1.0)
    {
        float r = length(pc);
        float rref = _PlanetR + 0.5 * (_LayerA[li].x + _LayerA[li].y);
        p = pc * ((rref + (r - rref) * _NoiseP.w) / max(r, 1.0));
    }
    float3 q = p + _NoiseOffset[li].xyz - flow;
    if (_NoiseP.y > 0.5)
        q += (_NoiseQ.x * scale) * NoiseValueVec(q / (_NoiseQ.y * scale), (uint)_NoiseOffset[li].w + 4001u);
    return q;
}

// The detail at a lookup point; freq 1 the first read, _NoiseT.y the fine octave (the volume's offset decorrelates it).
float NoiseDetailAt(int li, float3 q, float3 up, float scale, float freq)
{
    if (_NoiseP.z > 0.5)
        q += (tex3Dlod(_NoiseCurl, float4(q / (_NoiseR.z * scale), 0)).rgb * 2.0 - 1.0) * (_NoiseQ.z * scale);
    q -= up * _NoiseLayer[li].w;
    float dp = _NoiseR.w * scale;
    if (freq != 1.0) q = q * freq + float3(0.31, 0.57, 0.83) * dp;
    return tex3Dlod(_NoiseDetail, float4(q / dp, 0)).r;
}

// The share of the column that is cloud at a point for one flow offset.
float NoiseShareAt(int li, float3 pc, float scale, float3 flow, float cov, float coverage, float edge, float amount, float erosion)
{
    float3 q = NoiseLookup(li, pc, scale, flow);
    float n = tex3Dlod(_NoiseShape, float4(q / (_NoiseR.z * scale), 0)).r;
    float s = saturate((cov + (n - 0.5) * amount + coverage - 1.0) / edge + 0.5);
    // _NoiseT.z (layers.densityRemap, default 0): the erosion also thins the interior of a closed deck; 0 leaves the interior at the share 1.
    if (s <= 0 || erosion <= 0 || gNoiseLod != 0) return s;
    if (s >= 1.0 && _NoiseT.z <= 0) return s;
    // The distance LOD: the sample spacing against the detail's finest cell; once the spacing exceeds the cell the detail is dropped (the volume's mean).
    float lod = 0;
    if (_NoiseS.z > 0 && gNoiseFoot > 0)
    {
        float cell = _NoiseS.x * scale;
        lod = saturate((gNoiseFoot - _NoiseS.y * cell) / max((_NoiseS.z - _NoiseS.y) * cell, 1e-3));
    }
    float d = _NoiseS.w;
    float3 upv = pc / max(length(pc), 1.0);
    [branch] if (lod < 1.0)
    {
        d = NoiseDetailAt(li, q, upv, scale, 1.0);
        d += (_NoiseS.w - d) * lod;
    }
    // The fine octave, dropped by the distance LOD once the march spacing exceeds its own (smaller) cell.
    [branch] if (_NoiseT.x > 0)
    {
        float lod2 = 0;
        if (_NoiseS.z > 0 && gNoiseFoot > 0)
        {
            float cell2 = _NoiseS.x * scale / _NoiseT.y;
            lod2 = saturate((gNoiseFoot - _NoiseS.y * cell2) / max((_NoiseS.z - _NoiseS.y) * cell2, 1e-3));
        }
        float d2 = _NoiseS.w;
        [branch] if (lod2 < 1.0)
        {
            d2 = NoiseDetailAt(li, q, upv, scale, _NoiseT.y);
            d2 += (_NoiseS.w - d2) * lod2;
        }
        d += (d2 - d) * _NoiseT.x;
    }
    float er = erosion * (1.0 - d);
    float c = saturate((s - er) / max(1.0 - er, 1e-3));
    float eroded = saturate(c * (_NoiseR.x + _NoiseR.y * erosion));
    if (_NoiseT.z > 0) eroded *= saturate(1.0 - _NoiseT.z * er);
    return eroded;
}

// The flow map's direction at a point in world space (east and north of the map frame, from the map's red and green, 0.5 = still).
float3 NoiseFlowDir(int li, float3 pc)
{
    float2 uv = MapUv(pc, _LayerD[li].w);
    float2 v = tex2Dlod(_NoiseFlow, float4(uv, 0, 0)).rg * 2.0 - 1.0;
    float3 n = normalize(mul((float3x3)_WorldToMap, pc));
    float3 east = normalize(float3(-n.y, n.x, 1e-6));
    float3 north = cross(n, east);
    return mul(east * v.x + north * v.y, (float3x3)_WorldToMap);
}

// The share of the column that is cloud at a point of a layer (T01's threshold when the volumes are not ready or the layer has no noise).
// The single sample; the layer shader calls NoiseShare of ReDefinitionAtmospheresFarView.cginc (package T06), which takes the mean over the noise
// where the march's spacing is wider than the noise.
float NoiseShareNear(int li, float3 pc, float cov, float coverage, float edge, float amount, float erosion)
{
    float scale = _LayerD[li].y > 0 ? 1.0 / _LayerD[li].y : 0;
    if (_NoiseP.x < 0.5 || amount <= 0 || scale <= 0)
        return saturate((cov + coverage - 1.0) / edge + 0.5);
    float amp = _NoiseLayer[li].x;
    if (amp <= 0)
        return NoiseShareAt(li, pc, scale, 0, cov, coverage, edge, amount, erosion);
    // The flow map moves the whole lookup; the looped flow blends two phases half a loop apart.
    float3 dir = NoiseFlowDir(li, pc);
    float t0 = _NoiseQ.w;
    float a = NoiseShareAt(li, pc, scale, dir * (amp * t0), cov, coverage, edge, amount, erosion);
    if (_NoiseLayer[li].y < 0.5) return a;
    float t1 = frac(t0 + 0.5);
    float b = NoiseShareAt(li, pc, scale, dir * (amp * t1), cov, coverage, edge, amount, erosion);
    return lerp(a, b, abs(2.0 * t0 - 1.0));
}

// The colour a layer's cloud is tinted with at a point (the colour map, 1 = none).
float3 NoiseTint(int li, float3 pc)
{
    float k = _NoiseLayer[li].z;
    if (k <= 0) return float3(1, 1, 1);
    return lerp(float3(1, 1, 1), tex2Dlod(_NoiseColour, float4(MapUv(pc, _LayerD[li].w), 0, 0)).rgb, k);
}

#endif
