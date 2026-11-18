#pragma once

#include <d3d11.h>

#include <cstdint>
#include <string>

// For test runs: which Direct3D 11 resource a GPU address belonged to (command line switch -redefinitionGpuAddressBook). K-T2 2026-11-18: a GPU page
// fault, a write to an address the crash dump finds no resource at, with DLSS and the own clouds only; Aftermath's own resource tracking is off on the
// test machine. Every buffer and texture made on Unity's device is written down as it is made: its GPU address range (NVAPI's
// D3D11_GetResourceGPUVirtualAddressEx), its description, the time, and the module that asked for it (Unity, DLSS, the driver), by patching the
// device's Create* entries. A crash dump's fault address is looked up in it (GpuCrashDump::OnDump). Off without the switch; nothing is patched then.
namespace redefinition
{
    namespace GpuAddressBook
    {
        // Unity's device, once it exists: its Create* entries are patched when the switch is given.
        void Attach(ID3D11Device* device);

        // The resources whose range held the address, newest first, and the nearest ones below and above it, as log lines.
        void Explain(uint64_t address);
    }
}
