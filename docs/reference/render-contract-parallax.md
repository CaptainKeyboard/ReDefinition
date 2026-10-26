# Render contract of Parallax Continued

**For:** anyone who makes ReDefinition's rays show what Parallax's raster pass shows.
**You need:** the clone of Parallax Continued and the installed `ParallaxContinued` folder, to
check a row.
**You get:** what Parallax puts on screen, from which input, and what a raytracer must know to
see the same.

Every claim carries a mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](../development/writing-these-pages.md)). `C:` is the clone's `Mod
Source/Parallax/`, `S:` is its `Assets/Shaders/`.

## What was read

| What | Version and state | Mark |
|---|---|---|
| Clone, `References/KspUpscaler/Parallax-Continued` | commit `3cc1f83`, 2026-10-20, tag 1.0.6 | **[src]** |
| Installed `ParallaxContinued/Plugins/ParallaxContinued.dll` | file date 2026-09-25, decompiled | **[src]** |
| Difference that matters | the clone evaluates scatters in `Camera.onPreCull` for the flight cameras only; the installed build does it in `ScatterManager.LateUpdate` in every scene | **[src]** |
| Installed shader bundles | `ParallaxScatters`, `ParallaxTerrain`, `ParallaxCompute`: read as source in the clone, not opened | **[open]** |

Where the two builds differ, the installed one decides. `tools/checks/check_foreign_code.ps1`
pins the fields below (`ForeignAssumptions.ParallaxScatters`).

## The three parts

| Part | What it draws | Where it lives |
|---|---|---|
| Terrain | Tessellated terrain shaders on the PQS quads: `Custom/Parallax` and, for the space centre's ground, `Custom/ParallaxKSCTerrain` | **[src]** `S:Terrain/Parallax.shader`, `S:Terrain/KSCTerrain.shader` |
| Scatters | Grass, rocks and ferns as instanced meshes from compute-shader buffers | **[src]** `C:Scatter System/` |
| Scaled | The planet seen from far, with raymarched shadows | **[src]** `C:Scaled System/` |

A raytracer reads the terrain through its mesh and a colour rule, the scatters through their
append buffers, and the scaled planet not at all.

## Terrain

* Parallax's terrain shader has a `Deferred` pass besides `ForwardBase`, `ShadowCaster` and
  `ForwardAdd`. Its albedo mixes biplanar textures by the quad's vertex colour, slope and
  height **[src]** (`S:Terrain/Parallax.shader`, `ParallaxUtils.cginc`).
* The vertex colour is the planet's colour map, written into the quad's `colors32`. A quad's
  colours say nothing of which sphere it belongs to: the ocean's `PQS` has quads of its own
  over the same area **[src]**.
* The space centre's ground meshes are not `PQ` objects. `KSCTerrain.shader` has no
  tessellation and takes a `_GrassColor` for the colour of the land under it **[src]**.
* The mesh is displaced in the vertex stage, so the raytracer's flag `RT_INSTANCE_TERRAIN` asks
  for a tolerance (`RtCommon.hlsli`) **[src]**.

## Scatters: what runs when

1. `ScatterManager` is a `KSPAddon` at `PSystemSpawn` and stays alive across scenes **[src]**.
2. Each frame it clears every active renderer's append buffers (`PreRender`,
   `SetCounterValue(0)`).
3. For every `ScatterSystemQuadData` whose quad is visible it runs the compute shader
   `Evaluate` (`ScatterData.Evaluate`). The shader culls by `_WorldSpaceReferencePosition` and
   `_MaxRange`, and sorts the instance into the buffer of LOD 0, 1 or 2 by the camera's
   distance **[src]** (`S:Scatters/TerrainScatters.compute`).
4. `ScatterRenderer.Render` copies each buffer's count into the indirect arguments and calls
   `Graphics.DrawMeshInstancedIndirect(..., layer 15, Camera.main)` on Direct3D 11 **[src]**.

The reference position is `RuntimeOperations.vectorCraftPos` for a scatter with
`useCraftPosition` and `vectorCameraPos` for the others. The camera is
`FlightCamera.fetch?.mainCamera`, and the vessel is the active one, else the camera **[src]**
(`C:RuntimeOperations.cs`, `UpdateCameraData`).

## What an instance carries

A buffer holds one `float4x4` per instance. Rows 0 to 2 are the mesh's transform. Row 3 is the
colour: the average of the quad's three vertex colours for a scatter coloured by terrain, else
`(1, 1, 1, 1)` **[src]** (`S:Scatters/ScatterUtils.cginc`, `ENCODE_INSTANCING_DATA`).

The blade shader multiplies its albedo by that colour: `_MainTex * _Color * instance colour`
**[src]** (`S:Scatters/InstancingShaders/InstancedSolid.shader`, `DECODE_INSTANCE_DATA`).
A raytracer that reads only rows 0 to 2 sees untinted blades. ReDefinition carries the colour
as the fourth texel of each place (`RT_INSTANCE_TEXELS`), and the harness checks it **[meas]**.

## The shader's own parts that have no copy in the rays

* `Custom/ParallaxInstancedSolid` has four passes. Its queue is 2000 **[meas]**.
* Wind moves the vertices in the vertex stage (`PROCESS_WIND`). The rays see the blades still
  **[src]**.
* The despawn near the range's end happens in the compute shader, from `_RangeFadeStart` (0.8
  of the range by default), so a faded blade is not in the buffer **[src]**
  (`S:Scatters/ScatterUtils.cginc`).

## The space centre

| Observation | Mark |
|---|---|
| The Kerbin grass's three levels of detail hold 0, 0 and 14 400 to 17 166 instances in the space centre scene, the last level's mesh has 18 triangles | **[meas]** |
| Those instances lie about the world's origin at 795 to 1249 m from the camera, while the grass's range is 256 m | **[meas]** |
| `vectorCraftPos` read in the same scene equals the camera's position | **[meas]** |
| Without the raytracing no blade shows at that distance | **[meas]** |
| With `rtFoliageTransmission` 1 the dark patch in the rays disappears; the blades' sun occlusion makes it | **[meas]** |
| Why the compute shader keeps instances beyond its range here | **[open]** |

The rays therefore cull a GPU instance beyond the scatter's range about the point Parallax
culls it about (`RtGpuItem.Reach`, `ReDefinitionRtScatter.compute`). The lifted clumps of the
lawns (`Scenery/KscGrass.cs`) have no reach.

## What the raster result depends on that a raytracer does not see

* The meadow's look beyond the grass's range comes from the terrain texture. The rays replace
  it with the shade of `MeadowShade` (`RtCommon.hlsli`).
* The G-buffer of the terrain and the blades is Deferred's, with Parallax's own `Deferred` pass
  ([render-contract-deferred.md](render-contract-deferred.md)).
* The scaled planet's raymarched shadows have no geometry **[src]**.
