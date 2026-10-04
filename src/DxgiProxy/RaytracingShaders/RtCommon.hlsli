// What every raytracing pass shares: the bindings of the service's root signature
// (Raytracing.cpp, BuildPipelinesLocked), the frame's constants as RtFramePacket
// carries them, and the helpers to rebuild a pixel's position and to sample.
//
// Positions are camera-relative: the world's axes, the camera at the origin.

#ifndef REDEFINITION_RT_COMMON
#define REDEFINITION_RT_COMMON

cbuffer Frame : register(b0)
{
    row_major float4x4 clipToWorld;
    row_major float4x4 worldToClip;
    row_major float4x4 prevWorldToClip;
    float4 sunDirection;    // towards the sun; w: angular radius
    float4 sunColor;
    float4 skyColor;
    float4 params0;
    float4 params1;
    float4 bodySphere;   // the main body: centre relative to the camera, radius; w 0 for none
    float4 ambientUp;    // Unity's ambient light facing up, linear; w 1 in gamma space
    float4 ambientSide;  // facing sideways
    float4 ambientDown;  // facing down
    uint2 frameSize;
    uint frameIndex;
    uint frameFlags;
    // Bodies lit by the sun as lights (RtReflectors): towards each with its angular
    // radius in w, and its radiance, linear.
    float4 lightDirection0;
    float4 lightColor0;     // w: how far its shadow ray goes, negative for the main body
    float4 lightDirection1;
    float4 lightColor1;
    float4 lightParams;     // x: how many lights; y: 1 where Unity's colours are gamma-space values;
                            // z: how strongly water mirrors the sun's disc, 1 as it is; w: 1 where
                            // the main body's sea is KSP's own (no Scatterer), found by its level
    float4 nightLight;      // rgb: the least diffuse light of the forward light, as Unity's colours are
    float4 planetAlbedo;    // rgb: the main body's albedo, linear; w: the time in seconds, for the
                            // waves of KSP's own sea (StockWaves)
    float4 lightSurface0;   // rgb: the light's body lit by the star, linear: albedo times the star's colour
    float4 lightSurface1;
};

struct InstanceData
{
    uint meshSlot;
    uint material;
    uint flags;
    uint pad;
    float4 normalRow0;
    float4 normalRow1;
    float4 normalRow2;
};

struct MaterialData
{
    float4 albedo;
    float3 specular;
    float smoothness;
    float3 emission;
    int texture;
    // x: where > 0, the albedo is in Unity's colour space and the vertex colour times x
    // is added to it (Raytracing.h, RtMaterialDesc).
    float4 vertexColour;
};

struct VertexAttributes
{
    float3 normal;
    float2 uv;
    uint color;   // RGBA, a byte each, R lowest
};

float3 UnpackColor(uint c)
{
    return float3(c & 0xFFu, (c >> 8) & 0xFFu, (c >> 16) & 0xFFu) * (1.0f / 255.0f);
}

RaytracingAccelerationStructure scene : register(t0);
Texture2D<float> depthTexture : register(t1);
Texture2D<float4> gbuffer0 : register(t2);
Texture2D<float4> gbuffer1 : register(t3);
Texture2D<float4> gbuffer2 : register(t4);
Texture2D<float4> motionTexture : register(t5);
// The sky's radiance by direction, as a latitude-longitude image: x the angle around
// the vertical, y from straight up (0) to straight down (1).
Texture2D<float4> environmentTexture : register(t6);
Texture2DArray<float4> materialTextures : register(t11);
StructuredBuffer<InstanceData> instanceData : register(t9);
StructuredBuffer<MaterialData> materialData : register(t10);
ByteAddressBuffer meshIndices[] : register(t0, space1);
StructuredBuffer<VertexAttributes> meshAttributes[] : register(t0, space2);

RWTexture2D<float4> output0 : register(u0);
RWTexture2D<float4> output1 : register(u1);
RWTexture2D<float4> output2 : register(u2);
RWTexture2D<float4> output3 : register(u3);
RWTexture2D<float4> output4 : register(u4);
RWTexture2D<float4> output7 : register(u7);

SamplerState pointClamp : register(s0);
SamplerState linearClamp : register(s1);
SamplerState linearWrap : register(s2);

// The frame's parameters (RtFramePacket.params).
#define AO_DISTANCE params0.x
#define BOUNCES ((uint)params0.y)
#define DIFFUSE_DISTANCE params0.z
#define SPECULAR_DISTANCE params0.w
#define SAMPLE_CLAMP params1.x
#define HAS_ENVIRONMENT (params1.y > 0.5f)
#define MATERIAL_TEXTURES (params1.z > 0.5f)
#define HAS_GBUFFER (params1.w > 0.5f)
#define WATER_SURFACE ((frameFlags & 64u) != 0u)
#define LIGHT_COUNT ((uint)lightParams.x)
#define GAMMA_INPUTS (lightParams.y > 0.5f)
#define SUN_MIRROR (lightParams.z)
#define STOCK_WATER (lightParams.w > 0.5f)

// The light transport is linear: sums and averages of light -- a hemisphere of sky and
// ground, samples -- hold only there. Where Unity shades in gamma space, as KSP does,
// the colours it hands over are gamma-space values, taken to linear here with the
// curve Unity converts by (GammaToLinearSpace, UnityCG.cginc); the materials' colours
// and small textures come linear from the mod (RtScene). The signals stay linear
// through the denoiser and are taken back where Unity's lighting reads them.
float3 InputColor(float3 c)
{
    if (!GAMMA_INPUTS)
        return c;
    c = max(c, 0.0f);
    return c * (c * (c * 0.305306011f + 0.682171111f) + 0.012522878f);
}

