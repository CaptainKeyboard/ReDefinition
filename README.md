# ReDefinition

ReDefinition is an attempt to bring the mods of Kerbal Space Program 1 together in one
concept: their settings unified in one window, profiles that set them up together, and
rules that keep their settings compatible with each other. Furthermore, ReDefinition adds
upscaling with FSR and DLSS, frame generation and raytracing. They work on the whole
scene, other mods' effects included.

Mods for KSP are installed one by one and set up one by one, each in its own window, and
whether their settings work together is left to chance. ReDefinition starts with the major
graphics mods; any mod can join with a config file.

> **Early release, 0.1.3.** What works and what is open:
> [docs/project/status.md](docs/project/status.md). This page describes the repository as
> it stands; what 0.1.3 does not contain yet is listed under *Unreleased* in
> [CHANGELOG.md](CHANGELOG.md).

## Settings and profiles

* **One window for all settings.** Every setting of KSP, its axes and keys included, and
  the settings of the supported mods, sorted by what they do. It can take the place of
  KSP's own settings screen.
* **Profiles from Low to Max.** One choice sets up all installed mods together; High is
  the mod authors' own defaults.
* **Compatible settings.** What a mod requires of other settings is kept in every
  profile, and other mods' antialiasing is switched off while the upscaler runs.
* **Reset and restore.** Back to the defaults, or back to each mod's settings from before
  ReDefinition.
* **Open to other mods.** A mod or visual pack joins with a config file in its own folder.

## Upscaling and frame generation

* **Upscaling and antialiasing** with AMD FSR 3, NVIDIA DLSS, AMD's FSR DLL and Intel
  XeSS, fed with the game's own depth and motion vectors.
* **Frame generation** with AMD FSR 3.1, NVIDIA DLSS (on RTX 30 and RTX 20 through a
  community package) or Intel XeSS. You choose *Off*, *2x* to *6x* or *Dynamic* in the
  game.
* **Low latency.** NVIDIA Reflex, Intel XeLL, AMD Anti-Lag 2, and a brake that cuts the
  latency of raytraced frames on any GPU.

## Raytracing

* **Shadows, ambient occlusion, light with bounces and reflections** traced through the
  scene, with Deferred or without it. The sea reflects the sun and the moons.
* **Noise removal** with DLSS Ray Reconstruction or, on a Radeon RX 9000, AMD FSR Ray
  Regeneration.
* **Moonlight and night clouds.** The moons light the scene and the clouds as a second sun.
* Raytracing is off in every profile and needs a GPU with DirectX Raytracing 1.1:
  [docs/player/raytracing.md](docs/player/raytracing.md).

## Flight HUD

* **Flight readouts.** Vertical and horizontal speed beside the navball, time to impact,
  a round gauge and an altimeter that switches to the ground.
* **Flight panel profiles** that follow the situation (Ascent, Orbit, Rendezvous, Landing,
  Docking, Air), with an editor for the figures.
* **Navball.** A docking alignment marker, and a navball you can drag sideways.

## Mission planner and autopilot

* **Route planner.** Lay out a route such as Kerbin → Mun → Minmus → Kerbin in the VAB or
  the SPH, see whether the stages carry enough fuel for every leg, and pin stages to legs.
* **Navigation and autopilot.** In flight the autopilot flies the route's steps with
  MechJeb, can fly the route in the background, predicts landings and re-entries, and
  shows its guidance on the navball.
* **A mod of its own.** The planner is released as ReDefinition Planner and works with or
  without ReDefinition: [docs/player/planner-on-its-own.md](docs/player/planner-on-its-own.md).

## Web dashboard

* **Browser, phone or Corsair iCUE touch screen** with pages for flight, the main menu,
  the space centre and the editors, served on port 8765. A QR code in the settings window
  opens it on a phone; access from the network is a switch.
* Key names and the bracket syntax of its commands follow Telemachus' API.
* **A mod of its own.** The dashboard is released as ReDefinition Dashboard and works with or
  without ReDefinition: [docs/player/dashboard-on-its-own.md](docs/player/dashboard-on-its-own.md).

## Seasons

* **Seasons on Kerbin.** ReDefinition Seasons is a mod of its own with the clock and the snow
  mask for a snow line that moves with the year; the rendering is not in it yet:
  [docs/player/seasons.md](docs/player/seasons.md).

## Staging, editor and interface

* **Staging and editor.** Stage details in two columns, the delta-v tools inside the
  staging stack, buttons in the world that launch from the VAB or the SPH and take the
  vessel back, buttons over the space centre's buildings, and a grip choice for landing
  legs.
* **Readable on large screens.** The UI scale also for what KSP leaves at a fixed size,
  and for other mods' windows.
* **Main menu and space centre.** Fireworks on Kerbin's night side behind the main menu,
  one camera flight from the menu down to the space centre, and Parallax's grass on the
  space centre's lawns.
* **Diagnostics.** A window with live frame times, latency and GPU load, a search and a
  list of what changed.

## Supported mods

Scatterer, EVE Redux with its volumetric clouds, Parallax Continued, Deferred, Firefly,
Waterfall, Distant Object Enhancement, TUFX and Persistent Rotation, each of them
optional. How ReDefinition works with further graphics mods:
[docs/reference/graphics-mod-compatibility.md](docs/reference/graphics-mod-compatibility.md).

## Requirements

* Kerbal Space Program 1.12 on Windows, built and tested on 1.12.5
* HarmonyKSP (on CKAN: *Harmony2*)

