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
#include <wincodec.h>
#include <windows.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <cstdio>
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
        std::mutex g_stateLock;
        std::string g_state = "never started";

        void SetState(const std::string& state)
        {
            std::lock_guard<std::mutex> lock(g_stateLock);
            g_state = state;
        }

        std::string Narrow(const std::wstring& text)
        {
            std::string out;
            for (wchar_t c : text) out += c < 128 ? static_cast<char>(c) : '?';
            return out;
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
                if (FAILED(hr))
                {
                    char text[96];
                    std::snprintf(text, sizeof(text), "desktop duplication refused (0x%08lX)", static_cast<unsigned long>(hr));
                    failure = text;
                    break;
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

                // Made before the first frame, so the loop only copies: a texture made
                // per frame costs a composed frame now and then.
                std::vector<ComPtr<ID3D11Texture2D>> pool;
                {
                    D3D11_TEXTURE2D_DESC desc = {};
                    desc.Width = width;
                    desc.Height = height;
                    desc.MipLevels = 1;
                    desc.ArraySize = 1;
                    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
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
                    WritePng(wic.Get(), folder + name, static_cast<const uint8_t*>(mapped.pData), width, height,
                             mapped.RowPitch);
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
        SetState("running");
        LogLine("Screen recording started: " + std::to_string(frames) + " frame(s)");
        std::thread(Record, frames, folder, window).detach();
        return true;
    }

    std::string ScreenRecordingState()
    {
        std::lock_guard<std::mutex> lock(g_stateLock);
        return g_state;
    }
}
