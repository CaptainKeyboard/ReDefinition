#pragma once

#include <windows.h>

#include <d3d12.h>
#include <dxgi1_5.h>
#include <wrl/client.h>

#include <XeSS/xell.h>

#include <cstdint>
#include <mutex>
#include <string>

namespace redefinition
{
    // Declared from Intel's xefg_swapchain.h, xefg_swapchain_d3d12.h and
    // xefg_swapchain_debug.h (the XeSS SDK's inc/xess_fg, 2.1 and later): the
    // values and layouts the proxy hands libxess_fg.dll. Enums are 4 bytes ("All
    // enums must be 4 bytes"), structures packed to 8 (XEFG_SWAPCHAIN_PACK_B),
    // which on x64 is their natural alignment.
    namespace xefg
    {
        // The DLL's context, opaque: only pointers to it are handed back and forth.
        struct Context;
        using Handle = Context*;   // xefg_swapchain_handle_t
        using Result = int32_t;    // xefg_swapchain_result_t: "Warnings: positive values - not a failure"

        // The results the proxy tells apart: XEFG_SWAPCHAIN_RESULT_SUCCESS, _ERROR_POINTER_STILL_IN_USE and
        // _ERROR_MISMATCH_INPUT_RESOURCES.
        constexpr Result kSuccess = 0;
        constexpr Result kErrorPointerStillInUse = -19;
        constexpr Result kErrorMismatchInputResources = -12;

        // XEFG_SWAPCHAIN_INIT_FLAG_INVERTED_DEPTH, set from fgDepthInverted when the swapchain is made.
        constexpr uint32_t kInitInvertedDepth = 1u << 0;

        constexpr int32_t kResourceHudLessColour = 0;               // XEFG_SWAPCHAIN_RES_HUDLESS_COLOR
        constexpr int32_t kResourceDepth = 1;                       // XEFG_SWAPCHAIN_RES_DEPTH
        constexpr int32_t kResourceMotionVectors = 2;               // XEFG_SWAPCHAIN_RES_MOTION_VECTOR

        constexpr int32_t kValidUntilNextPresent = 0;               // XEFG_SWAPCHAIN_RV_UNTIL_NEXT_PRESENT

        constexpr int32_t kUiModeAuto = 0;                          // XEFG_SWAPCHAIN_UI_MODE_AUTO
        constexpr int32_t kUiCompositionDisabled = 0;               // XEFG_SWAPCHAIN_UI_COMPOSITION_STATE_DISABLED
        constexpr int32_t kUiCompositionEnabled = 1;                // XEFG_SWAPCHAIN_UI_COMPOSITION_STATE_ENABLED

        constexpr int32_t kLogDebug = 0;                            // XEFG_SWAPCHAIN_LOGGING_LEVEL_DEBUG
        constexpr int32_t kLogWarning = 2;                          // XEFG_SWAPCHAIN_LOGGING_LEVEL_WARNING

        constexpr int32_t kDebugShowOnlyInterpolation = 0;          // XEFG_SWAPCHAIN_DEBUG_FEATURE_SHOW_ONLY_INTERPOLATION
        constexpr int32_t kDebugTagInterpolatedFrames = 1;          // XEFG_SWAPCHAIN_DEBUG_FEATURE_TAG_INTERPOLATED_FRAMES
        constexpr int32_t kDebugPresentFailedInterpolation = 2;     // XEFG_SWAPCHAIN_DEBUG_FEATURE_PRESENT_FAILED_INTERPOLATION

        constexpr Result kErrorNotImplemented = -7;                 // XEFG_SWAPCHAIN_RESULT_ERROR_NOT_IMPLEMENTED

        // The DLL's version, as xefgSwapChainGetVersion reports it.
        struct Version   // xefg_swapchain_version_t
        {
            // Major, minor and patch number, then padding.
            uint16_t major;
            uint16_t minor;
            uint16_t patch;
            uint16_t reserved;
        };

        // A position or size in pixels, for a tagged resource's region.
        struct Point   // xefg_swapchain_2d_t
        {
            // Horizontal first, then vertical.
            uint32_t x;
            uint32_t y;
        };

