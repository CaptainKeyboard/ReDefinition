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
        if (SURFACE_INTENT)
        {
            output5[id.xy] = 0.0f;
            output6[id.xy] = 0.0f;
        }
        return;
    }
    float3 position = ClipToWorld(PixelToNdc(id.xy), depth);
    float3 normal = SurfaceNormal(id.xy, position);
    // The surfaces as their materials mean them, for the passes after this one and for
    // Unity's G-buffer (RtCamera, the surfaces written back).
    if (SURFACE_INTENT && HAS_GBUFFER)
    {
        uint mode;
        bool terrain, lawn;
        float lift;
        uint region;
        output5[id.xy] = SurfaceIntent(id.xy, position, normal, mode, terrain, lawn, lift, region);
        // In y the light the ground keeps under a meadow's blades past Parallax's grass, and the space centre's lawn
        // lifted to the terrain's albedo (MeadowShade),
        // which the G-buffer's albedo is multiplied by (ReDefinitionRtCapture.shader, fragMeadow): sun and
        // sky alike, at any distance -- the sun's visibility here goes into Unity's shadows, which it
        // fades out at its shadow distance.
        // -3 (the probe): every pixel the rays take as ground, green or not.
        float meadow = shadowFadeScale.z < -2.5f ? (terrain ? shadowFadeScale.w : 1.0f)
                       : terrain || shadowFadeScale.z < 0.0f ? MeadowShade(id.xy, position, lawn, lift) : 1.0f;
        // In z what the camera's ray met (RT_REGION_*), for the probe's surface images; in w the space centre's
        // lawn's occlusion as the terrain's (LawnOcclusion), which the G-buffer's occlusion is multiplied by, 1
        // elsewhere (more than a half: a surface found).
        float occlusion = lawn && shadowFadeScale.z > -1.5f ? lerp(1.0f, LawnOcclusion(gbuffer0[id.xy].rgb), lift) : 1.0f;
        output6[id.xy] = float4(float(mode) / 8.0f, meadow, float(region) / 8.0f, occlusion);
    }
    float3 towardsSun = normalize(sunDirection.xyz);
    float visible = 0.0f;
    if (dot(normal, towardsSun) > 0.0f)
    {
        Random random = MakeRandom(id.xy, frameIndex, 0u);
        float2 u = float2(NextFloat(random), NextFloat(random));
        float3 direction = SampleCone(towardsSun, sunDirection.w, u);
        visible = SunUnoccluded(RayOrigin(position, normal), direction, 1.0e7f, kTerrainSkip) ? 1.0f : 0.0f;
    }
    if ((frameFlags & RT_FLAG_CLOUD_SHADOW) != 0u)
    {
        float3 at;
        float fade;
        visible = cloudColor.w > 13.5f ? length(position)
                  : cloudColor.w > 10.5f ? (CloudExit(position, towardsSun, at, fade) ? CloudProbeValue(at) : -1.0f)
                                         : CloudTransmittance(position, towardsSun).r;
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
    // Through the grass's blades with the share they let through, as the sky's light passes them.
    float open = SunUnoccluded(RayOrigin(position, normal), direction, AO_DISTANCE, kTerrainSkip) ? 1.0f : 0.0f;
    output3[id.xy] = float4(open, 0.0f, 0.0f, 0.0f);
}

