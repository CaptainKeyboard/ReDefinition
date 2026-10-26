# Scatterer's render contract

**For:** developers of ReDefinition's raytracing, who need what Scatterer puts on screen,
and authors of mods that read Scatterer's renderers.
**You need:** nothing. The sources are in the first table.
**You get:** every way Scatterer draws, which of it has geometry, its inputs and reflections,
how to detect it, and what the picture depends on that rays cannot see.

Every claim carries a mark: **[src]**, **[meas]** or **[open]**
([writing-these-pages.md](../development/writing-these-pages.md)). A **[src]** names file and
symbol. The files are under `scatterer/` in Scatterer's repository.

## Sources read

| Source | Version | Used for |
|---|---|---|
| Clone `References/KspUpscaler/Scatterer`, `HEAD` of `master` | `c2d0b0f`, 2025-09-14, `versionNumber` "0.0878p" | Nothing: older than the install |
| Same clone, `origin/dev` | `3153828`, 2026-08-29, `versionNumber` "0.0908" | Every **[src]**, read through `git archive`; the checkout is unchanged |
| Installed `GameData/scatterer/Scatterer.dll` | file version 0.908.0.0, written 2026-08-23 | Decompiled with `ilspycmd`; each layer, queue, camera event, shader name and member named here is in it as in `dev` |
| `shaders/scatterershaders-directx` | written 2026-08-16, Unity 2019.4.18f1 | **[meas]**: parsed with UnityPy, which gives shader names, pass state and the resource names of the compiled code. It holds no HLSL |
| `KSP.log` of 2026-10-26, 18:04 | Scatterer 0.0908, Direct3D 11 | **[meas]** |
| Clone `scatterer/OldShaders`, `34d9ded` | 2022-02-20 | The only shader source: the forward ocean that the deferred one replaced |

The OpenGL bundle was not read. The layer names are **[meas]** from the `TagManager` of
`KSP_x64_Data/globalgamemanagers`: 9 Atmosphere, 10 Scaled Scenery, 15 Local Scenery.

## Draw paths

Scatterer has no `OnRenderImage`, no `Graphics.Draw*` and no `DrawProcedural` **[src]**
(searched in `dev` and in the decompile). `Graphics.Blit` fills only off-screen textures:
atmosphere tables, foam, sun flare extinction, the adjusted planet map and the ocean variance.
Everything on screen is a renderer that Unity draws, or a command buffer on a camera.

