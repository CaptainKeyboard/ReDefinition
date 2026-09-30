// The raytracing harness (docs/development/raytracing.md, "Testing without the game").
//
// It loads the proxy as Unity does, rasterises a G-buffer in Unity's layout with its
// own Direct3D 11 shaders, hands meshes, materials and instances to the proxy's
// raytracing service through its exports, sends frames through the render event, and
// checks what comes back against the CPU. Images of every check go next to the
// executable, as BMP, for a look.
//
// 0 when every check passes.

#include "Raytracing.h"

#include <windows.h>

#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <map>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
using namespace redefinition;

namespace
{
    constexpr UINT kWidth = 1280;
    constexpr UINT kHeight = 720;
    constexpr int kFrameEvent = 30;

    int failures = 0;

    void Check(bool passed, const char* what, const std::string& detail)
    {
        printf("%s  %s -- %s\n", passed ? "PASS" : "FAIL", what, detail.c_str());
        if (!passed)
            ++failures;
    }

    int Fatal(const char* what, HRESULT hr)
    {
        printf("FAIL  %s (0x%08lX)\n", what, static_cast<unsigned long>(hr));
        return 1;
    }

    std::wstring NextToExecutable(const wchar_t* name)
    {
        wchar_t path[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, path, MAX_PATH);
        std::wstring result(path);
        return result.substr(0, result.find_last_of(L"\\/") + 1) + name;
    }

    // ------------------------------------------------------------------ maths

    struct Vec3
    {
        double x = 0, y = 0, z = 0;
    };
    Vec3 operator+(Vec3 a, Vec3 b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
    Vec3 operator-(Vec3 a, Vec3 b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
    Vec3 operator*(Vec3 a, double s) { return { a.x * s, a.y * s, a.z * s }; }
    double Dot(Vec3 a, Vec3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
    Vec3 Cross(Vec3 a, Vec3 b) { return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x }; }
    double Length(Vec3 a) { return std::sqrt(Dot(a, a)); }
    Vec3 Normalize(Vec3 a) { return a * (1.0 / Length(a)); }

    // Rows first, for column vectors.
    struct Mat4
    {
        double m[16] = {};
        double& at(int r, int c) { return m[r * 4 + c]; }
        double at(int r, int c) const { return m[r * 4 + c]; }
    };

    Mat4 Multiply(const Mat4& a, const Mat4& b)
    {
        Mat4 r;
        for (int i = 0; i < 4; ++i)
            for (int j = 0; j < 4; ++j)
            {
                double s = 0;
                for (int k = 0; k < 4; ++k)
                    s += a.at(i, k) * b.at(k, j);
                r.at(i, j) = s;
            }
        return r;
    }

    Mat4 Inverse(const Mat4& a)
    {
        // Gauss-Jordan with partial pivoting.
        double w[4][8];
        for (int r = 0; r < 4; ++r)
            for (int c = 0; c < 8; ++c)
                w[r][c] = c < 4 ? a.at(r, c) : (c - 4 == r ? 1.0 : 0.0);
        for (int c = 0; c < 4; ++c)
        {
            int pivot = c;
            for (int r = c + 1; r < 4; ++r)
                if (std::fabs(w[r][c]) > std::fabs(w[pivot][c]))
                    pivot = r;
            for (int k = 0; k < 8; ++k)
                std::swap(w[c][k], w[pivot][k]);
            const double d = w[c][c];
            for (int k = 0; k < 8; ++k)
                w[c][k] /= d;
            for (int r = 0; r < 4; ++r)
                if (r != c)
                {
                    const double f = w[r][c];
                    for (int k = 0; k < 8; ++k)
                        w[r][k] -= f * w[c][k];
                }
        }
        Mat4 r;
        for (int i = 0; i < 4; ++i)
            for (int j = 0; j < 4; ++j)
                r.at(i, j) = w[i][j + 4];
        return r;
    }

    // Unity's view space: right-handed, the camera looking down -z; here without the
    // camera's position, which camera-relative space has taken out.
    Mat4 LookRotation(Vec3 forward, Vec3 up)
    {
        const Vec3 f = Normalize(forward);
        const Vec3 r = Normalize(Cross(f, up));
        const Vec3 u = Cross(r, f);
        Mat4 v;
        v.at(0, 0) = r.x; v.at(0, 1) = r.y; v.at(0, 2) = r.z;
        v.at(1, 0) = u.x; v.at(1, 1) = u.y; v.at(1, 2) = u.z;
        v.at(2, 0) = -f.x; v.at(2, 1) = -f.y; v.at(2, 2) = -f.z;
        v.at(3, 3) = 1.0;
        return v;
    }

    // Reversed depth as Unity has it on Direct3D: 1 at the near plane, 0 at the far.
    Mat4 Perspective(double verticalFov, double aspect, double nearPlane, double farPlane)
    {
        const double f = 1.0 / std::tan(verticalFov * 0.5);
        Mat4 p;
        p.at(0, 0) = f / aspect;
        p.at(1, 1) = f;
        p.at(2, 2) = nearPlane / (farPlane - nearPlane);
        p.at(2, 3) = farPlane * nearPlane / (farPlane - nearPlane);
        p.at(3, 2) = -1.0;
        return p;
    }

    void ToFloats(const Mat4& a, float* out)
    {
        for (int i = 0; i < 16; ++i)
            out[i] = static_cast<float>(a.m[i]);
    }

    // ------------------------------------------------------------------ scene

    struct Mesh
    {
        int id = 0;
        std::vector<float> positions;
        std::vector<float> normals;
        std::vector<uint32_t> indices;
        ComPtr<ID3D11Buffer> vertexBuffer;
        ComPtr<ID3D11Buffer> indexBuffer;
    };

    struct Instance
    {
        int mesh = 0;
        int material = 0;
        Vec3 position;        // in the world, in double precision
        double scale[3] = { 1, 1, 1 };
        float albedo[3] = { 0.5f, 0.5f, 0.5f };
        float smoothness = 0.2f;
        // A full transform in place of position and scale, rows first, mesh to world.
        bool hasMatrix = false;
        double matrix[12] = {};
    };

    void AddQuad(Mesh& mesh, Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 n)
    {
        const uint32_t base = static_cast<uint32_t>(mesh.positions.size() / 3);
        for (Vec3 p : { a, b, c, d })
        {
            mesh.positions.insert(mesh.positions.end(), { float(p.x), float(p.y), float(p.z) });
            mesh.normals.insert(mesh.normals.end(), { float(n.x), float(n.y), float(n.z) });
        }
        mesh.indices.insert(mesh.indices.end(), { base, base + 1, base + 2, base, base + 2, base + 3 });
    }

    // A unit cube from -0.5 to 0.5.
    Mesh MakeCube(int id)
    {
        Mesh m;
        m.id = id;
        const double h = 0.5;
        AddQuad(m, { -h, -h, h }, { h, -h, h }, { h, h, h }, { -h, h, h }, { 0, 0, 1 });
        AddQuad(m, { h, -h, -h }, { -h, -h, -h }, { -h, h, -h }, { h, h, -h }, { 0, 0, -1 });
        AddQuad(m, { h, -h, h }, { h, -h, -h }, { h, h, -h }, { h, h, h }, { 1, 0, 0 });
        AddQuad(m, { -h, -h, -h }, { -h, -h, h }, { -h, h, h }, { -h, h, -h }, { -1, 0, 0 });
        AddQuad(m, { -h, h, h }, { h, h, h }, { h, h, -h }, { -h, h, -h }, { 0, 1, 0 });
        AddQuad(m, { -h, -h, -h }, { h, -h, -h }, { h, -h, h }, { -h, -h, h }, { 0, -1, 0 });
        return m;
    }

    // A unit square in the xz plane, facing up, split into cells so that no triangle is
    // so large that the rasteriser's and the ray's precision part ways at its far end.
    Mesh MakePlane(int id, int cells)
    {
        Mesh m;
        m.id = id;
        for (int i = 0; i < cells; ++i)
            for (int j = 0; j < cells; ++j)
            {
                const double x0 = -0.5 + double(i) / cells, x1 = -0.5 + double(i + 1) / cells;
                const double z0 = -0.5 + double(j) / cells, z1 = -0.5 + double(j + 1) / cells;
                AddQuad(m, { x0, 0, z1 }, { x1, 0, z1 }, { x1, 0, z0 }, { x0, 0, z0 }, { 0, 1, 0 });
            }
        return m;
    }

    // A scene file from tools/export_rt_scene.py.
    bool LoadScene(const std::wstring& path, std::map<int, Mesh>& meshes, std::vector<Instance>& instances, int firstId)
    {
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        if (file == INVALID_HANDLE_VALUE)
            return false;
        LARGE_INTEGER size = {};
        GetFileSizeEx(file, &size);
        std::vector<uint8_t> bytes(static_cast<size_t>(size.QuadPart));
        DWORD read = 0;
        const BOOL ok = ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr);
        CloseHandle(file);
        if (!ok || read != bytes.size() || bytes.size() < 16 || memcmp(bytes.data(), "RTSC", 4) != 0)
            return false;
        size_t at = 4;
        auto u32 = [&]() { uint32_t v = 0; memcpy(&v, &bytes[at], 4); at += 4; return v; };
        auto floats = [&](std::vector<float>& out, size_t count)
        {
            out.resize(count);
            memcpy(out.data(), &bytes[at], count * 4);
            at += count * 4;
        };
        if (u32() != 1)
            return false;
        const uint32_t meshCount = u32();
        const uint32_t instanceCount = u32();
        for (uint32_t m = 0; m < meshCount; ++m)
        {
            Mesh mesh;
            mesh.id = firstId + static_cast<int>(m);
            const uint32_t vertices = u32();
            const uint32_t indices = u32();
            floats(mesh.positions, static_cast<size_t>(vertices) * 3);
            floats(mesh.normals, static_cast<size_t>(vertices) * 3);
            std::vector<float> uvs;
            floats(uvs, static_cast<size_t>(vertices) * 2);
            mesh.indices.resize(indices);
            memcpy(mesh.indices.data(), &bytes[at], static_cast<size_t>(indices) * 4);
            at += static_cast<size_t>(indices) * 4;
            meshes[mesh.id] = std::move(mesh);
        }
        for (uint32_t i = 0; i < instanceCount; ++i)
        {
            Instance instance;
            instance.mesh = firstId + static_cast<int>(u32());
            std::vector<float> values;
            floats(values, 12);
            instance.hasMatrix = true;
            for (int k = 0; k < 12; ++k)
                instance.matrix[k] = values[k];
            floats(values, 4);
            memcpy(instance.albedo, values.data(), 3 * sizeof(float));
            instance.smoothness = values[3];
            instances.push_back(instance);
        }
        return at == bytes.size();
    }

    struct Camera
    {
        Vec3 position;
        Vec3 target;
        double fov = 60.0 * 3.14159265358979 / 180.0;
        double nearPlane = 0.3;
        double farPlane = 100000.0;
        Mat4 worldToClip;   // camera-relative
        Mat4 clipToWorld;

        void Update()
        {
            const Mat4 view = LookRotation(target - position, { 0, 1, 0 });
            worldToClip = Multiply(Perspective(fov, double(kWidth) / kHeight, nearPlane, farPlane), view);
            clipToWorld = Inverse(worldToClip);
        }
    };

    // The 3x4 from mesh to camera-relative space, rows first.
    void RelativeTransform(const Instance& instance, const Vec3& camera, float* out)
    {
        if (instance.hasMatrix)
        {
            for (int i = 0; i < 12; ++i)
                out[i] = static_cast<float>(instance.matrix[i]);
            out[3] = static_cast<float>(instance.matrix[3] - camera.x);
            out[7] = static_cast<float>(instance.matrix[7] - camera.y);
            out[11] = static_cast<float>(instance.matrix[11] - camera.z);
            return;
        }
        const Vec3 t = instance.position - camera;
        const float rows[12] = { float(instance.scale[0]), 0, 0, float(t.x),
                                 0, float(instance.scale[1]), 0, float(t.y),
                                 0, 0, float(instance.scale[2]), float(t.z) };
        memcpy(out, rows, sizeof(rows));
    }

    // ------------------------------------------------------------------ G-buffer

    const char kGBufferShader[] = R"(
cbuffer Draw : register(b0)
{
    row_major float4x4 worldToClip;
    row_major float3x4 model;
    float4 albedo;
    float4 surface;
};
struct VsIn { float3 position : POSITION; float3 normal : NORMAL; };
struct VsOut { float4 position : SV_Position; float3 normal : NORMAL; };
VsOut VsMain(VsIn input)
{
    VsOut o;
    float3 world = mul(model, float4(input.position, 1.0));
    o.position = mul(worldToClip, float4(world, 1.0));
    float3x3 m = (float3x3)model;
    float3x3 cofactor = float3x3(cross(m[1], m[2]), cross(m[2], m[0]), cross(m[0], m[1]));
    o.normal = mul(cofactor, input.normal);
    return o;
}
struct PsOut
{
    float depth : SV_Target0;
    float4 gbuffer0 : SV_Target1;
    float4 gbuffer1 : SV_Target2;
    float4 gbuffer2 : SV_Target3;
};
PsOut PsMain(VsOut input)
{
    PsOut o;
    o.depth = input.position.z;
    o.gbuffer0 = float4(albedo.rgb, 1.0);
    o.gbuffer1 = float4(0.04, 0.04, 0.04, surface.x);
    o.gbuffer2 = float4(normalize(input.normal) * 0.5 + 0.5, 1.0);
    return o;
}
)";

