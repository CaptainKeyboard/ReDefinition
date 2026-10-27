# Runs the raytracing's parity probe in the game and fails where the rays' scene and the
# picture disagree more than they did when the thresholds were set. It starts KSP three times:
# at the space centre (the arrival test), in flight at the launch pad (the scene probe) and in flight from
# twelve camera poses (the views probe, with the rays off and on), each with -redefinitionProbeParity. It reads
# the third parity line of the log (the views: each pose's), compares the views' pictures tile by tile
# (tools/parity_images.py), stops KSP and restores what it changed. The probe's save gets a fixed time of day, so that the runs are
# alike. The raytracing is set to Light for the runs.
#
# What it changes and puts back: GameData\ReDefinition\PluginData\settings.cfg (raytracing)
# and saves\ReDefinitionProbe\persistent.sfs (the time). KSP must not run.
#
# Usage:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\parity_check.ps1 [-Scene Both|Ksc|Flight]
param(
    [ValidateSet('Both', 'Ksc', 'Flight', 'Views')][string]$Scene = 'Both',
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

# The views: the flight camera at these poses (pitch, distance, heading), each shot with the rays off and on. Each pose's
# parity must keep the share below, and tools\parity_images.py compares the pictures tile by tile.
$poses = '55,330,0;40,150,0;60,300,300;40,600,0;25,400,120;80,1500,0;15,800,60;60,150,200;40,3000,0;30,200,90;70,80,45;20,1200,240'
$viewMatch = 60.0
$viewOverSky = 0.2
# The most share of a pose's pixels where the rays' surface lies before the picture's: scene the picture does not draw.
$viewInFront = 1.0
$viewTiles = 6.0

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

    $scenes = if ($Scene -eq 'Both') { @('Ksc', 'Flight', 'Views') } elseif ($Scene -eq 'Views') { @('Views') } else { @($Scene) }
    foreach ($name in $scenes) {
        if ($name -eq 'Views') {
            $started = Get-Date
            $count = $poses.Split(';').Count
            $arguments = @('-popupwindow', '-screen-width', '1600', '-screen-height', '900', '-redefinitionProbe', '-redefinitionProbeStay',
                '-redefinitionProbeShot', 'gate', '-redefinitionProbeViews', $poses, '-redefinitionProbeViewModes', 'Off,Light', '-redefinitionProbeParity')
            $process = Start-Process -FilePath $exe -WorkingDirectory $kspRoot -ArgumentList $arguments -PassThru
            $deadline = $started.AddSeconds($TimeoutSeconds + 600)
            $done = $false
            while ((Get-Date) -lt $deadline -and -not $process.HasExited -and -not $done) {
                Start-Sleep -Seconds 5
                if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $started) {
                    $done = [bool](Select-String -Path $log -Pattern ('views: gate-v' + ($count - 1) + '-Light.png'))
                }
            }
            Start-Sleep -Seconds 3
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
            Start-Sleep -Seconds 4
            if (-not $done) { Write-Output "FAIL  Views: the poses were not all shot within the time"; $failed++; continue }
            # The parity of each pose: the line after 'parity of view <name>:'.
            $lines = Get-Content $log
            for ($i = 0; $i -lt $lines.Count - 1; $i++) {
                $head = [regex]::Match($lines[$i], 'Raytracing parity of view (gate-v\d+):')
                if (-not $head.Success) { continue }
                $match = [regex]::Match($lines[$i + 1], 'match ([0-9.]+) %.*rays in front ([0-9.]+) %.*rays over sky ([0-9.]+) %')
                if (-not $match.Success) { continue }
                $share = [double]::Parse($match.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
                $front = [double]::Parse($match.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
                $over = [double]::Parse($match.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
                $ok = $share -ge $viewMatch -and $over -le $viewOverSky -and $front -le $viewInFront
                Write-Output ('{0}  {1}: match {2} % (at least {3}), rays in front {6} % (at most {7}), rays over sky {4} % (at most {5})' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $head.Groups[1].Value, $share, $viewMatch, $over, $viewOverSky, $front, $viewInFront)
                if (-not $ok) { $failed++ }
            }
            $run = Get-ChildItem (Join-Path $kspRoot 'ReDefinitionCaptures') -Directory -Filter '*-run' | Sort-Object LastWriteTime | Select-Object -Last 1
            & python (Join-Path $PSScriptRoot 'parity_images.py') $run.FullName gate --max-share $viewTiles
            if ($LASTEXITCODE -ne 0) { $failed++ }
            continue
        }
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
