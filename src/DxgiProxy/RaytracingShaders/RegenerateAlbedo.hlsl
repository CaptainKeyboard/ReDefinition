// AMD FSR Ray Regeneration's diffuse albedo guide (RaytracingRegenerate.cpp), square-root
// encoded as its documentation asks (denoising.md, ffxDispatchDescDenoiser); the other
// guides in Regenerate.hlsl.

#include "RtCommon.hlsli"

// The guide written, 0 under the sky.
RWTexture2D<float4> diffuseAlbedoOut : register(u0, space3);

// Per pixel the surface's albedo (SurfaceAt), its square root.
[numthreads(8, 8, 1)]
void RegenerationAlbedo(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float depth = depthTexture[id.xy];
    if (IsSky(depth))
    {
        diffuseAlbedoOut[id.xy] = 0.0f;
        return;
    }
    float3 position = ClipToWorld(PixelToNdc(id.xy), depth);
    diffuseAlbedoOut[id.xy] = float4(sqrt(max(SurfaceAt(id.xy, position).albedo, 0.0f)), 1.0f);
}
