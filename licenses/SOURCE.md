# Source and notices

ReDefinition's own code is GPL-3.0 (version 3 only) with the Modding Exception and
the GPL-3.0 Linking Exception (`LICENSE`, `EXCEPTIONS.md`; the exception text is
DynamicShaderFrameGen's, from Community Shaders). It is "only" because files adapted
from MechJeb 2 (the planner's landing code) and from Deferred (the reflections shader)
are GPL-3.0 without "or later", as the licences of those mods say.

Files adapted from other works keep those works' licences, as the list below gives them
per work: MIT (FSR3Unity, AMD, NVIDIA, Intel, Unity's built-in shaders, Telemachus,
Nayuki), BSD-3-Clause (Bruneton), the Unity Companion License (the SRP macros in
`ffx_fsr_unity_common.cginc`), and AMD's licence for AMD's two runtime DLLs.

**The source.** The complete source of this build -- the commit it was built
from -- is distributed next to this package as `ReDefinition_<version>_source.zip`,
with the same version number as the package. Pass it on together with the
package.

**Parts by others, and their notices:**

* FSR3Unity, by Nico de Poel, adapted in ReDefinition for Unity 2019.4 -- MIT,
  `LICENSE-FSR3Unity.txt`. Compiled into `Plugins/ReDefinition.dll`, and its shader
  wrappers into `Shaders/redefinition.shaders`.
* AMD FidelityFX FSR 3 shaders, by Advanced Micro Devices -- MIT, under two
  notices, both in `LICENSE-FidelityFX.txt`. Compiled into
  `Shaders/redefinition.shaders`.
* Precomputed Atmospheric Scattering, by Eric Bruneton (INRIA), modified and ported to Unity by
  Justin Hawkins, as Scatterer carries it -- BSD-3-Clause, `LICENSE-Bruneton.txt`. Its optical
  depth, adapted in the moonlight on the clouds, compiled into `Shaders/redefinition.shaders`.
* AMD FidelityFX SDK API headers, by Advanced Micro Devices -- MIT,
  `dxgi_LICENSE-FidelityFX.txt` next to `KSP_x64.exe`. Compiled into `dxgi.dll`.
* NVIDIA Streamline SDK 2.14.16 headers, and the NVAPI function ids and structures
  the proxy declares from NVIDIA's `nvapi.h` and `nvapi_interface.h`, by NVIDIA -- MIT,
  `dxgi_LICENSE-NVIDIA.txt` next to `KSP_x64.exe`. Compiled into `dxgi.dll`.
* AMD Anti-Lag 2 SDK headers, by Advanced Micro Devices -- MIT,
  `dxgi_LICENSE-AntiLag2.txt` next to `KSP_x64.exe`. Compiled into `dxgi.dll`.
* Intel XeSS and XeLL headers, by Intel Corporation -- MIT,
  `dxgi_LICENSE-XeSS.txt` next to `KSP_x64.exe`. Compiled into `dxgi.dll`. The XeSS
  frame generation entry points are declared by hand from Intel's headers. Intel's
  libraries themselves are not in this package.
* AMD Capsaicin's hash-grid radiance cache, by Advanced Micro Devices, adapted in
  ReDefinition's radiance cache -- MIT, `dxgi_LICENSE-Capsaicin.txt` next to
  `KSP_x64.exe`. Compiled into `dxgi.dll`.
* Unity built-in shader source (`Internal-ScreenSpaceShadows.shader`,
  `Internal-DeferredReflections.shader`), Copyright (c) 2016 Unity Technologies --
  MIT, `LICENSE-Unity-shaders.txt`. Changed and compiled into `Shaders/redefinition.shaders`.
* Unity Graphics' Scriptable Render Pipeline core package, by Unity Technologies --
  Unity Companion License, `LICENSE-Unity-Companion.txt`. The `TextureXR.hlsl` macros adapted in
  `ffx_fsr_unity_common.cginc`, compiled into `Shaders/redefinition.shaders`.
* Deferred's reflections shader, by Ghassen Lahmar -- GPL-3.0 (Deferred's licence).
  Changed for ReDefinition and compiled into `Shaders/redefinition.shaders`.
* Telemachus, by TeleIO -- MIT, `LICENSE-Telemachus.txt`. The dashboard follows its API.
* Kerbal Alarm Clock, by TriggerAu (David Tregoning) -- MIT, `LICENSE-KerbalAlarmClock.txt`. The atmospheres'
  alarm bridge adapts the pattern of its API wrapper; compiled into `ReDefinition.Atmospheres.dll`.
* Project Nayuki's QR Code generator -- MIT, `LICENSE-QR-Code-generator.txt`.
  `src/Dashboard/QrCode.cs` is translated from its Java implementation.
* AMD FidelityFX frame generation runtime, `amd_fidelityfx_framegeneration_dx12.dll`,
  by Advanced Micro Devices -- AMD's licence, `amd_fidelityfx_framegeneration_dx12_LICENSE.md`
  next to it.
* AMD FSR Ray Regeneration, `amd_fidelityfx_denoiser_dx12.dll`, by Advanced Micro
  Devices -- AMD's licence, `amd_fidelityfx_denoiser_dx12_LICENSE.md` next to it.

Everyone else this project builds on, talks to or learnt from is named in
`CREDITS.md`.
