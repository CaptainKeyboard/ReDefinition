// A test driver for the swapchain proxy that does not need KSP.
//
// It does what Unity 2019.4 does: load
// dxgi.dll by bare name, create a D3D11 device, and ask
// IDXGIFactory2::CreateSwapChainForHwnd for a swapchain with the description the
// proxy logged during a real KSP run -- FLIP_DISCARD, two buffers,
// R8G8B8A8_UNORM, one sample, and flags 0x842: ALLOW_TEARING, ALLOW_MODE_SWITCH
// and FRAME_LATENCY_WAITABLE_OBJECT. It then asks for IDXGISwapChain2 and the
// waitable object, waits on it, renders into whatever GetBuffer hands back and
// presents a number of frames.
//
// Those flags matter: without the waitable object a harness passes where the game
// stops.
//
// It also runs the HUD-less copy end to end. Every frame draws a "scene"
// whose colour changes from frame to frame, copies it into a HUD-less texture
// the way the rig blits it after the last scene camera, and then draws a "UI"
// rectangle over it. The proxy's check must find exactly the rectangle in the
// direct comparison and, because the scene carries a feature, more than that in
// the mirrored one -- not nothing, which would mean the copy was taken after the
// UI, and not nearly everything, which would mean a stale one. The proxy's
// default is not to flip, as measured in flight; a flip here would show as the
// two comparisons swapped.
//
// And, when the FidelityFX runtime sits next to it, frame generation itself:
// depth and motion vectors registered and captured every frame the way the rig
// does it, interpolation on, off, on again, and on across a resize. The
// swapchain's own present count says whether frames were generated -- about two
// presents per rendered frame while it is on, one while it is off.
//
// Because it sits in the same directory as the proxy's dxgi.dll, the ordinary
// search order loads the proxy rather than the system one, as in KSP.
//
// A failing proxy shows here as an HRESULT and a log line, without a game
// launch.

#include "FileUtil.h"

#include <windows.h>

#include <d3d11.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <algorithm>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace
{
    constexpr UINT kWidth = 640;
    constexpr UINT kHeight = 360;
    constexpr UINT kFrames = 30;

    // The size the frame generation test resizes to, as a resolution change or
    // a switch to fullscreen would in the game.
    constexpr UINT kResizedWidth = 800;
    constexpr UINT kResizedHeight = 450;

    // The "UI": a white rectangle, drawn after the HUD-less copy.
    constexpr UINT kUiX = 40;
    constexpr UINT kUiY = 40;
    constexpr UINT kUiWidth = 160;
    constexpr UINT kUiHeight = 90;

    // A "scene feature": a black rectangle drawn before the copy, off to the
    // side where neither it nor its mirror image overlaps the UI. A uniform
    // scene cannot show whether the copy was flipped; this can.
    constexpr UINT kFeatureX = 400;
    constexpr UINT kFeatureY = 20;
    constexpr UINT kFeatureWidth = 120;
    constexpr UINT kFeatureHeight = 60;

    // REDEFINITION_HARNESS_DETAIL: a still scene of fine grain in place of the
    // changing colour, to compare the sharpness of generated and rendered frames
    // in the screen recording. REDEFINITION_HARNESS_JITTER: the packet carries a
    // jitter sequence, as the rig sends it.
    constexpr UINT kDetailX = 120;
    constexpr UINT kDetailY = 100;
    constexpr UINT kDetailWidth = 400;
    constexpr UINT kDetailHeight = 240;

    bool EnvironmentSet(const wchar_t* name)
    {
        wchar_t value[8] = {};
        return GetEnvironmentVariableW(name, value, 8) > 0 && value[0] != L'0';
    }

    float Halton(UINT index, UINT base)
    {
        float result = 0.0f;
        float fraction = 1.0f;
        for (UINT i = index + 1; i > 0; i /= base)
        {
            fraction /= static_cast<float>(base);
            result += fraction * static_cast<float>(i % base);
        }
        return result;
    }

    // Generated frames show as extra presents. Thresholds well clear of both
    // one and two, so a present or two still in flight at the end of a phase
    // cannot flip the verdict.
    constexpr float kGeneratingAtLeast = 1.5f;
    constexpr float kNotGeneratingAtMost = 1.2f;

    // The frame generation exports, reached the way the managed side reaches
    // them, and the event ids OnRenderEvent reads.
    using GetRenderEventFn = void* (*)();
    using RenderEventFn = void (*)(int, void*);
    using SetEnabledFn = void (*)(int);
    using LastCheckFn = int (*)(float*, float*);
    using RegisterInputsFn = void (*)(void*, void*, void*);
    using PacketSizeFn = unsigned int (*)();
    using StatusFn = int (*)(char*, int);
    using CountersFn = int (*)(unsigned int*, unsigned int*);
    using RegisterMainThreadFn = void (*)();
    using LoadFn = int (*)(float*, float*, float*, float*);
    constexpr int kPacketEvent = 1;

    // The per-frame packet, laid out as FrameGeneration.h declares it.
    struct FramePacket
    {
        uint32_t size, magic, frameIndex, renderWidth, renderHeight, reset;
        float jitterX, jitterY, motionVectorScaleX, motionVectorScaleY;
        float nearPlane, farPlane, verticalFovRadians, frameTimeDeltaMs;
        float position[3], up[3], right[3], forward[3];
        float viewToClip[16], clipToView[16], clipToPrevClip[16], prevClipToClip[16];
    };
    constexpr uint32_t kPacketMagic = 0x4B535046u;

    // DLSS, laid out as Dlss.h declares it.
    using DlssStatusFn = int (*)(char*, int);
    using DlssPacketSizeFn = unsigned int (*)();
    using DlssSizesFn = int (*)(unsigned int, unsigned int, int, unsigned int*, unsigned int*, unsigned int*);
    constexpr int kDlssEvent = 2;
    constexpr int kDlssReleaseEvent = 3;
    struct DlssPacket
    {
        uint32_t size, magic;
        void* colour;
        void* output;
        void* depth;
        void* motionVectors;
        uint32_t renderWidth, renderHeight, outputWidth, outputHeight;
        int32_t quality;
        uint32_t preset, flags;
        float jitterX, jitterY, motionVectorScaleX, motionVectorScaleY;
    };
    constexpr uint32_t kDlssMagic = 0x4B535044u;
    constexpr uint32_t kDlssHdr = 1u << 0;
    constexpr uint32_t kDlssDepthInverted = 1u << 1;
    constexpr uint32_t kDlssAutoExposure = 1u << 2;
    constexpr uint32_t kDlssReset = 1u << 3;
    constexpr int kDlssQueryEvent = 6;
    struct DlssSizeQuery
    {
        uint32_t size, magic;
        void* texture;
        uint32_t outputWidth, outputHeight;
    };
    constexpr uint32_t kDlssQueryMagic = 0x4B535051u;

    // AMD's upscaler, laid out as AmdUpscaler.h declares it; the flags as DLSS's.
    constexpr int kAmdEvent = 4;
    constexpr int kAmdReleaseEvent = 5;
    struct AmdPacket
    {
        uint32_t size, magic;
        void* colour;
        void* output;
        void* depth;
        void* motionVectors;
        uint32_t renderWidth, renderHeight, outputWidth, outputHeight, flags;
        float jitterX, jitterY, motionVectorScaleX, motionVectorScaleY;
        float sharpness, frameTimeDeltaMs, cameraNear, cameraFar, verticalFovRadians;
    };
    constexpr uint32_t kAmdMagic = 0x4B535041u;

    // Direct3D 12 for mods, laid out as D3d12Compute.h declares it.
    constexpr int kD3d12CreateEvent = 20;
    constexpr int kD3d12DestroyEvent = 21;
    constexpr int kD3d12DispatchEvent = 22;
    constexpr int kD3d12ReleaseEvent = 23;
    struct D3d12CreatePacket
    {
        uint32_t size, magic;
        int32_t pass;
        uint32_t bytecodeSize;
        const void* bytecode;
        char name[64];
    };
    struct D3d12DispatchPacket
    {
        uint32_t size, magic;
        int32_t pass;
        uint32_t flags, groupsX, groupsY, groupsZ, readCount, writeCount, constantsSize;
        void* read[8];
        void* write[8];
        uint8_t constants[256];
    };
    struct D3d12HandlePacket
    {
        uint32_t size, magic;
        int32_t pass;
        uint32_t reserved;
        void* texture;
    };
    constexpr uint32_t kD3d12CreateMagic = 0x4B534443u;
    constexpr uint32_t kD3d12DispatchMagic = 0x4B534444u;
    constexpr uint32_t kD3d12HandleMagic = 0x4B534448u;
    constexpr uint32_t kD3d12NextFrame = 1u << 0;

    int Fail(const char* what, HRESULT hr)
    {
        printf("FAIL  %s (0x%08lX)\n", what, static_cast<unsigned long>(hr));
        return 1;
    }

    LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM w, LPARAM l)
    {
        if (message == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(hwnd, message, w, l);
    }

    HWND CreateHostWindow()
    {
        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = WindowProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"ReDefinitionProxyHarness";
        RegisterClassExW(&wc);

        return CreateWindowExW(0, wc.lpszClassName, L"ReDefinition proxy harness",
                               WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT,
                               kWidth, kHeight, nullptr, nullptr, wc.hInstance, nullptr);
    }

    void PumpMessages()
    {
        MSG message = {};
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }

    HRESULT CreateFilledTexture(ID3D11Device* device, UINT width, UINT height, DXGI_FORMAT format,
                                uint32_t fill, ComPtr<ID3D11Texture2D>& out)
    {
        const std::vector<uint32_t> texels(static_cast<size_t>(width) * height, fill);

        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = width;
        desc.Height = height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

        D3D11_SUBRESOURCE_DATA initial = {};
        initial.pSysMem = texels.data();
        initial.SysMemPitch = width * 4;

        return device->CreateTexture2D(&desc, &initial, &out);
    }

    // RGBA half float, every texel the same.
    HRESULT CreateHalfTexture(ID3D11Device* device, UINT width, UINT height, const uint16_t texel[4],
                              UINT bindFlags, ComPtr<ID3D11Texture2D>& out)
    {
        std::vector<uint16_t> texels(static_cast<size_t>(width) * height * 4);
        for (size_t i = 0; i < texels.size(); ++i)
            texels[i] = texel[i % 4];

        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = width;
        desc.Height = height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = bindFlags;

        D3D11_SUBRESOURCE_DATA initial = {};
        initial.pSysMem = texels.data();
        initial.SysMemPitch = width * 8;

        return device->CreateTexture2D(&desc, &initial, &out);
    }

    float HalfToFloat(uint16_t half)
    {
        const int sign = (half >> 15) != 0 ? -1 : 1;
        const int exponent = (half >> 10) & 0x1F;
        const int mantissa = half & 0x3FF;
        if (exponent == 0)
            return static_cast<float>(sign) * std::ldexp(static_cast<float>(mantissa), -24);
        if (exponent == 31)
            return static_cast<float>(sign) * 65504.0f;
        return static_cast<float>(sign) * std::ldexp(static_cast<float>(mantissa + 1024), exponent - 25);
    }

    // What the rig hands over: R32_FLOAT depth and R16G16_FLOAT motion vectors,
    // the formats measured in the game. A flat depth and no motion are enough
    // to drive the whole path; the look of the generated frames is not tested
    // here.
    struct Inputs
    {
        ComPtr<ID3D11Texture2D> depth;
        ComPtr<ID3D11Texture2D> motion;
    };

    HRESULT CreateInputs(ID3D11Device* device, UINT width, UINT height, Inputs& out)
    {
        float half = 0.5f;
        uint32_t depthBits = 0;
        memcpy(&depthBits, &half, sizeof(depthBits));

        const HRESULT hr = CreateFilledTexture(device, width, height, DXGI_FORMAT_R32_FLOAT,
                                               depthBits, out.depth);
        if (FAILED(hr))
            return hr;

        return CreateFilledTexture(device, width, height, DXGI_FORMAT_R16G16_FLOAT, 0u, out.motion);
    }

    std::wstring PathNextToExecutable(const wchar_t* name)
    {
        return redefinition::ExecutableDirectory() + L"\\" + name;
    }

    bool FileNextToExecutable(const wchar_t* name)
    {
        return GetFileAttributesW(PathNextToExecutable(name).c_str()) != INVALID_FILE_ATTRIBUTES;
    }

    // The first NVIDIA adapter, or none: with integrated graphics beside the RTX
    // card, the default adapter can be the integrated one.
    ComPtr<IDXGIAdapter1> NvidiaAdapter(IDXGIFactory2* factory)
    {
        ComPtr<IDXGIAdapter1> adapter;
        for (UINT i = 0; factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i, adapter.Reset())
        {
            DXGI_ADAPTER_DESC1 desc = {};
            if (SUCCEEDED(adapter->GetDesc1(&desc)) && desc.VendorId == 0x10DE)
                return adapter;
        }
        return nullptr;
    }

    // A key in the ini next to the harness, the one the proxy reads. The
    // harness owns the keys it sets: set before the proxy loads, and gone
    // again -- the default -- on every return from main. A run that crashes
    // leaves its value behind, and the next run's first act sets it anew.
    class IniKey
    {
    public:
        explicit IniKey(const wchar_t* name) : key(name), path(PathNextToExecutable(L"ReDefinitionProxy.ini")) {}
        ~IniKey() { Set(nullptr); }

        IniKey(const IniKey&) = delete;
        IniKey& operator=(const IniKey&) = delete;

        void Set(const wchar_t* value) const
        {
            WritePrivateProfileStringW(L"Proxy", key, value, path.c_str());
        }

    private:
        const wchar_t* key;
        std::wstring path;
    };
}

namespace
{
    // REDEFINITION_HARNESS_REPLAY=<folder>: an input dump from the game
    // (FrameGenerationDump.cpp) played through frame generation again and again, at
    // half its size, while the screen is recorded -- frame generation's output for the
    // game's own frames, without the game. REDEFINITION_REPLAY_SHADOW_MOTION=1 gives
    // the pixels of the vessel's shadow the vessel's motion (the mask is taken from
    // the colour: ground darker than the lit runway near the vessel).
    struct ReplayFrame
    {
        FramePacket packet = {};
        std::vector<uint32_t> colour;   // RGBA8, half size, rows top-down
        std::vector<float> depth;       // half size, rows bottom-up as Unity renders
        std::vector<uint32_t> motion;   // R16G16 half floats, half size, rows bottom-up
    };

