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

        struct ClasDesc   // NVAPI_D3D12_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_INPUT_CLAS_DESC
        {
            uint32_t maxTotalClasCount;
            uint32_t maxClasCountPerArg;
        };

        struct TrianglesDesc   // ..._MULTI_INDIRECT_CLUSTER_OPERATION_INPUT_TRIANGLES_DESC
        {
            uint32_t vertexFormat;
            uint32_t maxGeometryIndexValue;
            uint32_t maxUniqueGeometryCountPerArg;
            uint32_t maxTriangleCountPerArg;
            uint32_t maxVertexCountPerArg;
            uint32_t maxTotalTriangleCount;
            uint32_t maxTotalVertexCount;
            uint32_t minPositionTruncateBitCount;
        };

        struct MovesDesc   // ..._MULTI_INDIRECT_CLUSTER_OPERATION_INPUT_MOVES_DESC
        {
            uint32_t type;
            uint32_t maxBytesMoved;
        };

        struct Inputs   // NVAPI_D3D12_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_INPUTS
        {
            uint32_t maxArgCount;
            uint32_t flags;
            uint32_t type;
            uint32_t mode;
            union
            {
                ClasDesc clasDesc;
                TrianglesDesc trianglesDesc;
                MovesDesc movesDesc;
            };
        };

        struct RequirementsInfo   // ..._MULTI_INDIRECT_CLUSTER_OPERATION_REQUIREMENTS_INFO
        {
            uint64_t resultDataMaxSizeInBytes;
            uint64_t scratchDataSizeInBytes;
        };

        struct RequirementsParams   // NVAPI_GET_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_REQUIREMENTS_INFO_PARAMS_V1
        {
            uint32_t version;
            const Inputs* pInput;
            RequirementsInfo* pInfo;
        };

        // NVAPI_D3D12_RAYTRACING_ACCELERATION_STRUCTURE_MULTI_INDIRECT_TRIANGLE_CLUSTER_ARGS
        struct TriangleClusterArgs
        {
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

        struct ClusterArgs   // NVAPI_D3D12_RAYTRACING_ACCELERATION_STRUCTURE_MULTI_INDIRECT_CLUSTER_ARGS
        {
            uint32_t clusterCount;
            uint32_t reserved;
            D3D12_GPU_VIRTUAL_ADDRESS clusterVAs;
        };

        struct OperationDesc   // NVAPI_D3D12_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_DESC
        {
            Inputs inputs;
            uint32_t addressResolutionFlags;
            D3D12_GPU_VIRTUAL_ADDRESS batchResultData;
            D3D12_GPU_VIRTUAL_ADDRESS batchScratchData;
            D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE destinationAddressArray;
            D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE resultSizeArray;
            D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE indirectArgArray;
            D3D12_GPU_VIRTUAL_ADDRESS indirectArgCount;
        };

        struct ExecuteParams   // NVAPI_RAYTRACING_EXECUTE_MULTI_INDIRECT_CLUSTER_OPERATION_PARAMS_V1
        {
            uint32_t version;
            const OperationDesc* pDesc;
        };

        static_assert(sizeof(Inputs) == 48, "NVAPI_D3D12_RAYTRACING_MULTI_INDIRECT_CLUSTER_OPERATION_INPUTS");
        static_assert(sizeof(TriangleClusterArgs) == 64, "..._MULTI_INDIRECT_TRIANGLE_CLUSTER_ARGS");
        static_assert(sizeof(ClusterArgs) == 16, "..._MULTI_INDIRECT_CLUSTER_ARGS");
        static_assert(sizeof(OperationDesc) == 128, "..._MULTI_INDIRECT_CLUSTER_OPERATION_DESC");
        static_assert(sizeof(RequirementsParams) == 24 && sizeof(ExecuteParams) == 16, "the parameters' structures");

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
