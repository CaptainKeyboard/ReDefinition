# Runs the raytracing's parity probe in the game and fails where the rays' scene and the
# picture disagree more than they did when the thresholds were set. It starts KSP twice:
# at the space centre (the arrival test) and in flight at the launch pad (the scene probe),
# each with -redefinitionProbeParity, reads the third parity line of the log, stops KSP and
# restores what it changed. The probe's save gets a fixed time of day, so that the runs are
# alike. The raytracing is set to Light for the runs.
#
# What it changes and puts back: GameData\ReDefinition\PluginData\settings.cfg (raytracing)
# and saves\ReDefinitionProbe\persistent.sfs (the time). KSP must not run.
#
# Usage:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\parity_check.ps1 [-Scene Both|Ksc|Flight]
param(
    [ValidateSet('Both', 'Ksc', 'Flight')][string]$Scene = 'Both',
    [int]$TimeoutSeconds = 420
)

$ErrorActionPreference = 'Stop'
$kspRoot = if ($env:KspRoot) { $env:KspRoot } else { 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }
$settings = Join-Path $kspRoot 'GameData\ReDefinition\PluginData\settings.cfg'
$save = Join-Path $kspRoot 'saves\ReDefinitionProbe\persistent.sfs'
$log = Join-Path $kspRoot 'KSP.log'
$exe = Join-Path $kspRoot 'KSP_x64.exe'

# The least share of matching pixels each scene must keep, and the most that may be rays over sky. The flight's
# is low because the wind moves the blades and the cut-out lattices are drawn with their holes; the space
# centre's is 93 % (2026-10-26).
$limits = @{
    Ksc    = @{ Match = 90.0; OverSky = 0.1; Arguments = @('-redefinitionKscArrivalTest', '-redefinitionKscArrivalStay') }
    Flight = @{ Match = 50.0; OverSky = 0.1; Arguments = @('-redefinitionProbe', '-redefinitionProbeStay') }
}

if (Get-Process KSP_x64 -ErrorAction SilentlyContinue) { throw 'KSP is running; close it first.' }
$settingsBefore = [IO.File]::ReadAllText($settings)
$saveBefore = [IO.File]::ReadAllText($save)
$failed = 0
try {
    # Raytracing on, and the game's time at a fixed hour: the first UT line of the save is the universe's.
    $light = [regex]::Replace($settingsBefore, '(?m)^(\s*raytracing\s*=\s*).*$', '${1}Light')
    [IO.File]::WriteAllText($settings, $light)
    $timed = [regex]::Replace($saveBefore, '(?m)^(\t\tUT = )[0-9.]+', '${1}1638120', 1)
    [IO.File]::WriteAllText($save, $timed)

    $scenes = if ($Scene -eq 'Both') { @('Ksc', 'Flight') } else { @($Scene) }
    foreach ($name in $scenes) {
        $started = Get-Date
        $arguments = @('-popupwindow', '-screen-width', '1600', '-screen-height', '900') + $limits[$name].Arguments + '-redefinitionProbeParity'
        $process = Start-Process -FilePath $exe -WorkingDirectory $kspRoot -ArgumentList $arguments -PassThru
        $line = $null
        $deadline = $started.AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline -and -not $process.HasExited -and -not $line) {
            Start-Sleep -Seconds 5
            if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $started) {
                # The third report on: the first ones come while the scene is still being built.
                $found = @(Select-String -Path $log -Pattern 'Raytracing parity \d+x\d+: match')
                if ($found.Count -ge 3) { $line = $found[$found.Count - 1].Line }
            }
        }
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        Start-Sleep -Seconds 4
        if (-not $line) {
            Write-Output "FAIL  ${name}: no parity line in the log within $TimeoutSeconds s"
            $failed++
            continue
        }
        $match = [regex]::Match($line, 'match ([0-9.]+) %.*rays over sky ([0-9.]+) %')
        $share = [double]::Parse($match.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
        $over = [double]::Parse($match.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
        $ok = $share -ge $limits[$name].Match -and $over -le $limits[$name].OverSky
        Write-Output ('{0}  {1}: match {2} % (at least {3}), rays over sky {4} % (at most {5})' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $share, $limits[$name].Match, $over, $limits[$name].OverSky)
        if (-not $ok) { $failed++ }
    }
}
finally {
    [IO.File]::WriteAllText($settings, $settingsBefore)
    [IO.File]::WriteAllText($save, $saveBefore)
}
if ($failed -gt 0) { exit 1 }
