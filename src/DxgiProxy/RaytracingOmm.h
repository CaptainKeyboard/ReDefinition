#pragma once

// Opacity Micromaps through NVAPI (RaytracingOmm.cpp; Config.h, rtOpacityMicromaps).
// Declared from NVIDIA's NVAPI headers (MIT; nvapi.h and nvapi_interface.h,
// github.com/NVIDIA/nvapi, licenses/NVIDIA-MIT.txt): the functions' ids for nvapi64.dll's
// nvapi_QueryInterface (NvidiaGpu.h, NvapiInterface) and the structures they take, field
// for field, under shorter names; each NVAPI name beside it. Versions are
// MAKE_NVAPI_VERSION -- the structure's size with the version number in the high word.
//
// The DXR 1.2 route (D3D12_RAYTRACING_GEOMETRY_OMM_TRIANGLES_DESC) needs Shader Model 6.9
// for a RayQuery to say it meets micromaps (RAYQUERY_FLAG_ALLOW_OPACITY_MICROMAPS), and the
// pipeline flag for earlier shader models "Not shipped yet" (DirectX-Specs, Raytracing,
// D3D12_STATE_OBJECT_FLAG_RAYQUERY_ALLOW_OPACITY_MICROMAPS_ON_PRE_SM69_SHADERS). NVIDIA's
// route has the driver told once, for every pipeline created after
// (NvAPI_D3D12_SetCreatePipelineStateOptions, ENABLE_OMM_SUPPORT), and the passes' shaders
// stay as they are.

#include <windows.h>

#include <d3d12.h>

#include <cstddef>
#include <cstdint>

namespace redefinition
{
    namespace nvomm
    {
        constexpr uint32_t kGetRaytracingCaps = 0x85a6c2a0;     // NvAPI_D3D12_GetRaytracingCaps
        constexpr uint32_t kSetCreatePipelineStateOptions = 0x5c607a27;   // NvAPI_D3D12_SetCreatePipelineStateOptions
        // NvAPI_D3D12_GetRaytracingOpacityMicromapArrayPrebuildInfo
        constexpr uint32_t kGetArrayPrebuildInfo = 0x4726d180;
        constexpr uint32_t kBuildArray = 0x814f8d11;            // NvAPI_D3D12_BuildRaytracingOpacityMicromapArray
        // NvAPI_D3D12_GetRaytracingAccelerationStructurePrebuildInfoEx
        constexpr uint32_t kGetPrebuildInfoEx = 0x8d025b77;
        constexpr uint32_t kBuildStructureEx = 0xe24ead45;      // NvAPI_D3D12_BuildRaytracingAccelerationStructureEx

        constexpr int kCapsOpacityMicromap = 1;   // NVAPI_D3D12_RAYTRACING_CAPS_TYPE_OPACITY_MICROMAP
        constexpr uint32_t kCapStandard = 1;       // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_CAP_STANDARD

        constexpr uint32_t kPipelineEnableOmm = 1u << 0;   // NVAPI_D3D12_PIPELINE_CREATION_STATE_FLAGS_ENABLE_OMM_SUPPORT

        // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_ARRAY_BUILD_FLAGS
        constexpr uint32_t kArrayPreferFastTrace = 1u << 0;
        constexpr uint32_t kArrayPreferFastBuild = 1u << 1;
        // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_FORMAT
        constexpr uint16_t kFormat2State = 1;   // ..._OC1_2_STATE
        constexpr uint16_t kFormat4State = 2;   // ..._OC1_4_STATE
        // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_OC1_MAX_SUBDIVISION_LEVEL
        constexpr uint32_t kMaxSubdivisionLevel = 12;
        // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_ARRAY_BYTE_ALIGNMENT
        constexpr UINT64 kArrayAlignment = 256;

        // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_SPECIAL_INDEX: "If a triangle has a uniform
        // OMM state in a BLAS build, it is preferable to signal this explicitly rather than
        // attaching a single state OMM."
        constexpr int32_t kIndexFullyTransparent = -1;
        constexpr int32_t kIndexFullyOpaque = -2;
        constexpr int32_t kIndexFullyUnknownTransparent = -3;
        constexpr int32_t kIndexFullyUnknownOpaque = -4;

