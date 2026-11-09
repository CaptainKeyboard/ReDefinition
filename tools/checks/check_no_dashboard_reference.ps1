# The player build of ReDefinition.dll must not reference ReDefinition.Dashboard: ReDefinition works without the dashboard installed.
#
# The dashboard is a mod of its own, built before ReDefinition so that its DLL lands in the output beside it, and referenced by nothing
# (src/ReDefinition.csproj, ReferenceOutputAssembly=false). This reads the assembly's references and fails if the dashboard is among
# them. The release package runs it after the build; it can be run by hand on any build:
#   powershell -File tools/checks/check_no_dashboard_reference.ps1 [path\to\ReDefinition.dll]

param([string]$Dll)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Dll) { $Dll = Join-Path $repo 'build\ReDefinition.dll' }
if (-not (Test-Path -LiteralPath $Dll)) {
    Write-Host "check_no_dashboard_reference: '$Dll' does not exist."
    exit 1
}

$path = (Resolve-Path -LiteralPath $Dll).Path
# Reflection-only: the references are read from the metadata, nothing is resolved or run.
$assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($path)
$names = @($assembly.GetReferencedAssemblies() | ForEach-Object { $_.Name })
if ($names -contains 'ReDefinition.Dashboard') {
    Write-Host "check_no_dashboard_reference: FAILED. $path references ReDefinition.Dashboard."
    Write-Host 'The player build must not name the dashboard; see src/ReDefinition.csproj.'
    exit 1
}
Write-Host "check_no_dashboard_reference: OK. $path does not reference ReDefinition.Dashboard."
exit 0
