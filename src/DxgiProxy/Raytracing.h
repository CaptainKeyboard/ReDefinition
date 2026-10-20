#pragma once

#include <windows.h>

#include <d3d11_4.h>
#include <d3d12.h>
#include <wrl/client.h>

#include "FrameGeneration.h"
#include "RaytracingShaders/RtContract.h"

#include <FidelityFX/api/include/ffx_api_loader.h>

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <map>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

namespace redefinition
{
    // Raytracing on the proxy's Direct3D 12 device (docs/development/raytracing.md).
    //
    // The managed side hands over meshes, materials and each frame's instances
    // through plain exports from the main thread; the data is copied at once. The
    // frame itself comes through Unity's render thread (event 30), with the
    // textures to read and to write and the camera: the service builds what is
    // new, traces, and copies the results back into Unity's textures.
    //
    // Positions are relative to the camera: an instance's transform takes its
    // mesh into a space with the world's axes and the camera at the origin, so
    // that what is near the camera keeps full float precision however far KSP's
    // world has moved.

    // The values and layouts the managed side and the shaders share are the contract's
    // (RaytracingShaders/RtContract.h); the static_asserts below hold the structures to it.
    constexpr uint32_t kRtMeshMagic = RT_MESH_MAGIC;
    constexpr uint32_t kRtMaterialMagic = RT_MATERIAL_MAGIC;
    constexpr uint32_t kRtFrameMagic = RT_FRAME_MAGIC;

    constexpr uint32_t kRtMaxTextures = 8;

    // A mesh: vertexCount positions (three floats each), normals (three) and
    // texture coordinates (two) or null, vertex colours (RGBA, a byte each, as
    // Unity's Color32) or null for white, and indexCount indices, three per
    // triangle.
    struct RtMeshDesc
    {
        uint32_t size;
        uint32_t magic;
        int32_t mesh;
        uint32_t vertexCount;
        uint32_t indexCount;
        uint32_t flags;   // reserved
        const float* positions;
        const float* normals;
        const float* uvs;
        const uint32_t* indices;
        const uint32_t* colors;
    };

    // A material: linear albedo, and in w the alpha below which its texture cuts the
    // surface out (0 for none); specular colour, smoothness as Unity's standard
    // shading takes it, emitted radiance, and the layer of the material texture array
    // (-1: none).
    struct RtMaterialDesc
    {
        uint32_t size;
        uint32_t magic;
        int32_t material;
        uint32_t flags;   // how its shine is meant: RT_SURFACE_*
        float albedo[4];
        float specular[3];
        float smoothness;
        float emission[3];
        int32_t texture;
        // Where vertexColour > 0, the albedo is in Unity's colour space and the vertex
        // colour times this is added to it at each hit, the sum taken to linear as
        // Unity's colours are (Parallax's terrain: RtTerrainColour); no texture then.
        float vertexColour;
        // RT_SURFACE_LEGACY and UNITY_LEGACY: what the G-buffer's smoothness, or its
        // specular colour, is divided by for the gloss; and the gloss where no G-buffer
        // gives it.
        float glossDivisor;
        float glossMean;
        // What of the frame's colour the forward light keeps as diffuse, as Deferred's
        // shading would (RtDeferredLook); 0 for all of it.
        float diffuseShare;
        // The sun's highlight the material's shader draws by itself, in Unity's colour
        // space, which the forward light takes out of the frame's colour before it relights
        // it (RtForwardHighlight): the specular colour, and the shape -- above 0 a legacy
        // Blinn-Phong exponent, otherwise minus the standard shader's smoothness. All 0
        // for none.
        float highlight[4];
    };

    // One instance of a mesh this frame: a 3x4 transform, rows first, from the
    // mesh into camera-relative space; its material; the ray mask (bits 0-7) and
    // flags (kRtInstance*).
    struct RtInstance
    {
        float transform[12];
        int32_t mesh;
        int32_t material;
        uint32_t mask;
        uint32_t flags;   // kRtInstance*
    };

    // Terrain, which the passes pass through for the first metre from a pixel's surface:
    // Parallax displaces the drawn terrain on the GPU, off the mesh the rays see.
    constexpr uint32_t kRtInstanceTerrain = RT_INSTANCE_TERRAIN;
    // Set by the service for an instance whose material is cut out.
    constexpr uint32_t kRtInstanceCutout = RT_INSTANCE_CUTOUT;
    constexpr uint32_t kRtInstanceScenery = RT_INSTANCE_SCENERY;

    // A run of instances the GPU made, in the frame's t7: mesh and material as the
    // managed side handed them over, where the run starts in t7 and how long it is, the
    // ray mask and flags each of its instances takes.
    struct RtGpuInstanceGroup
    {
        int32_t mesh;
        int32_t material;
        uint32_t first;
        uint32_t count;
        uint32_t mask;
        uint32_t flags;
        uint32_t reserved[2];
    };

    // The frame's flags.
    // The results are copied back before the event returns, whatever the flags; the mod
    // still sets this bit, and it is accepted without effect.
    constexpr uint32_t kRtFrameSameFrame = RT_FLAG_SAME_FRAME;
    constexpr uint32_t kRtFrameReset = RT_FLAG_RESET; // the history is not this camera's: start afresh
    constexpr uint32_t kRtFrameDenoise = RT_FLAG_DENOISE; // the signals through ReDefinition's denoiser
    constexpr uint32_t kRtFrameMotionVectors = RT_FLAG_MOTION_VECTORS; // t5 holds motion: the previous uv is uv - motion.xy
    constexpr uint32_t kRtFrameGuides = RT_FLAG_GUIDES; // DLSS Ray Reconstruction's guides from this frame's surfaces
    // A further trace of a frame already traced (the ocean's reflections): it leaves the
    // frame's Ray Reconstruction guides and the frame count as they are.
    constexpr uint32_t kRtFrameExtra = RT_FLAG_EXTRA;
    // The surface is water (Scatterer's ocean): its reflected rays stay above the
    // horizon; where they meet nothing, the reflections hold only the sun's and the
    // moons' discs mirrored by the waves, not single rays meeting them, and 65000 in w
    // (RtCommon.hlsli, BodiesMirroredAt).
    constexpr uint32_t kRtFrameWater = RT_FLAG_WATER;
    // With kRtFrameWater, for the light probe: the water's normal with ReDefinition's waves
    // in place of its reflections (n * 0.5 + 0.5; on Scatterer's ocean its ripples on the
    // level sea), to check that the waves stay on the water.
    constexpr uint32_t kRtFrameWaterNormals = RT_FLAG_WATER_NORMALS;
    // The G-buffer's smoothness is Deferred's from KSP's legacy part shaders: taken as
    // their highlight (RtCommon.hlsli, LegacySmoothness). The game's; not a test's.
    constexpr uint32_t kRtFrameLegacySmoothness = RT_FLAG_LEGACY_SMOOTHNESS;
    // The surfaces' shine as their materials mean it, found per pixel by the sun's pass and
    // written to u5 and u6 for the later passes and for Unity (RtCommon.hlsli, SurfaceIntent).
    constexpr uint32_t kRtFrameSurfaceIntent = RT_FLAG_SURFACE_INTENT;

    // The signals a frame traces; each writes its output slot.
    constexpr uint32_t kRtSignalSunVisibility = RT_SIGNAL_SUN; // u0: visibility of the sun, 0 to 1
    constexpr uint32_t kRtSignalAmbientOcclusion = RT_SIGNAL_OCCLUSION; // u3: the share of the sky a point sees
    constexpr uint32_t kRtSignalDiffuse = RT_SIGNAL_DIFFUSE; // u1: diffuse light arriving, and the hit distance
    constexpr uint32_t kRtSignalSpecular = RT_SIGNAL_SPECULAR; // u2: light from the specular lobe, and the hit distance
    constexpr uint32_t kRtSignalForwardLight = RT_SIGNAL_FORWARD_LIGHT; // u4: the light to add to a forward-rendered frame
    constexpr uint32_t kRtSignalPrimaryCheck = RT_SIGNAL_PRIMARY_CHECK; // u7: the camera ray's hit distance against the depth's

