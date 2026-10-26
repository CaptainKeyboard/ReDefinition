# Render contract: EVE

**For:** anyone who changes what the raytracing scene holds so that the rays match the
picture EVE draws.
**You need:** the repository, and EVE Redux with the raymarched volumetric clouds installed.
**You get:** every way EVE puts pixels on screen, which parts a ray can know as geometry,
the globals and lights EVE touches, and the members another mod reads to detect it.

Every claim carries a mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](../development/writing-these-pages.md)). A **[src]** names the
file and the symbol. EVE paths are relative to its repository root. Scatterer, Deferred and
Parallax appear only where they change what EVE draws.

## What was read

| What | Version and state |
|---|---|
| Installed build | EVE Redux 3.2.2, `Atmosphere.dll` 3.2.2.0 built 2026-08-16. All eleven plugin DLLs decompiled with ILSpy. **[meas]** |
| Source read | `LGhassen/EnvironmentalVisualEnhancements`, branch `release-5`, commit `024073f` of 2026-08-16. Same version, same class set. **[meas]** |
| Not in the build | `9a7786a` of 2026-08-29 (coherent ray placement). The installed renderer has no `rayPlacement`. **[meas]** |
| Unused clone state | The clone's checked-out `master` (`4ac5793`, 1.11.7.1) has no volumetric code. **[meas]** |
| Shaders | The raymarching shaders are not in the repository. `eveshaders.bundle` holds 43 names, among them `EVE/RaymarchCloud` and `EVE/ReconstructRaymarchedClouds`. **[meas]** Their bodies are **[open]**. |
| Configs | `GameData/StockVolumetricClouds`: 19 cloud objects, droplets, lightning, particle fields, wet surfaces, shadows. No `EVE_CITY_LIGHTS`, `EVE_TERRAIN` or `EVE_ATMOSPHERE` node in any `.cfg`. **[meas]** |
| Scatterer | 0.908 installed and decompiled. The clone (`c2d0b0f`, 2025-09-14) is older and not cited. **[meas]** |

The bundle list came from decompressing the UnityFS container (LZMA blocks, 10.5 MB) and
reading the shader names. The shader files in the repository are older or partial. **[meas]**

## Draw paths

Each row is **[src]** of the symbol in its last column.

