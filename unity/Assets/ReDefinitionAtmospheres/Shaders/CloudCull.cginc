// The visibility culling and the distance LOD of the own cloud layers (package T16). Included twice by ReDefinitionAtmospheresLayers.shader: before the
// march (the uniforms and the small functions the march calls) and, with CLOUDCULL_TILE defined, after SceneDistance (pass 5, the tile pass). The CPU twin in
// double precision is src/Atmospheres/Render/Layers/Cull/CullMath.cs; the unit tests hold the two to each other.
//
// The tile pass writes one texel per tile of 16 x 16 pixels of the full target: the layers the tile has to march. A layer is dropped for a tile when
//   - no ray of the tile's bundle can reach its shell (cone against sphere, or none of the corner rays and the centre ray hits),
//   - the scene ends before the layer starts (the nearest hit of the bundle is behind the farthest scene distance of the tile),
//   - the dilated maximum of its coverage map along the bundle is too low for the shape to reach the threshold (the march's own coverage bound, applied to
//     the maximum of the cells the bundle's footprint touches), or
//   - it lies above a deck that is certainly closed for the whole tile as seen from below (the dilated minimum makes the shape one, and the deck's least
//     vertical optical depth is above ClosedTau).
// The march reads the tile of its pixel and skips the dropped layers. The decisions are conservative: a bundle is a cone of the corner rays' angle, the
// footprint grows with the distance, and a cell of the bound map is used only up to 85 percent of its size.
//
// The distance LOD: a span whose layer is below a weight after the air is cut with a smooth fade (the layer is thinned by the fade, then not marched);
// layers beyond the nearest few thin out from a distance; the step grows along the span; the sun march is repeated less often with distance; the noise
// detail's footprint is the pixel's size at the distance.
//
// Techniques: hierarchical empty-space skipping and conservative bounds in volume rendering (a coarse maximum pyramid of the support tested along the ray with
// a footprint-wide filter).

#ifdef CLOUDCULL_TILE

// Can any ray within the angle rho of d reach a sphere of radius r about the planet's centre (o relative to it)?
bool CullConeMayHit(float3 o, float3 d, float rho, float r)
{
    float len = length(o);
    if (len <= r) return true;
    float alpha = acos(clamp(dot(d, -o / len), -1.0, 1.0));
    float beta = asin(min(r / len, 1.0));
    return alpha - rho <= beta + 1e-4;
}

// The dilated maximum (isMax) or minimum of the layer's coverage map over a footprint of the given radius (m) around p; negative where the footprint is
// wider than a coarse cell and no bound can be given.
float CullBound(int li, float3 p, float radiusM, bool isMax)
{
    float len = max(length(p), 1.0);
    float3 n = normalize(mul((float3x3)_WorldToMap, p));
    float lat = asin(clamp(n.z, -1.0, 1.0));
    float a = radiusM / len;
    float extent = max(a / max(cos(lat), 0.02), a);
    float2 uv = MapUv(p, _LayerD[li].w);
    float vMin = 0.5 / _MapDim.y;
    uv.y = clamp(uv.y, vMin, 1.0 - vMin);
    float3 uvw = float3(uv, _LayerC[li].z);
    if (extent <= 0.85 * 6.2831853 / 128.0)
        return isMax ? UNITY_SAMPLE_TEX2DARRAY_LOD(_CullMax0, uvw, 0).r : UNITY_SAMPLE_TEX2DARRAY_LOD(_CullMin0, uvw, 0).r;
    if (extent <= 0.85 * 6.2831853 / 32.0)
        return isMax ? UNITY_SAMPLE_TEX2DARRAY_LOD(_CullMax1, uvw, 0).r : UNITY_SAMPLE_TEX2DARRAY_LOD(_CullMin1, uvw, 0).r;
    return -1.0;
}

bool CullTest(int bit)
{
    return fmod(floor(_CullA.w / exp2((float)bit)), 2.0) > 0.5;
}

