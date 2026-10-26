# Raytracing

The managed side of ReDefinition's raytracing: it collects what the rays see, hands it to the
proxy, and sets up the light that comes back into Unity's lighting.

## Start here

| File | Read it for |
|---|---|
| `src/Raytracing/Ksp/RtKsp.cs` | `RaytracingMode`, `RtKsp.Apply` and `RtKsp.Update`: the raytracing in KSP. |
| `src/Raytracing/RtCamera.cs` | The raytracing of one camera: the packets, the passes, the frame. |
| `src/Raytracing/RtScene.cs` | `IRtSource`, `RtItem`, and how meshes and materials reach the proxy. |
| `src/Raytracing/Ksp/RtKspSources.cs` | What the rays see: vessels, terrain, the space centre, Parallax's scatter. |
| `src/Raytracing/RtLighting.cs` | The two lighting shaders that take Unity's place while it runs. |
| `src/Raytracing/RtMaterialIntent.cs` | How a material's shine is meant, for the surfaces the rays write back. |
| `src/Raytracing/Ksp/RtReflectors.cs` | The sun-lit planet and moons as the rays' lights. |

## What it does

- `RtKsp` owns the `RtCamera` on the scene camera and feeds it the sun, the sky, the floating
  origin's shifts and the upscaler's jitter.
- `RtCamera` fills its `RtScene` each frame from the sources and issues the proxy's render events
  through `RtNative` (`src/Proxy/RtNative.cs`).
- `RtScene` hands each mesh and material over once and the instances every frame.
- `RtMaterial`, `RtMaterialIntent`, `RtDeferredLook` and `RtForwardHighlight` work out what a ray
  that hits a surface takes: albedo, shine and the highlight the forward light keeps.
- `RtStockTerrain` and `RtTerrainColour` work out the ground's colour as KSP's and Parallax's
  terrain shaders give it.
- `Ksp/RtKspMods.cs` reaches into the installed mods (Deferred, Scatterer, PlanetShine, Kopernicus,
  Parallax), each only where it is there.
- `Ksp/RtReflectors.cs`, `RtReflectorLight`, `CloudLightBlend` and `Ksp/NightAdaptation.cs` make
  the moons' light; `Ksp/EveCloudMoonlight.cs` and `Ksp/EveCloudRelight.cs` put it on EVE's clouds.
- `Ksp/RtEveClouds.cs`, `Ksp/SeaFrameAnchor.cs`, `Ksp/SeaHighlights.cs`, `Ksp/MoonGlow.cs` and
  `Ksp/EditorHallShine.cs` cover clouds, sea and moons in the sky, and the editors' halls.

The area is a stop on
[Raytracing from scene to screen](../../docs/development/flow-raytracing.md) and on
[A frame in the mod](../../docs/development/flow-frame.md).

## What it uses and what uses it

It uses Api, Core, Modules, Probe, Proxy, Scenery and Upscaler.
The add-on in the root, Diagnostics, Modules, Probe, Scenery, Settings and Upscaler name it.

## Rules of the area

- `RtKsp` needs no other mod. A mod that is not installed leaves its part out
  (`src/Raytracing/Ksp/RtKspMods.cs`).
- `RtLighting` keeps what it replaced and sets it again when raytracing goes
  (`RtLighting.Keep`, `RtLighting.Restore`, called from `src/Raytracing/RtCamera.cs`).
- A camera that raytracing failed on is not tried again until the setting changes, because each
  try leaves a packet ring behind (`RtKsp.Apply` clears `failedOn`).
- When the proxy's device changes, `RtScene` hands everything over again.
- The flags, signals and packet layouts are the contract in
  `src/DxgiProxy/Raytracing/Shaders/RtContract.h`. `tests/ReDefinition.Tests/RtContractTests.cs`
  holds `src/Proxy/RtNative.cs` to it.
- `src/Raytracing/CloudLightBlend.cs` and `src/Raytracing/RtReflectorLight.cs` stay free of Unity
  and KSP, so the tests can run them.
- A surface's shine goes through `RtMaterialIntent` and `RtNative`'s `Surface` constants, so that
  the managed and the native side agree on the intent.
