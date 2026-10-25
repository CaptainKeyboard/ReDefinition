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
    // The raytracing's markers of an extra frame (kRtFrameExtra, the ocean's), apart.
    inline constexpr int kProfRtExtraOffset = 8;

    // The timestamp queries of the last few frames on Unity's device, and the last complete frame's result.
    class GpuProfiler
    {
    public:
        // The one instance, never destroyed.
        static GpuProfiler& Get();

        // Unity's device and immediate context from the swapchain proxy; the queries are made again for them.
        void Attach(const void* owner, ID3D11Device* device, ID3D11DeviceContext* context);
        // Lets go of the device and its queries where owner is the one attached; otherwise nothing.
        void Detach(const void* owner);

        // Switched on or off by the mod; logged where it changes.
        void SetEnabled(bool enabled);
        // Whether the mod has it on; markers and frames are only taken while it is.
        bool Enabled() const { return enabled.load(); }

        // On Unity's render thread, from the render event (eventId in the profiler's range).
        void OnMarker(int id);

        // On the thread that presents, before the proxy's own work on Unity's context:
        // the frame's last marker (id 1), its disjoint query ended, the next begun with id 0.
        void OnPresent();

        // The label a marker's annotation carries, set by the mod from any thread.
        void SetLabel(int id, const wchar_t* label);

        // The last complete frame: marker ids and their times in ms after the frame's
        // start (the Present before), at most capacity of them; the frame's number in
        // frameNumber. The count, or 0 before any frame completed.
        int Read(int* ids, float* ms, int capacity, int* frameNumber);

    private:
        // Only Get makes it.
        GpuProfiler() = default;

        // Frames in flight before one is dropped unanswered, and the markers a frame holds at most.
        static constexpr int kFrames = 6;
        static constexpr int kMarkersPerFrame = 2048;

        // One frame's queries: the disjoint query around it, a timestamp per marker with the marker's id, how
        // many are used, whether it waits to be read, and whether markers were dropped.
        struct Frame
        {
            // In the order of the comment above.
            Microsoft::WRL::ComPtr<ID3D11Query> disjoint;
            std::vector<Microsoft::WRL::ComPtr<ID3D11Query>> stamps;
            std::vector<int> ids;
            int used = 0;
            bool pending = false;
            bool overflowed = false;
        };

        // Every frame's queries made on the device once; a failure is logged and stands until the next Attach.
        bool EnsureQueries();
        void Collect();
        // The next frame's disjoint query begun, with its first timestamp (id 0); one still unanswered is dropped.
        void BeginFrame();

        // Whether the mod has it on.
        std::atomic<bool> enabled{ false };
        // Under this mutex: the owner, Unity's device, context and annotation interface, the frames, whether their
        // queries were made or failed, the frame being recorded (-1 none), and the frames read so far.
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

        // The marker labels by id, set from any thread.
        std::mutex labelMutex;
        std::vector<std::wstring> labels;

        // The last complete frame for Read: ids, milliseconds after its start, its number; and the overflow lines
        // logged, at most three.
        std::mutex resultMutex;
        std::vector<int> resultIds;
        std::vector<float> resultMs;
        int resultFrame = 0;
        int overflowsLogged = 0;
    };
}
