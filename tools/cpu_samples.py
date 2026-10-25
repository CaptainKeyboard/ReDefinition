"""Where the game's main thread spends its time, from the CPU sampler's file
(src/DxgiProxy/CpuSampler.h, -redefinitionProbeCpuSample):

    python tools/cpu_samples.py <KSP>/ReDefinitionCaptures/<time>-run/cpu-samples.txt [report.txt]

Each sample is the main thread's instruction pointer and the values on its stack that
point into a module's code. The addresses are named with dbghelp and the symbol
servers of Unity (UnityPlayer.dll, its PhysX) and Microsoft (Windows, D3D11), cached in
%LOCALAPPDATA%/ReDefinition/symbols. The report: the time by module and by function
where the thread was (self); and by function anywhere on its stack (inclusive, an upper
bound: a stale return address left on the stack counts as well). Code of no module is
Mono's compiled C# -- KSP's and the mods' scripts, which the perf monitor times by
method (-redefinitionPerfMonitorAll).
"""
import ctypes
import os
import sys
from collections import Counter
from ctypes import wintypes

# dbghelp.dll and symsrv.dll, which it loads from its own folder: copied together into
# the cache from where they are found (RenderDoc brings a dbghelp, Visual Studio's
# tools a symsrv).
DBGHELP = [r'C:\Program Files\RenderDoc\dbghelp.dll', r'C:\Windows\System32\dbghelp.dll']
SYMSRV = [r'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\Common7\IDE\Remote Debugger\x64\symsrv.dll',
          r'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\Remote Debugger\x64\symsrv.dll']
CACHE = os.path.join(os.environ.get('LOCALAPPDATA', '.'), 'ReDefinition', 'symbols')
SYMBOL_PATH = ('srv*{0}*https://symbolserver.unity3d.com/;srv*{0}*https://msdl.microsoft.com/download/symbols'
               .format(CACHE))


class SYMBOL_INFOW(ctypes.Structure):
    """dbghelp's SYMBOL_INFOW, with room for a name of 512 characters, which SymFromAddrW fills."""
    _fields_ = [('SizeOfStruct', wintypes.ULONG), ('TypeIndex', wintypes.ULONG), ('Reserved', ctypes.c_uint64 * 2),
                ('Index', wintypes.ULONG), ('Size', wintypes.ULONG), ('ModBase', ctypes.c_uint64),
                ('Flags', wintypes.ULONG), ('Value', ctypes.c_uint64), ('Address', ctypes.c_uint64),
                ('Register', wintypes.ULONG), ('Scope', wintypes.ULONG), ('Tag', wintypes.ULONG),
                ('NameLen', wintypes.ULONG), ('MaxNameLen', wintypes.ULONG), ('Name', wintypes.WCHAR * 512)]


def load_dbghelp():
    """dbghelp.dll, loaded from the cache's copy beside symsrv.dll; exits where either is not found."""
    import shutil
    dbghelp = next((p for p in DBGHELP if os.path.exists(p)), None)
    symsrv = next((p for p in SYMSRV if os.path.exists(p)), None)
    if dbghelp is None or symsrv is None:
        raise SystemExit('need dbghelp.dll (%s) and symsrv.dll (%s)' % (', '.join(DBGHELP), ', '.join(SYMSRV)))
    folder = os.path.join(CACHE, 'dbghelp')
    os.makedirs(folder, exist_ok=True)
    for source in (dbghelp, symsrv):
        target = os.path.join(folder, os.path.basename(source).lower())
        if not os.path.exists(target):
            shutil.copyfile(source, target)
    os.add_dll_directory(folder)
    return ctypes.WinDLL(os.path.join(folder, 'dbghelp.dll'))


def main():
    """Read the samples and modules, name the addresses with dbghelp, and write the report by module and function."""
    source = sys.argv[1]
    out = open(sys.argv[2], 'w', encoding='utf-8') if len(sys.argv) > 2 else sys.stdout
    modules = []
    samples = []
    for line in open(source, encoding='utf-8', errors='replace'):
        if line.startswith('M '):
            _, base, size, path = line.rstrip('\n').split(' ', 3)
            modules.append((int(base, 16), int(size, 16), path))
        elif line.startswith('S'):
            samples.append([int(a, 16) for a in line.split()[1:]])
    modules.sort()

    def module_of(address):
        for base, size, path in modules:
            if base <= address < base + size:
                return base, path
        return None, None

    os.makedirs(CACHE, exist_ok=True)
    dbghelp = load_dbghelp()
    dbghelp.SymSetOptions(0x00000002 | 0x00000004 | 0x00000200)  # UNDNAME, DEFERRED_LOADS, FAIL_CRITICAL_ERRORS
    process = wintypes.HANDLE(0x5EED)
    dbghelp.SymInitializeW.argtypes = [wintypes.HANDLE, wintypes.LPCWSTR, wintypes.BOOL]
    assert dbghelp.SymInitializeW(process, SYMBOL_PATH, False), 'SymInitialize failed'
    dbghelp.SymLoadModuleExW.argtypes = [wintypes.HANDLE, wintypes.HANDLE, wintypes.LPCWSTR, wintypes.LPCWSTR,
                                         ctypes.c_uint64, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]
    dbghelp.SymLoadModuleExW.restype = ctypes.c_uint64
    dbghelp.SymFromAddrW.argtypes = [wintypes.HANDLE, ctypes.c_uint64, ctypes.POINTER(ctypes.c_uint64),
                                     ctypes.POINTER(SYMBOL_INFOW)]
    used = set()
    for sample in samples:
        for address in sample:
            base, _ = module_of(address)
            if base is not None:
                used.add(base)
    for base, size, path in modules:
        if base in used:
            dbghelp.SymLoadModuleExW(process, None, path, None, base, size, None, 0)

    names = {}

    def name_of(address):
        if address in names:
            return names[address]
        base, path = module_of(address)
        if base is None:
            names[address] = '(compiled C#, no module)'
            return names[address]
        module = os.path.basename(path)
        info = SYMBOL_INFOW()
        info.SizeOfStruct = ctypes.sizeof(SYMBOL_INFOW) - ctypes.sizeof(wintypes.WCHAR * 512) + ctypes.sizeof(wintypes.WCHAR)
        info.MaxNameLen = 511
        displacement = ctypes.c_uint64(0)
        if dbghelp.SymFromAddrW(process, address, ctypes.byref(displacement), ctypes.byref(info)):
            names[address] = module + '!' + info.Name
        else:
            names[address] = module + '!+%x' % (address - base)
        return names[address]

    total = len(samples)
    by_module = Counter()
    by_self = Counter()
    by_inclusive = Counter()
    for sample in samples:
        leaf = name_of(sample[0])
        by_self[leaf] += 1
        by_module[leaf.split('!')[0]] += 1
        seen = set()
        for address in sample:
            seen.add(name_of(address))
        for name in seen:
            by_inclusive[name] += 1

    def table(title, counter, count):
        out.write('\n== %s\n' % title)
        for name, n in counter.most_common(count):
            out.write('%6.1f %%  %s\n' % (100.0 * n / total, name))

    out.write('%d samples of the main thread\n' % total)
    table('where the thread was, by module', by_module, 25)
    table('where the thread was, by function (self)', by_self, 60)
    table('anywhere on the stack, by function (inclusive, upper bound)', by_inclusive, 80)
    dbghelp.SymCleanup(process)


if __name__ == '__main__':
    main()
