// CELLS (owner order 2026-11-17): single clouds from the layer map's cloud share, each drawn as the body of a cumulus.
//
// The layer map of a body is coarse (Kerbin: 512 x 256, a texel of 7.4 km), a fair-weather cumulus is 0.5 to 5 km wide: the map cannot place single clouds, read as it is it draws its smoothed
// texels as the clouds. The cells keep the map for what it can say, how much of a region is cloud (the share), and put that share down as single clouds: a jittered lattice of the layer's pitch on
// the sphere (rows of constant height along the meridian, each row with as many columns as its circumference holds, so there is no seam and no stretching), each cell alive with a probability and a
// disc whose radius follows the share (CellVisibleRadius: a table from a Monte Carlo of this very lattice, so the discs cover the share).
//
// Each disc is a cloud's body (CellEnvelope), drawn as Schneider draws his clouds in Nubis (SIGGRAPH 2022 and 2023: a body with a sharp surface, its share rising within a thin band inside it,
// the billows moving that surface): a half ellipsoid over the type's flat base whose depth in metres the billow noise moves in three sizes, the heads on the heads in the same proportion.
//
// The code is the project's own. A jittered lattice for cell placement is the common Worley construction (S. Worley, "A Cellular Texture Basis Function", SIGGRAPH 1996).
#ifndef REDEFINITION_CLOUD_CELLS
#define REDEFINITION_CLOUD_CELLS

// The body of a single cloud per layer (CloudLayerDef's cell keys, the viewer's overrides through CloudLookTuning): x how far the billows move the body's surface, as a share of its shorter
// semi-axis (cfg cellBillow); y the width in m of the band the share rises over at the surface (cellBand); z the gain of the finer billow octaves (cellFine; 0: not read); w flags: 1 the march
// leaps over the space beyond the billows' reach (CellShellGap, cloud.cellTrace), 2 the body is filtered by the view sample's footprint (CellBody, cloud.cellFoot), 4 the march steps finely
// inside that reach, where the surface can lie (cloud.cellShell, CELL_SHELL_STEP_MIN).
float4 _CellE[4];
// The sky drawn from the weather map (MapSkyRegions.SheetTypes, cloud.weatherMapSky; zero while it is off): per layer x 1 where the layer's sheet types are drawn connected, y the type value
// of the low sheet type (Stratocumulus), z the one of the high sheet type (Altocumulus; above 1: none), w the type value of one type step. Where the type map stands at a sheet type, the
// map's own value is drawn instead of single clouds (wMap 1): a connected deck over the area the map gives; one type step away the lattice draws alone.
float4 _CellS[4];

// The weight of the connected sheet at a type value (0: the lattice alone): 1 at a sheet type, falling linearly to 0 one type step away.
float CellSheetWeight(int li, float typeValue)
{
    float4 s = _CellS[li];
    if (s.x < 0.5) return 0.0;
    float step = max(s.w, 1e-4);
    return saturate(1.0 - max(abs(typeValue - s.y), 0.0) / step) + saturate(1.0 - abs(typeValue - s.z) / step);
}

// The finest step inside a body's shell in m: the minimum step of the march the owner chose the single clouds' look with (e40puff CEIL, 2026-11-17: 6 m).
#define CELL_SHELL_STEP_MIN 6.0
// The view sample's pixel footprint in m, set by the view march before each read of the density (0 where the pixel's footprint is off, cloudDetailLod): the band at the surface is never
// narrower than a pixel there (cloud.cellFoot). -1 outside the view march: the shadow keeps the body as it is (its CPU twin, CloudCells.CellBody, has no footprint). The light march's reads
// keep the value of the view sample they light (as gNoiseFoot), so the light sees the same filtered body.
static float gBodyFoot = -1.0;

// Whether the body is filtered by the footprint: cloud.cellFoot on, the distance LOD of the noise on (_NoiseS.z > 0, Switches.DistanceLod, as for the erosion detail) and the view march.
bool CellFootOn(int li)
{
    return fmod(floor(_CellE[li].w * 0.5), 2.0) > 0.5 && _NoiseS.z > 0.0 && gBodyFoot >= 0.0;
}

// The width in m of the band the share rises over at the surface: the layer's, at least a pixel where the body is filtered and the pixel's footprint is on.
float CellBand(int li)
{
    float band = max(_CellE[li].y, 1.0);
    return CellFootOn(li) ? max(band, gBodyFoot) : band;
}

// The lattice: a centre lies anywhere in its cell (CELL_JITTER 1: the rows and columns do not show), a disc's size is 1 +- CELL_SIGMA of the share's radius, the share f / CELL_ALIVE_F0 of the
// cells is alive and never more than CELL_ALIVE_MAX (few clouds of their own size in a fair sky, not many small ones; never every cell, which would show the lattice). The profile is 0.5 at
// CELL_EDGE_X of its radius, and a profile radius is at most CELL_R_MAX pitches: no disc of a cell outside the 3 x 3 around a point reaches it (such a centre is at least a pitch away), and the
// empty reach keeps a margin of up to 0.7 pitches for the march's leap. The C# twin is CloudCells.cs (the same numbers).
#define CELL_JITTER 1.0
#define CELL_SIGMA 0.5
#define CELL_ALIVE_F0 0.25
#define CELL_ALIVE_MAX 0.85
#define CELL_EDGE_X 0.725
#define CELL_R_MAX 0.8

// The visible radius of a disc in pitches for the share f = 0, 1/16 .. 1 (the Monte Carlo of the lattice above, 200000 points over 88 x 88 cells; CloudCells.cs holds the same table and the test
// that measures it again).
static const float CellRadiusTable[17] = { 0.0, 0.2712, 0.2728, 0.2741, 0.2994, 0.3377, 0.3747, 0.4106, 0.4462, 0.4826, 0.5209, 0.5612, 0.6061, 0.6595, 0.7262, 0.8257, 1.2573 };

float CellVisibleRadius(float f)
{
    float x = saturate(f) * 16.0;
    int i = min((int)floor(x), 15);
    return lerp(CellRadiusTable[i], CellRadiusTable[i + 1], x - i);
}

// The share of the region that is cloud: the mean of the layer's shape over the uniform noise value without erosion (x0 the map's place on the edge ramp, a the noise's width on it; the closed form
// of the mean of saturate over [x0 - a/2, x0 + a/2]).
float CellAnti(float x)
{
    return x <= 0.0 ? 0.0 : (x < 1.0 ? 0.5 * x * x : x - 0.5);
}

float CellShare(float cov, float coverage, float edge, float amount)
{
    float x0 = (cov + coverage - 1.0) / edge + 0.5;
    float a = amount / edge;
    if (a < 1e-3) return saturate(x0);
    return (CellAnti(x0 + 0.5 * a) - CellAnti(x0 - 0.5 * a)) / a;
}

uint CellHash(uint x)
{
    x ^= x >> 16;
    x *= 0x7FEB352Du;
    x ^= x >> 15;
    x *= 0x846CA68Bu;
    x ^= x >> 16;
    return x;
}

