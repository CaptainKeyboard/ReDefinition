// Opacity Micromaps for the cut-out surfaces (Config.h, rtOpacityMicromaps), through NVIDIA's
// NVAPI (RaytracingOmm.h).
//
// What it is for. The passes meet grass and ferns as cards of triangles whose texture cuts
// them out: each hit on one of them is a candidate the shader decides by reading the material
// texture's coverage (RtCommon.hlsli, CandidateCounts). A blade of grass is mostly empty
// card, so most rays lose their time in candidates that come to nothing. An opacity micromap
// subdivides a triangle into 4^level micro-triangles and says of each whether it is
// transparent (the hit is skipped), opaque (the hit is committed) or unknown (a candidate, as
// before): "To reduce the overhead of redundant and potentially expensive any-hit shaders,
// Opacity Micromaps (OMM) are added to triangle primitives" (DirectX-Specs, Raytracing,
// Opacity micromaps).
//
// How it is done. The micromaps are baked on the CPU (RaytracingOmmBake.h) from a mesh's
// texture coordinates and the coverage of the material texture its cut-out instances use,
// on a thread of its own; the baker leaves "unknown" wherever the texels a micro-triangle
// reads are not all 0 or all 255, so the picture is the same. The structure is then built
// again through NVAPI with the micromaps attached, once the driver has been told, before
// the passes' pipelines were made, that micromaps are in use. The instances switch them
// off or on with their flags, so that the switch needs no structure built again.
//
// The decisions about the instances (RaytracingScene.cpp, BuildSceneLocked):
//  - a micromap decides opaque and transparent parts, so a cut-out instance with a micromap
//    that applies drops FORCE_NON_OPAQUE: "Ray flags and instance flags may still alter the
//    state of opaque hits, but note that any such flags are only applied after the OMM hit
//    classification has occurred" (DirectX-Specs, Raytracing); with FORCE_NON_OPAQUE the opaque
//    micro-triangles would be candidates again. The harness holds that against the driver.
//  - the sun's rays let a part of the light through a cut-out surface (rtFoliageTransmission),
//    which the shader rolls for each opaque hit (RtCommon.hlsli, SunUnoccluded); then the
//    instance keeps FORCE_NON_OPAQUE: the transparent micro-triangles are still skipped.
//  - where a micromap does not apply -- the switch is off, the measuring modes of rtCutoutTest
//    (they decide hits another way), the material textures not read, or the instance's
//    texture is not the one the mesh was baked for -- the instance sets DISABLE_OMMS: "Disable
//    OMMs for all triangles, and revert to using geometry opaque/non-opaque state instead
//    (legacy behavior)".

#include "Raytracing.h"

#include "Config.h"
#include "D3d12Util.h"
#include "Log.h"
#include "NvidiaGpu.h"
#include "RaytracingBuildFrame.h"
#include "RaytracingInternal.h"

#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <thread>

using Microsoft::WRL::ComPtr;

namespace redefinition
{
    namespace
    {
        constexpr uint64_t kFnvBasis = 14695981039346656037ull;
        constexpr uint64_t kFnvPrime = 1099511628211ull;

        uint64_t Fnv(const void* bytes, size_t size, uint64_t hash = kFnvBasis)
        {
            const uint8_t* p = static_cast<const uint8_t*>(bytes);
            for (size_t i = 0; i < size; ++i)
                hash = (hash ^ p[i]) * kFnvPrime;
            return hash;
        }

        uint64_t SetKey(uint64_t uvHash, uint64_t alphaHash, uint32_t maxLevel)
        {
            const uint64_t parts[3] = { uvHash, alphaHash, maxLevel };
            return Fnv(parts, sizeof(parts));
        }

        // The frames a set nobody holds is kept for.
        constexpr uint32_t kIdleUpdates = 300;

        UINT64 RoundUp(UINT64 value, UINT64 granularity)
        {
            return (value + granularity - 1) / granularity * granularity;
        }
    }