        // A frame's camera, jitter, motion vector scale and reset, tagged with its present ID.
        struct FrameConstants   // xefg_swapchain_frame_constant_data_t
        {
            float viewMatrix[16];         // "float4x4, row-major convention"
            float projectionMatrix[16];
            float jitterOffsetX;          // "in the range [-0.5, 0.5]"
            float jitterOffsetY;
            float motionVectorScaleX;
            float motionVectorScaleY;
            uint32_t resetHistory;
            float frameRenderTime;        // milliseconds
        };

        // What its swapchain is initialised with, and what xefgSwapChainD3D12GetInitializationParameters reads back.
        struct D3d12InitParams   // xefg_swapchain_d3d12_init_params_t
        {
            // The SDK's fields in its order; the proxy sets the flags, the frames and the UI mode, null heaps let
            // the DLL allocate its own.
            IDXGISwapChain* pApplicationSwapChain;
            uint32_t initFlags;
            uint32_t maxInterpolatedFrames;
            uint32_t creationNodeMask;
            uint32_t visibleNodeMask;
            ID3D12Heap* pTempBufferHeap;
            uint64_t bufferHeapOffset;
            ID3D12Heap* pTempTextureHeap;
            uint64_t textureHeapOffset;
            ID3D12PipelineLibrary* pPipelineLibrary;
            int32_t uiMode;
        };

        // One input tagged for a present ID: which (kResource...), how long it stays valid, its region, the
        // texture and the state it is in.
        struct D3d12ResourceData   // xefg_swapchain_d3d12_resource_data_t
        {
            // In the SDK's order.
            int32_t type;
            int32_t validity;
            Point resourceBase;
            Point resourceSize;
            ID3D12Resource* pResource;
            D3D12_RESOURCE_STATES incomingState;
        };

        // What the last Present did, read by FrameGeneration::ReadXessStateLocked.
        struct PresentStatus   // xefg_swapchain_present_status_t
        {
            // The frames it queued, its interpolation's result, and whether frame generation is still on.
            uint32_t framesPresented;
            Result frameGenResult;
            uint32_t isFrameGenEnabled;
        };

        // A context's needs and limits; the proxy reads only maxSupportedInterpolations.
        struct Properties   // xefg_swapchain_properties_t
        {
            // The descriptors and heap sizes it would take from the caller, and the most generated frames.
            uint32_t requiredDescriptorCount;
            uint64_t tempBufferHeapSize;
            uint64_t tempTextureHeapSize;
            uint64_t constantBufferSize;
            uint32_t maxSupportedInterpolations;
        };

        static_assert(sizeof(Properties) == 40, "xefg_swapchain_properties_t");
        static_assert(sizeof(FrameConstants) == 152, "xefg_swapchain_frame_constant_data_t");
        static_assert(sizeof(D3d12InitParams) == 72, "xefg_swapchain_d3d12_init_params_t");
        static_assert(sizeof(D3d12ResourceData) == 40, "xefg_swapchain_d3d12_resource_data_t");
        static_assert(sizeof(PresentStatus) == 12, "xefg_swapchain_present_status_t");
    }

    // Intel's XeSS frame generation (XeSS-FG) and Xe Low Latency (XeLL), from the
    // player's libxess_fg.dll and libxell.dll next to KSP_x64.exe or in
    // xessFgDirectory (Config.h), loaded when a swapchain is made.
    //
    // XeSS-FG brings a swapchain of its own -- "XeSS-FG provides a proxy swap chain
    // to simplify integration" -- which the proxy presents through as it does
    // through FidelityFX's or Streamline's (SwapChainProxyXess.cpp). Its inputs are
    // the ones FSR and DLSS-G get (FrameGenerationXess.cpp). The guide says "XeSS-FG requires a
    // working XeLL integration on all GPUs" and, for XeLL, "the same frame ID value for XeLL
    // markers and the XeSS-FG present ID" (xess_fg_developer_guide_english.md, Connect with
    // XeLL). LowLatency.h runs XeLL at the marker points Reflex has.
    //
    // This holds the two DLLs' functions only; the contexts belong to the swapchain
    // proxy that made them, which destroys XeSS-FG's before XeLL's: "XeLL context
    // must be destroyed after XeSS-FG context".
    class XessFg
    {
    public:
        // The one instance, made on first use and never destroyed.
        static XessFg& Get();

