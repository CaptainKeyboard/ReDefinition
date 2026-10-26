# Render contract: Kopernicus

**For:** anyone who makes ReDefinition's rays show what Kopernicus and KSP's terrain code put
on screen.
**You need:** the decompiled KSP and Kopernicus code named under *Sources*, to check a row.
**You get:** what draws terrain, ocean, scaled space, sun, rings and atmosphere, how to
read it, and what a scan of the terrain quads misses.

Every claim carries **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](../development/writing-these-pages.md)). A **[src]** names the
file and symbol. Kopernicus adds almost no drawing code of its own. It edits KSP's terrain
sphere (`PQS`), so most rows name KSP's classes.

## Sources

| Tag | What it is | Version |
|---|---|---|
| KSP | `Assembly-CSharp.dll` of the installed game, decompiled with `ilspycmd` | KSP 1.12.5, build 03190 |
| KOP | `GameData/Kopernicus/Plugins/Kopernicus.dll`, decompiled | file 1.12.249.0, KSP.log `1.0.249+4d926b3` |
| CLONE | Kopernicus source clone, paths relative to its source folder | `c19a16e`, 2026-10-20 |
| SCT | Scatterer source clone, for the ocean replacement only | `c2d0b0f`, 2025-09-14 |
| PAR | Parallax-Continued source clone, for its patches of `PQ` only | `3cc1f83`, 2026-10-20 |

* The clone is 53 commits after `4d926b3`. Where KOP and CLONE differ, KOP decides. The
  rings differ: KOP's `Ring.cs` has `useNewShader` and detail passes, CLONE's has not.
  **[src]**
* Installed Kopernicus drives the stock system only: `Config/System.cfg` is a shell of
  templates, and the empty `Kopernicus_Config.cfg` leaves every default. No planet pack is
  installed. Defaults: `EnforceShaders = false`, `CleanupKSCScatters = false`,
  `EnableKopernicusShadowManager = true`. **[src]**
* The installed Scatterer DLL was not decompiled. Its ocean replacement is read from SCT.
  **[open]** whether the installed build matches.

## Terrain: the quad tree

| Fact | Contract | Source |
|---|---|---|
| Tree | A `PQS` holds six root quads (`PQS.quads`). A `PQ` has four `subNodes` once `isSubdivided`. The drawn quads are the leaves. | **[src]** KSP `PQ.UpdateSubdivision` |
| Objects | Each quad is a GameObject with `PQ`, `MeshFilter`, `MeshRenderer`, taken from a cache and reused for other places. | **[src]** KSP `PQSCache`, `PQS.AssignQuad`, `PQS.DestroyQuad` |
| Material | One shared `PQS.surfaceMaterial` for all quads, chosen from the ultra, high, medium, low and fallback materials by `GameSettings.TERRAIN_SHADER_QUALITY`. A mod that needs `UniqueMaterialInstances` gives each quad its own. | **[src]** KSP `PQS.ApplyTerrainShaderSettings`, `PQS.useSharedMaterial` |
| Shader | KSP's `Terrain/PQS/*` shaders, among them `PQS Main - Optimised`, `PQS Main Shader`, `PQS Triplanar Zoom Rotation` and the texture-array one. Kopernicus enforces a level only if `EnforceShaders` is set. | **[src]** KOP `*Loader.SHADER_NAME`, `TerrainQualitySetter` |
| Layer | Each quad takes its sphere's layer. Kopernicus names layer 15 for local space, 10 for scaled space, 9 for scaled atmosphere. | **[src]** KSP `PQS.AssignQuad`; KOP `GameLayers` |
| Shadows | A quad casts a shadow only if `GameSettings.CELESTIAL_BODIES_CAST_SHADOWS` and `meshCastShadows`. Kopernicus's `ShadowMan` sets `meshCastShadows` and `meshRecieveShadows` on every body in flight, space centre and tracking station. | **[src]** KSP `PQS.AssignQuad`; KOP `ShadowMan.SetShadows` |
| Mesh | `PQS.BuildQuad` runs every height mod, then every colour mod, per vertex. A vertex is `directionFromCenter * vertHeight` in the sphere's own frame. ReDefinition reads the mesh on the CPU. | **[src]** KSP `PQS.BuildQuad`, `PQS.BuildVertexHeight`; `src/Scenery/KscGround.cs`, `MakeColours` |
| Edges | A neighbour's subdivision swaps the leaf's index buffer (`cacheIndices[edgeState]`) on the same `Mesh` and queues a normal update. | **[src]** KSP `PQ.UpdateVisibility` |