    void Raytracing::OmmMemory::Reset()
    {
        {
            std::lock_guard<std::mutex> lock(jobLock);
            jobs.clear();
            results.clear();
        }
        ++epoch;
        caps = -1;
        pipelines = false;
        getCaps = setOptions = getArrayPrebuild = buildArray = getPrebuildEx = buildEx = nullptr;
        live = keepNonOpaque = wanted = false;
        announcedLine.clear();
        capsWhy.clear();
        alpha.clear();
        sources.clear();
        cutoutMeshes.clear();
        sets.clear();
        byKey.clear();
        nextSet = 1;
        updates = 0;
        arrays.Reset();
        poolSetUp = false;
        wantedMeshes.clear();
        waiting.clear();
        toAttach.clear();
        metMeshes = bakes = ommEntries = uniformTriangles = builtTriangles = 0;
        bakeMs = 0.0;
        microTriangles = 0;
    }

    // ---------------------------------------------------------------- the frame's decisions

    void Raytracing::PrepareOmmLocked(const RtFramePacket& packet)
    {
        if (omm.caps < 0)
        {
            // Asked once a device. REDEFINITION_OMM_CAPS=none stands in for a driver without
            // them, for the harness.
            omm.getCaps = NvapiInterface(nvomm::kGetRaytracingCaps);
            omm.setOptions = NvapiInterface(nvomm::kSetCreatePipelineStateOptions);
            omm.getArrayPrebuild = NvapiInterface(nvomm::kGetArrayPrebuildInfo);
            omm.buildArray = NvapiInterface(nvomm::kBuildArray);
            omm.getPrebuildEx = NvapiInterface(nvomm::kGetPrebuildInfoEx);
            omm.buildEx = NvapiInterface(nvomm::kBuildStructureEx);
            std::string why;
            char simulated[16] = {};
            if (GetEnvironmentVariableA("REDEFINITION_OMM_CAPS", simulated, sizeof(simulated)) > 0
                && strcmp(simulated, "none") == 0)
            {
                omm.caps = 0;
                why = "the driver's capability simulated as none (REDEFINITION_OMM_CAPS)";
            }
            else if (omm.getCaps == nullptr)
            {
                omm.caps = 0;
                why = "no NVAPI (not an NVIDIA GPU)";
            }
            else if (omm.setOptions == nullptr || omm.getArrayPrebuild == nullptr || omm.buildArray == nullptr
                     || omm.getPrebuildEx == nullptr || omm.buildEx == nullptr)
            {
                omm.caps = 0;
                why = "the driver's NVAPI lacks the micromap functions";
            }
            else
            {
                uint32_t value = 0;
                const int status = reinterpret_cast<nvomm::GetRaytracingCapsFn>(omm.getCaps)(
                    device.Get(), nvomm::kCapsOpacityMicromap, &value, sizeof(value));
                omm.caps = status == 0 && (value & nvomm::kCapStandard) != 0 ? 1 : 0;
                why = omm.caps == 1 ? "NVAPI reports standard micromap support (NVIDIA)"
                                    : "NVAPI reports no micromap support (status " + std::to_string(status) + ", caps "
                                          + std::to_string(value) + ")";
            }
            omm.capsWhy = why;
        }

        const auto& config = Config();
        const bool switchedOn = config.rtOpacityMicromaps == 1;
        omm.wanted = switchedOn && omm.caps == 1;
        // Made again with the support the first time it is wanted on a device whose pipelines
        // were made without; the lists in flight are done with the old ones first.
        if (omm.wanted && !omm.pipelines && list != nullptr)
        {
            WaitForAllLocked();
            pipelines.clear();
            denoisePipelines.clear();
            rootSignature.Reset();
            if (!BuildPipelinesLocked())
            {
                // Without them the service would trace nothing: keep it going without micromaps.
                omm.wanted = false;
                omm.caps = 0;
                omm.capsWhy = "the passes' pipelines could not be made with micromap support";
                pipelines.clear();
                denoisePipelines.clear();
                rootSignature.Reset();
                BuildPipelinesLocked();
            }
        }
        omm.live = omm.wanted && omm.pipelines && config.rtCutoutTest == 0 && packet.params[6] > 0.5f;
        omm.keepNonOpaque = config.rtFoliageTransmission > 0.0f;

        // The log, when the state or the reason changes.
        const bool on = omm.wanted && omm.pipelines;
        const std::string why = !switchedOn ? "rtOpacityMicromaps=0" : omm.capsWhy;
        const std::string line = std::string("opacity micromaps ") + (on ? "on" : "off") + " -- " + why;
        if (omm.announcedLine != line)
        {
            omm.announcedLine = line;
            LogLine("Raytracing: " + line);
        }
    }

