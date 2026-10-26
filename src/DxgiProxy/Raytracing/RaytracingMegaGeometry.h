#pragma once

// RTX Mega Geometry's cluster operations through NVAPI (RaytracingMegaGeometry.cpp; Config.h,
// rtMegaGeometry). Declared from NVIDIA's NVAPI headers (MIT; nvapi.h and nvapi_interface.h,
// github.com/NVIDIA/nvapi, licenses/NVIDIA-MIT.txt): the functions' ids for nvapi64.dll's
// nvapi_QueryInterface (NvidiaGpu.h, NvapiInterface) and the structures they take, field
// for field, under shorter names; each NVAPI name beside it. Versions are
// MAKE_NVAPI_VERSION -- the structure's size with the version number in the high word.

#include <windows.h>

#include <d3d12.h>

#include <cstdint>

namespace redefinition
{
    namespace nvmg
    {
        // The functions' ids for nvapi_QueryInterface (nvapi_interface.h), each NVAPI name beside it.
        constexpr uint32_t kGetRaytracingCaps = 0x85a6c2a0;   // NvAPI_D3D12_GetRaytracingCaps
        // NvAPI_D3D12_GetRaytracingMultiIndirectClusterOperationRequirementsInfo
        constexpr uint32_t kGetRequirements = 0x5c9163f4;
        // NvAPI_D3D12_RaytracingExecuteMultiIndirectClusterOperation
        constexpr uint32_t kExecute = 0x67c798af;

        constexpr int kCapsClusterOperations = 3;   // NVAPI_D3D12_RAYTRACING_CAPS_TYPE_CLUSTER_OPERATIONS
        constexpr uint32_t kCapStandard = 1;         // NVAPI_D3D12_RAYTRACING_CLUSTER_OPERATIONS_CAP_STANDARD

        constexpr uint32_t kFlagFastTrace = 1u << 0;              // ..._CLUSTER_OPERATION_FLAG_FAST_TRACE
        constexpr uint32_t kTypeBuildBlasFromClas = 1;            // ..._CLUSTER_OPERATION_TYPE_BUILD_BLAS_FROM_CLAS
        constexpr uint32_t kTypeBuildClasFromTriangles = 2;       // ..._CLUSTER_OPERATION_TYPE_BUILD_CLAS_FROM_TRIANGLES
        constexpr uint32_t kModeImplicitDestinations = 0;         // ..._CLUSTER_OPERATION_MODE_IMPLICIT_DESTINATIONS
        constexpr uint32_t kModeExplicitDestinations = 1;         // ..._CLUSTER_OPERATION_MODE_EXPLICIT_DESTINATIONS
        constexpr uint32_t kIndexFormat8Bit = 1;                  // ..._CLUSTER_OPERATION_INDEX_FORMAT_8BIT
        constexpr uint32_t kGeometryFlagOpaque = 1u << 31;        // ..._CLUSTER_OPERATION_GEOMETRY_FLAG_OPAQUE
        // NVAPI_D3D12_RAYTRACING_CLAS_BYTE_ALIGNMENT; "Maximum supported value is 256" for
        // a cluster's triangles and vertices (..._INPUT_TRIANGLES_DESC).
        constexpr UINT64 kClasAlignment = 128;
        constexpr uint32_t kMaxClusterTriangles = 256;
        constexpr uint32_t kMaxClusterVertices = 256;

        // A build of BLAS from CLAS: the most CLAS in all and in one BLAS.
        struct ClasDesc   // NVAPI_D3D12_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_INPUT_CLAS_DESC
        {
            // In the order the comment above names them.
            uint32_t maxTotalClasCount;
            uint32_t maxClasCountPerArg;
        };

        // A build of CLAS from triangles: the bounds the driver sizes the operation by.
        struct TrianglesDesc   // ..._MULTI_INDIRECT_CLUSTER_OPERATION_INPUT_TRIANGLES_DESC
        {
            // The vertices' DXGI_FORMAT; the highest geometry index and the most geometries in
            // one cluster; the most triangles and vertices in one cluster and in all; the least
            // bits a cluster's positions are truncated by.
            uint32_t vertexFormat;
            uint32_t maxGeometryIndexValue;
            uint32_t maxUniqueGeometryCountPerArg;
            uint32_t maxTriangleCountPerArg;
            uint32_t maxVertexCountPerArg;
            uint32_t maxTotalTriangleCount;
            uint32_t maxTotalVertexCount;
            uint32_t minPositionTruncateBitCount;
        };

        // A move of cluster structures, which the service does not use: what is moved, and the
        // most bytes.
        struct MovesDesc   // ..._MULTI_INDIRECT_CLUSTER_OPERATION_INPUT_MOVES_DESC
        {
            // In the order the comment above names them.
            uint32_t type;
            uint32_t maxBytesMoved;
        };

        // One cluster operation's inputs, for its sizes and its execution alike.
        struct Inputs   // NVAPI_D3D12_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_INPUTS
        {
            // The most arguments (structures to make); kFlag*; kType*; kMode*.
            uint32_t maxArgCount;
            uint32_t flags;
            uint32_t type;
            uint32_t mode;
            // The description the type takes.
            union
            {
                // For kTypeBuildBlasFromClas, kTypeBuildClasFromTriangles, and a move.
                ClasDesc clasDesc;
                TrianglesDesc trianglesDesc;
                MovesDesc movesDesc;
            };
        };