// Unity's LinearToGammaSpace (UnityCG.cginc), for a signal read in gamma space.
float3 OutputColor(float3 c)
{
    if (!GAMMA_INPUTS)
        return c;
    c = max(c, 0.0f);
    return max(1.055f * pow(c, 0.416666667f) - 0.055f, 0.0f);
}

static const float kPi = 3.14159265358979f;

// The camera-relative position a clip-space point stands for.
float3 ClipToWorld(float2 ndc, float depth)
{
    float4 p = mul(clipToWorld, float4(ndc, depth, 1.0f));
    return p.xyz / p.w;
}

// A pixel's centre in clip space: x to the right, y up, the first row at +1.
float2 PixelToNdc(uint2 pixel)
{
    float2 uv = (float2(pixel) + 0.5f) / float2(frameSize);
    return float2(uv.x * 2.0f - 1.0f, 1.0f - uv.y * 2.0f);
}

// Nothing was drawn where depth is 0: the far plane, reversed.
bool IsSky(float depth)
{
    return depth <= 0.0f;
}

float3 DecodeNormal(float4 encoded)
{
    return normalize(encoded.xyz * 2.0f - 1.0f);
}

// A pixel's normal from the depth: from the positions of the neighbours on the nearer
// side, facing the camera.
float3 DepthNormal(uint2 pixel, float3 position)
{
    int2 size = int2(frameSize) - 1;
    int2 p = int2(pixel);
    float3 left = ClipToWorld(PixelToNdc(uint2(max(p.x - 1, 0), p.y)), depthTexture[uint2(max(p.x - 1, 0), p.y)]);
    float3 right = ClipToWorld(PixelToNdc(uint2(min(p.x + 1, size.x), p.y)), depthTexture[uint2(min(p.x + 1, size.x), p.y)]);
    float3 up = ClipToWorld(PixelToNdc(uint2(p.x, max(p.y - 1, 0))), depthTexture[uint2(p.x, max(p.y - 1, 0))]);
    float3 down = ClipToWorld(PixelToNdc(uint2(p.x, min(p.y + 1, size.y))), depthTexture[uint2(p.x, min(p.y + 1, size.y))]);
    float3 dx = abs(length(left) - length(position)) < abs(length(right) - length(position)) ? position - left : right - position;
    float3 dy = abs(length(up) - length(position)) < abs(length(down) - length(position)) ? position - up : down - position;
    float3 n = normalize(cross(dy, dx));
    return dot(n, position) > 0.0f ? -n : n;
}

