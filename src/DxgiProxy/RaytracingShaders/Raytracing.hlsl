// The raytracing passes, one entry point each, compiled to DXIL at build time
// (CMakeLists.txt) and embedded in dxgi.dll.

#include "RtCommon.hlsli"

// u0: how much of the sun a pixel sees, 0 to 1, in x; the pixel's view depth (clip w)
// in w, by which the next frame's lighting finds the pixel again.
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
    float3 normal = SurfaceNormal(id.xy, position);
    float3 towardsSun = normalize(sunDirection.xyz);
    float visible = 0.0f;
    if (dot(normal, towardsSun) > 0.0f)
    {
        Random random = MakeRandom(id.xy, frameIndex, 0u);
        float2 u = float2(NextFloat(random), NextFloat(random));
        float3 direction = SampleCone(towardsSun, sunDirection.w, u);
        visible = Unoccluded(RayOrigin(position, normal), direction, 1.0e7f, kTerrainSkip) ? 1.0f : 0.0f;
    }
    output0[id.xy] = float4(visible, 0.0f, 0.0f, mul(worldToClip, float4(position, 1.0f)).w);
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

// The G-buffer's surface at a pixel, or false where nothing was drawn.
bool Surface(uint2 pixel, out float3 position, out float3 normal)
{
    float depth = depthTexture[pixel];
    position = 0.0f;
    normal = float3(0.0f, 1.0f, 0.0f);
    if (IsSky(depth))
        return false;
    position = ClipToWorld(PixelToNdc(pixel), depth);
    normal = SurfaceNormal(pixel, position);
    return true;
}

// u3: in x, whether a cosine-weighted ray from the pixel's surface reaches
// AO_DISTANCE: averaged, the share of the sky the point sees.
[numthreads(8, 8, 1)]
void AmbientOcclusion(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float3 position, normal;
    if (!Surface(id.xy, position, normal))
    {
        output3[id.xy] = float4(1.0f, 0.0f, 0.0f, 0.0f);
        return;
    }
    Random random = MakeRandom(id.xy, frameIndex, 1u);
    float3 direction = SampleCosine(normal, float2(NextFloat(random), NextFloat(random)));
    float open = Unoccluded(RayOrigin(position, normal), direction, AO_DISTANCE, kTerrainSkip) ? 1.0f : 0.0f;
    output3[id.xy] = float4(open, 0.0f, 0.0f, 0.0f);
}

// u1: the diffuse light arriving at the pixel's surface, as a radiance in Unity's
// units: the pixel's albedo times it is its diffuse ambient light. The sun on the
// pixel itself is left out, as Unity's lighting adds it. The first hit's distance in w.
[numthreads(8, 8, 1)]
void DiffuseLight(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float3 position, normal;
    if (!Surface(id.xy, position, normal))
    {
        output1[id.xy] = float4(0.0f, 0.0f, 0.0f, 0.0f);
        return;
    }
    Random random = MakeRandom(id.xy, frameIndex, 2u);
    float3 direction = SampleCosine(normal, float2(NextFloat(random), NextFloat(random)));
    float distance;
    float3 radiance = Radiance(RayOrigin(position, normal), direction, DIFFUSE_DISTANCE, BOUNCES, random, distance,
                               kTerrainSkip);
    // The bodies' light on the pixel itself: Unity's lighting has no light for them.
    radiance += ReflectedLightAt(position, normal, random, kTerrainSkip);
    // The distance of a miss is the ray's length (100000 from the mod), past a half
    // float's largest value: written as is, it reads as infinity in the half-float
    // target. Clamped to FP16_MAX, as the sky in Reconstruct.hlsl.
    output1[id.xy] = float4(radiance, min(distance, 65504.0f));
}

