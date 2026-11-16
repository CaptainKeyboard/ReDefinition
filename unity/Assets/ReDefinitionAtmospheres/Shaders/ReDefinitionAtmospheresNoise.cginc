// The shape and erosion model of the own cloud system's layer core (package T02): the base shape from a tileable Perlin-Worley volume, the
// erosion from a Worley detail volume, the curl distortion, the untiling displacement, the flow map, the
// upward drift and the colour map. Included by ReDefinitionAtmospheresLayers.shader after MapUv (it uses MapUv, _LayerA, _LayerD, _PlanetR
// and _WorldToMap of that file). The CPU twin in double precision is src/Atmospheres/Render/Layers/Noise/CloudShapeModel.cs and the
// NumPy twin tools/weather_metrics/noise_model.py; the integer hash and the lookups are the same in the three.
//
// The model, in lengths of the layer's noise scale s (= 1 / _LayerD.y, the lattice cell of the shape volume's finest octave):
//   q = p + layer offset - flow (the pattern moves along the flow);  q -= a (q . pole) pole (the fibre stretch, a = 1 - 1 / k);  q += A_w s W(q / (C_w s))          the untiling displacement (W: vector value noise of the integer hash)
//   n = shape(q / (16 s));  the coverage threshold moves by (n - 0.5) * amount; the soft edge turns it into the share s0
//   d = detail((q + curl * A_c s - up * rise) / (4 s));  the share is eaten into by the erosion: c = (s0 - e (1 - d)) / (1 - e (1 - d)),
//   then hardened by (hb + hg e) and clamped: a ragged type has a hard edge, a smooth one a soft edge.
// The detail is read only where the share is between 0 and 1 (the edge band) and not on the light path (gNoiseLod).
//
// Techniques (no code taken): Schneider, "The real-time volumetric cloudscapes of Horizon Zero Dawn", SIGGRAPH 2015 (Perlin-Worley shape,
// Worley detail, curl distortion, the remap that erodes); the two-phase flow map is the common technique of looped flow maps. The integer
// hash is lowbias32 (Chris Wellons, public domain).
//
// CLOUDR1 (the heap volume, P2/P3): a type with a heap preset reads the macro shape from the green or blue channel of _NoiseShape instead of its red one, a rank-uniform volume of rounded heaps in a cascade of doubled
// frequencies (HeapVolumes.cs: dome octaves round jittered points, Worley 1996, joined by a polynomial smooth maximum, Quilez; summed with weights 2^(-H k), the spectral synthesis of
// fractional Brownian fields, Mandelbrot and Van Ness 1968; the shape-by-type volume of Schneider, SIGGRAPH 2015 and "Nubis", SIGGRAPH 2017). Its mip level drops an octave only where the
// march's footprint exceeds it. Its second half is a baked cavity (ambient occlusion of the heap field) that darkens the creases between lobes. No code of these works is taken.
#ifndef REDEFINITION_ATMOSPHERES_NOISE
#define REDEFINITION_ATMOSPHERES_NOISE

sampler3D _NoiseShape;   // the shape volume (R8); once a type asks for a heap preset (CLOUDR1 P2/P3) RGBA8: r the shape, g the heap field of preset 1, b of preset 2, a the cavity of preset 1 (the pass
                         // has no sampler register left for a texture of its own: ps_4_0 has 16; HeapVolumes.MergeWithShape)
sampler3D _NoiseDetail;
sampler3D _NoiseCurl;
sampler2D _NoiseFlow;
sampler2D _NoiseColour;
sampler3D _NoiseLattice; // the untiling warp's lattice table around the viewer (E40 C4; NoiseLattice.cs)
float4 _NoiseL;          // the table: x cells per side, y 1 / that, z slices per layer's slab, w 1 / the slices of the texture
float4 _NoiseP;          // x: volumes ready (1), y: untiling, z: curl, w: the warp reads the lattice table (1) or hashes the corners of its cell (0) (layers.warpTable; the vertical scale of the lattice is per layer, _NoiseStretch.y)
float4 _NoiseQ;          // warp amplitude, warp cell, curl amplitude (in noise scales), flow phase 0..1
float4 _NoiseR;          // shape period, detail period, heap period (in noise scales), w the share of the untiling displacement's horizontal part the heap lookup keeps (HeapModel.WarpShare)
float4 _NoiseOffset[4];  // per layer: xyz the offset of the lookup in m (seed and drift), w the layer's seed
float4 _NoiseLayer[4];   // per layer: x flow amplitude in m (0: no flow map), y flow looped (1) or one phase (0), z colour map strength, w rise of the detail in m