float CellUnit(uint h)
{
    return (h >> 8) * (1.0 / 16777216.0);
}

// The profile (0..1) of the nearest disc at the map place uv (u: longitude with the layer's shift, any value; v: 0 north to 1 south), for the pitch (m), the radius of the layer's middle rm (m),
// the share f and the layer's seed. size: the size factor of that disc's cell (1 where there is none); xBest: the distance to its centre over its profile radius rBest (m). reachM: a distance in
// m over which no disc begins (0 inside one; the march leaps over it, as over the map's empty reach).
// gCellBodyX (set by the caller, 0: not asked): the widest a body reaches from its centre in profile radii (CELL_EDGE_X times 1 plus the billows' swing); gCellOther returns the least horizontal
// distance in m from the point to where the body of a disc other than the nearest one can begin (the march's cap on CellShellGap; the twin does not need it, it is not an output of the profile).
static float gCellBodyX = 0.0;
static float gCellOther = 1e9;

// P0b-0 (CloudCells' prototype section below): x a share forced on every sample that has one (0: the map's own), for the cost at fixed shares; read only by the prototype's
// keywords (_BODIES_V13 for the accepted lattice). _BODIES_COUNT (layer pass 6, a debug copy of the march at shader model 5): counters of the density calls in a UAV.
#if defined(_BODIES_V1) || defined(_BODIES_V2) || defined(_BODIES_V3) || defined(_BODIES_V4) || defined(_BODIES_V5) || defined(_BODIES_V6) || defined(_BODIES_V7) || defined(_BODIES_V8) \
    || defined(_BODIES_V10) || defined(_BODIES_V11) || defined(_BODIES_V12) || defined(_BODIES_V13) || defined(_BODIES_K2) || defined(_BODIES_K3) || defined(_BODIES_K4) \
    || defined(_BODIES_K4W)
float4 _BodiesP;
#endif
#ifdef _BODIES_COUNT
// [0] cover calls of view reads (light 0), [1] of light reads, [2] live candidates in the cover calls, [3] body calls, [4] candidates looped in the body calls,
// [5] candidates that can reach the band (xv below 1 + swing + band / (2 axis)); K slots: [6] body calls where a candidate NOT kept bounds the share above the kept ones'
// result and the band (it could have changed the share), [7] body calls where the share over the kept ones differs from the share over every candidate (a second search).
RWStructuredBuffer<uint> _BodyCount : register(u1);
static uint gBodCountLive = 0u;
static int gBodCountLight = 0;
#endif

float CellProfile(float2 uv, float pitch, float rm, float f, uint seed, out float size, out float reachM, out float xBest, out float rBest)
{
    gCellOther = 1e9;
    float bestGap = 1e9;
    reachM = 0.0;
    size = 1.0;
    xBest = 1e9;
    rBest = 0.0;
    float alive = min(CELL_ALIVE_MAX, f / CELL_ALIVE_F0);
    float rProf = CellVisibleRadius(f) / CELL_EDGE_X * pitch;
    float rCap = CELL_R_MAX * pitch;
    float u = frac(uv.x);
    float lat = (0.5 - uv.y) * 3.14159265;
    float rows = max(1.0, floor(3.14159265 * rm / pitch));
    float rowH = 3.14159265 * rm / rows;
    float yRow = (lat + 1.5707963) * rm / rowH;
    float jr = floor(yRow);
    float coslat = cos(lat);
    // The reach stops where a disc of a cell outside the 3 x 3 around the point could begin: such a centre is at least one cell plus the point's distance to its own cell's nearer edge
    // away (the centres lie anywhere in their cells), along the meridian and in each row, and its disc is at most rCap wide.
    float fy = yRow - jr;
    float outerM = (1.0 + min(fy, 1.0 - fy)) * rowH;
    float best = 1e9, reach = 1e9;
    [unroll] for (int dr = -1; dr <= 1; dr++)
    {
        float j = jr + dr;
        if (j < 0.0 || j >= rows) continue;
        float cols = max(1.0, round(6.2831853 * rm * cos((j + 0.5) * rowH / rm - 1.5707963) / pitch));
        float xCol = u * cols;
        float ci = floor(xCol);
        float fx = xCol - ci;
        outerM = min(outerM, (1.0 + min(fx, 1.0 - fx)) * 6.2831853 * rm * coslat / cols);
        [unroll] for (int dc = -1; dc <= 1; dc++)
        {
            float c = ci + dc;
            float cw = c - cols * floor(c / cols);
            uint h = CellHash((uint)j * 0x9E3779B1u ^ (uint)cw * 0x85EBCA6Bu ^ seed * 0xC2B2AE35u);
            if (CellUnit(h) >= alive) continue;
#ifdef _BODIES_COUNT
            gBodCountLive += 1u;
#endif
            float jx = (CellUnit(CellHash(h ^ 0xA511E9B3u)) - 0.5) * CELL_JITTER;
            float jy = (CellUnit(CellHash(h ^ 0x63D83595u)) - 0.5) * CELL_JITTER;
            float s = 1.0 + CELL_SIGMA * (2.0 * CellUnit(CellHash(h ^ 0x2C1B3C6Du)) - 1.0);
            float dx = (u - (c + 0.5 + jx) / cols) * 6.2831853 * rm * coslat;
            float dy = (lat - ((j + 0.5 + jy) * rowH / rm - 1.5707963)) * rm;
            float d = sqrt(dx * dx + dy * dy);
            float r = min(rProf * s, rCap);
            float x = d / r;
            float gap = d - gCellBodyX * r;
            if (x < best) { best = x; size = s; rBest = r; gCellOther = min(gCellOther, bestGap); bestGap = gap; }
            else gCellOther = min(gCellOther, gap);
            reach = min(reach, d - r);
        }
    }
    gCellOther = min(gCellOther, outerM - gCellBodyX * rCap);
    reachM = max(min(reach, outerM - rCap), 0.0);
#ifdef _BODIES_V9
    // P0 leap check's control (cloud.bodies 9): the accepted lattice with no leaps.
    gCellOther = -1e9;
    reachM = 0.0;
#endif
    xBest = best;
    return 1.0 - smoothstep(0.45, 1.0, best);
}

// The nearest disc of the last CellCover (gCellX below 0: none read): its distance over its profile radius, the cloud's core strength, the share of the map's own value there (wMap) and the
// profile radius in m. The body (CellBody) reads them.
static float gCellX = -1.0;
static float gCellCore = 0.0;
static float gCellW = 1.0;
static float gCellR = 0.0;
// The depth of the last CellBody's point below the outer side of the shell the billows can move the surface within (m, negative outside the shell; it changes by at most 1 per m), and whether a
// body was read there (the caller resets it).
static float gBodyFar = 0.0;
static bool gBodyRead = false;