    // The signals the denoiser filters: the output slot each writes, its place among
    // the denoiser's textures, and its kind, 0 a scalar (the sun's visibility, the
    // occlusion), 1 a colour (the diffuse light), 2 a colour that moves with the view
    // (the reflections), whose history and filter smooth surfaces shorten.
    struct RtDenoisedSignal
    {
        uint32_t signal;
        uint32_t output;
        uint32_t index;
        uint32_t kind;
    };
    constexpr uint32_t kRtDenoisedSignals = 4;
    constexpr RtDenoisedSignal kRtDenoised[kRtDenoisedSignals] = {
        { kRtSignalSunVisibility, RT_OUTPUT_SUN, 0, 0 },
        { kRtSignalDiffuse, RT_OUTPUT_DIFFUSE, 1, 1 },
        { kRtSignalSpecular, RT_OUTPUT_SPECULAR, 2, 2 },
        { kRtSignalAmbientOcclusion, RT_OUTPUT_OCCLUSION, 3, 0 },
    };
    // The entry of a signal the denoiser filters.
    constexpr const RtDenoisedSignal& RtDenoisedFor(uint32_t signal)
    {
        uint32_t i = 0;
        while (i + 1 < kRtDenoisedSignals && kRtDenoised[i].signal != signal)
            ++i;
        return kRtDenoised[i];
    }

    // The inputs, by slot: t1 depth as Direct3D's clip-space z (R32_FLOAT), t2-t4 the
    // G-buffer in Unity's layout (albedo and occlusion, specular and smoothness, the
    // world normal as n * 0.5 + 0.5), t5 motion vectors.
    constexpr uint32_t kRtInputDepth = RT_INPUT_DEPTH;
    constexpr uint32_t kRtInputGBuffer0 = RT_INPUT_G_BUFFER0;
    constexpr uint32_t kRtInputGBuffer1 = RT_INPUT_G_BUFFER1;
    constexpr uint32_t kRtInputGBuffer2 = RT_INPUT_G_BUFFER2;
    constexpr uint32_t kRtInputMotion = RT_INPUT_MOTION;
    constexpr uint32_t kRtInputEnvironment = RT_INPUT_ENVIRONMENT;   // t6: the sky as a latitude-longitude image
    // t7: instances the GPU made (Parallax's scatter), camera-relative, as
    // R32G32B32A32_FLOAT: each the three rows of its transform in three texels side by
    // side, kRtInstancesPerRow to a texture row; all zero for none. The frame's
    // RtGpuInstanceGroup list says which mesh and material each run of them is.
    constexpr uint32_t kRtInputInstances = RT_INPUT_INSTANCES;
    constexpr uint32_t kRtInstancesPerRow = RT_INSTANCES_PER_ROW;

    // A frame's instances and groups set for every frame that has none of its own.
    constexpr uint32_t kRtAnyFrame = RT_ANY_FRAME;

    // The material textures: small copies of each material's main texture, in one
    // array of kRtMaterialTextureSize square RGBA8 layers, sRGB; at most as many layers
    // as a Direct3D 12 texture array holds (D3D12_REQ_TEXTURE2D_ARRAY_AXIS_DIMENSION).
    constexpr uint32_t kRtMaterialTextureSize = RT_MATERIAL_TEXTURE_SIZE;
    // The bytes of a slot's constant buffer (cbuffer Frame, RtCommon.hlsli).
    constexpr uint64_t kRtConstantsSize = 1024;

    // cbuffer Frame as the shaders read it (RtCommon.hlsli), field for field.
    struct RtFrameConstants
    {
        float clipToWorld[16];
        float worldToClip[16];
        float prevWorldToClip[16];
        float sunDirection[4];
        float sunColor[4];
        float skyColor[4];
        float params[8];
        float bodySphere[4];
        float ambient[12];
        uint32_t frameSize[2];
        uint32_t frameIndex;
        uint32_t frameFlags;
        float lights[16];
        float lightParams[4];
        float nightLight[4];
        float planet[4];
        float lightSurface[8];
        float sea[12];
        float clouds[24];
        float prevWorldToClipFar[16];
        float shadowFade[8];
        // The radiance cache's (RaytracingCache.cpp): the anchor's tile and the frame, the
        // anchor within its tile and the cells' angle, its parameters, its sizes.
        int32_t cacheTile[4];
        float cacheOffset[4];
        float cacheParams[4];
        uint32_t cacheSizes[4];
    };
    // Each field in the register the contract gives it (RtContract.h), as cbuffer Frame
    // places it (packoffset, RtCommon.hlsli).
    static_assert(offsetof(RtFrameConstants, clipToWorld) == RT_CB_CLIP_TO_WORLD * 16, "cbuffer Frame: clipToWorld");
    static_assert(offsetof(RtFrameConstants, worldToClip) == RT_CB_WORLD_TO_CLIP * 16, "cbuffer Frame: worldToClip");
    static_assert(offsetof(RtFrameConstants, prevWorldToClip) == RT_CB_PREV_WORLD_TO_CLIP * 16, "cbuffer Frame: prevWorldToClip");
    static_assert(offsetof(RtFrameConstants, sunDirection) == RT_CB_SUN_DIRECTION * 16, "cbuffer Frame: sunDirection");
    static_assert(offsetof(RtFrameConstants, sunColor) == RT_CB_SUN_COLOR * 16, "cbuffer Frame: sunColor");
    static_assert(offsetof(RtFrameConstants, skyColor) == RT_CB_SKY_COLOR * 16, "cbuffer Frame: skyColor");
    static_assert(offsetof(RtFrameConstants, params) == RT_CB_PARAMS * 16, "cbuffer Frame: params");
    static_assert(offsetof(RtFrameConstants, bodySphere) == RT_CB_BODY_SPHERE * 16, "cbuffer Frame: bodySphere");
    static_assert(offsetof(RtFrameConstants, ambient) == RT_CB_AMBIENT * 16, "cbuffer Frame: ambient");
    static_assert(offsetof(RtFrameConstants, frameSize) == RT_CB_FRAME_SIZE * 16, "cbuffer Frame: frameSize");
    static_assert(offsetof(RtFrameConstants, lights) == RT_CB_LIGHTS * 16, "cbuffer Frame: lights");
    static_assert(offsetof(RtFrameConstants, lightParams) == RT_CB_LIGHT_PARAMS * 16, "cbuffer Frame: lightParams");
    static_assert(offsetof(RtFrameConstants, nightLight) == RT_CB_NIGHT_LIGHT * 16, "cbuffer Frame: nightLight");
    static_assert(offsetof(RtFrameConstants, planet) == RT_CB_PLANET * 16, "cbuffer Frame: planet");
    static_assert(offsetof(RtFrameConstants, lightSurface) == RT_CB_LIGHT_SURFACE * 16, "cbuffer Frame: lightSurface");
    static_assert(offsetof(RtFrameConstants, sea) == RT_CB_SEA * 16, "cbuffer Frame: sea");
    static_assert(offsetof(RtFrameConstants, clouds) == RT_CB_CLOUDS * 16, "cbuffer Frame: clouds");
    static_assert(offsetof(RtFrameConstants, prevWorldToClipFar) == RT_CB_PREV_WORLD_TO_CLIP_FAR * 16,
                  "cbuffer Frame: prevWorldToClipFar");
    static_assert(offsetof(RtFrameConstants, shadowFade) == RT_CB_SHADOW_FADE * 16, "cbuffer Frame: shadowFade");
    static_assert(offsetof(RtFrameConstants, cacheTile) == RT_CB_CACHE_TILE * 16, "cbuffer Frame: cacheTile");
    static_assert(offsetof(RtFrameConstants, cacheOffset) == RT_CB_CACHE_OFFSET * 16, "cbuffer Frame: cacheOffset");
    static_assert(offsetof(RtFrameConstants, cacheParams) == RT_CB_CACHE_PARAMS * 16, "cbuffer Frame: cacheParams");
    static_assert(offsetof(RtFrameConstants, cacheSizes) == RT_CB_CACHE_SIZES * 16, "cbuffer Frame: cacheSizes");
    static_assert(offsetof(RtFrameConstants, frameIndex) == RT_CB_FRAME_SIZE * 16 + 8, "cbuffer Frame: frameIndex");
    static_assert(offsetof(RtFrameConstants, frameFlags) == RT_CB_FRAME_SIZE * 16 + 12, "cbuffer Frame: frameFlags");
    static_assert(sizeof(RtFrameConstants) == RT_CB_REGISTERS * 16, "cbuffer Frame: its size");
    static_assert(sizeof(RtFrameConstants) <= kRtConstantsSize, "cbuffer Frame outgrew its buffer");
    constexpr uint32_t kRtMaxMaterialTextures = RT_MAX_MATERIAL_TEXTURES;

