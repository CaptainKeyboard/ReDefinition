// The cloud's place in the previous frame, layer by layer (E40 MVCLOUD). Included by the reconstruction shaders after they declare the camera (_CamPos) and the previous
// camera (_PrevPos, _PrevF, _PrevR, _PrevU).
//
// A pixel's cloud is not on the first layer's middle sphere: a cirrus deck at 8 km and cumulus at 2 km move differently when the camera translates. Every layer is met by the ray
// at its middle sphere and seen from the previous camera; the places are blended by the share of the pixel's cloud each layer holds (LayerMotionModel.cs is the CPU twin):
//   presence  the coverage map's value at the hit as a share (the density's coverage threshold with its soft edge, without noise, the type's lift and the height variation),
//   opacity   1 - exp(-vertical optical depth / cosine of the ray to the radial direction) of the layer's column,
//   weight    the presence times (1 - presence * opacity) of every layer the ray meets nearer.
// The same place serves the history of the reconstruction (the cloud's colour of the last frame) and the motion vector the upscalers and frame generation read for the cloud.
#ifndef REDEFINITION_ATMOSPHERES_CLOUD_MOTION
#define REDEFINITION_ATMOSPHERES_CLOUD_MOTION
#include "MapSlew.cginc"

float4 _MvA[4];      // x: radius of the layer's middle sphere (m from the planet's centre), y: the layer's coverage, z: its edge softness, w: its coverage map slice (< 0: no map, the layer fills the pixel)
float4 _MvB[4];      // x: vertical optical depth of a full column, y: the map's longitude shift (degrees), z: the clear-sky K of the map value (0: none), w: 0
float4 _MvD[4];      // xyz: the layer's drift over one frame (m, planet frame), w: 0
float4 _MvN;         // x: the layers to blend (0: the caller takes the first layer's shell), y: the planet's radius (m), z: the previous camera exists (1) or not (0), w: 0
float4 _MvMap;       // the coverage maps' width and height
float4 _MvReach;     // the coverage maps' slew reach per layer (MapSlew.cginc)
float4x4 _MvWorldToMap;
UNITY_DECLARE_TEX2DARRAY(_MvCov);

// Whether the previous camera saw the point at v (relative to it): in front of it and within its view, widened by half a picture on every side (the previous place between -0.5 and 1.5 of the
// picture). Only such a point has a previous place. The test is made without the division: a point beside or behind the previous camera has no finite projection, and its place in RGHalf
// overflowed to Inf, the blend's 0 x Inf to NaN, which the upscaler read. A value that is not a number fails it. (Not the cause of DLSS's GPU page faults after a teleport, which K-T2 2026-11-18
// put inside NGX's D3D11 evaluation with finite inputs: DlssD3d12.h.)
bool PrevSees(float3 v)
{
    float f = dot(v, _PrevF.xyz);
    float x = dot(v, _PrevR.xyz), y = dot(v, _PrevU.xyz);
    return f > 1e-3 && abs(x) <= 2.0 * f && abs(y) <= 2.0 * f;
}

float MvPresence(int l, float3 pc)
{
    float slice = _MvA[l].w;
    if (slice < 0) return 1.0;
    float3 n = normalize(mul((float3x3)_MvWorldToMap, pc));
    float lat = asin(clamp(n.z, -1.0, 1.0));
    float lon = atan2(n.y, n.x);
    float2 uv = float2((lon * 57.29577951 - _MvB[l].y + 180.0) / 360.0, (90.0 - lat * 57.29577951) / 180.0);
    float vMin = 0.5 / _MvMap.y;
    uv.y = clamp(uv.y, vMin, 1.0 - vMin);
    float cov = SlewedMap(UNITY_SAMPLE_TEX2DARRAY_LOD(_MvCov, float3(uv, slice), 0).rg, _MvReach, slice);
    cov = min(cov, 2.0 * cov - _MvB[l].z);
    return saturate((cov + _MvA[l].y - 1.0) / max(_MvA[l].z, 1e-4) + 0.5);
}

// The previous place (uv, not limited to the picture) of the cloud seen along the ray from o (planet-relative) in direction d, and the weighted distance along the ray. False where
// no layer fills the pixel, or the cloud was behind the previous camera.
bool CloudPrevious(float3 o, float3 d, out float2 puv, out float depth)
{
    puv = 0;
    depth = 0;
    if (_MvN.x < 0.5) return false;
    float len = length(o);
    float b = dot(o, d);
    float tPlanet = 1e30;
    float discP = b * b - (len - _MvN.y) * (len + _MvN.y);
    if (discP > 0)
    {
        float tp = -b - sqrt(discP);
        if (tp > 0) tPlanet = tp;
    }
    float t[4], p[4], a[4], fv[4];
    float2 pu[4];
    [unroll] for (int l = 0; l < 4; l++)
    {
        t[l] = 0; p[l] = 0; a[l] = 0; fv[l] = 0; pu[l] = 0;
        if (l >= (int)_MvN.x) continue;
        float r = _MvA[l].x;
        float disc = b * b - (len - r) * (len + r);
        if (disc <= 0) continue;
        float sq = sqrt(disc);
        float t0 = -b - sq, t1 = -b + sq;
        float th = t0 > 0 ? t0 : t1;
        if (th <= 0 || th >= tPlanet) continue;
        float3 pc = o + d * th;
        t[l] = th;
        p[l] = MvPresence(l, pc);
        a[l] = 1.0 - exp(-_MvB[l].x / max(abs(dot(d, pc)) / r, 0.05));
        float3 v = pc - _MvD[l].xyz - _PrevPos.xyz;
        float f = dot(v, _PrevF.xyz);
        fv[l] = PrevSees(v) ? f : 0.0;   // only a place the previous camera saw counts (PrevSees)
        pu[l] = float2(dot(v, _PrevR.xyz), dot(v, _PrevU.xyz)) / max(f, 1e-3) * 0.5 + 0.5;
    }
    float sum = 0, seen = 0, td = 0;
    float2 su = 0;
    [unroll] for (int m = 0; m < 4; m++)
    {
        float w = p[m];
        if (w <= 0) continue;
        [unroll] for (int k = 0; k < 4; k++)
        {
            if (k != m && p[k] > 0 && t[k] < t[m]) w *= 1.0 - p[k] * a[k];
        }
        sum += w;
        td += w * t[m];
        if (fv[m] > 1e-3)
        {
            seen += w;
            su += w * pu[m];
        }
    }
    if (sum < 1e-3 || seen < 0.5 * sum) return false;
    puv = su / seen;
    depth = td / sum;
    return true;
}

#endif