| Path | Cameras and scenes | Layer, queue, event | Reads and writes | Off when | Symbol |
|---|---|---|---|---|---|
| 2D cloud layer | Local copy on the flight cameras, scaled copy on `Camera ScaledSpace`, clone on the menu body | Half sphere on layer 15 (local) or 10 (scaled), queue 2999, alpha blend, no `ZWrite` | Reads the layer's map, normal map, detail, `_CameraDepthTexture` (soft depth, local), sun; writes colour | Volumetric layer fully in (`scaledCloudFade` 0), editors, time fade 0 | `Atmosphere/Clouds2D.cs` `Reassign`, `Assets/Shaders/SphereCloud.shader` |
| Raymarch pass | `Camera 00` and the reflection probe cameras. Not `DepthCamera`, `NearCamera`, and not `Camera 01` when cameras are split | `CommandBuffer` "EVE Raymarched Volumetrics Renderer CommandBuffer" at `AfterForwardOpaque`, added in `OnPreRender`, removed in `OnPostRender` | Reads depth, maps, noise, light volume, history; writes ray targets and four history targets | Volume off, `fxOnlyLayer`, camera above `scaledFadeEndAltitude`, map view, menu, time fade 0 | `Atmosphere/RaymarchedClouds/DeferredRaymarchedVolumetricCloudsRenderer.cs` `EnableForThisFrame`, `OnPreRender`, `OnPostRender` |
| Composite quad | Every camera that culls layer 15 | Quad of infinite bounds on layer 15, `EVE/CompositeRaymarchedClouds`, made at queue 4000, set to 2998 each frame | Reads `colorBuffer`, `cloudFade`, the radii, `CameraToWorld`; writes colour | `rendererEnabled` 0, quad off a few frames after the last request | same file, `DeferredRaymarchedRendererToScreen` |
| Cloud shadows | Cameras that see the carrier `EVE ScreenSpaceShadowsManager` (layer 15, scale 0.0001) | `Light` buffers at `AfterScreenspaceMask` on the sun and `IVASun`; forward adds a camera buffer at `AfterForwardOpaque` | Reads depth and the shadow materials; multiplies the shadow mask or the colour | No shadow material and no light volume | `Atmosphere/RaymarchedClouds/LightVolume/ScreenSpaceShadowsManager.cs` |
| Light volume | `Camera 00` only | Updated in `OnPreRender`, compute `ReprojectLightVolume` or a material | Writes a 3D `RHalf` texture and its globals | `useLightVolume` false on every layer | `LightVolume.cs` `Update` |
| Eclipse shadows | Local: the main camera with an active PQS, not in the space centre. Scaled: the body's renderer | Local joins the shadow mask. Scaled is an overlay material at queue 2001 | Reads the casters' positions; multiplies | Menu, PQS inactive | `CelestialShadows/ShadowObject.cs`, `LocalShadowComponent` |
| Particle fields | Cameras except reflection probes, `DepthCamera`, `NearCamera` | `CommandBuffer` at `AfterForwardAlpha`, queue 3200, `ForwardBase` pass off | Reads `SampleCoverage` at the camera; writes colour only | Coverage under `minCoverageThreshold` | `Atmosphere/RaymarchedClouds/ParticleField/ParticleField.cs` |
| Wet surfaces | `Camera 00` while the PQS is active, only with Deferred | Buffer at `BeforeReflections`, `EVE/GBufferWetEffect`, three passes | Reads and writes G-buffers 0 to 2 for parts, scenery, terrain | No Deferred, no config for the body | `Atmosphere/RaymarchedClouds/WetSurfaces/WetSurfacesRenderer.cs` |
| Droplets | The internal camera in IVA | Buffer at `BeforeGBuffer` (deferred) or `BeforeForwardOpaque` | Writes colour over the window | Not in IVA, wetness 0 | `Atmosphere/RaymarchedClouds/Droplets/Droplets.cs` |
| Lightning | Layers with a `lightning` node | `Light` objects on layer 15, bolt quads at queue 2999 | Lights parts, kerbals, default layer | No storm | `Atmosphere/RaymarchedClouds/Lightning/Lightning.cs` |
| City lights | Local: every terrain quad. Scaled: the body renderer | Extra material on each `PQ` mesh, queue 2001, alpha blend, no `ZWrite` | Reads the overlay maps and `_SunDir` | No `EVE_CITY_LIGHTS` node. **[meas]** Not drawn here | `CityLights/CityLightsObject.cs`, `Utils/MaterialPQS.cs` |
| Terrain, ocean, atmosphere objects | Same carriers as city lights | `EVE/Terrain`, `EVE/Ocean`, `EVE/Atmosphere` | Not configured here. **[meas]** | Always | `Terrain/TerrainObject.cs` |

Two states of a layer decide what is on screen.

* **[src]** Under `scaledFadeStartAltitude` the volume draws and the 2D mesh is switched off,
  but its shadow material stays. Between start and end both draw, cross-faded. Above `end`
  only the 2D mesh draws (`CloudsPQS.Update`, `CloudsRaymarchedVolume.checkVisible`).
  The configs set start and end to 200 000 and 260 000 m on Kerbin, 100 000 and 130 000 m on
  Eve. **[meas]**
* **[src]** A body's cloud objects exist only within 2000 body radii of the camera and are
  destroyed beyond 4000 (`CelestialBodyCloudsHandler.Update`).

## What a ray can know as geometry