    // The number of passes whose GPU time KspRtPassTimings reports, in signal bit order;
    // the denoiser's time in place kRtPassDenoiser, which no signal has, the radiance
    // cache's resolve in place kRtPassCache, which none has either.
    constexpr uint32_t kRtPassTimings = 8;
    constexpr uint32_t kRtPassDenoiser = 6;
    constexpr uint32_t kRtPassCache = 5;

    // Event 30: a frame. Matrices rows first, for column vectors: clip = worldToClip *
    // (camera-relative position, 1). Clip y is +1 at the texture's first row, as
    // Direct3D has it.
    struct RtFramePacket
    {
        uint32_t size;
        uint32_t magic;
        uint32_t frameIndex;
        uint32_t flags;
        uint32_t width;
        uint32_t height;
        uint32_t signals;
        uint32_t reserved;
        float clipToWorld[16];
        float worldToClip[16];
        float prevWorldToClip[16];
        float sunDirection[4];   // towards the sun; w: its angular radius in radians
        float sunColor[4];       // the sun's radiance at the scene, linear rgb
        float skyColor[4];       // the sky's radiance where no environment is given
        float params[8];
        float bodySphere[4];     // the main body: centre relative to the camera, radius; radius 0 for none
        float ambient[12];       // Unity's ambient light, which its shading added already, linear: facing up
                                 // (rgb, w 1 where Unity shades in gamma space), sideways, down
        // The camera as AMD FSR Ray Regeneration takes it: matrices for row vectors, rows
        // first, the view looking down +z and camera-relative, the projection without
        // the jitter; the previous position minus this one in this frame's world; the
        // jitter in pixels; the near and far planes.
        float worldToView[16];
        float viewToClip[16];
        float cameraDelta[4];
        float jitter[2];
        float depthRange[2];
        // Up to two bodies lit by the sun, each a light of the size of its disc
        // (RtReflectors): towards it with its angular radius in w, then its radiance at
        // the scene, linear rgb, with in w how far its shadow ray goes, to the body's
        // surface.
        float lights[16];
        // x: how many of lights are used; y: 1 where the colours Unity hands over -- the
        // G-buffer's albedo, the sun's and the sky's colour, the environment -- are
        // gamma-space values, which the light transport takes as linear first; z: how
        // strongly water mirrors the sun's disc (kRtFrameWater), 1 as it is; w: 1 where
        // the main body's sea is KSP's own, which the specular signal takes as water by
        // its level.
        float lightParams[4];
        // The least diffuse light the forward light gives a point, as Unity's colours
        // are (rgb): KSP's flat ambient light (RenderSettings.ambientLight, which
        // Deferred's lighting takes too) times the player's share of it.
        float nightLight[4];
        // The main body's albedo, linear rgb: a ray that misses the scene and meets the
        // body's sphere (bodySphere) takes the sun's and the moons' light on the
        // sphere there times this, in place of the sky.
        float planet[4];
        // Per light (lights): its surface lit by the star, linear rgb -- its albedo times
        // the star's colour --, for a reflected ray that meets its disc.
        float lightSurface[8];
        // The sea's frame, fixed to the main body, for the waves (RtCommon.hlsli,
        // MakeSeaFrame): a point on the sea relative to the camera, and its east and north;
        // all 0 for none.
        float sea[12];
        // The main body's cloud layer, where a mod draws one (EVE; RtKsp, RtEveClouds),
        // with RT_INPUT_CLOUDS: rows 0-2 the camera-relative world to the layer's map,
        // affine (the rotated planet's frame); then the shadow's sphere radius, the layer's
        // inner and outer radii, 1 where it is there; the shadow's strength, the coverage's
        // time fade, the fade of the whole, the density's time fade; the shadow's colour
        // (rgb) and the cloud's brightness (w). All 0 for none.
        float clouds[24];
        // prevWorldToClip for what the bodies carry -- the terrain, the scenery, the
        // sea --, which a floating origin shift under Krakensbane moves by more than the
        // camera and the vessels near it (offset + nonFrame, SharedFrame.BodyShift): the
        // denoiser's history takes whichever of the two finds the surface it had.
        float prevWorldToClipFar[16];
        // How Unity fades the sun's realtime shadow out towards its shadow distance in
        // forward rendering (AutoLight.cginc, UnityComputeForwardShadows), after the
        // screen-space shadows the raytraced sun visibility goes into, for the forward
        // light's estimate of what Unity shaded with: the fade's centre relative to the
        // camera and its type (unity_ShadowFadeCenterAndType: 1 for the distance from the
        // centre, 0 for the view depth), then its scale and bias (_LightShadowData.zw).
        // All 0 for none.
        float shadowFade[8];
        void* inputs[kRtMaxTextures];
        void* outputs[kRtMaxTextures];
    };

    constexpr uint32_t kRtReconstructMagic = RT_RECONSTRUCT_MAGIC;

    // Event 31: a frame for DLSS Ray Reconstruction, in place of DLSS Super Resolution.
    // The guides come from the raytraced frame of the same frameIndex where it had
    // kRtFrameGuides, which needs no G-buffer; otherwise from gbuffer0-2 and specular.
    // clipToWorld as RtFramePacket has it, for the guides; the other matrices as
    // Streamline takes them (sl::Constants, DLSSDOptions), without jitter; the
    // jitter in pixels and the motion vector scale as Streamline's constants say.
    // mode is sl::DLSSMode's value, preset sl::DLSSDPreset's. flags: kRtFrameReset, and
    // kRtFrameLegacySmoothness as the raytraced frame had it, for guides from gbuffer1.
    struct RtReconstructPacket
    {
        uint32_t size;
        uint32_t magic;
        uint32_t frameIndex;
        uint32_t flags;
        uint32_t renderWidth;
        uint32_t renderHeight;
        uint32_t outputWidth;
        uint32_t outputHeight;
        uint32_t mode;
        uint32_t preset;
        uint32_t reserved[2];
        float clipToWorld[16];
        float viewToClip[16];
        float clipToView[16];
        float clipToPrevClip[16];
        float prevClipToClip[16];
        float worldToView[16];
        float viewToWorld[16];
        float jitter[2];
        float motionScale[2];
        float up[4];
        float right[4];
        float forward[4];
        float nearPlane;
        float farPlane;
        float verticalFov;
        float reserved2;
        void* colour;      // the noisy frame, HDR, at the render size
        void* depth;       // R32_FLOAT, Direct3D's clip-space z, reversed
        void* motion;      // RG16_FLOAT
        void* gbuffer0;
        void* gbuffer1;
        void* gbuffer2;
        void* specular;    // the reflections' output, the hit distance in w
        void* output;      // at the output size
    };

