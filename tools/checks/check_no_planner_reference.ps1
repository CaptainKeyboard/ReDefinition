# The player build of ReDefinition.dll must not reference ReDefinition.Planner: ReDefinition works without the planner installed.
#
# The planner's types are named only by the developer probes (src/Probe), which are compiled in with PlannerProbes=true
# (src/ReDefinition.csproj) and left out of the player package. This reads the assembly's references and fails if the planner
# is among them. The release package runs it after the build; it can be run by hand on any build:
#   powershell -File tools/checks/check_no_planner_reference.ps1 [path\to\ReDefinition.dll]

param([string]$Dll)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Dll) { $Dll = Join-Path $repo 'build\ReDefinition.dll' }
if (-not (Test-Path -LiteralPath $Dll)) {
    Write-Host "check_no_planner_reference: '$Dll' does not exist."
    exit 1
}

$path = (Resolve-Path -LiteralPath $Dll).Path
# Reflection-only: the references are read from the metadata, nothing is resolved or run.
$assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($path)
$names = @($assembly.GetReferencedAssemblies() | ForEach-Object { $_.Name })
if ($names -contains 'ReDefinition.Planner') {
    Write-Host "check_no_planner_reference: FAILED. $path references ReDefinition.Planner."
    Write-Host 'The player build must not name the planner. Build it with -p:PlannerProbes=false (the release package does); see src/ReDefinition.csproj.'
    exit 1
}
Write-Host "check_no_planner_reference: OK. $path does not reference ReDefinition.Planner."
exit 0
