// Frame generation's checks (FrameGeneration): whether the HUD-less copy differs
// from the presented frame by the UI alone, and whether the motion vectors
// reproject the frame before onto this one -- read back and logged, for the
// conventions the ini's switches set.

#include "FrameGeneration.h"

#include "Config.h"
#include "Log.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace redefinition
{
    namespace
    {
        // One character per tile of the HUD-less check's map: nothing, tenths,
        // or all of it.
        char TileMark(uint32_t differing, uint32_t sampled)
        {
            if (sampled == 0 || differing == 0)
                return '.';

            const double share = static_cast<double>(differing) / static_cast<double>(sampled);
            if (share >= 0.95)
                return '#';

            const int tenths = static_cast<int>(std::ceil(share * 10.0));
            return static_cast<char>('0' + std::min(std::max(tenths, 1), 9));
        }

        float HalfToFloat(uint16_t h)
        {
            const uint32_t sign = (h & 0x8000u) << 16;
            uint32_t exponent = (h >> 10) & 0x1Fu;
            uint32_t mantissa = h & 0x3FFu;
            uint32_t bits;
            if (exponent == 0)
            {
                if (mantissa == 0)
                    bits = sign;
                else
                {
                    exponent = 127 - 15 + 1;
                    while ((mantissa & 0x400u) == 0) { mantissa <<= 1; --exponent; }
                    mantissa &= 0x3FFu;
                    bits = sign | (exponent << 23) | (mantissa << 13);
                }
            }
            else if (exponent == 31)
                bits = sign | 0x7F800000u | (mantissa << 13);
            else
                bits = sign | ((exponent + 127 - 15) << 23) | (mantissa << 13);
            float f;
            memcpy(&f, &bits, sizeof(f));
            return f;
        }

        float Luminance(uint32_t rgba)
        {
            const float r = static_cast<float>(rgba & 0xFFu) / 255.0f;
            const float g = static_cast<float>((rgba >> 8) & 0xFFu) / 255.0f;
            const float b = static_cast<float>((rgba >> 16) & 0xFFu) / 255.0f;
            return 0.2126f * r + 0.7152f * g + 0.0722f * b;
        }
    }

    // ---------------------------------------------------------------- HUD-less

    // Reading back never blocks: copies the GPU has not finished yet are looked
    // at again on a later frame, so the check cannot stall the game.
    void FrameGeneration::CheckHudLessLocked(bool copied)
    {
        if (checkBroken || context11 == nullptr || backBuffer11 == nullptr || !S(hudLess).Valid())
        {
            // A pair whose second frame cannot be taken ends here, so the next
            // starts anew rather than pairing copies frames apart.
            if (!checkPending)
                checkHavePrevious = false;
            return;
        }

        if (checkPending)
        {
            D3D11_MAPPED_SUBRESOURCE presented = {};
            D3D11_MAPPED_SUBRESOURCE snapshot = {};
            D3D11_MAPPED_SUBRESOURCE depthRows = {};
            D3D11_MAPPED_SUBRESOURCE previous = {};
            D3D11_MAPPED_SUBRESOURCE motionRows = {};

            HRESULT hr = context11->Map(checkPresented.Get(), 0, D3D11_MAP_READ,
                                        D3D11_MAP_FLAG_DO_NOT_WAIT, &presented);
            if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
                return;

            if (SUCCEEDED(hr))
            {
                hr = context11->Map(checkSnapshot.Get(), 0, D3D11_MAP_READ,
                                    D3D11_MAP_FLAG_DO_NOT_WAIT, &snapshot);
                if (FAILED(hr))
                    context11->Unmap(checkPresented.Get(), 0);
                if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
                    return;
            }

            bool haveDepth = false;
            if (SUCCEEDED(hr) && checkDepth)
            {
                const HRESULT depthHr = context11->Map(checkDepth.Get(), 0, D3D11_MAP_READ,
                                                       D3D11_MAP_FLAG_DO_NOT_WAIT, &depthRows);
                if (depthHr == DXGI_ERROR_WAS_STILL_DRAWING)
                {
                    context11->Unmap(checkSnapshot.Get(), 0);
                    context11->Unmap(checkPresented.Get(), 0);
                    return;
                }
                haveDepth = SUCCEEDED(depthHr);
            }

            if (FAILED(hr))
            {
                LogLine("HUD-less check could not read back (" + Hr(hr) + ") and is switched off");
                checkBroken = true;
                ReleaseCheckLocked();
                return;
            }

            // Every figure below is read by the size of the texture that was
            // mapped, whatever the inputs are now.
            D3D11_TEXTURE2D_DESC colourDesc = {};
            D3D11_TEXTURE2D_DESC depthDesc = {};
            D3D11_TEXTURE2D_DESC motionDesc = {};
            checkPresented->GetDesc(&colourDesc);
            if (checkDepth)
                checkDepth->GetDesc(&depthDesc);
            if (checkMotion)
                checkMotion->GetDesc(&motionDesc);

            EvaluateCheckLocked(presented, snapshot, haveDepth ? &depthRows : nullptr,
                                colourDesc.Width, colourDesc.Height, depthDesc.Width, depthDesc.Height);

            // The motion vectors, if the previous frame's copy is there too.
            // Maps that are still drawing are simply skipped: the colour check
            // above already stands.
            if (checkHavePrevious && checkMotion
                && SUCCEEDED(context11->Map(checkPrevious.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &previous)))
            {
                if (SUCCEEDED(context11->Map(checkMotion.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &motionRows)))
                {
                    EvaluateMotionLocked(presented, snapshot, previous, motionRows,
                                         colourDesc.Width, colourDesc.Height, motionDesc.Width, motionDesc.Height);
                    context11->Unmap(checkMotion.Get(), 0);
                }
                context11->Unmap(checkPrevious.Get(), 0);
            }

            if (haveDepth)
                context11->Unmap(checkDepth.Get(), 0);
            context11->Unmap(checkSnapshot.Get(), 0);
            context11->Unmap(checkPresented.Get(), 0);
            checkPending = false;
            checkHavePrevious = false;
            return;
        }

        // Only a frame copied for frame generation that exists -- FSR's context,
        // or DLSS-G's swapchain -- makes a check's frame; any other ends a pair
        // begun, as above. Without a context no pair could be completed: while
        // one waits for its next attempt, copies are made once a second at most.
        const ProxyConfig& cfg = Config();
        if (!copied || !(contextCreated || streamline) || !cfg.hudLessColour || !inputsCopied || !packetFresh
            || (checkPresented == nullptr && !CreateCheckTexturesLocked()))
        {
            checkHavePrevious = false;
            return;
        }

        // Second frame of a check: this frame's copy, backbuffer, depth and
        // motion vectors, all as FSR gets them.
        if (checkHavePrevious)
        {
            context11->CopyResource(checkSnapshot.Get(), S(hudLess).d3d11.Get());
            context11->CopyResource(checkPresented.Get(), backBuffer11.Get());
            if (checkDepth)
                context11->CopyResource(checkDepth.Get(), S(depth).d3d11.Get());
            if (checkMotion)
                context11->CopyResource(checkMotion.Get(), S(motion).d3d11.Get());
            checkPending = true;
            return;
        }

        // A check the mod asked for (RequestCheck) starts with the next frame that
        // can be one, if that comes soon enough; otherwise every reportSeconds.
        const ULONGLONG now = GetTickCount64();
        const ULONGLONG requestedAt = checkRequestedAt.exchange(0);
        const bool requested = requestedAt != 0 && now - requestedAt <= kCheckRequestMs;
        if (requestedAt != 0 && !requested)
            LogLine("A requested motion vector check was dropped: no frame could start it within "
                    + std::to_string(kCheckRequestMs) + " ms");
        if (!requested && lastCheckTicks != 0
            && now - lastCheckTicks < static_cast<ULONGLONG>(cfg.reportSeconds) * 1000ull)
            return;

        // First frame of a check: the HUD-less copy, kept for the reprojection.
        context11->CopyResource(checkPrevious.Get(), S(hudLess).d3d11.Get());
        checkHavePrevious = true;
        lastCheckTicks = now;
    }

    // The motion vectors, tested. FSR reads a vector as
    // "where this pixel was in the previous frame", in pixels once scaled. So
    // for a grid of pixels the previous frame's HUD-less copy is looked up at
    // the place the vector points to, and the colour there is compared with
    // this frame's. Done for both signs of each axis and for the texture as
    // delivered and mirrored: the hypothesis with the smallest error is the
    // convention FSR is being given. Scene motion only -- UI pixels, where
    // copy and backbuffer differ, are left out.
    void FrameGeneration::EvaluateMotionLocked(const D3D11_MAPPED_SUBRESOURCE& presented,
                                               const D3D11_MAPPED_SUBRESOURCE& snapshot,
                                               const D3D11_MAPPED_SUBRESOURCE& previous,
                                               const D3D11_MAPPED_SUBRESOURCE& motionRows,
                                               UINT width, UINT height, UINT mvWidth, UINT mvHeight)
    {
        if (width < 64 || height < 64 || mvWidth == 0 || mvHeight == 0)
            return;

        const auto* presentedBytes = static_cast<const uint8_t*>(presented.pData);
        const auto* snapshotBytes = static_cast<const uint8_t*>(snapshot.pData);
        const auto* previousBytes = static_cast<const uint8_t*>(previous.pData);
        const auto* motionBytes = static_cast<const uint8_t*>(motionRows.pData);

        auto colourAt = [&](const uint8_t* bytes, UINT pitch, UINT x, UINT y) -> uint32_t
        {
            return reinterpret_cast<const uint32_t*>(bytes + static_cast<size_t>(y) * pitch)[x] & 0x00FFFFFFu;
        };
        auto difference = [](uint32_t a, uint32_t b) -> int
        {
            int total = 0;
            for (int shift = 0; shift < 24; shift += 8)
            {
                const int delta = static_cast<int>((a >> shift) & 0xFFu) - static_cast<int>((b >> shift) & 0xFFu);
                total += delta < 0 ? -delta : delta;
            }
            return total;
        };

        // Eight hypotheses: rows as delivered or mirrored, X sign, Y sign. The
        // vector is read in its own texture's units and scaled to display
        // pixels by the magnitude of the scale the packet carries.
        const float scaleX = std::fabs(packet.motionVectorScaleX) * static_cast<float>(width) / static_cast<float>(mvWidth);
        const float scaleY = std::fabs(packet.motionVectorScaleY) * static_cast<float>(height) / static_cast<float>(mvHeight);

        double error[8] = {};
        uint64_t counted[8] = {};
        double stillError = 0.0;
        uint64_t stillCounted = 0;
        double meanMagnitude = 0.0;
        uint64_t magnitudeCount = 0;

        for (UINT y = 8; y + 8 < height; y += 8)
        {
            for (UINT x = 8; x + 8 < width; x += 8)
            {
                const uint32_t current = colourAt(snapshotBytes, snapshot.RowPitch, x, y);
                if (current != colourAt(presentedBytes, presented.RowPitch, x, y))
                    continue;   // UI

                stillError += difference(current, colourAt(previousBytes, previous.RowPitch, x, y));
                ++stillCounted;

                for (int hypothesis = 0; hypothesis < 8; ++hypothesis)
                {
                    const bool mirrored = (hypothesis & 4) != 0;
                    const float signX = (hypothesis & 1) ? -1.0f : 1.0f;
                    const float signY = (hypothesis & 2) ? -1.0f : 1.0f;

                    const UINT mx = x * mvWidth / width;
                    UINT my = y * mvHeight / height;
                    if (mirrored)
                        my = mvHeight - 1 - my;

                    const auto* texel = reinterpret_cast<const uint16_t*>(
                        motionBytes + static_cast<size_t>(my) * motionRows.RowPitch) + static_cast<size_t>(mx) * 2;
                    const float dx = HalfToFloat(texel[0]) * scaleX * signX;
                    const float dy = HalfToFloat(texel[1]) * scaleY * signY;

                    if (hypothesis == 0)
                    {
                        meanMagnitude += std::sqrt(dx * dx + dy * dy);
                        ++magnitudeCount;
                    }

                    const int px = static_cast<int>(std::lround(static_cast<float>(x) + dx));
                    const int py = static_cast<int>(std::lround(static_cast<float>(y) + dy));
                    if (px < 0 || py < 0 || px >= static_cast<int>(width) || py >= static_cast<int>(height))
                        continue;

                    error[hypothesis] += difference(current, colourAt(previousBytes, previous.RowPitch,
                                                                     static_cast<UINT>(px), static_cast<UINT>(py)));
                    ++counted[hypothesis];
                }
            }
        }

        if (stillCounted == 0 || magnitudeCount == 0)
            return;

        const double magnitude = meanMagnitude / static_cast<double>(magnitudeCount);
        if (magnitude < 0.5)
        {
            char line[200] = {};
            FormatTo(line, "    Motion vectors: mean %.2f px between the two frames -- too little motion to test the convention", magnitude);
            LogLine(line);
            return;
        }

        int best = -1;
        double bestError = 1e300;
        std::string report;
        for (int hypothesis = 0; hypothesis < 8; ++hypothesis)
        {
            const double mean = counted[hypothesis] ? error[hypothesis] / static_cast<double>(counted[hypothesis]) : 1e300;
            // A hypothesis without a sample tells nothing, and is never the best.
            if (counted[hypothesis] != 0 && mean < bestError) { bestError = mean; best = hypothesis; }

            char cell[64] = {};
            if (counted[hypothesis] == 0)
                FormatTo(cell, " %s%c%c=n/a", (hypothesis & 4) ? "mirrored" : "rows", (hypothesis & 1) ? '-' : '+',
                         (hypothesis & 2) ? '-' : '+');
            else
                FormatTo(cell, " %s%c%c=%.1f", (hypothesis & 4) ? "mirrored" : "rows", (hypothesis & 1) ? '-' : '+',
                         (hypothesis & 2) ? '-' : '+', mean);
            report += cell;
        }

        // What FSR is actually given: the rows as delivered, and the sign of
        // the scale after fgNegateMotionScale and the flip's Y turn.
        const float negate = Config().fgNegateMotionScale ? -1.0f : 1.0f;
        const float givenX = packet.motionVectorScaleX * negate;
        const float givenY = packet.motionVectorScaleY * negate * (flippedLastCopy ? -1.0f : 1.0f);
        const int given = (givenX < 0.0f ? 1 : 0) | (givenY < 0.0f ? 2 : 0);

        char line[640] = {};
        // Every sample of every hypothesis outside the frame -- motion too large
        // to reproject, a scene load: no verdict, rather than a wrong one.
        if (best < 0)
        {
            FormatTo(line,
                     "    Motion vectors (as FSR gets them, mean %.1f px): no hypothesis had a sample inside the frame;%s. "
                     "Nothing to test in this frame.",
                     magnitude, report.c_str());
            LogLine(line);
            return;
        }
        FormatTo(line,
                  "    Motion vectors (as FSR gets them, mean %.1f px): reprojection error per pixel, no motion %.1f;%s. "
                  "Best: %s %c%c. FSR is given: rows as delivered %c%c. %s",
                  magnitude, stillError / static_cast<double>(stillCounted), report.c_str(),
                  (best & 4) ? "mirrored" : "rows as delivered", (best & 1) ? '-' : '+', (best & 2) ? '-' : '+',
                  (given & 1) ? '-' : '+', (given & 2) ? '-' : '+',
                  best == given ? "MATCH."
                                : (best & 4) ? "MISMATCH: the rows are upside down for FSR -- fgFlipInputs the other way."
                                             : "MISMATCH: a sign is wrong -- fgNegateMotionScale, or the Y turn of the flip.");
        LogLine(line);
    }

    bool FrameGeneration::CreateCheckTexturesLocked()
    {
        D3D11_TEXTURE2D_DESC desc = {};
        backBuffer11->GetDesc(&desc);

        // Eight bits per channel, colour in the low three bytes: what KSP's
        // backbuffer is. Other formats are not compared.
        switch (desc.Format)
        {
        case DXGI_FORMAT_R8G8B8A8_UNORM:
        case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
        case DXGI_FORMAT_B8G8R8A8_UNORM:
        case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
        case DXGI_FORMAT_B8G8R8X8_UNORM:
        case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB:
            break;
        default:
            LogLine("HUD-less check skipped: it cannot compare backbuffer format "
                    + std::to_string(static_cast<int>(desc.Format)));
            checkBroken = true;
            return false;
        }

        if (S(hudLess).width != desc.Width || S(hudLess).height != desc.Height)
        {
            LogLine("HUD-less check skipped: the copy and the backbuffer differ in size");
            checkBroken = true;
            return false;
        }

        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.SampleDesc.Count = 1;
        desc.SampleDesc.Quality = 0;
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        desc.MiscFlags = 0;

        HRESULT hr = device11->CreateTexture2D(&desc, nullptr, &checkPresented);
        if (SUCCEEDED(hr))
            hr = device11->CreateTexture2D(&desc, nullptr, &checkSnapshot);
        if (SUCCEEDED(hr))
            hr = device11->CreateTexture2D(&desc, nullptr, &checkPrevious);

        if (FAILED(hr))
        {
            LogLine("HUD-less check textures could not be created (" + Hr(hr) + "), check off");
            checkBroken = true;
            ReleaseCheckLocked();
            return false;
        }

        // Depth rows are only read back when the format is the one the rig
        // sends. Optional: the colour check stands without it.
        if (S(depth).Valid() && TypedFormat(S(depth).format) == DXGI_FORMAT_R32_FLOAT)
        {
            D3D11_TEXTURE2D_DESC depthDesc = {};
            S(depth).d3d11->GetDesc(&depthDesc);
            depthDesc.Usage = D3D11_USAGE_STAGING;
            depthDesc.BindFlags = 0;
            depthDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            depthDesc.MiscFlags = 0;
            if (FAILED(device11->CreateTexture2D(&depthDesc, nullptr, &checkDepth)))
                checkDepth.Reset();
        }

        if (S(motion).Valid() && TypedFormat(S(motion).format) == DXGI_FORMAT_R16G16_FLOAT)
        {
            D3D11_TEXTURE2D_DESC motionDesc = {};
            S(motion).d3d11->GetDesc(&motionDesc);
            motionDesc.Usage = D3D11_USAGE_STAGING;
            motionDesc.BindFlags = 0;
            motionDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            motionDesc.MiscFlags = 0;
            if (FAILED(device11->CreateTexture2D(&motionDesc, nullptr, &checkMotion)))
                checkMotion.Reset();
        }

        return true;
    }

    void FrameGeneration::ReleaseCheckLocked()
    {
        checkPresented.Reset();
        checkSnapshot.Reset();
        checkPrevious.Reset();
        checkDepth.Reset();
        checkMotion.Reset();
        checkPending = false;
        checkHavePrevious = false;
    }

    // What FSR will treat as UI: every pixel where the presented frame and the
    // HUD-less copy differ. Measured twice -- the copy as it is, and the copy
    // mirrored vertically -- so a copy that is upside down relative to the
    // backbuffer shows as "direct large, mirrored small". Colour only; alpha is
    // never shown. Every second pixel in each
    // direction, once every few seconds.
    void FrameGeneration::EvaluateCheckLocked(const D3D11_MAPPED_SUBRESOURCE& presented,
                                              const D3D11_MAPPED_SUBRESOURCE& snapshot,
                                              const D3D11_MAPPED_SUBRESOURCE* depthRows,
                                              UINT width, UINT height, UINT depthWidth, UINT depthHeight)
    {
        constexpr UINT kColumns = 12;
        constexpr UINT kRows = 5;

        if (width < kColumns || height < kRows)
            return;

        uint32_t tileSampled[kRows][kColumns] = {};
        uint32_t tileDiffering[kRows][kColumns] = {};
        uint64_t sampled = 0;
        uint64_t differing = 0;
        uint64_t differingMirrored = 0;
        uint64_t deltaSum = 0;

        // Luminance of the presented frame in its top and bottom tenth, next to
        // the depth there: on the launch pad the top of the image is sky.
        double topLuminance = 0.0, bottomLuminance = 0.0;
        uint64_t topCount = 0, bottomCount = 0;
        const UINT band = std::max(1u, height / 10);

        std::vector<uint8_t> columnOf(width);
        for (UINT x = 0; x < width; ++x)
            columnOf[x] = static_cast<uint8_t>(x * kColumns / width);

        const auto* presentedBytes = static_cast<const uint8_t*>(presented.pData);
        const auto* snapshotBytes = static_cast<const uint8_t*>(snapshot.pData);

        for (UINT y = 0; y < height; y += 2)
        {
            const auto* a = reinterpret_cast<const uint32_t*>(
                presentedBytes + static_cast<size_t>(y) * presented.RowPitch);
            const auto* b = reinterpret_cast<const uint32_t*>(
                snapshotBytes + static_cast<size_t>(y) * snapshot.RowPitch);
            const auto* m = reinterpret_cast<const uint32_t*>(
                snapshotBytes + static_cast<size_t>(height - 1 - y) * snapshot.RowPitch);
            const UINT row = y * kRows / height;

            for (UINT x = 0; x < width; x += 2)
            {
                const UINT column = columnOf[x];
                ++tileSampled[row][column];
                ++sampled;

                // The low three bytes are colour in every format the check
                // accepts; the top one is alpha or unused.
                const uint32_t pa = a[x] & 0x00FFFFFFu;
                const uint32_t pb = b[x] & 0x00FFFFFFu;
                const uint32_t pm = m[x] & 0x00FFFFFFu;

                if (y < band) { topLuminance += Luminance(pa); ++topCount; }
                else if (y >= height - band) { bottomLuminance += Luminance(pa); ++bottomCount; }

                if (pa != pm)
                    ++differingMirrored;

                if (pa == pb)
                    continue;

                ++tileDiffering[row][column];
                ++differing;

                int largest = 0;
                for (int shift = 0; shift < 24; shift += 8)
                {
                    const int delta = static_cast<int>((pa >> shift) & 0xFFu)
                                    - static_cast<int>((pb >> shift) & 0xFFu);
                    largest = std::max(largest, delta < 0 ? -delta : delta);
                }
                deltaSum += static_cast<uint64_t>(largest);
            }
        }

        lastDirect = sampled != 0
            ? static_cast<float>(static_cast<double>(differing) / static_cast<double>(sampled)) : 0.0f;
        lastMirrored = sampled != 0
            ? static_cast<float>(static_cast<double>(differingMirrored) / static_cast<double>(sampled)) : 0.0f;

        char line[512] = {};
        FormatTo(line,
                  "HUD-less check (frame %u, inputs %s): direct %.2f %% of the frame differs from the copy, "
                  "mirrored %.2f %%. The smaller one is what FSR treats as UI; the flip is right when "
                  "direct is the smaller.",
                  packet.frameIndex, flippedLastCopy ? "flipped" : "not flipped",
                  100.0 * static_cast<double>(lastDirect), 100.0 * static_cast<double>(lastMirrored));
        LogLine(line);

        std::string summary = "    Direct, by tile (. none, 1-9 tenths, # all)";
        if (differing != 0)
            summary += ", differing by " + std::to_string(deltaSum / differing) + "/255 on average";
        LogLine(summary + ":");

        for (UINT row = 0; row < kRows; ++row)
        {
            std::string cells = "    |";
            for (UINT column = 0; column < kColumns; ++column)
                cells += TileMark(tileDiffering[row][column], tileSampled[row][column]);
            LogLine(cells + "|");
        }

        // Depth as FSR receives it, top tenth against bottom tenth, next to the
        // luminance of the presented frame there. With reversed Z a larger
        // value is nearer; sky is far and bright, ground is near and darker.
        if (depthRows != nullptr && depthHeight >= 10)
        {
            const UINT depthBand = std::max(1u, depthHeight / 10);
            double top = 0.0, bottom = 0.0;
            uint64_t topN = 0, bottomN = 0;
            const auto* bytes = static_cast<const uint8_t*>(depthRows->pData);

            for (UINT y = 0; y < depthHeight; y += 2)
            {
                if (y >= depthBand && y < depthHeight - depthBand)
                    continue;
                const auto* row = reinterpret_cast<const float*>(bytes + static_cast<size_t>(y) * depthRows->RowPitch);
                for (UINT x = 0; x < depthWidth; x += 4)
                {
                    if (y < depthBand) { top += row[x]; ++topN; }
                    else { bottom += row[x]; ++bottomN; }
                }
            }

            FormatTo(line,
                      "    Depth as FSR gets it: top tenth mean %.4f, bottom tenth mean %.4f "
                      "(reversed Z: larger is nearer). Presented frame luminance: top %.3f, bottom %.3f.",
                      topN ? top / topN : 0.0, bottomN ? bottom / bottomN : 0.0,
                      topCount ? topLuminance / topCount : 0.0,
                      bottomCount ? bottomLuminance / bottomCount : 0.0);
            LogLine(line);
        }

        if (lastDirect >= 0.5f && lastMirrored >= 0.5f)
            LogLine("    WARNING: most of the frame differs in both orientations. The copy is not of "
                    "this frame -- FSR would treat nearly all of it as UI and interpolate almost nothing.");
        else if (lastMirrored < lastDirect)
            LogLine("    WARNING: the copy matches the frame mirrored, not direct -- fgFlipInputs is "
                    "the wrong way round for this texture.");
        else if (differing == 0)
            LogLine("    Nothing differs: either no UI is on screen right now, or the copy is taken "
                    "after the UI rather than before it.");
    }
}
