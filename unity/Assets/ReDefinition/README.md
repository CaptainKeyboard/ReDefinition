# ReDefinition (Unity project)

The Unity 2019.4.18f1 project that compiles the mod's shaders into the asset bundle
`redefinition.shaders`. It also checks the include file and the motion vectors the shaders compute.

## Start here

| File | Read it for |
|---|---|
| `unity/Assets/ReDefinition/Editor/BundleBuilder.cs` | Building the bundle, in the editor and in batch mode |
| `unity/Assets/ReDefinition/Shaders` | Every shader and compute shader the mod loads by name |
| `unity/Assets/ReDefinition/Include/ReDefinition.cginc` | The include file for other mods' shaders |
| `unity/Assets/ReDefinition/Editor/IncludeCheck.cs` | Compiling every function of that include |
| `unity/Assets/ReDefinition/Editor/MotionVectorCheck.cs` | Comparing computed motion vectors with Unity's in a player |

## What it does

- `Shaders` holds ReDefinition's own shaders. They draw the masks for FSR and the motion vectors
  of EVE's clouds, the distant planets and the vessel. They also draw the vessel's shadow for frame
  generation, the raytracing's inputs and its light added to the frame.
- It also holds the shaders of the space centre's lawn and Parallax's grass there, a moon's light
  on EVE's clouds, and the moon's glow.
- `ReDefinitionDeferredReflections.shader` is Deferred's reflections shader changed for the
  raytracing, and `ReDefinitionScreenSpaceShadows.shader` is Unity's built-in one changed the same
  way. Their headers name the source and the licence.
- The `ffx_fsr3upscaler_*.compute` files, `ffx_fsr_unity_common.cginc` and `Shaders/shaders` are
  FSR 3 as FSR3Unity has it, ported to Unity 2019.4 by `tools/port_fsr3_shaders.py`.
- `Include/ReDefinition.cginc` gives a mod the frame state, the jitter and the motion vector
  helpers. `Include/ReDefinitionIncludeCheck.shader` calls each function of it.
- `MotionVectorCheck` holds the player half of the motion vector check. Its four probes cover the
  camera and object case, the far ground, the vessel's motion vector repair and the vessel's shadow.

[How shaders reach the game](../../../docs/development/flow-shaders.md) shows the route of a shader
through this project. Credits for the FSR3Unity port, Deferred and Unity's shader sources:
[Credits and licences](../../../docs/credits-and-licences.md). How to run each check:
[Building and testing](../../../docs/development/building-and-testing.md).

## Rules of the area

- The bundle holds the `.compute` and `.shader` files directly in `Shaders` and no others.
  Includes and headers are not built on their own (`BundleBuilder.Build`).
- A bundle comes from exactly Unity 2019.4.18f1, KSP's version. The player rejects another
  version's bundle without a word. `unity/Packages/manifest.json` has to list
  `com.unity.modules.assetbundle`.
- `BundleBuilder` rebuilds with `ForceRebuildAssetBundle`. An incremental build does not treat a
  change of graphics APIs as a reason to recompile, and keeps a bundle without Direct3D 12 kernels.
- Change the FSR files through `tools/port_fsr3_shaders.py`, not by hand, and rebuild the bundle.
- `ReDefinition.cginc` is not in the bundle. A change to it is checked with
  `IncludeCheck.RunFromCommandLine`, which exits 1 on any shader error or warning.
- A change to the motion vector shaders is checked with `MotionVectorCheck.RunFromCommandLine`,
  which builds a small player. It exits 1 when the computed vectors differ from Unity's by more than
  its limits.
- The bundle goes straight into the mod's `Shaders` folder in the game, as `redefinition.shaders`.
  The repository does not hold it.
