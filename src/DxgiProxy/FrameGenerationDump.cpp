// Frame generation's input dump (FrameGeneration): for a few consecutive rendered
// frames, the HUD-less colour, depth and motion vectors exactly as frame generation
// receives them, the frame as presented, and each frame's packet, written into a
// folder for analysis away from the game. The copies are taken on consecutive frames; they are read back and
// written only once all of them are taken, so writing cannot break the sequence.

#include "FrameGeneration.h"

#include "Log.h"

#include <cmath>
#include <cstdio>
#include <string>
#include <thread>

namespace redefinition
{
    namespace
    {
        void WriteFile(const std::wstring& path, const void* bytes, size_t size)
        {
            FILE* file = nullptr;
            if (_wfopen_s(&file, path.c_str(), L"wb") != 0 || file == nullptr)
                return;
            fwrite(bytes, 1, size, file);
            fclose(file);
        }

        UINT BytesPerTexel(DXGI_FORMAT format)
        {
            switch (format)
            {
            case DXGI_FORMAT_R16G16B16A16_FLOAT:
            case DXGI_FORMAT_R16G16B16A16_TYPELESS:
                return 8;
            default:
                return 4;   // R32 float, R16G16 float, 8-bit RGBA: the rig's formats
            }
        }

        // A staging texture for a copy of source; reused where it already has its size
        // and format.
        void EnsureStaging(ID3D11Device* device, ID3D11Texture2D* source, Microsoft::WRL::ComPtr<ID3D11Texture2D>& staging)
        {
            D3D11_TEXTURE2D_DESC desc = {};
            source->GetDesc(&desc);
            if (staging)
            {
                D3D11_TEXTURE2D_DESC have = {};
                staging->GetDesc(&have);
                if (have.Width == desc.Width && have.Height == desc.Height && have.Format == desc.Format)
                    return;
                staging.Reset();
            }
            desc.Usage = D3D11_USAGE_STAGING;
            desc.BindFlags = 0;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            desc.MiscFlags = 0;
            device->CreateTexture2D(&desc, nullptr, &staging);
        }

        void AppendMatrix(std::string& text, const char* name, const float* m)
        {
            text += name;
            char cell[32];
            for (int i = 0; i < 16; ++i)
            {
                snprintf(cell, sizeof(cell), " %.9g", m[i]);
                text += cell;
            }
            text += "\n";
        }
    }

