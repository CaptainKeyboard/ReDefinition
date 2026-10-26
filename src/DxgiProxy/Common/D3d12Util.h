#pragma once

#include <windows.h>

#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>

#include <cstdint>
#include <cstring>

namespace redefinition
{
    // FrameGenerationInputs.cpp: the typed format of a typeless one.
    DXGI_FORMAT TypedFormat(DXGI_FORMAT format);

    // A transition of every subresource of a D3D12 resource, as the copies into
    // the backbuffer and the upscaler's dispatch record them.
    inline D3D12_RESOURCE_BARRIER TransitionBarrier(ID3D12Resource* resource, D3D12_RESOURCE_STATES from,
                                                    D3D12_RESOURCE_STATES to)
    {
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        barrier.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;
        barrier.Transition.pResource = resource;
        barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        barrier.Transition.StateBefore = from;
        barrier.Transition.StateAfter = to;
        return barrier;
    }

    // An unordered-access barrier on one resource, or on all of them for null.
    inline D3D12_RESOURCE_BARRIER UavBarrier(ID3D12Resource* resource)
    {
        D3D12_RESOURCE_BARRIER barrier = {};
        barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV;
        barrier.UAV.pResource = resource;
        return barrier;
    }

    // A 2D texture's shader resource view of its one mip.
    inline D3D12_SHADER_RESOURCE_VIEW_DESC SrvDesc(DXGI_FORMAT format)
    {
        D3D12_SHADER_RESOURCE_VIEW_DESC desc = {};
        desc.Format = format;
        desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
        desc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
        desc.Texture2D.MipLevels = 1;
        return desc;
    }

    // A 2D texture's unordered access view of its first mip.
    inline D3D12_UNORDERED_ACCESS_VIEW_DESC UavDesc(DXGI_FORMAT format)
    {
        D3D12_UNORDERED_ACCESS_VIEW_DESC desc = {};
        desc.Format = format;
        desc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
        return desc;
    }

    // The format of a shared copy: the typed format of a typeless one, and the
    // linear twin of an sRGB one, which can be bound for unordered access.
    // CopyResource copies between formats of one group, so the bytes arrive as
    // they are.
    inline DXGI_FORMAT ShareFormat(DXGI_FORMAT format)
    {
        const DXGI_FORMAT typed = TypedFormat(format);
        switch (typed)
        {
        case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
        case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
        default: return typed;
        }
    }

    // A fence made on Direct3D 12 and opened on Direct3D 11. Where it fails and
    // failedCall is given, it names the call that failed.
    inline HRESULT SharedFence(ID3D11Device5* device11, ID3D12Device* device12,
                               Microsoft::WRL::ComPtr<ID3D12Fence>& fence12, Microsoft::WRL::ComPtr<ID3D11Fence>& fence11,
                               const char** failedCall = nullptr)
    {
        const char* call = "CreateFence";
        HRESULT hr = device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence12));
        HANDLE handle = nullptr;
        if (SUCCEEDED(hr))
        {
            call = "CreateSharedHandle";
            hr = device12->CreateSharedHandle(fence12.Get(), nullptr, GENERIC_ALL, nullptr, &handle);
        }
        if (SUCCEEDED(hr))
        {
            call = "OpenSharedFence";
            hr = device11->OpenSharedFence(handle, IID_PPV_ARGS(&fence11));
            CloseHandle(handle);
        }
        if (failedCall != nullptr)
            *failedCall = FAILED(hr) ? call : nullptr;
        return hr;
    }

    // The description of a texture the CPU reads back, made from the texture's own.
    inline D3D11_TEXTURE2D_DESC StagingDesc(D3D11_TEXTURE2D_DESC desc)
    {
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        desc.MiscFlags = 0;
        return desc;
    }

    // A half float's value, infinities and NaN included.
    inline float HalfToFloat(uint16_t half)
    {
        const uint32_t sign = (half & 0x8000u) << 16;
        uint32_t exponent = (half >> 10) & 0x1Fu;
        uint32_t mantissa = half & 0x3FFu;
        uint32_t bits;
        if (exponent == 0)
        {
            if (mantissa == 0)
                bits = sign;
            else
            {
                // A subnormal half is a normal float.
                exponent = 127 - 15 + 1;
                while ((mantissa & 0x400u) == 0)
                {
                    mantissa <<= 1;
                    --exponent;
                }
                mantissa &= 0x3FFu;
                bits = sign | (exponent << 23) | (mantissa << 13);
            }
        }
        else if (exponent == 31)
            bits = sign | 0x7F800000u | (mantissa << 13);
        else
            bits = sign | ((exponent + 127 - 15) << 23) | (mantissa << 13);
        float value;
        memcpy(&value, &bits, sizeof(value));
        return value;
    }

    // A float as a half, rounded towards zero: subnormal where it is that small, zero
    // below the smallest subnormal, infinity beyond the largest half.
    inline uint16_t FloatToHalf(float value)
    {
        uint32_t bits;
        memcpy(&bits, &value, sizeof(bits));
        const uint32_t sign = (bits >> 16) & 0x8000u;
        const int exponent = static_cast<int>((bits >> 23) & 0xFFu);
        const uint32_t mantissa = bits & 0x7FFFFFu;
        if (exponent == 255)
            return static_cast<uint16_t>(sign | 0x7C00u | (mantissa != 0 ? 0x200u : 0u));
        const int halfExponent = exponent - 127 + 15;
        if (halfExponent >= 31)
            return static_cast<uint16_t>(sign | 0x7C00u);
        if (halfExponent > 0)
            return static_cast<uint16_t>(sign | (static_cast<uint32_t>(halfExponent) << 10) | (mantissa >> 13));
        if (halfExponent < -10)
            return static_cast<uint16_t>(sign);
        return static_cast<uint16_t>(sign | ((mantissa | 0x800000u) >> (14 - halfExponent)));
    }
}
