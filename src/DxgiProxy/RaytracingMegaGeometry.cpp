// RTX Mega Geometry for the meshes' structures (Config.h, rtMegaGeometry), NVIDIA's
// cluster acceleration structures through NVAPI (RaytracingMegaGeometry.h): each run of 256
// triangles of a new mesh a CLAS, built from the triangles in the upload, all of a frame's in
// one call; then each mesh a cluster BLAS of its CLAS, in a call of its own (see
// QueueClustersLocked for why). "Mega Geometry is supported on all RTX GPUs, starting with
// Turing" (NVIDIA RTX Blackwell GPU Architecture); the driver says whether it offers the
// operations (NvAPI_D3D12_GetRaytracingCaps,
// NVAPI_D3D12_RAYTRACING_CAPS_TYPE_CLUSTER_OPERATIONS), and without them every mesh is
// built the usual way (RaytracingScene.cpp).
//
// Why clusters, and not the partitioned TLAS: the load spike is the build of many small
// meshes at once (a vessel's parts, a flight's first frames), which the cluster
// operations do in two calls whatever their number; the partitioned TLAS saves rebuilding
// the instances that did not change, and every instance's transform here is relative to the
// camera and changes with every frame.
//
// A cluster numbers its triangles itself, and gives its number within the mesh as the
// triangle's geometry index (baseGeometryIndexAndFlags): the passes find a hit's triangle in
// the mesh's index buffer as geometry index * 256 + primitive index (RtCommon.hlsli,
// RT_TRIANGLE), which for every other structure, of one geometry, is the primitive index.
// A run of 256 triangles with more than 256 vertices fits no cluster; such a mesh is built
// the usual way.

#include "Raytracing.h"

#include "Config.h"
#include "D3d12Util.h"
#include "Log.h"
#include "NvidiaGpu.h"
#include "RaytracingBuildFrame.h"
#include "RaytracingInternal.h"

