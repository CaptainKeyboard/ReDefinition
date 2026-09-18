#pragma once

#include <atomic>
#include <condition_variable>
#include <mutex>
#include <thread>

#include <windows.h>

#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <FidelityFX/api/include/ffx_api.h>

#include <cstdint>
#include <vector>

namespace sl
{
    struct FrameToken;
}

namespace redefinition
{
    // Stands in for the swapchain Unity asked for.
    //
    // Unity renders into a shared D3D11 texture it believes is the backbuffer.
    // On Present that texture is copied into the backbuffer of a real D3D12
    // swapchain, and that swapchain presents. The D3D11 swapchain is never
    // created.
    //
    // The D3D12 swapchain is Streamline's while DLSS frame generation can run,
    // FidelityFX's while FSR's can, and a plain one otherwise. The inputs frame
    // generation reads are taken for it in Present too (FrameGeneration).
    //
    // Everything that is not GetBuffer or Present is forwarded to the real D3D12
    // swapchain, which knows the right answers about outputs, statistics and
    // fullscreen state.
    //
    // IDXGISwapChain4 rather than IDXGISwapChain1: KSP asks for the swapchain flag
    // FRAME_LATENCY_WAITABLE_OBJECT, and the handle that goes with it is only
    // reachable through IDXGISwapChain2. Answering E_NOINTERFACE there stops Unity
    // right after the swapchain is created.
    class SwapChainProxy final : public IDXGISwapChain4
    {
    public:
        // Returns a failure HRESULT if anything at all goes wrong, so the caller
        // can fall back to creating the swapchain Unity asked for, and a failing
        // proxy leaves a game that starts.
        static HRESULT Create(
            IDXGIFactory2* factory,
            IUnknown* device,
            HWND hwnd,
            const DXGI_SWAP_CHAIN_DESC1& desc,
            const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc,
            IDXGIOutput* restrictToOutput,
            IDXGISwapChain1** result);

        // Measuring pass-through: wraps the swapchain the game actually asked
        // for and forwards everything, timing Present on the way. Gives the
        // D3D11 baseline from the same code that measures the D3D12 path.
        static HRESULT CreateMeasuring(
            IUnknown* device,
            IDXGISwapChain1* real,
            const DXGI_SWAP_CHAIN_DESC1& desc,
            IDXGISwapChain1** result);

        // IUnknown
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override;
        ULONG STDMETHODCALLTYPE AddRef() override;
        ULONG STDMETHODCALLTYPE Release() override;

        // IDXGIObject
        HRESULT STDMETHODCALLTYPE SetPrivateData(REFGUID name, UINT size, const void* data) override;
        HRESULT STDMETHODCALLTYPE SetPrivateDataInterface(REFGUID name, const IUnknown* unknown) override;
        HRESULT STDMETHODCALLTYPE GetPrivateData(REFGUID name, UINT* size, void* data) override;
        HRESULT STDMETHODCALLTYPE GetParent(REFIID riid, void** parent) override;

        // IDXGIDeviceSubObject
        HRESULT STDMETHODCALLTYPE GetDevice(REFIID riid, void** device) override;

        // IDXGISwapChain
        HRESULT STDMETHODCALLTYPE Present(UINT syncInterval, UINT flags) override;
        HRESULT STDMETHODCALLTYPE GetBuffer(UINT buffer, REFIID riid, void** surface) override;
        HRESULT STDMETHODCALLTYPE SetFullscreenState(BOOL fullscreen, IDXGIOutput* target) override;
        HRESULT STDMETHODCALLTYPE GetFullscreenState(BOOL* fullscreen, IDXGIOutput** target) override;
        HRESULT STDMETHODCALLTYPE GetDesc(DXGI_SWAP_CHAIN_DESC* desc) override;
        HRESULT STDMETHODCALLTYPE ResizeBuffers(UINT bufferCount, UINT width, UINT height,
                                                DXGI_FORMAT format, UINT flags) override;
        HRESULT STDMETHODCALLTYPE ResizeTarget(const DXGI_MODE_DESC* target) override;
        HRESULT STDMETHODCALLTYPE GetContainingOutput(IDXGIOutput** output) override;
        HRESULT STDMETHODCALLTYPE GetFrameStatistics(DXGI_FRAME_STATISTICS* stats) override;
        HRESULT STDMETHODCALLTYPE GetLastPresentCount(UINT* count) override;

