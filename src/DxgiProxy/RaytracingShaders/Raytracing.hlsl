// The raytracing passes, one entry point each, compiled to DXIL at build time
// (CMakeLists.txt) and embedded in dxgi.dll.

#include "RtCommon.hlsli"

// u0: how much of the sun a pixel sees, 0 to 1, in x; the pixel's distance in w.
// One ray a frame into the sun's disc: the denoiser averages them into a soft edge.
[numthreads(8, 8, 1)]
void SunVisibility(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float depth = depthTexture[id.xy];
    if (IsSky(depth))
    {
        output0[id.xy] = float4(1.0f, 0.0f, 0.0f, 0.0f);
        return;
    }
    float3 position = ClipToWorld(PixelToNdc(id.xy), depth);
    float3 normal = DecodeNormal(gbuffer2[id.xy]);
    float3 towardsSun = normalize(sunDirection.xyz);
    float visible = 0.0f;
    if (dot(normal, towardsSun) > 0.0f)
    {
        Random random = MakeRandom(id.xy, frameIndex, 0u);
        float2 u = float2(NextFloat(random), NextFloat(random));
        float3 direction = SampleCone(towardsSun, sunDirection.w, u);
        visible = Unoccluded(RayOrigin(position, normal), direction, 1.0e7f) ? 1.0f : 0.0f;
    }
    output0[id.xy] = float4(visible, 0.0f, 0.0f, length(position));
}

// u7: the camera's ray through the pixel's centre against the scene, in x its hit
// distance (0 for none), in y the distance the depth gives, in z the instance hit
// plus one. It checks that the scene the rays see is the one Unity drew.
[numthreads(8, 8, 1)]
void PrimaryCheck(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float2 ndc = PixelToNdc(id.xy);
    float depth = depthTexture[id.xy];
    float expected = IsSky(depth) ? 0.0f : length(ClipToWorld(ndc, depth));

    // The camera sits at the origin; any point on the pixel's line gives the direction.
    float3 direction = normalize(ClipToWorld(ndc, 0.5f));
    RayDesc ray;
    ray.Origin = float3(0.0f, 0.0f, 0.0f);
    ray.Direction = direction;
    ray.TMin = 0.0f;
    ray.TMax = 1.0e7f;
    RayQuery<RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES | RAY_FLAG_FORCE_OPAQUE> query;
    query.TraceRayInline(scene, RAY_FLAG_NONE, 0xFF, ray);
    query.Proceed();
    float hit = 0.0f;
    float instance = 0.0f;
    if (query.CommittedStatus() == COMMITTED_TRIANGLE_HIT)
    {
        hit = query.CommittedRayT();
        instance = float(query.CommittedInstanceID() + 1u);
    }
    output7[id.xy] = float4(hit, expected, instance, 1.0f);
}
