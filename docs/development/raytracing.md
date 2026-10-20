# Raytracing

**For:** anyone building or changing ReDefinition's raytracing.
**You need:** `src/DxgiProxy` to hand, and a GPU with DirectX Raytracing tier 1.1 for
the tests.
**You get:** how raytraced lighting reaches KSP's frame, what each part does, and how it
is tested without the game.

Every claim here carries its mark: **[src]**, **[doc]**, **[meas]** or **[open]**
([writing-these-pages.md](writing-these-pages.md)).

## What raytracing replaces

With raytracing on, these parts of the lighting of the scene camera come from rays traced
through the scene instead of from Unity's approximations:

| Part | Unity's approximation | Raytraced |
|---|---|---|
| Sun shadows | cascaded shadow maps, a few kilometres deep | a shadow ray per pixel, soft by the sun's angular size, as far as the scene reaches |
| Ambient light | KSP's flat or gradient ambient colour, or Deferred's reflection probe, blurred; the same for every point | one or more bounces of diffuse light, from the sky, the sun, the moons and lit surfaces |
| Ambient occlusion | none | part of the traced diffuse light: sky a point does not see brings it no light |
| Reflections | the reflection probe, or Deferred's screen-space reflections | rays into the scene, sharp or rough by the surface's smoothness |
| Emissive surfaces | light only themselves | light what is around them |

Transparent and volumetric things keep their own rendering: plumes, reentry effects,
particles, clouds and the atmosphere. They take the raytraced shadows where they receive
Unity's screen-space shadow, and are not in the scene the rays see. The sea is not in the
scene either, but its reflections are traced (see "Water"). EVE's cloud layer darkens the
rays' light under it (see "EVE's clouds").

## How the frame goes

KSP renders with Direct3D 11, which has no raytracing. The proxy already runs a Direct3D 12
device beside Unity's, shares textures with it and runs compute passes on it
([shared-foundation.md](shared-foundation.md)). Raytracing runs there, with ray queries in
compute shaders (DirectX Raytracing 1.1, shader model 6.5) **[doc]**.

1. Unity renders frame N as usual. With Deferred, the scene camera fills its G-buffer:
   albedo and occlusion, specular colour and smoothness, the world normal, and emission
   **[src]**. Without Deferred, KSP renders forward and there is none.
2. Between the G-buffer and the lighting, a command buffer in the scene camera's own
   stream copies depth and the G-buffer into the raytracing inputs, and a render event
   starts the raytracing on Direct3D 12 (`RtCamera.TraceBeforeLighting`). With Deferred it
   runs at `CameraEvent.BeforeReflections`, as the reflections pass takes the diffuse light
   and the reflections; without it at `CameraEvent.AfterDepthTexture`. It is filled in the
   camera's `OnPreRender`, with the upscaler's jitter applied first
   (`CameraRedirect.EnsureJitter`).
3. The proxy traces the rays, denoises the signals and copies them back into Unity's
   textures before the render event returns: Direct3D 11 waits on a fence for Direct3D 12.
4. Frame N's lighting reads frame N's signals, at the same pixel.

