// THE CLOUDS' FORMS (owner orders CELLS 2026-11-17 and CLOUD FIELD 2026-11-19): the clouds of a type with a form, placed by the layer map's cloud share and the cloud field.
//
// The layer map of a body is coarse (Kerbin: 512 x 256, a texel of 7.4 km), a fair-weather cumulus is 0.5 to 5 km wide: the map cannot place the clouds, read as it is it draws its smoothed
// texels as the clouds. The map is kept for what it can say, how much of a region is cloud (the share), and the cloud field (FieldCloud below, CloudField.cs) puts that share down as clouds.
//
// Each cloud is a body, drawn as Schneider draws his clouds in Nubis (SIGGRAPH 2022 and 2023: a body with a sharp surface, its share rising within a thin band inside it,
// the bulges moving that surface): a half ellipsoid over the type's flat base whose depth in metres the bulge noise moves in three sizes, the heads on the heads in the same proportion.
//
// The code is the project's own. (The first placement, a jittered lattice of discs, was a test tool since the field and went with master plan 47.U stage 1.)
#ifndef REDEFINITION_CLOUD_CELLS
#define REDEFINITION_CLOUD_CELLS

// The body of a cloud per layer (CloudLayerDef's cell keys, the viewer's overrides through CloudLookTuning): x how far the bulges move the body's surface, as a share of its shorter
// semi-axis (cfg cellBulge); y the width in m of the band the share rises over at the surface (cellBand); z the gain of the finer bulge octaves (cellFine; 0: not read); w unused
// (until master plan 47.U stage 1 the test lattice's march flags).
float4 _CellE[4];

// The width in m of the band the share rises over at the surface: the layer's.
float CellBand(int li)
{
    return max(_CellE[li].y, 1.0);
}

// The body's numbers are carried in the units the first placement (a lattice of discs, removed in master plan 47.U stage 1) had: a body's horizontal coordinate times CELL_EDGE_X and its
// horizontal semi-axis over CELL_EDGE_X (gCellX, gCellR). The C# twin is CloudCells.cs (the same number).
#define CELL_EDGE_X 0.725

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

// What the field's read (FieldCloud) leaves for the body (CellBody; gCellX below 0: no body read): the point's horizontal coordinate in the body times CELL_EDGE_X, the cloud's core
// strength, the share of the map's own value there (0: the body draws; 1: the map does) and the body's horizontal semi-axis over CELL_EDGE_X, in m.
static float gCellX = -1.0;
static float gCellCore = 0.0;
static float gCellW = 1.0;
static float gCellR = 0.0;

// The finer bulge octaves of a cloud's body: the cumuliform field (the shape volume's green channel, preset bulge) read at 0.37 and 0.137 of its period at the noise lattice's true vertical scale
// (as the cumuliform lookup, so the heads are round), each centred on 0. The reads are straight (no blend), offset from each other and from the first octave. Needs the noise include's lookup.
float2 CellFineBulges(int li, float3 pc)
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

// The body of a cloud at a point: its share, 0 outside and 1 inside, rising over the band at its surface. The body is a half ellipsoid over the type's flat base: horizontal semi-axis the
// footprint's half width the field hands on (CELL_EDGE_X times gCellR, see CELL_EDGE_X above), vertical the cloud's own height (a share of the type's height typeHeightM from the core
// strength). Its depth below the surface in m, approximated as (1 - the ellipsoid's radial coordinate) times the shorter semi-axis, is moved by the bulges: the first octave (bulge: the
// layer's blended noise rank, 0..1) by up to _CellE.x of that semi-axis, the finer ones (fine, centred on 0) in proportion to their size, so the heads on the heads are smaller in the same
// ratio (the cumulus's fractal surface). h: the height share of the type at the point.
float CellBody(int li, float h, float bulge, float2 fine, float typeHeightM)
{
    float4 e = _CellE[li];
    float top = clamp(gCellCore / 0.5, 0.15, 1.0);
    float semiH = max(top * typeHeightM, 1.0), semiR = max(CELL_EDGE_X * gCellR, 1.0);
    float xv = gCellX / CELL_EDGE_X, hv = h / top;
    float axis = min(semiR, semiH);
    float qBase = (1.0 - sqrt(xv * xv + hv * hv)) * axis;
    float band = CellBand(li);
    float q = qBase + e.x * axis * (bulge - 0.5 + e.z * (0.37 * fine.x + 0.137 * fine.y));
    // The core profile (cloud.cellCoreDepth / .cellCoreFloor, a share rising from the surface into the body) was removed on 2026-11-25: no visible effect in the game (PRUEFPROTOKOLL 82).
    return saturate(q / band + 0.5);
}