// u1: the diffuse light arriving at the pixel's surface, as a radiance in Unity's
// units: the pixel's albedo times it is its diffuse ambient light. The sun on the
// pixel itself is left out, as Unity's lighting adds it. The first hit's distance in w.
// `cached`: with the radiance cache (RadianceCache.hlsli), as DiffuseLightCached, a
// pipeline of its own, so that the pass without it carries none of its code.
void DiffuseLightAt(uint3 id, bool cached)
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
                               kTerrainSkip, false, cached);
    // The pixel's ray is a sample of the light arriving at its surface, as the cache holds
    // it (RadianceCache.hlsli): into its cell, for the paths of other pixels and of the
    // frames after that meet this surface. Each group of 8x8 pixels writes them every
    // kCachePrimaryFrames-th frame, the groups in turn: a cell spans many pixels and
    // frames, and its value is their mean all the same; chosen by the group, the other
    // groups' waves go without the cost.
    uint2 group = id.xy / 8u;
    if (cached && CACHE_UPDATE && (Pcg(group.x + Pcg(group.y)) + frameIndex) % kCachePrimaryFrames == 0u)
        CacheAdd(position, normal, length(position), radiance, random);
    // The cache's debug view (rtRadianceCacheDebug): 1 the value of the pixel's cell, not
    // jittered, black where it has none; 2 the cells, each in a colour from its fingerprint.
    if (cached && CACHE_ON && CACHE_DEBUG != 0u)
    {
        CacheKey key = CacheKeyAt(position, normal, length(position), false, random);
        uint entry = CacheFind(key);
        float3 shown = 0.0f;
        if (CACHE_DEBUG == 2u)
            shown = float3(Pcg(key.checksum) & 0xFFu, Pcg(key.checksum + 1u) & 0xFFu, Pcg(key.checksum + 2u) & 0xFFu) / 255.0f;
        else if (entry != kCacheNone)
            shown = cacheValues[entry].rgb;
        output1[id.xy] = float4(shown, min(distance, kFp16Max));
        return;
    }
    // The bodies' light on the pixel itself: Unity's lighting has no light for them.
    radiance += ReflectedLightAt(position, normal, random, kTerrainSkip);
    // The distance of a miss is the ray's length (100000 from the mod), past a half
    // float's largest value: written as is, it reads as infinity in the half-float
    // target. Clamped to FP16_MAX, as the sky in Reconstruct.hlsl.
    output1[id.xy] = float4(radiance, min(distance, kFp16Max));
}

[numthreads(8, 8, 1)]
void DiffuseLight(uint3 id : SV_DispatchThreadID)
{
    DiffuseLightAt(id, false);
}

[numthreads(8, 8, 1)]
void DiffuseLightCached(uint3 id : SV_DispatchThreadID)
{
    DiffuseLightAt(id, true);
}

