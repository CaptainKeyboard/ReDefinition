# Shoots a command pod on the ground of each body asked for, from several poses. The raytracing is off, on Light and
# on Full, and the sun stands at 30 degrees in the morning: the light of the planets as the picture and the rays give it.
# Per body it starts KSP once, or twice where the first run's sun stood too low or too high.
# The pictures go to ReDefinitionCaptures\planets\<body>; tools\planet_sheets.py makes sheets of them.
#
# What it changes and puts back: GameData\ReDefinition\PluginData\settings.cfg and saves\ReDefinitionProbe\persistent.sfs.
# KSP must not run.
#
# Usage:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\planet_shots.ps1 [-Bodies Mun,Duna] [-TimeoutSeconds 900]
param(
    [string[]]$Bodies = @('Mun', 'Minmus', 'Duna', 'Eve', 'Moho', 'Laythe', 'Ike', 'Dres', 'Vall', 'Tylo', 'Bop', 'Pol', 'Eeloo', 'Gilly'),
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$kspRoot = if ($env:KspRoot) { $env:KspRoot } else { 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }
$settings = Join-Path $kspRoot 'GameData\ReDefinition\PluginData\settings.cfg'
$save = Join-Path $kspRoot 'saves\ReDefinitionProbe\persistent.sfs'
$log = Join-Path $kspRoot 'KSP.log'
$exe = Join-Path $kspRoot 'KSP_x64.exe'
$captures = Join-Path $kspRoot 'ReDefinitionCaptures'
# Pitch (positive looks down), distance in metres, heading: the pod's surroundings from near, the ground from further up, the sky.
$poses = '10,15,0;10,15,120;10,15,240;35,80,0;-8,12,0;-25,12,180'
$lastPose = $poses.Split(';').Count - 1

if (Get-Process KSP_x64 -ErrorAction SilentlyContinue) { throw 'KSP is running; close it first.' }
$settingsBefore = [IO.File]::ReadAllText($settings)
$saveBefore = [IO.File]::ReadAllText($save)

# One KSP run on the body at the longitude (null: the default, with the sun's check); the longitude the sun asks for
# where it quit, or 'done' where the views were shot.
function Invoke-Run([string]$body, $longitude) {
    $started = Get-Date
    $arguments = @('-popupwindow', '-screen-width', '1600', '-screen-height', '900', '-redefinitionProbe', '-redefinitionProbeStay',
        '-redefinitionProbeLand', $body, '-redefinitionProbeShot', $body, '-redefinitionProbeViews', $poses,
        '-redefinitionProbeViewModes', 'Off,Light,Full')
    if ($null -eq $longitude) { $arguments += '-redefinitionProbeLandAdjust' } else { $arguments += @('-redefinitionProbeLandLongitude', $longitude) }
    $process = Start-Process -FilePath $exe -WorkingDirectory $kspRoot -ArgumentList $arguments -PassThru
    $deadline = $started.AddSeconds($TimeoutSeconds)
    $result = $null
    while ((Get-Date) -lt $deadline -and $null -eq $result) {
        Start-Sleep -Seconds 5
        if (-not (Test-Path $log) -or (Get-Item $log).LastWriteTime -le $started) { continue }
        $text = Get-Content $log -Raw -ErrorAction SilentlyContinue
        if ($text -match "views: $body-v$lastPose-Full.png") { $result = 'done' }
        elseif ($process.HasExited) {
            $next = [regex]::Match($text, 'longitude for 30 degrees: (-?[0-9]+(?:\.[0-9]+)?)')
            $result = if ($next.Success) { $next.Groups[1].Value } else { 'failed' }
        }
        elseif ($text -match 'could not be put on|no level ground') { $result = 'failed' }
    }
    if ($null -eq $result) { $result = 'failed' }
    Start-Sleep -Seconds 3
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Start-Sleep -Seconds 4
    return $result
}

try {
    $light = [regex]::Replace($settingsBefore, '(?m)^(\s*raytracing\s*=\s*).*$', '${1}Light')
    [IO.File]::WriteAllText($settings, $light)
    foreach ($body in $Bodies) {
        $result = Invoke-Run $body $null
        if ($result -ne 'done' -and $result -ne 'failed') { $result = Invoke-Run $body ([double]::Parse($result, [Globalization.CultureInfo]::InvariantCulture)) }
        if ($result -ne 'done') { Write-Output "FAIL  ${body}: no pictures"; continue }
        $run = Get-ChildItem $captures -Directory -Filter '*-run' | Sort-Object LastWriteTime | Select-Object -Last 1
        $target = Join-Path $captures "planets\$body"
        New-Item -ItemType Directory -Force $target | Out-Null
        Get-ChildItem $run.FullName -Filter "$body-v*.png" | Copy-Item -Destination $target -Force
        $sun = Select-String -Path $log -Pattern 'Probe: land: .* the sun stands' | Select-Object -Last 1
        Write-Output ("OK    {0}: {1}" -f $body, ($sun.Line -replace '^.*Probe: land: ', ''))
    }
}
finally {
    [IO.File]::WriteAllText($settings, $settingsBefore)
    [IO.File]::WriteAllText($save, $saveBefore)
}
