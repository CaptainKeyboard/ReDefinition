#include "ShaderCompiler.h"

namespace redefinition
{
    const ShaderCompiler& LoadShaderCompiler()
    {
        static const ShaderCompiler compiler = []
        {
            ShaderCompiler loaded;
            const HMODULE module = LoadLibraryExW(L"d3dcompiler_47.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
            if (module != nullptr)
            {
                loaded.compile = reinterpret_cast<pD3DCompile>(GetProcAddress(module, "D3DCompile"));
                loaded.compileFromFile = reinterpret_cast<decltype(loaded.compileFromFile)>(
                    GetProcAddress(module, "D3DCompileFromFile"));
            }
            return loaded;
        }();
        return compiler;
    }

    HRESULT CompileComputeShader50(const char* source, size_t length, const char* name, const D3D_SHADER_MACRO* defines,
                                   Microsoft::WRL::ComPtr<ID3DBlob>& code, std::string& errors)
    {
        errors.clear();
        const ShaderCompiler& compiler = LoadShaderCompiler();
        if (compiler.compile == nullptr)
            return E_NOTIMPL;
        Microsoft::WRL::ComPtr<ID3DBlob> messages;
        const HRESULT hr = compiler.compile(source, length, name, defines, nullptr, "main", "cs_5_0", 0, 0, &code,
                                            &messages);
        if (messages)
            errors = static_cast<const char*>(messages->GetBufferPointer());
        return hr;
    }
}