// u2: the light the pixel's surface reflects towards the camera from its specular
// lobe, averaged over the lobe as a filtered reflection probe gives it; Unity's
// lighting multiplies it by the Fresnel term. The sun's disc is left out, as Unity's
// lighting draws its highlight. The reflected ray's hit distance in w. In the water's
// frame (kRtFrameWater) on KSP's own sea (STOCK_WATER) only the sea (IsStockWater), its
// waves and their roughness (StockWaves), whose highlight ReDefinition turns off:
// what the rays meet and the sky, and the sun's and the moons' discs as on Scatterer's
// ocean (BodiesMirroredAt), undenoised, as the glints are; the reflections' shader takes
// it there with water's Fresnel term (ReDefinitionDeferredReflections.shader).
// `cached`: with the radiance cache, as SpecularLightCached (DiffuseLightAt).
void SpecularLightAt(uint3 id, bool cached)
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
    bool stockWater = WATER_SURFACE && STOCK_WATER;
    float waterM = 0.0f;
    // The water a pixel covers across the view: its width at its distance.
    float pixelWidth = WATER_SURFACE ? length(ClipToWorld(PixelToNdc(id.xy + uint2(1u, 0u)), depth) - position) : 0.0f;
    if (stockWater && !IsStockWater(position, normal))
    {
        output2[id.xy] = float4(0.0f, 0.0f, 0.0f, 0.0f);
        return;
    }
    if (stockWater)
    {
        float4 waves = StockWaves(position, pixelWidth);
        normal = waves.xyz;
        if (WATER_NORMALS)
        {
            output2[id.xy] = float4(normal * 0.5f + 0.5f, 1.0f);
            return;
        }
        waterM = sqrt(waves.w * waves.w + kWaterLeastRoughness * kWaterLeastRoughness);
        // GGX's alpha for the reflected rays, which spread about as Beckmann's m / sqrt(2).
        alpha = 0.7071f * waterM;
    }
    else if (WATER_SURFACE && WATER_NORMALS)
    {
        // ReDefinition's ripples alone, on the level sea, whatever the reflected ray meets:
        // Scatterer's own waves run on its own time.
        float4 ripples = ScattererRipples(position, normalize(position - bodySphere.xyz), 2.0f * alpha, pixelWidth);
        output2[id.xy] = float4(ripples.xyz * 0.5f + 0.5f, kWaterSky);
        return;
    }
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
    // Water reflects nothing under its own surface: a wave's reflection that points
    // below the horizon is turned back over it, or it would meet the sea floor.
    float3 up = bodySphere.w > 0.0f ? normalize(position - bodySphere.xyz) : float3(0.0f, 1.0f, 0.0f);
    if (WATER_SURFACE && dot(direction, up) < 0.0f)
        direction -= 2.0f * dot(direction, up) * up;
    float distance;
    float3 origin = RayOrigin(position, normal);
    float3 radiance = Radiance(origin, direction, SPECULAR_DISTANCE, BOUNCES, random, distance, kTerrainSkip, true, cached);
    if (stockWater)
    {
        radiance += BodiesMirroredAt(position, normal, view, waterM, kTerrainSkip);
        output2[id.xy] = float4(radiance, min(distance, kFp16Max));
        return;
    }
    if (WATER_SURFACE)
    {
        // Where the ray met the scene, what it met, and its distance in w, at most
        // kWaterHitLimit. Where it met nothing, only the light of the sun's and the moons'
        // discs the wave mirrors, and kWaterSky in w: the ocean keeps its own reflection of
        // the sky, and this light adds to it (ReDefinitionRtCapture.shader, fragOceanCompose).
        if (distance < SPECULAR_DISTANCE)
        {
            output2[id.xy] = float4(radiance, min(distance, kWaterHitLimit));
            return;
        }
        // Scatterer's variance (alpha = sqrt(sigmaSq) / 2) as ripples on its normals.
        float4 rippled = bodySphere.w > 0.0f ? ScattererRipples(position, normal, 2.0f * alpha, pixelWidth)
                                             : float4(normal, 2.0f * alpha);
        float m = sqrt(rippled.w * rippled.w + kWaterLeastRoughness * kWaterLeastRoughness);
        output2[id.xy] = float4(BodiesMirroredAt(position, rippled.xyz, view, m, kTerrainSkip), kWaterSky);
        return;
    }
    // A ray that met nothing, nor the main body's sphere, sees the moons' discs; one that
    // meets a disc counts as a hit, far off.
    if (distance >= SPECULAR_DISTANCE && !BodyBlocks(origin, direction))
    {
        float3 disc = BodyDiscs(direction);
        if (any(disc > 0.0f))
        {
            radiance += disc;
            distance = kDiscHit;
        }
    }
    // A miss clamped to FP16_MAX, as in DiffuseLight.
    output2[id.xy] = float4(radiance, min(distance, kFp16Max));
}

[numthreads(8, 8, 1)]
void SpecularLight(uint3 id : SV_DispatchThreadID)
{
    SpecularLightAt(id, false);
}

[numthreads(8, 8, 1)]
void SpecularLightCached(uint3 id : SV_DispatchThreadID)
{
    SpecularLightAt(id, true);
}