        // libxell.dll, loaded first so that libxess_fg.dll finds it loaded. False, with
        // the reason in Why(), where it is missing or lacks a function; tried again
        // at the next swapchain.
        bool LoadXell();
        // libxess_fg.dll, after LoadXell; refused below 1.2.2 unless xessFgAllowOld is set.
        bool LoadFrameGeneration();
        // Why it does not run, or "runs" once a swapchain was made.
        std::string Why() const;
        // The reason it does not run, into Why() and the log.
        void Note(const std::string& text);

        // "XeSS-FG 1.3.0, XeLL 1.3.0", what the DLLs report; empty before.
        std::string Versions() const;

        // ---- XeLL (xell.h) ----
        xell_context_handle_t CreateXell(ID3D12Device* device);
        // Destroys a context CreateXell made; nothing for null.
        void DestroyXell(xell_context_handle_t context);
        // Its mode: latency reduction, the frame limit as the shortest frame in
        // microseconds (0 none) and the boost.
        bool XellSetSleepMode(xell_context_handle_t context, bool lowLatency, uint32_t minimumIntervalUs, bool boost);
        // xellSleep for the frame: waits as XeLL asks; XELL_RESULT_ERROR_UNINITIALIZED without a context.
        xell_result_t XellSleep(xell_context_handle_t context, uint32_t frameId);
        // xellAddMarkerData: one latency marker for the frame; XELL_RESULT_ERROR_UNINITIALIZED without a context.
        xell_result_t XellMarker(xell_context_handle_t context, uint32_t frameId, xell_latency_marker_type_t marker);

        // ---- XeSS-FG ----

        // What the swapchain is made with: the depth's direction, the generated frames
        // asked for (xessFgFrames), the UI mode (xessFgUiMode), and whether the GPU is
        // Intel's, where more than one may be generated.
        struct SwapChainOptions
        {
            // In the order of the comment above.
            bool invertedDepth = true;
            uint32_t frames = 1;
            int32_t uiMode = 0;
            bool intelGpu = false;
        };

        // The context checked on the device and connected to XeLL, the swapchain made
        // for the window on the queue: "Initialization from a swap chain description
        // ... Creates new swap chain for the provided window" (Initialization). On
        // failure nothing is left, and the log says why. framesMost: the generated
        // frames per rendered it was made for, the most it can be set to later.
        HRESULT CreateSwapChain(ID3D12Device* device, ID3D12CommandQueue* queue, IDXGIFactory2* factory, HWND window,
                                const DXGI_SWAP_CHAIN_DESC1& desc, const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* fullscreen,
                                xell_context_handle_t xell, const SwapChainOptions& options, xefg::Handle& context,
                                Microsoft::WRL::ComPtr<IDXGISwapChain3>& swapChain, uint32_t& framesMost);
        // After every reference to its swapchain is released.
        void Destroy(xefg::Handle context);

        // Frame generation on or off on the context (xefgSwapChainSetEnabled).
        xefg::Result SetEnabled(xefg::Handle context, bool enabled);
        // The ID the next Present carries, the one its inputs and XeLL's markers are tagged with.
        xefg::Result SetPresentId(xefg::Handle context, uint32_t presentId);
        // One input for the present ID, valid until the next Present, so without a command list.
        xefg::Result TagResource(xefg::Handle context, uint32_t presentId, const xefg::D3d12ResourceData& data);
        // The frame's constants for the present ID.
        xefg::Result TagConstants(xefg::Handle context, uint32_t presentId, const xefg::FrameConstants& constants);
        // What the last Present did; status is cleared first.
        xefg::Result LastPresentStatus(xefg::Handle context, xefg::PresentStatus& status);
        // The UI composed by XeSS-FG or interpolated with the frame; XEFG_SWAPCHAIN_RESULT_ERROR_NOT_IMPLEMENTED
        // where the DLL lacks the function.
        xefg::Result SetUiComposition(xefg::Handle context, bool enabled);
        // XEFG_SWAPCHAIN_RESULT_ERROR_NOT_IMPLEMENTED where the DLL lacks the function.
        xefg::Result DebugFeature(xefg::Handle context, int32_t feature, bool enabled);
        // The generated frames per rendered, at most the framesMost the swapchain was made for;
        // XEFG_SWAPCHAIN_RESULT_ERROR_NOT_IMPLEMENTED where the DLL lacks the function.
        xefg::Result SetFrames(xefg::Handle context, uint32_t frames);
        // How different two frames must be to count as a scene change;
        // XEFG_SWAPCHAIN_RESULT_ERROR_NOT_IMPLEMENTED where the DLL lacks the function.
        xefg::Result SetSceneChangeThreshold(xefg::Handle context, float threshold);