// CLOUD FIELD (CloudField.cs, CloudFieldGpu.cs; globals for the layer pass and the cloud shadow alike): _CloudField r the rank-uniform multi-scale field u, g its gradient (rank per km), b its
// coarse rank; _FieldP x on, yz the streets' direction, w the smallest feature in m; _FieldC the puff classes (x aspect, y top gain, z scale, w street stretch).
sampler2D _CloudField;
float4 _FieldP;
float4 _FieldC[8];

// The field's far view (master plan 46.4, PRUEFPROTOKOLL 340, 341, 344, 357), set by FieldCloud for the caller: gFieldFar the weight of the far mean (0: the clouds alone),
// gFieldShare the share of the pixel's footprint inside the clouds (the field's filtered picture, FieldCloud), gFieldTop the body's top there as a share of the type's height,
// gFieldRC that body's horizontal semi-axis in m (both from the coarse channel at the footprint's mip level).
static float gFieldFar, gFieldShare, gFieldTop, gFieldRC;
// The pixel's footprint, as a share of the field's smallest feature, from which the far mean begins (the feature spans 8 pixels) and at which it has taken over (4 pixels).
#define FIELD_FAR_FROM 0.125
#define FIELD_FAR_TO 0.25
// The field's spread inside a texel of each mip level (CloudFieldGpu, CloudField.MipSpread): the half width of the field's values about the texel's mean.
#define FIELD_SPREAD_LEVELS 12
float _FieldV[FIELD_SPREAD_LEVELS];

