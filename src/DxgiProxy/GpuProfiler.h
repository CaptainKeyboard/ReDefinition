#pragma once

#include <d3d11_1.h>
#include <wrl/client.h>

#include <atomic>
#include <mutex>
#include <string>
#include <vector>

// Where the GPU's time goes inside Unity's frame (the mod's GpuProfiler): the mod
// raises markers through Unity's render thread -- from command buffers on every
// camera and light event, between the command buffers other mods hang there, and
// around the scripts that draw -- and each marker ends a D3D11 timestamp query on
// Unity's immediate context, in the order the GPU meets them. A frame runs from
// one Present to the next, in a disjoint query of its own, so the ticks are only
// used where the GPU's clock held steady. Frames are read a few Presents later,
// without waiting, and the last complete one is handed to the mod (KspProfRead).
//
// Each marker is also a D3D11 user annotation marker with the mod's label for the
// work before it, so a RenderDoc capture shows the same names.
namespace redefinition
{
    // The proxy's own markers inside the raytracing's and Ray Reconstruction's events,
    // named by the mod (GpuProfiler.cs, NativeLabels): each ends the segment of the
    // D3D11 work, or the wait for D3D12, before it.
    inline constexpr int kProfNativeBase = 1000000;
    inline constexpr int kProfRtWaitShared = kProfNativeBase + 0;
    inline constexpr int kProfRtInputs = kProfNativeBase + 1;
    inline constexpr int kProfRtTrace = kProfNativeBase + 2;
    inline constexpr int kProfRtOutputs = kProfNativeBase + 3;
    inline constexpr int kProfRrWaitShared = kProfNativeBase + 4;
    inline constexpr int kProfRrInputs = kProfNativeBase + 5;
    inline constexpr int kProfRrEvaluate = kProfNativeBase + 6;
    inline constexpr int kProfRrOutput = kProfNativeBase + 7;

    class GpuProfiler
    {
    public:
        static GpuProfiler& Get();

        void Attach(const void* owner, ID3D11Device* device, ID3D11DeviceContext* context);
        void Detach(const void* owner);

        void SetEnabled(bool enabled);
        bool Enabled() const { return enabled.load(); }

        // On Unity's render thread, from the render event (eventId in the profiler's range).
        void OnMarker(int id);

        // On the thread that presents, before the proxy's own work on Unity's context:
        // the frame's last marker (id 0), its disjoint query ended, the next begun.
        void OnPresent();

        // The label a marker's annotation carries, set by the mod from any thread.
        void SetLabel(int id, const wchar_t* label);

        // The last complete frame: marker ids and their times in ms after the frame's
        // start (the Present before), at most capacity of them; the frame's number in
        // frameNumber. The count, or 0 before any frame completed.
        int Read(int* ids, float* ms, int capacity, int* frameNumber);

    private:
        GpuProfiler() = default;

        static constexpr int kFrames = 6;
        static constexpr int kMarkersPerFrame = 2048;

        struct Frame
        {
            Microsoft::WRL::ComPtr<ID3D11Query> disjoint;
            std::vector<Microsoft::WRL::ComPtr<ID3D11Query>> stamps;
            std::vector<int> ids;
            int used = 0;
            bool pending = false;
            bool overflowed = false;
        };

        bool EnsureQueries();
        void Collect();
        void BeginFrame();

        std::atomic<bool> enabled{ false };
        std::mutex mutex;
        const void* owner = nullptr;
        Microsoft::WRL::ComPtr<ID3D11Device> device;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
        Microsoft::WRL::ComPtr<ID3DUserDefinedAnnotation> annotation;
        Frame frames[kFrames];
        bool queriesMade = false;
        bool queriesFailed = false;
        int current = -1;
        int frameCounter = 0;

        std::mutex labelMutex;
        std::vector<std::wstring> labels;

        std::mutex resultMutex;
        std::vector<int> resultIds;
        std::vector<float> resultMs;
        int resultFrame = 0;
        int overflowsLogged = 0;
    };
}
