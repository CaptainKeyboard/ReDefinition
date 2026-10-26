// The harness's check of an opacity micromap against the driver (RaytracingHarness.cpp,
// RaytracingOmmHarness.cpp): a ray at the centre of each micro-triangle of each of a grid of
// triangles, every triangle with an OMM of its own. Compiled to DXIL by the build
// (CMakeLists.txt) as the passes are, with the same ray query and no shader model 6.9.
//
// Thread t is the ray for triangle t / cells at the centre of micro-triangle t % cells.
RaytracingAccelerationStructure scene : register(t0);
StructuredBuffer<float2> centres : register(t1);
RWStructuredBuffer<uint> table : register(u0);
RWStructuredBuffer<uint> counts : register(u1);

// What the harness sets per dispatch: the grid, the rays, what to expect, whether to fill the table, and
// the ray flags.
cbuffer Constants : register(b0)
{
    uint cells;       // micro-triangles of a triangle
    uint columns;     // triangles in a row of the grid
    uint total;       // rays
    // 0: the micro-triangle k of triangle k is the one that is opaque; 1: all that is
    // opaque is the triangle's, so any ray is expected to hit; 2: none is opaque.
    uint expectation;
    uint writeTable;
    uint rayFlags;
};

// One ray per thread up the grid; its result into table where asked, and into counts: misses, hits, rays
// that met a candidate, then the rays whose hit was not the expected one, the first 60 of them as
// triangle << 16 | micro-triangle.
[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    const uint t = id.x + id.y * 65535u * 256u;
    if (t >= total)
        return;
    const uint tri = t / cells;
    const uint micro = t % cells;
    const float2 centre = centres[micro];
    RayDesc ray;
    ray.Origin = float3(float(tri % columns) * 2.0f + centre.x, float(tri / columns) * 2.0f + centre.y, -1.0f);
    ray.Direction = float3(0.0f, 0.0f, 1.0f);
    ray.TMin = 0.0f;
    ray.TMax = 10.0f;
    RayQuery<RAY_FLAG_NONE> query;
    query.TraceRayInline(scene, rayFlags, 0xFF, ray);
    uint candidates = 0;
    while (query.Proceed())
    {
        ++candidates;
        // An unknown micro-triangle is a candidate; the shader decides it, here as the
        // passes would: committed.
        query.CommitNonOpaqueTriangleHit();
    }
    // bit 0: a triangle hit was committed, bits 1-4: the candidates met, at most 15.
    const uint result = (query.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 1u : 0u) | (min(candidates, 15u) << 1);
    if (writeTable != 0u)
        table[t] = result;
    const bool hit = (result & 1u) != 0u;
    const bool candidate = (result >> 1) != 0u;
    InterlockedAdd(counts[hit ? 1 : 0], 1u);
    if (candidate)
        InterlockedAdd(counts[2], 1u);
    const bool expected = expectation == 0u ? tri == micro : expectation == 1u;
    if (hit != expected)
    {
        uint slot;
        InterlockedAdd(counts[3], 1u, slot);
        if (slot < 60u)
            counts[4 + slot] = (tri << 16) | micro;
    }
}
