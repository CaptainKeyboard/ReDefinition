#pragma once

#include <windows.h>

#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>

#include <FidelityFX/api/include/ffx_api.h>
#include <FidelityFX/api/include/ffx_api_types.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <vector>

namespace sl
{
    struct FrameToken;
}

namespace redefinition
{
    // Unity creates its RenderTextures typeless (R32_TYPELESS for RFloat,
    // R16G16_TYPELESS for RGHalf, R8G8B8A8_TYPELESS for ARGB32). Views, the flip
    // shader and the shared textures need the typed reading.
    DXGI_FORMAT TypedFormat(DXGI_FORMAT format);

    // One texture both APIs can see: created on D3D11, opened on D3D12 through
    // an NT handle. Unity's own RenderTextures are not created shareable, so the
    // managed side's textures are copied into these once per frame.
    struct SharedTexture
    {
        Microsoft::WRL::ComPtr<ID3D11Texture2D> d3d11;
        Microsoft::WRL::ComPtr<ID3D11UnorderedAccessView> uav;
        Microsoft::WRL::ComPtr<ID3D12Resource> d3d12;
        UINT width = 0;
        UINT height = 0;
        DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;

        bool Create(ID3D11Device* device11, ID3D12Device* device12,
                    UINT width, UINT height, DXGI_FORMAT format);
        void Reset();
        bool Valid() const { return d3d11 != nullptr && d3d12 != nullptr; }
        // usage: added to what the resource's description implies; state: the
        // state the resource is in when FSR reads it.
        FfxApiResource AsFfx(uint32_t usage, uint32_t state) const;
    };

    // Everything about one frame that frame generation needs besides the
    // textures, in the order and layout the managed side writes it.
    //
    // It travels through Unity's render thread (IssuePluginEventAndData in the
    // upscaler's own CommandBuffer) rather than through a plain call from the
    // main thread. With multithreaded rendering the main thread runs ahead of
    // the render thread, and a value set from the main thread and read at
    // Present time can belong to the frame after the one being presented. Sent
    // through the command stream it arrives between the previous Present and
    // this one, and so belongs to this frame.
    //
    // A layout agreement across P/Invoke, guarded: size and magic come first
    // and are checked on every packet, and the managed side compares its own
    // sizeof against KspFgPacketSize before it sends anything.
    struct FramePacket
    {
        uint32_t size;
        uint32_t magic;
        uint32_t frameIndex;
        uint32_t renderWidth;
        uint32_t renderHeight;
        uint32_t reset;
        float jitterX;
        float jitterY;
        float motionVectorScaleX;
        float motionVectorScaleY;
        float nearPlane;
        float farPlane;
        float verticalFovRadians;
        float frameTimeDeltaMs;
        // Documented as required: "must contain valid information about the
        // camera position and orientation within the scene".
        float position[3];
        float up[3];
        float right[3];
        float forward[3];
        // For DLSS frame generation, as Streamline's common constants want them
        // (ProgrammingGuide.md 2.11.1): row-major, row vectors, without jitter.
        // View space looks down +z along forward, the camera basis above, as
        // Streamline's own helpers build it (sl_matrix_helpers.h); clip space is
        // Direct3D's, with Unity's reversed depth.
        float viewToClip[16];
        float clipToView[16];
        float clipToPrevClip[16];
        float prevClipToClip[16];
    };

    constexpr uint32_t kFramePacketMagic = 0x4B535046u;   // 'KSPF'

    // The layout the managed side's FramePacket is tested against
    // (FramePacketLayoutTests): what either side changes, both have to.
    static_assert(sizeof(FramePacket) == 360, "FramePacket layout changed: update FrameGenerationBridge.FramePacket");
    static_assert(offsetof(FramePacket, viewToClip) == 104, "FramePacket layout changed");
    static_assert(offsetof(FramePacket, prevClipToClip) == 296, "FramePacket layout changed");

    // FSR 3.1 frame interpolation fed with KSP's own depth and motion vectors.
    class FrameGeneration
    {
    public:
        static FrameGeneration& Get();