    // ---------------------------------------------------------------- what is known of meshes

    void Raytracing::OmmNoteTextureLocked(int layer, const std::vector<uint8_t>& pixels)
    {
        if (omm.caps == 0)
            return;
        // The sampler reads the alpha of the sRGB array, which stays linear.
        OmmMemory::Alpha alpha;
        alpha.bytes.resize(static_cast<size_t>(omm::kTextureSize) * omm::kTextureSize);
        if (pixels.size() < alpha.bytes.size() * 4)
            return;
        for (size_t i = 0; i < alpha.bytes.size(); ++i)
            alpha.bytes[i] = pixels[i * 4 + 3];
        alpha.hash = Fnv(alpha.bytes.data(), alpha.bytes.size());
        const auto known = omm.alpha.find(layer);
        if (known == omm.alpha.end() || known->second.hash != alpha.hash)
            ++omm.alphaChanges;
        omm.alpha[layer] = std::move(alpha);
    }

    void Raytracing::OmmCollectCutoutMeshesLocked()
    {
        omm.cutoutMeshes.clear();
        if (frameScenes.empty())
            return;
        const FrameScene& scene = frameScenes.back();
        const auto maybeCutout = [&](int32_t material)
        {
            const auto found = materialData.find(material);
            return found == materialData.end() || found->second.albedo[3] > 0.0f;
        };
        for (const RtInstance& instance : scene.instances)
            if (maybeCutout(instance.material))
                omm.cutoutMeshes.insert(instance.mesh);
        for (const RtGpuInstanceGroup& group : scene.groups)
            if (maybeCutout(group.material))
                omm.cutoutMeshes.insert(group.mesh);
    }

    void Raytracing::OmmNoteMeshLocked(Mesh& mesh, const PendingMesh& pending)
    {
        if (omm.caps != 1 || omm.cutoutMeshes.count(mesh.id) == 0)
            return;
        OmmMemory::Source source;
        const size_t triangles = pending.indices.size() / 3;
        source.triangles.resize(triangles);
        for (size_t t = 0; t < triangles; ++t)
            for (int k = 0; k < 3; ++k)
            {
                const float* attribute = &pending.attributes[static_cast<size_t>(pending.indices[t * 3 + k]) * kAttributeFloats];
                source.triangles[t].uv[k][0] = attribute[3];
                source.triangles[t].uv[k][1] = attribute[4];
            }
        source.uvHash = Fnv(source.triangles.data(), source.triangles.size() * sizeof(omm::TriangleUv));
        auto known = omm.sources.find(mesh.id);
        // The coordinates changed (a mesh handed over again): a bake asked for before is no
        // more the one wanted.
        if (known != omm.sources.end() && known->second.uvHash != source.uvHash)
            mesh.ommAsked = false;
        omm.sources[mesh.id] = std::move(source);
        mesh.ommSource = true;
        ++omm.metMeshes;
    }

    void Raytracing::OmmForgetMeshLocked(int32_t id)
    {
        omm.sources.erase(id);
        omm.wantedMeshes.erase(id);
    }

    void Raytracing::OmmUnrefSetLocked(uint32_t setId, UINT64 freeAt)
    {
        // Freed by OmmUpdateLocked once it has been unheld for a while.
        (void)freeAt;
        auto found = omm.sets.find(setId);
        if (found != omm.sets.end() && found->second.refs > 0 && --found->second.refs == 0)
            found->second.idleSince = omm.updates;
    }

    void Raytracing::OmmReleaseMeshLocked(Mesh& mesh, UINT64 freeAt)
    {
        if (mesh.ommSet == 0)
            return;
        const uint32_t id = mesh.ommSet;
        mesh.ommSet = 0;
        mesh.ommLayer = -1;
        {
            std::lock_guard<std::mutex> status(statusLock);
            if (ommMeshes > 0)
                --ommMeshes;
        }
        OmmUnrefSetLocked(id, freeAt);
    }

