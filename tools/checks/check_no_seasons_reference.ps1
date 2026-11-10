# The player build of ReDefinition.dll must not reference ReDefinition.Seasons: ReDefinition works without the seasons installed.
#
# The seasons are a mod of their own, built before ReDefinition so that its DLL lands in the output beside it, and referenced by nothing
# (src/ReDefinition.csproj, ReferenceOutputAssembly=false). This reads the assembly's references and fails if the seasons are among
# them. It also reads ReDefinition.Seasons.dll, which must name neither ReDefinition nor Harmony nor ZTheme. The release package runs
# it after the build; it can be run by hand on any build:
#   powershell -File tools/checks/check_no_seasons_reference.ps1 [path\to\ReDefinition.dll]

param([string]$Dll)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Dll) { $Dll = Join-Path $repo 'build\ReDefinition.dll' }
if (-not (Test-Path -LiteralPath $Dll)) {
    Write-Host "check_no_seasons_reference: '$Dll' does not exist."
    exit 1
}

function Get-References($file) {
    # Reflection-only: the references are read from the metadata, nothing is resolved or run.
    $assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Resolve-Path -LiteralPath $file).Path)
    return @($assembly.GetReferencedAssemblies() | ForEach-Object { $_.Name })
}

$path = (Resolve-Path -LiteralPath $Dll).Path
if ((Get-References $path) -contains 'ReDefinition.Seasons') {
    Write-Host "check_no_seasons_reference: FAILED. $path references ReDefinition.Seasons."
    Write-Host 'The player build must not name the seasons; see src/ReDefinition.csproj.'
    exit 1
}

$seasons = Join-Path (Split-Path -Parent $path) 'ReDefinition.Seasons.dll'
if (Test-Path -LiteralPath $seasons) {
    $bad = @(Get-References $seasons | Where-Object { $_ -eq 'ReDefinition' -or $_ -eq '0Harmony' -or $_ -like '*ZTheme*' })
    if ($bad.Count -gt 0) {
        Write-Host "check_no_seasons_reference: FAILED. $seasons references $($bad -join ', ')."
        exit 1
    }
}
Write-Host "check_no_seasons_reference: OK. $path does not reference ReDefinition.Seasons, and the seasons name neither ReDefinition, Harmony nor ZTheme."
exit 0
