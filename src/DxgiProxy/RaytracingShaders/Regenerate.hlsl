// AMD FSR Ray Regeneration's guides (RaytracingRegenerate.cpp), from the surface the
// passes see, as its documentation asks for them (denoising.md, 5.2-5.5, FidelityFX SDK
// 2.3.0): the signed linear depth; the motion as previous minus current UV, with the
// linear depth's change; the normal octahedrally encoded with the linear roughness and
// material type 0; the specular albedo square-root encoded. The diffuse albedo in
// RegenerateAlbedo.hlsl, as a table has four places to write.

#include "RtCommon.hlsli"

RWTexture2D<float> linearDepthOut : register(u0, space3);
RWTexture2D<float4> motionOut : register(u1, space3);
RWTexture2D<float4> normalsOut : register(u2, space3);
RWTexture2D<float4> specularAlbedoOut : register(u3, space3);
// The linear depth this pass wrote the frame before.
Texture2D<float> linearDepthBefore : register(t0, space3);

cbuffer Step : register(b1)
{
    uint stepFlags;   // 2: motion vectors in t5; 4: linearDepthBefore is the frame before's
    uint3 stepUnused;
};

// A linear depth past any far plane, where nothing was drawn: outside the bounds the
// denoiser is given, so it passes the sky through.
static const float kSkyDepth = 1.0e9f;

// denoising.md, 5.3.1.
float2 NormalToOctahedronUv(float3 n)
{
    n.xy /= abs(n.x) + abs(n.y) + abs(n.z);
    float2 k = sign(n.xy);
    float s = saturate(-n.z);
    n.xy = lerp(n.xy, (1.0f - abs(n.yx)) * k, s);
    return n.xy * 0.5f + 0.5f;
}

float2 PreviousUv(float4 previousClip)
{
    return float2(previousClip.x / previousClip.w * 0.5f + 0.5f, 0.5f - previousClip.y / previousClip.w * 0.5f);
}

// Whether the frame before had a surface at previousUv at the depth this one expects
// there, by the tolerance ReDefinition's denoiser keeps history with (Denoise.hlsl).
bool SurfaceBefore(float2 previousUv, float expectedDepth)
{
    if (any(previousUv <= 0.0f) || any(previousUv >= 1.0f))
        return false;
    float zBefore = linearDepthBefore[uint2(previousUv * float2(frameSize))];
    return zBefore > 0.0f && abs(zBefore - expectedDepth) < 0.03f * expectedDepth + 0.02f;
}

[numthreads(8, 8, 1)]
void RegenerationGuides(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float depth = depthTexture[id.xy];
    if (IsSky(depth))
    {
        linearDepthOut[id.xy] = kSkyDepth;
        motionOut[id.xy] = 0.0f;
        normalsOut[id.xy] = float4(NormalToOctahedronUv(float3(0.0f, 1.0f, 0.0f)), 1.0f, 0.0f);
        specularAlbedoOut[id.xy] = 0.0f;
        return;
    }
    float3 position = ClipToWorld(PixelToNdc(id.xy), depth);
    float linearDepth = mul(worldToClip, float4(position, 1.0f)).w;
    float4 previousClip = mul(prevWorldToClip, float4(position, 1.0f));
    float2 uv = (float2(id.xy) + 0.5f) / float2(frameSize);
    // By the motion vectors where they are bound, else by the camera's two matrices, as
    // ReDefinition's denoiser reprojects.
    float2 previousUv = (stepFlags & 2u) != 0u
        ? uv - motionTexture[id.xy].xy
        : PreviousUv(previousClip);
    // Without motion vectors, first as the camera and the vessels near it moved, then
    // where that finds no surface the frame before, as the bodies did: under
    // Krakensbane an origin shift moves the terrain by more than the camera
    // (prevWorldToClipFar; Denoise.hlsl's temporal pass does the same).
    if ((stepFlags & 6u) == 4u && !SurfaceBefore(previousUv, previousClip.w))
    {
        float4 farClip = mul(prevWorldToClipFar, float4(position, 1.0f));
        float2 farUv = PreviousUv(farClip);
        if (SurfaceBefore(farUv, farClip.w))
        {
            previousClip = farClip;
            previousUv = farUv;
        }
    }
    linearDepthOut[id.xy] = linearDepth;
    motionOut[id.xy] = float4(previousUv - uv, previousClip.w - linearDepth, 0.0f);

    SurfaceData surface = SurfaceAt(id.xy, position);
    // Linear roughness, squared to the GGX alpha: Unity's perceptual roughness.
    float roughness = 1.0f - surface.smoothness;
    normalsOut[id.xy] = float4(NormalToOctahedronUv(surface.normal), roughness, 0.0f);
    float3 view = -normalize(position);
    float3 specular = EnvBRDFApprox2(surface.specular, roughness * roughness, dot(surface.normal, view));
    specularAlbedoOut[id.xy] = float4(sqrt(max(specular, 0.0f)), 1.0f);
}
