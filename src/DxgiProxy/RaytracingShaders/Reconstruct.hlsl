// The guides DLSS Ray Reconstruction takes besides the noisy frame
// (ProgrammingGuideDLSS_RR.md 4.1), from Unity's G-buffer and the reflections'
// hit distance: diffuse albedo, specular albedo, the world normal with linear
// roughness in w, and the hit distance.

#include "RtCommon.hlsli"

Texture2D<float4> specularLight : register(t0, space3);
RWTexture2D<float4> diffuseAlbedo : register(u0, space3);
RWTexture2D<float4> specularAlbedo : register(u1, space3);
RWTexture2D<float4> normalRoughness : register(u2, space3);
RWTexture2D<float4> hitDistance : register(u3, space3);

// NVIDIA's approximation of the split-sum's environment term (ProgrammingGuideDLSS_RR.md
// 4.2.1, after Ray Tracing Gems, chapter 32), as the guide gives it.
float3 EnvBRDFApprox2(float3 specularColor, float alpha, float nov)
{
    nov = abs(nov);
    float4 x = float4(1.0f, nov, nov * nov, nov * nov * nov);
    float4 y = float4(1.0f, alpha, alpha * alpha, alpha * alpha * alpha);
    float2x2 m1 = float2x2(0.99044f, -1.28514f, 1.29678f, -0.755907f);
    float3x3 m2 = float3x3(1.0f, 2.92338f, 59.4188f, 20.3225f, -27.0302f, 222.592f, 121.563f, 626.13f, 316.627f);
    float2x2 m3 = float2x2(0.0365463f, 3.32707f, 9.0632f, -9.04756f);
    float3x3 m4 = float3x3(1.0f, 3.59685f, -1.36772f, 9.04401f, -16.3174f, 9.22949f, 5.56589f, 19.7886f, -20.2123f);
    float bias = dot(mul(m1, x.xy), y.xy) * rcp(dot(mul(m2, x.xyw), y.xyw));
    float scale = dot(mul(m3, x.xy), y.xy) * rcp(dot(mul(m4, x.xzw), y.xyw));
    bias *= saturate(specularColor.g * 50.0f);
    return mad(specularColor, max(0.0f, scale), max(0.0f, bias));
}

[numthreads(8, 8, 1)]
void ReconstructGuides(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float depth = depthTexture[id.xy];
    float4 g0 = gbuffer0[id.xy];
    float4 g1 = gbuffer1[id.xy];
    if (IsSky(depth))
    {
        diffuseAlbedo[id.xy] = float4(0.0f, 0.0f, 0.0f, 1.0f);
        specularAlbedo[id.xy] = float4(0.0f, 0.0f, 0.0f, 1.0f);
        normalRoughness[id.xy] = float4(0.0f, 1.0f, 0.0f, 1.0f);
        hitDistance[id.xy] = float4(0.0f, 0.0f, 0.0f, 0.0f);
        return;
    }
    float3 position = ClipToWorld(PixelToNdc(id.xy), depth);
    float3 normal = DecodeNormal(gbuffer2[id.xy]);
    float3 view = -normalize(position);
    // "Linear roughness" in NVIDIA's guide is squared to the GGX alpha: Unity's
    // perceptual roughness, one minus smoothness.
    float roughness = 1.0f - g1.a;
    diffuseAlbedo[id.xy] = float4(g0.rgb, 1.0f);
    specularAlbedo[id.xy] = float4(EnvBRDFApprox2(g1.rgb, roughness * roughness, dot(normal, view)), 1.0f);
    normalRoughness[id.xy] = float4(normal, roughness);
    hitDistance[id.xy] = float4(specularLight[id.xy].w, 0.0f, 0.0f, 0.0f);
}
