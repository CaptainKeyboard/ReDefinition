// The guides DLSS Ray Reconstruction takes besides the noisy frame
// (ProgrammingGuideDLSS_RR.md 4.1): diffuse albedo, specular albedo, the world normal
// with linear roughness in w, and the reflections' hit distance. ReconstructGuides
// takes them from Unity's G-buffer; FrameGuides, within the raytraced frame, from the
// surface the passes see, which needs no G-buffer.

#include "RtCommon.hlsli"

Texture2D<float4> specularLight : register(t0, space3);
RWTexture2D<float4> diffuseAlbedo : register(u0, space3);
RWTexture2D<float4> specularAlbedo : register(u1, space3);
RWTexture2D<float4> normalRoughness : register(u2, space3);
RWTexture2D<float4> hitDistance : register(u3, space3);

// The sky as NVIDIA's guide gives it (DLSS-RR Integration Guide 3.4.1-3.4.4, 3.4.9):
// albedo 0.5, specular albedo, normal and roughness 0, hit distance FP16_MAX.
void WriteSky(uint2 pixel)
{
    diffuseAlbedo[pixel] = float4(0.5f, 0.5f, 0.5f, 1.0f);
    specularAlbedo[pixel] = float4(0.0f, 0.0f, 0.0f, 1.0f);
    normalRoughness[pixel] = float4(0.0f, 0.0f, 0.0f, 0.0f);
    hitDistance[pixel] = float4(kFp16Max, 0.0f, 0.0f, 0.0f);
}

void WriteGuides(uint2 pixel, float3 position, float3 albedo, float3 specular, float smoothness, float3 normal,
                 float hit)
{
    float3 view = -normalize(position);
    // "Linear roughness" in NVIDIA's guide is squared to the GGX alpha: Unity's
    // perceptual roughness, one minus smoothness.
    float roughness = 1.0f - smoothness;
    diffuseAlbedo[pixel] = float4(albedo, 1.0f);
    specularAlbedo[pixel] = float4(EnvBRDFApprox2(specular, roughness * roughness, dot(normal, view)), 1.0f);
    normalRoughness[pixel] = float4(normal, roughness);
    hitDistance[pixel] = float4(hit, 0.0f, 0.0f, 0.0f);
}

[numthreads(8, 8, 1)]
void ReconstructGuides(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float depth = depthTexture[id.xy];
    if (IsSky(depth))
    {
        WriteSky(id.xy);
        return;
    }
    float4 g1 = gbuffer1[id.xy];
    WriteGuides(id.xy, ClipToWorld(PixelToNdc(id.xy), depth), gbuffer0[id.xy].rgb, g1.rgb, g1.a,
                DecodeNormal(gbuffer2[id.xy]), specularLight[id.xy].w);
}

// Within the raytraced frame, after its passes: the reflections' output in u2.
[numthreads(8, 8, 1)]
void FrameGuides(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float depth = depthTexture[id.xy];
    if (IsSky(depth))
    {
        WriteSky(id.xy);
        return;
    }
    float3 position = ClipToWorld(PixelToNdc(id.xy), depth);
    SurfaceData surface = SurfaceAt(id.xy, position);
    // The albedo as the G-buffer has it, in the colour space of the frame it guides.
    WriteGuides(id.xy, position, OutputColor(surface.albedo), surface.specular, surface.smoothness, surface.normal, output2[id.xy].w);
}