float4 _NoiseStretch[4]; // per layer: x 1 - 1 / k of the layer's fibre stretch k (0: isotropic), y the vertical scale of the layer's lattice (1: isotropic; E40 L5 shape), zw free
float4 _NoiseAxis[4];    // per layer: xyz the unit axis of the fibre stretch in world space (the layer's wind, the pole where there is none)

float4 _NoiseS;          // distance LOD of the erosion detail: x the detail's finest cell (noise scales), y/z the march spacing in cells up to which the detail is
                         // read in full / from which it is dropped (z = 0: LOD off), w the detail volume's mean (what the erosion reads once dropped)

float4 _NoiseT;          // fine octave of the erosion detail (stream T): x its weight (0: off), y its frequency over the first read, z layers.densityRemap (0: the erosion acts only in the soft edge),
                         // w log2 of the shape volume's voxels per noise scale (the mip level of a smoothing of s scales is log2(s) + w; E40 L6 shape)

float4 _BillowP;         // billow detail (package PUFF; all 0 = the model as it was): x the billow shading of the direct light (the march scales a sample's direct light by 1 + 2 x (d - mean), d the erosion detail's value there: the bumps catch the light, the crevices between them are shaded; 0: off), y the detail's contrast about
                         // its mean minus 1 (d' = mean + (d - mean) (1 + y)), z the erosion's gain toward the cloud's top (erosion (1 + z h), h the height in the cloud 0..1), w the composite's sharpening 0..1

float4 _HeapP;           // CLOUDR1: x the heap volume is built (0: every type reads the shape volume), y bit 0 the march's spacing raises the heap's mip level (cloud.heap.footLod), bit 1 the heap is read at the lattice's true vertical scale (cloud.heap.iso), z log2 of the heap's voxels per noise scale, w a type has a heap, an edge exponent or a cavity (0: the type info 2 is not read)

// The heap preset (1, 2; 0: the shape volume) of the type at the last point LayerDensity was asked for, and the cavity channel and strength there (the march copies them right after its own density read).
static int gHeapPreset = 0;
// The untiling displacement of the last NoiseLookup (m; zero without the warp): the heap read takes its radial part out and keeps a share of the rest.
static float3 gNoiseWarp = 0;
static float gHeapCav = 1;
static float gHeapCavK = 0;

float4 _BlendP;          // CLOUDR1: x the macro shape (shape and heap volume) hides its tiling by blending instead of the untiling displacement (cloud.noise.blend; 0: the displacement), y the exponent of the blend weights, z the lattice cell in periods

// The erosion detail's value at the last point LayerDensity was asked for (the detail's mean where it was not read; PUFF: the march copies it right after its own density read, like gTopAlt).
static float gNoiseD = 0.5;
// 1 on the light path: no erosion there (the light march reads the shape only).
static int gNoiseLod = 0;
// The march's sample spacing in m at the current sample (set by the march per layer span; 0: unknown, no distance LOD).
static float gNoiseFoot = 0;
// The width in noise scales the shape volume is averaged over at the current sample (the type's shapeSmoothing from the type info texture's alpha, set by LayerDensity; 0: not at all; E40 L6 shape).
static float gNoiseSm = 0;

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

// NoiseValueVec from the lattice table (E40 C4): the quintic fade of the position, and the texture's linear filter blends the 8 corners of the cell with the fade as weights. The vector is in rgb, 0..1 per component.
float4 NoiseLatticeAt(float3 c, float slab)
{
    float3 f0 = floor(c);
    float3 t = f0 + NzFade(c - f0);
    float z = t.z - _NoiseL.x * floor(t.z * _NoiseL.y);
    return tex3Dlod(_NoiseLattice, float4((t.xy + 0.5) * _NoiseL.y, (slab * _NoiseL.z + z + 0.5) * _NoiseL.w, 0));
}

