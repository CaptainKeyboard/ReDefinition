#pragma once

// The opacity micromaps' CPU side (RaytracingOmmBake.cpp): the order of an OMM's micro-triangles
// and a conservative baker from a mesh's texture coordinates and the material texture's
// coverage, with no Direct3D or NVAPI in it, so that the harness holds it against the driver.
//
// What the baker bakes for: the passes decide a hit on a cut-out surface by the coverage in
// the alpha of the material's 64 x 64 texture, sampled bilinear and wrapped
// (RtCommon.hlsli, CandidateCounts): a ray stops with that chance. A micro-triangle whose
// texels all have coverage 0 is therefore transparent for sure, and one whose texels all have
// coverage 1 opaque for sure; any other is unknown and left to the shader, which decides it as
// before. "It's always safe in terms of correctness to mark any micro-triangle as unknown and
// fall back to shader for final evaluation" (OMM SDK, integration guide); the picture does
// not change.

#include <cstddef>
#include <cstdint>
#include <vector>

namespace redefinition
{
    namespace omm
    {
        // A micro-triangle's state, as the OMM stores it in two bits (4-state) or one (2-state):
        // D3D12_RAYTRACING_OPACITY_MICROMAP_STATE.
        enum State : uint8_t
        {
            // A ray passes, or stops; unknown, left to the shader, with the state a 2-state
            // reading takes it as.
            kTransparent = 0,
            kOpaque = 1,
            kUnknownTransparent = 2,
            kUnknownOpaque = 3,
        };

        // The coverage texture's side (RtContract.h, RT_MATERIAL_TEXTURE_SIZE), and its alpha
        // channel.
        constexpr uint32_t kTextureSize = 64;

        // The micro-triangles of an OMM subdivided `level` times: 4^level.
        constexpr uint32_t MicroTriangleCount(uint32_t level)
        {
            return 1u << (2u * level);
        }

        // The micro-triangle at `index` of an OMM subdivided `level` times, along the OMM's
        // space-filling curve, as its three corners on the base triangle's barycentric grid:
        // (u, v) with the triangle's vertex 0 at (0, 0), vertex 1 at (2^level, 0) and vertex 2
        // at (0, 2^level), u and v being the weights of vertices 1 and 2 -- the hit's
        // barycentrics in a ray query. The corners keep the order the curve enters them.
        struct Corners
        {
            // The three corners' u, then their v, in that order.
            int32_t u[3];
            int32_t v[3];
        };
        // The corners from a table made once per level, levels past 8 taken as 8; `index` must
        // lie below the level's MicroTriangleCount.
        const Corners& MicroTriangle(uint32_t level, uint32_t index);

        // The index of the micro-triangle that holds the barycentrics (u, v) of a hit, for
        // a level: the inverse of MicroTriangle.
        uint32_t MicroTriangleAt(uint32_t level, double u, double v);

        // One triangle's texture coordinates: u, v of its three vertices.
        struct TriangleUv
        {
            // Per vertex, in the triangle's order, its u and v.
            float uv[3][2];
        };

        // The baker's result for a mesh: the OMMs, packed, and for each triangle either the
        // index of its OMM among them or a special index (RaytracingOmm.h, nvomm::kIndex*).
        // Every OMM is 4-state: two bits a micro-triangle, the first micro-triangle in the
        // lowest bits of the first byte.
        struct Entry
        {
            // An OMM: where it starts in the packed data, its level, and its format
            // (kFormat4State), as nvomm::Desc takes them.
            uint32_t byteOffset;
            uint16_t level;
            uint16_t format;
        };
        // The result itself, as the comment above says, with the counts the log reports.
        struct Baked
        {
            // The packed OMMs, their entries, and per triangle its OMM's index or special index.
            std::vector<uint8_t> data;
            std::vector<Entry> entries;
            std::vector<int32_t> triangleIndex;
            // OMMs by their level.
            uint32_t levelCount[13] = {};
            // Triangles by how they came out: each uniform state as a special index, and those
            // with an OMM of their own (or one shared with another).
            uint32_t uniform[4] = {};
            uint32_t withOmm = 0;
            // Micro-triangles of the OMMs by their state.
            uint64_t microTriangles[4] = {};
        };

        // The 4-state format's value, as NVAPI numbers it (nvomm::kFormat4State).
        constexpr uint16_t kFormat4State = 2;

        // The bytes an OMM of `level` takes, in two bits a micro-triangle, at least one.
        constexpr uint32_t OmmBytes(uint32_t level)
        {
            return level < 2 ? 1u : MicroTriangleCount(level) / 4u;
        }
        // The alignment each OMM starts at in the packed data.
        constexpr uint32_t kOmmAlignment = 4;

        // How the baker is asked: the finest level (at most 8), and the most micro-triangles
        // it may make in all, past which the levels are lowered.
        struct Settings
        {
            // The two the comment above names, in its order.
            uint32_t maxLevel = 6;
            uint64_t maxMicroTriangles = 1u << 22;
        };

        // The state of a micro-triangle of a triangle as the baker has it, `index` being its
        // place along the curve at `level` (the OMM's own level, or any for a special index).
        State StateOf(const Baked& baked, size_t triangle, uint32_t index);

        // alpha: kTextureSize^2 bytes, row 0 first; uvs: one TriangleUv a triangle.
        void Bake(const TriangleUv* triangles, size_t count, const uint8_t* alpha, const Settings& settings, Baked& out);

        // The coverage the passes read at a texture coordinate: the alpha, bilinear and
        // wrapped as the sampler (linearWrap) takes it; 0 to 1.
        double Coverage(const uint8_t* alpha, double u, double v);
    }
}
