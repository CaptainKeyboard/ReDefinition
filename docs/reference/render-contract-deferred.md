# Render contract of Deferred

**For:** anyone who makes ReDefinition's rays show what Deferred's raster pass shows.
**You need:** the clone of Deferred 1.3.5 and the installed `zzz_Deferred` folder, to check a
row.
**You get:** what Deferred puts on screen, from which input, and what a raytracer cannot see.

Every claim carries a mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](../development/writing-these-pages.md)). A source column names the
file and the symbol. `D:` is the clone's `DeferredKSP/`, `S:` is its
`shaders/DeferredKSPShaders/Assets/Shaders/`, `U:` is `CGIncludes` of Unity 2019.4.18f1. `P:`
is the Parallax Continued clone. `E:` is the installed EVE, decompiled.

## What was read

| What | Version and state | Mark |
|---|---|---|
| Clone, `References/KspUpscaler/Deferred` | tag 1.3.5.0, commit `ef3c211`, 2026-05-04 | **[src]** |
| Installed `zzz_Deferred/Deferred.dll` | `AssemblyVersion` 1.3.5.0, file date 2026-09-25 | **[src]** |
| Decompile against clone | the same 17 classes; same defaults, events, probe values and camera setup | **[src]** |
| Installed shader bundles | `deferredshaders`, `replacementshaders.shab`: compressed UnityFS, not opened | **[open]** |
| Shabby, EVE | Shabby 0.4.2.0; EVE 3.2.2.0 with wet surfaces (the clone of EVE has none) | **[src]** |

The shaders are read from the clone. Whether the installed bundles equal it is **[open]**.
Deferred's bundles use Unity 2019.4.18f1 (`S:ProjectSettings/ProjectVersion.txt`). KSP runs in
gamma colour space ([building-and-testing.md](../development/building-and-testing.md), the test
player) **[doc]**.

## Cameras and scenes

| Camera | Deferred sets | Source |
|---|---|---|
| `Camera 00` (Direct3D) | `renderingPath` DeferredShading, `DeferredPQSFade`, `ForwardRenderingCompatibility` (layer 15), `RefreshLegacyAmbient`, `ScreenSpaceReflections` | **[src]** `D:Deferred.cs` `HandleCameras` |
| `Camera ScaledSpace` | deferred, `DisableCameraReflectionProbe`, `ForwardRenderingCompatibility` (layer 10), `RefreshLegacyAmbient` | **[src]** `HandleCameras` |
| `Main Camera` (editors) | deferred; `EditorLighting` adds SMAA | **[src]** `HandleCameras`, `EditorLighting.HandleSMAA` |
| `InternalCamera` (IVA) | deferred, `ForwardRenderingCompatibility` (layer 20), `IVALightingRotation` | **[src]** `Deferred.OnCameraChange` |
| `Reflection Probes Camera` | deferred, set each `OnPreCull`; `disableProbeReflectionsOnCurrentCamera` 1 while it draws | **[src]** `DeferredReflectionProbeFixer` |

Other APIs than Direct3D use `Camera 01` and `Camera 00`, without screen-space reflections.
Every other camera stays as KSP made it. `OnSceneLoaded` runs at start and on every
`sceneLoaded`. `KSCReflectionProbe` exists in the space centre scene only **[src]**.

## How shaders are swapped

1. `ShaderLoader` loads `replacementshaders.shab` into `ReplacementShaders` and
   `deferredshaders` into `DeferredShaders` **[src]**.
2. `ReplaceIncompatibleShadersInExistingMaterials` runs once in `Start`, at the main menu. Each
   `Material` whose shader name is a key gets the replacement and keeps its `renderQueue`.
   `KSP/Scenery/Decal/Blended` is set to queue 2700 **[src]**.
3. Materials built later go through `Shader.Find`. Shabby patches every call site and answers
   with the same-named shader of any `.shab` bundle **[src]** (`Shabby.FindShader`).
4. A shader outside the 42 names is not touched. It draws forward, or has a deferred pass of
   its own **[src]**.

The 42 replacements are 17 part shaders (`KSP/*`) and 13 `Terrain/PQS/*` shaders. There are
11 scenery shaders (`KSP/Scenery/*`, with the two ground shaders and `Diffuse Multiply`) and
`DepthMask` **[src]** (`S:Replaced shaders`).

## The G-buffer it writes

All replacements are Unity surface shaders, so Unity packs the targets (`U:UnityGBuffer.cginc`,
`UnityStandardDataToGbuffer`) **[src]**.

