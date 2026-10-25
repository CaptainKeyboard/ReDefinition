// What every raytracing pass shares: the bindings of the service's root signature
// (RaytracingDevice.cpp, BuildPipelinesLocked), the frame's constants as RtFramePacket
// carries them, and the helpers to rebuild a pixel's position and to sample.
//
// Positions are camera-relative: the world's axes, the camera at the origin.

#ifndef REDEFINITION_RT_COMMON
#define REDEFINITION_RT_COMMON

#include "RtContract.h"

// A field of cbuffer Frame in the register the contract gives it (RtContract.h, RT_CB_*),
// in a component of it.
#define RT_AT_(r) packoffset(c##r)
#define RT_AT(r) RT_AT_(r)
#define RT_AT_Z_(r) packoffset(c##r.z)
#define RT_AT_Z(r) RT_AT_Z_(r)
#define RT_AT_W_(r) packoffset(c##r.w)
#define RT_AT_W(r) RT_AT_W_(r)

// The frame's constants (Raytracing.h, RtFrameConstants, written by WriteConstantsLocked),
// each field in the register the contract gives it.
cbuffer Frame : register(b0)
{
    // The camera's matrices, rows first: clip to camera-relative world, back, and the frame
    // before's world to clip; then the sun, the sky, the parameters (AO_DISTANCE and on).
    row_major float4x4 clipToWorld : RT_AT(RT_CB_CLIP_TO_WORLD);
    row_major float4x4 worldToClip : RT_AT(RT_CB_WORLD_TO_CLIP);
    row_major float4x4 prevWorldToClip : RT_AT(RT_CB_PREV_WORLD_TO_CLIP);
    float4 sunDirection : RT_AT(RT_CB_SUN_DIRECTION);   // towards the sun; w: angular radius
    float4 sunColor : RT_AT(RT_CB_SUN_COLOR);
    float4 skyColor : RT_AT(RT_CB_SKY_COLOR);
    float4 params0 : RT_AT(RT_CB_PARAMS);
    float4 params1 : RT_AT(RT_CB_PARAMS1);
    // The main body: centre relative to the camera, radius; w 0 for none.
    float4 bodySphere : RT_AT(RT_CB_BODY_SPHERE);
    // Unity's ambient light facing up, linear, w 1 in gamma space; facing sideways; down.
    float4 ambientUp : RT_AT(RT_CB_AMBIENT);
    float4 ambientSide : RT_AT(RT_CB_AMBIENT_SIDE);
    float4 ambientDown : RT_AT(RT_CB_AMBIENT_DOWN);
    uint2 frameSize : RT_AT(RT_CB_FRAME_SIZE);
    uint frameIndex : RT_AT_Z(RT_CB_FRAME_SIZE);
    uint frameFlags : RT_AT_W(RT_CB_FRAME_SIZE);
    // Bodies lit by the sun as lights (RtReflectors): towards each with its angular
    // radius in w, and its radiance, linear, with in w how far its shadow ray goes, to the
    // body's surface.
    float4 lightDirection0 : RT_AT(RT_CB_LIGHTS);
    float4 lightColor0 : RT_AT(RT_CB_LIGHT_COLOR0);
    float4 lightDirection1 : RT_AT(RT_CB_LIGHT_DIRECTION1);
    float4 lightColor1 : RT_AT(RT_CB_LIGHT_COLOR1);
    // x: how many lights; y: 1 where Unity's colours are gamma-space values; z: how
    // strongly water mirrors the sun's disc, 1 as it is; w: 1 where the main body's sea is
    // KSP's own (no Scatterer), found by its level.
    float4 lightParams : RT_AT(RT_CB_LIGHT_PARAMS);
    // rgb: the least diffuse light of the forward light, as Unity's colours are.
    float4 nightLight : RT_AT(RT_CB_NIGHT_LIGHT);
    // rgb: the main body's albedo, linear; w: the time in seconds, for the waves of KSP's
    // own sea (StockWaves).
    float4 planetAlbedo : RT_AT(RT_CB_PLANET);
    // rgb: the light's body lit by the star, linear: albedo times the star's colour.
    float4 lightSurface0 : RT_AT(RT_CB_LIGHT_SURFACE);
    float4 lightSurface1 : RT_AT(RT_CB_LIGHT_SURFACE1);
    // The sea's frame, fixed to the main body (RtCamera.Sea, SeaFrameAnchor in the game): a
    // point on the sea relative to the camera, and its east and north; all 0 for none.
    float4 seaOrigin : RT_AT(RT_CB_SEA);
    float4 seaEast : RT_AT(RT_CB_SEA_EAST);
    float4 seaNorth : RT_AT(RT_CB_SEA_NORTH);
    // The main body's cloud layer (Raytracing.h, RtFramePacket.clouds): the map's rows,
    // the radii, the strengths and fades, the colour.
    float4 cloudRow0 : RT_AT(RT_CB_CLOUDS);
    float4 cloudRow1 : RT_AT(RT_CB_CLOUDS_1);
    float4 cloudRow2 : RT_AT(RT_CB_CLOUDS_2);
    float4 cloudRadii : RT_AT(RT_CB_CLOUDS_3);
    float4 cloudFades : RT_AT(RT_CB_CLOUDS_4);
    float4 cloudColor : RT_AT(RT_CB_CLOUDS_5);
    // prevWorldToClip for what the bodies carry, which Krakensbane's origin shifts move
    // by more than the camera (Raytracing.h, RtFramePacket.prevWorldToClipFar).
    row_major float4x4 prevWorldToClipFar : RT_AT(RT_CB_PREV_WORLD_TO_CLIP_FAR);
    // How Unity fades the sun's shadow out towards its shadow distance (Raytracing.h,
    // RtFramePacket.shadowFade): the fade's centre relative to the camera and its type,
    // its scale and bias; in z how far Parallax draws its grass about the camera, in w the light a
    // meadow keeps from its ground (MeadowShade).
    float4 shadowFadeCentre : RT_AT(RT_CB_SHADOW_FADE);
    float4 shadowFadeScale : RT_AT(RT_CB_SHADOW_FADE1);
    // The point Parallax draws its grass about the vessel from, relative to the camera, and in w how far
    // (MeadowShade).
    float4 meadowVessel : RT_AT(RT_CB_SHADOW_FADE2);
    // The radiance cache (RadianceCache.hlsli; Raytracing.h, RtFrameConstants): its
    // anchor's tile and the frame it counts, the anchor within the tile and the cells'
    // angle; the least cell, the most samples, the samples it takes to be read, the chance a
    // path traces on all the same; its buckets, their slots, the frames an unused cell
    // lives, the debug view.
    int4 cacheTile : RT_AT(RT_CB_CACHE_TILE);
    float4 cacheOffset : RT_AT(RT_CB_CACHE_OFFSET);
    float4 cacheParams : RT_AT(RT_CB_CACHE_PARAMS);
    uint4 cacheSizes : RT_AT(RT_CB_CACHE_SIZES);
};

// Per instance of the scene, by its InstanceID (Raytracing.h, InstanceData; GpuInstances.hlsl
// writes the GPU's).
struct InstanceData
{
    // The mesh's descriptor slot into meshIndices and meshAttributes, the material's index in
    // materialData, the instance's flags (RT_INSTANCE_*), padding; then the normals' matrix,
    // the inverse transpose of the transform's 3x3, in three padded rows.
    uint meshSlot;
    uint material;
    uint flags;
    uint pad;
    float4 normalRow0;
    float4 normalRow1;
    float4 normalRow2;
};