// u2: the light the pixel's surface reflects towards the camera from its specular
// lobe, averaged over the lobe as a filtered reflection probe gives it; Unity's
// lighting multiplies it by the Fresnel term. The sun's disc is left out, as Unity's
// lighting draws its highlight. The reflected ray's hit distance in w.
[numthreads(8, 8, 1)]
void SpecularLight(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float depth = depthTexture[id.xy];
    if (IsSky(depth))
    {
        output2[id.xy] = float4(0.0f, 0.0f, 0.0f, 0.0f);
        return;
    }
    float3 position = ClipToWorld(PixelToNdc(id.xy), depth);
    SurfaceData surface = SurfaceAt(id.xy, position);
    float3 normal = surface.normal;
    float alpha = SmoothnessToAlpha(surface.smoothness);
    float3 view = -normalize(position);
    float3 t, b;
    Basis(normal, t, b);
    float3 viewLocal = float3(dot(view, t), dot(view, b), dot(view, normal));
    if (viewLocal.z <= 0.0f)
        viewLocal.z = 1e-4f;
    Random random = MakeRandom(id.xy, frameIndex, 3u);
    float3 microfacet = SampleGgxVisible(normalize(viewLocal), alpha, float2(NextFloat(random), NextFloat(random)));
    float3 m = t * microfacet.x + b * microfacet.y + normal * microfacet.z;
    float3 direction = reflect(-view, m);
    if (dot(direction, normal) <= 0.0f)
        direction = reflect(-view, normal);
    float distance;
    float3 radiance = Radiance(RayOrigin(position, normal), direction, SPECULAR_DISTANCE, BOUNCES, random, distance,
                               kTerrainSkip);
    // A miss clamped to FP16_MAX, as in DiffuseLight.
    output2[id.xy] = float4(radiance, min(distance, 65504.0f));
}

// The signals as the denoiser left them, for ForwardLight (Raytracing.cpp binds them).
Texture2D<float4> finishedDiffuse : register(t0, space3);
Texture2D<float4> finishedSpecular : register(t1, space3);

// u4, for forward rendering, which has no pass of Unity's to put the raytraced light in:
// the light to add to the frame Unity drew, from the signals as the denoiser left them;
// a signal not traced reads as none. The diffuse light and the reflections, less KSP's flat ambient light,
// which forward shading added already. Unity's standard Fresnel term (FresnelLerp,
// with its surface reduction) weighs the reflections.
[numthreads(8, 8, 1)]
void ForwardLight(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float depth = depthTexture[id.xy];
    if (IsSky(depth))
    {
        output4[id.xy] = 0.0f;
        return;
    }
    float3 position = ClipToWorld(PixelToNdc(id.xy), depth);
    SurfaceData surface = SurfaceAt(id.xy, position);
    // The traced diffuse light has the occlusion in it already; Unity's ambient light has
    // none. Added to a frame Unity shaded in its colour space: the linear signals taken
    // there, the surface's albedo as Unity has it.
    float3 albedo = OutputColor(surface.albedo);
    float3 ambient = UnityAmbient(surface.normal);
    float3 diffuse = OutputColor(finishedDiffuse[id.xy].rgb);
    // Never below the player's share of KSP's night light, as the deferred reflections
    // shader mixes it (ReDefinitionDeferredReflections.shader).
    diffuse = lerp(nightLight.rgb, diffuse, saturate((length(diffuse) - length(nightLight.rgb)) * 100.0f));
    float3 light = albedo * (diffuse - ambient);
    float3 view = -normalize(position);
    float nv = saturate(dot(surface.normal, view));
    float perceptual = 1.0f - surface.smoothness;
    float roughness = perceptual * perceptual;
    float reduction = 1.0f / (roughness * roughness + 1.0f);
    float grazing = saturate(surface.smoothness + max(surface.specular.r, max(surface.specular.g, surface.specular.b)));
    float t = pow(1.0f - nv, 5.0f);
    float3 fresnel = lerp(surface.specular, grazing.xxx, t);
    light += OutputColor(finishedSpecular[id.xy].rgb) * fresnel * reduction;
    output4[id.xy] = float4(light, 1.0f);
}
