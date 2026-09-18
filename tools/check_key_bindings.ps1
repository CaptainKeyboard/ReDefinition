# Checks the key bindings outside the game: KeyCombination from the built DLL on
# the texts the mods and KSP write, the shipped registrations' KEY blocks, and
# KSP's own bindings read from GameSettings.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_key_bindings.ps1
#
# Runs in Windows PowerShell 5.1 and in PowerShell 7. The KSP folder is taken
# from the KspRoot environment variable, as in Directory.Build.props, and
# defaults to the Steam location.

$repo = Split-Path -Parent $PSScriptRoot
$kspRoot = if ($env:KspRoot) { $env:KspRoot } else { 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }
$managed = [IO.Path]::Combine($kspRoot, 'KSP_x64_Data\Managed')
$harmony = [IO.Path]::Combine($kspRoot, 'GameData\000_Harmony')

# Plain .NET calls only, no cmdlets, and no re-entry: a resolver that calls a
# cmdlet can make PowerShell resolve an assembly again from inside itself,
# which overflows the stack in Windows PowerShell 5.1.
$global:resolving = $false
[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($s, $e)
    if ($global:resolving) { return $null }
    $n = ($e.Name -split ',')[0]
    if ($n.EndsWith('.resources')) { return $null }
    $global:resolving = $true
    try {
        foreach ($d in @($managed, $harmony)) {
            $p = [IO.Path]::Combine($d, $n + '.dll')
            if ([IO.File]::Exists($p)) { return [Reflection.Assembly]::LoadFrom($p) }
        }
        return $null
    }
    finally { $global:resolving = $false }
})

$ksp = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($managed, 'Assembly-CSharp.dll'))
# From the file, as check_bundled_mods.ps1 loads it. Microsoft Defender has blocked a script that
# loads an assembly from bytes in memory as Trojan:Win32/ClickFix.IIN!MTB.
$mod = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($repo, 'build\ReDefinition.dll'))

$failures = 0
function Expect($what, $ok) {
    if ($ok) { "PASS  $what" } else { "FAIL  $what"; $script:failures++ }
}

try {
    $combination = $mod.GetType('ReDefinition.Settings.KeyCombination')
    $parse = $combination.GetMethod('Parse', [Type[]]@([string]))
    $isText = $combination.GetMethod('IsText', [Type[]]@([string]))

    function Text($s) { return $parse.Invoke($null, [object[]]@($s)).ToString() }

    # What the mods and KSP write, read and written back as the window shows it.
    Expect "Scatterer's spelling reads back" ((Text 'F10') -eq 'F10')
    Expect "KSP's None reads back" ((Text 'None') -eq 'None')
    Expect "modifiers come in their written order" ((Text 'RightShift+RightControl+U') -eq 'RightControl+RightShift+U')
    Expect "a modifier alone is no binding" (-not $isText.Invoke($null, [object[]]@('LeftAlt')))
    Expect "the game's own mouse buttons are no binding" ((-not $isText.Invoke($null, [object[]]@('Mouse0'))) -and (-not $isText.Invoke($null, [object[]]@('Mouse1'))))
    Expect "the other mouse buttons are" ($isText.Invoke($null, [object[]]@('Mouse2')))

    # The shipped registrations: every KEY block a binding the reader takes.
    $parseNode = $ksp.GetType('ConfigNode').GetMethod('Parse', [Type[]]@([string]))
    $fromNode = $mod.GetType('ReDefinition.Settings.ModRegistration').GetMethod('FromConfigNode')
    $bindings = 0
    $bad = @()
    foreach ($file in [IO.Directory]::GetFiles([IO.Path]::Combine($repo, 'GameData\ReDefinition\Mods'), '*.cfg')) {
        $node = $parseNode.Invoke($null, [object[]]@([IO.File]::ReadAllText($file))).GetNode('MOD_SETTINGS')
        if ($null -eq $node) { continue }
        $problems = (New-Object 'System.Collections.Generic.List[string]').PSObject.BaseObject
        $registration = $fromNode.Invoke($null, [object[]]@($node, $problems))
        foreach ($setting in $registration.Settings) {
            if (-not $setting.IsBinding) { continue }
            $bindings++
            if ($null -eq $setting.Default -or -not $isText.Invoke($null, [object[]]@($setting.Default))) {
                $bad += ([IO.Path]::GetFileName($file) + ': ' + $setting.Name)
            }
        }
        $keyProblems = @($problems | Where-Object { $_ -like '*binding*' })
        if ($keyProblems.Count -gt 0) { $bad += ([IO.Path]::GetFileName($file) + ': ' + ($keyProblems -join '; ')) }
    }
    Expect "the shipped registrations hold key bindings" ($bindings -gt 0)
    Expect "every shipped binding has a default the reader takes" ($bad.Count -eq 0)
    if ($bad.Count -gt 0) { $bad | ForEach-Object { "      $_" } }

    # KSP's own bindings: the fields the Keys tab reads.
    $keyBinding = $ksp.GetType('KeyBinding')
    $primary = $keyBinding.GetField('primary')
    $switchState = $keyBinding.GetField('switchState')
    $code = $primary.FieldType.GetField('code')
    Expect "a KeyBinding holds primary, secondary and switchState" (
        $null -ne $primary -and $null -ne $keyBinding.GetField('secondary') -and $null -ne $switchState)
    Expect "its keys hold a KeyCode" ($null -ne $code -and $code.FieldType.Name -eq 'KeyCode')
    $fields = @($ksp.GetType('GameSettings').GetFields([Reflection.BindingFlags]'Public,Static') |
        Where-Object { $_.FieldType -eq $keyBinding })
    Expect "GameSettings holds KSP's bindings (at least 100)" ($fields.Count -ge 100)

    $readable = $mod.GetType('ReDefinition.Window.KspKeyBindings').GetMethod('Readable', [Reflection.BindingFlags]'NonPublic,Static')
    if ($null -eq $readable) {
        $readable = $mod.GetType('ReDefinition.Window.KspKeyBindings').GetMethod('Readable', [Reflection.BindingFlags]'Public,Static')
    }
    $named = @($fields | Where-Object { [string]::IsNullOrEmpty($readable.Invoke($null, [object[]]@($_.Name))) })
    Expect "every one of them has a name for its row" ($named.Count -eq 0)

    # KSP's defaults, read as the reset reads them: SetDefaultValues gives every binding
    # a new KeyBinding, and what it holds is what KSP ships.
    [void]$ksp.GetType('GameSettings').GetMethod('SetDefaultValues').Invoke($null, $null)
    $unset = @($fields | Where-Object { $null -eq $_.GetValue($null) })
    Expect "SetDefaultValues gives every binding a default" ($unset.Count -eq 0)
    $pitch = $ksp.GetType('GameSettings').GetField('PITCH_DOWN').GetValue($null)
    $stage = $ksp.GetType('GameSettings').GetField('LAUNCH_STAGES').GetValue($null)
    Expect "KSP's defaults are the known ones (Pitch down W, Launch stages Space)" (
        "$($code.GetValue($primary.GetValue($pitch)))" -eq 'W' -and "$($code.GetValue($primary.GetValue($stage)))" -eq 'Space')
}
catch {
    "FAIL  " + $_.Exception.GetBaseException().Message
    $failures++
}

if ($failures -eq 0) { "All checks passed." } else { "$failures check(s) failed."; exit 1 }
