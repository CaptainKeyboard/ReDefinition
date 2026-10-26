// DLSS-G's UI plane (dlssgUiRecomposition in Config.h).
//
// KSP draws its HUD -- Unity canvases and every mod's OnGUI window -- straight onto the
// backbuffer, after the scene camera, so the game has no plane of UI colour and alpha to
// hand DLSS-G. What exists is the HUD-less copy (UpscalerRig.HudLess: the backbuffer at the
// end of the last scene camera) and the presented frame; where the two differ, the pixel
// is UI. NVIDIA's guide wants "UI Color and Alpha" such that
// "Final_Color.RGB = UI.RGB + (1 - UI.Alpha) x Hudless.RGB", "RGB channels pre-multiplied
// by alpha", alpha 0 "for pixels with no UI elements" and non-zero "for pixels with UI"
// (ProgrammingGuideDLSS_G.md 5.0, table "Input"), and recomposition on "when both Hudless
// and a UI buffer are tagged" (6.6).
//
// One equation per channel does not give two unknowns. binary takes alpha 1 where the two
// differ and the presented colour as the UI: exact in the presented frame by construction,
// and the scene behind a translucent panel is taken from the rendered frame as it is.
// minimal takes the least alpha that the equation allows with a colour between 0 and alpha:
// exact for black and white UI, too small for grey UI over a bright scene.
//
// The plane is made on Unity's own D3D11 context at Present, as the flip of the inputs is,
// into a shared texture of the slot in use, which DLSS-G reads once D3D11's fence is passed
// like the other inputs.

#include "FrameGeneration.h"

#include "Config.h"
#include "Log.h"

#include <d3dcompiler.h>

