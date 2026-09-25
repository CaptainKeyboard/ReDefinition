// Frame generation's input dump (FrameGeneration): for a few consecutive rendered
// frames, the HUD-less colour, depth and motion vectors exactly as frame generation
// receives them, the frame as presented, and each frame's packet, written into a
// folder for analysis away from the game. The copies are taken on consecutive frames; they are read back and
// written only once all of them are taken, so writing cannot break the sequence.

#include "FrameGeneration.h"

#include "Log.h"

#include <cstdio>
#include <string>

namespace redefinition
{
    namespace
    {
        void WriteRaw(const std::wstring& path, const D3D11_MAPPED_SUBRESOURCE& mapped, UINT rowBytes, UINT rows)
        {
            FILE* file = nullptr;
            if (_wfopen_s(&file, path.c_str(), L"wb") != 0 || file == nullptr)
                return;
            const auto* bytes = static_cast<const uint8_t*>(mapped.pData);
            for (UINT y = 0; y < rows; ++y)
                fwrite(bytes + static_cast<size_t>(y) * mapped.RowPitch, 1, rowBytes, file);
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
    }

    // At Present, after the inputs were copied for this frame.
    void FrameGeneration::DumpInputsLocked(bool copied)
    {
        if (dumpToTake > 0)
        {
            if (!copied)
            {
                // A gap would make the frames not consecutive: begin anew.
                dumpTaken.clear();
                return;
            }
            DumpFrame frame;
            frame.packet = packet;
            frame.flipped = flippedLastCopy;
            ID3D11Texture2D* sources[4] = { S(hudLess).Valid() ? S(hudLess).d3d11.Get() : nullptr,
                                            S(depth).Valid() ? S(depth).d3d11.Get() : nullptr,
                                            S(motion).Valid() ? S(motion).d3d11.Get() : nullptr,
                                            backBuffer11.Get() };
            for (int i = 0; i < 4; ++i)
            {
                if (sources[i] == nullptr)
                    continue;
                D3D11_TEXTURE2D_DESC desc = {};
                sources[i]->GetDesc(&desc);
                desc.Usage = D3D11_USAGE_STAGING;
                desc.BindFlags = 0;
                desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                desc.MiscFlags = 0;
                if (FAILED(device11->CreateTexture2D(&desc, nullptr, &frame.staging[i])))
                    continue;
                context11->CopyResource(frame.staging[i].Get(), sources[i]);
            }
            dumpTaken.push_back(frame);
            if (--dumpToTake == 0)
                LogLine("Input dump: " + std::to_string(dumpTaken.size()) + " consecutive frames taken");
            return;
        }

        if (dumpTaken.empty())
            return;

        // All taken: read back and write, once the GPU is done with them.
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

        CreateDirectoryW(dumpFolder.c_str(), nullptr);
        static const wchar_t* const names[4] = { L"colour", L"depth", L"motion", L"frame" };
        std::string text = "# per frame: index, packet fields, then the three textures as raw rows\n";
        for (size_t f = 0; f < dumpTaken.size(); ++f)
        {
            const DumpFrame& frame = dumpTaken[f];
            const FramePacket& p = frame.packet;
            char line[512];
            snprintf(line, sizeof(line),
                     "frame %zu packetIndex %u render %ux%u reset %u jitter %.6f %.6f mvScale %.3f %.3f near %.6f far %.3f "
                     "fov %.6f dtMs %.3f flipped %d\n",
                     f, p.frameIndex, p.renderWidth, p.renderHeight, p.reset, p.jitterX, p.jitterY, p.motionVectorScaleX,
                     p.motionVectorScaleY, p.nearPlane, p.farPlane, p.verticalFovRadians, p.frameTimeDeltaMs,
                     frame.flipped ? 1 : 0);
            text += line;
            snprintf(line, sizeof(line), "position %.6f %.6f %.6f up %.6f %.6f %.6f right %.6f %.6f %.6f forward %.6f %.6f %.6f\n",
                     p.position[0], p.position[1], p.position[2], p.up[0], p.up[1], p.up[2], p.right[0], p.right[1],
                     p.right[2], p.forward[0], p.forward[1], p.forward[2]);
            text += line;
            AppendMatrix(text, "viewToClip", p.viewToClip);
            AppendMatrix(text, "clipToView", p.clipToView);
            AppendMatrix(text, "clipToPrevClip", p.clipToPrevClip);
            AppendMatrix(text, "prevClipToClip", p.prevClipToClip);

            for (int i = 0; i < 4; ++i)
            {
                if (!frame.staging[i])
                    continue;
                D3D11_TEXTURE2D_DESC desc = {};
                frame.staging[i]->GetDesc(&desc);
                D3D11_MAPPED_SUBRESOURCE mapped = {};
                if (FAILED(context11->Map(frame.staging[i].Get(), 0, D3D11_MAP_READ, 0, &mapped)))
                    continue;
                wchar_t file[64];
                swprintf_s(file, L"\\%s-%02zu.bin", names[i], f);
                const UINT rowBytes = desc.Width * BytesPerTexel(desc.Format);
                WriteRaw(dumpFolder + file, mapped, rowBytes, desc.Height);
                context11->Unmap(frame.staging[i].Get(), 0);
                char entry[160];
                snprintf(entry, sizeof(entry), "texture %ls %ux%u format %d bytesPerTexel %u\n", names[i], desc.Width,
                         desc.Height, static_cast<int>(desc.Format), BytesPerTexel(desc.Format));
                text += entry;
            }
        }

        FILE* file = nullptr;
        if (_wfopen_s(&file, (dumpFolder + L"\\frames.txt").c_str(), L"wb") == 0 && file != nullptr)
        {
            fwrite(text.data(), 1, text.size(), file);
            fclose(file);
        }
        LogLine("Input dump written: " + std::to_string(dumpTaken.size()) + " frames");
        dumpTaken.clear();
    }
}