// A per-pixel, per-frame random stream: PCG hashes of the pixel, the frame and a
// counter.
uint Pcg(uint value)
{
    uint state = value * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

struct Random
{
    uint state;
};

Random MakeRandom(uint2 pixel, uint frame, uint stream)
{
    Random r;
    r.state = Pcg(pixel.x + Pcg(pixel.y + Pcg(frame * 16u + stream)));
    return r;
}

float NextFloat(inout Random r)
{
    r.state = Pcg(r.state);
    return float(r.state >> 8) * (1.0f / 16777216.0f);
}

// Two directions at right angles to n.
void Basis(float3 n, out float3 t, out float3 b)
{
    float s = n.z >= 0.0f ? 1.0f : -1.0f;
    float a = -1.0f / (s + n.z);
    float c = n.x * n.y * a;
    t = float3(1.0f + s * n.x * n.x * a, s * c, -s * n.x);
    b = float3(c, s + n.y * n.y * a, -n.y);
}

// A direction in a cone of the given half angle around axis, uniform over its
// solid angle.
float3 SampleCone(float3 axis, float halfAngle, float2 u)
{
    if (halfAngle <= 0.0f)
        return axis;
    float cosMax = cos(halfAngle);
    float cosTheta = 1.0f - u.x * (1.0f - cosMax);
    float sinTheta = sqrt(saturate(1.0f - cosTheta * cosTheta));
    float phi = 2.0f * kPi * u.y;
    float3 t, b;
    Basis(axis, t, b);
    return normalize(t * (cos(phi) * sinTheta) + b * (sin(phi) * sinTheta) + axis * cosTheta);
}

// Where a ray from a surface starts: off the surface along its normal, by more the
// further the point is from the camera, as depth and position lose precision there.
float3 RayOrigin(float3 position, float3 normal)
{
    float distance = length(position);
    return position + normal * (0.01f + distance * 0.0005f);
}

// Whether the main body, which is not in the scene the rays see, stands in the way: its
// sphere, tested by formula. What stands on it near the camera is in the scene.
bool BodyBlocks(float3 origin, float3 direction)
{
    if (bodySphere.w <= 0.0f)
        return false;
    float3 toCentre = bodySphere.xyz - origin;
    float along = dot(toCentre, direction);
    if (along <= 0.0f)
        return false;
    float closest2 = dot(toCentre, toCentre) - along * along;
    // Slightly inside the radius: the ground itself lies on the sphere.
    float radius = bodySphere.w * 0.999f;
    return closest2 < radius * radius;
}

// How far a ray from a pixel's surface passes through terrain: Parallax displaces the
// drawn terrain on the GPU by up to some tenths of a metre near the camera
// (ParallaxUtils.cginc), off the mesh the rays see, which would otherwise shade the
// drawn surface where it lies below. Terrain is the only non-opaque geometry
// (Raytracing.h, kRtInstanceTerrain).
static const float kTerrainSkip = 1.0f;

// Whether a candidate hit of the non-opaque geometry counts: terrain only past
// terrainSkip; a cut-out surface where its texture covers the point. The alpha of a
// cut-out material's small texture is how much of each texel its full-size texture
// covers at the cut-out's threshold (RtScene.ReadTexture), as a fine cut-out -- a blade
// of grass -- is far finer than a texel; the ray stops there with that chance, so on
// average the surface lets through what its cut-out does (Raytracing.h,
// kRtInstanceCutout). A cut-out surface whose texture is not read yet lets every ray
// through: an opaque card would shade the ground under it as a square.
bool CandidateCounts(uint instanceId, uint primitive, float2 bary, float t, float terrainSkip, float3 origin)
{
    InstanceData instance = instanceData[instanceId];
    if ((instance.flags & 1u) != 0u && t < terrainSkip)
        return false;
    if ((instance.flags & 2u) == 0u)
        return true;
    MaterialData material = materialData[instance.material];
    if (!MATERIAL_TEXTURES || material.texture < 0)
        return false;
    uint slot = instance.meshSlot;
    uint3 index = meshIndices[NonUniformResourceIndex(slot)].Load3(primitive * 12u);
    float2 uv = meshAttributes[NonUniformResourceIndex(slot)][index.x].uv * (1.0f - bary.x - bary.y)
              + meshAttributes[NonUniformResourceIndex(slot)][index.y].uv * bary.x
              + meshAttributes[NonUniformResourceIndex(slot)][index.z].uv * bary.y;
    float coverage = materialTextures.SampleLevel(linearWrap, float3(uv, float(material.texture)), 0.0f).a;
    // Each ray its own chance: rays side by side meet a surface at the same distance.
    uint seed = Pcg(primitive ^ Pcg(asuint(t) ^ Pcg(asuint(origin.x) ^ Pcg(asuint(origin.z) ^ Pcg(frameIndex)))));
    return float(seed >> 8) * (1.0f / 16777216.0f) < coverage;
}

// True where nothing lies between origin and origin + direction * maxDistance; terrain
// nearer than terrainSkip does not count.
bool Unoccluded(float3 origin, float3 direction, float maxDistance, float terrainSkip = 0.0f)
{
    if (maxDistance > 1.0e6f && BodyBlocks(origin, direction))
        return false;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0f;
    ray.TMax = maxDistance;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    query.TraceRayInline(scene, RAY_FLAG_NONE, 0xFF, ray);
    while (query.Proceed())
        if (CandidateCounts(query.CandidateInstanceID(), query.CandidatePrimitiveIndex(), query.CandidateTriangleBarycentrics(),
                            query.CandidateTriangleRayT(), terrainSkip, query.WorldRayOrigin()))
            query.CommitNonOpaqueTriangleHit();
    return query.CommittedStatus() == COMMITTED_NOTHING;
}

// The sky's radiance in a direction: the environment where one is given, else the
// sky's colour.
float3 Sky(float3 direction)
{
    if (!HAS_ENVIRONMENT)
        return InputColor(skyColor.rgb);
    float u = atan2(direction.x, direction.z) * (0.5f / kPi) + 0.5f;
    float v = acos(clamp(direction.y, -1.0f, 1.0f)) / kPi;
    return InputColor(environmentTexture.SampleLevel(linearWrap, float2(u, v), 0.0f).rgb);
}

// A direction over the hemisphere around n, by the cosine of its angle to n.
float3 SampleCosine(float3 n, float2 u)
{
    float r = sqrt(u.x);
    float phi = 2.0f * kPi * u.y;
    float3 t, b;
    Basis(n, t, b);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(saturate(1.0f - u.x)));
}

// A microfacet normal of the GGX distribution as the view sees it (Heitz 2018,
// "Sampling the GGX Distribution of Visible Normals"), in the frame where n is z.
float3 SampleGgxVisible(float3 viewLocal, float alpha, float2 u)
{
    float3 vh = normalize(float3(alpha * viewLocal.x, alpha * viewLocal.y, viewLocal.z));
    float lensq = vh.x * vh.x + vh.y * vh.y;
    float3 t1 = lensq > 0.0f ? float3(-vh.y, vh.x, 0.0f) * rsqrt(lensq) : float3(1.0f, 0.0f, 0.0f);
    float3 t2 = cross(vh, t1);
    float r = sqrt(u.x);
    float phi = 2.0f * kPi * u.y;
    float p1 = r * cos(phi);
    float p2 = r * sin(phi);
    float s = 0.5f * (1.0f + vh.z);
    p2 = (1.0f - s) * sqrt(saturate(1.0f - p1 * p1)) + s * p2;
    float3 nh = p1 * t1 + p2 * t2 + sqrt(saturate(1.0f - p1 * p1 - p2 * p2)) * vh;
    return normalize(float3(alpha * nh.x, alpha * nh.y, max(0.0f, nh.z)));
}

// Unity's roughness from smoothness: the perceptual roughness squared.
float SmoothnessToAlpha(float smoothness)
{
    float perceptual = 1.0f - smoothness;
    return max(perceptual * perceptual, 0.002f);
}

// Where a ray meets the scene: the surface's point, its normal turned towards the
// ray, its texture coordinate and material.
struct Hit
{
    bool found;
    float distance;
    float3 position;
    float3 normal;
    float2 uv;
    float3 color;
    uint material;
};