| Part | Geometry? | What a ray can take |
|---|---|---|
| 2D layer | Yes: an icosphere half (5 subdivisions), `body.Radius + altitude`, turned to face the camera. **[src]** `HalfSphere`, `Clouds2D.Reassign` | A sphere at `Clouds2D.radius`. Its alpha comes from `_MainTex`, which is not the volume's coverage map. **[src]** `clouds.cfg` Kerbin base layer |
| Volume | No. The carrier quad draws nothing: `EVE/Invisible` moves every vertex out of clip space. **[src]** `Invisible.shader` | The shell between `InnerSphereRadius` and `OuterSphereRadius`. Kerbin base: 1750 to 7450 m, cumulonimbus 1750 to 11 450 m. **[meas]** |
| Cloud shadow | No. A screen-space mask, no shadow map. **[src]** | See the next section |
| Particle fields | No. Procedural, camera-relative, `fieldSize` metres around the camera. **[src]** | A coverage test at the camera |
| Lightning | Lights and quads, transient. **[src]** | A short point light |
| City lights | Extra material on the terrain meshes. **[src]** | Nothing, not drawn here |

The density the shader sees is not on the CPU. `CloudsRaymarchedVolume.SampleCoverage`
returns the maps times the curve, without noise erosion. **[src]**

## Inputs of the volumetric pass

* Coverage: `coverageMap`, an alpha map, read with the direction `cloudRotationMatrix * p`.
  The type map `cloudTypeMap` selects the cloud type. **[src]** `SampleCoverage`
* `DensityCurve`: a 128 by 128 `RG16` texture. Its x is the cloud type, its y the height
  fraction. R holds the coverage curve, G the density curve.
  `AccumulatedVerticalCoverageTexture` holds the integral. **[src]** `BakeCurvesTexture`
* Per type: `cloudTypeProperties0` (density, 1 / noise tiling, 1 minus edge hardness, curl
  strength) and `multipleScatteringBrightnessArray`. **[src]** `SetCloudTypesShaderParams`
* Noise: `BaseNoiseTexture` (3D, 128 cubed, `R8`, rendered at load), `CurlNoiseTexture`,
  `baseNoiseOffsets` moving with `upwardsCloudSpeed`, blue noise from `stbn.R8`. **[src]**
  `RenderNoiseTextures`, `UpdateNoiseOffsets`
* Altitude: `innerSphereRadius`, `outerSphereRadius`, `planetRadius`, `sphereCenter`.
  `useBodyRadiusIntersection` is 1 only for a body with a real PQS. **[src]** `SetShaderParams`
  What the shader does with it is **[open]**.
* Sun: `_SunDir` is minus the forward of `Sun.Instance`'s light, set every frame. **[src]**
  `UpdateShaderParams`
* Depth: `cameraDepthBufferForClouds` is `Depth`, or `ResolvedDepth` under deferred shading.
  Rays end at it. Transparent geometry is not in it. **[src]** `OnPreRender`
* Time: `timeFadeDensity`, `timeFadeCoverage` from `TimeSettings` and the universal time. The
  weather layers switch on and off with it. **[src]** `CloudsPQS.Update`, `clouds.cfg`
* Resolution: one in 12 rays a frame (`temporalUpscaling = x12`, 4 by 3), blue-noise
  placed, reprojected from history. **[meas]** `raymarchedClouds.cfg`. ReDefinition's
  window can change it. **[src]** `src/Settings/Behaviours/EveBehaviour.cs`

## Where cloud shadows reach the ground

* **[src]** The shadow is neither a shadow map nor a global. Each camera has a
  `ScreenSpaceShadowsRenderer`. It adds two `Light` buffers at `AfterScreenspaceMask`. One
  builds `EVEScreenSpaceShadows` at half resolution. The other multiplies the sun's shadow
  mask with it (`BlendScreenSpaceShadows` pass 0).
* **[src]** Without deferred shading, a camera buffer at `AfterForwardOpaque` multiplies the
  colour instead (pass 1), faded by Unity's shadow fade distance. Sky pixels get 1.