        // From the managed side. These take the lock themselves.
        //
        // Depth and motion vectors at render size, and the HUD-less image: the
        // backbuffer as the managed side copied it at the end of the last
        // scene camera, before any UI. All three are Unity RenderTextures and
        // are copied -- flipped, see fgFlipInputs -- into shared textures at
        // Present time, when everything Unity drew this frame has been
        // submitted.
        void RegisterInputs(ID3D11Resource* depth, ID3D11Resource* motionVectors,
                            ID3D11Resource* hudLess);
        void SetEnabled(bool enabled);

        // From the render thread, with the packet the managed side sent for
        // this frame.
        void OnFramePacket(const void* data);

        bool Enabled() const;

        // Whether the last Present was prepared for a generated frame, for the
        // proxy's frame limit, which is taken outside the context lock.
        bool GeneratedLastPresent() const;
        bool Ready() const;

        // The display size the current context was made for, or false when
        // there is none. Reported, so that a context left at an old size shows
        // as exactly that.
        bool ContextSize(UINT& width, UINT& height) const;

        // Whether the current context runs its work on the swapchain's compute
        // queue (fgAsyncWorkloads).
        bool ContextAsync() const;

        // The mod asks for the HUD-less and motion vector check at once, rather than
        // after reportSeconds: a fast camera turn, with its Debug switch on. The
        // request is good for kCheckRequestMs; a check that cannot start by then is
        // dropped with a line, never run later on frames without the turn.
        void RequestCheck() { checkRequestedAt = GetTickCount64(); }

        // How the last attempt to make the context went (EnsureContextLocked).
        static constexpr int kContextFine = 0;       // made, or not yet tried
        static constexpr int kContextRetrying = 1;   // failed; tried again once a second while frames arrive
        static constexpr int kContextStands = 2;     // failed for good; not tried again before a new
                                                     // swapchain, an ini rebuild or RetryContext --
                                                     // or a resize, if it stands only for lasting
        int ContextFailing() const { return contextFailing.load(); }

        // The player switched frame generation on, or a new scene began: a
        // context that could not be made gets another try at once.
        void RetryContext();

        bool HasInputs() const;
        bool HasHudLess() const;

        // The share of the frame that differed from its HUD-less copy at the
        // last completed check, compared directly and with the copy mirrored
        // vertically. Negative while no check has completed. The smaller of
        // the two is the orientation FSR should be given.
        void LastHudLessCheck(float& direct, float& mirrored) const;

        // From the swapchain proxy, which owns both devices.
        //
        // The proxy passes itself as owner. Only the owner may register its
        // backbuffer, release it or detach, so a proxy whose Initialise failed
        // cannot tear down the state of the one that presents. Unity makes one
        // swapchain per window and resizes it; two proxies presenting at once are
        // not handled beyond that.
        void Attach(const void* owner, ID3D11Device* device11, ID3D11DeviceContext* context11,
                    ID3D12Device* device12, UINT displayWidth, UINT displayHeight,
                    DXGI_FORMAT backBufferFormat);

        // The texture Unity renders into as its backbuffer, at creation and
        // after every resize. Its description is the display size and format
        // frame generation works at, and the HUD-less shared texture is made
        // in that format so FSR compares like with like.
        void RegisterBackBuffer(const void* owner, ID3D11Texture2D* backBuffer);

        // Before the swapchain resizes. The frame generation context is made for
        // one display size, so it goes -- switched off on the swapchain first,
        // AMD's order -- and the old backbuffer is let go.
        void ReleaseBackBuffer(const void* owner, void* swapChain);

        // At Present, before the proxy flushes D3D11: this frame's inputs into
        // the shared textures of the given slot, then the check.
        //
        // Two slots, used alternately. FSR reads a slot during the Present that
        // follows the copy; the proxy makes D3D11 wait, before writing a slot,
        // for the Present that last read it -- two frames back, long finished.
        // With a single slot the wait would be for the previous frame's
        // interpolation, serialising rendering and interpolation on the GPU
        // (measured: 11 ms per frame became 15).
        void CaptureForPresent(unsigned int slot);

        // The swapchain is passed so frame generation can be switched off on it
        // before the context goes -- AMD's shutdown order. Null when the
        // swapchain is not FidelityFX's.
        void Detach(const void* owner, void* swapChain);

