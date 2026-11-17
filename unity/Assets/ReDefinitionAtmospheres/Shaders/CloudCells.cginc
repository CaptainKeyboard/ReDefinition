// CELLS (owner order 2026-11-17): single clouds from the layer map's cloud share.
//
// The layer map of a body is coarse (Kerbin: 512 x 256, a texel of 7.4 km), a fair-weather cumulus is 0.5 to 5 km wide: the map cannot place single clouds, read as it is it draws its smoothed
// texels as the clouds. The cells keep the map for what it can say, how much of a region is cloud (the share), and put that share down as single clouds: a jittered lattice of the layer's pitch on
// the sphere (rows of constant height along the meridian, each row with as many columns as its circumference holds, so there is no seam and no stretching), each cell alive with a probability and a
// disc whose radius follows the share (CellVisibleRadius: a table from a Monte Carlo of this very lattice, so the discs cover the share). A disc's profile is the single-cloud bench's
// (tools/weather_metrics/e40single.py disc_map: 1 in the inner 0.45 of the radius, a smoothstep to 0 at the edge), so the layer pass, its type lift and its noise build on each cell the cloud
// the bench built on its one disc.
//
// The code is the project's own. A jittered lattice for cell placement is the common Worley construction (S. Worley, "A Cellular Texture Basis Function", SIGGRAPH 1996).
#ifndef REDEFINITION_CLOUD_CELLS
#define REDEFINITION_CLOUD_CELLS

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
// the share f and the layer's seed. size: the size factor of that disc's cell (1 where there is none). reachM: a distance in m over which no disc begins (0 inside one; the march leaps over it,
// as over the map's empty reach).
float CellProfile(float2 uv, float pitch, float rm, float f, uint seed, out float size, out float reachM)
{
    reachM = 0.0;
    size = 1.0;
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
            if (x < best) { best = x; size = s; }
            reach = min(reach, d - r);
        }
    }
    reachM = max(min(reach, outerM - rCap), 0.0);
    return 1.0 - smoothstep(0.45, 1.0, best);
}

// The layer map's value at a point with the cells. The disc's profile p is placed on the layer's threshold as the bench's disc map was on its own (p 0.5 at the threshold, 0 half a unit below it),
// and inside the disc it rises to the cloud's core strength: the map's excess over the threshold (how much the region convects: a fair sky's clouds stay low, a stormy region's grow) times the
// cell's size factor (the wide clouds are the tall ones; a field holds many low clouds and a few towers). wMap: the share of the map's own value (a closed deck above a share of 0.8, where the
// discs merge; and the far view, where the march's spacing passes a quarter of the pitch: the cells would alias, the map's value has their mean).
float CellCover(float cov, float2 uv, float pitch, float rm, float coverage, float edge, float amount, float foot, uint seed, out float reachM, out float wMap)
{
    float f = CellShare(cov, coverage, edge, amount);
    float size;
    float p = CellProfile(uv, pitch, rm, f, seed, size, reachM);
    float core = clamp(0.5 * (cov + coverage - 1.0) + 0.35, 0.15, 0.5) * size;
    float e = p > 0.5 ? (2.0 * p - 1.0) * core : p - 0.5;
    wMap = max(saturate((f - 0.8) / 0.15), foot > 0.0 ? saturate((foot - 0.25 * pitch) / (0.5 * pitch)) : 0.0);
    return lerp(1.0 - coverage + e, cov, wMap);
}

#endif