// The layer map's value at a point with the cells, for the coverage bounds and the far view. The disc's profile p is placed on the layer's threshold (p 0.5 at the threshold, 0 half a unit below
// it), and inside the disc it rises to the cloud's core strength: the map's excess over the threshold (how much the region convects: a fair sky's clouds stay low, a stormy region's grow) times
// the cell's size factor (the wide clouds are the tall ones; a field holds many low clouds and a few towers). wMap: the share of the map's own value (a closed deck above a share of 0.8, where
// the discs merge; and the far view, where the march's spacing passes a quarter of the pitch: the cells would alias, the map's value has their mean; and a sheet type of the map-driven
// sky, sheetW from CellSheetWeight, 0 otherwise).
float CellCover(float cov, float2 uv, float pitch, float rm, float coverage, float edge, float amount, float foot, uint seed, float sheetW, out float reachM, out float wMap)
{
    float f = CellShare(cov, coverage, edge, amount);
#ifdef _BODIES_V13
    if (_BodiesP.x > 0.0 && f > 0.0) f = _BodiesP.x;
#endif
#ifdef _BODIES_COUNT
    gBodCountLive = 0u;
#endif
    float size, xBest, rBest;
    float p = CellProfile(uv, pitch, rm, f, seed, size, reachM, xBest, rBest);
#ifdef _BODIES_COUNT
    InterlockedAdd(_BodyCount[gBodCountLight != 0 ? 1 : 0], 1u);
    InterlockedAdd(_BodyCount[2], gBodCountLive);
#endif
    float core = clamp(0.5 * (cov + coverage - 1.0) + 0.35, 0.15, 0.5) * size;
    float e = p > 0.5 ? (2.0 * p - 1.0) * core : p - 0.5;
    wMap = max(max(saturate((f - 0.8) / 0.15), foot > 0.0 ? saturate((foot - 0.25 * pitch) / (0.5 * pitch)) : 0.0), saturate(sheetW));
    gCellX = xBest;
    gCellR = rBest;
    gCellCore = core;
    gCellW = wMap;
    return lerp(1.0 - coverage + e, cov, wMap);
}

// The finer billow octaves of a single cloud's body: the heap field (the shape volume's green channel, preset billow) read at 0.37 and 0.137 of its period at the lattice's true vertical scale
// (as the heap lookup, so the heads are round), each centred on 0. The reads are straight (no blend), offset from each other and from the first octave. Needs the noise include's lookup.
float2 CellFineBillows(int li, float3 pc)
{
    float scale = _LayerD[li].y > 0 ? 1.0 / _LayerD[li].y : 250.0;
    float3 q = NoiseLookupBase(li, pc, scale, 0);
    float rl = max(length(pc), 1.0);
    q += (pc / rl) * ((1.0 - _NoiseStretch[li].y) * (rl - _PlanetR - 0.5 * (_LayerA[li].x + _LayerA[li].y)));
    float period = _NoiseR.z * scale;
    float b2 = tex3Dlod(_NoiseShape, float4(q / (0.37 * period) + float3(0.31, 0.57, 0.83), 0)).g;
    float b3 = tex3Dlod(_NoiseShape, float4(q / (0.137 * period) + float3(0.71, 0.13, 0.47), 0)).g;
    return float2(b2 - 0.5, b3 - 0.5);
}

// The body of a single cloud at a point: its share, 0 outside and 1 inside, rising over the band at its surface. The body is a half ellipsoid over the type's flat base: horizontal semi-axis the
// disc's visible radius (CELL_EDGE_X of its profile radius gCellR, where the radius table puts the edge), vertical the cloud's own height (a share of the type's height typeHeightM from the core
// strength). Its depth below the surface in m, approximated as (1 - the ellipsoid's radial coordinate) times the shorter semi-axis, is moved by the billows: the first octave (billow: the
// layer's blended noise rank, 0..1) by up to _CellE.x of that semi-axis, the finer ones (fine, centred on 0) in proportion to their size, so the heads on the heads are smaller in the same
// ratio (the cumulus's fractal surface). h: the height share of the type at the point.
float CellBody(int li, float h, float billow, float2 fine, float typeHeightM)
{
    float4 e = _CellE[li];
    float top = clamp(gCellCore / 0.5, 0.15, 1.0);
    float semiH = max(top * typeHeightM, 1.0), semiR = max(CELL_EDGE_X * gCellR, 1.0);
    float xv = gCellX / CELL_EDGE_X, hv = h / top;
    float axis = min(semiR, semiH);
    float qBase = (1.0 - sqrt(xv * xv + hv * hv)) * axis;
    float band = CellBand(li);
    // A4 (cloud.cellFoot): the body filtered by the view sample's footprint, as the erosion detail is (its distance LOD, NoiseShareAt): a finer octave fades out while the sample's footprint
    // (gNoiseFoot, the march's spacing, or the pixel's size there where that is larger and on) grows from one to two of its cells (the heap field's strongest octave has two cells per period, so
    // a cell is half the octave's period, 0.37 and 0.137 of the heap period), and the surface band is at least a pixel wide (gBodyFoot, CellBand). The octaves are centred on 0, so the body's mean
    // stays; finer than the sample, they could only be missed or flicker.
    [branch] if (CellFootOn(li) && gNoiseFoot > 0.0)
    {
        float cell = 0.5 * _NoiseR.z * (_LayerD[li].y > 0 ? 1.0 / _LayerD[li].y : 250.0);
        float full = _NoiseS.y, off = max(_NoiseS.z, full + 1e-3);
        fine.x *= 1.0 - saturate((gNoiseFoot - full * 0.37 * cell) / ((off - full) * 0.37 * cell));
        fine.y *= 1.0 - saturate((gNoiseFoot - full * 0.137 * cell) / ((off - full) * 0.137 * cell));
    }
    float q = qBase + e.x * axis * (billow - 0.5 + e.z * (0.37 * fine.x + 0.137 * fine.y));
    // The shell: the billows move the surface by at most e.x axis (0.5 + 0.5 e.z (0.37 + 0.137)) outwards, so beyond that the body's own term alone bounds the distance to it: the ellipsoid's
    // radial coordinate changes by at most 1 / axis per m, so its term by at most 1 (gBodyFar, the depth below the shell's outer side).
    gBodyFar = qBase + e.x * axis * (0.5 + 0.5 * e.z * (0.37 + 0.137));
    gBodyRead = true;
#ifdef _BODIES_COUNT
    InterlockedAdd(_BodyCount[3], 1u);
    InterlockedAdd(_BodyCount[4], 1u);
    InterlockedAdd(_BodyCount[5], (1.0 - xv + e.x * (0.5 + 0.5 * e.z * (0.37 + 0.137))) * axis > -0.5 * band ? 1u : 0u);
#endif
    return saturate(q / band + 0.5);
}

// The distance along any line from the point of the last CellBody within which the body's surface band cannot begin (m, 0 inside the shell or where no body was read): the depth below the shell's
// outer side, less the band's half (slope 1). Only the nearest disc's body is known, so the caller caps it below the reach of a neighbour's body (gCellOther).
float CellShellGap(int li)
{
    if (!gBodyRead) return 0.0;
    return max(-gBodyFar - 0.5 * CellBand(li), 0.0);
}