### What decides isVisible

* **[src]** `PQ.UpdateVisibility` calls `SetVisible` for a leaf with
  `gcd1 < sphereRoot.visibleRadius`. `gcd1` is the arc to the sphere's target times the
  radius times 1.3 (`PQ.UpdateTargetRelativity`).
  `visibleRadius` follows the target's altitude and the horizon (`PQS.UpdateTargetData`).
* **[src]** `PQ.SetVisible` sets `isVisible`, builds the mesh if needed, and enables the
  renderer unless `isForcedInvisible`.
* **[src]** `PQ.SetMasterInvisible`, which `PQS.DeactivateSphere` calls, disables the renderer
  and leaves `isVisible` set. Kopernicus's `PQS_StartSphere` postfix calls it in flight for a
  sphere that starts inactive, ocean included. Parallax's `PQ.SetVisible` and
  `PQ.SetInvisible` prefixes replace KSP's (PAR `QuadVisibilityPatch`).
* The test for "drawn" is `isVisible && isBuilt && !isForcedInvisible && meshRenderer.enabled`
  with an active GameObject. `RtTerrainSource` in `src/Raytracing/Ksp/RtKspSources.cs`
  tests the first three and `pqs.isActiveAndEnabled`, not `meshRenderer.enabled`. **[src]**
  **[open]** whether an inactive sphere ever reaches its 30 km range.
* **[src]** The target is the active vessel in flight
  (`PQSMod_CelestialBodyTransform.OnPreUpdate`).
  Without one, `PQS.OnSphereStartDefault` takes the parent's target, else `Camera.main`.
  **[open]** which branch the space centre runs.

## How the colour reaches the shader

| Channel | Written by | When | Source |
|---|---|---|---|
| Vertex colour rgb | `PQSLandControl`: the sum of the land classes' colours times their weight, if `createColors` is on | per vertex, `OnVertexBuild` | **[src]** KSP `PQSLandControl.OnVertexBuild` |
| Vertex colour alpha | `PQSLandControl`: height over `vHeightMax`, shifted by the land classes, clamped to 0 to 1. The band shaders read it. | same | **[src]** KSP `PQSLandControl.OnVertexBuild` |
| Vertex colour | `PQSMod_VertexColorMap` sets it from a `MapSO` at (`u`, `v`). `...MapBlend` lerps by `blend`. | same | **[src]** KSP `PQSMod_VertexColorMap`, `...MapBlend` |
| Vertex colour rgb | `PQSMod_MapDecalTangent` named `KSC` lerps toward its `colorMap`, keeps alpha. | same, inside the decal radius | **[src]** KSP `PQSMod_MapDecalTangent.OnVertexBuild` |
| Vertex colour | Kopernicus's `PQSMod_KSCScatterMask` writes colour only with `debugShowColorMap`. It clears `allowScatter` only with `CleanupKSCScatters`. Both are off. | same | **[src]** KOP `PQSMod_KSCScatterMask.OnVertexBuild` |
| uv0, uv2.x | `PQSMod_UVPlanetRelativePosition`: the vertex's planet-relative position | after the build, `OnQuadBuilt` | **[src]** KSP `PQSMod_UVPlanetRelativePosition` |
| uv2.y | The same mod: `1 - dot(up, normal)`, the steepness. It is written again when normals change. | `OnQuadBuilt`, `OnQuadUpdateNormals` | **[src]** KSP `PQSMod_UVPlanetRelativePosition` |
| uv3 | `PQSMod_TextureAtlas` writes the atlas layer indices and strengths straight into the mesh. | `OnQuadBuilt` | **[src]** KSP `PQSMod_TextureAtlas.OnQuadBuilt` |

* The mesh has vertex colours only if some mod requires `MeshColorChannel`
  (`PQS.reqColorChannel`). **[src]** KSP `PQS.SetupBuildDelegates`, `PQS.BuildMeshColorChannel`