    // ---------------------------------------------------------------- the instances

    Raytracing::OmmFlags Raytracing::OmmInstanceLocked(const Mesh& mesh, int32_t material, bool cutout)
    {
        OmmFlags flags;
        if (mesh.ommSet == 0)
            return flags;
        bool applies = false;
        if (cutout)
        {
            const auto data = materialData.find(material);
            const int layer = data != materialData.end() ? data->second.texture : -1;
            const auto alpha = omm.alpha.find(layer);
            applies = layer >= 0 && layer == mesh.ommLayer && alpha != omm.alpha.end() && alpha->second.hash == mesh.ommAlphaHash;
        }
        if (applies && omm.live)
            flags.dropNonOpaque = !omm.keepNonOpaque;
        else
            flags.disable = true;
        return flags;
    }

    void Raytracing::OmmWantLocked(Mesh& mesh, int32_t material)
    {
        if (!omm.wanted || !omm.pipelines || !mesh.ommSource || mesh.ommAsked
            || (mesh.ommSet != 0 && mesh.ommCheckedAt == omm.alphaChanges))
            return;
        const auto data = materialData.find(material);
        const int layer = data != materialData.end() ? data->second.texture : -1;
        const auto alpha = omm.alpha.find(layer);
        if (layer < 0 || alpha == omm.alpha.end())
            return;
        if (mesh.ommSet != 0)
        {
            // Already baked for this texture's coverage; for another texture, left as it is
            // (one mesh used with two textures keeps the first); for the same texture with
            // other coverage, baked again.
            if (mesh.ommLayer != layer || mesh.ommAlphaHash == alpha->second.hash)
            {
                mesh.ommCheckedAt = mesh.ommLayer == layer ? omm.alphaChanges : mesh.ommCheckedAt;
                return;
            }
        }
        mesh.ommAsked = true;
        omm.wantedMeshes[mesh.id] = layer;
    }

    // ---------------------------------------------------------------- baking and building