        // NVAPI_D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS_EX (the first six are
        // D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS at other places).
        constexpr uint32_t kBuildAllowUpdate = 1u << 0;
        constexpr uint32_t kBuildAllowCompaction = 1u << 1;
        constexpr uint32_t kBuildPreferFastTrace = 1u << 2;
        constexpr uint32_t kBuildPreferFastBuild = 1u << 3;
        constexpr uint32_t kBuildPerformUpdate = 1u << 5;
        // "Only applicable for BLAS builds. If enabled, any instances referencing this BLAS are
        // allowed to disable the OMM test through the ..._INSTANCE_FLAG_DISABLE_OMMS_EX flag."
        constexpr uint32_t kBuildAllowDisableOmms = 1u << 7;

        // NVAPI_D3D12_RAYTRACING_GEOMETRY_TYPE_EX
        constexpr int kGeometryTypeTriangles = 0;
        constexpr int kGeometryTypeOmmTriangles = 2;

        // NVAPI_D3D12_RAYTRACING_INSTANCE_FLAGS_EX beyond D3D12's own four.
        // FORCE_OMM_2_STATE: "Ignore the Unknown state and only consider the Transparent/Opaque
        // bit for all 4-state OMMs"; DISABLE_OMMS: "Disable OMMs for all triangles, and revert
        // to using geometry opaque/non-opaque state instead (legacy behavior). ... only valid
        // if the referenced BLAS was built with the ..._ALLOW_DISABLE_OMMS_EX flag".
        constexpr uint32_t kInstanceForceOmm2State = 1u << 4;
        constexpr uint32_t kInstanceDisableOmms = 1u << 5;

        struct UsageCount   // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_USAGE_COUNT
        {
            uint32_t count;
            uint32_t subdivisionLevel;
            uint32_t format;
        };

        struct Desc   // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_DESC
        {
            uint32_t byteOffset;
            uint16_t subdivisionLevel;
            uint16_t format;
        };

        struct ArrayInputs   // NVAPI_D3D12_BUILD_RAYTRACING_OPACITY_MICROMAP_ARRAY_INPUTS
        {
            uint32_t flags;
            uint32_t numOMMUsageCounts;
            const UsageCount* pOMMUsageCounts;
            D3D12_GPU_VIRTUAL_ADDRESS inputBuffer;
            D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE perOMMDescs;
        };

        struct ArrayPrebuildInfo   // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_ARRAY_PREBUILD_INFO
        {
            uint64_t resultDataMaxSizeInBytes;
            uint64_t scratchDataSizeInBytes;
        };

        struct ArrayPrebuildParams   // NVAPI_GET_RAYTRACING_OPACITY_MICROMAP_ARRAY_PREBUILD_INFO_PARAMS_V1
        {
            uint32_t version;
            const ArrayInputs* pDesc;
            ArrayPrebuildInfo* pInfo;
        };

        struct ArrayBuildDesc   // NVAPI_D3D12_BUILD_RAYTRACING_OPACITY_MICROMAP_ARRAY_DESC
        {
            D3D12_GPU_VIRTUAL_ADDRESS destOpacityMicromapArrayData;
            ArrayInputs inputs;
            D3D12_GPU_VIRTUAL_ADDRESS scratchOpacityMicromapArrayData;
        };

        struct ArrayPostbuildInfoDesc   // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_ARRAY_POSTBUILD_INFO_DESC
        {
            D3D12_GPU_VIRTUAL_ADDRESS destBuffer;
            uint32_t infoType;
        };

        struct ArrayBuildParams   // NVAPI_BUILD_RAYTRACING_OPACITY_MICROMAP_ARRAY_PARAMS_V1
        {
            uint32_t version;
            const ArrayBuildDesc* pDesc;
            uint32_t numPostbuildInfoDescs;
            const ArrayPostbuildInfoDesc* pPostbuildInfoDescs;
        };