    // The layouts the managed side shares (RtContract.h; RtNative.cs, held to it by
    // tests/ReDefinition.Tests, RtContractTests).
    static_assert(offsetof(RtReconstructPacket, size) == RT_RECONSTRUCT_PACKET_SIZE_FIELD, "RtReconstructPacket: size");
    static_assert(offsetof(RtReconstructPacket, magic) == RT_RECONSTRUCT_PACKET_MAGIC, "RtReconstructPacket: magic");
    static_assert(offsetof(RtReconstructPacket, frameIndex) == RT_RECONSTRUCT_PACKET_FRAME_INDEX, "RtReconstructPacket: frameIndex");
    static_assert(offsetof(RtReconstructPacket, flags) == RT_RECONSTRUCT_PACKET_FLAGS, "RtReconstructPacket: flags");
    static_assert(offsetof(RtReconstructPacket, renderWidth) == RT_RECONSTRUCT_PACKET_RENDER_WIDTH, "RtReconstructPacket: renderWidth");
    static_assert(offsetof(RtReconstructPacket, renderHeight) == RT_RECONSTRUCT_PACKET_RENDER_HEIGHT, "RtReconstructPacket: renderHeight");
    static_assert(offsetof(RtReconstructPacket, outputWidth) == RT_RECONSTRUCT_PACKET_OUTPUT_WIDTH, "RtReconstructPacket: outputWidth");
    static_assert(offsetof(RtReconstructPacket, outputHeight) == RT_RECONSTRUCT_PACKET_OUTPUT_HEIGHT, "RtReconstructPacket: outputHeight");
    static_assert(offsetof(RtReconstructPacket, mode) == RT_RECONSTRUCT_PACKET_MODE, "RtReconstructPacket: mode");
    static_assert(offsetof(RtReconstructPacket, preset) == RT_RECONSTRUCT_PACKET_PRESET, "RtReconstructPacket: preset");
    static_assert(offsetof(RtReconstructPacket, reserved) == RT_RECONSTRUCT_PACKET_RESERVED0, "RtReconstructPacket: reserved");
    static_assert(offsetof(RtReconstructPacket, clipToWorld) == RT_RECONSTRUCT_PACKET_CLIP_TO_WORLD, "RtReconstructPacket: clipToWorld");
    static_assert(offsetof(RtReconstructPacket, viewToClip) == RT_RECONSTRUCT_PACKET_VIEW_TO_CLIP, "RtReconstructPacket: viewToClip");
    static_assert(offsetof(RtReconstructPacket, clipToView) == RT_RECONSTRUCT_PACKET_CLIP_TO_VIEW, "RtReconstructPacket: clipToView");
    static_assert(offsetof(RtReconstructPacket, clipToPrevClip) == RT_RECONSTRUCT_PACKET_CLIP_TO_PREV_CLIP, "RtReconstructPacket: clipToPrevClip");
    static_assert(offsetof(RtReconstructPacket, prevClipToClip) == RT_RECONSTRUCT_PACKET_PREV_CLIP_TO_CLIP, "RtReconstructPacket: prevClipToClip");
    static_assert(offsetof(RtReconstructPacket, worldToView) == RT_RECONSTRUCT_PACKET_WORLD_TO_VIEW, "RtReconstructPacket: worldToView");
    static_assert(offsetof(RtReconstructPacket, viewToWorld) == RT_RECONSTRUCT_PACKET_VIEW_TO_WORLD, "RtReconstructPacket: viewToWorld");
    static_assert(offsetof(RtReconstructPacket, jitter) == RT_RECONSTRUCT_PACKET_JITTER, "RtReconstructPacket: jitter");
    static_assert(offsetof(RtReconstructPacket, motionScale) == RT_RECONSTRUCT_PACKET_MOTION_SCALE, "RtReconstructPacket: motionScale");
    static_assert(offsetof(RtReconstructPacket, up) == RT_RECONSTRUCT_PACKET_UP, "RtReconstructPacket: up");
    static_assert(offsetof(RtReconstructPacket, right) == RT_RECONSTRUCT_PACKET_RIGHT, "RtReconstructPacket: right");
    static_assert(offsetof(RtReconstructPacket, forward) == RT_RECONSTRUCT_PACKET_FORWARD, "RtReconstructPacket: forward");
    static_assert(offsetof(RtReconstructPacket, nearPlane) == RT_RECONSTRUCT_PACKET_NEAR_PLANE, "RtReconstructPacket: nearPlane");
    static_assert(offsetof(RtReconstructPacket, farPlane) == RT_RECONSTRUCT_PACKET_FAR_PLANE, "RtReconstructPacket: farPlane");
    static_assert(offsetof(RtReconstructPacket, verticalFov) == RT_RECONSTRUCT_PACKET_VERTICAL_FOV, "RtReconstructPacket: verticalFov");
    static_assert(offsetof(RtReconstructPacket, reserved2) == RT_RECONSTRUCT_PACKET_RESERVED2, "RtReconstructPacket: reserved2");
    static_assert(offsetof(RtReconstructPacket, colour) == RT_RECONSTRUCT_PACKET_COLOUR, "RtReconstructPacket: colour");
    static_assert(offsetof(RtReconstructPacket, depth) == RT_RECONSTRUCT_PACKET_DEPTH, "RtReconstructPacket: depth");
    static_assert(offsetof(RtReconstructPacket, motion) == RT_RECONSTRUCT_PACKET_MOTION, "RtReconstructPacket: motion");
    static_assert(offsetof(RtReconstructPacket, gbuffer0) == RT_RECONSTRUCT_PACKET_G_BUFFER0, "RtReconstructPacket: gbuffer0");
    static_assert(offsetof(RtReconstructPacket, gbuffer1) == RT_RECONSTRUCT_PACKET_G_BUFFER1, "RtReconstructPacket: gbuffer1");
    static_assert(offsetof(RtReconstructPacket, gbuffer2) == RT_RECONSTRUCT_PACKET_G_BUFFER2, "RtReconstructPacket: gbuffer2");
    static_assert(offsetof(RtReconstructPacket, specular) == RT_RECONSTRUCT_PACKET_SPECULAR, "RtReconstructPacket: specular");
    static_assert(offsetof(RtReconstructPacket, output) == RT_RECONSTRUCT_PACKET_OUTPUT, "RtReconstructPacket: output");
    static_assert(sizeof(RtReconstructPacket) == RT_RECONSTRUCT_PACKET_SIZE, "RtReconstructPacket: its size");
    static_assert(offsetof(RtFramePacket, size) == RT_FRAME_PACKET_SIZE_FIELD, "RtFramePacket: size");
    static_assert(offsetof(RtFramePacket, magic) == RT_FRAME_PACKET_MAGIC, "RtFramePacket: magic");
    static_assert(offsetof(RtFramePacket, frameIndex) == RT_FRAME_PACKET_FRAME_INDEX, "RtFramePacket: frameIndex");
    static_assert(offsetof(RtFramePacket, flags) == RT_FRAME_PACKET_FLAGS, "RtFramePacket: flags");
    static_assert(offsetof(RtFramePacket, width) == RT_FRAME_PACKET_WIDTH, "RtFramePacket: width");
    static_assert(offsetof(RtFramePacket, height) == RT_FRAME_PACKET_HEIGHT, "RtFramePacket: height");
    static_assert(offsetof(RtFramePacket, signals) == RT_FRAME_PACKET_SIGNALS, "RtFramePacket: signals");
    static_assert(offsetof(RtFramePacket, reserved) == RT_FRAME_PACKET_RESERVED, "RtFramePacket: reserved");
    static_assert(offsetof(RtFramePacket, clipToWorld) == RT_FRAME_PACKET_CLIP_TO_WORLD, "RtFramePacket: clipToWorld");
    static_assert(offsetof(RtFramePacket, worldToClip) == RT_FRAME_PACKET_WORLD_TO_CLIP, "RtFramePacket: worldToClip");
    static_assert(offsetof(RtFramePacket, prevWorldToClip) == RT_FRAME_PACKET_PREV_WORLD_TO_CLIP, "RtFramePacket: prevWorldToClip");
    static_assert(offsetof(RtFramePacket, sunDirection) == RT_FRAME_PACKET_SUN_DIRECTION, "RtFramePacket: sunDirection");
    static_assert(offsetof(RtFramePacket, sunColor) == RT_FRAME_PACKET_SUN_COLOR, "RtFramePacket: sunColor");
    static_assert(offsetof(RtFramePacket, skyColor) == RT_FRAME_PACKET_SKY_COLOR, "RtFramePacket: skyColor");
    static_assert(offsetof(RtFramePacket, params) == RT_FRAME_PACKET_PARAMS, "RtFramePacket: params");
    static_assert(offsetof(RtFramePacket, bodySphere) == RT_FRAME_PACKET_BODY_SPHERE, "RtFramePacket: bodySphere");
    static_assert(offsetof(RtFramePacket, ambient) == RT_FRAME_PACKET_AMBIENT, "RtFramePacket: ambient");
    static_assert(offsetof(RtFramePacket, worldToView) == RT_FRAME_PACKET_WORLD_TO_VIEW, "RtFramePacket: worldToView");
    static_assert(offsetof(RtFramePacket, viewToClip) == RT_FRAME_PACKET_VIEW_TO_CLIP, "RtFramePacket: viewToClip");
    static_assert(offsetof(RtFramePacket, cameraDelta) == RT_FRAME_PACKET_CAMERA_DELTA, "RtFramePacket: cameraDelta");
    static_assert(offsetof(RtFramePacket, jitter) == RT_FRAME_PACKET_JITTER, "RtFramePacket: jitter");
    static_assert(offsetof(RtFramePacket, depthRange) == RT_FRAME_PACKET_DEPTH_RANGE, "RtFramePacket: depthRange");
    static_assert(offsetof(RtFramePacket, lights) == RT_FRAME_PACKET_LIGHTS, "RtFramePacket: lights");
    static_assert(offsetof(RtFramePacket, lightParams) == RT_FRAME_PACKET_LIGHT_PARAMS, "RtFramePacket: lightParams");
    static_assert(offsetof(RtFramePacket, nightLight) == RT_FRAME_PACKET_NIGHT_LIGHT, "RtFramePacket: nightLight");
    static_assert(offsetof(RtFramePacket, planet) == RT_FRAME_PACKET_PLANET, "RtFramePacket: planet");
    static_assert(offsetof(RtFramePacket, lightSurface) == RT_FRAME_PACKET_LIGHT_SURFACE, "RtFramePacket: lightSurface");
    static_assert(offsetof(RtFramePacket, sea) == RT_FRAME_PACKET_SEA, "RtFramePacket: sea");
    static_assert(offsetof(RtFramePacket, clouds) == RT_FRAME_PACKET_CLOUDS, "RtFramePacket: clouds");
    static_assert(offsetof(RtFramePacket, prevWorldToClipFar) == RT_FRAME_PACKET_PREV_WORLD_TO_CLIP_FAR,
                  "RtFramePacket: prevWorldToClipFar");
    static_assert(offsetof(RtFramePacket, shadowFade) == RT_FRAME_PACKET_SHADOW_FADE, "RtFramePacket: shadowFade");
    static_assert(offsetof(RtFramePacket, inputs) == RT_FRAME_PACKET_INPUTS, "RtFramePacket: inputs");
    static_assert(offsetof(RtFramePacket, outputs) == RT_FRAME_PACKET_OUTPUTS, "RtFramePacket: outputs");
    static_assert(sizeof(RtFramePacket) == RT_FRAME_PACKET_SIZE, "RtFramePacket: its size");
    static_assert(sizeof(RtMeshDesc) == RT_MESH_DESC_SIZE, "RtMeshDesc: its size");
    static_assert(sizeof(RtMaterialDesc) == RT_MATERIAL_DESC_SIZE, "RtMaterialDesc: its size");
    static_assert(sizeof(RtInstance) == RT_INSTANCE_SIZE, "RtInstance: its size");
    static_assert(sizeof(RtGpuInstanceGroup) == RT_GPU_GROUP_SIZE, "RtGpuInstanceGroup: its size");

