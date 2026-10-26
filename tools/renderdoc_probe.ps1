# Starts KSP under RenderDoc with the scene probe (src/Probe/RenderDocCapture.cs).
# KSP loads the save ReDefinitionProbe. The probe captures a frame with the camera
# still and one at a step of its sweep (-redefinitionProbeCaptureStep, default 21,
# where the turn reverses). Then it captures the same without the vessel's renderers:
# the still frame, and the four frames around that step, one of which shows the same
# pose. Then KSP quits. The captures (.rdc) land in the session's folder,
# <KSP>\ReDefinitionCaptures\<time>-run, as renderdoc_frame<n>.rdc, to open in qrenderdoc
# or read with its Python API.
#
# The dxgi.dll proxy is renamed for the run and put back afterwards: under RenderDoc
# its D3D12 swapchain is refused and it falls back to the real one, so a capture
# holds the game without it either way. Unity's own passes do not depend on it.
#
# With -Stay the flight is instead left to run with its clouds and one frame captured
# 25 s on (-redefinitionProbeRenderDoc), for where the GPU's time goes inside Unity's
# passes (tools/renderdoc_gpu_times.py).
#
# Usage:
#
#   tools\renderdoc_probe.ps1 [-Ksp <KSP folder>] [-RenderDoc <renderdoccmd.exe>]
#                             [-KeepProxy] [-Stay] [-TimeoutSeconds 420]
param(
    # By default KspRoot, as the build takes it, or the Steam location.
    [string]$Ksp = $(if ($env:KspRoot) { $env:KspRoot } else { 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }),
    [string]$RenderDoc = 'C:\Program Files\RenderDoc\renderdoccmd.exe',
    [switch]$KeepProxy,
    [switch]$Stay,
    [int]$TimeoutSeconds = 420
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $RenderDoc)) { throw "no renderdoccmd at $RenderDoc" }
$exe = Join-Path $Ksp 'KSP_x64.exe'
$proxy = Join-Path $Ksp 'dxgi.dll'
$parked = Join-Path $Ksp 'dxgi.dll.renderdoc'
$moved = $false
if (-not $KeepProxy -and (Test-Path $proxy)) {
    Move-Item $proxy $parked -Force
    $moved = $true
}
try {
    $arguments = @('capture', '--wait-for-exit', '--working-dir', "`"$Ksp`"", "`"$exe`"", '-redefinitionProbe')
    if ($Stay) { $arguments += @('-redefinitionProbeStay', '-redefinitionProbeClouds', '-redefinitionProbeRenderDoc') }
    $process = Start-Process -FilePath $RenderDoc -WorkingDirectory $Ksp -PassThru -ArgumentList $arguments
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Get-Process KSP_x64 -ErrorAction SilentlyContinue | Stop-Process -Force
        Write-Output "KSP did not finish within $TimeoutSeconds s and was ended."
    }
    Get-Process KSP_x64 -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
}
finally {
    if ($moved) { Move-Item $parked $proxy -Force }
}
Select-String -Path (Join-Path $Ksp 'KSP.log') -Pattern 'Probe: (RenderDoc|flight)' | ForEach-Object { $_.Line }
Get-ChildItem (Join-Path $Ksp 'ReDefinitionCaptures') -Filter 'renderdoc*.rdc' -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 4 |
    ForEach-Object { "$($_.FullName)  $([math]::Round($_.Length / 1MB)) MB" }