        // IDXGISwapChain1
        HRESULT STDMETHODCALLTYPE GetDesc1(DXGI_SWAP_CHAIN_DESC1* desc) override;
        HRESULT STDMETHODCALLTYPE GetFullscreenDesc(DXGI_SWAP_CHAIN_FULLSCREEN_DESC* desc) override;
        HRESULT STDMETHODCALLTYPE GetHwnd(HWND* hwnd) override;
        HRESULT STDMETHODCALLTYPE GetCoreWindow(REFIID riid, void** unk) override;
        HRESULT STDMETHODCALLTYPE Present1(UINT syncInterval, UINT flags,
                                           const DXGI_PRESENT_PARAMETERS* parameters) override;
        BOOL STDMETHODCALLTYPE IsTemporaryMonoSupported() override;
        HRESULT STDMETHODCALLTYPE GetRestrictToOutput(IDXGIOutput** output) override;
        HRESULT STDMETHODCALLTYPE SetBackgroundColor(const DXGI_RGBA* colour) override;
        HRESULT STDMETHODCALLTYPE GetBackgroundColor(DXGI_RGBA* colour) override;
        HRESULT STDMETHODCALLTYPE SetRotation(DXGI_MODE_ROTATION rotation) override;
        HRESULT STDMETHODCALLTYPE GetRotation(DXGI_MODE_ROTATION* rotation) override;

        // IDXGISwapChain2 -- GetFrameLatencyWaitableObject is the one KSP needs.
        HRESULT STDMETHODCALLTYPE SetSourceSize(UINT width, UINT height) override;
        HRESULT STDMETHODCALLTYPE GetSourceSize(UINT* width, UINT* height) override;
        HRESULT STDMETHODCALLTYPE SetMaximumFrameLatency(UINT maxLatency) override;
        HRESULT STDMETHODCALLTYPE GetMaximumFrameLatency(UINT* maxLatency) override;
        HANDLE STDMETHODCALLTYPE GetFrameLatencyWaitableObject() override;
        HRESULT STDMETHODCALLTYPE SetMatrixTransform(const DXGI_MATRIX_3X2_F* matrix) override;
        HRESULT STDMETHODCALLTYPE GetMatrixTransform(DXGI_MATRIX_3X2_F* matrix) override;

        // IDXGISwapChain3
        UINT STDMETHODCALLTYPE GetCurrentBackBufferIndex() override;
        HRESULT STDMETHODCALLTYPE CheckColorSpaceSupport(DXGI_COLOR_SPACE_TYPE space,
                                                         UINT* support) override;
        HRESULT STDMETHODCALLTYPE SetColorSpace1(DXGI_COLOR_SPACE_TYPE space) override;
        HRESULT STDMETHODCALLTYPE ResizeBuffers1(UINT bufferCount, UINT width, UINT height,
                                                 DXGI_FORMAT format, UINT flags,
                                                 const UINT* nodeMask,
                                                 IUnknown* const* presentQueue) override;

        // IDXGISwapChain4
        HRESULT STDMETHODCALLTYPE SetHDRMetaData(DXGI_HDR_METADATA_TYPE type, UINT size,
                                                 void* metaData) override;

    private:
        SwapChainProxy() = default;
        ~SwapChainProxy();

        template <typename T>
        using ComPtr = Microsoft::WRL::ComPtr<T>;

        static constexpr UINT kFrameCount = 3;

        HRESULT Initialise(IDXGIFactory2* factory, ID3D11Device* device, HWND hwnd,
                           const DXGI_SWAP_CHAIN_DESC1& desc,
                           const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc,
                           IDXGIOutput* restrictToOutput);

        // Whichever swapchain the forwarded methods belong to. In measuring
        // mode that is the one the game asked for; otherwise the proxy's on D3D12.
        IDXGISwapChain1* Target() const;

        // The same object seen through the later interfaces, resolved once at
        // creation so the forwarders do not query per call.
        void ResolveTargetInterfaces();

        // Creates the presentation swapchain: through Streamline when DLSS frame
        // generation runs (SetUpStreamline), through FidelityFX when frame
        // generation is wanted and AMD's runtime is there, otherwise straight
        // from DXGI.
        HRESULT CreatePresentationSwapChain(
            IDXGIFactory2* factory, HWND hwnd, const DXGI_SWAP_CHAIN_DESC1& desc,
            const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc,
            IDXGIOutput* restrictToOutput);