    struct DrawConstants
    {
        float worldToClip[16];
        float model[12];
        float albedo[4];
        float surface[4];
    };

    struct GBuffer
    {
        ComPtr<ID3D11Texture2D> depth;       // R32_FLOAT, the clip-space z
        ComPtr<ID3D11Texture2D> gbuffer0;    // R8G8B8A8_UNORM
        ComPtr<ID3D11Texture2D> gbuffer1;
        ComPtr<ID3D11Texture2D> gbuffer2;    // R10G10B10A2_UNORM
        ComPtr<ID3D11Texture2D> depthStencil;
        ComPtr<ID3D11RenderTargetView> targets[4];
        ComPtr<ID3D11DepthStencilView> depthView;
        ComPtr<ID3D11VertexShader> vs;
        ComPtr<ID3D11PixelShader> ps;
        ComPtr<ID3D11InputLayout> layout;
        ComPtr<ID3D11Buffer> constants;
        ComPtr<ID3D11DepthStencilState> depthState;
        ComPtr<ID3D11RasterizerState> rasterizer;
    };

    HRESULT CreateTarget(ID3D11Device* device, DXGI_FORMAT format, UINT bind, ComPtr<ID3D11Texture2D>& out)
    {
        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = kWidth;
        desc.Height = kHeight;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = bind;
        return device->CreateTexture2D(&desc, nullptr, &out);
    }

