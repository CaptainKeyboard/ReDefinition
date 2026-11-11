# The player build of ReDefinition.dll must not reference a mod of its own (ReDefinition.Planner, ReDefinition.Dashboard,
# ReDefinition.Atmospheres): ReDefinition works without them installed, and they work without it.
#
# Each of them is built before ReDefinition so that its DLL lands in the output beside it, and is referenced by nothing
# (src/ReDefinition.csproj, ReferenceOutputAssembly=false). The planner's types are named only by the developer probes (src/Probe),
# compiled in with PlannerProbes=true and left out of the player package.
#
# For every name this reads the references of ReDefinition.dll and fails if the mod is among them. It also reads the mod's own DLL,
# beside ReDefinition.dll, which must name neither ReDefinition (the main mod), Harmony nor ZTheme. The release package runs it after
# the build; it can be run by hand on any build:
#   powershell -File tools/checks/check_no_reference.ps1 [-Name Planner,Dashboard,Atmospheres] [-Dll path\to\ReDefinition.dll]

param(
    [string[]]$Name = @('Planner', 'Dashboard', 'Atmospheres'),
    [string]$Dll
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Dll) { $Dll = Join-Path $repo 'build\ReDefinition.dll' }
if (-not (Test-Path -LiteralPath $Dll)) {
    Write-Host "check_no_reference: '$Dll' does not exist."
    exit 1
}

function Get-References($file) {
    # Reflection-only: the references are read from the metadata, nothing is resolved or run.
    $assembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Resolve-Path -LiteralPath $file).Path)
    return @($assembly.GetReferencedAssemblies() | ForEach-Object { $_.Name })
}

$path = (Resolve-Path -LiteralPath $Dll).Path
$mainReferences = Get-References $path
$folder = Split-Path -Parent $path
$failed = $false

foreach ($mod in ($Name | ForEach-Object { $_ -split ',' } | Where-Object { $_ })) {
    $assemblyName = "ReDefinition.$mod"
    if ($mainReferences -contains $assemblyName) {
        Write-Host "check_no_reference: FAILED. $path references $assemblyName."
        if ($mod -eq 'Planner') {
            Write-Host 'The player build must not name the planner. Build it with -p:PlannerProbes=false (the release package does); see src/ReDefinition.csproj.'
        } else {
            Write-Host "The player build must not name the $($mod.ToLower()); see src/ReDefinition.csproj."
        }
        $failed = $true
        continue
    }

    $own = Join-Path $folder "$assemblyName.dll"
    if (-not (Test-Path -LiteralPath $own)) {
        Write-Host "check_no_reference: FAILED. $own does not exist, so the references of $assemblyName cannot be read."
        $failed = $true
        continue
    }
    $bad = @(Get-References $own | Where-Object { $_ -eq 'ReDefinition' -or $_ -eq '0Harmony' -or $_ -like '*ZTheme*' })
    if ($bad.Count -gt 0) {
        Write-Host "check_no_reference: FAILED. $own references $($bad -join ', ')."
        $failed = $true
        continue
    }
    Write-Host "check_no_reference: OK. $path does not reference $assemblyName, and $assemblyName.dll names neither ReDefinition, Harmony nor ZTheme."
}

if ($failed) { exit 1 }
exit 0
