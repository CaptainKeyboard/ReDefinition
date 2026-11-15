# Registers the scheduled task 'ReDefinition Nightly Mod Update': every day at 03:00 local time it runs tools\nightly\modsync-nightly.ps1.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\nightly\register-modsync-task.ps1 [-DryRun] [-Unregister]
#
# The task runs only while the user is logged on (no password stored, no elevation), starts as soon as it can where 03:00 was missed
# (StartWhenAvailable), does not start a second instance while one runs (IgnoreNew) and is stopped after 3 hours.
# -DryRun prints the task definition and registers nothing. -Unregister removes the task.

param(
    [switch]$DryRun,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$name = 'ReDefinition Nightly Mod Update'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script = Join-Path $repo 'tools\nightly\modsync-nightly.ps1'
$arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $script + '"'

if ($Unregister) {
    if ($DryRun) { Write-Output "DRYRUN would: Unregister-ScheduledTask '$name'"; exit 0 }
    Unregister-ScheduledTask -TaskName $name -Confirm:$false
    Write-Output "removed '$name'"
    exit 0
}

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments -WorkingDirectory $repo
$trigger = New-ScheduledTaskTrigger -Daily -At '03:00'
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 3)
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited

if ($DryRun) {
    Write-Output "DRYRUN would register '$name':"
    Write-Output ("  action:    powershell.exe " + $arguments + " (working directory " + $repo + ")")
    Write-Output '  trigger:   daily at 03:00'
    Write-Output ("  settings:  StartWhenAvailable=" + $settings.StartWhenAvailable + ", MultipleInstances=" + $settings.MultipleInstances + ", ExecutionTimeLimit=" + $settings.ExecutionTimeLimit)
    Write-Output ("  principal: " + $principal.UserId + ", logon type " + $principal.LogonType + ", run level " + $principal.RunLevel + " (only while logged on, no password, no admin)")
    exit 0
}

Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description 'ReDefinition: checks the coupled mods for new releases, reviews the source changes, adapts on a branch and installs safe updates.' | Out-Null
Write-Output "registered '$name' (daily 03:00). Run it by hand: Start-ScheduledTask -TaskName '$name'"
