# Measures frame generation's frames against rendered ones with FLIP, in the game,
# without anyone at the keyboard (src/Probe/FlipProbe.cs). Two runs of KSP on the save
# ReDefinitionProbe, each recording what the monitor shows during the same camera
# sweep. The first is without frame generation at half the step (the reference: a rendered
# frame at every pose a generated frame stands at), the second with frame generation at the
# full step. The proxy's ini is set for each run and put back after. The two
# recordings then go to tools\analyze_flip.py. The game's own settings (upscaler,
# raytracing) stay as they are in both runs.
#
# Usage:
#
#   tools\flip_probe.ps1 [-Ksp <KSP folder>] [-Fsr] [-TimeoutSeconds 420]
param(
    # By default KspRoot, as the build takes it, or the Steam location.
    [string]$Ksp = $(if ($env:KspRoot) { $env:KspRoot } else { 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }),
    # FSR's frame generation in place of DLSS-G.
    [switch]$Fsr,
    # Unity's -monitor (1 = the first display): the desktop recording needs the game
    # on a monitor of the GPU it renders on. 0 leaves Unity's own choice.
    [int]$Monitor = 0,
    # KSP is ended after this long: a lost device leaves it hanging.
    [int]$TimeoutSeconds = 420
)

$ErrorActionPreference = 'Stop'
$ini = Join-Path $Ksp 'ReDefinitionProxy.ini'
$backup = "$ini.flip-probe-backup"
$log = Join-Path $Ksp 'KSP.log'

# Writes the proxy's ini as the backup with the given keys set: replaced where they stand, appended where not.
function Set-Ini([hashtable]$set) {
    $lines = Get-Content $backup
    foreach ($key in $set.Keys) {
        $found = $false
        $lines = $lines | ForEach-Object {
            if ($_ -match "^$key=") { $found = $true; "$key=$($set[$key])" } else { $_ }
        }
        if (-not $found) { $lines += "$key=$($set[$key])" }
    }
    Set-Content -Path $ini -Value $lines -Encoding ASCII
}

$monitorArguments = @()
if ($Monitor -gt 0) { $monitorArguments = @('-monitor', "$Monitor") }

# Runs KSP with the flip probe and the arguments, ends it after the timeout, prints its flip sweep lines and returns
# the recording's folder the log names, or null where the sweep did not finish.
function Invoke-Run([string[]]$arguments) {
    $process = Start-Process -FilePath (Join-Path $Ksp 'KSP_x64.exe') -WorkingDirectory $Ksp -PassThru `
        -ArgumentList (@('-redefinitionProbe', '-redefinitionProbeFlip') + $arguments + $monitorArguments)
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force
        Write-Host "KSP did not finish within $TimeoutSeconds s and was ended."
    }
    $done = Select-String -Path $log -Pattern 'Probe: flip sweep: done: (.+)$' | Select-Object -Last 1
    Select-String -Path $log -Pattern 'Probe: flip sweep' | ForEach-Object { Write-Host $_.Line }
    if ($done) { return $done.Matches[0].Groups[1].Value.Trim() }
    return $null
}

Copy-Item $ini $backup -Force
try {
    Set-Ini @{ 'frameGeneration' = '0' }
    $reference = Invoke-Run @('-redefinitionProbeHalfStep')
    Set-Ini @{
        'frameGeneration' = '1'
        'frameGenerationTechnique' = $(if ($Fsr) { 'fsr' } else { 'dlss' })
        # Frame generation also below the provider's minimum frame rate.
        'fgMinimumFps' = '0'
    }
    $generated = Invoke-Run @()
} finally {
    Copy-Item $backup $ini -Force
    Remove-Item $backup -Force
}
Write-Output "reference: $reference"
Write-Output "generated: $generated"