The render event sees the frame it is issued in **[meas]** (test player, a camera turning
by 12 degrees a frame: the view depth the trace keeps matches that frame's depth, 15.73
against 15.74 m at the centre; with Unity's graphics jobs native, legacy and off alike).
An earlier probe that found camera events a frame late rendered into a texture with 2x
MSAA, which Unity resolves only at the end of the camera **[src]** (the probe's
QualitySettings).

The DLSS Ray Reconstruction guide asks for "the Noisy Ray Traced Input Color" of the frame
and independent samples (3.4.5, 3.5) **[doc]**: signals of the previous frame, carried over,
are neither, and where the previous frame did not see a point there is none to carry.
Traced in its own frame, the lighting has a signal for every pixel **[meas]** (test player,
the camera turn against each pose's settled frame, where something came into view:
ReDefinition's denoiser 0.0011 against 0.0065 carried over, Ray Reconstruction 0.0041
against 0.0093, with the jitter and Deferred's shaders 0.0039 against 0.0061).

Unity draws the motion vectors after the lighting: ReDefinition's denoiser follows the
camera's motion alone, not that of moving parts. DLSS Ray Reconstruction gets the motion
vectors from the upscaler, after the frame.

With `TraceBeforeLighting` off, `RtCamera` traces in `OnRenderImage` instead, after the
frame, and the next frame's lighting reads the signals carried over by depth and the two
frames' camera matrices, corrected for KSP's floating origin and Krakensbane; the test
player's turn runs measure it beside the other. The carry-over below describes that mode.

## Where the raytraced light enters Unity's lighting

| Signal | Enters through | Why there |
|---|---|---|
| Sun visibility | ReDefinition's shader for Unity's screen-space shadows (`GraphicsSettings.SetCustomShader`, `BuiltinShaderType.ScreenSpaceShadows`), for the sun's mask alone | every surface that receives the sun's shadow reads that mask, forward and deferred; EVE's cloud and eclipse shadows and Scatterer's ocean caustics multiply into it afterwards, at `LightEvent.AfterScreenspaceMask` **[src]** |
| Diffuse light, occlusion, reflections | ReDefinition's shader for Unity's deferred reflections, in place of Deferred's while raytracing is on | Deferred adds its ambient light and the probe's reflection in that pass **[src]** |
| Diffuse light and reflections, without Deferred | ReDefinition's shader drawn into the frame after the opaque geometry's lighting | no pass of Unity's takes them; see below |

Deferred sets its own shaders for Unity's deferred shading and deferred reflections
**[src]**. ReDefinition's reflections shader keeps what Deferred's does besides ambient
light and the probe: the IVA's rotated normals, the LDR encoding and the alpha of the
lighting buffer. It is derived from Deferred's shader, which is GPL 3.0 like ReDefinition,
and credited in [credits-and-licences.md](../credits-and-licences.md). When raytracing is
off, Deferred's shader is set again.

The raytraced diffuse light never falls below the player's share of KSP's own night
light (*Night light*, 0 % unless set). KSP's night light is its flat ambient colour
(`RenderSettings.ambientLight`), which Deferred hands its shader as `legacyAmbientColor`
(`RefreshLegacyAmbient`) **[src]**. ReDefinition's reflections shader mixes the raytraced
light with that colour times the share, as Deferred mixes its probe's light. At 100 % a
planet's night side keeps the brightness KSP gives it **[meas]** (test player: the ground
at 0.323 with raytracing and without). Lower, the night is darker and the moonlight's
shadows show **[meas]** (KSP, at the launch pad at night with the Mun up, moonlight 150 %:
the craft at 17, 23 and 41 of 255 with 0, 30 and 100 %, 41 without raytracing; the far
grass at 20 and 31, 29 without).

Some surfaces bring KSP's flat ambient light into the frame in their own G-buffer pass:
their albedo times `RenderSettings.ambientLight`, written into the light buffer. Unity's
shaders do that where Deferred does not replace them **[src]**. At the space centre at
night, its flag (a `SkinnedMeshRenderer`) and the launch pad's grate held that light,
every other surface none **[meas]** (KSP, `-redefinitionProbeLightBuffer`). It kept the
day's light on them at night. Where the raytraced diffuse light replaces the ambient
light, a pass after the G-buffer therefore takes it out again (`CameraEvent.AfterGBuffer`,
pass 14 of `ReDefinitionRtCapture.shader`, `fragUnbakeAmbient`) **[src]**. It acts only
where the light buffer holds that light and nothing else, within a tenth, so an emissive
surface keeps its light. It runs in HDR only: in LDR, Unity keeps the light buffer
encoded **[src]**. The raytraced diffuse light then comes in the reflections pass, as for
every other surface.

The setting *Light colour* (`RaytracingLightColour`) sets the colour of the raytraced
diffuse light; its strength stays the rays' own. *Raytraced* keeps the rays' colour.
*As without raytracing* gives it the colour of Deferred's ambient light at the pixel.
*Neutral*, the default, makes it grey. The rays carry the sky's full colour, while
Deferred tints its ambient light by 0.7 (`deferredAmbientTint`, `zzz_Deferred/Deferred.cfg`)
**[src]**. In shadow, where the sky's light is all a blade of grass gets, the rays'
colour turned it blue by day and orange in the evening. Against the frame without
raytracing at the launch pad, the vegetation's red and blue chromaticity were off by
13 and 14 thousandths by day, 14 and 19 in the evening and 25 and 21 at night; with
*Neutral* by at most 6 **[meas]** (KSP, `-redefinitionProbeRtAmbientTint`, tiles of the
same shot). ReDefinition's reflections shader takes the choice in the fourth component
of `_ReDefinitionRtLight`. The choice acts only through that shader: without Deferred's
reflections pass to replace, the raytraced light goes in as it is, and the settings
window greys the row out (`OurModules.cs`) **[src]**.

KSP moves its world between two frames in two ways under Krakensbane: the floating origin
shifts what is near the vessel, and the bodies move by the frame's velocity as well
**[src]**. The lighting shaders try both shifts and take the one whose view depth matches
the depth the raytraced frame kept for the pixel.

Of the four texels around the point, the shaders read the one whose kept depth matches
best, at its centre: a bilinear read would mix a silhouette's light with the ground's
behind it. The point counts as seen where that texel's depth lies within 5 % of its own,
or its depth lies between the four kept ones, as on ground seen at a grazing angle. The
kept depth is a 32-bit float, so this holds past 65 km, in orbit. Otherwise the raytraced
frame did not see the point: it came out from behind something, or the camera turned
towards it. No sun visibility is carried there, and Unity's shadow maps shade it for that
frame **[meas]** (test player: with the camera turned from the sun's side, the near box's
shadow in the first frame at 89 %, 89 % and 53 % of the light beside it, between the shadow
maps' 91 %, 90 % and 58 % and the raytracing's 82 %, 82 % and 47 %, as its indirect light
comes from the fill below; before the rejection, at 100 %). A temporal filter after it,
DLSS Ray Reconstruction or ReDefinition's denoiser, would otherwise keep the wrong shadow
there for many frames.

The diffuse light and the reflections of such a point come from the nearest texel around
it, up to 512 texels away in eight directions, whose kept depth lies within 10 % of its own;
a point that left the frame's edge looks from the edge. NRD fills its history after a
disocclusion the same way. Unity's flat ambient light for a frame and the raytraced light
the frame after would be a jump that DLSS Ray Reconstruction takes for noise and keeps:
a band trailing a silhouette, and the frame's edge, as the camera turns **[meas]** (test
player: ground behind the near box, uncovered as the camera rises by a metre, 0.001 apart
in its first frame between a dim flat and a bright gradient ambient light, 0.185 without
the fill; a camera turn around a rocket at up to 12 degrees a frame through DLSS Ray
Reconstruction, against each pose's settled frame: an error of 0.012 in the tone-mapped
luminance where something came into view, 0.070 without the fill, 0.002 elsewhere). In
the game, Deferred's ambient light where the fill finds nothing is far darker than the
raytraced light **[meas]** (a recording at 2560 by 1440, a turn of about 12 degrees a frame
around a rocket on the pad: the ground behind it uncovered and not filled at a luminance
of 0.25 to 0.36 against 0.31 to 0.48 where it was seen or filled; with a reach of 32
texels, 53 to 70 % of the points the previous frame did not see were filled, with 512, 87
to 94 %, computed from the recording's depth and cameras).

The screen-space shadows shader is derived from Unity's built-in one, which is MIT **[doc]**.
For every camera but the scene camera it does what Unity's does, and for every light but
the sun: a flag set by command buffers on the sun's light, before and after its mask
(`LightEvent.BeforeScreenspaceMask`, `AfterScreenspaceMask`), picks the raytraced
visibility. PlanetShine's lights and Kopernicus's other stars keep their shadow maps.

Scatterer and Kopernicus set a screen-space shadows shader of their own for long shadow
distances **[src]** (Scatterer.cs, ShadowMan.cs; Kopernicus on every scene). While
raytracing runs, ReDefinition's takes its place, and when raytracing goes, the one set
last comes back. For the sun on the scene camera the rays reach as far as the scene.

### Without Deferred

Raytracing needs no other mod. Without Deferred, KSP renders forward and has no G-buffer,
and raytracing works as follows:

* Sun shadows enter through the same mask, which forward shading reads too. Unity fades
  shadows out at the shadow distance, the raytraced ones too; Deferred turns that fade
  off, so with Deferred they reach as far as the scene **[meas]** (test player).
* The proxy takes each pixel's surface from a ray of its own. The camera's ray to the
  point the depth gives lands on the triangle Unity drew there: its normal, its
  material's albedo, specular colour and smoothness **[meas]** (harness: the sun's
  visibility the same on 100 % of the pixels, the diffuse light the same to 0.01 %).
* A pass of the proxy works out the light to add (`ForwardLight` in `Raytracing.hlsl`)
  **[src]**. The traced diffuse light has the occlusion in it, and is kept above the
  player's share of KSP's flat ambient colour, as with Deferred.