Hit TraceClosest(float3 origin, float3 direction, float maxDistance, float terrainSkip = 0.0f)
{
    Hit hit;
    hit.found = false;
    hit.distance = maxDistance;
    hit.position = 0.0f;
    hit.normal = float3(0.0f, 1.0f, 0.0f);
    hit.uv = 0.0f;
    hit.color = 1.0f;
    hit.material = 0u;

    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0f;
    ray.TMax = maxDistance;
    RayQuery<RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    query.TraceRayInline(scene, RAY_FLAG_NONE, 0xFF, ray);
    while (query.Proceed())
        if (CandidateCounts(query.CandidateInstanceID(), query.CandidatePrimitiveIndex(), query.CandidateTriangleBarycentrics(),
                            query.CandidateTriangleRayT(), terrainSkip, query.WorldRayOrigin()))
            query.CommitNonOpaqueTriangleHit();
    if (query.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
        return hit;

    InstanceData instance = instanceData[query.CommittedInstanceID()];
    uint slot = instance.meshSlot;
    uint3 index = meshIndices[NonUniformResourceIndex(slot)].Load3(query.CommittedPrimitiveIndex() * 12u);
    VertexAttributes a0 = meshAttributes[NonUniformResourceIndex(slot)][index.x];
    VertexAttributes a1 = meshAttributes[NonUniformResourceIndex(slot)][index.y];
    VertexAttributes a2 = meshAttributes[NonUniformResourceIndex(slot)][index.z];
    float2 bary = query.CommittedTriangleBarycentrics();
    float w = 1.0f - bary.x - bary.y;
    float3 objectNormal = a0.normal * w + a1.normal * bary.x + a2.normal * bary.y;
    float3x3 normalMatrix = float3x3(instance.normalRow0.xyz, instance.normalRow1.xyz, instance.normalRow2.xyz);
    float3 normal = mul(normalMatrix, objectNormal);
    float length2 = dot(normal, normal);
    normal = length2 > 1e-12f ? normal * rsqrt(length2) : -direction;
    if (dot(normal, direction) > 0.0f)
        normal = -normal;

    hit.found = true;
    hit.distance = query.CommittedRayT();
    hit.position = origin + direction * hit.distance;
    hit.normal = normal;
    hit.uv = a0.uv * w + a1.uv * bary.x + a2.uv * bary.y;
    hit.color = UnpackColor(a0.color) * w + UnpackColor(a1.color) * bary.x + UnpackColor(a2.color) * bary.y;
    hit.material = instance.material;
    return hit;
}

// A material's albedo at a point: its colour times its small texture.
float3 Albedo(MaterialData material, float2 uv, float3 color)
{
    if (material.vertexColour.x > 0.0f)
        return InputColor(material.albedo.rgb + color * material.vertexColour.x);
    float3 albedo = material.albedo.rgb;
    if (MATERIAL_TEXTURES && material.texture >= 0)
        albedo *= materialTextures.SampleLevel(linearWrap, float3(uv, float(material.texture)), 0.0f).rgb;
    return albedo;
}

// What a diffuse surface point sends back of the sun, in Unity's units, where a
// light's colour times N.L is what a white surface returns: where it sees the sun.
float3 SunAt(Hit hit, float3 albedo, inout Random random)
{
    float3 towardsSun = normalize(sunDirection.xyz);
    float nl = dot(hit.normal, towardsSun);
    if (nl <= 0.0f || all(sunColor.rgb <= 0.0f))
        return 0.0f;
    float2 u = float2(NextFloat(random), NextFloat(random));
    float3 direction = SampleCone(towardsSun, sunDirection.w, u);
    if (!Unoccluded(RayOrigin(hit.position, hit.normal), direction, 1.0e7f))
        return 0.0f;
    return albedo * InputColor(sunColor.rgb) * nl;
}

// Where a ray that missed the scene meets the main body's sphere: the light the sphere
// sends back there, its albedo times the sun's and the moons' light on it, unshaded but
// by the sphere itself. Below the horizon the ground is there -- past the terrain the
// scene holds, over the ocean, under the metre of terrain a pixel's rays pass through --
// and the sky's capture would show it as drawn, lit by KSP's ambient light.
bool PlanetRadiance(float3 origin, float3 direction, out float3 radiance)
{
    radiance = 0.0f;
    if (!BodyBlocks(origin, direction))
        return false;
    float3 toCentre = bodySphere.xyz - origin;
    float along = dot(toCentre, direction);
    float closest2 = dot(toCentre, toCentre) - along * along;
    float t = along - sqrt(max(bodySphere.w * bodySphere.w - closest2, 0.0f));
    float3 normal = normalize(origin + direction * max(t, 0.0f) - bodySphere.xyz);
    float3 light = InputColor(sunColor.rgb) * saturate(dot(normal, normalize(sunDirection.xyz)));
    if (LIGHT_COUNT > 0u)
        light += lightColor0.rgb * saturate(dot(normal, normalize(lightDirection0.xyz)));
    if (LIGHT_COUNT > 1u)
        light += lightColor1.rgb * saturate(dot(normal, normalize(lightDirection1.xyz)));
    radiance = planetAlbedo.rgb * light;
    return true;
}

// A reflected ray that met nothing: the disc of a body the sun lights (RtReflectors)
// where the ray meets it, the body's surface there as the sun lights it -- its albedo
// times the star's light by N.L on its sphere, so a crescent shows as one. The diffuse
// light takes the bodies as lights (ReflectedLightAt), and not this.
float3 BodyDiscs(float3 direction)
{
    float3 total = 0.0f;
    float3 towardsSun = normalize(sunDirection.xyz);
    for (uint i = 0u; i < min(LIGHT_COUNT, 2u); ++i)
    {
        float4 towards = i == 0u ? lightDirection0 : lightDirection1;
        float3 surface = i == 0u ? lightSurface0.rgb : lightSurface1.rgb;
        // The body at distance 1: its sphere's radius the sine of its angular radius.
        float3 centre = normalize(towards.xyz);
        float radius = sin(towards.w);
        float along = dot(direction, centre);
        float closest2 = 1.0f - along * along;
        if (along <= 0.0f || closest2 >= radius * radius)
            continue;
        float3 hit = direction * (along - sqrt(radius * radius - closest2));
        float3 normal = normalize(hit - centre);
        total += surface * saturate(dot(normal, towardsSun));
    }
    return total;
}

// The specular light a disc of angular `radius` towards `towards` gives water of
// Beckmann roughness `m` (the root mean square of its slopes), per unit of the disc's
// illuminance. Beckmann's distribution, not GGX's: a sea's slopes are near Gaussian (Cox
// and Munk), as Bruneton's ocean and Scatterer take them, and GGX's long tails let the
// glints of slopes four and five times the usual mirror a disc as bright as the moon's
// far to its sides -- measured at sea, 2026-10-04, a moon's path 2.5 times as wide as its
// waves allow. The representative point --
// the direction on the disc closest to the reflection vector, after Frostbite's sun as a
// disc area light (Lagarde and de Rousiers, "Moving Frostbite to Physically Based
// Rendering", 2014, listing 6) -- lit as a punctual light, with the energy the wider
// lobe adds taken back by (alpha / alpha')^2, alpha' = alpha + radius / 3 (Karis, "Real
// Shading in Unreal Engine 4", 2013, equations 10 and 14), and never brighter than the
// disc itself, which that approximation overshoots at grazing angles. Without the
// Fresnel term, which the reader applies. Smooth water mirrors the disc as it is; rough
// water spreads it into a highlight stretched at grazing angles, the sea's long path.
float BeckmannG1(float cosine, float m)
{
    float c = cosine / (m * sqrt(max(1.0f - cosine * cosine, 1e-8f)));
    return c < 1.6f ? (3.535f * c + 2.181f * c * c) / (1.0f + 2.276f * c + 2.577f * c * c) : 1.0f;
}

float DiscSpecular(float3 normal, float3 view, float3 towards, float radius, float m)
{
    float3 r = reflect(-view, normal);
    float sinRadius = sin(radius);
    float cosRadius = cos(radius);
    float along = dot(towards, r);
    float3 aside = r - along * towards;
    float3 l = along < cosRadius && dot(aside, aside) > 1e-12f ? normalize(cosRadius * towards + normalize(aside) * sinRadius) : r;
    float nl = dot(normal, l);
    if (nl <= 0.0f)
        return 0.0f;
    float nv = max(dot(normal, view), 1e-4f);
    float3 h = normalize(view + l);
    float nh = max(dot(normal, h), 1e-4f);
    float a = max(m, 1e-3f);
    float a2 = a * a;
    float nh2 = nh * nh;
    float D = exp(-(1.0f - nh2) / (nh2 * a2)) / (kPi * a2 * nh2 * nh2);
    // Smith's shadowing for Beckmann's distribution, per direction (Walter et al.,
    // "Microfacet Models for Refraction through Rough Surfaces", 2007, equation 27).
    float visibility = BeckmannG1(nv, a) * BeckmannG1(nl, a) / (4.0f * nl * nv);
    float wide = saturate(a + radius / 3.0f);
    return min(D * visibility * nl * (a * a) / (wide * wide), 1.0f / (kPi * sinRadius * sinRadius));
}

// KSP's own sea (STOCK_WATER), drawn into Unity's G-buffer by its ocean shader with
// normals from its normal map: a pixel within kStockWaterBand metres of the main
// body's sea level whose normal faces up, as the sea's does and a hull floating in it
// mostly not. Its waves and roughness are ReDefinition's (StockWaves); kStockWaterSmooth
// the least roughness, of ripples shorter than the shortest wave.
static const float kStockWaterBand = 2.0f;
static const float kStockWaterSmooth = 0.004f;

bool IsStockWater(float3 position, float3 normal)
{
    float3 up = position - bodySphere.xyz;
    float level = length(up);
    return STOCK_WATER && bodySphere.w > 0.0f && abs(level - bodySphere.w) < kStockWaterBand
           && dot(normal, up / level) > 0.8f;
}

// KSP's own sea's waves, which its normal map barely carries. The long waves, 40 m down
// to 2 m, as kStockWaves sine waves spread about a wind direction and across it, each of
// slope kStockWaveSlope, running as deep water's waves do (omega^2 = g k); the ripples,
// 2 m down to 5 cm, as kStockRippleOctaves octaves of gradient noise, short-crested as
// ripples are -- sine waves that short would draw straight crests across the sea, a weave
// -- each of slope deviation kStockRippleSlope, drifting with the wind at their waves'
// speed. Together a slope variance of about 0.025, Scatterer's ocean's (measured at sea:
// its variance as Beckmann's m^2, m = 2 alpha, alpha 0.073 to 0.1). The time is
// planetAlbedo.w, in seconds. Fixed to the body: positions from its centre along the
// tangent plane under the camera. A wave is drawn as
// far as a pixel resolves it -- its length against the water the pixel covers along the
// wave's own direction (a ripple octave: along the view, the most), across the view and,
// stretched by the grazing angle, along it -- and what it loses goes into the roughness,
// as Bruneton's ocean and LEAN mapping move the slopes a pixel cannot show into the BRDF:
// the glints of the ripples near by, a smooth glow far off, no flicker. xyz the normal,
// w the Beckmann roughness of the waves no pixel shows, the root of their slopes' mean
// square.
static const uint kStockWaves = 24u;
static const float kStockWaveSlope = 0.0254f;
static const uint kStockRippleOctaves = 6u;
static const float kStockRippleSlope = 0.055f;

// A cell's random unit gradient, from its integer coordinates (an integer hash, exact at
// any distance from the origin, as sin-based hashes are not).
float2 RippleGradient(int2 cell, uint octave)
{
    uint h = uint(cell.x) * 1597334677u ^ uint(cell.y) * 3812015801u ^ octave * 2654435761u;
    h = (h ^ (h >> 16)) * 2246822519u;
    h ^= h >> 13;
    float angle = float(h & 0xffffu) * (2.0f * kPi / 65536.0f);
    return float2(cos(angle), sin(angle));
}

// Gradient noise at p and its derivative (quintic interpolation): x the value, yz d/dp.
float3 RippleNoise(float2 p, uint octave)
{
    float2 cellF = floor(p);
    int2 cell = int2(cellF);
    float2 f = p - cellF;
    float2 w = f * f * f * (f * (f * 6.0f - 15.0f) + 10.0f);
    float2 dw = 30.0f * f * f * (f * (f - 2.0f) + 1.0f);
    float2 ga = RippleGradient(cell, octave), gb = RippleGradient(cell + int2(1, 0), octave);
    float2 gc = RippleGradient(cell + int2(0, 1), octave), gd = RippleGradient(cell + int2(1, 1), octave);
    float va = dot(ga, f), vb = dot(gb, f - float2(1, 0)), vc = dot(gc, f - float2(0, 1)), vd = dot(gd, f - float2(1, 1));
    float value = va + w.x * (vb - va) + w.y * (vc - va) + w.x * w.y * (va - vb - vc + vd);
    float2 derivative = ga + w.x * (gb - ga) + w.y * (gc - ga) + w.x * w.y * (ga - gb - gc + gd)
                        + dw * (float2(vb - va, vc - va) + (va - vb - vc + vd) * w.yx);
    return float3(value, derivative);
}

// The sea under a pixel: the tangent plane under the camera, where the sea in view lies
// (positions fixed to the body, in metres along it), the view's direction along it, and
// the water the pixel covers across the view and, stretched by the grazing angle, along it.
struct SeaFrame
{
    float3 up, east, north;
    float2 at, along;
    float acrossFootprint, alongFootprint;
};

SeaFrame MakeSeaFrame(float3 position, float acrossFootprint)
{
    SeaFrame f;
    f.up = normalize(position - bodySphere.xyz);
    float3 below = normalize(-bodySphere.xyz);
    float3 east = cross(float3(0.0f, 1.0f, 0.0f), below);
    f.east = dot(east, east) > 1e-6f ? normalize(east) : float3(1.0f, 0.0f, 0.0f);
    f.north = cross(below, f.east);
    float3 fromCentre = position - bodySphere.xyz;
    f.at = float2(dot(fromCentre, f.east), dot(fromCentre, f.north));
    float3 towardsPixel = normalize(position);
    float2 along = float2(dot(towardsPixel, f.east), dot(towardsPixel, f.north));
    f.along = dot(along, along) > 1e-8f ? normalize(along) : float2(1.0f, 0.0f);
    f.acrossFootprint = acrossFootprint;
    f.alongFootprint = acrossFootprint / max(abs(dot(towardsPixel, f.up)), 0.02f);
    return f;
}

// The ripples' slopes along the frame's east and north (xy) and the slope variance of the
// octaves no pixel shows (z), each octave of slope deviation kStockRippleSlope times `scale`.
float3 SeaRipples(SeaFrame f, float scale)
{
    const float2 wind = float2(cos(0.35f), sin(0.35f));
    float time = planetAlbedo.w;
    float slopeDeviation = kStockRippleSlope * scale;
    float2 slope = 0.0f;
    float lost = 0.0f;
    for (uint octave = 0u; octave < kStockRippleOctaves; ++octave)
    {
        // Each octave half the last one's length, from 2 m: a noise cell is about a
        // ripple's length.
        float wavelength = 2.0f * exp2(-float(octave) * 0.97f);
        float shown = saturate(wavelength / (2.0f * max(f.alongFootprint, 1e-4f)) - 1.0f);
        lost += slopeDeviation * slopeDeviation * (1.0f - shown * shown);
        if (shown <= 0.0f)
            continue;
        float frequency = 1.0f / wavelength;
        float speed = sqrt(9.81f * wavelength / (2.0f * kPi));
        float2 p = (f.at - wind * speed * time) * frequency + float2(octave * 37.0f, octave * 11.0f);
        // The noise's derivative is about 1.3 in deviation per unit of p: to a slope of
        // slopeDeviation.
        float2 derivative = RippleNoise(p, octave).yz * frequency;
        slope += derivative * (slopeDeviation * shown * wavelength / 1.3f);
    }
    return float3(slope, lost);
}

float4 StockWaves(float3 position, float acrossFootprint)
{
    SeaFrame f = MakeSeaFrame(position, acrossFootprint);
    float time = planetAlbedo.w;
    float2 slope = 0.0f;
    float lost = 0.0f;
    for (uint i = 0u; i < kStockWaves; ++i)
    {
        // Lengths spread over the range, each at random within its step, and directions
        // at random: no two waves in step with each other.
        float3 dice = frac(sin(float3(i, i + 17u, i + 41u) * float3(12.9898f, 78.233f, 37.719f)) * 43758.5453f);
        float t = saturate((float(i) + dice.x) / float(kStockWaves));
        float wavelength = 40.0f * pow(2.0f / 40.0f, t);
        // Directions about the wind, most within 50 degrees, some across it.
        float u = dice.y * 2.0f - 1.0f;
        float angle = 0.35f + u * abs(u) * 2.2f;
        float2 direction = float2(cos(angle), sin(angle));
        float alongPart = dot(direction, f.along);
        float footprint = sqrt(alongPart * alongPart * f.alongFootprint * f.alongFootprint
                               + (1.0f - alongPart * alongPart) * f.acrossFootprint * f.acrossFootprint);
        float shown = saturate(wavelength / (2.0f * max(footprint, 1e-4f)) - 1.0f);
        lost += 0.5f * kStockWaveSlope * kStockWaveSlope * (1.0f - shown * shown);
        if (shown <= 0.0f)
            continue;
        float k = 2.0f * kPi / wavelength;
        float phase = dice.z * 2.0f * kPi - sqrt(9.81f * k) * time;
        slope += direction * (kStockWaveSlope * shown * cos(k * dot(direction, f.at) + phase));
    }
    float3 ripples = SeaRipples(f, 1.0f);
    slope += ripples.xy;
    lost += ripples.z;
    return float4(normalize(f.up - slope.x * f.east - slope.y * f.north), sqrt(lost));
}

// Scatterer's ocean with the same ripples on its own normals: its waves are geometry and
// normal maps down to some metres, the rest it keeps as a variance its glint spreads the
// sun over (Bruneton's BRDF), which mirrors a disc as a flat patch. The ripples carry that
// variance (`m`, Beckmann's, from it) as normals a pixel shows as glints, and the
// roughness only what no pixel shows. xyz the normal, w the roughness.
float4 ScattererRipples(float3 position, float3 normal, float m, float acrossFootprint)
{
    SeaFrame f = MakeSeaFrame(position, acrossFootprint);
    float scale = m / sqrt(float(kStockRippleOctaves) * kStockRippleSlope * kStockRippleSlope);
    float3 ripples = SeaRipples(f, scale);
    return float4(normalize(normal - ripples.x * f.east - ripples.y * f.north), sqrt(ripples.z));
}

// The same for water of roughness `roughness` (GGX's alpha) as it is.
// The sun's and the moons' discs mirrored by water of Beckmann roughness `m`
// (DiscSpecular), with nothing in the way towards the body: the sun by its light at the
// player's strength (Sun reflection); a moon by its disc's light, its surface as the sun
// lights it averaged over a Lambert sphere at its phase (2/3 of the full disc's centre at
// full phase) times its solid angle.
float3 BodiesMirroredAt(float3 position, float3 normal, float3 view, float m, float terrainSkip)
{
    float3 total = 0.0f;
    float3 towardsSun = normalize(sunDirection.xyz);
    if (SUN_MIRROR > 0.0f && dot(normal, towardsSun) > 0.0f)
    {
        float sunRadius = max(sunDirection.w, 0.0047f);
        float specular = DiscSpecular(normal, view, towardsSun, sunRadius, m);
        if (specular > 0.0f && Unoccluded(RayOrigin(position, normal), towardsSun, 1.0e7f, terrainSkip))
            total += InputColor(sunColor.rgb) * specular * SUN_MIRROR;
    }
    for (uint i = 0u; i < min(LIGHT_COUNT, 2u); ++i)
    {
        float4 towards = i == 0u ? lightDirection0 : lightDirection1;
        float3 surface = i == 0u ? lightSurface0.rgb : lightSurface1.rgb;
        if (all(surface <= 0.0f))
            continue;
        float3 l = normalize(towards.xyz);
        if (dot(normal, l) <= 0.0f)
            continue;
        float specular = DiscSpecular(normal, view, l, towards.w, m);
        if (specular <= 0.0f)
            continue;
        float cosPhase = clamp(dot(towardsSun, -l), -1.0f, 1.0f);
        float phaseAngle = acos(cosPhase);
        float phase = (sin(phaseAngle) + (kPi - phaseAngle) * cosPhase) / kPi;
        if (!Unoccluded(RayOrigin(position, normal), l, 1.0e7f, terrainSkip))
            continue;
        float solidAngle = kPi * sin(towards.w) * sin(towards.w);
        total += surface * (2.0f / 3.0f) * phase * solidAngle * specular;
    }
    return total;
}


// The light of the bodies the sun lights (RtReflectors) arriving at a surface point:
// one of them a sample, chosen by its brightness, towards its disc and in its shadow --
// the main body, the scene -- as the sun's, weighed back by the chance it was chosen;
// a white surface returns this times N.L, as for the sun.
float3 ReflectedLightAt(float3 position, float3 normal, inout Random random, float terrainSkip = 0.0f)
{
    uint count = LIGHT_COUNT;
    if (count == 0u)
        return 0.0f;
    float w0 = dot(lightColor0.rgb, float3(0.2126f, 0.7152f, 0.0722f));
    float w1 = count > 1u ? dot(lightColor1.rgb, float3(0.2126f, 0.7152f, 0.0722f)) : 0.0f;
    if (w0 + w1 <= 0.0f)
        return 0.0f;
    bool first = NextFloat(random) * (w0 + w1) < w0;
    float4 towards = first ? lightDirection0 : lightDirection1;
    float3 colour = first ? lightColor0.rgb : lightColor1.rgb;
    // How far the shadow ray goes: to the body's surface. Negative for the main body
    // itself, the light of the ground below an orbit: its own sphere does not shade it,
    // and the ray stays within the scene's reach, short of the body test.
    float reach = first ? lightColor0.w : lightColor1.w;
    float maxDistance = reach < 0.0f ? min(-reach, 1.0e6f) : max(reach, 1.0e6f + 1.0f);
    float chance = (first ? w0 : w1) / (w0 + w1);
    float3 axis = normalize(towards.xyz);
    float nl = dot(normal, axis);
    if (nl <= 0.0f)
        return 0.0f;
    float2 u = float2(NextFloat(random), NextFloat(random));
    float3 direction = SampleCone(axis, towards.w, u);
    if (!Unoccluded(RayOrigin(position, normal), direction, maxDistance, terrainSkip))
        return 0.0f;
    return colour * nl / chance;
}

// The radiance arriving along a ray, from what it hits and, bounce by bounce, what
// lights that; the sky where it escapes.
float3 Radiance(float3 origin, float3 direction, float maxDistance, uint bounces, inout Random random,
                out float firstDistance, float firstTerrainSkip = 0.0f)
{
    float3 total = 0.0f;
    float3 weight = 1.0f;
    firstDistance = maxDistance;
    for (uint bounce = 0u; bounce <= bounces; ++bounce)
    {
        Hit hit = TraceClosest(origin, direction, bounce == 0u ? maxDistance : 1.0e7f, bounce == 0u ? firstTerrainSkip : 0.0f);
        if (bounce == 0u)
            firstDistance = hit.distance;
        if (!hit.found)
        {
            float3 ground;
            total += weight * (PlanetRadiance(origin, direction, ground) ? ground : Sky(direction));
            break;
        }
        MaterialData material = materialData[hit.material];
        float3 albedo = Albedo(material, hit.uv, hit.color);
        total += weight * (SunAt(hit, albedo, random) + albedo * ReflectedLightAt(hit.position, hit.normal, random)
                           + material.emission);
        if (bounce == bounces)
            break;
        weight *= albedo;
        // Past the second bounce, a path goes on by the chance its weight gives it.
        if (bounce >= 2u)
        {
            float keep = saturate(max(weight.r, max(weight.g, weight.b)));
            if (NextFloat(random) >= keep)
                break;
            weight /= max(keep, 1e-4f);
        }
        origin = RayOrigin(hit.position, hit.normal);
        direction = SampleCosine(hit.normal, float2(NextFloat(random), NextFloat(random)));
    }
    float luminance = dot(total, float3(0.2126f, 0.7152f, 0.0722f));
    if (SAMPLE_CLAMP > 0.0f && luminance > SAMPLE_CLAMP)
        total *= SAMPLE_CLAMP / luminance;
    return total;
}

// What a pixel's surface is, for the passes: from the G-buffer where Unity's deferred
// lighting filled one; without (forward rendering), from the camera's ray to the point
// the depth gives, which lands on the triangle Unity drew there.
struct SurfaceData
{
    float3 normal;
    float smoothness;
    float3 albedo;
    float3 specular;
};

SurfaceData SurfaceAt(uint2 pixel, float3 position)
{
    SurfaceData surface;
    if (HAS_GBUFFER)
    {
        surface.normal = DecodeNormal(gbuffer2[pixel]);
        float4 g1 = gbuffer1[pixel];
        surface.smoothness = g1.a;
        surface.specular = g1.rgb;
        surface.albedo = InputColor(gbuffer0[pixel].rgb);
        return surface;
    }
    float distance = length(position);
    Hit hit = TraceClosest(float3(0.0f, 0.0f, 0.0f), position / max(distance, 1e-6f), distance * 1.01f + 0.05f);
    if (hit.found && abs(hit.distance - distance) < distance * 0.005f + 0.05f)
    {
        MaterialData material = materialData[hit.material];
        surface.normal = hit.normal;
        surface.smoothness = material.smoothness;
        surface.specular = material.specular;
        surface.albedo = Albedo(material, hit.uv, hit.color);
        return surface;
    }
    surface.normal = DepthNormal(pixel, position);
    surface.smoothness = 0.2f;
    surface.specular = 0.04f;
    surface.albedo = 0.5f;
    return surface;
}

float3 SurfaceNormal(uint2 pixel, float3 position)
{
    return HAS_GBUFFER ? DecodeNormal(gbuffer2[pixel]) : SurfaceAt(pixel, position).normal;
}

// The ambient light Unity's shading gave a surface of this normal: Unity's ambient probe
// (ShadeSH9), which for flat and gradient ambient light depends on the normal's height
// alone, and to second order: the quadratic through its values facing up, sideways and
// down is the probe itself. In gamma space as Unity then converts it (LinearToGammaSpace,
// UnityCG.cginc).
float3 UnityAmbient(float3 normal)
{
    float y = normal.y;
    float3 value = ambientSide.rgb + 0.5f * (ambientUp.rgb - ambientDown.rgb) * y
                   + (0.5f * (ambientUp.rgb + ambientDown.rgb) - ambientSide.rgb) * y * y;
    value = max(value, 0.0f);
    if (ambientUp.w > 0.5f)
        value = max(1.055f * pow(value, 0.416666667f) - 0.055f, 0.0f);
    return value;
}

// NVIDIA's approximation of the split-sum's environment term (ProgrammingGuideDLSS_RR.md
// 4.2.1, after Ray Tracing Gems, chapter 32), as the guide gives it: the specular albedo
// DLSS Ray Reconstruction and AMD FSR Ray Regeneration take as a guide.
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

#endif
