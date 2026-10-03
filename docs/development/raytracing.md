# Raytracing

**For:** anyone building or changing ReDefinition's raytracing.
**You need:** `src/DxgiProxy` to hand, and a GPU with DirectX Raytracing tier 1.1 for
the tests.
**You get:** how raytraced lighting reaches KSP's frame, what each part does, how it is
tested without the game, and the plan it is built by.

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
particles, clouds, the atmosphere and Scatterer's ocean. They take the raytraced shadows
where they receive Unity's screen-space shadow, and are not in the scene the rays see.

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
light (*Night light*, 30 % unless set). KSP's night light is its flat ambient colour
(`RenderSettings.ambientLight`), which Deferred hands its shader as `legacyAmbientColor`
(`RefreshLegacyAmbient`) **[src]**. ReDefinition's reflections shader mixes the raytraced
light with that colour times the share, as Deferred mixes its probe's light. At 100 % a
planet's night side keeps the brightness KSP gives it **[meas]** (test player: the ground
at 0.323 with raytracing and without). Lower, the night is darker and the moonlight's
shadows show **[meas]** (KSP, at the launch pad at night with the Mun up, moonlight 150 %:
the craft at 17, 23 and 41 of 255 with 0, 30 and 100 %, 41 without raytracing; the far
grass at 20 and 31, 29 without).

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
* A pass of the proxy works out the light to add: the albedo times the traced diffuse
  light, less Unity's ambient light, which forward shading added already; and the
  reflections, weighed by Unity's Fresnel term. The traced diffuse light has the
  occlusion in it, and is kept above the player's share of KSP's flat ambient colour, as
  with Deferred.
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
near box's shadow changes by 0.185 without raytracing and by 0.001 with it, with forward
rendering and with Unity's deferred lighting **[meas]**.