// The widest reach of a body from its disc's centre in profile radii for the layer (gCellBodyX): the horizontal semi-axis CELL_EDGE_X times the radial coordinate where the billows' largest outward
// swing still reaches the band, e.x (0.5 + e.z (0.37 + 0.137) / 2) of the shorter semi-axis, bounded by the horizontal one; the band's half width is the caller's.
float CellBodyReachX(int li)
{
    float4 e = _CellE[li];
    return CELL_EDGE_X * (1.0 + e.x * (0.5 + 0.5 * e.z * (0.37 + 0.137)));
}

// ---- P0 COST PROTOTYPE (M2a, .local/plans/wolken-m2a-technik.md section 9.1): bodies of several size classes, scheme G2 of the placement experiment ----
// Compiled only into the view march's pass with one of the local keywords _BODIES_V1 .. _BODIES_V6 (tuning key cloud.bodies 1 .. 6, default 0); without a keyword none of this is
// compiled and the pass is the accepted lattice above. Life, motion, clustering and the shadow are not part of the prototype; the share table is the experiment's g(f), approximate.
// Every class loop is a [loop]: fully unrolled (classes, cells and bodies), the march pass did not compile within Unity's shader compiler timeout (fxc: K = 2 took 17 minutes,
// G2 more than 20; Unity's compiler process was terminated).
//   V1 G2 as designed (3 classes, 2 x 2 each, jitter +-0.45, radius cap 0.5 pitch, two bodies per cell in the largest class): no arrays; the cover pass keeps the bounds and the
//      nearest disc, the body pass runs the placement again with the height and the billows known; the cells of a class [unroll].
//   V2 F2 (the largest class 3 x 3, jitter +-0.7, cap 0.8, one body per cell), V3 K = 2 (the two larger classes of G2): both as V5 (as V1, F2 did not compile in Unity's time).
//   V4 G2 with the candidates kept in arrays from the cover pass to the body pass (dynamically indexed under the class loop).
//   V5 V1 with the cells' loops as [loop] too. V6 the neighbourhood check: V5 with a 6 x 6 search per class (the same bounds): its picture must equal V5's bit for bit.
//   V7 V4 with two hashes per body in place of four and the size without pow (the hashes' share of the cost).
//   V8 the leap check: V5 with no leaps (no reach, no shell gap); V9 the same for the accepted lattice (in CellProfile), its control: V8 against V5 must differ as V9 against no keyword.
// P0b-0 (the split of V7's cost and the pruned form; since then only 7 and 10 .. 13 are compiled into the march pass, the code of 1 .. 6, 8, 9 stays):
//   V10 V7's search with no union: the cover pass searches as V7 (no arrays), the body is the nearest disc's alone (today's CellBody).
//   V11 the union over constant candidates: today's cover pass (the lattice), then V7's sixteen array slots filled from its nearest disc (the first that disc, the
//       other fifteen beyond reach) and V7's union loop over them: the picture is today's, the cost adds the arrays and the union.
//   V12 V7 pruned: a dead cell leaves after its alive hash (as the lattice does), only live candidates enter the arrays, the union loops over them and skips a body
//       before its square root where it cannot reach the band (xv at or above 1 + swing + band / (2 axis); its shell bound is then taken from xv alone).
//   V13 today's lattice with the forced share of _BodiesP (the control at fixed shares).
// P0b-1 (cloud.bodies 14, 15, 16; _BODIES_K2, K3, K4): the union over the K best live candidates in fixed register slots, no arrays. The march reads the type (base, top,
// the height share h) BEFORE the search, so the one search computes every candidate's exact body term below the billows (qb, its axis) and its exact shell depth
// (qb + axis x swing, which bounds its share from above); it keeps the K with the largest bound, takes the shell depth's maximum and the leaps' minima over ALL of them
// (the leaps are V7's). The body pass is the maximum over the K slots with the shared billow value. Every early exit of the march stays where V7 has it.
// P0d: _BODIES_K4W (cloud.bodies 17) is K4 with a 4 x 4 search in every class (one more cell on each side, its leap bounds widened to match): whether a body
// is cut at the border of the 2 x 2 search (the straight-edged body of the P0c crop).
#if defined(_BODIES_K4W)
#define _BODIES_K4 1
#define BODIES_WIDE 1
#endif
#if defined(_BODIES_K2) || defined(_BODIES_K3) || defined(_BODIES_K4)
#define BODIES_KSLOTS 1
#define BODIES_CHEAP_HASH 1
#if defined(_BODIES_K2)
#define BODY_KS 2
#elif defined(_BODIES_K3)
#define BODY_KS 3
#else
#define BODY_KS 4
#endif
#endif
#if defined(_BODIES_V1) || defined(_BODIES_V2) || defined(_BODIES_V3) || defined(_BODIES_V4) || defined(_BODIES_V5) || defined(_BODIES_V6) || defined(_BODIES_V7) || defined(_BODIES_V8) \
    || defined(_BODIES_V10) || defined(_BODIES_V11) || defined(_BODIES_V12) || defined(BODIES_KSLOTS)
#define CLOUD_BODIES 1
#if defined(_BODIES_V8)
#define BODIES_NO_LEAP 1
#endif
#if defined(_BODIES_V4) || defined(_BODIES_V7) || defined(_BODIES_V11) || defined(_BODIES_V12)
#define BODIES_ARRAYS 1
#endif
#if defined(_BODIES_V7) || defined(_BODIES_V10) || defined(_BODIES_V11) || defined(_BODIES_V12)
#define BODIES_CHEAP_HASH 1
#endif
#if defined(_BODIES_V10)
#define BODIES_NO_UNION 1
#endif
#if defined(_BODIES_V11)
#define BODIES_FIXED_CANDIDATES 1
#endif
#if defined(_BODIES_V12)
#define BODIES_PRUNE 1
#endif
#define BODIES_CLASS_LOOP [loop]
#if defined(_BODIES_V6)
#define BODIES_BRUTE 1
#endif
#if defined(_BODIES_V2) || defined(_BODIES_V3) || defined(_BODIES_V5) || defined(_BODIES_V6) || defined(_BODIES_V8) || defined(BODIES_WIDE)
#define BODIES_CELL_LOOP [loop]   // the wide search (K4W) unrolled took fxc 41 minutes
#else
#define BODIES_CELL_LOOP [unroll]
#endif

