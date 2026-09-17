# Checks the graphics profile reader outside the game: KSP's own
# ConfigNode.Parse, then GraphicsProfile.FromConfigNode from the built DLL, on
# profiles with one mistake of each kind the reader has to survive.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_profile_parser.ps1
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
function Has($list, $pattern) { return @($list | Where-Object { $_ -like $pattern }).Count -eq 1 }

try {
    $parse = $ksp.GetType('ConfigNode').GetMethod('Parse', [Type[]]@([string]))
    $from = $mod.GetType('ReDefinition.Framework.GraphicsProfile').GetMethod('FromConfigNode')
    $callArgs = New-Object 'object[]' 2

    function Read-Profile($text) {
        $script:callArgs[0] = $parse.Invoke($null, [object[]]@($text)).GetNode('GRAPHICS_PROFILE')
        $script:callArgs[1] = (New-Object 'System.Collections.Generic.List[string]').PSObject.BaseObject
        return $from.Invoke($null, $script:callArgs)
    }

    # Mistakes in modules.
    $text = "GRAPHICS_PROFILE`n{`n name = balanced`n title = Balanced`n MODULE`n {`n  name = upscaler`n  enabled = true`n  quality = NativeAA`n }`n MODULE`n {`n  enabled = false`n }`n}"
    $p = Read-Profile $text
    $problems = $callArgs[1]
    $source = $callArgs[0]
    Expect "name and title read" ($p.Name -eq 'balanced' -and $p.Title -eq 'Balanced')
    Expect "the named module kept, with its values" ($p.Modules.Count -eq 1 -and $p.Modules['upscaler'].GetValue('quality') -eq 'NativeAA')
    Expect "the module without a name skipped and reported" (Has $problems '*MODULE without a name*')
    [void]$p.Modules['upscaler'].SetValue('quality', 'Performance', $false)
    Expect "a module is a copy: changing it leaves the source node alone" ($source.GetNode('MODULE').GetValue('quality') -eq 'NativeAA')

    # Mistakes around them.
    $text = "GRAPHICS_PROFILE`n{`n name = odd`n title =`n colour = blue`n inherit = defaults`n order = soon`n MODUL`n {`n  name = upscaler`n }`n MOD`n {`n  name = parallax`n  densityMultiplier = 0.5`n }`n}"
    $p = Read-Profile $text
    $problems = $callArgs[1]
    Expect "an empty title falls back to the name" ($p.Title -eq 'odd')
    Expect "an unknown key reported" (Has $problems "*unknown key 'colour'*")
    Expect "inherit is no key: every profile starts from the defaults" (Has $problems "*unknown key 'inherit'*")
    Expect "an order that is no whole number reported" (Has $problems "*order 'soon'*")
    Expect "an unknown node reported" (Has $problems "*unknown node 'MODUL'*")
    Expect "a mod's values stand in its registration: a MOD node in a profile is reported" (Has $problems "*unknown node 'MOD'*")

    # Mod registrations (docs/modders/registering-a-mod.md): what may be left out, and
    # each kind of mistake the reader has to survive.
    $fromMod = $mod.GetType('ReDefinition.Framework.ModRegistration').GetMethod('FromConfigNode')
    function Read-Mod($text) {
        $script:callArgs[0] = $parse.Invoke($null, [object[]]@($text)).GetNode('MOD_SETTINGS')
        $script:callArgs[1] = (New-Object 'System.Collections.Generic.List[string]').PSObject.BaseObject
        return $fromMod.Invoke($null, $script:callArgs)
    }

    $m = Read-Mod "MOD_SETTINGS`n{`n name = mymod`n detect = MyMod.Settings`n SETTING`n {`n  name = fancyEffects`n  member = MyMod.Settings.fancyEffects`n  default = True`n }`n}"
    $problems = $callArgs[1]
    $s = $m.Settings[0]
    Expect "the smallest registration reads without a problem" ($null -ne $m -and $problems.Count -eq 0 -and $m.Settings.Count -eq 1)
    Expect "left out: the title is the name, values set at every start" ($m.Title -eq 'mymod' -and "$($m.Saving)" -eq 'AtEveryStart' -and $null -eq $m.Tab)
    Expect "left out: a setting's title is its name, other, not shown, from the next scene, no slider or list" ($s.Title -eq 'fancyEffects' -and "$($s.Kind)" -eq 'Other' -and $null -eq $s.Row -and "$($s.TakesEffect)" -eq 'NextScene' -and $null -eq $s.Min -and $null -eq $s.Choices -and $s.Default -eq 'True' -and -not $s.Invert)

    $m = Read-Mod "MOD_SETTINGS`n{`n name = scatterer`n title = Scatterer`n detect = Scatterer.Scatterer`n save = Scatterer.Scatterer.Save`n ready = Scatterer.Scatterer.Instance`n tab = Planets`n BUILD`n {`n  name = volumetric`n  has = Scatterer.MainSettingsReadWrite.useRaymarchedCloudGodrays`n }`n SETTING`n {`n  name = m_fourierGridSize`n  member = Scatterer.MainSettingsReadWrite.m_fourierGridSize`n  kind = quality`n  row = Planets`n  order = 40`n  choices = 32, 64, 128`n  labels = Low\, fast, Medium, High`n  tooltip = One\nTwo`n  default = 128`n }`n SETTING`n {`n  name = oceanMeshResolution`n  member = Scatterer.MainSettingsReadWrite.oceanMeshResolution`n  min = 2`n  max = 16`n  whole = True`n  takesEffect = Restart`n }`n DEFAULTS`n {`n  build = volumetric`n  version = 0.908.0.0`n  m_fourierGridSize = 256`n }`n REQUIRES`n {`n  setting = ksp.terrainDetail`n  highest = True`n  lock = True`n  reason = Needs it.`n }`n}"
    $problems = $callArgs[1]
    $grid = $m.Setting('m_fourierGridSize')
    $mesh = $m.Setting('oceanMeshResolution')
    Expect "a full registration reads without a problem" ($null -ne $m -and $problems.Count -eq 0)
    Expect "save makes its values saved in the mod's files; ready and the Advanced tab read" ("$($m.Saving)" -eq 'InModFiles' -and $m.Ready -eq 'Scatterer.Scatterer.Instance' -and "$($m.Tab)" -eq 'Planets' -and $m.Builds.Count -eq 1 -and $m.Builds[0].Has -like '*useRaymarchedCloudGodrays')
    Expect "a list with labels, a comma kept inside a label, a line break in the tooltip, kind by name case aside" ($grid.Choices.Count -eq 3 -and $grid.Labels[0] -eq 'Low, fast' -and $grid.Tooltip -eq "One`nTwo" -and "$($grid.Kind)" -eq 'Quality' -and "$($grid.Row)" -eq 'Planets' -and $grid.Order -eq 40)
    Expect "a slider with whole numbers, taking effect after a restart" ($mesh.Min -eq 2 -and $mesh.Max -eq 16 -and $mesh.Whole -eq $true -and "$($mesh.TakesEffect)" -eq 'Restart')
    Expect "defaults for a build and a requirement read" ($m.Defaults[0].Build -eq 'volumetric' -and $m.Defaults[0].Values['m_fourierGridSize'] -eq '256' -and "$($m.Requirements[0].Test)" -eq 'Highest' -and $m.Requirements[0].Lock)

    $none = Read-Mod "MOD_SETTINGS`n{`n name = nodetect`n}"
    $problems = $callArgs[1]
    Expect "a registration without detect skipped, with a message" ($null -eq $none -and (Has $problems '*no detect -- skipped*'))
    $none = Read-Mod "MOD_SETTINGS`n{`n name = My-Mod`n detect = X`n}"
    $problems = $callArgs[1]
    Expect "a name with other characters skipped, with a message" ($null -eq $none -and (Has $problems '*may hold only lower-case*'))

    $m = Read-Mod "MOD_SETTINGS`n{`n name = odd`n detect = Odd.Settings`n title = One`n title = Two`n colour = blue`n saving = Sometimes`n tab = Sky`n EXTRA`n {`n }`n SETTING`n {`n  member = Odd.Settings.a`n }`n SETTING`n {`n  name = nomember`n }`n SETTING`n {`n  name = bad`n  member = Odd.Settings.bad`n  kind = Pretty`n  row = Sky`n  order = soon`n  takesEffect = Tomorrow`n  min = 1`n  invert = maybe`n  shade = red`n }`n SETTING`n {`n  name = list`n  member = Odd.Settings.list`n  choices = a, b, c`n  labels = A, B`n }`n SETTING`n {`n  name = list`n  member = Odd.Settings.list2`n }`n BUILD`n {`n  name = half`n }`n REQUIRES`n {`n  setting = ksp.x`n  equals = 1`n  atMost = 2`n  reason = r`n }`n REQUIRES`n {`n  setting = ksp.y`n  reason = r`n }`n REQUIRES`n {`n  setting = ksp.z`n  atMost = lots`n  reason = r`n }`n DEFAULTS`n {`n  ghost = 1`n }`n}"
    $problems = $callArgs[1]
    Expect "a key given twice: the last counts, and it is reported" ($m.Title -eq 'Two' -and (Has $problems "*'title' given 2 times*"))
    Expect "an unknown key and node, a saving and a tab it does not know reported and ignored" ((Has $problems "*unknown key 'colour'*") -and (Has $problems "*unknown node 'EXTRA'*") -and (Has $problems "*saving 'Sometimes'*") -and (Has $problems "*tab 'Sky'*") -and "$($m.Saving)" -eq 'AtEveryStart')
    Expect "a setting without a name or without a member left out, with messages" ((Has $problems '*SETTING without a name*') -and (Has $problems "*'nomember': no member*") -and $null -eq $m.Setting('nomember'))
    Expect "a bad kind, row, order, takesEffect, a lone min, a bad switch and an unknown key reported; the setting kept" ($null -ne $m.Setting('bad') -and (Has $problems "*kind 'Pretty'*") -and (Has $problems "*row 'Sky'*") -and (Has $problems "*order 'soon'*") -and (Has $problems "*takesEffect 'Tomorrow'*") -and (Has $problems '*min and max go together*') -and (Has $problems "*invert 'maybe'*") -and (Has $problems "*unknown key 'shade'*") -and "$($m.Setting('bad').Kind)" -eq 'Other')
    Expect "labels that do not match the choices left out; a setting twice: the last counts" ((Has $problems '*2 labels for 3 choices*') -and (Has $problems "*setting 'list' twice*") -and $m.Setting('list').Member -eq 'Odd.Settings.list2')
    Expect "a build without has, requirements with two tests, none, or a limit that is no number left out" ((Has $problems '*BUILD without name or has*') -and $m.Builds.Count -eq 0 -and @($problems | Where-Object { $_ -like '*needs one of equals*' }).Count -eq 2 -and (Has $problems "*atMost 'lots' cannot be tested*") -and $m.Requirements.Count -eq 0)
    Expect "defaults for a setting the registration does not have reported and dropped" ((Has $problems "*defaults: 'ghost', which is no setting*") -and -not $m.Defaults[0].Values.ContainsKey('ghost'))

    # What a registration gives the graphics profiles.
    $m = Read-Mod "MOD_SETTINGS`n{`n name = profiled`n detect = Profiled.Settings`n SETTING`n {`n  name = detail`n  member = Profiled.Settings.detail`n  kind = Quality`n  default = 3`n }`n SETTING`n {`n  name = glow`n  member = Profiled.Settings.glow`n  kind = Taste`n  default = True`n }`n PROFILE`n {`n  name = low`n  detail = 1`n  detail = 2`n  glow = False`n  ghost = 1`n  INNER`n  {`n  }`n }`n PROFILE`n {`n  name = low`n  build = large`n  detail = 0`n }`n PROFILE`n {`n  detail = 4`n }`n}"
    $problems = $callArgs[1]
    Expect "profile blocks read with their name and build" ($m.Profiles.Count -eq 2 -and $m.Profiles[0].Name -eq 'low' -and $null -eq $m.Profiles[0].Build -and $m.Profiles[1].Build -eq 'large' -and $m.Profiles[1].Values['detail'] -eq '0')
    Expect "a key given twice in a profile block: the last counts, and it is reported" ($m.Profiles[0].Values['detail'] -eq '2' -and (Has $problems "*'detail' twice*"))
    Expect "a taste setting and a key that is no setting left out of a profile, with messages" (-not $m.Profiles[0].Values.ContainsKey('glow') -and -not $m.Profiles[0].Values.ContainsKey('ghost') -and (Has $problems "*'glow' is no quality setting*") -and (Has $problems "*'ghost', which is no setting*"))
    Expect "a node inside a profile block and a block without a name reported" ((Has $problems "*node 'INNER' inside a profile block*") -and (Has $problems '*PROFILE without a name*'))
    Expect "a block for a build no BUILD names reported" (Has $problems "*no BUILD is named 'large'*")
    $m = Read-Mod "MOD_SETTINGS`n{`n name = every`n detect = Every.Settings`n SETTING`n {`n  name = glow`n  member = Every.Settings.glow`n  kind = Taste`n }`n ALL_PROFILES`n {`n  glow = False`n  ghost = 1`n }`n}"
    $problems = $callArgs[1]
    Expect "a block for every profile keeps a setting of any kind and reports one the registration does not have" ($m.AllProfiles.Count -eq 1 -and $m.AllProfiles[0].Values['glow'] -eq 'False' -and -not $m.AllProfiles[0].Values.ContainsKey('ghost') -and (Has $problems "*every profile: 'ghost', which is no setting*"))

    # What a registration says about builds of another shape.
    $m = Read-Mod "MOD_SETTINGS`n{`n name = shaped`n detect = Shaped.Settings`n needs = Shaped.Settings.groupA, , Shaped.Settings.groupB`n SETTING`n {`n  name = a`n  member = Shaped.Settings.a`n  required = True`n  optional = False`n }`n SETTING`n {`n  name = b`n  member = Shaped.Settings.b`n  optional = True`n }`n SETTING`n {`n  name = c`n  member = Shaped.Settings.c`n  optional = only some builds`n  required = True`n }`n}"
    $problems = $callArgs[1]
    Expect "needs read as a list, an empty entry reported" ($m.Needs.Count -eq 2 -and $m.Needs[1] -eq 'Shaped.Settings.groupB' -and (Has $problems '*empty entry in needs*'))
    Expect "required read; optional = False is none, optional = True kept" ($m.Setting('a').Required -and $null -eq $m.Setting('a').Optional -and $m.Setting('b').Optional -eq 'True')
    Expect "optional with a reason and required together reported, required ignored" ((Has $problems '*optional and required go against each other*') -and -not $m.Setting('c').Required -and $m.Setting('c').Optional -eq 'only some builds' -and $problems.Count -eq 2)

    # No name at all.
    $none = Read-Profile "GRAPHICS_PROFILE`n{`n title = x`n}"
    $problems = $callArgs[1]
    Expect "a profile without a name refused, with a message" ($null -eq $none -and (Has $problems '*without a name -- skipped*'))

    # The profiles the package ships, read from their files as KSP reads them.
    $load = $ksp.GetType('ConfigNode').GetMethod('Load', [Type[]]@([string]))
    $shipped = [IO.Directory]::GetFiles([IO.Path]::Combine($repo, 'GameData\ReDefinition\Profiles'), '*.cfg')
    $shippedProfiles = 0
    foreach ($file in $shipped) {
        $root = $load.Invoke($null, [object[]]@($file))
        foreach ($node in $root.GetNodes('GRAPHICS_PROFILE')) {
            $shippedProfiles++
            $callArgs[0] = $node
            $callArgs[1] = (New-Object 'System.Collections.Generic.List[string]').PSObject.BaseObject
            $shippedProfile = $from.Invoke($null, $callArgs)
            $name = if ($null -ne $shippedProfile) { $shippedProfile.Name } else { '?' }
            Expect ("shipped profile '" + $name + "' reads without a problem") ($null -ne $shippedProfile -and $callArgs[1].Count -eq 0)
        }
    }
    # Profiles, not files: a file whose root node is misspelt holds none.
    Expect "the package ships at least one profile" ($shippedProfiles -gt 0)

    # The deploy removes only files with this prefix from the game before it
    # copies, so a shipped profile without it would outlive its removal here.
    $unprefixed = @($shipped | Where-Object { -not [IO.Path]::GetFileName($_).StartsWith('ReDefinition-') })
    Expect "every shipped profile file carries the prefix ReDefinition-" ($unprefixed.Count -eq 0)
}
catch {
    "FAIL  " + $_.Exception.GetBaseException().Message
    $failures++
}

if ($failures -eq 0) { "All checks passed." } else { "$failures check(s) failed."; exit 1 }