    bool LoadReplay(const std::wstring& folder, UINT& width, UINT& height, std::vector<ReplayFrame>& frames)
    {
        FILE* file = nullptr;
        if (_wfopen_s(&file, (folder + L"\\frames.txt").c_str(), L"rb") != 0 || file == nullptr)
            return false;
        char line[2048];
        UINT fullWidth = 0, fullHeight = 0;
        while (fgets(line, sizeof(line), file))
        {
            ReplayFrame* f = frames.empty() ? nullptr : &frames.back();
            float* matrix = nullptr;
            if (strncmp(line, "frame ", 6) == 0)
            {
                frames.emplace_back();
                FramePacket& p = frames.back().packet;
                unsigned int index = 0, packetIndex = 0, reset = 0;
                int flipped = 0;
                sscanf_s(line, "frame %u packetIndex %u render %ux%u reset %u jitter %f %f mvScale %f %f near %f far %f fov %f dtMs %f flipped %d",
                         &index, &packetIndex, &fullWidth, &fullHeight, &reset, &p.jitterX, &p.jitterY,
                         &p.motionVectorScaleX, &p.motionVectorScaleY, &p.nearPlane, &p.farPlane,
                         &p.verticalFovRadians, &p.frameTimeDeltaMs, &flipped);
            }
            else if (f != nullptr && strncmp(line, "position ", 9) == 0)
            {
                FramePacket& p = f->packet;
                sscanf_s(line, "position %f %f %f up %f %f %f right %f %f %f forward %f %f %f", &p.position[0],
                         &p.position[1], &p.position[2], &p.up[0], &p.up[1], &p.up[2], &p.right[0], &p.right[1],
                         &p.right[2], &p.forward[0], &p.forward[1], &p.forward[2]);
            }
            else if (f != nullptr && strncmp(line, "viewToClip ", 11) == 0) matrix = f->packet.viewToClip;
            else if (f != nullptr && strncmp(line, "clipToView ", 11) == 0) matrix = f->packet.clipToView;
            else if (f != nullptr && strncmp(line, "clipToPrevClip ", 15) == 0) matrix = f->packet.clipToPrevClip;
            else if (f != nullptr && strncmp(line, "prevClipToClip ", 15) == 0) matrix = f->packet.prevClipToClip;
            if (matrix != nullptr)
            {
                const char* cursor = strchr(line, ' ');
                for (int i = 0; i < 16 && cursor != nullptr; ++i)
                {
                    matrix[i] = strtof(cursor, const_cast<char**>(&cursor));
                }
            }
        }
        fclose(file);
        if (frames.empty() || fullWidth < 64 || fullHeight < 64)
            return false;

        width = fullWidth / 2;
        height = fullHeight / 2;
        const size_t full = static_cast<size_t>(fullWidth) * fullHeight;
        std::vector<uint32_t> colour(full), motion(full);
        std::vector<float> depth(full);
        for (size_t n = 0; n < frames.size(); ++n)
        {
            auto read = [&](const wchar_t* name, void* target) -> bool
            {
                wchar_t path[64];
                swprintf_s(path, L"\\%s-%02zu.bin", name, n);
                FILE* raw = nullptr;
                if (_wfopen_s(&raw, (folder + path).c_str(), L"rb") != 0 || raw == nullptr)
                    return false;
                const size_t got = fread(target, 4, full, raw);
                fclose(raw);
                return got == full;
            };
            if (!read(L"colour", colour.data()) || !read(L"depth", depth.data()) || !read(L"motion", motion.data()))
                return false;

            ReplayFrame& f = frames[n];
            f.colour.resize(static_cast<size_t>(width) * height);
            f.depth.resize(f.colour.size());
            f.motion.resize(f.colour.size());
            for (UINT y = 0; y < height; ++y)
            {
                for (UINT x = 0; x < width; ++x)
                {
                    // Colour: the 2x2 mean. Depth and motion: one texel, the nearest
                    // surface of the four, so an edge keeps one surface's values.
                    uint32_t sum[4] = {};
                    size_t nearest = 0;
                    float nearestDepth = -1.0f;
                    for (UINT dy = 0; dy < 2; ++dy)
                        for (UINT dx = 0; dx < 2; ++dx)
                        {
                            const size_t i = static_cast<size_t>(y * 2 + dy) * fullWidth + x * 2 + dx;
                            for (int c = 0; c < 4; ++c)
                                sum[c] += (colour[i] >> (8 * c)) & 0xFFu;
                            if (depth[i] > nearestDepth) { nearestDepth = depth[i]; nearest = i; }
                        }
                    f.colour[static_cast<size_t>(y) * width + x] = (sum[0] / 4) | ((sum[1] / 4) << 8)
                                                                    | ((sum[2] / 4) << 16) | 0xFF000000u;
                    // The dump's depth and motion rows are top-down (flipped for frame
                    // generation); the proxy flips again, so they go in as Unity has them.
                    const size_t target = static_cast<size_t>(height - 1 - y) * width + x;
                    f.depth[target] = depth[nearest];
                    f.motion[target] = motion[nearest];
                }
            }
            FramePacket& p = f.packet;
            p.size = sizeof(FramePacket);
            p.magic = kPacketMagic;
            p.renderWidth = width;
            p.renderHeight = height;
            p.motionVectorScaleX = -static_cast<float>(width);
            p.motionVectorScaleY = -static_cast<float>(height);
            p.jitterX *= 0.5f;
            p.jitterY *= 0.5f;
        }
        return true;
    }

    // The vessel's shadow on the ground, from the colour: darker than the lit
    // runway, not the vessel itself (motion near zero is the vessel), within the
    // lower part of the picture. The vessel's motion there: the median motion of
    // the vessel's own pixels.
    void GiveShadowVesselMotion(ReplayFrame& f, UINT width, UINT height)
    {
        auto half = [](uint16_t h) { return HalfToFloat(h); };
        std::vector<float> luminance(f.colour.size());
        for (size_t i = 0; i < f.colour.size(); ++i)
        {
            const uint32_t c = f.colour[i];
            luminance[i] = 0.299f * (c & 0xFF) + 0.587f * ((c >> 8) & 0xFF) + 0.114f * ((c >> 16) & 0xFF);
        }
        // Box blur, radius 6, so the runway's grain does not decide.
        std::vector<float> blurred(luminance.size()), row(luminance.size());
        const int r = 6;
        for (UINT y = 0; y < height; ++y)
            for (UINT x = 0; x < width; ++x)
            {
                float s = 0; int n = 0;
                for (int d = -r; d <= r; ++d)
                {
                    const int xx = static_cast<int>(x) + d;
                    if (xx >= 0 && xx < static_cast<int>(width)) { s += luminance[static_cast<size_t>(y) * width + xx]; ++n; }
                }
                row[static_cast<size_t>(y) * width + x] = s / n;
            }
        for (UINT y = 0; y < height; ++y)
            for (UINT x = 0; x < width; ++x)
            {
                float s = 0; int n = 0;
                for (int d = -r; d <= r; ++d)
                {
                    const int yy = static_cast<int>(y) + d;
                    if (yy >= 0 && yy < static_cast<int>(height)) { s += row[static_cast<size_t>(yy) * width + x]; ++n; }
                }
                blurred[static_cast<size_t>(y) * width + x] = s / n;
            }

        // Motion rows are bottom-up; colour rows top-down.
        auto motionAt = [&](UINT x, UINT yTop) -> uint32_t& { return f.motion[static_cast<size_t>(height - 1 - yTop) * width + x]; };
        std::vector<float> lit;
        std::vector<uint32_t> vesselMotion;
        for (UINT y = height / 3; y < height; y += 2)
            for (UINT x = 0; x < width; x += 2)
            {
                const uint32_t m = motionAt(x, y);
                const float mx = half(static_cast<uint16_t>(m & 0xFFFF)) * width;
                const float my = half(static_cast<uint16_t>(m >> 16)) * height;
                if (std::fabs(mx) + std::fabs(my) < 2.0f) vesselMotion.push_back(m);
                else lit.push_back(blurred[static_cast<size_t>(y) * width + x]);
            }
        if (lit.empty() || vesselMotion.empty())
            return;
        std::sort(lit.begin(), lit.end());
        const float litLevel = lit[lit.size() * 9 / 10];
        const float darkLevel = lit[lit.size() / 50];
        const float threshold = darkLevel + 0.6f * (litLevel - darkLevel);
        const uint32_t vessel = vesselMotion[vesselMotion.size() / 2];
        size_t changed = 0;
        for (UINT y = height / 3; y < height; ++y)
            for (UINT x = 0; x < width; ++x)
            {
                uint32_t& m = motionAt(x, y);
                const float mx = half(static_cast<uint16_t>(m & 0xFFFF)) * width;
                const float my = half(static_cast<uint16_t>(m >> 16)) * height;
                if (std::fabs(mx) + std::fabs(my) < 2.0f)
                    continue;
                if (blurred[static_cast<size_t>(y) * width + x] < threshold)
                {
                    m = vessel;
                    ++changed;
                }
            }
        printf("      shadow motion: %zu pixels given the vessel's motion (threshold %.0f, lit %.0f)\n", changed,
               threshold, litLevel);
    }

    int RunReplay(const std::wstring& folder)
    {
        UINT width = 0, height = 0;
        std::vector<ReplayFrame> frames;
        if (!LoadReplay(folder, width, height, frames))
            return Fail("reading the input dump", E_FAIL);
        printf("      replay: %zu frames at %ux%u\n", frames.size(), width, height);
        if (EnvironmentSet(L"REDEFINITION_REPLAY_SHADOW_MOTION"))
            for (ReplayFrame& f : frames)
                GiveShadowVesselMotion(f, width, height);
        // Variations, one cause at a time: no jitter; no motion vectors.
        if (EnvironmentSet(L"REDEFINITION_REPLAY_NO_JITTER"))
            for (ReplayFrame& f : frames)
                f.packet.jitterX = f.packet.jitterY = 0.0f;
        if (EnvironmentSet(L"REDEFINITION_REPLAY_NO_MOTION"))
            for (ReplayFrame& f : frames)
                std::fill(f.motion.begin(), f.motion.end(), 0u);
        // The camera said to stand still: clip to previous clip is the identity.
        if (EnvironmentSet(L"REDEFINITION_REPLAY_STILL_CAMERA"))
            for (ReplayFrame& f : frames)
                for (int i = 0; i < 16; ++i)
                    f.packet.clipToPrevClip[i] = f.packet.prevClipToClip[i] = (i % 5 == 0) ? 1.0f : 0.0f;
        // One depth everywhere, where anything is drawn.
        if (EnvironmentSet(L"REDEFINITION_REPLAY_FLAT_DEPTH"))
            for (ReplayFrame& f : frames)
                for (float& d : f.depth)
                    if (d > 0.0f) d = 0.01f;

        wchar_t streamlineBuffer[MAX_PATH] = {};
        GetEnvironmentVariableW(L"REDEFINITION_STREAMLINE_DIR", streamlineBuffer, MAX_PATH);
        const IniKey streamlineKey(L"streamlineDirectory");
        streamlineKey.Set(streamlineBuffer[0] != 0 ? streamlineBuffer : nullptr);
        const IniKey fgVSync(L"fgVSync");
        fgVSync.Set(L"0");

        const HMODULE dxgi = LoadLibraryW(L"dxgi.dll");
        if (dxgi == nullptr)
            return Fail("LoadLibrary(dxgi.dll)", HRESULT_FROM_WIN32(GetLastError()));
        using CreateFactory2Fn = HRESULT(WINAPI*)(UINT, REFIID, void**);
        const auto createFactory2 = reinterpret_cast<CreateFactory2Fn>(GetProcAddress(dxgi, "CreateDXGIFactory2"));
        ComPtr<IDXGIFactory2> factory;
        HRESULT hr = createFactory2(0, IID_PPV_ARGS(&factory));
        if (FAILED(hr))
            return Fail("CreateDXGIFactory2", hr);
        ComPtr<IDXGIAdapter1> adapter = NvidiaAdapter(factory.Get());
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        hr = D3D11CreateDevice(adapter.Get(), adapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE, nullptr, 0,
                               nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context);
        if (FAILED(hr))
            return Fail("D3D11CreateDevice", hr);

        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = WindowProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = L"ReDefinitionProxyReplay";
        RegisterClassExW(&wc);
        RECT rect = { 0, 0, static_cast<LONG>(width), static_cast<LONG>(height) };
        AdjustWindowRect(&rect, WS_OVERLAPPEDWINDOW, FALSE);
        const HWND window = CreateWindowExW(0, wc.lpszClassName, L"ReDefinition replay", WS_OVERLAPPEDWINDOW, 0, 0,
                                            rect.right - rect.left, rect.bottom - rect.top, nullptr, nullptr,
                                            wc.hInstance, nullptr);
        ShowWindow(window, SW_SHOW);
        SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        {
            const HWND foreground = GetForegroundWindow();
            const DWORD foregroundThread = foreground != nullptr ? GetWindowThreadProcessId(foreground, nullptr) : 0;
            const DWORD ownThread = GetCurrentThreadId();
            const bool joined = foregroundThread != 0 && foregroundThread != ownThread
                                && AttachThreadInput(ownThread, foregroundThread, TRUE);
            SetForegroundWindow(window);
            BringWindowToTop(window);
            SetFocus(window);
            if (joined)
                AttachThreadInput(ownThread, foregroundThread, FALSE);
            PumpMessages();
        }

        DXGI_SWAP_CHAIN_DESC1 desc = {};
        desc.Width = width;
        desc.Height = height;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT | DXGI_USAGE_SHADER_INPUT;
        desc.BufferCount = 2;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING | DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH
                   | DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        ComPtr<IDXGISwapChain1> swapChain;
        hr = factory->CreateSwapChainForHwnd(device.Get(), window, &desc, nullptr, nullptr, &swapChain);
        if (FAILED(hr))
            return Fail("CreateSwapChainForHwnd", hr);
        ComPtr<IDXGISwapChain2> swapChain2;
        swapChain.As(&swapChain2);
        const HANDLE waitable = swapChain2 ? swapChain2->GetFrameLatencyWaitableObject() : nullptr;
        ComPtr<ID3D11Texture2D> backBuffer;
        hr = swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
        if (FAILED(hr))
            return Fail("GetBuffer(0)", hr);

        const auto getRenderEvent = reinterpret_cast<GetRenderEventFn>(GetProcAddress(dxgi, "KspFgGetRenderEventFunc"));
        const auto setEnabled = reinterpret_cast<SetEnabledFn>(GetProcAddress(dxgi, "KspFgSetEnabled"));
        const auto registerInputs = reinterpret_cast<RegisterInputsFn>(GetProcAddress(dxgi, "KspFgRegisterInputs3"));
        const auto status = reinterpret_cast<StatusFn>(GetProcAddress(dxgi, "KspFgStatus"));
        using RecordFn = int(__cdecl*)(int);
        using RecordStateFn = int(__cdecl*)(char*, int);
        const auto record = reinterpret_cast<RecordFn>(GetProcAddress(dxgi, "KspRecordScreen"));
        const auto recordState = reinterpret_cast<RecordStateFn>(GetProcAddress(dxgi, "KspRecordScreenState"));
        if (!getRenderEvent || !setEnabled || !registerInputs || !status || !record || !recordState)
            return Fail("proxy exports missing", E_FAIL);
        const auto renderEvent = reinterpret_cast<RenderEventFn>(getRenderEvent());
        setEnabled(1);

        auto texture = [&](DXGI_FORMAT format, ComPtr<ID3D11Texture2D>& out) -> HRESULT
        {
            D3D11_TEXTURE2D_DESC t = {};
            t.Width = width;
            t.Height = height;
            t.MipLevels = 1;
            t.ArraySize = 1;
            t.Format = format;
            t.SampleDesc.Count = 1;
            t.Usage = D3D11_USAGE_DEFAULT;
            t.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            return device->CreateTexture2D(&t, nullptr, &out);
        };
        ComPtr<ID3D11Texture2D> depth, motion, hudLess;
        if (FAILED(texture(DXGI_FORMAT_R32_FLOAT, depth)) || FAILED(texture(DXGI_FORMAT_R16G16_FLOAT, motion))
            || FAILED(texture(DXGI_FORMAT_R8G8B8A8_UNORM, hudLess)))
            return Fail("creating the replay inputs", E_FAIL);
        registerInputs(depth.Get(), motion.Get(), hudLess.Get());

        // Each dumped frame once per round; the first of a round is a reset, the jump
        // back from the last being no motion.
        const int rounds = 20;
        uint32_t frameIndex = 0;
        bool recording = false;
        char state[512] = {};
        for (int round = 0; round < rounds || (recording && strncmp(state, "running", 7) == 0); ++round)
        {
            if (recording)
                recordState(state, sizeof(state));
            for (size_t n = 0; n < frames.size(); ++n)
            {
                PumpMessages();
                if (waitable != nullptr)
                    WaitForSingleObject(waitable, 1000);
                ReplayFrame& f = frames[n];
                context->UpdateSubresource(backBuffer.Get(), 0, nullptr, f.colour.data(), width * 4, 0);
                context->UpdateSubresource(hudLess.Get(), 0, nullptr, f.colour.data(), width * 4, 0);
                context->UpdateSubresource(depth.Get(), 0, nullptr, f.depth.data(), width * 4, 0);
                context->UpdateSubresource(motion.Get(), 0, nullptr, f.motion.data(), width * 4, 0);
                FramePacket packet = f.packet;
                packet.frameIndex = ++frameIndex;
                packet.reset = n == 0 ? 1u : 0u;
                renderEvent(kPacketEvent, &packet);
                hr = swapChain->Present(0, DXGI_PRESENT_ALLOW_TEARING);
                if (FAILED(hr))
                    return Fail("Present during the replay", hr);
                // Presented at about the game's pace, so pacing is not what differs.
                Sleep(15);
            }
            // Only the replay's own window may be recorded: the recorder takes the
            // foreground window, and anything else there is not the harness's.
            if (round == rounds / 2 && !recording)
            {
                if (GetForegroundWindow() != window)
                {
                    printf("      the replay window is not in front: nothing recorded\n");
                    break;
                }
                recording = record(static_cast<int>(frames.size()) * 4) == 1;
            }
        }
        char line[512] = {};
        status(line, sizeof(line));
        printf("      replay done  [%s]\n", line);
        recordState(line, sizeof(line));
        printf("      recording: %s\n", line);
        return 0;
    }
}