// A puffy type's cloud from the field, the one code of the layer pass and the cloud shadow (its twin on the CPU, number for number: CloudField.Cloud in CloudField.cs; a change here is a change there,
// judged by the weather player's scene cloud-shadow -shadownoise 3). The cloud is where the field u(x) is below the map's local share f ({u < f} has the map's cover
// whatever the field); its body is CellBody's with the distance to the footprint's edge in m, (f - u) over the gradient of u: a dome of the
// class's aspect over the type's flat base, its top lifted in the clusters' cores (the coarse channel). Where the share is above 0.8 (the closed cloud layer) the map's own value is drawn.
// Returns -1 there (the caller draws the map; cov is then the map's value with the field's footprint blended in by the weight, so there is no edge
// where it begins), 0 outside the body's reach (the density is 0), 1 inside it with the cell read's globals (gCellX, gCellR, gCellCore, gCellW) set for CellBody. uv: the layer map's
// coordinate (not clamped); rm: the layer's mid radius in m; h: the type's height share at the point; typeHeightM: the type's height in m. The field is read at the mip level of the march's
// spacing (foot): without it the field's 50 to 200 m texels alias into a speckle at distance.
// Where the pixel's footprint (foot, 0: none) nears the field's smallest feature the clouds lie below the pixel: the caller blends by gFieldFar to the clumpy medium of
// ShapeShare with gFieldShare, the share of the footprint inside the clouds (the field's filtered picture, the middle stage below; the map's share where the footprint spans the whole field). Until 2026-11-26 the map's own value was handed to the threshold there, which drew the map's 7.4 km texels as the clouds (dots and blocks from orbit).
int FieldCloud(int li, int puff, float2 uv, inout float cov, float coverage, float edge, float amount, float foot, float rm, float h, float typeHeightM)
{
    float4 fc = _FieldC[puff];
    float fShare = CellShare(cov, coverage, edge, amount);
    float wF = saturate((fShare - 0.8) / 0.15);
    // The far mean takes over while the field's smallest feature (_FieldP.w) shrinks from 8 pixels to 4 (PRUEFPROTOKOLL 355, 356): a cloud of fewer pixels cannot be drawn
    // as a shape by one sample a pixel; drawn so (until 2026-11-26 down to 1.3 pixels) the fields were a speckle from orbit (the owner: too noisy from above).
    gFieldFar = (foot > 0.0 ? saturate((foot - FIELD_FAR_FROM * _FieldP.w) / ((FIELD_FAR_TO - FIELD_FAR_FROM) * _FieldP.w)) : 0.0) * (1.0 - wF);
    float lat = (0.5 - uv.y) * 3.14159265;
    float xM = uv.x * 2.0 * 3.14159265 * rm * max(cos(lat), 0.02), yM = lat * rm;
    float along = (xM * _FieldP.y + yM * _FieldP.z) / fc.w, across = -xM * _FieldP.z + yM * _FieldP.y;
    float2 pF = float2(along * _FieldP.y - across * _FieldP.z, along * _FieldP.z + across * _FieldP.y) * (fc.z / 102400.0);
    // A texel is CloudField.TileM / CloudField.Size / z = 100 / z m.
    float lod = foot > 0.0 ? max(log2(foot * fc.z / 100.0), 0.0) : 0.0;
    float3 fv = tex2Dlod(_CloudField, float4(pF, 0, lod)).xyz;
    float dm = (fShare - fv.x) / max(fv.y * fc.z * 0.001, 1e-6);
    float coreF = clamp(0.5 * (cov + coverage - 1.0) + 0.35, 0.15, 0.5) * (1.0 + fc.y * (1.0 - 2.0 * fv.z));
    float topF = clamp(coreF / 0.5, 0.15, 1.0);
    float semiH = max(topF * typeHeightM, 1.0), rC = fc.x * semiH;
    // THE FAR VIEW'S MIDDLE STAGE (PRUEFPROTOKOLL 357): the share of the pixel's footprint that lies inside the clouds, {u < f}, is the field's own filtered picture.
    // fv.x is the field's mean over the footprint (the mip level of the footprint); inside it the field is spread about that mean by _FieldV at the level (taken as even
    // over mean +- spread; CloudField.MipSpread, from the field's own variance by level). Spread 0 (the finest level): the step u < f, the clouds' footprint itself;
    // the coarsest levels (mean 0.5, spread 0.5): f, the map's share. Between, the field's clusters and streets stay as a soft cover while they are wider than a pixel, and
    // on the approach the clouds sharpen out of their own blurred picture (the owner: nothing may pop). The body's top and width follow the coarse channel at the
    // same level. Until 2026-11-26 the far share was the map's alone: an even veil with the map's shapes as its borders.
    // Worked out only where the far mean has begun: a near sample (every accepted view) does none of it (PRUEFPROTOKOLL 359).
    gFieldShare = fShare;
    gFieldTop = topF;
    gFieldRC = rC;
    [branch] if (gFieldFar > 0.0)
    {
        float lodV = min(lod, FIELD_SPREAD_LEVELS - 1.001);
        int level = (int)floor(lodV);
        float spread = lerp(_FieldV[level], _FieldV[level + 1], lodV - level);
        gFieldShare = spread > 1e-4 ? saturate((fShare - fv.x) / (2.0 * spread) + 0.5) : step(fv.x, fShare);
    }
    if (wF > 0.0)
    {
        // The footprint as the profile the threshold takes: above the threshold by up to the core strength inside, below it by up to half a unit outside.
        float e = dm >= 0.0 ? min(dm / rC, 1.0) * coreF : max(dm / rC, -1.0) * 0.5;
        cov = lerp(1.0 - coverage + e, cov, wF);
        return -1;
    }
    float xv = 1.0 - clamp(dm / rC, -2.0, 1.0), hv = h / topF;
    float axis = min(rC, semiH);
    float swing = _CellE[li].x * axis * (0.5 + 0.5 * _CellE[li].z * (0.37 + 0.137));
    if ((1.0 - sqrt(xv * xv + hv * hv)) * axis + swing + 0.5 * CellBand(li) < 0.0) return 0;
    gCellX = CELL_EDGE_X * xv;
    gCellR = rC / CELL_EDGE_X;
    gCellCore = coreF;
    gCellW = 0.0;
    return 1;
}

#endif