// A material as the proxy writes it (Raytracing.h, MaterialData), by its index.
struct MaterialData
{
    // Linear albedo with the cut-out's alpha threshold in w, specular colour, smoothness,
    // emitted radiance, and the layer of materialTextures (-1 for none).
    float4 albedo;
    float3 specular;
    float smoothness;
    float3 emission;
    int texture;
    // x: where > 0, the albedo is in Unity's colour space and the vertex colour times x
    // is added to it; y: how its shine is meant (RT_SURFACE_*); z: the gloss divisor;
    // w: the mean gloss (Raytracing.h, RtMaterialDesc).
    float4 vertexColour;
    // x: what of the frame's colour the forward light keeps as diffuse (RtMaterialDesc).
    float4 look;
    // The sun's highlight the material's shader draws (RtMaterialDesc): its specular
    // colour in Unity's colour space; w above 0 a legacy Blinn-Phong exponent, otherwise
    // minus the standard shader's smoothness. All 0 for none.
    float4 highlight;
};

// A vertex's attributes as SetMesh lays them out, six floats (Raytracing.h, PendingMesh).
struct VertexAttributes
{
    // The mesh's normal and texture coordinates, and its colour.
    float3 normal;
    float2 uv;
    uint color;   // RGBA, a byte each, R lowest
};

// A vertex colour's rgb from 0 to 1; alpha is left out.
float3 UnpackColor(uint c)
{
    return float3(c & 0xFFu, (c >> 8) & 0xFFu, (c >> 16) & 0xFFu) * (1.0f / 255.0f);
}

// The scene's top-level structure, then the frame's inputs (Raytracing.h, kRtInput*): the depth
// as Direct3D's clip-space z, reversed; Unity's G-buffer; the motion vectors.
RaytracingAccelerationStructure scene : register(t0);
Texture2D<float> depthTexture : register(t1);
Texture2D<float4> gbuffer0 : register(t2);
Texture2D<float4> gbuffer1 : register(t3);
Texture2D<float4> gbuffer2 : register(t4);
Texture2D<float4> motionTexture : register(t5);
// The sky's radiance by direction, as a latitude-longitude image: x the angle around
// the vertical, y from straight up (0) to straight down (1).
Texture2D<float4> environmentTexture : register(t6);
// RT_INPUT_CLOUDS: the cloud layer's map, r coverage, g the density curve at the shadow's
// height, equirectangular (x around the map's y axis from +z, y from its +y down).
Texture2D<float4> cloudTexture : register(t8);
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
RWTexture2D<float4> output5 : register(u5);
RWTexture2D<float4> output6 : register(u6);
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
#define EXTRA_FRAME ((frameFlags & RT_FLAG_EXTRA) != 0u)
#define WATER_SURFACE ((frameFlags & RT_FLAG_WATER) != 0u)
#define WATER_NORMALS ((frameFlags & RT_FLAG_WATER_NORMALS) != 0u)
#define LEGACY_SMOOTHNESS ((frameFlags & RT_FLAG_LEGACY_SMOOTHNESS) != 0u)
#define SURFACE_INTENT ((frameFlags & RT_FLAG_SURFACE_INTENT) != 0u)
#define LIGHT_COUNT ((uint)lightParams.x)
#define GAMMA_INPUTS (lightParams.y > 0.5f)
#define SUN_MIRROR (lightParams.z)

// The hit distances the signals carry in w: a miss as a half float's largest value; a
// reflected ray that met a moon's disc (kDiscHit); in the water's frame a ray that met
// the scene at most kWaterHitLimit, one that met nothing kWaterSky (ReDefinitionRtCapture
// .shader, fragOceanCompose, reads both).
static const float kFp16Max = RT_FP16_MAX;
static const float kDiscHit = RT_DISC_HIT;
static const float kWaterHitLimit = RT_WATER_HIT_LIMIT;
static const float kWaterSky = RT_WATER_SKY;
// Whether the main body's sea is KSP's own, found by its level (lightParams.w).
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

// Pi, to the float's precision, for the samplers and the shading terms.
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

