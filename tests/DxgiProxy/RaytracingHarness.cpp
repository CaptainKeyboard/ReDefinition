// The raytracing harness (docs/development/flow-raytracing.md, "Testing without the game").
//
// It loads the proxy as Unity does, rasterises a G-buffer in Unity's layout with its
// own Direct3D 11 shaders, hands meshes, materials and instances to the proxy's
// raytracing service through its exports, sends frames through the render event, and
// checks what comes back against the CPU. Images of every check go next to the
// executable, as BMP, for a look.
//
// 0 when every check passes.

#include "D3d12Util.h"
#include "Dlss.h"
#include "Raytracing.h"

#include <windows.h>

#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
using namespace redefinition;

namespace
{
    // The frame's size in pixels, and the render event a frame goes through (event 30).
    constexpr UINT kWidth = 1280;
    constexpr UINT kHeight = 720;
    constexpr int kFrameEvent = RT_FRAME_EVENT;

    // The checks failed so far; main returns 1 where any did.
    int failures = 0;

    // A check's line, PASS or FAIL, with what it checks and what it found; a failure counted.
    void Check(bool passed, const char* what, const std::string& detail)
    {
        printf("%s  %s -- %s\n", passed ? "PASS" : "FAIL", what, detail.c_str());
        if (!passed)
            ++failures;
    }

    // A failure that ends the run, with its HRESULT; 1, for main to return.
    int Fatal(const char* what, HRESULT hr)
    {
        printf("FAIL  %s (0x%08lX)\n", what, static_cast<unsigned long>(hr));
        return 1;
    }

    // The path of a file of that name in the harness executable's folder.
    std::wstring NextToExecutable(const wchar_t* name)
    {
        wchar_t path[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, path, MAX_PATH);
        std::wstring result(path);
        return result.substr(0, result.find_last_of(L"\\/") + 1) + name;
    }

    // ------------------------------------------------------------------ maths