    HRESULT CreateGBuffer(ID3D11Device* device, GBuffer& g)
    {
        HRESULT hr = CreateTarget(device, DXGI_FORMAT_R32_FLOAT, D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE, g.depth);
        if (SUCCEEDED(hr))
            hr = CreateTarget(device, DXGI_FORMAT_R8G8B8A8_UNORM, D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE, g.gbuffer0);
        if (SUCCEEDED(hr))
            hr = CreateTarget(device, DXGI_FORMAT_R8G8B8A8_UNORM, D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE, g.gbuffer1);
        if (SUCCEEDED(hr))
            hr = CreateTarget(device, DXGI_FORMAT_R10G10B10A2_UNORM, D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE, g.gbuffer2);
        if (SUCCEEDED(hr))
            hr = CreateTarget(device, DXGI_FORMAT_D32_FLOAT, D3D11_BIND_DEPTH_STENCIL, g.depthStencil);
        ID3D11Texture2D* textures[4] = { g.depth.Get(), g.gbuffer0.Get(), g.gbuffer1.Get(), g.gbuffer2.Get() };
        for (int i = 0; SUCCEEDED(hr) && i < 4; ++i)
            hr = device->CreateRenderTargetView(textures[i], nullptr, &g.targets[i]);
        if (SUCCEEDED(hr))
            hr = device->CreateDepthStencilView(g.depthStencil.Get(), nullptr, &g.depthView);

        ComPtr<ID3DBlob> vsCode, psCode, errors;
        if (SUCCEEDED(hr))
            hr = D3DCompile(kGBufferShader, sizeof(kGBufferShader) - 1, "gbuffer", nullptr, nullptr, "VsMain", "vs_5_0", 0, 0,
                            &vsCode, &errors);
        if (SUCCEEDED(hr))
            hr = D3DCompile(kGBufferShader, sizeof(kGBufferShader) - 1, "gbuffer", nullptr, nullptr, "PsMain", "ps_5_0", 0, 0,
                            &psCode, &errors);
        if (FAILED(hr) && errors != nullptr)
            printf("      %s\n", static_cast<const char*>(errors->GetBufferPointer()));
        if (SUCCEEDED(hr))
            hr = device->CreateVertexShader(vsCode->GetBufferPointer(), vsCode->GetBufferSize(), nullptr, &g.vs);
        if (SUCCEEDED(hr))
            hr = device->CreatePixelShader(psCode->GetBufferPointer(), psCode->GetBufferSize(), nullptr, &g.ps);
        const D3D11_INPUT_ELEMENT_DESC elements[] = {
            { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0, D3D11_INPUT_PER_VERTEX_DATA, 0 },
            { "NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12, D3D11_INPUT_PER_VERTEX_DATA, 0 },
        };
        if (SUCCEEDED(hr))
            hr = device->CreateInputLayout(elements, 2, vsCode->GetBufferPointer(), vsCode->GetBufferSize(), &g.layout);
        D3D11_BUFFER_DESC cb = {};
        cb.ByteWidth = sizeof(DrawConstants);
        cb.Usage = D3D11_USAGE_DYNAMIC;
        cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        cb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (SUCCEEDED(hr))
            hr = device->CreateBuffer(&cb, nullptr, &g.constants);
        D3D11_DEPTH_STENCIL_DESC ds = {};
        ds.DepthEnable = TRUE;
        ds.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
        ds.DepthFunc = D3D11_COMPARISON_GREATER;
        if (SUCCEEDED(hr))
            hr = device->CreateDepthStencilState(&ds, &g.depthState);
        D3D11_RASTERIZER_DESC rs = {};
        rs.FillMode = D3D11_FILL_SOLID;
        rs.CullMode = D3D11_CULL_NONE;
        rs.DepthClipEnable = TRUE;
        if (SUCCEEDED(hr))
            hr = device->CreateRasterizerState(&rs, &g.rasterizer);
        return hr;
    }

    HRESULT UploadMeshForRaster(ID3D11Device* device, Mesh& mesh)
    {
        std::vector<float> interleaved;
        const size_t vertices = mesh.positions.size() / 3;
        interleaved.reserve(vertices * 6);
        for (size_t v = 0; v < vertices; ++v)
        {
            interleaved.insert(interleaved.end(), mesh.positions.begin() + v * 3, mesh.positions.begin() + v * 3 + 3);
            interleaved.insert(interleaved.end(), mesh.normals.begin() + v * 3, mesh.normals.begin() + v * 3 + 3);
        }
        D3D11_BUFFER_DESC vb = {};
        vb.ByteWidth = static_cast<UINT>(interleaved.size() * sizeof(float));
        vb.Usage = D3D11_USAGE_IMMUTABLE;
        vb.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        D3D11_SUBRESOURCE_DATA vd = { interleaved.data(), 0, 0 };
        HRESULT hr = device->CreateBuffer(&vb, &vd, &mesh.vertexBuffer);
        D3D11_BUFFER_DESC ib = {};
        ib.ByteWidth = static_cast<UINT>(mesh.indices.size() * sizeof(uint32_t));
        ib.Usage = D3D11_USAGE_IMMUTABLE;
        ib.BindFlags = D3D11_BIND_INDEX_BUFFER;
        D3D11_SUBRESOURCE_DATA id = { mesh.indices.data(), 0, 0 };
        if (SUCCEEDED(hr))
            hr = device->CreateBuffer(&ib, &id, &mesh.indexBuffer);
        return hr;
    }

    void RenderGBuffer(ID3D11DeviceContext* context, GBuffer& g, const Camera& camera, std::map<int, Mesh>& meshes,
                       const std::vector<Instance>& instances)
    {
        const float zero[4] = { 0, 0, 0, 0 };
        const float up[4] = { 0.5f, 1.0f, 0.5f, 1.0f };
        context->ClearRenderTargetView(g.targets[0].Get(), zero);
        context->ClearRenderTargetView(g.targets[1].Get(), zero);
        context->ClearRenderTargetView(g.targets[2].Get(), zero);
        context->ClearRenderTargetView(g.targets[3].Get(), up);
        context->ClearDepthStencilView(g.depthView.Get(), D3D11_CLEAR_DEPTH, 0.0f, 0);
        ID3D11RenderTargetView* targets[4] = { g.targets[0].Get(), g.targets[1].Get(), g.targets[2].Get(), g.targets[3].Get() };
        context->OMSetRenderTargets(4, targets, g.depthView.Get());
        context->OMSetDepthStencilState(g.depthState.Get(), 0);
        context->RSSetState(g.rasterizer.Get());
        const D3D11_VIEWPORT viewport = { 0, 0, float(kWidth), float(kHeight), 0, 1 };
        context->RSSetViewports(1, &viewport);
        context->IASetInputLayout(g.layout.Get());
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(g.vs.Get(), nullptr, 0);
        context->PSSetShader(g.ps.Get(), nullptr, 0);
        ID3D11Buffer* cbs[] = { g.constants.Get() };
        context->VSSetConstantBuffers(0, 1, cbs);
        context->PSSetConstantBuffers(0, 1, cbs);
        for (const Instance& instance : instances)
        {
            Mesh& mesh = meshes[instance.mesh];
            DrawConstants constants = {};
            ToFloats(camera.worldToClip, constants.worldToClip);
            RelativeTransform(instance, camera.position, constants.model);
            memcpy(constants.albedo, instance.albedo, sizeof(instance.albedo));
            constants.albedo[3] = 1.0f;
            constants.surface[0] = instance.smoothness;
            D3D11_MAPPED_SUBRESOURCE mapped = {};
            if (SUCCEEDED(context->Map(g.constants.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
            {
                memcpy(mapped.pData, &constants, sizeof(constants));
                context->Unmap(g.constants.Get(), 0);
            }
            const UINT stride = 24, offset = 0;
            ID3D11Buffer* vbs[] = { mesh.vertexBuffer.Get() };
            context->IASetVertexBuffers(0, 1, vbs, &stride, &offset);
            context->IASetIndexBuffer(mesh.indexBuffer.Get(), DXGI_FORMAT_R32_UINT, 0);
            context->DrawIndexed(static_cast<UINT>(mesh.indices.size()), 0, 0);
        }
        ID3D11RenderTargetView* none[4] = {};
        context->OMSetRenderTargets(4, none, nullptr);
    }

    // ------------------------------------------------------------------ readback

    template <typename T>
    bool ReadTexture(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* texture, UINT texelSize,
                     std::vector<T>& out)
    {
        D3D11_TEXTURE2D_DESC desc = {};
        texture->GetDesc(&desc);
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        desc.MiscFlags = 0;
        ComPtr<ID3D11Texture2D> staging;
        if (FAILED(device->CreateTexture2D(&desc, nullptr, &staging)))
            return false;
        context->CopyResource(staging.Get(), texture);
        D3D11_MAPPED_SUBRESOURCE mapped = {};
        if (FAILED(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)))
            return false;
        const UINT perTexel = texelSize / sizeof(T);
        out.resize(static_cast<size_t>(desc.Width) * desc.Height * perTexel);
        for (UINT y = 0; y < desc.Height; ++y)
            memcpy(&out[static_cast<size_t>(y) * desc.Width * perTexel],
                   static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch,
                   static_cast<size_t>(desc.Width) * texelSize);
        context->Unmap(staging.Get(), 0);
        return true;
    }

    float HalfToFloat(uint16_t h)
    {
        const uint32_t sign = (h & 0x8000u) << 16;
        const uint32_t exponent = (h >> 10) & 0x1Fu;
        const uint32_t mantissa = h & 0x3FFu;
        uint32_t bits;
        if (exponent == 0)
        {
            if (mantissa == 0)
                bits = sign;
            else
            {
                float value = std::ldexp(static_cast<float>(mantissa), -24);
                return (h & 0x8000u) ? -value : value;
            }
        }
        else if (exponent == 31)
            bits = sign | 0x7F800000u | (mantissa << 13);
        else
            bits = sign | ((exponent + 112) << 23) | (mantissa << 13);
        float result;
        memcpy(&result, &bits, sizeof(result));
        return result;
    }

    // A greyscale or colour image as a 24-bit BMP.
    void WriteBmp(const std::wstring& path, UINT width, UINT height, const std::vector<float>& rgb)
    {
        const UINT rowSize = (width * 3 + 3) & ~3u;
        std::vector<uint8_t> file(54 + static_cast<size_t>(rowSize) * height, 0);
        auto put32 = [&](size_t at, uint32_t value) { memcpy(&file[at], &value, 4); };
        file[0] = 'B';
        file[1] = 'M';
        put32(2, static_cast<uint32_t>(file.size()));
        put32(10, 54);
        put32(14, 40);
        put32(18, width);
        put32(22, height);
        file[26] = 1;
        file[28] = 24;
        for (UINT y = 0; y < height; ++y)
            for (UINT x = 0; x < width; ++x)
            {
                const float* p = &rgb[(static_cast<size_t>(y) * width + x) * 3];
                uint8_t* out = &file[54 + static_cast<size_t>(height - 1 - y) * rowSize + x * 3];
                for (int c = 0; c < 3; ++c)
                {
                    const float v = std::pow(std::min(std::max(p[2 - c], 0.0f), 1.0f), 1.0f / 2.2f);
                    out[c] = static_cast<uint8_t>(v * 255.0f + 0.5f);
                }
            }
        HANDLE handle = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (handle == INVALID_HANDLE_VALUE)
            return;
        DWORD written = 0;
        WriteFile(handle, file.data(), static_cast<DWORD>(file.size()), &written, nullptr);
        CloseHandle(handle);
    }

    // ------------------------------------------------------------------ the proxy

    struct Proxy
    {
        HMODULE module = nullptr;
        int (*available)(char*, int) = nullptr;
        unsigned int (*structSizes)(unsigned int*, unsigned int*, unsigned int*) = nullptr;
        int (*setMesh)(const RtMeshDesc*) = nullptr;
        void (*releaseMesh)(int) = nullptr;
        int (*setMaterial)(const RtMaterialDesc*) = nullptr;
        int (*setInstances)(const RtInstance*, int) = nullptr;
        void (*passTimings)(float*, int) = nullptr;
        int (*status)(char*, int) = nullptr;
        void (*timings)(float*, float*) = nullptr;
        void (*event)(int, void*) = nullptr;

        template <typename T>
        bool Load(T& function, const char* name)
        {
            function = reinterpret_cast<T>(GetProcAddress(module, name));
            return function != nullptr;
        }

        std::string Status()
        {
            char text[512] = {};
            status(text, sizeof(text));
            return text;
        }
    };

    int SendMesh(Proxy& proxy, const Mesh& mesh)
    {
        RtMeshDesc desc = {};
        desc.size = sizeof(desc);
        desc.magic = kRtMeshMagic;
        desc.mesh = mesh.id;
        desc.vertexCount = static_cast<uint32_t>(mesh.positions.size() / 3);
        desc.indexCount = static_cast<uint32_t>(mesh.indices.size());
        desc.positions = mesh.positions.data();
        desc.normals = mesh.normals.data();
        desc.indices = mesh.indices.data();
        return proxy.setMesh(&desc);
    }

    void SendInstances(Proxy& proxy, const std::vector<Instance>& instances, const Vec3& camera)
    {
        std::vector<RtInstance> list(instances.size());
        for (size_t i = 0; i < instances.size(); ++i)
        {
            RelativeTransform(instances[i], camera, list[i].transform);
            list[i].mesh = instances[i].mesh;
            list[i].material = instances[i].material;
            list[i].mask = 0xFF;
        }
        proxy.setInstances(list.data(), static_cast<int>(list.size()));
    }

    struct Outputs
    {
        ComPtr<ID3D11Texture2D> visibility;   // u0, R16G16B16A16_FLOAT
        ComPtr<ID3D11Texture2D> diffuse;      // u1
        ComPtr<ID3D11Texture2D> specular;     // u2
        ComPtr<ID3D11Texture2D> occlusion;    // u3
        ComPtr<ID3D11Texture2D> primary;      // u7, R32G32B32A32_FLOAT
    };

    // What a frame traces besides the camera, with the defaults of the shadow checks.
    struct Lighting
    {
        float sunColor[3] = { 1.0f, 1.0f, 1.0f };
        float sky[3] = { 1.0f, 1.0f, 1.0f };
        float aoDistance = 2.0f;
        float bounces = 2.0f;
        float diffuseDistance = 100000.0f;
        float specularDistance = 100000.0f;
        float clamp = 0.0f;
        ID3D11Texture2D* environment = nullptr;
    };

    void SendFrame(Proxy& proxy, const Camera& camera, GBuffer& g, Outputs& outputs, Vec3 towardsSun, float sunRadius,
                   uint32_t frameIndex, uint32_t signals, const Lighting& lighting = Lighting())
    {
        RtFramePacket packet = {};
        packet.size = sizeof(packet);
        packet.magic = kRtFrameMagic;
        packet.frameIndex = frameIndex;
        packet.flags = kRtFrameSameFrame;
        packet.width = kWidth;
        packet.height = kHeight;
        packet.signals = signals;
        ToFloats(camera.clipToWorld, packet.clipToWorld);
        ToFloats(camera.worldToClip, packet.worldToClip);
        ToFloats(camera.worldToClip, packet.prevWorldToClip);
        const Vec3 l = Normalize(towardsSun);
        packet.sunDirection[0] = float(l.x);
        packet.sunDirection[1] = float(l.y);
        packet.sunDirection[2] = float(l.z);
        packet.sunDirection[3] = sunRadius;
        memcpy(packet.sunColor, lighting.sunColor, sizeof(lighting.sunColor));
        memcpy(packet.skyColor, lighting.sky, sizeof(lighting.sky));
        packet.params[0] = lighting.aoDistance;
        packet.params[1] = lighting.bounces;
        packet.params[2] = lighting.diffuseDistance;
        packet.params[3] = lighting.specularDistance;
        packet.params[4] = lighting.clamp;
        packet.params[5] = lighting.environment != nullptr ? 1.0f : 0.0f;
        packet.inputs[kRtInputEnvironment] = lighting.environment;
        packet.inputs[kRtInputDepth] = g.depth.Get();
        packet.inputs[kRtInputGBuffer0] = g.gbuffer0.Get();
        packet.inputs[kRtInputGBuffer1] = g.gbuffer1.Get();
        packet.inputs[kRtInputGBuffer2] = g.gbuffer2.Get();
        packet.outputs[0] = outputs.visibility.Get();
        packet.outputs[1] = outputs.diffuse.Get();
        packet.outputs[2] = outputs.specular.Get();
        packet.outputs[3] = outputs.occlusion.Get();
        packet.outputs[7] = outputs.primary.Get();
        proxy.event(kFrameEvent, &packet);
    }

    // The sun's visibility the CPU expects at every pixel: the depth's position, off the
    // surface as the pass sets it off, against every box in the scene.
    std::vector<int> ExpectedVisibility(const Camera& camera, const std::vector<float>& depth,
                                        const std::vector<uint32_t>& normals, const std::vector<Instance>& boxes,
                                        Vec3 towardsSun)
    {
        const Vec3 l = Normalize(towardsSun);
        std::vector<int> expected(static_cast<size_t>(kWidth) * kHeight, -1);
        for (UINT y = 0; y < kHeight; ++y)
            for (UINT x = 0; x < kWidth; ++x)
            {
                const size_t i = static_cast<size_t>(y) * kWidth + x;
                if (depth[i] <= 0.0f)
                    continue;
                const double nx = ((x + 0.5) / kWidth) * 2.0 - 1.0;
                const double ny = 1.0 - ((y + 0.5) / kHeight) * 2.0;
                const Mat4& m = camera.clipToWorld;
                double p[4];
                for (int r = 0; r < 4; ++r)
                    p[r] = m.at(r, 0) * nx + m.at(r, 1) * ny + m.at(r, 2) * depth[i] + m.at(r, 3);
                const Vec3 position = { p[0] / p[3], p[1] / p[3], p[2] / p[3] };
                const uint32_t packed = normals[i];
                const Vec3 n = Normalize({ (packed & 0x3FF) / 1023.0 * 2 - 1, ((packed >> 10) & 0x3FF) / 1023.0 * 2 - 1,
                                           ((packed >> 20) & 0x3FF) / 1023.0 * 2 - 1 });
                if (Dot(n, l) <= 0.0)
                {
                    expected[i] = 0;
                    continue;
                }
                const Vec3 origin = position + n * (0.01 + Length(position) * 0.0005);
                bool blocked = false;
                for (const Instance& box : boxes)
                {
                    const Vec3 centre = box.position - camera.position;
                    double tMin = 0.0, tMax = 1e30;
                    const double o[3] = { origin.x, origin.y, origin.z };
                    const double d[3] = { l.x, l.y, l.z };
                    const double c[3] = { centre.x, centre.y, centre.z };
                    bool miss = false;
                    for (int a = 0; a < 3 && !miss; ++a)
                    {
                        const double lo = c[a] - box.scale[a] * 0.5, hi = c[a] + box.scale[a] * 0.5;
                        if (std::fabs(d[a]) < 1e-12)
                        {
                            miss = o[a] < lo || o[a] > hi;
                            continue;
                        }
                        double t0 = (lo - o[a]) / d[a], t1 = (hi - o[a]) / d[a];
                        if (t0 > t1)
                            std::swap(t0, t1);
                        tMin = std::max(tMin, t0);
                        tMax = std::min(tMax, t1);
                        miss = tMin > tMax;
                    }
                    blocked = blocked || !miss;
                }
                expected[i] = blocked ? 0 : 1;
            }
        return expected;
    }

    struct Frame
    {
        std::vector<float> depth;
        std::vector<uint32_t> normals;
        std::vector<uint16_t> visibility;   // four halves a texel
        std::vector<float> primary;         // four floats a texel
    };

    bool ReadFrame(ID3D11Device* device, ID3D11DeviceContext* context, GBuffer& g, Outputs& outputs, Frame& frame)
    {
        return ReadTexture(device, context, g.depth.Get(), 4, frame.depth)
               && ReadTexture(device, context, g.gbuffer2.Get(), 4, frame.normals)
               && ReadTexture(device, context, outputs.visibility.Get(), 8, frame.visibility)
               && ReadTexture(device, context, outputs.primary.Get(), 16, frame.primary);
    }

    // Compares the visibility with the CPU's, apart from pixels at a shadow's edge,
    // where one pixel's rasterised position and the CPU's differ by rounding.
    void CheckVisibility(const char* what, const Frame& frame, const std::vector<int>& expected)
    {
        size_t compared = 0, wrong = 0;
        for (UINT y = 1; y + 1 < kHeight; ++y)
            for (UINT x = 1; x + 1 < kWidth; ++x)
            {
                const size_t i = static_cast<size_t>(y) * kWidth + x;
                if (expected[i] < 0)
                    continue;
                bool edge = false;
                for (int dy = -1; dy <= 1 && !edge; ++dy)
                    for (int dx = -1; dx <= 1 && !edge; ++dx)
                        edge = expected[i + dy * static_cast<int>(kWidth) + dx] != expected[i];
                if (edge)
                    continue;
                ++compared;
                const float got = HalfToFloat(frame.visibility[i * 4]);
                if ((got > 0.5f ? 1 : 0) != expected[i])
                    ++wrong;
            }
        char detail[160];
        sprintf_s(detail, "%zu of %zu pixels differ from the CPU's shadow", wrong, compared);
        Check(compared > 100000 && wrong * 1000 <= compared, what, detail);
    }

    void CheckPrimary(const char* what, const Frame& frame)
    {
        size_t compared = 0, close = 0, missed = 0;
        double worst = 0.0;
        for (size_t i = 0; i < static_cast<size_t>(kWidth) * kHeight; ++i)
        {
            const float hit = frame.primary[i * 4];
            const float expected = frame.primary[i * 4 + 1];
            if (expected <= 0.0f)
                continue;
            ++compared;
            if (hit <= 0.0f)
            {
                ++missed;
                continue;
            }
            const double error = std::fabs(hit - expected) / expected;
            worst = std::max(worst, error);
            if (error < 1e-3)
                ++close;
        }
        char detail[200];
        sprintf_s(detail, "%zu of %zu camera rays hit where the depth says (within 0.1 %%), %zu missed", close, compared,
                  missed);
        Check(compared > 100000 && close * 1000 >= compared * 995, what, detail);
    }

    void SaveVisibility(const wchar_t* name, const Frame& frame)
    {
        std::vector<float> rgb(static_cast<size_t>(kWidth) * kHeight * 3);
        for (size_t i = 0; i < static_cast<size_t>(kWidth) * kHeight; ++i)
        {
            const float lit = frame.depth[i] > 0.0f ? 0.15f + 0.85f * HalfToFloat(frame.visibility[i * 4]) : 0.0f;
            const uint32_t packed = frame.normals[i];
            const float up = frame.depth[i] > 0.0f ? ((packed >> 10) & 0x3FF) / 1023.0f : 0.0f;
            rgb[i * 3 + 0] = lit;
            rgb[i * 3 + 1] = lit;
            rgb[i * 3 + 2] = frame.depth[i] > 0.0f ? lit * (0.7f + 0.3f * up) : 0.4f;
        }
        WriteBmp(NextToExecutable(name), kWidth, kHeight, rgb);
    }

    int SendMaterial(Proxy& proxy, int id, const float albedo[3], const float emission[3], float smoothness)
    {
        RtMaterialDesc desc = {};
        desc.size = sizeof(desc);
        desc.magic = kRtMaterialMagic;
        desc.material = id;
        memcpy(desc.albedo, albedo, 3 * sizeof(float));
        desc.albedo[3] = 1.0f;
        desc.specular[0] = desc.specular[1] = desc.specular[2] = 0.04f;
        desc.smoothness = smoothness;
        memcpy(desc.emission, emission, 3 * sizeof(float));
        desc.texture = -1;
        return proxy.setMaterial(&desc);
    }

    // The mean of an output's rgb over many frames, per pixel.
    bool Accumulate(Proxy& proxy, ID3D11Device* device, ID3D11DeviceContext* context, const Camera& camera, GBuffer& g,
                    Outputs& outputs, ID3D11Texture2D* which, uint32_t signals, const Lighting& lighting, Vec3 sun,
                    int frames, uint32_t firstFrame, std::vector<double>& mean)
    {
        mean.assign(static_cast<size_t>(kWidth) * kHeight * 3, 0.0);
        std::vector<uint16_t> read;
        for (int f = 0; f < frames; ++f)
        {
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, firstFrame + f, signals, lighting);
            if (!ReadTexture(device, context, which, 8, read))
                return false;
            for (size_t i = 0; i < static_cast<size_t>(kWidth) * kHeight; ++i)
                for (int c = 0; c < 3; ++c)
                    mean[i * 3 + c] += HalfToFloat(read[i * 4 + c]);
        }
        for (double& value : mean)
            value /= frames;
        return true;
    }

    bool Finite(const std::vector<uint16_t>& halves)
    {
        for (uint16_t h : halves)
            if ((h & 0x7C00u) == 0x7C00u)
                return false;
        return true;
    }

    // The pixel a camera-relative point falls on.
    bool Project(const Camera& camera, Vec3 relative, int& x, int& y)
    {
        const Mat4& m = camera.worldToClip;
        double c[4];
        for (int r = 0; r < 4; ++r)
            c[r] = m.at(r, 0) * relative.x + m.at(r, 1) * relative.y + m.at(r, 2) * relative.z + m.at(r, 3);
        if (c[3] <= 0.0)
            return false;
        x = static_cast<int>((c[0] / c[3] * 0.5 + 0.5) * kWidth);
        y = static_cast<int>((0.5 - c[1] / c[3] * 0.5) * kHeight);
        return x >= 2 && y >= 2 && x < static_cast<int>(kWidth) - 2 && y < static_cast<int>(kHeight) - 2;
    }

    double MeanAround(const std::vector<double>& image, int x, int y, int channel)
    {
        double sum = 0.0;
        for (int dy = -2; dy <= 2; ++dy)
            for (int dx = -2; dx <= 2; ++dx)
                sum += image[(static_cast<size_t>(y + dy) * kWidth + (x + dx)) * 3 + channel];
        return sum / 25.0;
    }

    // Where a ray from origin meets an axis-aligned box, or a negative value.
    double RayBox(Vec3 origin, Vec3 direction, Vec3 centre, const double scale[3])
    {
        const double o[3] = { origin.x, origin.y, origin.z };
        const double d[3] = { direction.x, direction.y, direction.z };
        const double c[3] = { centre.x, centre.y, centre.z };
        double tMin = 0.0, tMax = 1e30;
        for (int a = 0; a < 3; ++a)
        {
            const double lo = c[a] - scale[a] * 0.5, hi = c[a] + scale[a] * 0.5;
            if (std::fabs(d[a]) < 1e-12)
            {
                if (o[a] < lo || o[a] > hi)
                    return -1.0;
                continue;
            }
            double t0 = (lo - o[a]) / d[a], t1 = (hi - o[a]) / d[a];
            if (t0 > t1)
                std::swap(t0, t1);
            tMin = std::max(tMin, t0);
            tMax = std::min(tMax, t1);
            if (tMin > tMax)
                return -1.0;
        }
        return tMin;
    }

    void SaveColour(const wchar_t* name, const std::vector<double>& image, double scale)
    {
        std::vector<float> rgb(image.size());
        for (size_t i = 0; i < image.size(); ++i)
            rgb[i] = static_cast<float>(image[i] * scale);
        WriteBmp(NextToExecutable(name), kWidth, kHeight, rgb);
    }
}

int main()
{
    printf("ReDefinition raytracing harness\n");

    Proxy proxy;
    proxy.module = LoadLibraryW(L"dxgi.dll");
    if (proxy.module == nullptr)
        return Fatal("LoadLibrary(dxgi.dll)", HRESULT_FROM_WIN32(GetLastError()));
    void* (*eventFunc)() = nullptr;
    if (!proxy.Load(proxy.available, "KspRtAvailable") || !proxy.Load(proxy.structSizes, "KspRtStructSizes")
        || !proxy.Load(proxy.setMesh, "KspRtSetMesh") || !proxy.Load(proxy.releaseMesh, "KspRtReleaseMesh")
        || !proxy.Load(proxy.setMaterial, "KspRtSetMaterial") || !proxy.Load(proxy.setInstances, "KspRtSetInstances")
        || !proxy.Load(proxy.status, "KspRtStatus") || !proxy.Load(proxy.timings, "KspRtTimings")
        || !proxy.Load(proxy.passTimings, "KspRtPassTimings")
        || !proxy.Load(eventFunc, "KspFgGetRenderEventFunc"))
        return Fatal("the proxy's raytracing exports", E_FAIL);
    proxy.event = reinterpret_cast<void (*)(int, void*)>(eventFunc());

    unsigned int meshSize = 0, materialSize = 0, instanceSize = 0;
    const unsigned int frameSize = proxy.structSizes(&meshSize, &materialSize, &instanceSize);
    if (frameSize != sizeof(RtFramePacket) || meshSize != sizeof(RtMeshDesc) || materialSize != sizeof(RtMaterialDesc)
        || instanceSize != sizeof(RtInstance))
        return Fatal("the raytracing structures differ between harness and proxy", E_FAIL);

    using CreateFactory2Fn = HRESULT(WINAPI*)(UINT, REFIID, void**);
    const auto createFactory2 = reinterpret_cast<CreateFactory2Fn>(GetProcAddress(proxy.module, "CreateDXGIFactory2"));
    ComPtr<IDXGIFactory2> factory;
    HRESULT hr = createFactory2(0, IID_PPV_ARGS(&factory));
    if (FAILED(hr))
        return Fatal("CreateDXGIFactory2", hr);
    // The GPU for performance, as a game gets it on a machine with two.
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIFactory6> factory6;
    if (SUCCEEDED(factory.As(&factory6)))
        hr = factory6->EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE, IID_PPV_ARGS(&adapter));
    else
        hr = factory->EnumAdapters1(0, &adapter);
    if (FAILED(hr))
        return Fatal("the adapter", hr);
    DXGI_ADAPTER_DESC1 adapterDesc = {};
    adapter->GetDesc1(&adapterDesc);
    wprintf(L"      adapter: %s\n", adapterDesc.Description);

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device,
                           nullptr, &context);
    if (FAILED(hr))
        return Fatal("D3D11CreateDevice", hr);

    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"ReDefinitionRaytracingHarness";
    RegisterClassExW(&wc);
    const HWND window = CreateWindowExW(0, wc.lpszClassName, L"ReDefinition raytracing harness", WS_OVERLAPPEDWINDOW,
                                        CW_USEDEFAULT, CW_USEDEFAULT, kWidth, kHeight, nullptr, nullptr, wc.hInstance,
                                        nullptr);
    ShowWindow(window, SW_SHOW);