* Forward rendering relights the frame by its own colour, not by a guess from the
  material. Per channel, k = (diffuse light - ambient) / (sun + ambient), with the sun
  as Unity shaded it: its colour times N.L, or Disney's diffuse term for Unity's standard
  shader, darkened by its shadow at the light's shadow strength and faded out towards
  the shadow distance as Unity fades it (`UnityComputeForwardShadows`; the fade's values
  read back from the GPU after the sun's mask) **[src]**. A frame colour c becomes
  c + min(c, w) k, w the largest channel of sun + ambient, so a pixel other lights
  brightened is capped (`ReDefinitionRtForwardLight.shader`). The pass reads its copy of
  the frame at the pixel's own texel: where the camera draws straight into the back
  buffer, the copy keeps the back buffer's rows **[meas]** (test player).
* The sun's highlight a shader draws stays as drawn: the proxy works it out as Unity's
  standard shader (`BRDF1_Unity_PBS`, GGX) or its legacy Blinn-Phong does
  (`RtForwardHighlight`), and takes its share of k back out **[src]**. Shaders whose
  highlight is not known (a smoothness or gloss map, KSP's mapped ones) keep it in the
  relit colour.
* k takes the share of a surface's colour that Deferred would shade as diffuse
  (`RtDeferredLook`): Deferred draws KSP's shaders with Unity's standard shading, which
  takes the specular colour off the diffuse one **[src]** (Deferred 1.3.5's replaced
  shaders). The terrain and the space centre's ground take a dielectric's share, KSP's
  part and scenery shaders that of their specular colour. The terrain and the space
  centre's ground take the same share, so that they keep the balance KSP's own shaders
  draw them in.
* With Unity's deferred lighting and no Deferred, the light to add is the G-buffer's
  albedo times the diffuse light less Unity's ambient light, which its shading added
  already.
* Either way the reflections come on top, weighed by Unity's Fresnel term and its
  surface reduction (`BRDF1_Unity_PBS`) **[src]**.
* ReDefinition's shader adds that light to the frame after the opaque geometry's
  lighting: after Unity's forward opaque pass, or with Unity's deferred lighting after
  the lighting, as the forward opaque pass is left out when nothing is drawn forward
  **[meas]** (test player). Without HDR, Unity's deferred lighting keeps `exp2(-light)`
  **[src]**; the shader multiplies there instead of adding.

Unity's ambient light is its ambient probe, which `LightProbes.GetInterpolatedProbe`
returns; `RenderSettings.ambientProbe` holds only a baked one and is zero in a player
**[meas]** (test player). It is handed over facing up, sideways and down. For flat and
gradient ambient light, which is what KSP's scenes use **[src]** (their `RenderSettings`),
the quadratic in the normal's height through those three values is the probe itself.

The test player checks it: between a dim flat and a bright gradient ambient light, the
near box's shadow changes by 0.185 without raytracing, and with it by 0.002 with forward
rendering and 0.001 with Unity's deferred lighting **[meas]**.

While the raytraced reflections come in without Deferred, Unity's own are off for the
scene camera: its reflection probes, KSP's on the vessel among them, and the sky's
(`RenderSettings.reflectionIntensity`), set back after the camera for the others
**[src]**. Kept, a standard shader's surface shone with both; in the test player the
probe added 0.098 to the near box's shadow **[meas]**.

## What the rays see

The scene is a set of meshes, each with a transform per frame. The C# side collects them,
the proxy builds the acceleration structures: one per mesh, and one over all of them each
frame.

| Source | Taken from | Changes |
|---|---|---|
| Vessel parts | `MeshFilter.sharedMesh`, readable as KSP loads them **[src]**, within 5 km | skinned meshes baked when their pose changes |
| Scenery | the renderers under each `PQSCity` and `PQSCity2`, the space centre and the launch sites, within 8 km; in the editors the VAB's and SPH's halls (`VABscenery`, `SPHscenery`) **[src]** (`RtScenerySource`) | looked through once a scene, again 5 and 20 s into it, and a second after a building is upgraded, collapses or is repaired; skinned meshes, the space centre's flags, baked twice a second; an unreadable mesh is left out, and the log's line every 30 s counts those left out and names the last **[src]** |
| Terrain | each visible leaf `PQ`'s mesh within 30 km, from the quad tree (`PQS.quads`, `PQ.subNodes`) **[src]** | handed over again when the quad moves or its edges change **[src]**; Parallax's finer copy near the camera in place of the quad, whose material discards it (`Custom/DiscardAll`) **[src]**; the copy, rebuilt in place, handed over again when its shape changes, at most every ten frames |
| Parallax scatter | the append buffers Parallax fills on the GPU each frame, one per scatter and level of detail, with their meshes and materials **[src]** | gathered into a texture on Unity's side, turned into instances on the proxy's, as their count is only known on the GPU; each buffer gets the places it took lately, read back a frame or two late, and a quarter more, not its capacity **[meas]** (test player: 25 096 places for 20 000 rocks in a buffer of 100 000); where ReDefinition lifts Parallax's grass onto the space centre's lawns, the rays take the clumps no ground of the space centre covers, as the clumps under it stand on the terrain below, out of every ray's reach (`KscGrass.RaysBuffer`) **[src]** |
| The main body | a sphere, tested by formula in the sun's rays, so that its night side and what flies over it lie in its shadow | none |

Left out: anything transparent or additive (Waterfall, Firefly, particles, translucent
parts), EVE's clouds as geometry, the sea's surface, KSP's and Scatterer's alike,
Scatterer's atmosphere, and the UI. The sea and EVE's cloud layer reach the rays another
way: see "Water" and "EVE's clouds".

KSP moves the world with its floating origin and, under Krakensbane, every physics frame
without an event **[src]**. The transforms are therefore taken every frame, relative to the
camera, so that the scene near the camera keeps full float precision. Each frame's
instances go to the proxy with the frame's index, and the raytracing of that frame takes
them: the main thread runs ahead of the render thread.

Only what the scene camera draws is in the scene: renderers on a layer outside its culling
mask stay out, such as the IVA, which FreeIva and ThroughTheEyes draw with the internal
camera **[src]**.

Parallax displaces terrain on the GPU near the camera, fading out at 50 m **[src]**, by
some tenths of a metre **[open]** (the amount follows from its configuration; no value is
stated). The drawn surface can therefore lie below the mesh the rays see. Rays from a
pixel's surface pass through terrain for their first metre; everything else still stops
them **[meas]** (harness: ground 0.3 m above the drawn one leaves 593 110 of 593 110 lit
pixels lit as terrain, 3 275 as another mesh).

Parallax culls its scatter to the view and a short radius around the camera **[src]**:
rocks and trees behind the camera beyond that radius cast no raytraced shadow.

The first metre of terrain is passed through from every pixel, a vessel's too: under a
landed vessel, the ground within a metre of its underside neither shades it nor darkens
it there.

## Surfaces the rays hit

A ray that hits something shades it from what the proxy holds per mesh and per material:

* the normal and the texture coordinate, from the mesh;
* the albedo: the material's colour times a small copy of its main texture, 64 by 64
  pixels, which Unity renders once per material into a texture array; for Unity's
  standard shaders less what their specular colour takes of it, as they shade it
  (`UnityStandardUtils.cginc`) **[src]**; for Parallax's terrain, its colour as
  Parallax gives it (see below);
* specular colour and smoothness, as the material's shader means them (its surface
  intent, below) **[src]**;
* emission;
* where the material is cut out, the alpha below which it is: Unity's and KSP's cut-out
  shaders, and Parallax's scatter, whose grass sits at the opaque queue with its own
  keyword (`ALPHA_CUTOFF`) **[src]**.

The small texture is read from the full-size one in 8 by 8 points per texel. For a cut-out
material its alpha is the share of points the cut-out keeps, its colour that of those
points alone: a mip level's averaged alpha would blur a blade of grass into the threshold,
and the colour of the cut-away texels is never seen. The texture is read again when the
material's main texture changes. Parallax sets its scatter's textures a frame or more
after it makes the materials (`ScatterRenderer.SetMaterialTexturesAsync`) **[src]**, and
read only once, its cards would stay untextured.

Parallax's terrain shaders colour the ground with their influence map
(`INFLUENCE_MAPPING`, always in `KSCTerrain.shader`) as
`lerp(vertexColour * (luminance(texture) + 0.5), texture, influence)`
(`ParallaxUtils.cginc`) **[src]**. The vertex colours carry the planet's colour map; the
KSC's shader takes `_GrassColor` for them. Kerbin's influence averages 0.09 to 0.13, and
its grass texture is a bright yellow green (gamma 0.523, 0.655, 0.039) **[meas]**
(KSP.log). Taken alone, as the rays first did, the ground was about twice as bright as
drawn and had almost no blue: the yellow green under a craft. The rays therefore take
`vertexColour * (L + 0.5) * (1 - I) + T * I`, with T, L and I the band's texture, its
luminance and its influence channel averaged, all in Unity's colour space
(`RtTerrainColour`). The meshes hand their vertex colours to the proxy, which adds them
at each hit and takes the sum to linear. Against the G-buffer, on a terrain quad 136 km
out, the rays take 0.249, 0.288, 0.143 where Parallax drew 0.243, 0.255, 0.187
**[meas]** (KSP, `-redefinitionProbeLight`).

A cut-out surface is non-opaque geometry. A ray that meets it reads the share its small
texture keeps there and stops with that chance: on average the surface lets through what
its cut-out does **[meas]** (harness: a card covered on its left third, half covered in the
middle, cut out on the right, leaves the ground under it in the sun by 0.000, 0.506 and
1.000; as a GPU instance by 0.000, 0.512 and 1.000; with the cut-out off, 0.000 under all.
Test player: cards a quarter covered in stripes coarser and finer than the small texture's
texels shade the ground alike, 95 % and 95 %, 95 % and 95 %, 86 % and 87 % of the light
beside them). A cut-out surface whose texture is not read yet lets every ray through
**[meas]** (harness: 1.000 under all of the card): as an opaque card it would shade the
ground under it as a square.

A hit further along a ray is shaded as diffuse: its albedo takes the light, and its
specular values are not read **[src]** (`Radiance` in `RtCommon.hlsli`). The specular
values serve the surface a pixel shows.

### Surface intent

Deferred gives every KSP shader a shine by one formula (`LegacyToStandard.cginc`), the
diffuse-only shaders and the space centre's ground included **[src]**. Its G-buffer
turned matt surfaces glossy and KSP's highlights into mirrors. With a G-buffer, the
sun's pass therefore finds each pixel's material by the camera's ray and writes the
shine that material means into the G-buffer's specular buffer, before the reflections
and the lighting read it (`SurfaceIntent` in `RtCommon.hlsli`, the surface pass of
`ReDefinitionRtCapture.shader`) **[src]**. `RtMaterialIntent` sorts each material by its
shader's family, read from KSP's compiled shaders and Deferred's replacements **[src]**:

| Intent | Materials | Shine |
|---|---|---|
| `DRY` | KSP's and Unity's diffuse, bumped, cut-out, lightmapped, decal and flag shaders on the scenery; KSP's own terrain (`Terrain/PQS/*`); the space centre's concrete, and its buildings left at Unity's default highlight | none |
| `LEGACY` | KSP's legacy specular shaders, the space centre's metal among them; Parallax's terrain | Blinn-Phong's highlight: the roughness from its exponent, a specular colour as bright as the highlight, times the gloss Deferred's smoothness gives back |
| `UNITY_LEGACY` | Unity's own legacy specular shaders | as `LEGACY`, the gloss from their specular colour |
| `FIXED` | the space centre's ground with a specular colour of its own | that colour, at a smoothness of 0.25 |
| `GROUND` | the space centre's ground | the G-buffer's on the lawn, none on the rest (below) |
| `WATER` | KSP's sea | the G-buffer's; the water's reflections replace it (see "Water") |
| `KEEP` | Unity's physically based shaders, a vessel's parts without a highlight, every other shader | the G-buffer's own, at a smoothness of 0.95 at most |
| `MISSED` | a pixel whose material the ray did not find, such as an edge or Parallax's displaced ground | the G-buffer's, its smoothness read as KSP's highlight (`LegacySmoothness`) |

Without a G-buffer, a pixel's surface comes from the rays, and the material's intended
shine with it (`IntendedSpecular`). The harness checks four intents and that the
reflections read them.

The editors draw the VAB's and SPH's halls with a camera of their own, on the scenery's
layer, which the surface pass never reaches **[src]**. Deferred's G-buffer made the
hall's concrete a mirror there (`_SpecColor` 1, `_Shininess` 1). While the raytracing runs
with Deferred, `EditorHallShine` gives the halls' materials values from which Deferred's
own conversion writes the shine their intent means: the matt ones the specular variant
of their shader with a black specular colour, KSP's legacy highlight a specular colour and
shininess at the gloss's mean **[src]**. The rays keep the shine the original values mean
(`RtMaterialIntent.Hold`). Everything is put back when the raytracing stops or the scene
goes.

The space centre's ground (`Diffuse Ground KSC`) draws its tarmac, concrete and lawn
with one material. In the drawn image the proxy keeps the G-buffer's shine on its lawn
and takes none on the rest (`RT_SURFACE_GROUND`, `SurfaceIntent` in `RtCommon.hlsli`)
**[src]**. It tells them apart by the drawn colour's saturation: under 0.10 on the
grey ground, 0.30 to 0.50 on the lawn **[meas]** (KSP, at the launch pad). Without the
raytracing, Deferred's G-buffer gives the lawn a specular colour of 0.22 at a smoothness
of 0.013, which lifts it at a glancing view to the brightness of Parallax's terrain
beside it **[meas]**. Fully matt, the lawn at the horizon was a dark strip: 66 of 255
against the terrain's 100, where the frame without the raytracing has 89 against 94.
With its shine, 100 against 108 **[meas]**. Rays that hit the ground see it matt.

A ray that hits nothing takes the sky's light from a capture of the scene's reflection
probe, Deferred's or KSP's own in flight (`FlightReflectionProbe`) **[src]**, and the sun's
disc for sharp reflections. Without a probe it takes KSP's ambient sky colour.

Without Scatterer, the sky's light is taken grey at its own luminance (`SkyNeutral`,
`SkyLight` in `RtCommon.hlsli`) **[src]**. KSP's own sky is a painted blue under a white
sun, and as light it turned the daylight blue. Scatterer's atmosphere tints the sun warm,
which balances its sky's blue **[meas]** (KSP, at the launch pad: the sky's light 0.065,
0.135, 0.314 linear under a white sun; with Scatterer 0.043, 0.093, 0.194 under a sun of
0.73, 0.57, 0.34). With Scatterer the sky keeps its colour.

