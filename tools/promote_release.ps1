# After a release: its steps and its surface become the last release's.
#
# The steps under Unreleased in src/Migration/Migrations.cs move into Released, between the marker comments there.
# src/Migration/surface.txt, the surface of the build, becomes src/Migration/surface.shipped.txt under "# release <version>".
# The version is the one in src/ReDefinition.csproj.
# Run it on the commit the release was made from, once it is out, with surface.txt current.
# Write that with REDEFINITION_UPDATE_SURFACE=1 dotnet test --filter TheWrittenSurface.
# Then commit both files, and say so in the Connectors line.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$steps = Join-Path $repo 'src/Migration/Migrations.cs'
$surface = Join-Path $repo 'src/Migration/surface.txt'
$shipped = Join-Path $repo 'src/Migration/surface.shipped.txt'
$project = Join-Path $repo 'src/ReDefinition.csproj'

$text = [IO.File]::ReadAllText($project)
if ($text -notmatch '<Version>([\d.]+)</Version>') { Write-Error "No <Version> in $project."; exit 1 }
$version = $Matches[1]

$code = [IO.File]::ReadAllText($steps)
$newline = if ($code.Contains("`r`n")) { "`r`n" } else { "`n" }
$moved = 0
foreach ($kind in @(@('unreleased-steps', 'released-steps'), @('unreleased-bundled', 'released-bundled'))) {
    $begin = "// $($kind[0])-begin"
    $end = "// $($kind[0])-end"
    $target = "// $($kind[1])-end"
    $from = $code.IndexOf($begin)
    $to = $code.IndexOf($end)
    $into = $code.IndexOf($target)
    if ($from -lt 0 -or $to -lt $from -or $into -lt 0) { Write-Error "The markers for $($kind[0]) are missing in $steps."; exit 1 }
    $start = $from + $begin.Length
    $block = $code.Substring($start, $to - $start)
    if ($block.Trim().Length -eq 0) { continue }
    $moved++
    # The steps out of Unreleased, in front of the marker that ends Released, and the lines of the marker kept as they were.
    $code = $code.Remove($start, $to - $start).Insert($start, $newline + '            ')
    $into = $code.IndexOf($target)
    $code = $code.Insert($into, $block.Trim([char[]]"`r`n ") + $newline + '            ')
}
[IO.File]::WriteAllText($steps, $code, (New-Object System.Text.UTF8Encoding($false)))

$lines = [IO.File]::ReadAllLines($surface) | Where-Object { $_ -notmatch '^# release ' }
$header = $lines | Select-Object -First 1
$body = $lines | Select-Object -Skip 1
[IO.File]::WriteAllLines($shipped, @($header, "# release $version") + $body, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Release ${version}: $moved groups of steps moved into Released, surface.shipped.txt written from surface.txt."