float4 CullTilePass(v2f i)
{
    float2 full = _CullT.zw;
    float2 tile = floor(i.uv * _CullT.xy);
    float2 uv0 = tile * _CullA.z / full;
    float2 uv1 = min((tile + 1.0) * _CullA.z / full, 1.0);
    float2 c = 0.5 * (uv0 + uv1);
    float2 h = 0.5 * (uv1 - uv0) + 1.5 / full;
    // The parity test cell replaces the density: nothing is dropped for it.
    if (_CellA.w > 0) return float4(15.0, 0.0, 0.0, 1.0);
    float3 o = _CamPos.xyz - _PlanetCenter.xyz;
    float3 dc = RayDir(c);
    // The five rays of the bundle: the centre and the four corners (index 0 is the centre).
    float3 rays[5];
    rays[0] = dc;
    rays[1] = RayDir(c + float2(-h.x, -h.y));
    rays[2] = RayDir(c + float2(h.x, -h.y));
    rays[3] = RayDir(c + float2(-h.x, h.y));
    rays[4] = RayDir(c + float2(h.x, h.y));
    float rho = 0.0;
    [unroll] for (int k = 1; k < 5; k++) rho = max(rho, acos(clamp(dot(dc, rays[k]), -1.0, 1.0)));
    // The scene: the farthest and the nearest end of a ray in the tile (8 x 8 taps, two pixels apart).
    float tMaxTile = 0.0, tMinTile = 1e30;
    [loop] for (int ty = 0; ty < 8; ty++)
    {
        [loop] for (int tx = 0; tx < 8; tx++)
        {
            float2 uvT = lerp(uv0, uv1, (float2(tx, ty) + 0.5) / 8.0);
            float td = SceneDistance(uvT, RayDir(uvT));
            tMaxTile = max(tMaxTile, td);
            tMinTile = min(tMinTile, td);
        }
    }
    float bits = 0.0, closedOuter = 1e30;
    float fShell = 0.0, fTerrain = 0.0, fMaps = 0.0, fClosed = 0.0;   // the reasons, one flag each (several layers may share a reason)
    float rangeA[4], rangeB[4];
    [unroll] for (int li = 0; li < MAXL; li++)
    {
        rangeA[li] = 0.0;
        rangeB[li] = 0.0;
        if (li >= (int)_LayerCount) break;
        if (_LimitP.z > 0 && fmod(floor(_LimitP.z / exp2((float)li)), 2.0) < 0.5) continue;
        bool must = true;
        float4 sp;
        float tIn = 1e30, tOut = 0.0;
        int hits = 0;
        [unroll] for (int r = 0; r < 5; r++)
        {
            LayerSpans(li, o, rays[r], 1e30, sp);
            bool any = sp.y > sp.x || sp.w > sp.z;
            if (any)
            {
                hits++;
                tIn = min(tIn, sp.y > sp.x ? sp.x : sp.z);
                tOut = max(tOut, sp.w > sp.z ? sp.w : sp.y);
            }
        }
        float tA = 0.0, tB = 0.0;
        if (hits == 0)
        {
            // Nothing of the five rays reaches the layer: provably nothing of the cone does when the cone misses the shell's outer sphere.
            if (CullTest(0) && !CullConeMayHit(o, dc, rho, _PlanetR + _LayerA[li].w))
            {
                must = false;
                fShell = 1.0;
            }
        }
        else if (hits == 5)
        {
            float m = 0.1 * (tOut - tIn) + 4.0 * rho * tOut;
            tA = max(tIn - m, 0.0);
            tB = tOut + m;
            if (CullTest(1) && tA >= tMaxTile)
            {
                must = false;
                fTerrain = 1.0;
            }
            else
            {
                tB = min(tB, max(tMaxTile, tA));
                rangeA[li] = tA;
                rangeB[li] = tB;
                float slice = _LayerC[li].z;
                float coverage = _LayerB[li].y, edge = max(_LayerB[li].z, 1e-4);
                float noiseBound = 0.5 * _LayerD[li].x;
                float slack = 2.0 / 255.0;
                if (CullTest(2) && slice >= 0 && tB > tA)
                {
                    bool empty = true;
                    float dtSample = (tB - tA) / 12.0;
                    [loop] for (int j = 0; j < 12; j++)
                    {
                        float t = tA + dtSample * (j + 0.5);
                        float v = CullBound(li, o + dc * t, rho * t + 0.5 * dtSample, true);
                        if (v < 0.0 || v + coverage - 1.0 + noiseBound + 0.5 * edge + slack > 0.0) { empty = false; break; }
                    }
                    if (empty)
                    {
                        must = false;
                        fMaps = 1.0;
                    }
                }
            }
        }
        if (must) bits += exp2((float)li);
    }
    // Closure: a deck above the camera whose shape is one over the whole bundle and whose column is thick enough closes the tile; the layers above it are dropped.
    if (_CullA.y > 0.5)
    {
        [unroll] for (int ci = 0; ci < MAXL; ci++)
        {
            if (ci >= (int)_LayerCount) break;
            bool on = fmod(floor(bits / exp2((float)ci)), 2.0) > 0.5;
            float slice = _LayerC[ci].z;
            if (!on || slice < 0 || _CullL[ci].x < 6.0 || _LayerC[ci].y > _LayerC[ci].x || _LayerA[ci].z <= 0) continue;
            if (length(o) >= _PlanetR + _LayerA[ci].z) continue;
            float tA = rangeA[ci], tB = rangeB[ci];
            if (tB <= tA || tMinTile <= tB) continue;
            float coverage = _LayerB[ci].y, edge = max(_LayerB[ci].z, 1e-4);
            float noiseBound = 0.5 * _LayerD[ci].x;
            bool solid = true;
            float dtSample = (tB - tA) / 12.0;
            [loop] for (int j = 0; j < 12; j++)
            {
                float t = tA + dtSample * (j + 0.5);
                float v = CullBound(ci, o + dc * t, rho * t + 0.5 * dtSample, false);
                if (v < 0.0 || v + coverage - 1.0 - noiseBound - 0.5 * edge - 2.0 / 255.0 < 0.0) { solid = false; break; }
            }
            if (solid) closedOuter = min(closedOuter, _LayerA[ci].w);
        }
        if (closedOuter < 1e29)
        {
            [unroll] for (int di = 0; di < MAXL; di++)
            {
                if (di >= (int)_LayerCount) break;
                bool on = fmod(floor(bits / exp2((float)di)), 2.0) > 0.5;
                if (on && _LayerA[di].z >= closedOuter && length(o) < _PlanetR + _LayerA[di].z)
                {
                    bits -= exp2((float)di);
                    fClosed = 1.0;
                }
            }
        }
    }
    float aid = fMaps + 2.0 * fTerrain + 4.0 * fShell + 8.0 * fClosed;
    return float4(bits, aid, min(tMaxTile, 60000.0), 1.0);
}