## Light sources

Every light the rays carry is traced with a shadow ray and bounces off what it lights:

| Source | Where it comes from | Shadow |
|---|---|---|
| The sun | the scene's sunlight, its colour and the size of its disc | a ray towards the disc, and the main body's sphere |
| Moons | each a disc of its angular size, lit by the sun (see below) | a ray towards the disc, as far as the body's surface |
| The planet | rays that miss the scene and meet the main body's sphere: its albedo times the sun's and the moons' light there (see below) | the scene, as occlusion |
| The sky | the other rays that hit nothing take the reflection probe's capture, or KSP's ambient sky colour | the scene, as occlusion |
| Emission | the material's emission | none; it lights what its rays reach |
| Bounces | each hit lit by all of the above, times its albedo, again for each bounce | as for each source |

### Linear light

KSP renders in Unity's gamma colour space: its colours, its G-buffer and its lights are
gamma-space values **[src]**. Light adds up only in linear values. Averaged in gamma
values, a hemisphere half sky of 1.0 and half grass of 0.1 linear gives the grass a share
of 26 % in place of 9 %. The bounce light then takes far too much of the ground's colour,
as green under a craft over grass did **[meas]**.

The rays therefore carry linear light. In gamma space, the proxy takes each colour Unity
hands over to linear with Unity's own curve (`GammaToLinearSpace`, `UnityCG.cginc`): the
G-buffer's albedo, the sun's and the sky's colour, the reflection probe's capture. The
materials' colours and small textures come linear from the C# side (`RtScene`). The
signals stay linear through the denoiser, which also averages. Each consumer then takes
them back into gamma space with `LinearToGammaSpace`: the reflections shader under
`UNITY_COLORSPACE_GAMMA`, the forward pass, and Ray Reconstruction's albedo guide, so that
it matches the frame it guides. Converting per sample, before the denoiser, would bias the
average **[src]** (Jensen's inequality). A game in linear colour space converts nothing.

After the change, the craft's underside over the launch pad's grass keeps the colour
ratio it has without raytracing **[meas]** (green over red 0.96 with raytracing, 0.97
without).

### Moons as lights

The moons of the planet the camera is at light the scene as the sun does; on a moon, the
planet it orbits does too. Each is a disc of its angular size, and its light reaches only
where the disc is in view with nothing between. The Mun lights the launch pad only while
it stands over it. Bodies of other planets are left out: their light is far below what
the screen shows.

Physically, a sphere that scatters by Lambert's law sends, at phase angle a,
`E = E_sun * albedo * 2/3 * phase(a) * (R/d)^2`, with
`phase(a) = (sin a + (pi - a) cos a) / pi` (Lambert's sphere). The full Mun over Kerbin
sends some 6e-5 of the sunlight **[src]** (`RtReflectorLight`, tests). That is too faint
to see on a screen without the eye's adaptation. The raytracing therefore takes the
square root of that share times ten, eased towards 0.3 of the sunlight. This keeps the
bodies' order: Minmus is fainter than the Mun, and a crescent fainter than the full disc.
It brings the full Mun to some 0.06 of the sunlight. The player's *Moonlight* percentage
scales the result, 10 % unless set.

The lights are tinted cool, (0.83, 1.01, 1.42) at the same luminance. Moonlight is nearly
the sun's colour, but at night the eye sees it bluish (the Purkinje effect), and a night
lit in the sun's colour looked warm **[meas]** (KSP, the craft's blue over red at night:
1.26 to 1.46 with the moonlight, 1.03 without raytracing).

A body's albedo and colour are the linear average of its scaled-space texture, rendered
down to 16 by 16 pixels: Parallax's `_ColorMap`, otherwise `_MainTex` **[src]**. Parallax
leaves `_MainTex` white and loads `_ColorMap` only when the body comes into view
(`ScaledOnDemandComponent`) **[src]**. A body is grey until its texture is there, and is
read again every ten seconds **[meas]** (KSP.log: the Mun 0.176 grey, Minmus 0.358, 0.501,
0.384). The light's colour is the star's own, Kopernicus's `LightShifter.sunlightColor`
or white. The scene's sunlight is no guide: Scatterer tints it by the atmosphere, and at
night it is dark **[meas]** (moonlight orange by day, none at night, before the change).

### The moons' discs in the sky

The moons' discs in the flight's sky are a little brighter, with a soft glow round them,
the more the darker the sky (`MoonGlow`, `ReDefinitionMoonGlow.shader`) **[src]**. The
darkness is 0 by day and 1 once the sun is some degrees under the horizon
(`NightAdaptation`). The glow is drawn on the scene camera where it sees sky, before the
transparent pass, so clouds and the atmosphere draw over it. Where the main body, or at a
moon its planet, is in front of a moon, or Scatterer's ocean covers the sky, the
shader leaves those pixels out. It lights nothing, and it runs with the raytracing off as
well. The player's *Moon glow* switches it, on unless set.

### The planet below

A ray that misses the scene below the horizon meets the ground beyond it: past the 30 km
of terrain the scene holds, over the ocean, which is not in it, and under the first metre
of terrain a pixel's rays pass through. The reflection probe shows that ground as drawn,
lit by KSP's ambient light: at night a craft over grass got a green light from below,
brighter than the moon's **[meas]** (the player's screenshot). Such a ray therefore meets
the main body's sphere, which sends its albedo times the sun's and the moons' light on
the sphere there, by N.L and unshaded but by the sphere (`RtCommon.hlsli`,
`PlanetRadiance`). The albedo is the body's, as for the moons (Kerbin 0.139, 0.169, 0.168
**[meas]**, KSP.log). By day this is the sunlit ground's light, at night nothing but the
moons' **[meas]** (harness: a box's underside over a planet of albedo 0.3 at 0.302 with
the sun overhead and 0.002 with it under the horizon; KSP at night without moonlight:
the craft at 0.7 of 255, 37 without raytracing). The probe still brings the sky above
the horizon.

