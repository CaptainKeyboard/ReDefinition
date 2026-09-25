# Plays an input dump through frame generation in the proxy harness and measures the
# result: the recording is cut to the replay window at once, the rest of the screen
# deleted, and the detail of rendered and generated frames printed.
#
#   tools\replay_fg.ps1 <dump folder> [NAME=VALUE ...]
#
# NAME=VALUE pairs set harness variables for this run, e.g. REDEFINITION_REPLAY_NO_JITTER=1.
# REDEFINITION_STREAMLINE_DIR must name Streamline's DLLs. The replay window has to
# stay in front while it runs; otherwise nothing is recorded.
param(
    [Parameter(Mandatory = $true)][string]$Dump,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Variables
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'build\DxgiProxy\Release'
$names = @('REDEFINITION_REPLAY_SHADOW_MOTION', 'REDEFINITION_REPLAY_NO_JITTER', 'REDEFINITION_REPLAY_NO_MOTION', 'REDEFINITION_REPLAY_STILL_CAMERA', 'REDEFINITION_REPLAY_FLAT_DEPTH')
foreach ($n in $names) { Remove-Item "Env:$n" -ErrorAction SilentlyContinue }
foreach ($v in $Variables) {
    $pair = $v.Split('=', 2)
    Set-Item "Env:$($pair[0])" $pair[1]
}
$env:REDEFINITION_HARNESS_REPLAY = $Dump

Push-Location $bin
try {
    $output = & .\ProxyHarness.exe 2>&1 | Out-String
} finally {
    Pop-Location
}
$match = [regex]::Match($output, 'recording: done: (\S+)')
if (-not $match.Success) {
    Write-Output ($output -split "`n" | Select-String 'replay|recording|front|FAIL')
    exit 1
}
$folder = $match.Groups[1].Value
python (Join-Path $PSScriptRoot 'replay_measure.py') $folder $Dump
