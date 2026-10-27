# Common

What every other folder of the proxy uses: the settings read from `ReDefinitionProxy.ini`, the
log, file helpers and Direct3D 12 helpers.

## Start here

| File | Read it for |
|---|---|
| `src/DxgiProxy/Common/Config.h` | `ProxyConfig`, every setting with its default, and which ones are live |
| `src/DxgiProxy/Common/Config.cpp` | Reading the ini, completing it from the template, reloading it |
| `src/DxgiProxy/Common/Log.h` | `LogLine`, `Hr` and `FormatTo` |
| `src/DxgiProxy/Common/D3d12Util.h` | Barrier and view helpers for Direct3D 12 |
| `src/DxgiProxy/Common/ShaderCompiler.h` | `d3dcompiler_47.dll` loaded by hand from System32, and a compute shader compiled from source |
| `src/DxgiProxy/Common/RuntimeLoader.h` | A vendor's DLL loaded from its folder, with the reason when it did not load |
| `src/DxgiProxy/Common/FileUtil.h` | The game's folder, file versions and stamps, UTF-8 |
| `src/DxgiProxy/Common/ProjectIdentity.h` | What ReDefinition tells NVIDIA's libraries it is |

## What it does

- `Config()` returns the settings as `ProxyConfig`. `LoadConfig` reads them at load, and
  `ReloadConfigIfChanged` reads them again while the game runs.
- `ConfigAsked` and the directory functions give a thread other than the present path the file's
  current values.
- `Config.cpp` completes a player's ini from the template built into the DLL. It adds the keys the
  file lacks, each after its comment, and keeps the player's values.
- `Log.cpp` writes `ReDefinitionProxy.log` next to the executable, one flushed line at a time.
- `FileUtil` finds the executable's folder and reads a DLL's version resource.
- `D3d12Util.h` has the transition and UAV barriers and the view descriptions the other folders
  repeat.

## What it uses and what uses it

It uses nothing of the proxy. Every other folder uses it.

## Rules of the area

- The ini is re-read at most once a second, when its write time changed. The present path calls
  it, and a file query for every frame would land in the frame times the proxy measures
  (`ReloadConfigIfChanged`).
- Some settings are read once at load: `enabled`, `measureOnly`, `frameGeneration` and
  `allowTearing`. A swapchain cannot be exchanged under a running game, so a reload keeps their
  values.
- The proxy records the values it wrote into `ReDefinitionProxy.defaults`. A value still as written
  follows a newer version's default, and a value the player changed stays (`RaiseDefaults`).
- A new setting needs its field in `ProxyConfig`, its line and comment in
  `src/DxgiProxy/ReDefinitionProxy.ini`, and a read in `Config.cpp`.
- Log lines with numbers go through `FormatTo`. `sprintf_s` ends the process when the text is
  longer than the buffer (`src/DxgiProxy/Common/Log.h`).
- The log is next to the executable, not in the working directory. The working directory of a
  Steam launch is not reliably the game folder (`src/DxgiProxy/Common/Log.cpp`).