    class Raytracing
    {
    public:
        static Raytracing& Get();

        void Attach(const void* owner, ID3D11Device5* device11, ID3D11DeviceContext4* context11,
                    ID3D12Device* device12, ID3D12CommandQueue* queue);
        void Detach(const void* owner);

        // 1 ready, 0 no device yet, -1 not possible here; the text says why.
        int Available(char* buffer, int size) const;

        // From the main thread; the data is copied before they return.
        int SetMesh(const RtMeshDesc* desc);
        void ReleaseMesh(int32_t mesh);
        int SetMaterial(const RtMaterialDesc* desc);
        void ReleaseMaterial(int32_t material);
        // A frame's instances and GPU instance groups, taken by the frame of that index
        // (RtFramePacket's frameIndex); a frame without its own takes the newest.
        int SetInstances(const RtInstance* instances, int count, uint32_t frameIndex);
        int SetGpuGroups(const RtGpuInstanceGroup* groups, int count, uint32_t frameIndex);
        // A layer of the material textures: size by size pixels, RGBA8, rows from the top.
        int SetMaterialTexture(int layer, const uint8_t* pixels, int size);

        // From the render thread.
        void OnFrame(const void* data);
        void OnReconstruct(const void* data);
        // Ray Reconstruction no longer used by the mod: Streamline's resources for it,
        // or NGX's feature, freed (OnReconstructRelease, render event 32).
        void OnReconstructRelease();
        // Raytracing no longer used by the mod (render event 33): what is made per frame
        // size, the shared copies of Unity's textures and the meshes it released go.
        void OnRelease();

        // Whether DLSS Ray Reconstruction runs: 1 yes, 0 no, the reason in buffer --
        // through Streamline or through NGX (rrPath, RaytracingReconstruct.cpp).
        int ReconstructionAvailable(char* buffer, int size) const;
        uint64_t ReconstructedFrames();

        int Status(char* buffer, int size) const;

        // From the main thread, before it hands anything over: the generation (Status) its

        // ids are of. What it hands over for another than the service's own is dropped.

        void HandOverFor(uint32_t managedGeneration);

        uint32_t Generation() const { return generation.load(); }

        // What the last frame took on the GPU, in milliseconds: building, tracing.
        void Timings(float& build, float& trace) const;
        // Each pass's time, by signal bit, the denoiser's at kRtPassDenoiser; 0 for a pass
        // that did not run.
        void PassTimings(float* out, int count) const;

    private:
        Raytracing() = default;

        struct PendingMesh
        {
            std::vector<float> positions;
            std::vector<float> attributes;   // normal xyz, uv xy, the colour's four bytes: six floats a vertex
            std::vector<uint32_t> indices;
            uint32_t vertexCount = 0;
            // The indices' FNV-1a hash, an index at a time, for whether a mesh handed over again keeps its
            // triangles (RefitMeshLocked).
            uint64_t indexHash = 0;
            // Frames its build failed in; at kMeshAttempts it is dropped.
            uint32_t failures = 0;
        };
        static constexpr uint32_t kMeshAttempts = 30;

        struct Mesh
        {
            Microsoft::WRL::ComPtr<ID3D12Resource> positions;
            Microsoft::WRL::ComPtr<ID3D12Resource> attributes;
            Microsoft::WRL::ComPtr<ID3D12Resource> indices;
            Microsoft::WRL::ComPtr<ID3D12Resource> blas;
            // Where the mesh was handed over again with the same triangles: its structure
            // built to be updated, and the scratch its updates take.
            Microsoft::WRL::ComPtr<ID3D12Resource> updateScratch;
            uint32_t slot = 0;
            bool built = false;
            bool updatable = false;
            uint32_t vertexCount = 0;
            uint32_t indexCount = 0;
            uint64_t indexHash = 0;
        };

        struct MaterialData
        {
            float albedo[4];
            float specular[3];
            float smoothness;
            float emission[3];
            int32_t texture;
            float vertexColour[4];
            // x: the diffuse share (RtMaterialDesc), 1 where none was given.
            float look[4];
            // The highlight the material's shader draws (RtMaterialDesc).
            float highlight[4];
        };

        struct InstanceData
        {
            uint32_t meshSlot;
            uint32_t material;
            uint32_t flags;
            uint32_t pad;
            float normalRows[12];   // the inverse transpose of the transform's 3x3, rows first, padded
        };

