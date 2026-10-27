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

    constexpr uint32_t kD3d12CreateMagic = 0x4B534443u;     // The create packet's tag, 'KSDC'
    constexpr uint32_t kD3d12DispatchMagic = 0x4B534444u;   // The dispatch packet's tag, 'KSDD'
    constexpr uint32_t kD3d12HandleMagic = 0x4B534448u;     // The handle packet's tag, 'KSDH'

    // Event 20: a compute pass from shader bytecode, DXIL or DXBC.
    struct D3d12CreatePacket
    {
        // Size and magic, the handle the mod chose, the bytecode and its size, and a name for the log.
        uint32_t size;
        uint32_t magic;
        int32_t pass;
        uint32_t bytecodeSize;
        const void* bytecode;
        char name[64];
    };

    // Dispatch flag: the copies back into Unity's textures wait for the pass's next dispatch, not this frame.
    constexpr uint32_t kD3d12DispatchNextFrame = 1u << 0;

    // Event 22: one dispatch of a pass. read and write hold Unity's textures
    // (ID3D11Resource), readCount and writeCount of them; constants the bytes of b0.
    struct D3d12DispatchPacket
    {
        // Size, magic, the pass handle, kD3d12Dispatch... flags, the thread groups, the counts, then the arrays.
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
        // Size, magic, the pass handle (event 21) or 0, padding, and the Unity texture (event 23) or null.
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
    // (docs/development/flow-mod-interface.md, "Direct3D 12 compute from a mod").
    //
    // A compute pass is shader bytecode against one root signature: textures t0-t7,
    // read-write textures u0-u7, a constant buffer b0 of up to 256 bytes, samplers
    // s0 point clamp and s1 linear clamp. A dispatch goes the way AMD's upscaler goes
    // (AmdUpscaler.h). Unity's textures are copied into textures both devices share. The queue waits
    // on a shared fence for the point after those copies, and the pass runs. The write textures are
    // then copied back into Unity's. Direct3D 11 waits for the queue in the same frame, or, with
    // kD3d12DispatchNextFrame, at the pass's next dispatch.
    //
    // Runs on Unity's render thread, from render events. The capabilities and the status are read
    // from the main thread.
    class D3d12Compute
    {
    public:
        static D3d12Compute& Get();

        // The swapchain proxy hands over its devices; what was made on earlier ones is released, the passes kept.
        void Attach(const void* owner, ID3D11Device5* device11, ID3D11DeviceContext4* context11,
                    ID3D12Device* device12, ID3D12CommandQueue* queue);
        // Releases everything and the devices, where owner is the one attached; otherwise nothing.
        void Detach(const void* owner);

        // What the device offers, asked once at Attach.
        struct Capabilities
        {
            // D3D_FEATURE_LEVEL and D3D_SHADER_MODEL as numbers, and the tiers of raytracing, mesh shaders and
            // variable rate shading; 0 where not asked or not supported.
            int featureLevel = 0;
            int shaderModel = 0;
            int raytracingTier = 0;
            int meshShaderTier = 0;
            int variableShadingRateTier = 0;
        };

        // False while no swapchain proxy presents through Direct3D 12.
        bool Available(Capabilities& out) const;

        // Event 20: a pass made, or made again, from the packet's bytecode, built where a device is attached.
        void OnCreatePass(const void* data);
        // Event 21: a pass and its status gone; copies back it left are dropped.
        void OnDestroyPass(const void* data);
        // Event 22: one dispatch of a pass, its textures copied across and back.
        void OnDispatch(const void* data);
        // Event 23: the shared copy of a texture the mod lets go released.
        void OnReleaseTexture(const void* data);

        // 1 built, 0 not yet, -1 failed or unknown; the text says what.
        int PassStatus(int pass, char* buffer, int size) const;

        // The service's own line: device, passes, shared textures, the last problem.
        int Status(char* buffer, int size) const;

    private:
        // Only Get makes it.
        D3d12Compute() = default;

        // One pass by handle; pendingFence and pendingCopies are the copies back a kD3d12DispatchNextFrame
        // dispatch left for the pass's next.
        struct Pass
        {
            // The name it was made with, its bytecode, and its pipeline on the current device.
            std::string name;
            std::vector<uint8_t> bytecode;
            Microsoft::WRL::ComPtr<ID3D12PipelineState> pipeline;
            // The device the pipeline was built on: a new one rebuilds it.
            const ID3D12Device* builtOn = nullptr;
            UINT64 pendingFence = 0;
            std::vector<std::pair<Microsoft::WRL::ComPtr<ID3D11Resource>, Microsoft::WRL::ComPtr<ID3D11Texture2D>>>
                pendingCopies;
        };

        // The shared copy of one of Unity's textures, by its ID3D11Resource.
        struct Shared
        {
            // The copy both devices see, and when it was last used, for eviction.
            SharedTexture texture;
            ULONGLONG lastUse = 0;
            // The done fence's value after the last list that used the copy: Direct3D 11
            // waits for it before it copies into the copy again.
            UINT64 lastFence = 0;
        };

        // The slots, so that a dispatch rarely waits for the GPU.
        static constexpr UINT kSlots = 4;
        // One list in flight per slot, used in turn.
        struct Slot
        {
            // Its allocator, descriptor heap, mapped constant buffer, and the done value of its last list.
            Microsoft::WRL::ComPtr<ID3D12CommandAllocator> allocator;
            Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> heap;
            Microsoft::WRL::ComPtr<ID3D12Resource> constants;
            uint8_t* mapped = nullptr;
            UINT64 done = 0;
            // What the slot's list reads and writes, held until the slot is used again.
            std::vector<Microsoft::WRL::ComPtr<ID3D12Resource>> held;
        };

        // The two shared fences, the slots' allocators, heaps and constant buffers, and the list, made once per
        // device.
        bool EnsureQueueLocked();
        bool EnsureRootSignatureLocked();
        // The pass's compute pipeline on the current device and root signature; a failure goes into its status.
        bool BuildLocked(Pass& pass);
        // The shared copy of a Unity texture, made or made again for its size and format. It is null
        // with problem set for a texture that is not one plain 2D level or whose format has no typed
        // UAV store (or load, to read).
        Shared* SharedForLocked(void* unityTexture, bool write, std::string& problem);
        // The copies back a kD3d12DispatchNextFrame dispatch left: D3D11 waits for its list, then copies them.
        void CompletePendingLocked(Pass& pass);
        // Blocks until the slot's last list has finished, before its allocator is reset.
        void WaitForSlotLocked(Slot& slot);
        // Blocks until the queue has finished every list handed to it.
        void WaitForAllLocked();
        // Shared copies unused for 10 s released, looked at once a second.
        void EvictLocked();
        void ReleaseLocked();
        // A pass's state and text for PassStatus; a failure is logged where it changes.
        void SetPassStatus(int pass, int state, const std::string& text);
        // The problem Status names last, logged where it changes.
        void SetProblem(const std::string& text);

        // Held by the render events and by Attach and Detach.
        mutable std::mutex mutex;

        // The swapchain proxy attached, whether all four of its objects were given, and the objects.
        const void* owner = nullptr;
        std::atomic<bool> attached{ false };
        Microsoft::WRL::ComPtr<ID3D11Device5> device11;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> context11;
        Microsoft::WRL::ComPtr<ID3D12Device> device12;
        Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;

        // t0-t7, u0-u7, b0 and the two samplers, shared by every pass; made with the first pass built.
        Microsoft::WRL::ComPtr<ID3D12RootSignature> rootSignature;
        // A fence per direction, so each carries values of one command stream only and they rise in
        // the order they complete. Direct3D 11 signals the copies into the shared textures on copied,
        // which the queue waits for. The queue signals its lists on done, which Direct3D 11 and the
        // CPU wait for.
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
        // What the device says of a format (D3D12_FEATURE_DATA_FORMAT_SUPPORT): asked once per format and device.
        // Whether the format is usable for a read or a write is decided from these bits at each dispatch.
        struct FormatBits
        {
            // Whether the query succeeded, and its Support1 and Support2.
            bool answered = false;
            UINT support1 = 0;
            UINT support2 = 0;
        };
        std::map<DXGI_FORMAT, FormatBits> formatSupport;

        // Under its own lock, read from the main thread: the capabilities and the statuses below.
        mutable std::mutex statusLock;
        Capabilities capabilities;
        // What PassStatus reports for one pass.
        struct PassState
        {
            // 1 built, 0 not yet, -1 failed; and the text PassStatus reports.
            int state = 0;
            std::string text;
        };
        // Each pass's state by handle, the last problem not tied to a pass, and the dispatches run, for Status.
        std::map<int, PassState> passStates;
        std::string lastProblem;
        uint64_t dispatched = 0;
    };
}