// Unity's direct light from the sun on a surface, as its shader draws it, per unit of the
// light's colour after its shadow: the diffuse term returned, without the albedo, and the
// highlight. The standard shader (UnityStandardBRDF.cginc, BRDF1_Unity_PBS, GGX): Disney's
// diffuse term times N.L, and V D pi -- its square root in gamma space -- times N.L and
// Schlick's Fresnel term. Unity's legacy Blinn-Phong (Lighting.cginc,
// UnityBlinnPhongLight): N.L, and (N.H)^exponent times the specular colour and gloss.
float UnityDirectTerms(SurfaceData surface, float3 towards, float3 viewDirection, out float3 highlight)
{
    float3 normal = surface.normal;
    float nl = saturate(dot(normal, towards));
    float3 halfDirection = normalize(towards + viewDirection);
    float nh = saturate(dot(normal, halfDirection));
    highlight = 0.0f;
    if (surface.highlight.w > 0.0f)
    {
        highlight = surface.highlight.rgb * pow(nh, surface.highlight.w);
        return nl;
    }
    if (!any(surface.highlight.rgb > 0.0f))
        return nl;
    float perceptual = 1.0f - saturate(-surface.highlight.w);
    float nv = abs(dot(normal, viewDirection));
    float lh = saturate(dot(towards, halfDirection));
    float fd90 = 0.5f + 2.0f * lh * lh * perceptual;
    float lightScatter = 1.0f + (fd90 - 1.0f) * pow(1.0f - nl, 5.0f);
    float viewScatter = 1.0f + (fd90 - 1.0f) * pow(1.0f - nv, 5.0f);
    float roughness = max(perceptual * perceptual, 0.002f);
    float lambdaV = nl * (nv * (1.0f - roughness) + roughness);
    float lambdaL = nv * (nl * (1.0f - roughness) + roughness);
    float visibility = 0.5f / (lambdaV + lambdaL + 1e-5f);
    float a2 = roughness * roughness;
    float d = (nh * a2 - nh) * nh + 1.0f;
    float distribution = a2 / (3.14159265f * (d * d + 1e-7f));
    float term = visibility * distribution * 3.14159265f;
    if (GAMMA_INPUTS)
        term = sqrt(max(1e-4f, term));
    term = max(0.0f, term * nl);
    float3 f0 = surface.highlight.rgb;
    highlight = term * (f0 + (1.0f - f0) * pow(1.0f - lh, 5.0f));
    return lightScatter * viewScatter * nl;
}

// The signals as the denoiser left them, for ForwardLight (Raytracing.cpp binds them).
Texture2D<float4> finishedDiffuse : register(t0, space3);
Texture2D<float4> finishedSpecular : register(t1, space3);