// The classes in units of the layer's pitch P: class pitch PM x P, size unit UM x P (the experiment's 1.60 / 2.53 / 4.0 and 1 / 2 / 4 with its middle class at today's lattice),
// cells searched per axis S, centre jitter +-J pitches, profile radius cap R pitches, bodies per cell NB, alive share per f / CELL_ALIVE_F0 A (the count law b = 1.678, the "norm" rule).
#if defined(_BODIES_V2)
#define BODY_K 3
#define BODY_SMAX 3
#define BODY_N 17
static const float BodyPM[3] = { 0.8, 1.265, 2.0 };
static const float BodyUM[3] = { 0.5, 1.0, 2.0 };
static const int BodyS[3] = { 2, 2, 3 };
static const float BodyJ[3] = { 0.45, 0.45, 0.7 };
static const float BodyR[3] = { 0.5, 0.5, 0.8 };
static const int BodyNB[3] = { 1, 1, 1 };
static const float BodyA[3] = { 0.4096, 0.64, 1.0 };
#elif defined(_BODIES_V3)
#define BODY_K 2
#define BODY_SMAX 2
#define BODY_N 12
static const float BodyPM[2] = { 1.265, 2.0 };
static const float BodyUM[2] = { 1.0, 2.0 };
static const int BodyS[2] = { 2, 2 };
static const float BodyJ[2] = { 0.45, 0.45 };
static const float BodyR[2] = { 0.5, 0.5 };
static const int BodyNB[2] = { 1, 2 };
static const float BodyA[2] = { 0.64, 0.5 };
#else
#define BODY_K 3
#define BODY_SMAX 2
#define BODY_N 16
static const float BodyPM[3] = { 0.8, 1.265, 2.0 };
static const float BodyUM[3] = { 0.5, 1.0, 2.0 };
static const int BodyS[3] = { 2, 2, 2 };
static const float BodyJ[3] = { 0.45, 0.45, 0.45 };
static const float BodyR[3] = { 0.5, 0.5, 0.5 };
static const int BodyNB[3] = { 1, 1, 2 };
static const float BodyA[3] = { 0.4096, 0.64, 0.5 };
#endif
#define BODY_NBMAX 2
#ifdef BODIES_BRUTE
#define BODIES_ROWS_MAX (BODY_SMAX + 4)
#elif defined(BODIES_WIDE)
#define BODIES_ROWS_MAX (BODY_SMAX + 2)
#else
#define BODIES_ROWS_MAX BODY_SMAX
#endif

// The visible radius in size units for the share f = 0, 0.25 .. 1 (the experiment's calibrated g of G2 with its life envelope and clusters; approximate here, where neither is drawn).
static const float BodyGTable[5] = { 0.27, 0.300, 0.361, 0.471, 0.60 };

float BodyVisibleG(float f)
{
    float x = saturate(f) * 4.0;
    int i = min((int)floor(x), 3);
    return lerp(BodyGTable[i], BodyGTable[i + 1], x - i);
}

// The cover pass's inputs, kept for the body pass (V4, V5, V6), and the candidates' state (V1, V2, V3: the visible distance d / (CELL_EDGE_X r), the visible semi-axis, the top share).
static float2 gBodUv = float2(0.0, 0.0);
static float gBodPitch = 0.0, gBodRm = 0.0, gBodF = 0.0, gBodCore = 0.0;
static uint gBodSeed = 0u;
#ifdef BODIES_ARRAYS
static float gBodX[BODY_N];
static float gBodSemiR[BODY_N];
static float gBodTop[BODY_N];
#endif
static int gBodN = BODY_N;   // V12: the live candidates in the arrays
#ifdef BODIES_KSLOTS
// The K slots as separate scalars (fxc made static arrays indexable even with constant indices): the bound qb + axis x swing (sorted, largest first), qb and the axis;
// the largest bound of a candidate not kept; the shell depth's maximum over every candidate.
static float gKU0 = -1e9, gKU1 = -1e9, gKU2 = -1e9, gKU3 = -1e9;
static float gKQ0 = -1e9, gKQ1 = -1e9, gKQ2 = -1e9, gKQ3 = -1e9;
static float gKA0 = 0.0, gKA1 = 0.0, gKA2 = 0.0, gKA3 = 0.0;
static float gKDrop = -1e9;
static float gKFar = -1e9;
// P0d: the largest share and core strength within the leap's reach (from _BodiesCovMax) and whether that bound is used.
static float gBodFLoc = 1.0, gBodCoreLoc = 0.5;
static bool gBodLocalOn = false;
// P0d: the coverage map's largest value over the neighbourhood a leap can reach around each texel (built on the CPU, LayerRenderer.BodiesCovMax; read with Load).
// _BodiesP.z 1 where it is bound, _BodiesP.w the leap's largest length in m that its window covers (the march's leaps are clamped to it).
Texture2D<float> _BodiesCovMax;
// One step of the insertion: the candidate (tu, tq, ta) passes slot i, the larger bound stays, the smaller goes on (selects, no branch).
#define BODY_KSWAP(i) { bool sw_ = tu > gKU##i; float x_ = gKU##i; gKU##i = sw_ ? tu : x_; tu = sw_ ? x_ : tu; \
                        x_ = gKQ##i; gKQ##i = sw_ ? tq : x_; tq = sw_ ? x_ : tq; x_ = gKA##i; gKA##i = sw_ ? ta : x_; ta = sw_ ? x_ : ta; }
#if BODY_KS == 2
#define BODY_KINSERT BODY_KSWAP(0) BODY_KSWAP(1)
#define BODY_KMAX(bt) max(gKQ0 + gKA0 * (bt), gKQ1 + gKA1 * (bt))
#elif BODY_KS == 3
#define BODY_KINSERT BODY_KSWAP(0) BODY_KSWAP(1) BODY_KSWAP(2)
#define BODY_KMAX(bt) max(max(gKQ0 + gKA0 * (bt), gKQ1 + gKA1 * (bt)), gKQ2 + gKA2 * (bt))
#else
#define BODY_KINSERT BODY_KSWAP(0) BODY_KSWAP(1) BODY_KSWAP(2) BODY_KSWAP(3)
#define BODY_KMAX(bt) max(max(gKQ0 + gKA0 * (bt), gKQ1 + gKA1 * (bt)), max(gKQ2 + gKA2 * (bt), gKQ3 + gKA3 * (bt)))
#endif
#endif

// The one source of a body's jitter and size from its hash.
void BodyDraw(uint hb, float J, out float jx, out float jy, out float s)
{
#ifdef BODIES_CHEAP_HASH
    // V7: one extra hash per body (its halves the jitter), the size from the body hash's low byte (the alive draw reads the upper 24 bits) through a cubic
    // fit of the power law below (largest error 0.002).
    uint hj = CellHash(hb ^ 0xA511E9B3u);
    jx = ((hj & 0xFFFFu) * (2.0 / 65535.0) - 1.0) * J;
    jy = ((hj >> 16) * (2.0 / 65535.0) - 1.0) * J;
    float us = (hb & 0xFFu) * (1.0 / 255.0);
    s = ((0.21457 * us + 0.07435) * us + 0.41727) * us + 0.70586;
#else
    jx = (2.0 * CellUnit(CellHash(hb ^ 0xA511E9B3u)) - 1.0) * J;
    jy = (2.0 * CellUnit(CellHash(hb ^ 0x63D83595u)) - 1.0) * J;
    // The size within the class: dN/dl ~ l^-1.678 over one octave, 2^-0.5 .. 2^0.5 of the class's size unit.
    s = pow(1.2647 + CellUnit(CellHash(hb ^ 0x2C1B3C6Du)) * (0.7907 - 1.2647), -1.4749);
#endif
}