* The shader's albedo is not the vertex colour. It mixes the colour with the textures by the
  alpha bands, `uv2.y` and `uv3`, then applies `_saturation`, `_contrast`, `_tintColor` and
  `_albedoBrightness`. `src/Raytracing/RtStockTerrain.cs` reads that from KSP 1.12.5's compiled
  shaders. **[src]**
* Kopernicus patches `PQSLandControl.OnVertexBuildHeight` to turn `sx` from [-0.25, 0.75]
  into [0, 1] longitude. **[src]** KOP `PQSLandControl_OnVertexBuildHeight`
* With Kopernicus, a `MapSO` can be a `MapSODemand`. A read of an unloaded map loads it when
  `AutoLoad` is set, else returns black. **[src]** KOP `MapSODemand.EnsureLoaded`

## Asking the planet's colour at a position

KSP has no `GetSurfaceColor`. A search of the decompiled `Assembly-CSharp.dll` and
`Kopernicus.dll` finds no such name. **[src]** The ways to a colour, by cost:

| Way | Signature | You get | Catch |
|---|---|---|---|
| Read the built quad | `PQ.mesh.colors32` | the colours the raster uses | only where a quad exists; rgb and alpha only |
| Height only | `PQS.GetSurfaceHeight(Vector3d radialVector)`, public | the radius at the vertex | runs height mods only; skips `PQSMod_FlattenArea` because `overrideQuadBuildCheck` is false |
| Height with flattening | `PQS.GetSurfaceHeight(Vector3d, bool overrideQuadBuildCheck)`, internal, `true` | the radius with flattening | `Vessel` and `PQSCity.Orientate` call it so; reflection needed |
| Colour | private `Mod_OnVertexBuildHeight(VertexBuildData, bool)` then private `Mod_OnVertexBuild(VertexBuildData)` | `data.vertColor` | needs the set-up below |

**[src]** KSP `PQS.GetSurfaceHeight`, `PQS.Mod_OnVertexBuildHeight`, `PQS.Mod_OnVertexBuild`,
`PQSMod_FlattenArea.OnVertexBuildHeight`, `Vessel`, `PQSCity.Orientate`.

The colour query is what Kopernicus's `PlanetTextureExporter.UpdateTextures` does. KSP's
`PQS.CreateMaps` runs the same two calls with the flag false. **[src]** The steps:

1. Call `pqs.SetupExternalRender()`. It sets `isBuildingMaps`, clears `Global_AllowScatter`,
   runs `SetupMods` and so every mod's `OnSetup` again, and sets `isFakeBuild`. Without
   `isBuildingMaps`, `PQSMod_MapDecalTangent` uses the `quadActive` of the last built quad.
   **[open]** the side effects of that call on a running flight sphere. Kopernicus then sets
   `pqs.enabled = false` for the run and `true` after it.
2. Make a `PQS.VertexBuildData`. Set `directionFromCenter` to the unit radial vector and
   `vertHeight` to `pqs.radius`.
3. Call the height method with `true`, then the colour method. `u`, `v`, `sx` and `sy` are set
   inside the height call, when a mod requires `VertexMapCoords`.
4. Read `vertColor`. Call `pqs.CloseExternalRender()`. It restores only `isBuildingMaps` and
   `Global_AllowScatter`.

* **Space of the vector:** the sphere's own frame, which turns with the body. From a world
  position use `PQS.GetRelativePosition(world)` and normalise. From latitude and longitude
  use `CelestialBody.GetRelSurfaceNVector(lat, lon)`. **[src]** KSP `PQS.GetRelativePosition`
* **Thread and state:** main thread only. `PQS.vbData` is one shared object, and the PQS
  itself calls `GetSurfaceHeight` every update (`PQS.UpdateTargetData`). `MapSO` writes its
  bilinear coordinates into its own fields (`MapSO.ConstructBilinearCoords`, patched by
  Kopernicus). **[src]**
* **Scenes:** the mods exist on a started sphere only. **[open]** the query in the space
  centre scene and on an inactive sphere.
* **What you do not get:** `uv2.y` and `uv3`. The mods write them after the build, so the
  result is not the shader's albedo (see the last section).

## The ocean sphere

* The ocean is a second `PQS`, a child GameObject named `<Body>Ocean` with `parentSphere` set.
  `PQS.ChildSpheres` is every `PQS` below the planet's. **[src]** KOP `OceanLoader`;
  KSP `PQS.ChildSpheres`