DLSS needs an NVIDIA RTX GPU, frame generation a GPU with Direct3D 12, raytracing a GPU
with DirectX Raytracing 1.1. ReDefinition downloads NVIDIA's DLSS files and Intel's XeSS
files itself once their licences are accepted in its settings window. AMD's upscaler DLL
and the package for DLSS frame generation on RTX 30 and RTX 20 you bring yourself. What
each technique needs, by GPU:
[docs/player/upscaler-and-frame-generation.md](docs/player/upscaler-and-frame-generation.md),
[docs/player/low-latency.md](docs/player/low-latency.md) and
[docs/player/raytracing.md](docs/player/raytracing.md).

## Installation

### With CKAN

Once ReDefinition's CKAN entry is merged, search for it there and install it. CKAN
installs HarmonyKSP and ReDefinition Common with it, and recommends ReDefinition Planner
and ReDefinition Dashboard.
CKAN does not replace a `dxgi.dll` it did not install, such as ReShade's: move that one
away from `KSP_x64.exe` first.

### Manually

1. Install HarmonyKSP.
2. Extract the release zip into the KSP folder, the one with `KSP_x64.exe`.
   `GameData/ReDefinition` goes into `GameData`; `dxgi.dll`, AMD's frame generation
   runtime and AMD's denoiser go next to `KSP_x64.exe`.

The zip holds `GameData/ReDefinition/Main`, `Common`, `Planner`, `Dashboard` and `Seasons`. The
planner, the route planner and the autopilot, needs neither Harmony nor ReDefinition and is
released as its own zip too: [docs/player/planner-on-its-own.md](docs/player/planner-on-its-own.md).
So is the dashboard: [docs/player/dashboard-on-its-own.md](docs/player/dashboard-on-its-own.md).

Then, in the game, open ReDefinition from the toolbar and choose a profile. Details, and
how to remove ReDefinition again: [docs/player/installing.md](docs/player/installing.md).

## Choosing a profile

| Profile | What it sets |
|---|---|
| Low | every visual mod on, at its lowest useful level |
| Medium | clouds, ocean and scatter a step below the mods' defaults |
| High | every mod as its authors ship it |
| Ultra | Scatterer's High preset, finer clouds, tessellation and scatter |
| Max | Scatterer's Very High preset and the finest detail everywhere |

Start with High and take the tier below it if the frame rate does not hold: which tier
a graphics card holds has not been measured. What each profile sets:
[docs/player/graphics-profiles.md](docs/player/graphics-profiles.md).

## Known limitations

* Another `dxgi.dll` next to `KSP_x64.exe`, such as ReShade's, cannot be used together
  with ReDefinition's.
* KerbalVR does not work together with ReDefinition's upscaler.
* Anti-Lag 2, XeLL, XeSS frame generation with more than one generated frame and FSR Ray
  Regeneration have not been run on Radeon and Intel Arc hardware yet.
* Ghosting can show behind engine plumes, and trajectory lines can look thin. The open
  questions: [docs/project/status.md](docs/project/status.md).

## Reporting a problem

Open an issue. The form asks for what is needed: your GPU, what was switched on, your
mods, and the two logs, `KSP.log` from the KSP folder and `ReDefinitionProxy.log` from
next to `KSP_x64.exe`. *Write diagnostics to log*, in the diagnostics window, adds the
upscaler's current state to `KSP.log`.

## For mod authors

A mod or visual pack registers its settings, defaults and profile values with a config
file in its own folder: [docs/modders/registering-a-mod.md](docs/modders/registering-a-mod.md).

A mod can also use what ReDefinition works out once for every mod: the jitter, history
resets, its own motion vectors, the upscaled image and overlays. The Direct3D 12 device
the frames are presented through can run a mod's compute passes too. It works with or
without depending on ReDefinition, and there is a shader include, a wrapper file and an
example mod to start from:
[docs/modders/shared-foundation.md](docs/modders/shared-foundation.md).

## Building

```
dotnet build ReDefinition.sln
dotnet test tests/ReDefinition.Tests
```

The shaders, the proxy and the release package:
[docs/development/building-and-testing.md](docs/development/building-and-testing.md).
All documentation: [docs/README.md](docs/README.md).

## Credits and licence

**GPL-3.0 WITH Modding Exception AND GPL-3.0 Linking Exception.** See
[LICENSE](LICENSE) and [EXCEPTIONS.md](EXCEPTIONS.md). The text of the exceptions is the
one Skyrim Community Shaders and DynamicShaderFrameGen carry.

The ten works ReDefinition owes the most to:

1. **NVIDIA:** DLSS, Streamline, Reflex.
2. **AMD:** FidelityFX with FSR 3, frame generation and Ray Regeneration, Anti-Lag 2.
3. **Intel:** XeSS and XeLL.
4. **FSR3Unity** by Nico de Poel: FSR 3 inside the game.
5. **MechJeb 2:** the landing simulation, and the autopilot the planner flies with.
6. **Deferred** by Ghassen Lahmar (LGhassen): the reflections shader of the raytraced camera.
7. **The graphics mods:** Scatterer, EVE Redux, Parallax Continued, TUFX, Waterfall,
   Firefly, Distant Object Enhancement and Kopernicus.
8. **HarmonyKSP** and Andreas Pardeike's Harmony.
9. **dlssg_for_sm86** by sdli1995: DLSS frame generation on RTX 30 and RTX 20.
10. **Telemachus Reborn** and Project Nayuki's QR Code generator: the dashboard.

Everyone whose work ReDefinition builds on or talks to, with the licence of that work:
[docs/credits-and-licences.md](docs/credits-and-licences.md).
