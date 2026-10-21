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

The rows are under *Display* in the settings window, below the upscaler's.

| Setting | Does |
|---|---|
| Raytracing | *Off*; *Shadows*: the sun's shadows from rays, as far as the scene reaches; *Shadows and light*: also the light bouncing off surfaces and the sky, which darkens corners and colours what stands beside something bright; *Full*: also reflections |
| Light bounces | how often the traced light bounces between surfaces, from 1 to 4. More bounces light shadowed sides and rooms more fully, at more GPU time. Only with *Shadows and light* or *Full* |
| Ray Reconstruction | on at first. With DLSS as the upscaler, NVIDIA's DLSS Ray Reconstruction removes the noise of the traced light and upscales in one step ([below](#with-dlss)). Off, ReDefinition's own denoiser and DLSS Super Resolution do it. Only with DLSS and raytracing on |
| Ray Reconstruction from | the least raytracing mode that uses it: *Shadows*, *Light* or *Full*. *Shadows* is the factory value, Ray Reconstruction in every mode. Below the chosen mode ReDefinition's own denoiser and DLSS Super Resolution do the work. On an RTX A4500 at DLSS *Quality* Ray Reconstruction takes 8.7 to 9.0 ms of the GPU in every mode; with *Full* in this setting the frames a second were 58.5 against 42.0 in *Shadows* and 48.9 against 38.5 in *Light*, with a different look |
| Moonlight | how brightly the moons light the scene, from 0 to 300 %, in a cool colour, as the eye sees moonlight at night. Their light falls only where the moon is in the sky, with shadows. Only with *Shadows and light* or *Full* |
| Moon reflection | how strongly water mirrors the moons, from 0 to 500 %. 100 % is the moon as bright in the water as the eye sees it at night. It lights nothing. Only with *Full* |
| Moon glow | the moons' discs a little brighter in the night sky, with a soft glow round them, the more the darker the sky. It lights nothing, and works without raytracing too |
| Sun reflection | how strongly water mirrors the sun, from 0 to 300 %: the raytraced sun on the sea with *Full*, otherwise Scatterer's own glint. 100 % is Scatterer's |
| Night light | how much of KSP's own night light stays, from 0 to 100 %: the least light anything gets where neither the sun nor a moon reaches it. Lower makes the night darker, and the moonlight's shadows show more clearly. Only with *Shadows and light* or *Full* |
| Light colour | the colour of the light from the sky and the surroundings; its strength is the rays' either way. *Raytraced*: the sky's own, blue by day, orange at dusk. *As without raytracing*: the colour of the ambient light the game has without it. *Neutral*, the default: grey, so that grass and ground in shadow keep their own colour. Only with *Shadows and light* or *Full*, and only with Deferred, in whose lighting the colour is set |

A profile never switches raytracing on: it needs a GPU with raytracing, and it costs what
you decide to spend.

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

*Ray Reconstruction from* sets the lowest raytracing mode that uses it.

The download beside *NVIDIA DLSS files* brings its files
([installing.md](installing.md)). Without them, ReDefinition's own denoiser removes the
noise, and DLSS upscales as usual.

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
| `rtMegaGeometry=1` | builds new meshes' structures from RTX Mega Geometry's clusters of up to 256 triangles. Works on every RTX GPU | not in the benchmark. In the raytracing harness the trace took 2.54 ms against 1.24 ms with 15 003 instances, and a vessel's meshes took no less to build |

`rtBlasBatch`, `rtBlasPool`, `rtBlasBudget` and `rtBlasCompaction` set how the meshes of
a vessel are built when they appear. With the defaults, the frame that builds the meshes
of a vessel of 150 parts took 0.63 ms of GPU time, where building mesh by mesh took 21.6
ms (the raytracing harness on the RTX A4500).
