# Source and notices

ReDefinition is free software: GPL-3.0-or-later with the Modding Exception and
the GPL-3.0 Linking Exception (`LICENSE`, `EXCEPTIONS.md`).

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
* AMD FidelityFX SDK API headers, by Advanced Micro Devices -- MIT,
  `dxgi_LICENSE-FidelityFX.txt` next to `KSP_x64.exe`. Compiled into `dxgi.dll`.
* NVIDIA Streamline SDK and NVAPI headers, by NVIDIA -- MIT,
  `dxgi_LICENSE-NVIDIA.txt` next to `KSP_x64.exe`. Compiled into `dxgi.dll`.
* AMD FidelityFX frame generation runtime, `amd_fidelityfx_framegeneration_dx12.dll`,
  by Advanced Micro Devices -- AMD's licence, `amd_fidelityfx_framegeneration_dx12_LICENSE.md`
  next to it.

Everyone else this project builds on, talks to or learnt from is named in
`CREDITS.md`.
