// The surface the C# mod calls into.
//
// Exported from dxgi.dll, which is already loaded in the process, so the managed
// side reaches it with a plain DllImport("dxgi.dll") and no extra file to ship.
//
// A flat C surface with scalar arguments wherever a call is made
// from the main thread. The one structure that crosses the boundary -- the
// per-frame packet -- crosses it through Unity's render thread, because that is
// the only way its values arrive with the frame they describe; its layout is
// checked on both sides (see FramePacket).

#include "AmdUpscaler.h"
#include "Config.h"
#include "D3d12Compute.h"
#include "Dlss.h"
#include "FrameGeneration.h"
#include "LoadMonitor.h"
#include "Log.h"
#include "NvidiaGpu.h"
#include "Streamline.h"
#include "SwapChainProxy.h"

#include <windows.h>

#include <cstdio>
#include <cstring>

// Unity spells its render event callback with this macro. On x64 Windows there
// is only one calling convention, so it expands to nothing; keeping the name
// makes the signature recognisable as the one Unity expects.
#define UNITY_INTERFACE_API

extern "C"
{
    // Texture pointers come from RenderTexture.GetNativeTexturePtr, which returns
    // ID3D11Resource on D3D11. Measured stable for the life of the rig, so this
    // is called once per rig rather than per frame.
    //
    // Renamed whenever its arguments change, so that a managed build out of step
    // with the native one fails to find the entry point and turns frame
    // generation off -- instead of handing one texture over as another.
    __declspec(dllexport) void KspFgRegisterInputs3(void* depth, void* motionVectors, void* hudLess)
    {
        redefinition::FrameGeneration::Get().RegisterInputs(
            static_cast<ID3D11Resource*>(depth),
            static_cast<ID3D11Resource*>(motionVectors),
            static_cast<ID3D11Resource*>(hudLess));
    }

    __declspec(dllexport) void KspFgSetEnabled(int enabled)
    {
        redefinition::FrameGeneration::Get().SetEnabled(enabled != 0);
    }

    // Whether frame generation is switched on in this proxy's ini. With
    // frameGeneration=0 every export is still here and generates nothing, so
    // the managed side asks before it offers the switch.
    __declspec(dllexport) int KspFgConfigured()
    {
        return redefinition::Config().frameGeneration ? 1 : 0;
    }

    // Whether frames can be generated at all: a swapchain made through
    // Streamline or FidelityFX exists. Not so without NVIDIA's or AMD's runtime,
    // with measureOnly=1 or after a failed setup, whatever the ini allows. A frame generation context
    // that fails to be made is not counted here but in KspFgContextFailing: the
    // proxy tries again once a second while packets arrive, and not at all once
    // the failure stands.
    __declspec(dllexport) int KspFgCanGenerate()
    {
        return redefinition::FrameGenerationSwapChainPresent() ? 1 : 0;
    }

    // Which frame generation the swapchain presents with -- 2 DLSS, 1 FSR, 0
    // none -- and, for DLSS, frames shown per rendered frame while it generates,
    // 0 while it does not. For the mod's settings, which show what runs.
    __declspec(dllexport) int KspFgTechnique(int* multiplier)
    {
        if (multiplier != nullptr)
            *multiplier = redefinition::FrameGeneration::Get().StreamlineMultiplier();
        return redefinition::FrameGenerationTechnique();
    }

    // Whether DLSS frame generation presents with V-Sync where KSP asks for it: 0
    // where its build said it does not support it (DLSSGState::
    // bIsVsyncSupportAvailable), 1 otherwise, before it has said too. For the
    // mod's V-Sync row.
    __declspec(dllexport) int KspFgDlssVsync()
    {
        return redefinition::FrameGeneration::Get().StreamlineVsyncSupported() ? 1 : 0;
    }

    // The NVIDIA GPU of the adapter KSP renders on (NvidiaGpu.h): its NVAPI
    // architecture and implementation, what the mod offers NVIDIA's DLLs for
    // download by. Returns 0 where it is not NVIDIA's, where NVAPI does not answer,
    // or before the swapchain exists.
    __declspec(dllexport) int KspNvidiaGpu(unsigned int* architecture, unsigned int* implementation)
    {
        redefinition::NvidiaGpuInfo info;
        const bool known = redefinition::KnownNvidiaGpu(info);
        if (architecture != nullptr) *architecture = info.architecture;
        if (implementation != nullptr) *implementation = info.implementation;
        return known ? 1 : 0;
    }

    // Where the ini says NVIDIA's DLLs lie, if not next to KSP_x64.exe -- dlssDirectory
    // and streamlineDirectory, empty where unset -- so the mod looks for them and
    // places them where the proxy loads them from. UTF-16, cut to size characters.
    __declspec(dllexport) void KspNvidiaDirectories(wchar_t* dlss, wchar_t* streamline, int size)
    {
        if (size <= 0)
            return;
        if (dlss != nullptr)
            wcsncpy_s(dlss, static_cast<size_t>(size), redefinition::DlssDirectory().c_str(), _TRUNCATE);
        if (streamline != nullptr)
            wcsncpy_s(streamline, static_cast<size_t>(size), redefinition::StreamlineDirectory().c_str(), _TRUNCATE);
    }

    // How the proxy's last attempt to make the frame generation context went:
    // FrameGeneration::kContextFine, kContextRetrying or kContextStands.
    __declspec(dllexport) int KspFgContextFailing()
    {
        return redefinition::FrameGeneration::Get().ContextFailing();
    }

    // The player switched frame generation on, or a new scene began: a context
    // that could not be made gets another try at once, a standing failure
    // included.
    __declspec(dllexport) void KspFgRetryContext()
    {
        redefinition::FrameGeneration::Get().RetryContext();
    }

    // A HUD-less and motion vector check at once, rather than after reportSeconds:
    // the mod's Debug switch, on a fast camera turn.
    __declspec(dllexport) void KspFgRequestCheck()
    {
        redefinition::FrameGeneration::Get().RequestCheck();
    }

    // Running totals for the mod's window: frames the game presented through
    // the proxy, and frames the swapchain presented, generated ones included.
    // Both wrap at 2^32; the caller differences two readings a second apart.
    // One snapshot, so the two always belong to the same frame. Returns 0
    // while no frame has been counted.
    __declspec(dllexport) int KspFgCounters(unsigned int* rendered, unsigned int* presented)
    {
        uint32_t r = 0;
        uint32_t p = 0;
        const bool valid = redefinition::FrameCounters(r, p);
        if (rendered != nullptr) *rendered = r;
        if (presented != nullptr) *presented = p;
        return valid ? 1 : 0;
    }

    // The game's main thread, so the proxy can measure its load (LoadMonitor).
    // Called once by the mod, from that thread.
    __declspec(dllexport) void KspPerfRegisterMainThread()
    {
        redefinition::LoadMonitor::Get().RegisterMainThread();
    }

    // The last completed second of load, each in percent, -1 where unknown:
    // the main thread's and the render thread's CPU time as a share of one
    // core, the busiest 3D engine of this process's adapter, and this
    // process's share of it. Returns 1 with figures, 0 before a second has
    // completed, -1 when the proxy is switched off in its ini.
    __declspec(dllexport) int KspPerfLoad(float* mainThread, float* renderThread, float* gpu, float* gpuThisProcess)
    {
        if (!redefinition::LoadMonitor::Get().Allowed())
            return -1;

        redefinition::LoadSample sample;
        const bool valid = redefinition::LoadMonitor::Get().Latest(sample);
        if (mainThread != nullptr) *mainThread = sample.mainThread;
        if (renderThread != nullptr) *renderThread = sample.renderThread;
        if (gpu != nullptr) *gpu = sample.gpu;
        if (gpuThisProcess != nullptr) *gpuThisProcess = sample.gpuThisProcess;
        return valid ? 1 : 0;
    }

    // What the managed side must write per frame, so it can check its own
    // sizeof before sending anything.
    __declspec(dllexport) unsigned int KspFgPacketSize()
    {
        return static_cast<unsigned int>(sizeof(redefinition::FramePacket));
    }

    // Unity calls this on its render thread when the managed side raises the
    // event from a CommandBuffer -- UnityRenderingEventAndData: the event id
    // and the pointer given to IssuePluginEventAndData.
    //
    // 1: this frame's packet. The textures themselves are copied at Present,
    //    when everything Unity drew this frame is submitted.
    // 2: a DLSS frame (Dlss::OnPacket), evaluated here -- from the upscaler's
    //    dispatch buffer, which Unity runs after the frame's drawing is
    //    submitted (render events from a camera's own buffers lag a frame).
    // 3: the rig is gone, and the DLSS feature with it.
    // 4: a frame for AMD's upscaler (AmdUpscaler::OnPacket), the same way.
    // 5: the rig is gone, and AMD's context with it.
    // 6: a question for DLSS's render sizes (Dlss::OnSizeQuery), before a rig.
    // 20-23: Direct3D 12 for mods (D3d12Compute.h): a compute pass made, destroyed,
    //    dispatched, a texture's shared copy released.
    static void UNITY_INTERFACE_API OnRenderEvent(int eventId, void* data)
    {
        if (eventId == 1)
            redefinition::FrameGeneration::Get().OnFramePacket(data);
        else if (eventId == 2)
            redefinition::Dlss::Get().OnPacket(data);
        else if (eventId == 3)
            redefinition::Dlss::Get().OnRelease();
        else if (eventId == 4)
            redefinition::AmdUpscaler::Get().OnPacket(data);
        else if (eventId == 5)
            redefinition::AmdUpscaler::Get().OnRelease();
        else if (eventId == 6)
            redefinition::Dlss::Get().OnSizeQuery(data);
        else if (eventId == 20)
            redefinition::D3d12Compute::Get().OnCreatePass(data);
        else if (eventId == 21)
            redefinition::D3d12Compute::Get().OnDestroyPass(data);
        else if (eventId == 22)
            redefinition::D3d12Compute::Get().OnDispatch(data);
        else if (eventId == 23)
            redefinition::D3d12Compute::Get().OnReleaseTexture(data);
    }

    __declspec(dllexport) void* KspFgGetRenderEventFunc()
    {
        return reinterpret_cast<void*>(&OnRenderEvent);
    }

    // DLSS (Dlss.h): the packet size the managed side must write, what the
    // native side sees (1 running, -1 stopped, -2 stopped for a reason that
    // stands until the GPU, driver or DLL changes, 0 not yet), and the render sizes
    // DLSS asked for at an output size and quality value once a feature or a
    // query was made for them -- the optimal one and the range, each width and
    // height; 0 before.
    __declspec(dllexport) unsigned int KspDlssPacketSize()
    {
        return static_cast<unsigned int>(sizeof(redefinition::DlssPacket));
    }

    // AMD's upscaler (AmdUpscaler.h): the packet size, and what the native side
    // sees, 1 running, -1 stopped, -2 stopped for a reason that stands (no usable
    // DLL, while its folder and file are unchanged), 0 not yet.
    __declspec(dllexport) unsigned int KspAmdUpscalerPacketSize()
    {
        return static_cast<unsigned int>(sizeof(redefinition::AmdUpscalerPacket));
    }

    __declspec(dllexport) int KspAmdUpscalerStatus(char* buffer, int size)
    {
        return redefinition::AmdUpscaler::Get().Status(buffer, size);
    }

    // 1 while a swapchain proxy presents through D3D12 and AMD's upscaler has its
    // devices, which it cannot run without.
    __declspec(dllexport) int KspAmdUpscalerReady()
    {
        return redefinition::AmdUpscaler::Get().Attached() ? 1 : 0;
    }

    __declspec(dllexport) int KspDlssStatus(char* buffer, int size)
    {
        return redefinition::Dlss::Get().Status(buffer, size);
    }

    // "2" in the name: the export takes an output size, and a managed side of
    // another layout finds no entry point. The managed side probes this name
    // before it sends anything (DlssBridge).
    __declspec(dllexport) int KspDlssRenderSizes2(unsigned int outputWidth, unsigned int outputHeight, int quality,
                                                  unsigned int* optimal, unsigned int* minimum, unsigned int* maximum)
    {
        uint32_t o[2] = {};
        uint32_t lo[2] = {};
        uint32_t hi[2] = {};
        const bool valid = redefinition::Dlss::Get().RenderSizes(outputWidth, outputHeight, quality, o, lo, hi);
        for (int i = 0; i < 2; ++i)
        {
            if (optimal != nullptr) optimal[i] = o[i];
            if (minimum != nullptr) minimum[i] = lo[i];
            if (maximum != nullptr) maximum[i] = hi[i];
        }
        return valid ? 1 : 0;
    }

    // Direct3D 12 for mods (D3d12Compute.h). Whether the proxy presents through
    // Direct3D 12, with what the device supports, each in Direct3D's own encoding:
    // D3D_FEATURE_LEVEL, D3D_SHADER_MODEL, D3D12_RAYTRACING_TIER, D3D12_MESH_SHADER_TIER,
    // D3D12_VARIABLE_SHADING_RATE_TIER. Returns 0 while there is no device.
    __declspec(dllexport) int KspD3d12Capabilities(int* featureLevel, int* shaderModel, int* raytracingTier,
                                                   int* meshShaderTier, int* variableShadingRateTier)
    {
        redefinition::D3d12Compute::Capabilities found;
        const bool available = redefinition::D3d12Compute::Get().Available(found);
        if (featureLevel != nullptr) *featureLevel = found.featureLevel;
        if (shaderModel != nullptr) *shaderModel = found.shaderModel;
        if (raytracingTier != nullptr) *raytracingTier = found.raytracingTier;
        if (meshShaderTier != nullptr) *meshShaderTier = found.meshShaderTier;
        if (variableShadingRateTier != nullptr) *variableShadingRateTier = found.variableShadingRateTier;
        return available ? 1 : 0;
    }

    __declspec(dllexport) unsigned int KspD3d12CreatePacketSize()
    {
        return static_cast<unsigned int>(sizeof(redefinition::D3d12CreatePacket));
    }

    __declspec(dllexport) unsigned int KspD3d12DispatchPacketSize()
    {
        return static_cast<unsigned int>(sizeof(redefinition::D3d12DispatchPacket));
    }

    // HLSL to a compute shader's DXBC (D3d12Compute.h, CompileComputeShader): from source
    // of sourceLength bytes, or from the file at path when source is null. 1 compiled, 0 the
    // compiler's errors in messages, -1 capacity too small, -2 no d3dcompiler_47.dll.
    __declspec(dllexport) int KspD3d12Compile(const char* source, int sourceLength, const wchar_t* path,
                                              const char* entry, unsigned char* bytecode, int capacity, int* written,
                                              char* messages, int messagesSize)
    {
        int size = 0;
        const int result = redefinition::CompileComputeShader(source, sourceLength, path, entry, bytecode, capacity, size,
                                                     messages, messagesSize);
        if (written != nullptr)
            *written = size;
        return result;
    }

    // A pass: 1 built, 0 not yet, -1 failed or unknown, with the text.
    __declspec(dllexport) int KspD3d12PassStatus(int pass, char* buffer, int size)
    {
        return redefinition::D3d12Compute::Get().PassStatus(pass, buffer, size);
    }

    __declspec(dllexport) int KspD3d12Status(char* buffer, int size)
    {
        return redefinition::D3d12Compute::Get().Status(buffer, size);
    }

    // What the native side sees, for the mod's own diagnostics.
    __declspec(dllexport) int KspFgStatus(char* buffer, int size)
    {
        if (buffer == nullptr || size <= 0)
            return 0;

        redefinition::FrameGeneration& fg = redefinition::FrameGeneration::Get();

        char contextSize[32] = "no";
        UINT contextWidth = 0;
        UINT contextHeight = 0;
        if (fg.ContextSize(contextWidth, contextHeight))
            redefinition::FormatTo(contextSize, "%ux%u", contextWidth, contextHeight);

        char check[64] = "none yet";
        float direct = -1.0f;
        float mirrored = -1.0f;
        fg.LastHudLessCheck(direct, mirrored);
        if (direct >= 0.0f)
            redefinition::FormatTo(check, "direct %.2f%%, mirrored %.2f%% differ",
                      100.0 * static_cast<double>(direct), 100.0 * static_cast<double>(mirrored));

        UINT checkWidth = 0;
        UINT checkHeight = 0;
        fg.CheckMotionSize(checkWidth, checkHeight);

        char device[40] = "ok";
        const HRESULT removed = redefinition::DeviceRemovedReason();
        if (FAILED(removed))
            redefinition::FormatTo(device, "removed (0x%08lX)", static_cast<unsigned long>(removed));

        // Which frame generation, and for DLSS what it generates or why it does
        // not run -- last in the line, which readers may cut.
        char technique[200] = "none";
        const int running = redefinition::FrameGenerationTechnique();
        if (running == 2)
            redefinition::FormatTo(technique, "dlss (%dx)", fg.StreamlineMultiplier());
        else if (redefinition::Config().frameGeneration && redefinition::Config().dlssFrameGeneration)
            _snprintf_s(technique, _TRUNCATE, "%s (dlss: %s)", running == 1 ? "fsr" : "none",
                        redefinition::Streamline::Get().Describe().c_str());
        else if (running == 1)
            strcpy_s(technique, "fsr");

        // Cut to the buffer: sprintf_s would end the process on a longer text.
        const int written = _snprintf_s(buffer, static_cast<size_t>(size), _TRUNCATE,
                                        "requested=%s context=%s async=%s inputs=%s hudless=%s device=%s"
                                        " retired=%u check=%ux%u (last check: %s) technique=%s",
                                        fg.Enabled() ? "yes" : "no",
                                        contextSize,
                                        fg.ContextAsync() ? "yes" : "no",
                                        fg.HasInputs() ? "yes" : "no",
                                        fg.HasHudLess() ? "yes" : "no",
                                        device,
                                        static_cast<unsigned int>(fg.RetiredCount()),
                                        checkWidth, checkHeight,
                                        check,
                                        technique);

        return written < 0 ? static_cast<int>(strnlen(buffer, static_cast<size_t>(size))) : written;
    }

    // The share of the frame that differed from its HUD-less copy at the last
    // completed check, direct and mirrored. Returns 0 until a check has
    // completed. For the harness, which has no game log to read.
    __declspec(dllexport) int KspFgLastHudLessCheck(float* direct, float* mirrored)
    {
        float d = -1.0f;
        float m = -1.0f;
        redefinition::FrameGeneration::Get().LastHudLessCheck(d, m);
        if (d < 0.0f || direct == nullptr || mirrored == nullptr)
            return 0;

        *direct = d;
        *mirrored = m;
        return 1;
    }
}