// The world normal from Unity's G-buffer encoding, n * 0.5 + 0.5 (gbuffer2).
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
// counter. A further trace of a frame (EXTRA_FRAME, the ocean's) draws streams of its
// own, independent of the frame's.
uint Pcg(uint value)
{
    uint state = value * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// One stream of the random numbers, MakeRandom's and NextFloat's.
struct Random
{
    // The last hash, which the next draw hashes again.
    uint state;
};

// The stream for a pixel and a frame, one of several (stream) per pass.
Random MakeRandom(uint2 pixel, uint frame, uint stream)
{
    Random r;
    r.state = Pcg(pixel.x + Pcg(pixel.y + Pcg(frame * 16u + stream + (EXTRA_FRAME ? 8u : 0u))));
    return r;
}

// The stream's next number, uniform in [0, 1), from the hash's upper 24 bits.
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
// 5 cm a kilometre: at 50 cm a kilometre the rays started over Parallax's grass from some
// hundreds of metres on (75 cm at 1.5 km), and its shadow faded out with the camera's
// distance -- the terrain under it saw 0.94 of the sun at 1.5 km and 0.99 at 4 km, 0.68
// at 200 m; 0.72 and 0.70 at 5 cm, and the shadows of the space centre's buildings and the
// sun on its tarmac the same within 0.03 (the launch pad at 200 m to 4 km, 2026-10-25).
float3 RayOrigin(float3 position, float3 normal)
{
    float distance = length(position);
    return position + normal * (0.01f + distance * 0.00005f);
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

// The ray flags the cut-out measurement asks for (RT_FLAG_CUTOUT_OPAQUE): none otherwise.
uint CutoutRayFlags()
{
    return (frameFlags & RT_FLAG_CUTOUT_OPAQUE) != 0u ? RAY_FLAG_FORCE_OPAQUE : RAY_FLAG_NONE;
}

// The triangle a ray query's hit is on in its mesh's index buffer. RTX Mega Geometry's cluster
// structures (RaytracingMegaGeometry.cpp, rtMegaGeometry) number the triangles within each
// cluster of 256 and give the cluster's number as the geometry index; every other structure
// has one geometry, index 0.
#define RT_TRIANGLE(query, which) (query.which##GeometryIndex() * 256u + query.which##PrimitiveIndex())

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
    if ((instance.flags & RT_INSTANCE_TERRAIN) != 0u && t < terrainSkip)
        return false;
    if ((instance.flags & RT_INSTANCE_CUTOUT) == 0u)
        return true;
    if ((frameFlags & RT_FLAG_CUTOUT_PLAIN) != 0u)
        return (primitive & 1u) == 0u;
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
    query.TraceRayInline(scene, CutoutRayFlags(), 0xFF, ray);
    while (query.Proceed())
        if (CandidateCounts(query.CandidateInstanceID(), RT_TRIANGLE(query, Candidate), query.CandidateTriangleBarycentrics(),
                            query.CandidateTriangleRayT(), terrainSkip, query.WorldRayOrigin()))
            query.CommitNonOpaqueTriangleHit();
    return query.CommittedStatus() == COMMITTED_NOTHING;
}

// The same towards the sun: a cut-out surface's hit -- grass, ferns -- lets the ray on
// with the chance the flags give (RT_FLAG_FOLIAGE_SHIFT), as a blade of grass lets
// through part of the light that falls on it.
bool SunUnoccluded(float3 origin, float3 direction, float maxDistance, float terrainSkip = 0.0f)
{
    float through = float((frameFlags >> RT_FLAG_FOLIAGE_SHIFT) & RT_FLAG_FOLIAGE_MASK) * (1.0f / 255.0f);
    if (through <= 0.0f)
        return Unoccluded(origin, direction, maxDistance, terrainSkip);
    if (maxDistance > 1.0e6f && BodyBlocks(origin, direction))
        return false;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0f;
    ray.TMax = maxDistance;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> query;
    query.TraceRayInline(scene, CutoutRayFlags(), 0xFF, ray);
    while (query.Proceed())
    {
        uint instanceId = query.CandidateInstanceID();
        uint primitive = RT_TRIANGLE(query, Candidate);
        float t = query.CandidateTriangleRayT();
        if (!CandidateCounts(instanceId, primitive, query.CandidateTriangleBarycentrics(), t, terrainSkip,
                             query.WorldRayOrigin()))
            continue;
        if ((instanceData[instanceId].flags & RT_INSTANCE_CUTOUT) != 0u)
        {
            uint seed = Pcg(primitive * 7919u ^ Pcg(asuint(t) ^ Pcg(asuint(origin.y) ^ Pcg(frameIndex + 17u))));
            if (float(seed >> 8) * (1.0f / 16777216.0f) < through)
                continue;
        }
        query.CommitNonOpaqueTriangleHit();
    }
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

// The sky's radiance as diffuse light: its colour taken off by skyColor.w (0 keeps it, 1
// leaves its luminance alone). KSP's own sky is a painted blue under a white sun: as
// light it would turn the daylight blue, where an atmosphere that tints the sun
// (Scatterer's) balances its blue with a warm sun (RtKsp, SkyNeutral).
float3 SkyLight(float3 direction)
{
    float3 c = Sky(direction);
    return lerp(c, dot(c, float3(0.2126f, 0.7152f, 0.0722f)).xxx, saturate(skyColor.w));
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
    // Whether it met anything, how far along the ray (the ray's length where not), the point,
    // the normal, the texture coordinate, the vertex colour, the material's index, and the
    // instance's flags.
    bool found;
    float distance;
    float3 position;
    float3 normal;
    float2 uv;
    float3 color;
    uint material;
    uint flags;
};

// The share of the light a cut-out surface -- a blade of grass -- lets through (rtFoliageTransmission).
float FoliageThrough()
{
    return float((frameFlags >> RT_FLAG_FOLIAGE_SHIFT) & RT_FLAG_FOLIAGE_MASK) * (1.0f / 255.0f);
}

// `through`: a cut-out surface's hit lets the ray on with that chance, as a blade lets the sky's light
// through as it lets the sun's (Radiance's diffuse rays; SunUnoccluded).
Hit TraceClosest(float3 origin, float3 direction, float maxDistance, float terrainSkip = 0.0f, float through = 0.0f)
{
    Hit hit;
    hit.found = false;
    hit.distance = maxDistance;
    hit.flags = 0u;
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
    query.TraceRayInline(scene, CutoutRayFlags(), 0xFF, ray);
    while (query.Proceed())
    {
        uint instanceId = query.CandidateInstanceID();
        uint primitive = RT_TRIANGLE(query, Candidate);
        float t = query.CandidateTriangleRayT();
        if (!CandidateCounts(instanceId, primitive, query.CandidateTriangleBarycentrics(), t, terrainSkip, query.WorldRayOrigin()))
            continue;
        if (through > 0.0f && (instanceData[instanceId].flags & RT_INSTANCE_CUTOUT) != 0u)
        {
            uint seed = Pcg(primitive * 7919u ^ Pcg(asuint(t) ^ Pcg(asuint(origin.x) ^ Pcg(frameIndex + 29u))));
            if (float(seed >> 8) * (1.0f / 16777216.0f) < through)
                continue;
        }
        query.CommitNonOpaqueTriangleHit();
    }
    if (query.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
        return hit;

    InstanceData instance = instanceData[query.CommittedInstanceID()];
    uint slot = instance.meshSlot;
    uint3 index = meshIndices[NonUniformResourceIndex(slot)].Load3(RT_TRIANGLE(query, Committed) * 12u);
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
    hit.flags = instance.flags;
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

// The main body's cloud layer (EVE; RtEveClouds in the game): where a light's ray from a point
// below the shadow's sphere (cloudRadii.x) leaves it, and the fade it takes there, as the
// light comes from above the sphere and the point lies below it; false without a layer or
// above the sphere (CloudTransmittance).
bool CloudExit(float3 position, float3 towards, out float3 at, out float fade)
{
    at = position;
    fade = 0.0f;
    if (cloudRadii.w < 0.5f || bodySphere.w <= 0.0f)
        return false;
    float radius = cloudRadii.x;
    float3 toCentre = bodySphere.xyz - position;
    float distance = length(toCentre);
    if (distance > radius)
        return false;
    // The far root of |position + towards t - centre| = radius: EVE's dot(L, -S), S the
    // light's travel, is the centre's distance along `towards`.
    float along = dot(toCentre, towards);
    float t = along + sqrt(max(radius * radius - (distance * distance - along * along), 0.0f));
    at = position + towards * t;
    fade = saturate(100.0f * dot(toCentre / max(distance, 1e-3f), -towards)) * saturate(0.01f * (radius - distance)) * cloudFades.z;
    return fade > 0.0f;
}

// The probe's check (RT_FLAG_CLOUD_SHADOW with cloudColor.w 11, 12, 13): the map's u, v or
// coverage at the exit point, in place of the transmittance.
float CloudProbeValue(float3 at)
{
    float3 q = float3(dot(cloudRow0, float4(at, 1.0f)), dot(cloudRow1, float4(at, 1.0f)), dot(cloudRow2, float4(at, 1.0f)));
    float3 n = q / max(length(q), 1e-3f);
    float2 uv = float2(0.5f + atan2(n.x, n.z) * (0.5f / kPi), acos(clamp(n.y, -1.0f, 1.0f)) / kPi);
    return cloudColor.w < 11.5f ? uv.x : cloudColor.w < 12.5f ? uv.y : cloudTexture.SampleLevel(linearWrap, uv, 0.0f).r;
}

// The layer's density at a point on the shadow's sphere, and its transmittance.
float CloudTransmittanceAt(float3 at)
{
    float3 q = float3(dot(cloudRow0, float4(at, 1.0f)), dot(cloudRow1, float4(at, 1.0f)), dot(cloudRow2, float4(at, 1.0f)));
    float3 n = q / max(length(q), 1e-3f);
    float2 uv = float2(0.5f + atan2(n.x, n.z) * (0.5f / kPi), acos(clamp(n.y, -1.0f, 1.0f)) / kPi);
    float4 map = cloudTexture.SampleLevel(linearWrap, uv, 0.0f);
    float keep = 1.0f - saturate(cloudFades.y);
    float coverage = (min(max(map.r, keep), 1.0f) - keep) / max(cloudFades.y, 1e-3f);
    float density = map.g < 0.01f || coverage < 0.01f ? 0.0f : map.g * coverage * cloudFades.w;
    return 1.0f / (1.0f + 7.0f * density + 26.0f * density * density);
}

// What of a light's ray from a point towards it gets through the main body's cloud layer, as
// EVE's own screen-space cloud shadow takes it for the sun (its ScreenSpaceCloudShadow shader,
// volumetric variant): where the ray leaves the shadow's sphere above the point
// (CloudExit), the coverage and the density curve at that height from the layer's map
// (cloudTexture), the transmittance 1 / (1 + 7d + 26d^2), tinted by the shadow's colour at
// the shadow's strength, faded in as the light comes from above and below the sphere. 1
// without a layer.
float3 CloudTransmittance(float3 position, float3 towards)
{
    float3 at;
    float fade;
    if (!CloudExit(position, towards, at, fade))
        return 1.0f;
    float tr = CloudTransmittanceAt(at);
    return 1.0f + fade * cloudFades.x * (1.0f - tr) * (saturate(tr * cloudColor.rgb) - 1.0f);
}

// The sky a ray that escapes the scene sees from under the layer: the cloud where it
// covers, lit by the sun and the moons from above and by the sky, darker underneath the denser it is,
// over the sky by its opacity (1 - its transmittance). The reflection probe the sky comes
// from otherwise (HAS_ENVIRONMENT) holds EVE's clouds as EVE draws them already.
float3 CloudOverSky(float3 origin, float3 direction, float3 sky)
{
    float3 at;
    float fade;
    if (HAS_ENVIRONMENT || !CloudExit(origin, direction, at, fade))
        return sky;
    // From under the layer, the ray's own direction, not the light's, fades it: rays that
    // run along the horizon meet the layer far away, where EVE's layer thins out too.
    fade = saturate(0.01f * (cloudRadii.x - length(bodySphere.xyz - origin))) * cloudFades.z;
    float tr = CloudTransmittanceAt(at);
    float3 up = normalize(at - bodySphere.xyz);
    float3 lit = InputColor(sunColor.rgb) * saturate(dot(up, normalize(sunDirection.xyz))) + SkyLight(up);
    // The bodies the sun lights (RtReflectors) light the cloud from above as the sun does.
    if (LIGHT_COUNT > 0u)
        lit += lightColor0.rgb * saturate(dot(up, normalize(lightDirection0.xyz)));
    if (LIGHT_COUNT > 1u)
        lit += lightColor1.rgb * saturate(dot(up, normalize(lightDirection1.xyz)));
    float3 cloud = lit * cloudColor.w * lerp(1.0f, 0.5f, 1.0f - tr);
    return lerp(sky, cloud, fade * (1.0f - tr));
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
    if (!SunUnoccluded(RayOrigin(hit.position, hit.normal), direction, 1.0e7f))
        return 0.0f;
    return albedo * InputColor(sunColor.rgb) * nl * CloudTransmittance(hit.position, towardsSun);
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

// Smith's shadowing of one direction for Beckmann's distribution of roughness `m`
// (Walter et al., "Microfacet Models for Refraction through Rough Surfaces", 2007,
// equation 27).
float BeckmannG1(float cosine, float m)
{
    float c = cosine / (m * sqrt(max(1.0f - cosine * cosine, 1e-8f)));
    return c < 1.6f ? (3.535f * c + 2.181f * c * c) / (1.0f + 2.276f * c + 2.577f * c * c) : 1.0f;
}

// The specular light a disc of angular `radius` towards `towards` gives water of
// Beckmann roughness `m` (the root mean square of its slopes), per unit of the disc's
// illuminance. Beckmann's distribution, not GGX's: a sea's slopes are near Gaussian (Cox
// and Munk), as Bruneton's ocean and Scatterer take them, and GGX's long tails let the
// glints of slopes four and five times the usual mirror a disc as bright as the moon's
// far to its sides -- measured at sea, 2026-10-04, a moon's path 2.5 times as wide as its
// waves allow. The representative point -- the direction on the disc closest to the
// reflection vector, after Frostbite's sun as a disc area light (Lagarde and de
// Rousiers, "Moving Frostbite to Physically Based Rendering", 2014, listing 6) -- lit as
// a punctual light, with the energy the wider lobe adds taken back by (m / m')^2,
// m' = m + radius / 3 (Karis, "Real
// Shading in Unreal Engine 4", 2013, equations 10 and 14), and never brighter than the
// disc itself, which that approximation overshoots at grazing angles. Without the
// Fresnel term, which the reader applies. Smooth water mirrors the disc as it is; rough
// water spreads it into a highlight stretched at grazing angles, the sea's long path.
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
    float visibility = BeckmannG1(nv, a) * BeckmannG1(nl, a) / (4.0f * nl * nv);
    float wide = saturate(a + radius / 3.0f);
    return min(D * visibility * nl * (a * a) / (wide * wide), 1.0f / (kPi * sinRadius * sinRadius));
}

// KSP's own sea (STOCK_WATER), drawn into Unity's G-buffer by its ocean shader with
// normals from its normal map: a pixel within kStockWaterBand metres of the main
// body's sea level whose normal faces up, as the sea's does, and which is no surface
// of the raytracing's scene, as a vessel's deck is. Its waves and roughness are ReDefinition's (StockWaves); kWaterLeastRoughness
// the least roughness, of ripples shorter than the shortest wave.
static const float kStockWaterBand = 2.0f;
static const float kWaterLeastRoughness = 0.004f;

// Whether the camera's pixel at position, with its G-buffer normal, is KSP's own sea, by the
// test the comment above describes.
bool IsStockWater(float3 position, float3 normal)
{
    float3 up = position - bodySphere.xyz;
    float level = length(up);
    // The band as the reflections' shader takes it (ReDefinitionDeferredReflections.shader,
    // IsStockSea): the sea's quads are flat, and their middles lie below the level by
    // their edges' length squared over 8 radii, metres on the far, coarse quads; and the
    // depth's precision falls with the camera's height.
    float distance = length(position);
    float cameraHeight = max(length(bodySphere.xyz) - bodySphere.w, 0.0f);
    float band = max(kStockWaterBand, max(0.001f * distance, 0.01f * cameraHeight));
    if (!(STOCK_WATER && bodySphere.w > 0.0f && abs(level - bodySphere.w) < band
          && dot(normal, up / level) > 0.8f))
        return false;
    // Not where the camera's ray meets the scene's own surface there: the sea is not in the
    // raytracing's scene, a vessel's deck or a flat shore at the sea's level is (the
    // player saw the sea's waves and glints on a capsule's hatch).
    Hit hit = TraceClosest(float3(0.0f, 0.0f, 0.0f), position / max(distance, 1e-6f), distance * 1.01f + 0.1f);
    // The sea's floor lies behind the water: met more than a metre beyond it, it is not
    // the surface there (a shallow coast kept its sea).
    float tolerance = distance * 0.005f + 0.1f;
    return !(hit.found && hit.distance - distance > -tolerance && hit.distance - distance < min(tolerance, 1.0f));
}

// KSP's own sea's waves, which its normal map barely carries. The long waves, 40 m down
// to 2 m, as kStockWaves sine waves spread about a wind direction and across it, each of
// slope kStockWaveSlope, running as deep water's waves do (omega^2 = g k); the ripples,
// 2 m down to 5 cm, as kRippleOctaves octaves of gradient noise, short-crested as
// ripples are -- sine waves that short would draw straight crests across the sea, a weave
// -- each of slope deviation kRippleSlope, drifting with the wind at their waves'
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
static const uint kRippleOctaves = 6u;
static const float kRippleSlope = 0.055f;

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

// The sea under a pixel: where it lies on the sea, in metres east and north of a point
// fixed to the main body near the camera (seaOrigin, seaEast, seaNorth: RtCamera.Sea), so
// that the waves stay where they are on the water as the camera moves and the body turns;
// its own up, and east and north turned into its own plane; the view's direction along
// it; and the water the pixel covers across the view and, stretched by the grazing angle,
// along it. Positions relative to that point, which lies near the camera, keep a float's
// precision to millimetres. Without the point (seaEast 0, as in the raytracing's test
// player) the plane under the camera.
struct SeaFrame
{
    // The sea's up, east and north; the pixel's place in metres east and north and the view's
    // direction in the plane; the water the pixel covers across and along the view.
    float3 up, east, north;
    float2 at, along;
    float acrossFootprint, alongFootprint;
};

// The sea's frame under a camera-relative point, the pixel covering acrossFootprint metres.
SeaFrame MakeSeaFrame(float3 position, float acrossFootprint)
{
    SeaFrame f;
    f.up = normalize(position - bodySphere.xyz);
    float3 origin, east;
    if (dot(seaEast.xyz, seaEast.xyz) > 0.5f)
    {
        origin = seaOrigin.xyz;
        east = seaEast.xyz;
        float3 fromOrigin = position - origin;
        f.at = float2(dot(fromOrigin, seaEast.xyz), dot(fromOrigin, seaNorth.xyz));
    }
    else
    {
        float3 below = normalize(-bodySphere.xyz);
        east = cross(float3(0.0f, 1.0f, 0.0f), below);
        east = dot(east, east) > 1e-6f ? normalize(east) : float3(1.0f, 0.0f, 0.0f);
        float3 fromCentre = position - bodySphere.xyz;
        f.at = float2(dot(fromCentre, east), dot(fromCentre, cross(below, east)));
    }
    float3 flatEast = east - dot(east, f.up) * f.up;
    f.east = dot(flatEast, flatEast) > 1e-8f ? normalize(flatEast) : east;
    f.north = cross(f.up, f.east);
    float3 towardsPixel = normalize(position);
    float2 along = float2(dot(towardsPixel, f.east), dot(towardsPixel, f.north));
    f.along = dot(along, along) > 1e-8f ? normalize(along) : float2(1.0f, 0.0f);
    f.acrossFootprint = acrossFootprint;
    f.alongFootprint = acrossFootprint / max(abs(dot(towardsPixel, f.up)), 0.02f);
    return f;
}

// The ripples' slopes along the frame's east and north (xy) and the slope variance of the
// octaves no pixel shows (z), each octave of slope deviation kRippleSlope times `scale`.
float3 SeaRipples(SeaFrame f, float scale)
{
    const float2 wind = float2(cos(0.35f), sin(0.35f));
    float time = planetAlbedo.w;
    float slopeDeviation = kRippleSlope * scale;
    float2 slope = 0.0f;
    float lost = 0.0f;
    for (uint octave = 0u; octave < kRippleOctaves; ++octave)
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
        // The noise's gradient is 0.884 in root mean square per unit of p (0.625 along
        // each axis; tools/sea_reflection_check.py --noise): to a slope of
        // slopeDeviation, as the roughness counts what is lost.
        float2 derivative = RippleNoise(p, octave).yz * frequency;
        slope += derivative * (slopeDeviation * shown * wavelength / 0.884f);
    }
    return float3(slope, lost);
}

// KSP's own sea's normal and roughness at a point, the waves and ripples above kStockWaves
// describes summed over the sea's frame.
float4 StockWaves(float3 position, float acrossFootprint)
{
    SeaFrame f = MakeSeaFrame(position, acrossFootprint);
    float time = planetAlbedo.w;
    float2 slope = 0.0f;
    float lost = 0.0f;
    for (uint i = 0u; i < kStockWaves; ++i)
    {
        // Lengths spread over the range, each at random within its step, and directions
        // at random: no two waves in step with each other. The dice from integer hashes,
        // the same on every GPU, as sin-based hashes are not.
        float3 dice = float3(Pcg(3u * i + 101u) >> 8, Pcg(3u * i + 102u) >> 8, Pcg(3u * i + 103u) >> 8) * (1.0f / 16777216.0f);
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
    float scale = m / sqrt(float(kRippleOctaves) * kRippleSlope * kRippleSlope);
    float3 ripples = SeaRipples(f, scale);
    return float4(normalize(normal - ripples.x * f.east - ripples.y * f.north), sqrt(ripples.z));
}

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
            total += InputColor(sunColor.rgb) * specular * SUN_MIRROR * CloudTransmittance(position, towardsSun);
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
        total += surface * (2.0f / 3.0f) * phase * solidAngle * specular * CloudTransmittance(position, l);
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
    // How far the shadow ray goes: to the body's surface, and past the main body's sphere
    // test (Unoccluded), which the light of a body beyond its horizon takes.
    float maxDistance = max(first ? lightColor0.w : lightColor1.w, 1.0e6f + 1.0f);
    float chance = (first ? w0 : w1) / (w0 + w1);
    float3 axis = normalize(towards.xyz);
    float nl = dot(normal, axis);
    if (nl <= 0.0f)
        return 0.0f;
    float2 u = float2(NextFloat(random), NextFloat(random));
    float3 direction = SampleCone(axis, towards.w, u);
    if (!Unoccluded(RayOrigin(position, normal), direction, maxDistance, terrainSkip))
        return 0.0f;
    return colour * nl / chance * CloudTransmittance(position, axis);
}

#include "RadianceCache.hlsli"

// The radiance arriving along a ray, from what it hits and, bounce by bounce, what
// lights that; the sky where it escapes: as light (SkyLight), or, for a mirror's ray
// that sees it directly (`mirror`, its first segment), as it looks.
//
// With the radiance cache (`cached`, the passes' own pipelines for it, and CACHE_ON), at a
// hit the path would bounce on from, the light arriving there is the cache's where its
// cell holds CACHE_WARM_SAMPLES samples, and the path ends: its albedo times that in place
// of what the bounces would bring, the light that reaches the hit itself (the sun, the
// moons) traced as before. With the chance
// CACHE_REFRESH, and where the cell is cold, the path traces on; the light its bounces
// bring to its first such hit -- what it gathered past the hit, divided by the weight it
// had there -- goes into the cache's cell of the hit (CACHE_UPDATE), as Binder et al.'s
// vertices add "their contribution" to their voxel. As GI-1.0's "temporal radiance
// feedback" (2.2.4), the cache's value holds the light of every bounce in time, where
// today's paths stop after `bounces`.
float3 Radiance(float3 origin, float3 direction, float maxDistance, uint bounces, inout Random random,
                out float firstDistance, float firstTerrainSkip = 0.0f, bool mirror = false, bool cached = false)
{
    float3 total = 0.0f;
    float3 weight = 1.0f;
    firstDistance = maxDistance;
    // The hit whose arriving light goes into the cache: where it is, what the path had
    // gathered there and its weight past the hit's albedo.
    bool record = false;
    float3 recordPosition = 0.0f, recordNormal = float3(0.0f, 1.0f, 0.0f), recordTotal = 0.0f, recordWeight = 0.0f;
    float recordRay = 0.0f;
    for (uint bounce = 0u; bounce <= bounces; ++bounce)
    {
        // Diffuse rays through the grass's blades with the share they let through; a reflection sees them.
        Hit hit = TraceClosest(origin, direction, bounce == 0u ? maxDistance : 1.0e7f, bounce == 0u ? firstTerrainSkip : 0.0f,
                               mirror ? 0.0f : FoliageThrough());
        if (bounce == 0u)
            firstDistance = hit.distance;
        if (!hit.found)
        {
            float3 ground;
            total += weight * (PlanetRadiance(origin, direction, ground)
                                   ? ground
                                   : CloudOverSky(origin, direction, mirror && bounce == 0u ? Sky(direction) : SkyLight(direction)));
            break;
        }
        MaterialData material = materialData[hit.material];
        float3 albedo = Albedo(material, hit.uv, hit.color);
        total += weight * (SunAt(hit, albedo, random) + albedo * ReflectedLightAt(hit.position, hit.normal, random)
                           + material.emission);
        if (bounce == bounces)
            break;
        if (cached && CACHE_ON)
        {
            float4 cell;
            bool warm = CacheLookup(hit.position, hit.normal, hit.distance, random, cell) && cell.w >= CACHE_WARM_SAMPLES;
            bool refresh = NextFloat(random) < CACHE_REFRESH;
            CacheCount(RT_CACHE_STAT_QUERIES, true);
            CacheCount(RT_CACHE_STAT_WARM, warm && !refresh);
            if (warm && !refresh)
            {
                total += weight * albedo * cell.rgb;
                break;
            }
            if (CACHE_UPDATE && !record)
            {
                record = true;
                recordPosition = hit.position;
                recordNormal = hit.normal;
                recordRay = hit.distance;
                recordTotal = total;
                recordWeight = weight * albedo;
            }
        }
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
    // The light the bounces brought to the recorded hit, where its albedo lets it be told:
    // a channel the albedo all but takes away says nothing of the light arriving.
    if (record && all(recordWeight > 0.01f))
    {
        float3 arriving = (total - recordTotal) / recordWeight;
        float arrivingLuminance = dot(arriving, float3(0.2126f, 0.7152f, 0.0722f));
        if (SAMPLE_CLAMP > 0.0f && arrivingLuminance > SAMPLE_CLAMP)
            arriving *= SAMPLE_CLAMP / arrivingLuminance;
        CacheAdd(recordPosition, recordNormal, recordRay, max(arriving, 0.0f), random);
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
    // The world normal, Unity's smoothness, the albedo (linear) and the specular colour.
    float3 normal;
    float smoothness;
    float3 albedo;
    float3 specular;
    // False where neither a G-buffer nor the scene says what the surface is (a surface
    // the rays do not hold, KSP's sea say): the values below are a guess.
    bool found;
    // Without a G-buffer, the material's diffuse share (MaterialData.look); 1 otherwise.
    float diffuseShare;
    // Without a G-buffer, the sun's highlight its shader draws (MaterialData.highlight), a
    // legacy one's colour times the gloss in the texture's alpha; 0 otherwise.
    float4 highlight;
};

// A surface's smoothness as KSP's legacy part shaders give its highlight. Deferred turns
// their Blinn-Phong shininess into a smoothness for its G-buffer by look
// (LegacyToStandard.cginc: shininess ^ 0.215 times the specular map times the root of the
// specular colour's length, the specular colour over pi), which reaches 1, a mirror,
// from a shininess of about 0.4 with a white specular colour (measured at sea,
// 2026-10-04: a capsule at 0.96 with a specular colour of 0.24; the player saw the Mun's
// disc sharp in a wing). KSP's own shaders light those parts with Blinn-Phong's exponent
// 128 times the shininess, which Beckmann's distribution matches with
// m = sqrt(2 / (n + 2)) (Walter et al. 2007): at most a shininess of 1, m 0.124, a
// smoothness of 0.65. In the game only (LEGACY_SMOOTHNESS: RtCamera.LegacySmoothness);
// a test's G-buffer is Unity's own. Specular colours over Deferred's 1 / pi are Unity's standard
// shaders' own and kept, and so is the standard shader's dielectric (0.2209 grey, 4 % in
// the game's gamma colour space: unity_ColorSpaceDielectricSpec), as well as the water's
// frame (WATER_SURFACE), whose surface is Scatterer's ocean with its own variance; the
// specular map's share is taken for the shininess's.
static const float kLegacyShininessPower = 0.215f;
static const float kStandardDielectric = 0.2209163f;

// Whether a G-buffer specular colour is Deferred's from a legacy part shader: no channel
// over 1 / pi, and not the standard shader's dielectric grey.
bool LegacySpecular(float3 specular)
{
    return max(specular.r, max(specular.g, specular.b)) <= 1.0f / kPi + 0.004f
           && any(abs(specular - kStandardDielectric) > 0.002f);
}

// The G-buffer's smoothness taken back to the legacy shininess and on to the smoothness of
// Beckmann's match, as the comment above kLegacyShininessPower says; as it is elsewhere.
float LegacySmoothness(float smoothness, float3 specular)
{
    if (!LEGACY_SMOOTHNESS || WATER_SURFACE || !LegacySpecular(specular))
        return smoothness;
    float length3 = length(specular * kPi);
    if (length3 <= 1e-4f)
        return smoothness;
    float shininess = pow(saturate(smoothness / sqrt(length3)), 1.0f / kLegacyShininessPower);
    float m = sqrt(2.0f / (128.0f * shininess + 2.0f));
    return 1.0f - sqrt(m);
}

// A material's shine as its shader means it, for a hit without a G-buffer: the legacy
// families' highlight at their mean gloss (RtMaterialIntent.cs).
float3 IntendedSpecular(MaterialData material)
{
    uint mode = uint(material.vertexColour.y + 0.5f);
    if (mode == RT_SURFACE_DRY)
        return 0.0f;
    if (mode == RT_SURFACE_LEGACY || mode == RT_SURFACE_UNITY_LEGACY)
        return material.specular * material.vertexColour.w;
    // The space centre's ground, where a ray hits it: matt, as its tarmac is; its lawn's
    // shine is only told apart by the drawn colour.
    if (mode == RT_SURFACE_GROUND)
        return 0.0f;
    return material.specular;
}

// What a pixel's surface reflects as its material means it (SURFACE_INTENT), in the
// G-buffer's terms: the specular colour and the smoothness. Deferred's G-buffer gives every
// KSP shader a shine by one formula (LegacyToStandard.cginc), diffuse-only shaders and the
// space centre's ground included (shine-table, 2026-10-05: concrete at 0.16 / 0.45, the
// ground at 0.22 / 0.55, steel at a smoothness of 1); the material found by the camera's
// ray through the pixel says what its shader draws (RtMaterialIntent.cs): nothing for
// the diffuse ones; for KSP's legacy Blinn-Phong highlight its exponent's roughness and a
// specular colour as bright as the highlight, times its gloss, which Deferred's smoothness
// (or Unity's own legacy specular colour) gives back divided by the material's divisor;
// fixed values; or the G-buffer's own, never a perfect mirror. KSP's sea keeps the
// G-buffer's, which the water's reflections replace. Where the ray finds no material at
// the pixel's depth (an edge, Parallax's displaced ground), the G-buffer's, its legacy
// smoothness as KSP's highlight (LegacySmoothness).
static const float kMostSmoothness = 0.95f;

// The light a meadow's own blades keep from the ground they stand on, for the terrain where Parallax
// no longer draws them: 1 nearer than its grass's range, the meadow's shade (shadowFadeScale.w) from
// the range on, faded in over its last fifth as the grass fades out, and so to the horizon. Parallax
// draws its grass within its range of the camera or of the vessel, as each scatter says (Kerbin's main
// grass about the vessel: meadowVessel), and the shade lies past both; on green ground, which on Kerbin
// is its meadow band (Parallax's lowest texture) and not its rock, sand or snow. 1 where Parallax draws
// no grass. The grass casts this shade itself where it is drawn (its blades in the rays, letting part
// of the sun through: rtFoliageTransmission), so the ground beyond looks the same; as a distant
// meadow's darker colour, it takes the sky's light as the sun's.
// `lawn`: the space centre's lawn, where it shines as the terrain (SurfaceIntent), told from its tarmac
// and concrete by its drawn colour (LawnShare). It is lifted to the terrain's look (kLawnLift): darker
// than the terrain's as drawn (albedo 0.30 against the terrain's 0.39 beside it), it is as bright only
// with the rough shine Deferred gives it, which it does not keep under the rays (RT_SURFACE_GROUND).
// 1.36 with the terrain's occlusion (LawnOcclusion): its picture 1.01 of the terrain's at the same
// distance from the vessel by day and 1.03 in the evening, its outer edge at the launch pad 1.09 and
// 1.06 (medians of eleven views from 150 m to 4 km, 2026-10-25). Where Parallax draws its grass, lifted
// onto the lawn (Scenery/KscGrass.cs), the grass in the rays shades it itself; past it, the lawn takes
// the meadow's shade as the terrain beside it, as the space centre's lawns away from the launch pad,
// which stood brighter than the terrain round them without it (2026-10-25). The lift holds wherever
// Parallax's grass is drawn or not.
static const float kLawnLift = 1.36f;

// The share of a pixel of the space centre's ground that is its lawn, by its drawn colour: its tarmac
// and concrete are grey (a saturation under 0.10 at the launch pad, 2026-10-19), its lawn a saturated
// yellow-green (0.47, green over red, 2026-10-25), its yellow and orange markings red over green.
float LawnShare(float3 drawn)
{
    float most = max(drawn.r, max(drawn.g, drawn.b));
    float saturation = (most - min(drawn.r, min(drawn.g, drawn.b))) / max(most, 1e-3f);
    return smoothstep(0.15f, 0.30f, saturation) * step(drawn.r, drawn.g);
}

// The space centre's lawn's occlusion as the terrain's beside it: the G-buffer holds 0.85 for Parallax's
// terrain and 0.99 for the lawn (the launch pad and the space centre's lawns from 150 m to 4 km,
// 2026-10-25), which the raytraced diffuse light is multiplied by (ReDefinitionDeferredReflections.shader).
// With the albedo lifted alike (kLawnLift), the lawn took 10 to 37 % more light than the terrain under the
// same sun, and its outer edge at the launch pad, with less grass about it than its middle, stood out
// brighter.
static const float kLawnOcclusion = 0.86f;

// The occlusion the lawn's share of a pixel takes (kLawnOcclusion), 1 for none of it.
float LawnOcclusion(float3 drawn)
{
    return lerp(1.0f, kLawnOcclusion, LawnShare(drawn));
}

// The meadow's shade, or the lawn's lift, at a pixel's drawn colour, as the comment above
// kLawnLift says; `lift` the share of kLawnLift the lawn takes.
float MeadowShade(uint2 pixel, float3 position, bool lawn = false, float lift = 1.0f)
{
    if (!HAS_GBUFFER)
        return 1.0f;
    float range = shadowFadeScale.z;
    float vesselRange = meadowVessel.w;
    float shade = shadowFadeScale.w;
    // A range below 0, for the probe to see where the shade can reach (-redefinitionProbeMeadowFrom): -1 every
    // pixel, -2 every green one, whatever the rays met there.
    bool everyGreen = range < -1.5f;
    if (everyGreen)
        range = 1.0f;
    else if (range < 0.0f)
        return shade;
    float3 drawn = gbuffer0[pixel].rgb;
    bool none = (range <= 0.0f && vesselRange <= 0.0f) || shade <= 0.0f || shade >= 1.0f;
    // Past both of the grass's ranges: the one about the camera, and the one about the vessel.
    float beyond = 1.0f;
    if (range > 0.0f)
        beyond = smoothstep(range * 0.8f, range, length(position));
    if (vesselRange > 0.0f)
        beyond = min(beyond, smoothstep(vesselRange * 0.8f, vesselRange, distance(position, meadowVessel.xyz)));
    // The lawn: as bright as the terrain (kLawnLift), and past the grass in the meadow's shade as the terrain.
    if (lawn && !everyGreen)
    {
        float share = LawnShare(drawn);
        return lerp(1.0f, kLawnLift, share * lift) * (none ? 1.0f : lerp(1.0f, shade, beyond * share));
    }
    if (none)
        return 1.0f;
    float most = max(drawn.r, max(drawn.g, drawn.b));
    float saturation = (most - min(drawn.r, min(drawn.g, drawn.b))) / max(most, 1e-3f);
    // Green: the green channel the brightest, by a margin over red, and saturated.
    float green = smoothstep(0.0f, 0.04f, drawn.g - max(drawn.r, drawn.b)) * smoothstep(0.12f, 0.25f, saturation);
    return lerp(1.0f, shade, green * beyond);
}

// A blade of grass or other scatter: a cut-out, or a legacy-shaded surface that is neither terrain nor
// scenery: Parallax's scatter, and a vessel's parts with KSP's specular shaders too.
bool IsBlade(Hit hit)
{
    if (!hit.found)
        return false;
    if ((hit.flags & RT_INSTANCE_CUTOUT) != 0u)
        return true;
    uint mode = uint(materialData[hit.material].vertexColour.y + 0.5f);
    return (hit.flags & (RT_INSTANCE_TERRAIN | RT_INSTANCE_SCENERY)) == 0u && mode == RT_SURFACE_LEGACY;
}

// `region`: what the camera's ray met (RT_REGION_*), for the probe's surface images.
float4 SurfaceIntent(uint2 pixel, float3 position, float3 normal, out uint mode, out bool terrain, out bool lawn, out float lift,
                     out uint region)
{
    float4 g1 = gbuffer1[pixel];
    terrain = false;
    lawn = false;
    lift = 0.0f;
    region = RT_REGION_OTHER;
    if (IsStockWater(position, normal))
    {
        mode = RT_SURFACE_WATER;
        return g1;
    }
    float distance = length(position);
    float3 direction = position / max(distance, 1e-6f);
    float reach = distance * 1.05f + 2.0f;
    Hit hit = TraceClosest(float3(0.0f, 0.0f, 0.0f), direction, reach);
    // The ground: the terrain the rays see, and past it -- they see it to 30 km
    // (RtKspSources, the terrain's quads) -- what they meet nothing at, as the parts, the
    // buildings and the scatter near the camera are all in their scene.
    // The space centre's lawns too, which Parallax's grass is lifted onto: its scenery (MeadowShade takes
    // the green of it only), not the grass's blades, which shade themselves.
    // A blade the rays meet in front of the drawn surface -- the rays' grass is not the drawn grass, blade for
    // blade -- stands on the ground behind it, which the pixel shows: the ray goes on past it, and past a
    // few more, to the ground, whose shine the pixel takes (IsBlade).
    float front = distance - (distance * 0.005f + 0.05f);
    bool behind = false;
    for (uint pass = 0u; pass < 4u && hit.found && hit.distance < front && IsBlade(hit); pass++)
    {
        float from = hit.distance + 0.01f;
        hit = TraceClosest(direction * from, direction, reach - from);
        hit.distance += from;
        behind = true;
    }
    terrain = !hit.found ? distance > 1000.0f
              : behind && IsBlade(hit) ? hit.distance < front
              : (hit.flags & (RT_INSTANCE_TERRAIN | RT_INSTANCE_SCENERY)) != 0u;
    // The ground the rays see is its undisplaced mesh, Parallax draws it displaced: a
    // looser match for it.
    float tolerance = (hit.flags & RT_INSTANCE_TERRAIN) != 0u ? distance * 0.03f + 2.0f : distance * 0.005f + 0.05f;
    bool near = hit.found && abs(hit.distance - distance) < tolerance;
    uint hitMode = hit.found ? uint(materialData[hit.material].vertexColour.y + 0.5f) : RT_SURFACE_KEEP;
    // The lawn's rules (MeadowShade): the space centre's ground at the drawn surface, where it shines as the
    // terrain (its material's specular colour, RtMaterialIntent.Ground).
    float3 groundShine = near && hitMode == RT_SURFACE_GROUND ? materialData[hit.material].specular : 0.0f;
    lawn = max(groundShine.r, max(groundShine.g, groundShine.b)) > 0.0f;
    // Drawn as the terrain already (Scenery/KscGround; RtMaterialIntent.Ground): neither lifted nor its occlusion
    // taken to the terrain's.
    lift = lawn && materialData[hit.material].vertexColour.w < 0.5f ? 1.0f : 0.0f;
    region = !hit.found ? (behind ? RT_REGION_OTHER_MISSED : RT_REGION_NOTHING)
             : near ? (behind ? RT_REGION_BEHIND_BLADE : (hit.flags & RT_INSTANCE_TERRAIN) != 0u ? RT_REGION_TERRAIN
                       : hitMode == RT_SURFACE_GROUND ? RT_REGION_GROUND : IsBlade(hit) ? RT_REGION_BLADE : RT_REGION_OTHER)
             : RT_REGION_OTHER_MISSED;
    if (!near)
    {
        mode = RT_SURFACE_MISSED;
        // Deferred's own value for every shader without a specular colour, 0.5 / pi grey
        // (ReplacementShader.cginc): a diffuse surface, as a cut-out grate's holes let the
        // ray past it.
        if (all(abs(g1.rgb - 0.5f / kPi) < 0.003f))
            return 0.0f;
        return float4(g1.rgb, min(LegacySmoothness(g1.a, g1.rgb), kMostSmoothness));
    }
    MaterialData material = materialData[hit.material];
    mode = uint(material.vertexColour.y + 0.5f);
    if (mode == RT_SURFACE_DRY)
        return 0.0f;
    if (mode == RT_SURFACE_FIXED)
        return float4(material.specular, material.smoothness);
    if (mode == RT_SURFACE_GROUND)
    {
        // The lawn by its drawn colour (LawnShare). It shines as the terrain beside it, the
        // material's values (RtMaterialIntent.Ground, Parallax's terrain at its gloss there),
        // or without them the G-buffer's (0.22 at a smoothness of 0.013 there, rough); the
        // rest not at all.
        float share = LawnShare(gbuffer0[pixel].rgb);
        if (max(material.specular.r, max(material.specular.g, material.specular.b)) > 0.0f)
            return float4(material.specular * share, material.smoothness * share);
        return float4(g1.rgb * share, g1.a * share);
    }
    if (mode == RT_SURFACE_LEGACY || mode == RT_SURFACE_UNITY_LEGACY)
    {
        float measured = mode == RT_SURFACE_LEGACY ? g1.a : max(g1.r, max(g1.g, g1.b));
        float gloss = saturate(measured / max(material.vertexColour.z, 1e-4f));
        return float4(material.specular * gloss, material.smoothness);
    }
    mode = RT_SURFACE_KEEP;
    return float4(g1.rgb, min(g1.a, kMostSmoothness));
}

// A pixel's surface (SurfaceData): the G-buffer's, with the intended shine where the sun's pass
// found it; else the material of the triangle the camera's ray meets at the depth; else a
// grey guess on the depth's normal, found false.
SurfaceData SurfaceAt(uint2 pixel, float3 position)
{
    SurfaceData surface;
    surface.found = true;
    surface.diffuseShare = 1.0f;
    surface.highlight = 0.0f;
    if (HAS_GBUFFER)
    {
        surface.normal = DecodeNormal(gbuffer2[pixel]);
        if (SURFACE_INTENT)
        {
            // Found by the sun's pass, which runs first (Raytracing.hlsl, SunVisibility).
            float4 intended = output5[pixel];
            surface.smoothness = intended.a;
            surface.specular = intended.rgb;
        }
        else
        {
            float4 g1 = gbuffer1[pixel];
            surface.smoothness = LegacySmoothness(g1.a, g1.rgb);
            surface.specular = g1.rgb;
        }
        surface.albedo = InputColor(gbuffer0[pixel].rgb);
        return surface;
    }
    float distance = length(position);
    Hit hit = TraceClosest(float3(0.0f, 0.0f, 0.0f), position / max(distance, 1e-6f), distance * 1.01f + 0.05f);
    if (hit.found && abs(hit.distance - distance) < distance * 0.005f + 0.05f)
    {
        MaterialData material = materialData[hit.material];
        surface.normal = hit.normal;
        // The space centre's ground matt, as its intended specular colour (IntendedSpecular).
        surface.smoothness = uint(material.vertexColour.y + 0.5f) == RT_SURFACE_GROUND ? 0.0f : material.smoothness;
        surface.specular = IntendedSpecular(material);
        surface.albedo = Albedo(material, hit.uv, hit.color);
        surface.diffuseShare = material.look.x;
        surface.highlight = material.highlight;
        if (material.highlight.w > 0.0f && MATERIAL_TEXTURES && material.texture >= 0)
            surface.highlight.rgb *= materialTextures.SampleLevel(linearWrap, float3(hit.uv, float(material.texture)), 0.0f).a;
        return surface;
    }
    surface.normal = DepthNormal(pixel, position);
    surface.smoothness = 0.2f;
    surface.specular = 0.04f;
    surface.albedo = 0.5f;
    surface.found = false;
    return surface;
}

// A pixel's normal alone: the G-buffer's where there is one, without tracing.
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