#include <cstring>
#include <string>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        // Both frames are compared, and the plane written, as stored: an sRGB view would
        // decode one and not the other.
        DXGI_FORMAT LinearView(DXGI_FORMAT format)
        {
            switch (format)
            {
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
            case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
            default: return format;
            }
        }

        // THRESHOLD in 0..1 (the levels over 255), MINIMAL 0 or 1.
        const char kUiShader[] =
            "Texture2D<float4> presented : register(t0);\n"
            "Texture2D<float4> hudless : register(t1);\n"
            "RWTexture2D<float4> ui : register(u0);\n"
            "[numthreads(8, 8, 1)]\n"
            "void main(uint3 id : SV_DispatchThreadID)\n"
            "{\n"
            "    uint width, height;\n"
            "    ui.GetDimensions(width, height);\n"
            "    if (id.x >= width || id.y >= height) return;\n"
            "    float3 shown = presented.Load(int3(id.xy, 0)).rgb;\n"
            "    float3 bare = hudless.Load(int3(id.xy, 0)).rgb;\n"
            "    float3 d = shown - bare;\n"
            "    float most = max(abs(d.r), max(abs(d.g), abs(d.b)));\n"
            "    float4 result = float4(0, 0, 0, 0);\n"
            "    if (most > THRESHOLD)\n"
            "    {\n"
            "#if MINIMAL\n"
            "        // A channel under the HUD-less one needs alpha of at least -d / bare, one over it\n"
            "        // d / (1 - bare); the colour is then d + alpha * bare, between 0 and alpha.\n"
            "        float3 below = max(-d, 0) / max(bare, 1e-4);\n"
            "        float3 above = max(d, 0) / max(1 - bare, 1e-4);\n"
            "        float3 least = max(below, above);\n"
            "        float alpha = saturate(max(least.r, max(least.g, least.b)));\n"
            "        result = float4(saturate(d + alpha * bare), alpha);\n"
            "#else\n"
            "        result = float4(shown, 1);\n"
            "#endif\n"
            "    }\n"
            "    ui[id.xy] = result;\n"
            "}\n";
    }

    bool FrameGeneration::EnsureUiShaderLocked(bool minimal, float thresholdLevels)
    {
        if (uiShader && uiShaderMinimal == minimal && uiShaderThreshold == thresholdLevels)
            return true;
        if (uiUnavailable && uiShaderMinimal == minimal && uiShaderThreshold == thresholdLevels)
            return false;
        if (device11 == nullptr)
            return false;

        uiShaderMinimal = minimal;
        uiShaderThreshold = thresholdLevels;
        uiShader.Reset();
        uiUnavailable = true;

        // Loaded by hand, as the flip's compiler is: linked, this dxgi.dll would fail to load
        // on a machine without it.
        const HMODULE compiler = LoadLibraryW(L"d3dcompiler_47.dll");
        const auto compile = compiler != nullptr
            ? reinterpret_cast<pD3DCompile>(GetProcAddress(compiler, "D3DCompile"))
            : nullptr;
        if (compile == nullptr)
        {
            LogLine("DLSS frame generation: d3dcompiler_47.dll is not available -- no UI plane");
            return false;
        }

        char threshold[32];
        FormatTo(threshold, "%.6f", static_cast<double>(thresholdLevels) / 255.0);
        const D3D_SHADER_MACRO defines[] = { { "THRESHOLD", threshold }, { "MINIMAL", minimal ? "1" : "0" },
                                             { nullptr, nullptr } };
        ComPtr<ID3DBlob> code;
        ComPtr<ID3DBlob> errors;
        HRESULT hr = compile(kUiShader, sizeof(kUiShader) - 1, "ui", defines, nullptr, "main", "cs_5_0", 0, 0, &code, &errors);
        if (FAILED(hr))
        {
            LogLine(std::string("DLSS frame generation: the UI plane's shader did not compile (") + Hr(hr) + "): "
                    + (errors ? static_cast<const char*>(errors->GetBufferPointer()) : ""));
            return false;
        }
        hr = device11->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &uiShader);
        if (FAILED(hr))
        {
            LogLine("DLSS frame generation: the UI plane's shader could not be created (" + Hr(hr) + ")");
            return false;
        }
        uiUnavailable = false;
        return true;
    }

    bool FrameGeneration::EnsureUiPlaneLocked()
    {
        if (backBuffer11 == nullptr || device11 == nullptr || device12 == nullptr)
            return false;
        D3D11_TEXTURE2D_DESC desc = {};
        backBuffer11->GetDesc(&desc);
        const bool made = uiPlane[0].Valid() && uiPlane[1].Valid() && uiFrameCopy
                          && uiPlane[0].width == desc.Width && uiPlane[0].height == desc.Height
                          && uiFrameFormat == desc.Format;
        if (made)
            return true;
        if (uiPlaneFailed && uiPlaneFailedWidth == desc.Width && uiPlaneFailedHeight == desc.Height
            && uiPlaneFailedFormat == desc.Format)
            return false;

        ReleaseUiPlaneLocked();
        uiPlaneFailed = true;
        uiPlaneFailedWidth = desc.Width;
        uiPlaneFailedHeight = desc.Height;
        uiPlaneFailedFormat = desc.Format;
        for (SharedTexture& plane : uiPlane)
            if (!plane.Create(device11.Get(), device12.Get(), desc.Width, desc.Height, DXGI_FORMAT_R8G8B8A8_UNORM)
                || !plane.uav)
            {
                LogLine("DLSS frame generation: the UI plane's textures could not be made -- no UI plane until the "
                        "backbuffer changes");
                ReleaseUiPlaneLocked();
                return false;
            }

        // The presented frame, copied, to be read through a view: the backbuffer itself is
        // Unity's render target, which a shader view on it would take from Unity's state.
        D3D11_TEXTURE2D_DESC copy = desc;
        copy.MipLevels = 1;
        copy.ArraySize = 1;
        copy.Usage = D3D11_USAGE_DEFAULT;
        copy.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        copy.CPUAccessFlags = 0;
        copy.MiscFlags = 0;
        HRESULT hr = device11->CreateTexture2D(&copy, nullptr, &uiFrameCopy);
        if (SUCCEEDED(hr))
        {
            D3D11_SHADER_RESOURCE_VIEW_DESC view = {};
            view.Format = LinearView(TypedFormat(desc.Format));
            view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
            view.Texture2D.MipLevels = 1;
            hr = device11->CreateShaderResourceView(uiFrameCopy.Get(), &view, &uiFrameView);
        }
        if (FAILED(hr))
        {
            LogLine("DLSS frame generation: the copy of the presented frame for the UI plane could not be made ("
                    + Hr(hr) + ") -- no UI plane until the backbuffer changes");
            ReleaseUiPlaneLocked();
            return false;
        }
        uiPlaneFailed = false;
        uiFrameFormat = desc.Format;
        LogLine("DLSS frame generation: UI plane " + std::to_string(desc.Width) + "x" + std::to_string(desc.Height)
                + " made, format 28");
        return true;
    }

    void FrameGeneration::ReleaseUiPlaneLocked()
    {
        // The textures may still be read by frames on the GPU: retired, as the inputs are.
        for (SharedTexture& plane : uiPlane)
            RetireLocked(plane);
        for (int i = 0; i < 2; ++i)
        {
            uiHudLessView[i].Reset();
            uiHudLessTexture[i] = nullptr;
        }
        uiFrameView.Reset();
        uiFrameCopy.Reset();
        uiFrameFormat = DXGI_FORMAT_UNKNOWN;
    }

    void FrameGeneration::ResetUiLocked()
    {
        ReleaseUiPlaneLocked();
        uiShader.Reset();
        uiShaderMinimal = false;
        uiShaderThreshold = -1.0f;
        uiUnavailable = false;
        uiPlaneFresh = false;
        uiPlaneFailed = false;
    }

    bool FrameGeneration::SplitUiLocked()
    {
        const ProxyConfig& cfg = Config();
        if (!S(hudLess).Valid() || context11 == nullptr || backBuffer11 == nullptr)
            return false;
        if (S(hudLess).width != displayWidth || S(hudLess).height != displayHeight)
            return false;
        if (!EnsureUiPlaneLocked() || !EnsureUiShaderLocked(cfg.dlssgUiAlpha == kDlssgUiAlphaMinimal, cfg.dlssgUiThreshold))
            return false;

        // A view on the HUD-less texture of this slot, made again where the texture was replaced.
        // The view holds a reference, so the address cannot be taken by another texture meanwhile.
        ID3D11Texture2D* hudLessTexture = S(hudLess).d3d11.Get();
        if (uiHudLessTexture[slot] != hudLessTexture || !uiHudLessView[slot])
        {
            uiHudLessView[slot].Reset();
            D3D11_SHADER_RESOURCE_VIEW_DESC view = {};
            view.Format = LinearView(TypedFormat(S(hudLess).format));
            view.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
            view.Texture2D.MipLevels = 1;
            const HRESULT hr = device11->CreateShaderResourceView(hudLessTexture, &view, &uiHudLessView[slot]);
            if (FAILED(hr))
            {
                LogLine("DLSS frame generation: no shader view on the HUD-less copy for the UI plane (" + Hr(hr) + ")");
                return false;
            }
            uiHudLessTexture[slot] = hudLessTexture;
        }

        context11->CopyResource(uiFrameCopy.Get(), backBuffer11.Get());
        ID3D11ShaderResourceView* views[] = { uiFrameView.Get(), uiHudLessView[slot].Get() };
        ID3D11UnorderedAccessView* uavs[] = { uiPlane[slot].uav.Get() };
        context11->CSSetShader(uiShader.Get(), nullptr, 0);
        context11->CSSetShaderResources(0, 2, views);
        context11->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
        context11->Dispatch((uiPlane[slot].width + 7) / 8, (uiPlane[slot].height + 7) / 8, 1);

        // The plane's bindings go, so Unity's next dispatch does not find a view where it
        // expects nothing.
        ID3D11ShaderResourceView* noViews[] = { nullptr, nullptr };
        ID3D11UnorderedAccessView* noUav[] = { nullptr };
        context11->CSSetShaderResources(0, 2, noViews);
        context11->CSSetUnorderedAccessViews(0, 1, noUav, nullptr);
        context11->CSSetShader(nullptr, nullptr, 0);
        return true;
    }
}