    DXGI_SWAP_CHAIN_DESC1 desc = {};
    desc.Width = kWidth;
    desc.Height = kHeight;
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
        return Fatal("CreateSwapChainForHwnd", hr);

    char reason[256] = {};
    const int ready = proxy.available(reason, sizeof(reason));
    printf("      raytracing: %s\n", reason);
    if (ready != 1)
        return Fatal("the proxy's raytracing is not ready", E_FAIL);

    GBuffer g;
    hr = CreateGBuffer(device.Get(), g);
    if (FAILED(hr))
        return Fatal("the G-buffer", hr);
    Outputs outputs;
    hr = CreateTarget(device.Get(), DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_BIND_SHADER_RESOURCE, outputs.visibility);
    if (SUCCEEDED(hr))
        hr = CreateTarget(device.Get(), DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_BIND_SHADER_RESOURCE, outputs.diffuse);
    if (SUCCEEDED(hr))
        hr = CreateTarget(device.Get(), DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_BIND_SHADER_RESOURCE, outputs.specular);
    if (SUCCEEDED(hr))
        hr = CreateTarget(device.Get(), DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_BIND_SHADER_RESOURCE, outputs.occlusion);
    if (SUCCEEDED(hr))
        hr = CreateTarget(device.Get(), DXGI_FORMAT_R32G32B32A32_FLOAT, D3D11_BIND_SHADER_RESOURCE, outputs.primary);
    if (FAILED(hr))
        return Fatal("the output textures", hr);