    private:
        // Only Get makes it.
        XessFg() = default;

        // Note with the mutex already held.
        void NoteLocked(const std::string& text);

        // The mutex for loading (the per-frame calls do not take it), the reason Why() gives, the versions and the
        // two modules once loaded.
        mutable std::mutex mutex;
        std::string why = "not loaded yet";
        std::string versions;
        HMODULE xellModule = nullptr;
        HMODULE fgModule = nullptr;

        // libxell.dll's functions, resolved by LoadXell; xellSetLoggingCallback may stay null.
        xell_result_t (*xellCreate)(ID3D12Device*, xell_context_handle_t*) = nullptr;
        xell_result_t (*xellDestroy)(xell_context_handle_t) = nullptr;
        xell_result_t (*xellSetSleepMode)(xell_context_handle_t, const xell_sleep_params_t*) = nullptr;
        xell_result_t (*xellSleep)(xell_context_handle_t, uint32_t) = nullptr;
        xell_result_t (*xellAddMarkerData)(xell_context_handle_t, uint32_t, xell_latency_marker_type_t) = nullptr;
        xell_result_t (*xellGetVersion)(xell_version_t*) = nullptr;
        xell_result_t (*xellSetLoggingCallback)(xell_context_handle_t, xell_logging_level_t, xell_app_log_callback_t) = nullptr;

        // The callback libxess_fg.dll hands its messages to: text, level, user data.
        using LogCallback = void (*)(const char*, int32_t, void*);
        // libxess_fg.dll's functions, resolved by LoadFrameGeneration; the logging callback, the initialisation
        // parameters and those from fgSetUiCompositionState on are optional and may stay null.
        xefg::Result (*fgGetVersion)(xefg::Version*) = nullptr;
        xefg::Result (*fgCreateContext)(ID3D12Device*, xefg::Handle*) = nullptr;
        xefg::Result (*fgSetLoggingCallback)(xefg::Handle, int32_t, LogCallback, void*) = nullptr;
        xefg::Result (*fgSetLatencyReduction)(xefg::Handle, void*) = nullptr;
        xefg::Result (*fgInitFromSwapChainDesc)(xefg::Handle, HWND, const DXGI_SWAP_CHAIN_DESC1*,
                                                const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, ID3D12CommandQueue*,
                                                IDXGIFactory2*, const xefg::D3d12InitParams*) = nullptr;
        xefg::Result (*fgGetSwapChainPtr)(xefg::Handle, REFIID, void**) = nullptr;
        xefg::Result (*fgGetInitializationParameters)(xefg::Handle, xefg::D3d12InitParams*) = nullptr;
        xefg::Result (*fgDestroy)(xefg::Handle) = nullptr;
        xefg::Result (*fgSetEnabled)(xefg::Handle, uint32_t) = nullptr;
        xefg::Result (*fgSetPresentId)(xefg::Handle, uint32_t) = nullptr;
        xefg::Result (*fgTagFrameResource)(xefg::Handle, ID3D12CommandList*, uint32_t,
                                           const xefg::D3d12ResourceData*) = nullptr;
        xefg::Result (*fgTagFrameConstants)(xefg::Handle, uint32_t, const xefg::FrameConstants*) = nullptr;
        xefg::Result (*fgGetLastPresentStatus)(xefg::Handle, xefg::PresentStatus*) = nullptr;
        xefg::Result (*fgSetUiCompositionState)(xefg::Handle, int32_t) = nullptr;
        xefg::Result (*fgEnableDebugFeature)(xefg::Handle, int32_t, uint32_t, void*) = nullptr;
        xefg::Result (*fgGetProperties)(xefg::Handle, xefg::Properties*) = nullptr;
        xefg::Result (*fgSetNumInterpolatedFrames)(xefg::Handle, uint32_t) = nullptr;
        xefg::Result (*fgSetSceneChangeThreshold)(xefg::Handle, float) = nullptr;
    };
}
