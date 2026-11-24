#include "Dlss.h"
#include "DlssD3d12.h"

#include "Config.h"
#include "FileUtil.h"
#include "JitterTrace.h"
#include "Log.h"
#include "NgxCore.h"
#include "ProjectIdentity.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <cwchar>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // NVSDK_NGX_Feature_SuperSampling, and DLAA's quality value (DlssPacket::quality).
        constexpr int kNgxFeatureSuperSampling = 1;
        constexpr int kNgxQualityDlaa = 5;

        // The driver's D3D11 exports (NgxCore.h for the types they share with
        // D3D12's). Init_ProjectID takes the SDK version before the common info,
        // unlike the SDK's own Init_with_ProjectID -- the order OptiScaler's NGX
        // proxy calls the driver with.
        using InitProjectIdFn = uint32_t (*)(const char* projectId, int engineType, const char* engineVersion,
                                             const wchar_t* dataPath, ID3D11Device* device, int sdkVersion,
                                             const NgxCommonInfo* info);
        using CreateFeatureFn = uint32_t (*)(ID3D11DeviceContext* context, int feature, NgxParameters* parameters,
                                             void** handle);
        using EvaluateFeatureFn = uint32_t (*)(ID3D11DeviceContext* context, const void* handle,
                                               const NgxParameters* parameters, void* progress);
        // NVSDK_NGX_D3D11_Shutdown, not Shutdown1, as for Ray Reconstruction (DlssRayReconstruction.cpp).
        // With no nvngx_dlss.dll beside the game, Shutdown1 on the device ended the process with an
        // access violation right after the capability map said DLSS was not available. This happened
        // on driver 617.14 with an RTX 4090, and the same on an RTX A4500 (RaytracingHarness with the
        // file moved away, 2026-10-25). Shutdown returns. It shuts NGX down on every Direct3D 11
        // device, and this is the only one that starts it there.
        using ShutdownFn = uint32_t (*)();

        // The driver's D3D11 exports the proxy calls.
        struct NgxD3d11
        {
            // Init, the capability and parameter maps, the feature's creation, evaluation and release, shutdown.
            InitProjectIdFn initProjectId = nullptr;
            NgxGetParametersFn getCapabilityParameters = nullptr;
            NgxGetParametersFn getParameters = nullptr;
            NgxDestroyParametersFn destroyParameters = nullptr;
            CreateFeatureFn createFeature = nullptr;
            EvaluateFeatureFn evaluateFeature = nullptr;
            NgxReleaseFeatureFn releaseFeature = nullptr;
            ShutdownFn shutdown = nullptr;
        };

        // The exports, resolved once from the driver's core.
        NgxD3d11 g_ngx;

        // The GPU a device renders on, for messages.
        std::string AdapterName(ID3D11Device* device)
        {
            ComPtr<IDXGIDevice> dxgiDevice;
            ComPtr<IDXGIAdapter> adapter;
            DXGI_ADAPTER_DESC desc = {};
            if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice))) || FAILED(dxgiDevice->GetAdapter(&adapter))
                || FAILED(adapter->GetDesc(&desc)))
                return "this GPU";
            return Narrow(desc.Description);
        }

        // An NGX result as eight hexadecimal digits, for messages.
        std::string Hex(uint32_t value)
        {
            return NgxHex(value);
        }

        // A size as "1920x1080", for messages.
        std::string Size(unsigned int width, unsigned int height)
        {
            return NgxSize(width, height);
        }

        // What NGX's search for nvngx_dlss.dll comes down to: the folders it is
        // given, and the file in each.
        std::wstring SearchSignature()
        {
            std::wstring signature;
            for (const std::wstring& folder : { DlssDirectory(), ExecutableDirectory() })
            {
                if (!folder.empty())
                    signature += folder + L"|" + FileStamp(folder + L"\\nvngx_dlss.dll") + L";";
            }
            return signature;
        }

        // Whether a feature made for a can take b: the same sizes, quality, preset and creation flags.
        bool SameFeature(const DlssPacket& a, const DlssPacket& b)
        {
            const uint32_t creationFlags = kDlssHdr | kDlssDepthInverted | kDlssAutoExposure;
            return a.renderWidth == b.renderWidth && a.renderHeight == b.renderHeight
                && a.outputWidth == b.outputWidth && a.outputHeight == b.outputHeight
                && a.quality == b.quality && a.preset == b.preset
                && (a.flags & creationFlags) == (b.flags & creationFlags);
        }

        // The parameter map behind the pointer the class keeps.
        NgxParameters* Map(void* parameters)
        {
            return static_cast<NgxParameters*>(parameters);
        }

        // A test start's watch on DLSS's four textures (-redefinitionUpscalerScan; K-T2 2026-11-18: a GPU page fault inside DLSS, a write to an address with no resource, with the
        // own clouds only and never with FSR at the same size). Each texture DLSS is given is marked with a serial in its private data; the log names every change of a slot,
        // and a texture at the same address as before that carries no mark (or another serial) is a new one in the old one's place.
        const bool kWatchInputs = [] {
            const wchar_t* line = GetCommandLineW();
            return line != nullptr && wcsstr(line, L"-redefinitionUpscalerScan") != nullptr;
        }();
        // {6C1F0B57-3E2A-4D7B-9B52-1A0E5D2C7F31}
        const GUID kWatchSerial = { 0x6c1f0b57, 0x3e2a, 0x4d7b, { 0x9b, 0x52, 0x1a, 0x0e, 0x5d, 0x2c, 0x7f, 0x31 } };
        uint32_t g_nextSerial = 1;
        struct WatchedSlot
        {
            void* pointer = nullptr;
            uint32_t serial = 0;
        };
        WatchedSlot g_watched[4];
        uint64_t g_watchFrame = 0;

        std::string DescribeTexture(ID3D11Resource* resource)
        {
            ComPtr<ID3D11Texture2D> texture;
            if (resource == nullptr || FAILED(resource->QueryInterface(IID_PPV_ARGS(&texture))))
                return "not a 2D texture";
            D3D11_TEXTURE2D_DESC desc = {};
            texture->GetDesc(&desc);
            char text[200];
            sprintf_s(text, "%ux%u format %u mips %u array %u samples %u/%u usage %u bind 0x%X misc 0x%X", desc.Width, desc.Height,
                      static_cast<unsigned>(desc.Format), desc.MipLevels, desc.ArraySize, desc.SampleDesc.Count, desc.SampleDesc.Quality,
                      static_cast<unsigned>(desc.Usage), desc.BindFlags, desc.MiscFlags);
            return text;
        }

        // A test start's control (-redefinitionDlssPrivateCopies; the same K-T2 fault): NGX is handed textures of the proxy's own, made once with
        // the descriptions of Unity's, which the frame's inputs are copied into and its output out of. If the fault goes, it lies in how Unity's
        // textures live (aliasing, lifetime); if it stays, inside NGX.
        const bool kPrivateCopies = [] {
            const wchar_t* line = GetCommandLineW();
            return line != nullptr && wcsstr(line, L"-redefinitionDlssPrivateCopies") != nullptr;
        }();
        // A test start's control (-redefinitionDlssFence; the same K-T2 fault, always within half a second of the probe's placement): the GPU's
        // work is waited for right before NGX's evaluation and right after it, and the device's state is read each time. A device removed after
        // the evaluation but not before it puts the fault inside NGX's own work; removed before it, in the frame's other work.
        const bool kFence = [] {
            const wchar_t* line = GetCommandLineW();
            return line != nullptr && wcsstr(line, L"-redefinitionDlssFence") != nullptr;
        }();
        uint64_t g_fenceFrame = 0;
        bool g_fenceReported = false;
        int g_fenceSinceReset = 1000;

        // The last frames' packets under the fence, written out when the device is lost in NGX's own work: what NGX was handed then.
        constexpr int kFenceRing = 12;
        DlssPacket g_fenceRing[kFenceRing] = {};
        uint64_t g_fenceRingFrame[kFenceRing] = {};
        // Each frame's four textures as width x height (colour, output, depth, motion vectors): a size change at the cut shows.
        uint32_t g_fenceRingSizes[kFenceRing][8] = {};

        void TextureSize(void* resource, uint32_t& width, uint32_t& height)
        {
            ComPtr<ID3D11Texture2D> texture;
            width = height = 0;
            if (resource == nullptr || FAILED(static_cast<ID3D11Resource*>(resource)->QueryInterface(IID_PPV_ARGS(&texture))))
                return;
            D3D11_TEXTURE2D_DESC desc = {};
            texture->GetDesc(&desc);
            width = desc.Width;
            height = desc.Height;
        }

        float HalfValue(uint16_t h)
        {
            const int exponent = (h >> 10) & 0x1F;
            const int mantissa = h & 0x3FF;
            const float magnitude = exponent == 0 ? mantissa / 1024.0f / 16384.0f : (1.0f + mantissa / 1024.0f) * ldexpf(1.0f, exponent - 15);
            return (h & 0x8000) != 0 ? -magnitude : magnitude;
        }

        // The values a texture of NGX's inputs holds, read back synchronously (only under the fence, in the frames of a reset and the few after it): the
        // values that are not finite, the largest magnitude, and for depth the range and the values outside 0..1. K-T2 2026-11-18: the fault came in the
        // evaluation of the first frames after the jump into the inside view, never at a reset with the camera still; the asynchronous scan never got
        // those frames back.
        // Read within the render rectangle (DLSS reads only that), motion vectors in pixels (times the packet's scale).
        std::string FenceScan(ID3D11Device* device, ID3D11DeviceContext* context, void* resource, const char* name, const DlssPacket& packet)
        {
            ComPtr<ID3D11Texture2D> texture;
            if (resource == nullptr || FAILED(static_cast<ID3D11Resource*>(resource)->QueryInterface(IID_PPV_ARGS(&texture))))
                return std::string(name) + ": not a 2D texture";
            D3D11_TEXTURE2D_DESC desc = {};
            texture->GetDesc(&desc);
            const DXGI_FORMAT format = desc.Format;
            const bool half4 = format == DXGI_FORMAT_R16G16B16A16_TYPELESS || format == DXGI_FORMAT_R16G16B16A16_FLOAT;
            const bool half2 = format == DXGI_FORMAT_R16G16_TYPELESS || format == DXGI_FORMAT_R16G16_FLOAT;
            const bool float1 = format == DXGI_FORMAT_R32_TYPELESS || format == DXGI_FORMAT_R32_FLOAT || format == DXGI_FORMAT_D32_FLOAT;
            if (!half4 && !half2 && !float1)
                return std::string(name) + ": format " + std::to_string(static_cast<unsigned>(format)) + " not read";
            D3D11_TEXTURE2D_DESC staging = desc;
            staging.MipLevels = 1;
            staging.ArraySize = 1;
            staging.Usage = D3D11_USAGE_STAGING;
            staging.BindFlags = 0;
            staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            staging.MiscFlags = 0;
            ComPtr<ID3D11Texture2D> copy;
            if (FAILED(device->CreateTexture2D(&staging, nullptr, &copy)))
                return std::string(name) + ": no staging texture";
            // The first mip of the first slice: the staging copy has one of each, whatever the source has.
            context->CopySubresourceRegion(copy.Get(), 0, 0, 0, 0, texture.Get(), 0, nullptr);
            D3D11_MAPPED_SUBRESOURCE mapped = {};
            const HRESULT mappedResult = context->Map(copy.Get(), 0, D3D11_MAP_READ, 0, &mapped);
            if (FAILED(mappedResult))
            {
                char failed[120];
                sprintf_s(failed, "%s: not mapped (0x%08lX)", name, static_cast<unsigned long>(mappedResult));
                return failed;
            }
            const int channels = half4 ? 4 : half2 ? 2 : 1;
            const UINT width = packet.renderWidth < desc.Width ? packet.renderWidth : desc.Width;
            const UINT height = packet.renderHeight < desc.Height ? packet.renderHeight : desc.Height;
            uint64_t notFinite = 0, aboveOne = 0, outside = 0;
            float largest = 0.0f, lowest = 3.4e38f, highest = -3.4e38f;
            for (UINT y = 0; y < height; ++y)
            {
                const uint8_t* row = static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch;
                for (UINT x = 0; x < width; ++x)
                {
                    for (int c = 0; c < channels; ++c)
                    {
                        float v;
                        if (float1)
                        {
                            memcpy(&v, row + x * 4, 4);
                        }
                        else
                        {
                            uint16_t h;
                            memcpy(&h, row + (static_cast<size_t>(x) * channels + c) * 2, 2);
                            if ((h & 0x7C00) == 0x7C00)
                            {
                                ++notFinite;
                                continue;
                            }
                            v = HalfValue(h);
                        }
                        if (!std::isfinite(v))
                        {
                            ++notFinite;
                            continue;
                        }
                        // Motion vectors in pixels; one longer than the render size in its axis counts.
                        const float a = half2 ? fabsf(v * (c == 0 ? packet.motionVectorScaleX : packet.motionVectorScaleY)) : fabsf(v);
                        if (a > largest)
                            largest = a;
                        if (half2 ? a > static_cast<float>(c == 0 ? packet.renderWidth : packet.renderHeight) : a > 1.0f)
                            ++aboveOne;
                        if (float1)
                        {
                            lowest = v < lowest ? v : lowest;
                            highest = v > highest ? v : highest;
                            if (v < 0.0f || v > 1.0f)
                                ++outside;
                        }
                    }
                }
            }
            context->Unmap(copy.Get(), 0);
            char text[360];
            if (float1)
                sprintf_s(text, "%s %ux%u (mips %u array %u samples %u, read %ux%u): %llu not finite, range %.6g..%.6g, %llu outside 0..1", name, desc.Width,
                          desc.Height, desc.MipLevels, desc.ArraySize, desc.SampleDesc.Count, width, height, static_cast<unsigned long long>(notFinite), lowest,
                          highest, static_cast<unsigned long long>(outside));
            else if (half2)
                sprintf_s(text, "%s %ux%u (mips %u array %u samples %u, read %ux%u): %llu not finite, longest %.6g px, %llu longer than the render size",
                          name, desc.Width, desc.Height, desc.MipLevels, desc.ArraySize, desc.SampleDesc.Count, width, height,
                          static_cast<unsigned long long>(notFinite), largest, static_cast<unsigned long long>(aboveOne));
            else
                sprintf_s(text, "%s %ux%u (mips %u array %u samples %u, read %ux%u): %llu not finite, largest %.6g", name, desc.Width, desc.Height,
                          desc.MipLevels, desc.ArraySize, desc.SampleDesc.Count, width, height, static_cast<unsigned long long>(notFinite), largest);
            return text;
        }

        std::string PacketLine(const DlssPacket& p, uint64_t frame, const uint32_t (&sizes)[8])
        {
            char text[360];
            sprintf_s(text, "frame %llu: flags 0x%X%s, jitter %.6g %.6g, motion vector scale %.6g %.6g, render %ux%u, output %ux%u, quality %d, preset %u; "
                            "textures colour %ux%u output %ux%u depth %ux%u motion %ux%u",
                      static_cast<unsigned long long>(frame), p.flags, (p.flags & kDlssReset) != 0 ? " (reset)" : "", p.jitterX, p.jitterY,
                      p.motionVectorScaleX, p.motionVectorScaleY, p.renderWidth, p.renderHeight, p.outputWidth, p.outputHeight, p.quality, p.preset,
                      sizes[0], sizes[1], sizes[2], sizes[3], sizes[4], sizes[5], sizes[6], sizes[7]);
            return text;
        }

        // Waits until the GPU has done all work handed to the context so far; false where the device was removed (the reason is logged once).
        bool FenceAlive(ID3D11Device* device, ID3D11DeviceContext* context, const char* where)
        {
            if (g_fenceReported)
                return false;
            // Made once per device and never released: at process exit a static destructor would release it after d3d11.dll may be gone.
            static ID3D11Query* query = nullptr;
            static ID3D11Device* queryDevice = nullptr;
            HRESULT reason = device->GetDeviceRemovedReason();
            HRESULT data = S_OK;
            if (reason == S_OK && (query == nullptr || queryDevice != device))
            {
                D3D11_QUERY_DESC desc = { D3D11_QUERY_EVENT, 0 };
                if (query != nullptr)
                    query->Release();
                query = nullptr;
                queryDevice = device;
                const HRESULT made = device->CreateQuery(&desc, &query);
                if (FAILED(made))
                {
                    g_fenceReported = true;
                    char text[120];
                    sprintf_s(text, "DLSS fence: no event query could be made (0x%08lX); the fence is off", static_cast<unsigned long>(made));
                    LogLine(text);
                    return false;
                }
            }
            bool timedOut = false;
            if (reason == S_OK)
            {
                context->End(query);
                BOOL done = FALSE;
                // GetData flushes the context; the loop ends with the work, with the device, or after 5 s (a GPU that hangs without a reset).
                const ULONGLONG start = GetTickCount64();
                while ((data = context->GetData(query, &done, sizeof(done), 0)) == S_FALSE)
                {
                    reason = device->GetDeviceRemovedReason();
                    if (reason != S_OK)
                        break;
                    if (GetTickCount64() - start > 5000)
                    {
                        timedOut = true;
                        break;
                    }
                    YieldProcessor();
                }
                // A query that failed is the device's end before the device reports it: its reason is read for up to 200 ms, so the removal is
                // put on this check and not on the next one.
                const ULONGLONG failedAt = GetTickCount64();
                do
                    reason = device->GetDeviceRemovedReason();
                while (reason == S_OK && FAILED(data) && GetTickCount64() - failedAt < 200);
            }
            if (reason == S_OK && SUCCEEDED(data) && !timedOut)
                return true;
            g_fenceReported = true;
            char text[260];
            sprintf_s(text, "DLSS fence: %s (reason 0x%08lX, query 0x%08lX), seen %s, frame %llu",
                      timedOut ? "the GPU did not finish within 5 s" : "the device was removed", static_cast<unsigned long>(reason),
                      static_cast<unsigned long>(data), where, static_cast<unsigned long long>(g_fenceFrame));
            LogLine(text);
            return false;
        }

        ComPtr<ID3D11Texture2D> g_private[4];
        D3D11_TEXTURE2D_DESC g_privateDesc[4] = {};

        // The private texture for a slot with the description of Unity's (made again where that changes); null where it cannot be made.
        ID3D11Resource* PrivateFor(ID3D11Device* device, int slot, void* unity)
        {
            ComPtr<ID3D11Texture2D> source;
            if (unity == nullptr || FAILED(static_cast<ID3D11Resource*>(unity)->QueryInterface(IID_PPV_ARGS(&source))))
                return nullptr;
            D3D11_TEXTURE2D_DESC desc = {};
            source->GetDesc(&desc);
            if (g_private[slot] == nullptr || memcmp(&desc, &g_privateDesc[slot], sizeof(desc)) != 0)
            {
                g_private[slot].Reset();
                if (FAILED(device->CreateTexture2D(&desc, nullptr, &g_private[slot])))
                    return nullptr;
                g_privateDesc[slot] = desc;
                LogLine("DLSS private copies: slot " + std::to_string(slot) + " made, " + std::to_string(desc.Width) + "x" + std::to_string(desc.Height)
                        + " format " + std::to_string(static_cast<unsigned>(desc.Format)));
            }
            return g_private[slot].Get();
        }

        void WatchInputs(const DlssPacket& packet)
        {
            if (!kWatchInputs)
                return;
            ++g_watchFrame;
            void* const slots[4] = { packet.colour, packet.output, packet.depth, packet.motionVectors };
            const char* const names[4] = { "colour", "output", "depth", "motion vectors" };
            for (int i = 0; i < 4; ++i)
            {
                ID3D11Resource* resource = static_cast<ID3D11Resource*>(slots[i]);
                uint32_t serial = 0;
                UINT size = sizeof(serial);
                const bool marked = resource != nullptr && SUCCEEDED(resource->GetPrivateData(kWatchSerial, &size, &serial)) && size == sizeof(serial);
                if (marked && slots[i] == g_watched[i].pointer && serial == g_watched[i].serial)
                    continue;
                if (!marked && resource != nullptr)
                {
                    serial = g_nextSerial++;
                    resource->SetPrivateData(kWatchSerial, sizeof(serial), &serial);
                }
                char head[200];
                sprintf_s(head, "DLSS input watch: frame %llu, %s %s %p (serial %u, before %p serial %u): ", static_cast<unsigned long long>(g_watchFrame), names[i],
                          slots[i] == g_watched[i].pointer ? "NEW OBJECT AT THE SAME ADDRESS" : "changed to", slots[i], serial, g_watched[i].pointer,
                          g_watched[i].serial);
                LogLine(std::string(head) + DescribeTexture(resource));
                g_watched[i].pointer = slots[i];
                g_watched[i].serial = serial;
            }
        }
    }

    // Never destroyed: at process exit a static destructor would release Unity's
    // D3D11 device after d3d11.dll may already be gone.
    Dlss& Dlss::Get()
    {
        static Dlss* instance = new Dlss();
        return *instance;
    }

    void Dlss::SetStatus(int newState, const std::string& text)
    {
        renderState = newState;
        bool changed = false;
        {
            std::lock_guard<std::mutex> lock(mutex);
            changed = state != newState || statusText != text;
            state = newState;
            statusText = text;
            stableSearch = newState == -2 ? failedSearch : std::wstring();
        }
        if (changed && newState < 0)
            LogLine("DLSS: " + text);
    }

    int Dlss::Status(char* buffer, int size) const
    {
        int reported = 0;
        std::wstring stood;
        {
            std::lock_guard<std::mutex> lock(mutex);
            // Cut to the buffer: sprintf_s would end the process on a longer text.
            if (buffer != nullptr && size > 0)
                strncpy_s(buffer, static_cast<size_t>(size), statusText.c_str(), _TRUNCATE);
            reported = state;
            if (reported == -2)
                stood = stableSearch;
        }
        // A failure that stands stands only while NGX's search is as it was: once
        // the player has copied nvngx_dlss.dll or set dlssDirectory, the next integration
        // tries again (EnsureInitialised). The files are asked outside the lock
        // the render thread takes.
        if (reported == -2 && SearchSignature() != stood)
            reported = -1;
        return reported;
    }

    bool Dlss::RenderSizes(uint32_t outputWidth, uint32_t outputHeight, int32_t quality, uint32_t optimal[2],
                           uint32_t minimum[2], uint32_t maximum[2]) const
    {
        std::lock_guard<std::mutex> lock(mutex);
        for (const RenderSizeEntry& entry : sizes)
        {
            if (entry.optimal[0] == 0 || entry.outputWidth != outputWidth || entry.outputHeight != outputHeight
                || entry.quality != quality)
                continue;
            for (int i = 0; i < 2; ++i)
            {
                optimal[i] = entry.optimal[i];
                minimum[i] = entry.minimum[i];
                maximum[i] = entry.maximum[i];
            }
            return true;
        }
        return false;
    }

    void Dlss::OnPacket(const void* data)
    {
        const DlssPacket* packet = static_cast<const DlssPacket*>(data);
        if (packet == nullptr || packet->size != sizeof(DlssPacket) || packet->magic != kDlssPacketMagic)
        {
            SetStatus(-1, "DLSS frame packet rejected: the mod and the proxy disagree on its layout");
            return;
        }

        const ID3D11Resource* colour = static_cast<ID3D11Resource*>(packet->colour);
        if (colour == nullptr || packet->output == nullptr || packet->depth == nullptr
            || packet->motionVectors == nullptr)
        {
            SetStatus(-1, "DLSS frame without all four textures");
            return;
        }

        if (JitterTraceOn())
        {
            static uint64_t traced = 0;
            char text[200];
            sprintf_s(text, "jitter trace: DLSS packet %llu: jitter %.6g %.6g, reset %d, render %ux%u, %s, t %.3f ms", static_cast<unsigned long long>(++traced),
                      packet->jitterX, packet->jitterY, (packet->flags & kDlssReset) != 0 ? 1 : 0, packet->renderWidth, packet->renderHeight,
                      DlssD3d12::On() ? "Direct3D 12" : "Direct3D 11", JitterTraceMs());
            LogLine(text);
        }

        // On a Direct3D 12 device of its own (DlssD3d12.h says why); its status is this class's, which the
        // managed side reads.
        if (DlssD3d12::On())
        {
            DlssD3d12& d3d12 = DlssD3d12::Get();
            d3d12.OnPacket(*packet);
            char text[256] = {};
            const int reported = d3d12.Status(text, sizeof(text));
            MirrorStatus(reported, text);
            return;
        }

        ComPtr<ID3D11Device> textureDevice;
        static_cast<ID3D11Resource*>(packet->colour)->GetDevice(&textureDevice);
        std::lock_guard<std::mutex> ngx(NgxMutex());
        if (!EnsureInitialised(textureDevice.Get()))
            return;

        ComPtr<ID3D11DeviceContext> context;
        textureDevice->GetImmediateContext(&context);
        if (!EnsureFeature(context.Get(), *packet))
            return;

        WatchInputs(*packet);
        if (kPrivateCopies)
        {
            DlssPacket copy = *packet;
            void* const unity[4] = { packet->colour, packet->output, packet->depth, packet->motionVectors };
            ID3D11Resource* mine[4] = {};
            for (int i = 0; i < 4; ++i)
                mine[i] = PrivateFor(textureDevice.Get(), i, unity[i]);
            if (mine[0] != nullptr && mine[1] != nullptr && mine[2] != nullptr && mine[3] != nullptr)
            {
                context->CopyResource(mine[0], static_cast<ID3D11Resource*>(unity[0]));
                context->CopyResource(mine[2], static_cast<ID3D11Resource*>(unity[2]));
                context->CopyResource(mine[3], static_cast<ID3D11Resource*>(unity[3]));
                copy.colour = mine[0];
                copy.output = mine[1];
                copy.depth = mine[2];
                copy.motionVectors = mine[3];
                if (Evaluate(context.Get(), copy))
                    context->CopyResource(static_cast<ID3D11Resource*>(unity[1]), mine[1]);
                return;
            }
        }
        if (kFence)
        {
            ++g_fenceFrame;
            g_fenceRing[g_fenceFrame % kFenceRing] = *packet;
            g_fenceRingFrame[g_fenceFrame % kFenceRing] = g_fenceFrame;
            uint32_t* ringSizes = g_fenceRingSizes[g_fenceFrame % kFenceRing];
            void* const textures[4] = { packet->colour, packet->output, packet->depth, packet->motionVectors };
            for (int i = 0; i < 4; ++i)
                TextureSize(textures[i], ringSizes[2 * i], ringSizes[2 * i + 1]);
            const bool aliveBefore = FenceAlive(textureDevice.Get(), context.Get(), "before NGX's evaluation (the frame's work up to it)");
            if ((packet->flags & kDlssReset) != 0)
                g_fenceSinceReset = 0;
            else if (g_fenceSinceReset < 1000)
                ++g_fenceSinceReset;
            if (aliveBefore && g_fenceSinceReset <= 3)
            {
                char head[80];
                sprintf_s(head, "DLSS fence: frame %llu, %d after a reset, NGX's inputs: ", static_cast<unsigned long long>(g_fenceFrame), g_fenceSinceReset);
                LogLine(std::string(head) + FenceScan(textureDevice.Get(), context.Get(), packet->colour, "colour", *packet) + "; "
                        + FenceScan(textureDevice.Get(), context.Get(), packet->depth, "depth", *packet) + "; "
                        + FenceScan(textureDevice.Get(), context.Get(), packet->motionVectors, "motion vectors", *packet));
            }
        }
        Evaluate(context.Get(), *packet);
        if (kFence && !g_fenceReported && !FenceAlive(textureDevice.Get(), context.Get(), "right after NGX's evaluation (its own work)"))
        {
            LogLine("DLSS fence: the packets of the last frames, oldest first:");
            for (int i = 1; i <= kFenceRing; ++i)
            {
                const int slot = static_cast<int>((g_fenceFrame + i) % kFenceRing);
                if (g_fenceRingFrame[slot] != 0)
                    LogLine("  " + PacketLine(g_fenceRing[slot], g_fenceRingFrame[slot], g_fenceRingSizes[slot]));
            }
            LogLine("DLSS fence: the feature was created for " + std::to_string(created.renderWidth) + "x" + std::to_string(created.renderHeight) + " to "
                    + std::to_string(created.outputWidth) + "x" + std::to_string(created.outputHeight) + ", flags " + Hex(created.flags)
                    + ", quality " + std::to_string(created.quality));
            void* const slots[4] = { packet->colour, packet->output, packet->depth, packet->motionVectors };
            const char* const names[4] = { "colour", "output", "depth", "motion vectors" };
            for (int i = 0; i < 4; ++i)
                LogLine(std::string("  ") + names[i] + ": " + DescribeTexture(static_cast<ID3D11Resource*>(slots[i])));
        }
    }

    void Dlss::MirrorStatus(int newState, const std::string& text)
    {
        renderState = newState;
        std::lock_guard<std::mutex> lock(mutex);
        state = newState;
        statusText = text;
        stableSearch = newState == -2 ? SearchSignature() : std::wstring();
    }

    void Dlss::OnRelease()
    {
        if (DlssD3d12::On())
        {
            DlssD3d12::Get().OnRelease();
            MirrorStatus(0, "DLSS feature released, no DLSS frame since");
            return;
        }
        std::lock_guard<std::mutex> ngx(NgxMutex());
        ReleaseFeature();
        hasFailed = false;
        // NGX's start is tried again by a new integration, except after a failure that
        // stands -- no RTX GPU, a driver too old, no usable nvngx_dlss.dll --
        // which keeps its status; EnsureInitialised tries that again once what
        // NGX searches has changed.
        if (failedDevice != nullptr && failedStable)
            return;
        failedDevice = nullptr;
        SetStatus(0, "DLSS feature released, no DLSS frame since");
    }

    bool Dlss::EnsureInitialised(ID3D11Device* textureDevice)
    {
        if (device.Get() == textureDevice && parameters != nullptr)
            return true;
        // Not again on the device it failed on -- except a failure that stood,
        // once what NGX searches has changed: the player copied nvngx_dlss.dll or
        // set dlssDirectory, and no release has to come first. The files are
        // asked at most once a second, not with every packet.
        const bool retrying = failedDevice == textureDevice;
        if (retrying)
        {
            if (!failedStable)
                return false;
            const ULONGLONG now = GetTickCount64();
            if (now < nextSearchCheck)
                return false;
            nextSearchCheck = now + 1000;
        }
        const std::wstring search = SearchSignature();
        if (retrying && search == failedSearch)
            return false;

        Shutdown();
        failedDevice = textureDevice;
        failedSearch = search;
        failedStable = false;

        if (core == nullptr)
        {
            std::string error;
            bool stable = false;
            core = NgxCoreModule(error, stable);
            if (core == nullptr)
            {
                SetStatus(stable ? -2 : -1, error);
                failedStable = stable;
                return false;
            }

            g_ngx.initProjectId = reinterpret_cast<InitProjectIdFn>(GetProcAddress(core, "NVSDK_NGX_D3D11_Init_ProjectID"));
            g_ngx.getCapabilityParameters = reinterpret_cast<NgxGetParametersFn>(
                GetProcAddress(core, "NVSDK_NGX_D3D11_GetCapabilityParameters"));
            g_ngx.getParameters = reinterpret_cast<NgxGetParametersFn>(GetProcAddress(core, "NVSDK_NGX_D3D11_GetParameters"));
            g_ngx.destroyParameters = reinterpret_cast<NgxDestroyParametersFn>(
                GetProcAddress(core, "NVSDK_NGX_D3D11_DestroyParameters"));
            g_ngx.createFeature = reinterpret_cast<CreateFeatureFn>(GetProcAddress(core, "NVSDK_NGX_D3D11_CreateFeature"));
            g_ngx.evaluateFeature = reinterpret_cast<EvaluateFeatureFn>(
                GetProcAddress(core, "NVSDK_NGX_D3D11_EvaluateFeature"));
            g_ngx.releaseFeature = reinterpret_cast<NgxReleaseFeatureFn>(
                GetProcAddress(core, "NVSDK_NGX_D3D11_ReleaseFeature"));
            g_ngx.shutdown = reinterpret_cast<ShutdownFn>(GetProcAddress(core, "NVSDK_NGX_D3D11_Shutdown"));
        }

        // On every start, not only when the core is loaded: a core without them
        // stays loaded.
        if (g_ngx.initProjectId == nullptr || g_ngx.getCapabilityParameters == nullptr
            || g_ngx.destroyParameters == nullptr || g_ngx.createFeature == nullptr
            || g_ngx.evaluateFeature == nullptr || g_ngx.releaseFeature == nullptr || g_ngx.shutdown == nullptr)
        {
            SetStatus(-2, "the driver's NGX core lacks a D3D11 function DLSS needs -- driver too old?");
            failedStable = true;
            return false;
        }

        // NGX looks next to the executable itself; dlssDirectory comes first.
        searchPaths.clear();
        const std::wstring directory = DlssDirectory();
        if (!directory.empty())
            searchPaths.push_back(directory);
        searchPaths.push_back(ExecutableDirectory());
        searchPathPointers.clear();
        for (std::wstring& path : searchPaths)
            searchPathPointers.push_back(&path[0]);

        NgxCommonInfo info = {};
        info.pathList.path = searchPathPointers.data();
        info.pathList.length = static_cast<unsigned int>(searchPathPointers.size());
        info.logging.callback = &NgxLog;
        info.logging.minimumLevel = kNgxLoggingOn;
        // Into the proxy's log only: otherwise NGX also writes nvngx.log next to
        // the game and to the console.
        info.logging.disableOtherSinks = true;

        // NGX's lines for this start, whatever earlier ones used up.
        ResetNgxLog();
        const std::wstring dataPath = ExecutableDirectory();
        uint32_t result = g_ngx.initProjectId(kNvidiaProjectId, kNgxEngineCustom, kEngineVersion, dataPath.c_str(),
                                              textureDevice, kNgxVersionApi, &info);
        if (result == kNgxFeatureNotSupported)
        {
            SetStatus(-2, "DLSS does not run on " + AdapterName(textureDevice)
                              + ", the GPU KSP renders on: NGX reports it as unsupported -- DLSS needs an NVIDIA RTX GPU");
            failedStable = true;
            return false;
        }
        if (NgxFailed(result))
        {
            SetStatus(-1, "NGX could not be initialised on " + AdapterName(textureDevice) + " (" + Hex(result) + ")");
            return false;
        }

        // The capability map, which the DLSS queries read and the feature takes;
        // an older driver has only the one NGX manages itself (guide 5.2.6).
        NgxParameters* map = nullptr;
        result = g_ngx.getCapabilityParameters(&map);
        parametersOwned = !NgxFailed(result) && map != nullptr;
        if (!parametersOwned && g_ngx.getParameters != nullptr)
            result = g_ngx.getParameters(&map);
        if (NgxFailed(result) || map == nullptr)
        {
            g_ngx.shutdown();
            SetStatus(-1, "NGX gave no parameter map (" + Hex(result) + ")");
            return false;
        }
        parameters = map;
        device = textureDevice;

        int needsUpdatedDriver = 0;
        unsigned int minimumMajor = 0;
        unsigned int minimumMinor = 0;
        if (!NgxFailed(map->Get("SuperSampling.NeedsUpdatedDriver", &needsUpdatedDriver)) && needsUpdatedDriver != 0)
        {
            map->Get("SuperSampling.MinDriverVersionMajor", &minimumMajor);
            map->Get("SuperSampling.MinDriverVersionMinor", &minimumMinor);
            SetStatus(-2, "DLSS needs a newer NVIDIA driver, at least " + std::to_string(minimumMajor) + "."
                              + std::to_string(minimumMinor));
            Shutdown();
            failedStable = true;
            return false;
        }

        int available = 0;
        if (NgxFailed(map->Get("SuperSampling.Available", &available)) || available == 0)
        {
            std::string where;
            for (const std::wstring& path : searchPaths)
                where += (where.empty() ? "" : " or ") + Narrow(path);
            SetStatus(-2, "DLSS is not available: no usable nvngx_dlss.dll in " + where
                              + ", or no RTX GPU -- NVIDIA DLSS files in ReDefinition's settings window downloads it");
            Shutdown();
            failedStable = true;
            return false;
        }

        int initResult = 0;
        if (!NgxFailed(map->Get("SuperSampling.FeatureInitResult", &initResult)) && NgxFailed(static_cast<uint32_t>(initResult)))
        {
            SetStatus(-2, "NGX denies DLSS for this application (" + Hex(static_cast<uint32_t>(initResult)) + ")");
            Shutdown();
            failedStable = true;
            return false;
        }

        failedDevice = nullptr;
        LogLine("DLSS: NGX initialised on Unity's D3D11 device");
        SetStatus(0, "NGX initialised, no DLSS feature yet");
        return true;
    }

    // Event 6: the render sizes of every mode at an output size, before the integration
    // that renders at one of them is built. NGX is started on the device of the
    // texture the query carries, as a frame would start it.
    void Dlss::OnSizeQuery(const void* data)
    {
        const DlssSizeQuery* query = static_cast<const DlssSizeQuery*>(data);
        if (query == nullptr || query->size != sizeof(DlssSizeQuery) || query->magic != kDlssSizeQueryMagic
            || query->texture == nullptr)
            return;

        ComPtr<ID3D11Device> textureDevice;
        static_cast<ID3D11Resource*>(query->texture)->GetDevice(&textureDevice);

        // Asked of NGX on DLSS's Direct3D 12 device, so NGX never starts on Unity's D3D11 device (DlssD3d12.h).
        if (DlssD3d12::On())
        {
            for (const int quality : { 0, 1, 2, 3, kNgxQualityDlaa })
            {
                RenderSizeEntry entry = {};
                if (RenderSizes(query->outputWidth, query->outputHeight, quality, entry.optimal, entry.minimum, entry.maximum))
                    continue;
                std::string error;
                if (DlssD3d12::Get().RenderSize(textureDevice.Get(), query->outputWidth, query->outputHeight, quality, entry.optimal,
                                                entry.minimum, entry.maximum, error))
                {
                    entry.outputWidth = query->outputWidth;
                    entry.outputHeight = query->outputHeight;
                    entry.quality = quality;
                    StoreRenderSize(entry);
                }
                else
                    LogLine("DLSS: no render size for quality " + std::to_string(quality) + " at "
                            + Size(query->outputWidth, query->outputHeight) + ": " + error);
            }
            char text[256] = {};
            const int reported = DlssD3d12::Get().Status(text, sizeof(text));
            if (reported < 0)
                MirrorStatus(reported, text);
            return;
        }

        std::lock_guard<std::mutex> ngx(NgxMutex());
        // A failure that need not stand is not kept for the integration's first frame to
        // meet: no release runs between a query and the integration after it.
        if (!EnsureInitialised(textureDevice.Get()))
        {
            if (!failedStable)
                failedDevice = nullptr;
            return;
        }

        for (const int quality : { 0, 1, 2, 3, kNgxQualityDlaa })
        {
            uint32_t optimal[2] = {};
            uint32_t minimum[2] = {};
            uint32_t maximum[2] = {};
            if (RenderSizes(query->outputWidth, query->outputHeight, quality, optimal, minimum, maximum))
                continue;

            RenderSizeEntry entry = {};
            std::string error;
            if (QueryRenderSize(query->outputWidth, query->outputHeight, quality, entry, error))
                StoreRenderSize(entry);
            else
                LogLine("DLSS: no render size for quality " + std::to_string(quality) + " at "
                        + Size(query->outputWidth, query->outputHeight) + ": " + error);
        }
    }

    // The render size DLSS wants for an output size and quality value, and the range it accepts
    // (guide 5.2.8). Output size and quality go in, and the optimal render size comes out. The range
    // comes from the dynamic keys, or is the optimal size where a library has none. DLAA renders at
    // the output size.
    bool Dlss::QueryRenderSize(uint32_t outputWidth, uint32_t outputHeight, int32_t quality, RenderSizeEntry& entry,
                               std::string& error)
    {
        NgxParameters* map = Map(parameters);
        unsigned int optimalWidth = outputWidth;
        unsigned int optimalHeight = outputHeight;
        unsigned int minWidth = optimalWidth, minHeight = optimalHeight;
        unsigned int maxWidth = optimalWidth, maxHeight = optimalHeight;
        if (quality != kNgxQualityDlaa)
        {
            void* callback = nullptr;
            map->Get("DLSSOptimalSettingsCallback", &callback);
            if (callback == nullptr)
            {
                error = "this DLSS library gives no optimal settings";
                return false;
            }
            map->Set("Width", outputWidth);
            map->Set("Height", outputHeight);
            map->Set("PerfQualityValue", static_cast<int>(quality));
            map->Set("RTXValue", 0);
            const uint32_t result = static_cast<NgxOptimalSettingsFn>(callback)(map);
            if (NgxFailed(result))
            {
                error = "DLSS optimal settings query failed (" + Hex(result) + ")";
                return false;
            }
            map->Get("OutWidth", &optimalWidth);
            map->Get("OutHeight", &optimalHeight);
            minWidth = maxWidth = optimalWidth;
            minHeight = maxHeight = optimalHeight;
            map->Get("DLSS.Get.Dynamic.Min.Render.Width", &minWidth);
            map->Get("DLSS.Get.Dynamic.Min.Render.Height", &minHeight);
            map->Get("DLSS.Get.Dynamic.Max.Render.Width", &maxWidth);
            map->Get("DLSS.Get.Dynamic.Max.Render.Height", &maxHeight);
        }

        if (optimalWidth == 0 || optimalHeight == 0)
        {
            error = "DLSS offers this quality mode not at " + Size(outputWidth, outputHeight);
            return false;
        }
        if (minWidth == 0 || minHeight == 0)
        {
            minWidth = optimalWidth;
            minHeight = optimalHeight;
        }
        if (maxWidth == 0 || maxHeight == 0)
        {
            maxWidth = optimalWidth;
            maxHeight = optimalHeight;
        }

        entry = RenderSizeEntry{ outputWidth, outputHeight, quality, { optimalWidth, optimalHeight },
                                 { minWidth, minHeight }, { maxWidth, maxHeight } };
        return true;
    }

    // Replaces the entry for the same output size and quality, or the oldest.
    void Dlss::StoreRenderSize(const RenderSizeEntry& entry)
    {
        std::lock_guard<std::mutex> lock(mutex);
        size_t at = nextSize;
        bool known = false;
        for (size_t i = 0; i < kRenderSizeEntries && !known; ++i)
        {
            if (sizes[i].optimal[0] != 0 && sizes[i].outputWidth == entry.outputWidth
                && sizes[i].outputHeight == entry.outputHeight && sizes[i].quality == entry.quality)
            {
                at = i;
                known = true;
            }
        }
        if (!known)
            nextSize = (nextSize + 1) % kRenderSizeEntries;
        sizes[at] = entry;
    }

    bool Dlss::EnsureFeature(ID3D11DeviceContext* context, const DlssPacket& packet)
    {
        if (feature != nullptr && SameFeature(created, packet))
            return true;
        if (hasFailed && SameFeature(failed, packet))
            return false;

        ReleaseFeature();
        NgxParameters* map = Map(parameters);

        RenderSizeEntry wanted = {};
        std::string error;
        if (!QueryRenderSize(packet.outputWidth, packet.outputHeight, packet.quality, wanted, error))
        {
            SetStatus(-1, error);
            failed = packet;
            hasFailed = true;
            return false;
        }
        StoreRenderSize(wanted);
        const unsigned int optimalWidth = wanted.optimal[0];
        const unsigned int optimalHeight = wanted.optimal[1];
        const unsigned int minWidth = wanted.minimum[0];
        const unsigned int minHeight = wanted.minimum[1];
        const unsigned int maxWidth = wanted.maximum[0];
        const unsigned int maxHeight = wanted.maximum[1];

        // Created at the render size DLSS asked for -- the create parameters'
        // InWidth and InHeight, "Obtained via NGX_DLSS_GET_OPTIMAL_SETTINGS" --
        // and evaluated at the size the integration renders, which has to lie in the range
        // the same query returned: otherwise "the call to DLSS Evaluate fails with
        // an NVSDK_NGX_Result_FAIL_InvalidParameter error code" (guide 3.2.2). The
        // integration reads the optimal size back and renders at it (KspDlssRenderSizes2);
        // with presets L and M, a frame larger than at creation "triggers an
        // expensive internal buffer reallocation" (3.2.2.1).
        if (packet.renderWidth < minWidth || packet.renderWidth > maxWidth || packet.renderHeight < minHeight
            || packet.renderHeight > maxHeight)
        {
            SetStatus(-1, "DLSS takes this mode at " + Size(minWidth, minHeight) + " to " + Size(maxWidth, maxHeight)
                              + ", not at " + Size(packet.renderWidth, packet.renderHeight) + "; it asks for "
                              + Size(optimalWidth, optimalHeight));
            failed = packet;
            hasFailed = true;
            return false;
        }

        // The preset for every mode: the feature is created for one of them.
        for (const char* mode : { "DLSS.Hint.Render.Preset.DLAA", "DLSS.Hint.Render.Preset.Quality",
                                  "DLSS.Hint.Render.Preset.Balanced", "DLSS.Hint.Render.Preset.Performance",
                                  "DLSS.Hint.Render.Preset.UltraPerformance", "DLSS.Hint.Render.Preset.UltraQuality" })
            map->Set(mode, static_cast<unsigned int>(packet.preset));

        // Motion vectors at render resolution ("MVLowRes") and without jitter:
        // the integration's are Unity's own, rendered unjittered.
        int flags = kNgxFlagMotionVectorsLowRes;
        if ((packet.flags & kDlssHdr) != 0)
            flags |= kNgxFlagIsHdr;
        if ((packet.flags & kDlssDepthInverted) != 0)
            flags |= kNgxFlagDepthInverted;
        if ((packet.flags & kDlssAutoExposure) != 0)
            flags |= kNgxFlagAutoExposure;

        map->Set("CreationNodeMask", 1u);
        map->Set("VisibilityNodeMask", 1u);
        map->Set("Width", optimalWidth);
        map->Set("Height", optimalHeight);
        map->Set("OutWidth", static_cast<unsigned int>(packet.outputWidth));
        map->Set("OutHeight", static_cast<unsigned int>(packet.outputHeight));
        map->Set("PerfQualityValue", static_cast<int>(packet.quality));
        map->Set("DLSS.Feature.Create.Flags", flags);
        map->Set("DLSS.Enable.Output.Subrects", 0);

        ResetNgxLog();
        void* handle = nullptr;
        const uint32_t result = g_ngx.createFeature(context, kNgxFeatureSuperSampling, map, &handle);
        if (NgxFailed(result) || handle == nullptr)
        {
            SetStatus(-1, "the DLSS feature could not be created (" + Hex(result) + ")");
            failed = packet;
            hasFailed = true;
            return false;
        }

        feature = handle;
        created = packet;
        hasFailed = false;
        loggedEvaluateFailure = false;
        evaluated = 0;
        // The status names the new feature with its first evaluated frame.
        renderState = 0;
        LogLine("DLSS: " + LoadedNgxModule(L"nvngx_dlss.dll") + ", created for " + Size(optimalWidth, optimalHeight) + " (range "
                + Size(minWidth, minHeight) + " to " + Size(maxWidth, maxHeight) + ") to "
                + Size(packet.outputWidth, packet.outputHeight) + ", rendered at "
                + Size(packet.renderWidth, packet.renderHeight) + ", quality " + std::to_string(packet.quality)
                + ", preset " + std::to_string(packet.preset) + ", flags " + Hex(static_cast<uint32_t>(flags)));
        return true;
    }

    bool Dlss::Evaluate(ID3D11DeviceContext* context, const DlssPacket& packet)
    {
        NgxParameters* map = Map(parameters);
        map->Set("Color", static_cast<ID3D11Resource*>(packet.colour));
        map->Set("Output", static_cast<ID3D11Resource*>(packet.output));
        map->Set("Depth", static_cast<ID3D11Resource*>(packet.depth));
        map->Set("MotionVectors", static_cast<ID3D11Resource*>(packet.motionVectors));
        map->Set("Jitter.Offset.X", packet.jitterX);
        map->Set("Jitter.Offset.Y", packet.jitterY);
        map->Set("MV.Scale.X", packet.motionVectorScaleX);
        map->Set("MV.Scale.Y", packet.motionVectorScaleY);
        map->Set("Reset", (packet.flags & kDlssReset) != 0 ? 1 : 0);
        map->Set("DLSS.Render.Subrect.Dimensions.Width", static_cast<unsigned int>(packet.renderWidth));
        map->Set("DLSS.Render.Subrect.Dimensions.Height", static_cast<unsigned int>(packet.renderHeight));
        map->Set("DLSS.Pre.Exposure", 1.0f);
        map->Set("DLSS.Exposure.Scale", 1.0f);
        // Deprecated (guide 3.11); zero so no older library sharpens.
        map->Set("Sharpness", 0.0f);

        const uint32_t result = g_ngx.evaluateFeature(context, feature, map, nullptr);
        if (NgxFailed(result))
        {
            if (!loggedEvaluateFailure)
                LogLine("DLSS: evaluation failed (" + Hex(result) + ")");
            loggedEvaluateFailure = true;
            SetStatus(-1, "DLSS evaluation failed (" + Hex(result) + ")");
            return false;
        }

        ++evaluated;
        // Straight back to running after a failure, and every 1024 frames the count.
        if (renderState != 1 || (evaluated & 1023) == 0)
            SetStatus(1, "DLSS running, " + std::to_string(created.renderWidth) + "x"
                             + std::to_string(created.renderHeight) + " to " + std::to_string(created.outputWidth)
                             + "x" + std::to_string(created.outputHeight) + ", " + std::to_string(evaluated)
                             + " frames");
        return true;
    }

    void Dlss::ReleaseFeature()
    {
        if (feature == nullptr)
            return;
        g_ngx.releaseFeature(feature);
        feature = nullptr;
        created = DlssPacket{};
        LogLine("DLSS: feature released");
    }

    void Dlss::Shutdown()
    {
        ReleaseFeature();
        if (parameters != nullptr && parametersOwned)
            g_ngx.destroyParameters(Map(parameters));
        parameters = nullptr;
        parametersOwned = false;
        if (device != nullptr)
            g_ngx.shutdown();
        device.Reset();
    }
}
