#pragma once

// One frame's copies, barriers and builds of the meshes' structures (RaytracingScene.cpp,
// BuildMeshesLocked; RaytracingMegaGeometry.cpp).

#include "Raytracing.h"
#include "RaytracingMegaGeometry.h"

#include <deque>
#include <unordered_map>
#include <vector>

namespace redefinition
{
    // One frame's work on the meshes' structures. With rtBlasBatch the copies of all its
    // meshes come first, then one barrier for all the buffers they wrote, then every build
    // back to back, each with scratch memory of its own, and the one UAV barrier before the
    // scene's structure (BuildMeshesLocked) -- NVIDIA, "Best Practices for Using NVIDIA RTX
    // Ray Tracing (Updated)": "Consecutively execute ... all BLAS build calls. This allows
    // the driver to parallelize the calls to an extent. Do not place resource barriers
    // between consecutive calls. All BLAS build calls need unique scratch memory to allow
    // execution without barriers. ... you can have a single global UAV barrier before TLAS
    // build to ensure all BLAS builds are completed, regardless of the resource where they
    // reside." Without it each mesh's copies, barrier and build go in turn.
    struct Raytracing::BuildFrame
    {
        UINT slotNumber = 0;
        bool batched = true;
        bool pooled = true;
        int compaction = 0;
        // The mesh being prepared, for the compacted size its build asks for.
        int32_t meshId = 0;
        // The geometry buffers' states on this list. Buffers decay to COMMON once a list is
        // done on the GPU ("Using Resource Barriers to Synchronize Resource States in
        // Direct3D 12", State decay to common), and a copy promotes them to COPY_DEST.
        std::unordered_map<ID3D12Resource*, D3D12_RESOURCE_STATES> states;
        std::vector<D3D12_RESOURCE_BARRIER> barriers;
        // The geometry buffers the queued builds read.
        std::vector<ID3D12Resource*> reads;
        // The copies from the uploads, one where they follow each other in both buffers.
        struct Copy
        {
            ID3D12Resource* destination;
            UINT64 destinationOffset;
            ID3D12Resource* source;
            UINT64 sourceOffset;
            UINT64 size;
        };
        std::vector<Copy> copies;
        void CopyInto(ID3D12Resource* destination, UINT64 destinationOffset, ID3D12Resource* source, UINT64 sourceOffset,
                      UINT64 size)
        {
            if (!copies.empty())
            {
                Copy& last = copies.back();
                if (last.destination == destination && last.source == source
                    && last.destinationOffset + last.size == destinationOffset && last.sourceOffset + last.size == sourceOffset)
                {
                    last.size += size;
                    return;
                }
            }
            copies.push_back({ destination, destinationOffset, source, sourceOffset, size });
        }
        struct Build
        {
            D3D12_RAYTRACING_GEOMETRY_DESC geometry = {};
            D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC desc = {};
            bool askSize = false;
            UINT sizeIndex = 0;
        };
        // A deque, so that a build's inputs keep pointing at its geometry.
        std::deque<Build> builds;
        UINT sizesAsked = 0;
        ID3D12Resource* sizes = nullptr;

        // rtMegaGeometry: the frame's clusters (RaytracingMegaGeometry.cpp), each a
        // CLAS built from triangles in the upload, and its meshes, each a cluster BLAS of
        // its clusters at an address of the mesh's own.
        bool megaGeometry = false;
        std::vector<nvmg::TriangleClusterArgs> clusters;
        struct ClusterMesh
        {
            int32_t id;
            uint32_t firstCluster;
            uint32_t clusterCount;
            D3D12_GPU_VIRTUAL_ADDRESS blas;
        };
        std::vector<ClusterMesh> clusterMeshes;
        uint32_t clusterVerticesMax = 0;
        uint32_t clusterTrianglesMax = 0;
        uint32_t clusterVertices = 0;
        uint32_t clusterTriangles = 0;
        uint32_t clustersPerMeshMax = 0;
    };

}