| Path and class | Cameras | Scenes | Layer, queue, event | Off when | Reads and writes |
|---|---|---|---|---|---|
| Sky sphere, `SkySphereContainer`, `SkySphereLocalRenderer` | scaled mode: `Camera ScaledSpace`; local mode: cameras that draw layer 15, but not `Camera 00` with two local cameras | flight, space centre, tracking station, main menu | layer 9, or 15 when local and in the main menu; queue 2998; local pass 2 `DrawRenderer` at `BeforeImageEffectsOpaque` | the stock sky returns below altitude 0 when `useOceanShaders` is off | reads colour copy, cloud texture, atmosphere atlas; writes colour; ZWrite off, cull front **[meas]** |
| Scaled atmosphere quad "Scatterer scaled depth-buffer atmo", `ScaledDepthBufferScatteringContainer` | `Camera ScaledSpace` | flight, space centre, tracking station | layer 10; queue 2997, then 2899 upward by body distance; blend One Zero **[meas]** | PQS active, main menu, stock scaled renderer off | reads colour copy and `ScattererCameraDepthTexture`; replaces colour |
| Scaled atmosphere fallback "Scatterer scaled atmo fallback", `ScaledScatteringContainer` | scaled or local cameras | flight, space centre, main menu | layer 10, or 15 in local mode; queue 2997; alpha blend **[meas]** | scaled space (outside the main menu), tracking station | copy of the scaled planet mesh; reads colour copy; writes colour |
| Local scattering "Scatterer screenspace scattering <body>", `ScreenSpaceScatteringContainer`, `ScatteringCommandBuffer` | every camera that draws the quad, except `NearCamera` and `DepthCamera` | flight, space centre | layer 15, invisible shader; `BeforeForwardAlpha`; full or quarter resolution (`quarterResScattering`) | PQS inactive, camera underwater | reads colour copy, depth, ocean depth; writes colour, and depth when the body has an ocean |
| Ocean, `OceanRenderer`, `OceanCommandBuffer` | every camera except `TRReflectionCamera`, `Reflection Probes Camera`, `DepthCamera`, `NearCamera` | flight, space centre | carrier layer 15; geometry buffer at `AfterForwardOpaque`; shading buffer at `AfterImageEffectsOpaque` | map view, PQS inactive, `useOceanShaders` off, no `Scatterer_ocean` node for the body | reads camera depth, sun shadow map, EVE shadows, `unity_SpecCube0`, `ssrOutput`; writes its own targets, camera depth, `ScattererDepthCopy`, colour by alpha blend |
| Underwater scattering, shader `UnderwaterScatterDepthBuffer` | as local scattering | flight, space centre | queue 2502; same buffer as local scattering | camera above the water | reads depth copy and colour copy; writes colour and depth **[meas]** |
| Caustics: `CausticsShadowMaskModulate` on the sun light; `CausticsLightRaysCameraScript` | rays: every camera but `Reflection Probes Camera` | flight only | `LightEvent.AfterScreenspaceMask`; rays at `AfterForwardAlpha` | mask: above 6000 m or ocean not drawn; rays: camera above water | multiplies the sun's shadow mask underwater; adds light rays to colour |
| Godrays, `RaymarchedGodraysRenderer`, `LegacyGodraysRenderer` | the earliest local camera | flight, space centre | raymarched at `AfterForwardOpaque`; legacy at `BeforeForwardOpaque` with a 512 by 512 plane "GodraysVolumeDepth <body>", renderer off | off in the installed `config.cfg` **[meas]** | writes its own targets, which the scattering shaders read as `_godrayDepthTexture` |
| Sun flare, `SunFlare` | `Camera ScaledSpace` (map view, tracking station), `Camera 00` | flight, space centre, tracking station | layer 10, or 15 outside map view; queue 3100 | `fullLensFlareReplacement` off, flare occluded | reads depth and its extinction texture |
| Eclipse pass on the stock scaled planet, `ScaledPlanetEclipse` | `Camera ScaledSpace` | flight, space centre, tracking station | queue 2001 and 2002, an extra material on the planet's renderer | eclipses off, or no caster | adds shadow to the planet's colour |

The screen copy is no draw. `ScreenCopyCommandBuffer` runs at `BeforeImageEffectsOpaque` on any
camera that draws a carrier. It copies the colour to `ScattererScreenCopyBeforeOcean`, binds
`ScattererCameraDepthTexture`, and sets `forceIntoRenderTexture` on the camera **[src]**.

The order on one local camera follows the camera events **[src]**:

1. `BeforeForwardOpaque`: depth pre-pass merge (off by default), legacy godrays, shadow fade.
2. `AfterForwardOpaque`: ocean geometry, raymarched godrays, depth to distance (two cameras).
3. `BeforeImageEffectsOpaque`: screen copy, then the local sky.
4. `AfterImageEffectsOpaque`: ocean shading.
5. `BeforeForwardAlpha`: local scattering. Particles and EVE clouds draw after it.
6. `AfterForwardAlpha`: caustics rays, then TAA and SMAA, which ReDefinition switches off.

Scatterer runs in four scenes: `Scatterer.Awake` destroys it in the editors **[src]**. It
builds an ocean in flight and the space centre only: `LoadEffectsForBody` sets `hasOcean` to
false in the tracking station and the main menu **[src]**. A body loads and unloads by its own
`loadDistance` and `unloadDistance` **[src]**.

## What a raytracer has to know as geometry

