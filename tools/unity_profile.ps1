# Runs KSP once with Unity's development player in place of its release one, for Unity's
# own profiler. KSP's release player has none of its samplers, and the development player
# of the same Unity version (2019.4.18f1, from the editor's Windows support) has them
# all. The probe's flight is left to run with its clouds. The perf monitor logs the
# main thread's time by script and by Unity's samplers (culling, rendering, physics,
# the canvases, the scripts, GC) into KSP.log. The GPU profiler logs the GPU's frame.
# Unity's profiler records into <KSP>\ReDefinitionCaptures\<time>-unity-profile\unity-profile.raw, which the
# editor's Profiler window loads. KSP's own UnityPlayer.dll is put back afterwards.
#
# The development player is stricter: what a mod does in a MonoBehaviour's constructor
# that Unity does not allow throws there and not in the game (HUDReplacer's GUI skins).
#
# Usage:
#
#   tools\unity_profile.ps1 [-Ksp <KSP folder>] [-Unity <Unity editor folder>] [-TimeoutSeconds 600]
param(
    [string]$Ksp = $(if ($env:KspRoot) { $env:KspRoot } else { 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }),
    [string]$Unity = 'C:\Program Files\Unity\Editor',
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
if (Get-Process KSP_x64 -ErrorAction SilentlyContinue) { throw 'KSP is running' }
$variation = Join-Path $Unity 'Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_development_mono'
$development = Join-Path $variation 'UnityPlayer.dll'
if (-not (Test-Path $development)) { throw "no development player at $variation" }
$player = Join-Path $Ksp 'UnityPlayer.dll'
$release = Join-Path $Ksp 'UnityPlayer.dll.release'
$pix = Join-Path $Ksp 'WinPixEventRuntime.dll'
$pixAdded = -not (Test-Path $pix)
if (-not (Test-Path $release)) { Copy-Item $player $release }
$raw = Join-Path $Ksp ('ReDefinitionCaptures\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-unity-profile\unity-profile.raw')
New-Item -ItemType Directory -Force (Split-Path $raw) | Out-Null
try {
    Copy-Item $development $player -Force
    if ($pixAdded) { Copy-Item (Join-Path $variation 'WinPixEventRuntime.dll') $pix }
    $process = Start-Process -FilePath (Join-Path $Ksp 'KSP_x64.exe') -WorkingDirectory $Ksp -PassThru -ArgumentList @(
        '-redefinitionProbe', '-redefinitionProbeStay', '-redefinitionProbeClouds', '-redefinitionPerfMonitor',
        '-redefinitionPerfMonitorUnity', '-redefinitionGpuProfile', '-redefinitionUnityProfilerLog', "`"$raw`"",
        '-redefinitionProbeShot', 'unity-profile')
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process | Stop-Process -Force
        Write-Output "KSP did not finish within $TimeoutSeconds s and was ended."
    }
}
finally {
    Copy-Item $release $player -Force
    if ($pixAdded -and (Test-Path $pix)) { Remove-Item $pix }
}
Get-ChildItem (Split-Path $raw) -Filter 'unity-profile*.raw' | ForEach-Object { "$($_.FullName)  $([math]::Round($_.Length / 1MB)) MB" }