// u4, for rendering without a G-buffer of Unity's own lighting pass to put the raytraced
// light in: the light to add to the frame Unity drew, from the signals as the denoiser
// left them; a signal not traced reads as none. With a G-buffer (Unity's deferred path
// without Deferred's reflections pass): the diffuse light less KSP's flat ambient light,
// which Unity's shading added already, times the G-buffer's albedo, and the reflections.
// Without one (forward), the surface's colour is taken from the frame Unity drew, not
// guessed from its material: u7 holds per channel k = (diffuse - ambient) / (sun + ambient),
// the light Unity shaded with (the sun as the screen-space shadows let it through, our own
// sun visibility, faded out towards the shadow distance as Unity fades it, and the ambient
// light), and in w the largest channel of sun + ambient;
// the frame's colour c becomes c + min(c, w) k (ReDefinitionRtForwardLight.shader), its
// colour as Unity shaded it, capped where other lights brightened it. Unity's sun term is
// its shader's own: N.L, or Disney's diffuse term for its standard shader, and the sun's
// highlight the material's shader draws (UnityDirectTerms), which u4 takes back out of
// the relit colour, so that it stays as drawn; u4 holds it with the reflections. Unity's
// standard Fresnel term (FresnelLerp, with its surface reduction) weighs the reflections.
[numthreads(8, 8, 1)]
void ForwardLight(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= frameSize))
        return;
    float depth = depthTexture[id.xy];
    if (IsSky(depth))
    {
        output4[id.xy] = 0.0f;
        if (!HAS_GBUFFER)
            output7[id.xy] = 0.0f;
        return;
    }
    float3 position = ClipToWorld(PixelToNdc(id.xy), depth);
    SurfaceData surface = SurfaceAt(id.xy, position);
    // A surface the rays do not hold keeps Unity's shading: its colour and shine unknown,
    // light added to it would be a guess's.
    if (!surface.found)
    {
        output4[id.xy] = 0.0f;
        if (!HAS_GBUFFER)
            output7[id.xy] = 0.0f;
        return;
    }
    // The traced diffuse light has the occlusion in it already; Unity's ambient light has
    // none. Added to a frame Unity shaded in its colour space: the linear signals taken
    // there.
    float3 ambient = UnityAmbient(surface.normal);
    float3 diffuse = OutputColor(finishedDiffuse[id.xy].rgb);
    // Never below the player's share of KSP's night light, as the deferred reflections
    // shader mixes it (ReDefinitionDeferredReflections.shader).
    diffuse = lerp(nightLight.rgb, diffuse, saturate((length(diffuse) - length(nightLight.rgb)) * 100.0f));
    float3 light = 0.0f;
    if (HAS_GBUFFER)
        light = OutputColor(surface.albedo) * (diffuse - ambient);
    else
    {
        // Unity's sun term in its colour space: the light's colour, N.L and its shadow, which
        // takes the light's shadow strength (sunColor.w) of it -- KSP's sun keeps a quarter
        // in its shadows.
        float3 towards = normalize(sunDirection.xyz);
        float3 viewDirection = -normalize(position);
        float nl = saturate(dot(surface.normal, towards));
        float attenuation = 1.0f - saturate(sunColor.w) * (1.0f - saturate(output0[id.xy].x));
        // Faded out towards the shadow distance as Unity fades it (UnityComputeForwardShadows,
        // AutoLight.cginc): by the view depth or by the distance from the fade's centre.
        float viewDepth = mul(worldToClip, float4(position, 1.0f)).w;
        float fadeDistance = lerp(viewDepth, distance(position, shadowFadeCentre.xyz), shadowFadeCentre.w);
        attenuation = lerp(attenuation, 1.0f, saturate(fadeDistance * shadowFadeScale.x + shadowFadeScale.y));
        float3 highlight;
        float diffuseTerm = UnityDirectTerms(surface, towards, viewDirection, highlight);
        highlight *= sunColor.rgb * attenuation;
        float3 shaded = sunColor.rgb * diffuseTerm * attenuation + ambient;
        float3 k = (diffuse - ambient) / max(shaded, 1e-3f);
        // As Deferred shades the surface: its diffuse share of the light, sun and sky alike.
        k = surface.diffuseShare * (1.0f + k) - 1.0f;
        output7[id.xy] = float4(k, max(shaded.r, max(shaded.g, shaded.b)));
        // The highlight kept as Unity drew it: what u4 adds takes back its share of k.
        light -= highlight * k;
    }
    float3 view = -normalize(position);
    float nv = saturate(dot(surface.normal, view));
    float perceptual = 1.0f - surface.smoothness;
    float roughness = perceptual * perceptual;
    // Unity's surface reduction as BRDF1_Unity_PBS has it, in gamma space its approximation
    // of the linear one taken there (UnityStandardBRDF.cginc).
    float reduction = GAMMA_INPUTS ? 1.0f - 0.28f * roughness * perceptual : 1.0f / (roughness * roughness + 1.0f);
    float grazing = saturate(surface.smoothness + max(surface.specular.r, max(surface.specular.g, surface.specular.b)));
    float t = pow(1.0f - nv, 5.0f);
    float3 fresnel = lerp(surface.specular, grazing.xxx, t);
    light += OutputColor(finishedSpecular[id.xy].rgb) * fresnel * reduction;
    output4[id.xy] = float4(light, 1.0f);
}