* For an ocean it creates, Kopernicus sets the radius to the body's and copies the stock
  ocean's layer. It adds the sphere to `planetFade.secondaryRenderers`. **[src]** KOP
  `OceanLoader`
* It activates, deactivates and fades with its parent. **[src]** KSP `PQS.ActivateSphere`,
  `PQS.DeactivateSphere`
* Its shader is `Terrain/PQS/Ocean Surface Quad`, with a fallback shader.
  `PQSMod_OceanFX` sets `_Mix` and `_SpecColor` each update, and `_WaterTex` and
  `_WaterTex1` with `cycleTextures`.
  **[src]** KOP `PQSOceanSurfaceQuadLoader.SHADER_NAME`; KSP `PQSMod_OceanFX.OnUpdateFinished`
* `FindObjectsOfType<PQ>()` returns every sphere's quads, ocean quads included. `PQ.sphereRoot`
  tells them apart. `src/Scenery/KscGround.cs` filters on `quad.sphereRoot == city.sphere` and
  counts the rest as `otherSpheres`. **[src]**
* `KscGround.cs` notes white vertex colours on the ocean's quads under the space centre.
  **[meas]** (a comment by the author) **[open]** which mods the stock ocean carries.
* Scatterer replaces it: `FakeOceanPQS.Apply` sets the ocean's `surfaceMaterial` to
  `Scatterer/invisible`, stops its coroutines and calls `DeactivateSphere`, again every ten
  fixed updates. The quads stay, `isVisible` stays set, the renderers are off. Scatterer
  draws its own `OceanNode` instead. **[src]** SCT `FakeOceanPQS`,
  `OceanUtils.removeStockOceans`

## The space centre

| Fact | Contract | Source |
|---|---|---|
| Objects | `PQSCity` named `KSC` and `PQSMod_MapDecalTangent` named `KSC`, both under the home body's `PQS`. The buildings and the ground meshes are children of the city. | **[src]** KOP `KSC.Start`; `src/Scenery/KscGround.cs` |
| Placement | `Orientate` sets `localPosition` to `repositionRadial * (radius + offset)`, or to the surface height with `repositionToSphereSurface`, then turns the up axis to the radial. Kopernicus overwrites those fields from its `KSC` node and restarts `SpaceCenter`. | **[src]** KSP `PQSCity.Orientate`; KOP `KSC.Start` |
| Flattening | Inside `inclusionAngle` the decal sets `vertHeight` to `Lerp(vertHeight, deformity * height / dot + (absolute ? radius + offset : 0), smoothFactor)`. Its `quadActive` and `buildHeight` come from `OnQuadPreBuild` and `OnQuadBuilt`. | **[src]** KSP `PQSMod_MapDecalTangent` |
| Ground meshes | Renderers under the city with `KSP/Scenery/Diffuse Ground KSC` or its `Specular` variant: `_NearGrassTexture` times `_GrassColor`, `_TarmacTexture` on uv0, `_BlendMaskTexture` red on uv1, no vertex colour. | **[src]** `src/Raytracing/RtStockTerrain.cs`, `KscColours` |
| Terrain beneath | The terrain quads under the space centre are drawn as well. The ground meshes and the quads overlap. | **[open]** how the raster resolves the overlap |
| LOD | `PQSCity.lod[i].SetActive(distance < visibleRange)` switches renderers and objects by the distance to `sphere.target`. Kopernicus multiplies `visibleRange` by `lodvisibleRangeMultiplier`. | **[src]** KSP `PQSCity.OnUpdateFinished`; KOP `KSC.Start` |
| Material values | A component made in `KSC.Start` waits for the space centre scene. Its first `Update` there sets textures and colours on every material whose shader name contains `Ground KSC`, from the `KSC` node, then removes it. In the editors, `EditorMaterialFixer` does it for `ksc_terrain`. | **[src]** KOP `KSC.MaterialFixer`, `EditorMaterialFixer` |
| Scenes | If `FlightGlobals.fetch` is null, `OnPreUpdate` only activates the sphere. Otherwise it follows the active vessel and `deactivateAltitude`. | **[src]** KSP `PQSMod_CelestialBodyTransform.OnPreUpdate`. **[open]** which branch the space centre takes |