* **[src]** Per pixel, the shader rebuilds the world position from the half-resolution depth,
  follows the sun direction to the sphere of radius `_Radius`, and samples one density there.
  That is the 2D layer's radius, `altitude` above the surface, not the volume's shell. The
  transmittance is `fakedMultiScatteringBeerAbsorption(density * 20)`, tinted by `_Color`,
  scaled by `_ShadowFactor`, `_Color.a` and `cloudTimeFadeDensity`.
  (`ScreenSpaceCloudShadow.shader`)
* **[src]** A pixel where the reconstructed clouds' transmittance is 0 gets no shadow.
* **[src]** Every registered layer is blended with `Blend Zero SrcColor`. Two layers multiply.
  Eclipses join the same target last (`ScreenSpaceShadowsRenderer.UpdateCommandBuffer`).
* **[src]** Closer than 1000 m with both kinds present, the 2D shader returns 1 and the
  light volume's shadow takes over, blended to 2500 m. The light volume shader is **[open]**.
* **[src]** The ocean takes the same shadow through `AddOceanShadowCommands`, which Scatterer
  calls (`ScattererOceanShadowsTexture`). Layers without a `layer2D` (weather, snow) have no
  shadow material and shadow only through the light volume.

## Globals, textures and lights

| Name | Kind | Set by | Read by |
|---|---|---|---|
| `scattererReconstructedCloud` | texture | EVE's buffer at `AfterForwardOpaque`; the white texture again in `OnPostRender` | the shadow shader (transmittance in alpha), `EveCloudRelight`, `CloudMotionVectors`. Scatterer resets it. |
| `scattererReconstructedCloudMotionVectors` | texture | EVE's buffer. It holds the secondary buffer, not motion vectors. **[doc]** `CloudMotionVectors.cs` | `CloudMotionVectors` |
| `colorBuffer`, `historyBuffer`, `historyMotionVectors`, `historyDistance`, `upscalingHeuristics`, `newRaysBuffer` | texture | EVE's buffer | the reconstruction and composite shaders |
| `scattererCloudLightVolumeEnabled` | float | every `OnPostRender` | Scatterer |
| `lightVolume`, `directLightVolume`, `lightVolumeDimensions`, `paraboloidPosition`, `paraboloidToWorld`, `worldToParaboloid`, `innerLightVolumeRadius`, `outerLightVolumeRadius`, `lightVolumeZClamp`, `ambientVolumeZClamp` | 3D texture, vectors, matrices | `LightVolume.Update`, set for Scatterer too | cloud and Scatterer shaders |
| `EVEDownscaledDepth`, `EVEShadowsDownscaledDepth`, `EVEScreenSpaceShadows` | texture | the shadow buffers | the blend shaders |
| `ScattererOceanShadowsTexture`, `ScattererDownscaledOceanDepthTexture` | texture | `AddOceanShadowCommands` | Scatterer's ocean |
| `currentVP` | matrix | `ScreenSpaceShadowsRenderer.OnPreCull` | the shadow shaders |
| `PartsDepthTexture`, `_originalGbuffer1Texture`, `_originalGbuffer2Texture` | texture | droplets, wet surfaces | their shaders |
| `_CameraDepthTexture`, `unity_CameraToWorld` | built-in | Unity | soft depth, shadows, composite |

All rows are **[src]**: `Utils/ShaderProperties.cs` and the `SetGlobal` calls in the files
of the draw-path table. The raymarch material's own properties (`CloudCoverage`, `CloudType`,
`DensityCurve`, `BaseNoiseTexture`, `cloudColor`, `skylightMultiplier`, `lightMarchSteps`,
`directPhaseFunctionProperties`) are per material, not global. **[src]** `SetShaderParams`

Lights, **[src]**:

* EVE reads `Sun.Instance.GetComponent<Light>()`, the private `scaledSunLight` of `Sun` by
  reflection, and `IVASun`. No code in the assembly writes the sun's intensity or colour.
* It adds buffers to those lights, creates the lightning lights, and sets the flight reflection
  probe's `shadowDistance` to at least 0.01 so that its shadow buffers fire.
