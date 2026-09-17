#include "D3d12Compute.h"

#include "D3d12Util.h"
#include "Log.h"

#include <d3dcompiler.h>

#include <algorithm>
#include <cstdio>
#include <cstring>

using Microsoft::WRL::ComPtr;

namespace ksp
{
    namespace
    {
        // A shared copy not used for this long is released.
        constexpr ULONGLONG kUnusedMs = 10000;

        constexpr UINT kDescriptors = 2 * kD3d12MaxTextures;
        // Constant buffers are sized in multiples of 256 bytes.
        constexpr UINT64 kConstantBufferSize = 256;

        // The format of the shared copy: the typed format of a typeless one, and the
        // linear twin of an sRGB one, which can be bound for unordered access.
        // CopyResource copies between formats of one group, so the bytes arrive as
        // they are.
        DXGI_FORMAT ShareFormat(DXGI_FORMAT format)
        {
            const DXGI_FORMAT typed = TypedFormat(format);
            switch (typed)
            {
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return DXGI_FORMAT_R8G8B8A8_UNORM;
            case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return DXGI_FORMAT_B8G8R8A8_UNORM;
            default: return typed;
            }
        }

        D3D12_SHADER_RESOURCE_VIEW_DESC SrvDesc(DXGI_FORMAT format)
        {
            D3D12_SHADER_RESOURCE_VIEW_DESC desc = {};
            desc.Format = format;
            desc.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D;
            desc.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            desc.Texture2D.MipLevels = 1;
            return desc;
        }

        D3D12_UNORDERED_ACCESS_VIEW_DESC UavDesc(DXGI_FORMAT format)
        {
            D3D12_UNORDERED_ACCESS_VIEW_DESC desc = {};
            desc.Format = format;
            desc.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
            return desc;
        }