    // A point or direction in double precision, as the CPU's references take it.
    struct Vec3
    {
        // Its components, in Unity's axes (y up).
        double x = 0, y = 0, z = 0;
    };
    Vec3 operator+(Vec3 a, Vec3 b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
    Vec3 operator-(Vec3 a, Vec3 b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
    Vec3 operator*(Vec3 a, double s) { return { a.x * s, a.y * s, a.z * s }; }
    // The dot product of the two.
    double Dot(Vec3 a, Vec3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
    // The cross product a x b.
    Vec3 Cross(Vec3 a, Vec3 b) { return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x }; }
    // The vector's euclidean length.
    double Length(Vec3 a) { return std::sqrt(Dot(a, a)); }
    // The vector scaled to length 1; not for a zero vector.
    Vec3 Normalize(Vec3 a) { return a * (1.0 / Length(a)); }

    // Rows first, for column vectors.
    struct Mat4
    {
        // The sixteen elements, row by row.
        double m[16] = {};
        // The element in row r and column c, to write.
        double& at(int r, int c) { return m[r * 4 + c]; }
        // The element in row r and column c, to read.
        double at(int r, int c) const { return m[r * 4 + c]; }
    };

    // The product a * b: b applies first to a column vector.
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

    // The inverse of an invertible matrix.
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

    // The matrix as the packets take it: sixteen floats, rows first.
    void ToFloats(const Mat4& a, float* out)
    {
        for (int i = 0; i < 16; ++i)
            out[i] = static_cast<float>(a.m[i]);
    }

    // ------------------------------------------------------------------ scene

    // A test mesh: the id it is handed to the proxy under, its data as RtMeshDesc takes it,
    // and its buffers for the rasterised G-buffer (UploadMeshForRaster).
    struct Mesh
    {
        // The id; three floats a vertex for the positions and for the normals.
        int id = 0;
        std::vector<float> positions;
        std::vector<float> normals;
        std::vector<float> uvs;   // two a vertex, or none
        std::vector<uint32_t> colors;   // RGBA bytes a vertex, or none
        std::vector<uint32_t> indices;
        ComPtr<ID3D11Buffer> vertexBuffer;
        ComPtr<ID3D11Buffer> indexBuffer;
    };

    // An instance of a test mesh: drawn into the G-buffer with its albedo, smoothness and
    // specular, and handed to the proxy camera-relative (SendInstances).
    struct Instance
    {
        // The ids of its mesh and its material.
        int mesh = 0;
        int material = 0;
        Vec3 position;        // in the world, in double precision
        double scale[3] = { 1, 1, 1 };
        float albedo[3] = { 0.5f, 0.5f, 0.5f };
        float smoothness = 0.2f;
        // The G-buffer's specular colour, grey.
        float specular = 0.04f;
        // RtInstance's flags (kRtInstance*).
        uint32_t flags = 0;
        // Marked as made shinier after the G-buffer pass: the alpha of the normals 0 where Unity writes 1.
        bool wet = false;
        // A full transform in place of position and scale, rows first, mesh to world.
        bool hasMatrix = false;
        double matrix[12] = {};
    };

    // A quad of the corners a to d in order, as two triangles with the normal n at each corner.
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

    // A tube from -0.5 to 0.5 in y, of radius 0.5, closed at both ends: a vessel's part of
    // 2 * segments * (rings + 1) triangles.
    Mesh MakeTube(int id, int segments, int rings)
    {
        Mesh m;
        m.id = id;
        const double pi = 3.14159265358979;
        for (int r = 0; r <= rings; ++r)
            for (int s = 0; s <= segments; ++s)
            {
                const double angle = 2.0 * pi * s / segments;
                const double y = -0.5 + double(r) / rings;
                m.positions.insert(m.positions.end(), { float(0.5 * std::cos(angle)), float(y), float(0.5 * std::sin(angle)) });
                m.normals.insert(m.normals.end(), { float(std::cos(angle)), 0.0f, float(std::sin(angle)) });
            }
        for (int r = 0; r < rings; ++r)
            for (int s = 0; s < segments; ++s)
            {
                const uint32_t a = static_cast<uint32_t>(r * (segments + 1) + s), b = a + 1;
                const uint32_t c = a + static_cast<uint32_t>(segments + 1), d = c + 1;
                m.indices.insert(m.indices.end(), { a, c, b, b, c, d });
            }
        for (int end = 0; end < 2; ++end)
        {
            const double y = end == 0 ? -0.5 : 0.5;
            const uint32_t centre = static_cast<uint32_t>(m.positions.size() / 3);
            m.positions.insert(m.positions.end(), { 0.0f, float(y), 0.0f });
            m.normals.insert(m.normals.end(), { 0.0f, end == 0 ? -1.0f : 1.0f, 0.0f });
            for (int s = 0; s <= segments; ++s)
            {
                const double angle = 2.0 * pi * s / segments;
                m.positions.insert(m.positions.end(), { float(0.5 * std::cos(angle)), float(y), float(0.5 * std::sin(angle)) });
                m.normals.insert(m.normals.end(), { 0.0f, end == 0 ? -1.0f : 1.0f, 0.0f });
            }
            for (int s = 0; s < segments; ++s)
            {
                const uint32_t a = centre + 1 + static_cast<uint32_t>(s);
                if (end == 0)
                    m.indices.insert(m.indices.end(), { centre, a, a + 1 });
                else
                    m.indices.insert(m.indices.end(), { centre, a + 1, a });
            }
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

    // The test camera, as Unity's would be: where it is and looks, its projection, and the
    // matrices the packets take (Update).
    struct Camera
    {
        // In the world; the vertical field of view in radians, the near and far planes; the
        // camera-relative world to clip space, and back.
        Vec3 position;
        Vec3 target;
        double fov = 60.0 * 3.14159265358979 / 180.0;
        double nearPlane = 0.3;
        double farPlane = 100000.0;
        Mat4 worldToClip;   // camera-relative
        Mat4 clipToWorld;

        // The matrices from the position, target and projection, at the frame's aspect.
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

    // The G-buffer's shaders, HLSL compiled at run time: depth as clip-space z, the albedo,
    // the specular grey and smoothness, the world normal as n * 0.5 + 0.5, as Unity lays them out.
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
    o.gbuffer1 = float4(surface.yyy, surface.x);
    o.gbuffer2 = float4(normalize(input.normal) * 0.5 + 0.5, surface.z > 0.5 ? 0.0 : 1.0);
    return o;
}
)";

    // cbuffer Draw of kGBufferShader, per instance.
    struct DrawConstants
    {
        // The camera's matrix, the instance's camera-relative transform, its albedo, and its
        // smoothness and specular grey in x and y.
        float worldToClip[16];
        float model[12];
        float albedo[4];
        float surface[4];
    };

    // The rasterised G-buffer's textures, as the frame's inputs, and what draws them.
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

    // A texture of the frame's size in that format, bound as asked.
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

    // The G-buffer's textures and views, its shaders compiled, and its states: reversed depth
    // (GREATER), no culling.
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

    // The mesh's vertex buffer (position and normal interleaved) and index buffer for drawing.
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

    // The G-buffer cleared (depth 0, the normal up) and every instance drawn into it.
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
            constants.surface[1] = instance.specular;
            constants.surface[2] = instance.wet ? 1.0f : 0.0f;
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

    // A texture's texels through a staging copy, rows packed, texelSize bytes each; false where
    // the copy could not be made or mapped.
    template <typename T>
    bool ReadTexture(ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* texture, UINT texelSize,
                     std::vector<T>& out)
    {
        D3D11_TEXTURE2D_DESC desc = {};
        texture->GetDesc(&desc);
        desc = StagingDesc(desc);
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

    // The proxy DLL loaded as Unity loads it, and the exports the harness calls.
    struct Proxy
    {
        // The loaded dxgi.dll.
        HMODULE module = nullptr;
        // KspRtAvailable: 1 ready, 0 no device yet, -1 not possible, the reason in the text.
        int (*available)(char*, int) = nullptr;
        // KspRtStructSizes: the mesh's, the material's and the instance's sizes as the proxy has
        // them; the frame packet's returned.
        unsigned int (*structSizes)(unsigned int*, unsigned int*, unsigned int*) = nullptr;
        // Raytracing::SetMesh.
        int (*setMesh)(const RtMeshDesc*) = nullptr;
        // Raytracing::ReleaseMesh.
        void (*releaseMesh)(int) = nullptr;
        // Raytracing::SetMaterial.
        int (*setMaterial)(const RtMaterialDesc*) = nullptr;
        // Raytracing::SetMaterialTexture: a layer of the material textures, RGBA8.
        int (*setMaterialTexture)(int, const uint8_t*, int) = nullptr;
        // Raytracing::SetInstances.
        int (*setInstances)(const RtInstance*, int, unsigned int) = nullptr;
        // Raytracing::SetGpuGroups: the GPU instance groups of a frame.
        int (*setGpuGroups)(const RtGpuInstanceGroup*, int, unsigned int) = nullptr;
        // Raytracing::PassTimings.
        void (*passTimings)(float*, int) = nullptr;
        // Raytracing::ReconstructionAvailable.
        int (*reconstructionAvailable)(char*, int) = nullptr;
        // Raytracing::Status.
        int (*status)(char*, int) = nullptr;
        // Raytracing::Timings: the last frame's build and trace on the GPU.
        void (*timings)(float*, float*) = nullptr;
        // The render event callback (KspFgGetRenderEventFunc) Unity calls, with the event's id
        // and its data.
        void (*event)(int, void*) = nullptr;

        // An export by name into the function pointer; false where the DLL has none.
        template <typename T>
        bool Load(T& function, const char* name)
        {
            function = reinterpret_cast<T>(GetProcAddress(module, name));
            return function != nullptr;
        }

        // The service's status line (Raytracing::Status).
        std::string Status()
        {
            char text[1024] = {};
            status(text, sizeof(text));
            return text;
        }

        // The status's count of reconstructed frames.
        size_t Reconstructed()
        {
            const std::string text = Status();
            const size_t at = text.find("reconstructed=");
            return at == std::string::npos ? 0 : static_cast<size_t>(std::stoull(text.substr(at + 14)));
        }
    };

    // The mesh handed to the proxy (KspRtSetMesh); its answer, 1 where it took it.
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
        desc.uvs = mesh.uvs.empty() ? nullptr : mesh.uvs.data();
        desc.colors = mesh.colors.empty() ? nullptr : mesh.colors.data();
        desc.indices = mesh.indices.data();
        return proxy.setMesh(&desc);
    }

    // The instances handed over relative to the camera, every ray mask bit set, for every
    // frame (kRtAnyFrame).
    void SendInstances(Proxy& proxy, const std::vector<Instance>& instances, const Vec3& camera)
    {
        std::vector<RtInstance> list(instances.size());
        for (size_t i = 0; i < instances.size(); ++i)
        {
            RelativeTransform(instances[i], camera, list[i].transform);
            list[i].mesh = instances[i].mesh;
            list[i].material = instances[i].material;
            list[i].mask = 0xFF;
            list[i].flags = instances[i].flags;
        }
        proxy.setInstances(list.data(), static_cast<int>(list.size()), kRtAnyFrame);
    }

    // The frame's output textures, by the slot each signal writes.
    struct Outputs
    {
        // The sun's visibility, the diffuse light, the reflections, the occlusion, the forward
        // light, the surface's intent and its mode, the camera rays' check.
        ComPtr<ID3D11Texture2D> visibility;   // u0, R16G16B16A16_FLOAT
        ComPtr<ID3D11Texture2D> diffuse;      // u1
        ComPtr<ID3D11Texture2D> specular;     // u2
        ComPtr<ID3D11Texture2D> occlusion;    // u3
        ComPtr<ID3D11Texture2D> forward;      // u4
        ComPtr<ID3D11Texture2D> surface;      // u5
        ComPtr<ID3D11Texture2D> surfaceMode;  // u6
        ComPtr<ID3D11Texture2D> primary;      // u7, R32G32B32A32_FLOAT
    };

    // The output textures at the frame's size: half floats, the camera rays' check in full floats.
    HRESULT CreateOutputs(ID3D11Device* device, Outputs& outputs)
    {
        HRESULT hr = S_OK;
        // ComPtr's & releases; std::addressof takes the address.
        for (ComPtr<ID3D11Texture2D>* texture :
             { std::addressof(outputs.visibility), std::addressof(outputs.diffuse), std::addressof(outputs.specular),
               std::addressof(outputs.occlusion), std::addressof(outputs.forward), std::addressof(outputs.surface),
               std::addressof(outputs.surfaceMode) })
            if (SUCCEEDED(hr))
                hr = CreateTarget(device, DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_BIND_SHADER_RESOURCE, *texture);
        if (SUCCEEDED(hr))
            hr = CreateTarget(device, DXGI_FORMAT_R32G32B32A32_FLOAT, D3D11_BIND_SHADER_RESOURCE, outputs.primary);
        return hr;
    }

    // What a frame traces besides the camera, with the defaults of the shadow checks.
    struct Lighting
    {
        // The sun's radiance and the sky's (RtFramePacket sunColor, skyColor), linear rgb.
        float sunColor[3] = { 1.0f, 1.0f, 1.0f };
        float sky[3] = { 1.0f, 1.0f, 1.0f };
        // Unity's ambient light a forward frame has already: facing up, sideways, down.
        float ambient[3] = { 0.0f, 0.0f, 0.0f };
        float aoDistance = 2.0f;
        float bounces = 2.0f;
        float diffuseDistance = 100000.0f;
        float specularDistance = 100000.0f;
        float clamp = 0.0f;
        ID3D11Texture2D* environment = nullptr;
        // Instances the GPU made, in t7's layout (Raytracing.h, kRtInputInstances).
        ID3D11Texture2D* gpuInstances = nullptr;
        uint32_t flags = 0;
        // The main body as a sphere, centre in the world, radius; radius 0 for none.
        double body[4] = { 0, 0, 0, 0 };
        // Whether the materials' small textures are read (RtFramePacket params[6]).
        bool materialTextures = false;
        // Whether the service reads the G-buffer, or takes the surface from its own rays
        // as it does for forward rendering.
        bool gbuffer = true;
        // The previous frame's world-to-clip in this frame's camera-relative space; this
        // frame's where not given.
        const Mat4* previous = nullptr;
        // The width the packet gives, kWidth where 0; the textures stay kWidth wide.
        UINT width = 0;
        // The bodies the sun lights, as RtFramePacket has them: per light the direction
        // and angular radius, the colour and the shadow ray's reach; how many.
        float lights[16] = {};
        int lightCount = 0;
        // Whether Unity's colours are gamma-space values (RtFramePacket lightParams y).
        bool gamma = false;
        // The main body's albedo (RtFramePacket planet).
        float planet[3] = { 0.0f, 0.0f, 0.0f };
        // Per light its surface lit by the star (RtFramePacket lightSurface).
        float lightSurface[8] = {};
        // How strongly water mirrors the sun's disc (RtFramePacket lightParams z).
        float sunMirror = 0.0f;
        // Whether the main body's sea is KSP's own (RtFramePacket lightParams w), and the
        // time its waves run on (planet w).
        bool stockSea = false;
        float time = 0.0f;
        // The sea's frame (RtFramePacket sea): a point on the sea in the world, its east
        // and north; none where sea[4..6] is zero.
        double sea[12] = {};
        // The camera's previous position less this one (RtFramePacket.cameraDelta).
        Vec3 cameraDelta;
    };

    // A frame through the render event as the mod sends it. It fills the packet with the camera, the sun and the
    // lighting's fields, and sets the G-buffer as the inputs and the outputs. The service copies
    // the results back before the call returns.
    void SendFrame(Proxy& proxy, const Camera& camera, GBuffer& g, Outputs& outputs, Vec3 towardsSun, float sunRadius,
                   uint32_t frameIndex, uint32_t signals, const Lighting& lighting = Lighting())
    {
        RtFramePacket packet = {};
        packet.size = sizeof(packet);
        packet.magic = kRtFrameMagic;
        packet.frameIndex = frameIndex;
        packet.flags = kRtFrameSameFrame | lighting.flags;
        packet.width = lighting.width != 0 ? lighting.width : kWidth;
        packet.height = kHeight;
        packet.signals = signals;
        ToFloats(camera.clipToWorld, packet.clipToWorld);
        ToFloats(camera.worldToClip, packet.worldToClip);
        ToFloats(lighting.previous != nullptr ? *lighting.previous : camera.worldToClip, packet.prevWorldToClip);
        ToFloats(lighting.previous != nullptr ? *lighting.previous : camera.worldToClip, packet.prevWorldToClipFar);
        const Vec3 l = Normalize(towardsSun);
        packet.sunDirection[0] = float(l.x);
        packet.sunDirection[1] = float(l.y);
        packet.sunDirection[2] = float(l.z);
        packet.sunDirection[3] = sunRadius;
        memcpy(packet.sunColor, lighting.sunColor, sizeof(lighting.sunColor));
        memcpy(packet.skyColor, lighting.sky, sizeof(lighting.sky));
        for (int k = 0; k < 3; ++k)
            packet.ambient[k * 4] = packet.ambient[k * 4 + 1] = packet.ambient[k * 4 + 2] = lighting.ambient[k];
        packet.params[0] = lighting.aoDistance;
        packet.params[1] = lighting.bounces;
        packet.params[2] = lighting.diffuseDistance;
        packet.params[3] = lighting.specularDistance;
        packet.params[4] = lighting.clamp;
        packet.params[5] = lighting.environment != nullptr ? 1.0f : 0.0f;
        packet.params[6] = lighting.materialTextures ? 1.0f : 0.0f;
        packet.params[7] = lighting.gbuffer ? 1.0f : 0.0f;
        packet.bodySphere[0] = float(lighting.body[0] - camera.position.x);
        packet.bodySphere[1] = float(lighting.body[1] - camera.position.y);
        packet.bodySphere[2] = float(lighting.body[2] - camera.position.z);
        packet.bodySphere[3] = float(lighting.body[3]);
        memcpy(packet.lights, lighting.lights, sizeof(lighting.lights));
        packet.lightParams[0] = float(lighting.lightCount);
        packet.lightParams[1] = lighting.gamma ? 1.0f : 0.0f;
        packet.lightParams[2] = lighting.sunMirror;
        packet.lightParams[3] = lighting.stockSea ? 1.0f : 0.0f;
        memcpy(packet.planet, lighting.planet, sizeof(lighting.planet));
        packet.planet[3] = lighting.time;
        const double origin[3] = { camera.position.x, camera.position.y, camera.position.z };
        for (int k = 0; k < 12; ++k)
            packet.sea[k] = float(k < 3 ? lighting.sea[k] - origin[k] : lighting.sea[k]);
        if (lighting.sea[4] == 0.0 && lighting.sea[5] == 0.0 && lighting.sea[6] == 0.0)
            memset(packet.sea, 0, sizeof(packet.sea));
        memcpy(packet.lightSurface, lighting.lightSurface, sizeof(lighting.lightSurface));
        packet.cameraDelta[0] = float(lighting.cameraDelta.x);
        packet.cameraDelta[1] = float(lighting.cameraDelta.y);
        packet.cameraDelta[2] = float(lighting.cameraDelta.z);
        packet.inputs[kRtInputEnvironment] = lighting.environment;
        packet.inputs[kRtInputInstances] = lighting.gpuInstances;
        packet.inputs[kRtInputDepth] = g.depth.Get();
        packet.inputs[kRtInputGBuffer0] = g.gbuffer0.Get();
        packet.inputs[kRtInputGBuffer1] = g.gbuffer1.Get();
        packet.inputs[kRtInputGBuffer2] = g.gbuffer2.Get();
        packet.outputs[0] = outputs.visibility.Get();
        packet.outputs[1] = outputs.diffuse.Get();
        packet.outputs[2] = outputs.specular.Get();
        packet.outputs[3] = outputs.occlusion.Get();
        packet.outputs[4] = outputs.forward.Get();
        if ((lighting.flags & kRtFrameSurfaceIntent) != 0)
        {
            packet.outputs[RT_OUTPUT_SURFACE] = outputs.surface.Get();
            packet.outputs[RT_OUTPUT_SURFACE_MODE] = outputs.surfaceMode.Get();
        }
        packet.outputs[7] = outputs.primary.Get();
        proxy.event(kFrameEvent, &packet);
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
                    blocked = blocked || RayBox(origin, l, box.position - camera.position, box.scale) >= 0.0;
                expected[i] = blocked ? 0 : 1;
            }
        return expected;
    }

    // A frame read back for the checks (ReadFrame).
    struct Frame
    {
        // The depth and the packed G-buffer normals, a value a pixel.
        std::vector<float> depth;
        std::vector<uint32_t> normals;
        std::vector<uint16_t> visibility;   // four halves a texel
        std::vector<float> primary;         // four floats a texel
    };

    // The depth, the normals, the sun's visibility and the camera rays' check read back.
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

    // The camera rays' hit distance (u7 x) against the depth's (u7 y): passes where 99.5 % of
    // the surface pixels agree within 0.1 %.
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

    // The sun's visibility as an image next to the executable: surfaces grey by how lit they
    // are, bluer the more their normal faces up; the sky dark blue.
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

    // A material without texture handed to the proxy (KspRtSetMaterial), its specular a grey;
    // 1 where it took it.
    int SendMaterial(Proxy& proxy, int id, const float albedo[3], const float emission[3], float smoothness,
                     float vertexColour = 0.0f, float specular = 0.04f, uint32_t flags = RT_SURFACE_KEEP,
                     float glossDivisor = 0.0f, float glossMean = 0.0f)
    {
        RtMaterialDesc desc = {};
        desc.vertexColour = vertexColour;
        desc.flags = flags;
        desc.glossDivisor = glossDivisor;
        desc.glossMean = glossMean;
        desc.size = sizeof(desc);
        desc.magic = kRtMaterialMagic;
        desc.material = id;
        memcpy(desc.albedo, albedo, 3 * sizeof(float));
        desc.albedo[3] = 0.0f;
        desc.specular[0] = desc.specular[1] = desc.specular[2] = specular;
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

    // Whether no half float is infinite or NaN (all exponent bits set).
    bool Finite(const std::vector<uint16_t>& halves)
    {
        for (uint16_t h : halves)
            if ((h & 0x7C00u) == 0x7C00u)
                return false;
        return true;
    }

    // The camera-relative point a pixel's depth stands for.
    Vec3 PositionAt(const Camera& camera, const std::vector<float>& depth, size_t x, size_t y)
    {
        const double nx = ((x + 0.5) / kWidth) * 2.0 - 1.0;
        const double ny = 1.0 - ((y + 0.5) / kHeight) * 2.0;
        const Mat4& m = camera.clipToWorld;
        double p[4];
        for (int r = 0; r < 4; ++r)
            p[r] = m.at(r, 0) * nx + m.at(r, 1) * ny + m.at(r, 2) * depth[y * kWidth + x] + m.at(r, 3);
        return { p[0] / p[3], p[1] / p[3], p[2] / p[3] };
    }

    // A G-buffer normal, R10G10B10A2 as n * 0.5 + 0.5.
    Vec3 NormalOf(uint32_t packed)
    {
        return { (packed & 0x3FF) / 1023.0 * 2 - 1, ((packed >> 10) & 0x3FF) / 1023.0 * 2 - 1,
                 ((packed >> 20) & 0x3FF) / 1023.0 * 2 - 1 };
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

    // One channel's mean over the 5x5 pixels about (x, y) of an rgb image.
    double MeanAround(const std::vector<double>& image, int x, int y, int channel)
    {
        double sum = 0.0;
        for (int dy = -2; dy <= 2; ++dy)
            for (int dx = -2; dx <= 2; ++dx)
                sum += image[(static_cast<size_t>(y + dy) * kWidth + (x + dx)) * 3 + channel];
        return sum / 25.0;
    }

    // Two images of light against each other where the depth has surfaces, per 16x16 block
    // of surface pixels: the blocks' mean absolute difference of luminance over their mean
    // luminance; the ratio of the means over all those blocks in meanRatio.
    double BlockDifference(const std::vector<double>& got, const std::vector<double>& reference, const std::vector<float>& depth,
                           double& meanRatio)
    {
        const UINT block = 16;
        double differences = 0.0, gotSum = 0.0, referenceSum = 0.0;
        for (UINT by = 0; by + block <= kHeight; by += block)
            for (UINT bx = 0; bx + block <= kWidth; bx += block)
            {
                double a = 0.0, b = 0.0;
                bool surface = true;
                for (UINT y = 0; y < block && surface; ++y)
                    for (UINT x = 0; x < block; ++x)
                    {
                        const size_t i = static_cast<size_t>(by + y) * kWidth + bx + x;
                        if (depth[i] <= 0.0f)
                        {
                            surface = false;
                            break;
                        }
                        a += 0.2126 * got[i * 3] + 0.7152 * got[i * 3 + 1] + 0.0722 * got[i * 3 + 2];
                        b += 0.2126 * reference[i * 3] + 0.7152 * reference[i * 3 + 1] + 0.0722 * reference[i * 3 + 2];
                    }
                if (!surface)
                    continue;
                differences += std::fabs(a - b);
                gotSum += a;
                referenceSum += b;
            }
        meanRatio = referenceSum > 0.0 ? gotSum / referenceSum : 0.0;
        return referenceSum > 0.0 ? differences / referenceSum : 1.0;
    }

    // An rgb image, times scale, as a BMP next to the executable.
    void SaveColour(const wchar_t* name, const std::vector<double>& image, double scale)
    {
        std::vector<float> rgb(image.size());
        for (size_t i = 0; i < image.size(); ++i)
            rgb[i] = static_cast<float>(image[i] * scale);
        WriteBmp(NextToExecutable(name), kWidth, kHeight, rgb);
    }
}

// RaytracingOmmHarness.cpp: opacity micromaps against the driver.
int RunOmmDriverChecks(IDXGIAdapter1* adapter, const wchar_t* shaderPath, bool dumpTable);

// A tuft of grass: six blades of three segments each, 36 triangles, the texture coordinates
// across and along the blade, for a blade texture (OmmBladeAlpha) that cuts its shape out.
static Mesh MakeTuft(int id)
{
    Mesh m;
    m.id = id;
    const int blades = 6, segments = 3;
    for (int b = 0; b < blades; ++b)
    {
        const double angle = b * 2.0 * 3.14159265358979 / blades;
        const Vec3 dir = { std::cos(angle), 0, std::sin(angle) };
        const Vec3 side = { -std::sin(angle), 0, std::cos(angle) };
        const uint32_t base = static_cast<uint32_t>(m.positions.size() / 3);
        for (int i = 0; i <= segments; ++i)
        {
            const double h = double(i) / segments;
            const Vec3 centre = dir * (0.12 + 0.3 * h * h) + Vec3{ 0, h, 0 };
            const double half = 0.16 * (1.0 - 0.45 * h);
            for (int s = -1; s <= 1; s += 2)
            {
                const Vec3 p = centre + side * (s * half);
                m.positions.insert(m.positions.end(), { float(p.x), float(p.y), float(p.z) });
                m.normals.insert(m.normals.end(), { float(dir.x), 0.3f, float(dir.z) });
                m.uvs.insert(m.uvs.end(), { 0.5f + 0.5f * s, float(1.0 - h) });
            }
        }
        for (int i = 0; i < segments; ++i)
        {
            const uint32_t a = base + 2 * i;
            m.indices.insert(m.indices.end(), { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
        }
    }
    return m;
}

// The coverage of a blade: a lens with a soft edge, 255 inside, a ramp over five texels, 0 out.
static uint8_t OmmBladeAlpha(uint32_t x, uint32_t y, double ramp = 0.15)
{
    const double u = (x + 0.5) / kRtMaterialTextureSize, v = (y + 0.5) / kRtMaterialTextureSize;
    const double h = 1.0 - v;
    const double across = std::fabs(u - 0.5) * 2.0;
    const double edge = 0.97 * (1.0 - 0.6 * h * h) - 0.0;
    const double tip = h > 0.9 ? (1.0 - h) / 0.1 : 1.0;
    const double alpha = std::clamp((edge - across) / ramp, 0.0, 1.0) * std::clamp(tip * 2.0, 0.0, 1.0);
    return static_cast<uint8_t>(std::lround(alpha * 255.0));
}

// Checks opacity micromaps through the proxy. The scene is a field of tufts of grass as a cut-out material's meshes.
// The check sets the picture with them and without (the sun's visibility, the camera's rays, the diffuse
// light) against each other, and reports what they cost and save.
static int RunOmmProxyChecks(Proxy& proxy, ID3D11Device* device, ID3D11DeviceContext* context, IDXGISwapChain1* swapChain,
                             IDXGIAdapter1* adapter, GBuffer& g, Outputs& outputs, std::map<int, Mesh>& meshes,
                             const std::vector<Instance>& scene, const Camera& camera, Vec3 sun, bool expectNone)
{
    const int before = failures;
    DXGI_ADAPTER_DESC1 adapterDesc = {};
    adapter->GetDesc1(&adapterDesc);
    ComPtr<IDXGIAdapter3> adapter3;
    adapter->QueryInterface(IID_PPV_ARGS(&adapter3));
    const auto videoMemory = [&]()
    {
        DXGI_QUERY_VIDEO_MEMORY_INFO info = {};
        if (adapter3 == nullptr || FAILED(adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info)))
            return 0.0;
        return static_cast<double>(info.CurrentUsage) / 1e6;
    };
    const std::wstring ini = NextToExecutable(L"ReDefinitionProxy.ini");

    // The tuft, its material and texture.
    Mesh tuft = MakeTuft(90);
    meshes[90] = tuft;
    if (FAILED(UploadMeshForRaster(device, meshes[90])) || SendMesh(proxy, meshes[90]) != 1)
        return Fatal("the tuft of grass", E_FAIL);
    std::vector<uint8_t> pixels(static_cast<size_t>(kRtMaterialTextureSize) * kRtMaterialTextureSize * 4, 90);
    uint64_t opaqueTexels = 0, partialTexels = 0;
    for (uint32_t y = 0; y < kRtMaterialTextureSize; ++y)
        for (uint32_t x = 0; x < kRtMaterialTextureSize; ++x)
        {
            const uint8_t a = OmmBladeAlpha(x, y);
            pixels[(y * kRtMaterialTextureSize + x) * 4 + 3] = a;
            opaqueTexels += a == 255;
            partialTexels += a != 0 && a != 255;
        }
    if (proxy.setMaterialTexture(6, pixels.data(), static_cast<int>(kRtMaterialTextureSize)) != 1)
        return Fatal("the blade texture", E_FAIL);
    RtMaterialDesc material = {};
    material.size = sizeof(material);
    material.magic = kRtMaterialMagic;
    material.material = 91;
    material.albedo[0] = 0.2f; material.albedo[1] = 0.5f; material.albedo[2] = 0.15f;
    material.albedo[3] = 0.5f;
    material.specular[0] = material.specular[1] = material.specular[2] = 0.04f;
    material.smoothness = 0.2f;
    material.texture = 6;
    if (proxy.setMaterial(&material) != 1)
        return Fatal("the grass material", E_FAIL);

    // A field of them around the origin, turned and sized by a fixed sequence.
    const int tufts = 700;
    std::vector<Instance> field = scene;
    std::vector<Instance> tuftInstances;
    uint32_t seed = 987654321u;
    const auto next = [&]()
    {
        seed = seed * 1664525u + 1013904223u;
        return double(seed >> 8) / 16777216.0;
    };
    for (int i = 0; i < tufts; ++i)
    {
        Instance t;
        t.mesh = 90;
        t.material = 91;
        const double radius = 4.0 + 26.0 * std::sqrt(next()), angle = next() * 6.2831853;
        const double turn = next() * 6.2831853, size = 1.2 + 1.6 * next();
        t.hasMatrix = true;
        const double c = std::cos(turn) * size, s = std::sin(turn) * size;
        const double m[12] = { c, 0, s, radius * std::cos(angle), 0, size * 1.4, 0, 0.0, -s, 0, c, radius * std::sin(angle) };
        memcpy(t.matrix, m, sizeof(m));
        t.position = { m[3], m[7], m[11] };
        tuftInstances.push_back(t);
    }
    field.insert(field.end(), tuftInstances.begin(), tuftInstances.end());

    // The same tufts as a GPU instance group, in t7's layout.
    const UINT rows = (tufts + kRtInstancesPerRow - 1) / kRtInstancesPerRow;
    std::vector<float> texels(static_cast<size_t>(kRtInstancesPerRow) * kRtInstanceTexels * rows * 4, 0.0f);
    for (int i = 0; i < tufts; ++i)
        for (int r = 0; r < 3; ++r)
        {
            float transform[12];
            RelativeTransform(tuftInstances[i], camera.position, transform);
            const size_t texel = static_cast<size_t>(i / kRtInstancesPerRow) * kRtInstancesPerRow * kRtInstanceTexels
                                 + (i % kRtInstancesPerRow) * kRtInstanceTexels + r;
            memcpy(&texels[texel * 4], transform + r * 4, 16);
        }
    D3D11_TEXTURE2D_DESC placesDesc = {};
    placesDesc.Width = kRtInstancesPerRow * kRtInstanceTexels;
    placesDesc.Height = rows;
    placesDesc.MipLevels = 1;
    placesDesc.ArraySize = 1;
    placesDesc.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
    placesDesc.SampleDesc.Count = 1;
    placesDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    const D3D11_SUBRESOURCE_DATA placesData = { texels.data(), placesDesc.Width * 16, 0 };
    ComPtr<ID3D11Texture2D> places;
    if (FAILED(device->CreateTexture2D(&placesDesc, &placesData, &places)))
        return Fatal("the tufts' places", E_FAIL);
    RtGpuInstanceGroup group = {};
    group.mesh = 90;
    group.material = 91;
    group.first = 0;
    group.count = tufts;
    group.mask = 0xFF;

    struct Status
    {
        unsigned meshes = 0, bakes = 0;
        double ommMb = 0, bakeMs = 0, blasMb = 0;
    };
    const auto status = [&]()
    {
        Status s;
        const std::string text = proxy.Status();
        const size_t at = text.find("omm=");
        if (at != std::string::npos)
            sscanf_s(text.c_str() + at, "omm=%u/%lfMB/%u/%lfms", &s.meshes, &s.ommMb, &s.bakes, &s.bakeMs);
        const size_t blas = text.find(" blas=");
        if (blas != std::string::npos)
            s.blasMb = atof(text.c_str() + blas + 6);
        return s;
    };
    const auto setKeys = [&](std::initializer_list<std::pair<const wchar_t*, const wchar_t*>> keys)
    {
        for (const auto& key : keys)
            WritePrivateProfileStringW(L"Proxy", key.first, key.second, ini.c_str());
        // The proxy looks at the file at most once a second, from its Present.
        Sleep(1100);
        swapChain->Present(0, 0);
    };

    uint32_t frameIndex = 52000;
    bool fromGpu = false;
    const auto prepare = [&](bool gpu)
    {
        fromGpu = gpu;
        SendInstances(proxy, gpu ? scene : field, camera.position);
        proxy.setGpuGroups(gpu ? &group : nullptr, gpu ? 1 : 0, kRtAnyFrame);
        std::vector<Instance> drawn = field;
        RenderGBuffer(context, g, camera, meshes, drawn);
    };
    Lighting lit;
    lit.materialTextures = true;
    ID3D11Texture2D* gpuTexture = places.Get();
    const auto lighting = [&]()
    {
        Lighting l = lit;
        l.gpuInstances = fromGpu ? gpuTexture : nullptr;
        return l;
    };
    const uint32_t traced = kRtSignalSunVisibility | kRtSignalAmbientOcclusion | kRtSignalDiffuse | kRtSignalSpecular;

    struct Measure
    {
        Frame frame;
        std::vector<uint16_t> diffuse;
        double trace = 0, sunPass = 0, blasMb = 0, ommMb = 0, memory = 0;
        unsigned meshes = 0, bakes = 0;
        bool ok = false;
    };
    const auto measure = [&]() -> Measure
    {
        Measure m;
        for (int f = 0; f < 24; ++f)
            SendFrame(proxy, camera, g, outputs, sun, 0.0047f, frameIndex++, traced, lighting());
        // The least of three batches' means: the machine's own noise is larger than the effect.
        double trace = 1e9, sunPass = 1e9;
        for (int batch = 0; batch < 3; ++batch)
        {
            double batchTrace = 0, batchSun = 0;
            for (int f = 0; f < 16; ++f)
            {
                SendFrame(proxy, camera, g, outputs, sun, 0.0047f, frameIndex++, traced, lighting());
                float build = 0, t = 0, passes[8] = {};
                proxy.timings(&build, &t);
                proxy.passTimings(passes, 8);
                batchTrace += t / 16.0;
                batchSun += passes[0] / 16.0;
            }
            trace = std::min(trace, batchTrace);
            sunPass = std::min(sunPass, batchSun);
        }
        // The picture: the same frame index every time, so that the rays' random numbers are.
        SendFrame(proxy, camera, g, outputs, sun, 0.0047f, 59990, kRtSignalSunVisibility | kRtSignalPrimaryCheck, lighting());
        if (!ReadFrame(device, context, g, outputs, m.frame))
            return m;
        SendFrame(proxy, camera, g, outputs, sun, 0.0047f, 59991, kRtSignalDiffuse, lighting());
        if (!ReadTexture(device, context, outputs.diffuse.Get(), 8, m.diffuse))
            return m;
        const Status s = status();
        m.trace = trace;
        m.sunPass = sunPass;
        m.blasMb = s.blasMb;
        m.ommMb = s.ommMb;
        m.meshes = s.meshes;
        m.bakes = s.bakes;
        m.memory = videoMemory();
        m.ok = true;
        return m;
    };
    // Pixels that differ between two pictures: the sun's visibility or the diffuse light, whose
    // rays meet the cut-out surfaces and read their normals and colours, of those that show a
    // surface. The camera's hit distance is counted apart and left out: the primary check
    // traces with RAY_FLAG_FORCE_OPAQUE (Raytracing.hlsl), which takes a card as it was drawn,
    // opaque; a micromap's transparent micro-triangles stay skipped for such a ray ("there is no
    // way to turn a transparent micro-triangle miss into a hit even by using force-opaque ray
    // or instance flags", DirectX-Specs, Raytracing). No pass of the service traces so.
    size_t unlikeParts[3] = {};
    const auto compare = [&](const Measure& a, const Measure& b, size_t& compared)
    {
        size_t differing = 0;
        compared = 0;
        unlikeParts[0] = unlikeParts[1] = unlikeParts[2] = 0;
        for (size_t i = 0; i < a.frame.depth.size(); ++i)
        {
            if (a.frame.depth[i] <= 0.0f)
                continue;
            ++compared;
            const bool litA = HalfToFloat(a.frame.visibility[i * 4]) > 0.5f, litB = HalfToFloat(b.frame.visibility[i * 4]) > 0.5f;
            const float hitA = a.frame.primary[i * 4], hitB = b.frame.primary[i * 4];
            bool light = true;
            for (int c = 0; c < 3; ++c)
            {
                const float x = HalfToFloat(a.diffuse[i * 4 + c]), y = HalfToFloat(b.diffuse[i * 4 + c]);
                light = light && std::fabs(x - y) <= 0.02f * std::max(1.0f, std::fabs(y));
            }
            const bool hit = std::fabs(hitA - hitB) > 1e-3f * std::max(1.0f, std::fabs(hitB));
            unlikeParts[0] += litA != litB;
            unlikeParts[1] += hit;
            unlikeParts[2] += !light;
            if (litA != litB || !light)
                ++differing;
        }
        return differing;
    };
    const auto describe = [&](const char* name, const Measure& m)
    {
        printf("        %-46s trace %6.3f ms  sun pass %6.3f ms  structures %7.4f MB  micromaps %u meshes %7.4f MB  video memory %7.1f MB\n",
               name, m.trace, m.sunPass, m.blasMb, m.meshes, m.ommMb, m.memory);
    };

    // The blade texture in numbers, for the log of a run.
    printf("      the blade texture: %llu texels fully covered, %llu partly, of %u; %d tufts of %zu triangles\n",
           static_cast<unsigned long long>(opaqueTexels), static_cast<unsigned long long>(partialTexels),
           kRtMaterialTextureSize * kRtMaterialTextureSize, tufts, tuft.indices.size() / 3);

    setKeys({ { L"rtOpacityMicromaps", L"0" }, { L"rtFoliageTransmission", L"0" }, { L"rtCutoutTest", L"0" } });
    prepare(false);
    const Measure plain = measure();
    if (!plain.ok)
        return Fatal("reading the tufts' frame back", E_FAIL);
    SaveVisibility(L"rt_omm_off.bmp", plain.frame);
    describe("off", plain);

    // On: the bake runs on a thread of its own, the structure is built again when it is done.
    setKeys({ { L"rtOpacityMicromaps", L"1" } });
    int waited = 0;
    for (; waited < 600; ++waited)
    {
        SendFrame(proxy, camera, g, outputs, sun, 0.0047f, frameIndex++, kRtSignalSunVisibility, lighting());
        if (status().meshes >= 1)
            break;
        Sleep(10);
    }
    const Measure on = measure();
    if (!on.ok)
        return Fatal("reading the micromaps' frame back", E_FAIL);
    SaveVisibility(L"rt_omm_on.bmp", on.frame);
    describe("on", on);
    {
        char detail[300];
        sprintf_s(detail, "%u mesh(es) with micromaps %.3f MB after %d frames, %u bake(s) of %.1f ms in all", on.meshes, on.ommMb,
                  waited, on.bakes, status().bakeMs);
        const bool wanted = !expectNone && adapterDesc.VendorId == 0x10DE;
        Check(wanted ? on.meshes >= 1 : on.meshes == 0, expectNone ? "no micromaps without the driver's capability" : "micromaps built",
              detail);
    }
    size_t compared = 0;
    {
        const size_t differing = compare(plain, on, compared);
        // Where they differ, red over the picture with them: sun visibility without, with.
        std::vector<float> image(static_cast<size_t>(kWidth) * kHeight * 3);
        for (size_t i = 0; i < static_cast<size_t>(kWidth) * kHeight; ++i)
        {
            const float a = HalfToFloat(plain.frame.visibility[i * 4]), b = HalfToFloat(on.frame.visibility[i * 4]);
            image[i * 3] = plain.frame.depth[i] > 0.0f ? 0.2f + 0.8f * b : 0.4f;
            image[i * 3 + 1] = plain.frame.depth[i] > 0.0f ? 0.2f + 0.8f * b : 0.4f;
            image[i * 3 + 2] = plain.frame.depth[i] > 0.0f ? 0.2f + 0.8f * b : 0.4f;
            if (a != b)
            {
                image[i * 3] = a > b ? 1.0f : 0.0f;
                image[i * 3 + 1] = a > b ? 0.0f : 1.0f;
                image[i * 3 + 2] = 0.0f;
            }
        }
        WriteBmp(NextToExecutable(L"rt_omm_difference.bmp"), kWidth, kHeight, image);
        char detail[300];
        sprintf_s(detail, "%zu of %zu pixels differ (sun visibility %zu, diffuse light %zu; camera rays of the primary check %zu apart); trace %.3f ms against %.3f,"
                          " sun pass %.3f ms against %.3f, structures %.2f MB against %.2f",
                  differing, compared, unlikeParts[0], unlikeParts[2], unlikeParts[1], on.trace, plain.trace, on.sunPass, plain.sunPass, on.blasMb, plain.blasMb);
        Check(compared > 50000 && differing * 5000 <= compared, "the same picture with micromaps", detail);
    }

    // Off again, live: the structures keep their micromaps, the instances switch them off.
    setKeys({ { L"rtOpacityMicromaps", L"0" } });
    const Measure off = measure();
    if (!off.ok)
        return Fatal("reading the frame back", E_FAIL);
    describe("off again, live", off);
    {
        const size_t differing = compare(plain, off, compared);
        char detail[300];
        sprintf_s(detail, "%zu of %zu pixels differ from before; trace %.3f ms against %.3f", differing, compared, off.trace,
                  plain.trace);
        Check(compared > 50000 && differing * 5000 <= compared, "micromaps switched off live", detail);
    }

    // What the cut-out surfaces' tests cost the rays at most: every surface opaque, no candidate.
    setKeys({ { L"rtCutoutTest", L"2" } });
    describe("rtCutoutTest=2, every surface opaque", measure());
    setKeys({ { L"rtCutoutTest", L"0" } });

    // The material's texture changes (a hard-edged blade, almost no partial texels): the
    // micromaps are baked again for it, and the structure built again; meanwhile the instances
    // switch them off, as the picture of the old coverage would be wrong.
    {
        std::vector<uint8_t> hard = pixels;
        uint64_t partial = 0;
        for (uint32_t y = 0; y < kRtMaterialTextureSize; ++y)
            for (uint32_t x = 0; x < kRtMaterialTextureSize; ++x)
            {
                const uint8_t a = OmmBladeAlpha(x, y, 0.02);
                hard[(y * kRtMaterialTextureSize + x) * 4 + 3] = a;
                partial += a != 0 && a != 255;
            }
        setKeys({ { L"rtOpacityMicromaps", L"1" } });
        const unsigned bakesBefore = status().bakes;
        proxy.setMaterialTexture(6, hard.data(), static_cast<int>(kRtMaterialTextureSize));
        const Measure during = measure();
        int rebaked = 0;
        for (; rebaked < 600 && !expectNone; ++rebaked)
        {
            SendFrame(proxy, camera, g, outputs, sun, 0.0047f, frameIndex++, kRtSignalSunVisibility, lighting());
            if (status().bakes > bakesBefore)
                break;
            Sleep(10);
        }
        const Measure hardOn = measure();
        setKeys({ { L"rtOpacityMicromaps", L"0" } });
        const Measure hardOff = measure();
        char name[100];
        sprintf_s(name, "hard blade (%llu partly covered texels), on", static_cast<unsigned long long>(partial));
        describe(name, hardOn);
        describe("hard blade, off", hardOff);
        size_t n = 0, m = 0;
        const size_t differing = compare(hardOff, hardOn, n);
        const size_t whileDiffering = compare(hardOff, during, m);
        char detail[300];
        sprintf_s(detail, "a new texture for the material: %zu of %zu pixels differ once its micromaps are baked (%u bakes in all,"
                          " after %d frames), %zu of %zu while they are not; trace %.3f ms against %.3f without, sun pass %.3f against %.3f",
                  differing, n, status().bakes, rebaked, whileDiffering, m, hardOn.trace, hardOff.trace, hardOn.sunPass,
                  hardOff.sunPass);
        Check(n > 50000 && differing * 5000 <= n && whileDiffering * 5000 <= m && (expectNone || status().bakes > bakesBefore),
              "micromaps follow a texture that changes", detail);
        proxy.setMaterialTexture(6, pixels.data(), static_cast<int>(kRtMaterialTextureSize));
    }

    // The sun's rays let part of the light through a cut-out surface: the micromaps' opaque
    // parts stay candidates then.
    const auto pair = [&](const char* name, std::initializer_list<std::pair<const wchar_t*, const wchar_t*>> keys, bool gpu)
    {
        std::vector<std::pair<const wchar_t*, const wchar_t*>> offKeys(keys.begin(), keys.end());
        std::vector<std::pair<const wchar_t*, const wchar_t*>> onKeys = offKeys;
        offKeys.push_back({ L"rtOpacityMicromaps", L"0" });
        onKeys.push_back({ L"rtOpacityMicromaps", L"1" });
        prepare(gpu);
        for (const auto& key : onKeys)
            WritePrivateProfileStringW(L"Proxy", key.first, key.second, ini.c_str());
        Sleep(1100);
        swapChain->Present(0, 0);
        for (int f = 0; f < 600 && !expectNone && status().meshes < 1; ++f)
        {
            SendFrame(proxy, camera, g, outputs, sun, 0.0047f, frameIndex++, kRtSignalSunVisibility, lighting());
            Sleep(10);
        }
        const Measure withOmm = measure();
        for (const auto& key : offKeys)
            WritePrivateProfileStringW(L"Proxy", key.first, key.second, ini.c_str());
        Sleep(1100);
        swapChain->Present(0, 0);
        const Measure without = measure();
        if (!withOmm.ok || !without.ok)
            return;
        size_t n = 0;
        const size_t differing = compare(without, withOmm, n);
        char detail[300];
        sprintf_s(detail, "%zu of %zu pixels differ with micromaps; trace %.3f ms against %.3f", differing, n, withOmm.trace,
                  without.trace);
        Check(n > 50000 && differing * 5000 <= n, name, detail);
    };
    pair("micromaps with the sun's rays let through foliage", { { L"rtFoliageTransmission", L"0.5" } }, false);
    pair("micromaps with the measuring test of the cut-out surfaces", { { L"rtFoliageTransmission", L"0" }, { L"rtCutoutTest", L"1" } }, false);
    pair("micromaps on instances from the GPU", { { L"rtFoliageTransmission", L"0" }, { L"rtCutoutTest", L"0" } }, true);

    // A colour on a GPU instance, the fourth texel of its place, multiplies its material's albedo (Parallax's grass:
    // its albedo times the terrain's colour). Tufts under the colour 0.5 light the ground as tufts of a material
    // with half the albedo do from the CPU; without the colour they do not.
    {
        RtMaterialDesc half = material;
        half.material = 92;
        for (int c = 0; c < 3; ++c)
            half.albedo[c] *= 0.5f;
        std::vector<float> tintedTexels = texels;
        for (int i = 0; i < tufts; ++i)
        {
            const size_t texel = static_cast<size_t>(i) * kRtInstanceTexels + 3;
            tintedTexels[texel * 4] = tintedTexels[texel * 4 + 1] = tintedTexels[texel * 4 + 2] = 0.5f;
        }
        ComPtr<ID3D11Texture2D> tintedPlaces;
        const D3D11_SUBRESOURCE_DATA tintedData = { tintedTexels.data(), placesDesc.Width * 16, 0 };
        if (proxy.setMaterial(&half) != 1 || FAILED(device->CreateTexture2D(&placesDesc, &tintedData, &tintedPlaces)))
        {
            Check(false, "an instance's colour from the GPU", "the half-albedo material or the tinted places were refused");
            return failures - before;
        }
        std::vector<Instance> halfField = scene;
        for (Instance t : tuftInstances)
        {
            t.material = 92;
            halfField.push_back(t);
        }
        setKeys({ { L"rtOpacityMicromaps", L"0" }, { L"rtFoliageTransmission", L"0" }, { L"rtCutoutTest", L"0" } });
        fromGpu = false;
        SendInstances(proxy, halfField, camera.position);
        proxy.setGpuGroups(nullptr, 0, kRtAnyFrame);
        RenderGBuffer(context, g, camera, meshes, field);
        const Measure cpuHalf = measure();
        fromGpu = true;
        SendInstances(proxy, scene, camera.position);
        proxy.setGpuGroups(&group, 1, kRtAnyFrame);
        RenderGBuffer(context, g, camera, meshes, field);
        gpuTexture = tintedPlaces.Get();
        const Measure gpuTinted = measure();
        gpuTexture = places.Get();
        const Measure gpuPlain = measure();
        if (cpuHalf.ok && gpuTinted.ok && gpuPlain.ok)
        {
            size_t n = 0, nPlain = 0;
            const size_t withColour = compare(cpuHalf, gpuTinted, n);
            const size_t without = compare(cpuHalf, gpuPlain, nPlain);
            char detail[300];
            sprintf_s(detail, "%zu of %zu pixels differ from the CPU's half-albedo tufts with the colour 0.5, %zu without it", withColour, n, without);
            Check(n > 50000 && withColour * 1000 <= n && without > withColour * 20, "an instance's colour from the GPU", detail);
        }
    }

    setKeys({ { L"rtOpacityMicromaps", nullptr }, { L"rtFoliageTransmission", nullptr }, { L"rtCutoutTest", nullptr } });
    proxy.setGpuGroups(nullptr, 0, kRtAnyFrame);
    proxy.releaseMesh(90);
    meshes.erase(90);
    SendInstances(proxy, scene, camera.position);
    RenderGBuffer(context, g, camera, meshes, scene);
    return failures - before;
}

// The check the harness is at: with stdout unbuffered (main), the last of these before a crash
// says where it stopped.
static void Progress(const char* step)
{
    printf("      at: %s\n", step);
}

// Loads the proxy, makes its devices through a swapchain as Unity would, and runs the checks
// (or only the opacity micromaps' with --omm, --omm-table, --omm-scene, --omm-none); 0 where
// every check passed.
int main(int argc, char** argv)
{
    // Unbuffered: piped, a crash would take the buffered lines with it.
    setvbuf(stdout, nullptr, _IONBF, 0);
    printf("ReDefinition raytracing harness\n");
    // --omm: only the opacity micromaps' checks against the driver; --omm-table also lists
    // the order the driver has.
    const bool ommOnly = argc > 1 && (strcmp(argv[1], "--omm") == 0 || strcmp(argv[1], "--omm-table") == 0);
    const bool ommTable = argc > 1 && strcmp(argv[1], "--omm-table") == 0;
    // --omm-scene: only the opacity micromaps through the proxy; --omm-none the same with the
    // driver's capability simulated as none (REDEFINITION_OMM_CAPS).
    const bool ommScene = argc > 1 && (strcmp(argv[1], "--omm-scene") == 0 || strcmp(argv[1], "--omm-none") == 0);
    const bool ommNone = argc > 1 && strcmp(argv[1], "--omm-none") == 0;
    if (ommNone)
        SetEnvironmentVariableA("REDEFINITION_OMM_CAPS", "none");

    Proxy proxy;
    proxy.module = LoadLibraryW(L"dxgi.dll");
    if (proxy.module == nullptr)
        return Fatal("LoadLibrary(dxgi.dll)", HRESULT_FROM_WIN32(GetLastError()));
    void* (*eventFunc)() = nullptr;
    if (!proxy.Load(proxy.available, "KspRtAvailable") || !proxy.Load(proxy.structSizes, "KspRtStructSizes")
        || !proxy.Load(proxy.setMesh, "KspRtSetMesh") || !proxy.Load(proxy.releaseMesh, "KspRtReleaseMesh")
        || !proxy.Load(proxy.setMaterial, "KspRtSetMaterial") || !proxy.Load(proxy.setInstances, "KspRtSetInstances")
        || !proxy.Load(proxy.setGpuGroups, "KspRtSetGpuGroups")
        || !proxy.Load(proxy.setMaterialTexture, "KspRtSetMaterialTexture")
        || !proxy.Load(proxy.status, "KspRtStatus") || !proxy.Load(proxy.timings, "KspRtTimings")
        || !proxy.Load(proxy.passTimings, "KspRtPassTimings")
        || !proxy.Load(proxy.reconstructionAvailable, "KspRtReconstructionAvailable")
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
    if (ommOnly)
    {
        const int failed = RunOmmDriverChecks(adapter.Get(), NextToExecutable(L"OmmProbe.cso").c_str(), ommTable);
        printf(failed == 0 ? "all opacity micromap checks passed\n" : "%d opacity micromap check(s) failed\n", failed);
        return failed == 0 ? 0 : 1;
    }

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
    hr = CreateOutputs(device.Get(), outputs);
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

    if (ommScene)
    {
        const int failed = RunOmmProxyChecks(proxy, device.Get(), context.Get(), swapChain.Get(), adapter.Get(), g, outputs, meshes,
                                             scene, camera, sun, ommNone);
        printf(failed == 0 ? "all opacity micromap checks passed\n" : "%d opacity micromap check(s) failed\n", failed);
        return failed == 0 ? 0 : 1;
    }

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

    // ---------------------------------------------------------------- instances from the GPU
    {
        // The boxes not as the CPU's instances but in a texture as Parallax's scatter comes,
        // one group each, the rest of the texture empty: the same shadows.
        std::vector<Instance> ground;
        for (const Instance& instance : scene)
            if (instance.mesh != 1)
                ground.push_back(instance);
        const UINT rows = 2;
        std::vector<float> texels(static_cast<size_t>(kRtInstancesPerRow) * kRtInstanceTexels * rows * 4, 0.0f);
        std::vector<RtGpuInstanceGroup> groups;
        for (size_t i = 0; i < boxes.size(); ++i)
        {
            float transform[12];
            RelativeTransform(boxes[i], camera.position, transform);
            // The last instance on the second texture row, the rest on the first.
            const uint32_t at = i + 1 == boxes.size() ? kRtInstancesPerRow + 7 : static_cast<uint32_t>(i);
            for (int r = 0; r < 3; ++r)
            {
                const size_t texel = static_cast<size_t>(at / kRtInstancesPerRow) * kRtInstancesPerRow * kRtInstanceTexels
                                     + (at % kRtInstancesPerRow) * kRtInstanceTexels + r;
                memcpy(&texels[texel * 4], transform + r * 4, 16);
            }
            RtGpuInstanceGroup group = {};
            group.mesh = boxes[i].mesh;
            group.material = boxes[i].material;
            group.first = at;
            group.count = 1;
            group.mask = 0xFF;
            groups.push_back(group);
        }
        // An empty run as well, whose instances are all zero: none of them.
        RtGpuInstanceGroup empty = {};
        empty.mesh = 1;
        empty.first = 100;
        empty.count = 50;
        empty.mask = 0xFF;
        groups.push_back(empty);

        D3D11_TEXTURE2D_DESC texture = {};
        texture.Width = kRtInstancesPerRow * kRtInstanceTexels;
        texture.Height = rows;
        texture.MipLevels = 1;
        texture.ArraySize = 1;
        texture.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
        texture.SampleDesc.Count = 1;
        texture.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        const D3D11_SUBRESOURCE_DATA initial = { texels.data(), texture.Width * 16, 0 };
        ComPtr<ID3D11Texture2D> transforms;
        HRESULT made = device->CreateTexture2D(&texture, &initial, &transforms);
        if (FAILED(made))
            return Fatal("the GPU instances' texture", made);

        SendInstances(proxy, ground, camera.position);
        proxy.setGpuGroups(groups.data(), static_cast<int>(groups.size()), kRtAnyFrame);
        Lighting fromGpu;
        fromGpu.gpuInstances = transforms.Get();
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, 2, kRtSignalSunVisibility | kRtSignalPrimaryCheck, fromGpu);
        Frame gpuFrame;
        if (!ReadFrame(device.Get(), context.Get(), g, outputs, gpuFrame))
            return Fatal("reading the frame back", E_FAIL);
        printf("      %s\n", proxy.Status().c_str());
        CheckVisibility("instances from the GPU", gpuFrame,
                        ExpectedVisibility(camera, gpuFrame.depth, gpuFrame.normals, boxes, sun));
        proxy.setGpuGroups(nullptr, 0, kRtAnyFrame);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- 14 km away
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
            // Back into a half for SaveVisibility: exact for 0 and 1, close between.
            image.visibility[i * 4] = FloatToHalf(std::min(std::max(v, 0.0f), 1.0f));
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
        for (UINT y = 0; y < envHeight; ++y)
            for (UINT x = 0; x < envWidth; ++x)
            {
                uint16_t* t = &texels[(static_cast<size_t>(y) * envWidth + x) * 4];
                t[0] = FloatToHalf((x + 0.5f) / envWidth);
                t[1] = FloatToHalf((y + 0.5f) / envHeight);
                t[2] = FloatToHalf(0.25f);
                t[3] = FloatToHalf(1.0f);
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

            // The radiance cache (rtRadianceCache) on the pad, every signal traced as at Full:
            // the diffuse light over 128 frames against the same without it (BlockDifference),
            // and the GPU's times, without and with it in turn, five times 24 frames each,
            // the median frame of each: the GPU's clock wanders over a run.
            {
                const std::wstring ini = NextToExecutable(L"ReDefinitionProxy.ini");
                const auto setCache = [&](const wchar_t* value)
                {
                    WritePrivateProfileStringW(L"Proxy", L"rtRadianceCache", value, ini.c_str());
                    Sleep(1100);
                    swapChain->Present(0, 0);
                };
                uint32_t kspFrame = 7300;
                // A cache made anew is cleared by its first frame and warm after 60.
                const auto warmUp = [&]()
                {
                    Lighting cleared = lighting;
                    cleared.flags = kRtFrameReset;
                    SendFrame(proxy, view, g, outputs, sun, 0.0047f, kspFrame++, all, cleared);
                    for (int f = 1; f < 60; ++f)
                        SendFrame(proxy, view, g, outputs, sun, 0.0047f, kspFrame++, all, lighting);
                };
                // Per frame: the whole trace, the sun, the occlusion, the diffuse light, the
                // reflections, the resolve.
                std::vector<float> times[2][6];
                const auto measure = [&](int cached)
                {
                    for (int f = 0; f < 28; ++f)
                    {
                        SendFrame(proxy, view, g, outputs, sun, 0.0047f, kspFrame++, all, lighting);
                        float now[kRtPassTimings] = {};
                        float b = 0.0f, t = 0.0f;
                        proxy.passTimings(now, kRtPassTimings);
                        proxy.timings(&b, &t);
                        // The timings are a frame or two behind; the first ones of a turn are
                        // the other's.
                        if (f < 4)
                            continue;
                        times[cached][0].push_back(t);
                        for (int k = 0; k < 4; ++k)
                            times[cached][1 + k].push_back(now[k]);
                        times[cached][5].push_back(now[kRtPassCache]);
                    }
                };
                const auto median = [](std::vector<float> values)
                {
                    if (values.empty())
                        return 0.0f;
                    std::sort(values.begin(), values.end());
                    return values[values.size() / 2];
                };
                for (int round = 0; round < 5; ++round)
                {
                    measure(0);
                    setCache(L"1");
                    warmUp();
                    measure(1);
                    setCache(L"0");
                }
                std::vector<double> off, offAgain, on;
                if (!Accumulate(proxy, device.Get(), context.Get(), view, g, outputs, outputs.diffuse.Get(), all, lighting, sun,
                                128, kspFrame, off)
                    || !Accumulate(proxy, device.Get(), context.Get(), view, g, outputs, outputs.diffuse.Get(), all, lighting,
                                   sun, 128, kspFrame + 128, offAgain))
                    return Fatal("reading the KSP scene back", E_FAIL);
                kspFrame += 256;
                setCache(L"1");
                warmUp();
                const bool ok = Accumulate(proxy, device.Get(), context.Get(), view, g, outputs, outputs.diffuse.Get(), all,
                                           lighting, sun, 128, kspFrame, on);
                const std::string status = proxy.Status();
                setCache(L"0");
                if (!ok)
                    return Fatal("reading the KSP scene back", E_FAIL);
                std::vector<float> kspDepth;
                ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, kspDepth);
                double ratio = 0.0, noiseRatio = 0.0;
                const double difference = BlockDifference(on, off, kspDepth, ratio);
                const double noise = BlockDifference(offAgain, off, kspDepth, noiseRatio);
                const size_t at = status.find(" cache ");
                printf("      KSP scene, radiance cache:%s\n", at == std::string::npos ? " not counted" : status.substr(at).c_str());
                char detail[300];
                sprintf_s(detail, "diffuse light per 16x16 block off by %.2f %% (mean %+.2f %%), two runs without it %.2f %%",
                          100.0 * difference, 100.0 * (ratio - 1.0), 100.0 * noise);
                Check(difference < noise + 0.01 && std::fabs(ratio - 1.0) < 0.01, "KSP scene: the radiance cache's light",
                      detail);
                printf("      KSP scene, every signal, the median of %zu frames: %.3f ms without the radiance cache (sun %.3f,"
                       " occlusion %.3f, diffuse %.3f, specular %.3f), %.3f ms with it (sun %.3f, occlusion %.3f, diffuse %.3f,"
                       " specular %.3f, resolve %.3f)\n",
                       times[1][0].size(), median(times[0][0]), median(times[0][1]), median(times[0][2]), median(times[0][3]),
                       median(times[0][4]), median(times[1][0]), median(times[1][1]), median(times[1][2]), median(times[1][3]),
                       median(times[1][4]), median(times[1][5]));
                SaveColour(L"rt_ksp_cache_on.bmp", on, 1.0);
            }
            for (auto& entry : kspMeshes)
            {
                proxy.releaseMesh(entry.first);
                meshes.erase(entry.first);
            }
        }
    }

    // ---------------------------------------------------------------- denoising
    {
        // The camera glides into place over 60 frames and holds for 30. The denoised
        // signals are then compared with a reference of 1024 frames at the end position,
        // against what one noisy frame gives.
        SendInstances(proxy, scene, camera.position);
        Lighting lighting;
        lighting.sky[0] = 0.4f;
        lighting.sky[1] = 0.5f;
        lighting.sky[2] = 0.7f;
        lighting.bounces = 2.0f;
        lighting.clamp = 20.0f;
        const float sunRadius = 0.03f;
        const uint32_t signals = kRtSignalSunVisibility | kRtSignalDiffuse;

        Camera end = camera;
        RenderGBuffer(context.Get(), g, end, meshes, scene);
        std::vector<double> referenceDiffuse(static_cast<size_t>(kWidth) * kHeight * 3, 0.0);
        std::vector<double> referenceSun(static_cast<size_t>(kWidth) * kHeight, 0.0);
        std::vector<uint16_t> read;
        const int referenceFrames = 1024;
        for (int f = 0; f < referenceFrames; ++f)
        {
            SendFrame(proxy, end, g, outputs, sun, sunRadius, 8000 + f, signals, lighting);
            ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, read);
            for (size_t i = 0; i < referenceSun.size(); ++i)
                for (int c = 0; c < 3; ++c)
                    referenceDiffuse[i * 3 + c] += HalfToFloat(read[i * 4 + c]);
            ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, read);
            for (size_t i = 0; i < referenceSun.size(); ++i)
                referenceSun[i] += HalfToFloat(read[i * 4]);
        }
        for (double& v : referenceDiffuse)
            v /= referenceFrames;
        for (double& v : referenceSun)
            v /= referenceFrames;
        std::vector<float> depth;
        ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);

        // One noisy frame at the end position, and the difference between two.
        std::vector<uint16_t> noisyDiffuse, noisySun, noisyDiffuse2;
        SendFrame(proxy, end, g, outputs, sun, sunRadius, 9500, signals, lighting);
        ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, noisyDiffuse);
        ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, noisySun);
        SendFrame(proxy, end, g, outputs, sun, sunRadius, 9501, signals, lighting);
        ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, noisyDiffuse2);

        const auto error = [&](const std::vector<uint16_t>& image, bool colour, double& rmse, double& mean,
                               double& referenceMean)
        {
            double sum = 0.0, total = 0.0, totalReference = 0.0;
            size_t count = 0;
            for (size_t i = 0; i < referenceSun.size(); ++i)
            {
                if (depth[i] <= 0.0f)
                    continue;
                const int channels = colour ? 3 : 1;
                for (int c = 0; c < channels; ++c)
                {
                    const double got = HalfToFloat(image[i * 4 + c]);
                    const double want = colour ? referenceDiffuse[i * 3 + c] : referenceSun[i];
                    sum += (got - want) * (got - want);
                    total += got;
                    totalReference += want;
                    ++count;
                }
            }
            rmse = std::sqrt(sum / std::max<size_t>(count, 1));
            mean = total / std::max<size_t>(count, 1);
            referenceMean = totalReference / std::max<size_t>(count, 1);
        };
        const auto flicker = [&](const std::vector<uint16_t>& a, const std::vector<uint16_t>& b)
        {
            double sum = 0.0;
            size_t count = 0;
            for (size_t i = 0; i < referenceSun.size(); ++i)
                if (depth[i] > 0.0f)
                {
                    for (int c = 0; c < 3; ++c)
                        sum += std::fabs(HalfToFloat(a[i * 4 + c]) - HalfToFloat(b[i * 4 + c]));
                    ++count;
                }
            return sum / std::max<size_t>(count, 1);
        };
        double noisyDiffuseError, noisySunError, unused, unusedReference;
        error(noisyDiffuse, true, noisyDiffuseError, unused, unusedReference);
        error(noisySun, false, noisySunError, unused, unusedReference);
        const double noisyFlicker = flicker(noisyDiffuse, noisyDiffuse2);

        // The glide, denoised: from 6 m to the side, the previous frame's matrix given;
        // `drawn` the G-buffer's scene.
        const auto glide = [&](const std::vector<Instance>& drawn, uint32_t firstFrame, std::vector<uint16_t>& diffuseOut,
                               std::vector<uint16_t>& sunOut, std::vector<uint16_t>& lastOut)
        {
            Camera previous = end;
            for (int f = 0; f < 90; ++f)
            {
                Camera now = end;
                const double t = std::min(1.0, f / 60.0);
                now.position = end.position + Vec3{ -6.0 * (1.0 - t), 2.0 * (1.0 - t), 0.0 };
                now.Update();
                // The previous camera's clip from this camera's relative space: shift by the
                // cameras' offset, then the previous transform.
                Mat4 shift;
                for (int d = 0; d < 4; ++d)
                    shift.at(d, d) = 1.0;
                shift.at(0, 3) = now.position.x - previous.position.x;
                shift.at(1, 3) = now.position.y - previous.position.y;
                shift.at(2, 3) = now.position.z - previous.position.z;
                const Mat4 previousClip = Multiply(previous.worldToClip, shift);
                Lighting moving = lighting;
                moving.flags = kRtFrameDenoise | (f == 0 ? kRtFrameReset : 0u);
                moving.previous = &previousClip;
                SendInstances(proxy, drawn, now.position);
                RenderGBuffer(context.Get(), g, now, meshes, drawn);
                SendFrame(proxy, now, g, outputs, sun, sunRadius, firstFrame + f, signals, moving);
                if (f == 88)
                    ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, lastOut);
                previous = now;
            }
            ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, diffuseOut);
            ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, sunOut);
        };
        std::vector<uint16_t> denoisedDiffuse, denoisedSun, lastDiffuse;
        glide(scene, 10000, denoisedDiffuse, denoisedSun, lastDiffuse);
        double diffuseError, diffuseMean, diffuseReference, sunError, sunMean, sunReference;
        error(denoisedDiffuse, true, diffuseError, diffuseMean, diffuseReference);
        error(denoisedSun, false, sunError, sunMean, sunReference);
        const double denoisedFlicker = flicker(lastDiffuse, denoisedDiffuse);

        char detail[300];
        sprintf_s(detail, "diffuse light off the 1024-frame reference by %.4f (one noisy frame %.4f), its mean %.4f"
                          " against %.4f, frame to frame %.4f (noisy %.4f)",
                  diffuseError, noisyDiffuseError, diffuseMean, diffuseReference, denoisedFlicker, noisyFlicker);
        Check(diffuseError < 0.35 * noisyDiffuseError && std::fabs(diffuseMean - diffuseReference) < 0.03 * diffuseReference
                  && denoisedFlicker < 0.2 * noisyFlicker,
              "denoised diffuse light", detail);
        sprintf_s(detail, "sun visibility off the reference by %.4f (one noisy frame %.4f), its mean %.4f against %.4f",
                  sunError, noisySunError, sunMean, sunReference);
        // The sun's visibility keeps a short history and no spatial steps (rtSunAtrousSteps,
        // rtSunHistory), for the shadows of the blades of grass: less of its noise goes than of
        // the diffuse light's (0.37 of a noisy frame's error with 0 steps and 4 frames).
        Check(sunError < 0.45 * noisySunError && std::fabs(sunMean - sunReference) < 0.03 * sunReference,
              "denoised sun visibility", detail);

        std::vector<double> image(referenceDiffuse.size());
        for (size_t i = 0; i < referenceSun.size(); ++i)
            for (int c = 0; c < 3; ++c)
                image[i * 3 + c] = HalfToFloat(denoisedDiffuse[i * 4 + c]);
        SaveColour(L"rt_denoised_diffuse.bmp", image, 1.0);
        for (size_t i = 0; i < referenceSun.size(); ++i)
            for (int c = 0; c < 3; ++c)
                image[i * 3 + c] = HalfToFloat(noisyDiffuse[i * 4 + c]);
        SaveColour(L"rt_noisy_diffuse.bmp", image, 1.0);
        SaveColour(L"rt_reference_diffuse.bmp", referenceDiffuse, 1.0);
        for (size_t i = 0; i < referenceSun.size(); ++i)
            image[i * 3] = image[i * 3 + 1] = image[i * 3 + 2] = HalfToFloat(denoisedSun[i * 4]);
        SaveColour(L"rt_denoised_sun.bmp", image, 1.0);

        float passes[kRtPassTimings] = {};
        proxy.passTimings(passes, kRtPassTimings);
        printf("      denoiser at %ux%u: %.3f ms for sun and diffuse light\n", kWidth, kHeight, passes[6]);

        Progress("the glide over smooth ground");
        // The same glide over a mirror-smooth ground: the diffuse light does not move with
        // the view, and keeps its history and filter as on the rough ground.
        std::vector<Instance> smooth = scene;
        smooth[0].smoothness = 1.0f;
        std::vector<uint16_t> smoothDiffuse, smoothSun, smoothLast;
        glide(smooth, 10200, smoothDiffuse, smoothSun, smoothLast);
        double smoothError, smoothMean, smoothReference;
        error(smoothDiffuse, true, smoothError, smoothMean, smoothReference);
        const double smoothFlicker = flicker(smoothLast, smoothDiffuse);
        sprintf_s(detail, "over a smooth ground off the reference by %.4f and frame to frame %.4f, over a rough one %.4f and "
                          "%.4f",
                  smoothError, smoothFlicker, diffuseError, denoisedFlicker);
        Check(smoothError < 1.3 * diffuseError + 1e-4 && smoothFlicker < 1.5 * denoisedFlicker + 1e-4,
              "denoised diffuse light on a smooth surface", detail);

        Progress("a signal paused and back");
        // A signal paused and back: the diffuse light off for four frames while the light
        // goes out, then on again without a reset. Its history from before the pause is
        // not this light's: the first frame back is dark, as the light is.
        Lighting dark = lighting;
        dark.sky[0] = dark.sky[1] = dark.sky[2] = 0.0f;
        dark.sunColor[0] = dark.sunColor[1] = dark.sunColor[2] = 0.0f;
        dark.flags = kRtFrameDenoise;
        SendInstances(proxy, scene, end.position);
        RenderGBuffer(context.Get(), g, end, meshes, scene);
        for (int f = 0; f < 4; ++f)
            SendFrame(proxy, end, g, outputs, sun, sunRadius, 10400 + f, kRtSignalSunVisibility, dark);
        std::vector<uint16_t> back;
        SendFrame(proxy, end, g, outputs, sun, sunRadius, 10404, signals, dark);
        ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, back);
        double backError, backMean, backReference;
        error(back, true, backError, backMean, backReference);
        sprintf_s(detail, "the first frame back %.4f in the dark, before the pause %.4f", backMean, diffuseMean);
        Check(backMean < 0.01 * diffuseMean, "a denoised signal paused and back", detail);

        Progress("the short history for thin shadows");
        // What the short history and no spatial steps are for (rtSunAtrousSteps 0,
        // rtSunHistory 4): the shadows of thin posts, as of the blades of grass, keep their
        // fine structure, the reference's detail (each pixel less its 9x9 mean) in the
        // denoised visibility's, as a share. Against 5 steps and 32 frames, switched live
        // through the ini beside the harness, which the proxy reads again at a Present;
        // then back, the glide's visibility as before to the bit.
        std::vector<Instance> posts = scene;
        for (int i = 0; i < 12; ++i)
            for (int j = 0; j < 12; ++j)
            {
                Instance post;
                post.mesh = 1;
                post.position = { 6.0 + i * 1.3, 0.6, -24.0 + j * 1.3 };
                post.scale[0] = post.scale[2] = 0.12;
                post.scale[1] = 1.2;
                post.albedo[0] = 0.3f; post.albedo[1] = 0.6f; post.albedo[2] = 0.2f;
                posts.push_back(post);
            }
        SendInstances(proxy, posts, end.position);
        RenderGBuffer(context.Get(), g, end, meshes, posts);
        std::vector<double> postsReference(static_cast<size_t>(kWidth) * kHeight, 0.0);
        const int postsFrames = 512;
        for (int f = 0; f < postsFrames; ++f)
        {
            SendFrame(proxy, end, g, outputs, sun, sunRadius, 11000 + f, kRtSignalSunVisibility, lighting);
            ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, read);
            for (size_t i = 0; i < postsReference.size(); ++i)
                postsReference[i] += HalfToFloat(read[i * 4]) / postsFrames;
        }
        std::vector<float> postsDepth;
        ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, postsDepth);
        const auto structureKept = [&](const std::vector<uint16_t>& image)
        {
            const int r = 4;
            const size_t w = kWidth + 1;
            std::vector<double> sumImage(w * (kHeight + 1), 0.0), sumReference(sumImage.size(), 0.0),
                surfaces(sumImage.size(), 0.0);
            for (UINT y = 0; y < kHeight; ++y)
                for (UINT x = 0; x < kWidth; ++x)
                {
                    const size_t i = static_cast<size_t>(y) * kWidth + x;
                    const size_t at = (y + 1) * w + x + 1;
                    const bool surface = postsDepth[i] > 0.0f;
                    sumImage[at] = (surface ? HalfToFloat(image[i * 4]) : 0.0) + sumImage[at - 1] + sumImage[at - w]
                                   - sumImage[at - w - 1];
                    sumReference[at] = (surface ? postsReference[i] : 0.0) + sumReference[at - 1] + sumReference[at - w]
                                       - sumReference[at - w - 1];
                    surfaces[at] = (surface ? 1.0 : 0.0) + surfaces[at - 1] + surfaces[at - w] - surfaces[at - w - 1];
                }
            const auto box = [&](const std::vector<double>& sums, UINT x, UINT y)
            {
                const size_t x0 = x - r, x1 = x + r + 1, y0 = y - r, y1 = y + r + 1;
                return sums[y1 * w + x1] - sums[y0 * w + x1] - sums[y1 * w + x0] + sums[y0 * w + x0];
            };
            const double area = (2.0 * r + 1) * (2.0 * r + 1);
            double shared = 0.0, own = 0.0;
            for (UINT y = r; y + r < kHeight; ++y)
                for (UINT x = r; x + r < kWidth; ++x)
                {
                    if (box(surfaces, x, y) < area)
                        continue;
                    const size_t i = static_cast<size_t>(y) * kWidth + x;
                    const double reference = postsReference[i] - box(sumReference, x, y) / area;
                    const double got = HalfToFloat(image[i * 4]) - box(sumImage, x, y) / area;
                    shared += got * reference;
                    own += reference * reference;
                }
            return own > 0.0 ? shared / own : 0.0;
        };
        const std::wstring ini = NextToExecutable(L"ReDefinitionProxy.ini");
        const int defaultSteps = static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtSunAtrousSteps", 0, ini.c_str()));
        const int defaultHistory = static_cast<int>(GetPrivateProfileIntW(L"Proxy", L"rtSunHistory", 4, ini.c_str()));
        const auto setSun = [&](int steps, int history)
        {
            WritePrivateProfileStringW(L"Proxy", L"rtSunAtrousSteps", std::to_wstring(steps).c_str(), ini.c_str());
            WritePrivateProfileStringW(L"Proxy", L"rtSunHistory", std::to_wstring(history).c_str(), ini.c_str());
            // The proxy looks at the file at most once a second, from its Present.
            Sleep(1100);
            swapChain->Present(0, 0);
        };
        std::vector<uint16_t> postsDiffuse, postsSun, postsLast, wideSun, backSun;
        glide(posts, 11600, postsDiffuse, postsSun, postsLast);
        setSun(5, 32);
        glide(posts, 11600, postsDiffuse, wideSun, postsLast);
        setSun(defaultSteps, defaultHistory);
        glide(posts, 11600, postsDiffuse, backSun, postsLast);
        const double keptNow = structureKept(postsSun), keptWide = structureKept(wideSun);
        sprintf_s(detail, "%.2f of the reference's fine structure kept with %d steps and %d frames, %.2f with 5 and 32;"
                          " %s afterwards",
                  keptNow, defaultSteps, defaultHistory, keptWide, backSun == postsSun ? "the same" : "not the same");
        Check(defaultSteps == 0 && defaultHistory == 4 && backSun == postsSun && keptNow > 0.8 && keptNow > keptWide + 0.15,
              "the sun's fine shadows kept", detail);
        SendInstances(proxy, scene, end.position);
        RenderGBuffer(context.Get(), g, end, meshes, scene);

        Progress("the denoiser's video memory");
        // The denoiser's textures: per signal it filters 64 bytes a pixel (the noisy trace,
        // two histories in full floats, two moments, a work texture in half floats), for
        // any 40 more (the work texture, two view distances and normals). The process's
        // video memory with the sun, the diffuse light and the reflections denoised; with
        // the sun alone, once the slots holding the others' textures came round; and after
        // raytracing stopped (render event 33), which lets the sun's go too. Then the sun
        // denoised again, its textures made anew.
        ComPtr<IDXGIAdapter3> adapter3;
        adapter.As(&adapter3);
        const auto videoMemory = [&]()
        {
            DXGI_QUERY_VIDEO_MEMORY_INFO info = {};
            if (adapter3 == nullptr || FAILED(adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info)))
                return 0.0;
            return static_cast<double>(info.CurrentUsage) / 1e6;
        };
        const uint32_t all = kRtSignalSunVisibility | kRtSignalDiffuse | kRtSignalSpecular;
        Lighting denoised = lighting;
        denoised.flags = kRtFrameDenoise;
        std::vector<uint16_t> synced;
        for (int f = 0; f < 4; ++f)
            SendFrame(proxy, end, g, outputs, sun, sunRadius, 10500 + f, all, denoised);
        ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, synced);
        const double withAll = videoMemory();
        for (int f = 0; f < 4; ++f)
            SendFrame(proxy, end, g, outputs, sun, sunRadius, 10504 + f, kRtSignalSunVisibility, denoised);
        ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, synced);
        const double withSun = videoMemory();
        proxy.event(RT_RELEASE_EVENT, nullptr);
        const double stopped = videoMemory();
        const double pixelsMb = static_cast<double>(kWidth) * kHeight / 1e6;
        const double twoSignals = 2 * 64 * pixelsMb, sunAlone = (64 + 40) * pixelsMb;
        sprintf_s(detail, "%.1f MB with three signals, %.1f with the sun alone (%.1f less, %.1f expected), %.1f once stopped"
                          " (%.1f less, at least %.1f expected)",
                  withAll, withSun, withAll - withSun, twoSignals, stopped, withSun - stopped, sunAlone);
        Check(adapter3 != nullptr && withAll - withSun > 0.9 * twoSignals && withSun - stopped > 0.9 * sunAlone,
              "the denoiser's textures for its signals", detail);

        Lighting again = denoised;
        for (int f = 0; f < 30; ++f)
        {
            again.flags = kRtFrameDenoise | (f == 0 ? kRtFrameReset : 0u);
            SendFrame(proxy, end, g, outputs, sun, sunRadius, 10510 + f, kRtSignalSunVisibility, again);
        }
        std::vector<uint16_t> afterStop;
        ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, afterStop);
        double stopError, stopMean, stopReference;
        error(afterStop, false, stopError, stopMean, stopReference);
        sprintf_s(detail, "sun visibility off the reference by %.4f (one noisy frame %.4f), its mean %.4f against %.4f",
                  stopError, noisySunError, stopMean, stopReference);
        Check(stopError < 0.45 * noisySunError && std::fabs(stopMean - stopReference) < 0.03 * stopReference,
              "the denoiser after raytracing stopped", detail);
    }

    Progress("Super Resolution beside Ray Reconstruction");
    // ---------------------------------------------------------------- Super Resolution beside Ray Reconstruction
    // DLSS Super Resolution on this Direct3D 11 device (Dlss.cpp), started here, before the
    // proxy starts NGX on its Direct3D 12 device for Ray Reconstruction, and kept running
    // past the end of the harness' checks, where that instance is shut down.
    using SrStatusFn = int (*)(char*, int);
    const auto srStatus = reinterpret_cast<SrStatusFn>(GetProcAddress(proxy.module, "KspDlssStatus"));
    char srText[512] = {};
    constexpr UINT srRenderWidth = 320, srRenderHeight = 180;
    ComPtr<ID3D11Texture2D> srColour, srOutput, srDepth, srMotion;
    const uint16_t srGrey[4] = { 0x3400, 0x3800, 0x3A00, 0x3C00 };   // 0.25, 0.5, 0.75, 1
    const uint16_t srBlack[4] = { 0, 0, 0, 0x3C00 };
    const auto srFilled = [&](UINT width, UINT height, DXGI_FORMAT format, size_t texel, const void* value, UINT bindFlags,
                              ComPtr<ID3D11Texture2D>& out)
    {
        D3D11_TEXTURE2D_DESC textureDesc = {};
        textureDesc.Width = width;
        textureDesc.Height = height;
        textureDesc.MipLevels = 1;
        textureDesc.ArraySize = 1;
        textureDesc.Format = format;
        textureDesc.SampleDesc.Count = 1;
        textureDesc.BindFlags = bindFlags;
        std::vector<uint8_t> data(static_cast<size_t>(width) * height * texel);
        for (size_t i = 0; i < static_cast<size_t>(width) * height; ++i)
            memcpy(&data[i * texel], value, texel);
        D3D11_SUBRESOURCE_DATA initial = { data.data(), static_cast<UINT>(width * texel), 0 };
        return device->CreateTexture2D(&textureDesc, &initial, &out);
    };
    Progress("Super Resolution beside Ray Reconstruction: frames");
    // Frames through the render event, as the rig sends them, into a black output each
    // time; whether its centre is then the flat image the colour was.
    const auto superResolution = [&](UINT firstFrame, bool& flat)
    {
        srOutput.Reset();
        if (FAILED(srFilled(kWidth / 2, kHeight / 2, DXGI_FORMAT_R16G16B16A16_FLOAT, 8, srBlack,
                            D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS, srOutput)))
            return -1;
        int state = 0;
        for (UINT frame = firstFrame; frame < firstFrame + 8; ++frame)
        {
            DlssPacket packet = {};
            packet.size = sizeof(packet);
            packet.magic = kDlssPacketMagic;
            packet.colour = srColour.Get();
            packet.output = srOutput.Get();
            packet.depth = srDepth.Get();
            packet.motionVectors = srMotion.Get();
            packet.renderWidth = srRenderWidth;
            packet.renderHeight = srRenderHeight;
            packet.outputWidth = kWidth / 2;
            packet.outputHeight = kHeight / 2;
            packet.quality = 0;
            packet.preset = 11;
            packet.flags = kDlssHdr | kDlssDepthInverted | kDlssAutoExposure | (frame == firstFrame ? kDlssReset : 0u);
            packet.jitterX = 0.25f * static_cast<float>(frame % 4) - 0.375f;
            packet.jitterY = 0.25f * static_cast<float>((frame / 4) % 4) - 0.375f;
            packet.motionVectorScaleX = static_cast<float>(srRenderWidth);
            packet.motionVectorScaleY = static_cast<float>(srRenderHeight);
            proxy.event(2, &packet);
            state = srStatus(srText, sizeof(srText));
        }
        std::vector<uint16_t> read;
        flat = false;
        if (state > 0 && ReadTexture(device.Get(), context.Get(), srOutput.Get(), 8, read))
        {
            const size_t centre = (static_cast<size_t>(kHeight / 4) * (kWidth / 2) + kWidth / 4) * 4;
            flat = std::fabs(HalfToFloat(read[centre]) - 0.25f) < 0.05f
                   && std::fabs(HalfToFloat(read[centre + 1]) - 0.5f) < 0.05f
                   && std::fabs(HalfToFloat(read[centre + 2]) - 0.75f) < 0.05f;
        }
        return state;
    };
    bool srRunning = false;
    if (srStatus != nullptr)
    {
        const float srHalf = 0.5f;
        const uint32_t srNone = 0;
        HRESULT srMade = srFilled(srRenderWidth, srRenderHeight, DXGI_FORMAT_R16G16B16A16_FLOAT, 8, srGrey,
                                  D3D11_BIND_SHADER_RESOURCE, srColour);
        if (SUCCEEDED(srMade))
            srMade = srFilled(srRenderWidth, srRenderHeight, DXGI_FORMAT_R32_FLOAT, 4, &srHalf, D3D11_BIND_SHADER_RESOURCE,
                              srDepth);
        if (SUCCEEDED(srMade))
            srMade = srFilled(srRenderWidth, srRenderHeight, DXGI_FORMAT_R16G16_FLOAT, 4, &srNone,
                              D3D11_BIND_SHADER_RESOURCE, srMotion);
        if (FAILED(srMade))
            return Fatal("the Super Resolution textures", srMade);
        bool srFlat = false;
        srRunning = superResolution(0, srFlat) > 0 && srFlat;
        if (!srRunning)
            printf("      Super Resolution beside Ray Reconstruction skipped: %s\n", srText);
    }

    Progress("Ray Reconstruction");
    // ---------------------------------------------------------------- Ray Reconstruction
    // Through the route the proxy took for this swapchain (rrPath): Streamline's where
    // its swapchain presents, NGX's otherwise. Where it was Streamline's, a swapchain of
    // its own at the end takes NGX's (Ray Reconstruction through NGX, below).
    const std::vector<Instance> rrScene = scene;
    const Camera rrCamera = camera;
    bool reconstructedThroughStreamline = false;
    const auto reconstruct = [&](const std::string& route)
    {
        {
            // The noisy frame put together as Unity's lighting would with raw signals:
            // albedo times the sun and the diffuse light, plus the reflections times a
            // dielectric's Fresnel. Ray Reconstruction at the render size (DLAA) against
            // the same made of 256-frame averages.
            SendInstances(proxy, rrScene, rrCamera.position);
            RenderGBuffer(context.Get(), g, rrCamera, meshes, rrScene);
            Lighting lighting;
            lighting.sky[0] = 0.4f;
            lighting.sky[1] = 0.5f;
            lighting.sky[2] = 0.7f;
            lighting.bounces = 2.0f;
            lighting.clamp = 20.0f;
            const uint32_t signals = kRtSignalSunVisibility | kRtSignalDiffuse | kRtSignalSpecular;
            std::vector<float> depth;
            std::vector<uint32_t> normals, albedo;
            ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
            ReadTexture(device.Get(), context.Get(), g.gbuffer2.Get(), 4, normals);
            ReadTexture(device.Get(), context.Get(), g.gbuffer0.Get(), 4, albedo);
            const Vec3 l = Normalize(sun);
            const size_t pixels = static_cast<size_t>(kWidth) * kHeight;
            const auto compose = [&](const std::vector<double>& sunVis, const std::vector<double>& diffuse,
                                     const std::vector<double>& specular, std::vector<float>& out)
            {
                out.assign(pixels * 4, 0.0f);
                for (size_t i = 0; i < pixels; ++i)
                {
                    float* p = &out[i * 4];
                    p[3] = 1.0f;
                    if (depth[i] <= 0.0f)
                    {
                        p[0] = lighting.sky[0];
                        p[1] = lighting.sky[1];
                        p[2] = lighting.sky[2];
                        continue;
                    }
                    const uint32_t packed = normals[i];
                    const Vec3 n = Normalize({ (packed & 0x3FF) / 1023.0 * 2 - 1, ((packed >> 10) & 0x3FF) / 1023.0 * 2 - 1,
                                               ((packed >> 20) & 0x3FF) / 1023.0 * 2 - 1 });
                    const double direct = std::max(0.0, Dot(n, l)) * sunVis[i];
                    for (int c = 0; c < 3; ++c)
                    {
                        const double a = ((albedo[i] >> (8 * c)) & 0xFF) / 255.0;
                        p[c] = static_cast<float>(a * (direct + diffuse[i * 3 + c]) + 0.04 * specular[i * 3 + c]);
                    }
                }
            };

            // The reference: the signals averaged over 256 frames, put together.
            std::vector<double> sunMean(pixels, 0.0), diffuseMean(pixels * 3, 0.0), specularMean(pixels * 3, 0.0);
            std::vector<uint16_t> read;
            const auto readSignals = [&](std::vector<double>& s, std::vector<double>& d, std::vector<double>& sp, double w)
            {
                ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, read);
                for (size_t i = 0; i < pixels; ++i)
                    s[i] += w * HalfToFloat(read[i * 4]);
                ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, read);
                for (size_t i = 0; i < pixels * 3; ++i)
                    d[i] += w * HalfToFloat(read[(i / 3) * 4 + i % 3]);
                ReadTexture(device.Get(), context.Get(), outputs.specular.Get(), 8, read);
                for (size_t i = 0; i < pixels * 3; ++i)
                    sp[i] += w * HalfToFloat(read[(i / 3) * 4 + i % 3]);
            };
            for (int f = 0; f < 256; ++f)
            {
                SendFrame(proxy, rrCamera, g, outputs, sun, 0.02f, 20000 + f, signals, lighting);
                readSignals(sunMean, diffuseMean, specularMean, 1.0 / 256.0);
            }
            std::vector<float> reference;
            compose(sunMean, diffuseMean, specularMean, reference);

            D3D11_TEXTURE2D_DESC colourDesc = {};
            colourDesc.Width = kWidth;
            colourDesc.Height = kHeight;
            colourDesc.MipLevels = 1;
            colourDesc.ArraySize = 1;
            colourDesc.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
            colourDesc.SampleDesc.Count = 1;
            colourDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            ComPtr<ID3D11Texture2D> colour, motion, output;
            HRESULT made = device->CreateTexture2D(&colourDesc, nullptr, &colour);
            colourDesc.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
            if (SUCCEEDED(made))
                made = device->CreateTexture2D(&colourDesc, nullptr, &output);
            colourDesc.Format = DXGI_FORMAT_R16G16_FLOAT;
            std::vector<uint32_t> still(pixels, 0);
            const D3D11_SUBRESOURCE_DATA stillData = { still.data(), kWidth * 4, 0 };
            if (SUCCEEDED(made))
                made = device->CreateTexture2D(&colourDesc, &stillData, &motion);
            if (FAILED(made))
            {
                Fatal("Ray Reconstruction's textures", made);
                ++failures;
                return;
            }

            // The matrices as Streamline takes them: for row vectors, the view looking
            // down +z.
            Mat4 flipZ;
            for (int d = 0; d < 4; ++d)
                flipZ.at(d, d) = 1.0;
            flipZ.at(2, 2) = -1.0;
            const Mat4 view = LookRotation(rrCamera.target - rrCamera.position, { 0, 1, 0 });
            const Mat4 projection = Perspective(rrCamera.fov, double(kWidth) / kHeight, rrCamera.nearPlane, rrCamera.farPlane);
            const auto transposed = [](const Mat4& m, float* out)
            {
                for (int r = 0; r < 4; ++r)
                    for (int c = 0; c < 4; ++c)
                        out[r * 4 + c] = static_cast<float>(m.at(c, r));
            };
            const Mat4 viewPlusZ = Multiply(flipZ, view);
            const Mat4 projectionPlusZ = Multiply(projection, flipZ);
            Mat4 identity;
            for (int d = 0; d < 4; ++d)
                identity.at(d, d) = 1.0;

            RtReconstructPacket packet = {};
            packet.size = sizeof(packet);
            packet.magic = kRtReconstructMagic;
            packet.renderWidth = packet.outputWidth = kWidth;
            packet.renderHeight = packet.outputHeight = kHeight;
            packet.mode = 6;     // sl::DLSSMode::eDLAA
            packet.preset = 0;   // sl::DLSSDPreset::eDefault
            ToFloats(rrCamera.clipToWorld, packet.clipToWorld);
            transposed(projectionPlusZ, packet.viewToClip);
            transposed(Inverse(projectionPlusZ), packet.clipToView);
            transposed(identity, packet.clipToPrevClip);
            transposed(identity, packet.prevClipToClip);
            transposed(viewPlusZ, packet.worldToView);
            transposed(Inverse(viewPlusZ), packet.viewToWorld);
            packet.motionScale[0] = 1.0f / kWidth;
            packet.motionScale[1] = 1.0f / kHeight;
            const Vec3 forward = Normalize(rrCamera.target - rrCamera.position);
            const Vec3 right = Normalize(Cross(forward, { 0, 1, 0 }));
            const Vec3 up = Cross(right, forward);
            const float basis[3][3] = { { float(up.x), float(up.y), float(up.z) },
                                        { float(right.x), float(right.y), float(right.z) },
                                        { float(forward.x), float(forward.y), float(forward.z) } };
            memcpy(packet.up, basis[0], 12);
            memcpy(packet.right, basis[1], 12);
            memcpy(packet.forward, basis[2], 12);
            packet.nearPlane = static_cast<float>(rrCamera.nearPlane);
            packet.farPlane = static_cast<float>(rrCamera.farPlane);
            packet.verticalFov = static_cast<float>(rrCamera.fov);
            packet.colour = colour.Get();
            packet.depth = g.depth.Get();
            packet.motion = motion.Get();
            packet.gbuffer0 = g.gbuffer0.Get();
            packet.gbuffer1 = g.gbuffer1.Get();
            packet.gbuffer2 = g.gbuffer2.Get();
            packet.specular = outputs.specular.Get();
            packet.output = output.Get();

            double noisyError = 0.0, noisyFlicker = 0.0;
            std::vector<float> composed, previousOutput;
            std::vector<uint16_t> result;
            double reconstructedFlicker = 0.0;
            for (int f = 0; f < 64; ++f)
            {
                SendFrame(proxy, rrCamera, g, outputs, sun, 0.02f, 21000 + f, signals, lighting);
                std::vector<double> s(pixels, 0.0), d(pixels * 3, 0.0), sp(pixels * 3, 0.0);
                readSignals(s, d, sp, 1.0);
                compose(s, d, sp, composed);
                context->UpdateSubresource(colour.Get(), 0, nullptr, composed.data(), kWidth * 16, 0);
                packet.frameIndex = static_cast<uint32_t>(f);
                packet.flags = f == 0 ? kRtFrameReset : 0u;
                proxy.event(RT_RECONSTRUCT_EVENT, &packet);
                if (f == 62 || f == 63)
                {
                    ReadTexture(device.Get(), context.Get(), output.Get(), 8, result);
                    std::vector<float> current(pixels * 3);
                    for (size_t i = 0; i < pixels * 3; ++i)
                        current[i] = HalfToFloat(result[(i / 3) * 4 + i % 3]);
                    if (f == 63)
                    {
                        for (size_t i = 0; i < pixels * 3; ++i)
                            reconstructedFlicker += std::fabs(current[i] - previousOutput[i]);
                        reconstructedFlicker /= pixels * 3;
                    }
                    previousOutput = current;
                }
            }
            double sum = 0.0, sumNoisy = 0.0, mean = 0.0, meanReference = 0.0;
            for (size_t i = 0; i < pixels; ++i)
                for (int c = 0; c < 3; ++c)
                {
                    const double want = reference[i * 4 + c];
                    const double got = previousOutput[i * 3 + c];
                    sum += (got - want) * (got - want);
                    sumNoisy += (composed[i * 4 + c] - want) * (composed[i * 4 + c] - want);
                    mean += got;
                    meanReference += want;
                }
            const double error = std::sqrt(sum / (pixels * 3));
            noisyError = std::sqrt(sumNoisy / (pixels * 3));
            mean /= pixels * 3;
            meanReference /= pixels * 3;
            printf("      %s\n", proxy.Status().c_str());
            char detail[300];
            sprintf_s(detail, "the reconstructed frame off the 256-frame reference by %.4f (the noisy frame %.4f), its mean"
                              " %.4f against %.4f, frame to frame %.4f",
                      error, noisyError, mean, meanReference, reconstructedFlicker);
            (void)noisyFlicker;
            Check(error < 0.5 * noisyError && std::fabs(mean - meanReference) < 0.05 * meanReference,
                  ("DLSS Ray Reconstruction" + route).c_str(), detail);
            std::vector<double> image(pixels * 3);
            for (size_t i = 0; i < pixels * 3; ++i)
                image[i] = previousOutput[i];
            SaveColour(route.empty() ? L"rt_reconstructed.bmp" : L"rt_reconstructed_ngx.bmp", image, 1.0);
            for (size_t i = 0; i < pixels; ++i)
                for (int c = 0; c < 3; ++c)
                    image[i * 3 + c] = composed[i * 4 + c];
            if (route.empty())
                SaveColour(L"rt_reconstruct_noisy.bmp", image, 1.0);

            // Ray Reconstruction let go as the mod does when it stops using it (event 32):
            // Streamline's resources freed, and made again by the next evaluation below.
            proxy.event(32, nullptr);

            // Without a G-buffer, as under forward rendering: the guides from the raytraced
            // frame of the same index, which takes the surface from its rays.
            Lighting unbuffered = lighting;
            unbuffered.gbuffer = false;
            unbuffered.flags |= kRtFrameGuides;
            packet.gbuffer0 = packet.gbuffer1 = packet.gbuffer2 = nullptr;
            packet.specular = nullptr;
            const size_t reconstructedBefore = proxy.Reconstructed();
            for (int f = 0; f < 64; ++f)
            {
                SendFrame(proxy, rrCamera, g, outputs, sun, 0.02f, 22000 + f, signals, unbuffered);
                std::vector<double> s(pixels, 0.0), d(pixels * 3, 0.0), sp(pixels * 3, 0.0);
                readSignals(s, d, sp, 1.0);
                compose(s, d, sp, composed);
                context->UpdateSubresource(colour.Get(), 0, nullptr, composed.data(), kWidth * 16, 0);
                packet.frameIndex = static_cast<uint32_t>(22000 + f);
                packet.flags = f == 0 ? kRtFrameReset : 0u;
                proxy.event(RT_RECONSTRUCT_EVENT, &packet);
            }
            ReadTexture(device.Get(), context.Get(), output.Get(), 8, result);
            double sumForward = 0.0, meanForward = 0.0;
            for (size_t i = 0; i < pixels; ++i)
                for (int c = 0; c < 3; ++c)
                {
                    const double got = HalfToFloat(result[i * 4 + c]);
                    const double want = reference[i * 4 + c];
                    sumForward += (got - want) * (got - want);
                    meanForward += got;
                }
            const double errorForward = std::sqrt(sumForward / (pixels * 3));
            meanForward /= pixels * 3;
            const size_t reconstructedForward = proxy.Reconstructed() - reconstructedBefore;
            sprintf_s(detail, "%zu of 64 frames reconstructed with the guides of their raytraced frame, off the reference by"
                              " %.4f, its mean %.4f against %.4f",
                      reconstructedForward, errorForward, meanForward, meanReference);
            Check(reconstructedForward == 64 && errorForward < 0.5 * noisyError
                      && std::fabs(meanForward - meanReference) < 0.05 * meanReference,
                  ("DLSS Ray Reconstruction without a G-buffer" + route).c_str(), detail);
        }
    };
    {
        char why[256] = {};
        if (proxy.reconstructionAvailable == nullptr || proxy.reconstructionAvailable(why, sizeof(why)) != 1)
            printf("      DLSS Ray Reconstruction skipped: %s\n", why);
        else
        {
            printf("      DLSS Ray Reconstruction %s\n", why);
            reconstructedThroughStreamline = strstr(why, "Streamline") != nullptr;
            reconstruct(reconstructedThroughStreamline ? "" : " through NGX");
        }
    }

    // ---------------------------------------------------------------- the planet's shadow
    {
        // A box high over the ground, the sun low under the horizon of a planet the ground
        // lies on: without the planet's sphere the box's sunward faces are lit, with it
        // they are in the planet's shadow.
        std::vector<Instance> high = { box };
        high[0].position = { 0, 150, 0 };
        Camera up = camera;
        up.position = { 30, 140, 60 };
        up.target = { 0, 150, 0 };
        up.Update();
        SendInstances(proxy, high, up.position);
        RenderGBuffer(context.Get(), g, up, meshes, high);
        const Vec3 low = { 0.0, -0.25, 1.0 };
        const auto litPixels = [&](const Lighting& lighting) -> size_t
        {
            SendFrame(proxy, up, g, outputs, low, 0.0f, 12000, kRtSignalSunVisibility, lighting);
            std::vector<uint16_t> vis;
            ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, vis);
            std::vector<float> depth;
            ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
            size_t lit = 0;
            for (size_t i = 0; i < depth.size(); ++i)
                lit += depth[i] > 0.0f && HalfToFloat(vis[i * 4]) > 0.5f ? 1 : 0;
            return lit;
        };
        Lighting open;
        Lighting planet;
        planet.body[0] = 0;
        planet.body[1] = -600000.0;
        planet.body[2] = 0;
        planet.body[3] = 600000.0;
        const size_t without = litPixels(open);
        const size_t with = litPixels(planet);
        char detail[160];
        sprintf_s(detail, "%zu of the box's pixels lit by a sun under the horizon without the planet's sphere, %zu with it",
                  without, with);
        Check(without > 1000 && with == 0, "the planet's shadow", detail);
    }

    // ---------------------------------------------------------------- displaced terrain
    {
        // The ground drawn at y = 0, the rays' ground 0.3 m higher, as Parallax's
        // displacement leaves the drawn terrain below its mesh: as terrain, the ground
        // stays lit where the sun reaches it; as any other mesh, it shades itself.
        const auto litGround = [&](uint32_t flags) -> size_t
        {
            std::vector<Instance> raised;
            for (Instance instance : scene)
            {
                if (instance.mesh == 2)
                {
                    instance.position.y += 0.3;
                    instance.flags = flags;
                }
                raised.push_back(instance);
            }
            SendInstances(proxy, raised, camera.position);
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, 13000 + flags, kRtSignalSunVisibility);
            std::vector<uint16_t> vis;
            ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, vis);
            size_t lit = 0;
            for (size_t i = 0; i < first.depth.size(); ++i)
                lit += first.depth[i] > 0.0f && HalfToFloat(vis[i * 4]) > 0.5f ? 1 : 0;
            return lit;
        };
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        size_t expected = 0;
        for (size_t i = 0; i < first.depth.size(); ++i)
            expected += first.depth[i] > 0.0f && HalfToFloat(first.visibility[i * 4]) > 0.5f ? 1 : 0;
        const size_t asTerrain = litGround(kRtInstanceTerrain);
        const size_t asMesh = litGround(0);
        char detail[200];
        sprintf_s(detail, "%zu lit pixels over ground 0.3 m above the drawn one as terrain, %zu as another mesh, %zu with"
                          " the ground where it is drawn", asTerrain, asMesh, expected);
        Check(asTerrain * 1000 >= expected * 995 && asTerrain * 1000 <= expected * 1005 && asMesh * 2 < expected,
              "displaced terrain", detail);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- cut-out surfaces
    {
        // A card 3 m over the ground, the sun straight above: its small texture covered on
        // the left third, half covered in the middle, cut out on the right. Under the left
        // third the ground lies in its shadow, under the middle in half of it, as a texel
        // covered by half stops half the rays, under the right in the sun; with the cut-out
        // off, under all. The same with the card among the GPU's instances; and with its
        // texture not read yet, in the sun under all of it. Covered texels stop the sun's
        // rays wholly only where a blade lets none of the light through (rtFoliageTransmission
        // 0; 0.5 by default): set so for the check, and put back after it.
        const std::wstring ini = NextToExecutable(L"ReDefinitionProxy.ini");
        wchar_t foliageBefore[32] = {};
        const bool foliageSet = GetPrivateProfileStringW(L"Proxy", L"rtFoliageTransmission", L"", foliageBefore, 32, ini.c_str()) > 0;
        WritePrivateProfileStringW(L"Proxy", L"rtFoliageTransmission", L"0", ini.c_str());
        // The proxy looks at the file at most once a second, from its Present.
        Sleep(1100);
        swapChain->Present(0, 0);
        Mesh card;
        card.id = 40;
        AddQuad(card, { -0.5, 0, 0.5 }, { 0.5, 0, 0.5 }, { 0.5, 0, -0.5 }, { -0.5, 0, -0.5 }, { 0, 1, 0 });
        card.uvs = { 0.0f, 1.0f, 1.0f, 1.0f, 1.0f, 0.0f, 0.0f, 0.0f };
        if (FAILED(UploadMeshForRaster(device.Get(), card)) || SendMesh(proxy, card) != 1)
            return Fatal("the cut-out card", E_FAIL);
        std::vector<uint8_t> pixels(static_cast<size_t>(kRtMaterialTextureSize) * kRtMaterialTextureSize * 4, 128);
        for (uint32_t y = 0; y < kRtMaterialTextureSize; ++y)
            for (uint32_t x = 0; x < kRtMaterialTextureSize; ++x)
                pixels[(y * kRtMaterialTextureSize + x) * 4 + 3] =
                    x < kRtMaterialTextureSize / 3 ? 255 : x < kRtMaterialTextureSize * 2 / 3 ? 128 : 0;
        if (proxy.setMaterialTexture(7, pixels.data(), static_cast<int>(kRtMaterialTextureSize)) != 1)
            return Fatal("the cut-out texture", E_FAIL);
        const auto cardMaterial = [&](int id, float cutoff, int texture)
        {
            RtMaterialDesc desc = {};
            desc.size = sizeof(desc);
            desc.magic = kRtMaterialMagic;
            desc.material = id;
            desc.albedo[0] = desc.albedo[1] = desc.albedo[2] = 0.5f;
            desc.albedo[3] = cutoff;
            desc.specular[0] = desc.specular[1] = desc.specular[2] = 0.04f;
            desc.smoothness = 0.2f;
            desc.texture = texture;
            return proxy.setMaterial(&desc);
        };
        // 43: cut out, its texture not read yet.
        if (cardMaterial(41, 0.5f, 7) != 1 || cardMaterial(42, 0.0f, 7) != 1 || cardMaterial(43, 0.5f, -1) != 1)
            return Fatal("the cut-out materials", E_FAIL);
        std::map<int, Mesh> cardMeshes;
        cardMeshes[2] = meshes[2];
        cardMeshes[40] = card;
        Camera low;
        low.position = { 0, 1.5, -15 };
        low.target = { 0, 0, 0 };
        low.Update();
        const Vec3 above = { 0, 1, 0 };
        // The card's transform for the GPU's instances, in t7's layout.
        D3D11_TEXTURE2D_DESC placesDesc = {};
        placesDesc.Width = kRtInstancesPerRow * kRtInstanceTexels;
        placesDesc.Height = 1;
        placesDesc.MipLevels = 1;
        placesDesc.ArraySize = 1;
        placesDesc.Format = DXGI_FORMAT_R32G32B32A32_FLOAT;
        placesDesc.SampleDesc.Count = 1;
        placesDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        std::vector<float> placeTexels(static_cast<size_t>(kRtInstancesPerRow) * kRtInstanceTexels * 4, 0.0f);
        Instance cardPlace;
        cardPlace.mesh = 40;
        cardPlace.position = { 0, 3, 0 };
        cardPlace.scale[0] = cardPlace.scale[2] = 10.0;
        RelativeTransform(cardPlace, low.position, placeTexels.data());
        const D3D11_SUBRESOURCE_DATA placesData = { placeTexels.data(), placesDesc.Width * 16, 0 };
        ComPtr<ID3D11Texture2D> places;
        if (FAILED(device->CreateTexture2D(&placesDesc, &placesData, &places)))
            return Fatal("the card's place", E_FAIL);
        const auto halves = [&](int material, bool fromGpu, double* thirds)
        {
            std::vector<Instance> cardScene;
            Instance ground;
            ground.mesh = 2;
            ground.scale[0] = ground.scale[2] = 60.0;
            cardScene.push_back(ground);
            Instance over = cardPlace;
            over.material = material;
            std::vector<Instance> drawn = cardScene;
            drawn.push_back(over);
            if (!fromGpu)
                cardScene.push_back(over);
            SendInstances(proxy, cardScene, low.position);
            RtGpuInstanceGroup group = {};
            group.mesh = 40;
            group.material = material;
            group.count = 1;
            group.mask = 0xFF;
            proxy.setGpuGroups(&group, fromGpu ? 1 : 0, kRtAnyFrame);
            RenderGBuffer(context.Get(), g, low, cardMeshes, drawn);
            Lighting cut;
            cut.materialTextures = true;
            cut.gpuInstances = fromGpu ? places.Get() : nullptr;
            SendFrame(proxy, low, g, outputs, above, 0.0f, 15000 + material + (fromGpu ? 100 : 0), kRtSignalSunVisibility, cut);
            std::vector<uint16_t> vis;
            std::vector<float> depth;
            ReadTexture(device.Get(), context.Get(), outputs.visibility.Get(), 8, vis);
            ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
            double sums[3] = {}, counts[3] = {};
            for (uint32_t y = 0; y < kHeight; ++y)
                for (uint32_t x = 0; x < kWidth; ++x)
                {
                    const size_t i = static_cast<size_t>(y) * kWidth + x;
                    if (depth[i] <= 0.0f)
                        continue;
                    const double nx = ((x + 0.5) / kWidth) * 2.0 - 1.0;
                    const double ny = 1.0 - ((y + 0.5) / kHeight) * 2.0;
                    double q[4];
                    for (int r = 0; r < 4; ++r)
                        q[r] = low.clipToWorld.at(r, 0) * nx + low.clipToWorld.at(r, 1) * ny + low.clipToWorld.at(r, 2) * depth[i]
                             + low.clipToWorld.at(r, 3);
                    const Vec3 world = Vec3{ q[0] / q[3], q[1] / q[3], q[2] / q[3] } + low.position;
                    if (std::fabs(world.y) > 0.05 || std::fabs(world.z) > 4.0)
                        continue;
                    // The card's thirds, with a margin from their edges: x -5 to -1.7, to 1.7, to 5.
                    const int third = world.x > -4.5 && world.x < -2.5 ? 0 : world.x > -1.0 && world.x < 1.0 ? 1
                                    : world.x > 2.5 && world.x < 4.5 ? 2 : -1;
                    if (third < 0)
                        continue;
                    sums[third] += HalfToFloat(vis[i * 4]);
                    counts[third] += 1.0;
                }
            for (int t = 0; t < 3; ++t)
                thirds[t] = counts[t] > 0 ? sums[t] / counts[t] : -1.0;
        };
        double cut[3], whole[3], gpu[3], unread[3];
        halves(41, false, cut);
        halves(42, false, whole);
        halves(41, true, gpu);
        halves(43, true, unread);
        proxy.setGpuGroups(nullptr, 0, kRtAnyFrame);
        char detail[400];
        sprintf_s(detail, "the ground in the sun under the card's opaque, half and cut-out thirds %.3f, %.3f, %.3f; as a GPU"
                          " instance %.3f, %.3f, %.3f; with the cut-out off %.3f, %.3f, %.3f; its texture not read %.3f,"
                          " %.3f, %.3f",
                  cut[0], cut[1], cut[2], gpu[0], gpu[1], gpu[2], whole[0], whole[1], whole[2], unread[0], unread[1],
                  unread[2]);
        const auto right = [](const double* t)
        {
            return t[0] >= 0.0 && t[0] < 0.05 && t[1] > 0.4 && t[1] < 0.6 && t[2] > 0.95;
        };
        Check(right(cut) && right(gpu) && whole[1] >= 0.0 && whole[1] < 0.05 && whole[2] >= 0.0 && whole[2] < 0.05
                  && unread[0] > 0.95 && unread[1] > 0.95 && unread[2] > 0.95,
              "cut-out surfaces", detail);
        WritePrivateProfileStringW(L"Proxy", L"rtFoliageTransmission", foliageSet ? foliageBefore : nullptr, ini.c_str());
        Sleep(1100);
        swapChain->Present(0, 0);
        proxy.releaseMesh(40);
        SendInstances(proxy, scene, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
    }

    // ---------------------------------------------------------------- without a G-buffer
    {
        // The surface from the service's own rays, as for forward rendering: the same sun
        // visibility and diffuse light as from the G-buffer.
        SendInstances(proxy, scene, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        Lighting withBuffer;
        withBuffer.bounces = 1.0f;
        Lighting fromRays = withBuffer;
        fromRays.gbuffer = false;
        std::vector<double> sunA, sunB, diffuseA, diffuseB;
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.visibility.Get(), kRtSignalSunVisibility,
                        withBuffer, sun, 1, 13000, sunA)
            || !Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.visibility.Get(), kRtSignalSunVisibility,
                           fromRays, sun, 1, 13000, sunB)
            || !Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                           withBuffer, sun, 64, 13100, diffuseA)
            || !Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                           fromRays, sun, 64, 13100, diffuseB))
            return Fatal("reading the frames without a G-buffer back", E_FAIL);
        size_t same = 0, pixels = 0;
        double meanA = 0.0, meanB = 0.0;
        for (size_t i = 0; i < sunA.size() / 3; ++i)
        {
            ++pixels;
            same += (sunA[i * 3] > 0.5) == (sunB[i * 3] > 0.5) ? 1 : 0;
            meanA += diffuseA[i * 3 + 1];
            meanB += diffuseB[i * 3 + 1];
        }
        char detail[200];
        sprintf_s(detail, "the sun's visibility the same on %.2f %% of the pixels, diffuse light %.4f against %.4f",
                  100.0 * same / pixels, meanB / pixels, meanA / pixels);
        Check(same * 1000 >= pixels * 995 && std::fabs(meanB - meanA) < 0.02 * meanA, "the surface from the rays", detail);
    }

    // ---------------------------------------------------------------- forward light
    {
        // The light a forward frame gets, without a G-buffer: in u7 per channel the diffuse
        // light less the flat ambient over the light Unity shaded with (here the ambient
        // alone, the sun black), in w the largest channel of that; nothing where nothing
        // was drawn.
        // Unity's ambient light as a probe brighter above than below: the pass takes off
        // the quadratic in the normal's height through the three values.
        const float up = 0.3f, side = 0.2f, down = 0.05f;
        // The rays' surface takes the material's albedo: the scene's, with the G-buffer's.
        const float albedoOf[3] = { 0.6f, 0.6f, 0.6f };
        const float none[3] = { 0.0f, 0.0f, 0.0f };
        SendMaterial(proxy, 30, albedoOf, none, 0.2f);
        std::vector<Instance> grey = scene;
        for (Instance& instance : grey)
        {
            instance.material = 30;
            memcpy(instance.albedo, albedoOf, sizeof(albedoOf));
        }
        SendInstances(proxy, grey, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, grey);
        Lighting lighting;
        lighting.gbuffer = false;
        lighting.sunColor[0] = lighting.sunColor[1] = lighting.sunColor[2] = 0.0f;
        lighting.bounces = 0.0f;
        lighting.ambient[0] = up;
        lighting.ambient[1] = side;
        lighting.ambient[2] = down;
        // Through the denoiser, as the game runs it: the pass reads what the denoiser left.
        lighting.flags = kRtFrameDenoise | kRtFrameReset;
        // Linear, and as KSP shades, in gamma space: there the pass takes the linear
        // signals and the surface's albedo into gamma space, as Unity's lighting adds them.
        const auto toGamma = [](double c) { c = c > 0.0 ? c : 0.0; return std::max(1.055 * std::pow(c, 0.416666667) - 0.055, 0.0); };
        const auto toLinear = [](double c) { return c * (c * (c * 0.305306011 + 0.682171111) + 0.012522878); };
        for (int gamma = 0; gamma < 2; ++gamma)
        {
            lighting.gamma = gamma != 0;
            // The materials' colours linear, as the mod sends them in gamma space (RtScene).
            const float linearAlbedo[3] = { float(toLinear(albedoOf[0])), float(toLinear(albedoOf[1])), float(toLinear(albedoOf[2])) };
            SendMaterial(proxy, 30, gamma != 0 ? linearAlbedo : albedoOf, none, 0.2f);
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, 14000,
                      kRtSignalSunVisibility | kRtSignalDiffuse | kRtSignalAmbientOcclusion | kRtSignalForwardLight, lighting);
            std::vector<float> ratio;
            std::vector<uint16_t> diffuse;
            std::vector<uint32_t> normals;
            std::vector<float> depth;
            ReadTexture(device.Get(), context.Get(), outputs.primary.Get(), 16, ratio);
            ReadTexture(device.Get(), context.Get(), g.gbuffer2.Get(), 4, normals);
            ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, diffuse);
            ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
            size_t drawn = 0, right = 0, empty = 0, emptyRight = 0;
            for (size_t i = 0; i < depth.size(); ++i)
            {
                const double got = ratio[i * 4 + 1];
                const double gotW = ratio[i * 4 + 3];
                if (depth[i] <= 0.0f)
                {
                    ++empty;
                    emptyRight += got == 0.0 && gotW == 0.0 ? 1 : 0;
                    continue;
                }
                ++drawn;
                const double ny = ((normals[i] >> 10) & 0x3FF) / 1023.0 * 2 - 1;
                // As the proxy has it: the packet's values, already in Unity's colour space.
                const double ambient = side + 0.5 * (up - down) * ny + (0.5 * (up + down) - side) * ny * ny;
                const double light = HalfToFloat(diffuse[i * 4 + 1]);
                const double want = ((gamma != 0 ? toGamma(light) : light) - ambient) / std::max(ambient, 1e-3);
                right += std::fabs(got - want) < 0.01 + 0.03 * std::fabs(want) && std::fabs(gotW - ambient) < 0.01 + 0.02 * ambient
                             ? 1 : 0;
            }
            char detail[200];
            sprintf_s(detail, "%zu of %zu drawn pixels get the diffuse light less the ambient over the ambient, %zu of %zu empty ones nothing", right,
                      drawn, emptyRight, empty);
            Check(drawn > 100000 && right * 100 >= drawn * 99 && emptyRight == empty,
                  gamma != 0 ? "forward light in gamma space" : "forward light", detail);
        }

        // The reflections, alone in u4 without a G-buffer, of a rough surface whose specular
        // colour is 0.5 under a white sky: Unity's Fresnel term is then 0.5 at every angle, and its surface
        // reduction, in gamma space, 1 - 0.28 roughness^3 = 0.72 (BRDF1_Unity_PBS), and
        // 1 / (roughness^4 + 1) = 0.5 linear.
        const float shiny[3] = { float(toLinear(0.5)), float(toLinear(0.5)), float(toLinear(0.5)) };
        lighting.ambient[0] = lighting.ambient[1] = lighting.ambient[2] = 0.0f;
        for (int gamma = 0; gamma < 2; ++gamma)
        {
            lighting.gamma = gamma != 0;
            SendMaterial(proxy, 30, gamma != 0 ? shiny : albedoOf, none, 0.0f, 0.0f, 0.5f);
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, 14100,
                      kRtSignalDiffuse | kRtSignalSpecular | kRtSignalForwardLight, lighting);
            std::vector<uint16_t> forward, diffuse, specular;
            std::vector<float> depth;
            ReadTexture(device.Get(), context.Get(), outputs.forward.Get(), 8, forward);
            ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, diffuse);
            ReadTexture(device.Get(), context.Get(), outputs.specular.Get(), 8, specular);
            ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
            const double reduction = gamma != 0 ? 0.72 : 0.5;
            size_t drawn = 0, right = 0;
            for (size_t i = 0; i < depth.size(); ++i)
            {
                if (depth[i] <= 0.0f)
                    continue;
                ++drawn;
                const double reflected = HalfToFloat(specular[i * 4 + 1]);
                const double want = gamma != 0 ? toGamma(reflected) * 0.5 * reduction : reflected * 0.5 * reduction;
                const double got = HalfToFloat(forward[i * 4 + 1]);
                right += std::fabs(got - want) < 0.01 + 0.02 * std::fabs(want) ? 1 : 0;
            }
            char detail[200];
            sprintf_s(detail, "%zu of %zu drawn pixels get the reflections times 0.5 and %.2f", right,
                      drawn, reduction);
            Check(drawn > 100000 && right * 100 >= drawn * 99,
                  gamma != 0 ? "forward light's reflections in gamma space" : "forward light's reflections", detail);
        }
        SendMaterial(proxy, 30, albedoOf, none, 0.2f);
    }

    // ---------------------------------------------------------------- moonlight
    {
        // A body the sun lights as the only light, from the west 45 degrees up, no sun and
        // no sky: the open ground gets its colour times N.L, the ground in the box's
        // shadow nothing. Then a body's light straight from below a box high over a
        // planet: the planet shades it.
        SendInstances(proxy, scene, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        Lighting lighting;
        lighting.sunColor[0] = lighting.sunColor[1] = lighting.sunColor[2] = 0.0f;
        lighting.sky[0] = lighting.sky[1] = lighting.sky[2] = 0.0f;
        lighting.bounces = 0.0f;
        const float moon = 0.1f;
        const float diagonal = float(std::sqrt(0.5));
        const float west[8] = { -diagonal, diagonal, 0.0f, 0.01f, moon, moon, moon, 1.0e8f };
        memcpy(lighting.lights, west, sizeof(west));
        lighting.lightCount = 1;
        std::vector<double> mean;
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                        lighting, sun, 128, 15000, mean))
            return Fatal("reading the moonlight back", E_FAIL);
        int openX = 0, openY = 0, shadeX = 0, shadeY = 0;
        const bool seen = Project(camera, Vec3{ 15, 0, -15 } - camera.position, openX, openY)
                          && Project(camera, Vec3{ 9, 0, 0 } - camera.position, shadeX, shadeY);
        const double open = seen ? MeanAround(mean, openX, openY, 0) : 0.0;
        const double shaded = seen ? MeanAround(mean, shadeX, shadeY, 0) : 1.0;
        const double wanted = moon * diagonal;

        // A planet under the high box, a body's light straight from below.
        std::vector<Instance> high = { box };
        high[0].position = { 0, 150, 0 };
        Camera under = camera;
        under.position = { 30, 120, 60 };
        under.target = { 0, 150, 0 };
        under.Update();
        SendInstances(proxy, high, under.position);
        RenderGBuffer(context.Get(), g, under, meshes, high);
        Lighting below = lighting;
        below.body[0] = 0.0;
        below.body[1] = -6.0e5;
        below.body[2] = 0.0;
        below.body[3] = 6.0e5;
        const auto bottomLight = [&](float reach, double& result) -> bool
        {
            const float light[8] = { 0.0f, -1.0f, 0.0f, 1.0f, moon, moon, moon, reach };
            memcpy(below.lights, light, sizeof(light));
            std::vector<double> m;
            if (!Accumulate(proxy, device.Get(), context.Get(), under, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                            below, sun, 64, 15200, m))
                return false;
            std::vector<uint32_t> normals;
            std::vector<float> depth;
            ReadTexture(device.Get(), context.Get(), g.gbuffer2.Get(), 4, normals);
            ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
            double total = 0.0;
            size_t count = 0;
            for (size_t i = 0; i < normals.size(); ++i)
            {
                if (depth[i] <= 0.0f || ((normals[i] >> 10) & 0x3FF) / 1023.0 * 2 - 1 > -0.9)
                    continue;
                total += m[i * 3 + 1];
                ++count;
            }
            result = count > 1000 ? total / count : -1.0;
            return true;
        };
        double otherBody = 0.0;
        if (!bottomLight(150.0f, otherBody))
            return Fatal("reading the light from below back", E_FAIL);
        char detail[240];
        sprintf_s(detail, "open ground %.4f (N.L times the light %.4f), in the box's shadow %.4f; the box's underside %.4f by "
                          "a body's light below the horizon",
                  open, wanted, shaded, otherBody);
        Check(seen && std::fabs(open - wanted) < 0.05 * wanted && shaded < 0.05 * wanted && otherBody >= 0.0
                  && otherBody < 0.01 * moon,
              "moonlight", detail);
    }

    // ---------------------------------------------------------------- a moon in the mirror
    {
        // A mirror-smooth plane, no sky, a full moon of 0.2 radians 30 degrees up behind
        // the box, the sun behind the camera: the plane reflects the moon's disc at its
        // surface's brightness, and nothing else.
        std::vector<Instance> mirrored = scene;
        mirrored[0].smoothness = 1.0f;
        SendInstances(proxy, mirrored, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, mirrored);
        Lighting lighting;
        lighting.sunColor[0] = lighting.sunColor[1] = lighting.sunColor[2] = 0.0f;
        lighting.sky[0] = lighting.sky[1] = lighting.sky[2] = 0.0f;
        lighting.bounces = 0.0f;
        const Vec3 view = Normalize(Vec3{ 0, 5, 0 } - camera.position);
        const Vec3 flat = Normalize(Vec3{ view.x, 0, view.z });
        const Vec3 moon = Normalize(Vec3{ flat.x, std::tan(30.0 * 3.14159265 / 180.0), flat.z });
        const float disc[8] = { float(moon.x), float(moon.y), float(moon.z), 0.2f, 0.0f, 0.0f, 0.0f, 1.0e8f };
        memcpy(lighting.lights, disc, sizeof(disc));
        lighting.lightCount = 1;
        lighting.lightSurface[0] = lighting.lightSurface[1] = lighting.lightSurface[2] = 0.5f;
        std::vector<double> mean;
        // The sun opposite the moon: its side facing the scene lit, full.
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.specular.Get(), kRtSignalSpecular,
                        lighting, Vec3{ -moon.x, -moon.y, -moon.z }, 4, 18000, mean))
            return Fatal("reading the moon's reflection back", E_FAIL);
        size_t lit = 0, over = 0;
        double brightest = 0.0;
        for (size_t i = 0; i < mean.size() / 3; ++i)
        {
            const double value = mean[i * 3 + 1];
            brightest = std::max(brightest, value);
            lit += value > 0.25 ? 1 : 0;
            over += value > 0.55 ? 1 : 0;
        }
        char detail[200];
        sprintf_s(detail, "%zu pixels show the moon's disc, the brightest at %.3f (its surface 0.5 lit head-on), %zu above it",
                  lit, brightest, over);
        Check(lit > 200 && brightest > 0.45 && over == 0, "a moon in the mirror", detail);

        // The main body between the mirror and the moon, of no albedo: its sphere hides
        // the disc.
        const Vec3 between = camera.position + moon * 3.0e5;
        lighting.body[0] = between.x;
        lighting.body[1] = between.y;
        lighting.body[2] = between.z;
        lighting.body[3] = 1.5e5;
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.specular.Get(), kRtSignalSpecular,
                        lighting, Vec3{ -moon.x, -moon.y, -moon.z }, 4, 18010, mean))
            return Fatal("reading the moon's reflection back", E_FAIL);
        size_t hidden = 0;
        for (size_t i = 0; i < mean.size() / 3; ++i)
            hidden += mean[i * 3 + 1] > 0.25 ? 1 : 0;
        sprintf_s(detail, "%zu pixels show the moon's disc through the main body", hidden);
        Check(hidden == 0, "a moon behind the main body", detail);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- the moon on water
    {
        // A flat sea, the Mun's disc of 0.95 degrees 15 degrees up and full, nothing
        // else; only the ground counted, its normal up (the boxes' walls mirror too).
        // Calm (smoothness 1): the disc mirrored as it is, round, no brighter than its
        // average (2/3 of its lit centre) but by the jitter at its edge. Rough (0.75, a
        // sea's slopes): its light spread over a patch longer towards the horizon than
        // across, dimmer at its brightest, less of it at the grazing angle where the
        // waves shadow each other (Smith's term). Each the same
        // in every frame but for a few pixels at its edge, which the jitter moves, and no
        // ray counted as meeting something.
        Lighting lighting;
        lighting.flags = kRtFrameWater;
        lighting.sunColor[0] = lighting.sunColor[1] = lighting.sunColor[2] = 0.0f;
        lighting.sky[0] = lighting.sky[1] = lighting.sky[2] = 0.0f;
        lighting.bounces = 0.0f;
        // Ahead, turned 30 degrees aside, where its reflection meets open ground past the box.
        const Vec3 view = Normalize(Vec3{ 0, 5, 0 } - camera.position);
        const double turn = 30.0 * 3.14159265 / 180.0;
        const Vec3 flat = Normalize(Vec3{ view.x * std::cos(turn) - view.z * std::sin(turn), 0,
                                          view.x * std::sin(turn) + view.z * std::cos(turn) });
        const Vec3 moon = Normalize(Vec3{ flat.x, std::tan(15.0 * 3.14159265 / 180.0), flat.z });
        const float radius = 0.0166f;
        const float disc[8] = { float(moon.x), float(moon.y), float(moon.z), radius, 0.0f, 0.0f, 0.0f, 1.0e8f };
        memcpy(lighting.lights, disc, sizeof(disc));
        lighting.lightCount = 1;
        lighting.lightSurface[0] = lighting.lightSurface[1] = lighting.lightSurface[2] = 0.5f;
        const Vec3 behind = { -moon.x, -moon.y, -moon.z };
        struct Patch
        {
            size_t lit = 0, changed = 0, hits = 0, wide = 0, high = 0;
            double brightest = 0.0, sum = 0.0;
        };
        const auto mirrored = [&](float smoothness, Patch& patch)
        {
            std::vector<Instance> water = scene;
            water[0].smoothness = smoothness;
            SendInstances(proxy, water, camera.position);
            RenderGBuffer(context.Get(), g, camera, meshes, water);
            std::vector<uint16_t> first, second;
            std::vector<uint32_t> normals;
            SendFrame(proxy, camera, g, outputs, behind, 0.0f, 19000, kRtSignalSpecular, lighting);
            ReadTexture(device.Get(), context.Get(), outputs.specular.Get(), 8, first);
            SendFrame(proxy, camera, g, outputs, behind, 0.0f, 19001, kRtSignalSpecular, lighting);
            ReadTexture(device.Get(), context.Get(), outputs.specular.Get(), 8, second);
            ReadTexture(device.Get(), context.Get(), g.gbuffer2.Get(), 4, normals);
            const auto ground = [&](size_t i) { return ((normals[i] >> 10) & 0x3FF) / 1023.0 * 2 - 1 > 0.9; };
            for (size_t i = 0; i < first.size() / 4; ++i)
                if (ground(i))
                    patch.brightest = std::max(patch.brightest, double(HalfToFloat(first[i * 4 + 1])));
            size_t left = kWidth, right = 0, top = kHeight, bottom = 0;
            for (size_t i = 0; i < first.size() / 4; ++i)
            {
                if (!ground(i))
                    continue;
                const double value = HalfToFloat(first[i * 4 + 1]);
                patch.sum += value;
                patch.hits += HalfToFloat(first[i * 4 + 3]) < 60000.0 && value > 0.0 ? 1 : 0;
                // The patch: a tenth of its brightest and more.
                if (value <= 0.1 * patch.brightest)
                    continue;
                ++patch.lit;
                patch.changed += std::fabs(value - HalfToFloat(second[i * 4 + 1])) > 1e-3 ? 1 : 0;
                left = std::min(left, i % kWidth);
                right = std::max(right, i % kWidth);
                top = std::min(top, i / kWidth);
                bottom = std::max(bottom, i / kWidth);
            }
            patch.wide = right >= left ? right - left + 1 : 0;
            patch.high = bottom >= top ? bottom - top + 1 : 0;
            std::vector<float> rgb(static_cast<size_t>(kWidth) * kHeight * 3);
            for (size_t i = 0; i < rgb.size() / 3; ++i)
                rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] =
                    static_cast<float>(std::min(HalfToFloat(first[i * 4 + 1]) / std::max(patch.brightest, 1e-6), 1.0));
            WriteBmp(NextToExecutable(smoothness >= 1.0f ? L"rt_moon_on_water_calm.bmp" : L"rt_moon_on_water_rough.bmp"),
                     kWidth, kHeight, rgb);
        };
        Patch calm, rough;
        mirrored(1.0f, calm);
        mirrored(0.75f, rough);
        const double light = calm.sum > 0.0 ? rough.sum / calm.sum : 0.0;
        const size_t calmLonger = std::max(calm.wide, calm.high), calmShorter = std::min(calm.wide, calm.high);
        char detail[320];
        sprintf_s(detail, "calm: %zu pixels, %zu wide and %zu high, the brightest at %.3f (the disc's average 0.333); rough: %zu "
                          "pixels, %zu wide and %zu high, the brightest at %.3f, its light %.2f of the calm one's; %zu changed "
                          "between frames, %zu counted as hits",
                  calm.lit, calm.wide, calm.high, calm.brightest, rough.lit, rough.wide, rough.high, rough.brightest, light,
                  calm.changed + rough.changed, calm.hits + rough.hits);
        Check(calm.lit > 30 && calmShorter * 10 >= calmLonger * 6 && calm.brightest > 0.25 && calm.brightest < 0.34
                  && rough.lit > 2 * calm.lit && rough.brightest < calm.brightest && rough.high > rough.wide && light > 0.4
                  && light < 2.0 && (calm.changed + rough.changed) * 10 <= calm.lit + rough.lit && calm.hits + rough.hits == 0,
              "the moon on water", detail);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- the sun on water
    {
        // Rough water, the sun 10 degrees up, no sky; only the ground counted: where
        // the water mirrors its disc, glitter brighter than white, a path half as long
        // again as it is wide; with the sun's mirroring off, none (the sunlit box
        // mirrored stays below white).
        std::vector<Instance> water = scene;
        water[0].smoothness = 0.75f;
        SendInstances(proxy, water, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, water);
        std::vector<uint32_t> normals;
        ReadTexture(device.Get(), context.Get(), g.gbuffer2.Get(), 4, normals);
        Lighting lighting;
        lighting.flags = kRtFrameWater;
        lighting.sky[0] = lighting.sky[1] = lighting.sky[2] = 0.0f;
        lighting.bounces = 0.0f;
        lighting.sunMirror = 1.0f;
        // Ahead, turned 30 degrees aside, where its reflection meets open ground past the box.
        const Vec3 view = Normalize(Vec3{ 0, 5, 0 } - camera.position);
        const double turn = 30.0 * 3.14159265 / 180.0;
        const Vec3 flat = Normalize(Vec3{ view.x * std::cos(turn) - view.z * std::sin(turn), 0,
                                          view.x * std::sin(turn) + view.z * std::cos(turn) });
        const Vec3 towardsSun = Normalize(Vec3{ flat.x, std::tan(10.0 * 3.14159265 / 180.0), flat.z });
        size_t left = kWidth, right = 0, top = kHeight, bottom = 0;
        const auto glitter = [&](const Lighting& l, size_t& count, double& brightest)
        {
            std::vector<uint16_t> out;
            SendFrame(proxy, camera, g, outputs, towardsSun, 0.0047f, 19200, kRtSignalSpecular, l);
            ReadTexture(device.Get(), context.Get(), outputs.specular.Get(), 8, out);
            count = 0;
            brightest = 0.0;
            for (size_t i = 0; i < out.size() / 4; ++i)
            {
                if (((normals[i] >> 10) & 0x3FF) / 1023.0 * 2 - 1 <= 0.9)
                    continue;
                const double value = HalfToFloat(out[i * 4 + 1]);
                brightest = std::max(brightest, value);
                if (value <= 1.0)
                    continue;
                ++count;
                left = std::min(left, i % kWidth);
                right = std::max(right, i % kWidth);
                top = std::min(top, i / kWidth);
                bottom = std::max(bottom, i / kWidth);
            }
        };
        size_t lit = 0, litOff = 0;
        double brightest = 0.0, brightestOff = 0.0;
        glitter(lighting, lit, brightest);
        const size_t wide = right >= left ? right - left + 1 : 0, high = bottom >= top ? bottom - top + 1 : 0;
        Lighting off = lighting;
        off.sunMirror = 0.0f;
        glitter(off, litOff, brightestOff);
        char detail[200];
        sprintf_s(detail, "%zu pixels mirror the sun brighter than white, the brightest at %.0f, %zu wide and %zu high; with it "
                          "off %zu, at most %.3f", lit, brightest, wide, high, litOff, brightestOff);
        Check(lit > 50 && brightest > 10.0 && 2 * high > 3 * wide && litOff == 0 && brightestOff < 1.0, "the sun on water", detail);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- water over the horizon
    {
        // The box's walls as water, a white sky, a dark ground, nothing lit: a wall's
        // reflection that points down is turned back over the horizon and meets nothing --
        // the ocean's own sky shows there -- not the ground.
        std::vector<Instance> walls = scene;
        for (Instance& instance : walls)
            instance.smoothness = 1.0f;
        SendInstances(proxy, walls, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, walls);
        Lighting lighting;
        lighting.flags = kRtFrameWater;
        lighting.sunColor[0] = lighting.sunColor[1] = lighting.sunColor[2] = 0.0f;
        lighting.bounces = 0.0f;
        std::vector<uint16_t> out;
        std::vector<uint32_t> normals;
        std::vector<float> depth;
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, 19100, kRtSignalSpecular, lighting);
        ReadTexture(device.Get(), context.Get(), outputs.specular.Get(), 8, out);
        ReadTexture(device.Get(), context.Get(), g.gbuffer2.Get(), 4, normals);
        ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
        size_t wall = 0, sky = 0;
        for (size_t i = 0; i < depth.size(); ++i)
        {
            const double ny = ((normals[i] >> 10) & 0x3FF) / 1023.0 * 2 - 1;
            if (depth[i] <= 0.0f || std::fabs(ny) > 0.1)
                continue;
            ++wall;
            sky += HalfToFloat(out[i * 4 + 3]) >= 60000.0 ? 1 : 0;
        }
        char detail[200];
        sprintf_s(detail, "%zu of %zu wall pixels reflect the sky, their rays meeting nothing", sky, wall);
        Check(wall > 1000 && sky * 100 >= wall * 90, "water over the horizon", detail);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- KSP's own sea
    {
        // The ground as KSP's sea at the level of a planet's sphere, drawn into the G-buffer
        // but not in the rays' scene, and a deck at the same level that the rays' scene
        // holds. With the waves' normals shown (kRtFrameWaterNormals) the sea gets
        // ReDefinition's waves, unit normals near up that vary, and neither the deck nor
        // the boxes any.
        Instance deck = box;
        deck.position = { 15, -0.5, -15 };
        deck.scale[0] = 8.0;
        deck.scale[1] = 1.0;
        deck.scale[2] = 8.0;
        std::vector<Instance> drawn = scene;
        drawn.push_back(deck);
        const std::vector<Instance> traced(drawn.begin() + 1, drawn.end());
        Lighting lighting;
        lighting.flags = kRtFrameWater | kRtFrameWaterNormals;
        lighting.stockSea = true;
        lighting.bounces = 0.0f;
        lighting.body[1] = -6.0e5;
        lighting.body[3] = 6.0e5;
        lighting.time = 12.5f;
        // The sea's frame: at the world's origin, east along x, north along z.
        lighting.sea[4] = 1.0;
        lighting.sea[10] = 1.0;
        struct SeaView
        {
            std::vector<uint16_t> out;
            std::vector<uint32_t> normals;
            std::vector<float> depth;
        };
        const auto look = [&](const Camera& view, const std::vector<Instance>& rays, const std::vector<Instance>& gbuffer,
                              const Lighting& l, SeaView& result)
        {
            SendInstances(proxy, rays, view.position);
            RenderGBuffer(context.Get(), g, view, meshes, gbuffer);
            SendFrame(proxy, view, g, outputs, sun, 0.0f, 19400, kRtSignalSpecular, l);
            ReadTexture(device.Get(), context.Get(), outputs.specular.Get(), 8, result.out);
            ReadTexture(device.Get(), context.Get(), g.gbuffer2.Get(), 4, result.normals);
            ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, result.depth);
        };
        // The wave normal a pixel shows, where its w says it shows one: 1 on KSP's sea, on
        // Scatterer's ocean kWaterSky (65000, RtCommon.hlsli) as a half float.
        const auto waveAt = [](const SeaView& v, size_t i, float w, Vec3& n)
        {
            const float got = HalfToFloat(v.out[i * 4 + 3]);
            if (w > 60000.0f ? got < 60000.0f : got != w)
                return false;
            n = { HalfToFloat(v.out[i * 4]) * 2.0 - 1.0, HalfToFloat(v.out[i * 4 + 1]) * 2.0 - 1.0,
                  HalfToFloat(v.out[i * 4 + 2]) * 2.0 - 1.0 };
            return true;
        };
        SeaView here;
        look(camera, traced, drawn, lighting, here);
        size_t sea = 0, water = 0, waves = 0, deckPixels = 0, deckWater = 0, raised = 0, raisedWater = 0;
        double sumX = 0.0, sumX2 = 0.0;
        for (size_t y = 0; y < kHeight; ++y)
            for (size_t x = 0; x < kWidth; ++x)
            {
                const size_t i = y * kWidth + x;
                if (here.depth[i] <= 0.0f)
                    continue;
                const Vec3 p = PositionAt(camera, here.depth, x, y) + camera.position;
                Vec3 n;
                const bool wet = waveAt(here, i, 1.0f, n);
                const bool nearDeck = std::fabs(p.x - 15.0) < 4.5 && std::fabs(p.z + 15.0) < 4.5;
                if (std::fabs(p.y) > 0.5)
                {
                    ++raised;
                    raisedWater += wet ? 1 : 0;
                }
                else if (std::fabs(p.x - 15.0) < 3.5 && std::fabs(p.z + 15.0) < 3.5 && std::fabs(p.y) < 0.05)
                {
                    ++deckPixels;
                    deckWater += wet ? 1 : 0;
                }
                else if (!nearDeck && std::fabs(p.y) < 0.05 && NormalOf(here.normals[i]).y > 0.9)
                {
                    ++sea;
                    if (!wet)
                        continue;
                    ++water;
                    if (std::fabs(Length(n) - 1.0) < 0.01 && n.y > 0.9)
                        ++waves;
                    sumX += n.x;
                    sumX2 += n.x * n.x;
                }
            }
        const double spread = water > 0 ? std::sqrt(std::max(sumX2 / water - (sumX / water) * (sumX / water), 0.0)) : 0.0;
        char detail[300];
        sprintf_s(detail, "%zu of %zu sea pixels water, %zu of them unit normals near up, spread %.4f across; %zu of %zu deck "
                          "pixels and %zu of %zu raised ones water",
                  water, sea, waves, spread, deckWater, deckPixels, raisedWater, raised);
        Check(sea > 100000 && water * 100 >= sea * 99 && waves == water && spread > 0.005 && deckPixels > 1000 && deckWater == 0
                  && raised > 10000 && raisedWater == 0,
              "KSP's own sea", detail);

        // The same water from a camera moved sideways by 16 pixels' width of ground at one
        // row: each of its pixels there shows the point the first camera showed 16 pixels to
        // its right, and the waves on it are the same, as the sea's frame holds them to the
        // water.
        const size_t row = kHeight * 3 / 4, from = kWidth / 8;
        const Vec3 step = PositionAt(camera, here.depth, from + 1, row) - PositionAt(camera, here.depth, from, row);
        Camera moved = camera;
        moved.position = camera.position + step * 16.0;
        moved.target = camera.target + step * 16.0;
        moved.Update();
        SeaView there;
        look(moved, traced, drawn, lighting, there);
        double aligned = 0.0, unaligned = 0.0;
        size_t compared = 0;
        for (size_t x = from + 16; x < kWidth - 16; ++x)
        {
            Vec3 a, b, c;
            if (!waveAt(here, row * kWidth + x, 1.0f, a) || !waveAt(there, row * kWidth + x - 16, 1.0f, b)
                || !waveAt(there, row * kWidth + x, 1.0f, c))
                continue;
            ++compared;
            aligned += Length(a - b);
            unaligned += Length(a - c);
        }
        aligned /= std::max<size_t>(compared, 1);
        unaligned /= std::max<size_t>(compared, 1);
        sprintf_s(detail, "%zu pixels along a row: the same point's normal %.5f apart, the same pixel's %.5f", compared, aligned,
                  unaligned);
        Check(compared > 500 && unaligned > 0.002 && aligned < 0.25 * unaligned, "the waves stay on the water", detail);

        // Scatterer's ocean: the ground as water in the rays' scene, the waves' normals shown:
        // every ground pixel shows ReDefinition's ripples, whatever its reflected ray meets.
        std::vector<Instance> ocean = scene;
        ocean[0].smoothness = 0.75f;
        Lighting scatterer = lighting;
        scatterer.stockSea = false;
        SeaView open;
        look(camera, ocean, ocean, scatterer, open);
        size_t ground = 0, rippled = 0;
        for (size_t i = 0; i < open.depth.size(); ++i)
        {
            if (open.depth[i] <= 0.0f || NormalOf(open.normals[i]).y < 0.99)
                continue;
            ++ground;
            Vec3 n;
            rippled += waveAt(open, i, 65000.0f, n) && std::fabs(Length(n) - 1.0) < 0.01 && n.y > 0.5 ? 1 : 0;
        }
        sprintf_s(detail, "%zu of %zu ground pixels show the ripples' normals", rippled, ground);
        Check(ground > 100000 && rippled == ground, "the waves on Scatterer's ocean", detail);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- the ground below
    {
        // A box high over a planet, a white sky: the rays from its underside meet the
        // planet's sphere, which sends its albedo times the sun on it -- with the sun
        // overhead the albedo, with the sun under the horizon nothing -- not the sky.
        std::vector<Instance> high = { box };
        high[0].position = { 0, 150, 0 };
        Camera under = camera;
        under.position = { 30, 120, 60 };
        under.target = { 0, 150, 0 };
        under.Update();
        SendInstances(proxy, high, under.position);
        RenderGBuffer(context.Get(), g, under, meshes, high);
        Lighting lighting;
        lighting.bounces = 0.0f;
        lighting.body[1] = -6.0e5;
        lighting.body[3] = 6.0e5;
        lighting.planet[0] = 0.3f;
        lighting.planet[1] = 0.2f;
        lighting.planet[2] = 0.1f;
        const auto underside = [&](Vec3 towardsSun, double& result) -> bool
        {
            std::vector<double> m;
            if (!Accumulate(proxy, device.Get(), context.Get(), under, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                            lighting, towardsSun, 64, 17000, m))
                return false;
            std::vector<uint32_t> normals;
            std::vector<float> depth;
            ReadTexture(device.Get(), context.Get(), g.gbuffer2.Get(), 4, normals);
            ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
            double total = 0.0;
            size_t count = 0;
            for (size_t i = 0; i < normals.size(); ++i)
            {
                if (depth[i] <= 0.0f || ((normals[i] >> 10) & 0x3FF) / 1023.0 * 2 - 1 > -0.9)
                    continue;
                total += m[i * 3];
                ++count;
            }
            result = count > 1000 ? total / count : -1.0;
            return true;
        };
        double day = 0.0, night = 0.0;
        if (!underside(Vec3{ 0, 1, 0 }, day) || !underside(Vec3{ 0, -1, 0 }, night))
            return Fatal("reading the ground's light back", E_FAIL);
        char detail[200];
        sprintf_s(detail, "the box's underside %.3f in red with the sun overhead (the planet's albedo 0.3), %.3f with the sun "
                          "under the horizon, under a white sky", day, night);
        Check(std::fabs(day - 0.3) < 0.03 && night >= 0.0 && night < 0.01, "the ground below", detail);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- vertex colours
    {
        // The ground as Parallax's terrain or KSP's own: a colour in Unity's colour space
        // plus the vertex colour times a factor, taken to linear as a whole (gamma space).
        // A white box high over it, the sun overhead, a black sky: its underside sees the
        // sunlit ground alone, so its light has the ground's colour.
        Mesh coloured = MakePlane(50, 32);
        const uint32_t vertex = 0x00996633u;   // R 0x33, G 0x66, B 0x99
        coloured.colors.assign(coloured.positions.size() / 3, vertex);
        const float base[3] = { 0.1f, 0.1f, 0.1f };
        const float none[3] = { 0.0f, 0.0f, 0.0f };
        const float factor = 0.5f;
        const bool sent = SendMesh(proxy, coloured) == 1 && SendMaterial(proxy, 51, base, none, 0.0f, factor) == 1;
        std::vector<Instance> ground = { scene[0], box };
        ground[0].mesh = 50;
        ground[0].material = 51;
        ground[1].position = { 0, 20, 0 };
        ground[1].albedo[0] = ground[1].albedo[1] = ground[1].albedo[2] = 1.0f;
        Camera under = camera;
        under.position = { 20, 8, 20 };
        under.target = { 0, 20, 0 };
        under.Update();
        SendInstances(proxy, ground, under.position);
        // A map of its own: RenderGBuffer looks each instance's mesh up by operator[].
        std::map<int, Mesh> groundMeshes = meshes;
        groundMeshes[50] = coloured;
        RenderGBuffer(context.Get(), g, under, groundMeshes, ground);
        Lighting lighting;
        lighting.gamma = true;
        lighting.bounces = 0.0f;
        lighting.sky[0] = lighting.sky[1] = lighting.sky[2] = 0.0f;
        std::vector<double> m;
        if (!Accumulate(proxy, device.Get(), context.Get(), under, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                        lighting, Vec3{ 0, 1, 0 }, 64, 16000, m))
            return Fatal("reading the underside's light back", E_FAIL);
        std::vector<uint32_t> normals;
        std::vector<float> depth;
        ReadTexture(device.Get(), context.Get(), g.gbuffer2.Get(), 4, normals);
        ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
        double got[3] = {};
        size_t count = 0;
        for (size_t i = 0; i < normals.size(); ++i)
        {
            if (depth[i] <= 0.0f || ((normals[i] >> 10) & 0x3FF) / 1023.0 * 2 - 1 > -0.9)
                continue;
            for (int c = 0; c < 3; ++c)
                got[c] += m[i * 3 + c];
            ++count;
        }
        const auto toLinear = [](double c) { return c * (c * (c * 0.305306011 + 0.682171111) + 0.012522878); };
        double want[3];
        for (int c = 0; c < 3; ++c)
            want[c] = toLinear(base[c] + factor * double((vertex >> (8 * c)) & 0xFFu) / 255.0);
        // Its colour, the light's channels to its green as the albedo's.
        bool right = sent && count > 1000 && got[1] > 0.0;
        for (int c = 0; c < 3 && right; c += 2)
            right = std::fabs(got[c] / got[1] - want[c] / want[1]) < 0.05 * (want[c] / want[1]);
        char detail[240];
        sprintf_s(detail, "the box's underside lit by the ground in the ratio %.3f : 1 : %.3f, the ground's linear albedo "
                          "%.3f : 1 : %.3f (%zu pixels)", got[1] > 0 ? got[0] / got[1] : 0.0, got[1] > 0 ? got[2] / got[1] : 0.0,
                  want[0] / want[1], want[2] / want[1], count);
        Check(right, "vertex colours", detail);
        SendInstances(proxy, scene, camera.position);
        proxy.releaseMesh(50);
    }

    // ---------------------------------------------------------------- a further trace
    {
        // The reflections of a rough ground traced twice in one frame, the second as a
        // further trace (kRtFrameExtra): each its own random numbers, so the two differ
        // where the next frame's differ.
        std::vector<Instance> rough = scene;
        rough[0].smoothness = 0.5f;
        SendInstances(proxy, rough, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, rough);
        Lighting lighting;
        lighting.bounces = 0.0f;
        std::vector<uint16_t> own, next, further;
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, 19300, kRtSignalSpecular, lighting);
        ReadTexture(device.Get(), context.Get(), outputs.specular.Get(), 8, own);
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, 19301, kRtSignalSpecular, lighting);
        ReadTexture(device.Get(), context.Get(), outputs.specular.Get(), 8, next);
        lighting.flags = kRtFrameExtra;
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, 19300, kRtSignalSpecular, lighting);
        ReadTexture(device.Get(), context.Get(), outputs.specular.Get(), 8, further);
        const auto differ = [&](const std::vector<uint16_t>& a, const std::vector<uint16_t>& b)
        {
            size_t count = 0;
            for (size_t i = 0; i < a.size() / 4; ++i)
                count += a[i * 4] != b[i * 4] || a[i * 4 + 1] != b[i * 4 + 1] || a[i * 4 + 3] != b[i * 4 + 3] ? 1 : 0;
            return count;
        };
        const size_t nextDiffers = differ(own, next), furtherDiffers = differ(own, further);
        char detail[200];
        sprintf_s(detail, "%zu pixels differ from the frame's trace in its further one, %zu in the next frame's", furtherDiffers,
                  nextDiffers);
        Check(nextDiffers > 10000 && furtherDiffers * 10 > nextDiffers * 8, "a further trace's own random numbers", detail);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- a further trace's scene
    Outputs furtherOutputs;
    hr = CreateOutputs(device.Get(), furtherOutputs);
    if (FAILED(hr))
        return Fatal("the further trace's output textures", hr);
    {
        // The further trace (the ocean's) takes the scene of the frame before it, from that
        // frame's slot. The next frames are recorded at once, as the game records them,
        // without waiting for the GPU; the second takes that slot again and writes its
        // materials with the tower's glow put out. The further trace's reflections are
        // the ones it gives where the GPU has finished it before the next frames.
        const float black[3] = { 0.0f, 0.0f, 0.0f };
        const float glow[3] = { 4.0f, 2.0f, 1.0f };
        std::vector<Instance> mirrored = scene;
        mirrored[0].smoothness = 1.0f;
        mirrored[2].material = 22;
        mirrored[2].position = { -22, 15, -12 };
        SendInstances(proxy, mirrored, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, mirrored);
        Lighting lighting;
        lighting.sunColor[0] = lighting.sunColor[1] = lighting.sunColor[2] = 0.0f;
        lighting.sky[0] = lighting.sky[1] = lighting.sky[2] = 0.2f;
        lighting.bounces = 0.0f;
        Lighting extra = lighting;
        extra.flags = kRtFrameExtra;
        const auto run = [&](bool finished, std::vector<uint16_t>& out)
        {
            SendMaterial(proxy, 22, black, glow, 0.2f);
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, 19400, kRtSignalSpecular, lighting);
            SendFrame(proxy, camera, g, furtherOutputs, sun, 0.0f, 19400, kRtSignalSpecular, extra);
            if (finished)
                ReadTexture(device.Get(), context.Get(), furtherOutputs.specular.Get(), 8, out);
            SendMaterial(proxy, 22, black, black, 0.2f);
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, 19401, kRtSignalSpecular, lighting);
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, 19402, kRtSignalSpecular, lighting);
            if (!finished)
                ReadTexture(device.Get(), context.Get(), furtherOutputs.specular.Get(), 8, out);
        };
        std::vector<uint16_t> reference, raced;
        run(true, reference);
        size_t glowing = 0;
        for (size_t i = 0; i < reference.size() / 4; ++i)
            glowing += HalfToFloat(reference[i * 4]) > 3.0f ? 1 : 0;
        size_t worst = 0;
        for (int attempt = 0; attempt < 16; ++attempt)
        {
            run(false, raced);
            size_t differ = 0;
            for (size_t i = 0; i < reference.size(); ++i)
                differ += reference[i] != raced[i] ? 1 : 0;
            worst = std::max(worst, differ);
        }
        char detail[200];
        sprintf_s(detail, "%zu pixels reflect the tower's glow; at most %zu values of 16 runs differ where the next frames"
                          " came at once",
                  glowing, worst);
        Check(glowing > 2000 && worst == 0, "a further trace while the next frames are recorded", detail);
        SendMaterial(proxy, 22, black, glow, 0.2f);
        SendInstances(proxy, scene, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
    }
    {
        // A mesh handed over between a frame and its further trace: the further trace builds
        // it and the scene anew, and its shadow is there; the frame's own has none.
        Mesh added = MakeCube(95);
        std::vector<Instance> shaded = scene;
        Instance block;
        block.mesh = 95;
        block.position = { 12, 3, -14 };
        block.scale[0] = block.scale[1] = block.scale[2] = 6.0;
        shaded.push_back(block);
        std::vector<Instance> shading = boxes;
        shading.push_back(block);
        SendInstances(proxy, shaded, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        Lighting extra;
        extra.flags = kRtFrameExtra;
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, 19420, kRtSignalSunVisibility);
        const bool accepted = SendMesh(proxy, added) == 1;
        SendFrame(proxy, camera, g, furtherOutputs, sun, 0.0f, 19420, kRtSignalSunVisibility, extra);
        Frame own, after;
        if (!ReadFrame(device.Get(), context.Get(), g, outputs, own) || !ReadFrame(device.Get(), context.Get(), g, furtherOutputs, after))
            return Fatal("reading the frames back", E_FAIL);
        const std::vector<int> without = ExpectedVisibility(camera, own.depth, own.normals, boxes, sun);
        const std::vector<int> with = ExpectedVisibility(camera, after.depth, after.normals, shading, sun);
        size_t shadowed = 0;
        for (size_t i = 0; i < with.size(); ++i)
            shadowed += with[i] == 0 && without[i] == 1 ? 1 : 0;
        if (!accepted || shadowed < 1000)
            Check(false, "a further trace after a mesh is built", accepted ? "the new box casts no shadow in view" : "the mesh was not taken");
        else
        {
            printf("      the new box shadows %zu pixels\n", shadowed);
            CheckVisibility("a further trace after a mesh is built: the frame's own", own, without);
            CheckVisibility("a further trace after a mesh is built", after, with);
        }
        proxy.releaseMesh(95);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- a mesh handed over again
    {
        // A mesh handed over again with the same triangles and its vertices moved, as a
        // skinned part's bake is: the first time its structure is built anew to be
        // updated, from then on updated in place. Each time the shadow is the moved box's.
        Mesh moving = MakeCube(96);
        Instance block;
        block.mesh = 96;
        block.position = { 12, 3, -14 };
        block.scale[0] = block.scale[1] = block.scale[2] = 6.0;
        std::vector<Instance> shaded = scene;
        shaded.push_back(block);
        SendInstances(proxy, shaded, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        const double offsets[3] = { 0.0, 0.4, -0.4 };
        std::vector<int> unmoved;
        size_t moved = 0;
        bool accepted = true;
        for (int k = 0; k < 3; ++k)
        {
            Mesh sent = moving;
            for (size_t v = 0; v < sent.positions.size(); v += 3)
                sent.positions[v] += static_cast<float>(offsets[k]);
            accepted = accepted && SendMesh(proxy, sent) == 1;
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, 19440 + k, kRtSignalSunVisibility);
            Frame frame;
            if (!ReadFrame(device.Get(), context.Get(), g, outputs, frame))
                return Fatal("reading the frame back", E_FAIL);
            Instance where = block;
            where.position.x += offsets[k] * block.scale[0];
            std::vector<Instance> shading = boxes;
            shading.push_back(where);
            const std::vector<int> expected = ExpectedVisibility(camera, frame.depth, frame.normals, shading, sun);
            if (k == 0)
                unmoved = expected;
            else
                for (size_t i = 0; i < expected.size(); ++i)
                    moved += expected[i] != unmoved[i] ? 1 : 0;
            const char* names[3] = { "a mesh handed over again: first", "a mesh handed over again: built to be updated",
                                     "a mesh handed over again: updated in place" };
            CheckVisibility(names[k], frame, expected);
        }
        Check(accepted && moved > 1000, "a mesh handed over again moves its shadow",
              accepted ? (std::to_string(moved) + " pixel(s) of the shadow moved over the two").c_str() : "a mesh was not taken");

        // What it saves: a larger mesh handed over every frame, once under its own id and
        // once under a new one each time, the one before released.
        const auto timed = [&](bool sameId)
        {
            Mesh cloth = MakePlane(97, 64);
            LARGE_INTEGER start = {}, end = {}, frequency = {};
            QueryPerformanceFrequency(&frequency);
            QueryPerformanceCounter(&start);
            for (int frame = 0; frame < 60; ++frame)
            {
                for (size_t v = 1; v < cloth.positions.size(); v += 3)
                    cloth.positions[v] = 0.01f * static_cast<float>(frame % 7);
                if (!sameId)
                {
                    proxy.releaseMesh(cloth.id);
                    cloth.id = 2000 + frame;
                }
                SendMesh(proxy, cloth);
                SendFrame(proxy, camera, g, outputs, sun, 0.0f, 19450 + frame, kRtSignalSunVisibility);
            }
            Frame last;
            ReadFrame(device.Get(), context.Get(), g, outputs, last);
            QueryPerformanceCounter(&end);
            proxy.releaseMesh(cloth.id);
            return static_cast<double>(end.QuadPart - start.QuadPart) * 1000.0 / frequency.QuadPart / 60.0;
        };
        const double inPlace = timed(true);
        const double anew = timed(false);
        printf("      a mesh of 16 384 vertices handed over every frame: %.3f ms a frame under its id, %.3f ms under"
               " a new id each frame\n", inPlace, anew);
        proxy.releaseMesh(96);
        SendInstances(proxy, scene, camera.position);
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

        // The main thread's calls while the render thread is in its frames, building meshes
        // and waiting for the GPU: they hand over without waiting for the frame.
        std::atomic<bool> running{ true };
        double worstCall = 0.0;
        size_t calls = 0;
        std::thread mainThread([&]()
        {
            LARGE_INTEGER frequency = {};
            QueryPerformanceFrequency(&frequency);
            while (running)
            {
                LARGE_INTEGER start = {}, end = {};
                QueryPerformanceCounter(&start);
                SendInstances(proxy, crowd, camera.position);
                QueryPerformanceCounter(&end);
                worstCall = std::max(worstCall, static_cast<double>(end.QuadPart - start.QuadPart) * 1000.0 / frequency.QuadPart);
                ++calls;
            }
        });
        LARGE_INTEGER frequency = {}, start = {}, end = {};
        QueryPerformanceFrequency(&frequency);
        QueryPerformanceCounter(&start);
        for (int frame = 0; frame < 60; ++frame)
        {
            for (int k = 0; k < 20; ++k)
            {
                Mesh m = MakeCube(nextId++);
                SendMesh(proxy, m);
                extra[m.id] = std::move(m);
            }
            while (extra.size() > 100)
            {
                proxy.releaseMesh(extra.begin()->first);
                extra.erase(extra.begin());
            }
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, 1100 + frame, kRtSignalSunVisibility);
        }
        Frame last;
        ReadFrame(device.Get(), context.Get(), g, outputs, last);
        QueryPerformanceCounter(&end);
        running = false;
        mainThread.join();
        const double frameMs = static_cast<double>(end.QuadPart - start.QuadPart) * 1000.0 / frequency.QuadPart / 60.0;
        char detail[200];
        sprintf_s(detail, "%zu calls of 20 103 instances during 60 frames of %.2f ms; the longest %.3f ms", calls, frameMs,
                  worstCall);
        Check(calls > 60 && worstCall < frameMs, "the main thread's calls during frames", detail);
        for (auto& entry : extra)
            proxy.releaseMesh(entry.first);
        extra.clear();
    }

    // ---------------------------------------------------------------- a frame that stops early
    {
        // The box's mesh, handed over again, is built in a frame that then stops: its
        // denoiser's textures cannot be made at a width past Direct3D 12's 16384. The
        // next frame's shadows need the box's structure that frame built.
        SendInstances(proxy, scene, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        const bool accepted = SendMesh(proxy, meshes[1]) == 1;
        Lighting stopping;
        stopping.flags = kRtFrameDenoise;
        stopping.width = 16385;
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, 3000, kRtSignalSunVisibility, stopping);
        const bool stopped = proxy.Status().find("the denoiser's textures could not be made") != std::string::npos;
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, 3001, kRtSignalSunVisibility);
        Frame after;
        if (!ReadFrame(device.Get(), context.Get(), g, outputs, after))
            return Fatal("reading the frame back", E_FAIL);
        printf("      %s\n", proxy.Status().c_str());
        if (!accepted || !stopped)
            Check(false, "a frame that stops early", accepted ? "the frame did not stop" : "the mesh was not taken");
        else
            CheckVisibility("a frame that stops early", after,
                            ExpectedVisibility(camera, after.depth, after.normals, boxes, sun));
    }

    // ---------------------------------------------------------------- a material's texture
    {
        // A material whose texture lies past the material textures' array is refused.
        RtMaterialDesc past = {};
        past.size = sizeof(past);
        past.magic = kRtMaterialMagic;
        past.material = 60;
        past.texture = static_cast<int32_t>(kRtMaxMaterialTextures);
        const int refused = proxy.setMaterial(&past);
        past.texture = -1;
        const int taken = proxy.setMaterial(&past);
        char detail[200];
        sprintf_s(detail, "texture %u %s, texture -1 %s", kRtMaxMaterialTextures, refused == 0 ? "refused" : "taken",
                  taken == 1 ? "taken" : "refused");
        Check(refused == 0 && taken == 1, "a material's texture past the array", detail);
    }

    // ---------------------------------------------------------------- surface intent
    {
        // The sun's pass finds each pixel's material by the camera's ray and writes its
        // shine as the material means it (u5) and how it was found (u6, the mode / 8). The
        // plane diffuse under Deferred's values for a diffuse shader, the box KSP's legacy
        // highlight whose gloss Deferred's smoothness gives back, the tower the G-buffer's
        // own, and a box drawn into the G-buffer only, which no ray finds, under a diffuse
        // shader's 0.5 / pi grey.
        const float none[3] = { 0.0f, 0.0f, 0.0f };
        const float glow[3] = { 4.0f, 2.0f, 1.0f };
        SendMaterial(proxy, 70, scene[0].albedo, none, 0.0f, 0.0f, 0.0f, RT_SURFACE_DRY);
        SendMaterial(proxy, 71, scene[1].albedo, none, 0.55f, 0.0f, 0.3f, RT_SURFACE_LEGACY, 0.5f, 0.5f);
        SendMaterial(proxy, 72, scene[2].albedo, glow, 0.2f, 0.0f, 0.04f, RT_SURFACE_KEEP);
        std::vector<Instance> traced = scene;
        traced[0].material = 70;
        traced[0].specular = 0.159f;
        traced[0].smoothness = 0.45f;
        traced[1].material = 71;
        traced[1].smoothness = 0.25f;
        traced[2].material = 72;
        traced[2].smoothness = 1.0f;
        Instance ghost;
        ghost.mesh = 1;
        ghost.position = { 18, 2, -8 };
        ghost.scale[0] = ghost.scale[1] = ghost.scale[2] = 4.0;
        ghost.albedo[0] = 0.2f; ghost.albedo[1] = 0.8f; ghost.albedo[2] = 0.3f;
        ghost.specular = 0.5f / 3.14159265f;
        ghost.smoothness = 0.45f;
        std::vector<Instance> drawn = traced;
        drawn.push_back(ghost);
        SendInstances(proxy, traced, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, drawn);
        Lighting lighting;
        lighting.flags = kRtFrameSurfaceIntent;
        lighting.sunColor[0] = lighting.sunColor[1] = lighting.sunColor[2] = 0.0f;
        lighting.bounces = 0.0f;
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, 15000, kRtSignalSunVisibility, lighting);
        std::vector<uint16_t> surface, mode;
        std::vector<uint32_t> albedo;
        std::vector<float> depth;
        ReadTexture(device.Get(), context.Get(), outputs.surface.Get(), 8, surface);
        ReadTexture(device.Get(), context.Get(), outputs.surfaceMode.Get(), 8, mode);
        ReadTexture(device.Get(), context.Get(), g.gbuffer0.Get(), 4, albedo);
        ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);

        // Each pixel's object by the albedo the G-buffer has: 0 plane, 1 box, 2 tower,
        // 3 the box no ray finds, -1 the sky; a pixel near another object's is left out.
        std::vector<int> object(depth.size(), -1);
        for (size_t i = 0; i < depth.size(); ++i)
        {
            if (depth[i] <= 0.0f)
                continue;
            double nearest = 1e9;
            for (int k = 0; k < 4; ++k)
            {
                double d = 0.0;
                for (int c = 0; c < 3; ++c)
                {
                    const double e = ((albedo[i] >> (8 * c)) & 0xFF) / 255.0 - drawn[k].albedo[c];
                    d += e * e;
                }
                if (d < nearest)
                {
                    nearest = d;
                    object[i] = k;
                }
            }
        }
        const auto inside = [&](size_t i)
        {
            const size_t x = i % kWidth, y = i / kWidth;
            if (object[i] < 0 || x < 2 || y < 2 || x + 2 >= kWidth || y + 2 >= kHeight)
                return false;
            for (int dy = -2; dy <= 2; ++dy)
                for (int dx = -2; dx <= 2; ++dx)
                    if (object[i + dy * static_cast<int>(kWidth) + dx] != object[i])
                        return false;
            return true;
        };
        // Per object the specular colour and smoothness it should get, in the G-buffer's
        // 8 bits, and its mode: the box's gloss 0.25 / 0.5, the tower's smoothness 1 kept
        // at 0.95, the unfound box's grey as diffuse.
        const float gloss = std::min((std::lround(0.25 * 255.0) / 255.0f) / 0.5f, 1.0f);
        const float kept = std::lround(0.04 * 255.0) / 255.0f;
        const float want[4][5] = { { 0.0f, 0.0f, 0.0f, 0.0f, float(RT_SURFACE_DRY) },
                                   { 0.3f * gloss, 0.3f * gloss, 0.3f * gloss, 0.55f, float(RT_SURFACE_LEGACY) },
                                   { kept, kept, kept, 0.95f, float(RT_SURFACE_KEEP) },
                                   { 0.0f, 0.0f, 0.0f, 0.0f, float(RT_SURFACE_MISSED) } };
        size_t count[4] = {}, right[4] = {};
        double sum[4][5] = {};
        for (size_t i = 0; i < depth.size(); ++i)
        {
            if (!inside(i))
                continue;
            const int k = object[i];
            ++count[k];
            const float found = HalfToFloat(mode[i * 4]) * 8.0f;
            bool ok = std::fabs(found - want[k][4]) < 0.01f;
            for (int c = 0; c < 4; ++c)
            {
                const float got = HalfToFloat(surface[i * 4 + c]);
                sum[k][c] += got;
                ok = ok && std::fabs(got - want[k][c]) < 0.002f;
            }
            sum[k][4] += found;
            right[k] += ok ? 1 : 0;
        }
        const char* names[4] = { "surface intent: diffuse under Deferred's shine", "surface intent: legacy gloss",
                                 "surface intent: kept, never a mirror", "surface intent: no material found" };
        for (int k = 0; k < 4; ++k)
        {
            const double n = count[k] > 0 ? double(count[k]) : 1.0;
            char detail[300];
            sprintf_s(detail, "%zu of %zu pixels right; mean specular %.4f, smoothness %.4f, mode %.2f (want %.4f, %.4f, %d)",
                      right[k], count[k], sum[k][0] / n, sum[k][3] / n, sum[k][4] / n, want[k][0], want[k][3],
                      int(want[k][4]));
            Check(count[k] > 2000 && right[k] * 1000 >= count[k] * 995, names[k], detail);
        }
        std::vector<float> rgb(depth.size() * 3);
        for (size_t i = 0; i < depth.size(); ++i)
        {
            rgb[i * 3 + 0] = HalfToFloat(surface[i * 4]) * 4.0f;
            rgb[i * 3 + 1] = HalfToFloat(surface[i * 4 + 3]);
            rgb[i * 3 + 2] = HalfToFloat(mode[i * 4]);
        }
        WriteBmp(NextToExecutable(L"rt_surface_intent.bmp"), kWidth, kHeight, rgb);

        // The forward light's reflections take the surface from u5: none on the diffuse
        // plane under a white sky and the tower's glow, some on the box and the tower; on
        // the plane some without the intent, from the G-buffer's values.
        lighting.sky[0] = lighting.sky[1] = lighting.sky[2] = 1.0f;
        const uint32_t reflections = kRtSignalSunVisibility | kRtSignalSpecular | kRtSignalForwardLight;
        double reflected[2][3] = {};
        for (int intent = 1; intent >= 0; --intent)
        {
            Lighting run = lighting;
            run.flags = intent != 0 ? kRtFrameSurfaceIntent : 0u;
            std::vector<double> mean;
            if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.forward.Get(), reflections, run,
                            sun, 8, 15100 + intent * 10, mean))
                return Fatal("reading the forward light back", E_FAIL);
            size_t counted[3] = {};
            for (size_t i = 0; i < depth.size(); ++i)
            {
                if (!inside(i) || object[i] > 2)
                    continue;
                ++counted[object[i]];
                reflected[intent][object[i]] += (mean[i * 3] + mean[i * 3 + 1] + mean[i * 3 + 2]) / 3.0;
            }
            for (int k = 0; k < 3; ++k)
                reflected[intent][k] /= counted[k] > 0 ? double(counted[k]) : 1.0;
            if (intent != 0)
                SaveColour(L"rt_surface_intent_reflections.bmp", mean, 2.0);
        }
        char detail[300];
        sprintf_s(detail, "mean reflection: plane %.5f (%.4f from the G-buffer), legacy box %.4f, kept tower %.4f",
                  reflected[1][0], reflected[0][0], reflected[1][1], reflected[1][2]);
        Check(reflected[1][0] < 1e-4 && reflected[0][0] > 0.02 && reflected[1][1] > 0.02 && reflected[1][2] > 0.01,
              "surface intent: reflections read it", detail);

        // A surface a mod made shinier after the G-buffer pass (EVE's wet effect) is marked in the alpha of the normals,
        // and keeps the G-buffer's shine over the dry one its material means: the plane, dry above, keeps 0.159 at 0.45.
        std::vector<Instance> wetDrawn = drawn;
        wetDrawn[0].wet = true;
        RenderGBuffer(context.Get(), g, camera, meshes, wetDrawn);
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, 15200, kRtSignalSunVisibility, lighting);
        std::vector<uint16_t> wetSurface, wetMode;
        ReadTexture(device.Get(), context.Get(), outputs.surface.Get(), 8, wetSurface);
        ReadTexture(device.Get(), context.Get(), outputs.surfaceMode.Get(), 8, wetMode);
        size_t planePixels = 0, planeWet = 0;
        for (size_t i = 0; i < depth.size(); ++i)
        {
            if (!inside(i) || object[i] != 0)
                continue;
            ++planePixels;
            const float specularOut = HalfToFloat(wetSurface[i * 4]), smoothnessOut = HalfToFloat(wetSurface[i * 4 + 3]);
            const float modeOut = HalfToFloat(wetMode[i * 4]) * 8.0f;
            if (std::fabs(specularOut - 0.159f) < 0.003f && std::fabs(smoothnessOut - 0.45f) < 0.003f
                && std::fabs(modeOut - float(RT_SURFACE_KEEP)) < 0.01f)
                ++planeWet;
        }
        char wetDetail[200];
        sprintf_s(wetDetail, "%zu of %zu pixels of the marked plane keep the G-buffer's shine", planeWet, planePixels);
        Check(planePixels > 2000 && planeWet * 1000 >= planePixels * 995, "surface intent: a surface made wetter keeps the G-buffer's", wetDetail);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- the radiance cache
    {
        // The cache (rtRadianceCache) on the box and the plane, under a sky and the sun, two
        // bounces as the game traces: it fills and resolves, a camera that moves with its
        // move told keeps finding its cells, its cells age out once no ray meets them, and
        // the diffuse light it gives stays near the one without it. The light compared as
        // the mean over 256 frames, per 16x16 block of surface pixels: the blocks' mean
        // absolute difference of luminance over their mean luminance, against the light
        // without the cache at two bounces and at eight; the cache's light holds every
        // bounce in time (RtCommon.hlsli, Radiance). And the white furnace at two bounces,
        // where every bounce brings the sky's 1 again: without the cache two bounces fall
        // short of 1 in the box's shadow, with it the light is 1 everywhere.
        const std::wstring ini = NextToExecutable(L"ReDefinitionProxy.ini");
        const auto setIni = [&](const wchar_t* key, const wchar_t* value)
        {
            WritePrivateProfileStringW(L"Proxy", key, value, ini.c_str());
        };
        const auto reload = [&]()
        {
            // The proxy looks at the file at most once a second, from its Present.
            Sleep(1100);
            swapChain->Present(0, 0);
        };
        const auto statusCount = [&](const char* name) -> double
        {
            const std::string text = proxy.Status();
            const size_t at = text.find(std::string(" ") + name + "=", text.find(" cache "));
            return text.find(" cache ") == std::string::npos || at == std::string::npos
                       ? -1.0
                       : std::stod(text.substr(at + strlen(name) + 2));
        };
        ComPtr<IDXGIAdapter3> adapter3;
        adapter.As(&adapter3);
        const auto videoMemory = [&]()
        {
            DXGI_QUERY_VIDEO_MEMORY_INFO info = {};
            if (adapter3 == nullptr || FAILED(adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info)))
                return 0.0;
            return static_cast<double>(info.CurrentUsage) / 1e6;
        };

        SendInstances(proxy, scene, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        std::vector<float> depth;
        ReadTexture(device.Get(), context.Get(), g.depth.Get(), 4, depth);
        Lighting lighting;
        lighting.sky[0] = 0.4f;
        lighting.sky[1] = 0.5f;
        lighting.sky[2] = 0.7f;
        lighting.bounces = 2.0f;
        lighting.clamp = 20.0f;
        const int frames = 256;
        uint32_t frameIndex = 20000;
        std::vector<double> offTwo, offEight, on;
        Lighting eight = lighting;
        eight.bounces = 8.0f;
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                        lighting, sun, frames, frameIndex, offTwo))
            return Fatal("reading the light without the cache back", E_FAIL);
        frameIndex += frames;
        float passesOff[kRtPassTimings] = {};
        proxy.passTimings(passesOff, kRtPassTimings);
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                        eight, sun, frames, frameIndex, offEight))
            return Fatal("reading the light without the cache back", E_FAIL);
        frameIndex += frames;

        setIni(L"rtRadianceCache", L"1");
        reload();
        // Its first frame clears it, as a cut does (kRtFrameReset).
        Lighting cleared = lighting;
        cleared.flags = kRtFrameReset;
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, cleared);
        for (int f = 0; f < 60; ++f)
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, lighting);
        std::vector<uint16_t> synced;
        ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, synced);
        printf("      %s\n", proxy.Status().c_str());
        const double live = statusCount("live"), samples = statusCount("samples"), queries = statusCount("queries"),
                     warm = statusCount("warm"), full = statusCount("full");
        char detail[400];
        sprintf_s(detail, "after 61 frames: %.0f entries live, %.0f samples written a frame, %.0f of %.0f path hits ended"
                          " in it, %.0f not placed",
                  live, samples, warm, queries, full);
        Check(live > 1000 && samples > 0.15 * kWidth * kHeight && warm > 0.5 * queries && full == 0,
              "radiance cache: fills and resolves", detail);

        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                        lighting, sun, frames, frameIndex, on))
            return Fatal("reading the light with the cache back", E_FAIL);
        frameIndex += frames;
        float passesOn[kRtPassTimings] = {};
        proxy.passTimings(passesOn, kRtPassTimings);
        double ratioTwo = 0.0, ratioEight = 0.0, ratioNoise = 0.0;
        const double errorTwo = BlockDifference(on, offTwo, depth, ratioTwo);
        const double errorEight = BlockDifference(on, offEight, depth, ratioEight);
        // What the noise of 256 frames alone leaves between two runs without the cache.
        const double errorNoise = BlockDifference(offEight, offTwo, depth, ratioNoise);
        sprintf_s(detail, "light per 16x16 block off by %.2f %% from two bounces without it (mean %+.2f %%), %.2f %% from"
                          " eight (mean %+.2f %%); eight bounces against two %.2f %% (mean %+.2f %%)",
                  100.0 * errorTwo, 100.0 * (ratioTwo - 1.0), 100.0 * errorEight, 100.0 * (ratioEight - 1.0),
                  100.0 * errorNoise, 100.0 * (ratioNoise - 1.0));
        Check(errorEight < errorNoise + 0.01 && std::fabs(ratioEight - 1.0) < 0.01 && std::fabs(ratioTwo - 1.0) < 0.03,
              "radiance cache: the light as without it", detail);
        SaveColour(L"rt_cache_on.bmp", on, 1.0);
        SaveColour(L"rt_cache_off.bmp", offTwo, 1.0);
        // One frame's times, for scale; the KSP scene above compares them in turn.
        printf("      radiance cache at %ux%u, the diffuse light alone, one frame: diffuse %.3f ms without it, %.3f ms with"
               " it; its resolve %.3f ms\n",
               kWidth, kHeight, passesOff[2], passesOn[2], passesOn[kRtPassCache]);

        // A camera that moves, its move told (RtFramePacket.cameraDelta): its cells stay where
        // they are in the world, and the paths keep ending in them. Told none, as if the world
        // moved with the camera, the cells it finds are new.
        const auto glide = [&](bool told, double& warmShare)
        {
            Camera moving = camera;
            double warmSum = 0.0, querySum = 0.0;
            for (int f = 0; f < 30; ++f)
            {
                const Vec3 step = { 0.37, 0.0, 0.21 };
                Lighting run = lighting;
                if (f > 0)
                {
                    moving.position = moving.position + step;
                    moving.target = moving.target + step;
                    moving.Update();
                    if (told)
                        run.cameraDelta = Vec3{ 0, 0, 0 } - step;
                }
                SendInstances(proxy, scene, moving.position);
                RenderGBuffer(context.Get(), g, moving, meshes, scene);
                SendFrame(proxy, moving, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, run);
                if (f >= 10)
                {
                    warmSum += statusCount("warm");
                    querySum += statusCount("queries");
                }
            }
            warmShare = querySum > 0.0 ? warmSum / querySum : 0.0;
            // Back, and the cache filled again where the camera was.
            SendInstances(proxy, scene, camera.position);
            RenderGBuffer(context.Get(), g, camera, meshes, scene);
            Lighting reset = lighting;
            reset.flags = kRtFrameReset;
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, reset);
            for (int f = 0; f < 40; ++f)
                SendFrame(proxy, camera, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, lighting);
        };
        double warmTold = 0.0, warmUntold = 0.0;
        glide(true, warmTold);
        glide(false, warmUntold);
        sprintf_s(detail, "a camera gliding 11 m: %.1f %% of the path hits end in the cache with its move told, %.1f %%"
                          " without (%.1f %% standing)",
                  100.0 * warmTold, 100.0 * warmUntold, queries > 0.0 ? 100.0 * warm / queries : 0.0);
        Check(warmTold > 0.8 * (warm / std::max(queries, 1.0)) && warmTold > warmUntold + 0.1,
              "radiance cache: cells fixed to the world", detail);

        // The debug views (rtRadianceCacheDebug): the cache's light, and its cells.
        std::vector<uint16_t> view;
        std::vector<double> shown(static_cast<size_t>(kWidth) * kHeight * 3, 0.0);
        double shownSum = 0.0, diffuseSum = 0.0;
        for (const wchar_t* mode : { L"1", L"2" })
        {
            setIni(L"rtRadianceCacheDebug", mode);
            reload();
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, lighting);
            ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, view);
            for (size_t i = 0; i < shown.size(); ++i)
                shown[i] = HalfToFloat(view[(i / 3) * 4 + i % 3]);
            if (mode[0] == L'1')
                for (size_t i = 0; i < depth.size(); ++i)
                    if (depth[i] > 0.0f)
                    {
                        shownSum += shown[i * 3] + shown[i * 3 + 1] + shown[i * 3 + 2];
                        diffuseSum += on[i * 3] + on[i * 3 + 1] + on[i * 3 + 2];
                    }
            SaveColour(mode[0] == L'1' ? L"rt_cache_value.bmp" : L"rt_cache_cells.bmp", shown, 1.0);
        }
        setIni(L"rtRadianceCacheDebug", L"0");
        reload();
        sprintf_s(detail, "the cache's own light %.3f of the diffuse light with it, on the surfaces",
                  diffuseSum > 0.0 ? shownSum / diffuseSum : 0.0);
        Check(diffuseSum > 0.0 && std::fabs(shownSum / diffuseSum - 1.0) < 0.1, "radiance cache: its debug view", detail);

        // No ray meets its cells: the camera looks at the sky alone, and after
        // rtRadianceCacheMaxAge frames they go, an eighth of the table looked at a frame.
        Camera up = camera;
        up.target = camera.position + Vec3{ 0.0, 100.0, 1.0 };
        up.Update();
        RenderGBuffer(context.Get(), g, up, meshes, scene);
        double evicted = 0.0;
        for (int f = 0; f < 70; ++f)
        {
            SendFrame(proxy, up, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, lighting);
            evicted = std::max(evicted, statusCount("evicted"));
        }
        ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, synced);
        SendFrame(proxy, up, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, lighting);
        ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, synced);
        const double liveAfter = statusCount("live");
        sprintf_s(detail, "%.0f entries live before, %.0f after 70 frames of sky (at most %.0f evicted in a frame)", live,
                  liveAfter, evicted);
        Check(liveAfter >= 0.0 && liveAfter < 0.01 * live && evicted > 0.0, "radiance cache: unused cells evicted",
              detail);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);

        // The white furnace at two bounces.
        const float white[3] = { 1.0f, 1.0f, 1.0f };
        const float none[3] = { 0.0f, 0.0f, 0.0f };
        SendMaterial(proxy, 10, white, none, 0.2f);
        std::vector<Instance> furnace = scene;
        for (Instance& instance : furnace)
            instance.material = 10;
        SendInstances(proxy, furnace, camera.position);
        Lighting furnaceLight;
        furnaceLight.sunColor[0] = furnaceLight.sunColor[1] = furnaceLight.sunColor[2] = 0.0f;
        furnaceLight.bounces = 2.0f;
        const auto furnaceMean = [&](std::vector<double>& mean, double& lowest)
        {
            double total = 0.0;
            size_t count = 0;
            lowest = 2.0;
            for (size_t i = 0; i < depth.size(); ++i)
            {
                if (depth[i] <= 0.0f)
                    continue;
                total += (mean[i * 3] + mean[i * 3 + 1] + mean[i * 3 + 2]) / 3.0;
                ++count;
            }
            // The darkest 16x16 block, the box's foot.
            for (UINT by = 0; by + 16 <= kHeight; by += 16)
                for (UINT bx = 0; bx + 16 <= kWidth; bx += 16)
                {
                    double sum = 0.0;
                    bool surface = true;
                    for (UINT y = by; y < by + 16 && surface; ++y)
                        for (UINT x = bx; x < bx + 16; ++x)
                        {
                            const size_t i = static_cast<size_t>(y) * kWidth + x;
                            surface = surface && depth[i] > 0.0f;
                            sum += (mean[i * 3] + mean[i * 3 + 1] + mean[i * 3 + 2]) / 3.0;
                        }
                    if (surface)
                        lowest = std::min(lowest, sum / 256.0);
                }
            return count > 0 ? total / count : 0.0;
        };
        std::vector<double> furnaceOn, furnaceOff;
        Lighting furnaceFirst = furnaceLight;
        furnaceFirst.flags = kRtFrameReset;
        SendFrame(proxy, camera, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, furnaceFirst);
        for (int f = 0; f < 60; ++f)
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, furnaceLight);
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                        furnaceLight, sun, frames, frameIndex, furnaceOn))
            return Fatal("reading the furnace back", E_FAIL);
        frameIndex += frames;

        // Off again: its memory goes, and the status no longer counts it. The process's video
        // memory read with nothing else changing in between: the same signals traced, once
        // the slots that held the cache's buffers came round.
        for (int f = 0; f < 4; ++f)
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, furnaceLight);
        ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, synced);
        const double memoryOn = videoMemory();
        setIni(L"rtRadianceCache", L"0");
        reload();
        for (int f = 0; f < 8; ++f)
            SendFrame(proxy, camera, g, outputs, sun, 0.0f, frameIndex++, kRtSignalDiffuse, furnaceLight);
        ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, synced);
        const double memoryOff = videoMemory();
        const bool counted = proxy.Status().find(" cache ") != std::string::npos;
        if (!Accumulate(proxy, device.Get(), context.Get(), camera, g, outputs, outputs.diffuse.Get(), kRtSignalDiffuse,
                        furnaceLight, sun, frames, frameIndex, furnaceOff))
            return Fatal("reading the furnace back", E_FAIL);
        frameIndex += frames;
        double lowestOn = 0.0, lowestOff = 0.0;
        const double meanOn = furnaceMean(furnaceOn, lowestOn), meanOff = furnaceMean(furnaceOff, lowestOff);
        sprintf_s(detail, "white furnace at two bounces: %.4f on average with it, its darkest block %.4f; %.4f and %.4f"
                          " without",
                  meanOn, lowestOn, meanOff, lowestOff);
        Check(std::fabs(meanOn - 1.0) < 0.02 && lowestOn > 0.95 && lowestOff < lowestOn - 0.02,
              "radiance cache: the white furnace", detail);
        const double expectedMb = (1 << 20) * 44.0 / 1e6;
        sprintf_s(detail, "video memory %.1f MB less once off (%.1f MB its buffers), the status %s", memoryOn - memoryOff,
                  expectedMb, counted ? "still counts it" : "without it");
        Check(memoryOn - memoryOff > 0.9 * expectedMb && !counted, "radiance cache: let go when off", detail);
        SendInstances(proxy, scene, camera.position);
    }

    // ---------------------------------------------------------------- timings
    {
        SendInstances(proxy, scene, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        const uint32_t all = kRtSignalSunVisibility | kRtSignalAmbientOcclusion | kRtSignalDiffuse | kRtSignalSpecular;
        // Without the bodies' lights, and with two of them in gamma space, as in KSP.
        Lighting lit;
        const float two[16] = { -0.5f, 0.7f, 0.1f, 0.01f, 0.05f, 0.05f, 0.05f, 1.0e8f,
                                0.2f, -0.9f, 0.1f, 0.5f, 0.1f, 0.1f, 0.1f, 1.0e3f };
        memcpy(lit.lights, two, sizeof(two));
        lit.lightCount = 2;
        lit.gamma = true;
        for (int run = 0; run < 2; ++run)
        {
            for (int i = 0; i < 8; ++i)
                SendFrame(proxy, camera, g, outputs, sun, 0.0047f, 2000 + run * 8 + i, all, run == 1 ? lit : Lighting());
            float build = 0, trace = 0;
            proxy.timings(&build, &trace);
            float passes[kRtPassTimings] = {};
            proxy.passTimings(passes, kRtPassTimings);
            printf("      timings at %ux%u%s: scene build %.3f ms; sun %.3f, occlusion %.3f, diffuse %.3f, specular %.3f;"
                   " all passes %.3f ms\n", kWidth, kHeight, run == 1 ? " with two moonlights" : "", build, passes[0],
                   passes[1], passes[2], passes[3], trace);
        }

        // Further traces of the last frame, of the reflections alone, as the ocean's: the
        // timings stay the frame's, whose list is read once its slot comes round again.
        Lighting further;
        further.flags = kRtFrameExtra | kRtFrameWater;
        for (int i = 0; i < 6; ++i)
            SendFrame(proxy, camera, g, outputs, sun, 0.0047f, 2015, kRtSignalSpecular, further);
        float passes[kRtPassTimings] = {};
        proxy.passTimings(passes, kRtPassTimings);
        char detail[200];
        sprintf_s(detail, "after six further traces: sun %.3f, occlusion %.3f, diffuse %.3f, specular %.3f ms", passes[0],
                  passes[1], passes[2], passes[3]);
        Check(passes[0] > 0.0f && passes[1] > 0.0f && passes[2] > 0.0f && passes[3] > 0.0f, "timings past further traces",
              detail);
    }

    // ---------------------------------------------------------------- Ray Reconstruction through NGX
    if (reconstructedThroughStreamline)
    {
        // A swapchain of its own with rrPath=ngx (ReDefinitionProxy.ini), read as KSP's
        // would be made: with frame generation off it is then not Streamline's, and Ray
        // Reconstruction goes through NGX on its Direct3D 12 device, after Streamline
        // shut down with the swapchain before.
        const std::wstring ini = NextToExecutable(L"ReDefinitionProxy.ini");
        wchar_t before[32] = {};
        GetPrivateProfileStringW(L"Proxy", L"rrPath", L"auto", before, 32, ini.c_str());
        WritePrivateProfileStringW(L"Proxy", L"rrPath", L"ngx", ini.c_str());
        swapChain.Reset();
        hr = factory->CreateSwapChainForHwnd(device.Get(), window, &desc, nullptr, nullptr, &swapChain);
        WritePrivateProfileStringW(L"Proxy", L"rrPath", before, ini.c_str());
        if (FAILED(hr))
            return Fatal("CreateSwapChainForHwnd with rrPath=ngx", hr);
        char why[256] = {};
        if (proxy.available(why, sizeof(why)) != 1)
            return Fatal("the proxy's raytracing on the swapchain with rrPath=ngx", E_FAIL);
        // The meshes again: the raytracing's go with the device (Raytracing::ReleaseLocked).
        std::vector<int> sent;
        for (const Instance& instance : rrScene)
            if (std::find(sent.begin(), sent.end(), instance.mesh) == sent.end())
            {
                sent.push_back(instance.mesh);
                if (SendMesh(proxy, meshes[instance.mesh]) != 1)
                    return Fatal("a mesh on the swapchain with rrPath=ngx", E_FAIL);
            }
        if (proxy.reconstructionAvailable(why, sizeof(why)) != 1)
            Check(false, "DLSS Ray Reconstruction through NGX", why);
        else
        {
            printf("      DLSS Ray Reconstruction %s\n", why);
            Check(strstr(why, "NGX") != nullptr, "Ray Reconstruction's route with rrPath=ngx", why);
            reconstruct(" through NGX");
        }
    }

    // ---------------------------------------------------------------- a vessel's meshes at once
    {
        // The load spike of a flight's first frames: the proxy's log of a launch had 130 to
        // 170 meshes built in one frame, 40 000 to 63 000 triangles, the largest 400 to 6 600,
        // their builds 19 to 25 ms of the GPU (ReDefinitionProxy.log, "a slow frame"). Parts
        // of a vessel as tubes of 80 to 6 600 triangles, 150 of them as such a frame hands
        // them over and 600 at once, built under each of the ways the ini offers
        // (Config.h, rtBlas*, rtTlasFastTrace), switched live as the sun's denoiser above:
        // the CPU's time recording the builds, the GPU's to the meshes built, the frames it
        // takes, the trace afterwards and the memory; each picture against the first's.
        ComPtr<IDXGIAdapter3> adapter3;
        adapter.As(&adapter3);
        const auto videoMemory = [&]()
        {
            DXGI_QUERY_VIDEO_MEMORY_INFO info = {};
            if (adapter3 == nullptr || FAILED(adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info)))
                return 0.0;
            return static_cast<double>(info.CurrentUsage) / 1e6;
        };
        const auto statusValue = [&](const std::string& text, const char* key)
        {
            const size_t at = text.find(key);
            return at == std::string::npos ? 0.0 : atof(text.c_str() + at + strlen(key));
        };
        struct MeshBuild
        {
            unsigned count = 0, timed = 0;
            double cpu = 0, gpu = 0;
        };
        const auto meshBuild = [&](const std::string& text)
        {
            MeshBuild b;
            const size_t at = text.find("meshBuild=");
            if (at != std::string::npos)
                sscanf_s(text.c_str() + at, "meshBuild=%u/%lfms/%lfms/%u", &b.count, &b.cpu, &b.gpu, &b.timed);
            return b;
        };

        struct Variant
        {
            const char* name;
            const wchar_t* keys[7][2];
        };
        const Variant variants[] = {
            { "before: mesh by mesh, own buffers",
              { { L"rtBlasBatch", L"0" }, { L"rtBlasPool", L"0" }, { L"rtBlasBudget", L"0" }, { L"rtBlasCompaction", L"0" },
                { L"rtBlasRefineBudget", L"65536" }, { L"rtTlasFastTrace", L"0" },
                { L"rtMegaGeometry", L"0" } } },
            { "batched",
              { { L"rtBlasBatch", L"1" }, { L"rtBlasPool", L"0" }, { L"rtBlasBudget", L"0" }, { L"rtBlasCompaction", L"0" },
                { L"rtBlasRefineBudget", L"65536" }, { L"rtTlasFastTrace", L"0" },
                { L"rtMegaGeometry", L"0" } } },
            { "pooled",
              { { L"rtBlasBatch", L"0" }, { L"rtBlasPool", L"1" }, { L"rtBlasBudget", L"0" }, { L"rtBlasCompaction", L"0" },
                { L"rtBlasRefineBudget", L"65536" }, { L"rtTlasFastTrace", L"0" },
                { L"rtMegaGeometry", L"0" } } },
            { "batched, pooled",
              { { L"rtBlasBatch", L"1" }, { L"rtBlasPool", L"1" }, { L"rtBlasBudget", L"0" }, { L"rtBlasCompaction", L"0" },
                { L"rtBlasRefineBudget", L"65536" }, { L"rtTlasFastTrace", L"0" },
                { L"rtMegaGeometry", L"0" } } },
            { "batched, pooled, 20 000 triangles a frame",
              { { L"rtBlasBatch", L"1" }, { L"rtBlasPool", L"1" }, { L"rtBlasBudget", L"20000" }, { L"rtBlasCompaction", L"0" },
                { L"rtBlasRefineBudget", L"65536" }, { L"rtTlasFastTrace", L"0" },
                { L"rtMegaGeometry", L"0" } } },
            { "batched, pooled, compacted",
              { { L"rtBlasBatch", L"1" }, { L"rtBlasPool", L"1" }, { L"rtBlasBudget", L"0" }, { L"rtBlasCompaction", L"1" },
                { L"rtBlasRefineBudget", L"65536" }, { L"rtTlasFastTrace", L"0" },
                { L"rtMegaGeometry", L"0" } } },
            { "batched, pooled, fast build, rebuilt, compacted: defaults",
              { { L"rtBlasBatch", L"1" }, { L"rtBlasPool", L"1" }, { L"rtBlasBudget", L"0" }, { L"rtBlasCompaction", L"2" },
                { L"rtBlasRefineBudget", L"65536" }, { L"rtTlasFastTrace", L"0" },
                { L"rtMegaGeometry", L"0" } } },
            { "the defaults, the scene structure fast trace",
              { { L"rtBlasBatch", L"1" }, { L"rtBlasPool", L"1" }, { L"rtBlasBudget", L"0" }, { L"rtBlasCompaction", L"2" },
                { L"rtBlasRefineBudget", L"65536" }, { L"rtTlasFastTrace", L"1" },
                { L"rtMegaGeometry", L"0" } } },
            // RTX Mega Geometry's clusters (RaytracingMegaGeometry.cpp) against the defaults.
            { "the defaults, RTX Mega Geometry's clusters",
              { { L"rtBlasBatch", L"1" }, { L"rtBlasPool", L"1" }, { L"rtBlasBudget", L"0" }, { L"rtBlasCompaction", L"2" },
                { L"rtBlasRefineBudget", L"65536" }, { L"rtTlasFastTrace", L"0" }, { L"rtMegaGeometry", L"1" } } },
        };
        const size_t variantCount = sizeof(variants) / sizeof(variants[0]);
        const std::wstring ini = NextToExecutable(L"ReDefinitionProxy.ini");
        std::wstring before[7];
        for (int k = 0; k < 7; ++k)
        {
            wchar_t value[64] = {};
            GetPrivateProfileStringW(L"Proxy", variants[0].keys[k][0], L"", value, 64, ini.c_str());
            before[k] = value;
        }
        const auto apply = [&](const Variant& variant)
        {
            for (const auto& key : variant.keys)
                WritePrivateProfileStringW(L"Proxy", key[0], key[1], ini.c_str());
            // The proxy looks at the file at most once a second, from its Present.
            Sleep(1100);
            swapChain->Present(0, 0);
        };

        // The parts: tubes on a grid around the box, each a mesh of its own.
        const auto partMesh = [](int id, int i)
        {
            const int kind = i % 40;
            const int segments = kind < 28 ? 8 + (i % 5) * 2 : kind < 39 ? 24 : 48;
            const int rings = kind < 28 ? 4 + (i % 7) : kind < 39 ? 6 + (i % 9) : 40 + (i % 29);
            return MakeTube(id, segments, rings);
        };
        const uint32_t traced = kRtSignalSunVisibility | kRtSignalAmbientOcclusion | kRtSignalDiffuse | kRtSignalSpecular;
        const int sizes[2] = { 150, 600 };
        int nextId = 70000;
        uint32_t frameIndex = 30000;
        std::vector<int> released;
        struct Result
        {
            double cpu = 1e9, peakCpu = 1e9, gpu = 1e9, peakGpu = 1e9, build = 1e9, trace = 1e9, blas = 0, geometry = 0, reserved = 0, memory = 0,
                   forestBuild = 1e9, forestTrace = 1e9;
            size_t forestInstances = 0;
            unsigned clustered = 0;
            int frames = 0;
            size_t differing = 0, compared = 0;
            uint64_t triangles = 0;
        };
        std::vector<Result> results(variantCount * 2);
        std::vector<Frame> reference(2);
        std::vector<std::vector<uint16_t>> referenceDiffuse(2);
        for (int round = 0; round < 3; ++round)
            for (size_t v = 0; v < variantCount; ++v)
                for (int s = 0; s < 2; ++s)
                {
                    Result& result = results[v * 2 + s];
                    if (s == 0)
                        apply(variants[v]);
                    // The parts of the run before gone, and the slots past them.
                    for (int id : released)
                    {
                        proxy.releaseMesh(id);
                        meshes.erase(id);
                    }
                    released.clear();
                    SendInstances(proxy, scene, camera.position);
                    for (int f = 0; f < 4; ++f)
                        SendFrame(proxy, camera, g, outputs, sun, 0.0047f, frameIndex++, kRtSignalSunVisibility);

                    std::vector<Instance> vessel = scene;
                    uint64_t triangles = 0;
                    for (int i = 0; i < sizes[s]; ++i)
                    {
                        const int id = nextId++;
                        meshes[id] = partMesh(id, i);
                        triangles += meshes[id].indices.size() / 3;
                        if (FAILED(UploadMeshForRaster(device.Get(), meshes[id])) || SendMesh(proxy, meshes[id]) != 1)
                            return Fatal("a vessel's mesh", E_FAIL);
                        released.push_back(id);
                        Instance part;
                        part.mesh = id;
                        const int side = s == 0 ? 13 : 25;
                        part.position = { -18.0 + (i % side) * 36.0 / side, 1.2, -18.0 + (i / side) * 36.0 / side };
                        part.scale[0] = part.scale[2] = 0.9 * 18.0 / side;
                        part.scale[1] = 2.4;
                        part.albedo[0] = 0.7f; part.albedo[1] = 0.7f; part.albedo[2] = 0.75f;
                        vessel.push_back(part);
                    }
                    result.triangles = triangles;
                    SendInstances(proxy, vessel, camera.position);
                    RenderGBuffer(context.Get(), g, camera, meshes, vessel);

                    // The frames until every part is built, and those its slot takes to come
                    // round, which reads its timestamps.
                    const MeshBuild start = meshBuild(proxy.Status());
                    double peakGpu = 0.0, sumGpu = 0.0, cpuMs = 0.0, peakCpu = 0.0, buildMs = 0.0;
                    unsigned timed = start.timed;
                    int buildFrames = 0;
                    for (int f = 0; f < 40; ++f)
                    {
                        SendFrame(proxy, camera, g, outputs, sun, 0.0047f, frameIndex++, traced);
                        const std::string text = proxy.Status();
                        const MeshBuild now = meshBuild(text);
                        if (now.timed != timed)
                        {
                            timed = now.timed;
                            peakGpu = std::max(peakGpu, now.gpu);
                            sumGpu += now.gpu;
                            buildMs = std::max(buildMs, statusValue(text, " build="));
                        }
                        if (statusValue(text, " built=") > 0 && f < 30)
                        {
                            ++buildFrames;
                            cpuMs += now.cpu;
                            peakCpu = std::max(peakCpu, now.cpu);
                        }
                    }
                    // The trace once the structures settled, the passes' mean over 16 frames.
                    double traceSum = 0.0;
                    for (int f = 0; f < 16; ++f)
                    {
                        SendFrame(proxy, camera, g, outputs, sun, 0.0047f, frameIndex++, traced);
                        float build = 0, trace = 0;
                        proxy.timings(&build, &trace);
                        traceSum += trace;
                    }
                    const std::string settled = proxy.Status();
                    result.clustered = static_cast<unsigned>(statusValue(settled, "clustered="));
                    // The picture: the sun's visibility and the camera's rays, as the first
                    // variant's.
                    SendFrame(proxy, camera, g, outputs, sun, 0.0047f, 39999, kRtSignalSunVisibility | kRtSignalPrimaryCheck);
                    Frame frame;
                    if (!ReadFrame(device.Get(), context.Get(), g, outputs, frame))
                        return Fatal("reading the vessel's frame back", E_FAIL);
                    // And the diffuse light, whose rays read the normals of what they meet, by
                    // the triangle their hit is on (RtCommon.hlsli, RT_TRIANGLE).
                    SendFrame(proxy, camera, g, outputs, sun, 0.0047f, 39998, kRtSignalDiffuse);
                    std::vector<uint16_t> diffuse;
                    if (!ReadTexture(device.Get(), context.Get(), outputs.diffuse.Get(), 8, diffuse))
                        return Fatal("reading the vessel's diffuse light back", E_FAIL);
                    if (v == 0 && round == 0)
                    {
                        reference[s] = frame;
                        referenceDiffuse[s] = diffuse;
                        if (s == 1)
                            SaveVisibility(L"rt_vessel_parts.bmp", frame);
                    }
                    size_t differing = 0, compared = 0;
                    for (size_t i = 0; i < frame.depth.size(); ++i)
                    {
                        if (frame.depth[i] <= 0.0f)
                            continue;
                        ++compared;
                        const bool lit = HalfToFloat(frame.visibility[i * 4]) > 0.5f;
                        const bool litBefore = HalfToFloat(reference[s].visibility[i * 4]) > 0.5f;
                        const float hit = frame.primary[i * 4], hitBefore = reference[s].primary[i * 4];
                        bool light = true;
                        for (int c = 0; c < 3; ++c)
                        {
                            const float now = HalfToFloat(diffuse[i * 4 + c]);
                            const float was = HalfToFloat(referenceDiffuse[s][i * 4 + c]);
                            light = light && std::fabs(now - was) <= 0.02f * std::max(1.0f, std::fabs(was));
                        }
                        if (lit != litBefore || !light || std::fabs(hit - hitBefore) > 1e-3f * std::max(1.0f, std::fabs(hitBefore)))
                            ++differing;
                    }
                    // The scene's structure over many instances, as Parallax's scatter makes
                    // them: each part a hundred times (150) or forty (600) over the plane; the
                    // scene build's and the trace's mean over 16 frames, the G-buffer the
                    // vessel's.
                    std::vector<Instance> forest = scene;
                    const int copies = s == 0 ? 100 : 40;
                    const int forestRows = copies * sizes[s] / 160 + 1;
                    for (int c = 0; c < copies; ++c)
                        for (int i = 0; i < sizes[s]; ++i)
                        {
                            Instance part = vessel[scene.size() + i];
                            const int at = c * sizes[s] + i;
                            part.position = { -90.0 + (at % 160) * 1.125, 1.2, -90.0 + (at / 160) * 180.0 / forestRows };
                            part.scale[0] = part.scale[2] = 0.8;
                            forest.push_back(part);
                        }
                    SendInstances(proxy, forest, camera.position);
                    for (int f = 0; f < 3; ++f)
                        SendFrame(proxy, camera, g, outputs, sun, 0.0047f, frameIndex++, traced);
                    double forestBuild = 0.0, forestTrace = 0.0;
                    for (int f = 0; f < 16; ++f)
                    {
                        SendFrame(proxy, camera, g, outputs, sun, 0.0047f, frameIndex++, traced);
                        float build = 0, trace = 0;
                        proxy.timings(&build, &trace);
                        forestBuild += build / 16.0;
                        forestTrace += trace / 16.0;
                    }
                    result.forestInstances = forest.size();
                    result.forestBuild = std::min(result.forestBuild, forestBuild);
                    result.forestTrace = std::min(result.forestTrace, forestTrace);

                    // The best of the three rounds; the picture and the memory of the last.
                    result.cpu = std::min(result.cpu, cpuMs);
                    result.peakCpu = std::min(result.peakCpu, peakCpu);
                    result.gpu = std::min(result.gpu, sumGpu);
                    result.peakGpu = std::min(result.peakGpu, peakGpu);
                    result.build = std::min(result.build, buildMs);
                    result.trace = std::min(result.trace, traceSum / 16.0);
                    result.frames = buildFrames;
                    result.blas = statusValue(settled, "blas=");
                    result.geometry = statusValue(settled, "geometry=");
                    result.reserved = statusValue(settled, "reserved=");
                    result.memory = videoMemory();
                    result.differing = differing;
                    result.compared = compared;
                }
        for (int k = 0; k < 7; ++k)
            WritePrivateProfileStringW(L"Proxy", variants[0].keys[k][0], before[k].empty() ? nullptr : before[k].c_str(),
                                       ini.c_str());
        Sleep(1100);
        swapChain->Present(0, 0);
        for (int id : released)
        {
            proxy.releaseMesh(id);
            meshes.erase(id);
        }
        SendInstances(proxy, scene, camera.position);
        for (int f = 0; f < 4; ++f)
            SendFrame(proxy, camera, g, outputs, sun, 0.0047f, frameIndex++, kRtSignalSunVisibility);

        for (int s = 0; s < 2; ++s)
        {
            printf("      %d parts, %llu triangles: CPU ms recording the builds (the slowest frame's), GPU ms to the"
                   " meshes built (the slowest frame's), the scene build's slowest frame, frames with builds, the trace's"
                   " mean; with %zu instances the scene build's and the trace's mean; the structures' and the geometry's"
                   " MB, the pools' and build buffers' MB in all, process video memory MB; pixels unlike the first's\n",
                   sizes[s], static_cast<unsigned long long>(results[s].triangles), results[s].forestInstances);
            for (size_t v = 0; v < variantCount; ++v)
            {
                const Result& r = results[v * 2 + s];
                printf("        %-52s cpu %7.3f (%7.3f)  gpu %7.3f (%7.3f)  build %7.3f  frames %2d  trace %6.3f"
                       "  many: build %6.3f trace %6.3f  MB %6.2f %6.2f %6.2f %7.1f  %zu of %zu\n",
                       variants[v].name, r.cpu, r.peakCpu, r.gpu, r.peakGpu, r.build, r.frames, r.trace, r.forestBuild,
                       r.forestTrace, r.blas, r.geometry, r.reserved, r.memory, r.differing, r.compared);
            }
        }
        // RTX Mega Geometry's clusters, where the driver has them: the meshes built so.
        {
            const Result& r = results[results.size() - 1];
            const Result& prior = results[results.size() - 3];
            char detail[200];
            sprintf_s(detail, "%u meshes built from clusters by then, %u before them", r.clustered, prior.clustered);
            Check(r.clustered > prior.clustered || adapterDesc.VendorId != 0x10DE, "RTX Mega Geometry's clusters built",
                  detail);
        }
        // The defaults (batched, pooled, built fast, rebuilt and compacted) against mesh by
        // mesh with buffers of their own: the slowest frame's builds faster on the GPU and
        // the CPU, the structures smaller than uncompacted ones.
        for (int s = 0; s < 2; ++s)
        {
            const Result& old = results[s];
            const Result& plain = results[3 * 2 + s];
            const Result& now = results[6 * 2 + s];
            char detail[400];
            sprintf_s(detail, "%d parts, the slowest frame: %.3f ms of the GPU and %.3f of the CPU before, %.3f and %.3f now"
                              " (batched and pooled alone %.3f and %.3f); structures %.2f MB, %.2f uncompacted",
                      sizes[s], old.peakGpu, old.peakCpu, now.peakGpu, now.peakCpu, plain.peakGpu, plain.peakCpu, now.blas,
                      plain.blas);
            Check(now.peakGpu < old.peakGpu && now.peakCpu < old.peakCpu && plain.peakGpu < old.peakGpu && now.blas < plain.blas,
                  "a vessel's meshes built at once", detail);
        }
        for (size_t v = 1; v < variantCount; ++v)
            for (int s = 0; s < 2; ++s)
            {
                const Result& r = results[v * 2 + s];
                char detail[300];
                sprintf_s(detail, "%s, %d parts: %zu of %zu pixels unlike mesh by mesh", variants[v].name, sizes[s], r.differing,
                          r.compared);
                Check(r.compared > 100000 && r.differing * 1000 <= r.compared, "the same picture however built", detail);
            }
    }

    // ---------------------------------------------------------------- opacity micromaps
    {
        failures += RunOmmDriverChecks(adapter.Get(), NextToExecutable(L"OmmProbe.cso").c_str(), false);
        failures += RunOmmProxyChecks(proxy, device.Get(), context.Get(), swapChain.Get(), adapter.Get(), g, outputs, meshes, scene,
                                      camera, sun, false);
        // With the driver's capability simulated as none, in a process of its own: the capability
        // is asked once a device (REDEFINITION_OMM_CAPS).
        wchar_t self[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, self, MAX_PATH);
        std::wstring command = L"\"" + std::wstring(self) + L"\" --omm-none";
        STARTUPINFOW startup = {};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process = {};
        DWORD code = 1;
        if (CreateProcessW(self, command.data(), nullptr, nullptr, TRUE, 0, nullptr, nullptr, &startup, &process))
        {
            WaitForSingleObject(process.hProcess, 600000);
            GetExitCodeProcess(process.hProcess, &code);
            CloseHandle(process.hThread);
            CloseHandle(process.hProcess);
        }
        Check(code == 0, "opacity micromaps with the driver's capability simulated as none", code == 0 ? "the same picture, none built" : "see above");
    }

    // ---------------------------------------------------------------- the pools' chunks let go of
    {
        // A mesh of about a million triangles has chunks of its own in the pools of the
        // structures and the geometry (RtBufferPool, rtBlasPool): once it is let go of and
        // the chunks have been wholly free for a few seconds of frames (kIdleMs) they are let
        // go of too, the pools' others kept, and a mesh built after them finds its memory
        // again, the picture the one it was.
        const auto reservedMb = [&]()
        {
            const std::string text = proxy.Status();
            const size_t at = text.find(" reserved=");
            return at == std::string::npos ? 0.0 : atof(text.c_str() + at + 10);
        };
        const uint32_t signals = kRtSignalSunVisibility | kRtSignalAmbientOcclusion | kRtSignalDiffuse | kRtSignalSpecular;
        uint32_t poolFrame = 60000;
        const auto frames = [&](int count)
        {
            for (int f = 0; f < count; ++f)
                SendFrame(proxy, camera, g, outputs, sun, 0.0047f, poolFrame++, signals);
        };
        const auto picture = [&](Frame& frame)
        {
            // The same frame number each time, whose random numbers are the same.
            SendFrame(proxy, camera, g, outputs, sun, 0.0047f, 39999, kRtSignalSunVisibility | kRtSignalPrimaryCheck);
            return ReadFrame(device.Get(), context.Get(), g, outputs, frame);
        };
        const auto differing = [&](const Frame& a, const Frame& b)
        {
            size_t count = 0;
            for (size_t i = 0; i < a.depth.size(); ++i)
            {
                if (a.depth[i] <= 0.0f)
                    continue;
                const bool lit = HalfToFloat(a.visibility[i * 4]) > 0.5f;
                const bool litThen = HalfToFloat(b.visibility[i * 4]) > 0.5f;
                const float hit = a.primary[i * 4], hitThen = b.primary[i * 4];
                if (lit != litThen || std::fabs(hit - hitThen) > 1e-3f * std::max(1.0f, std::fabs(hitThen)))
                    ++count;
            }
            return count;
        };

        SendInstances(proxy, scene, camera.position);
        RenderGBuffer(context.Get(), g, camera, meshes, scene);
        frames(8);
        Frame reference;
        if (!picture(reference))
            return Fatal("reading the frame before the large mesh", E_FAIL);
        const double base = reservedMb();

        const int largeId = 90000;
        const auto withLarge = [&]()
        {
            meshes[largeId] = MakeTube(largeId, 700, 700);
            if (SendMesh(proxy, meshes[largeId]) != 1)
                return false;
            std::vector<Instance> list = scene;
            Instance large;
            large.mesh = largeId;
            large.position = { 2000.0, 1.2, 2000.0 };
            list.push_back(large);
            SendInstances(proxy, list, camera.position);
            frames(60);
            return true;
        };
        const auto withoutLarge = [&](int seconds)
        {
            proxy.releaseMesh(largeId);
            meshes.erase(largeId);
            SendInstances(proxy, scene, camera.position);
            const ULONGLONG start = GetTickCount64();
            while (GetTickCount64() - start < static_cast<ULONGLONG>(seconds) * 1000)
            {
                frames(1);
                Sleep(10);
            }
        };

        if (!withLarge())
            return Fatal("the large mesh", E_FAIL);
        const double held = reservedMb();
        withoutLarge(0);
        frames(8);
        const double soon = reservedMb();
        withoutLarge(7);
        const double gone = reservedMb();
        Frame after;
        if (!picture(after))
            return Fatal("reading the frame after the large mesh", E_FAIL);
        const size_t differentAfter = differing(after, reference);
        if (!withLarge())
            return Fatal("the large mesh again", E_FAIL);
        const double again = reservedMb();
        Frame builtAgain;
        if (!picture(builtAgain))
            return Fatal("reading the frame with the large mesh again", E_FAIL);
        const size_t differentAgain = differing(builtAgain, reference);
        withoutLarge(0);
        char detail[300];
        sprintf_s(detail, "reserved %.1f MB before, %.1f with the mesh, %.1f just after it went, %.1f seven seconds later,"
                          " %.1f with it built again; pixels unlike the first picture: %zu, %zu with it again",
                  base, held, soon, gone, again, differentAfter, differentAgain);
        Check(held - base > 25.0 && soon - base > 25.0 && gone - base < 5.0 && again - base > 25.0 && differentAfter == 0
                  && differentAgain == 0,
              "the pools' chunks let go of when idle", detail);
    }

    // ---------------------------------------------------------------- Super Resolution beside Ray Reconstruction
    if (srRunning)
    {
        // The proxy's device goes with the swapchain, and Ray Reconstruction's NGX instance
        // with it (Raytracing::ReleaseLocked), under the Super Resolution feature on this
        // device: NGX's Direct3D 12 shutdown without a device "will shut down NGX on ALL
        // devices" (nvsdk_ngx.h), and the next Super Resolution frame has to run all the same.
        char why[256] = {};
        const bool reconstructing = proxy.reconstructionAvailable(why, sizeof(why)) == 1;
        swapChain.Reset();
        Sleep(200);
        bool flat = false;
        const int after = superResolution(100, flat);
        Check(reconstructing && after > 0 && flat,
              "Super Resolution running after Ray Reconstruction's NGX shut down on the Direct3D 12 device",
              std::string(reconstructing ? "" : "Ray Reconstruction was not running: ") + srText);
        proxy.event(3, nullptr);
    }

    printf(failures == 0 ? "all raytracing checks passed\n" : "%d raytracing check(s) failed\n", failures);
    return failures == 0 ? 0 : 1;
}