* It sets `depthTextureMode = Depth` on `Camera 00`, `Camera 01` and the scaled camera at the
  start of a flight or space centre scene (`CloudsPQS.SceneLoaded`).

## City lights and night side

* **[src]** `CityLightsObject.Apply` appends `EVE/TerrainCityLight` to every `PQ` mesh
  renderer (`MaterialPQS`), queue 2001, `ZWrite Off`, `Offset -0.25`. On the scaled body it
  adds `EVE/PlanetCityLight` through `OverlayRenderer`. The main camera's `LocalCityComponent`
  sets `_SunDir` in `OnPreCull`.
* **[src]** The data are a cube or alpha map `_CityOverlayTex` and two detail textures, sampled
  by the vertex's sphere normal. The terminator comes from `_SunDir` against that normal.
  The overlay is an unlit colour, not a light.
* **[meas]** Nothing is configured here, so no city lights are drawn. The space centre's night
  lights are KSP's own and not EVE's.

## Compatibility surface

| Mod | What meets EVE | Source |
|---|---|---|
| Scatterer 0.908 | Swaps eight EVE shaders in EVE's `shaderDictionary` in the menu, when `integrateWithEVEClouds` is on (it is). The cloud shaders that draw are Scatterer's. It reads EVE's objects and materials by reflection and sets its atmosphere keys on them. | **[src]** `ShaderReplacer.ReplaceEVEshaders`, `EVEReflectionHandler`, `SkyNode.UpdateEVECloudMaterials`; **[meas]** `config.cfg` |
| Deferred | EVE tests for the type `Deferred.Deferred`. Wet surfaces need it, and the 2D shadow projector stays on. The frame order puts cloud shadows before deferred lighting and the cloud pass after forward opaque. | **[src]** `Tools.IsDeferredInstalled`; **[doc]** `Deferred/DeferredKSP/VisualsRenderingOrder.md` |
| Parallax | EVE adds nothing to its terrain here. **[meas]** Parallax finds the vanilla scaled material by skipping names with `Scatterer/` or `Eve/`. A comment in EVE says Parallax grass has an invalid mask for the wet effect. | **[src]** Parallax `ScaledManager.GetScaledMaterialIndex`; EVE `WetSurfacesRenderer` |
| Kopernicus | EVE works by body name and `pqsController`. It waits for Kopernicus to clone the menu body. `PQSWrapper` gives gas giants a PQS (Jool in `pqs.cfg`). | **[src]** `GlobalEVEManager`, `Clouds2D.CloneForMainMenu`, `PQSManagerClass.HasRealPQS` |

## Detection

