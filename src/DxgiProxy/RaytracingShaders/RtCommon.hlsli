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

// True where nothing lies between origin and origin + direction * maxDistance.
bool Unoccluded(float3 origin, float3 direction, float maxDistance)
{
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

#endif
