// The contract between the managed side (src/Proxy/RtNative.cs), the proxy
// (Raytracing.h) and the raytracing shaders (RtCommon.hlsli): the values and layouts all
// three read, in one place. Plain #defines, so that C++ and HLSL include it alike;
// tests/ReDefinition.Tests (RtContractTests) reads it and holds RtNative's constants and
// the managed packets' offsets to it, Raytracing.h's static_asserts hold the proxy's
// structures to it, and cbuffer Frame places its fields by it (packoffset).
//
// RT_<name> is RtNative's constant <Name> where it has one.

#ifndef REDEFINITION_RT_CONTRACT
#define REDEFINITION_RT_CONTRACT

// The render events' ids the managed side issues on Unity's render thread (ManagedBridge.cpp,
// DispatchRenderEvent): a frame, a Ray Reconstruction frame, its release, the release of all.
#define RT_FRAME_EVENT 30
#define RT_RECONSTRUCT_EVENT 31
#define RT_RECONSTRUCT_RELEASE_EVENT 32
#define RT_RELEASE_EVENT 33

// The magic in the second field of a mesh, a material, a frame and a Ray Reconstruction
// packet, which the proxy checks with the size to reject a layout it does not share.
#define RT_MESH_MAGIC 0x4B53524Du          // 'KSRM'
#define RT_MATERIAL_MAGIC 0x4B535241u      // 'KSRA'
#define RT_FRAME_MAGIC 0x4B535246u         // 'KSRF'
#define RT_RECONSTRUCT_MAGIC 0x4B535252u   // 'KSRR'

// A frame's flags (Raytracing.h, kRtFrame*).
#define RT_FLAG_SAME_FRAME 0x1u
#define RT_FLAG_RESET 0x2u
#define RT_FLAG_DENOISE 0x4u
#define RT_FLAG_MOTION_VECTORS 0x8u
#define RT_FLAG_GUIDES 0x10u
#define RT_FLAG_EXTRA 0x20u
#define RT_FLAG_WATER 0x40u
#define RT_FLAG_WATER_NORMALS 0x80u
#define RT_FLAG_LEGACY_SMOOTHNESS 0x100u
#define RT_FLAG_SURFACE_INTENT 0x200u
// The probe's check of the cloud layer: the sun's visibility is the clouds' transmittance
// towards the sun alone (RtCommon.hlsli, CloudTransmittance), to lay over EVE's own shadow.
#define RT_FLAG_CLOUD_SHADOW 0x400u
// For measuring what the cut-out surfaces' tests cost (the proxy's rtCutoutTest): their
// hits decided without reading the texture, half of the triangles counting; or every
// surface taken as opaque, as no candidate hit stops a ray.
#define RT_FLAG_CUTOUT_PLAIN 0x800u
#define RT_FLAG_CUTOUT_OPAQUE 0x1000u
// The radiance cache (RadianceCache.hlsli; the proxy's rtRadianceCache), set by the proxy:
// the paths read it at their hits, and, in a frame's own trace but not in a further one
// (the ocean's), write their samples into it.
#define RT_FLAG_RADIANCE_CACHE 0x2000u
#define RT_FLAG_RADIANCE_CACHE_UPDATE 0x4000u
// What of the sun's light a cut-out surface -- a blade of grass, a fern -- lets through,
// in 255ths, in these bits of the flags (the proxy's rtFoliageTransmission).
#define RT_FLAG_FOLIAGE_SHIFT 16u
#define RT_FLAG_FOLIAGE_MASK 0xFFu

// The signals a frame traces (Raytracing.h, kRtSignal*).
#define RT_SIGNAL_SUN 0x1u
#define RT_SIGNAL_OCCLUSION 0x2u
#define RT_SIGNAL_DIFFUSE 0x4u
#define RT_SIGNAL_SPECULAR 0x8u
#define RT_SIGNAL_FORWARD_LIGHT 0x10u
#define RT_SIGNAL_PRIMARY_CHECK 0x80u

