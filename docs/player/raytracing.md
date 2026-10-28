# Raytracing

**For:** players who want KSP's shadows and light traced through the scene.
**You need:** ReDefinition installed with the proxy ([installing.md](installing.md)), a
graphics profile chosen, and a GPU with DirectX Raytracing 1.1: NVIDIA RTX 20 or newer,
AMD Radeon RX 6000 or newer, or Intel Arc.
**You get:** what each setting does, what changes on screen, what it costs, and where it
falls short.

Raytracing runs on the proxy's Direct3D 12 device beside KSP. It needs no other mod. With
Deferred installed it uses Deferred's lighting; without it, it adds its light to KSP's
own.

## The settings

*Raytracing* and *Ray Reconstruction* are under *Display* in the settings window, below the
upscaler's. The other rows are under *Detail*, in the *Raytracing* group.

| Setting | Does |
|---|---|
| Raytracing | *Off*; *Shadows*: the sun's shadows from rays, as far as the scene reaches; *Shadows and light*: also the light bouncing off surfaces and the sky, which darkens corners and colours what stands beside something bright; *Full*: also reflections |
| Ray Reconstruction | *Auto* at first, or *Off* or *On*. With DLSS as the upscaler, NVIDIA's DLSS Ray Reconstruction removes the noise of the traced light and upscales in one step ([below](#with-dlss)). *Auto* turns it on with an RTX 40 or 50 in every mode, and with an RTX 20 or 30 in *Full* only. *On* runs it in every mode. Otherwise ReDefinition's own denoiser and DLSS Super Resolution do the work. Only with DLSS and raytracing on |
| Moonlight | how brightly the moons light the scene, from 0 to 300 %, in a cool colour, as the eye sees moonlight at night. Their light falls only where the moon is in the sky, with shadows. Only with *Shadows and light* or *Full* |
| Moon reflection | how strongly water mirrors the moons, from 0 to 500 %. 100 % is the moon as bright in the water as the eye sees it at night. It lights nothing. Only with *Full* |
| Moon glow | the moons' discs a little brighter in the night sky, with a soft glow round them, the more the darker the sky. It lights nothing, and works without raytracing too |
| Night clouds | how brightly the moons light the clouds at night, from 0 to 300 %, on the volumetric and the flat layers of every planet with clouds. The scale is that of *Moonlight*; at 70 %, the default, the clouds stand to the ground as they do in the sun. It works without raytracing too, and needs Scatterer and EVE |
| Sun reflection | how strongly water mirrors the sun, from 0 to 300 %: the raytraced sun on the sea with *Full*, otherwise Scatterer's own glint. 100 % is Scatterer's |
| Night light | how much of KSP's own night light stays, from 0 to 100 %: the least light anything gets where neither the sun nor a moon reaches it. Lower makes the night darker, and the moonlight's shadows show more clearly. Only with *Shadows and light* or *Full* |

The profiles set the mode: Low and Medium leave raytracing *Off*, High sets *Shadows*, Ultra
*Shadows and light* and Max *Full*. They set two light bounces, which no row changes. More
bounces light shadowed sides more fully, at more GPU time. On a GPU without raytracing,
nothing runs, whatever the profile says.

## What changes on screen

* The sun's shadows reach as far as the scene the rays see, soft by the sun's size.
  Unity's shadow maps end a few kilometres out.
* A planet's side away from the sun keeps as much of KSP's own night light as *Night
  light* says.
* At night the moons light the ground and your craft where they are in the sky, with
  shadows. *Moonlight* sets how brightly.
* The planet shadows what flies over its night side.
* With *Shadows and light*, the sky and lit surfaces light what the sun does not reach.
* With *Full*, surfaces reflect the scene by how smooth they are. The sea mirrors the
  sun, the moons and what stands over it: KSP's own sea, and with Deferred installed
  Scatterer's ocean as well.
* Each surface shines as its material is meant to. The space centre's grey ground and
  the halls of the VAB and the SPH stay matt, and the lawns keep their own sheen.
* At night the moons light the clouds as they light the ground, from the ground to orbit, with
  or without raytracing. *Night clouds* sets how brightly. The auroras of SPVE shine by
  themselves and stay as they are.
* With EVE's volumetric clouds, the clouds' shadows darken the sun's and the moons'
  light where the rays meet the ground, and rays that leave the scene see the cloud
  layer.