// The point the volumes are read at (see the header).
float3 NoiseLookup(int li, float3 pc, float scale, float3 flow)
{
    float3 p = pc;
    if (_NoiseStretch[li].y != 1.0)
    {
        float r = length(pc);
        float rref = _PlanetR + 0.5 * (_LayerA[li].x + _LayerA[li].y);
        p = pc * ((rref + (r - rref) * _NoiseStretch[li].y) / max(r, 1.0));
    }
    float3 q = p + _NoiseOffset[li].xyz - flow;
    // E40 L2 D1 (cfg fibreStretch): the lookup is compressed along the layer's axis (E40 L5 shape: its wind; the body's pole, the third row of the world-to-map matrix, where there is none) by 1 - _NoiseStretch[li].x, so the features are longer along it (cirrus fibres).
    float3 ax = _NoiseAxis[li].xyz;
    q -= ax * (dot(q, ax) * _NoiseStretch[li].x);
    gNoiseWarp = 0;
    if (_NoiseP.y > 0.5)
    {
        float3 wv;
        [branch] if (_NoiseP.w > 0.5) wv = NoiseLatticeAt(q / (_NoiseQ.y * scale), li).rgb * 2.0 - 1.0;
        else wv = NoiseValueVec(q / (_NoiseQ.y * scale), (uint)_NoiseOffset[li].w + 4001u);
        gNoiseWarp = (_NoiseQ.x * scale) * wv;
        q += gNoiseWarp;
    }
    return q;
}

// ---- tiling hidden without distortion (CLOUDR1; Heitz and Neyret, "High-Performance By-Example Noise using a Histogram-Preserving Blending Operator", HPG 2018, and Deliot and Heitz, "Procedural
// Stochastic Textures by Tiling and Blending", GPU Zen 2019: the method, here in 3D) ----
// The lookup point is not displaced: a cubic lattice of cells of _BlendP.z periods, each lattice point with a random offset of the volume (a translation, so the local strain is exactly 1), and the
// four corners of the Kuhn tetrahedron around the point are read at their own offsets and rotations. The reads are turned into Gaussians (the volumes are rank-uniform), blended with the weights w^e normalised
// to keep the variance, and turned back into a rank, so the result is uniform again (the cover meaning holds) and the volume's tile is not repeated.
float NzProbit(float p)
{
    p = clamp(p, 0.002, 0.998);
    float t = sqrt(-2.0 * log(min(p, 1.0 - p)));
    float x = t - (2.515517 + 0.802853 * t + 0.010328 * t * t) / (1.0 + 1.432788 * t + 0.189269 * t * t + 0.001308 * t * t * t);   // Abramowitz and Stegun 26.2.23
    return p < 0.5 ? -x : x;
}

float NzCdf(float x)
{
    float t = 1.0 / (1.0 + 0.2316419 * abs(x));   // Abramowitz and Stegun 26.2.17
    float p = 0.3989423 * exp(-0.5 * x * x) * t * (0.3193815 + t * (-0.3565638 + t * (1.781478 + t * (-1.821256 + t * 1.330274))));
    return x > 0.0 ? 1.0 - p : p;
}