        struct Attachment   // NVAPI_D3D12_RAYTRACING_GEOMETRY_OMM_ATTACHMENT_DESC
        {
            D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE opacityMicromapIndexBuffer;
            DXGI_FORMAT opacityMicromapIndexFormat;
            uint32_t opacityMicromapBaseLocation;
            D3D12_GPU_VIRTUAL_ADDRESS opacityMicromapArray;
            uint32_t numOMMUsageCounts;
            const UsageCount* pOMMUsageCounts;
        };

        struct OmmTriangles   // NVAPI_D3D12_RAYTRACING_GEOMETRY_OMM_TRIANGLES_DESC
        {
            D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC triangles;
            Attachment ommAttachment;
        };

        // NVAPI_D3D12_RAYTRACING_GEOMETRY_DESC_EX: the type, the flags and a union of which
        // this uses the triangles and the triangles with an OMM, padded to the union's size
        // (its displacement micromap, sphere and linear swept sphere members).
        struct GeometryDescEx
        {
            int32_t type;
            uint32_t flags;
            union
            {
                D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC triangles;
                OmmTriangles ommTriangles;
                uint8_t union_[160];
            };
        };

        struct InputsEx   // NVAPI_D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS_EX
        {
            int32_t type;
            uint32_t flags;
            uint32_t numDescs;
            int32_t descsLayout;
            uint32_t geometryDescStrideInBytes;
            union
            {
                D3D12_GPU_VIRTUAL_ADDRESS instanceDescs;
                const GeometryDescEx* pGeometryDescs;
                const GeometryDescEx* const* ppGeometryDescs;
            };
        };

        struct PrebuildExParams   // NVAPI_GET_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO_EX_PARAMS_V1
        {
            uint32_t version;
            const InputsEx* pDesc;
            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO* pInfo;
        };

        struct BuildDescEx   // NVAPI_D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC_EX
        {
            D3D12_GPU_VIRTUAL_ADDRESS destAccelerationStructureData;
            InputsEx inputs;
            D3D12_GPU_VIRTUAL_ADDRESS sourceAccelerationStructureData;
            D3D12_GPU_VIRTUAL_ADDRESS scratchAccelerationStructureData;
        };

        struct BuildExParams   // NVAPI_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_EX_PARAMS_V1
        {
            uint32_t version;
            const BuildDescEx* pDesc;
            uint32_t numPostbuildInfoDescs;
            const D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC* pPostbuildInfoDescs;
        };

        struct PipelineOptionsParams   // NVAPI_D3D12_SET_CREATE_PIPELINE_STATE_OPTIONS_PARAMS_V1
        {
            uint32_t version;
            uint32_t flags;
        };

        static_assert(sizeof(UsageCount) == 12 && sizeof(Desc) == 8, "the usage count and the OMM's description");
        static_assert(sizeof(ArrayInputs) == 40, "NVAPI_D3D12_BUILD_RAYTRACING_OPACITY_MICROMAP_ARRAY_INPUTS");
        static_assert(sizeof(Attachment) == 48, "NVAPI_D3D12_RAYTRACING_GEOMETRY_OMM_ATTACHMENT_DESC");
        static_assert(sizeof(GeometryDescEx) == 168, "NVAPI_D3D12_RAYTRACING_GEOMETRY_DESC_EX");

        constexpr uint32_t Version(size_t size, uint32_t version)
        {
            return static_cast<uint32_t>(size) | (version << 16);
        }

        // NVAPI_INTERFACE: extern "C" NvAPI_Status __cdecl.
        using GetRaytracingCapsFn = int(__cdecl*)(ID3D12Device* device, int type, void* data, size_t size);
        using SetCreatePipelineStateOptionsFn = int(__cdecl*)(ID3D12Device5* device, const PipelineOptionsParams* params);
        using GetArrayPrebuildInfoFn = int(__cdecl*)(ID3D12Device5* device, ArrayPrebuildParams* params);
        using BuildArrayFn = int(__cdecl*)(ID3D12GraphicsCommandList4* list, ArrayBuildParams* params);
        using GetPrebuildInfoExFn = int(__cdecl*)(ID3D12Device5* device, PrebuildExParams* params);
        using BuildStructureExFn = int(__cdecl*)(ID3D12GraphicsCommandList4* list, const BuildExParams* params);
    }
}