        // DLSS frame generation (Streamline.h): Streamline initialised for the
        // adapter, the D3D12 device handed to it and upgraded to its proxy --
        // before the queue, which is made through that proxy. False, with
        // Streamline shut down again, wherever it does not run.
        bool SetUpStreamline(IDXGIAdapter* adapter);
        HRESULT CreateStreamlineSwapChain(IDXGIFactory2* factory, HWND hwnd, const DXGI_SWAP_CHAIN_DESC1& desc,
                                          const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc,
                                          IDXGIOutput* restrictToOutput);
        // Its proxies released and Streamline shut down, before the device goes.
        void TearDownStreamline();

        // DLSS-G off, with the last frame presented again for it to take effect,
        // before a resize or a full screen switch.
        void PresentWithStreamlineOff(const char* reason);

        // The most frames DLSS-G may generate for a Present with this sync interval.
        uint32_t StreamlineFrameLimit(UINT syncInterval);

        // The token of the frame about to be presented, and after a Present the
        // next frame's, with Reflex's sleep under it.
        const sl::FrameToken* TakeFrameToken();
        void BeginStreamlineFrame();

        // Before D3D11 writes an input slot again: DLSS-G done with what it read
        // of it, as far as its fence says, at most kInputsWaitMs.
        void WaitForStreamlineInputs(UINT slot);

        // Frame pacing lives on the swapchain context, not the frame generation
        // one, so it is configured from here.
        void ApplyPacingTuning();

        HRESULT CreateSharedColour(UINT width, UINT height, DXGI_FORMAT format);
        void ReleaseSharedColour();
        HRESULT CopyAndPresent(UINT syncInterval, UINT flags);
        // The two halves of a Present's command list, shared by CopyAndPresent and
        // PresentWithStreamlineOff: this allocator's list reset once its last frame
        // is done, and -- after whatever else is recorded into it -- the shared
        // colour copied into the backbuffer, the list closed and executed. The list
        // is closed on every way out of the second.
        HRESULT ResetCommandList();
        HRESULT CopySharedColourAndExecute();
        // Whether Unity's backbuffer exists, said once while it does not.
        bool HasSharedColour();
        // Also shared by the two: the queue waits for what Unity has submitted to
        // D3D11 so far; and a frame's end on the queue, its fence signalled for
        // this allocator and whatever waits for the frame, which it returns.
        HRESULT WaitForUnityWork();
        UINT64 SignalFrameDone();
        void WaitForFrame(UINT64 value);
        void NoteDeviceRemoved();

        // fgHalfRefreshLimit (Config.h): the wait after a Present, and the
        // refresh rate of the monitor the window is on.
        void LimitToHalfRefresh(UINT syncInterval);
        void ReleaseLimitTimer();
        double MonitorRefreshRate();
        void PauseRefreshMonitor();
        void StopRefreshMonitor();

        // Frame times, recorded for the log's report.
        void RecordFrameTime();
        void ReportFrameTimes();

        bool measureOnly = false;
        ComPtr<IDXGISwapChain1> realSwapChain;

        ComPtr<IDXGISwapChain2> target2;
        ComPtr<IDXGISwapChain3> target3;
        ComPtr<IDXGISwapChain4> target4;

        std::vector<double> frameTimesMs;
        UINT lastPresentCount = 0;
        int64_t lastTicks = 0;
        int64_t ticksPerSecond = 0;
        int64_t lastReportTicks = 0;

        long refCount = 1;

        HWND window = nullptr;
        DXGI_SWAP_CHAIN_DESC1 description = {};

        // What Unity sees and renders into.
        ComPtr<ID3D11Device5> d3d11Device;
        ComPtr<ID3D11DeviceContext4> d3d11Context;
        ComPtr<ID3D11Texture2D> sharedColour;
        ComPtr<ID3D11Fence> sharedFence11;

        // What actually reaches the screen.
        ComPtr<ID3D12Device> d3d12Device;
        ComPtr<ID3D12CommandQueue> queue;
        ComPtr<IDXGISwapChain3> swapChain;

        // Non-null when the swapchain came from FidelityFX and therefore has to
        // be given back to it rather than simply released.
        ffxContext fgSwapChainContext = nullptr;

