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
    uint2 frameSize;
    uint frameIndex;
    uint frameFlags;
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
};

struct VertexAttributes
{
    float3 normal;
    float2 uv;
};

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

// A pixel's normal: the G-buffer's where there is one; without (forward rendering),
// the depth's, from the positions of the neighbours on the nearer side, facing the camera.
float3 SurfaceNormal(uint2 pixel, float3 position)
{
    if (HAS_GBUFFER)
        return DecodeNormal(gbuffer2[pixel]);
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

// True where nothing lies between origin and origin + direction * maxDistance.
bool Unoccluded(float3 origin, float3 direction, float maxDistance)
{
    if (maxDistance > 1.0e6f && BodyBlocks(origin, direction))
        return false;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0f;
    ray.TMax = maxDistance;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES | RAY_FLAG_FORCE_OPAQUE> query;
    query.TraceRayInline(scene, RAY_FLAG_NONE, 0xFF, ray);
    query.Proceed();
    return query.CommittedStatus() == COMMITTED_NOTHING;
}

// The sky's radiance in a direction: the environment where one is given, else the
// sky's colour.
float3 Sky(float3 direction)
{
    if (!HAS_ENVIRONMENT)
        return skyColor.rgb;
    float u = atan2(direction.x, direction.z) * (0.5f / kPi) + 0.5f;
    float v = acos(clamp(direction.y, -1.0f, 1.0f)) / kPi;
    return environmentTexture.SampleLevel(linearWrap, float2(u, v), 0.0f).rgb;
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
    uint material;
};

Hit TraceClosest(float3 origin, float3 direction, float maxDistance)
{
    Hit hit;
    hit.found = false;
    hit.distance = maxDistance;
    hit.position = 0.0f;
    hit.normal = float3(0.0f, 1.0f, 0.0f);
    hit.uv = 0.0f;
    hit.material = 0u;

    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0f;
    ray.TMax = maxDistance;
    RayQuery<RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES | RAY_FLAG_FORCE_OPAQUE> query;
    query.TraceRayInline(scene, RAY_FLAG_NONE, 0xFF, ray);
    query.Proceed();
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
    hit.material = instance.material;
    return hit;
}

// A material's albedo at a point: its colour times its small texture.
float3 Albedo(MaterialData material, float2 uv)
{
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
    return albedo * sunColor.rgb * nl;
}

// The radiance arriving along a ray, from what it hits and, bounce by bounce, what
// lights that; the sky where it escapes.
float3 Radiance(float3 origin, float3 direction, float maxDistance, uint bounces, inout Random random,
                out float firstDistance)
{
    float3 total = 0.0f;
    float3 weight = 1.0f;
    firstDistance = maxDistance;
    for (uint bounce = 0u; bounce <= bounces; ++bounce)
    {
        Hit hit = TraceClosest(origin, direction, bounce == 0u ? maxDistance : 1.0e7f);
        if (bounce == 0u)
            firstDistance = hit.distance;
        if (!hit.found)
        {
            total += weight * Sky(direction);
            break;
        }
        MaterialData material = materialData[hit.material];
        float3 albedo = Albedo(material, hit.uv);
        total += weight * (SunAt(hit, albedo, random) + material.emission);
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

#endif
