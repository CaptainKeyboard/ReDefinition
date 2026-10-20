# Runs the vendors' own checks of what ReDefinition hands their upscalers and frame
# generation, in the game, without anyone at the keyboard (src/Probe/VendorProbe.cs):
# KSP starts with -redefinitionProbe -redefinitionProbeVendor, loads the save
# ReDefinitionProbe, cycles the debug visualizations of NVIDIA's development DLLs
# and records each, then quits. The proxy's ini is set for it and put back after:
# NVIDIA's development DLLs from the Streamline SDK's bin\x64\development,
# vendorDiagnostics=1 (AMD's debug checker), streamlineImgui as asked, and NGX's
# ngxdlssg.json with the detailed on-screen text.
#
#   tools\vendor_probe.ps1 [-Ksp <KSP folder>] [-Development <folder with the dev DLLs>]
#                          [-ShowOnlyGenerated]
#
# The recordings land in <KSP>\ReDefinitionCaptures, one folder per state, named in
# KSP.log ("Probe: vendor ..."). The window must stay in front: the hotkeys go
# through Windows' input queue.
param(
    # By default KspRoot, as the build takes it, or the Steam location.
    [string]$Ksp = $(if ($env:KspRoot) { $env:KspRoot } else { 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }),
    # Beside the main checkout, also from a git worktree of it: References holds the vendors' SDKs.
    [string]$Development = (Join-Path (Split-Path -Parent (Split-Path -Parent (git -C $PSScriptRoot rev-parse --path-format=absolute --git-common-dir))) 'References\KspUpscaler\streamline-sdk-v2.14.1\bin\x64\development'),
    [switch]$ShowOnlyGenerated,
    # Without AMD's debug checker (vendorDiagnostics).
    [switch]$NoDiagnostics,
    # Streamline's ImGUI plugin too (streamlineImgui): with it the GPU device was
    # removed while KSP loaded (2026-10-02).
    [switch]$Imgui,
    # FSR's frame generation in place of DLSS-G, for AMD's checker to see its inputs.
    [switch]$Fsr,
    # The camera sweep the other way round (-redefinitionProbeSweepRight).
    [switch]$SweepRight,
    # KSP is ended after this long: a lost device leaves it hanging.
    [int]$TimeoutSeconds = 420
)

$ErrorActionPreference = 'Stop'
$ini = Join-Path $Ksp 'ReDefinitionProxy.ini'
$backup = "$ini.vendor-probe-backup"
$json = Join-Path $Ksp 'ngxdlssg.json'
if (-not (Test-Path (Join-Path $Development 'sl.imgui.dll'))) { throw "no development DLLs in $Development" }

Copy-Item $ini $backup -Force
try {
    $lines = Get-Content $ini
    $set = @{
        'streamlineDirectory' = $Development
        'dlssDirectory' = $Development
        'vendorDiagnostics' = $(if ($NoDiagnostics) { '0' } else { '1' })
        'streamlineImgui' = $(if ($Imgui) { '1' } else { '0' })
        'frameGenerationTechnique' = $(if ($Fsr) { 'fsr' } else { 'dlss' })
        # Frame generation also below the provider's minimum frame rate, so that it
        # runs and the checker sees its inputs.
        'fgMinimumFps' = '0'
        'fgShowOnlyGenerated' = $(if ($ShowOnlyGenerated) { '1' } else { '0' })
    }
    foreach ($key in $set.Keys) {
        $found = $false
        $lines = $lines | ForEach-Object {
            if ($_ -match "^$key=") { $found = $true; "$key=$($set[$key])" } else { $_ }
        }
        if (-not $found) { $lines += "$key=$($set[$key])" }
    }
    Set-Content -Path $ini -Value $lines -Encoding ASCII
    # NGX's DLSS-G debug settings: the detailed indicator text, default hotkeys
    # (DLSS-FG Programming Guide, JSON Configuration).
    Set-Content -Path $json -Encoding ASCII -Value '{ "text_indicator_level": 2 }'

    $process = Start-Process -FilePath (Join-Path $Ksp 'KSP_x64.exe') -WorkingDirectory $Ksp -PassThru `
        -ArgumentList (@('-redefinitionProbe', '-redefinitionProbeVendor') + $(if ($SweepRight) { @('-redefinitionProbeSweepRight') } else { @() }))
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force
        Write-Output "KSP did not finish within $TimeoutSeconds s and was ended."
    }
} finally {
    Copy-Item $backup $ini -Force
    Remove-Item $backup -Force
    Remove-Item $json -Force -ErrorAction SilentlyContinue
}
Select-String -Path (Join-Path $Ksp 'KSP.log') -Pattern 'Probe: vendor' | ForEach-Object { $_.Line }
Select-String -Path (Join-Path $Ksp 'ReDefinitionProxy.log') -Pattern 'FidelityFX (warning|error|message)|error\(s\) from its Present|debug checker|Streamline error' |
    Select-Object -First 40 | ForEach-Object { $_.Line }