        struct Shared
        {
            SharedTexture texture;
            ULONGLONG lastUse = 0;
            UINT64 lastFence = 0;
        };

        // A texture of the denoiser's own, with the state it is in.
        struct DenoiseTarget
        {
            Microsoft::WRL::ComPtr<ID3D12Resource> resource;
            D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_COMMON;
            DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
            bool Create(ID3D12Device* device, UINT width, UINT height, DXGI_FORMAT format);
        };

        // The guides DLSS Ray Reconstruction takes (RaytracingShaders/Reconstruct.hlsl).
        struct GuideTargets
        {
            DenoiseTarget albedo;
            DenoiseTarget specularAlbedo;
            DenoiseTarget normalRoughness;
            DenoiseTarget hitDistance;
            UINT width = 0;
            UINT height = 0;
            // The raytraced frame that wrote them, where one did.
            bool fromFrame = false;
            uint32_t frame = 0;
        };

        // A texture a pass reads or writes: a resource and the format to view it in.
        struct View
        {
            ID3D12Resource* resource = nullptr;
            DXGI_FORMAT format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        };
        static View Of(const DenoiseTarget* target)
        {
            return target != nullptr ? View{ target->resource.Get(), target->format } : View{};
        }
        static View Of(const Shared* shared)
        {
            return shared != nullptr ? View{ shared->texture.d3d12.Get(), shared->texture.format } : View{};
        }

        // AMD FSR Ray Regeneration: its DLL's functions, its context, and the guides it
        // takes (RaytracingRegenerate.cpp).
        struct Regeneration
        {
            HMODULE module = nullptr;
            ffxFunctions api = {};
            ffxContext context = nullptr;
            bool tried = false;
            UINT width = 0;
            UINT height = 0;
            uint32_t signals = 0;
            // Set where the device gave no context or a dispatch failed: not tried again
            // until the device goes.
            bool unsupported = false;
            bool reset = true;
            // The last frame AMD's denoiser took, and whether the one before this did.
            uint32_t lastFrame = 0;
            bool hadFrame = false;
            bool previousWasAmd = false;
            DenoiseTarget linearDepth;
            // The linear depth the guides pass wrote the frame before, which it reads to
            // find where the near reprojection has no surface (Regenerate.hlsl).
            DenoiseTarget linearDepthBefore;
            DenoiseTarget motion;
            DenoiseTarget normals;
            DenoiseTarget specularAlbedo;
            DenoiseTarget diffuseAlbedo;
        };

        // Per signal (kRtDenoised: sun, diffuse, specular, occlusion): the noisy trace, and
        // the history and its moments for this frame and the one before; a work texture,
        // and one in half floats per signal; the view distance and normal for this frame
        // and the one before; the signals the last run filtered, by their place. Each made
        // only for the signals that take it (EnsureDenoiseTargetsLocked,
        // EnsureFilterTargetsLocked): for all four signals they take 296 bytes a pixel,
        // 485 MB at 1707x960.
        struct DenoiseTargets
        {
            DenoiseTarget noisy[kRtDenoisedSignals];
            DenoiseTarget history[kRtDenoisedSignals][2];
            DenoiseTarget moments[kRtDenoisedSignals][2];
            DenoiseTarget work;
            DenoiseTarget workHalf[kRtDenoisedSignals];
            DenoiseTarget linearDepth[2];
            DenoiseTarget normal[2];
            UINT width = 0;
            UINT height = 0;
            UINT parity = 0;
            bool fresh = true;
            uint32_t lastPresent = 0;
            // The frames in a row, not the further traces, that did not take the denoiser.
            uint32_t framesWithout = 0;
        };
        // After this many, the denoiser's textures go (TakeNoisyTargetsLocked): Ray
        // Reconstruction, or the denoiser turned off.
        static constexpr uint32_t kDenoiseUnusedFrames = 120;

        static constexpr UINT kSlots = 3;

        // The world-space hash grid of the indirect light (RaytracingCache.cpp,
        // RaytracingShaders/RadianceCache.hlsli): per entry its fingerprint, the frame's
        // sums, its value and the frame it was last used in, whose buffer's tail holds the
        // counts (kCacheStats), copied per slot for the CPU to read; and the list of the
        // entries the frame's samples went into.
        static constexpr uint32_t kCacheStats = 8;
        struct RadianceCache
        {
            Microsoft::WRL::ComPtr<ID3D12Resource> checksums;
            Microsoft::WRL::ComPtr<ID3D12Resource> accumulation;
            Microsoft::WRL::ComPtr<ID3D12Resource> values;
            Microsoft::WRL::ComPtr<ID3D12Resource> meta;
            Microsoft::WRL::ComPtr<ID3D12Resource> touched;
            Microsoft::WRL::ComPtr<ID3D12Resource> statsReadback;
            uint32_t log2Entries = 0;
            // To be cleared before the frame's passes read it: made anew, or the camera's
            // history broken (kRtFrameReset).
            bool clear = true;
            // Whether this frame reads it, and writes it (not a further trace).
            bool active = false;
            bool update = false;
            // The camera's place in the cache's frame, summed from the frames' moves
            // (RtFramePacket.cameraDelta), in metres; the frames it counted.
            double anchor[3] = {};
            uint32_t frame = 0;
            // The cells' angle this frame, from the camera's projection.
            float cellAngle = 0.0f;
            // Which slots' lists copied the counts; the counts the latest of them read, and
            // whether they stand for the cache running, under statusLock.
            bool statsCopied[kSlots] = {};
            uint32_t stats[kCacheStats] = {};
            bool reported = false;
            ULONGLONG nextLog = 0;
        };
        static constexpr UINT kMaxMeshes = 16384;
        static constexpr UINT kFrameDescriptors = 2 * kRtMaxTextures;
        static constexpr UINT kDenoiseDescriptors = 512;
        static constexpr UINT kTableSize = 12;
        // Tables at fixed places at the end of a slot's kDenoiseDescriptors, counted back
        // from it; the denoiser's and Ray Reconstruction's guides' tables are taken from
        // its start and stay below kFixedTables.
        static constexpr UINT kForwardLightTable = kDenoiseDescriptors - 1 * kTableSize;
        static constexpr UINT kFrameGuidesTable = kDenoiseDescriptors - 2 * kTableSize;
        static constexpr UINT kGpuInstancesTable = kDenoiseDescriptors - 3 * kTableSize;
        static constexpr UINT kRegenerationGuidesTable = kDenoiseDescriptors - 4 * kTableSize;
        static constexpr UINT kRegenerationAlbedoTable = kDenoiseDescriptors - 5 * kTableSize;
        static constexpr UINT kFixedTables = kRegenerationAlbedoTable;
        // The shader-visible heap: per slot its frame's views (kFrameDescriptors); the
        // meshes' index and attribute views (2 kMaxMeshes); per slot the material
        // textures' view, written with the frame so that a slot still running reads its
        // own; per slot its tables of the passes after the trace (kDenoiseDescriptors).
        static constexpr UINT kMaterialViews = kSlots * kFrameDescriptors + 2 * kMaxMeshes;
        static constexpr UINT kSlotTables = kMaterialViews + kSlots;
        static constexpr UINT kHeapDescriptors = kSlotTables + kSlots * kDenoiseDescriptors;