// The output slot each signal writes.
#define RT_OUTPUT_SUN 0
#define RT_OUTPUT_DIFFUSE 1
#define RT_OUTPUT_SPECULAR 2
#define RT_OUTPUT_OCCLUSION 3
#define RT_OUTPUT_FORWARD_LIGHT 4
// The surfaces as their materials mean them (RT_FLAG_SURFACE_INTENT): the specular colour
// and smoothness in the G-buffer's terms, and how each pixel's were found (the mode / 8).
#define RT_OUTPUT_SURFACE 5
#define RT_OUTPUT_SURFACE_MODE 6
#define RT_OUTPUT_PRIMARY_CHECK 7
// Without a G-buffer, the forward light's ratio (Raytracing.hlsl, ForwardLight); the
// primary check, a test of its own, uses the same slot.
#define RT_OUTPUT_FORWARD_RATIO 7

// How a material's shine is meant (RtMaterialDesc.flags; RtMaterialIntent.cs): the
// G-buffer's kept, none, KSP's legacy Blinn-Phong highlight with its gloss read back from
// Deferred's smoothness (LegacyToStandard) or from Unity's own legacy specular colour, or
// fixed values; in the mode output also water and a pixel no material was found for.
#define RT_SURFACE_KEEP 0
#define RT_SURFACE_DRY 1
#define RT_SURFACE_LEGACY 2
#define RT_SURFACE_FIXED 3
#define RT_SURFACE_UNITY_LEGACY 4
#define RT_SURFACE_WATER 5
#define RT_SURFACE_MISSED 6
// The space centre's ground: the terrain's shine where the drawn colour is its lawn,
// none on its tarmac and concrete (RtCommon.hlsli, SurfaceIntent).
#define RT_SURFACE_GROUND 7

// What the camera's ray through a pixel met, in the surface mode output's z (the region / 8),
// for the probe's surface images (tools/surface_sheet.py): the terrain at the drawn
// surface, the space centre's ground there, a blade or other scatter there, ground behind
// a blade the rays meet in front of the drawn surface, nothing near, something else there,
// and no material at the drawn surface (something in front of it or beyond it).
#define RT_REGION_TERRAIN 1
#define RT_REGION_GROUND 2
#define RT_REGION_BLADE 3
#define RT_REGION_BEHIND_BLADE 4
#define RT_REGION_NOTHING 5
#define RT_REGION_OTHER 6
#define RT_REGION_OTHER_MISSED 7

// The input slots (Raytracing.h, kRtInput*).
#define RT_INPUT_DEPTH 0
#define RT_INPUT_G_BUFFER0 1
#define RT_INPUT_G_BUFFER1 2
#define RT_INPUT_G_BUFFER2 3
#define RT_INPUT_MOTION 4
#define RT_INPUT_ENVIRONMENT 5
#define RT_INPUT_INSTANCES 6
// The main body's cloud layer (RtCommon.hlsli, CloudTransmittance): an equirectangular map,
// r its coverage, g the density curve at the shadow's height (ReDefinitionRtClouds.shader).
#define RT_INPUT_CLOUDS 7
#define RT_INSTANCES_PER_ROW 256

// An instance's flags (Raytracing.h, kRtInstance*).
#define RT_INSTANCE_TERRAIN 0x1u
#define RT_INSTANCE_CUTOUT 0x2u
// The space centre's buildings and grounds (RtScenerySource): their materials' untouched
// defaults are taken as matt (RtMaterialIntent).
#define RT_INSTANCE_SCENERY 0x4u

// Set by the service in the flags of a group of GPU instances (Raytracing's AddGpuInstancesLocked),
// high above the managed side's: the group's cut-out instances do not take FORCE_NON_OPAQUE
// because their opacity micromaps decide the opaque parts, or switch the micromaps off
// (RaytracingOmm.cpp). GpuInstances.hlsl turns them into the instances' flags and leaves
// them out of what the passes read.
#define RT_GROUP_NO_NON_OPAQUE 0x100000u
#define RT_GROUP_DISABLE_OMMS 0x200000u
#define RT_GROUP_SERVICE_FLAGS 0x300000u

#define RT_ANY_FRAME 0xFFFFFFFFu
#define RT_MATERIAL_TEXTURE_SIZE 64
#define RT_MAX_MATERIAL_TEXTURES 2048

