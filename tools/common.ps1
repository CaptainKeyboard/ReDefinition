# Shared by the probe scripts in tools\ (flip_probe, parity_check, planet_shots, renderdoc_probe, unity_profile,
# vendor_probe) and, through checks\check_common.ps1, by the check scripts. It only defines functions: it sets no
# variable and installs nothing (the assembly resolver is check_common.ps1's). A script dot-sources it, so that
# the path does not depend on the working directory:
#
#   . (Join-Path $PSScriptRoot 'common.ps1')
#
# A script of tools\checks reaches it through check_common.ps1.

# The KSP folder: the KspRoot environment variable, as the build takes it (Directory.Build.props), else the Steam location.
function Get-KspRoot {
    if ($env:KspRoot) { $env:KspRoot } else { 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }
}

# Writes the proxy's ini to $target as $source with the given keys set: replaced where they stand, appended where not.
# $source and $target may be the same file.
function Set-IniKeys([string]$source, [string]$target, [hashtable]$set) {
    $lines = Get-Content $source
    foreach ($key in $set.Keys) {
        $found = $false
        $lines = $lines | ForEach-Object {
            if ($_ -match "^$key=") { $found = $true; "$key=$($set[$key])" } else { $_ }
        }
        if (-not $found) { $lines += "$key=$($set[$key])" }
    }
    Set-Content -Path $target -Value $lines -Encoding ASCII
}

# The two files a probe run changes and puts back, with their text as it was: the mod's settings.cfg and the probe
# save's persistent.sfs. Read before the first change, so that Restore-ProbeFiles can write them back in a finally.
function Backup-ProbeFiles([string]$kspRoot) {
    $settings = Join-Path $kspRoot 'GameData\ReDefinition\PluginData\settings.cfg'
    $save = Join-Path $kspRoot 'saves\ReDefinitionProbe\persistent.sfs'
    [pscustomobject]@{
        Settings = $settings
        Save = $save
        SettingsBefore = [IO.File]::ReadAllText($settings)
        SaveBefore = [IO.File]::ReadAllText($save)
    }
}

# Writes the text of Backup-ProbeFiles back to both files.
function Restore-ProbeFiles($files) {
    [IO.File]::WriteAllText($files.Settings, $files.SettingsBefore)
    [IO.File]::WriteAllText($files.Save, $files.SaveBefore)
}

# The raytracing set to Light in the settings.cfg that Backup-ProbeFiles read.
function Set-RaytracingLight($files) {
    $light = [regex]::Replace($files.SettingsBefore, '(?m)^(\s*raytracing\s*=\s*).*$', '${1}Light')
    [IO.File]::WriteAllText($files.Settings, $light)
}