        struct Slot
        {
            Microsoft::WRL::ComPtr<ID3D12CommandAllocator> allocator;
            Microsoft::WRL::ComPtr<ID3D12Resource> constants;
            uint8_t* constantsMapped = nullptr;
            Microsoft::WRL::ComPtr<ID3D12Resource> instanceDescs;
            Microsoft::WRL::ComPtr<ID3D12Resource> instanceData;
            Microsoft::WRL::ComPtr<ID3D12Resource> materials;
            UINT instanceCapacity = 0;
            UINT materialCapacity = 0;
            // With instances from the GPU: all instances in default buffers the GPU
            // completes, and the groups for its pass.
            Microsoft::WRL::ComPtr<ID3D12Resource> sceneDescs;
            Microsoft::WRL::ComPtr<ID3D12Resource> sceneData;
            Microsoft::WRL::ComPtr<ID3D12Resource> gpuGroups;
            UINT sceneCapacity = 0;
            UINT gpuGroupCapacity = 0;
            // The state the scene buffers are in once the slot's list has run, and the one
            // its recorded list leaves them in, taken over only when it is executed.
            D3D12_RESOURCE_STATES sceneState = D3D12_RESOURCE_STATE_COMMON;
            D3D12_RESOURCE_STATES sceneStateRecorded = D3D12_RESOURCE_STATE_COMMON;
            D3D12_GPU_VIRTUAL_ADDRESS instanceDataAddress = 0;
            UINT64 done = 0;
            // The last list of another slot that read this slot's scene (a further trace of
            // the same frame, ReuseSceneLocked): its instances and materials are written by
            // the CPU when the slot is taken again, so that waits for this one too.
            UINT64 sceneReadUntil = 0;
            // Whether the slot's list resolves its timestamps: not for a frame that stopped
            // early, nor for Ray Reconstruction's.
            bool timed = false;
            // Whether its last list was an extra trace (kRtFrameExtra), timed apart.
            bool extra = false;
            // What the slot's list uses, held until the slot comes round again.
            std::vector<Microsoft::WRL::ComPtr<ID3D12Resource>> held;
        };

        // One frame's work between the steps of OnFrame (Raytracing.cpp).
        struct FrameWork
        {
            const RtFramePacket* packet = nullptr;
            UINT slotNumber = 0;
            Slot* slot = nullptr;
            Shared* inputs[kRtMaxTextures] = {};
            Shared* outputs[kRtMaxTextures] = {};
            // With the denoiser, its noisy texture an output slot's pass writes in place of
            // Unity's.
            DenoiseTarget* noisyFor[kRtMaxTextures] = {};
            bool denoising = false;
            bool extra = false;
            UINT timestampBase = 0;
            // The frame's views in the heap: t1-t8, then u0-u7.
            D3D12_GPU_DESCRIPTOR_HANDLE frameTable = {};
            // Unity's textures back into COMMON once the passes are done.
            std::vector<D3D12_RESOURCE_BARRIER> back;
            uint32_t built = 0;
            UINT instanceCount = 0;
            // The slot whose scene the passes read: the frame's own, or for a further trace
            // of the same frame (the ocean's) the trace's before it (ReuseSceneLocked).
            Slot* sceneSlot = nullptr;
            UINT sceneSlotNumber = 0;
        };

        // OnFrame's steps, in the order it takes them (Raytracing.cpp).
        void EvictTexturesLocked();
        // The shared copies of the frame's textures, its inputs copied into them.
        bool TakeTexturesLocked(FrameWork& frame);
        // The next slot, its list reset, the first timestamp.
        bool BeginListLocked(FrameWork& frame);
        // The released meshes retired, the pending ones built; how many were built.
        uint32_t BuildMeshesLocked(Slot& slot);
        bool UploadMaterialTexturesLocked(Slot& slot);
        bool TakeNoisyTargetsLocked(FrameWork& frame);
        // A further trace of the frame whose scene was just built (kRtFrameExtra with
        // kRtFrameSameFrame, no mesh built in between): its scene is that one. False if
        // there is none to take.
        bool ReuseSceneLocked(FrameWork& frame);
        void WriteFrameViewsLocked(FrameWork& frame);
        void WriteConstantsLocked(const FrameWork& frame);
        void TransitionTexturesLocked(FrameWork& frame);
        void BindFrameLocked(const FrameWork& frame);
        void RunPassesLocked(FrameWork& frame);
        void DispatchPassLocked(FrameWork& frame, uint32_t signal, ID3D12PipelineState* pipeline);
        // AMD FSR Ray Regeneration and ReDefinition's denoiser, in the denoiser's place
        // among the passes.
        void RunDenoisersLocked(FrameWork& frame);
        void WriteFrameGuidesLocked(FrameWork& frame);
        // The radiance cache (RaytracingCache.cpp): made, cleared or let go as the settings
        // say, before the frame's constants; its constants and bindings; its clearing or the
        // frame's counts zeroed before the passes; its resolve after them; the counts read
        // once the slot's list is done.
        void PrepareRadianceCacheLocked(FrameWork& frame);
        void WriteRadianceCacheConstantsLocked(RtFrameConstants& constants) const;
        void BindRadianceCacheLocked();
        void BeginRadianceCacheLocked(FrameWork& frame);
        void ResolveRadianceCacheLocked(FrameWork& frame);
        void ReadRadianceCacheStatsLocked(UINT slotNumber);
        void ReleaseRadianceCacheLocked(Slot* holder);
        // The pass's pipeline with the cache where this frame uses it (the diffuse light's and
        // the reflections' own), the given one otherwise.
        ID3D12PipelineState* CachedPipelineLocked(uint32_t signal, ID3D12PipelineState* plain);
        // The textures back, the list submitted, the results copied into Unity's textures.
        void FinishFrameLocked(FrameWork& frame);

        // The device's objects (RaytracingDevice.cpp).
        bool EnsureDeviceLocked();
        bool LoadShadersLocked();
        bool BuildPipelinesLocked();
        Shared* SharedForLocked(void* unityTexture, std::string& problem);
        // The scene (RaytracingScene.cpp).
        bool TakeDescriptorSlotLocked(uint32_t& slot);
        bool UploadMeshLocked(Mesh& mesh, const PendingMesh& pending, Slot& slot);
        // A built mesh handed over again with the same vertex count and indices: its new
        // vertices into its own buffers and its structure updated, not made anew. False,
        // with nothing recorded, where a buffer could not be made.
        bool RefitMeshLocked(Mesh& mesh, const PendingMesh& pending, Slot& slot);
        // A mesh's buffers held by the slot, its descriptor slot free again once the
        // slot's list is done.
        void RetireMeshLocked(Mesh& mesh, Slot& slot);
        // Whether a material's texture cuts its surface out.
        bool IsCutoutLocked(int32_t material) const;
        bool BuildSceneLocked(Slot& slot, UINT slotNumber, uint32_t frameIndex, Shared* gpuInstances, UINT& instanceCount);
        // The GPU's instances after the CPU's in the slot's scene buffers.
        bool AddGpuInstancesLocked(Slot& slot, UINT slotNumber, Shared* gpuInstances, const std::vector<RtGpuInstanceGroup>& given,
                                   const std::unordered_map<int32_t, uint32_t>& materialIndex, UINT cpuCount, UINT& total);
        bool EnsureUploadBuffer(Microsoft::WRL::ComPtr<ID3D12Resource>& buffer, UINT64 size);
        void WriteMaterialViewLocked(UINT slotNumber);
        bool EnsureMaterialTexturesLocked(UINT layers, Slot& slot);
        // The denoiser's textures (DenoiseTargets): the noisy ones of the signals by their
        // place in kRtDenoised, then the filter's for the signals it filters; each let go
        // into the slot's held resources where it is no longer taken.
        bool EnsureDenoiseTargetsLocked(UINT width, UINT height, uint32_t noisy, Slot& slot);
        bool EnsureFilterTargetsLocked(uint32_t present, Slot& slot);
        void DropDenoiseTargetLocked(DenoiseTarget& target, Slot& slot);
        void DropDenoiseTargetsLocked(Slot& slot);
        void UseLocked(DenoiseTarget& target, D3D12_RESOURCE_STATES wanted);
        // Views for one dispatch of the passes in space 3: eight to read, four to write.
        // Returns the table's start on the GPU; the writes follow the reads.
        D3D12_GPU_DESCRIPTOR_HANDLE TableLocked(UINT slotNumber, UINT& used, const View (&reads)[8],
                                                const View (&writes)[4]);
        // A table of TableLocked's layout bound: its reads at root parameter 10, its writes
        // at 11.
        void BindTablesLocked(D3D12_GPU_DESCRIPTOR_HANDLE table);
        void RunDenoiserLocked(const RtFramePacket& packet, UINT slotNumber, Shared* const* outputs, Shared* motionInput);
        // AMD FSR Ray Regeneration for the diffuse light and the reflections where its DLL
        // runs on this GPU; false where ReDefinition's denoiser takes them.
        bool RegenerationReadyLocked(const RtFramePacket& packet);
        bool RunRegenerationLocked(const RtFramePacket& packet, UINT slotNumber, Shared* const* outputs, Shared* motionInput,
                                   Slot& slot);
        void ReleaseRegenerationLocked();
        // The guides at this size, the ones before held by the slot until it is done.
        bool EnsureGuidesLocked(std::vector<Microsoft::WRL::ComPtr<ID3D12Resource>>& held, UINT width, UINT height);
        bool EnsureDefaultBuffer(Microsoft::WRL::ComPtr<ID3D12Resource>& buffer, UINT64 size,
                                 D3D12_RESOURCE_STATES state, D3D12_RESOURCE_FLAGS flags);
        // Closes the list and executes it; the fence value it signals, or 0 where the list
        // could not be closed and the device's objects were let go of.
        UINT64 SubmitLocked(Slot& slot, Shared* const* inputs, Shared* const* outputs);
        void WaitForSlotLocked(Slot& slot);
        void WaitForAllLocked();
        void ReleaseLocked();
        void SetProblem(const std::string& text);