// The hit distances the signals carry in w (RtCommon.hlsli): a miss as a half float's
// largest value, a reflected ray that met a moon's disc; in the water's frame a ray that
// met the scene at most RT_WATER_HIT_LIMIT, one that met nothing RT_WATER_SKY.
#define RT_FP16_MAX 65504.0f
#define RT_DISC_HIT 60000.0f
#define RT_WATER_HIT_LIMIT 59999.0f
#define RT_WATER_SKY 65000.0f

// The structures' sizes in bytes.
#define RT_MESH_DESC_SIZE 64
#define RT_MATERIAL_DESC_SIZE 96
#define RT_INSTANCE_SIZE 64
#define RT_GPU_GROUP_SIZE 32
#define RT_FRAME_PACKET_SIZE 1056
#define RT_RECONSTRUCT_PACKET_SIZE 640

// RtFramePacket's fields, by their offset in bytes.
#define RT_FRAME_PACKET_SIZE_FIELD 0
#define RT_FRAME_PACKET_MAGIC 4
#define RT_FRAME_PACKET_FRAME_INDEX 8
#define RT_FRAME_PACKET_FLAGS 12
#define RT_FRAME_PACKET_WIDTH 16
#define RT_FRAME_PACKET_HEIGHT 20
#define RT_FRAME_PACKET_SIGNALS 24
#define RT_FRAME_PACKET_RESERVED 28
#define RT_FRAME_PACKET_CLIP_TO_WORLD 32
#define RT_FRAME_PACKET_WORLD_TO_CLIP 96
#define RT_FRAME_PACKET_PREV_WORLD_TO_CLIP 160
#define RT_FRAME_PACKET_SUN_DIRECTION 224
#define RT_FRAME_PACKET_SUN_COLOR 240
#define RT_FRAME_PACKET_SKY_COLOR 256
#define RT_FRAME_PACKET_PARAMS 272
#define RT_FRAME_PACKET_BODY_SPHERE 304
#define RT_FRAME_PACKET_AMBIENT 320
#define RT_FRAME_PACKET_WORLD_TO_VIEW 368
#define RT_FRAME_PACKET_VIEW_TO_CLIP 432
#define RT_FRAME_PACKET_CAMERA_DELTA 496
#define RT_FRAME_PACKET_JITTER 512
#define RT_FRAME_PACKET_DEPTH_RANGE 520
#define RT_FRAME_PACKET_LIGHTS 528
#define RT_FRAME_PACKET_LIGHT_PARAMS 592
#define RT_FRAME_PACKET_NIGHT_LIGHT 608
#define RT_FRAME_PACKET_PLANET 624
#define RT_FRAME_PACKET_LIGHT_SURFACE 640
#define RT_FRAME_PACKET_SEA 672
#define RT_FRAME_PACKET_CLOUDS 720
#define RT_FRAME_PACKET_PREV_WORLD_TO_CLIP_FAR 816
#define RT_FRAME_PACKET_SHADOW_FADE 880
#define RT_FRAME_PACKET_INPUTS 928
#define RT_FRAME_PACKET_OUTPUTS 992

