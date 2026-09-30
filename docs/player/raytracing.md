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

Both rows are under *Display* in the settings window, below the upscaler's.

| Setting | Does |
|---|---|
| Raytracing | *Off*; *Shadows*: the sun's shadows from rays, as far as the scene reaches; *Shadows and light*: also the light bouncing off surfaces and the sky, which darkens corners and colours what stands beside something bright; *Full*: also reflections |
| Light bounces | how often the traced light bounces between surfaces, from 1 to 4. More bounces light shadowed sides and rooms more fully, at more GPU time. Only with *Shadows and light* or *Full* |

A profile never switches raytracing on: it needs a GPU with raytracing, and it costs what
you decide to spend.

## What changes on screen

* The sun's shadows reach as far as the scene the rays see, soft by the sun's size.
  Unity's shadow maps end a few kilometres out.
* A planet's side away from the sun is never darker than KSP's own night light.
* The planet shadows what flies over its night side.
* With *Shadows and light*, the sky and lit surfaces light what the sun does not reach.
* With *Full*, surfaces reflect the scene by how smooth they are.

The rays see vessels, the ground within 30 km, the space centre's buildings and
Parallax's rocks and trees. They do not see anything transparent: plumes, clouds, the
atmosphere, the ocean, particles. Those keep KSP's shadows where they receive them, and
clouds still shadow the ground.

## With other mods

| Mod | With raytracing |
|---|---|
| Deferred | with *Shadows and light* or *Full*, raytracing takes over its ambient light and reflections, and gives them back when you switch it off |
| Parallax Continued | its rocks and trees near the camera cast raytraced shadows; those it does not draw, behind you further out, cast none |
| PlanetShine | its lights are off while *Shadows and light* or *Full* runs, as the traced light brings the planet's own; back as they were when you switch raytracing off |
| Scatterer, EVE | their shadows, eclipses and cloud shadows stay |
| Kopernicus | the star lighting the scene is the one raytraced; other stars keep KSP's shadows |

## With DLSS

When DLSS is the upscaler and DLSS frame generation runs, DLSS Ray Reconstruction takes
DLSS's place. It removes the noise of the traced light and upscales in one step. It runs
through the same NVIDIA Streamline as frame generation, so on RTX 40 and newer. The
download beside *NVIDIA DLSS files* brings its two files with the frame generation files
([installing.md](installing.md)). Without frame generation or those files,
ReDefinition's own denoiser removes the noise, and DLSS upscales as usual.

## When something looks wrong

KSP's log (`KSP.log`) gets a line every 30 seconds starting with `[ReDefinition]
Raytracing:`. It says what the rays see, how long each part took on the GPU, and any
problem. Add that line to a bug report.

If raytracing does not start, the log says why once: no proxy, a GPU without raytracing,
or a shader bundle older than the mod.