Forward shading keeps its own reflections. KSP's part shaders have none **[src]**; Unity's
standard shaders add their reflection probe's, and the raytraced reflections come on top
**[open]** (whether that shows on anything in KSP's scenes).

## What the rays see

The scene is a set of meshes, each with a transform per frame. The C# side collects them,
the proxy builds the acceleration structures: one per mesh, and one over all of them each
frame.

| Source | Taken from | Changes |
|---|---|---|
| Vessel parts | `MeshFilter.sharedMesh`, readable as KSP loads them **[src]** | skinned meshes baked when their pose changes |
| KSC buildings | the renderers under each `PQSCity` | readability checked at run time; unreadable meshes copied on the GPU **[open]** |
| Terrain | each visible leaf `PQ`'s mesh within 30 km, from the quad tree (`PQS.quads`, `PQ.subNodes`) **[src]** | handed over again when the quad moves or its edges change **[src]**; Parallax's finer copy near the camera in place of the quad, whose material discards it (`Custom/DiscardAll`) **[src]**; the copy, rebuilt in place, handed over again when its shape changes, at most every ten frames |
| Parallax scatter | the append buffers Parallax fills on the GPU each frame, one per scatter and level of detail, with their meshes and materials **[src]** | gathered into a texture on Unity's side, turned into instances on the proxy's, as their count is only known on the GPU; each buffer gets the places it took lately, read back a frame or two late, and a quarter more, not its capacity **[meas]** (test player: 25 096 places for 20 000 rocks in a buffer of 100 000) |
| The main body | a sphere, tested by formula in the sun's rays, so that its night side and what flies over it lie in its shadow | none |

Left out: anything transparent or additive (Waterfall, Firefly, particles, translucent
parts), EVE's clouds, Scatterer's ocean and atmosphere, and the UI.

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
* specular colour and smoothness, converted from KSP's shaders as Deferred converts them
  **[src]**;
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

A ray that hits nothing takes the sky's light from a capture of the scene's reflection
probe, Deferred's or KSP's own in flight (`FlightReflectionProbe`) **[src]**, and the sun's
disc for sharp reflections. Without a probe it takes KSP's ambient sky colour.

## Light sources

Every light the rays carry is traced with a shadow ray and bounces off what it lights:

| Source | Where it comes from | Shadow |
|---|---|---|
| The sun | the scene's sunlight, its colour and the size of its disc | a ray towards the disc, and the main body's sphere |
| Moons and the planet | each a disc of its angular size, lit by the sun (see below) | a ray towards the disc, as far as the body's surface |
| The sky | rays that hit nothing take the reflection probe's capture, or KSP's ambient sky colour | the scene, as occlusion |
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

### Moons and the planet as lights

The moons of the planet the camera is at, and that planet, light the scene as the sun
does. Each is a disc of its angular size, and its light reaches only where the disc is in
view with nothing between. The Mun lights the launch pad only while it stands over it.
Bodies of other planets are left out: their light is far below what the screen shows.

Physically, a sphere that scatters by Lambert's law sends, at phase angle a,
`E = E_sun * albedo * 2/3 * phase(a) * (R/d)^2`, with
`phase(a) = (sin a + (pi - a) cos a) / pi` (Lambert's sphere). The full Mun over Kerbin
sends some 6e-5 of the sunlight **[src]** (`RtReflectorLight`, tests). That is too faint
to see on a screen without the eye's adaptation. The raytracing therefore takes the
square root of that share times ten, eased towards 0.3 of the sunlight. This keeps the
bodies' order: Minmus is fainter than the Mun, and a crescent fainter than the full disc.
It brings the full Mun to some 0.06 of the sunlight. The player's *Moonlight* percentage
scales the result.

A body's albedo and colour are the linear average of its scaled-space texture, rendered
down to 16 by 16 pixels: Parallax's `_ColorMap`, otherwise `_MainTex` **[src]**. Parallax
leaves `_MainTex` white and loads `_ColorMap` only when the body comes into view
(`ScaledOnDemandComponent`) **[src]**. A body is grey until its texture is there, and is
read again every ten seconds **[meas]** (KSP.log: the Mun 0.176 grey, Minmus 0.358, 0.501,
0.384). The light's colour is the star's own, Kopernicus's `LightShifter.sunlightColor`
or white. The scene's sunlight is no guide: Scatterer tints it by the atmosphere, and at
night it is dark **[meas]** (moonlight orange by day, none at night, before the change).

The planet itself is a light only where nothing else brings its light:

* With a reflection probe, the rays that miss already see the planet in it.
* Below the terrain's 30 km, the rays hit the ground as terrain.

Its own sphere does not shade it: its shadow ray ends at the surface, short of the
sphere test.

Two lights at most go to the proxy each frame, the brightest. Each sample picks one by
its brightness and weighs it back by that chance. Each light costs a shadow ray per
diffuse sample and per bounce hit **[meas]** (harness, 1280x720: all passes 0.140 ms
without and 0.162 ms with two lights, in the same run). PlanetShine's lights are off
while the diffuse light is traced.

## Denoising

Each signal is traced with few rays per pixel and is noisy. A denoiser turns it into a
smooth one:

| Denoiser | Runs on | Needs | Takes |
|---|---|---|---|
| ReDefinition's own, after SVGF: accumulation over frames, a variance estimate, an à-trous filter | every GPU with raytracing | nothing | every signal separately |
| AMD FSR Ray Regeneration 1.2 | Radeon RX 9000 and newer, Windows 11 **[doc]** | `amd_fidelityfx_denoiser_dx12.dll`, which the package ships next to `KSP_x64.exe` | the diffuse light and the reflections; the sun's visibility stays with ReDefinition's |
| NVIDIA DLSS Ray Reconstruction | GeForce RTX **[doc]** | the player's `sl.dlss_d.dll` and `nvngx_dlssd.dll` | the whole frame with its noise, in place of DLSS Super Resolution **[doc]** |

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
second, the upscaler stays with Super Resolution and writes why into the log. Whether it
runs in the game is **[open]**.

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
| C# unit tests | the layout of the frame packet; the moons' light (`RtReflectorLight`) | that the managed side and the proxy read the same packet; Lambert's sphere and the brightness the rays give it |
| Unity test player | a Unity 2019.4.18f1 player built like KSP's, with forward rendering, Unity's deferred lighting and Deferred's own shader bundle from `GameData`, and the proxy beside it (`tools/build_rt_player.py`) | the shaders in Unity's lighting, the timing of the render events, the one-frame carry-over |

The harness checks:

1. **Shadows.** A box on a plane, the sun at a known angle: the shadowed pixels match the
   shadow computed on the CPU, apart from a one-pixel edge.
2. **White furnace.** A plane under a uniform sky of radiance L: the diffuse light
   converges to L, within 2 %.
3. **Occlusion.** A point in an inner corner sees less sky than one on the open plane.
4. **Mirror.** A plane of smoothness 1 reflects a box in the box's colour.
5. **Denoising.** A camera path: the denoised image against a reference accumulated over
   1024 frames, by mean squared error, and its flicker between frames.
6. **Floating origin.** The whole scene and the camera shifted by 10 km between two
   frames: the signals do not change.
7. **Churn.** Meshes added and removed every frame, 20 000 instances.
8. **Timings.** Each pass, measured on the GPU, per render resolution.
9. **The planet's shadow.** A sun under the horizon: the main body's sphere shadows a box
   the scene alone leaves lit.
10. **Surface from the rays.** Without a G-buffer: the sun's visibility and the diffuse
    light as with one.
11. **Forward light.** Without a G-buffer, under an ambient light brighter above than
    below: every drawn pixel gets its albedo times the diffuse light less that ambient
    light, through the denoiser.
12. **Ray Reconstruction.** 64 frames of the noisy signals put together: the reconstructed
    frame against a reference of 256 frames, with the G-buffer's guides and without a
    G-buffer, with the raytraced frame's.
13. **Instances from the GPU.** The boxes not as the CPU's instances but in a texture as
    Parallax's scatter comes, beside an empty run: the same shadows, to the pixel.
14. **Displaced terrain.** The rays' ground 0.3 m above the drawn one: lit as terrain,
    shading itself as any other mesh.
15. **Cut-out surfaces.** A card over the ground, its small texture covered, half covered
    and cut out in thirds, the sun straight above: shadow, half shadow and sun under them,
    among the CPU's instances and the GPU's; with its texture not read yet, sun under all
    of it.
16. **Forward light in gamma space.** As check 11, the colours as gamma-space values: the
    pass takes the linear signals and the albedo back into gamma space.
17. **Moonlight.** A body's light as the only light: the open ground gets its colour
    times N.L, the ground in the box's shadow nothing. The main body's light reaches a
    box's underside past its own sphere, and another body's light from below the horizon
    does not.
18. **Vertex colours.** A ground of a colour plus its vertex colours times a factor, as
    Parallax's terrain: in gamma space its forward light under a white sky is that sum.

## Plan

Each stage ends with its checks passing, a review, and one commit.

| Stage | Builds | Done when |
|---|---|---|
| 1 | The raytracing service in the proxy: raytracing tier check, geometry pool, acceleration structures, meshes, materials and instances through exports, the frame packet, DXIL shaders compiled at build time and embedded; the harness with its analytic scenes | checks 1, 6 and 7 pass |
| 2 | The signals: sun visibility, occlusion, diffuse light with bounces, reflections, emission, the sky capture | checks 2, 3 and 4 pass, and the KSP scenes render without NaNs |
| 3 | ReDefinition's denoiser; the adapters for Ray Regeneration and Ray Reconstruction | check 5 passes; Ray Reconstruction runs in the harness on NVIDIA's DLLs |
| 4 | The Unity side free of KSP: collection, per-frame instances, G-buffer capture, the two shaders, the carry-over; the Unity test player | the test player shows raytraced shadows and light, and its checks pass |
| 5 | KSP: parts, KSC, terrain, floating origin, the sun, cameras, settings, the diagnostics log; raytracing without Deferred | the C# tests pass, a log line says what went into the scene, and the test player's checks without Deferred pass |
| 6 | Compatibility with every installed mod (table below), Parallax's scatter from the GPU | each row has its handling, marked **[src]** |
| 7 | Performance, the player page, one flight's checklist | the timings fit the budget below |

Budget on the RTX 4090, 2560 by 1440 with DLSS Quality: everything together within 4 ms
of GPU time. The test player's budget run renders 1707 by 960, as DLSS Quality does at
that size, with 400 m of ground in 131 072 triangles, 404 instances and 25 096 from the
GPU, every signal, two bounces and the denoiser: 1.92 ms **[meas]**. The denoiser takes
1.38 ms of it, the scene's build 0.17 ms. The run renders without the vertical sync, as
the game keeps the GPU busy: held to the display's refresh, the GPU idles most of each
frame, its driver lowers the clocks, and the same frame measures 4.2 to 4.3 ms
**[meas]**. The denoiser's à-trous steps filter all signals in one pass each, the depth
and normal of a tap weighed once for all, and the steps after the first in half floats.
The camera takes its textures' native handles once, as they are made: Unity's
GetNativeTexturePtr waits for the render thread. In the game the scene is larger; the
log's line every 30 seconds gives its timings **[open]**.

In the game, one flight checks what the test player cannot: the terrain and the parts in
the rays, Scatterer's and EVE's shadows over the raytraced ones, Parallax's scatter, a
night side, and DLSS Ray Reconstruction where DLSS frame generation runs **[open]**.

## Installed mods

| Mod | What raytracing does with it |
|---|---|
| Deferred | optional; provides the G-buffer; its reflections shader is replaced while diffuse light is raytraced, and set back afterwards **[src]**; its screen-space reflections are off on the scene camera while reflections are raytraced, and set back afterwards **[src]**; without it, see "Without Deferred" |
| Parallax Continued | its finer terrain copy near the camera is in the scene, the quad under it left out **[src]**; its displacement is passed through for the first metre of a pixel's rays **[meas]**; its scatter comes from its GPU buffers **[src]** **[meas]** (test player: a box only in such a buffer casts its shadow) |
| Scatterer | its ocean and atmosphere stay out of the scene; its long-distance shadows shader gives way while raytracing runs and comes back after **[src]**; its eclipse and extinction change the sun's colour only while the local cameras draw, and the rays take the colour then **[src]**; its ocean caustics multiply into the sun's mask after the raytraced visibility **[src]** |
| EVE, Stock Volumetric Clouds | clouds stay out of the scene; cloud and eclipse shadows multiply into the sun's mask after the raytraced visibility **[src]** |
| Waterfall, Firefly | out of the scene: every material of theirs is transparent **[src]**; Firefly draws its effects after the raytraced light **[src]** |
| TUFX | its post-processing runs on the scene camera after the lit frame **[src]** |
| Kopernicus | the scene's star is its current star (`KopernicusStar.Current`), not `Sun.Instance`, which its star clones set **[src]**; the other stars' lights keep their shadow maps; its ShadowMan's shadows shader is handled as Scatterer's **[src]** |
| PlanetShine | its lights are turned off while the raytraced diffuse light brings the planet's and the moons' light (see "Light sources"), and set back as they were afterwards **[src]** |
| DistantObject | its flares are transparent and stay out of the scene **[src]** |
| FreeIva, ThroughTheEyes | the IVA and the first-person kerbal are drawn by the internal camera, on layers the scene camera does not draw: out of the scene, with Unity's lighting **[src]** |
| JSI (RasterPropMonitor), VesselView | RasterPropMonitor's cameras copy the scene camera without its command buffers **[src]** **[doc]** and keep Unity's lighting; VesselView draws without a camera **[src]** |
| KSPCommunityFixes, Shabby, KSPTextureLoader, B9PartSwitch | part meshes stay readable **[src]**; Shabby replaces nothing without its configurations **[src]**; KSPTextureLoader's textures are ordinary ones **[src]**; B9PartSwitch switches by turning objects on and off and gives renderers materials of their own, which the scene follows each frame **[src]** |
| ReDefinition's upscaler and frame generation | raytracing runs at the render resolution, before the upscaler; with DLSS Ray Reconstruction, Ray Reconstruction takes DLSS's place |