| Target | Channels | Value Deferred gives |
|---|---|---|
| G0 | rgb, a | diffuse colour (albedo times one less the specular colour); occlusion, 1 unless a shader sets it |
| G1 | rgb, a | specular colour; smoothness |
| G2 | rgb, a | world normal times 0.5 plus 0.5; unused |
| G3 | rgb, a | emission only, as `exp2(-x)` without HDR; the alpha is 1 for every shader but `DepthMask` |

The parts' spherical-harmonics ambient is zeroed in the deferred pass (`unity_SHAr`,
`unity_SHAg`, `unity_SHAb` are 0). Deferred adds the ambient itself, below **[src]**
(`S:ReplacementShader.cginc`).

| Family | Albedo, specular, smoothness | Source |
|---|---|---|
| Parts, scenery with `SPECULAR_ON` | albedo `_Color * vertex colour * _BurnColor * _MainTex`; specular `saturate(_SpecColor)/pi`; smoothness `_Shininess^0.215 * albedo.a * sqrt(length(specular))` | **[src]** `LegacyToStandard.cginc` |
| Parts, scenery without it | specular `0.5/pi`; smoothness 0.80 times albedo alpha, 0.45 where alpha is 0.99 or more | **[src]** `ReplacementShader.cginc` |
| `(Mapped)` | specular is `_SpecMap.rgb`, smoothness `_Shininess`, no division by pi | **[src]** `DeferredSpecularMappedReplacementShader` |
| Emission, `InternalSpace` | map, plus `_TemperatureColor.a * rgb`, plus a rim; `InternalSpace` takes it from its lightmap and occlusion `pow(lightmap.a, _Occlusion)` | **[src]** `Emission.cginc` |

Stencil values written with `Comp Always, Pass Replace`: parts 1, terrain 2, scenery and ground
3. Parallax's instanced grass writes 32 **[src]** (`D:Readme.md`, `P:InstancedSolid.shader`).

## Lighting and reflection passes

Deferred replaces two of Unity's built-in shaders with `GraphicsSettings.SetCustomShader` on
every scene load **[src]** (`SetupCustomLightingAndAmbientShaders`).

* `Deferred/Internal-DeferredShading` is Unity's lighting with three changes. The main
  directional light's shadow fade centre is 1e10, so shadows reach the horizon. NaN becomes 0.
  The decode pass tells `DepthMask` pixels by the emission alpha. It reads G0 to G2, the depth,
  the shadow maps and `_LightColor.rgb * atten`. `pixelLightCount` is `max(LIGHT_QUALITY, 64)`
  **[src]**.
* `Deferred/Internal-DeferredReflections` reads G0 to G2, the depth and the probe cube. It also
  computes the ambient light per pixel **[src]**:
  1. It returns 0 where `useReflectionProbeOnCurrentCamera` is 0, which is the scaled camera.
  2. Ambient diffuse is
     `deferredAmbientBrightness * Unity_GlossyEnvironment(unity_SpecCube0, normal, 0.55)`,
     lerped from its length to its colour by `deferredAmbientTint`.
  3. It lerps to `legacyAmbientColor` by `saturate((length(probe) - length(legacy)) * 100)`,
     then multiplies by G0.a.
  4. In IVA it rotates normal and view vector by `internalSpaceToWorld`.
  5. Probe specular is added only where `useSSROnCurrentCamera` and
     `disableProbeReflectionsOnCurrentCamera` are both 0, through `UNITY_BRDF_PBS`.

## The space centre ground and a shiny launch pad

The raster has no wetness property, puddle or wet texture in Deferred **[src]** (a search of
the clone). The shine at the pad comes from one chain, and EVE can add to it. Deferred gives
the tarmac a smoothness of 0.25 or 0.55 times the square root of its texture alpha, and a
gamma-space dielectric specular of 0.2209. The probe and, on the specular variant, screen-space
reflections turn that into a sheen at grazing angles.