struct BodiesAcc
{
    float best, bestS, bestR, reach, other;   // cover pass: the least d / r, that body's size and radius, the least d - r, the least room to a body outside the search
    int live;                                 // cover pass: the live candidates
    float q, far;                             // body pass: the largest depth below the moved surface and below the shell's outer side
};

// One run over every class's searched cells. mode 0: the cover pass; mode 1: the body pass (h, typeHeightM, bt = e.x times the shared billow term, sw = e.x times its outward swing).
BodiesAcc BodiesRun(int mode, float2 uv, float pitch, float rm, float f, uint seed, float coreBase, float h, float typeHeightM, float bt, float sw)
{
    BodiesAcc a;
    a.best = 1e9; a.bestS = 1.0; a.bestR = 0.0; a.reach = 1e9; a.other = 1e9; a.q = -1e9; a.far = -1e9; a.live = 0;
#ifdef BODIES_KSLOTS
    if (mode == 2)
    {
        gKU0 = -1e9; gKU1 = -1e9; gKU2 = -1e9; gKU3 = -1e9;
        gKQ0 = -1e9; gKQ1 = -1e9; gKQ2 = -1e9; gKQ3 = -1e9;
        gKA0 = 0.0; gKA1 = 0.0; gKA2 = 0.0; gKA3 = 0.0;
        gKDrop = -1e9;
    }
#endif
    float g = BodyVisibleG(f);
    float gMax = BodyGTable[4];   // the radius at the largest share (the leaps' bound, P0c)
    float u0 = frac(uv.x);
    float lat = (0.5 - uv.y) * 3.14159265;
    float coslat = cos(lat);
    int n = 0;
    BODIES_CLASS_LOOP for (int k = 0; k < BODY_K; k++)
    {
        float pk = pitch * BodyPM[k];
        float unitM = pitch * BodyUM[k];
        int S = BODY_SMAX == 2 ? 2 : BodyS[k];   // a constant where every class is 2 x 2 (also under a [loop] over the classes)
        float J = BodyJ[k];
        float rCap = BodyR[k] * pk;
        int nb = BodyNB[k];
#ifdef BODIES_KSLOTS
        if (fmod(floor(_BodiesP.y * 0.015625), 2.0) >= 1.0) nb = 1;   // P0d debug bit 64: no second body in the largest class
#endif
        float alive = min(1.0, f / CELL_ALIVE_F0 * BodyA[k]);
        float u = frac(u0 + 0.3819660 * (k + 1));
        uint seedK = (seed * 0xC2B2AE35u) ^ ((uint)(k + 1) * 0x27D4EB2Fu);
        float rows = max(1.0, floor(3.14159265 * rm / pk));
        float rowH = 3.14159265 * rm / rows;
        float yRow = (lat + 1.5707963) * rm / rowH;
        // 2 x 2: the two nearest rows (cells), 3 x 3: the cell and its neighbours. A cell outside the search has its centre at least (S / 2 - J + the point's distance to the
        // search's nearer edge) pitches away, along the meridian and in each searched row.
        float sh = S == 2 ? 0.5 : 0.0;
        float ys = yRow - sh;
        float fs = ys - floor(ys);
        float j0 = floor(ys) - (S == 3 ? 1.0 : 0.0);
#ifdef BODIES_WIDE
        float wideM = 1.0;   // the wide search: one more cell on each side, so a cell outside it is one pitch further
        j0 -= 1.0;
        int rowsN = S + 2;
#else
        float wideM = 0.0;
#ifdef BODIES_BRUTE
        j0 -= 2.0;
        int rowsN = S + 4;
#else
        int rowsN = S;
#endif
#endif
        float outerM = (0.5 * S + wideM - J + min(fs, 1.0 - fs)) * rowH;
        BODIES_CELL_LOOP for (int dr = 0; dr < BODIES_ROWS_MAX; dr++)
        {
            if (dr >= rowsN) break;
            float j = j0 + dr;
            bool rowOk = j >= 0.0 && j < rows;
            float jc = clamp(j, 0.0, rows - 1.0);
            float cols = max(1.0, round(6.2831853 * rm * cos((jc + 0.5) * rowH / rm - 1.5707963) / pk));
            float xs = u * cols - sh;
            float fx = xs - floor(xs);
            float c0 = floor(xs) - (S == 3 ? 1.0 : 0.0);
            outerM = min(outerM, (0.5 * S + wideM - J + min(fx, 1.0 - fx)) * 6.2831853 * rm * coslat / cols);
#ifdef BODIES_BRUTE
            c0 -= 2.0;
#endif
#ifdef BODIES_WIDE
            c0 -= 1.0;
#endif
            BODIES_CELL_LOOP for (int dc = 0; dc < BODIES_ROWS_MAX; dc++)
            {
                if (dc >= rowsN) break;
                float c = c0 + dc;
                float cw = c - cols * floor(c / cols);
                uint h0 = CellHash((uint)jc * 0x9E3779B1u ^ (uint)cw * 0x85EBCA6Bu ^ seedK);
                [unroll] for (int b = 0; b < BODY_NBMAX; b++)
                {
                    if (b >= nb) break;
                    uint hb = b == 0 ? h0 : CellHash(h0 ^ 0x68E31DA4u);
                    bool live = rowOk && CellUnit(hb) < alive;
#ifdef BODIES_PRUNE
                    if (!live) continue;
#endif
                    float jx, jy, s;
                    BodyDraw(hb, J, jx, jy, s);
                    float dx = (u - (c + 0.5 + jx) / cols) * 6.2831853 * rm * coslat;
                    float dy = (lat - ((jc + 0.5 + jy) * rowH / rm - 1.5707963)) * rm;
                    float d = sqrt(dx * dx + dy * dy);
                    float r = min(g * s * unitM / CELL_EDGE_X, rCap);
                    float sAbs = s * BodyUM[k];   // the size factor in today's units (the height law of CellCover / CellBody)
                    float x = d / r;
                    if (mode == 0 || mode == 2)
                    {
#ifdef BODIES_KSLOTS
                        // P0c fix (the streaks of the P0b ground pictures): whether a body is alive and how large it is follow the share at the SAMPLE, and the share
                        // changes along the ray, so a body dead or small here can be alive and larger a few hundred m on, inside the leap. The leaps' bounds (the reach
                        // and the shell depth) are taken over every body of the search as large as it can get at any share: alive, radius at g(1), top 1 (the type's full
                        // height). The share itself stays exact (the slots hold the live bodies as they are here).
                        // P0d: with the local bound (the default where its map is bound) the largest form is the one at the largest share within the leap's reach
                        // (gBodFLoc, gBodCoreLoc from the max-filtered coverage map): alive with that share, its radius and its top. Debug bit 32: the global largest (P0c).
                        if (mode == 2 && rowOk && fmod(floor(_BodiesP.y * 0.0625), 2.0) < 1.0)
                        {
                            bool localB = gBodLocalOn;
                            bool aliveB = localB ? CellUnit(hb) < min(1.0, gBodFLoc / CELL_ALIVE_F0 * BodyA[k]) : true;
                            if (aliveB)
                            {
                                float rM = min((localB ? BodyVisibleG(gBodFLoc) : gMax) * s * unitM / CELL_EDGE_X, rCap);
                                float topM = localB ? clamp(gBodCoreLoc * sAbs / 0.5, 0.15, 1.0) : 1.0;
                                a.reach = min(a.reach, d - rM);
                                float axisM = min(max(CELL_EDGE_X * rM, 1.0), max(topM * typeHeightM, 1.0));
                                float xvM = d / rM / CELL_EDGE_X, hvM = h / topM;
                                a.far = max(a.far, (1.0 - sqrt(xvM * xvM + hvM * hvM)) * axisM + axisM * sw);
                            }
                        }
#endif
                        if (live)
                        {
                            if (x < a.best) { a.best = x; a.bestS = sAbs; a.bestR = r; }
#ifdef BODIES_KSLOTS
                            if (fmod(floor(_BodiesP.y * 0.0625), 2.0) >= 1.0) a.reach = min(a.reach, d - r);   // debug bit 16: the bounds at the sample's share (P0b)
#else
                            a.reach = min(a.reach, d - r);
#endif
                            a.live++;
#ifdef BODIES_KSLOTS
                            // The candidate's exact body term below the billows and its bound (its shell depth); kept if among the K largest bounds.
                            float topK = clamp(coreBase * sAbs / 0.5, 0.15, 1.0);
                            float axisK = min(max(CELL_EDGE_X * r, 1.0), max(topK * typeHeightM, 1.0));
                            float xvK = x / CELL_EDGE_X, hvK = h / topK;
                            float qbK = (1.0 - sqrt(xvK * xvK + hvK * hvK)) * axisK;
                            float uK = qbK + axisK * sw;
                            if (fmod(floor(_BodiesP.y * 0.0625), 2.0) >= 1.0) a.far = max(a.far, uK);
                            float tu = uK, tq = qbK, ta = axisK;
                            BODY_KINSERT
                            gKDrop = max(gKDrop, tu);
#endif
                        }
#ifdef BODIES_ARRAYS
                        gBodX[n] = live ? x / CELL_EDGE_X : 1e4;
                        gBodSemiR[n] = max(CELL_EDGE_X * r, 1.0);
                        gBodTop[n] = clamp(coreBase * sAbs / 0.5, 0.15, 1.0);
#endif
                    }
                    else if (live)
                    {
                        float top = clamp(coreBase * sAbs / 0.5, 0.15, 1.0);
                        float semiH = max(top * typeHeightM, 1.0), semiR = max(CELL_EDGE_X * r, 1.0);
                        float xv = x / CELL_EDGE_X, hv = h / top;
                        float axis = min(semiR, semiH);
                        float qb = (1.0 - sqrt(xv * xv + hv * hv)) * axis;
                        a.q = max(a.q, qb + axis * bt);
                        a.far = max(a.far, qb + axis * sw);
                    }
                    n++;
                }
            }
        }
        a.reach = min(a.reach, outerM - rCap);
        a.other = min(a.other, outerM - gCellBodyX * rCap);
    }
#ifdef BODIES_PRUNE
    if (mode == 0) gBodN = n;
#endif
#ifdef BODIES_NO_LEAP
    a.reach = 0.0;
    a.other = -1e9;
#endif
    return a;
}