Two lights at most go to the proxy each frame, the brightest. Each sample picks one by
its brightness and weighs it back by that chance. Each light costs a shadow ray per
diffuse sample and per bounce hit **[meas]** (harness, 1280x720: all passes 0.140 ms
without and 0.162 ms with two lights, in the same run). PlanetShine's lights are off
while the diffuse light is traced.

## Water

The sea is not in the rays' scene, but with *Full* its reflections are traced. A further
trace of the same frame takes the reflections alone (`RtCamera.TraceOcean`, a frame with
`kRtFrameExtra` and `kRtFrameWater`), in the frame's own command buffer, and uses the
scene the frame built (`Raytracing::ReuseSceneLocked`) **[src]**. Which sea it takes
depends on who draws it:

| Sea | What the trace takes | Where its reflections go |
|---|---|---|
| KSP's own, where no Scatterer ocean is on the body | Unity's G-buffer; a pixel counts as sea within 2 m of the body's sea level, facing up, and on no surface of the scene, such as a vessel's deck (`IsStockWater`) | the reflections shader, with water's Fresnel term, or without a G-buffer the forward light's relight pass; undenoised, as glints are |
| Scatterer's ocean | its surface, copied from its own G-buffer into the shape of Unity's | the texture the ocean reads Deferred's screen-space reflections from (`ssrOutput`); a ray that meets nothing has weight 0, so the ocean keeps its own reflection of Scatterer's sky |