| Step | What happens | Property and value | Source |
|---|---|---|---|
| 1 | The shader name is swapped. The surface writes stencil 3. `_BlendMaskTexture.r` on `uv2` picks tarmac or lawn. `_TarmacColor.rgb` is capped | `KSP/Scenery/Diffuse Ground KSC`, `... Specular`; cap 0.89 | **[src]** `S:DiffuseKSCGround*.shader`, `surf` |
| 2 | Smoothness: tarmac, lawn | `0.25 * sqrt(max(_TarmacTexture.a * _TarmacColor.a, 1e-7))`, the specular variant `0.55`; lawn `0.1 * grass.a` | **[src]** `surf` |
| 3 | Specular colour | `Metallic` is never written, so Unity's metallic setup gives `unity_ColorSpaceDielectricSpec`: 0.2209 in gamma space, 0.04 linear | **[src]** `U:UnityStandardUtils.cginc`; **[open]** the initial `Metallic` of a surface shader |
| 4 | `_SpecularColor` of the specular variant | declared, never read | **[src]** `DiffuseKSCGroundSpecular.shader` |
| 5 | Reflection | `ind.specular * surfaceReduction * FresnelLerp(F0, saturate(smoothness + 0.2209), n.v)` | **[src]** `U:UnityStandardBRDF.cginc` `BRDF1_Unity_PBS`; **[open]** that desktop picks BRDF1 |
| 6 | `ind.specular` comes from the probe at roughness `1 - smoothness`: 0.75 to 0.9 plain, about 0.45 at texture alpha 1 on the specular variant | `unity_SpecCube0` | **[src]** reflections shader |
| 7 | Screen-space reflections start at smoothness 0.4, weight `saturate((s - 0.4) / 0.2)`. The plain tarmac never reaches 0.4. The specular variant does from alpha 0.53, at most 0.75 | `ScreenSpaceReflections.shader` | **[src]** compose and trace passes |
| 8 | EVE's wet effect rewrites G0 to G2 before the reflections pass: `scenerySmoothness` 0.6, `sceneryDiffuse`, puddles | `EVE_WET_SURFACES_CONFIG wet-surfaces-Kerbin` | **[src]** `E:WetSurfacesPerCameraRenderer`, `WetSurfacesRenderer` |

* The probe is the stock `FlightReflectionProbe` in flight and `KSCReflectionProbe` in the
  space centre scene (see Reflections) **[src]**.
* The log counts 28 materials of the specular variant and 3 of the plain one **[meas]**
  (`KSP.log`, 2026-10-26 18:04, a ReDefinition line). Which one the pad uses is **[open]**.
  The stock `_TarmacColor` and texture alpha are **[open]**.
* EVE's `EVE/GBufferWetEffect` is in a bundle, so its formula is **[open]**. Its inputs are
  **[src]**. The installed `wetSurfaces.cfg` sets scenery `wetSmoothness` 0.6,
  `puddleAccumulationSpeed` 0.01 and `maxPuddleAccumulation` 0.75. The wetness follows rain
  history; the log shows a catch-up at the scene start **[meas]**.
* With Parallax installed, ReDefinition draws these materials with its own shader. It keeps the
  tarmac smoothness, the 0.89 cap and the dielectric specular, and replaces the lawn **[src]**
  (`src/Scenery/KscGround.cs`,
  `unity/Assets/ReDefinition/Shaders/ReDefinitionKscGround.shader`).
* `KSCModelNormalsFixer.FixKSCModels` rewrites mesh normals and tangents at start. Tarmac
  normals point up by `_BlendMaskTexture.r`. On the runway they snap to the x or z axis. The
  vertices stay **[src]**.

## Reflections

| Part | What it does | Source |
|---|---|---|
| Flight probe | KSP's own: layers Water, Local Scenery and Scaled Scenery; clear colour the sky colour; `ViaScripting` | **[src]** Assembly-CSharp `FlightReflectionProbe.Start` |
| Caps | `REFLECTION_PROBE_REFRESH_MODE` above 1 becomes 1 (individual faces); below 1 becomes 1 at any setting. `REFLECTION_PROBE_TEXTURE_RESOLUTION` above 1 becomes 1 (256). `Update` keeps the probe's size at 1e6 or more | **[src]** `HandleStockProbe`, `EnsureReflectionProbeRange` |
| `KSCReflectionProbe` | realtime, every frame, individual faces, layer 15 only, black clear, size 1e6, not HDR, parent `FlightCamera.fetch` | **[src]** `Awake` |
| Editors | `RenderSettings.customReflection` null; one probe at (0,10,0), layer 15, skybox clear, HDR, rendered once at scene start | **[src]** `EditorLighting.FixReflections` |
| SSR | first local camera only, Direct3D only, `useScreenSpaceReflections`. Inputs: depth, motion vectors (`depthTextureMode` 5), G1 smoothness, G2 normal, last frame's colour. Steps: hierarchical depth, reprojection, trace, blur, compose at `BeforeImageEffectsOpaque` (12); history copy at `AfterForwardAlpha` (17) | **[src]** `HandleCameras`, `CreateSSRCommandBuffer`, `OnPreRender` |
| SSR miss | the colour is the probe's, confidence 0; edge fade; a hit under 0.005 of the screen on a near-equal normal is dropped | **[src]** `frag` of pass 1 |
| Terrain, Parallax | the reflections pass reads only the G-buffer, so each material's smoothness decides; Parallax writes its own | **[src]** |
| Water | Deferred replaces no water shader. Scatterer's ocean registers (`RegisterScattererOceanForCamera`); SSR reads `oceanGbufferDepth` and `oceanGbufferNormalsAndSigma`, and mirrors a reflection vector that points under the planet's tangent plane (`SSRPlanetPosition`) | **[src]** pass 1 and 3 |