int main()
{
    printf("ReDefinition proxy harness\n");

    {
        wchar_t replayFolder[MAX_PATH] = {};
        if (GetEnvironmentVariableW(L"REDEFINITION_HARNESS_REPLAY", replayFolder, MAX_PATH) > 0)
            return RunReplay(replayFolder);
    }

    // Before the proxy loads, so it starts with these whatever the ini next to
    // the harness says. fgVSync=0: the game's sync interval passes through, the
    // default. reportSeconds=1: the HUD-less check runs every second, across
    // the mode changes below, whose larger inputs its staging textures have to
    // follow.
    const IniKey fgVSync(L"fgVSync");
    const IniKey fgHalfRefreshLimit(L"fgHalfRefreshLimit");
    const IniKey fgAsyncWorkloads(L"fgAsyncWorkloads");
    const IniKey reportSeconds(L"reportSeconds");
    fgVSync.Set(L"0");
    fgHalfRefreshLimit.Set(L"0");
    fgAsyncWorkloads.Set(L"0");
    reportSeconds.Set(L"1");

    // DLSS, when REDEFINITION_DLSS_DIR names a folder with nvngx_dlss.dll.
    wchar_t dlssDirectoryBuffer[MAX_PATH] = {};
    GetEnvironmentVariableW(L"REDEFINITION_DLSS_DIR", dlssDirectoryBuffer, MAX_PATH);
    const std::wstring dlssDirectory(dlssDirectoryBuffer);
    const IniKey dlssDirectoryKey(L"dlssDirectory");
    dlssDirectoryKey.Set(dlssDirectory.empty() ? nullptr : dlssDirectory.c_str());

    // AMD's upscaler, when REDEFINITION_AMD_UPSCALER_DIR names a folder with a
    // player's amd_fidelityfx_upscaler_dx12.dll.
    wchar_t amdDirectoryBuffer[MAX_PATH] = {};
    GetEnvironmentVariableW(L"REDEFINITION_AMD_UPSCALER_DIR", amdDirectoryBuffer, MAX_PATH);
    const std::wstring amdDirectory(amdDirectoryBuffer);
    const IniKey amdDirectoryKey(L"amdUpscalerDirectory");
    amdDirectoryKey.Set(amdDirectory.empty() ? nullptr : amdDirectory.c_str());

    // DLSS frame generation, when REDEFINITION_STREAMLINE_DIR names a folder with
    // NVIDIA's Streamline 2.14.1 DLLs -- the SDK's bin\x64; without it the proxy
    // generates with FSR, if AMD's runtime is next to the harness.
    wchar_t streamlineDirectoryBuffer[MAX_PATH] = {};
    GetEnvironmentVariableW(L"REDEFINITION_STREAMLINE_DIR", streamlineDirectoryBuffer, MAX_PATH);
    const std::wstring streamlineDirectory(streamlineDirectoryBuffer);
    const IniKey streamlineDirectoryKey(L"streamlineDirectory");
    streamlineDirectoryKey.Set(streamlineDirectory.empty() ? nullptr : streamlineDirectory.c_str());

    // By bare name, exactly as Unity does it, so the application directory wins.
    const HMODULE dxgi = LoadLibraryW(L"dxgi.dll");
    if (dxgi == nullptr)
        return Fail("LoadLibrary(dxgi.dll)", HRESULT_FROM_WIN32(GetLastError()));

    wchar_t loaded[MAX_PATH] = {};
    GetModuleFileNameW(dxgi, loaded, MAX_PATH);
    wprintf(L"      loaded: %s\n", loaded);

    using CreateFactory2Fn = HRESULT(WINAPI*)(UINT, REFIID, void**);
    const auto createFactory2 =
        reinterpret_cast<CreateFactory2Fn>(GetProcAddress(dxgi, "CreateDXGIFactory2"));
    if (createFactory2 == nullptr)
        return Fail("GetProcAddress(CreateDXGIFactory2)", E_FAIL);

    ComPtr<IDXGIFactory2> factory;
    HRESULT hr = createFactory2(0, IID_PPV_ARGS(&factory));
    if (FAILED(hr))
        return Fail("CreateDXGIFactory2", hr);

    // On the default adapter -- except for DLSS frame generation, which runs on
    // NVIDIA's only.
    ComPtr<IDXGIAdapter1> deviceAdapter;
    if (!streamlineDirectory.empty())
    {
        deviceAdapter = NvidiaAdapter(factory.Get());
        if (deviceAdapter == nullptr)
            return Fail("REDEFINITION_STREAMLINE_DIR is set, but there is no NVIDIA adapter", E_FAIL);
    }

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    hr = D3D11CreateDevice(deviceAdapter.Get(), deviceAdapter ? D3D_DRIVER_TYPE_UNKNOWN : D3D_DRIVER_TYPE_HARDWARE,
                           nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context);
    if (FAILED(hr))
        return Fail("D3D11CreateDevice", hr);

    const HWND window = CreateHostWindow();
    if (window == nullptr)
        return Fail("CreateWindowEx", HRESULT_FROM_WIN32(GetLastError()));
    ShowWindow(window, SW_SHOW);
    // In front, like a game being played: DLSS frame generation generates only
    // for a window that has the focus ("DLSS-G disabled: window not focused",
    // Streamline's log). A process started from a console may not take the
    // foreground by itself; joined to the input of the window that has it, it may.
    {
        const HWND foreground = GetForegroundWindow();
        const DWORD foregroundThread = foreground != nullptr ? GetWindowThreadProcessId(foreground, nullptr) : 0;
        const DWORD ownThread = GetCurrentThreadId();
        const bool joined = foregroundThread != 0 && foregroundThread != ownThread
                            && AttachThreadInput(ownThread, foregroundThread, TRUE);
        SetForegroundWindow(window);
        BringWindowToTop(window);
        SetFocus(window);
        if (joined)
            AttachThreadInput(ownThread, foregroundThread, FALSE);
        PumpMessages();
        printf("      window in front: %s\n", GetForegroundWindow() == window ? "yes" : "no");
    }

    // The description KSP asks for, as the proxy logs it in the game: two
    // buffers and flags 0x842, which is ALLOW_TEARING
    // plus ALLOW_MODE_SWITCH plus FRAME_LATENCY_WAITABLE_OBJECT.
    //
    DXGI_SWAP_CHAIN_DESC1 desc = {};
    desc.Width = kWidth;
    desc.Height = kHeight;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT | DXGI_USAGE_SHADER_INPUT;
    desc.BufferCount = 2;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    desc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING
               | DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH
               | DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

    ComPtr<IDXGISwapChain1> swapChain;
    hr = factory->CreateSwapChainForHwnd(device.Get(), window, &desc, nullptr, nullptr, &swapChain);
    if (FAILED(hr))
        return Fail("CreateSwapChainForHwnd", hr);

    // What Unity does next: reach for IDXGISwapChain2 to get the frame latency
    // waitable object.
    ComPtr<IDXGISwapChain2> swapChain2;
    hr = swapChain.As(&swapChain2);
    if (FAILED(hr))
        return Fail("QueryInterface(IDXGISwapChain2) -- this is what stopped KSP", hr);

    const HANDLE waitable = swapChain2->GetFrameLatencyWaitableObject();
    if (waitable == nullptr)
        return Fail("GetFrameLatencyWaitableObject returned null", E_FAIL);
    printf("      waitable object: ok\n");

    // If the proxy engaged, this is its shared texture rather than a real
    // backbuffer. Either way it has to behave like a render target.
    ComPtr<ID3D11Texture2D> backBuffer;
    hr = swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
    if (FAILED(hr))
        return Fail("GetBuffer(0)", hr);

    D3D11_TEXTURE2D_DESC actual = {};
    backBuffer->GetDesc(&actual);
    printf("      backbuffer: %ux%u format %d misc 0x%X\n",
           actual.Width, actual.Height, static_cast<int>(actual.Format), actual.MiscFlags);

    const bool shared = (actual.MiscFlags & D3D11_RESOURCE_MISC_SHARED_NTHANDLE) != 0;
    printf("      proxy engaged: %s\n", shared ? "YES (shared texture)" : "no (real backbuffer)");

    ComPtr<ID3D11RenderTargetView> rtv;
    hr = device->CreateRenderTargetView(backBuffer.Get(), nullptr, &rtv);
    if (FAILED(hr))
        return Fail("CreateRenderTargetView", hr);

    // The HUD-less and frame generation tests need the proxy in place; without
    // it there is nothing to test.
    RenderEventFn renderEvent = nullptr;
    SetEnabledFn setEnabled = nullptr;
    LastCheckFn lastCheck = nullptr;
    RegisterInputsFn registerInputs = nullptr;
    PacketSizeFn packetSize = nullptr;
    StatusFn status = nullptr;
    LoadFn load = nullptr;
    ComPtr<ID3D11Texture2D> ui;
    ComPtr<ID3D11Texture2D> feature;
    ComPtr<ID3D11Texture2D> detail;
    const bool detailScene = EnvironmentSet(L"REDEFINITION_HARNESS_DETAIL");
    const bool jitterSequence = EnvironmentSet(L"REDEFINITION_HARNESS_JITTER");
    ComPtr<ID3D11Texture2D> hudLessCopy;
    Inputs inputs;
    FramePacket packet = {};

    if (shared)
    {
        const auto getRenderEvent =
            reinterpret_cast<GetRenderEventFn>(GetProcAddress(dxgi, "KspFgGetRenderEventFunc"));
        setEnabled = reinterpret_cast<SetEnabledFn>(GetProcAddress(dxgi, "KspFgSetEnabled"));
        lastCheck = reinterpret_cast<LastCheckFn>(GetProcAddress(dxgi, "KspFgLastHudLessCheck"));
        registerInputs = reinterpret_cast<RegisterInputsFn>(GetProcAddress(dxgi, "KspFgRegisterInputs3"));
        packetSize = reinterpret_cast<PacketSizeFn>(GetProcAddress(dxgi, "KspFgPacketSize"));
        status = reinterpret_cast<StatusFn>(GetProcAddress(dxgi, "KspFgStatus"));

        // The proxy measures this thread's load as the game's main thread; here
        // it is also the one that presents.
        const auto registerMainThread =
            reinterpret_cast<RegisterMainThreadFn>(GetProcAddress(dxgi, "KspPerfRegisterMainThread"));
        load = reinterpret_cast<LoadFn>(GetProcAddress(dxgi, "KspPerfLoad"));
        if (registerMainThread == nullptr || load == nullptr)
            return Fail("GetProcAddress(KspPerfRegisterMainThread / KspPerfLoad)", E_FAIL);
        registerMainThread();

        if (getRenderEvent == nullptr || setEnabled == nullptr || lastCheck == nullptr
            || registerInputs == nullptr || packetSize == nullptr || status == nullptr)
            return Fail("frame generation exports missing from dxgi.dll", E_FAIL);

        if (packetSize() != sizeof(FramePacket))
            return Fail("frame packet layout differs between harness and proxy", E_FAIL);

        renderEvent = reinterpret_cast<RenderEventFn>(getRenderEvent());

        // The snapshot is only taken while frame generation is wanted, exactly
        // as in the game.
        setEnabled(1);

        hr = CreateFilledTexture(device.Get(), kUiWidth, kUiHeight, DXGI_FORMAT_R8G8B8A8_UNORM,
                                 0xFFFFFFFFu, ui);
        if (FAILED(hr))
            return Fail("CreateTexture2D for the UI rectangle", hr);

        hr = CreateFilledTexture(device.Get(), kFeatureWidth, kFeatureHeight, DXGI_FORMAT_R8G8B8A8_UNORM,
                                 0xFF000000u, feature);
        if (FAILED(hr))
            return Fail("CreateTexture2D for the scene feature", hr);

        if (detailScene)
        {
            hr = CreateFilledTexture(device.Get(), kDetailWidth, kDetailHeight, DXGI_FORMAT_R8G8B8A8_UNORM,
                                     0u, detail);
            if (FAILED(hr))
                return Fail("CreateTexture2D for the detail scene", hr);
            std::vector<uint32_t> grain(static_cast<size_t>(kDetailWidth) * kDetailHeight);
            uint32_t seed = 12345u;
            for (uint32_t& texel : grain)
            {
                seed = seed * 1664525u + 1013904223u;
                const uint32_t v = 64u + ((seed >> 24) & 0x7Fu);
                texel = 0xFF000000u | (v << 16) | (v << 8) | v;
            }
            context->UpdateSubresource(detail.Get(), 0, nullptr, grain.data(), kDetailWidth * 4, 0);
            printf("      detail scene: still grain, jitter %s\n", jitterSequence ? "sequence" : "none");
        }
    }

    // The rig's inputs at a given size: depth, motion vectors and the HUD-less
    // texture the scene is copied into before the UI. Registered together, as
    // the rig does it, with the packet's render size to match.
    auto provideInputs = [&](UINT width, UINT height, UINT displayWidth, UINT displayHeight) -> HRESULT
    {
        HRESULT created = CreateInputs(device.Get(), width, height, inputs);
        if (FAILED(created))
            return created;

        created = CreateFilledTexture(device.Get(), displayWidth, displayHeight,
                                      DXGI_FORMAT_R8G8B8A8_UNORM, 0u, hudLessCopy);
        if (FAILED(created))
            return created;

        registerInputs(inputs.depth.Get(), inputs.motion.Get(), hudLessCopy.Get());

        // As the rig sends it: jitter, the motion vector scale of Unity's
        // vectors, raw clip planes, vertical field of view, frame time,
        // render size. And a valid camera basis, which the API requires.
        packet = {};
        packet.size = sizeof(FramePacket);
        packet.magic = kPacketMagic;
        packet.renderWidth = width;
        packet.renderHeight = height;
        packet.motionVectorScaleX = -static_cast<float>(width);
        packet.motionVectorScaleY = -static_cast<float>(height);
        packet.nearPlane = 0.1f;
        packet.farPlane = 1000.0f;
        packet.verticalFovRadians = 1.0472f;
        packet.frameTimeDeltaMs = 16.7f;
        packet.up[1] = 1.0f;
        packet.right[0] = 1.0f;
        packet.forward[2] = 1.0f;

        // The rig's matrices for DLSS frame generation: row-major, view space
        // down +z, Direct3D clip space with reversed depth -- and a camera that
        // does not move, so current and previous clip space are the same.
        const float yScale = 1.0f / std::tan(packet.verticalFovRadians * 0.5f);
        const float xScale = yScale * static_cast<float>(height) / static_cast<float>(width);
        const float n = packet.nearPlane;
        const float f = packet.farPlane;
        const float c = -n / (f - n);
        const float d = n * f / (f - n);
        const float viewToClip[16] = { xScale, 0, 0, 0, 0, yScale, 0, 0, 0, 0, c, 1, 0, 0, d, 0 };
        const float clipToView[16] = { 1 / xScale, 0, 0, 0, 0, 1 / yScale, 0, 0, 0, 0, 0, 1 / d, 0, 0, 1, -c / d };
        const float identity[16] = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        memcpy(packet.viewToClip, viewToClip, sizeof(viewToClip));
        memcpy(packet.clipToView, clipToView, sizeof(clipToView));
        memcpy(packet.clipToPrevClip, identity, sizeof(identity));
        memcpy(packet.prevClipToClip, identity, sizeof(identity));
        return S_OK;
    };

    // Depth and motion vectors smaller than the display, as every mode below
    // AA only makes them: the HUD-less check makes its staging textures at
    // this size, and right after it, at display size, has to follow.
    if (shared)
    {
        hr = provideInputs(kWidth * 2 / 3, kHeight * 2 / 3, kWidth, kHeight);
        if (FAILED(hr))
            return Fail("creating the inputs", hr);
    }

    // One frame the way the game draws it: scene, the captures the managed side
    // raises, the UI on top, present.
    UINT frameNumber = 0;
    // KSP's V-Sync setting as Unity passes it to Present; 0 but where a run sets it.
    UINT syncInterval = 0;
    auto renderFrame = [&]() -> HRESULT
    {
        PumpMessages();

        // Waiting on the object before rendering is the point of asking for it,
        // and it is what the game does too.
        WaitForSingleObject(waitable, 1000);

        // A colour that changes every frame, so a stuck image is visible as such
        // rather than looking like success -- and so a snapshot from an earlier
        // frame differs everywhere instead of passing for a correct one.
        const float t = static_cast<float>(frameNumber % kFrames) / kFrames;
        ++frameNumber;
        const float colour[4] = { t, 0.2f, 1.0f - t, 1.0f };
        const float still[4] = { 0.3f, 0.3f, 0.3f, 1.0f };
        context->ClearRenderTargetView(rtv.Get(), detailScene ? still : colour);

        if (renderEvent != nullptr)
        {
            // Something in the scene that is not symmetric top to bottom.
            const D3D11_BOX featureBox = { 0, 0, 0, kFeatureWidth, kFeatureHeight, 1 };
            context->CopySubresourceRegion(backBuffer.Get(), 0, kFeatureX, kFeatureY, 0,
                                           feature.Get(), 0, &featureBox);
            if (detail)
            {
                const D3D11_BOX detailBox = { 0, 0, 0, kDetailWidth, kDetailHeight, 1 };
                context->CopySubresourceRegion(backBuffer.Get(), 0, kDetailX, kDetailY, 0,
                                               detail.Get(), 0, &detailBox);
            }
            if (jitterSequence)
            {
                packet.jitterX = Halton(frameNumber % 8, 2) - 0.5f;
                packet.jitterY = Halton(frameNumber % 8, 3) - 0.5f;
            }

            // The scene is finished: the rig blits the backbuffer into its
            // HUD-less texture here. Then this frame's packet, then the "UI"
            // on top. The proxy copies everything at Present.
            context->CopyResource(hudLessCopy.Get(), backBuffer.Get());
            packet.frameIndex = frameNumber;
            renderEvent(kPacketEvent, &packet);

            const D3D11_BOX box = { 0, 0, 0, kUiWidth, kUiHeight, 1 };
            context->CopySubresourceRegion(backBuffer.Get(), 0, kUiX, kUiY, 0, ui.Get(), 0, &box);
        }

        return swapChain->Present(syncInterval, syncInterval == 0 && shared ? DXGI_PRESENT_ALLOW_TEARING : 0);
    };

    for (UINT frame = 0; frame < kFrames; ++frame)
    {
        hr = renderFrame();
        if (FAILED(hr))
            return Fail("Present", hr);
    }

    printf("      %u frames presented\n", kFrames);

    if (lastCheck != nullptr)
    {
        float direct = -1.0f;
        float mirrored = -1.0f;
        // Its readback never waits for the GPU, and a Present that returns at
        // once -- DLSS-G's presents asynchronously -- can leave the thirty frames
        // done before the copies are: up to two seconds more.
        LARGE_INTEGER checkFrequency = {};
        QueryPerformanceFrequency(&checkFrequency);
        LARGE_INTEGER checkStart = {};
        QueryPerformanceCounter(&checkStart);
        LARGE_INTEGER checkNow = checkStart;
        while (lastCheck(&direct, &mirrored) == 0 && checkNow.QuadPart - checkStart.QuadPart < 2 * checkFrequency.QuadPart)
        {
            hr = renderFrame();
            if (FAILED(hr))
                return Fail("Present while the HUD-less check completes", hr);
            QueryPerformanceCounter(&checkNow);
        }
        if (lastCheck(&direct, &mirrored) == 0)
            return Fail("the HUD-less check never completed", E_FAIL);

        const float pixels = static_cast<float>(kWidth * kHeight);
        const float uiShare = static_cast<float>(kUiWidth * kUiHeight) / pixels;
        const float featureShare = static_cast<float>(kFeatureWidth * kFeatureHeight) / pixels;
        printf("      HUD-less check: direct %.2f %%, mirrored %.2f %% of the frame differs; "
               "the UI covers %.2f %%, the scene feature %.2f %%\n",
               100.0 * direct, 100.0 * mirrored, 100.0 * uiShare, 100.0 * featureShare);

        // With the flip off, as the proxy defaults, the copy differs from the
        // frame by exactly the UI; mirrored, by the UI plus the scene feature
        // and its mirror image. Nothing would mean the copy was taken after
        // the UI; nearly everything in both, a stale one; the two swapped, a
        // flip that should not have happened.
        if (direct < uiShare - 0.005f || direct > uiShare + 0.005f)
            return Fail("the HUD-less copy differs from the frame by something other than the UI", E_FAIL);
        const float mirroredExpected = uiShare + 2.0f * featureShare;
        if (!detailScene && (mirrored < mirroredExpected - 0.005f || mirrored > mirroredExpected + 0.005f))
            return Fail("the HUD-less copy was flipped, or the scene feature is missing", E_FAIL);
    }

    // The check's staging textures were made at the inputs above, smaller than
    // the display as every mode below AA only makes them. Now the inputs grow
    // to display size, and the next check has to make them anew at it rather
    // than read a mapping made at the smaller size by the larger one. The check
    // runs every second here (reportSeconds), and needs no FidelityFX runtime.
    if (shared)
    {
        hr = provideInputs(kWidth, kHeight, kWidth, kHeight);
        if (FAILED(hr))
            return Fail("creating the inputs at display size", hr);

        LARGE_INTEGER ticksPerSecond = {};
        QueryPerformanceFrequency(&ticksPerSecond);
        LARGE_INTEGER start = {};
        QueryPerformanceCounter(&start);
        LARGE_INTEGER now = start;
        while (now.QuadPart - start.QuadPart < ticksPerSecond.QuadPart * 3 / 2)
        {
            hr = renderFrame();
            if (FAILED(hr))
                return Fail("Present while the check follows the inputs", hr);
            QueryPerformanceCounter(&now);
        }

        char checkLine[256] = {};
        status(checkLine, sizeof(checkLine));
        char expected[32] = {};
        sprintf_s(expected, "check=%ux%u", kWidth, kHeight);
        printf("      check after the inputs grew:  [%s]\n", checkLine);
        if (strstr(checkLine, expected) == nullptr)
            return Fail("the HUD-less check's staging textures did not follow the inputs", E_FAIL);
    }

    // ---------------------------------------------------------------- frame generation

    // Which frame generation the proxy's swapchain presents with (KspFgTechnique).
    using TechniqueFn = int (*)(int*);
    const TechniqueFn technique = reinterpret_cast<TechniqueFn>(GetProcAddress(dxgi, "KspFgTechnique"));
    if (shared && technique == nullptr)
        return Fail("GetProcAddress(KspFgTechnique)", E_FAIL);
    int multiplier = 0;
    const bool dlssFrameGeneration = shared && technique(&multiplier) == 2;

    // Whether DLSS-G presents with V-Sync where KSP asks for it (KspFgDlssVsync),
    // which the mod's V-Sync row follows.
    using DlssVsyncFn = int (*)();
    const DlssVsyncFn dlssVsync = reinterpret_cast<DlssVsyncFn>(GetProcAddress(dxgi, "KspFgDlssVsync"));
    if (dlssVsync == nullptr)
        return Fail("GetProcAddress(KspFgDlssVsync)", E_FAIL);

    // The GPU the mod offers NVIDIA's DLLs by (KspNvidiaGpu): with DLSS frame
    // generation running, at least Ada's architecture.
    using NvidiaGpuFn = int (*)(unsigned int*, unsigned int*);
    const NvidiaGpuFn nvidiaGpu = reinterpret_cast<NvidiaGpuFn>(GetProcAddress(dxgi, "KspNvidiaGpu"));
    if (nvidiaGpu == nullptr)
        return Fail("GetProcAddress(KspNvidiaGpu)", E_FAIL);
    {
        unsigned int arch = 0;
        unsigned int implementation = 0;
        const int known = nvidiaGpu(&arch, &implementation);
        printf("      NVIDIA GPU of the swapchain's adapter: %s, architecture 0x%X, implementation 0x%X\n",
               known ? "known" : "none", arch, implementation);
        if (dlssFrameGeneration && arch < 0x190)
            return Fail("DLSS frame generation runs, but the adapter's architecture reads below Ada's", E_FAIL);
    }
    if (shared && !streamlineDirectory.empty() && !dlssFrameGeneration)
    {
        char why[256] = {};
        status(why, sizeof(why));
        printf("      [%s]\n", why);
        return Fail("REDEFINITION_STREAMLINE_DIR is set, but DLSS frame generation does not run", E_FAIL);
    }

    // DLSS frame generation: the same phases as FSR's below, counted by the
    // proxy's own totals (KspFgCounters), which take DLSS-G's count of the
    // frames it presented. What is FSR's alone -- its context, VSync pacing, the
    // half refresh limit, async workloads -- is not asked here.
    bool dlssGenerated = false;
    if (dlssFrameGeneration)
    {
        const CountersFn counters = reinterpret_cast<CountersFn>(GetProcAddress(dxgi, "KspFgCounters"));
        if (counters == nullptr)
            return Fail("GetProcAddress(KspFgCounters)", E_FAIL);

        // Presents per rendered frame over a stretch of rendering, the last half
        // second of which settles DLSS-G after a change before counting starts.
        auto presentsPerFrame = [&](float seconds, float& ratio) -> HRESULT
        {
            LARGE_INTEGER frequency = {};
            QueryPerformanceFrequency(&frequency);
            auto renderSeconds = [&](float span) -> HRESULT
            {
                LARGE_INTEGER start = {};
                QueryPerformanceCounter(&start);
                LARGE_INTEGER now = start;
                while (static_cast<float>(now.QuadPart - start.QuadPart) < span * static_cast<float>(frequency.QuadPart))
                {
                    const HRESULT presented = renderFrame();
                    if (FAILED(presented))
                        return presented;
                    QueryPerformanceCounter(&now);
                }
                return S_OK;
            };
            HRESULT result = renderSeconds(0.5f);
            if (FAILED(result))
                return result;
            unsigned int renderedBefore = 0;
            unsigned int presentedBefore = 0;
            counters(&renderedBefore, &presentedBefore);
            result = renderSeconds(seconds);
            if (FAILED(result))
                return result;
            unsigned int renderedAfter = 0;
            unsigned int presentedAfter = 0;
            counters(&renderedAfter, &presentedAfter);
            const unsigned int rendered = renderedAfter - renderedBefore;
            ratio = rendered != 0 ? static_cast<float>(presentedAfter - presentedBefore) / static_cast<float>(rendered)
                                  : 0.0f;
            return S_OK;
        };

        char line[256] = {};
        float on = 0.0f;
        hr = presentsPerFrame(2.0f, on);
        if (FAILED(hr))
            return Fail("Present with DLSS frame generation on", hr);
        status(line, sizeof(line));
        technique(&multiplier);
        printf("      DLSS frame generation on: %.2f presents per frame, %dx  [%s]\n", on, multiplier, line);
        if (on < kGeneratingAtLeast || multiplier < 2)
            return Fail(GetForegroundWindow() != window
                            ? "DLSS frame generation on, but no frames were generated -- the harness window lost the"
                              " focus, without which DLSS-G generates nothing; run it again without touching anything"
                            : "DLSS frame generation on, but no frames were generated",
                        E_FAIL);

        // The screen recording (ScreenRecorder.h) while it generates: the frames the
        // monitor shows, into ReDefinitionCaptures beside the harness.
        {
            using RecordFn = int(__cdecl*)(int);
            using RecordStateFn = int(__cdecl*)(char*, int);
            HMODULE proxy = GetModuleHandleW(L"dxgi.dll");
            auto record = reinterpret_cast<RecordFn>(GetProcAddress(proxy, "KspRecordScreen"));
            auto recordState = reinterpret_cast<RecordStateFn>(GetProcAddress(proxy, "KspRecordScreenState"));
            if (record == nullptr || recordState == nullptr)
                return Fail("the proxy exports no screen recording", E_FAIL);
            if (record(40) != 1)
                return Fail("the screen recording did not start", E_FAIL);
            char state[256] = {};
            for (int wait = 0; wait < 40; ++wait)
            {
                float ignored = 0.0f;
                hr = presentsPerFrame(0.1f, ignored);
                if (FAILED(hr))
                    return Fail("Present during the screen recording", hr);
                recordState(state, sizeof(state));
                if (std::strncmp(state, "running", 7) != 0)
                    break;
            }
            printf("      screen recording while DLSS frame generation runs: %s\n", state);
            if (std::strncmp(state, "done", 4) != 0)
                return Fail("the screen recording did not finish", E_FAIL);

            // The cut-out is larger than the harness window and holds whatever else is
            // on the screen: only that it ran is tested, so the files go again.
            const std::string done(state);
            const size_t from = done.find("done: ");
            const size_t to = done.find(" (", from);
            if (from != std::string::npos && to != std::string::npos)
            {
                const std::string folder = done.substr(from + 6, to - from - 6);
                WIN32_FIND_DATAA found = {};
                const HANDLE search = FindFirstFileA((folder + "\\*").c_str(), &found);
                if (search != INVALID_HANDLE_VALUE)
                {
                    do
                        if (!(found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
                            DeleteFileA((folder + "\\" + found.cFileName).c_str());
                    while (FindNextFileA(search, &found));
                    FindClose(search);
                }
                RemoveDirectoryA(folder.c_str());
            }
        }

        // Mode changes while it generates, as the toolbar makes them.
        const UINT sizes[2][2] = { { kWidth * 2 / 3, kHeight * 2 / 3 }, { kWidth / 2, kHeight / 2 } };
        for (UINT change = 0; change < 8; ++change)
        {
            hr = provideInputs(sizes[change % 2][0], sizes[change % 2][1], kWidth, kHeight);
            if (FAILED(hr))
                return Fail("creating the inputs for a mode change under DLSS frame generation", hr);
            for (UINT frame = 0; frame < 5; ++frame)
            {
                hr = renderFrame();
                if (FAILED(hr))
                    return Fail("Present after a mode change under DLSS frame generation", hr);
            }
        }
        float afterChanges = 0.0f;
        hr = presentsPerFrame(1.0f, afterChanges);
        if (FAILED(hr))
            return Fail("Present after the mode changes under DLSS frame generation", hr);
        status(line, sizeof(line));
        printf("      DLSS frame generation after 8 mode changes: %.2f presents per frame  [%s]\n", afterChanges, line);
        if (afterChanges < kGeneratingAtLeast)
            return Fail("DLSS frame generation stopped after mode changes", E_FAIL);
        if (strstr(line, "device=ok") == nullptr)
            return Fail("the proxy's device did not survive the mode changes under DLSS frame generation", E_FAIL);
        hr = provideInputs(kWidth, kHeight, kWidth, kHeight);
        if (FAILED(hr))
            return Fail("creating the inputs after the mode changes under DLSS frame generation", hr);

        setEnabled(0);
        float off = 0.0f;
        hr = presentsPerFrame(1.0f, off);
        if (FAILED(hr))
            return Fail("Present with DLSS frame generation switched off", hr);
        printf("      DLSS frame generation off: %.2f presents per frame\n", off);
        if (off > kNotGeneratingAtMost)
            return Fail("DLSS frame generation switched off, but frames were still generated", E_FAIL);

        setEnabled(1);
        float again = 0.0f;
        hr = presentsPerFrame(1.0f, again);
        if (FAILED(hr))
            return Fail("Present with DLSS frame generation on again", hr);
        printf("      DLSS frame generation on again: %.2f presents per frame\n", again);
        if (again < kGeneratingAtLeast)
            return Fail("DLSS frame generation switched back on, but no frames were generated", E_FAIL);

        // KSP's "Every second refresh": DLSS-G presents every refresh with it
        // (SwapChainProxy::CopyAndPresent), and goes on generating.
        syncInterval = 2;
        float synced = 0.0f;
        hr = presentsPerFrame(2.0f, synced);
        syncInterval = 0;
        if (FAILED(hr))
            return Fail("Present with DLSS frame generation and V-Sync", hr);
        status(line, sizeof(line));
        printf("      DLSS frame generation with V-Sync every second refresh: %.2f presents per frame  [%s]\n", synced,
               line);
        if (synced < kGeneratingAtLeast)
            return Fail("DLSS frame generation with V-Sync generated no frames", E_FAIL);
        // The build in the SDK's bin\x64 says it supports V-Sync.
        if (dlssVsync() != 1)
            return Fail("DLSS frame generation reports no V-Sync support (KspFgDlssVsync)", E_FAIL);

        // A resize, as a resolution change or full screen makes one.
        rtv.Reset();
        backBuffer.Reset();
        context->ClearState();
        hr = swapChain->ResizeBuffers(0, kResizedWidth, kResizedHeight, DXGI_FORMAT_UNKNOWN, desc.Flags);
        if (FAILED(hr))
            return Fail("ResizeBuffers under DLSS frame generation", hr);
        hr = swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
        if (FAILED(hr))
            return Fail("GetBuffer(0) after the resize under DLSS frame generation", hr);
        hr = device->CreateRenderTargetView(backBuffer.Get(), nullptr, &rtv);
        if (FAILED(hr))
            return Fail("CreateRenderTargetView after the resize under DLSS frame generation", hr);
        hr = provideInputs(kResizedWidth, kResizedHeight, kResizedWidth, kResizedHeight);
        if (FAILED(hr))
            return Fail("creating inputs after the resize under DLSS frame generation", hr);

        float resized = 0.0f;
        hr = presentsPerFrame(1.0f, resized);
        if (FAILED(hr))
            return Fail("Present with DLSS frame generation after the resize", hr);
        status(line, sizeof(line));
        printf("      DLSS frame generation after a resize to %ux%u: %.2f presents per frame  [%s]\n", kResizedWidth,
               kResizedHeight, resized, line);
        if (resized < kGeneratingAtLeast)
            return Fail("DLSS frame generation on after a resize, but no frames were generated", E_FAIL);
        dlssGenerated = true;
    }

    // One swapchain, one frame generation: with DLSS-G running, FSR's phases below
    // are not reached, and a second run without REDEFINITION_STREAMLINE_DIR tests
    // them.
    const bool frameGeneration = shared && !dlssFrameGeneration
                                 && FileNextToExecutable(L"amd_fidelityfx_framegeneration_dx12.dll");
    if (dlssFrameGeneration)
        printf("      FSR frame generation: not tested in this run, DLSS frame generation runs -- run again"
               " without REDEFINITION_STREAMLINE_DIR for it\n");
    else if (shared && !frameGeneration)
        printf("      frame generation: skipped, no FidelityFX runtime next to the harness\n");

    if (frameGeneration)
    {
        // Presents per rendered frame over a phase, from the swapchain's own
        // count. FSR presents generated frames from a thread of its own, so the
        // count is read after a moment's pause.
        auto presentCount = [&]() -> UINT
        {
            Sleep(100);
            UINT count = 0;
            swapChain->GetLastPresentCount(&count);
            return count;
        };

        // Rendered frames per second over a phase, measured around the loop
        // alone -- not around the pauses presentCount takes.
        LARGE_INTEGER frequency = {};
        QueryPerformanceFrequency(&frequency);
        float fps = 0.0f;

        auto runPhase = [&](UINT frames, float& ratio) -> HRESULT
        {
            const UINT before = presentCount();
            LARGE_INTEGER start = {};
            QueryPerformanceCounter(&start);
            for (UINT frame = 0; frame < frames; ++frame)
            {
                const HRESULT presented = renderFrame();
                if (FAILED(presented))
                    return presented;
            }
            LARGE_INTEGER end = {};
            QueryPerformanceCounter(&end);
            fps = static_cast<float>(frames) * static_cast<float>(frequency.QuadPart)
                / static_cast<float>(end.QuadPart - start.QuadPart);
            ratio = static_cast<float>(presentCount() - before) / static_cast<float>(frames);
            return S_OK;
        };

        // Rendering without a pause for a while: how many frames, and how many
        // a second.
        auto renderFor = [&](float seconds, float& rate, UINT& frames) -> HRESULT
        {
            frames = 0;
            LARGE_INTEGER start = {};
            QueryPerformanceCounter(&start);
            LARGE_INTEGER now = start;
            const LONGLONG until = static_cast<LONGLONG>(seconds * static_cast<float>(frequency.QuadPart));
            while (now.QuadPart - start.QuadPart < until)
            {
                const HRESULT presented = renderFrame();
                if (FAILED(presented))
                    return presented;
                ++frames;
                QueryPerformanceCounter(&now);
            }
            rate = static_cast<float>(frames) * static_cast<float>(frequency.QuadPart)
                 / static_cast<float>(now.QuadPart - start.QuadPart);
            return S_OK;
        };

        // The refresh rate of the monitor the window is on, for the pacing
        // check below.
        DEVMODEW mode = {};
        mode.dmSize = sizeof(mode);
        float refresh = 0.0f;
        const HMONITOR monitor = MonitorFromWindow(window, MONITOR_DEFAULTTOPRIMARY);
        MONITORINFOEXW monitorInfo = {};
        monitorInfo.cbSize = sizeof(monitorInfo);
        if (GetMonitorInfoW(monitor, &monitorInfo)
            && EnumDisplaySettingsW(monitorInfo.szDevice, ENUM_CURRENT_SETTINGS, &mode))
            refresh = static_cast<float>(mode.dmDisplayFrequency);

        float on = 0.0f;
        hr = runPhase(2 * kFrames, on);
        if (FAILED(hr))
            return Fail("Present with frame generation on", hr);

        // The status line names the display size the context was made for, so
        // a context left behind at an old size shows as exactly that.
        auto contextIs = [&](UINT width, UINT height, char (&text)[256]) -> bool
        {
            status(text, sizeof(text));
            char expected[48] = {};
            sprintf_s(expected, "context=%ux%u", width, height);
            return strstr(text, expected) != nullptr;
        };

        char line[256] = {};
        const bool contextAtDisplaySize = contextIs(kWidth, kHeight, line);
        const float fpsOn = fps;
        printf("      frame generation on: %.2f presents per frame, %.0f rendered fps at %.0f Hz  [%s]\n",
               on, fpsOn, refresh, line);

        // Two and a half seconds of rendering without a pause -- runPhase
        // pauses for FSR's count, and the phase above is too short for FSR's
        // queue to have filled. This is the steady rate the pacing check below
        // needs. The proxy's running totals behind the mod's window
        // (KspFgCounters) have to agree with it: one rendered frame for every
        // frame presented here, and the generated ones on top.
        const CountersFn counters = reinterpret_cast<CountersFn>(GetProcAddress(dxgi, "KspFgCounters"));
        if (counters == nullptr)
            return Fail("GetProcAddress(KspFgCounters)", E_FAIL);
        unsigned int renderedBefore = 0;
        unsigned int presentedBefore = 0;
        if (counters(&renderedBefore, &presentedBefore) == 0)
            return Fail("the proxy has counted no frames", E_FAIL);

        float fpsSteady = 0.0f;
        UINT steadyFrames = 0;
        hr = renderFor(2.5f, fpsSteady, steadyFrames);
        if (FAILED(hr))
            return Fail("Present during the steady run", hr);

        unsigned int renderedAfter = 0;
        unsigned int presentedAfter = 0;
        counters(&renderedAfter, &presentedAfter);
        const unsigned int renderedCounted = renderedAfter - renderedBefore;
        const unsigned int presentedCounted = presentedAfter - presentedBefore;
        printf("      frame generation on, steady: %.0f rendered fps; the proxy counted %u rendered and "
               "%u presented for %u frames\n", fpsSteady, renderedCounted, presentedCounted, steadyFrames);
        if (renderedCounted != steadyFrames)
            return Fail("the proxy's rendered total does not match the frames presented", E_FAIL);
        if (static_cast<float>(presentedCounted) < kGeneratingAtLeast * static_cast<float>(renderedCounted))
            return Fail("the proxy's totals do not show the generated frames", E_FAIL);


        // Mode changes while frame generation generates, as the toolbar makes
        // them: new inputs at another render size, registered between two
        // Presents with no wait, while the GPU still has frames in flight that
        // read the old ones. Released at once, the old ones would be a GPU fault
        // that removes the proxy's device. Eight
        // changes, alternating sizes; frame generation has to carry on, and
        // the device has to survive.
        {
            const UINT sizes[2][2] = { { kWidth * 2 / 3, kHeight * 2 / 3 }, { kWidth / 2, kHeight / 2 } };
            for (UINT change = 0; change < 8; ++change)
            {
                hr = provideInputs(sizes[change % 2][0], sizes[change % 2][1], kWidth, kHeight);
                if (FAILED(hr))
                    return Fail("creating the inputs for a mode change", hr);
                for (UINT frame = 0; frame < 5; ++frame)
                {
                    hr = renderFrame();
                    if (FAILED(hr))
                        return Fail("Present after a mode change", hr);
                }
            }
        }
        float afterChanges = 0.0f;
        hr = runPhase(kFrames, afterChanges);
        if (FAILED(hr))
            return Fail("Present after the mode changes", hr);
        char changedLine[256] = {};
        status(changedLine, sizeof(changedLine));
        printf("      frame generation on after 8 mode changes: %.2f presents per frame  [%s]\n",
               afterChanges, changedLine);
        if (afterChanges < kGeneratingAtLeast)
            return Fail("frame generation stopped after mode changes", E_FAIL);
        if (strstr(changedLine, "device=ok") == nullptr)
            return Fail("the proxy's device did not survive the mode changes", E_FAIL);
        if (strstr(changedLine, "retired=0") == nullptr)
            return Fail("inputs replaced by the mode changes were never released", E_FAIL);

        // Back to the display size for the phases that follow.
        hr = provideInputs(kWidth, kHeight, kWidth, kHeight);
        if (FAILED(hr))
            return Fail("creating the inputs after the mode changes", hr);


        setEnabled(0);
        float off = 0.0f;
        hr = runPhase(kFrames, off);
        if (FAILED(hr))
            return Fail("Present with frame generation switched off", hr);
        const float fpsOff = fps;
        printf("      frame generation off: %.2f presents per frame, %.0f rendered fps\n", off, fpsOff);

        setEnabled(1);
        float again = 0.0f;
        hr = runPhase(kFrames, again);
        if (FAILED(hr))
            return Fail("Present with frame generation on again", hr);
        printf("      frame generation on again: %.2f presents per frame\n", again);

        // A resize, as a resolution change or a switch to fullscreen makes one:
        // every reference to the old buffer let go, the swapchain resized, the
        // buffer fetched anew -- and the rig's inputs rebuilt at the new size.
        rtv.Reset();
        backBuffer.Reset();
        context->ClearState();

        hr = swapChain->ResizeBuffers(0, kResizedWidth, kResizedHeight, DXGI_FORMAT_UNKNOWN, desc.Flags);
        if (FAILED(hr))
            return Fail("ResizeBuffers", hr);

        hr = swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
        if (FAILED(hr))
            return Fail("GetBuffer(0) after the resize", hr);

        hr = device->CreateRenderTargetView(backBuffer.Get(), nullptr, &rtv);
        if (FAILED(hr))
            return Fail("CreateRenderTargetView after the resize", hr);

        hr = provideInputs(kResizedWidth, kResizedHeight, kResizedWidth, kResizedHeight);
        if (FAILED(hr))
            return Fail("creating inputs after the resize", hr);

        float resized = 0.0f;
        hr = runPhase(kFrames, resized);
        if (FAILED(hr))
            return Fail("Present with frame generation on after the resize", hr);
        const bool contextFollowedResize = contextIs(kResizedWidth, kResizedHeight, line);
        printf("      frame generation on after a resize to %ux%u: %.2f presents per frame  [%s]\n",
               kResizedWidth, kResizedHeight, resized, line);

        if (on < kGeneratingAtLeast)
            return Fail("frame generation on, but no frames were generated", E_FAIL);
        if (off > kNotGeneratingAtMost)
            return Fail("frame generation switched off, but frames were still generated", E_FAIL);
        if (again < kGeneratingAtLeast)
            return Fail("frame generation switched back on, but no frames were generated", E_FAIL);
        if (resized < kGeneratingAtLeast)
            return Fail("frame generation on after a resize, but no frames were generated", E_FAIL);
        if (!contextAtDisplaySize)
            return Fail("no frame generation context at the display size", E_FAIL);

        // The harness presents without VSync, like KSP with its own VSync off.
        // The proxy passes the game's sync interval through, also while
        // generating (fgVSync=0 by default): rendering is
        // not held at half the refresh rate then, and with frame generation off
        // this tiny scene renders far faster than the monitor refreshes. The
        // first check only where the scene renders well above the refresh rate
        // with frame generation off: on a slow GPU or a very fast monitor a low
        // rate says nothing about pacing.
        const bool fastEnough = refresh > 0.0f && fpsOff > 2.0f * refresh;
        if (!fastEnough)
            printf("      pacing checks skipped: %.0f rendered fps with frame generation off, not above twice %.0f Hz\n",
                   fpsOff, refresh);
        if (fastEnough && fpsSteady < 0.65f * refresh)
            return Fail("frame generation on, but rendering is paced although the game presents without VSync", E_FAIL);
        if (refresh > 0.0f && fpsOff < refresh)
            return Fail("frame generation off, but the game's own sync interval was not passed through", E_FAIL);

        // fgVSync=1, through the ini's live reload, which is looked at once a
        // second: while generating, the proxy then presents with VSync whatever
        // the game asked for, and FSR holds rendering near half the refresh
        // rate.
        if (fastEnough)
        {
            fgVSync.Set(L"1");
            float settling = 0.0f;
            UINT settlingFrames = 0;
            hr = renderFor(1.5f, settling, settlingFrames);
            if (FAILED(hr))
                return Fail("Present while the ini reloads", hr);

            float fpsForced = 0.0f;
            UINT forcedFrames = 0;
            hr = renderFor(2.0f, fpsForced, forcedFrames);
            if (FAILED(hr))
                return Fail("Present with fgVSync=1", hr);
            fgVSync.Set(L"0");

            printf("      fgVSync=1 from the ini: %.0f rendered fps at %.0f Hz while generating\n",
                   fpsForced, refresh);
            if (fpsForced > 0.65f * refresh)
                return Fail("fgVSync=1, but rendering is not paced by VSync while generating", E_FAIL);

            // fgHalfRefreshLimit=1: without VSync, the proxy itself holds rendering
            // slightly below half the refresh rate while generating.
            fgHalfRefreshLimit.Set(L"1");
            hr = renderFor(1.5f, settling, settlingFrames);
            if (FAILED(hr))
                return Fail("Present while the ini reloads the frame limit", hr);
            float fpsLimited = 0.0f;
            UINT limitedFrames = 0;
            hr = renderFor(2.0f, fpsLimited, limitedFrames);
            if (FAILED(hr))
                return Fail("Present with fgHalfRefreshLimit=1", hr);
            fgHalfRefreshLimit.Set(L"0");

            printf("      fgHalfRefreshLimit=1 from the ini: %.0f rendered fps at %.0f Hz while generating\n",
                   fpsLimited, refresh);
            if (fpsLimited > 0.55f * refresh || fpsLimited < 0.45f * refresh)
                return Fail("fgHalfRefreshLimit=1, but rendering is not held at half the refresh rate", E_FAIL);

            // And lets go again with the key back at 0.
            hr = renderFor(1.5f, settling, settlingFrames);
            if (FAILED(hr))
                return Fail("Present while the ini reloads the frame limit off", hr);
            float fpsReleased = 0.0f;
            UINT releasedFrames = 0;
            hr = renderFor(1.5f, fpsReleased, releasedFrames);
            if (FAILED(hr))
                return Fail("Present with fgHalfRefreshLimit=0 again", hr);
            printf("      fgHalfRefreshLimit=0 again: %.0f rendered fps while generating\n", fpsReleased);
            if (fpsReleased < 0.65f * refresh)
                return Fail("fgHalfRefreshLimit=0 again, but rendering is still held", E_FAIL);
        }

        // fgAsyncWorkloads=1 through the ini, which rebuilds the context: the
        // interpolation moves to a compute queue of FSR's swapchain. Frame
        // generation has to carry on, across mode changes too, whose replaced
        // inputs go only once the swapchain has finished its work with them.
        {
            fgAsyncWorkloads.Set(L"1");
            float settling = 0.0f;
            UINT settlingFrames = 0;
            hr = renderFor(1.5f, settling, settlingFrames);
            if (FAILED(hr))
                return Fail("Present while the ini switches async workloads on", hr);

            for (UINT change = 0; change < 4; ++change)
            {
                const UINT divisor = change % 2 == 0 ? 2 : 3;
                hr = provideInputs(kResizedWidth * 2 / divisor, kResizedHeight * 2 / divisor, kResizedWidth,
                                   kResizedHeight);
                if (FAILED(hr))
                    return Fail("creating the inputs for a mode change with async workloads", hr);
                for (UINT frame = 0; frame < 5; ++frame)
                {
                    hr = renderFrame();
                    if (FAILED(hr))
                        return Fail("Present after a mode change with async workloads", hr);
                }
            }
            hr = provideInputs(kResizedWidth, kResizedHeight, kResizedWidth, kResizedHeight);
            if (FAILED(hr))
                return Fail("creating the inputs after the async mode changes", hr);

            float asyncOn = 0.0f;
            hr = runPhase(kFrames, asyncOn);
            if (FAILED(hr))
                return Fail("Present with fgAsyncWorkloads=1", hr);
            char asyncLine[256] = {};
            status(asyncLine, sizeof(asyncLine));
            printf("      fgAsyncWorkloads=1 from the ini, after 4 mode changes: %.2f presents per frame  [%s]\n",
                   asyncOn, asyncLine);
            fgAsyncWorkloads.Set(L"0");

            if (strstr(asyncLine, "async=yes") == nullptr)
                return Fail("fgAsyncWorkloads=1, but the context was not rebuilt for async workloads", E_FAIL);
            if (asyncOn < kGeneratingAtLeast)
                return Fail("fgAsyncWorkloads=1, but no frames were generated", E_FAIL);
            if (strstr(asyncLine, "device=ok") == nullptr)
                return Fail("the proxy's device did not survive async workloads", E_FAIL);
            if (strstr(asyncLine, "retired=0") == nullptr)
                return Fail("inputs replaced with async workloads were never released", E_FAIL);

            hr = renderFor(1.5f, settling, settlingFrames);
            if (FAILED(hr))
                return Fail("Present while the ini switches async workloads off", hr);
        }

        // Presents alone cannot show this one: FSR goes on generating frames
        // from a context made for the old size. Its documentation says the
        // input colour "needs to be displaySize", so the context has to follow.
        if (!contextFollowedResize)
            return Fail("the frame generation context did not follow the resize", E_FAIL);
    }

    // The load behind the proxy's "Load" line (LoadMonitor), read after two
    // seconds of this thread rendering flat out, so the last completed second
    // lies within them rather than in a paced phase before. Main and render
    // thread are the same here, so its CPU share cannot be near zero. The GPU
    // figures come from Windows' counters and may be unavailable (-1).
    if (shared)
    {
        LARGE_INTEGER ticksPerSecond = {};
        QueryPerformanceFrequency(&ticksPerSecond);
        LARGE_INTEGER start = {};
        QueryPerformanceCounter(&start);
        LARGE_INTEGER now = start;
        while (now.QuadPart - start.QuadPart < ticksPerSecond.QuadPart * 2)
        {
            hr = renderFrame();
            if (FAILED(hr))
                return Fail("Present while the load is measured", hr);
            QueryPerformanceCounter(&now);
        }

        float mainLoad = -1.0f;
        float renderLoad = -1.0f;
        float gpuLoad = -1.0f;
        float gpuOwnLoad = -1.0f;
        if (load(&mainLoad, &renderLoad, &gpuLoad, &gpuOwnLoad) != 1)
            return Fail("the proxy has measured no load", E_FAIL);
        printf("      load, last second: main thread %.0f %%, render thread %.0f %%, GPU %.0f %% (this process %.0f %%)\n",
               mainLoad, renderLoad, gpuLoad, gpuOwnLoad);
        if (mainLoad < 5.0f || renderLoad < 5.0f)
            return Fail("the proxy's thread load is implausible", E_FAIL);
    }

    // DLSS Super Resolution on this D3D11 device through NVIDIA's NGX, the way the
    // rig drives it: packets through the render event, Performance from
    // 320x180 to 640x360 with preset K, a flat image that has to come out flat.
    bool dlssTested = false;
    if (!dlssDirectory.empty())
    {
        const DlssStatusFn dlssStatus = reinterpret_cast<DlssStatusFn>(GetProcAddress(dxgi, "KspDlssStatus"));
        const DlssPacketSizeFn dlssPacketSize =
            reinterpret_cast<DlssPacketSizeFn>(GetProcAddress(dxgi, "KspDlssPacketSize"));
        const DlssSizesFn dlssSizes = reinterpret_cast<DlssSizesFn>(GetProcAddress(dxgi, "KspDlssRenderSizes2"));
        const GetRenderEventFn dlssEventFunc =
            reinterpret_cast<GetRenderEventFn>(GetProcAddress(dxgi, "KspFgGetRenderEventFunc"));
        if (dlssStatus == nullptr || dlssPacketSize == nullptr || dlssSizes == nullptr || dlssEventFunc == nullptr)
            return Fail("GetProcAddress(KspDlssStatus / KspDlssPacketSize / KspDlssRenderSizes2)", E_FAIL);
        if (dlssPacketSize() != sizeof(DlssPacket))
            return Fail("the DLSS packet layout differs between harness and proxy", E_FAIL);
        const RenderEventFn dlssEvent = static_cast<RenderEventFn>(dlssEventFunc());

        // On the NVIDIA adapter: the device above may be on another.
        ComPtr<ID3D11Device> dlssDevice;
        ComPtr<ID3D11DeviceContext> dlssContext;
        const ComPtr<IDXGIAdapter1> nvidia = NvidiaAdapter(factory.Get());
        if (nvidia == nullptr)
            return Fail("REDEFINITION_DLSS_DIR is set, but there is no NVIDIA adapter", E_FAIL);
        hr = D3D11CreateDevice(nvidia.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0,
                               D3D11_SDK_VERSION, &dlssDevice, nullptr, &dlssContext);
        if (FAILED(hr))
            return Fail("D3D11CreateDevice on the NVIDIA adapter", hr);

        constexpr UINT renderWidth = 320;
        constexpr UINT renderHeight = 180;
        const uint16_t grey[4] = { 0x3400, 0x3800, 0x3A00, 0x3C00 };   // 0.25, 0.5, 0.75, 1
        const uint16_t black[4] = { 0, 0, 0, 0x3C00 };
        ComPtr<ID3D11Texture2D> dlssColour;
        ComPtr<ID3D11Texture2D> dlssOutput;
        Inputs dlssInputs;
        hr = CreateHalfTexture(dlssDevice.Get(), renderWidth, renderHeight, grey, D3D11_BIND_SHADER_RESOURCE, dlssColour);
        if (SUCCEEDED(hr))
            hr = CreateHalfTexture(dlssDevice.Get(), kWidth, kHeight, black,
                                   D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, dlssOutput);
        if (SUCCEEDED(hr))
            hr = CreateInputs(dlssDevice.Get(), renderWidth, renderHeight, dlssInputs);
        if (FAILED(hr))
            return Fail("DLSS test textures", hr);

        // Every mode's render size at an output size, asked for before any
        // feature exists, as the mod asks before it builds the rig. Another output
        // size than the frames below use, so what they find is the feature's own.
        constexpr UINT queryWidth = 1280;
        constexpr UINT queryHeight = 720;
        DlssSizeQuery query = {};
        query.size = sizeof(query);
        query.magic = kDlssQueryMagic;
        query.texture = dlssColour.Get();
        query.outputWidth = queryWidth;
        query.outputHeight = queryHeight;
        dlssEvent(kDlssQueryEvent, &query);
        unsigned int asked[2] = {};
        unsigned int askedMinimum[2] = {};
        unsigned int askedMaximum[2] = {};
        if (dlssSizes(queryWidth, queryHeight, 2, asked, askedMinimum, askedMaximum) != 1)
            return Fail("the DLSS size query left no render size for Quality", E_FAIL);
        printf("      DLSS asked before any feature: Quality for %ux%u renders at %ux%u (%ux%u to %ux%u)\n", queryWidth,
               queryHeight, asked[0], asked[1], askedMinimum[0], askedMinimum[1], askedMaximum[0], askedMaximum[1]);

        const auto dlssFrameFor = [&](UINT frame, UINT width, UINT height)
        {
            DlssPacket dlssFrame = {};
            dlssFrame.size = sizeof(dlssFrame);
            dlssFrame.magic = kDlssMagic;
            dlssFrame.colour = dlssColour.Get();
            dlssFrame.output = dlssOutput.Get();
            dlssFrame.depth = dlssInputs.depth.Get();
            dlssFrame.motionVectors = dlssInputs.motion.Get();
            dlssFrame.renderWidth = width;
            dlssFrame.renderHeight = height;
            dlssFrame.outputWidth = kWidth;
            dlssFrame.outputHeight = kHeight;
            dlssFrame.quality = 0;
            dlssFrame.preset = 11;
            dlssFrame.flags = kDlssHdr | kDlssDepthInverted | kDlssAutoExposure | (frame == 0 ? kDlssReset : 0u);
            dlssFrame.jitterX = 0.25f * static_cast<float>(frame % 4) - 0.375f;
            dlssFrame.jitterY = 0.25f * static_cast<float>((frame / 4) % 4) - 0.375f;
            dlssFrame.motionVectorScaleX = static_cast<float>(width);
            dlssFrame.motionVectorScaleY = static_cast<float>(height);
            return dlssFrame;
        };

        char text[512] = {};
        for (UINT frame = 0; frame < 16; ++frame)
        {
            DlssPacket dlssFrame = dlssFrameFor(frame, renderWidth, renderHeight);
            dlssEvent(kDlssEvent, &dlssFrame);
            if (dlssStatus(text, sizeof(text)) < 0)
            {
                printf("      DLSS: %s\n", text);
                return Fail("DLSS did not run", E_FAIL);
            }
        }

        D3D11_TEXTURE2D_DESC stagingDesc = {};
        dlssOutput->GetDesc(&stagingDesc);
        stagingDesc.Width = 1;
        stagingDesc.Height = 1;
        stagingDesc.Usage = D3D11_USAGE_STAGING;
        stagingDesc.BindFlags = 0;
        stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging;
        hr = dlssDevice->CreateTexture2D(&stagingDesc, nullptr, &staging);
        if (FAILED(hr))
            return Fail("DLSS readback texture", hr);
        D3D11_BOX centre = { kWidth / 2, kHeight / 2, 0, kWidth / 2 + 1, kHeight / 2 + 1, 1 };
        dlssContext->CopySubresourceRegion(staging.Get(), 0, 0, 0, 0, dlssOutput.Get(), 0, &centre);
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        hr = dlssContext->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr))
            return Fail("DLSS readback", hr);
        const uint16_t* half = static_cast<const uint16_t*>(mapped.pData);
        const float red = HalfToFloat(half[0]);
        const float green = HalfToFloat(half[1]);
        const float blue = HalfToFloat(half[2]);
        dlssContext->Unmap(staging.Get(), 0);

        unsigned int optimal[2] = {};
        unsigned int minimum[2] = {};
        unsigned int maximum[2] = {};
        unsigned int never[2] = {};
        if (dlssSizes(kWidth, kHeight, 2, never, never, never) != 0)
            return Fail("the proxy reports DLSS render sizes for a mode that never ran at this output size", E_FAIL);
        if (dlssSizes(kWidth * 3, kHeight * 3, 0, never, never, never) != 0)
            return Fail("the proxy reports DLSS render sizes for an output size nobody asked for", E_FAIL);
        if (dlssSizes(kWidth, kHeight, 0, optimal, minimum, maximum) != 1)
            return Fail("the proxy kept no DLSS render sizes for the mode that ran", E_FAIL);
        dlssStatus(text, sizeof(text));
        printf("      DLSS: %s; Performance for %ux%u renders at %ux%u (%ux%u to %ux%u); output centre %.3f %.3f %.3f\n",
               text, kWidth, kHeight, optimal[0], optimal[1], minimum[0], minimum[1], maximum[0], maximum[1],
               red, green, blue);

        char cut[8] = {};
        dlssStatus(cut, sizeof(cut));
        if (lstrlenA(cut) != static_cast<int>(sizeof(cut)) - 1)
            return Fail("the DLSS status is not cut to the buffer it is given", E_FAIL);

        // A frame outside the range DLSS takes for the mode is refused, and the
        // next one inside it runs again.
        DlssPacket outside = dlssFrameFor(16, 8, 8);
        dlssEvent(kDlssEvent, &outside);
        const bool refused = dlssStatus(text, sizeof(text)) < 0;
        printf("      DLSS at 8x8: %s\n", text);
        DlssPacket inside = dlssFrameFor(17, renderWidth, renderHeight);
        dlssEvent(kDlssEvent, &inside);
        const bool recovered = dlssStatus(text, sizeof(text)) > 0;
        dlssEvent(kDlssReleaseEvent, nullptr);
        if (!refused || !recovered)
            return Fail("DLSS did not refuse a render size outside its range, or did not run again after it", E_FAIL);
        if (std::fabs(red - 0.25f) > 0.05f || std::fabs(green - 0.5f) > 0.05f || std::fabs(blue - 0.75f) > 0.05f)
            return Fail("the DLSS output is not the flat image it was given", E_FAIL);
        dlssTested = true;
    }
    else
    {
        printf("      DLSS skipped: REDEFINITION_DLSS_DIR names no folder with nvngx_dlss.dll\n");
    }

    // AMD's upscaler on the proxy's D3D12 device, the way the rig drives it:
    // packets through the render event, 320x180 to 640x360 over a flat image
    // that has to come out flat. On the adapter the proxy presents on, with
    // whichever version the DLL offers there; its name is in the status.
    bool amdTested = false;
    if (!amdDirectory.empty())
    {
        const DlssStatusFn amdStatus = reinterpret_cast<DlssStatusFn>(GetProcAddress(dxgi, "KspAmdUpscalerStatus"));
        const DlssPacketSizeFn amdPacketSize =
            reinterpret_cast<DlssPacketSizeFn>(GetProcAddress(dxgi, "KspAmdUpscalerPacketSize"));
        const GetRenderEventFn amdEventFunc =
            reinterpret_cast<GetRenderEventFn>(GetProcAddress(dxgi, "KspFgGetRenderEventFunc"));
        if (amdStatus == nullptr || amdPacketSize == nullptr || amdEventFunc == nullptr)
            return Fail("GetProcAddress(KspAmdUpscalerStatus / KspAmdUpscalerPacketSize)", E_FAIL);
        if (amdPacketSize() != sizeof(AmdPacket))
            return Fail("the AMD upscaler packet layout differs between harness and proxy", E_FAIL);
        using ReadyFn = int (*)();
        const ReadyFn amdReady = reinterpret_cast<ReadyFn>(GetProcAddress(dxgi, "KspAmdUpscalerReady"));
        if (amdReady == nullptr || amdReady() != 1)
            return Fail("the proxy does not report AMD's upscaler ready on its D3D12 device", E_FAIL);
        const RenderEventFn amdEvent = static_cast<RenderEventFn>(amdEventFunc());

        constexpr UINT amdRenderWidth = 320;
        constexpr UINT amdRenderHeight = 180;
        const uint16_t amdGrey[4] = { 0x3400, 0x3800, 0x3A00, 0x3C00 };   // 0.25, 0.5, 0.75, 1
        const uint16_t amdBlack[4] = { 0, 0, 0, 0x3C00 };
        ComPtr<ID3D11Texture2D> amdColour;
        ComPtr<ID3D11Texture2D> amdOutput;
        Inputs amdInputs;
        hr = CreateHalfTexture(device.Get(), amdRenderWidth, amdRenderHeight, amdGrey, D3D11_BIND_SHADER_RESOURCE, amdColour);
        if (SUCCEEDED(hr))
            hr = CreateHalfTexture(device.Get(), kWidth, kHeight, amdBlack,
                                   D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, amdOutput);
        if (SUCCEEDED(hr))
            hr = CreateInputs(device.Get(), amdRenderWidth, amdRenderHeight, amdInputs);
        if (FAILED(hr))
            return Fail("AMD upscaler test textures", hr);

        char amdText[512] = {};
        for (UINT frame = 0; frame < 16; ++frame)
        {
            AmdPacket amdFrame = {};
            amdFrame.size = sizeof(amdFrame);
            amdFrame.magic = kAmdMagic;
            amdFrame.colour = amdColour.Get();
            amdFrame.output = amdOutput.Get();
            amdFrame.depth = amdInputs.depth.Get();
            amdFrame.motionVectors = amdInputs.motion.Get();
            amdFrame.renderWidth = amdRenderWidth;
            amdFrame.renderHeight = amdRenderHeight;
            amdFrame.outputWidth = kWidth;
            amdFrame.outputHeight = kHeight;
            amdFrame.flags = kDlssHdr | kDlssDepthInverted | kDlssAutoExposure | (frame == 0 ? kDlssReset : 0u);
            amdFrame.jitterX = 0.25f * static_cast<float>(frame % 4) - 0.375f;
            amdFrame.jitterY = 0.25f * static_cast<float>((frame / 4) % 4) - 0.375f;
            amdFrame.motionVectorScaleX = -static_cast<float>(amdRenderWidth);
            amdFrame.motionVectorScaleY = -static_cast<float>(amdRenderHeight);
            amdFrame.sharpness = 0.0f;
            amdFrame.frameTimeDeltaMs = 16.7f;
            amdFrame.cameraNear = 0.1f;
            amdFrame.cameraFar = 1000.0f;
            amdFrame.verticalFovRadians = 1.0f;
            amdEvent(kAmdEvent, &amdFrame);
            if (amdStatus(amdText, sizeof(amdText)) < 0)
            {
                printf("      AMD upscaler: %s\n", amdText);
                return Fail("AMD's upscaler did not run", E_FAIL);
            }
        }

        D3D11_TEXTURE2D_DESC amdStagingDesc = {};
        amdOutput->GetDesc(&amdStagingDesc);
        amdStagingDesc.Width = 1;
        amdStagingDesc.Height = 1;
        amdStagingDesc.Usage = D3D11_USAGE_STAGING;
        amdStagingDesc.BindFlags = 0;
        amdStagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> amdStaging;
        hr = device->CreateTexture2D(&amdStagingDesc, nullptr, &amdStaging);
        if (FAILED(hr))
            return Fail("AMD upscaler readback texture", hr);
        D3D11_BOX amdCentre = { kWidth / 2, kHeight / 2, 0, kWidth / 2 + 1, kHeight / 2 + 1, 1 };
        context->CopySubresourceRegion(amdStaging.Get(), 0, 0, 0, 0, amdOutput.Get(), 0, &amdCentre);
        D3D11_MAPPED_SUBRESOURCE amdMapped = {};
        hr = context->Map(amdStaging.Get(), 0, D3D11_MAP_READ, 0, &amdMapped);
        if (FAILED(hr))
            return Fail("AMD upscaler readback", hr);
        const uint16_t* amdHalf = static_cast<const uint16_t*>(amdMapped.pData);
        const float amdRed = HalfToFloat(amdHalf[0]);
        const float amdGreen = HalfToFloat(amdHalf[1]);
        const float amdBlue = HalfToFloat(amdHalf[2]);
        context->Unmap(amdStaging.Get(), 0);
        amdStatus(amdText, sizeof(amdText));
        printf("      AMD upscaler: %s; output centre %.3f %.3f %.3f\n", amdText, amdRed, amdGreen, amdBlue);
        char amdCut[8] = {};
        amdStatus(amdCut, sizeof(amdCut));
        amdEvent(kAmdReleaseEvent, nullptr);
        if (lstrlenA(amdCut) != static_cast<int>(sizeof(amdCut)) - 1)
            return Fail("the AMD upscaler's status is not cut to the buffer it is given", E_FAIL);
        if (std::fabs(amdRed - 0.25f) > 0.05f || std::fabs(amdGreen - 0.5f) > 0.05f || std::fabs(amdBlue - 0.75f) > 0.05f)
            return Fail("the AMD upscaler's output is not the flat image it was given", E_FAIL);
        amdTested = true;
    }
    else
    {
        printf("      AMD upscaler skipped: REDEFINITION_AMD_UPSCALER_DIR names no folder with the DLL\n");
    }

    // Direct3D 12 for mods (D3d12Compute.h), the way the managed side drives it: HLSL
    // compiled by the proxy -- from source, from a file with an #include, and source with an
    // error, whose message has to come back --, a compute pass built against the fixed root
    // signature from that DXBC and from DXIL where the build compiled HarnessPass.cso, each
    // with a dispatch whose result is back in the same frame and two whose results come a
    // dispatch later, read back on Direct3D 11.
    bool computeTested = false;
    bool dxilTested = false;
    bool exampleTested = false;
    if (shared)
    {
        using CapabilitiesFn = int (*)(int*, int*, int*, int*, int*);
        using PassStatusFn = int (*)(int, char*, int);
        const CapabilitiesFn capabilities =
            reinterpret_cast<CapabilitiesFn>(GetProcAddress(dxgi, "KspD3d12Capabilities"));
        const DlssPacketSizeFn createSize =
            reinterpret_cast<DlssPacketSizeFn>(GetProcAddress(dxgi, "KspD3d12CreatePacketSize"));
        const DlssPacketSizeFn dispatchSize =
            reinterpret_cast<DlssPacketSizeFn>(GetProcAddress(dxgi, "KspD3d12DispatchPacketSize"));
        const PassStatusFn passStatus = reinterpret_cast<PassStatusFn>(GetProcAddress(dxgi, "KspD3d12PassStatus"));
        const DlssStatusFn computeStatus = reinterpret_cast<DlssStatusFn>(GetProcAddress(dxgi, "KspD3d12Status"));
        const GetRenderEventFn computeEventFunc =
            reinterpret_cast<GetRenderEventFn>(GetProcAddress(dxgi, "KspFgGetRenderEventFunc"));
        if (capabilities == nullptr || createSize == nullptr || dispatchSize == nullptr || passStatus == nullptr
            || computeStatus == nullptr || computeEventFunc == nullptr)
            return Fail("GetProcAddress(KspD3d12Capabilities / PacketSize / PassStatus / Status)", E_FAIL);
        if (createSize() != sizeof(D3d12CreatePacket) || dispatchSize() != sizeof(D3d12DispatchPacket))
            return Fail("the Direct3D 12 packet layouts differ between harness and proxy", E_FAIL);

        int level = 0, model = 0, raytracing = 0, mesh = 0, variableRate = 0;
        if (capabilities(&level, &model, &raytracing, &mesh, &variableRate) != 1)
            return Fail("the proxy does not offer Direct3D 12 while it presents through it", E_FAIL);
        printf("      Direct3D 12 for mods: feature level 0x%X, shader model 0x%X, raytracing tier %d, mesh shader tier %d,"
               " variable rate shading tier %d\n", static_cast<unsigned>(level), static_cast<unsigned>(model), raytracing,
               mesh, variableRate);
        const RenderEventFn computeEvent = static_cast<RenderEventFn>(computeEventFunc());

        using CompileFn = int (*)(const char*, int, const wchar_t*, const char*, unsigned char*, int, int*, char*, int);
        const CompileFn compile = reinterpret_cast<CompileFn>(GetProcAddress(dxgi, "KspD3d12Compile"));
        if (compile == nullptr)
            return Fail("GetProcAddress(KspD3d12Compile)", E_FAIL);

        // The same shader as HarnessPass.hlsl.
        static const char source[] =
            "Texture2D<float4> input : register(t0);\n"
            "RWTexture2D<float4> output : register(u0);\n"
            "cbuffer Constants : register(b0) { float4 add; };\n"
            "[numthreads(8, 8, 1)]\n"
            "void main(uint3 id : SV_DispatchThreadID) { output[id.xy] = input[id.xy] * 2.0 + add; }\n";
        char messages[1024] = {};
        int needed = 0;
        if (compile(source, sizeof(source) - 1, nullptr, "main", nullptr, 0, &needed, messages, sizeof(messages)) != -1
            || needed <= 0)
            return Fail("the proxy's compiler did not ask for a larger buffer", E_FAIL);
        std::vector<uint8_t> bytecode(static_cast<size_t>(needed));
        int written = 0;
        if (compile(source, sizeof(source) - 1, nullptr, "main", bytecode.data(), needed, &written, messages,
                    sizeof(messages)) != 1 || written != needed)
        {
            printf("      compiler: %s\n", messages);
            return Fail("the proxy did not compile the harness's compute shader", E_FAIL);
        }

        static const char broken[] = "[numthreads(8, 8, 1)]\nvoid main(uint3 id : SV_DispatchThreadID) { undefinedName = 1; }\n";
        if (compile(broken, sizeof(broken) - 1, nullptr, "main", bytecode.data(), needed, &written, messages,
                    sizeof(messages)) != 0 || strstr(messages, "undefinedName") == nullptr)
        {
            printf("      compiler on broken source: %s\n", messages);
            return Fail("the proxy's compiler did not return the error of a broken shader", E_FAIL);
        }
        printf("      compiler on broken source: %s", messages);

        // From a file that includes another next to it.
        const std::wstring includePath = PathNextToExecutable(L"HarnessInclude.hlsli");
        const std::wstring filePath = PathNextToExecutable(L"HarnessFromFile.hlsl");
        const auto writeFile = [](const std::wstring& path, const char* text) -> bool
        {
            HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file == INVALID_HANDLE_VALUE)
                return false;
            DWORD done = 0;
            const BOOL ok = WriteFile(file, text, static_cast<DWORD>(strlen(text)), &done, nullptr);
            CloseHandle(file);
            return ok && done == strlen(text);
        };
        if (!writeFile(includePath, "static const float kFactor = 2.0;\n")
            || !writeFile(filePath, "#include \"HarnessInclude.hlsli\"\n"
                                    "Texture2D<float4> input : register(t0);\n"
                                    "RWTexture2D<float4> output : register(u0);\n"
                                    "cbuffer Constants : register(b0) { float4 add; };\n"
                                    "[numthreads(8, 8, 1)]\n"
                                    "void main(uint3 id : SV_DispatchThreadID) { output[id.xy] = input[id.xy] * kFactor + add; }\n"))
            return Fail("writing the harness's shader files", E_FAIL);
        std::vector<uint8_t> fromFile(65536);
        int fromFileSize = 0;
        const int fileResult = compile(nullptr, 0, filePath.c_str(), "main", fromFile.data(),
                                       static_cast<int>(fromFile.size()), &fromFileSize, messages, sizeof(messages));
        DeleteFileW(filePath.c_str());
        DeleteFileW(includePath.c_str());
        if (fileResult != 1)
        {
            printf("      compiler from file: %s\n", messages);
            return Fail("the proxy did not compile a shader file with an #include", E_FAIL);
        }
        fromFile.resize(static_cast<size_t>(fromFileSize));

        constexpr UINT computeSize = 64;
        const uint16_t computeGrey[4] = { 0x3400, 0x3800, 0x3A00, 0x3C00 };   // 0.25, 0.5, 0.75, 1
        const uint16_t computeBlack[4] = { 0, 0, 0, 0x3C00 };
        ComPtr<ID3D11Texture2D> computeInput;
        ComPtr<ID3D11Texture2D> computeOutput;
        hr = CreateHalfTexture(device.Get(), computeSize, computeSize, computeGrey, D3D11_BIND_SHADER_RESOURCE, computeInput);
        if (SUCCEEDED(hr))
            hr = CreateHalfTexture(device.Get(), computeSize, computeSize, computeBlack, D3D11_BIND_SHADER_RESOURCE,
                                   computeOutput);
        D3D11_TEXTURE2D_DESC computeStagingDesc = {};
        ComPtr<ID3D11Texture2D> computeStaging;
        if (SUCCEEDED(hr))
        {
            computeOutput->GetDesc(&computeStagingDesc);
            computeStagingDesc.Width = 1;
            computeStagingDesc.Height = 1;
            computeStagingDesc.Usage = D3D11_USAGE_STAGING;
            computeStagingDesc.BindFlags = 0;
            computeStagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            hr = device->CreateTexture2D(&computeStagingDesc, nullptr, &computeStaging);
        }
        if (FAILED(hr))
            return Fail("compute pass test textures", hr);

        const auto readRed = [&](float& red) -> HRESULT
        {
            D3D11_BOX centre = { computeSize / 2, computeSize / 2, 0, computeSize / 2 + 1, computeSize / 2 + 1, 1 };
            context->CopySubresourceRegion(computeStaging.Get(), 0, 0, 0, 0, computeOutput.Get(), 0, &centre);
            D3D11_MAPPED_SUBRESOURCE mapped = {};
            const HRESULT mapResult = context->Map(computeStaging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
            if (FAILED(mapResult))
                return mapResult;
            red = HalfToFloat(static_cast<const uint16_t*>(mapped.pData)[0]);
            context->Unmap(computeStaging.Get(), 0);
            return S_OK;
        };

        // One pass: built, dispatched three times, destroyed. 0 on success.
        const auto runPass = [&](int32_t pass, const void* code, size_t codeSize, const char* label) -> int
        {
            D3d12CreatePacket create = {};
            create.size = sizeof(create);
            create.magic = kD3d12CreateMagic;
            create.pass = pass;
            create.bytecodeSize = static_cast<uint32_t>(codeSize);
            create.bytecode = code;
            strcpy_s(create.name, label);
            computeEvent(kD3d12CreateEvent, &create);
            char text[256] = {};
            if (passStatus(pass, text, sizeof(text)) != 1)
            {
                printf("      compute pass from %s: %s\n", label, text);
                return Fail("the compute pass was not built", E_FAIL);
            }

            const auto dispatchCompute = [&](float add, bool nextFrame)
            {
                D3d12DispatchPacket dispatch = {};
                dispatch.size = sizeof(dispatch);
                dispatch.magic = kD3d12DispatchMagic;
                dispatch.pass = pass;
                dispatch.flags = nextFrame ? kD3d12NextFrame : 0u;
                dispatch.groupsX = computeSize / 8;
                dispatch.groupsY = computeSize / 8;
                dispatch.groupsZ = 1;
                dispatch.readCount = 1;
                dispatch.writeCount = 1;
                dispatch.read[0] = computeInput.Get();
                dispatch.write[0] = computeOutput.Get();
                const float constants[4] = { add, add, add, 0.0f };
                dispatch.constantsSize = sizeof(constants);
                memcpy(dispatch.constants, constants, sizeof(constants));
                computeEvent(kD3d12DispatchEvent, &dispatch);
            };

            // 0.25 * 2 + 0.1: back in the same frame.
            float red = 0.0f;
            dispatchCompute(0.1f, false);
            HRESULT read = readRed(red);
            const float sameFrame = red;
            // + 0.2 a frame later: not there after its own dispatch ...
            dispatchCompute(0.2f, true);
            if (SUCCEEDED(read))
                read = readRed(red);
            const float beforeNext = red;
            // ... but after the next one, which finishes a frame later itself.
            dispatchCompute(0.3f, true);
            if (SUCCEEDED(read))
                read = readRed(red);
            const float afterNext = red;
            if (FAILED(read))
                return Fail("compute pass readback", read);

            computeStatus(text, sizeof(text));
            printf("      compute pass from %s: [%s]; red %.3f in the same frame, %.3f before and %.3f after the next"
                   " dispatch\n", label, text, sameFrame, beforeNext, afterNext);

            D3d12HandlePacket handle = {};
            handle.size = sizeof(handle);
            handle.magic = kD3d12HandleMagic;
            handle.pass = pass;
            computeEvent(kD3d12DestroyEvent, &handle);
            if (passStatus(pass, nullptr, 0) != -1)
                return Fail("a destroyed compute pass is still reported", E_FAIL);

            if (std::fabs(sameFrame - 0.6f) > 0.02f)
                return Fail("the compute pass's result is not back in the same frame", E_FAIL);
            if (std::fabs(beforeNext - 0.6f) > 0.02f)
                return Fail("a dispatch for the next frame wrote its result at once", E_FAIL);
            if (std::fabs(afterNext - 0.7f) > 0.02f)
                return Fail("a dispatch for the next frame did not bring its result with the next dispatch", E_FAIL);
            return 0;
        };

        if (int failed = runPass(1, bytecode.data(), bytecode.size(), "DXBC from source"))
            return failed;
        if (int failed = runPass(3, fromFile.data(), fromFile.size(), "DXBC from a file"))
            return failed;
        computeTested = true;

        const std::wstring dxilPath = PathNextToExecutable(L"HarnessPass.cso");
        HANDLE dxilFile = CreateFileW(dxilPath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                      FILE_ATTRIBUTE_NORMAL, nullptr);
        if (dxilFile != INVALID_HANDLE_VALUE)
        {
            std::vector<uint8_t> dxil(static_cast<size_t>(GetFileSize(dxilFile, nullptr)));
            DWORD bytesRead = 0;
            const BOOL ok = ReadFile(dxilFile, dxil.data(), static_cast<DWORD>(dxil.size()), &bytesRead, nullptr);
            CloseHandle(dxilFile);
            if (!ok || bytesRead != dxil.size())
                return Fail("reading HarnessPass.cso", E_FAIL);
            if (int failed = runPass(2, dxil.data(), dxil.size(), "DXIL"))
                return failed;
            dxilTested = true;
        }
        else
        {
            printf("      DXIL compute pass skipped: no HarnessPass.cso next to the harness (the build compiles it with"
                   " the Windows SDK's dxc)\n");
        }

        // The example mod's shader (docs/modders/examples/ReDefinitionExample), compiled and built
        // the way the example loads it.
        if (FileNextToExecutable(L"ExampleEffect.hlsl"))
        {
            std::vector<uint8_t> example(65536);
            int exampleSize = 0;
            if (compile(nullptr, 0, PathNextToExecutable(L"ExampleEffect.hlsl").c_str(), "main", example.data(),
                        static_cast<int>(example.size()), &exampleSize, messages, sizeof(messages)) != 1)
            {
                printf("      compiler on ExampleEffect.hlsl: %s\n", messages);
                return Fail("the example mod's compute shader does not compile", E_FAIL);
            }
            D3d12CreatePacket create = {};
            create.size = sizeof(create);
            create.magic = kD3d12CreateMagic;
            create.pass = 4;
            create.bytecodeSize = static_cast<uint32_t>(exampleSize);
            create.bytecode = example.data();
            strcpy_s(create.name, "ExampleEffect");
            computeEvent(kD3d12CreateEvent, &create);
            char exampleText[256] = {};
            const int exampleState = passStatus(4, exampleText, sizeof(exampleText));
            D3d12HandlePacket destroy = {};
            destroy.size = sizeof(destroy);
            destroy.magic = kD3d12HandleMagic;
            destroy.pass = 4;
            computeEvent(kD3d12DestroyEvent, &destroy);
            if (exampleState != 1)
            {
                printf("      ExampleEffect: %s\n", exampleText);
                return Fail("the example mod's compute shader was not built", E_FAIL);
            }
            printf("      the example mod's ExampleEffect.hlsl compiled and built\n");
            exampleTested = true;
        }

        D3d12HandlePacket release = {};
        release.size = sizeof(release);
        release.magic = kD3d12HandleMagic;
        release.texture = computeInput.Get();
        computeEvent(kD3d12ReleaseEvent, &release);
        release.texture = computeOutput.Get();
        computeEvent(kD3d12ReleaseEvent, &release);
    }

    // Whether frames can be generated follows the FidelityFX swapchain: yes while
    // it exists, no once it is released (KspFgCanGenerate).
    using CanGenerateFn = int (*)();
    const CanGenerateFn canGenerate = reinterpret_cast<CanGenerateFn>(GetProcAddress(dxgi, "KspFgCanGenerate"));
    if (canGenerate == nullptr)
        return Fail("GetProcAddress(KspFgCanGenerate)", E_FAIL);
    if ((frameGeneration || dlssGenerated) && canGenerate() != 1)
        return Fail("the proxy does not report that it can generate while its generating swapchain exists", E_FAIL);
    const CanGenerateFn contextFailing = reinterpret_cast<CanGenerateFn>(GetProcAddress(dxgi, "KspFgContextFailing"));
    if (contextFailing == nullptr)
        return Fail("GetProcAddress(KspFgContextFailing)", E_FAIL);
    if (frameGeneration && contextFailing() != 0)
        return Fail("the proxy reports a failing frame generation context although it generated", E_FAIL);
    // The player's retry leaves a context that was made as it is.
    using RetryContextFn = void (*)();
    const RetryContextFn retryContext = reinterpret_cast<RetryContextFn>(GetProcAddress(dxgi, "KspFgRetryContext"));
    if (retryContext == nullptr)
        return Fail("GetProcAddress(KspFgRetryContext)", E_FAIL);
    retryContext();
    if (frameGeneration)
    {
        char retried[256] = {};
        status(retried, sizeof(retried));
        printf("      after the player's retry: [%s]\n", retried);
        if (strstr(retried, "context=no ") != nullptr || contextFailing() != 0)
            return Fail("the player's retry did away with a frame generation context that was made", E_FAIL);
    }

    // The mod's request for a check at once is there to be asked (KspFgRequestCheck).
    using RequestCheckFn = void (*)();
    const RequestCheckFn requestCheck = reinterpret_cast<RequestCheckFn>(GetProcAddress(dxgi, "KspFgRequestCheck"));
    if (requestCheck == nullptr)
        return Fail("GetProcAddress(KspFgRequestCheck)", E_FAIL);
    requestCheck();

    // Release in order, then confirm the process survives teardown: a device
    // removed on shutdown fails the run.
    ui.Reset();
    feature.Reset();
    hudLessCopy.Reset();
    inputs.depth.Reset();
    inputs.motion.Reset();
    rtv.Reset();
    backBuffer.Reset();
    swapChain2.Reset();
    swapChain.Reset();
    if (canGenerate() != 0)
        return Fail("the proxy still reports that it can generate after its swapchain was released", E_FAIL);
    context.Reset();
    device.Reset();
    factory.Reset();
    DestroyWindow(window);

    printf("PASS  proxy survived creation, %u presents and teardown%s%s\n", frameNumber,
           lastCheck != nullptr ? "; the HUD-less copy differs by exactly the UI" : "",
           frameGeneration ? "; frame generation on, off, on again and across a resize" : "");
    if (dlssGenerated)
        printf("PASS  DLSS frame generation through Streamline: on, after mode changes, off, on again and across a "
               "resize\n");
    if (dlssTested)
        printf("PASS  DLSS evaluated through NGX on D3D11\n");
    if (amdTested)
        printf("PASS  AMD's upscaler ran on the proxy's D3D12 device\n");
    if (computeTested)
        printf("PASS  a mod's compute pass ran on the proxy's D3D12 device, its result back in the same frame and a "
               "dispatch later, compiled by the proxy from source and from a file%s%s\n", dxilTested ? ", and from DXIL" : "",
               exampleTested ? "; the example mod's shader builds" : "");
    return 0;
}