    // ---------------------------------------------------------------- a box on a plane
    std::map<int, Mesh> meshes;
    meshes[1] = MakeCube(1);
    meshes[2] = MakePlane(2, 32);
    for (auto& entry : meshes)
    {
        if (FAILED(UploadMeshForRaster(device.Get(), entry.second)) || SendMesh(proxy, entry.second) != 1)
            return Fatal("a mesh", E_FAIL);
    }

    std::vector<Instance> scene;
    Instance plane;
    plane.mesh = 2;
    plane.scale[0] = plane.scale[2] = 400.0;
    plane.albedo[0] = 0.6f; plane.albedo[1] = 0.6f; plane.albedo[2] = 0.55f;
    scene.push_back(plane);
    std::vector<Instance> boxes;
    Instance box;
    box.mesh = 1;
    box.position = { 0, 5, 0 };
    box.scale[0] = box.scale[1] = box.scale[2] = 10.0;
    box.albedo[0] = 0.8f; box.albedo[1] = 0.3f; box.albedo[2] = 0.2f;
    scene.push_back(box);
    boxes.push_back(box);
    Instance tower;
    tower.mesh = 1;
    tower.position = { -25, 15, 20 };
    tower.scale[0] = 4.0; tower.scale[1] = 30.0; tower.scale[2] = 4.0;
    tower.albedo[0] = 0.3f; tower.albedo[1] = 0.5f; tower.albedo[2] = 0.8f;
    scene.push_back(tower);
    boxes.push_back(tower);