KSP's sea gets ReDefinition's waves and their roughness (`StockWaves`). Scatterer's
ocean keeps its waves and gets ripples that carry the variance its glint spreads the sun
over (`ScattererRipples`) **[src]**. The waves are laid out in metres from a point on the
sea fixed to the body by latitude and longitude, so they stay on the water as the camera
moves (`SeaFrameAnchor`, `MakeSeaFrame`). That point is set anew under the camera once it
is 50 km away **[src]**. A reflection that points below the horizon is turned back over
it, as water reflects nothing under its own surface **[src]**.

The trace adds the sun's and the moons' discs as the water mirrors them, by its roughness
(`BodiesMirroredAt`) **[src]**. The sun comes at the player's *Sun reflection*, where
100 % is three times the sun's own brightness (`OwnSettings.SunReflectionAt100`). A
moon's disc in the water is brightened by the night's darkness, up to 150 times at full
night (`NightAdaptation.MoonBoost`), times the player's *Moon reflection*. The real moon
outshines the night sea by orders of magnitude, so the few per cent water reflects still
show its path. At 1000 times, the path's middle came to 3 to 17 times white, and the
screen clipped it to a band 1.6 to 2 times as wide as the waves give it **[meas]** (KSP,
at sea, against `tools/sea_reflection_check.py`). Under EVE's cloud layer the discs are
darkened (see "EVE's clouds").

The sea's own highlight would then shine twice. `SeaHighlights` turns KSP's sea shader's
highlight (`_SpecColor`) off while its reflections are traced, and puts it back otherwise
**[src]**. Scatterer's glint is set to the player's *Sun reflection* and is off while the
ocean takes the raytraced reflections **[src]**. Scatterer's ocean weighs the reflections
it reads down from 1 km of the camera's height and to nothing at 2 km; there the discs go
onto the ocean after it is drawn, through its tonemapper (`fragOceanDiscs`) **[src]**.

Where Scatterer's ocean code is not the one this was checked against
(`ForeignAssumptions.ScattererOceanReflections`), its ocean keeps its own reflections,
and Deferred's screen-space reflections stay on for it **[src]**.

## EVE's clouds

Where EVE draws a volumetric cloud layer over the main body, the rays take it in as a
shell around the body (`RtEveClouds`; `CloudTransmittance`, `CloudOverSky` in
`RtCommon.hlsli`) **[src]**. EVE's coverage, type and density maps are packed into one
texture of ReDefinition's, 4096 by 2048, again every few seconds and when they change.
EVE's own transform to the map and its fades come every frame. At 2048 texels across the
layer's fine structure averaged out, and its shadow came out 0.62 where EVE's own is
0.76 **[meas]** (KSP, at sea).

Under the layer, the rays darken by its transmittance what EVE's own shadow does not
reach: the sun's light at the rays' hits, the moons' light, and the sun's and the moons'
discs on the water. The pixel's own sun visibility takes EVE's cloud shadow as before,
multiplied into the mask after the rays **[src]**. A ray that escapes the scene sees the
cloud over the sky, lit by the sun from above and by the sky, where the sky does not come
from the reflection probe, which holds EVE's clouds as drawn already **[src]**.

ReDefinition reads EVE's members by reflection, from Blackrack's builds of EVE for KSP
1.12 with the raymarched volumetric layer. Another build turns this off with one warning
in the log **[src]**. `-redefinitionProbeNoRtClouds` leaves the clouds out of the rays for
a comparison.

## Denoising

Each signal is traced with few rays per pixel and is noisy. A denoiser turns it into a
smooth one:

| Denoiser | Runs on | Needs | Takes |
|---|---|---|---|
| ReDefinition's own, after SVGF: accumulation over frames, a variance estimate, an à-trous filter | every GPU with raytracing | nothing | every signal separately |
| AMD FSR Ray Regeneration 1.2 | Radeon RX 9000 and newer, Windows 11 **[doc]** | `amd_fidelityfx_denoiser_dx12.dll`, which the package ships next to `KSP_x64.exe` | the diffuse light and the reflections; the sun's visibility stays with ReDefinition's |
| NVIDIA DLSS Ray Reconstruction | GeForce RTX **[doc]** | the player's `sl.dlss_d.dll` and `nvngx_dlssd.dll`, which *NVIDIA DLSS files* in the settings window downloads from NVIDIA's Streamline release | the whole frame with its noise, in place of DLSS Super Resolution **[doc]** |

ReDefinition's denoiser keeps the sun's visibility shorter than the other signals: at most
4 frames of history and none of the à-trous steps (`rtSunAtrousSteps`, `rtSunHistory` in
`ReDefinitionProxy.ini`). The rays draw the shadows the blades of grass cast. With 32
frames and 5 steps, as for the diffuse light, the lawn at the launch pad lost them: its
structure against a 9x9 mean was 5.1 there, 6.3 with 4 frames and no steps, and 5.5
without the raytracing. Its pixel noise was 4.2 and 4.9 against 4.3 without the
raytracing **[meas]** (KSP, RTX A4500). Either change alone kept the lawn smooth: 5.0 to
5.2 with 0 to 5 steps at 32 frames, and with 2 to 8 frames at 5 steps **[meas]**. The
signal before the denoiser has them in full, 10.2 **[meas]** (`rtSunUnfiltered=1`).

ReDefinition's denoiser finds a pixel's history where the camera's two matrices put it.
Under Krakensbane it tries two: first the shift of the camera and the vessels near it
(`prevWorldToClip`), then the larger shift of the bodies and what stands on them
(`prevWorldToClipFar`) **[src]** (`Denoise.hlsl`, `RtCamera.FillPacket`). The first whose
history matches the pixel's depth and normal is taken. An origin shift moves the terrain
by more than the camera, and only the second finds its surface again.