| Need | Member | Source |
|---|---|---|
| The cloud objects | `EVEManager.GenericEVEManager<Atmosphere.CloudsObject>.ObjectList`, protected static. `Atmosphere.CloudsManager` inherits it. | **[src]** `GenericEVEManager.cs` |
| One layer | `CloudsObject.Body`, `Layer2D`, `LayerRaymarchedVolume` | **[src]** `CloudsObject.cs` |
| Its volume | `enabled`, `InnerSphereRadius`, `OuterSphereRadius`, `CloudRotationMatrix`, `CurrentTimeFadeCoverage`, `CurrentTimeFadeDensity`, `VolumetricLayerScaledFade`, `RaymarchedCloudMaterial`, `CoverageMap`, `CloudTypeMap`, `SampleCoverage`, private `curvesTexture` | **[src]** `CloudsRaymarchedVolume.cs` |
| Its 2D layer | `Scaled`, `ScreenSpaceShadowMaterial` (`_Radius`, `_ShadowFactor`, `_Color`, `cloudTimeFadeDensity`, `mapViewFade`) | **[src]** `Clouds2D.cs` |
| The renderer | `Atmosphere.DeferredRaymarchedVolumetricCloudsRenderer`, internal, one `MonoBehaviour` per camera. The private static `CameraToDeferredRaymarchedVolumetricCloudsRenderer` maps `Camera` to it, with `null` for excluded cameras. | **[src]** `DeferredRaymarchedVolumetricCloudsRenderer.cs` |
| Its camera | private `targetCamera`, `MainFlightCamera`, private `reflectionProbeCamera` | same file |
| Drawing this frame | private `renderingEnabled`, true from `EnableForThisFrame` (called from the carrier's `OnWillRenderObject`) to `OnPostRender`; private `volumesAdded` | same file |
| Its buffer | `AfterForwardOpaque`, name "EVE Raymarched Volumetrics Renderer CommandBuffer", present only between `OnPreRender` and `OnPostRender`. The static `CloudRenderingCameraEvent` holds the event. | same file |
| Its output | `historyRT`, `historyDistanceRT`, `historyMotionVectorsRT` at `[useFlipUpscalingBuffer, eye, face]` | same file, `HistoryManager.get_Item` |
| The composite | static `deferredRaymarchedRendererToScreen`, its `compositeColorMaterial` (`rendererEnabled`, `cloudFade`, `innerSphereRadius`, `outerSphereRadius`) | same file |

A renderer is not ready on its first frame. It initialises in its first `OnPostRender`, and
only for a camera with an `activeTexture`, so it draws from the next frame on. **[src]**

What ReDefinition reads today, each pinned in `src/Core/ForeignAssumptions.cs`:

* **[src]** `EveRtClouds` (`src/Raytracing/Ksp/RtEveClouds.cs`): the object list, `Body`,
  `Layer2D`, the volume members above, the shadow material's `_Radius`, `_ShadowFactor`,
  `_Color` and fades, the two maps through `TextureWrapper.GetTexture`, `type`, `alphaMask`,
  and `curvesTexture`. Tested against EVE 3.2.2.0.
* **[src]** `EveCloudRelight` (`src/Raytracing/Ksp/EveCloudRelight.cs`): `targetCamera`,
  `renderingEnabled`, `reflectionProbeCamera`, `useFlipUpscalingBuffer`, `volumesAdded`,
  `historyRT`, `historyDistanceRT`, and the `colorBuffer` binding.
* **[src]** `EveCloudJitter`, `EveCloudMotion`, `EveCloudComposite` and `EveShadows`
  (`src/Upscaler/Compatibility/EveCompatibility.cs`) cover the upscaler's side.

## What the picture depends on and the rays do not see

1. **[src]** More than one layer shadows the ground. Kerbin and Laythe have a base layer and
   a tall layer, each with a 2D layer, and EVE multiplies their shadows. `RtEveClouds.FillFrom`
   returns the first layer that qualifies, so the second is left out.
2. **[src]** Weather and snow layers have no 2D layer. `FillFrom` skips them, and the picture
   shows them through the light volume and the raymarch pass only.
3. **[src]** The ground shadow is one density sample on the 2D layer's sphere, not an integral
   through the shell. A ray through the shell would darken more or less than the picture does.
4. **[src]** Close to the camera the shadow comes from the light volume, a time-sliced 3D
   texture. No ray sees it. **[open]** Its range and the `LightVolumeShadow` shader.
5. **[src]** The cloud image is reconstructed from history, one ray in twelve per frame, and
   Scatterer's shader code. A ray result matches it on average, not per frame.
6. **[src]** EVE sets the composite's queue to 2998, so transparent geometry from queue 3000
   on (plumes, particles) draws over the clouds whatever its distance. Whether Scatterer's
   replaced composite and sky pass change that order is **[open]**. Deferred's order
   document puts the cloud pass after forward opaque and the sky pass later. **[doc]**
7. **[src]** Reflection probe cameras draw the clouds with five times the step size and no
   light march when `renderCloudsInReflectionProbes` is on. KSP's reflections see that version.
8. **[src]** Wet surfaces change diffuse, smoothness and normals in the G-buffers (Kerbin
   only here). The rays take the dry materials.
9. **[src]** Rain, snow and lightning are camera-relative or transient and have no scene
   counterpart. Layers also fade by universal time, so a layer can be absent for hours.
