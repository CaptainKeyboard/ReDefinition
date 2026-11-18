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
// the discs merge; and the far view, where the march's spacing passes a quarter of the pitch: the cells would alias, the map's value has their mean).
float CellCover(float cov, float2 uv, float pitch, float rm, float coverage, float edge, float amount, float foot, uint seed, out float reachM, out float wMap)
{
    float f = CellShare(cov, coverage, edge, amount);
    float size, xBest, rBest;
    float p = CellProfile(uv, pitch, rm, f, seed, size, reachM, xBest, rBest);
    float core = clamp(0.5 * (cov + coverage - 1.0) + 0.35, 0.15, 0.5) * size;
    float e = p > 0.5 ? (2.0 * p - 1.0) * core : p - 0.5;
    wMap = max(saturate((f - 0.8) / 0.15), foot > 0.0 ? saturate((foot - 0.25 * pitch) / (0.5 * pitch)) : 0.0);
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

#endif
