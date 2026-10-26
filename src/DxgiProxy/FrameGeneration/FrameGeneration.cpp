// Frame generation (FrameGeneration): who owns it -- the swapchain proxy that
// attaches and detaches its devices -- and the state the mod and the status line
// ask for. FSR's context is in FrameGenerationContext.cpp, its preparation for
// each Present in FrameGenerationPrepare.cpp, the frame rate gate in
// FrameGenerationRate.cpp, the shared inputs in FrameGenerationInputs.cpp, the
// HUD-less and motion vector checks in FrameGenerationCheck.cpp, DLSS-G in
// FrameGenerationDlss.cpp, XeSS-FG in FrameGenerationXess.cpp.

#include "FrameGeneration.h"

#include "Log.h"

#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    // ---------------------------------------------------------- FrameGeneration

    FrameGeneration& FrameGeneration::Get()
    {
        // Never destroyed: it holds Unity's textures across a swapchain made anew, and
        // releasing Direct3D objects while the DLL unloads at the process's end cut the
        // process short (the harness lost the output it had printed). The process's
        // end takes them back.
        static FrameGeneration* instance = new FrameGeneration();
        return *instance;
    }

    void FrameGeneration::Attach(const void* newOwner, ID3D11Device* newDevice11, ID3D11DeviceContext* newContext11,
                                 ID3D12Device* newDevice12, UINT width, UINT height,
                                 DXGI_FORMAT format)
    {
        std::lock_guard<std::mutex> lock(mutex);

        // The context of an earlier owner whose swapchain still lives was made on
        // that owner's D3D12 device: it goes, switched off on that swapchain first.
        if (contextCreated && owner != nullptr && owner != newOwner)
            DestroyContextLocked(contextSwapChain);

        // The UI plane and its shader were made on the devices of the earlier owner.
        ResetUiLocked();

        owner = newOwner;
        device11 = newDevice11;
        context11 = newContext11;
        device12 = newDevice12;
        displayWidth = width;
        displayHeight = height;
        backBufferFormat = format;
        ForgetContextFailuresLocked();

        // FSR's until the owner says otherwise (UseStreamline). DLSS-G off first on
        // the swapchain of an earlier owner that still lives: nothing turns it off
        // there once this one owns the inputs -- Streamline stays that swapchain's
        // (Streamline::Acquire), so the options still reach it.
        StreamlineOffLocked("frame generation belongs to a swapchain made after this one");
        streamline = false;
        streamlineOn = false;
        streamlineMultiplier = 0;
        // XeSS-FG's alike, on the earlier owner's context, which still lives.
        XessOffLocked("frame generation belongs to a swapchain made after this one");
        xess = false;
        xessContext = nullptr;
        xessFramesNow = 0;
        xessFramesMostNow = 0;

        // Unity's textures, which the rig registers once, outlive a swapchain made
        // anew without a resize: shared again on the new devices. Only textures of
        // the D3D11 device attached can be copied from.
        if (depth.unity || motion.unity || hudLess.unity)
        {
            ComPtr<IUnknown> attachedDevice;
            if (device11 != nullptr)
                device11.As(&attachedDevice);
            bool sameDevice = attachedDevice != nullptr && device12 != nullptr;
            for (const Input* input : { &depth, &motion, &hudLess })
            {
                if (!sameDevice || !input->unity)
                    continue;
                ComPtr<ID3D11Device> owning;
                ComPtr<IUnknown> owningUnknown;
                input->unity->GetDevice(&owning);
                sameDevice = owning != nullptr && SUCCEEDED(owning.As(&owningUnknown))
                             && owningUnknown.Get() == attachedDevice.Get();
            }
            if (sameDevice)
            {
                const ComPtr<ID3D11Resource> unityDepth = depth.unity;
                const ComPtr<ID3D11Resource> unityMotion = motion.unity;
                const ComPtr<ID3D11Resource> unityHudLess = hudLess.unity;
                LogLine("Frame generation's inputs, registered before, shared on the new swapchain's devices");
                ShareInputsLocked(unityDepth.Get(), unityMotion.Get(), unityHudLess.Get());
            }
            else
            {
                // Held, they would keep another device alive.
                for (Input* input : { &depth, &motion, &hudLess })
                    input->unity.Reset();
            }
        }
    }

    void FrameGeneration::Detach(const void* caller, void* swapChain)
    {
        std::lock_guard<std::mutex> lock(mutex);

        // A proxy that never attached -- measuring pass-through, a failed
        // Initialise -- has nothing to detach; one that lost ownership is
        // worth a line.
        if (caller != owner)
        {
            if (owner != nullptr)
                LogLine("Detach from a swapchain that does not own frame generation -- ignored");
            return;
        }

        DestroyContextLocked(swapChain);
        ForgetContextFailuresLocked();

        // Unity's textures stay: the rig registers them once, and a swapchain made
        // anew shares them again (Attach).
        for (Input* input : { &depth, &motion, &hudLess })
        {
            input->buffers[0].Reset();
            input->buffers[1].Reset();
            input->unityView.Reset();
        }

        // The UI plane and its shader belong to the devices that go.
        ResetUiLocked();

        // The proxy's destructor waited for the GPU before this, so nothing
        // reads them any more.
        retired.clear();

        for (auto& shader : flipShaders)
            shader.Reset();
        flipReady = false;

        ReleaseCheckLocked();
        backBuffer11.Reset();
        packetFresh = false;
        inputsCopied = false;

        streamline = false;
        streamlineOn = false;
        streamlineFrames = 1;
        streamlineFramesSet = 1;
        streamlineStatusKnown = false;
        streamlineRetryAfter = 0;
        streamlineGrace = 0;
        streamlineOffStatus = 0;
        streamlineMultiplier = 0;
        xess = false;
        xessContext = nullptr;
        xessOn = false;
        xessMultiplier = 0;
        xessFramesNow = 0;
        xessFramesMostNow = 0;
        loggedUnflipped = false;
        loggedFlipMissing = false;

        context11.Reset();
        device11.Reset();
        device12.Reset();
        owner = nullptr;
    }

    void FrameGeneration::SetEnabled(bool enabled)
    {
        if (wanted.load() == enabled)
            return;
        std::lock_guard<std::mutex> lock(mutex);
        if (wanted == enabled)
            return;

        wanted = enabled;
        LogLine(std::string("Frame generation requested: ") + (enabled ? "on" : "off"));
    }

    void FrameGeneration::SetMultiplier(int multiplier, bool dynamic)
    {
        if (multiplier < 0)
            multiplier = 0;
        if (multiplier > 0xFFFF)
            multiplier = 0xFFFF;
        const uint32_t packed = static_cast<uint32_t>(multiplier) | (dynamic ? 0x10000u : 0u);
        if (askedPacked.exchange(packed) == packed)
            return;
        loggedDynamicAsked = false;
        std::string text = dynamic ? "dynamic" : multiplier >= 2 ? std::to_string(multiplier) + "x" : "none asked for";
        LogLine("Frame generation multiplier requested: " + text);
    }

    bool FrameGeneration::GeneratedLastPresent() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return generatedLastPresent;
    }

    bool FrameGeneration::Enabled() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return wanted;
    }

    bool FrameGeneration::ContextSize(UINT& width, UINT& height) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        width = contextWidth;
        height = contextHeight;
        return contextCreated;
    }

    bool FrameGeneration::ContextAsync() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return contextCreated && contextAsync;
    }

    bool FrameGeneration::HasInputs() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return S(depth).Valid() && S(motion).Valid();
    }

    bool FrameGeneration::HasHudLess() const
    {
        std::lock_guard<std::mutex> lock(mutex);
        return S(hudLess).Valid();
    }

    void FrameGeneration::LastHudLessCheck(float& direct, float& mirrored) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        direct = lastDirect;
        mirrored = lastMirrored;
    }
}
