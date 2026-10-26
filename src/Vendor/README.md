# Vendor

What ReDefinition needs from the GPU makers: the class of the player's graphics card, and the
download and install of NVIDIA's, Intel's and the RTX 30 package's files.

## Start here

| File | Read it for |
|---|---|
| `src/Vendor/TechniqueFiles.cs` | What choosing a technique with missing files does, step by step |
| `src/Vendor/GpuClass.cs` | `GpuClass`, the classes profile values differ by, and `GpuClass.Of` |
| `src/Vendor/NvidiaFiles.cs` | The Streamline release's entries, checksums, `NvidiaFiles.Extract` and `NvidiaFiles.PlaceAll` |
| `src/Vendor/NvidiaDownloader.cs` | The download: ranges from the archive, a thread for the check, the dialog |
| `src/Vendor/AmperePackage.cs` | The dlssg_for_sm86 zip and what comes out of it |

## What it does

- `GpuClass` sorts the adapter into RTX 20 to RTX 50, older NVIDIA, Radeon and Intel classes.
- `NvidiaFiles` and `IntelFiles` describe the vendors' releases. Each file has an offset, sizes
  and a checksum.
- `NvidiaDownloader` and `IntelDownloader` fetch only the needed entries of the vendor's archive,
  after the player accepts the vendor's licence in a dialog.
- `AmperePackage`, `AmpereFrameGeneration` and `AmpereOptions` install the community package
  that runs DLSS frame generation on RTX 30 and RTX 20, and edit its ini.
- `TechniqueFiles` chains the dialogs for the files a chosen technique lacks, and chooses the
  technique only once they are in place.
- ReDefinition ships none of these DLLs. The player downloads or places them.

## What it uses and what uses it

It uses Core, Modules, Proxy, Ui and Window. Modules, Probe, Settings and Window use it.

## Rules of the area

- A download places all files or none. Every file is checked against its checksum in a partial
  file beside its target, and `NvidiaFiles.PlaceAll` moves them only when all are right.
- `NvidiaFiles`, `IntelFiles`, `AmperePackage` and `AmpereOptions` stay free of Unity, for their
  tests in `tests/ReDefinition.Tests` (`NvidiaFilesTests.cs`, `IntelFilesTests.cs`,
  `AmperePackageTests.cs`, `AmpereOptionsTests.cs`).
- The check and the placing run on a thread of their own. The download's thread counts the
  received bytes, and the settings row reads them on the main thread
  (`src/Vendor/NvidiaDownloader.cs`).
- A new release of a vendor's SDK changes the figures in `NvidiaFiles` or `IntelFiles`. Read
  them from the release again; do not guess them.
- `AmperePackage` pins no file, because the package's layout changes between releases. What has
  to be there is checked, and each file comes out with the size and CRC-32 the zip gives it.
- DLSS takes new files at once. Streamline and the Intel frame generation DLLs load when KSP
  starts, so frame generation runs after a restart.