## Terrain and PQS

* The 13 terrain shaders write albedo `diffuse * _albedoBrightness`, `Metallic` 0, occlusion 1,
  no emission and stencil 2. Textures come from world position plus `_floatingOriginOffset`,
  triplanar, by zoom level. The colour is `lerp(vertex, vertex * texture, 0.8)`, with an atlas
  `1.75 * vertex * texture`. The vertex alpha is the altitude **[src]**
  (`S:Terrain Shaders/TerrainReplacementShader.cginc`).
* Smoothness: the 7 legacy non-zoomable shaders 0.1; the 2 zoomable ones without an atlas
  `sqrt(max(length(_specularColor.rgb), 0.1)) * diffuse.a`; the 4 atlas shaders `diffuse.a`
  **[src]**.
* `DeferredPQSFade` fades to scaled space. It reads `_PlanetOpacity` from
  `currentMainBody.pqsController.surfaceMaterial`. For a value in (0, 1] it copies the camera
  target at `BeforeGBuffer`. Then it blends the copy over stencil 2 pixels at `AfterFinalPass`
  with that weight. The G-buffer is unchanged **[src]**. Whether the fade applies at the space
  centre is **[open]**.
* Parallax's terrain has its own `Deferred` pass with stencil 2; its KSC terrain shader is not
  in its build **[src]** (`P:`). Deferred has no Kopernicus code, so a Kopernicus body is
  swapped by shader name only **[src]**. The README calls it compatible **[doc]**.
* The space centre's ground and buildings write stencil 3. `KSCModelNormalsFixer` runs over the
  upgradeable facilities and the island airfield **[src]**.

## What stays forward, and dissolve

| Item | Behaviour | Source |
|---|---|---|
| `KSP/Alpha/Translucent`, `... Specular` | forward, queue Transparent, `alpha:fade`, `o.Alpha = _Opacity * color.a` | **[src]** `Alpha.shader` |
| `(Cutoff)` specular shaders; `KSP/Alpha/Cutoff` | deferred, stencil 1. The specular ones define no `CUTOUT_ON`, so they do not clip alpha. `Alpha/Cutoff` clips at `_Cutoff` | **[src]** `SpecularCutoff.shader`, `AlphaCutoff.shader` |
| Dissolve | skipped above 0.99; else a pixel is dropped when `snoise(worldPos * 2) * 0.5 + 0.5` exceeds `saturate((_Opacity - 0.2) / 0.8)` | **[src]** `Dissolve.cginc` |
| Dither | on `DITHER_FADE_ON` shaders only when `useDitheredTransparency` is set; off in the installed build | **[src]** `ditherClipTexture` |
| `DepthMask` | `ColorMask 0`, `ZWrite On`, queue Geometry-1000, no G-buffer | **[src]** `DepthMask.shader` |
| Decals, shadow receiver, other forward shaders | not replaced, so forward, no deferred ambient. The editor's shadow plane is forward with `Blend DstColor Zero` | **[doc]** `D:Readme.md` |
| Sky, clouds, ocean, godrays, SSAO | event buffers around the forward passes | **[doc]** `D:VisualsRenderingOrder.md` |

## Configuration

`GameData/zzz_Deferred/Deferred.cfg`, node `Deferred_config`, read once by
`Settings.LoadSettings` from the first `Deferred` config that has the node. The installed file
equals the defaults **[src]**. Nothing writes it back.

| Key | Default and installed | Effect on the picture |
|---|---|---|
| `ambientBrightness` | 0.9 | scales the ambient diffuse; global `deferredAmbientBrightness` |
| `ambientTint` | 0.7 | 0 is grey ambient, 1 the probe's colour; global `deferredAmbientTint` |
| `useScreenSpaceReflections` | True | adds the SSR rig; with it the reflections pass skips probe specular |
| `useHalfResolutionScreenSpaceReflections` | True | traces at half width |
| `capReflectionProbeRefreshRate` | True | see the reflections table |
| `capReflectionProbeResolution` | True | probe 256 at most |
| `useDitheredTransparency` | False | dither on diffuse shaders |
| `useSmaaInEditors` | True | SMAA on the editors' camera |
| `guiModifierKey1`, `guiModifierKey2`, `guiKey` | LeftControl, LeftAlt, D | none; debug window |

