# Builds the shader bundle of ReDefinition Atmospheres, atmospheres.shaders, with Unity in batch mode and puts it into
# GameData\ReDefinition\Atmospheres\Shaders of the KSP folder (or into -Output). The bundle must come from exactly
# Unity 2019.4.18f1, KSP's version. The editor must not have the project open. KSP need not be stopped, but the
# bundle is read when the game starts.
#
#   tools\build_atmospheres_bundle.ps1 [-Ksp <KSP folder>] [-Unity <path to Unity.exe>] [-Output <bundle file>]
#
# The log is build\atmospheres-bundle.log; shader errors are in it. The exit code is Unity's: 1 where the build fails,
# and 2 where Unity exits 0 but a shader failed to compile (Unity then bundles the shader without a working variant).
param(
    [string]$Ksp,
    [string]$Unity = 'C:\Program Files\Unity\Editor 2019.4.18f1\Editor\Unity.exe',
    [string]$Output
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $PSBoundParameters.ContainsKey('Ksp')) { $Ksp = Get-KspRoot }
if (-not (Test-Path $Unity)) { throw "no Unity at $Unity" }
$repo = Split-Path -Parent $PSScriptRoot
$log = Join-Path $repo 'build\atmospheres-bundle.log'
New-Item -ItemType Directory -Force (Split-Path -Parent $log) | Out-Null

$arguments = @('-batchmode', '-quit', '-projectPath', (Join-Path $repo 'unity'),
    '-executeMethod', 'ReDefinition.EditorTools.AtmospheresBundleBuilder.BuildFromCommandLine',
    '-kspRoot', $Ksp, '-logFile', $log)
if ($Output) { $arguments += @('-output', $Output) }
$quoted = ($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
# Wait for Unity itself only. Start-Process -Wait also waits for every descendant, and Unity leaves its C# compiler server (VBCSCompiler.exe) behind, which idles
# for about ten minutes before it exits: every series run stood that long between the build and the game's start (seen 2026-11-19, 20:52 to 21:02).
$process = Start-Process -FilePath $Unity -ArgumentList $quoted -PassThru
# Reading the handle keeps the exit code readable after the process has ended (without it ExitCode is empty in Windows PowerShell).
$null = $process.Handle
$process.WaitForExit()
Get-Content $log | Select-String -Pattern 'ReDefinition Atmospheres:|error CS|Shader error|Compilation failed' | ForEach-Object { $_.Line }
if ($process.ExitCode -ne 0) { Write-Host "build_atmospheres_bundle: FAILED, exit code $($process.ExitCode); see $log"; exit $process.ExitCode }
$shaderErrors = @(Get-Content $log | Select-String -Pattern 'Shader error in ')
if ($shaderErrors.Count -gt 0) {
    Write-Host "build_atmospheres_bundle: FAILED, $($shaderErrors.Count) shader error(s) although Unity exited 0; see $log"
    exit 2
}
Write-Host "build_atmospheres_bundle: OK, see $log"