        // The API documents which of its calls must be externally synchronised,
        // and the swapchain's Present is one of them once a frame generation
        // callback is configured. The proxy therefore holds this across both the
        // prepare dispatch and the present; the symptoms of not doing so are
        // listed as crashes, visual artefacts and infinite waits.
        std::mutex& ContextMutex() { return mutex; }

        // Caller must hold ContextMutex. The context goes, and failed attempts
        // with it, for the ini that changed how it is made.
        void RebuildContextLocked(void* swapChain);

        // Caller must hold ContextMutex. Returns false when anything is missing,
        // in which case presentation carries on without interpolation.
        bool PrepareForPresentLocked(void* swapChain, ID3D12GraphicsCommandList* commandList);

        // Shared inputs replaced by RegisterInputs -- a mode change, a new rig
        // -- are kept until the GPU has finished every frame that can read
        // them (RetireLocked). The proxy calls this once per Present, under
        // ContextMutex, with the frame fence value it signalled last, the one
        // the GPU had completed when this Present began, the one it has
        // completed now, and FidelityFX's swapchain context, or null.
        void ReleaseRetiredLocked(uint64_t lastSignalled, uint64_t completedAtStart, uint64_t completedNow,
                                  ffxContext* swapChainContext);

        // Replaced inputs still waiting for the GPU, for the status line.
        size_t RetiredCount() const;

        // The size of the check's motion vector staging texture, 0 x 0 while
        // there is none -- for the status line, where the harness sees that it
        // follows the inputs.
        void CheckMotionSize(UINT& width, UINT& height) const;

        // Whether presentation should be paced by VSync: for as long as frame
        // generation is switched on and has a context, not only on the frames
        // it dispatches. A frame without inputs -- a scene change, the map, a
        // missed capture -- presented with a different sync interval would
        // mix paced and unpaced presents, and AMD notes that changing VSync
        // resets pacing. Caller must hold ContextMutex.
        bool PacedByVSyncLocked() const { return wanted && contextCreated; }

        // ---- DLSS frame generation (Streamline.h, FrameGenerationDlss.cpp) ----
        //
        // Which frame generation the owner's swapchain presents with, said once
        // after Attach and before its first Present. The inputs, the packet and
        // the check are the same for both; only what they are handed to differs.
        void UseStreamline(const void* caller);

        // Caller must hold ContextMutex. This Present's constants, tags and
        // options for DLSS-G under its frame token: generation on when this
        // frame's own inputs and packet arrived flipped to the screen's
        // orientation, the mod wants it and its status allows; off otherwise, off
        // without a token, and off with the reason holdOff where it is given. At
        // most maxFrames() generated frames, asked only for a Present that
        // generates; fewer where the GPU offers fewer. Returns whether this frame
        // generates.
        bool PrepareStreamlineLocked(const sl::FrameToken* frame, const std::function<uint32_t()>& maxFrames,
                                     const char* holdOff);

        // Caller must hold ContextMutex. DLSS-G off from the next Present on,
        // with the reason in the log -- none for nullptr; returns whether it was
        // on. The owner's swapchain may be one made before this owner's
        // (Attach).
        bool StreamlineOffLocked(const char* reason);

        // What DLSS-G said after a Present (ReadStreamlineStateLocked): the frames
        // it presented since the last one, and the fence and value its work on this
        // Present's inputs completes at -- "SL client must wait on SL DLSS-G
        // plugin-internal fence and associated value, before it can modify or
        // destroy the tagged resources ... on a non-presenting queue ... in the
        // frame it would modify those inputs" (sl_dlss_g.h, DLSSGState), which
        // D3D11 is. Null where DLSS-G gives none.
        struct StreamlinePresent
        {
            uint32_t presented = 0;
            Microsoft::WRL::ComPtr<ID3D12Fence> inputsFence;
            uint64_t inputsFenceValue = 0;
            // DLSS-G on for this Present, and its status fine.
            bool statusOk = false;
        };

        // Caller must hold ContextMutex. After Present: DLSS-G's state -- how many
        // frames it may generate, its status -- for the next frame, the log and
        // the status line.
        StreamlinePresent ReadStreamlineStateLocked();

        // Frames shown per rendered frame while DLSS-G generates: 2 for one
        // generated frame, and so on. 0 while it does not.
        int StreamlineMultiplier() const { return streamlineMultiplier.load(); }

