// The screen recorder (ScreenRecorder.h).
//
// Desktop duplication, from Microsoft's documentation: IDXGIOutput1::DuplicateOutput
// on a device of the adapter the monitor hangs on; AcquireNextFrame hands the desktop
// image as the monitor gets it, with LastPresentTime zero where only the mouse moved,
// and AccumulatedFrames above one where frames were composed that this loop did not
// see. Each frame is copied on the GPU into a staging texture of its own and read
// back only once the recording is over, so the loop keeps pace with the display.

#include "ScreenRecorder.h"

#include "FileUtil.h"
#include "Log.h"

#include <d3d11.h>
#include <dxgi1_2.h>
#include <dxgi1_6.h>
#include <wincodec.h>
#include <windows.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        constexpr UINT kCutWidth = 1600;
        constexpr UINT kCutHeight = 900;
        constexpr int kMaxFrames = 120;

        std::atomic<bool> g_running{ false };

        // The game's frames, where the desktop's copy is refused (RecordBackBuffer).
        struct BackBufferFrame
        {
            ComPtr<ID3D11Texture2D> staging;
            LONGLONG presentTime = 0;
            std::vector<uint8_t> pixels;
        };
        std::mutex g_backLock;
        int g_backWanted = 0;
        size_t g_backRead = 0;
        std::wstring g_backFolder;
        std::vector<BackBufferFrame> g_backFrames;
        DXGI_FORMAT g_backFormat = DXGI_FORMAT_UNKNOWN;
        UINT g_backWidth = 0, g_backHeight = 0;
        std::mutex g_stateLock;
        std::string g_state = "never started";

        void SetState(const std::string& state)
        {
            std::lock_guard<std::mutex> lock(g_stateLock);
            g_state = state;
        }

        float HalfToFloat(uint16_t h)
        {
            const uint32_t sign = (h >> 15) & 1u, exponent = (h >> 10) & 0x1Fu, mantissa = h & 0x3FFu;
            float value;
            if (exponent == 0) value = std::ldexp(static_cast<float>(mantissa), -24);
            else if (exponent == 31) value = mantissa ? 0.0f : 65504.0f;
            else value = std::ldexp(static_cast<float>(mantissa | 0x400u), static_cast<int>(exponent) - 25);
            return sign ? -value : value;
        }

        uint8_t Encode(float linear)
        {
            linear = std::min(std::max(linear, 0.0f), 1.0f);
            const float s = linear <= 0.0031308f ? linear * 12.92f : 1.055f * std::pow(linear, 1.0f / 2.4f) - 0.055f;
            return static_cast<uint8_t>(s * 255.0f + 0.5f);
        }

        // A row of the desktop in BGRA8 from the format duplication hands it in: an HDR
        // desktop (scRGB half floats, linear with 1.0 at 80 nits) or a 10-bit one.
        void ToBgra8(DXGI_FORMAT format, const uint8_t* source, uint8_t* target, UINT width)
        {
            for (UINT x = 0; x < width; ++x)
            {
                uint8_t* out = target + x * 4;
                if (format == DXGI_FORMAT_R16G16B16A16_FLOAT)
                {
                    const uint16_t* c = reinterpret_cast<const uint16_t*>(source) + x * 4;
                    out[2] = Encode(HalfToFloat(c[0]));
                    out[1] = Encode(HalfToFloat(c[1]));
                    out[0] = Encode(HalfToFloat(c[2]));
                    out[3] = 255;
                }
                else if (format == DXGI_FORMAT_R10G10B10A2_UNORM)
                {
                    const uint32_t c = reinterpret_cast<const uint32_t*>(source)[x];
                    out[2] = static_cast<uint8_t>((c & 0x3FFu) >> 2);
                    out[1] = static_cast<uint8_t>(((c >> 10) & 0x3FFu) >> 2);
                    out[0] = static_cast<uint8_t>(((c >> 20) & 0x3FFu) >> 2);
                    out[3] = 255;
                }
                else
                {
                    std::memcpy(out, source + x * 4, 4);
                }
            }
        }

        bool WritePng(IWICImagingFactory* wic, const std::wstring& path, const uint8_t* pixels, UINT width,
                      UINT height, UINT pitch)
        {
            ComPtr<IWICStream> stream;
            ComPtr<IWICBitmapEncoder> encoder;
            ComPtr<IWICBitmapFrameEncode> frame;
            if (FAILED(wic->CreateStream(&stream)) || FAILED(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE))
                || FAILED(wic->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder))
                || FAILED(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache))
                || FAILED(encoder->CreateNewFrame(&frame, nullptr)) || FAILED(frame->Initialize(nullptr))
                || FAILED(frame->SetSize(width, height)))
                return false;
            WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
            if (FAILED(frame->SetPixelFormat(&format)) || format != GUID_WICPixelFormat32bppBGRA)
                return false;
            return SUCCEEDED(frame->WritePixels(height, pitch, pitch * height, const_cast<BYTE*>(pixels)))
                   && SUCCEEDED(frame->Commit()) && SUCCEEDED(encoder->Commit());
        }

        void Record(int frames, std::wstring folder, HWND window)
        {
            HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            const bool coInit = SUCCEEDED(hr);
            std::string failure;
            do
            {
                ComPtr<ID3D11Device> device;
                ComPtr<ID3D11DeviceContext> context;
                if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
                                             D3D11_SDK_VERSION, &device, nullptr, &context)))
                {
                    failure = "no Direct3D 11 device for the recording";
                    break;
                }
                ComPtr<IDXGIDevice> dxgiDevice;
                ComPtr<IDXGIAdapter> adapter;
                device.As(&dxgiDevice);
                if (!dxgiDevice || FAILED(dxgiDevice->GetAdapter(&adapter)))
                {
                    failure = "no adapter";
                    break;
                }

                // The output the game window is on.
                HMONITOR monitor = MonitorFromWindow(window, MONITOR_DEFAULTTOPRIMARY);
                ComPtr<IDXGIOutput> output;
                DXGI_OUTPUT_DESC outputDesc = {};
                for (UINT i = 0;; ++i)
                {
                    ComPtr<IDXGIOutput> candidate;
                    if (adapter->EnumOutputs(i, &candidate) == DXGI_ERROR_NOT_FOUND) break;
                    DXGI_OUTPUT_DESC desc = {};
                    candidate->GetDesc(&desc);
                    if (desc.Monitor == monitor)
                    {
                        output = candidate;
                        outputDesc = desc;
                        break;
                    }
                }
                ComPtr<IDXGIOutput1> output1;
                if (!output || FAILED(output.As(&output1)))
                {
                    failure = "the game's monitor is not on the default adapter";
                    break;
                }
                ComPtr<IDXGIOutputDuplication> duplication;
                hr = output1->DuplicateOutput(device.Get(), &duplication);
                // DXGI_ERROR_UNSUPPORTED where the desktop is in a mode DuplicateOutput
                // does not hand out, an HDR or 10-bit one: DuplicateOutput1 takes the
                // formats the caller can read (IDXGIOutput5::DuplicateOutput1).
                ComPtr<IDXGIOutput5> output5;
                if (hr == DXGI_ERROR_UNSUPPORTED && SUCCEEDED(output.As(&output5)))
                {
                    const DXGI_FORMAT formats[] = { DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_R10G10B10A2_UNORM,
                                                    DXGI_FORMAT_R16G16B16A16_FLOAT };
                    hr = output5->DuplicateOutput1(device.Get(), 0, ARRAYSIZE(formats), formats, &duplication);
                }
                // The proxy harness checks the game's-frames path with this set.
                if (GetEnvironmentVariableW(L"REDEFINITION_RECORD_GAME_FRAMES", nullptr, 0) > 0)
                {
                    duplication.Reset();
                    hr = DXGI_ERROR_UNSUPPORTED;
                }
                if (FAILED(hr))
                {
                    // The game's own frames instead, taken at its Presents.
                    char text[160];
                    std::snprintf(text, sizeof(text), "desktop duplication refused (0x%08lX): the game's frames without frame"
                                  " generation's", static_cast<unsigned long>(hr));
                    LogLine(std::string("Screen recording: ") + text);
                    {
                        std::lock_guard<std::mutex> lock(g_backLock);
                        g_backFrames.clear();
                        g_backRead = 0;
                        g_backFolder = folder;
                        g_backWanted = frames;
                    }
                    SetState(std::string("recording, ") + text);
                    if (coInit) CoUninitialize();
                    // The game's frames come only through the D3D12 proxy's Present with
                    // frame generation attached; none within two seconds, and there is no
                    // such Present.
                    Sleep(2000);
                    std::lock_guard<std::mutex> lock(g_backLock);
                    if (g_backWanted == frames && g_backFrames.empty())
                    {
                        g_backWanted = 0;
                        SetState("failed: desktop duplication refused, and no game frame reached the proxy's Present"
                                 " (it takes them only while frame generation is attached)");
                        LogLine("Screen recording: no game frame within two seconds, stopped");
                        g_running = false;
                    }
                    return;
                }

                // The cut-out around the middle of the window's client area, in the
                // output's coordinates.
                RECT client = {};
                GetClientRect(window, &client);
                POINT topLeft = { client.left, client.top };
                ClientToScreen(window, &topLeft);
                const LONG centreX = topLeft.x + (client.right - client.left) / 2 - outputDesc.DesktopCoordinates.left;
                const LONG centreY = topLeft.y + (client.bottom - client.top) / 2 - outputDesc.DesktopCoordinates.top;
                const LONG outputWidth = outputDesc.DesktopCoordinates.right - outputDesc.DesktopCoordinates.left;
                const LONG outputHeight = outputDesc.DesktopCoordinates.bottom - outputDesc.DesktopCoordinates.top;
                const UINT width = static_cast<UINT>(std::min<LONG>(kCutWidth, outputWidth));
                const UINT height = static_cast<UINT>(std::min<LONG>(kCutHeight, outputHeight));
                const LONG left = std::max<LONG>(0, std::min<LONG>(centreX - static_cast<LONG>(width) / 2, outputWidth - width));
                const LONG top = std::max<LONG>(0, std::min<LONG>(centreY - static_cast<LONG>(height) / 2, outputHeight - height));
                const D3D11_BOX box = { static_cast<UINT>(left), static_cast<UINT>(top), 0,
                                        static_cast<UINT>(left) + width, static_cast<UINT>(top) + height, 1 };

                DXGI_OUTDUPL_DESC duplicationDesc = {};
                duplication->GetDesc(&duplicationDesc);
                const DXGI_FORMAT desktopFormat = duplicationDesc.ModeDesc.Format == DXGI_FORMAT_UNKNOWN
                                                      ? DXGI_FORMAT_B8G8R8A8_UNORM : duplicationDesc.ModeDesc.Format;
                std::vector<uint8_t> converted;

                // Made before the first frame, so the loop only copies: a texture made
                // per frame costs a composed frame now and then.
                std::vector<ComPtr<ID3D11Texture2D>> pool;
                {
                    D3D11_TEXTURE2D_DESC desc = {};
                    desc.Width = width;
                    desc.Height = height;
                    desc.MipLevels = 1;
                    desc.ArraySize = 1;
                    desc.Format = desktopFormat;
                    desc.SampleDesc.Count = 1;
                    desc.Usage = D3D11_USAGE_STAGING;
                    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                    for (int i = 0; i < frames; ++i)
                    {
                        ComPtr<ID3D11Texture2D> texture;
                        if (FAILED(device->CreateTexture2D(&desc, nullptr, &texture))) break;
                        pool.push_back(texture);
                    }
                }
                if (pool.empty())
                {
                    failure = "no memory for the frames";
                    break;
                }
                frames = static_cast<int>(pool.size());
                std::vector<ComPtr<ID3D11Texture2D>> staged;
                std::vector<LONGLONG> presentTimes;
                std::vector<UINT> accumulated;
                const ULONGLONG deadline = GetTickCount64() + 5000;
                while (static_cast<int>(staged.size()) < frames && GetTickCount64() < deadline)
                {
                    DXGI_OUTDUPL_FRAME_INFO info = {};
                    ComPtr<IDXGIResource> resource;
                    hr = duplication->AcquireNextFrame(250, &info, &resource);
                    if (hr == DXGI_ERROR_WAIT_TIMEOUT) continue;
                    if (FAILED(hr))
                    {
                        char text[96];
                        std::snprintf(text, sizeof(text), "a frame could not be taken (0x%08lX)", static_cast<unsigned long>(hr));
                        failure = text;
                        break;
                    }
                    if (info.LastPresentTime.QuadPart != 0)
                    {
                        ComPtr<ID3D11Texture2D> desktop;
                        resource.As(&desktop);
                        ComPtr<ID3D11Texture2D> copy = pool[staged.size()];
                        context->CopySubresourceRegion(copy.Get(), 0, 0, 0, 0, desktop.Get(), 0, &box);
                        staged.push_back(copy);
                        presentTimes.push_back(info.LastPresentTime.QuadPart);
                        accumulated.push_back(info.AccumulatedFrames);
                    }
                    duplication->ReleaseFrame();
                }
                if (!failure.empty()) break;
                if (staged.empty())
                {
                    failure = "no frame came within five seconds";
                    break;
                }

                // The frames are taken; writing them takes a few seconds more.
                SetState("saving " + std::to_string(staged.size()) + " frame(s), the recording itself is over");
                CreateDirectoryW(folder.c_str(), nullptr);
                ComPtr<IWICImagingFactory> wic;
                if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic))))
                {
                    failure = "no image encoder";
                    break;
                }
                LARGE_INTEGER frequency = {};
                QueryPerformanceFrequency(&frequency);
                std::string table = "frame presentTimeMs sinceBeforeMs accumulatedFrames\n";
                for (size_t i = 0; i < staged.size(); ++i)
                {
                    D3D11_MAPPED_SUBRESOURCE mapped = {};
                    if (FAILED(context->Map(staged[i].Get(), 0, D3D11_MAP_READ, 0, &mapped))) continue;
                    wchar_t name[32];
                    swprintf_s(name, L"\\%03u.png", static_cast<unsigned>(i));
                    const uint8_t* pixels = static_cast<const uint8_t*>(mapped.pData);
                    UINT pitch = mapped.RowPitch;
                    if (desktopFormat != DXGI_FORMAT_B8G8R8A8_UNORM)
                    {
                        converted.resize(static_cast<size_t>(width) * height * 4);
                        for (UINT y = 0; y < height; ++y)
                            ToBgra8(desktopFormat, pixels + static_cast<size_t>(y) * mapped.RowPitch,
                                    converted.data() + static_cast<size_t>(y) * width * 4, width);
                        pixels = converted.data();
                        pitch = width * 4;
                    }
                    WritePng(wic.Get(), folder + name, pixels, width, height, pitch);
                    context->Unmap(staged[i].Get(), 0);
                    const double ms = presentTimes[i] * 1000.0 / static_cast<double>(frequency.QuadPart);
                    const double since = i == 0 ? 0.0
                        : (presentTimes[i] - presentTimes[i - 1]) * 1000.0 / static_cast<double>(frequency.QuadPart);
                    char line[128];
                    std::snprintf(line, sizeof(line), "%03u %.3f %.3f %u\n", static_cast<unsigned>(i), ms, since,
                                  accumulated[i]);
                    table += line;
                }
                FILE* file = nullptr;
                if (_wfopen_s(&file, (folder + L"\\frames.txt").c_str(), L"wb") == 0 && file != nullptr)
                {
                    std::fwrite(table.data(), 1, table.size(), file);
                    std::fclose(file);
                }
                char done[160];
                std::snprintf(done, sizeof(done), "%u frame(s) of %ux%u", static_cast<unsigned>(staged.size()), width, height);
                SetState("done: " + Narrow(folder) + " (" + done + ")");
                LogLine("Screen recording: " + std::string(done) + " in " + Narrow(folder));
            } while (false);

            if (!failure.empty())
            {
                SetState("failed: " + failure);
                LogLine("Screen recording failed: " + failure);
            }
            if (coInit) CoUninitialize();
            g_running = false;
        }
    }

    bool StartScreenRecording(int frames, const std::wstring& folder)
    {
        bool expected = false;
        if (!g_running.compare_exchange_strong(expected, true)) return false;
        if (frames < 1) frames = 1;
        if (frames > kMaxFrames) frames = kMaxFrames;
        HWND window = GetForegroundWindow();
        SetState("recording");
        LogLine("Screen recording started: " + std::to_string(frames) + " frame(s)");
        std::thread(Record, frames, folder, window).detach();
        return true;
    }

    void RecordBackBuffer(ID3D11DeviceContext* context, ID3D11Texture2D* backBuffer)
    {
        std::unique_lock<std::mutex> lock(g_backLock);
        if (context == nullptr || backBuffer == nullptr || (g_backWanted == 0 && g_backFrames.empty()))
            return;
        if (g_backWanted > 0)
        {
            D3D11_TEXTURE2D_DESC desc = {};
            backBuffer->GetDesc(&desc);
            // A backbuffer of another size or format than the frames before ends the
            // capture: they are all read back and written as the first one is.
            if (!g_backFrames.empty()
                && (desc.Width != g_backWidth || desc.Height != g_backHeight || desc.Format != g_backFormat))
            {
                g_backWanted = 0;
                return;
            }
            desc.Usage = D3D11_USAGE_STAGING;
            desc.BindFlags = 0;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            desc.MiscFlags = 0;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.SampleDesc.Count = 1;
            ComPtr<ID3D11Device> device;
            context->GetDevice(&device);
            BackBufferFrame frame;
            if (!device || FAILED(device->CreateTexture2D(&desc, nullptr, &frame.staging)))
            {
                g_backWanted = 0;
                g_backFrames.clear();
                SetState("failed: no staging texture for the game's frames");
                g_running = false;
                return;
            }
            context->CopyResource(frame.staging.Get(), backBuffer);
            LARGE_INTEGER now = {};
            QueryPerformanceCounter(&now);
            frame.presentTime = now.QuadPart;
            g_backFormat = desc.Format;
            g_backWidth = desc.Width;
            g_backHeight = desc.Height;
            g_backFrames.push_back(std::move(frame));
            --g_backWanted;
            return;
        }

        // All taken: one read back a Present, without waiting for the GPU.
        if (g_backRead < g_backFrames.size())
        {
            BackBufferFrame& frame = g_backFrames[g_backRead];
            D3D11_MAPPED_SUBRESOURCE mapped = {};
            if (context->Map(frame.staging.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped) != S_OK)
                return;
            const UINT rowBytes = g_backWidth * 4;
            frame.pixels.resize(static_cast<size_t>(rowBytes) * g_backHeight);
            for (UINT y = 0; y < g_backHeight; ++y)
            {
                const uint8_t* row = static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch;
                uint8_t* out = frame.pixels.data() + static_cast<size_t>(y) * rowBytes;
                const bool rgba = g_backFormat == DXGI_FORMAT_R8G8B8A8_UNORM || g_backFormat == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
                if (g_backFormat == DXGI_FORMAT_R16G16B16A16_FLOAT || g_backFormat == DXGI_FORMAT_R10G10B10A2_UNORM)
                {
                    ToBgra8(g_backFormat, row, out, g_backWidth);
                }
                else
                {
                    for (UINT x = 0; x < g_backWidth; ++x)
                    {
                        out[x * 4 + 0] = row[x * 4 + (rgba ? 2 : 0)];
                        out[x * 4 + 1] = row[x * 4 + 1];
                        out[x * 4 + 2] = row[x * 4 + (rgba ? 0 : 2)];
                        out[x * 4 + 3] = 255;
                    }
                }
            }
            context->Unmap(frame.staging.Get(), 0);
            frame.staging.Reset();
            ++g_backRead;
            return;
        }

        // All read: written in a thread of their own.
        std::vector<BackBufferFrame> frames = std::move(g_backFrames);
        g_backFrames.clear();
        g_backRead = 0;
        const std::wstring folder = g_backFolder;
        const UINT width = g_backWidth, height = g_backHeight;
        lock.unlock();
        SetState("saving " + std::to_string(frames.size()) + " frame(s), the recording itself is over");
        std::thread([frames = std::move(frames), folder, width, height]() {
            const HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            CreateDirectoryW(folder.c_str(), nullptr);
            ComPtr<IWICImagingFactory> wic;
            CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic));
            LARGE_INTEGER frequency = {};
            QueryPerformanceFrequency(&frequency);
            std::string table = "frame presentTimeMs sinceBeforeMs accumulatedFrames\n";
            for (size_t i = 0; i < frames.size() && wic; ++i)
            {
                wchar_t name[32];
                swprintf_s(name, L"\\%03u.png", static_cast<unsigned>(i));
                WritePng(wic.Get(), folder + name, frames[i].pixels.data(), width, height, width * 4);
                const double ms = frames[i].presentTime * 1000.0 / static_cast<double>(frequency.QuadPart);
                const double since = i == 0 ? 0.0
                    : (frames[i].presentTime - frames[i - 1].presentTime) * 1000.0 / static_cast<double>(frequency.QuadPart);
                char line[128];
                std::snprintf(line, sizeof(line), "%03u %.3f %.3f 1\n", static_cast<unsigned>(i), ms, since);
                table += line;
            }
            FILE* file = nullptr;
            if (_wfopen_s(&file, (folder + L"\\frames.txt").c_str(), L"wb") == 0 && file != nullptr)
            {
                std::fwrite(table.data(), 1, table.size(), file);
                std::fclose(file);
            }
            char done[160];
            std::snprintf(done, sizeof(done), "%u game frame(s) of %ux%u, frame generation's not among them",
                          static_cast<unsigned>(frames.size()), width, height);
            SetState("done: " + Narrow(folder) + " (" + done + ")");
            LogLine("Screen recording: " + std::string(done) + " in " + Narrow(folder));
            if (SUCCEEDED(hr)) CoUninitialize();
            g_running = false;
        }).detach();
    }

    std::string ScreenRecordingState()
    {
        std::lock_guard<std::mutex> lock(g_stateLock);
        return g_state;
    }
}
