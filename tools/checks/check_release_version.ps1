# The version of a release against what changed since the last one.
#
# src/Migration/surface.shipped.txt holds the surface of the last release under "# release <version>".
# src/Migration/surface.txt is the surface of the build (tools/Surface).
# Where they differ in a setting, a registration, a profile or the proxy's ini, the version in src/ReDefinition.csproj
# must be higher than the last release's.
# Where something is gone or changed, it must be at least the next minor version (0.<n+1>.0), and from 1.0 on the next major one.
# The release package runs this first. It reads text only: the build does not need the game.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$shippedPath = Join-Path $repo 'src/Migration/surface.shipped.txt'
$currentPath = Join-Path $repo 'src/Migration/surface.txt'
$project = Join-Path $repo 'src/ReDefinition.csproj'

function Read-Surface($path) {
    $lines = @{}
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line.Length -eq 0 -or $line[0] -eq '#') { continue }
        $at = $line.IndexOf(' = ')
        if ($at -gt 0) { $lines[$line.Substring(0, $at)] = $line.Substring($at + 3) }
    }
    return $lines
}

$header = [IO.File]::ReadAllLines($shippedPath) | Where-Object { $_ -match '^# release ' } | Select-Object -First 1
if (-not $header) { Write-Error "$shippedPath has no '# release <version>' line."; exit 1 }
$shippedVersion = [version]($header -replace '^# release ', '').Trim()
$text = [IO.File]::ReadAllText($project)
if ($text -notmatch '<Version>([\d.]+)</Version>') { Write-Error "No <Version> in $project."; exit 1 }
$version = [version]$Matches[1]

$shipped = Read-Surface $shippedPath
$current = Read-Surface $currentPath
$counted = '^(own|mod|profile|proxy)\.'
$added = 0; $changed = 0; $removed = 0
foreach ($key in ($shipped.Keys + $current.Keys | Sort-Object -Unique)) {
    if ($key -notmatch $counted) { continue }
    if (-not $shipped.ContainsKey($key)) { $added++ }
    elseif (-not $current.ContainsKey($key)) { $removed++ }
    elseif ($shipped[$key] -ne $current[$key]) { $changed++ }
}
Write-Host "Since ${shippedVersion}: $added added, $changed changed, $removed removed. Version now $version."

$failed = $false
if ($added + $changed + $removed -gt 0 -and $version -le $shippedVersion) {
    Write-Host "ERROR: the connectors changed since $shippedVersion, and the version is still $version. Raise it in src/ReDefinition.csproj."
    $failed = $true
}
if ($changed + $removed -gt 0) {
    $minimum = if ($shippedVersion.Major -ge 1) { [version]::new($shippedVersion.Major + 1, 0, 0) } else { [version]::new(0, $shippedVersion.Minor + 1, 0) }
    if ($version -lt $minimum) {
        Write-Host "ERROR: something that was there is gone or changed, so the version must be at least $minimum, not $version."
        $failed = $true
    }
}
if ($failed) { exit 1 }
Write-Host 'The version fits what changed.'