// CellCover with the classes: the cover bound and the far view read the nearest disc (the least d / r over every candidate), the reach is the least over them and over each class's
// outside, gCellOther the least room to a body of a cell outside a class's search (the march's cap on the shell gap; every searched body is in the union, so its own shell counts).
float CellCoverBodies(float cov, float2 uv, float pitch, float rm, float coverage, float edge, float amount, float foot, uint seed, out float reachM, out float wMap)
{
#ifdef BODIES_FIXED_CANDIDATES
    // V11: today's cover pass, then the sixteen slots from its nearest disc (the first that disc as today's body reads it, the others beyond every reach).
    float covToday = CellCover(cov, uv, pitch, rm, coverage, edge, amount, foot, seed, reachM, wMap);
    float topToday = clamp(gCellCore / 0.5, 0.15, 1.0), semiRToday = max(CELL_EDGE_X * gCellR, 1.0);
    [loop] for (int nf = 0; nf < BODY_N; nf++)
    {
        gBodX[nf] = gCellX / CELL_EDGE_X + (nf == 0 ? 0.0 : 4.0 + 0.25 * nf);
        gBodSemiR[nf] = semiRToday * (1.0 + 0.01 * nf);
        gBodTop[nf] = topToday;
    }
    return covToday;
#else
    float f = CellShare(cov, coverage, edge, amount);
    if (_BodiesP.x > 0.0 && f > 0.0) f = _BodiesP.x;
    float coreBase = clamp(0.5 * (cov + coverage - 1.0) + 0.35, 0.15, 0.5);
    BodiesAcc a = BodiesRun(0, uv, pitch, rm, f, seed, coreBase, 0.0, 0.0, 0.0, 0.0);
#ifdef _BODIES_COUNT
    InterlockedAdd(_BodyCount[gBodCountLight != 0 ? 1 : 0], 1u);
    InterlockedAdd(_BodyCount[2], (uint)a.live);
#endif
    gBodUv = uv;
    gBodPitch = pitch;
    gBodRm = rm;
    gBodF = f;
    gBodSeed = seed;
    gBodCore = coreBase;
    gCellOther = a.other;
    reachM = max(a.reach, 0.0);
    float p = 1.0 - smoothstep(0.45, 1.0, a.best);
    float core = coreBase * a.bestS;
    float e = p > 0.5 ? (2.0 * p - 1.0) * core : p - 0.5;
    wMap = max(saturate((f - 0.8) / 0.15), foot > 0.0 ? saturate((foot - 0.25 * pitch) / (0.5 * pitch)) : 0.0);
    gCellX = a.best;
    gCellR = a.bestR;
    gCellCore = core;
    gCellW = wMap;
    return lerp(1.0 - coverage + e, cov, wMap);
#endif
}