// The blended rank (x) and cavity (y) of the macro shape at the undisplaced lookup point qu: ch 0 the shape volume's red channel, 1 and 2 the heap presets (green, blue; the cavity of preset 1 in alpha).
float2 NoiseBlend(int li, float3 qu, float periodM, float lod, int ch)
{
    float3 u = qu / (periodM * _BlendP.z);
    float3 fl = floor(u);
    float3 f = u - fl;
    int3 i0 = (int3)fl;
    int3 o1, o2;
    if (f.x >= f.y)
    {
        if (f.y >= f.z) { o1 = int3(1, 0, 0); o2 = int3(1, 1, 0); }
        else if (f.x >= f.z) { o1 = int3(1, 0, 0); o2 = int3(1, 0, 1); }
        else { o1 = int3(0, 0, 1); o2 = int3(1, 0, 1); }
    }
    else
    {
        if (f.y < f.z) { o1 = int3(0, 0, 1); o2 = int3(0, 1, 1); }
        else if (f.x < f.z) { o1 = int3(0, 1, 0); o2 = int3(0, 1, 1); }
        else { o1 = int3(0, 1, 0); o2 = int3(1, 1, 0); }
    }
    float s0 = max(f.x, max(f.y, f.z)), s2 = min(f.x, min(f.y, f.z)), s1 = f.x + f.y + f.z - s0 - s2;
    float4 w = pow(max(float4(1.0 - s0, s0 - s1, s1 - s2, s2), 0.0), _BlendP.y);
    float norm = rsqrt(max(dot(w, w), 1e-12)), wsum = max(w.x + w.y + w.z + w.w, 1e-12);
    uint seed = (uint)_NoiseOffset[li].w + 5003u;
    float g = 0.0, cav = 0.0;
    [loop] for (int k = 0; k < 4; k++)
    {
        int3 iv = i0 + (k == 0 ? int3(0, 0, 0) : (k == 1 ? o1 : (k == 2 ? o2 : int3(1, 1, 1))));
        uint hv = NzHash(iv, seed);
        float3 off = NzVec(hv) * periodM;
        // The volume is read rotated by one of the 48 axis permutations and sign flips of the cube (a symmetry of its statistics), so the elongation of its big lobes does not repeat either.
        uint pr = NzLow(hv ^ 0x1234567u) % 6u, sb = NzLow(hv ^ 0x7654321u);
        float3 xr = pr == 0u ? qu : (pr == 1u ? qu.xzy : (pr == 2u ? qu.yxz : (pr == 3u ? qu.yzx : (pr == 4u ? qu.zxy : qu.zyx))));
        xr *= float3((sb & 1u) != 0u ? -1.0 : 1.0, (sb & 2u) != 0u ? -1.0 : 1.0, (sb & 4u) != 0u ? -1.0 : 1.0);
        float4 t = tex3Dlod(_NoiseShape, float4((xr + off) / periodM, lod));
        float v = ch == 0 ? t.r : (ch == 1 ? t.g : t.b);
        g += w[k] * NzProbit(v);
        cav += w[k] * (ch == 1 ? t.a : 1.0);
    }
    return float2(NzCdf(g * norm), cav / wsum);
}

// The detail at a lookup point; freq 1 the first read, _NoiseT.y the fine octave (the volume's offset decorrelates it).
float NoiseDetailAt(int li, float3 q, float3 up, float scale, float freq)
{
    if (_NoiseP.z > 0.5)
        q += (tex3Dlod(_NoiseCurl, float4(q / (_NoiseR.x * scale), 0)).rgb * 2.0 - 1.0) * (_NoiseQ.z * scale);
    q -= up * _NoiseLayer[li].w;
    float dp = _NoiseR.y * scale;
    if (freq != 1.0) q = q * freq + float3(0.31, 0.57, 0.83) * dp;
    return tex3Dlod(_NoiseDetail, float4(q / dp, 0)).r;
}