        // Whether this build of DLSS-G presents with V-Sync while it generates
        // (DLSSGState::bIsVsyncSupportAvailable), as its last state said; true
        // before the first. From any thread: the mod's settings ask it too.
        bool StreamlineVsyncSupported() const { return streamlineVsyncSupported.load(); }

    private:
        FrameGeneration() = default;

        bool EnsureContextLocked();
        void DestroyContextLocked(void* swapChain);
        // Forgets failed context attempts, standing ones too: a new swapchain or
        // ini may be what they needed. From Attach, Detach, RebuildContextLocked,
        // RetryContext and a success, and from a resize for a failure that stands
        // only for lasting (contextEscalated): a resize mends none of the codes
        // that stand by themselves.
        void ForgetContextFailuresLocked();

        // Whether no attempt to make the context may be made now: one stands, or
        // the last failed less than a second ago. EnsureContextLocked and
        // CaptureForPresent ask the same.
        bool AttemptWaitingLocked(ULONGLONG now) const;
        void ConfigureOffLocked(void* swapChain, const char* reason);
        void LogInputsLocked() const;

        // One registered Unity texture and its two shared twins.
        struct Input
        {
            Microsoft::WRL::ComPtr<ID3D11Resource> unity;
            Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> unityView;
            SharedTexture buffers[2];
            const char* name = "";
        };

        // The slot in use this frame.
        SharedTexture& S(Input& input) { return input.buffers[slot]; }
        const SharedTexture& S(const Input& input) const { return input.buffers[slot]; }
        unsigned int slot = 0;

        // Whether the context was made with async workload support
        // (fgAsyncWorkloads in Config.h), which it is then configured with.
        bool contextAsync = false;
        std::atomic<ULONGLONG> checkRequestedAt{ 0 };
        static constexpr ULONGLONG kCheckRequestMs = 500;

        // A context that could not be made is not tried again before
        // contextRetryAfter (GetTickCount64), which only a success and
        // RetryContext clear; failures in a row are counted, for the log and for
        // kAttemptsBeforeStanding.
        ULONGLONG contextRetryAfter = 0;
        unsigned int contextFailures = 0;
        std::atomic<int> contextFailing{ kContextFine };
        static constexpr unsigned int kAttemptsBeforeStanding = 10;
        // Whether a standing failure stands for lasting kAttemptsBeforeStanding
        // rather than by its code.
        bool contextEscalated = false;

        bool CreateInputLocked(Input& input, ID3D11Resource* unity, DXGI_FORMAT sharedFormat);
        void CopyInputLocked(Input& input, bool flip);

        // Replaced shared textures that frames on the GPU may still read.
        // releaseAfter stays 0 until the next Present names the frame fence
        // value that covers them.
        struct Retired
        {
            SharedTexture texture;
            uint64_t releaseAfter = 0;
        };
        std::vector<Retired> retired;
        int retiredLogged = 0;
        void RetireLocked(SharedTexture& texture);

        // The vertical flip: a compute shader compiled at run time from the
        // system's D3DCompiler, one variant per channel count. Absent, the
        // inputs are copied unflipped and the log says so.
        bool EnsureFlipLocked();
        // Whether an input has what a flip needs: a variant for its format, a view
        // on Unity's texture and a write view on the shared one.
        bool CanFlipLocked(Input& input);
        bool FlipLocked(Input& input);

        void CheckHudLessLocked(bool copied);
        bool CreateCheckTexturesLocked();
        void ReleaseCheckLocked();
        // Sizes are those of the staging textures that were mapped, never the
        // inputs': a mode change replaces the inputs, and reading a mapping by
        // another texture's size runs off its end.
        void EvaluateCheckLocked(const D3D11_MAPPED_SUBRESOURCE& presented,
                                 const D3D11_MAPPED_SUBRESOURCE& snapshot,
                                 const D3D11_MAPPED_SUBRESOURCE* depthRows,
                                 UINT width, UINT height, UINT depthWidth, UINT depthHeight);
        void EvaluateMotionLocked(const D3D11_MAPPED_SUBRESOURCE& presented,
                                 const D3D11_MAPPED_SUBRESOURCE& snapshot,
                                 const D3D11_MAPPED_SUBRESOURCE& previous,
                                 const D3D11_MAPPED_SUBRESOURCE& motionRows,
                                 UINT width, UINT height, UINT motionWidth, UINT motionHeight);