// RtReconstructPacket's fields, by their offset in bytes.
#define RT_RECONSTRUCT_PACKET_SIZE_FIELD 0
#define RT_RECONSTRUCT_PACKET_MAGIC 4
#define RT_RECONSTRUCT_PACKET_FRAME_INDEX 8
#define RT_RECONSTRUCT_PACKET_FLAGS 12
#define RT_RECONSTRUCT_PACKET_RENDER_WIDTH 16
#define RT_RECONSTRUCT_PACKET_RENDER_HEIGHT 20
#define RT_RECONSTRUCT_PACKET_OUTPUT_WIDTH 24
#define RT_RECONSTRUCT_PACKET_OUTPUT_HEIGHT 28
#define RT_RECONSTRUCT_PACKET_MODE 32
#define RT_RECONSTRUCT_PACKET_PRESET 36
#define RT_RECONSTRUCT_PACKET_RESERVED0 40
#define RT_RECONSTRUCT_PACKET_RESERVED1 44
#define RT_RECONSTRUCT_PACKET_CLIP_TO_WORLD 48
#define RT_RECONSTRUCT_PACKET_VIEW_TO_CLIP 112
#define RT_RECONSTRUCT_PACKET_CLIP_TO_VIEW 176
#define RT_RECONSTRUCT_PACKET_CLIP_TO_PREV_CLIP 240
#define RT_RECONSTRUCT_PACKET_PREV_CLIP_TO_CLIP 304
#define RT_RECONSTRUCT_PACKET_WORLD_TO_VIEW 368
#define RT_RECONSTRUCT_PACKET_VIEW_TO_WORLD 432
#define RT_RECONSTRUCT_PACKET_JITTER 496
#define RT_RECONSTRUCT_PACKET_MOTION_SCALE 504
#define RT_RECONSTRUCT_PACKET_UP 512
#define RT_RECONSTRUCT_PACKET_RIGHT 528
#define RT_RECONSTRUCT_PACKET_FORWARD 544
#define RT_RECONSTRUCT_PACKET_NEAR_PLANE 560
#define RT_RECONSTRUCT_PACKET_FAR_PLANE 564
#define RT_RECONSTRUCT_PACKET_VERTICAL_FOV 568
#define RT_RECONSTRUCT_PACKET_RESERVED2 572
#define RT_RECONSTRUCT_PACKET_COLOUR 576
#define RT_RECONSTRUCT_PACKET_DEPTH 584
#define RT_RECONSTRUCT_PACKET_MOTION 592
#define RT_RECONSTRUCT_PACKET_G_BUFFER0 600
#define RT_RECONSTRUCT_PACKET_G_BUFFER1 608
#define RT_RECONSTRUCT_PACKET_G_BUFFER2 616
#define RT_RECONSTRUCT_PACKET_SPECULAR 624
#define RT_RECONSTRUCT_PACKET_OUTPUT 632

// cbuffer Frame (RtCommon.hlsli, Raytracing.h RtFrameConstants): each field's register,
// 16 bytes each, and the registers of the shaders' names within a field;
// frameIndex and frameFlags in z and w of RT_CB_FRAME_SIZE's.
#define RT_CB_CLIP_TO_WORLD 0
#define RT_CB_WORLD_TO_CLIP 4
#define RT_CB_PREV_WORLD_TO_CLIP 8
#define RT_CB_SUN_DIRECTION 12
#define RT_CB_SUN_COLOR 13
#define RT_CB_SKY_COLOR 14
#define RT_CB_PARAMS 15
#define RT_CB_PARAMS1 16
#define RT_CB_BODY_SPHERE 17
#define RT_CB_AMBIENT 18
#define RT_CB_AMBIENT_SIDE 19
#define RT_CB_AMBIENT_DOWN 20
#define RT_CB_FRAME_SIZE 21
#define RT_CB_LIGHTS 22
#define RT_CB_LIGHT_COLOR0 23
#define RT_CB_LIGHT_DIRECTION1 24
#define RT_CB_LIGHT_COLOR1 25
#define RT_CB_LIGHT_PARAMS 26
#define RT_CB_NIGHT_LIGHT 27
#define RT_CB_PLANET 28
#define RT_CB_LIGHT_SURFACE 29
#define RT_CB_LIGHT_SURFACE1 30
#define RT_CB_SEA 31
#define RT_CB_SEA_EAST 32
#define RT_CB_SEA_NORTH 33
#define RT_CB_CLOUDS 34
#define RT_CB_CLOUDS_1 35
#define RT_CB_CLOUDS_2 36
#define RT_CB_CLOUDS_3 37
#define RT_CB_CLOUDS_4 38
#define RT_CB_CLOUDS_5 39
#define RT_CB_PREV_WORLD_TO_CLIP_FAR 40
#define RT_CB_SHADOW_FADE 44
#define RT_CB_SHADOW_FADE1 45
#define RT_CB_SHADOW_FADE2 46
// The radiance cache's (RadianceCache.hlsli), the proxy's own: its anchor's tile and the
// frame it counts, the anchor within the tile and the cells' angle, its parameters, its
// sizes.
#define RT_CB_CACHE_TILE 47
#define RT_CB_CACHE_OFFSET 48
#define RT_CB_CACHE_PARAMS 49
#define RT_CB_CACHE_SIZES 50
#define RT_CB_REGISTERS 51

#endif