| Thing | Mesh | Where it lives |
|---|---|---|
| Ocean surface | no, a screen grid | `oceanScreenGrid`: `MakePlane(vertCountX, vertCountY)` on XY in clip space, one vertex per `oceanMeshResolution` pixels (6 in the install **[meas]**). `_Ocean_Radius` is the body's radius. The 2022 shader casts a ray per vertex onto that sphere and displaces the hit **[src]** |
| Ocean carrier "Scatterer ocean water display" | a quad, no surface | layer 15, child of the body's transform; every pass of its material is off, or its shader is `Scatterer/invisible` |
| Sky sphere | a UV sphere, no surface | radius `1.5 * (Rt - Rg*s) + Rg*s` about the body, front faces culled **[src]** |
| Scaled planet | the stock mesh, changed | `TweakScaledMesh` pulls vertices above the radius toward it by `flattenScaledSpaceMesh` and replaces `sharedMesh`; with `adjustScaledTexture`, `TweakStockScaledTexture` swaps `_MainTex` for a render texture |
| Stock ocean | hidden, not gone | `StockOceanDisabler` sets `PQS.isDisabled` and calls `PQ.SetMasterInvisible`, which sets `meshRenderer.enabled` to false; the quads stay in the hierarchy **[src]** (KSP's `PQ`, decompiled). The log names `KerbinOcean`, `LaytheOcean`, `EveOcean` **[meas]** |
| Atmosphere quads, sun flare, godray plane, `Scatterer ReflectionProbeCheckerGO` | quads, no surface | invisible or screen-space shaders. The last one stays at the scene origin: `ReflectionProbeChecker.OnUpdate` is no Unity message and nothing calls it **[src]** |

Skip every renderer whose material shader is named `Scatterer/*` or `Scatterer-EVE/*`
**[meas]**: none of them is a surface.

How the ocean gets its heights, per frame **[src]** unless marked:

* `OceanFFTgpu.UpdateNode` builds five maps of `m_fourierGridSize` squared (128 in the
  install **[meas]**), ARGBHalf, mipmapped, repeating: `_Ocean_Map0` heights, `_Ocean_Map1` and
  `_Ocean_Map2` slopes, `_Ocean_Map3` and `_Ocean_Map4` horizontal displacement. It runs while
  the body is in local space. The time is `Planetarium.GetUniversalTime() % 100000`. The phases
  are random (`Random.value` in `GenerateWavesSpectrum`), so no two sessions share a sea.
* Four cascades, `m_gridSizes` 5488, 392, 28 and 2 m, with `m_choppyness` 2.3, 2.1, 1.3, 0.9.
  The 2022 vertex shader sums the four heights into `dP.z` and the choppy displacement into
  `dP.xy` (`OceanWhiteCapsModProj3.shader`). The deferred shader's source is not available. Its
  resource names include the same maps **[meas]**. Its exact displacement is **[open]**.
* The ocean's frame follows the camera. `OceanCameraUpdateHook` builds `ux`, `uy`, `uz`, `oo`
  from the camera position and keeps `m_Offset`, which it resets at 20000. The shared material
  holds the values of the camera that rendered last: the hook runs in each camera's
  `OnWillRenderObject`.
* The geometry buffer renders the grid's displaced vertices into `vertWorldPositionsXYZ
  OceanPositionsXId` (ARGBFloat) and `vertOceanPositionsYId`, one texel per vertex, and
  exposes them as globals. Whether they are world or camera-relative positions is **[open]**.
* The CPU has only `GPUWaveInteractionHandler`: up to 4096 positions a frame, read back
  asynchronously, set as `PartBuoyancy.waterLevel` and scaled to zero at 50 m/s, within
  `maxWaveInteractionShipAltitude` (500 m). The camera's water height arrives the same way.

## Lighting and material inputs

* The sun is `ProlandManager.mainSunLight`: the light named after `mainSunCelestialBody`, or
  `Scatterer.sunLight` ("SunLight") for the Sun **[src]**.
* Per body `SkyNode` holds `interpolatedSettings`, blended from `configPoints` by the camera's
  altitude: sky alpha, exposures, extinction, post-process alpha and depth **[src]**. The
  exposures scale the sky and the scattering, so a ray that ends in the sky needs them.
* `SetUniforms` and `UpdatePostProcessMaterialUniforms` set per material: `_Sun_WorldSunDir`,
  `_sunColor` (the config colour, times the scaled light's intensity with
  `sunsUseIntensityCurves`), `cloudSunColor`, `planetShineSources`, `planetShineRGB` (four
  secondary suns), `lightOccluders1` and `2`, `sunPosAndRadius` (eight eclipse casters),
  `ringNormal`, `ringTexture` **[src]**. The atmosphere is a texture atlas `AtmosphereAtlas`,
  computed once and loaded from `GameData/ScattererAtmosphereCache/PluginData/<hash>/`
  (`atlas.half`). There is no sky cube map **[src]**.
* Globals that Scatterer sets **[src]**: `ScattererCameraDepthTexture`,
  `ScattererUseCustomDepthTexture`, `ScattererScreenCopyBeforeOcean`, `ScattererDepthCopy`,
  `ScattererOceanActiveOnCurrentCamera`, `ScattererLocalSkyActiveOnCurrentCamera`,
  `oceanGbufferDepth`, `oceanGbufferNormalsAndSigma`, `oceanGbufferFoam`,
  `oceanBackgroundScreenCopy`, `ScattererDownscaledOceanDepthTexture`,
  `ScattererOceanShadowsTexture`, `useOceanSSR`, `halfResolutionOceanSSR`, `_VarianceMax`,
  `_ShadowMapTextureScatterer`, `unity_ShadowFadeCenterAndType`, `scattererReconstructedCloud`,
  `scattererCloudLightVolumeEnabled`, `_godrayDepthTexture`, `TONEMAPPING_MODE` and three
  `TAA_*` values.
* Ocean material data: foam is `_Ocean_Foam0` and `_Ocean_Foam1`, filled from the Jacobians,
  with `_Ocean_WhiteCapStr` and `shoreFoam`. Caustics are a texture from the config path with
  two scrolling layers. Godrays come from the sun's shadow map. There is no wetness input.
* It also changes Unity's state: `QualitySettings.antiAliasing` 0, `shadowProjection`
  StableFit, `shadowDistance`, `shadowCascade4Split` per frame, per-light bias and resolution,
  and in unified mode Unity's `ScreenSpaceShadows` shader for good. It replaces `MotionVectors`
  with `Scatterer/Internal-MotionVectors` with TAA on, and forces `meshCastShadows` and
  `meshRecieveShadows` on for PQS with `terrainShadows`.

## Reflections

The ocean has three sources **[src]** (`OceanNode.InitOceanMaterial`):

| Source | Switch | How |
|---|---|---|
| Analytic sky | `oceanSkyReflections` (keyword `SKY_REFLECTIONS_ON`) | sky radiance from the atmosphere atlas through a BRDF with slope variance (`_Ocean_Variance`) |
| Reflection probe | `reflectionProbeOceanAmbient`: 1 when the flight camera's probe is enabled, or in the space centre with Deferred | the shader reads `unity_SpecCube0` **[meas]** |
| Screen-space reflections | `oceanScreenSpaceReflections` and `useOceanSSR`; off in the installed `config.cfg` **[meas]** | Deferred computes them from Scatterer's G-buffer; the ocean reads `ssrOutput` **[meas]** |

There is no planar reflection and no reflection camera for the ocean. The only extra camera
renders the scaled space for KSP's own probe (`ReflectionProbeFixer`).

Land and asphalt: **no**. Scatterer gives them no reflection and no wet or puddled look.

* No source file in `dev` or in the decompile contains wet, puddle, rain, asphalt, runway,
  smoothness or metallic **[src]**.
* Of the 53 shaders in the DX bundle, only `OceanWhiteCaps` and `OceanWhiteCapsDeferred` name
  `unity_SpecCube0`, and only the deferred one names `ssrOutput` **[meas]**.
* The only land materials it touches are the scaled planets. `TweakStockAtmosphere` sets
  `_SpecColor` and `_Shininess` on `Terrain/Scaled Planet (RimAerial)` from `specR`, `specG`,
  `specB` and `shininess`. It also turns the `AERIAL` keyword off on every PQS surface
  material **[src]**.

A wet launch pad comes from another mod or from KSP's own materials.

## Compatibility surface

| Mod | What Scatterer does **[src]** |
|---|---|
| Deferred | `DeferredReflectionHandler` looks up `Deferred.Deferred` and `Deferred.ScreenSpaceReflections` and calls `RegisterScattererOceanForCamera`. With Deferred it skips the split eclipse material, and on a deferred scaled camera it copies the depth by hand (`ScattererScaledDeferredDepthCopy`) |
| EVE | `ShaderReplacer.ReplaceEVEshaders` swaps eight EVE shaders (`Cloud`, `ScreenSpaceCloudShadow`, `CloudVolumeParticle`, `GeometryCloudVolumeParticle`, `GeometryCloudVolumeParticleToTexture`, `RaymarchCloud`, `CompositeRaymarchedClouds`, `ReconstructRaymarchedClouds`) in EVE's `ShaderLoader` dictionary and in existing materials. `EVEReflectionHandler` reads `Atmosphere.CloudsManager` by reflection and ranks the cloud queues from 2899 by distance |
| Kopernicus | finds `<body>Ring` objects, reads `texture`, `ringMr` and `tiles` by reflection, sets their queue to 3005 and the sun corona's to 2998 |
| Parallax, Harmony, ModuleManager | no reference in the code. BurstPQS and TUFX appear in comments only (the order of `StockOceanDisabler`, the five-frame start delay) |
| KSP | `DisableAmbientLight` on `Camera ScaledSpace` sets `RenderSettings.ambientLight` to black for that camera's render only. `ReflectionProbeFixer` on `Reflection Probes Camera` removes layer 10 from its mask |

`Dx12UnifiedCamera` sets `GameSettings.graphicsVersion` to D3D11 only where Unity reports
Direct3D 12. The log reports Direct3D 11.0 **[meas]**.

## Detecting the renderer

All members are public unless marked, and exist in the installed DLL **[src]**.

| Member | Tells you |
|---|---|
| `Scatterer.Scatterer.Instance`, `.isActive`, `.versionNumber` | whether it runs ("0.0908"); `Instance` is null or a destroyed object in the editors |
| `.unifiedCameraMode`, `.nearCamera`, `.farCamera`, `.scaledSpaceCamera` | unified when `Camera 00` exists and `Camera 01` does not; the log says "Running in unified camera mode" **[meas]** |
| `.sunLight`, `.scaledSpaceSunLight`, `.ivaLight` | the lights it modulates |
| `Scatterer.OceanNode` (abstract; `OceanFFTgpu`, `OceanWhiteCaps`) | one per body with an ocean; it sits on the `Camera ScaledSpace` object, not on the sea, and `FindObjectsOfType` finds it |
| `OceanNode.prolandManager`, `ProlandManager.parentCelestialBody`, `.skyNode`, `.GetOceanNode()` | which body the ocean belongs to |
| `OceanNode.waterMeshRenderer.enabled` | the sea is drawn: `!MapView.MapIsEnabled && !skyNode.inScaledSpace`; a reflection camera sets it false from `OnPreCull` to `OnPostRender` (`DisableEffectsForReflectionsCamera`) |
| `SkyNode.inScaledSpace`, `.altitude`, `.simulateOceanInteraction` | PQS active or not; camera altitude |
| `OceanNode.isUnderwater`, `.m_oceanMaterial`, `.height`, `.ux`, `.uy`, `.uz`, `.oo`, `.m_Offset` | underwater; the material; the ocean frame |
| `ScattererCelestialBodiesManager.isCustomOceanEnabledOnScattererPlanet`, `.underwater` | an ocean is on the planet; the camera is under it |
| `SunlightModulatorsManager.Instance.GetLastModulationColor(Light)` | the sun's colour as last modulated, readable at any time |
| `OceanCommandBuffer.renderingEnabled` (private) | the ocean draws on that camera now |
| Command buffers on a camera, by name, from `GetCommandBuffers` | "Scatterer ocean Geometry CommandBuffer" (`AfterForwardOpaque`), "Scatterer ocean Shading CommandBuffer" (`AfterImageEffectsOpaque`), "Scatterer screen-space scattering CommandBuffer" and its "1/4 res" twin (`BeforeForwardAlpha`), "Scatterer screen copy CommandBuffer" and "Scatterer local sky commandBuffer" (`BeforeImageEffectsOpaque`); present from `OnWillRenderObject` to `OnPostRender` of that camera |
| Global `ScattererOceanActiveOnCurrentCamera` | 1 while a camera renders the ocean; 0 after its `OnPostRender` |

`src/Core/ForeignAssumptions.cs` pins `ScattererOceanMask`, `ScattererOceanReflections` and
`ScattererOceanSun`. They name `OceanCommandBuffer` literals and `OceanNode.m_oceanMaterial`.
None pins `waterMeshRenderer`, `SkyNode.inScaledSpace`, `GetLastModulationColor` or the
command buffer names.

## What the picture depends on that rays do not see

1. **The sun's colour.** `SunlightModulator` multiplies `Light.color` only between `OnPreCull`
   and `OnPostRender` of `Camera 00`, `Camera 01`, the IVA camera and `Reflection Probes
   Camera`. Outside that window the light is unmodulated. The factor is the atmosphere's
   transmittance at the active vessel's position, the eclipse term at the camera, and the
   underwater dimming and caustics boost (`SkyNode.UpdateLightExtinctions`,
   `UpdateEclipseCasters`, `OceanNode.ApplyUnderwaterDimming`).
2. **The atmosphere is in no material.** The stock `Atmosphere` child (layer 9) is inactive and
   the PQS materials have `AERIAL_OFF`. Scatterer adds extinction and in-scatter to each pixel
   at `BeforeForwardAlpha` from the depth buffer. A ray's hit colour carries none of it.
3. **The ocean has no CPU surface.** Heights and displacement live in five GPU textures in a
   camera-following frame, with random phases. The stock ocean is hidden; its renderers stay.
4. **Ocean shading is a G-buffer pass with alpha.** Shore transparency and refraction read
   `oceanBackgroundScreenCopy`. Shadows come from `_ShadowMapTextureScatterer`, EVE's cloud
   shadows and the eclipse pass.
5. **The sky is analytic.** A miss needs the atlas, the exposures of the current config point
   and the sun colour of point 1. Planetshine, ring shadows and eclipses are per-pixel
   analytic terms with up to four suns and eight casters.
6. **Ambient is zero** in the scaled camera's render. Sun flare, godrays and caustics are
   screen effects with no geometry.
7. **Unity's state is changed**: shadow projection, distance and cascades, the screen-space
   shadow shader, the motion vector shader, antialiasing off.
8. **EVE's clouds light by Scatterer's shaders.** They read `_LightColor0`, so they take the
   modulated colour **[meas]** (resource names of the replaced shaders).

## Open points

* The deferred ocean shader's displacement, alpha and the frame of the vertex-position
  targets are **[open]**: the clone has no source for them.
* Whether `_CameraDepthTexture` and Unity's motion vectors contain the ocean is **[open]**.
  The ocean writes camera depth through ZWrite **[meas]**. The TAA script has
  `checkOceanDepth`.
* The culling masks of `Camera 00` and `Camera ScaledSpace` were not read at run time.
  `ReflectionProbeFixer` gives 1536 (layers 9 and 10) for the scaled camera **[src]**.
* Whether passes 0 and 1 of the sky shader draw on the layer 15 sphere is **[open]**.