#include <algorithm>
#include <cstring>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // The triangles of a cluster, as RtCommon.hlsli's RT_TRIANGLE counts them.
        constexpr uint32_t kClusterTriangles = nvmg::kMaxClusterTriangles;

        bool Requirements(void* function, ID3D12Device5* device, const nvmg::Inputs& inputs, nvmg::RequirementsInfo& info)
        {
            const auto get = reinterpret_cast<nvmg::GetRequirementsFn>(function);
            nvmg::RequirementsParams params = {};
            params.version = nvmg::Version(sizeof(params), 1);
            params.pInput = &inputs;
            params.pInfo = &info;
            info = {};
            return get != nullptr && get(device, &params) == 0;
        }

        // The inputs of the cluster BLAS of one mesh of clusterCount clusters, which the size
        // and scratch asked for it and the call that builds it both take.
        nvmg::Inputs ClusterBlasInputs(uint32_t clusterCount)
        {
            nvmg::Inputs inputs = {};
            inputs.maxArgCount = 1;
            inputs.flags = nvmg::kFlagFastTrace;
            inputs.type = nvmg::kTypeBuildBlasFromClas;
            inputs.mode = nvmg::kModeExplicitDestinations;
            inputs.clasDesc.maxTotalClasCount = clusterCount;
            inputs.clasDesc.maxClasCountPerArg = clusterCount;
            return inputs;
        }
    }

    bool Raytracing::MegaGeometryReadyLocked()
    {
        if (blasMemory.clusterSupport >= 0)
            return blasMemory.clusterSupport == 1;
        const auto caps = reinterpret_cast<nvmg::GetRaytracingCapsFn>(NvapiInterface(nvmg::kGetRaytracingCaps));
        blasMemory.getRequirements = NvapiInterface(nvmg::kGetRequirements);
        blasMemory.execute = NvapiInterface(nvmg::kExecute);
        uint32_t clusterCaps = 0;
        const int status = caps != nullptr ? caps(device.Get(), nvmg::kCapsClusterOperations, &clusterCaps, sizeof(clusterCaps)) : -1;
        const bool there = status == 0 && (clusterCaps & nvmg::kCapStandard) != 0 && blasMemory.getRequirements != nullptr
                           && blasMemory.execute != nullptr;
        blasMemory.clusterSupport = there ? 1 : 0;
        LogLine(there ? std::string("Raytracing: RTX Mega Geometry's cluster operations there (NVAPI), for rtMegaGeometry=1")
                      : "Raytracing: no RTX Mega Geometry cluster operations (NVAPI "
                            + std::string(caps == nullptr ? "without NvAPI_D3D12_GetRaytracingCaps"
                                                          : "status " + std::to_string(status) + ", caps "
                                                                + std::to_string(clusterCaps))
                            + "); rtMegaGeometry=1 builds the usual way");
        return there;
    }

    bool Raytracing::QueueClustersLocked(BuildFrame& frame, Mesh& mesh, const PendingMesh& pending, Slot& slot)
    {
        // The runs of 256 triangles in the mesh's order, each with its vertices numbered
        // anew from 0, the order they first come in.
        const uint32_t triangles = static_cast<uint32_t>(pending.indices.size() / 3);
        const uint32_t clusterCount = (triangles + kClusterTriangles - 1) / kClusterTriangles;
        if (clusterCount == 0 || clusterCount > (1u << 24))
            return false;
        std::vector<uint32_t> firstVertex(clusterCount + 1, 0);
        std::vector<uint32_t> vertices;
        vertices.reserve(pending.vertexCount + clusterCount * 16);
        std::vector<uint8_t> localIndices(pending.indices.size());
        std::vector<uint32_t> seenIn(pending.vertexCount, UINT32_MAX);
        std::vector<uint8_t> localOf(pending.vertexCount, 0);
        for (uint32_t c = 0; c < clusterCount; ++c)
        {
            firstVertex[c] = static_cast<uint32_t>(vertices.size());
            const uint32_t end = std::min(triangles, (c + 1) * kClusterTriangles) * 3;
            for (uint32_t i = c * kClusterTriangles * 3; i < end; ++i)
            {
                const uint32_t v = pending.indices[i];
                if (seenIn[v] != c)
                {
                    if (vertices.size() - firstVertex[c] >= nvmg::kMaxClusterVertices)
                        return false;
                    seenIn[v] = c;
                    localOf[v] = static_cast<uint8_t>(vertices.size() - firstVertex[c]);
                    vertices.push_back(v);
                }
                localIndices[i] = localOf[v];
            }
        }
        firstVertex[clusterCount] = static_cast<uint32_t>(vertices.size());

        // The cluster BLAS' size and scratch for its count of clusters, from the driver, once a
        // count. "For NVAPI_D3D12_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_MODE_EXPLICIT_DESTINATIONS
        // this will be one per object" (..._REQUIREMENTS_INFO) -- for the inputs the operation
        // then executes with (..._OPERATION_DESC: "The inputs previously used with
        // NvAPI_D3D12_GetRaytracingMultiIndirectClusterOperationRequirementsInfo"). The size
        // follows the call's maxTotalClasCount alone, whatever the argument count and the most
        // clusters an argument takes (measured on the driver), so a call for several meshes
        // would ask every destination for the size of all their clusters together: each mesh
        // is built in a call of its own (FlushClustersLocked), with these inputs.
        auto known = blasMemory.clusterBlasSizes.find(clusterCount);
        if (known == blasMemory.clusterBlasSizes.end())
        {
            nvmg::RequirementsInfo info = {};
            if (!Requirements(blasMemory.getRequirements, device.Get(), ClusterBlasInputs(clusterCount), info)
                || info.resultDataMaxSizeInBytes == 0)
                return false;
            known = blasMemory.clusterBlasSizes
                        .emplace(clusterCount, BlasMemory::ClusterBlasSize{ info.resultDataMaxSizeInBytes, info.scratchDataSizeInBytes })
                        .first;
        }

        // The clusters' vertices and indices, in the upload: the build reads them there, and
        // "The GPU-side input resources are not referenced after the build has concluded"
        // (NvAPI_D3D12_RaytracingExecuteMultiIndirectClusterOperation).
        const UINT64 positionsBytes = static_cast<UINT64>(vertices.size()) * 3 * sizeof(float);
        ID3D12Resource* upload = nullptr;
        UINT64 offset = 0;
        uint8_t* mapped = nullptr;
        if (!UploadSpaceLocked(frame, slot, positionsBytes + localIndices.size(), upload, offset, mapped))
            return false;
        if (!MakeBlasLocked(known->second.result, frame.pooled, mesh.blas))
            return false;
        for (size_t k = 0; k < vertices.size(); ++k)
            memcpy(mapped + k * 3 * sizeof(float), &pending.positions[static_cast<size_t>(vertices[k]) * 3], 3 * sizeof(float));
        memcpy(mapped + positionsBytes, localIndices.data(), localIndices.size());
        const D3D12_GPU_VIRTUAL_ADDRESS address = upload->GetGPUVirtualAddress() + offset;

        BuildFrame::ClusterMesh queued = {};
        queued.id = frame.meshId;
        queued.firstCluster = static_cast<uint32_t>(frame.clusters.size());
        queued.clusterCount = clusterCount;
        queued.blas = mesh.blas.address;
        frame.clusterMeshes.push_back(queued);
        for (uint32_t c = 0; c < clusterCount; ++c)
        {
            const uint32_t count = std::min(triangles - c * kClusterTriangles, kClusterTriangles);
            const uint32_t vertexCount = firstVertex[c + 1] - firstVertex[c];
            nvmg::TriangleClusterArgs args = {};
            args.clusterId = c;
            args.triangleCount = count;
            args.vertexCount = vertexCount;
            args.indexFormat = nvmg::kIndexFormat8Bit;
            // The cluster's number within the mesh as the geometry index; opaque, as the
            // usual structures' geometry is (RaytracingScene.cpp, GeometryOf).
            args.baseGeometryIndexAndFlags = c | nvmg::kGeometryFlagOpaque;
            args.indexBuffer = address + positionsBytes + static_cast<UINT64>(c) * kClusterTriangles * 3;
            args.vertexBuffer = address + static_cast<UINT64>(firstVertex[c]) * 3 * sizeof(float);
            frame.clusters.push_back(args);
            frame.clusterTrianglesMax = std::max(frame.clusterTrianglesMax, count);
            frame.clusterVerticesMax = std::max(frame.clusterVerticesMax, vertexCount);
        }
        frame.clusterTriangles += triangles;
        frame.clusterVertices += static_cast<uint32_t>(vertices.size());
        frame.clustersPerMeshMax = std::max(frame.clustersPerMeshMax, clusterCount);
        return true;
    }

    void Raytracing::FlushClustersLocked(BuildFrame& frame, Slot& slot)
    {
        if (frame.clusters.empty())
            return;
        const uint32_t clusterCount = static_cast<uint32_t>(frame.clusters.size());
        const uint32_t meshCount = static_cast<uint32_t>(frame.clusterMeshes.size());

        // The CLAS: the driver places them in one range and writes their addresses
        // (MODE_IMPLICIT_DESTINATIONS), which the cluster BLAS' arguments point at.
        nvmg::Inputs clas = {};
        clas.maxArgCount = clusterCount;
        clas.flags = nvmg::kFlagFastTrace;
        clas.type = nvmg::kTypeBuildClasFromTriangles;
        clas.mode = nvmg::kModeImplicitDestinations;
        clas.trianglesDesc.vertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
        clas.trianglesDesc.maxGeometryIndexValue = frame.clustersPerMeshMax - 1;
        clas.trianglesDesc.maxUniqueGeometryCountPerArg = 1;
        clas.trianglesDesc.maxTriangleCountPerArg = frame.clusterTrianglesMax;
        clas.trianglesDesc.maxVertexCountPerArg = frame.clusterVerticesMax;
        clas.trianglesDesc.maxTotalTriangleCount = frame.clusterTriangles;
        clas.trianglesDesc.maxTotalVertexCount = frame.clusterVertices;
        clas.trianglesDesc.minPositionTruncateBitCount = 0;

        nvmg::RequirementsInfo clasInfo = {};
        BlasMemory::ClasBatch batch;
        BlasMemory::PerSlot& perSlot = blasMemory.slots[frame.slotNumber];
        const UINT64 arraysSize = static_cast<UINT64>(clusterCount + meshCount) * sizeof(D3D12_GPU_VIRTUAL_ADDRESS);
        const UINT64 argsSize = static_cast<UINT64>(clusterCount) * sizeof(nvmg::TriangleClusterArgs);
        const UINT64 blasArgsSize = static_cast<UINT64>(meshCount) * sizeof(nvmg::ClusterArgs);
        const UINT64 destinationsSize = static_cast<UINT64>(meshCount) * sizeof(D3D12_GPU_VIRTUAL_ADDRESS);
        D3D12_GPU_VIRTUAL_ADDRESS clasScratch = 0;
        // Each mesh's cluster BLAS has scratch of its own, so that the calls follow each other
        // without a barrier between them ("All BLAS build calls need unique scratch memory",
        // RaytracingBuildFrame.h).
        std::vector<D3D12_GPU_VIRTUAL_ADDRESS> blasScratch(meshCount, 0);
        ID3D12Resource* upload = nullptr;
        UINT64 uploadOffset = 0;
        uint8_t* mapped = nullptr;
        bool ok = Requirements(blasMemory.getRequirements, device.Get(), clas, clasInfo)
                  && clasInfo.resultDataMaxSizeInBytes > 0
                  && blasMemory.blas.Allocate(device.Get(), clasInfo.resultDataMaxSizeInBytes, batch.range)
                  && ScratchLocked(frame, slot, clasInfo.scratchDataSizeInBytes, clasScratch);
        for (uint32_t i = 0; ok && i < meshCount; ++i)
        {
            const auto size = blasMemory.clusterBlasSizes.find(frame.clusterMeshes[i].clusterCount);
            ok = size != blasMemory.clusterBlasSizes.end() && ScratchLocked(frame, slot, size->second.scratch, blasScratch[i]);
        }
        if (ok && perSlot.clusterArraysSize < arraysSize)
        {
            // The slot's list before is done with it.
            if (perSlot.clusterArrays != nullptr)
                slot.held.push_back(perSlot.clusterArrays);
            perSlot.clusterArraysSize = std::max<UINT64>(arraysSize * 2, 65536);
            ok = EnsureDefaultBuffer(perSlot.clusterArrays, perSlot.clusterArraysSize, D3D12_RESOURCE_STATE_COMMON,
                                     D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS);
            if (!ok)
                perSlot.clusterArraysSize = 0;
        }
        ok = ok && UploadSpaceLocked(frame, slot, argsSize + blasArgsSize + destinationsSize, upload, uploadOffset, mapped);

        // The meshes with no cluster structure, which are built the usual way from their
        // geometry, uploaded already (BuildMeshLocked), once the rest is recorded.
        std::vector<const BuildFrame::ClusterMesh*> usual;
        if (!ok)
        {
            // The ranges taken go once the list is done.
            SetProblem("RTX Mega Geometry's cluster build could not be made; its " + std::to_string(meshCount)
                       + " meshes are built the usual way, as the rest from here on");
            blasMemory.clusterSupport = 0;
            blasMemory.blas.Free(batch.range, fenceValue + 1);
            for (const BuildFrame::ClusterMesh& queued : frame.clusterMeshes)
                usual.push_back(&queued);
        }
        else
        {
            const D3D12_GPU_VIRTUAL_ADDRESS arrays = perSlot.clusterArrays->GetGPUVirtualAddress();
            const D3D12_GPU_VIRTUAL_ADDRESS uploadAddress = upload->GetGPUVirtualAddress() + uploadOffset;
            memcpy(mapped, frame.clusters.data(), static_cast<size_t>(argsSize));
            nvmg::ClusterArgs* blasArgs = reinterpret_cast<nvmg::ClusterArgs*>(mapped + argsSize);
            D3D12_GPU_VIRTUAL_ADDRESS* destinations =
                reinterpret_cast<D3D12_GPU_VIRTUAL_ADDRESS*>(mapped + argsSize + blasArgsSize);
            for (uint32_t i = 0; i < meshCount; ++i)
            {
                const BuildFrame::ClusterMesh& queued = frame.clusterMeshes[i];
                blasArgs[i].clusterCount = queued.clusterCount;
                blasArgs[i].reserved = 0;
                blasArgs[i].clusterVAs = arrays + static_cast<UINT64>(queued.firstCluster) * sizeof(D3D12_GPU_VIRTUAL_ADDRESS);
                destinations[i] = queued.blas;
            }

            // The destinations into the slot's arrays after the CLAS addresses, which the
            // calls take "in D3D12_RESOURCE_STATE_UNORDERED_ACCESS state"; the arguments stay
            // in the upload, which is in GENERIC_READ, NON_PIXEL_SHADER_RESOURCE among it.
            const UINT64 destinationsAt = static_cast<UINT64>(clusterCount) * sizeof(D3D12_GPU_VIRTUAL_ADDRESS);
            list->CopyBufferRegion(perSlot.clusterArrays.Get(), destinationsAt, upload,
                                   uploadOffset + argsSize + blasArgsSize, destinationsSize);
            const D3D12_RESOURCE_BARRIER toWrite = TransitionBarrier(
                perSlot.clusterArrays.Get(), D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_UNORDERED_ACCESS);
            list->ResourceBarrier(1, &toWrite);

            const auto execute = reinterpret_cast<nvmg::ExecuteFn>(blasMemory.execute);
            nvmg::OperationDesc clasDesc = {};
            clasDesc.inputs = clas;
            clasDesc.batchResultData = blasMemory.blas.Address(batch.range);
            clasDesc.batchScratchData = clasScratch;
            clasDesc.destinationAddressArray = { arrays, sizeof(D3D12_GPU_VIRTUAL_ADDRESS) };
            clasDesc.indirectArgArray = { uploadAddress, sizeof(nvmg::TriangleClusterArgs) };
            nvmg::ExecuteParams clasParams = {};
            clasParams.version = nvmg::Version(sizeof(clasParams), 1);
            clasParams.pDesc = &clasDesc;
            const int clasStatus = execute(list.Get(), &clasParams);

            const D3D12_RESOURCE_BARRIER written = UavBarrier(nullptr);
            list->ResourceBarrier(1, &written);

            // The cluster BLAS, each at the address of its mesh's own
            // (MODE_EXPLICIT_DESTINATIONS), so that the scene's instances can name it, in a
            // call of its own with the inputs its destination was sized for.
            int failedStatus = 0;
            std::vector<bool> done(meshCount, false);
            for (uint32_t i = 0; clasStatus == 0 && i < meshCount; ++i)
            {
                nvmg::OperationDesc blasDesc = {};
                blasDesc.inputs = ClusterBlasInputs(frame.clusterMeshes[i].clusterCount);
                blasDesc.batchScratchData = blasScratch[i];
                blasDesc.destinationAddressArray = { arrays + destinationsAt + static_cast<UINT64>(i) * sizeof(D3D12_GPU_VIRTUAL_ADDRESS),
                                                     sizeof(D3D12_GPU_VIRTUAL_ADDRESS) };
                blasDesc.indirectArgArray = { uploadAddress + argsSize + static_cast<UINT64>(i) * sizeof(nvmg::ClusterArgs),
                                              sizeof(nvmg::ClusterArgs) };
                nvmg::ExecuteParams blasParams = {};
                blasParams.version = nvmg::Version(sizeof(blasParams), 1);
                blasParams.pDesc = &blasDesc;
                const int status = execute(list.Get(), &blasParams);
                done[i] = status == 0;
                if (status != 0)
                    failedStatus = status;
            }
            if (clasStatus != 0 || failedStatus != 0)
            {
                SetProblem("RTX Mega Geometry's cluster build returned NVAPI status " + std::to_string(clasStatus) + " and "
                           + std::to_string(failedStatus) + "; the meshes it failed for are built the usual way, as the rest"
                           + " from here on");
                blasMemory.clusterSupport = 0;
            }

            // The batch of CLAS, held by the meshes built of them.
            const uint32_t id = blasMemory.nextClasBatch++;
            for (uint32_t i = 0; i < meshCount; ++i)
            {
                const BuildFrame::ClusterMesh& queued = frame.clusterMeshes[i];
                if (!done[i])
                {
                    usual.push_back(&queued);
                    continue;
                }
                auto found = meshes.find(queued.id);
                if (found == meshes.end() || found->second.blas.address != queued.blas)
                    continue;
                found->second.clasBatch = id;
                ++batch.meshes;
            }
            if (batch.meshes == 0)
                blasMemory.blas.Free(batch.range, fenceValue + 1);
            else
                blasMemory.clasBatches[id] = batch;
            std::lock_guard<std::mutex> status(statusLock);
            clusteredMeshes += batch.meshes;
        }

        // The structure that was not built goes, and one is built the usual way in its place
        // (BuildMeshLocked: fast trace, not compacted) from the geometry buffers, which
        // FlushBuildsLocked has copied to the GPU and put in NON_PIXEL_SHADER_RESOURCE.
        for (const BuildFrame::ClusterMesh* queued : usual)
        {
            auto found = meshes.find(queued->id);
            if (found == meshes.end() || found->second.blas.address != queued->blas)
                continue;
            Mesh& mesh = found->second;
            RetireBlasLocked(mesh.blas, slot);
            mesh.built = false;
            mesh.refine = kRefineNone;
            D3D12_RAYTRACING_GEOMETRY_DESC geometry = {};
            geometry.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
            geometry.Flags = D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
            geometry.Triangles.VertexBuffer.StartAddress = mesh.positionsBuffer->GetGPUVirtualAddress() + mesh.positionsOffset;
            geometry.Triangles.VertexBuffer.StrideInBytes = 3 * sizeof(float);
            geometry.Triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
            geometry.Triangles.VertexCount = mesh.vertexCount;
            geometry.Triangles.IndexBuffer = mesh.indicesBuffer->GetGPUVirtualAddress() + mesh.indicesOffset;
            geometry.Triangles.IndexFormat = DXGI_FORMAT_R32_UINT;
            geometry.Triangles.IndexCount = mesh.indexCount;
            D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC build = {};
            build.Inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
            build.Inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
            build.Inputs.NumDescs = 1;
            build.Inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
            build.Inputs.pGeometryDescs = &geometry;
            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
            device->GetRaytracingAccelerationStructurePrebuildInfo(&build.Inputs, &info);
            if (info.ResultDataMaxSizeInBytes == 0 || !MakeBlasLocked(info.ResultDataMaxSizeInBytes, frame.pooled, mesh.blas)
                || !ScratchLocked(frame, slot, info.ScratchDataSizeInBytes, build.ScratchAccelerationStructureData))
            {
                RetireBlasLocked(mesh.blas, slot);
                SetProblem("a mesh's acceleration structure could not be made after its clusters failed");
                continue;
            }
            build.DestAccelerationStructureData = mesh.blas.address;
            list->BuildRaytracingAccelerationStructure(&build, 0, nullptr);
            mesh.built = true;
        }
    }

    void Raytracing::ReleaseClustersLocked(Mesh& mesh, UINT64 freeAt)
    {
        if (mesh.clasBatch == 0)
            return;
        auto found = blasMemory.clasBatches.find(mesh.clasBatch);
        mesh.clasBatch = 0;
        if (found == blasMemory.clasBatches.end() || --found->second.meshes > 0)
            return;
        blasMemory.blas.Free(found->second.range, freeAt);
        blasMemory.clasBatches.erase(found);
    }
}