The denoiser keeps textures only for the signals it filters: per signal 64 bytes a pixel
(the noisy trace, two histories in full floats, two moments and a work texture in half
floats), and for any 40 more (a work texture and two frames' view distances and normals)
**[src]** (`RaytracingDenoise.cpp`). A signal no longer filtered lets its textures go. When
the raytracing stops, `RtCamera` sends render event 33, and the proxy lets go of
everything it makes per frame size (`Raytracing::OnRelease`): the denoiser's textures, Ray
Reconstruction's guides, AMD's context and its guides, the shared copies of Unity's
textures and the meshes the mod released **[src]**. The device and the meshes still held
stay. The harness checks both.

ReDefinition gives Ray Reconstruction its own Streamline viewport, apart from frame
generation's. It runs where Streamline is set up: where DLSS frame generation runs, and,
with `frameGeneration=0` in `ReDefinitionProxy.ini`, wherever the GPU runs Ray
Reconstruction, on a GeForce RTX 20 or 30 among them. Streamline needs its swapchain for
that too, "you must ensure the common plugin's presentCommon() function is called every
frame" **[doc]** (ProgrammingGuideManualHooking.md 2.0), and FSR's frame generation needs
its own, so with frame generation on and no DLSS frame generation FSR's swapchain is made
**[src]**. The raytracing harness passes both Ray Reconstruction checks that way
**[meas]** (Streamline without frame generation, RTX 4090).

With DLSS Ray Reconstruction the signals go into the lighting undenoised. Ray
Reconstruction then denoises and upscales the finished frame in one step, where DLSS Super
Resolution would otherwise run. It gets the albedo, specular albedo, normal, roughness and
the reflections' hit distance as guides.

The guides come from the raytraced frame of the same index, from the surface its passes
see: the G-buffer's with Deferred, the rays' own without. Ray Reconstruction therefore
needs no G-buffer **[meas]** (harness: 64 of 64 frames reconstructed without one, off the
reference by 0.0059 against 0.0053 with it). The sky's guides are NVIDIA's defaults:
albedo 0.5, specular albedo, normal and roughness 0, hit distance 65 504 **[doc]** (DLSS-RR
Integration Guide 3.4.1 to 3.4.4, 3.4.9). The upscaler sends a frame to Ray
Reconstruction when DLSS is chosen, the scene camera is raytraced and renders HDR, and the
proxy reports Ray Reconstruction ready; "DLSS-RR only support HDR inputs" **[doc]**
(ProgrammingGuideDLSS_RR.md), so an LDR camera, as in KSP's editors, keeps Super
Resolution. The first frame after switching goes through Super Resolution, as its
raytraced frame made no guides yet, and the first frame after Ray Reconstruction gave up
starts Super Resolution's history afresh. Where Ray Reconstruction shows no frame for a
second, the upscaler stays with Super Resolution and writes why into the log. In the game
it runs beside DLSS frame generation, 4.3 ms to 4.5 ms a frame on an RTX 4090 **[meas]**
([performance-findings.md](performance-findings.md)).

The proxy loads AMD's DLL from next to `KSP_x64.exe`, else from the proxy's
`amdUpscalerDirectory`, and asks it for a context at the render size. Where it gets one,
AMD's denoiser takes the diffuse light and the reflections, with guides the proxy makes
from the frame's surfaces as AMD's documentation asks for them. The sun's visibility
stays with ReDefinition's denoiser: its output keeps the view depth the next frame finds
its pixels by. Where AMD's DLL gives no
context, or no DLL is there, ReDefinition's denoiser takes everything, and the log says
why once **[meas]** (RTX 4090: the DLL, 1.2.0.2740, loads and gives no context, code 4;
every harness check passes, with the DLL and without). A switch between the two denoisers
starts the history of the one that takes over afresh. The depth change in the motion
guide comes from the camera's two matrices, as for ReDefinition's own denoiser: for what
moves by itself, a vessel in flight, it is off by its motion. On a Radeon RX 9000 it is
**[open]**.

NVIDIA's NRD denoisers are left out: their licence forbids use that makes them subject to
an open-source licence **[doc]**.

## Testing without the game

Most of it is tested without starting KSP. What is left for the game is how it plays with
the installed mods live.

| Level | What it runs | What it proves |
|---|---|---|
| Raytracing harness | a native program that loads the proxy as Unity does, rasterises a G-buffer in Unity's layout and runs the raytracing through the proxy's own interface | the acceleration structures, every signal against a formula or a reference, the denoisers, floating-origin shifts, timings on the GPU |
| Scenes from KSP | meshes exported from KSP's own files with UnityPy: the launch pad, the runway, the VAB, the SPH, part meshes | real geometry, real triangle counts, real scale |
| C# unit tests | the layout of the frame packet and the constants of `RtContract.h`; the moons' light (`RtReflectorLight`) | that the managed side and the proxy read the same packet and constants; Lambert's sphere and the brightness the rays give it |
| Unity test player | a Unity 2019.4.18f1 player built like KSP's, with forward rendering, Unity's deferred lighting and Deferred's own shader bundle from `GameData`, and the proxy beside it (`tools/build_rt_player.py`) | the shaders in Unity's lighting, the timing of the render events, the one-frame carry-over |

The harness checks, in the order it runs them (`src/DxgiProxy/RaytracingHarness.cpp`):

| Area | Checks |
|---|---|
| The scene and its shadows | *primary rays*: each camera ray lands where the depth says, within 0.1 %; *sun shadows*: a box on a plane, the sun at a known angle, against the shadow computed on the CPU, apart from a one-pixel edge; *instances from the GPU*: the boxes in a texture as Parallax's scatter comes, beside an empty run, the same shadows; *floating origin*: the scene and the camera 14 km away, the same signals; *soft shadows*: a sun of a larger angular size gives a penumbra |
| Light | *white furnace*: a white plane under a uniform sky of radiance L converges to L within 2 %; *ambient occlusion*: an inner corner sees less sky than the open plane; *mirror*: a smooth plane reflects a glowing tower where its reflection is, and the sky elsewhere; *environment*: a smooth plane reflects a sky that is its own direction |
| KSP's scene | the launch pad's meshes from KSP's files: *primary rays*, and *no NaN or infinity* in any signal after 33 frames |
| ReDefinition's denoiser | *denoised diffuse light* and *denoised sun visibility*: a camera glide against a reference of 1024 frames; *on a smooth surface*: the diffuse light keeps its history as on rough ground; *a denoised signal paused and back*: no history of an earlier light; *the sun's fine shadows kept*: thin posts' shadows keep their structure with `rtSunAtrousSteps=0` and `rtSunHistory=4`, against 5 and 32 set live through the ini; *the denoiser's textures for its signals*: video memory with three signals, with the sun's alone, and none after render event 33; *the denoiser after raytracing stopped*: its textures made anew, the sun as before |
| Ray Reconstruction | *DLSS Ray Reconstruction*, with the G-buffer's guides and *without a G-buffer*: 64 noisy frames reconstructed against a reference of 256; only where NVIDIA's DLLs are there |
| The main body and terrain | *the planet's shadow*: the body's sphere shades a box the scene leaves lit; *displaced terrain*: the rays' ground 0.3 m above the drawn one stays lit as terrain and shades itself as another mesh; *the ground below*: a box's underside gets the planet's albedo with the sun overhead, nothing with it under the horizon; *vertex colours*: a ground's colour plus its vertex colours, as Parallax's terrain, in its light |
| Surfaces | *cut-out surfaces*: a card covered, half covered and cut out in thirds gives shadow, half shadow and sun, among the CPU's and the GPU's instances, and sun under all of it with its texture not read yet; *the surface from the rays*: without a G-buffer, the same sun visibility and diffuse light; *surface intent*: four intents written as their materials mean them, and *reflections read it* |
| Forward light | *forward light* and *in gamma space*: each drawn pixel relit by its diffuse light less a gradient ambient light, through the denoiser; *forward light's reflections*, linear and in gamma space: Unity's Fresnel term and surface reduction |
| The moons | *moonlight*: a body's light as the only light, N.L on the open ground and nothing in the box's shadow, the main body's sphere in the way from below; *a moon in the mirror*: its disc at its surface's brightness; *a moon behind the main body*: hidden |
| Water | *the moon on water* and *the sun on water*: a disc mirrored round on calm water, a path longer than wide on rough water, none with the sun's mirroring off; *water over the horizon*: a reflection that points down meets the sky, not the ground; *KSP's own sea*: the sea gets the waves, a deck at the same level none; *the waves stay on the water*: the same waves from a camera moved sideways; *the waves on Scatterer's ocean* |
| Further traces | *a further trace's own random numbers*; *a further trace while the next frames are recorded*: its reflections are those of its own frame's scene; *a further trace after a mesh is built*: the new mesh's shadow there, and not in the frame's own |
| Robustness | *churn*: meshes added and removed every frame, 20 000 instances; *a frame that stops early*: the meshes it built are there for the next; *a material's texture past the array*: refused |
| Timings | each pass on the GPU, without and with two moonlights, and *timings past further traces* |

## Measuring keys

`ReDefinitionProxy.ini` has keys for measuring the raytracing. The proxy reads them again
while the game runs (`src/DxgiProxy/Config.h`) **[src]**:

| Key | Default | What it does |
|---|---|---|
| `rtCutoutTest` | 0 | 1 decides the cut-out surfaces' hits without reading their textures, 2 takes every surface as opaque; the picture is wrong with either |
| `rtSunUnfiltered` | 0 | 1 hands the sun's visibility on as its pass traced it, one ray a pixel, past the denoiser |
| `rtSunAtrousSteps` | 0 | how many of the denoiser's five à-trous steps filter the sun's visibility |
| `rtSunHistory` | 4 | the longest history of the sun's visibility, in frames, 1 to 32 |
| `rtFoliageTransmission` | 0 | what of the sun's light a cut-out surface lets through to what lies behind it, 0 to 1; above 0 the grass loses its own shading |

`rtOwnQueue` puts the raytracing and Ray Reconstruction on a Direct3D 12 queue of their
own. The proxy reads it when KSP creates its swapchain **[src]**.

## The GPU budget

The budget on the RTX 4090, 2560 by 1440 with DLSS Quality, is everything together within
4 ms of GPU time. The test player's budget run renders 1707 by 960, as DLSS Quality does
at that size, with 400 m of ground in 131 072 triangles, 404 instances and 25 096 from
the GPU, every signal, two bounces and the denoiser: 1.92 ms **[meas]**. The denoiser
takes 1.38 ms of it, the scene's build 0.17 ms. The run renders without the vertical sync,
as the game keeps the GPU busy: held to the display's refresh, the GPU idles most of each
frame, its driver lowers the clocks, and the same frame measures 4.2 to 4.3 ms
**[meas]**. The denoiser's à-trous steps filter all signals in one pass each, the depth
and normal of a tap weighed once for all, and the steps after the first in half floats.
The camera takes its textures' native handles once, as they are made: Unity's
GetNativeTexturePtr waits for the render thread.

In the game the scene is larger. At the launch pad on the RTX 4090, at 3440 by 1440 with
DLSS *AA only*, the raytracing takes 6.05 ms **[meas]**
([performance-findings.md](performance-findings.md)). The log's line every 30 seconds
gives the timings of any run.

Whether Scatterer's and EVE's shadows sit over the raytraced ones in the game as they
sit over Unity's is **[open]**.

## Installed mods

| Mod | What raytracing does with it |
|---|---|
| Deferred | optional; provides the G-buffer; its reflections shader is replaced while diffuse light is raytraced, and set back afterwards **[src]**; its screen-space reflections are off on the scene camera while reflections are raytraced, and set back afterwards, unless Scatterer's ocean is of a version not checked **[src]**; the light colour setting acts in its reflections pass; without it, see "Without Deferred" |
| Parallax Continued | its finer terrain copy near the camera is in the scene, the quad under it left out **[src]**; its displacement is passed through for the first metre of a pixel's rays **[meas]**; its scatter comes from its GPU buffers **[src]** **[meas]** (test player: a box only in such a buffer casts its shadow) |
| Scatterer | its ocean and atmosphere stay out of the scene; its ocean takes the raytraced reflections where it reads Deferred's screen-space reflections, and its glint follows *Sun reflection* (see "Water") **[src]**; with it the sky's light keeps its colour **[src]**; its long-distance shadows shader gives way while raytracing runs and comes back after **[src]**; its eclipse and extinction change the sun's colour only while the local cameras draw, and the rays take the colour then **[src]**; its ocean caustics multiply into the sun's mask after the raytraced visibility **[src]** |
| EVE, Stock Volumetric Clouds | clouds stay out of the scene; cloud and eclipse shadows multiply into the sun's mask after the raytraced visibility **[src]**; EVE's volumetric layer darkens the rays' light under it and shows to rays that escape the scene (see "EVE's clouds") **[src]** |
| Waterfall, Firefly | out of the scene: every material of theirs is transparent **[src]**; Firefly draws its effects after the raytraced light **[src]** |
| TUFX | its post-processing runs on the scene camera after the lit frame **[src]** |
| Kopernicus | the scene's star is its current star (`KopernicusStar.Current`), not `Sun.Instance`, which its star clones set **[src]**; the other stars' lights keep their shadow maps; its ShadowMan's shadows shader is handled as Scatterer's **[src]** |
| PlanetShine | its lights are turned off while the raytraced diffuse light brings the planet's and the moons' light (see "Light sources"), and set back as they were afterwards **[src]** |
| DistantObject | its flares are transparent and stay out of the scene **[src]** |
| FreeIva, ThroughTheEyes | the IVA and the first-person kerbal are drawn by the internal camera, on layers the scene camera does not draw: out of the scene, with Unity's lighting **[src]** |
| JSI (RasterPropMonitor), VesselView | RasterPropMonitor's cameras copy the scene camera without its command buffers **[src]** **[doc]** and keep Unity's lighting; VesselView draws without a camera **[src]** |
| KSPCommunityFixes, Shabby, KSPTextureLoader, B9PartSwitch | part meshes stay readable **[src]**; Shabby replaces nothing without its configurations **[src]**; KSPTextureLoader's textures are ordinary ones **[src]**; B9PartSwitch switches by turning objects on and off and gives renderers materials of their own, which the scene follows each frame **[src]** |
| ReDefinition's upscaler and frame generation | raytracing runs at the render resolution, before the upscaler; with DLSS Ray Reconstruction, Ray Reconstruction takes DLSS's place |