        // The memory a cluster operation needs, as the driver gives it.
        struct RequirementsInfo   // ..._MULTI_INDIRECT_CLUSTER_OPERATION_REQUIREMENTS_INFO
        {
            // The most its results take, and its scratch, in bytes.
            uint64_t resultDataMaxSizeInBytes;
            uint64_t scratchDataSizeInBytes;
        };

        // What the function of the id kGetRequirements takes.
        struct RequirementsParams   // NVAPI_GET_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_REQUIREMENTS_INFO_PARAMS_V1
        {
            // Version(sizeof, 1); the inputs asked about; where the driver writes the sizes.
            uint32_t version;
            const Inputs* pInput;
            RequirementsInfo* pInfo;
        };

        // NVAPI_D3D12_RAYTRACING_ACCELERATION_STRUCTURE_MULTI_INDIRECT_TRIANGLE_CLUSTER_ARGS
        struct TriangleClusterArgs
        {
            // One CLAS from triangles, read by the GPU. The first fields are its id and flags.
            // Then come its triangles' and vertices' count, the bits its positions are truncated
            // by, and the index formats. Then come its triangles' geometry index and flags (here
            // the cluster's number in the mesh, opaque). The last fields are the strides and
            // addresses of its indices, vertices, per-triangle geometry indices and micromaps,
            // 0 for none.
            uint32_t clusterId;
            uint32_t clusterFlags;
            uint32_t triangleCount : 9;
            uint32_t vertexCount : 9;
            uint32_t positionTruncateBitCount : 6;
            uint32_t indexFormat : 4;
            uint32_t opacityMicromapIndexFormat : 4;
            uint32_t baseGeometryIndexAndFlags;
            uint16_t indexBufferStride;
            uint16_t vertexBufferStride;
            uint16_t geometryIndexAndFlagsBufferStride;
            uint16_t opacityMicromapIndexBufferStride;
            D3D12_GPU_VIRTUAL_ADDRESS indexBuffer;
            D3D12_GPU_VIRTUAL_ADDRESS vertexBuffer;
            D3D12_GPU_VIRTUAL_ADDRESS geometryIndexAndFlagsBuffer;
            D3D12_GPU_VIRTUAL_ADDRESS opacityMicromapArray;
            D3D12_GPU_VIRTUAL_ADDRESS opacityMicromapIndexBuffer;
        };

        // One cluster BLAS from CLAS, read by the GPU.
        struct ClusterArgs   // NVAPI_D3D12_RAYTRACING_ACCELERATION_STRUCTURE_MULTI_INDIRECT_CLUSTER_ARGS
        {
            // How many CLAS, padding, and the GPU address of their addresses.
            uint32_t clusterCount;
            uint32_t reserved;
            D3D12_GPU_VIRTUAL_ADDRESS clusterVAs;
        };

        // One execution of a cluster operation.
        struct OperationDesc   // NVAPI_D3D12_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_DESC
        {
            // Its inputs, as asked about for its memory. The address resolution flags are 0 here:
            // every address below points at its data itself, none at an address of it. With
            // implicit destinations the batch result data is the range the results go into,
            // otherwise ignored. The scratch is at least the asked size. The results' addresses
            // are written with implicit destinations and read with explicit ones, and their sizes
            // are written where an array is given (none here). The arguments are one structure of
            // the operation's type each. The address of their count is 0 here: inputs.maxArgCount
            // of them (nvapi.h, NVAPI_D3D12_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_DESC).
            Inputs inputs;
            uint32_t addressResolutionFlags;
            D3D12_GPU_VIRTUAL_ADDRESS batchResultData;
            D3D12_GPU_VIRTUAL_ADDRESS batchScratchData;
            D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE destinationAddressArray;
            D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE resultSizeArray;
            D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE indirectArgArray;
            D3D12_GPU_VIRTUAL_ADDRESS indirectArgCount;
        };

        // What the function of the id kExecute takes.
        struct ExecuteParams   // NVAPI_RAYTRACING_EXECUTE_MULTI_INDIRECT_CLUSTER_OPERATION_PARAMS_V1
        {
            // Version(sizeof, 1), and the operation.
            uint32_t version;
            const OperationDesc* pDesc;
        };

        static_assert(sizeof(Inputs) == 48, "NVAPI_D3D12_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_INPUTS");
        static_assert(sizeof(TriangleClusterArgs) == 64, "..._MULTI_INDIRECT_TRIANGLE_CLUSTER_ARGS");
        static_assert(sizeof(ClusterArgs) == 16, "..._MULTI_INDIRECT_CLUSTER_ARGS");
        static_assert(sizeof(OperationDesc) == 128, "..._MULTI_INDIRECT_CLUSTER_OPERATION_DESC");
        static_assert(sizeof(RequirementsParams) == 24 && sizeof(ExecuteParams) == 16, "the parameters' structures");

        // MAKE_NVAPI_VERSION: the structure's size, the version number in the high word.
        inline uint32_t Version(size_t size, uint32_t version)
        {
            return static_cast<uint32_t>(size) | (version << 16);
        }

        // NVAPI_INTERFACE: extern "C" NvAPI_Status __cdecl.
        using GetRaytracingCapsFn = int(__cdecl*)(ID3D12Device* device, int type, void* data, size_t size);
        using GetRequirementsFn = int(__cdecl*)(ID3D12Device5* device, const RequirementsParams* params);
        using ExecuteFn = int(__cdecl*)(ID3D12GraphicsCommandList4* list, const ExecuteParams* params);
    }
}