The rays see vessels, the ground within 30 km, the space centre's buildings, launch
sites and flags, and Parallax's rocks, trees and grass. They do not see what is
transparent as a surface: plumes, the atmosphere, particles. Those keep KSP's shadows
where they receive them.

## With other mods

| Mod | With raytracing |
|---|---|
| Deferred | with *Shadows and light* or *Full*, raytracing takes over its ambient light and reflections, and gives them back when you switch it off |
| Parallax Continued | its rocks and trees near the camera cast raytraced shadows; those it does not draw, behind you further out, cast none |
| PlanetShine | its lights are off while *Shadows and light* or *Full* runs, as the traced light brings the planet's own; back as they were when you switch raytracing off |
| Scatterer, EVE | their shadows, eclipses and cloud shadows stay |
| Kopernicus | the star lighting the scene is the one raytraced; other stars keep KSP's shadows |

## With DLSS

When DLSS is the upscaler and *Ray Reconstruction* is on, DLSS Ray Reconstruction takes
DLSS's place. It removes the noise of the traced light and upscales in one step. It takes
only a scene rendered in HDR: KSP's editors render without it, and there DLSS upscales as
usual and ReDefinition's own denoiser removes the noise.

It runs beside every frame generation. With DLSS frame generation, or with frame
generation off, it runs through NVIDIA Streamline. Beside FSR 3's or XeSS's frame
generation it runs through NVIDIA's NGX directly. `rrPath` in `ReDefinitionProxy.ini`
next to `KSP_x64.exe` chooses the way
([reference/proxy-settings.md](../reference/proxy-settings.md#loading-and-the-techniques)).

On an RTX A4500 at DLSS *Quality*, Ray Reconstruction takes about 9 ms of the GPU in every
mode. ReDefinition's own denoiser and DLSS Super Resolution take 2.2 ms with *Shadows*,
4.2 ms with *Shadows and light* and 5.2 ms with *Full*.

Choosing DLSS in the *Upscaler* row offers to download its files
([installing.md](installing.md#get-the-files-for-dlss-xess-and-amds-upscaler)). Without
them, ReDefinition's own denoiser removes the noise, and DLSS upscales as usual.

## With a Radeon RX 9000

On a Radeon RX 9000 or newer, AMD FSR Ray Regeneration removes the noise of the traced
light and its reflections. Its DLL comes with ReDefinition, next to `KSP_x64.exe`. On
other GPUs, ReDefinition's own denoiser does.

## When something looks wrong

KSP's log (`KSP.log`) gets a line every 30 seconds starting with `[ReDefinition]
Raytracing:`. It says what the rays see, how long each part took on the GPU, and any
problem. Add that line to a bug report.

If raytracing does not start, the log says why once: no proxy, a GPU without raytracing,
or a shader bundle older than the mod.

## Faster and finer, on NVIDIA GPUs

These settings of `ReDefinitionProxy.ini` are off at first, and each is described in
[reference/proxy-settings.md](../reference/proxy-settings.md#raytracing). Measured on an
RTX A4500 at the Medium profile with raytracing on *Full* and FSR 3, from the benchmark in
[development/benchmarks.md](../development/benchmarks.md):

| Setting | Does | Measured |
|---|---|---|
| `rtOpacityMicromaps=1` | lets the GPU decide most hits on grass and ferns without testing their textures. In hardware on RTX 40 and newer; an RTX 30 emulates it | 4 to 10 % fewer frames a second on the RTX A4500, an Ampere GPU that emulates them: 40.5 against 45.0 in the standing comparison, 43.1 and 42.3 against 44.8 in a run of its own. Taking every cut-out test away gives 7.6 % more frames a second there, 48.2 against 44.8, which is the most micromaps could save. The gain on an RTX 40 is open |
| `rtRadianceCache=1` | keeps the indirect light in a grid over the world, so that fewer rays are traced a frame. The light follows a change some frames late | 45.4 against 45.0 frames a second, 1 % more |
| `rtMegaGeometry=1` | builds new meshes' structures from RTX Mega Geometry's clusters of up to 256 triangles. Works on every RTX GPU | 36.9 against 46.0 frames a second, 20 % fewer: the build is cheaper, and the rays take longer through the clusters |

`rtBlasBatch`, `rtBlasPool`, `rtBlasBudget` and `rtBlasCompaction` set how the meshes of
a vessel are built when they appear. With the defaults, the frame that builds the meshes
of a vessel of 150 parts took 0.63 ms of GPU time, where building mesh by mesh took 21.6
ms (the raytracing harness on the RTX A4500).
