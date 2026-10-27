#pragma once

#include <windows.h>

#include <d3dcompiler.h>
#include <wrl/client.h>

#include <string>

namespace redefinition
{
    // The two functions of d3dcompiler_47.dll the proxy calls. The DLL ships with Windows but is loaded by
    // hand: linked, the proxy's dxgi.dll would fail to load, and the game not start, on a machine without it.
    struct ShaderCompiler
    {
        // D3DCompile and D3DCompileFromFile, null where the DLL could not be loaded.
        pD3DCompile compile = nullptr;
        HRESULT(WINAPI* compileFromFile)(LPCWSTR, const D3D_SHADER_MACRO*, ID3DInclude*, LPCSTR, LPCSTR, UINT, UINT,
                                         ID3DBlob**, ID3DBlob**) = nullptr;
    };

    // Loaded once, from System32 only, and never freed.
    const ShaderCompiler& LoadShaderCompiler();

    // A compute shader of the source text for shader model 5.0, entry point main. On failure the compiler's
    // messages are in errors (empty where it gave none) and the result is its HRESULT. The compiler's
    // absence is E_NOTIMPL.
    HRESULT CompileComputeShader50(const char* source, size_t length, const char* name, const D3D_SHADER_MACRO* defines,
                                   Microsoft::WRL::ComPtr<ID3DBlob>& code, std::string& errors);
}
