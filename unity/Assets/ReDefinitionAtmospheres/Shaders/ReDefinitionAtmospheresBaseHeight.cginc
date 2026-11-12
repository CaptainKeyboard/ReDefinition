// The height variation of the cloud layers (package T13): the base and the top of a cloud type move with the place. This file is the
// shader's mirror of src/Atmospheres/Render/Layers/Height (HeightModel.RootWeight, HeightModel.Ragged, LayerGeometry.LocalBaseTop), in
// float. It sits beside the noise include because the bundle project resolves includes only next to the shader; the brief names it
// Include/BaseHeight.cginc. It is included by ReDefinitionAtmospheresLayers.shader after the noise include and after MapUv.
//
// The maps: one RGBA32 slice per layer (R root offset, G top term, B variation), a byte b meaning (b - 128) / 127 of the layer's range in
// m (so 128 is zero). Per layer the ranges and the ragged boundary's numbers come in two uniforms.
#ifndef REDEFINITION_ATMOSPHERES_BASEHEIGHT
#define REDEFINITION_ATMOSPHERES_BASEHEIGHT

UNITY_DECLARE_TEX2DARRAY(_HeightMaps);
float4 _LayerH[4];    // root range m, top range m, variation range m, ragged boundary m (0: off)
float4 _LayerH2[4];   // 1 / ragged scale (1/m), thinnest cloud m (0: the layer has no height variation), ragged seed, maps present (1) or not (0)

// Smooth value noise in [0, 1) at a point in lattice units: the hash of the lattice corners blended trilinearly (HeightModel.ValueNoise).
float HeightValueNoise(float3 p, uint seed)
{
    float3 f0 = floor(p);
    int3 i0 = (int3)f0;
    float3 u = NzFade(p - f0);
    float acc = 0;
    [unroll] for (int k = 0; k < 8; k++)
    {
        int3 o = int3(k & 1, (k >> 1) & 1, (k >> 2) & 1);
        float w = (o.x ? u.x : 1.0 - u.x) * (o.y ? u.y : 1.0 - u.y) * (o.z ? u.z : 1.0 - u.z);
        acc += w * NzUnit(NzHash(i0 + o, seed));
    }
    return acc;
}

// The roughness of the lower boundary, -1 to 1 (HeightModel.Ragged).
float HeightRagged(float3 p, uint seed)
{
    float a = HeightValueNoise(p, seed) * 2.0 - 1.0;
    float b = HeightValueNoise(p * 2.31 + 17.0, seed ^ 0x5bd1e995u) * 2.0 - 1.0;
    return 0.7 * a + 0.3 * b;
}

// The share of a type that is rooted in the surface air (HeightModel.RootWeight).
float HeightRootWeight(float baseShare)
{
    float x = saturate((baseShare - 0.1) / 0.3);
    return 1.0 - x * x * (3.0 - 2.0 * x);
}

// How far below the layer's inner altitude and above its outer altitude a cloud of the layer can reach, in m.
float HeightReachDown(int li) { return _LayerH[li].x + _LayerH[li].z + 2.0 * _LayerH[li].w; }
float HeightReachUp(int li) { return _LayerH[li].x + _LayerH[li].z + _LayerH[li].y + 2.0 * _LayerH[li].w + _LayerH2[li].y; }

// The base and top shares of a type at a point (LayerGeometry.LocalBaseTop): the type table's shares b and tp, the type's erosion, the
// layer's thickness in m, the point relative to the planet's centre.
void HeightLocalBaseTop(int li, float3 pc, float b, float tp, float erosion, float thickness, float shiftDeg, out float lb, out float lt)
{
    float3 hm = 0;
    if (_LayerH2[li].w > 0.5)
    {
        float2 uv = MapUv(pc, shiftDeg);
        hm = (UNITY_SAMPLE_TEX2DARRAY_LOD(_HeightMaps, float3(uv, li), 0).rgb - 128.0 / 255.0) * (255.0 / 127.0);
    }
    float root = HeightRootWeight(b);
    float smoothOffset = root * hm.x * _LayerH[li].x + hm.z * _LayerH[li].z;
    float baseOffset = smoothOffset;
    if (_LayerH[li].w > 0)
    {
        float k = (_PlanetR + _LayerA[li].x) / max(length(pc), 1.0) * _LayerH2[li].x;
        baseOffset += HeightRagged(pc * k, (uint)_LayerH2[li].z) * _LayerH[li].w * (erosion / 0.5);
    }
    float topOffset = smoothOffset + root * hm.y * _LayerH[li].y;
    lb = b + baseOffset / thickness;
    lt = max(tp + topOffset / thickness, lb + _LayerH2[li].y / thickness);
}

#endif