// The share of the column that is cloud at a point for one flow offset.
float NoiseShareAt(int li, float3 pc, float scale, float3 flow, float cov, float coverage, float edge, float amount, float erosion)
{
    float3 q = NoiseLookup(li, pc, scale, flow);
    float lodShape = max(0.0, log2(max(gNoiseSm, 1e-4)) + _NoiseT.w);
    float n;
    // CLOUDR1 P2: a type with a heap preset reads the macro shape from the heap volume (rank-uniform like the shape volume, so the cover meaning holds). The mip level is the type's smoothing or the
    // march's spacing, whichever is coarser: an octave of the cascade is dropped only where it is below the footprint (HeapVolumes mips hold the octaves a level can resolve).
    bool isHeap = gHeapPreset > 0 && _HeapP.x > 0.5;
    float3 qh = q, qb = q - gNoiseWarp;   // the point the heap is read at (displaced), the undisplaced point the blend takes
    float lodUse = lodShape, periodUse = _NoiseR.x * scale;
    int chUse = 0;
    [branch] if (isHeap)
    {
        float lodHeap = max(0.0, log2(max(gNoiseSm, 1e-4)) + _HeapP.z);   // the smoothing in this volume's voxels per noise scale
        if (fmod(_HeapP.y, 2.0) > 0.5 && gNoiseFoot > 0) lodHeap = max(lodHeap, log2(max(gNoiseFoot / scale, 1e-4)) + _HeapP.z);
        // The levels below 8 voxels are box means that are not rank-uniform (the last is a constant 0.5: a cloud or none), the far view's mean takes over beyond them.
        lodHeap = min(lodHeap, _HeapP.z);
        // The untiling displacement stretches what it moves: its radial part would draw the heaps as columns, and all of it as worms. The heap lookup drops the radial part and keeps a share
        // of the horizontal part (the NumPy and C# twins: HeapLookup); with the blend (cloud.noise.blend) the point is not displaced at all.
        float3 upw = pc / max(length(pc), 1.0);
        float3 wh = gNoiseWarp - upw * dot(gNoiseWarp, upw);
        if (_NoiseR.w >= 0.0) qh = q - gNoiseWarp + wh * _NoiseR.w;   // _NoiseR.w < 0: the whole displacement (cloud.heap.warp -1, the lookup the shape volume has)
        // The heap is read at the lattice's true vertical scale (_HeapP.y bit 2, cloud.heap.iso): a layer's vertical scale (verticalScale) stretches the SHAPE's features along the up, which would
        // draw the rounded heaps as vertical columns and the base as drips; the compression of NoiseLookup is taken back, as for the erosion detail below.
        if (_HeapP.y > 1.5)
        {
            float rl = max(length(pc), 1.0);
            float3 isoAdd = (pc / rl) * ((1.0 - _NoiseStretch[li].y) * (rl - _PlanetR - 0.5 * (_LayerA[li].x + _LayerA[li].y)));
            qh += isoAdd;
            qb += isoAdd;
        }
        lodUse = lodHeap;
        periodUse = _NoiseR.z * scale;
        chUse = gHeapPreset;
    }
    // CLOUDR1 P2: a type with a heap preset reads the macro shape from the heap volume (rank-uniform like the shape volume, so the cover meaning holds); the other types the shape volume. With
    // cloud.noise.blend either hides its tiling by NoiseBlend (one call site: its taps are a loop, an unrolled copy of it in every inlined density crashed the shader compiler).
    [branch] if (_BlendP.x > 0.5)
    {
        float2 bl = NoiseBlend(li, qb, periodUse, lodUse, chUse);
        n = bl.x;
        if (isHeap) gHeapCav = chUse == 1 ? bl.y : 1.0;
    }
    else if (isHeap)
    {
        float4 hv = tex3Dlod(_NoiseShape, float4(qh / periodUse, lodUse));
        n = chUse == 1 ? hv.g : hv.b;
        gHeapCav = chUse == 1 ? hv.a : 1.0;   // the cavity of preset 2 is not on the GPU (one alpha channel)
    }
    else n = tex3Dlod(_NoiseShape, float4(q / periodUse, lodShape)).r;
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
    float rl = max(length(pc), 1.0);
    float3 upv = pc / rl;
    // E40 L5 shape: the detail reads the lattice at its true vertical scale (the layer's vertical scale lengthens the shape's features only): the compression of NoiseLookup is taken back.
    q += upv * ((1.0 - _NoiseStretch[li].y) * (rl - _PlanetR - 0.5 * (_LayerA[li].x + _LayerA[li].y)));
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
    // PUFF: the detail's contrast about its mean (0: as it was).
    d = saturate(_NoiseS.w + (d - _NoiseS.w) * (1.0 + _BillowP.y));
    gNoiseD = d;
    float er = erosion * (1.0 - d);
    float c = saturate((s - er) / max(1.0 - er, 1e-3));
    float eroded = c;
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
