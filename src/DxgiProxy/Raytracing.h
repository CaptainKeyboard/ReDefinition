#pragma once

#include <windows.h>

#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>

#include "FrameGeneration.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <map>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace redefinition
{
    // Raytracing on the proxy's Direct3D 12 device (docs/development/raytracing.md).
    //
    // The managed side hands over meshes, materials and each frame's instances
    // through plain exports from the main thread; the data is copied at once. The
    // frame itself comes through Unity's render thread (event 30), with the
    // textures to read and to write and the camera: the service builds what is
    // new, traces, and copies the results back into Unity's textures.
    //
    // Positions are relative to the camera: an instance's transform takes its
    // mesh into a space with the world's axes and the camera at the origin, so
    // that what is near the camera keeps full float precision however far KSP's
    // world has moved.

    constexpr uint32_t kRtMeshMagic = 0x4B53524Du;      // 'KSRM'
    constexpr uint32_t kRtMaterialMagic = 0x4B535241u;  // 'KSRA'
    constexpr uint32_t kRtFrameMagic = 0x4B535246u;     // 'KSRF'

    constexpr uint32_t kRtMaxTextures = 8;

    // A mesh: vertexCount positions (three floats each), normals (three) and
    // texture coordinates (two) or null, and indexCount indices, three per
    // triangle.
    struct RtMeshDesc
    {
        uint32_t size;
        uint32_t magic;
        int32_t mesh;
        uint32_t vertexCount;
        uint32_t indexCount;
        uint32_t flags;
        const float* positions;
        const float* normals;
        const float* uvs;
        const uint32_t* indices;
    };

    // A material: linear albedo and its alpha, specular colour, smoothness as
    // Unity's standard shading takes it, emitted radiance, and the layer of the
    // material texture array (-1: none).
    struct RtMaterialDesc
    {
        uint32_t size;
        uint32_t magic;
        int32_t material;
        uint32_t flags;
        float albedo[4];
        float specular[3];
        float smoothness;
        float emission[3];
        int32_t texture;
    };

    // One instance of a mesh this frame: a 3x4 transform, rows first, from the
    // mesh into camera-relative space; its material; the ray mask (bits 0-7) and
    // flags (none yet).
    struct RtInstance
    {
        float transform[12];
        int32_t mesh;
        int32_t material;
        uint32_t mask;
        uint32_t flags;
    };

    // The frame's flags.
    constexpr uint32_t kRtFrameSameFrame = 1u << 0;   // copy the results back before the event returns
    constexpr uint32_t kRtFrameReset = 1u << 1;       // the history is not this camera's: start afresh
    constexpr uint32_t kRtFrameDenoise = 1u << 2;     // the signals through ReDefinition's denoiser
    constexpr uint32_t kRtFrameMotionVectors = 1u << 3;   // t5 holds motion: the previous uv is uv - motion.xy

    // The signals a frame traces; each writes its output slot.
    constexpr uint32_t kRtSignalSunVisibility = 1u << 0;   // u0: visibility of the sun, 0 to 1
    constexpr uint32_t kRtSignalAmbientOcclusion = 1u << 1;   // u3: the share of the sky a point sees
    constexpr uint32_t kRtSignalDiffuse = 1u << 2;         // u1: diffuse light arriving, and the hit distance
    constexpr uint32_t kRtSignalSpecular = 1u << 3;        // u2: light from the specular lobe, and the hit distance
    constexpr uint32_t kRtSignalPrimaryCheck = 1u << 7;    // u7: the camera ray's hit distance against the depth's

    // The inputs, by slot: t1 depth as Direct3D's clip-space z (R32_FLOAT), t2-t4 the
    // G-buffer in Unity's layout (albedo and occlusion, specular and smoothness, the
    // world normal as n * 0.5 + 0.5), t5 motion vectors.
    constexpr uint32_t kRtInputDepth = 0;
    constexpr uint32_t kRtInputGBuffer0 = 1;
    constexpr uint32_t kRtInputGBuffer1 = 2;
    constexpr uint32_t kRtInputGBuffer2 = 3;
    constexpr uint32_t kRtInputMotion = 4;
    constexpr uint32_t kRtInputEnvironment = 5;   // t6: the sky as a latitude-longitude image

    // The material textures: small copies of each material's main texture, in one
    // array of kRtMaterialTextureSize square RGBA8 layers, sRGB.
    constexpr uint32_t kRtMaterialTextureSize = 64;
    constexpr uint32_t kRtMaxMaterialTextures = 4096;

    // The number of passes whose GPU time KspRtPassTimings reports, in signal bit order.
    constexpr uint32_t kRtPassTimings = 8;

    // Event 30: a frame. Matrices rows first, for column vectors: clip = worldToClip *
    // (camera-relative position, 1). Clip y is +1 at the texture's first row, as
    // Direct3D has it.
    struct RtFramePacket
    {
        uint32_t size;
        uint32_t magic;
        uint32_t frameIndex;
        uint32_t flags;
        uint32_t width;
        uint32_t height;
        uint32_t signals;
        uint32_t reserved;
        float clipToWorld[16];
        float worldToClip[16];
        float prevWorldToClip[16];
        float sunDirection[4];   // towards the sun; w: its angular radius in radians
        float sunColor[4];       // the sun's radiance at the scene, linear rgb
        float skyColor[4];       // the sky's radiance where no environment is given
        float params[8];
        float bodySphere[4];     // the main body: centre relative to the camera, radius; radius 0 for none
        void* inputs[kRtMaxTextures];
        void* outputs[kRtMaxTextures];
    };

    constexpr uint32_t kRtReconstructMagic = 0x4B535252u;   // 'KSRR'

    // Event 31: a frame for DLSS Ray Reconstruction, in place of DLSS Super Resolution.
    // clipToWorld as RtFramePacket has it, for the guides; the other matrices as
    // Streamline takes them (sl::Constants, DLSSDOptions), without jitter; the
    // jitter in pixels and the motion vector scale as Streamline's constants say.
    // mode is sl::DLSSMode's value, preset sl::DLSSDPreset's.
    struct RtReconstructPacket
    {
        uint32_t size;
        uint32_t magic;
        uint32_t frameIndex;
        uint32_t flags;
        uint32_t renderWidth;
        uint32_t renderHeight;
        uint32_t outputWidth;
        uint32_t outputHeight;
        uint32_t mode;
        uint32_t preset;
        uint32_t reserved[2];
        float clipToWorld[16];
        float viewToClip[16];
        float clipToView[16];
        float clipToPrevClip[16];
        float prevClipToClip[16];
        float worldToView[16];
        float viewToWorld[16];
        float jitter[2];
        float motionScale[2];
        float up[4];
        float right[4];
        float forward[4];
        float nearPlane;
        float farPlane;
        float verticalFov;
        float reserved2;
        void* colour;      // the noisy frame, HDR, at the render size
        void* depth;       // R32_FLOAT, Direct3D's clip-space z, reversed
        void* motion;      // RG16_FLOAT
        void* gbuffer0;
        void* gbuffer1;
        void* gbuffer2;
        void* specular;    // the reflections' output, the hit distance in w
        void* output;      // at the output size
    };

    static_assert(offsetof(RtReconstructPacket, clipToWorld) == 48, "RtReconstructPacket layout changed");
    static_assert(offsetof(RtReconstructPacket, colour) == 576, "RtReconstructPacket layout changed");
    static_assert(sizeof(RtReconstructPacket) == 640, "RtReconstructPacket layout changed: update the managed side");

    static_assert(sizeof(RtMeshDesc) == 56, "RtMeshDesc layout changed: update the managed side");
    static_assert(sizeof(RtMaterialDesc) == 64, "RtMaterialDesc layout changed: update the managed side");
    static_assert(sizeof(RtInstance) == 64, "RtInstance layout changed: update the managed side");
    static_assert(offsetof(RtFramePacket, clipToWorld) == 32, "RtFramePacket layout changed");
    static_assert(offsetof(RtFramePacket, inputs) == 320, "RtFramePacket layout changed");
    static_assert(sizeof(RtFramePacket) == 448, "RtFramePacket layout changed: update the managed side");

    class Raytracing
    {
    public:
        static Raytracing& Get();

        void Attach(const void* owner, ID3D11Device5* device11, ID3D11DeviceContext4* context11,
                    ID3D12Device* device12, ID3D12CommandQueue* queue);
        void Detach(const void* owner);

        // 1 ready, 0 no device yet, -1 not possible here; the text says why.
        int Available(char* buffer, int size) const;

        // From the main thread; the data is copied before they return.
        int SetMesh(const RtMeshDesc* desc);
        void ReleaseMesh(int32_t mesh);
        int SetMaterial(const RtMaterialDesc* desc);
        void ReleaseMaterial(int32_t material);
        int SetInstances(const RtInstance* instances, int count);
        // A layer of the material textures: size by size pixels, RGBA8, rows from the top.
        int SetMaterialTexture(int layer, const uint8_t* pixels, int size);

        // From the render thread.
        void OnFrame(const void* data);
        void OnReconstruct(const void* data);

        // Whether DLSS Ray Reconstruction runs: 1 yes, 0 no, the reason in buffer.
        int ReconstructionAvailable(char* buffer, int size) const;

        int Status(char* buffer, int size) const;

        // What the last frame took on the GPU, in milliseconds: building, tracing.
        void Timings(float& build, float& trace) const;
        // Pass 6 of PassTimings is the denoiser.
        // Each pass's time, by signal bit; 0 for a pass that did not run.
        void PassTimings(float* out, int count) const;

    private:
        Raytracing() = default;

        struct PendingMesh
        {
            std::vector<float> positions;
            std::vector<float> attributes;   // normal xyz, uv xy: five floats a vertex
            std::vector<uint32_t> indices;
            uint32_t vertexCount = 0;
        };

        struct Mesh
        {
            Microsoft::WRL::ComPtr<ID3D12Resource> positions;
            Microsoft::WRL::ComPtr<ID3D12Resource> attributes;
            Microsoft::WRL::ComPtr<ID3D12Resource> indices;
            Microsoft::WRL::ComPtr<ID3D12Resource> blas;
            uint32_t slot = 0;
            uint32_t vertexCount = 0;
            uint32_t indexCount = 0;
            bool built = false;
        };

        struct MaterialData
        {
            float albedo[4];
            float specular[3];
            float smoothness;
            float emission[3];
            int32_t texture;
        };

        struct InstanceData
        {
            uint32_t meshSlot;
            uint32_t material;
            uint32_t flags;
            uint32_t pad;
            float normalRows[12];   // the inverse transpose of the transform's 3x3, rows first, padded
        };

        struct Shared
        {
            SharedTexture texture;
            ULONGLONG lastUse = 0;
            UINT64 lastFence = 0;
        };

        // A texture of the denoiser's own, with the state it is in.
        struct DenoiseTarget
        {
            Microsoft::WRL::ComPtr<ID3D12Resource> resource;
            D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_COMMON;
            DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
            bool Create(ID3D12Device* device, UINT width, UINT height, DXGI_FORMAT format);
        };

        // Per signal (sun, diffuse, specular, occlusion): the noisy trace, and the history
        // and its moments for this frame and the one before; two work textures; the view
        // distance and normal for this frame and the one before.
        // The guides DLSS Ray Reconstruction takes (RaytracingShaders/Reconstruct.hlsl).
        struct GuideTargets
        {
            DenoiseTarget albedo;
            DenoiseTarget specularAlbedo;
            DenoiseTarget normalRoughness;
            DenoiseTarget hitDistance;
            UINT width = 0;
            UINT height = 0;
        };

        // A texture a pass reads or writes: a resource and the format to view it in.
        struct View
        {
            ID3D12Resource* resource = nullptr;
            DXGI_FORMAT format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        };
        static View Of(const DenoiseTarget* target)
        {
            return target != nullptr ? View{ target->resource.Get(), target->format } : View{};
        }
        static View Of(const Shared* shared)
        {
            return shared != nullptr ? View{ shared->texture.d3d12.Get(), shared->texture.format } : View{};
        }

        struct DenoiseTargets
        {
            DenoiseTarget noisy[4];
            DenoiseTarget history[4][2];
            DenoiseTarget moments[4][2];
            DenoiseTarget work[2];
            DenoiseTarget linearDepth[2];
            DenoiseTarget normal[2];
            UINT width = 0;
            UINT height = 0;
            UINT parity = 0;
            bool fresh = true;
        };

        static constexpr UINT kSlots = 3;
        static constexpr UINT kMaxMeshes = 16384;
        static constexpr UINT kFrameDescriptors = 2 * kRtMaxTextures;
        static constexpr UINT kDenoiseDescriptors = 512;
        static constexpr UINT kTableSize = 12;

        struct Slot
        {
            Microsoft::WRL::ComPtr<ID3D12CommandAllocator> allocator;
            Microsoft::WRL::ComPtr<ID3D12Resource> constants;
            uint8_t* constantsMapped = nullptr;
            Microsoft::WRL::ComPtr<ID3D12Resource> instanceDescs;
            Microsoft::WRL::ComPtr<ID3D12Resource> instanceData;
            Microsoft::WRL::ComPtr<ID3D12Resource> materials;
            UINT instanceCapacity = 0;
            UINT materialCapacity = 0;
            UINT64 done = 0;
            // What the slot's list uses, held until the slot comes round again.
            std::vector<Microsoft::WRL::ComPtr<ID3D12Resource>> held;
        };

        bool EnsureDeviceLocked();
        bool LoadShadersLocked();
        bool BuildPipelinesLocked();
        Shared* SharedForLocked(void* unityTexture, std::string& problem);
        bool TakeDescriptorSlotLocked(uint32_t& slot);
        bool UploadMeshLocked(Mesh& mesh, const PendingMesh& pending, Slot& slot);
        bool BuildSceneLocked(Slot& slot, UINT& instanceCount);
        bool EnsureUploadBuffer(Microsoft::WRL::ComPtr<ID3D12Resource>& buffer, UINT64 size);
        bool EnsureMaterialTexturesLocked(UINT layers, Slot& slot);
        bool EnsureDenoiseTargetsLocked(UINT width, UINT height, Slot& slot);
        void UseLocked(DenoiseTarget& target, D3D12_RESOURCE_STATES wanted);
        // Views for one dispatch of the passes in space 3: eight to read, four to write.
        // Returns the table's start on the GPU; the writes follow the reads.
        D3D12_GPU_DESCRIPTOR_HANDLE TableLocked(UINT slotNumber, UINT& used, const View (&reads)[8],
                                                const View (&writes)[4]);
        void RunDenoiserLocked(const RtFramePacket& packet, UINT slotNumber, Shared* const* outputs, Shared* motionInput);
        bool EnsureDefaultBuffer(Microsoft::WRL::ComPtr<ID3D12Resource>& buffer, UINT64 size,
                                 D3D12_RESOURCE_STATES state, D3D12_RESOURCE_FLAGS flags);
        void CompletePendingLocked();
        void WaitForSlotLocked(Slot& slot);
        void WaitForAllLocked();
        void ReleaseLocked();
        void SetProblem(const std::string& text);

        mutable std::mutex mutex;

        const void* owner = nullptr;
        std::atomic<bool> attached{ false };
        Microsoft::WRL::ComPtr<ID3D11Device5> device11;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> context11;
        Microsoft::WRL::ComPtr<ID3D12Device5> device;
        Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;
        int tier = 0;
        std::string unavailable;

        std::map<std::string, std::vector<uint8_t>> shaders;
        Microsoft::WRL::ComPtr<ID3D12RootSignature> rootSignature;
        std::map<uint32_t, Microsoft::WRL::ComPtr<ID3D12PipelineState>> pipelines;
        std::map<std::string, Microsoft::WRL::ComPtr<ID3D12PipelineState>> denoisePipelines;
        DenoiseTargets denoise;
        GuideTargets guides;

        Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> heap;
        UINT increment = 0;
        std::vector<uint32_t> freeSlots;
        // Descriptor slots of meshes gone, free again once the queue is past the value.
        std::vector<std::pair<uint32_t, UINT64>> retiredSlots;

        Microsoft::WRL::ComPtr<ID3D12Fence> copied12;
        Microsoft::WRL::ComPtr<ID3D11Fence> copied11;
        UINT64 copiedValue = 0;
        Microsoft::WRL::ComPtr<ID3D12Fence> fence12;
        Microsoft::WRL::ComPtr<ID3D11Fence> fence11;
        HANDLE fenceEvent = nullptr;
        UINT64 fenceValue = 0;
        Slot slots[kSlots];
        UINT slotIndex = 0;
        Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList4> list;

        Microsoft::WRL::ComPtr<ID3D12Resource> tlas;
        Microsoft::WRL::ComPtr<ID3D12Resource> tlasScratch;
        UINT64 tlasSize = 0;
        UINT64 tlasScratchSize = 0;

        Microsoft::WRL::ComPtr<ID3D12QueryHeap> timestamps;
        Microsoft::WRL::ComPtr<ID3D12Resource> timestampReadback;
        UINT64 timestampFrequency = 0;

        Microsoft::WRL::ComPtr<ID3D12Resource> materialTextures;
        UINT materialTextureLayers = 0;

        // From the main thread, taken by the next frame.
        std::unordered_map<int, std::vector<uint8_t>> pendingTextures;
        std::unordered_map<int32_t, PendingMesh> pendingMeshes;
        std::vector<int32_t> pendingReleases;
        std::unordered_map<int32_t, MaterialData> materialData;
        std::vector<RtInstance> instances;

        std::unordered_map<int32_t, Mesh> meshes;
        std::unordered_map<void*, Shared> textures;
        std::vector<std::pair<Microsoft::WRL::ComPtr<ID3D11Resource>, Microsoft::WRL::ComPtr<ID3D11Texture2D>>>
            pendingCopies;
        UINT64 pendingFence = 0;
        ULONGLONG nextEviction = 0;

        // Counts the devices the service has let go of: the managed side hands its
        // meshes over again when it changes.
        std::atomic<uint32_t> generation{ 0 };

        mutable std::mutex statusLock;
        std::string lastProblem;
        uint64_t frames = 0;
        uint64_t reconstructed = 0;
        uint32_t liveMeshes = 0;
        uint32_t lastInstances = 0;
        uint32_t lastBuilt = 0;
        float buildMs = 0.0f;
        float traceMs = 0.0f;
        float passMs[kRtPassTimings] = {};
    };
}
