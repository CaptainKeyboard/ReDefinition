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

#include "FrameGeneration.h"
#include "XessFg.h"

#include <cstdint>
#include <vector>

namespace redefinition
{
    // Stands in for the swapchain Unity asked for.
    //
    // Unity renders into a shared D3D11 texture it believes is the backbuffer.
    // On Present that texture is copied into the backbuffer of a real D3D12
    // swapchain, and that swapchain presents. The D3D11 swapchain is never
    // created.
    //
    // The D3D12 swapchain is Streamline's while DLSS frame generation can run, or with
    // frame generation off while DLSS Ray Reconstruction needs it, Intel's XeSS-FG's
    // while XeSS's can, FidelityFX's while FSR's can, and a plain one otherwise. The inputs frame
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
            HWND hwnd,
            IDXGISwapChain1* real,
            const DXGI_SWAP_CHAIN_DESC1& desc,
            IDXGISwapChain1** result);

        // IUnknown. The interfaces up to IDXGISwapChain1 always, the later ones only where the swapchain behind
        // has them; anything else goes to the real swapchain in measuring mode and is refused, logged, otherwise.
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override;
        // One reference more on the proxy's own count.
        ULONG STDMETHODCALLTYPE AddRef() override;
        // One reference fewer; the proxy deletes itself when none is left.
        ULONG STDMETHODCALLTYPE Release() override;

        // IDXGIObject: private data and the parent are those of the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE SetPrivateData(REFGUID name, UINT size, const void* data) override;
        // Forwarded to the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE SetPrivateDataInterface(REFGUID name, const IUnknown* unknown) override;
        // Forwarded to the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE GetPrivateData(REFGUID name, UINT* size, void* data) override;
        // The factory of the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE GetParent(REFIID riid, void** parent) override;

        // IDXGIDeviceSubObject: the D3D11 device the caller made the swapchain with, never the D3D12 one.
        HRESULT STDMETHODCALLTYPE GetDevice(REFIID riid, void** device) override;

        // IDXGISwapChain. The frame Unity rendered copied into the D3D12 swapchain and presented
        // (CopyAndPresent); DXGI_PRESENT_TEST only asks (PresentTest); timed in either mode.
        HRESULT STDMETHODCALLTYPE Present(UINT syncInterval, UINT flags) override;
        // The shared texture for every index: the backbuffer Unity renders into.
        HRESULT STDMETHODCALLTYPE GetBuffer(UINT buffer, REFIID riid, void** surface) override;
        HRESULT STDMETHODCALLTYPE SetFullscreenState(BOOL fullscreen, IDXGIOutput* target) override;
        // Forwarded to the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE GetFullscreenState(BOOL* fullscreen, IDXGIOutput** target) override;
        // The description of the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE GetDesc(DXGI_SWAP_CHAIN_DESC* desc) override;
        // The D3D12 swapchain resized and the shared texture made again for it, after frame generation has let go
        // of the old one; the tearing flag it was made with is kept.
        HRESULT STDMETHODCALLTYPE ResizeBuffers(UINT bufferCount, UINT width, UINT height,
                                                DXGI_FORMAT format, UINT flags) override;
        // Forwarded with DLSS-G off first, and logged the first 20 times with SetFullscreenState.
        HRESULT STDMETHODCALLTYPE ResizeTarget(const DXGI_MODE_DESC* target) override;
        // Forwarded to the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE GetContainingOutput(IDXGIOutput** output) override;
        // DXGI's statistics of the swapchain behind (Target), which is what reaches the monitor.
        HRESULT STDMETHODCALLTYPE GetFrameStatistics(DXGI_FRAME_STATISTICS* stats) override;
        // The present count of the swapchain behind (Target), not the game's Presents through the proxy.
        HRESULT STDMETHODCALLTYPE GetLastPresentCount(UINT* count) override;

        // IDXGISwapChain1: the description of the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE GetDesc1(DXGI_SWAP_CHAIN_DESC1* desc) override;
        // Forwarded to the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE GetFullscreenDesc(DXGI_SWAP_CHAIN_FULLSCREEN_DESC* desc) override;
        // The window the caller made the swapchain for.
        HRESULT STDMETHODCALLTYPE GetHwnd(HWND* hwnd) override;
        // Forwarded to the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE GetCoreWindow(REFIID riid, void** unk) override;
        // As Present; parameters (dirty rectangles, scroll) are dropped, the frame being copied whole, and passed
        // on in measuring mode only.
        HRESULT STDMETHODCALLTYPE Present1(UINT syncInterval, UINT flags,
                                           const DXGI_PRESENT_PARAMETERS* parameters) override;
        // Forwarded to the swapchain behind (Target).
        BOOL STDMETHODCALLTYPE IsTemporaryMonoSupported() override;
        // Forwarded to the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE GetRestrictToOutput(IDXGIOutput** output) override;
        // Forwarded to the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE SetBackgroundColor(const DXGI_RGBA* colour) override;
        // Forwarded to the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE GetBackgroundColor(DXGI_RGBA* colour) override;
        // Forwarded to the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE SetRotation(DXGI_MODE_ROTATION rotation) override;
        // Forwarded to the swapchain behind (Target).
        HRESULT STDMETHODCALLTYPE GetRotation(DXGI_MODE_ROTATION* rotation) override;

        // IDXGISwapChain2 -- GetFrameLatencyWaitableObject is the one KSP needs.
        HRESULT STDMETHODCALLTYPE SetSourceSize(UINT width, UINT height) override;
        // Forwarded to the swapchain behind; E_NOINTERFACE where it has no IDXGISwapChain2.
        HRESULT STDMETHODCALLTYPE GetSourceSize(UINT* width, UINT* height) override;
        // Forwarded to the swapchain behind; E_NOINTERFACE where it has no IDXGISwapChain2.
        HRESULT STDMETHODCALLTYPE SetMaximumFrameLatency(UINT maxLatency) override;
        // Forwarded to the swapchain behind; E_NOINTERFACE where it has no IDXGISwapChain2.
        HRESULT STDMETHODCALLTYPE GetMaximumFrameLatency(UINT* maxLatency) override;
        HANDLE STDMETHODCALLTYPE GetFrameLatencyWaitableObject() override;
        // Forwarded to the swapchain behind; E_NOINTERFACE where it has no IDXGISwapChain2.
        HRESULT STDMETHODCALLTYPE SetMatrixTransform(const DXGI_MATRIX_3X2_F* matrix) override;
        // Forwarded to the swapchain behind; E_NOINTERFACE where it has no IDXGISwapChain2.
        HRESULT STDMETHODCALLTYPE GetMatrixTransform(DXGI_MATRIX_3X2_F* matrix) override;

        // IDXGISwapChain3: the index of the swapchain behind, 0 where it has no IDXGISwapChain3. Unity's
        // backbuffer is the one shared texture whatever it says.
        UINT STDMETHODCALLTYPE GetCurrentBackBufferIndex() override;
        // Forwarded to the swapchain behind; E_NOINTERFACE where it has no IDXGISwapChain3.
        HRESULT STDMETHODCALLTYPE CheckColorSpaceSupport(DXGI_COLOR_SPACE_TYPE space,
                                                         UINT* support) override;
        // Forwarded to the swapchain behind; E_NOINTERFACE where it has no IDXGISwapChain3.
        HRESULT STDMETHODCALLTYPE SetColorSpace1(DXGI_COLOR_SPACE_TYPE space) override;
        // ResizeBuffers, the node mask and queues dropped: only the proxy owns a D3D12 queue. Forwarded as it is
        // in measuring mode.
        HRESULT STDMETHODCALLTYPE ResizeBuffers1(UINT bufferCount, UINT width, UINT height,
                                                 DXGI_FORMAT format, UINT flags,
                                                 const UINT* nodeMask,
                                                 IUnknown* const* presentQueue) override;

        // IDXGISwapChain4: forwarded to the swapchain behind; E_NOINTERFACE where it has no IDXGISwapChain4.
        HRESULT STDMETHODCALLTYPE SetHDRMetaData(DXGI_HDR_METADATA_TYPE type, UINT size,
                                                 void* metaData) override;

    private:
        // Only Create and CreateMeasuring make one, with a reference count of 1.
        SwapChainProxy() = default;
        // The GPU idle first; then low latency, the upscalers, compute and frame generation detached, and
        // FidelityFX's, XeSS-FG's and Streamline's swapchains and contexts torn down in the order their SDKs ask.
        ~SwapChainProxy();

        // WRL's ComPtr, short.
        template <typename T>
        using ComPtr = Microsoft::WRL::ComPtr<T>;

        // The proxy's command allocators, one per frame in flight on its queue.
        static constexpr UINT kFrameCount = 3;

        // Proxy mode set up: the D3D12 device on the D3D11 device's adapter, Streamline where it is wanted, the queue,
        // the presentation swapchain, the shared fence and backbuffer, and the parts that run on both devices
        // attached. A failure leaves the caller to make the swapchain Unity asked for.
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

        // Creates the presentation swapchain. It goes through Streamline where that was set up
        // (SetUpStreamline), for DLSS frame generation or for DLSS Ray Reconstruction with frame
        // generation off. It goes through XeSS-FG where that is wanted (xessWanted) and runs. It goes
        // through FidelityFX when frame generation is wanted and AMD's runtime is there. Otherwise it
        // comes straight from DXGI.
        HRESULT CreatePresentationSwapChain(
            IDXGIFactory2* factory, HWND hwnd, const DXGI_SWAP_CHAIN_DESC1& desc,
            const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc,
            IDXGIOutput* restrictToOutput);

        // Streamline (Streamline.h), for DLSS frame generation or Ray
        // Reconstruction alone: initialised for the adapter, the D3D12 device
        // handed to it and upgraded to its proxy -- before the queue, which is
        // made through that proxy. False, with Streamline shut down again,
        // wherever it does not run.
        bool SetUpStreamline(IDXGIAdapter* adapter, bool frameGenerationWanted);
        // The swapchain made on Streamline's queue proxy through its proxy of Unity's factory; the native swapchain
        // behind it kept for DXGI_PRESENT_TEST. Failures go into Streamline's state.
        HRESULT CreateStreamlineSwapChain(IDXGIFactory2* factory, HWND hwnd, const DXGI_SWAP_CHAIN_DESC1& desc,
                                          const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc,
                                          IDXGIOutput* restrictToOutput);
        // Its proxies released and Streamline shut down, before the device goes.
        void TearDownStreamline();

        // XeSS-FG (XessFg.h, SwapChainProxyXess.cpp): XeLL's context made first, the
        // swapchain made through libxess_fg.dll; false, with nothing left of it, where
        // it does not run or another swapchain's XeSS-FG lives. Torn down XeSS-FG
        // first, XeLL after.
        HRESULT CreateXessSwapChain(IDXGIFactory2* factory, HWND hwnd, const DXGI_SWAP_CHAIN_DESC1& desc,
                                    const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreenDesc);
        void TearDownXess();
        // XeLL's frame limit as the settings and the monitor's refresh rate have it now
        // (xellLimitLive): xellSetSleepMode again when it differs from the one set, once
        // the GPU is idle. On the present path, under the context lock, before the frame's
        // work is recorded.
        void FollowXellLimit(double refresh);
        // The semaphore KSP's frame latency waitable object is (xessFgOwnWaitable): one
        // more released after each Present.
        void ReleaseOwnWaitable();
        // XeLL alone on an Intel GPU where no other frame generation's swapchain
        // presents (LowLatency.h), where xell asks for it, its mode as lowLatency says
        // now; returns whether that mode is on.
        bool SetUpIntelXell();

        // The refresh rate of the window's monitor, asked now: for what is set once,
        // when the swapchain is made (XeLL's frame limit); never on the present path,
        // which has MonitorRefreshRate.
        double QueryWindowRefreshRate() const;

        // DLSS-G off, with the last frame presented again for it to take effect,
        // before a resize or a full screen switch.
        void PresentWithStreamlineOff(const char* reason);

        // The most frames DLSS-G may generate for a Present with this sync interval.
        uint32_t StreamlineFrameLimit(UINT syncInterval);

        // Says in the log when the game's window is not in the foreground while DLSS-G is
        // asked to generate (SwapChainProxyStreamline.cpp).
        void NoteStreamlineFocus();

        // The index of the frame about to be presented, 0 for none. It is the game's where its main
        // thread began one and Unity's render thread received it since the last Present
        // (Streamline::TakeGameFrame). Otherwise it is the swapchain's own, begun after the last
        // Present with Reflex's sleep under it (BeginStreamlineFrame). While the game's frames are
        // presented, a Present without one, or with takeGameFrame false, gets none. An index once
        // given its constants takes no others ("eErrorDuplicatedConstants", sl.common), and a new one
        // would run ahead of the game's next. After kPresentsWithoutGameFrame of them in a row the
        // swapchain begins its own frames again.
        uint32_t TakeFrameIndex(bool takeGameFrame);
        void BeginStreamlineFrame();

        // Before D3D11 writes an input slot again: DLSS-G done with what it read
        // of it, as far as its fence says, at most kInputsWaitMs.
        void WaitForStreamlineInputs(UINT slot);

        // Frame pacing lives on the swapchain context, not the frame generation
        // one, so it is configured from here.
        void ApplyPacingTuning();

        HRESULT CreateSharedColour(UINT width, UINT height, DXGI_FORMAT format);
        // Both sides of the shared backbuffer released, the D3D12 one first.
        void ReleaseSharedColour();
        // Present in proxy mode: frame generation's inputs taken, Unity's frame copied into the D3D12 backbuffer, and
        // the Present through whichever swapchain this is, paced as the settings ask.
        HRESULT CopyAndPresent(UINT syncInterval, UINT flags);
        // The parts of CopyAndPresent, in its order, all under the frame generation context's lock.
        // The cap V-Sync sets on the frames shown, for frame generation's minimum, with the refresh rate
        // monitor started or paused as the cap needs it.
        void UpdatePresentCapLocked(UINT syncInterval, UINT xellSync, UINT streamlineSync, bool xellFollows);
        // This Present's frame generation prepared for whichever swapchain this is. For DLSS-G that is its constants, tags
        // and options under the frame index it takes. For XeSS-FG it is its tags under the game's frame.
        // FSR's preparation goes in the command list. frameIndex and streamlineGenerating are what DLSS-G's came to.
        void PrepareTechniqueLocked(UINT streamlineSync, UINT xellSync, uint32_t lowLatencyFrame,
                                    uint32_t& frameIndex, bool& streamlineGenerating);
        // The sync interval this Present goes out with: the game's, as XeLL and DLSS-G clamp it while they run, and
        // 1 for FSR paced by VSync. Said once in the log where one of them changes it.
        UINT ChooseSyncIntervalLocked(UINT syncInterval, bool xell, UINT xellSync, bool streamlineGenerating,
                                      UINT streamlineSync, bool pacedByVSync);
        // What a Present left to read: its result, DLSS-G's state after it and XeSS-FG's frames presented.
        struct PresentReadings
        {
            HRESULT hr = S_OK;
            FrameGeneration::StreamlinePresent streamline;
            uint32_t xessPresented = 0;
        };
        // The latency markers around the Present itself, the Present timed, and what the frame generation
        // reports after it.
        PresentReadings PresentAndReadLocked(UINT effectiveSync, UINT presentFlags, uint32_t frameIndex,
                                             uint32_t lowLatencyFrame);
        // After the Present, outside the lock: DLSS-G's fence for this slot's inputs kept, D3D11's wait for the
        // copy signalled, and the frame ended.
        void SignalInputsDone(UINT slot, bool streamlineGenerating,
                              const FrameGeneration::StreamlinePresent& streamlinePresent);
        // Present and Present1 alike: a test only asks, measuring mode hands the call to the real swapchain,
        // otherwise the frame is copied and presented. realPresent is the real swapchain's call.
        template <typename RealPresent>
        HRESULT PresentThrough(UINT syncInterval, UINT flags, RealPresent realPresent);

        // DXGI_PRESENT_TEST: the swapchain's state asked of DXGI -- occluded or
        // not -- with nothing presented: of the native swapchain behind
        // Streamline's, of FidelityFX's, which answers it from its own, or of the
        // plain one.
        HRESULT PresentTest(UINT syncInterval, UINT flags);

        // The running totals behind KspFgCounters after a Present of the game's
        // frame: one more rendered, and as many presented as the swapchain says --
        // DLSS-G's or XeSS-FG's own count where it gives one.
        void CountPresent(uint32_t framesPresented);

        // The frame presented as it stands where it could not be copied, so the picture never goes
        // away. The Present is counted, and said now and then. The lowLatencyFrame is the game's
        // frame taken for this Present (LowLatency::TakeGameFrame), whose markers then wrap it. It is
        // 0 where none was taken, and the frame stays for the next Present.
        HRESULT PresentUncopied(UINT syncInterval, UINT flags, const char* why, uint32_t lowLatencyFrame = 0);
        // Presents that went out uncopied so far, for the log.
        unsigned long long uncopiedPresents = 0;
        // The two halves of a Present's command list, shared by CopyAndPresent and
        // PresentWithStreamlineOff. The first half resets this allocator's list once its last frame
        // is done. The second half runs after whatever else is recorded into the list. It copies the
        // shared colour into the backbuffer, closes the list and executes it. The list is closed on
        // every way out of the second.
        HRESULT ResetCommandList();
        // The second half: Unity's texture copied into the current backbuffer, the list closed and executed.
        HRESULT CopySharedColourAndExecute();
        // Whether Unity's backbuffer exists; while it does not, this says so in the log once.
        bool RequireSharedColour();
        // Also shared by the two: the queue waits for what Unity has submitted to
        // D3D11 so far; and a frame's end on the queue, its fence signalled for
        // this allocator and whatever waits for the frame, which it returns.
        HRESULT WaitForUnityWork();
        // The frame's end on the queue: the frame fence signalled for this allocator, the next allocator taken.
        UINT64 SignalFrameDone();
        // Blocks until the frame fence has reached value, timed as a wait for a frame in flight (WaitStats.h).
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
        // The frame times since the last report into the log -- mean, percentiles, presented per rendered, what
        // DXGI says reached the monitor -- and the render thread's waits per frame; the times start again after.
        void ReportFrameTimes();

        // Measuring mode, and the swapchain the game asked for, which it then wraps and forwards to.
        bool measureOnly = false;
        ComPtr<IDXGISwapChain1> realSwapChain;

        // The swapchain behind seen as IDXGISwapChain2, 3 and 4, null where it lacks one (ResolveTargetInterfaces).
        ComPtr<IDXGISwapChain2> target2;
        ComPtr<IDXGISwapChain3> target3;
        ComPtr<IDXGISwapChain4> target4;
        // The interfaces last logged, as bits; -1 before the first.
        int loggedTargets = -1;

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
        // the frame indices its tokens are asked for with (TakeFrameIndex). The
        // queue above is the native one behind the queue proxy, which everything
        // else uses.
        ComPtr<ID3D12Device> streamlineDevice;
        ComPtr<ID3D12CommandQueue> streamlineQueue;
        ComPtr<IDXGISwapChain> streamlineNativeSwapChain;
        bool streamlineSwapChain = false;
        // Whether it generates frames, not only presents for Ray Reconstruction.
        bool streamlineGenerates = false;
        uint32_t nextFrameIndex = 0;
        uint32_t presentedFrameIndex = 0;
        bool presentingGameFrames = false;
        bool freshGameFrame = false;
        uint32_t presentsWithoutGameFrame = 0;
        static constexpr uint32_t kPresentsWithoutGameFrame = 3;
        bool loggedPresentWithoutGameFrame = false;
        bool loggedLateGameFrame = false;
        uint32_t streamlineIdlePresents = 0;
        // Whether the game's window was in the foreground at the last Present DLSS-G generated
        // for: 1 yes, 0 no, -1 not looked at yet. For the log line that says when it is not.
        int streamlineFocused = -1;

        // XeSS-FG: its context, the XeLL context it needs -- or the one an Intel GPU's
        // low latency runs on without it -- whether the swapchain is its, and whether
        // it was asked for at creation (Config.h, frameGenerationTechnique).
        // The generated frames per rendered its swapchain was made for, the most it
        // can be set to (xessFgFrames), and the presented frames a second XeLL's frame
        // limit holds it to, 0 for none (fgHalfRefreshLimit).
        xefg::Handle xessContext = nullptr;
        xell_context_handle_t xellContext = nullptr;
        bool xessSwapChain = false;
        bool xessWanted = false;
        bool intelAdapter = false;
        uint32_t xessFramesMost = 0;
        double xellLimitFps = 0.0;
        // XeLL's frame limit as set, in microseconds a frame, 0 for none, and the
        // refresh rate it was made from (FollowXellLimit).
        uint32_t xellIntervalUs = 0;
        double xellLimitRefresh = 0.0;
        bool xellBoost = false;
        // The proxy's frame latency waitable object, a semaphore, where KSP is not given
        // XeSS-FG's swapchain's (xessFgOwnWaitable); null otherwise.
        HANDLE ownWaitable = nullptr;
        bool loggedXessSyncClamp = false;
        bool loggedXessNoFrame = false;

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
        // QueryInterface calls refused and logged so far, with the interface asked for.
        unsigned int refusedQueryLogs = 0;

        // The frame limit's state. It holds when the next frame is due, the timer it waits on and
        // what kind it is, and whether it raised the system timer resolution. It also holds how many
        // Presents went by without a generated frame, and the refresh rate, which a thread of its own
        // keeps asking for.
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

    // Whether a swapchain made through FidelityFX, Streamline or XeSS-FG exists: without
    // one nothing generates, whatever the ini allows.
    bool FrameGenerationSwapChainPresent();

    // Which frame generation the swapchains present with: 3 XeSS, 2 DLSS, 1 FSR, 0
    // none.
    int FrameGenerationTechnique();

    // Which frame generation the last swapchain made was made for, as frameGenerationTechnique and
    // the GPU decided it then. It is kFgTechniqueXess where XeSS-FG was wanted, asked for or auto
    // on an Intel GPU. It is kFgTechniqueDlss where DLSS-G was and kFgTechniqueFsr where FSR's alone
    // was. It is -1 with frame generation off and -2 before the first swapchain.
    int FrameGenerationTechniqueWanted();

    // The window the last swapchain made through the proxy presents to, or null.
    HWND PresentingWindow();
}
