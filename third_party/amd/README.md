# AMD FidelityFX runtimes

The player package ships `amd_fidelityfx_framegeneration_dx12.dll` next to
`KSP_x64.exe`, where the proxy (`dxgi.dll`) loads it for frame generation, and
`amd_fidelityfx_denoiser_dx12.dll`, AMD FSR Ray Regeneration, which it loads for the
raytracing. Each is AMD's signed binary from the FidelityFX SDK release below,
redistributed under AMD's licence, which the package reproduces next to it
(`licenses/AMD-FidelityFX-SDK-license.md`, the release's own
`Kits/FidelityFX/docs/license.md`): "in binary form only", with AMD's
copyright notice, the permission notice and the disclaimers, and no reverse
engineering, decompilation or disassembly.

The DLLs are not kept in this repository. `python tools/fetch_amd_runtime.py`
puts them here and checks each against the SHA-256 in its `.sha256` file; the
package build checks the same pins and refuses without a match.

| | |
|---|---|
| Release | FSR SDK v2.3.0 of GPUOpen-LibrariesAndSDKs/FidelityFX-SDK, 2026-06-24 |
| File | `Kits/FidelityFX/signedbin/amd_fidelityfx_framegeneration_dx12.dll` |
| Version | AMD FidelityFX Frame Generation 4.0.1, file version 4.0.1.2740 |
| Signature | valid, Advanced Micro Devices, Inc. |
| Size | 40 085 776 bytes |

| | |
|---|---|
| Release | the same |
| File | `Kits/FidelityFX/signedbin/amd_fidelityfx_denoiser_dx12.dll` |
| Version | AMD FidelityFX (DirectX) Denoiser Library, file version 1.2.0.2740 |
| Signature | valid, Advanced Micro Devices, Inc. |
| Size | 13 782 280 bytes |

The release's frame generation header says 4.0.1 and its denoiser header 1.2.0, the
versions the proxy is built against (`src/DxgiProxy/extern`).

To move to a newer release: update the headers in `src/DxgiProxy/extern`, the tag
in `tools/fetch_amd_runtime.py`, the pin and this table; rebuild the proxy; run
`tools/audit_ffx_fields.py` and the proxy harness.