The installed `settings.cfg` has refresh mode 1 and probe resolution 1, so the caps change
nothing here **[meas]**.

## Detection

| What | Exact name | Source |
|---|---|---|
| Assembly | `Deferred`, 1.3.5.0, depends on `0Harmony`, `Shabby` | **[src]** `D:Deferred.cs` |
| Add-on, settings | `Deferred.Deferred`, `KSPAddon(MainMenu, once)`; private `Settings settings` of `Deferred.Settings` with the public fields of the table above | **[src]** |
| Active | `GraphicsSettings.GetCustomShader(BuiltinShaderType.DeferredShading)` is `Deferred/Internal-DeferredShading`; `Camera.renderingPath` is DeferredShading | **[src]** |
| Shaders | `ShaderLoader.ReplacementShaders`, `DeferredShaders` (public static); the keys are the shader names | **[src]** |
| SSR | `Deferred.ScreenSpaceReflections` on the first local camera; private `targetCamera`, `screenColor`, `RecreateTexturesAndBuffers(int, bool)`; public `RegisterScattererOceanForCamera` | **[src]** |
| SSR switch | `OnPreRender` sets `useSSROnCurrentCamera` 1 and adds its buffers; `OnPostRender` removes them and sets 0; global `ssrOutput` | **[src]** |
| Globals | `useReflectionProbeOnCurrentCamera`, `disableProbeReflectionsOnCurrentCamera`, `useSSROnCurrentCamera`, `deferredAmbientBrightness`, `deferredAmbientTint`, `internalSpaceToWorld`, `legacyAmbientColor`, `_DeferredUseDitheredTransparency`, `_LightColor0`, `_WorldSpaceLightPos0` | **[src]** |
| Other parts | `Deferred.DeferredPQSFade` on the first local camera; a quad of `ForwardRenderingCompatibility` under each camera (`DummyForwardShader`, bounds 1e18, vertex at (2,2,2,1)); the probes `Deferred KSC Reflection Probe` and `Deferred VAB/SPH Reflection Probe` | **[src]** |

ReDefinition pins these in `src/Core/ForeignAssumptions.cs`: `deferred-reflections` (the
globals above), `deferred-ssr`, `deferred-ssr-hold` (`OnPreRender`, `OnPostRender`, fingerprint
`17e177397045`), `deferred-smaa`.

What `src/Raytracing/RtDeferredLook.cs` and `RtMaterialIntent.cs` assume, against the above:

| Assumption | Verdict |
|---|---|
| Metallic setup for the ground, `Diffuse Multiply` and PQS: diffuse share `1 - dielectric` | **[src]** holds |
| Specular `_SpecColor / pi`, diffuse parts `0.5 / pi`, mapped parts the mean of `_SpecMap` | **[src]** holds |
| Legacy divisor `_Shininess^0.215 * sqrt(length(c))` | **[src]** holds |
| The ground's specular colour is `_SpecularColor` | **[src]** differs: Deferred never reads it; the specular is the dielectric one |
| The ground and `Terrain/PQS/*` shine not at all (`Dry`, `Ground`) | **[src]** differs: the raster shines with the smoothness and probe above |
| Quads hung on cameras are no part of the scene (`src/Raytracing/Ksp/RtKspSources.cs`) | **[src]** holds |

## What the raster depends on and rays do not see

* The probe's content: the layers it culls, its sky colour, 256 texels and blur by roughness.
  It is the only source of ambient and of rough reflections **[src]**.
* The ambient formulas: brightness 0.9, tint 0.7, the legacy fallback, and no ambient on the
  scaled camera **[src]**.
* Last frame's picture, reprojected by motion vectors, as the source of screen-space
  reflections. It holds transparents, the ocean and clouds, and lacks everything off screen
  **[src]**.
* The PQS fade: near it the picture shows scaled space where the geometry is still solid
  **[src]**.
* Per-renderer values KSP sets at run time: `_Opacity`, `_BurnColor`, `_TemperatureColor`,
  `_RimColor`, `_RimFalloff`. They change dissolve, albedo and emission **[src]**.
* The main light as `SunLight` gives it (`ForwardRenderingCompatibility`), with Scatterer's and
  EVE's cloud shadows and eclipses in its shadows and no shadow fade **[src]**.
* Edited mesh normals. `DepthMask` meshes hide what lies behind them without a colour. Other
  mods write the G-buffer too: EVE's wet effect, Parallax's pass, Scatterer's ocean **[src]**.
* The editors' flat ambient, zero fog and the shadow plane's multiply **[src]**.