        // DLSS frame generation: Streamline's proxies of the device and of the
        // queue made through it -- the swapchain above is its proxy too -- and
        // the frame index its tokens are asked for with. The queue above is the
        // native one behind the queue proxy, which everything else uses.
        ComPtr<ID3D12Device> streamlineDevice;
        ComPtr<ID3D12CommandQueue> streamlineQueue;
        bool streamlineSwapChain = false;
        uint32_t streamlineFrameIndex = 0;
        const sl::FrameToken* nextFrameToken = nullptr;
        uint32_t streamlineIdlePresents = 0;

        // DLSS-G's fence and value for its work on each input slot, from the
        // Present that last read the slot, and the event its wait uses.
        ComPtr<ID3D12Fence> inputsFence[2];
        UINT64 inputsFenceValue[2] = {};
        HANDLE inputsEvent = nullptr;
        bool loggedInputsWait = false;
        // Whether DLSS-G's status was fine at the Present that stored each slot's
        // fence; and a wait ran out: fences from Presents whose status failed are
        // not waited for again until it is fine.
        bool inputsStatusOk[2] = {};
        bool inputsWaitRanOut = false;

        // The thread that presents, the render thread; and DLSS-G off asked for
        // by another thread, for the next Present.
        std::atomic<DWORD> presentingThread{ 0 };
        std::atomic<bool> streamlineOffRequested{ false };
        ComPtr<ID3D12Resource> sharedColour12;
        ComPtr<ID3D12Fence> sharedFence12;
        ComPtr<ID3D12Fence> frameFence;
        HANDLE frameEvent = nullptr;

        ComPtr<ID3D12CommandAllocator> allocators[kFrameCount];
        ComPtr<ID3D12GraphicsCommandList> commandList;
        UINT64 frameFenceValues[kFrameCount] = {};

        UINT64 sharedFenceValue = 0;
        UINT64 frameFenceValue = 0;

        // The shared fence value signalled after the Present that last read
        // each input slot. D3D11 waits for it before writing that slot again.
        UINT64 afterPresentFence[2] = {};

        // DXGI's own account of what reached the monitor, for the report.
        DXGI_FRAME_STATISTICS lastStats = {};

        // FSR's present count at the last Present, for the running totals
        // behind KspFgCounters.
        UINT countedPresents = 0;
        bool haveCountedPresents = false;

        bool loggedDeviceRemoved = false;
        bool loggedNoSharedColour = false;
        HRESULT removedReason = S_OK;

        // Which of the proxy's command allocators is in use. Not the back buffer
        // index: with frame generation the swapchain presents generated frames, so
        // its index advances between the game's presents and is stale by the next
        // one.
        UINT allocatorIndex = 0;

        uint64_t presentCount = 0;

        // The sync interval actually used for the last Present, for the report:
        // the game's own, or 1 while frame generation paces by VSync.
        UINT lastSyncInterval = 0;
        bool loggedVSyncPacing = false;
        bool loggedStreamlineSyncClamp = false;

        // SetFullscreenState and ResizeTarget calls logged so far.
        unsigned int fullscreenLogs = 0;

        // The frame limit's state: when the next frame is due, the timer it
        // waits on and what kind it is, whether it raised the system timer
        // resolution, how many Presents went by without a generated frame, and
        // the refresh rate, which a thread of its own keeps asking for.
        int64_t limitDue = 0;
        HANDLE limitTimer = nullptr;
        bool limitTimerTried = false;
        bool limitTimerHighResolution = false;
        bool limitTimePeriod = false;
        unsigned int limitIdlePresents = 0;
        bool loggedLimit = false;
        std::thread refreshThread;
        std::mutex refreshMutex;
        std::condition_variable refreshWake;
        bool refreshStop = false;
        bool refreshNow = false;
        std::atomic<bool> refreshActive{ false };
        std::atomic<double> refreshRate{ 0.0 };
    };

    // Running totals for the mod's window (KspFgCounters): frames the game
    // presented through the proxy, and frames the swapchain presented -- FSR's
    // own count, generated frames included. Both wrap at 2^32; a reader
    // differences two readings. False while no frame has been counted.
    bool FrameCounters(uint32_t& rendered, uint32_t& presented);

    // Why the proxy's D3D12 device was removed, or S_OK while it was not.
    HRESULT DeviceRemovedReason();

    // Whether a swapchain made through FidelityFX or Streamline exists: without
    // one nothing generates, whatever the ini allows.
    bool FrameGenerationSwapChainPresent();

    // Which frame generation the swapchains present with: 2 DLSS, 1 FSR, 0 none.
    int FrameGenerationTechnique();
}