    uint32_t Raytracing::OmmUpdateLocked(BuildFrame& frame, Slot& slot)
    {
        if (!omm.wanted || !omm.pipelines)
            return 0;

        // The bakes done: each a set, and the meshes that waited for it queued.
        std::vector<OmmMemory::Result> done;
        {
            std::lock_guard<std::mutex> lock(omm.jobLock);
            done.swap(omm.results);
        }
        for (OmmMemory::Result& result : done)
        {
            if (result.epoch != omm.epoch.load())
                continue;
            ++omm.bakes;
            omm.bakeMs += result.ms;
            if (omm.bakes <= 20)
            {
                const omm::Baked& b = result.baked;
                LogLine("Raytracing: opacity micromaps baked for " + std::to_string(b.triangleIndex.size()) + " triangles in "
                        + std::to_string(result.ms) + " ms: " + std::to_string(b.uniform[0]) + " transparent, "
                        + std::to_string(b.uniform[1]) + " opaque, " + std::to_string(b.uniform[2] + b.uniform[3])
                        + " unknown, " + std::to_string(b.withOmm) + " with micromaps of their own (" + std::to_string(b.entries.size())
                        + " micromaps, " + std::to_string(b.data.size()) + " bytes; " + std::to_string(b.microTriangles[0])
                        + " micro-triangles transparent, " + std::to_string(b.microTriangles[1]) + " opaque, "
                        + std::to_string(b.microTriangles[2] + b.microTriangles[3]) + " unknown)");
            }
            {
                std::lock_guard<std::mutex> status(statusLock);
                ommBakes = omm.bakes;
                ommBakeMs = static_cast<float>(omm.bakeMs);
            }
            OmmMemory::Set set;
            set.key = result.key;
            set.triangleIndex = std::move(result.baked.triangleIndex);
            uint32_t refs[13] = {};
            for (int32_t index : set.triangleIndex)
                if (index >= 0)
                    ++refs[result.baked.entries[static_cast<size_t>(index)].level];
            for (uint32_t level = 0; level < 13; ++level)
                if (refs[level] > 0)
                    set.usage.push_back({ refs[level], level, omm::kFormat4State });
            for (const omm::Entry& entry : result.baked.entries)
                set.descs.push_back({ entry.byteOffset, entry.level, entry.format });
            set.data = std::move(result.baked.data);
            omm.ommEntries += static_cast<uint32_t>(set.descs.size());
            omm.microTriangles += result.baked.microTriangles[0] + result.baked.microTriangles[1]
                                  + result.baked.microTriangles[2] + result.baked.microTriangles[3];
            omm.uniformTriangles += result.baked.uniform[0] + result.baked.uniform[1] + result.baked.uniform[2] + result.baked.uniform[3];
            omm.builtTriangles += static_cast<uint32_t>(set.triangleIndex.size());
            const uint32_t id = omm.nextSet++;
            omm.byKey[result.key] = id;
            omm.sets[id] = std::move(set);
            auto waiters = omm.waiting.find(result.key);
            if (waiters != omm.waiting.end())
            {
                for (const auto& waiter : waiters->second)
                    omm.toAttach.push_back({ waiter.first, id });
                omm.waiting.erase(waiters);
            }
        }

        // The meshes asked for: a set of the same coordinates and coverage already there, a bake
        // already on its way, or a bake to start.
        const uint32_t maxLevel = static_cast<uint32_t>(Config().rtOmmMaxSubdivision);
        for (const auto& want : omm.wantedMeshes)
        {
            auto mesh = meshes.find(want.first);
            auto source = omm.sources.find(want.first);
            auto alpha = omm.alpha.find(want.second);
            if (mesh == meshes.end() || !mesh->second.built || source == omm.sources.end() || alpha == omm.alpha.end())
            {
                if (mesh != meshes.end())
                    mesh->second.ommAsked = false;
                continue;
            }
            const uint64_t key = SetKey(source->second.uvHash, alpha->second.hash, maxLevel);
            mesh->second.ommLayer = mesh->second.ommSet != 0 ? mesh->second.ommLayer : want.second;
            const auto known = omm.byKey.find(key);
            if (known != omm.byKey.end())
            {
                omm.toAttach.push_back({ want.first, known->second });
                continue;
            }
            auto& waiters = omm.waiting[key];
            const bool started = !waiters.empty();
            waiters.push_back({ want.first, want.second });
            if (started)
                continue;
            OmmMemory::Job job;
            job.key = key;
            job.triangles = source->second.triangles;
            job.alpha = alpha->second.bytes;
            job.settings.maxLevel = maxLevel;
            job.epoch = omm.epoch.load();
            {
                std::lock_guard<std::mutex> lock(omm.jobLock);
                omm.jobs.push_back(std::move(job));
            }
            if (!omm.workerStarted)
            {
                omm.workerStarted = true;
                std::thread(
                    [this]()
                    {
                        // Below the game's own threads: a bake is no hurry.
                        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
                        for (;;)
                        {
                            OmmMemory::Job next;
                            {
                                std::unique_lock<std::mutex> lock(omm.jobLock);
                                omm.jobSignal.wait(lock, [&] { return !omm.jobs.empty(); });
                                next = std::move(omm.jobs.front());
                                omm.jobs.pop_front();
                            }
                            OmmMemory::Result result;
                            result.key = next.key;
                            result.epoch = next.epoch;
                            const auto begin = std::chrono::steady_clock::now();
                            omm::Bake(next.triangles.data(), next.triangles.size(), next.alpha.data(), next.settings, result.baked);
                            result.ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - begin).count();
                            std::lock_guard<std::mutex> lock(omm.jobLock);
                            if (next.epoch == omm.epoch.load())
                                omm.results.push_back(std::move(result));
                        }
                    })
                    .detach();
            }
            omm.jobSignal.notify_one();
        }
        omm.wantedMeshes.clear();

