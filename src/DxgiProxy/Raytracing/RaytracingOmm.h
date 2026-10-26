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
        // The functions' ids for nvapi_QueryInterface (nvapi_interface.h), each NVAPI name beside it.
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
        // bit for all 4-state OMMs". DISABLE_OMMS: "Disable OMMs for all triangles, and revert
        // to using geometry opaque/non-opaque state instead (legacy behavior). ... only valid
        // if the referenced BLAS was built with the ..._ALLOW_DISABLE_OMMS_EX flag".
        constexpr uint32_t kInstanceForceOmm2State = 1u << 4;
        constexpr uint32_t kInstanceDisableOmms = 1u << 5;

        // How many micromaps of one subdivision level and format an array holds, or a
        // geometry's triangles use.
        struct UsageCount   // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_USAGE_COUNT
        {
            // The count, the level and the format (kFormat*).
            uint32_t count;
            uint32_t subdivisionLevel;
            uint32_t format;
        };

        // One micromap of an array: where its states start in the array's input data, its
        // subdivision level and format.
        struct Desc   // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_DESC
        {
            // In the order the comment above names them.
            uint32_t byteOffset;
            uint16_t subdivisionLevel;
            uint16_t format;
        };

        // What a micromap array is built from.
        struct ArrayInputs   // NVAPI_D3D12_BUILD_RAYTRACING_OPACITY_MICROMAP_ARRAY_INPUTS
        {
            // The build flags (kArray*); the usage counts of the micromaps in it; the GPU
            // address of their states; the address and stride of their descriptions (Desc).
            uint32_t flags;
            uint32_t numOMMUsageCounts;
            const UsageCount* pOMMUsageCounts;
            D3D12_GPU_VIRTUAL_ADDRESS inputBuffer;
            D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE perOMMDescs;
        };

        // The memory a micromap array's build needs, as the driver gives it.
        struct ArrayPrebuildInfo   // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_ARRAY_PREBUILD_INFO
        {
            // The most the array takes, and the build's scratch, in bytes.
            uint64_t resultDataMaxSizeInBytes;
            uint64_t scratchDataSizeInBytes;
        };

        // What the function of the id kGetArrayPrebuildInfo takes.
        struct ArrayPrebuildParams   // NVAPI_GET_RAYTRACING_OPACITY_MICROMAP_ARRAY_PREBUILD_INFO_PARAMS_V1
        {
            // Version(sizeof, 1); the inputs asked about; where the driver writes the sizes.
            uint32_t version;
            const ArrayInputs* pDesc;
            ArrayPrebuildInfo* pInfo;
        };

        // A micromap array's build: where it goes, what it is built from, its scratch.
        struct ArrayBuildDesc   // NVAPI_D3D12_BUILD_RAYTRACING_OPACITY_MICROMAP_ARRAY_DESC
        {
            // In the order the comment above names them, GPU addresses.
            D3D12_GPU_VIRTUAL_ADDRESS destOpacityMicromapArrayData;
            ArrayInputs inputs;
            D3D12_GPU_VIRTUAL_ADDRESS scratchOpacityMicromapArrayData;
        };

        // Information an array's build writes after it; the service asks for none.
        struct ArrayPostbuildInfoDesc   // NVAPI_D3D12_RAYTRACING_OPACITY_MICROMAP_ARRAY_POSTBUILD_INFO_DESC
        {
            // Where it is written, and which information it is.
            D3D12_GPU_VIRTUAL_ADDRESS destBuffer;
            uint32_t infoType;
        };

        // What the function of the id kBuildArray takes.
        struct ArrayBuildParams   // NVAPI_BUILD_RAYTRACING_OPACITY_MICROMAP_ARRAY_PARAMS_V1
        {
            // Version(sizeof, 1); the build; the information to write after it, here none.
            uint32_t version;
            const ArrayBuildDesc* pDesc;
            uint32_t numPostbuildInfoDescs;
            const ArrayPostbuildInfoDesc* pPostbuildInfoDescs;
        };

        // How a geometry's triangles find their micromaps in an array.
        struct Attachment   // NVAPI_D3D12_RAYTRACING_GEOMETRY_OMM_ATTACHMENT_DESC
        {
            // Per triangle the index of its micromap or a kIndexFully* state, with its format;
            // what is added to each index; the array; the usage counts of the micromaps the
            // triangles use.
            D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE opacityMicromapIndexBuffer;
            DXGI_FORMAT opacityMicromapIndexFormat;
            uint32_t opacityMicromapBaseLocation;
            D3D12_GPU_VIRTUAL_ADDRESS opacityMicromapArray;
            uint32_t numOMMUsageCounts;
            const UsageCount* pOMMUsageCounts;
        };

        // Triangles with micromaps: D3D12's own description of them and the attachment.
        struct OmmTriangles   // NVAPI_D3D12_RAYTRACING_GEOMETRY_OMM_TRIANGLES_DESC
        {
            // The two parts the comment above names.
            D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC triangles;
            Attachment ommAttachment;
        };

        // NVAPI_D3D12_RAYTRACING_GEOMETRY_DESC_EX: the type, the flags and a union of which
        // this uses the triangles and the triangles with an OMM, padded to the union's size
        // (its displacement micromap, sphere and linear swept sphere members).
        struct GeometryDescEx
        {
            // kGeometryType*, and D3D12_RAYTRACING_GEOMETRY_FLAGS.
            int32_t type;
            uint32_t flags;
            // The description the type says, in NVAPI's union.
            union
            {
                // The plain triangles, the triangles with micromaps, and the union's full size.
                D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC triangles;
                OmmTriangles ommTriangles;
                uint8_t union_[160];
            };
        };

        // A structure's inputs as D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS has them,
        // with a stride for the geometries and the extended geometry descriptions.
        struct InputsEx   // NVAPI_D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS_EX
        {
            // Top or bottom level; the build flags (kBuild*); how many descriptions and in which
            // layout; the bytes from one geometry's description to the next.
            int32_t type;
            uint32_t flags;
            uint32_t numDescs;
            int32_t descsLayout;
            uint32_t geometryDescStrideInBytes;
            // The descriptions, by the type and the layout.
            union
            {
                // A top level's instances on the GPU; a bottom level's geometries as an array, or
                // as an array of pointers.
                D3D12_GPU_VIRTUAL_ADDRESS instanceDescs;
                const GeometryDescEx* pGeometryDescs;
                const GeometryDescEx* const* ppGeometryDescs;
            };
        };

        // What the function of the id kGetPrebuildInfoEx takes.
        struct PrebuildExParams   // NVAPI_GET_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO_EX_PARAMS_V1
        {
            // Version(sizeof, 1); the inputs asked about; where the driver writes the sizes.
            uint32_t version;
            const InputsEx* pDesc;
            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO* pInfo;
        };

        // A structure's build as D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC has it, with
        // the extended inputs.
        struct BuildDescEx   // NVAPI_D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC_EX
        {
            // Where it goes; its inputs; the structure an update starts from (0 for a build);
            // its scratch.
            D3D12_GPU_VIRTUAL_ADDRESS destAccelerationStructureData;
            InputsEx inputs;
            D3D12_GPU_VIRTUAL_ADDRESS sourceAccelerationStructureData;
            D3D12_GPU_VIRTUAL_ADDRESS scratchAccelerationStructureData;
        };

        // What the function of the id kBuildStructureEx takes.
        struct BuildExParams   // NVAPI_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_EX_PARAMS_V1
        {
            // Version(sizeof, 1); the build; the information to write after it.
            uint32_t version;
            const BuildDescEx* pDesc;
            uint32_t numPostbuildInfoDescs;
            const D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC* pPostbuildInfoDescs;
        };

        // What the function of the id kSetCreatePipelineStateOptions takes.
        struct PipelineOptionsParams   // NVAPI_D3D12_SET_CREATE_PIPELINE_STATE_OPTIONS_PARAMS_V1
        {
            // Version(sizeof, 1), and the flags for the pipelines created after (kPipeline*).
            uint32_t version;
            uint32_t flags;
        };

        static_assert(sizeof(UsageCount) == 12 && sizeof(Desc) == 8, "the usage count and the OMM's description");
        static_assert(sizeof(ArrayInputs) == 40, "NVAPI_D3D12_BUILD_RAYTRACING_OPACITY_MICROMAP_ARRAY_INPUTS");
        static_assert(sizeof(Attachment) == 48, "NVAPI_D3D12_RAYTRACING_GEOMETRY_OMM_ATTACHMENT_DESC");
        static_assert(sizeof(GeometryDescEx) == 168, "NVAPI_D3D12_RAYTRACING_GEOMETRY_DESC_EX");

        // MAKE_NVAPI_VERSION: the structure's size, the version number in the high word.
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
