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
    // The packets of the Direct3D 12 service, written by the managed side
    // (D3d12Bridge.cs) and sent through Unity's render thread; each checked by size
    // and magic, and the two larger ones by KspD3d12CreatePacketSize and
    // KspD3d12DispatchPacketSize before the first.
    constexpr uint32_t kD3d12MaxTextures = 8;
    constexpr uint32_t kD3d12MaxConstants = 256;

    constexpr uint32_t kD3d12CreateMagic = 0x4B534443u;     // 'KSDC'
    constexpr uint32_t kD3d12DispatchMagic = 0x4B534444u;   // 'KSDD'
    constexpr uint32_t kD3d12HandleMagic = 0x4B534448u;     // 'KSDH'

    // Event 20: a compute pass from shader bytecode, DXIL or DXBC.
    struct D3d12CreatePacket
    {
        uint32_t size;
        uint32_t magic;
        int32_t pass;
        uint32_t bytecodeSize;
        const void* bytecode;
        char name[64];
    };

    constexpr uint32_t kD3d12DispatchNextFrame = 1u << 0;

    // Event 22: one dispatch of a pass. read and write hold Unity's textures
    // (ID3D11Resource), readCount and writeCount of them; constants the bytes of b0.
    struct D3d12DispatchPacket
    {
        uint32_t size;
        uint32_t magic;
        int32_t pass;
        uint32_t flags;
        uint32_t groupsX;
        uint32_t groupsY;
        uint32_t groupsZ;
        uint32_t readCount;
        uint32_t writeCount;
        uint32_t constantsSize;
        void* read[kD3d12MaxTextures];
        void* write[kD3d12MaxTextures];
        uint8_t constants[kD3d12MaxConstants];
    };

    // Event 21: a pass destroyed; event 23: a texture's shared copy released.
    struct D3d12HandlePacket
    {
        uint32_t size;
        uint32_t magic;
        int32_t pass;
        uint32_t reserved;
        void* texture;
    };

    static_assert(sizeof(D3d12CreatePacket) == 88, "D3d12CreatePacket layout changed: update D3d12Bridge");
    static_assert(sizeof(D3d12DispatchPacket) == 424, "D3d12DispatchPacket layout changed: update D3d12Bridge");
    static_assert(offsetof(D3d12DispatchPacket, read) == 40, "D3d12DispatchPacket layout changed");
    static_assert(offsetof(D3d12DispatchPacket, constants) == 168, "D3d12DispatchPacket layout changed");
    static_assert(sizeof(D3d12HandlePacket) == 24, "D3d12HandlePacket layout changed: update D3d12Bridge");

    // HLSL to a compute shader's DXBC (cs_5_0) with Windows' d3dcompiler_47.dll, loaded from
    // System32 on first use: from source, or from the file at path when source is null, with
    // #include resolved relative to that file. Thread-safe, needs no device.
    //
    // Returns 1 with the bytecode in bytecode and its size in written, 0 with the compiler's
    // errors in messages, -1 when capacity is too small (written the size needed), -2 when
    // d3dcompiler_47.dll cannot be loaded. After 1, messages holds the compiler's warnings.
    int CompileComputeShader(const char* source, int sourceLength, const wchar_t* path, const char* entry,
                             uint8_t* bytecode, int capacity, int& written, char* messages, int messagesSize);

    // The proxy's Direct3D 12 device and queue, offered to other mods
    // (docs/development/shared-foundation.md, "Stage 2").
    //
    // A compute pass is shader bytecode against one root signature: textures t0-t7,
    // read-write textures u0-u7, a constant buffer b0 of up to 256 bytes, samplers
    // s0 point clamp and s1 linear clamp. A dispatch goes the way AMD's upscaler goes
    // (AmdUpscaler.h): Unity's textures are copied into textures both devices share,
    // the queue waits on a shared fence for the point after those copies, the pass
    // runs, and the write textures are copied back into Unity's -- after Direct3D 11
    // waits for the queue in the same frame, or, with kD3d12DispatchNextFrame, at the
    // pass's next dispatch.
    //
    // Runs on Unity's render thread, from render events; the capabilities and the
    // status are read from the main thread.
    class D3d12Compute
    {
    public:
        static D3d12Compute& Get();

        void Attach(const void* owner, ID3D11Device5* device11, ID3D11DeviceContext4* context11,
                    ID3D12Device* device12, ID3D12CommandQueue* queue);
        void Detach(const void* owner);

        struct Capabilities
        {
            int featureLevel = 0;
            int shaderModel = 0;
            int raytracingTier = 0;
            int meshShaderTier = 0;
            int variableShadingRateTier = 0;
        };

        // False while no swapchain proxy presents through Direct3D 12.
        bool Available(Capabilities& out) const;

        void OnCreatePass(const void* data);
        void OnDestroyPass(const void* data);
        void OnDispatch(const void* data);
        void OnReleaseTexture(const void* data);

        // 1 built, 0 not yet, -1 failed or unknown; the text says what.
        int PassStatus(int pass, char* buffer, int size) const;

        // The service's own line: device, passes, shared textures, the last problem.
        int Status(char* buffer, int size) const;

    private:
        D3d12Compute() = default;

        struct Pass
        {
            std::string name;
            std::vector<uint8_t> bytecode;
            Microsoft::WRL::ComPtr<ID3D12PipelineState> pipeline;
            // The device the pipeline was built on: a new one rebuilds it.
            const ID3D12Device* builtOn = nullptr;
            UINT64 pendingFence = 0;
            std::vector<std::pair<Microsoft::WRL::ComPtr<ID3D11Resource>, Microsoft::WRL::ComPtr<ID3D11Texture2D>>>
                pendingCopies;
        };

        struct Shared
        {
            SharedTexture texture;
            ULONGLONG lastUse = 0;
            // The done fence's value after the last list that used the copy: Direct3D 11
            // waits for it before it copies into the copy again.
            UINT64 lastFence = 0;
        };

        static constexpr UINT kSlots = 4;
        struct Slot
        {
            Microsoft::WRL::ComPtr<ID3D12CommandAllocator> allocator;
            Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> heap;
            Microsoft::WRL::ComPtr<ID3D12Resource> constants;
            uint8_t* mapped = nullptr;
            UINT64 done = 0;
            // What the slot's list reads and writes, held until the slot is used again.
            std::vector<Microsoft::WRL::ComPtr<ID3D12Resource>> held;
        };

        bool EnsureQueueLocked();
        bool EnsureRootSignatureLocked();
        bool BuildLocked(Pass& pass);
        Shared* SharedForLocked(void* unityTexture, bool write, std::string& problem);
        void CompletePendingLocked(Pass& pass);
        void WaitForSlotLocked(Slot& slot);
        void WaitForAllLocked();
        void EvictLocked();
        void ReleaseLocked();
        void SetPassStatus(int pass, int state, const std::string& text);
        void SetProblem(const std::string& text);

        mutable std::mutex mutex;

        const void* owner = nullptr;
        std::atomic<bool> attached{ false };
        Microsoft::WRL::ComPtr<ID3D11Device5> device11;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> context11;
        Microsoft::WRL::ComPtr<ID3D12Device> device12;
        Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;

        Microsoft::WRL::ComPtr<ID3D12RootSignature> rootSignature;
        // A fence per direction, so each carries values of one command stream only and
        // they rise in the order they complete: Direct3D 11 signals the copies into the
        // shared textures on copied, which the queue waits for; the queue signals its
        // lists on done, which Direct3D 11 and the CPU wait for.
        Microsoft::WRL::ComPtr<ID3D12Fence> copied12;
        Microsoft::WRL::ComPtr<ID3D11Fence> copied11;
        UINT64 copiedValue = 0;
        Microsoft::WRL::ComPtr<ID3D12Fence> fence12;
        Microsoft::WRL::ComPtr<ID3D11Fence> fence11;
        HANDLE fenceEvent = nullptr;
        UINT64 fenceValue = 0;
        Slot slots[kSlots];
        UINT slotIndex = 0;
        Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList> list;

        std::map<int, Pass> passes;
        std::unordered_map<void*, Shared> textures;
        ULONGLONG nextEviction = 0;

        mutable std::mutex statusLock;
        Capabilities capabilities;
        struct PassState
        {
            int state = 0;
            std::string text;
        };
        std::map<int, PassState> passStates;
        std::string lastProblem;
        uint64_t dispatched = 0;
    };
}