        // A fence made on Direct3D 12 and opened on Direct3D 11.
        HRESULT SharedFence(ID3D11Device5* device11, ID3D12Device* device12, ComPtr<ID3D12Fence>& fence12,
                            ComPtr<ID3D11Fence>& fence11)
        {
            HRESULT hr = device12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence12));
            HANDLE handle = nullptr;
            if (SUCCEEDED(hr))
                hr = device12->CreateSharedHandle(fence12.Get(), nullptr, GENERIC_ALL, nullptr, &handle);
            if (SUCCEEDED(hr))
            {
                hr = device11->OpenSharedFence(handle, IID_PPV_ARGS(&fence11));
                CloseHandle(handle);
            }
            return hr;
        }

        int HighestFeatureLevel(ID3D12Device* device)
        {
            const D3D_FEATURE_LEVEL levels[] = {
                D3D_FEATURE_LEVEL_12_2, D3D_FEATURE_LEVEL_12_1, D3D_FEATURE_LEVEL_12_0,
                D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0,
            };
            D3D12_FEATURE_DATA_FEATURE_LEVELS query = {};
            query.NumFeatureLevels = static_cast<UINT>(sizeof(levels) / sizeof(levels[0]));
            query.pFeatureLevelsRequested = levels;
            if (FAILED(device->CheckFeatureSupport(D3D12_FEATURE_FEATURE_LEVELS, &query, sizeof(query))))
                return 0;
            return static_cast<int>(query.MaxSupportedFeatureLevel);
        }

        // "If the shader model version requested is higher than the version the
        // runtime understands, CheckFeatureSupport returns E_INVALIDARG": asked from
        // the highest down.
        int HighestShaderModel(ID3D12Device* device)
        {
            const D3D_SHADER_MODEL models[] = {
                D3D_SHADER_MODEL_6_7, D3D_SHADER_MODEL_6_6, D3D_SHADER_MODEL_6_5, D3D_SHADER_MODEL_6_4,
                D3D_SHADER_MODEL_6_3, D3D_SHADER_MODEL_6_2, D3D_SHADER_MODEL_6_1, D3D_SHADER_MODEL_6_0,
            };
            for (D3D_SHADER_MODEL model : models)
            {
                D3D12_FEATURE_DATA_SHADER_MODEL query = { model };
                if (SUCCEEDED(device->CheckFeatureSupport(D3D12_FEATURE_SHADER_MODEL, &query, sizeof(query))))
                    return static_cast<int>(query.HighestShaderModel);
            }
            return static_cast<int>(D3D_SHADER_MODEL_5_1);
        }
    }

    namespace
    {
        struct Compiler
        {
            pD3DCompile compile = nullptr;
            HRESULT(WINAPI* compileFromFile)(LPCWSTR, const D3D_SHADER_MACRO*, ID3DInclude*, LPCSTR, LPCSTR, UINT,
                                             UINT, ID3DBlob**, ID3DBlob**) = nullptr;
        };

        // Loaded once, from System32 only, and never freed.
        const Compiler& LoadCompiler()
        {
            static const Compiler compiler = []
            {
                Compiler loaded;
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

        void CopyText(ID3DBlob* blob, char* buffer, int size)
        {
            if (buffer == nullptr || size <= 0)
                return;
            buffer[0] = '\0';
            if (blob == nullptr || blob->GetBufferSize() == 0)
                return;
            const size_t length = strnlen(static_cast<const char*>(blob->GetBufferPointer()), blob->GetBufferSize());
            _snprintf_s(buffer, static_cast<size_t>(size), _TRUNCATE, "%.*s", static_cast<int>(length),
                        static_cast<const char*>(blob->GetBufferPointer()));
        }
    }

    int CompileComputeShader(const char* source, int sourceLength, const wchar_t* path, const char* entry,
                             uint8_t* bytecode, int capacity, int& written, char* messages, int messagesSize)
    {
        written = 0;
        if (messages != nullptr && messagesSize > 0)
            messages[0] = '\0';
        const Compiler& compiler = LoadCompiler();
        if (compiler.compile == nullptr || compiler.compileFromFile == nullptr)
        {
            if (messages != nullptr && messagesSize > 0)
                strncpy_s(messages, static_cast<size_t>(messagesSize), "d3dcompiler_47.dll could not be loaded from System32",
                          _TRUNCATE);
            return -2;
        }

        const char* entryPoint = entry != nullptr && entry[0] != '\0' ? entry : "main";
        const UINT flags = D3DCOMPILE_OPTIMIZATION_LEVEL3;
        ComPtr<ID3DBlob> code;
        ComPtr<ID3DBlob> output;
        HRESULT hr = E_INVALIDARG;
        if (source != nullptr && sourceLength > 0)
            hr = compiler.compile(source, static_cast<SIZE_T>(sourceLength), "source", nullptr,
                                  D3D_COMPILE_STANDARD_FILE_INCLUDE, entryPoint, "cs_5_0", flags, 0, &code, &output);
        else if (path != nullptr && path[0] != L'\0')
            hr = compiler.compileFromFile(path, nullptr, D3D_COMPILE_STANDARD_FILE_INCLUDE, entryPoint, "cs_5_0", flags,
                                          0, &code, &output);

        CopyText(output.Get(), messages, messagesSize);
        if (FAILED(hr) || code == nullptr)
        {
            if (output == nullptr && messages != nullptr && messagesSize > 0)
                _snprintf_s(messages, static_cast<size_t>(messagesSize), _TRUNCATE,
                            source == nullptr && (path == nullptr || path[0] == L'\0')
                                ? "no source and no file given"
                                : "the compiler failed without a message (%s)",
                            Hr(hr).c_str());
            return 0;
        }

        written = static_cast<int>(code->GetBufferSize());
        if (bytecode == nullptr || capacity < written)
            return -1;
        memcpy(bytecode, code->GetBufferPointer(), code->GetBufferSize());
        return 1;
    }

    // Never destroyed, like AmdUpscaler: at process exit the devices it holds may be
    // gone.
    D3d12Compute& D3d12Compute::Get()
    {
        static D3d12Compute* instance = new D3d12Compute();
        return *instance;
    }

    void D3d12Compute::Attach(const void* newOwner, ID3D11Device5* newDevice11, ID3D11DeviceContext4* newContext11,
                              ID3D12Device* newDevice12, ID3D12CommandQueue* newQueue)
    {
        std::lock_guard<std::mutex> lock(mutex);
        ReleaseLocked();
        owner = newOwner;
        device11 = newDevice11;
        context11 = newContext11;
        device12 = newDevice12;
        queue = newQueue;

        Capabilities found;
        if (device12 != nullptr)
        {
            found.featureLevel = HighestFeatureLevel(device12.Get());
            found.shaderModel = HighestShaderModel(device12.Get());
            D3D12_FEATURE_DATA_D3D12_OPTIONS5 options5 = {};
            if (SUCCEEDED(device12->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &options5, sizeof(options5))))
                found.raytracingTier = static_cast<int>(options5.RaytracingTier);
            D3D12_FEATURE_DATA_D3D12_OPTIONS6 options6 = {};
            if (SUCCEEDED(device12->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS6, &options6, sizeof(options6))))
                found.variableShadingRateTier = static_cast<int>(options6.VariableShadingRateTier);
            D3D12_FEATURE_DATA_D3D12_OPTIONS7 options7 = {};
            if (SUCCEEDED(device12->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS7, &options7, sizeof(options7))))
                found.meshShaderTier = static_cast<int>(options7.MeshShaderTier);
        }
        {
            std::lock_guard<std::mutex> status(statusLock);
            capabilities = found;
        }
        attached = device11 != nullptr && context11 != nullptr && device12 != nullptr && queue != nullptr;
        if (attached)
        {
            char line[160] = {};
            sprintf_s(line, "Direct3D 12 for mods: feature level 0x%X, shader model 0x%X, raytracing tier %d,"
                            " mesh shader tier %d, variable rate shading tier %d",
                      static_cast<unsigned>(found.featureLevel), static_cast<unsigned>(found.shaderModel),
                      found.raytracingTier, found.meshShaderTier, found.variableShadingRateTier);
            LogLine(line);
        }
    }

    void D3d12Compute::Detach(const void* detaching)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (detaching != owner)
            return;
        ReleaseLocked();
        queue.Reset();
        device12.Reset();
        context11.Reset();
        device11.Reset();
        owner = nullptr;
        attached = false;
    }

    bool D3d12Compute::Available(Capabilities& out) const
    {
        std::lock_guard<std::mutex> status(statusLock);
        out = capabilities;
        return attached.load();
    }

    void D3d12Compute::OnCreatePass(const void* data)
    {
        const D3d12CreatePacket* packet = static_cast<const D3d12CreatePacket*>(data);
        if (packet == nullptr || packet->size != sizeof(D3d12CreatePacket) || packet->magic != kD3d12CreateMagic)
        {
            SetProblem("a compute pass packet rejected: the mod and the proxy disagree on its layout");
            return;
        }

        std::lock_guard<std::mutex> lock(mutex);
        Pass pass;
        char name[sizeof(packet->name) + 1] = {};
        memcpy(name, packet->name, sizeof(packet->name));
        pass.name = name;
        if (packet->bytecode == nullptr || packet->bytecodeSize == 0)
        {
            SetPassStatus(packet->pass, -1, "no shader bytecode");
            return;
        }
        const uint8_t* bytes = static_cast<const uint8_t*>(packet->bytecode);
        pass.bytecode.assign(bytes, bytes + packet->bytecodeSize);

        Pass& stored = passes[packet->pass] = std::move(pass);
        if (!attached)
        {
            SetPassStatus(packet->pass, 0, "waiting for the proxy's Direct3D 12 device");
            return;
        }
        if (BuildLocked(stored))
            SetPassStatus(packet->pass, 1, "built");
    }

    void D3d12Compute::OnDestroyPass(const void* data)
    {
        const D3d12HandlePacket* packet = static_cast<const D3d12HandlePacket*>(data);
        if (packet == nullptr || packet->size != sizeof(D3d12HandlePacket) || packet->magic != kD3d12HandleMagic)
            return;

        std::lock_guard<std::mutex> lock(mutex);
        auto found = passes.find(packet->pass);
        if (found != passes.end())
        {
            // Its results a frame late are not wanted any more; its list may still run.
            found->second.pendingCopies.clear();
            passes.erase(found);
        }
        std::lock_guard<std::mutex> status(statusLock);
        passStates.erase(packet->pass);
    }

    void D3d12Compute::OnReleaseTexture(const void* data)
    {
        const D3d12HandlePacket* packet = static_cast<const D3d12HandlePacket*>(data);
        if (packet == nullptr || packet->size != sizeof(D3d12HandlePacket) || packet->magic != kD3d12HandleMagic)
            return;

        std::lock_guard<std::mutex> lock(mutex);
        // A copy back into the texture the mod lets go is dropped; the slots hold the
        // shared copy's Direct3D 12 side until their lists are done.
        for (auto& entry : passes)
        {
            auto& copies = entry.second.pendingCopies;
            copies.erase(std::remove_if(copies.begin(), copies.end(),
                                        [&](const auto& copy) { return copy.first.Get() == packet->texture; }),
                         copies.end());
        }
        textures.erase(packet->texture);
    }

    void D3d12Compute::OnDispatch(const void* data)
    {
        const D3d12DispatchPacket* packet = static_cast<const D3d12DispatchPacket*>(data);
        if (packet == nullptr || packet->size != sizeof(D3d12DispatchPacket) || packet->magic != kD3d12DispatchMagic)
        {
            SetProblem("a dispatch packet rejected: the mod and the proxy disagree on its layout");
            return;
        }

        std::lock_guard<std::mutex> lock(mutex);
        auto found = passes.find(packet->pass);
        if (found == passes.end())
        {
            SetPassStatus(packet->pass, -1, "dispatched, but no pass of that handle was created");
            return;
        }
        Pass& pass = found->second;
        if (!attached)
        {
            SetPassStatus(packet->pass, 0, "waiting for the proxy's Direct3D 12 device");
            return;
        }
        if (packet->readCount > kD3d12MaxTextures || packet->writeCount > kD3d12MaxTextures
            || packet->constantsSize > kD3d12MaxConstants || packet->groupsX == 0 || packet->groupsY == 0
            || packet->groupsZ == 0)
        {
            SetPassStatus(packet->pass, -1, "a dispatch out of range: at most 8 read and 8 write textures, 256 bytes"
                                            " of constants, and at least one thread group in each dimension");
            return;
        }
        for (uint32_t w = 0; w < packet->writeCount; ++w)
        {
            for (uint32_t r = 0; r < packet->readCount; ++r)
                if (packet->write[w] != nullptr && packet->write[w] == packet->read[r])
                {
                    SetPassStatus(packet->pass, -1, "a texture is both read and written in one dispatch");
                    return;
                }
            for (uint32_t other = 0; other < w; ++other)
                if (packet->write[w] != nullptr && packet->write[w] == packet->write[other])
                {
                    SetPassStatus(packet->pass, -1, "a texture is written twice in one dispatch");
                    return;
                }
        }

        if (pass.pipeline == nullptr || pass.builtOn != device12.Get())
        {
            if (!BuildLocked(pass))
                return;
            SetPassStatus(packet->pass, 1, "built");
        }
        if (!EnsureQueueLocked())
            return;

        // The last dispatch's results, a frame late, before this one's inputs -- this
        // pass's, and those of every pass whose results go into a texture this dispatch
        // reads or writes, which it would otherwise read before them, or overwrite in the
        // shared copy before they are copied back.
        CompletePendingLocked(pass);
        for (auto& other : passes)
        {
            if (&other.second == &pass || other.second.pendingCopies.empty())
                continue;
            bool touched = false;
            for (const auto& copy : other.second.pendingCopies)
                for (uint32_t i = 0; i < kD3d12MaxTextures && !touched; ++i)
                    touched = (i < packet->readCount && packet->read[i] == copy.first.Get())
                              || (i < packet->writeCount && packet->write[i] == copy.first.Get());
            if (touched)
                CompletePendingLocked(other.second);
        }
        EvictLocked();

        Shared* read[kD3d12MaxTextures] = {};
        Shared* write[kD3d12MaxTextures] = {};
        std::string problem;
        for (uint32_t i = 0; i < packet->readCount; ++i)
            if ((read[i] = SharedForLocked(packet->read[i], false, problem)) == nullptr)
            {
                SetPassStatus(packet->pass, -1, "read texture t" + std::to_string(i) + ": " + problem);
                return;
            }
        for (uint32_t i = 0; i < packet->writeCount; ++i)
            if ((write[i] = SharedForLocked(packet->write[i], true, problem)) == nullptr)
            {
                SetPassStatus(packet->pass, -1, "write texture u" + std::to_string(i) + ": " + problem);
                return;
            }

        // Direct3D 11: this frame's textures into the shared copies -- the write ones
        // too, so a pass may change part of one -- once the queue is past every list
        // that used them, and a point in its stream after them for the queue to wait for.
        UINT64 usedUntil = 0;
        for (uint32_t i = 0; i < packet->readCount; ++i)
            usedUntil = std::max(usedUntil, read[i]->lastFence);
        for (uint32_t i = 0; i < packet->writeCount; ++i)
            usedUntil = std::max(usedUntil, write[i]->lastFence);
        if (usedUntil != 0)
            context11->Wait(fence11.Get(), usedUntil);
        for (uint32_t i = 0; i < packet->readCount; ++i)
            context11->CopyResource(read[i]->texture.d3d11.Get(), static_cast<ID3D11Resource*>(packet->read[i]));
        for (uint32_t i = 0; i < packet->writeCount; ++i)
            context11->CopyResource(write[i]->texture.d3d11.Get(), static_cast<ID3D11Resource*>(packet->write[i]));
        const UINT64 inputsCopied = ++copiedValue;
        context11->Signal(copied11.Get(), inputsCopied);
        context11->Flush();
        queue->Wait(copied12.Get(), inputsCopied);

        Slot& slot = slots[slotIndex];
        slotIndex = (slotIndex + 1) % kSlots;
        WaitForSlotLocked(slot);
        slot.held.clear();
        HRESULT hr = slot.allocator->Reset();
        if (SUCCEEDED(hr))
            hr = list->Reset(slot.allocator.Get(), nullptr);
        if (FAILED(hr))
        {
            SetPassStatus(packet->pass, -1, "the command list could not be reset (" + Hr(hr) + ")");
            return;
        }

        // The descriptors: t0-t7 then u0-u7, a null view where a slot is not bound.
        const UINT increment = device12->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        D3D12_CPU_DESCRIPTOR_HANDLE cpu = slot.heap->GetCPUDescriptorHandleForHeapStart();
        for (UINT i = 0; i < kD3d12MaxTextures; ++i)
        {
            const bool bound = i < packet->readCount;
            const D3D12_SHADER_RESOURCE_VIEW_DESC desc =
                SrvDesc(bound ? read[i]->texture.format : DXGI_FORMAT_R8G8B8A8_UNORM);
            device12->CreateShaderResourceView(bound ? read[i]->texture.d3d12.Get() : nullptr, &desc, cpu);
            cpu.ptr += increment;
        }
        for (UINT i = 0; i < kD3d12MaxTextures; ++i)
        {
            const bool bound = i < packet->writeCount;
            const D3D12_UNORDERED_ACCESS_VIEW_DESC desc =
                UavDesc(bound ? write[i]->texture.format : DXGI_FORMAT_R8G8B8A8_UNORM);
            device12->CreateUnorderedAccessView(bound ? write[i]->texture.d3d12.Get() : nullptr, nullptr, &desc, cpu);
            cpu.ptr += increment;
        }
        memset(slot.mapped, 0, static_cast<size_t>(kConstantBufferSize));
        memcpy(slot.mapped, packet->constants, packet->constantsSize);

        // Shared resources are in COMMON whenever Direct3D 11 touches them.
        const D3D12_RESOURCE_STATES readState = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE;
        std::vector<D3D12_RESOURCE_BARRIER> into;
        std::vector<D3D12_RESOURCE_BARRIER> back;
        for (uint32_t i = 0; i < packet->readCount; ++i)
        {
            bool before = false;
            for (uint32_t j = 0; j < i; ++j)
                before = before || read[j] == read[i];
            if (before)
                continue;
            into.push_back(TransitionBarrier(read[i]->texture.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON, readState));
            back.push_back(TransitionBarrier(read[i]->texture.d3d12.Get(), readState, D3D12_RESOURCE_STATE_COMMON));
            slot.held.push_back(read[i]->texture.d3d12);
        }
        for (uint32_t i = 0; i < packet->writeCount; ++i)
        {
            into.push_back(TransitionBarrier(write[i]->texture.d3d12.Get(), D3D12_RESOURCE_STATE_COMMON,
                                             D3D12_RESOURCE_STATE_UNORDERED_ACCESS));
            back.push_back(TransitionBarrier(write[i]->texture.d3d12.Get(), D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
                                             D3D12_RESOURCE_STATE_COMMON));
            slot.held.push_back(write[i]->texture.d3d12);
        }
        if (!into.empty())
            list->ResourceBarrier(static_cast<UINT>(into.size()), into.data());

        ID3D12DescriptorHeap* heaps[] = { slot.heap.Get() };
        list->SetDescriptorHeaps(1, heaps);
        list->SetComputeRootSignature(rootSignature.Get());
        list->SetPipelineState(pass.pipeline.Get());
        D3D12_GPU_DESCRIPTOR_HANDLE gpu = slot.heap->GetGPUDescriptorHandleForHeapStart();
        list->SetComputeRootDescriptorTable(0, gpu);
        gpu.ptr += static_cast<UINT64>(kD3d12MaxTextures) * increment;
        list->SetComputeRootDescriptorTable(1, gpu);
        list->SetComputeRootConstantBufferView(2, slot.constants->GetGPUVirtualAddress());
        list->Dispatch(packet->groupsX, packet->groupsY, packet->groupsZ);

        if (!back.empty())
            list->ResourceBarrier(static_cast<UINT>(back.size()), back.data());
        hr = list->Close();
        if (FAILED(hr))
        {
            SetPassStatus(packet->pass, -1, "the command list could not be closed (" + Hr(hr) + ")");
            return;
        }
        ID3D12CommandList* lists[] = { list.Get() };
        queue->ExecuteCommandLists(1, lists);
        const UINT64 finished = ++fenceValue;
        queue->Signal(fence12.Get(), finished);
        slot.done = finished;
        for (uint32_t i = 0; i < packet->readCount; ++i)
            read[i]->lastFence = finished;
        for (uint32_t i = 0; i < packet->writeCount; ++i)
            write[i]->lastFence = finished;

        if ((packet->flags & kD3d12DispatchNextFrame) != 0)
        {
            // Copied back at the pass's next dispatch; Unity's texture and the shared
            // copy are held until then.
            pass.pendingFence = finished;
            pass.pendingCopies.clear();
            for (uint32_t i = 0; i < packet->writeCount; ++i)
                pass.pendingCopies.emplace_back(static_cast<ID3D11Resource*>(packet->write[i]), write[i]->texture.d3d11);
        }
        else
        {
            // Direct3D 11: the results into Unity's textures once the queue is past them.
            context11->Wait(fence11.Get(), finished);
            for (uint32_t i = 0; i < packet->writeCount; ++i)
                context11->CopyResource(static_cast<ID3D11Resource*>(packet->write[i]), write[i]->texture.d3d11.Get());
        }

        std::lock_guard<std::mutex> status(statusLock);
        ++dispatched;
    }

    int D3d12Compute::PassStatus(int pass, char* buffer, int size) const
    {
        std::lock_guard<std::mutex> status(statusLock);
        auto found = passStates.find(pass);
        const int state = found != passStates.end() ? found->second.state : -1;
        const std::string text = found != passStates.end() ? found->second.text : "no pass of that handle";
        // Cut to the buffer: sprintf_s would end the process on a longer text.
        if (buffer != nullptr && size > 0)
            strncpy_s(buffer, static_cast<size_t>(size), text.c_str(), _TRUNCATE);
        return state;
    }

    int D3d12Compute::Status(char* buffer, int size) const
    {
        if (buffer == nullptr || size <= 0)
            return attached.load() ? 1 : 0;
        std::lock_guard<std::mutex> status(statusLock);
        const int written = _snprintf_s(buffer, static_cast<size_t>(size), _TRUNCATE,
                                        "device=%s passes=%u dispatches=%llu%s%s",
                                        attached.load() ? "yes" : "no",
                                        static_cast<unsigned>(passStates.size()),
                                        static_cast<unsigned long long>(dispatched),
                                        lastProblem.empty() ? "" : " last problem: ",
                                        lastProblem.c_str());
        return written < 0 ? static_cast<int>(strnlen(buffer, static_cast<size_t>(size))) : written;
    }

    // The root signature every pass is built against: t0-t7, u0-u7, b0, s0 and s1.
    bool D3d12Compute::EnsureRootSignatureLocked()
    {
        if (rootSignature != nullptr)
            return true;

        D3D12_DESCRIPTOR_RANGE ranges[2] = {};
        ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV;
        ranges[0].NumDescriptors = kD3d12MaxTextures;
        ranges[0].BaseShaderRegister = 0;
        ranges[0].OffsetInDescriptorsFromTableStart = D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND;
        ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV;
        ranges[1].NumDescriptors = kD3d12MaxTextures;
        ranges[1].BaseShaderRegister = 0;
        ranges[1].OffsetInDescriptorsFromTableStart = D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND;

        D3D12_ROOT_PARAMETER parameters[3] = {};
        parameters[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        parameters[0].DescriptorTable.NumDescriptorRanges = 1;
        parameters[0].DescriptorTable.pDescriptorRanges = &ranges[0];
        parameters[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
        parameters[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        parameters[1].DescriptorTable.NumDescriptorRanges = 1;
        parameters[1].DescriptorTable.pDescriptorRanges = &ranges[1];
        parameters[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
        parameters[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV;
        parameters[2].Descriptor.ShaderRegister = 0;
        parameters[2].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;

        D3D12_STATIC_SAMPLER_DESC samplers[2] = {};
        for (UINT i = 0; i < 2; ++i)
        {
            samplers[i].Filter = i == 0 ? D3D12_FILTER_MIN_MAG_MIP_POINT : D3D12_FILTER_MIN_MAG_MIP_LINEAR;
            samplers[i].AddressU = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
            samplers[i].AddressV = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
            samplers[i].AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
            samplers[i].MaxLOD = D3D12_FLOAT32_MAX;
            samplers[i].ShaderRegister = i;
            samplers[i].ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
        }

        D3D12_ROOT_SIGNATURE_DESC desc = {};
        desc.NumParameters = 3;
        desc.pParameters = parameters;
        desc.NumStaticSamplers = 2;
        desc.pStaticSamplers = samplers;
        desc.Flags = D3D12_ROOT_SIGNATURE_FLAG_NONE;

        ComPtr<ID3DBlob> serialized;
        ComPtr<ID3DBlob> errors;
        HRESULT hr = D3D12SerializeRootSignature(&desc, D3D_ROOT_SIGNATURE_VERSION_1, &serialized, &errors);
        if (SUCCEEDED(hr))
            hr = device12->CreateRootSignature(0, serialized->GetBufferPointer(), serialized->GetBufferSize(),
                                               IID_PPV_ARGS(&rootSignature));
        if (FAILED(hr))
        {
            std::string text = "the root signature could not be made (" + Hr(hr) + ")";
            if (errors != nullptr)
                text += ": " + std::string(static_cast<const char*>(errors->GetBufferPointer()), errors->GetBufferSize());
            SetProblem(text);
            rootSignature.Reset();
            return false;
        }
        return true;
    }

    bool D3d12Compute::BuildLocked(Pass& pass)
    {
        if (!EnsureRootSignatureLocked())
            return false;

        int handle = 0;
        for (const auto& entry : passes)
            if (&entry.second == &pass)
                handle = entry.first;

        D3D12_COMPUTE_PIPELINE_STATE_DESC desc = {};
        desc.pRootSignature = rootSignature.Get();
        desc.CS.pShaderBytecode = pass.bytecode.data();
        desc.CS.BytecodeLength = pass.bytecode.size();
        pass.pipeline.Reset();
        const HRESULT hr = device12->CreateComputePipelineState(&desc, IID_PPV_ARGS(&pass.pipeline));
        if (FAILED(hr))
        {
            pass.pipeline.Reset();
            pass.builtOn = nullptr;
            SetPassStatus(handle, -1, "'" + pass.name + "' could not be built (" + Hr(hr)
                                          + "): the bytecode is not a compute shader for this root signature -- t0-t7,"
                                            " u0-u7, b0, s0, s1 -- or unsigned DXIL");
            return false;
        }
        pass.builtOn = device12.Get();
        return true;
    }

    bool D3d12Compute::EnsureQueueLocked()
    {
        if (list != nullptr)
            return true;

        HRESULT hr = SharedFence(device11.Get(), device12.Get(), copied12, copied11);
        if (SUCCEEDED(hr))
            hr = SharedFence(device11.Get(), device12.Get(), fence12, fence11);

        D3D12_DESCRIPTOR_HEAP_DESC heapDesc = {};
        heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV;
        heapDesc.NumDescriptors = kDescriptors;
        heapDesc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;

        D3D12_HEAP_PROPERTIES upload = {};
        upload.Type = D3D12_HEAP_TYPE_UPLOAD;
        D3D12_RESOURCE_DESC bufferDesc = {};
        bufferDesc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        bufferDesc.Width = kConstantBufferSize;
        bufferDesc.Height = 1;
        bufferDesc.DepthOrArraySize = 1;
        bufferDesc.MipLevels = 1;
        bufferDesc.SampleDesc.Count = 1;
        bufferDesc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;

        for (UINT i = 0; SUCCEEDED(hr) && i < kSlots; ++i)
        {
            Slot& slot = slots[i];
            hr = device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&slot.allocator));
            if (SUCCEEDED(hr))
                hr = device12->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&slot.heap));
            if (SUCCEEDED(hr))
                hr = device12->CreateCommittedResource(&upload, D3D12_HEAP_FLAG_NONE, &bufferDesc,
                                                       D3D12_RESOURCE_STATE_GENERIC_READ, nullptr,
                                                       IID_PPV_ARGS(&slot.constants));
            if (SUCCEEDED(hr))
            {
                const D3D12_RANGE none = { 0, 0 };
                void* mapped = nullptr;
                hr = slot.constants->Map(0, &none, &mapped);
                slot.mapped = static_cast<uint8_t*>(mapped);
            }
            slot.done = 0;
            slot.held.clear();
        }
        if (SUCCEEDED(hr))
            hr = device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, slots[0].allocator.Get(), nullptr,
                                             IID_PPV_ARGS(&list));
        if (FAILED(hr))
        {
            SetProblem("the shared fence, command lists or descriptor heaps could not be made (" + Hr(hr) + ")");
            for (Slot& slot : slots)
                slot = Slot{};
            list.Reset();
            fence11.Reset();
            fence12.Reset();
            copied11.Reset();
            copied12.Reset();
            return false;
        }
        list->Close();

        if (fenceEvent == nullptr)
            fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        fenceValue = 0;
        copiedValue = 0;
        slotIndex = 0;
        return true;
    }

    D3d12Compute::Shared* D3d12Compute::SharedForLocked(void* unityTexture, bool write, std::string& problem)
    {
        ComPtr<ID3D11Texture2D> texture2d;
        if (unityTexture == nullptr
            || FAILED(static_cast<ID3D11Resource*>(unityTexture)->QueryInterface(IID_PPV_ARGS(&texture2d))))
        {
            problem = "not a 2D texture";
            return nullptr;
        }
        D3D11_TEXTURE2D_DESC desc = {};
        texture2d->GetDesc(&desc);
        if (desc.MipLevels != 1 || desc.ArraySize != 1 || desc.SampleDesc.Count != 1)
        {
            problem = "a texture with mipmaps, an array or multisampling cannot be copied into a shared texture";
            return nullptr;
        }

        const DXGI_FORMAT format = ShareFormat(desc.Format);
        D3D12_FEATURE_DATA_FORMAT_SUPPORT support = { format };
        if (FAILED(device12->CheckFeatureSupport(D3D12_FEATURE_FORMAT_SUPPORT, &support, sizeof(support)))
            || (support.Support2 & D3D12_FORMAT_SUPPORT2_UAV_TYPED_STORE) == 0
            || (!write && (support.Support1 & D3D12_FORMAT_SUPPORT1_SHADER_LOAD) == 0))
        {
            problem = "format " + std::to_string(static_cast<int>(desc.Format))
                      + " cannot be shared for unordered access";
            return nullptr;
        }

        Shared& shared = textures[unityTexture];
        shared.lastUse = GetTickCount64();
        if (shared.texture.Valid() && shared.texture.width == desc.Width && shared.texture.height == desc.Height
            && shared.texture.format == format)
            return &shared;
        if (!shared.texture.Create(device11.Get(), device12.Get(), desc.Width, desc.Height, format))
        {
            textures.erase(unityTexture);
            problem = "the texture shared with Direct3D 12 could not be made";
            return nullptr;
        }
        return &shared;
    }

    void D3d12Compute::CompletePendingLocked(Pass& pass)
    {
        if (pass.pendingCopies.empty())
            return;
        context11->Wait(fence11.Get(), pass.pendingFence);
        for (auto& copy : pass.pendingCopies)
            context11->CopyResource(copy.first.Get(), copy.second.Get());
        pass.pendingCopies.clear();
        pass.pendingFence = 0;
    }

    void D3d12Compute::WaitForSlotLocked(Slot& slot)
    {
        if (slot.done == 0 || fence12 == nullptr || fenceEvent == nullptr)
            return;
        if (fence12->GetCompletedValue() >= slot.done)
            return;
        if (SUCCEEDED(fence12->SetEventOnCompletion(slot.done, fenceEvent)))
            WaitForSingleObject(fenceEvent, INFINITE);
    }

    void D3d12Compute::WaitForAllLocked()
    {
        if (fence12 == nullptr || fenceEvent == nullptr || fenceValue == 0)
            return;
        if (fence12->GetCompletedValue() >= fenceValue)
            return;
        if (SUCCEEDED(fence12->SetEventOnCompletion(fenceValue, fenceEvent)))
            WaitForSingleObject(fenceEvent, INFINITE);
    }

    void D3d12Compute::EvictLocked()
    {
        const ULONGLONG now = GetTickCount64();
        if (now < nextEviction)
            return;
        nextEviction = now + 1000;
        for (auto it = textures.begin(); it != textures.end();)
        {
            if (now - it->second.lastUse > kUnusedMs)
                it = textures.erase(it);
            else
                ++it;
        }
    }

    // The device is going or changing: what was made on it goes, the passes keep
    // their bytecode and are built again on the next device.
    void D3d12Compute::ReleaseLocked()
    {
        WaitForAllLocked();
        for (auto& entry : passes)
        {
            entry.second.pipeline.Reset();
            entry.second.builtOn = nullptr;
            entry.second.pendingCopies.clear();
            entry.second.pendingFence = 0;
        }
        textures.clear();
        list.Reset();
        for (Slot& slot : slots)
        {
            if (slot.constants != nullptr && slot.mapped != nullptr)
                slot.constants->Unmap(0, nullptr);
            slot = Slot{};
        }
        rootSignature.Reset();
        fence11.Reset();
        fence12.Reset();
        fenceValue = 0;
        copied11.Reset();
        copied12.Reset();
        copiedValue = 0;
    }

    void D3d12Compute::SetPassStatus(int pass, int state, const std::string& text)
    {
        bool changed = false;
        {
            std::lock_guard<std::mutex> status(statusLock);
            PassState& entry = passStates[pass];
            changed = entry.state != state || entry.text != text;
            entry.state = state;
            entry.text = text;
        }
        if (changed && state < 0)
            LogLine("Direct3D 12 for mods, pass " + std::to_string(pass) + ": " + text);
    }

    void D3d12Compute::SetProblem(const std::string& text)
    {
        bool changed = false;
        {
            std::lock_guard<std::mutex> status(statusLock);
            changed = lastProblem != text;
            lastProblem = text;
        }
        if (changed)
            LogLine("Direct3D 12 for mods: " + text);
    }
}