    void FrameGeneration::StartInputDump(int frames, const std::wstring& folder)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (dumpToTake > 0 || !dumpTaken.empty())
            return;
        dumpFolder = folder;
        dumpToTake = frames;
        dumpFiles.clear();
        dumpText.clear();
        dumpRead = 0;
        dumpWaited = 0;
        dumpHaveForward = false;
        dumpReady.clear();
    }

    // At Present, after the inputs were copied for this frame.
    void FrameGeneration::DumpInputsLocked(bool copied)
    {
        ID3D11Texture2D* sources[4] = { S(hudLess).Valid() ? S(hudLess).d3d11.Get() : nullptr,
                                        S(depth).Valid() ? S(depth).d3d11.Get() : nullptr,
                                        S(motion).Valid() ? S(motion).d3d11.Get() : nullptr,
                                        backBuffer11.Get() };
        if (dumpToTake > 0 && dumpTaken.empty() && dumpReady.empty())
        {
            dumpReady.resize(static_cast<size_t>(dumpToTake));
            for (DumpFrame& ready : dumpReady)
                for (int i = 0; i < 4; ++i)
                    if (sources[i] != nullptr)
                        EnsureStaging(device11.Get(), sources[i], ready.staging[i]);
        }
        if (dumpToTake > 0 && dumpTaken.empty() && dumpWaited < kDumpWaitFrames)
        {
            // Not yet turning: the frames of a turn are the ones worth looking at.
            const float* f = packet.forward;
            const float cosine = f[0] * dumpForward[0] + f[1] * dumpForward[1] + f[2] * dumpForward[2];
            const bool turning = dumpHaveForward && cosine < std::cos(kDumpTurnDegrees * 3.14159265f / 180.0f);
            dumpForward[0] = f[0];
            dumpForward[1] = f[1];
            dumpForward[2] = f[2];
            dumpHaveForward = true;
            if (!turning)
            {
                if (++dumpWaited == kDumpWaitFrames)
                    LogLine("Input dump: no camera turn within " + std::to_string(kDumpWaitFrames) + " frames, taken as they come");
                else
                    return;
            }
            else
                LogLine("Input dump: the camera turns, frames taken from here");
        }
        if (dumpToTake > 0)
        {
            if (!copied)
            {
                // A gap would make the frames not consecutive: begin anew.
                dumpTaken.clear();
                return;
            }
            DumpFrame frame;
            if (!dumpReady.empty())
            {
                frame = std::move(dumpReady.back());
                dumpReady.pop_back();
            }
            frame.packet = packet;
            frame.flipped = flippedLastCopy;
            for (int i = 0; i < 4; ++i)
            {
                if (sources[i] == nullptr)
                {
                    frame.staging[i].Reset();
                    continue;
                }
                EnsureStaging(device11.Get(), sources[i], frame.staging[i]);
                if (frame.staging[i])
                    context11->CopyResource(frame.staging[i].Get(), sources[i]);
            }
            dumpTaken.push_back(std::move(frame));
            if (--dumpToTake == 0)
            {
                dumpReady.clear();
                LogLine("Input dump: " + std::to_string(dumpTaken.size()) + " consecutive frames taken");
            }
            return;
        }

        if (dumpTaken.empty())
            return;

        // All taken: once the GPU is done with them, one texture read back a Present.
        if (dumpRead == 0)
        {
            for (const DumpFrame& frame : dumpTaken)
                for (const auto& staging : frame.staging)
                {
                    if (!staging)
                        continue;
                    D3D11_MAPPED_SUBRESOURCE probe = {};
                    const HRESULT hr = context11->Map(staging.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &probe);
                    if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
                        return;
                    if (SUCCEEDED(hr))
                        context11->Unmap(staging.Get(), 0);
                }
            dumpText = "# per frame: index, packet fields, then the three textures as raw rows\n";
        }

        static const wchar_t* const names[4] = { L"colour", L"depth", L"motion", L"frame" };
        const size_t total = dumpTaken.size() * 4;
        while (dumpRead < total)
        {
            const size_t f = dumpRead / 4;
            const int i = static_cast<int>(dumpRead % 4);
            ++dumpRead;
            DumpFrame& frame = dumpTaken[f];
            if (i == 0)
            {
                const FramePacket& p = frame.packet;
                char line[512];
                snprintf(line, sizeof(line),
                         "frame %zu packetIndex %u render %ux%u reset %u jitter %.6f %.6f mvScale %.3f %.3f near %.6f far %.3f "
                         "fov %.6f dtMs %.3f flipped %d\n",
                         f, p.frameIndex, p.renderWidth, p.renderHeight, p.reset, p.jitterX, p.jitterY, p.motionVectorScaleX,
                         p.motionVectorScaleY, p.nearPlane, p.farPlane, p.verticalFovRadians, p.frameTimeDeltaMs,
                         frame.flipped ? 1 : 0);
                dumpText += line;
                snprintf(line, sizeof(line), "position %.6f %.6f %.6f up %.6f %.6f %.6f right %.6f %.6f %.6f forward %.6f %.6f %.6f\n",
                         p.position[0], p.position[1], p.position[2], p.up[0], p.up[1], p.up[2], p.right[0], p.right[1],
                         p.right[2], p.forward[0], p.forward[1], p.forward[2]);
                dumpText += line;
                AppendMatrix(dumpText, "viewToClip", p.viewToClip);
                AppendMatrix(dumpText, "clipToView", p.clipToView);
                AppendMatrix(dumpText, "clipToPrevClip", p.clipToPrevClip);
                AppendMatrix(dumpText, "prevClipToClip", p.prevClipToClip);
            }
            if (!frame.staging[i])
                continue;
            D3D11_TEXTURE2D_DESC desc = {};
            frame.staging[i]->GetDesc(&desc);
            D3D11_MAPPED_SUBRESOURCE mapped = {};
            if (SUCCEEDED(context11->Map(frame.staging[i].Get(), 0, D3D11_MAP_READ, 0, &mapped)))
            {
                const UINT rowBytes = desc.Width * BytesPerTexel(desc.Format);
                DumpFile out;
                wchar_t file[64];
                swprintf_s(file, L"\\%s-%02zu.bin", names[i], f);
                out.path = dumpFolder + file;
                out.bytes.resize(static_cast<size_t>(rowBytes) * desc.Height);
                const auto* bytes = static_cast<const uint8_t*>(mapped.pData);
                for (UINT y = 0; y < desc.Height; ++y)
                    memcpy(out.bytes.data() + static_cast<size_t>(y) * rowBytes, bytes + static_cast<size_t>(y) * mapped.RowPitch,
                           rowBytes);
                context11->Unmap(frame.staging[i].Get(), 0);
                dumpFiles.push_back(std::move(out));
                char entry[160];
                snprintf(entry, sizeof(entry), "texture %ls %ux%u format %d bytesPerTexel %u\n", names[i], desc.Width,
                         desc.Height, static_cast<int>(desc.Format), BytesPerTexel(desc.Format));
                dumpText += entry;
            }
            frame.staging[i].Reset();
            return;
        }

        // All read: written away from the render thread.
        DumpFile text;
        text.path = dumpFolder + L"\\frames.txt";
        text.bytes.assign(dumpText.begin(), dumpText.end());
        dumpFiles.push_back(std::move(text));
        std::thread([folder = dumpFolder, files = std::move(dumpFiles)]() {
            CreateDirectoryW(folder.c_str(), nullptr);
            for (const DumpFile& f : files)
                WriteFile(f.path, f.bytes.data(), f.bytes.size());
            LogLine("Input dump written: " + std::to_string(files.size() - 1) + " files");
        }).detach();
        dumpFiles.clear();
        dumpText.clear();
        dumpRead = 0;
        dumpTaken.clear();
    }
}