#ifdef BODIES_KSLOTS
// The K-slot search (P0b-1): as CellCoverBodies, with the height share h and the type's height in m of the sample known, so that the search keeps the K best bodies.
float CellCoverBodiesK(int li, float cov, float2 uv, float pitch, float rm, float coverage, float edge, float amount, float foot, uint seed, float h, float typeHeightM,
                       float clearK, out float reachM, out float wMap)
{
    float4 ce = _CellE[li];
    float sw = ce.x * (0.5 + 0.5 * ce.z * (0.37 + 0.137));
    float f = CellShare(cov, coverage, edge, amount);
    if (_BodiesP.x > 0.0 && f > 0.0) f = _BodiesP.x;
    float coreBase = clamp(0.5 * (cov + coverage - 1.0) + 0.35, 0.15, 0.5);
    // P0d: the largest share within the leap's reach. The map's largest value over the texel's neighbourhood (its window holds every texel the bilinear read of any
    // point within _BodiesP.w m can touch) goes through the same steps as the sample's value (the clear-sky rule, CellShare, the core strength), each monotone in it.
    gBodLocalOn = _BodiesP.z > 0.5 && fmod(floor(_BodiesP.y * 0.03125), 2.0) < 1.0;
    [branch] if (gBodLocalOn)
    {
        uint mw, mh;
        _BodiesCovMax.GetDimensions(mw, mh);
        int tx = (int)floor(frac(uv.x) * mw) % (int)mw, ty = clamp((int)floor(saturate(uv.y) * mh), 0, (int)mh - 1);
        float cm = _BodiesCovMax.Load(int3(tx, ty, 0));
        cm = min(cm, 2.0 * cm - clearK);
        gBodFLoc = _BodiesP.x > 0.0 ? max(_BodiesP.x, f) : max(CellShare(cm, coverage, edge, amount), f);
        gBodCoreLoc = max(clamp(0.5 * (cm + coverage - 1.0) + 0.35, 0.15, 0.5), coreBase);
    }
    BodiesAcc a = BodiesRun(2, uv, pitch, rm, f, seed, coreBase, h, typeHeightM, 0.0, sw);
#ifdef _BODIES_COUNT
    InterlockedAdd(_BodyCount[gBodCountLight != 0 ? 1 : 0], 1u);
    InterlockedAdd(_BodyCount[2], (uint)a.live);
#endif
    gBodUv = uv;
    gBodPitch = pitch;
    gBodRm = rm;
    gBodF = f;
    gBodSeed = seed;
    gBodCore = coreBase;
    gKFar = a.far;
    gCellOther = a.other;
    reachM = max(a.reach, 0.0);
    // P0d: no leap reaches beyond the window the local maximum was taken over (the shell leap is capped by gCellOther, the reach leap by reachM).
    if (gBodLocalOn)
    {
        gCellOther = min(gCellOther, _BodiesP.w);
        reachM = min(reachM, _BodiesP.w);
    }
    // P0c diagnosis (cloud.bodies.debug, _BodiesP.y): bit 1 no leap over the reach (the empty discs), bit 2 no shell leap (the bodies' shells), bit 4 the body is today's
    // single nearest disc (CellBody) instead of the union.
    float dbgK = _BodiesP.y;
    if (fmod(dbgK, 2.0) >= 1.0) reachM = 0.0;
    if (fmod(floor(dbgK * 0.5), 2.0) >= 1.0) gCellOther = -1e9;     // bit 2: no room to bodies outside the search (the shell leap stops)
    if (fmod(floor(dbgK * 0.125), 2.0) >= 1.0) gKFar = 1e9;         // bit 8: no shell depth of the searched bodies (the shell leap stops)
    float p = 1.0 - smoothstep(0.45, 1.0, a.best);
    float core = coreBase * a.bestS;
    float e = p > 0.5 ? (2.0 * p - 1.0) * core : p - 0.5;
    wMap = max(saturate((f - 0.8) / 0.15), foot > 0.0 ? saturate((foot - 0.25 * pitch) / (0.5 * pitch)) : 0.0);
    gCellX = a.best;
    gCellR = a.bestR;
    gCellCore = core;
    gCellW = wMap;
    return lerp(1.0 - coverage + e, cov, wMap);
}
#endif

// CellBody with the classes: the share from the largest depth over every candidate's FULL body term (each its own axis on the shared billow value), the shell gap from the largest
// depth below the shells' outer sides (the least gap over the candidates).
float CellBodyBodies(int li, float h, float billow, float2 fine, float typeHeightM)
{
#ifdef BODIES_NO_UNION
    return CellBody(li, h, billow, fine, typeHeightM);
#endif
    float4 e = _CellE[li];
    float band = CellBand(li);
    [branch] if (CellFootOn(li) && gNoiseFoot > 0.0)
    {
        float cell = 0.5 * _NoiseR.z * (_LayerD[li].y > 0 ? 1.0 / _LayerD[li].y : 250.0);
        float full = _NoiseS.y, off = max(_NoiseS.z, full + 1e-3);
        fine.x *= 1.0 - saturate((gNoiseFoot - full * 0.37 * cell) / ((off - full) * 0.37 * cell));
        fine.y *= 1.0 - saturate((gNoiseFoot - full * 0.137 * cell) / ((off - full) * 0.137 * cell));
    }
    float bt = e.x * (billow - 0.5 + e.z * (0.37 * fine.x + 0.137 * fine.y));
    float sw = e.x * (0.5 + 0.5 * e.z * (0.37 + 0.137));
#if defined(BODIES_KSLOTS)
    [branch] if (fmod(floor(_BodiesP.y * 0.25), 2.0) >= 1.0) return CellBody(li, h, billow, fine, typeHeightM);   // P0c diagnosis bit 4
    float q = BODY_KMAX(bt);
    float far = gKFar;
#ifdef _BODIES_COUNT
    // [6]: a candidate not kept bounds the share above what the kept ones give and above the band's lower edge (it could have changed the share). Its bound is its exact shell
    // depth (qb + axis x swing, at least its term for any billow value), so 0 here means the K slots give V7's share exactly. ([7], a second full search, did not compile
    // inside the light march's loop; it is not needed while [6] is 0.)
    InterlockedAdd(_BodyCount[3], 1u);
    InterlockedAdd(_BodyCount[6], gKDrop > max(q, -0.5 * band) ? 1u : 0u);
#endif
#elif defined(BODIES_ARRAYS)
    float q = -1e9, far = -1e9;
#ifdef _BODIES_COUNT
    uint countReach = 0u;
#endif
    [loop] for (int n = 0; n < gBodN; n++)
    {
        float top = gBodTop[n];
        float semiH = max(top * typeHeightM, 1.0), semiR = gBodSemiR[n];
        float xv = gBodX[n], hv = h / top;
        float axis = min(semiR, semiH);
        // The depth is at most (1 - xv) axis, its moved surface at most sw axis above it: below -band / 2 the body adds no share (V12 skips it before the root and keeps
        // that bound as its shell depth, which can only shorten a leap).
        float qUp = (1.0 - xv + sw) * axis;
#ifdef _BODIES_COUNT
        countReach += qUp > -0.5 * band ? 1u : 0u;
#endif
#ifdef BODIES_PRUNE
        [branch] if (qUp <= -0.5 * band)
        {
            far = max(far, qUp);
            continue;
        }
#endif
        float qb = (1.0 - sqrt(xv * xv + hv * hv)) * axis;
        q = max(q, qb + axis * bt);
        far = max(far, qb + axis * sw);
    }
#ifdef _BODIES_COUNT
    InterlockedAdd(_BodyCount[3], 1u);
    InterlockedAdd(_BodyCount[4], (uint)gBodN);
    InterlockedAdd(_BodyCount[5], countReach);
#endif
#else
    BodiesAcc a = BodiesRun(1, gBodUv, gBodPitch, gBodRm, gBodF, gBodSeed, gBodCore, h, typeHeightM, bt, sw);
    float q = a.q, far = a.far;
#endif
    gBodyFar = far;
    gBodyRead = true;
    return saturate(q / band + 0.5);
}
#endif

#endif
