#include "GpuProfiler.h"

#include "Log.h"

#include <string>

namespace redefinition
{
    GpuProfiler& GpuProfiler::Get()
    {
        // Never destroyed, as FrameGeneration::Get: it holds Direct3D 11 queries and the
        // device, and releasing Direct3D objects while the DLL unloads at the process's
        // end (under the loader lock) cut the process short. The process's end takes
        // them back.
        static GpuProfiler* instance = new GpuProfiler();
        return *instance;
    }

    void GpuProfiler::Attach(const void* newOwner, ID3D11Device* newDevice, ID3D11DeviceContext* newContext)
    {
        std::lock_guard<std::mutex> lock(mutex);
        owner = newOwner;
        device = newDevice;
        context = newContext;
        annotation.Reset();
        if (context)
            context.As(&annotation);
        for (Frame& frame : frames)
            frame = Frame();
        queriesMade = queriesFailed = false;
        current = -1;
        frameOpen.store(false);
    }

    void GpuProfiler::Detach(const void* oldOwner)
    {
        std::lock_guard<std::mutex> lock(mutex);
        if (owner != oldOwner)
            return;
        owner = nullptr;
        for (Frame& frame : frames)
            frame = Frame();
        annotation.Reset();
        context.Reset();
        device.Reset();
        queriesMade = false;
        current = -1;
        frameOpen.store(false);
    }

    void GpuProfiler::SetEnabled(bool on)
    {
        const bool was = enabled.exchange(on);
        if (was != on)
            LogLine(std::string("GPU profiler ") + (on ? "on" : "off"));
    }

    bool GpuProfiler::EnsureQueries()
    {
        if (queriesMade)
            return true;
        if (queriesFailed || !device)
            return false;
        D3D11_QUERY_DESC disjointDesc = { D3D11_QUERY_TIMESTAMP_DISJOINT, 0 };
        D3D11_QUERY_DESC stampDesc = { D3D11_QUERY_TIMESTAMP, 0 };
        for (Frame& frame : frames)
        {
            frame = Frame();
            HRESULT hr = device->CreateQuery(&disjointDesc, &frame.disjoint);
            frame.stamps.resize(kMarkersPerFrame);
            frame.ids.resize(kMarkersPerFrame);
            for (int i = 0; SUCCEEDED(hr) && i < kMarkersPerFrame; ++i)
                hr = device->CreateQuery(&stampDesc, &frame.stamps[i]);
            if (FAILED(hr))
            {
                queriesFailed = true;
                LogLine("GPU profiler: the timestamp queries could not be made (" + std::to_string(hr) + ")");
                return false;
            }
        }
        queriesMade = true;
        return true;
    }

    // The oldest pending frames whose disjoint query has its answer: their stamps are
    // read and the newest of them kept. Never waits: a frame not done yet is asked again
    // at the next Present.
    void GpuProfiler::Collect()
    {
        for (int step = 1; step <= kFrames; ++step)
        {
            Frame& frame = frames[(current + step) % kFrames];
            if (!frame.pending)
                continue;
            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint = {};
            if (context->GetData(frame.disjoint.Get(), &disjoint, sizeof(disjoint), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK)
                return;
            frame.pending = false;
            if (disjoint.Disjoint || disjoint.Frequency == 0 || frame.used < 2)
                continue;
            std::vector<UINT64> ticks(frame.used);
            bool all = true;
            for (int i = 0; i < frame.used && all; ++i)
                all = context->GetData(frame.stamps[i].Get(), &ticks[i], sizeof(UINT64), D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK;
            if (!all)
                continue;
            const double toMs = 1000.0 / static_cast<double>(disjoint.Frequency);
            std::lock_guard<std::mutex> lock(resultMutex);
            resultIds.assign(frame.ids.begin(), frame.ids.begin() + frame.used);
            resultMs.resize(frame.used);
            for (int i = 0; i < frame.used; ++i)
                resultMs[i] = ticks[i] >= ticks[0] ? static_cast<float>(static_cast<double>(ticks[i] - ticks[0]) * toMs) : -1.0f;
            resultFrame = ++frameCounter;
            if (frame.overflowed && overflowsLogged < 3)
            {
                ++overflowsLogged;
                LogLine("GPU profiler: more than " + std::to_string(kMarkersPerFrame) + " markers in a frame; the rest dropped");
            }
        }
    }

    void GpuProfiler::BeginFrame()
    {
        current = (current + 1) % kFrames;
        frameOpen.store(true);
        Frame& frame = frames[current];
        // Still unanswered after kFrames Presents: dropped.
        frame.pending = false;
        frame.used = 0;
        frame.overflowed = false;
        context->Begin(frame.disjoint.Get());
        frame.ids[0] = 0;
        context->End(frame.stamps[0].Get());
        frame.used = 1;
    }

    void GpuProfiler::OnMarker(int id)
    {
        if (!enabled.load())
            return;
        std::lock_guard<std::mutex> lock(mutex);
        if (!context || current < 0)
            return;
        Frame& frame = frames[current];
        if (frame.used >= kMarkersPerFrame)
        {
            frame.overflowed = true;
            return;
        }
        frame.ids[frame.used] = id;
        context->End(frame.stamps[frame.used].Get());
        ++frame.used;
        if (annotation && annotation->GetStatus())
        {
            std::lock_guard<std::mutex> labelLock(labelMutex);
            if (id >= 0 && id < static_cast<int>(labels.size()) && !labels[id].empty())
                annotation->SetMarker(labels[id].c_str());
        }
    }

    void GpuProfiler::OnPresent()
    {
        // Off with no frame open: nothing to do, and no lock taken for it.
        if (!enabled.load() && !frameOpen.load())
            return;
        std::lock_guard<std::mutex> lock(mutex);
        if (!context)
            return;
        if (!enabled.load())
        {
            // Off: the open frame closed, nothing new begun.
            if (current >= 0)
            {
                Frame& frame = frames[current];
                context->End(frame.disjoint.Get());
                current = -1;
                frameOpen.store(false);
            }
            return;
        }
        if (!EnsureQueries())
            return;
        if (current >= 0)
        {
            Frame& frame = frames[current];
            if (frame.used < kMarkersPerFrame)
            {
                frame.ids[frame.used] = 1;
                context->End(frame.stamps[frame.used].Get());
                ++frame.used;
            }
            context->End(frame.disjoint.Get());
            frame.pending = true;
            Collect();
        }
        BeginFrame();
    }

    void GpuProfiler::SetLabel(int id, const wchar_t* label)
    {
        if (id < 0 || id > 1 << 20 || label == nullptr)
            return;
        std::lock_guard<std::mutex> lock(labelMutex);
        if (static_cast<int>(labels.size()) <= id)
            labels.resize(id + 1);
        labels[id] = label;
    }

    int GpuProfiler::Read(int* ids, float* ms, int capacity, int* frameNumber)
    {
        std::lock_guard<std::mutex> lock(resultMutex);
        if (frameNumber != nullptr)
            *frameNumber = resultFrame;
        const int count = static_cast<int>(resultIds.size()) < capacity ? static_cast<int>(resultIds.size()) : capacity;
        for (int i = 0; i < count; ++i)
        {
            if (ids != nullptr) ids[i] = resultIds[i];
            if (ms != nullptr) ms[i] = resultMs[i];
        }
        return count;
    }
}