#else

float4 _CullA;   // x the tile pass is on (skip of empty), y closure on, z the tile size in pixels, w the tests of the skip (bit 0 shells, 1 terrain, 2 maps)
float4 _CullB;   // x distance cut on, y the weight below which a layer is cut, z the layer cap distance in m (0: off), w the spans kept at any distance
float4 _CullC;   // x the step growth per m along a span (0: off), y the tap LOD's start (m), z its end (m), w its factor at the end (1: off)
float4 _CullD;   // x the footprint detail LOD on, y the pixel's angular size times its scale, z, w unused
float4 _CullT;   // x, y the tile map's size in tiles, z, w the full target's size in pixels
float4 _CullL[4];   // x the least vertical optical depth of the layer's whole height (0: none provable)
sampler2D _CullTiles;
UNITY_DECLARE_TEX2DARRAY(_CullMax0);
UNITY_DECLARE_TEX2DARRAY(_CullMax1);
UNITY_DECLARE_TEX2DARRAY(_CullMin0);
UNITY_DECLARE_TEX2DARRAY(_CullMin1);

// The layers the pixel's tile marches (bit i = layer i); all of them where the tile pass did not run.
static float gCullBits = 15.0;
static float gCullAid = 0.0;

bool CullLayerOn(int li)
{
    return fmod(floor(gCullBits / exp2((float)li)), 2.0) > 0.5;
}

float CullSmooth(float x)
{
    x = saturate(x);
    return x * x * (3.0 - 2.0 * x);
}

// Reads the tile of a pixel (uv of the full target).
void CullReadTile(float2 uv)
{
    gCullBits = 15.0;
    gCullAid = 0.0;
    if (_CullA.x > 0.5 || _CullA.y > 0.5)
    {
        float2 tuv = (floor(uv * _CullT.zw / _CullA.z) + 0.5) / _CullT.xy;
        float4 tile = tex2Dlod(_CullTiles, float4(tuv, 0, 0));
        gCullBits = tile.r;
        gCullAid = tile.g;
    }
}

// The visibility of a span that starts at distance t: 1 near, 0 where the air leaves less than the cut weight of the layer, in between a smooth step.
float CullDistanceFade(float3 o, float3 d, float t)
{
    if (_CullB.x < 0.5 || _FarA.x < 0.5) return 1.0;
    // One piece of the air's optical depth with the altitude linear along the ray (FarAirTau cuts it into six, and a second copy of that loop is more than the
    // march's shader program can take): a measure of the fade, not the colour; the clearest channel decides.
    float h0 = length(o) - _PlanetR, h1 = length(o + d * t) - _PlanetR;
    float clear = min(_FarB.x, min(_FarB.y, _FarB.z));
    float tau = (clear * FarChord(h0, h1, t, _FarA.z) + _FarB.w * FarChord(h0, h1, t, _FarA.w)) * _FarA.y;
    float w = exp(-tau);
    return CullSmooth((w - _CullB.y) / _CullB.y);
}

// The layer cap by distance: the nearest few spans stay, the others thin out from the cap distance over a quarter of it.
float CullCapFade(int rank, float t)
{
    if (_CullB.z <= 0.0 || rank < (int)(_CullB.w + 0.5)) return 1.0;
    return 1.0 - CullSmooth((t - _CullB.z) / (0.25 * _CullB.z));
}

float CullStepScale(float along)
{
    return 1.0 + _CullC.x * max(along, 0.0);
}

float CullTapFactor(float t)
{
    if (_CullC.w <= 1.0) return 1.0;
    return 1.0 + (_CullC.w - 1.0) * saturate((t - _CullC.y) / max(_CullC.z - _CullC.y, 1.0));
}

float CullFoot(float t, float spacing)
{
    return _CullD.x > 0.5 ? max(spacing, _CullD.y * t) : spacing;
}

#endif
