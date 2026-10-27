# Shoots a command pod at each gas giant asked for, from poses that look at the planet and its clouds, with the
# raytracing off, on Light and on Full. Each orbit altitude (-redefinitionProbeOrbit) is a circular orbit where the sun
# lights the side the camera looks at. Each air altitude (-redefinitionProbeHover) holds the pod at rest in the air there,
# where it would otherwise be crushed or burnt in seconds. Jool's air reaches 200 km, and a pod is destroyed below 0 m.
# The pictures go to ReDefinitionCaptures\planets\<body>-<altitude>; tools\planet_sheets.py makes sheets of them.
#
# What it changes and puts back: GameData\ReDefinition\PluginData\settings.cfg and saves\ReDefinitionProbe\persistent.sfs.
# KSP must not run.
#
# Usage:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\gas_giant_shots.ps1 [-Bodies Jool] [-Altitudes 220,600,2500] [-AirAltitudes 120,60,30,12] [-NoOrbit]
param(
    [string[]]$Bodies = @('Jool'),
    [string]$Altitudes = '220,600,2500',
    [string]$AirAltitudes = '',
    [switch]$NoOrbit,
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
# Comma lists, as -File passes them whole.
$orbitList = @($Altitudes -split ',' | Where-Object { $_ } | ForEach-Object { [int]$_ })
$airList = @($AirAltitudes -split ',' | Where-Object { $_ } | ForEach-Object { [int]$_ })
. (Join-Path $PSScriptRoot 'common.ps1')
$kspRoot = Get-KspRoot
$settings = Join-Path $kspRoot 'GameData\ReDefinition\PluginData\settings.cfg'
$save = Join-Path $kspRoot 'saves\ReDefinitionProbe\persistent.sfs'
$log = Join-Path $kspRoot 'KSP.log'
$exe = Join-Path $kspRoot 'KSP_x64.exe'
$captures = Join-Path $kspRoot 'ReDefinitionCaptures'
# Pitch (positive looks down), distance in metres, heading. The camera turns with the pod, which turns freely in orbit,
# so these are the poses that looked at the planet from 600 km (headings 180 and 270 of the pod's own frame).
$orbitPoses = '-30,30,180;10,30,180;50,30,180;-30,30,270;10,30,270;50,30,270;80,30,180;80,30,270'
# In the air the pod stands upright: the horizon all round, the ground below and the sun above.
$airPoses = '5,30,0;5,30,90;5,30,180;5,30,270;50,30,0;-25,30,0'

if (Get-Process KSP_x64 -ErrorAction SilentlyContinue) { throw 'KSP is running; close it first.' }
$settingsBefore = [IO.File]::ReadAllText($settings)
$saveBefore = [IO.File]::ReadAllText($save)
try {
    [IO.File]::WriteAllText($settings, [regex]::Replace($settingsBefore, '(?m)^(\s*raytracing\s*=\s*).*$', '${1}Light'))
    foreach ($body in $Bodies) {
        $runs = @($orbitList | Where-Object { -not $NoOrbit } | ForEach-Object { @{ Altitude = $_; Air = $false } }) + @($airList | ForEach-Object { @{ Altitude = $_; Air = $true } })
        foreach ($run in $runs) {
            $altitude = $run.Altitude
            $poses = if ($run.Air) { $airPoses } else { $orbitPoses }
            $lastPose = $poses.Split(';').Count - 1
            $shot = if ($run.Air) { "$body-air-${altitude}km" } else { "$body-${altitude}km" }
            $started = Get-Date
            $arguments = @('-popupwindow', '-screen-width', '1600', '-screen-height', '900', '-redefinitionProbe', '-redefinitionProbeStay', '-redefinitionProbeClouds',
                '-redefinitionProbeOrbit', $body, '-redefinitionProbeOrbitAltitude', ($(if ($run.Air) { 600 } else { $altitude }) * 1000),
                '-redefinitionProbeShot', $shot, '-redefinitionProbeViews', $poses, '-redefinitionProbeViewModes', 'Off,Light,Full')
            if ($run.Air) { $arguments += @('-redefinitionProbeHover', ($altitude * 1000)) }
            $process = Start-Process -FilePath $exe -WorkingDirectory $kspRoot -ArgumentList $arguments -PassThru
            $deadline = $started.AddSeconds($TimeoutSeconds)
            $done = $false
            while ((Get-Date) -lt $deadline -and -not $process.HasExited -and -not $done) {
                Start-Sleep -Seconds 5
                if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $started) {
                    $done = [bool](Select-String -Path $log -Pattern "views: $shot-v$lastPose-Full.png")
                }
            }
            Start-Sleep -Seconds 3
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
            Start-Sleep -Seconds 4
            if (-not $done) { Write-Output "FAIL  ${shot}: no pictures"; continue }
            $folder = Get-ChildItem $captures -Directory -Filter '*-run' | Sort-Object LastWriteTime | Select-Object -Last 1
            $target = Join-Path $captures "planets\$shot"
            New-Item -ItemType Directory -Force $target | Out-Null
            Get-ChildItem $folder.FullName -Filter "$shot-v*.png" | Copy-Item -Destination $target -Force
            Write-Output "OK    $shot"
        }
    }
}
finally {
    [IO.File]::WriteAllText($settings, $settingsBefore)
    [IO.File]::WriteAllText($save, $saveBefore)
}