        // The meshes whose set is ready: their structures built again with the micromaps, a
        // frame's triangles at most (rtBlasRefineBudget).
        const uint64_t budget = static_cast<uint64_t>(std::max(1, Config().rtBlasRefineBudget));
        uint64_t triangles = 0;
        uint32_t rebuilt = 0;
        while (!omm.toAttach.empty())
        {
            const auto [meshId, setId] = omm.toAttach.front();
            auto mesh = meshes.find(meshId);
            auto set = omm.sets.find(setId);
            if (mesh == meshes.end() || !mesh->second.built || set == omm.sets.end())
            {
                omm.toAttach.pop_front();
                if (mesh != meshes.end())
                    mesh->second.ommAsked = false;
                continue;
            }
            Mesh& target = mesh->second;
            if (rebuilt > 0 && triangles + target.indexCount / 3 > budget)
                break;
            omm.toAttach.pop_front();
            // Still the coordinates and the coverage it was baked for, and still the settings.
            auto source = omm.sources.find(meshId);
            const auto alpha = omm.alpha.find(target.ommLayer);
            if (source == omm.sources.end() || alpha == omm.alpha.end()
                || SetKey(source->second.uvHash, alpha->second.hash, maxLevel) != set->second.key
                || set->second.triangleIndex.size() != target.indexCount / 3)
            {
                target.ommAsked = false;
                continue;
            }
            if (target.ommSet == setId)
            {
                target.ommAsked = false;
                continue;
            }
            // The previous set let go of once this one is in place.
            ++set->second.refs;
            const uint32_t previous = target.ommSet;
            if (!OmmQueueBuildLocked(frame, target, setId, slot))
            {
                --set->second.refs;
                target.ommSet = previous;
                target.ommAsked = true;   // not tried again for these inputs
                continue;
            }
            if (previous != 0)
                OmmUnrefSetLocked(previous, fenceValue + 1);
            else
            {
                std::lock_guard<std::mutex> status(statusLock);
                ++ommMeshes;
            }
            target.ommAlphaHash = alpha->second.hash;
            target.ommCheckedAt = omm.alphaChanges;
            target.ommAsked = false;
            triangles += target.indexCount / 3;
            ++rebuilt;
        }