        mutable std::mutex mutex;

        const void* owner = nullptr;
        Microsoft::WRL::ComPtr<ID3D11Device> device11;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> context11;
        Microsoft::WRL::ComPtr<ID3D12Device> device12;

        Input depth;
        Input motion;
        Input hudLess;

        // The frame's packet, and whether one arrived since the last Present.
        // Without one -- after a scene change, in a menu -- interpolation would use
        // the inputs of an earlier frame.
        FramePacket packet = {};
        bool packetFresh = false;
        bool inputsCopied = false;
        bool flippedLastCopy = false;
        int packetsRejected = 0;

        // The texture Unity renders into as its backbuffer. Owned by the proxy,
        // held here between RegisterBackBuffer calls.
        Microsoft::WRL::ComPtr<ID3D11Texture2D> backBuffer11;

        uint64_t hudLessCaptures = 0;
        bool lastUsedHudLess = false;
        int hudLessStatesLogged = 0;

        // The flip shaders, by channel count: float, float2, float4.
        Microsoft::WRL::ComPtr<ID3D11ComputeShader> flipShaders[3];
        bool flipReady = false;
        bool flipUnavailable = false;
        bool loggedFlipMissing = false;

        // The check: staging copies, read back without ever waiting on the
        // GPU.
        Microsoft::WRL::ComPtr<ID3D11Texture2D> checkPresented;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> checkSnapshot;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> checkPrevious;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> checkDepth;
        Microsoft::WRL::ComPtr<ID3D11Texture2D> checkMotion;
        // The check spans two frames: the HUD-less copy of one, then the copy,
        // backbuffer, depth and motion vectors of the next, so the motion
        // vectors can be tested by reprojecting one onto the other.
        bool checkHavePrevious = false;
        bool checkPending = false;
        bool checkBroken = false;
        ULONGLONG lastCheckTicks = 0;
        float lastDirect = -1.0f;
        float lastMirrored = -1.0f;

        ffxContext context = nullptr;
        bool contextCreated = false;
        UINT contextWidth = 0;
        UINT contextHeight = 0;
        // Atomic so SetEnabled, called by the mod every frame, returns without the
        // lock the present path holds when nothing changes.
        std::atomic<bool> wanted{ false };

        // Whether FSR was last told to generate frames. Coming back on is a
        // discontinuity FSR has to be told about; see PrepareForPresentLocked.
        bool configuredOn = false;

        // Whether PrepareForPresentLocked last prepared a frame for generation.
        bool generatedLastPresent = false;
        bool loggedOffFailure = false;

        UINT displayWidth = 0;
        UINT displayHeight = 0;
        DXGI_FORMAT backBufferFormat = DXGI_FORMAT_UNKNOWN;

        // DLSS-G: whether the owner's swapchain is Streamline's, whether it was
        // last told to generate, how many frames it generates -- the most the GPU
        // reports, DLSSGState::numFramesToGenerateMax -- and its last status, for
        // logging each change once.
        bool streamline = false;
        bool streamlineOn = false;
        uint32_t streamlineFrames = 1;
        // The frames this Present was set to generate, which its state reports on.
        uint32_t streamlineFramesSet = 1;
        uint32_t streamlineStatus = 0;
        bool streamlineStatusKnown = false;
        bool streamlineVsyncKnown = false;
        std::atomic<bool> streamlineVsyncSupported{ true };
        // While its status reports a failure: off, and on again for another look
        // once this tick count has passed -- "If not, disable DLSS-G and fix the
        // integration as needed" (ProgrammingGuideDLSS_G.md 17.0). The status of
        // a Present that generated only: one it did not says nothing about that.
        ULONGLONG streamlineRetryAfter = 0;
        // Generating Presents left before a failing status counts, and the status
        // it was last switched off for.
        uint32_t streamlineGrace = 0;
        uint32_t streamlineOffStatus = 0;
        bool loggedFence = false;
        bool loggedUnflipped = false;
        std::atomic<int> streamlineMultiplier{ 0 };

        uint64_t frameId = 0;
        uint64_t capturedFrames = 0;
        bool loggedFirstDispatch = false;
        ULONGLONG lastInputLogTicks = 0;
    };
}
