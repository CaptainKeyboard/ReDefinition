// The instances the GPU made (Parallax's scatter) into the scene's instance buffers,
// after the CPU's (RaytracingScene.cpp, AddGpuInstancesLocked): each one's descriptor for
// the acceleration structure and the data the passes look its mesh, material and
// normals up by. The transforms come in t7's layout (Raytracing.h, kRtInputInstances);
// one all zero is no instance, its mask 0.

#include "RtContract.h"

// A run of the GPU's instances as AddGpuInstancesLocked writes it (GpuGroup,
// RaytracingScene.cpp): one mesh and material for all of them.
struct Group
{
    // The mesh's structure's address, low half first; its descriptor slot; the material's index
    // among the frame's; where the run starts in t7 and how long it is; its first instance's
    // place among the GPU's instances.
    uint2 blas;
    uint meshSlot;
    uint material;
    uint first;
    uint count;
    uint start;
    uint maskFlags;   // the ray mask in the low byte, the instance's flags above
};

// D3D12_RAYTRACING_INSTANCE_DESC: the transform's rows, InstanceID (24 bits) and
// InstanceMask (8), InstanceContributionToHitGroupIndex (24) and Flags (8), the
// bottom-level structure's address.
struct InstanceDesc
{
    // The fields in the order the comment above gives them.
    float4 rows[3];
    uint idMask;
    uint contributionFlags;
    uint2 blas;
};

// RtCommon.hlsli's InstanceData.
struct InstanceData
{
    // The mesh's descriptor slot, the material's index, the instance's flags, padding, and the
    // normals' matrix in three padded rows.
    uint meshSlot;
    uint material;
    uint flags;
    uint pad;
    float4 normalRows[3];
};

// The groups, t7's transforms, and the scene's descriptions and data, written past the CPU's
// instances.
StructuredBuffer<Group> groups : register(t0, space3);
Texture2D<float4> transforms : register(t1, space3);
RWStructuredBuffer<InstanceDesc> descs : register(u0, space3);
RWStructuredBuffer<InstanceData> data : register(u1, space3);

// The dispatch's root constants (AddGpuInstancesLocked).
cbuffer Step : register(b1)
{
    // How many groups and GPU instances there are, and how many of the CPU's come before them.
    uint groupCount;
    uint gpuCount;
    uint cpuCount;
    uint unused;
};

// The instances to a row of t7, and the instance flags the descriptions take.
static const uint kPerRow = RT_INSTANCES_PER_ROW;
static const uint kCullDisable = 1u;   // D3D12_RAYTRACING_INSTANCE_FLAG_TRIANGLE_CULL_DISABLE
static const uint kNonOpaque = 8u;     // D3D12_RAYTRACING_INSTANCE_FLAG_FORCE_NON_OPAQUE
static const uint kDisableOmms = 32u;  // NVAPI_D3D12_RAYTRACING_INSTANCE_FLAG_DISABLE_OMMS_EX
static const uint kTerrain = RT_INSTANCE_TERRAIN;
static const uint kCutout = RT_INSTANCE_CUTOUT;

// One GPU instance a thread: its group found, its transform read, its description and data
// written at its place after the CPU's.
[numthreads(64, 1, 1)]
void GpuInstances(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gpuCount)
        return;
    // The groups follow each other, their starts rising from 0 (AddGpuInstancesLocked
    // leaves out the empty ones): the last whose start is not past this instance, found
    // by halving [g, end).
    uint g = 0;
    uint end = groupCount;
    while (end - g > 1u)
    {
        uint middle = (g + end) / 2u;
        if (groups[middle].start <= id.x)
            g = middle;
        else
            end = middle;
    }
    Group group = groups[g];
    uint t = group.first + (id.x - group.start);
    uint2 at = uint2((t % kPerRow) * 3, t / kPerRow);
    float4 r0 = transforms[at];
    float4 r1 = transforms[at + uint2(1, 0)];
    float4 r2 = transforms[at + uint2(2, 0)];
    bool live = any(r0 != 0.0f) || any(r1 != 0.0f) || any(r2 != 0.0f);
    uint index = cpuCount + id.x;

    InstanceDesc desc;
    desc.rows[0] = r0;
    desc.rows[1] = r1;
    desc.rows[2] = r2;
    desc.idMask = (index & 0xFFFFFFu) | ((live ? group.maskFlags & 0xFFu : 0u) << 24);
    uint flags = group.maskFlags >> 8;
    const bool nonOpaque = (flags & (kTerrain | kCutout)) != 0 && (flags & RT_GROUP_NO_NON_OPAQUE) == 0u;
    desc.contributionFlags = (kCullDisable | (nonOpaque ? kNonOpaque : 0u) | ((flags & RT_GROUP_DISABLE_OMMS) != 0u ? kDisableOmms : 0u)) << 24;
    desc.blas = group.blas;
    descs[index] = desc;

    // The normals' matrix, the inverse transpose of the 3x3: its cofactors over the
    // determinant, as NormalMatrix in RaytracingScene.cpp.
    float3 c0 = cross(r1.xyz, r2.xyz);
    float3 c1 = cross(r2.xyz, r0.xyz);
    float3 c2 = cross(r0.xyz, r1.xyz);
    float det = dot(r0.xyz, c0);
    float s = abs(det) > 1e-20f ? 1.0f / det : 0.0f;
    InstanceData item;
    item.meshSlot = group.meshSlot;
    item.material = group.material;
    item.flags = (group.maskFlags >> 8) & ~RT_GROUP_SERVICE_FLAGS;
    item.pad = 0;
    item.normalRows[0] = float4(c0 * s, 0.0f);
    item.normalRows[1] = float4(c1 * s, 0.0f);
    item.normalRows[2] = float4(c2 * s, 0.0f);
    data[index] = item;
}