    Camera camera;
    camera.position = { 28, 20, -40 };
    camera.target = { 0, 5, 0 };
    camera.Update();
    const Vec3 sun = { -0.5, 0.7, 0.4 };

    SendInstances(proxy, scene, camera.position);
    RenderGBuffer(context.Get(), g, camera, meshes, scene);
    SendFrame(proxy, camera, g, outputs, sun, 0.0f, 1, kRtSignalSunVisibility | kRtSignalPrimaryCheck);
    Frame first;
    if (!ReadFrame(device.Get(), context.Get(), g, outputs, first))
        return Fatal("reading the frame back", E_FAIL);
    printf("      %s\n", proxy.Status().c_str());
    CheckPrimary("primary rays", first);
    CheckVisibility("sun shadows", first, ExpectedVisibility(camera, first.depth, first.normals, boxes, sun));
    SaveVisibility(L"rt_shadows.bmp", first);

    // ---------------------------------------------------------------- 10 km away
    {
        const Vec3 offset = { 10000.0, 0.0, -10000.0 };
        std::vector<Instance> moved = scene;
        std::vector<Instance> movedBoxes = boxes;
        for (Instance& instance : moved)
            instance.position = instance.position + offset;
        for (Instance& instance : movedBoxes)
            instance.position = instance.position + offset;
        Camera distant = camera;
        distant.position = camera.position + offset;
        distant.target = camera.target + offset;
        distant.Update();
        SendInstances(proxy, moved, distant.position);
        RenderGBuffer(context.Get(), g, distant, meshes, moved);
        SendFrame(proxy, distant, g, outputs, sun, 0.0f, 2, kRtSignalSunVisibility | kRtSignalPrimaryCheck);
        Frame shifted;
        if (!ReadFrame(device.Get(), context.Get(), g, outputs, shifted))
            return Fatal("reading the shifted frame back", E_FAIL);
        size_t differ = 0, lit = 0;
        for (size_t i = 0; i < static_cast<size_t>(kWidth) * kHeight; ++i)
        {
            const bool a = HalfToFloat(first.visibility[i * 4]) > 0.5f;
            const bool b = HalfToFloat(shifted.visibility[i * 4]) > 0.5f;
            differ += a != b ? 1 : 0;
            lit += a ? 1 : 0;
        }
        char detail[160];
        sprintf_s(detail, "%zu of %zu pixels differ after moving scene and camera by 14 km", differ,
                  static_cast<size_t>(kWidth) * kHeight);
        Check(differ * 10000 <= static_cast<size_t>(kWidth) * kHeight && lit > 0, "floating origin", detail);
    }

    // ---------------------------------------------------------------- soft shadows
    {
        SendInstances(proxy, scene, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        // The sun's real angular radius is 0.0047 rad; a larger one shows the penumbra.
        std::vector<double> sum(static_cast<size_t>(kWidth) * kHeight, 0.0);
        const int samples = 64;
        Frame soft;
        for (int s = 0; s < samples; ++s)
        {
            SendFrame(proxy, camera, g, outputs, sun, 0.05f, 100 + s, kRtSignalSunVisibility);
            if (!ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, soft.visibility))
                return Fatal("reading the soft shadows back", E_FAIL);
            for (size_t i = 0; i < sum.size(); ++i)
                sum[i] += HalfToFloat(soft.visibility[i * 4]);
        }
        size_t penumbra = 0;
        for (double& value : sum)
        {
            value /= samples;
            penumbra += value > 0.05 && value < 0.95 ? 1 : 0;
        }
        char detail[160];
        sprintf_s(detail, "%zu pixels between shadow and light after %d frames", penumbra, samples);
        Check(penumbra > 1000, "soft shadows", detail);
        Frame image = first;
        for (size_t i = 0; i < sum.size(); ++i)
        {
            const float v = static_cast<float>(sum[i]);
            uint32_t half = 0;
            // Back into a half for SaveVisibility: exact for 0 and 1, close between.
            const float clamped = std::min(std::max(v, 0.0f), 1.0f);
            uint32_t bits;
            memcpy(&bits, &clamped, 4);
            half = clamped == 0.0f ? 0 : static_cast<uint16_t>(((bits >> 16) & 0x8000) | ((((bits >> 23) & 0xFF) - 112) << 10)
                                                             | ((bits >> 13) & 0x3FF));
            image.visibility[i * 4] = static_cast<uint16_t>(half);
        }
        SaveVisibility(L"rt_soft_shadows.bmp", image);
    }