        ++omm.updates;
        // Sets nobody has held for a while nor waits for.
        for (auto it = omm.sets.begin(); it != omm.sets.end();)
        {
            bool waited = false;
            for (const auto& entry : omm.toAttach)
                waited = waited || entry.second == it->first;
            if (it->second.refs == 0 && !waited && omm.updates - it->second.idleSince > kIdleUpdates)
            {
                if (it->second.array.Valid())
                    omm.arrays.Free(it->second.array.range, fenceValue + 1);
                omm.byKey.erase(it->second.key);
                it = omm.sets.erase(it);
            }
            else
                ++it;
        }
        if (rebuilt > 0)
        {
            std::lock_guard<std::mutex> status(statusLock);
            ommBytes = omm.arrays.Used();
        }
        return rebuilt;
    }

    bool Raytracing::OmmQueueBuildLocked(BuildFrame& frame, Mesh& mesh, uint32_t setId, Slot& slot)
    {
        OmmMemory::Set& set = omm.sets[setId];
        const uint32_t triangles = mesh.indexCount / 3;

        // The array first, on the first mesh to use the set: its inputs in the upload, 256-byte
        // aligned as NVAPI asks, and a range of the arrays' pool for the result.
        if (!set.arrayBuilt)
        {
            if (!set.descs.empty())
            {
                UINT64 dataAt = 0;
                const UINT64 descsAt = RoundUp(set.data.size(), 16);
                const UINT64 total = descsAt + set.descs.size() * sizeof(nvomm::Desc);
                ID3D12Resource* upload = nullptr;
                UINT64 offset = 0;
                uint8_t* mapped = nullptr;
                if (!UploadSpaceLocked(frame, slot, total, upload, offset, mapped, nvomm::kArrayAlignment))
                    return false;
                memcpy(mapped + dataAt, set.data.data(), set.data.size());
                memcpy(mapped + descsAt, set.descs.data(), set.descs.size() * sizeof(nvomm::Desc));
                BuildFrame::OmmArrayBuild build;
                // The usage counts of the array itself: the micromaps in it, by level.
                uint32_t entries[13] = {};
                for (const nvomm::Desc& desc : set.descs)
                    ++entries[desc.subdivisionLevel];
                for (uint32_t level = 0; level < 13; ++level)
                    if (entries[level] > 0)
                        build.usage.push_back({ entries[level], level, omm::kFormat4State });
                nvomm::ArrayInputs& inputs = build.desc.inputs;
                inputs.flags = nvomm::kArrayPreferFastTrace;
                inputs.numOMMUsageCounts = static_cast<uint32_t>(build.usage.size());
                inputs.pOMMUsageCounts = build.usage.data();
                inputs.inputBuffer = upload->GetGPUVirtualAddress() + offset + dataAt;
                inputs.perOMMDescs = { upload->GetGPUVirtualAddress() + offset + descsAt, sizeof(nvomm::Desc) };
                nvomm::ArrayPrebuildInfo info = {};
                nvomm::ArrayPrebuildParams params = { nvomm::Version(sizeof(nvomm::ArrayPrebuildParams), 1), &inputs, &info };
                if (reinterpret_cast<nvomm::GetArrayPrebuildInfoFn>(omm.getArrayPrebuild)(device.Get(), &params) != 0
                    || info.resultDataMaxSizeInBytes == 0)
                {
                    SetProblem("the driver gave no size for a micromap array");
                    return false;
                }
                if (!omm.arrays.Allocate(device.Get(), info.resultDataMaxSizeInBytes, set.array.range))
                {
                    SetProblem("a micromap array could not be made");
                    return false;
                }
                set.array.address = omm.arrays.Address(set.array.range);
                set.array.size = set.array.range.size;
                D3D12_GPU_VIRTUAL_ADDRESS scratch = 0;
                if (!ScratchLocked(frame, slot, info.scratchDataSizeInBytes, scratch))
                {
                    omm.arrays.Free(set.array.range, 0);
                    set.array = RtStoredBuffer{};
                    return false;
                }
                build.desc.destOpacityMicromapArrayData = set.array.address;
                build.desc.scratchOpacityMicromapArrayData = scratch;
                frame.ommArrays.push_back(std::move(build));
            }
            set.arrayBuilt = true;
            set.data.clear();
            set.data.shrink_to_fit();
            set.descs.clear();
            set.descs.shrink_to_fit();
        }

        // The index of each triangle's micromap, in the upload: read when the structure is built.
        ID3D12Resource* indexUpload = nullptr;
        UINT64 indexOffset = 0;
        uint8_t* indexMapped = nullptr;
        if (!UploadSpaceLocked(frame, slot, static_cast<UINT64>(triangles) * sizeof(int32_t), indexUpload, indexOffset, indexMapped))
            return false;
        memcpy(indexMapped, set.triangleIndex.data(), static_cast<size_t>(triangles) * sizeof(int32_t));

        BuildFrame::Build build;
        build.ex = true;
        build.usage = set.usage;
        nvomm::GeometryDescEx& geometry = build.geometryEx;
        geometry.type = nvomm::kGeometryTypeOmmTriangles;
        // Opaque as the usual structures' geometry is (GeometryOf): where the micromaps are
        // switched off, the instance is what it was.
        geometry.flags = D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
        nvomm::OmmTriangles& attachment = geometry.ommTriangles;
        attachment.triangles.VertexBuffer.StartAddress = mesh.positionsBuffer->GetGPUVirtualAddress() + mesh.positionsOffset;
        attachment.triangles.VertexBuffer.StrideInBytes = 3 * sizeof(float);
        attachment.triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT;
        attachment.triangles.VertexCount = mesh.vertexCount;
        attachment.triangles.IndexBuffer = mesh.indicesBuffer->GetGPUVirtualAddress() + mesh.indicesOffset;
        attachment.triangles.IndexFormat = DXGI_FORMAT_R32_UINT;
        attachment.triangles.IndexCount = mesh.indexCount;
        attachment.ommAttachment.opacityMicromapIndexBuffer = { indexUpload->GetGPUVirtualAddress() + indexOffset, sizeof(int32_t) };
        attachment.ommAttachment.opacityMicromapIndexFormat = DXGI_FORMAT_R32_UINT;
        attachment.ommAttachment.opacityMicromapBaseLocation = 0;
        attachment.ommAttachment.opacityMicromapArray = set.array.Valid() ? set.array.address : 0;
        attachment.ommAttachment.numOMMUsageCounts = set.array.Valid() ? static_cast<uint32_t>(build.usage.size()) : 0;

        nvomm::BuildDescEx& desc = build.descEx;
        nvomm::InputsEx& inputs = desc.inputs;
        inputs.type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
        inputs.flags = nvomm::kBuildPreferFastTrace | nvomm::kBuildAllowDisableOmms
                       | (frame.compaction != 0 ? nvomm::kBuildAllowCompaction : 0u);
        inputs.numDescs = 1;
        inputs.descsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY;
        inputs.geometryDescStrideInBytes = sizeof(nvomm::GeometryDescEx);
        // The pointers are set again where the build is kept (the deque holds it) and recorded.
        inputs.pGeometryDescs = &build.geometryEx;
        attachment.ommAttachment.pOMMUsageCounts = build.usage.data();

        D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO info = {};
        nvomm::PrebuildExParams prebuild = { nvomm::Version(sizeof(nvomm::PrebuildExParams), 1), &inputs, &info };
        if (reinterpret_cast<nvomm::GetPrebuildInfoExFn>(omm.getPrebuildEx)(device.Get(), &prebuild) != 0
            || info.ResultDataMaxSizeInBytes == 0)
        {
            SetProblem("the driver gave no size for a structure with micromaps");
            return false;
        }
        RtStoredBuffer blas;
        if (!MakeBlasLocked(info.ResultDataMaxSizeInBytes, frame.pooled, blas)
            || !ScratchLocked(frame, slot, info.ScratchDataSizeInBytes, desc.scratchAccelerationStructureData))
        {
            if (blas.Valid())
                RetireBlasLocked(blas, slot);
            SetProblem("a structure with micromaps could not be made");
            return false;
        }
        desc.destAccelerationStructureData = blas.address;
        ToReadLocked(frame, mesh.positionsBuffer);
        ToReadLocked(frame, mesh.indicesBuffer);

        // In place of the structure the mesh had: that one let go of past this list.
        RetireBlasLocked(mesh.blas, slot);
        ReleaseClustersLocked(mesh, fenceValue + 1);
        if (mesh.updateScratch != nullptr)
        {
            slot.held.push_back(mesh.updateScratch);
            mesh.updateScratch.Reset();
        }
        mesh.updatable = false;
        mesh.blas = blas;
        mesh.blasSerial = blasMemory.nextSerial++;
        mesh.refine = kRefineNone;
        mesh.ommSet = setId;

        frame.builds.push_back(std::move(build));
        BuildFrame::Build& queued = frame.builds.back();
        queued.geometryEx.ommTriangles.ommAttachment.pOMMUsageCounts = queued.usage.data();
        queued.descEx.inputs.pGeometryDescs = &queued.geometryEx;
        const UINT index = frame.sizesAsked;
        if (frame.compaction != 0 && AskSizeLocked(frame, mesh.id, mesh.blasSerial))
        {
            queued.askSize = true;
            queued.sizeIndex = index;
            mesh.refine = kRefineCompact;
        }
        if (!frame.batched)
            FlushBuildsLocked(frame);
        return true;
    }

    void Raytracing::FlushOmmArraysLocked(BuildFrame& frame)
    {
        if (frame.ommArrays.empty())
            return;
        for (BuildFrame::OmmArrayBuild& build : frame.ommArrays)
        {
            build.desc.inputs.pOMMUsageCounts = build.usage.data();
            nvomm::ArrayBuildParams params = { nvomm::Version(sizeof(nvomm::ArrayBuildParams), 1), &build.desc, 0, nullptr };
            if (reinterpret_cast<nvomm::BuildArrayFn>(omm.buildArray)(list.Get(), &params) != 0)
                SetProblem("the driver refused a micromap array's build");
        }
        frame.ommArrays.clear();
        // The structures that name the arrays read them once they are done.
        const D3D12_RESOURCE_BARRIER barrier = UavBarrier(nullptr);
        list->ResourceBarrier(1, &barrier);
    }
}