        mutable std::mutex mutex;

        const void* owner = nullptr;
        std::atomic<bool> attached{ false };
        Microsoft::WRL::ComPtr<ID3D11Device5> device11;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext4> context11;
        Microsoft::WRL::ComPtr<ID3D12Device5> device;
        Microsoft::WRL::ComPtr<ID3D12CommandQueue> queue;
        // The device for Status, and the debug layer's messages where it is on
        // (REDEFINITION_D3D12_DEBUG), under statusLock: Status, from the main thread,
        // does not wait for the mutex.
        Microsoft::WRL::ComPtr<ID3D12Device5> statusDevice;
        Microsoft::WRL::ComPtr<ID3D12InfoQueue> debugMessages;
        int tier = 0;
        std::string unavailable;

        std::map<std::string, std::vector<uint8_t>> shaders;
        Microsoft::WRL::ComPtr<ID3D12RootSignature> rootSignature;
        std::map<uint32_t, Microsoft::WRL::ComPtr<ID3D12PipelineState>> pipelines;
        std::map<std::string, Microsoft::WRL::ComPtr<ID3D12PipelineState>> denoisePipelines;
        DenoiseTargets denoise;
        GuideTargets guides;
        Regeneration regeneration;
        std::string regenerationWhy;
        RadianceCache cache;

        Microsoft::WRL::ComPtr<ID3D12DescriptorHeap> heap;
        UINT increment = 0;
        std::vector<uint32_t> freeSlots;
        // Descriptor slots of meshes gone, free again once the queue is past the value.
        std::vector<std::pair<uint32_t, UINT64>> retiredSlots;

        Microsoft::WRL::ComPtr<ID3D12Fence> copied12;
        Microsoft::WRL::ComPtr<ID3D11Fence> copied11;
        UINT64 copiedValue = 0;
        Microsoft::WRL::ComPtr<ID3D12Fence> fence12;
        Microsoft::WRL::ComPtr<ID3D11Fence> fence11;
        HANDLE fenceEvent = nullptr;
        UINT64 fenceValue = 0;
        // Whether Ray Reconstruction was evaluated since its resources were last freed,
        // and through which: Streamline's sl.dlss_d or NGX (DlssRayReconstruction.h).
        bool reconstructionEvaluated = false;
        bool reconstructionThroughNgx = false;
        // rrPath (Config.h) as the file had it when the swapchain was made (Attach).
        std::atomic<int> rrPath{ 0 };
        Slot slots[kSlots];
        UINT slotIndex = 0;
        Microsoft::WRL::ComPtr<ID3D12GraphicsCommandList4> list;

        Microsoft::WRL::ComPtr<ID3D12Resource> tlas;
        Microsoft::WRL::ComPtr<ID3D12Resource> tlasScratch;
        UINT64 tlasSize = 0;
        UINT64 tlasScratchSize = 0;

        Microsoft::WRL::ComPtr<ID3D12QueryHeap> timestamps;
        Microsoft::WRL::ComPtr<ID3D12Resource> timestampReadback;
        UINT64 timestampFrequency = 0;

        Microsoft::WRL::ComPtr<ID3D12Resource> materialTextures;
        UINT materialTextureLayers = 0;

        // From the main thread through the hand-over below, taken by the next frame.
        std::unordered_map<int, std::vector<uint8_t>> pendingTextures;
        std::unordered_map<int32_t, PendingMesh> pendingMeshes;
        std::vector<int32_t> pendingReleases;
        std::unordered_map<int32_t, MaterialData> materialData;
        struct FrameScene
        {
            uint32_t frameIndex = kRtAnyFrame;
            std::vector<RtInstance> instances;
            std::vector<RtGpuInstanceGroup> groups;
            // In the hand-over: which of the two the main thread set, or the groups taken
            // from a scene before it that had them set.
            bool instancesSet = false;
            bool groupsSet = false;
        };
        // The last few frames' scenes, newest last.
        std::deque<FrameScene> frameScenes;

        // What the main thread hands over (SetMesh, ReleaseMesh, SetMaterial,
        // ReleaseMaterial, SetInstances, SetGpuGroups, SetMaterialTexture), written under
        // handOverLock alone: the main thread never waits for the service's mutex, which
        // the render thread holds through a frame, its waits for the GPU and its builds
        // included. The render thread swaps it for the empty one under that lock at the
        // start of a frame and takes it into the pending state above (TakeHandOverLocked).
        struct HandOver
        {
            std::unordered_map<int, std::vector<uint8_t>> textures;
            // A mesh is in one of the two, by what was asked of it last.
            std::unordered_map<int32_t, PendingMesh> meshes;
            std::vector<int32_t> releases;
            // A material's data, or false where it was released.
            std::unordered_map<int32_t, std::pair<bool, MaterialData>> materials;
            std::deque<FrameScene> scenes;
        };
        std::mutex handOverLock;
        HandOver handOver;
        HandOver handOverTaken;
        void TakeHandOverLocked();

        std::unordered_map<int32_t, Mesh> meshes;
        std::unordered_map<void*, Shared> textures;
        // The descriptor slots ran out once on this device (logged once).
        bool meshSlotsFull = false;
        ULONGLONG nextEviction = 0;

        // Counts the devices the service has let go of: the managed side hands its
        // meshes over again when it changes.
        std::atomic<uint32_t> generation{ 0 };
        // The generation the managed side hands over for (HandOverFor), UINT32_MAX until
        // it says: a mesh, material, texture or scene it hands over for another than the
        // service's own carries ids of a device let go of, which it resends under new ids
        // once it sees the new generation -- built, they would never be released. Read
        // and changed under handOverLock, as generation is changed.
        uint32_t handOverGeneration = UINT32_MAX;
        bool HandOverCurrentLocked() const
        {
            return handOverGeneration == UINT32_MAX || handOverGeneration == generation.load();
        }

        mutable std::mutex statusLock;
        std::string lastProblem;
        uint64_t frames = 0;
        uint64_t reconstructed = 0;
        uint64_t regenerated = 0;
        uint32_t liveMeshes = 0;
        uint32_t lastInstances = 0;
        uint32_t lastBuilt = 0;
        int slowFramesLogged = 0;
        int bigBuildsLogged = 0;
        float buildMs = 0.0f;
        float traceMs = 0.0f;
        // The same of the last extra trace (the ocean's), timed apart.
        float extraBuildMs = 0.0f;
        float extraTraceMs = 0.0f;
        // The last scene built: its slot and frame, for a further trace of that frame.
        // The scene's structure (tlas) is one; no other build comes between.
        Slot* builtSceneSlot = nullptr;
        UINT builtSceneSlotNumber = 0;
        uint32_t builtSceneFrame = 0;
        UINT builtSceneInstances = 0;
        float passMs[kRtPassTimings] = {};
    };
}