    // ---------------------------------------------------------------- white furnace
    {
        // Every surface white, the sky uniformly 1, no sun: light arriving anywhere is 1,
        // however often it bounces.
        const float white[3] = { 1.0f, 1.0f, 1.0f };
        const float none[3] = { 0.0f, 0.0f, 0.0f };
        if (SendMaterial(proxy, 10, white, none, 0.2f) != 1)
            return Fatal("a material", E_FAIL);
        std::vector<Instance> furnace = scene;
        for (Instance& instance : furnace)
            instance.material = 10;
        SendInstances(proxy, furnace, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, furnace);
        Lighting lighting;
        lighting.sunColor[0] = lighting.sunColor[1] = lighting.sunColor[2] = 0.0f;
        lighting.bounces = 16.0f;
        std::vector<double> mean;
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                        lighting, sun, 256, 3000, mean))
            return Fatal("reading the furnace back", E_FAIL);
        std::vector<float> depth;
        ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
        double total = 0.0;
        size_t count = 0, within = 0;
        for (size_t i = 0; i < depth.size(); ++i)
        {
            if (depth[i] <= 0.0f)
                continue;
            const double value = (mean[i * 3] + mean[i * 3 + 1] + mean[i * 3 + 2]) / 3.0;
            total += value;
            ++count;
            within += std::fabs(value - 1.0) < 0.05 ? 1 : 0;
        }
        const double average = count > 0 ? total / count : 0.0;
        char detail[200];
        sprintf_s(detail, "diffuse light %.4f on average over %zu pixels, %.1f %% of them within 5 %% of 1", average, count,
                  count > 0 ? 100.0 * within / count : 0.0);
        Check(count > 100000 && std::fabs(average - 1.0) < 0.02 && within * 100 >= count * 95, "white furnace", detail);
        SaveColour(L"rt_furnace.bmp", mean, 0.8);
    }

    // ---------------------------------------------------------------- occlusion
    {
        SendInstances(proxy, scene, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        Lighting lighting;
        std::vector<double> mean;
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.occlusion.Get(),
                        kRtSignalAmbientOcclusion, lighting, sun, 256, 4000, mean))
            return Fatal("reading the occlusion back", E_FAIL);
        int openX = 0, openY = 0, cornerX = 0, cornerY = 0;
        const bool seen = Project(camera, Vec3{ 15, 0, -15 } - camera.position, openX, openY)
                          && Project(camera, Vec3{ 0, 0.05, -5.25 } - camera.position, cornerX, cornerY);
        const double open = seen ? MeanAround(mean, openX, openY, 0) : 0.0;
        const double corner = seen ? MeanAround(mean, cornerX, cornerY, 0) : 1.0;
        char detail[160];
        sprintf_s(detail, "the open plane sees %.3f of the sky, the foot of the box %.3f", open, corner);
        Check(seen && open > 0.98 && corner < 0.8, "ambient occlusion", detail);
        SaveColour(L"rt_occlusion.bmp", mean, 1.0);
    }

    // ---------------------------------------------------------------- mirror
    {
        // A mirror-smooth plane, the tower glowing and black, a dim uniform sky: the plane
        // reflects the tower's glow where its reflection is, and the sky elsewhere.
        const float black[3] = { 0.0f, 0.0f, 0.0f };
        const float glow[3] = { 4.0f, 2.0f, 1.0f };
        if (SendMaterial(proxy, 20, black, glow, 0.2f) != 1)
            return Fatal("a material", E_FAIL);
        std::vector<Instance> mirrored = scene;
        mirrored[0].smoothness = 1.0f;
        mirrored[2].material = 20;
        // Beside the box as the camera sees it, so that its reflection is not behind the box's.
        mirrored[2].position = { -22, 15, -12 };
        SendInstances(proxy, mirrored, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, mirrored);
        Lighting lighting;
        lighting.sunColor[0] = lighting.sunColor[1] = lighting.sunColor[2] = 0.0f;
        lighting.sky[0] = lighting.sky[1] = lighting.sky[2] = 0.2f;
        lighting.bounces = 0.0f;
        std::vector<double> mean;
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.specular.Get(), kRtSignalSpecular,
                        lighting, sun, 16, 5000, mean))
            return Fatal("reading the reflections back", E_FAIL);
        std::vector<float> depth;
        std::vector<uint32_t> normals;
        ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
        ReadTexture(device.Get(), context.Get(), g.gbuffer2.Get(), 4, normals);
        // 0 sky, 1 tower, 2 box or not the plane.
        std::vector<int> expected(depth.size(), 2);
        for (UINT y = 0; y < kHeight; ++y)
            for (UINT x = 0; x < kWidth; ++x)
            {
                const size_t i = static_cast<size_t>(y) * kWidth + x;
                if (depth[i] <= 0.0f || ((normals[i] >> 10) & 0x3FF) < 1020)
                    continue;
                const double nx = ((x + 0.5) / kWidth) * 2.0 - 1.0;
                const double ny = 1.0 - ((y + 0.5) / kHeight) * 2.0;
                double c[4];
                for (int r = 0; r < 4; ++r)
                    c[r] = camera.clipToWorld.at(r, 0) * nx + camera.clipToWorld.at(r, 1) * ny
                           + camera.clipToWorld.at(r, 2) * depth[i] + camera.clipToWorld.at(r, 3);
                const Vec3 position = { c[0] / c[3], c[1] / c[3], c[2] / c[3] };
                const Vec3 view = Normalize(position);
                const Vec3 reflected = { view.x, -view.y, view.z };
                const Vec3 origin = position + Vec3{ 0, 1, 0 } * (0.01 + Length(position) * 0.0005);
                const double toTower = RayBox(origin, reflected, mirrored[2].position - camera.position, mirrored[2].scale);
                const double toBox = RayBox(origin, reflected, mirrored[1].position - camera.position, mirrored[1].scale);
                if (toBox >= 0.0 && (toTower < 0.0 || toBox < toTower))
                    continue;
                expected[i] = toTower >= 0.0 ? 1 : 0;
            }
        // Two pixels off an edge: a smoothness of 1 is still Unity's smallest roughness,
        // whose lobe reaches that far past the tower's side.
        size_t towerPixels = 0, towerRight = 0, skyPixels = 0, skyRight = 0;
        for (UINT y = 2; y + 2 < kHeight; ++y)
            for (UINT x = 2; x + 2 < kWidth; ++x)
            {
                const size_t i = static_cast<size_t>(y) * kWidth + x;
                if (expected[i] == 2)
                    continue;
                bool edge = false;
                for (int dy = -2; dy <= 2 && !edge; ++dy)
                    for (int dx = -2; dx <= 2 && !edge; ++dx)
                        edge = expected[i + dy * static_cast<int>(kWidth) + dx] != expected[i];
                if (edge)
                    continue;
                const double* got = &mean[i * 3];
                if (expected[i] == 1)
                {
                    ++towerPixels;
                    // GGX's long tail sends a ray in a few dozen past the tower's side even at
                    // Unity's smallest roughness: within 15 %.
                    towerRight += std::fabs(got[0] - 4.0) < 0.6 && std::fabs(got[1] - 2.0) < 0.3
                                          && std::fabs(got[2] - 1.0) < 0.15
                                      ? 1
                                      : 0;
                }
                else
                {
                    ++skyPixels;
                    skyRight += std::fabs(got[0] - 0.2) < 0.006 && std::fabs(got[2] - 0.2) < 0.006 ? 1 : 0;
                }
            }
        char detail[200];
        sprintf_s(detail, "%zu of %zu pixels show the tower's glow, %zu of %zu the sky", towerRight, towerPixels, skyRight,
                  skyPixels);
        Check(towerPixels > 2000 && towerRight * 100 >= towerPixels * 99 && skyRight * 100 >= skyPixels * 99, "mirror",
              detail);
        SaveColour(L"rt_mirror.bmp", mean, 0.25);
    }

    // ---------------------------------------------------------------- environment
    {
        // A sky that is its own direction: red the angle around the vertical, green the
        // angle from straight up. A mirror-smooth plane alone shows it reflected.
        constexpr UINT envWidth = 256, envHeight = 128;
        std::vector<uint16_t> texels(static_cast<size_t>(envWidth) * envHeight * 4);
        auto toHalf = [](float value)
        {
            uint32_t bits;
            memcpy(&bits, &value, 4);
            if (value == 0.0f)
                return static_cast<uint16_t>(0);
            return static_cast<uint16_t>(((bits >> 16) & 0x8000) | ((((bits >> 23) & 0xFF) - 112) << 10) | ((bits >> 13) & 0x3FF));
        };
        for (UINT y = 0; y < envHeight; ++y)
            for (UINT x = 0; x < envWidth; ++x)
            {
                uint16_t* t = &texels[(static_cast<size_t>(y) * envWidth + x) * 4];
                t[0] = toHalf((x + 0.5f) / envWidth);
                t[1] = toHalf((y + 0.5f) / envHeight);
                t[2] = toHalf(0.25f);
                t[3] = toHalf(1.0f);
            }
        D3D11_TEXTURE2D_DESC envDesc = {};
        envDesc.Width = envWidth;
        envDesc.Height = envHeight;
        envDesc.MipLevels = 1;
        envDesc.ArraySize = 1;
        envDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        envDesc.SampleDesc.Count = 1;
        envDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        const D3D11_SUBRESOURCE_DATA envData = { texels.data(), envWidth * 8, 0 };
        ComPtr<ID3D11Texture2D> environment;
        if (FAILED(device->CreateTexture2D(&envDesc, &envData, &environment)))
            return Fatal("the environment texture", E_FAIL);

        std::vector<Instance> alone = { scene[0] };
        alone[0].smoothness = 1.0f;
        SendInstances(proxy, alone, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, alone);
        Lighting lighting;
        lighting.sunColor[0] = lighting.sunColor[1] = lighting.sunColor[2] = 0.0f;
        lighting.bounces = 0.0f;
        lighting.environment = environment.Get();
        std::vector<double> mean;
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.specular.Get(), kRtSignalSpecular,
                        lighting, sun, 16, 6000, mean))
            return Fatal("reading the environment back", E_FAIL);
        std::vector<float> depth;
        ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
        size_t compared = 0, right = 0;
        for (UINT y = 0; y < kHeight; ++y)
            for (UINT x = 0; x < kWidth; ++x)
            {
                const size_t i = static_cast<size_t>(y) * kWidth + x;
                if (depth[i] <= 0.0f)
                    continue;
                const double nx = ((x + 0.5) / kWidth) * 2.0 - 1.0;
                const double ny = 1.0 - ((y + 0.5) / kHeight) * 2.0;
                double c[4];
                for (int r = 0; r < 4; ++r)
                    c[r] = camera.clipToWorld.at(r, 0) * nx + camera.clipToWorld.at(r, 1) * ny
                           + camera.clipToWorld.at(r, 2) * depth[i] + camera.clipToWorld.at(r, 3);
                const Vec3 view = Normalize({ c[0] / c[3], c[1] / c[3], c[2] / c[3] });
                const Vec3 reflected = { view.x, -view.y, view.z };
                const double u = std::atan2(reflected.x, reflected.z) / (2.0 * 3.14159265358979) + 0.5;
                const double v = std::acos(std::min(std::max(reflected.y, -1.0), 1.0)) / 3.14159265358979;
                if (u < 0.02 || u > 0.98 || v < 0.02)
                    continue;
                ++compared;
                right += std::fabs(mean[i * 3] - u) < 0.02 && std::fabs(mean[i * 3 + 1] - v) < 0.02
                                 && std::fabs(mean[i * 3 + 2] - 0.25) < 0.005
                             ? 1
                             : 0;
            }
        char detail[160];
        sprintf_s(detail, "%zu of %zu pixels reflect the sky in the direction they should", right, compared);
        Check(compared > 100000 && right * 100 >= compared * 99, "environment", detail);
        SaveColour(L"rt_environment.bmp", mean, 1.0);
    }

    // ---------------------------------------------------------------- KSP's launch pad
    {
        wchar_t scenePath[MAX_PATH] = {};
        if (GetEnvironmentVariableW(L"REDEFINITION_RT_SCENE", scenePath, MAX_PATH) == 0)
            printf("      KSP scene skipped: REDEFINITION_RT_SCENE names no file (tools/export_rt_scene.py)\n");
        else
        {
            std::map<int, Mesh> kspMeshes;
            std::vector<Instance> ksp;
            if (!LoadScene(scenePath, kspMeshes, ksp, 5000))
                return Fatal("the KSP scene file", E_FAIL);
            size_t triangles = 0;
            for (auto& entry : kspMeshes)
            {
                triangles += entry.second.indices.size() / 3;
                if (FAILED(UploadMeshForRaster(device.Get(), entry.second)) || SendMesh(proxy, entry.second) != 1)
                    return Fatal("a KSP mesh", E_FAIL);
                meshes[entry.first] = entry.second;
            }
            // The ground the pad stands on.
            Instance ground = scene[0];
            ground.scale[0] = ground.scale[2] = 4000.0;
            ground.position = { 0.0, -0.2, 0.0 };
            ksp.push_back(ground);

            Camera view;
            view.position = { 55.0, 35.0, -70.0 };
            view.target = { 0.0, 8.0, 0.0 };
            view.Update();
            SendInstances(proxy, ksp, view.position);
            RenderGBuffer(context.Get(), g, view, meshes, ksp);
            const uint32_t all = kRtSignalSunVisibility | kRtSignalAmbientOcclusion | kRtSignalDiffuse | kRtSignalSpecular;
            Lighting lighting;
            lighting.sky[0] = 0.35f;
            lighting.sky[1] = 0.45f;
            lighting.sky[2] = 0.6f;
            lighting.bounces = 2.0f;
            lighting.clamp = 20.0f;
            SendFrame(proxy, view, g, outputs, sun, 0.0047f, 7000, all | kRtSignalPrimaryCheck, lighting);
            Frame frame;
            if (!ReadFrame(device.Get(), context.Get(), g, outputs, frame))
                return Fatal("reading the KSP scene back", E_FAIL);
            char label[96];
            sprintf_s(label, "KSP scene: primary rays (%zu meshes, %zu triangles)", kspMeshes.size(), triangles);
            CheckPrimary(label, frame);

            std::vector<double> diffuse, specular;
            if (!Accumulate(proxy, device.Get(), context.Get(), view, g, outputs, outputs.diffuse.Get(), all, lighting, sun,
                            32, 7100, diffuse))
                return Fatal("reading the KSP scene back", E_FAIL);
            std::vector<uint16_t> halves[4];
            bool finite = true;
            ID3D11Texture2D* written[4] = { outputs.visibility.Get(), outputs.diffuse.Get(), outputs.specular.Get(),
                                            outputs.occlusion.Get() };
            for (int k = 0; k < 4; ++k)
                finite = finite && ReadTexture(device.Get(), context.Get(), written[k], 8, halves[k]) && Finite(halves[k]);
            Check(finite, "KSP scene: no NaN or infinity", "sun, occlusion, diffuse and specular after 33 frames");

            // A picture of what the signals make together: albedo times sun and diffuse light.
            std::vector<uint32_t> albedo;
            ReadTexture(device.Get(), context.Get(), g.gbuffer0.Get(), 4, albedo);
            std::vector<double> lit(diffuse.size(), 0.0);
            const Vec3 l = Normalize(sun);
            for (size_t i = 0; i < static_cast<size_t>(kWidth) * kHeight; ++i)
            {
                if (frame.depth[i] <= 0.0f)
                {
                    lit[i * 3] = lighting.sky[0];
                    lit[i * 3 + 1] = lighting.sky[1];
                    lit[i * 3 + 2] = lighting.sky[2];
                    continue;
                }
                const uint32_t packed = frame.normals[i];
                const Vec3 n = Normalize({ (packed & 0x3FF) / 1023.0 * 2 - 1, ((packed >> 10) & 0x3FF) / 1023.0 * 2 - 1,
                                           ((packed >> 20) & 0x3FF) / 1023.0 * 2 - 1 });
                const double direct = std::max(0.0, Dot(n, l)) * HalfToFloat(halves[0][i * 4]);
                for (int c = 0; c < 3; ++c)
                {
                    const double a = std::pow(((albedo[i] >> (8 * c)) & 0xFF) / 255.0, 2.2);
                    lit[i * 3 + c] = a * (direct + diffuse[i * 3 + c]);
                }
            }
            SaveColour(L"rt_ksp_lit.bmp", lit, 1.0);
            SaveColour(L"rt_ksp_diffuse.bmp", diffuse, 1.0);

            float passes[kRtPassTimings] = {};
            float build = 0, trace = 0;
            proxy.timings(&build, &trace);
            proxy.passTimings(passes, kRtPassTimings);
            printf("      KSP scene at %ux%u: scene build %.3f ms; sun %.3f, occlusion %.3f, diffuse %.3f, specular %.3f ms\n",
                   kWidth, kHeight, build, passes[0], passes[1], passes[2], passes[3]);
            for (auto& entry : kspMeshes)
            {
                proxy.releaseMesh(entry.first);
                meshes.erase(entry.first);
            }
        }
    }

    // ---------------------------------------------------------------- churn
    {
        std::vector<Instance> crowd = scene;
        for (int i = 0; i < 20000; ++i)
        {
            Instance tiny;
            tiny.mesh = 1;
            tiny.position = { -3000.0 + (i % 200) * 30.0, 1.0, 3000.0 + (i / 200) * 30.0 };
            crowd.push_back(tiny);
        }
        std::map<int, Mesh> extra;
        int nextId = 1000;
        bool accepted = true;
        for (int frame = 0; frame < 60; ++frame)
        {
            for (int k = 0; k < 20; ++k)
            {
                Mesh m = MakeCube(nextId++);
                accepted = accepted && SendMesh(proxy, m) == 1;
                extra[m.id] = std::move(m);
            }
            while (extra.size() > 100)
            {
                proxy.releaseMesh(extra.begin()->first);
                extra.erase(extra.begin());
            }
            std::vector<Instance> all = crowd;
            for (auto& entry : extra)
            {
                Instance extraInstance;
                extraInstance.mesh = entry.first;
                extraInstance.position = { double(entry.first % 50) * 3.0, 50.0, 200.0 };
                all.push_back(extraInstance);
            }
            SendInstances(proxy, all, camera.position);
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, 1000 + frame, kRtSignalSunVisibility);
        }
        const std::string status = proxy.Status();
        printf("      %s\n", status.c_str());
        char expectedMeshes[64];
        sprintf_s(expectedMeshes, "meshes=%zu ", meshes.size() + extra.size());
        Check(accepted && status.find(expectedMeshes) != std::string::npos && status.find("instances=20103") != std::string::npos
                  && status.find("last problem") == std::string::npos,
              "churn", "20 meshes added and 20 released a frame for 60 frames under 20 000 instances");
    }

    // ---------------------------------------------------------------- timings
    {
        SendInstances(proxy, scene, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        const uint32_t all = kRtSignalSunVisibility | kRtSignalAmbientOcclusion | kRtSignalDiffuse | kRtSignalSpecular;
        for (int i = 0; i < 8; ++i)
            SendFrame(proxy, camera, g, outputs, sun, 0.0047f, 2000 + i, all);
        float build = 0, trace = 0;
        proxy.timings(&build, &trace);
        float passes[kRtPassTimings] = {};
        proxy.passTimings(passes, kRtPassTimings);
        printf("      timings at %ux%u: scene build %.3f ms; sun %.3f, occlusion %.3f, diffuse %.3f, specular %.3f;"
               " all passes %.3f ms\n", kWidth, kHeight, build, passes[0], passes[1], passes[2], passes[3], trace);
    }

    printf(failures == 0 ? "all raytracing checks passed\n" : "%d raytracing check(s) failed\n", failures);
    return failures == 0 ? 0 : 1;
}