## Scaled space

* A body's scaled version is a GameObject (`CelestialBody.scaledBody`) on layer 10, with a
  mesh and a renderer. The shader follows `ScaledMaterialType`: `Terrain/Scaled Planet
  (Simple)`, `(RimAerial)`, `(RimAerial) Standard`, `Emissive Multi Ramp Sunspots` or
  `Terrain/Gas Giant`. **[src]** KOP `ScaledVersionLoader.MaterialTypeToShaderName`
* **When it draws.** Kopernicus's `SharedScaledSpaceFader.Update`, for the body that is the
  flight's main body and outside map view, reads `pqs.visibleAltitude`. At or below `fadeStart`
  the renderer is off. Above it the renderer is on and `floatName` ramps to 1 at `fadeEnd`.
  While the sphere is active, both the quads and the scaled body draw in that band. **[src]**
* For any other body, or in map view, the renderer is on while its angular size in the scaled
  camera exceeds 1, off otherwise. **[src]** KOP `SharedScaledSpaceFader.Update`
* **When the quads stop.** `PQSMod_CelestialBodyTransform` deactivates the sphere above
  `deactivateAltitude` (90500 by default, a per-body value in the planet's config). It uses
  `highQualityShaderDeactivateAltitude` at terrain quality 2 or above.
  **[src]** KSP `PQSMod_CelestialBodyTransform.OnPreUpdate`
* **Coordinates.** `ScaledSpace.LocalToScaledSpace(p) = p * InverseScaleFactor - totalOffset`.
  `scaleFactor` starts at 6000. **[src]** KSP `ScaledSpace`
* **Cameras.** `Camera ScaledSpace`, `Camera 01` and `Camera 00`, found by name. The main menu
  has the single `Landscape Camera`. **[src]** KOP `ShadowMan.SetupMainCameras`. **[open]**
  each camera's culling mask.
* **Textures.** `ScaledSpaceOnDemand` loads them in `OnBecameVisible` and unloads them after a
  delay in `OnBecameInvisible`. **[src]** KOP `ScaledSpaceOnDemand`

## Sun, rings, atmosphere

| Thing | How it is drawn | Source |
|---|---|---|
| Sun light | `KopernicusStar : Sun` owns the `Light`. Each `LateUpdate` sets colour from `shifter.sunlightColor`, intensity from `shifter.intensityCurve` at the distance to the sun, and shadow strength. The light is on only in flight, in a planetarium scene and in the space centre. | **[src]** KOP `KopernicusStar.LateUpdate` |
| Current star | `KopernicusStar.Current` is the star `StarComponent.SetAsActive` chose. | **[src]** KOP `StarLightSwitcher` |
| Sun direction | A `Camera.onPreCull` lambda points the light's `forward` from the scaled sun at the scaled target, rounded to `sunRotationPrecision`. It runs once per culled camera. | **[src]** KOP `KopernicusStar.Awake` |
| Scaled light | A second light, `Scaledspace SunLight`, with `cullingMask = 1 << 10`, when `useLocalSpaceSunLight`. | **[src]** KOP `KopernicusStar.Start` |
| Flare | `KopernicusSunFlare : SunFlare` turns to the sun in `Camera.onPreCull` and sets the brightness in `LateUpdate`. `KopernicusStar` disables the `Sun`'s own `LensFlare`. A lens flare is not geometry. | **[src]** KOP `KopernicusSunFlare`, `KopernicusStar.LateUpdate` |
| Sun body | Scaled mesh with `Emissive Multi Ramp Sunspots`; `SharedSunShaderController` sets `_Offset0` to `_Offset3` and `_RampMap` each update. | **[src]** KOP `SharedSunShaderController` |
| Ring | A `Ring` component on a child of the scaled body builds a double-sided mesh (`MakeLinearMesh` or `MakeTiledMesh`) on the body's layer. Shader `Kopernicus/Rings` (`useNewShader`), else `Unlit/Transparent` or `Legacy Shaders/Transparent/Diffuse`. Queue 3010. `Update` sets scale, rotation, `sunRadius` and `sunPosRelativeToPlanet`. | **[src]** KOP `Ring.BuildRing`, `Ring.Update` |
| Ring `OnPreCull` | `Ring.OnPreCull` sets the rotation too. Unity sends that message only to components on a camera, so it should never run. | **[open]** |
| Atmosphere | A child `Atmosphere` of the scaled body: `ReferenceGeosphere` mesh, layer 9, shader `AtmosphereFromGround`, queue 3020, scale 1.025 by default. `LateUpdate` writes camera position, light direction and radii into a property block. | **[src]** KOP `AtmosphereFromGroundLoader`; KSP `AtmosphereFromGround.UpdateAtmosphere`, `SetMaterial` |
| Ring and atmosphere order | A body with a ring has its atmosphere raised to queue 3020 above the ring's 3010. | **[src]** KOP `Ring.BuildRing` |

## Detection

What another mod reads today, and what the catalogue pins (`src/Core/ForeignAssumptions.cs`):

| Need | Type and member | Kind | Pinned |
|---|---|---|---|
| The scene's sphere | `CelestialBody.pqsController`, `PQS.quads`, `PQ.subNodes` | KSP public field | stock |
| A quad's state | `PQ.isVisible`, `isBuilt`, `isSubdivided`, `isForcedInvisible`, `meshRenderer`, `mesh`, `positionPlanet`, `edgeState`, `sphereRoot` | KSP public field | stock |
| The ocean | `PQS.ChildSpheres`, `PQ.sphereRoot`, `PQS.surfaceMaterial`, `fallbackMaterial` | KSP public | stock |
| The space centre | `PQSCity` named `KSC`, its `sphere`, `lod` | KSP public | stock |
| The enforced shader level | `Kopernicus.RuntimeUtility.RuntimeUtility.KopernicusConfig`, `EnforceShaders`, `WarnShaders`, `EnforcedShaderLevel` | Kopernicus field | `kopernicus-shaders` |
| The star | `KopernicusStar.Current`, `.shifter`, `LightShifter.sunlightColor`; `Sun.sunLight` | Kopernicus field | `kopernicus-star` |
| Not pinned yet | `Kopernicus.Components.KSC.Instance`, `SharedScaledSpaceFader`, `Ring` (`ringMr`, `innerRadius`, `outerRadius`), `KopernicusStar.sunRotationPrecision` | Kopernicus field | none |

* `src/Scenery/KscGround.cs` finds the city by `city.name == "KSC"` and reads `city.sphere`.
  `src/Raytracing/Ksp/RtKspMods.cs` loads the star by reflection over the assembly named
  `Kopernicus`. **[src]**
* `src/Raytracing/RtTerrainColour.cs` and `RtStockTerrain.cs` read the quad meshes' colours
  with `Mesh.colors32` and `GetUVs(1)`, `GetUVs(2)`. **[src]**

## What a scan of the quads does not see

1. **The albedo.** The quad's colours are one input. The shader adds bands, steepness, atlas
   weights and material values. Parallax puts its own material and a finer child renderer on
   the quad. A colour query returns neither. **[src]** (see the colour section)
2. **A hidden quad that counts as visible.** `isVisible` outlives a disabled renderer (see
   *What decides isVisible*). **[src]**
3. **The ocean's quads.** They sit in the same scan as the terrain. Scatterer leaves them with
   an invisible material. **[src]**
4. **The space centre's ground.** It is not a `PQ`. Its meshes sit under `PQSCity` and switch
   by LOD distance. **[src]**
5. **Scaled space.** Far terrain and every other body is a scaled mesh with another shader.
   In the fade band both draw. Above `deactivateAltitude` only the scaled mesh draws. **[src]**
6. **Changes without a new mesh.** Edge triangles and normals change on the same `Mesh`. A
   reused `PQ` shows another place. The place and `edgeState` tell. **[src]**
7. **Transparent shells and rings.** The atmosphere is an analytic scattering shell in queue
   3020. The ring is a double-sided alpha mesh in queue 3010. A ray needs the ring's texture
   alpha, and the atmosphere is better left out. **[src]**
8. **The sun.** Direction is written per camera in `onPreCull` and rounded. Colour and
   intensity come from the shifter. The flare is not geometry. **[src]**
9. **Shadows.** `ShadowMan` forces terrain to cast shadows, so the shadow map holds quads that
   the rays must also hold. **[src]**
