# AMD FidelityFX frame generation runtime

The player package ships `amd_fidelityfx_framegeneration_dx12.dll` next to
`KSP_x64.exe`, where the proxy (`dxgi.dll`) loads it for frame generation. It
is AMD's signed binary from the FidelityFX SDK release below,
redistributed under AMD's licence, which the package reproduces next to it
(`licenses/AMD-FidelityFX-SDK-license.md`, the release's own
`Kits/FidelityFX/docs/license.md`): "in binary form only", with AMD's
copyright notice, the permission notice and the disclaimers, and no reverse
engineering, decompilation or disassembly.

The DLL is not kept in this repository. `python tools/fetch_amd_runtime.py`
puts it here and checks it against the SHA-256 in
`amd_fidelityfx_framegeneration_dx12.dll.sha256`; the package build checks the
same pin and refuses without a match.

| | |
|---|---|
| Release | FSR SDK v2.3.0 of GPUOpen-LibrariesAndSDKs/FidelityFX-SDK, 2026-06-24 |
| File | `Kits/FidelityFX/signedbin/amd_fidelityfx_framegeneration_dx12.dll` |
| Version | AMD FidelityFX Frame Generation 4.0.1, file version 4.0.1.2740 |
| Signature | valid, Advanced Micro Devices, Inc. |
| Size | 40 085 776 bytes |

The release's frame generation header says 4.0.1, the version the proxy is built
against (`src/native/extern`).

To move to a newer release: update the headers in `src/native/extern`, the tag
in `tools/fetch_amd_runtime.py`, the pin and this table; rebuild the proxy; run
`tools/audit_ffx_fields.py` and the proxy harness.
