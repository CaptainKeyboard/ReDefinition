#pragma once

#include <windows.h>

#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>

#include "D3d12Util.h"
#include "FrameGeneration.h"

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>

namespace redefinition
{
    struct AmdUpscalerPacket;

    // Transitions collected for one ResourceBarrier call, in the order they were added. The upscaler's
    // dispatch has at most six before it and six after it.
    class BarrierBatch
    {
    public:
        // The resource from one state to the other, after the ones added before.
        void Add(ID3D12Resource* resource, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to)
        {
            if (count < kMost)
                barriers[count++] = TransitionBarrier(resource, from, to);
        }
        // All of them on the list in one call; nothing for none.
        void Record(ID3D12GraphicsCommandList* commandList) const
        {
            if (count != 0)
                commandList->ResourceBarrier(count, barriers);
        }

    private:
        // The most it holds.
        static constexpr UINT kMost = 8;
        D3D12_RESOURCE_BARRIER barriers[kMost] = {};
        UINT count = 0;
    };

    // What AMD's upscaler (AmdUpscaler.h) and XeSS (XessUpscaler.h) share: the D3D11 to D3D12 hand-off.
    // Unity's textures are copied into textures both devices share, the vendor's upscaler runs on the
    // proxy's D3D12 queue, and a fence both devices share orders the two. Each provider keeps its own DLL,
    // its context and its dispatch.
    class UpscalerRig
    {
    public:
        // The swapchain proxy that made the devices hands them over; what an earlier owner left is released.
        void Attach(const void* owner, ID3D11Device5* device11, ID3D11DeviceContext4* context11,
                    ID3D12Device* device12, ID3D12CommandQueue* queue);
        // Lets go of everything, the devices too, where owner is the one attached; otherwise nothing.
        void Detach(const void* owner);
        // Whether a D3D12 device and queue are attached, which the upscaler needs to run at all.
        bool Attached() const { return attached.load(); }

        // 1 while frames are upscaled, -1 once something stopped it, -2 when that stands -- no usable DLL,
        // while its folder and file are as they were -- 0 before the first packet; the text says what and why.
        int Status(char* buffer, int size) const;

    protected:
        // logPrefix starts the log line for a stop, initialText is the status before the first frame.
        UpscalerRig(const char* logPrefix, const char* initialText);
        // Never deleted through the base.
        ~UpscalerRig() = default;

        // The context destroyed and the shared textures released, after the queue has finished.
        virtual void ReleaseLocked() = 0;
        // The DLL's folder and the stamp of its file, which a -2 stands for while it stays the same.
        virtual std::wstring RuntimeSignature() const = 0;

        // The state and text Status reports; a stop is logged where it changes.
        void SetStatus(int state, const std::string& text);
        // Whether the DLL is to be tried now. The first time always; after that at most once a second, and only
        // once RuntimeSignature changed. Remembers what it answered for.
        bool RuntimeDueLocked();
        // The fence shared with D3D11, the command allocators and the list, made once per attachment.
        bool EnsureFenceLocked();
        // The four shared textures, made again where the frame's differ in size or format.
        bool EnsureTexturesLocked(const AmdUpscalerPacket& packet);
        // Blocks until the queue has finished every list handed to it.
        void WaitForListsLocked();
        // The four shared textures released.
        void ReleaseTexturesLocked();
        // After the inputs are copied on D3D11: a point in its stream for the queue to wait for.
        void SignalInputsLocked();
        // The next of the allocators, whose list has left the GPU, reset with the list; false with a status where
        // that fails.
        bool BeginListLocked(UINT& index);
        // The list closed and executed, the queue's fence signalled for it, and D3D11 made to wait for that --
        // also where the dispatch failed: the signal is queued either way, and a later D3D11 signal on the shared
        // fence that ran ahead of it would set the fence back. False with a status where the list does not close.
        bool FinishListLocked(UINT index);

        // The description of a D3D11 resource that is a 2D texture; false for anything else.
        static bool TextureDesc(void* texture, D3D11_TEXTURE2D_DESC& desc);
        // Whether a context made for a can take b: the same sizes and the same creation flags.
        static bool SameContext(const AmdUpscalerPacket& a, const AmdUpscalerPacket& b);
        // The shared texture made again unless it already has the size and the typed format of desc.
        static bool Match(SharedTexture& shared, ID3D11Device* device11, ID3D12Device* device12,
                          const D3D11_TEXTURE2D_DESC& desc);

        // Held by every entry point: Attach and Detach come from the swapchain proxy, the rest from Unity's render thread.
        mutable std::mutex mutex;

        // The swapchain proxy attached, whether it gave a D3D12 device and queue, and its devices.
        const void* owner = nullptr;
        std::atomic<bool> attached{ false };
        Microsoft::WRL::ComPtr<ID3D11Device5> device11;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> context11;
        Microsoft::WRL::ComPtr<ID3D12Device> device12;
        Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;

        // Whether loading the DLL was tried, the folder and file stamp it was tried with, and when it may be
        // tried next. A DLL that did not load is tried again by a new rig only once the stamp changed.
        bool runtimeTried = false;
        std::wstring runtimeSignature;
        ULONGLONG nextRuntimeCheck = 0;

        // The fence both devices share (one value counted up for both), the event to wait on it, and three
        // allocators in turn for the one list, each with the fence value that marks it finished.
        Microsoft::WRL::ComPtr<ID3D12Fence> fence12;
        Microsoft::WRL::ComPtr<ID3D11Fence> fence11;
        HANDLE fenceEvent = nullptr;
        UINT64 fenceValue = 0;
        static constexpr UINT kLists = 3;
        Microsoft::WRL::ComPtr<ID3D12CommandAllocator> allocators[kLists];
        UINT64 listDone[kLists] = {};
        UINT listIndex = 0;
        Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList> list;

        // The frame's textures as both devices see them: Unity's are copied in, the output copied out.
        SharedTexture colour;
        SharedTexture depth;
        SharedTexture motion;
        SharedTexture output;

        // Frames upscaled since the context was made, and the state last set, read without the status lock.
        uint64_t upscaled = 0;
        int renderState = 0;

        // What Status reports, under its own lock apart from mutex, which OnPacket holds while it sets the status:
        // the state, its text, and the folder and file stamp a -2 stands for.
        mutable std::mutex statusLock;
        int state = 0;
        std::string statusText;
        std::wstring stableSignature;

    private:
        // The start of the log line for a stop.
        const char* logPrefix;
    };
}
