# Checks the bundled mods outside the game: loads the installed graphics mods,
# then reads every registration in GameData\ReDefinition\Mods and builds its mod
# with the built DLL -- the reader and the registry the game runs -- and
# reports whether it found every member it needs, which settings it offers,
# the rows, defaults and requirements it derives from them, and whether the
# assembly it takes toolbar buttons from is the one the mod's button lives in.
# Nothing is read from or written to the mods: their objects do not exist
# outside the game.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_bundled_mods.ps1
#
# Runs in Windows PowerShell 5.1 and in PowerShell 7. The KSP folder is taken
# from the KspRoot environment variable, as in Directory.Build.props, and
# defaults to the Steam location.

$repo = Split-Path -Parent $PSScriptRoot
$kspRoot = if ($env:KspRoot) { $env:KspRoot } else { 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }
$managed = [IO.Path]::Combine($kspRoot, 'KSP_x64_Data\Managed')
$gameData = [IO.Path]::Combine($kspRoot, 'GameData')

# Plain .NET calls only, no cmdlets, and no re-entry (see check_profile_parser.ps1).
$global:resolving = $false
[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($s, $e)
    if ($global:resolving) { return $null }
    $n = ($e.Name -split ',')[0]
    if ($n.EndsWith('.resources')) { return $null }
    $global:resolving = $true
    try {
        $p = [IO.Path]::Combine($managed, $n + '.dll')
        if ([IO.File]::Exists($p)) { return [Reflection.Assembly]::LoadFrom($p) }
        foreach ($f in [IO.Directory]::GetFiles($gameData, $n + '.dll', [IO.SearchOption]::AllDirectories)) {
            return [Reflection.Assembly]::LoadFrom($f)
        }
        return $null
    }
    finally { $global:resolving = $false }
})

$failures = 0
$lastThrow = $null
function Expect($what, $ok) {
    # A method that threw can never be a pass, whatever the assertion made of
    # the $null it returned: the net belongs here, not at each call site.
    if ($ok -and -not $script:lastThrow) {
        "PASS  $what"
    } else {
        "FAIL  $what"
        # The cause on the same stream as the failure it explains, so a
        # redirected log keeps them together.
        if ($script:lastThrow) { "      $script:lastThrow" }
        $script:failures++
    }
    $script:lastThrow = $null
}

[void][Reflection.Assembly]::LoadFrom([IO.Path]::Combine($managed, 'Assembly-CSharp.dll'))
[void][Reflection.Assembly]::LoadFrom([IO.Path]::Combine($managed, 'UnityEngine.CoreModule.dll'))

# The mods, as KSP would load them: every plugin assembly of their folders.
$mods = @{
    'scatterer'  = @('Scatterer');
    'eve'        = @('EnvironmentalVisualEnhancements\Plugins');
    'parallax'   = @('ParallaxContinued\Plugins');
    'firefly'    = @('Firefly\Plugins');
    'deferred'   = @('zzz_Deferred');
    'tufx'       = @('TUFX\Plugins', '001_ToolbarControl\Plugins');
    'waterfall'  = @('Waterfall\Plugins');
    'distantobject' = @('DistantObject\Plugins');
    # The worked example's mod (docs\modders\examples).
    'trajectories' = @('Trajectories\Plugins');
}
foreach ($folders in $mods.Values) {
    foreach ($folder in $folders) {
        $dir = [IO.Path]::Combine($gameData, $folder)
        if (-not [IO.Directory]::Exists($dir)) { continue }
        foreach ($f in [IO.Directory]::GetFiles($dir, '*.dll')) {
            try { [void][Reflection.Assembly]::LoadFrom($f) } catch { "note  $f not loaded: $($_.Exception.Message)" }
        }
    }
}

$mod = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($repo, 'build\ReDefinition.dll'))
$loaded = [AppDomain]::CurrentDomain.GetAssemblies()
$sflags = [Reflection.BindingFlags]'Static, Public, NonPublic'

# A property the framework keeps internal: PowerShell sees public members only.
function Internal($object, $name) {
    $property = $object.GetType().GetProperty($name, [Reflection.BindingFlags]'Instance, Public, NonPublic')
    if ($null -eq $property) { return $null }
    return $property.GetValue($object, $null)
}

# The class each mod's toolbar button is made by, from its source.
$buttonClass = @{
    'scatterer' = 'ToolbarButton';
    'eve'       = 'GlobalEVEManager';
    'parallax'  = 'ToolbarMenu';
    'firefly'   = 'WindowManager';
    'distantobject' = 'SettingsGui';
}

# What each registration is known to leave out on this install, by setting name.
$expectedDrops = @{
    'scatterer' = @('quarterResScattering');   # Scatterer does not save it
    # AERO_FX_QUALITY while Firefly is loaded, which sets it itself; the
    # reflection refresh while Deferred caps it -- outside the game its cap
    # cannot be read and counts as on, its own default.
    'ksp'       = @('AERO_FX_QUALITY', 'REFLECTION_PROBE_REFRESH_MODE');
    # Its reflection-probe caps, with which it would lower KSP's own settings for good.
    'deferred'  = @('capReflectionProbeRefreshRate', 'capReflectionProbeResolution');
}

# Settings one build of a mod does not have, left out without a word (`optional = True`).
$buildAbsent = @{
    'scatterer.volumetric' = @('oceanCraftWaveInteractionsOverrideWaterCrashTolerance', 'buoyancyCrashToleranceMultOverride',
                               'oceanCraftWaveInteractionsOverrideDrag');
    'scatterer.public'     = @('oceanScreenSpaceReflections', 'useRaymarchedCloudGodrays', 'useRaymarchedTerrainGodrays',
                               'raymarchedGodraysStepCount', 'raymarchedGodraysScreenshotDenoisingIterations');
}

# KSP's own ConfigNode reads every file, as the GameDatabase reads it.
$configNodeType = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('ConfigNode', $false) } | Where-Object { $_ } | Select-Object -First 1
$loadNode = $configNodeType.GetMethod('Load', [Type[]]@([string]))

# The registrations, read and built as the game reads and builds them
# (ModRegistry): a problem in any of them is a failure here, since the game only
# logs it.
$ibundled = $mod.GetType('ReDefinition.Settings.IBundledMod', $true)
# The mods as an IBundledMod[] -- an IList<IBundledMod>, as the framework's
# methods take them.
function As-Mods($items) {
    $list = @($items)
    $array = [Array]::CreateInstance($ibundled, $list.Count)
    for ($i = 0; $i -lt $list.Count; $i++) { $array.SetValue($list[$i].PSObject.BaseObject, $i) }
    return ,$array
}
$registryType = $mod.GetType('ReDefinition.Settings.ModRegistry', $true)
$readRegistrations = $registryType.GetMethod('Read', $sflags)
$buildRegistered = $registryType.GetMethod('Build', $sflags)
$registrations = @()
$registered = @()
$installedList = As-Mods @()
$registeredList = As-Mods @()
$byId = New-Object 'System.Collections.Generic.Dictionary[string,object]'
try {
    $registrationNodes = (New-Object 'System.Collections.Generic.List[object]').PSObject.BaseObject
    foreach ($file in [IO.Directory]::GetFiles([IO.Path]::Combine($repo, 'GameData\ReDefinition\Mods'), '*.cfg')) {
        $fileRoot = $loadNode.Invoke($null, [object[]]@($file))
        foreach ($n in $fileRoot.GetNodes('MOD_SETTINGS')) { $registrationNodes.Add($n) }
    }
    $registrationProblems = (New-Object 'System.Collections.Generic.List[string]').PSObject.BaseObject
    # The list itself for Build: wrapped in an array it is no IList of registrations.
    $registrationList = $readRegistrations.Invoke($null, [object[]]@($registrationNodes, $registrationProblems))
    $registrations = @($registrationList)
    $registered = @($buildRegistered.Invoke($null, [object[]]@($registrationList, $registrationProblems)))
    $installed = @()
    foreach ($m in $registered) {
        $byId[$m.Id] = $m
        if ($m.IsInstalled) { $installed += $m }
    }
    $registeredList = As-Mods $registered
    $installedList = As-Mods $installed
    foreach ($p in $registrationProblems) { "      $p" }
    Expect "every registration reads and builds without a problem ($($registrations.Count) registrations)" (($registrationProblems.Count -eq 0) -and ($registered.Count -ge 1))
}
catch {
    $script:lastThrow = "registrations threw: " + $_.Exception.GetBaseException().Message
    Expect "the registrations could be read and built" $false
}

function Registration-Of($id) {
    foreach ($r in $registrations) { if ($r.Name -ceq $id) { return $r } }
    return $null
}
function Setting-Of($id, $name) {
    $r = Registration-Of $id
    if ($null -eq $r) { return $null }
    return $r.Setting($name)
}

$offered = @()
$knownDrops = @()
$valueControls = @()
foreach ($m in $registered) {
    $id = $m.Id
    try {
        if (-not (Internal $m 'Detected')) {
            if (@($m.DroppedMembers).Count -gt 0) { foreach ($d in @($m.DroppedMembers)) { "note  $($m.ModName) does not bundle: $d" } }
            else { "note  $($m.ModName) is not installed here: its registration is read, not checked against it" }
            continue
        }
        Expect "$($m.ModName) is bundled: its registration finds every member it needs" $m.IsInstalled
        # What the settings window would tell the player is missing: a row this
        # build cannot give, or a setting it saves that no registration names.
        $notShown = @($m.RowsNotShown) + @($m.SettingsNotKnown | ForEach-Object { "$_ (not registered)" })
        Expect "$($m.ModName): nothing it has is missing from the window" ($notShown.Count -eq 0)
        foreach ($n in $notShown) { "      $n" }
        # Settings this build of the mod does not offer, or that another mod
        # holds for itself, listed: a dropped member is no failure -- that row
        # goes, the rest of the mod stays bundled -- and outside the game this is
        # where it shows.
        $dropped = @($m.DroppedMembers)
        foreach ($d in $dropped) { "note  $($m.ModName) does not bundle: $d" }
        # Only the known ones pass: a new one is a change to look at.
        $expected = @()
        if ($expectedDrops.ContainsKey($id)) { $expected = $expectedDrops[$id] }
        # Volumetric Clouds' members, which the public build of Scatterer has not.
        if ($id -eq 'scatterer' -and $m.Build -eq 'public') {
            $expected += @('oceanScreenSpaceReflections', 'useRaymarchedCloudGodrays', 'raymarchedGodraysStepCount')
        }
        $droppedNames = @($dropped | ForEach-Object { ($_ -split ' -- ')[0] })
        # '<=' is the reference side, $droppedNames: a member dropped that is not
        # known. A known drop that did not happen passes.
        $unexpected = @(Compare-Object $droppedNames $expected | Where-Object { $_.SideIndicator -eq '<=' } |
            ForEach-Object { $_.InputObject })
        if ($unexpected.Count -gt 0) { $script:lastThrow = "unexpected: " + ($unexpected -join ', ') }
        Expect "$($m.ModName) leaves out only what it is known to leave out" ($unexpected.Count -eq 0)
        $keys = @()
        foreach ($s in $m.Settings) {
            $control = "$($s.Control)"
            if ($control -eq 'Choice' -and $s.ChoicesSource) { $control += ' [from the running mod]' }
            elseif ($control -eq 'Choice') { $control += ' [' + ($s.Choices -join ' ') + ']' }
            # Whole numbers or not: the one slider property a change can turn
            # over without anything outside the game noticing.
            if ($control -eq 'Slider') { $control += " [$($s.Min) .. $($s.Max)" + $(if ($s.WholeNumbers) { ', whole' } else { ', fractions' }) + ']' }
            $entry = $m.Registration.Setting($s.Key.Substring($id.Length + 1))
            $where = 'not shown'
            if ($null -ne $entry -and $null -ne $entry.Row) {
                $where = "$($entry.Row) $($entry.Order)"
                if ($entry.RowUnless) { $where += " (without $($entry.RowUnless))" }
            }
            $keys += "        $($s.Key)  $where  $($s.Kind)  $control  $($s.Window)"
            if ($control -eq 'Value') { $valueControls += $s.Key }
        }
        "      build $(if ($m.Build) { $m.Build } else { 'any' }), version $(if ($m.Version) { $m.Version } else { 'unknown' })"
        "      $($m.Settings.Count) setting(s):"
        $keys
        $offered += @($m.Settings | ForEach-Object { $_.Key })
        foreach ($e in $expected) { $knownDrops += "$id.$e" }
        if ($buttonClass.ContainsKey($id)) {
            $asm = $loaded | Where-Object { $_.GetName().Name -ieq $m.ButtonAssembly } | Select-Object -First 1
            $has = $false
            if ($asm) { try { $has = @($asm.GetTypes() | Where-Object { $_.Name -eq $buttonClass[$id] }).Count -gt 0 } catch { $has = $false } }
            Expect "$($m.ModName) takes buttons from '$($m.ButtonAssembly)', where $($buttonClass[$id]) lives" $has
        }
        elseif ($m.ToolbarControlNamespace) {
            # The namespace is a string TUFX passes at run time (RegisterMod("TUFX"), decompiled);
            # outside the game only the registration's side can be seen.
            Expect "$($m.ModName) looks for the ToolbarControl namespace TUFX registers with" ($m.ToolbarControlNamespace -eq 'TUFX')
        }
        else {
            Expect "$($m.ModName) has no button to take over" ($m.ButtonAssembly -eq $null)
        }
    }
    catch {
        $script:lastThrow = $_.Exception.ToString().Split("`n")[0]
        Expect "$id could be checked" $false
    }
}

# The rows, as WindowLayout derives them from the registrations for the mods
# installed here: a row a registration names that its mod does not offer would
# be missing without a word -- unless it is listed as left out here -- and a
# setting checked by its type only has no control the window can draw.
try {
    $categoryType = $mod.GetType('ReDefinition.Settings.SettingCategory', $true)
    $rowsIn = @($mod.GetType('ReDefinition.Settings.WindowLayout', $true).GetMethods($sflags) |
        Where-Object { $_.Name -eq 'In' -and $_.GetParameters().Count -eq 2 })[0]
    $shown = @()
    foreach ($tab in @('General', 'ShadowsAndReflections', 'Planets', 'Effects')) {
        $rows = @($rowsIn.Invoke($null, [object[]]@([Enum]::Parse($categoryType, $tab), $installedList)))
        "      ${tab}: " + (@($rows | ForEach-Object { $_.Key }) -join ', ')
        $shown += $rows
    }
    $named = @()
    foreach ($r in $registrations) {
        if (-not $byId.ContainsKey($r.Name) -or -not $byId[$r.Name].IsInstalled) { continue }
        foreach ($s in $r.Settings) { if ($null -ne $s.Row) { $named += "$($r.Name).$($s.Name)" } }
    }
    $unlisted = @($named | Where-Object { $offered -notcontains $_ -and $knownDrops -notcontains $_ })
    if ($unlisted.Count -gt 0) { $script:lastThrow = "not offered: " + ($unlisted -join ', ') }
    Expect "every row a registration names is a setting its mod offers here ($($named.Count) rows named, $($shown.Count) shown)" ($unlisted.Count -eq 0 -and $shown.Count -ge 1)
    $undrawable = @($shown | Where-Object { "$($_.Control)" -eq 'Value' } | ForEach-Object { $_.Key })
    if ($undrawable.Count -gt 0) { $script:lastThrow = "no control to draw: " + ($undrawable -join ', ') }
    Expect "no row is a setting without a control the window can draw" ($undrawable.Count -eq 0)
}
catch {
    $script:lastThrow = "rows threw: " + $_.Exception.GetBaseException().Message
    Expect "the rows could be derived" $false
}

# Every setting a registration names is offered by its mod here -- but those the
# installed build does not have, and those listed as left out here.
$inventory = @()
foreach ($r in $registrations) {
    if (-not $byId.ContainsKey($r.Name) -or -not $byId[$r.Name].IsInstalled) { continue }
    foreach ($s in $r.Settings) { $inventory += "$($r.Name).$($s.Name)" }
}
$notOffered = @()
foreach ($key in $inventory) {
    if ($offered -contains $key -or $knownDrops -contains $key) { continue }
    $modId = $key.Substring(0, $key.IndexOf('.'))
    $member = $key.Substring($modId.Length + 1)
    $absent = "$modId.$($byId[$modId].Build)"
    if ($buildAbsent.ContainsKey($absent) -and $buildAbsent[$absent] -contains $member) { continue }
    $notOffered += $key
}
if ($notOffered.Count -gt 0) { $script:lastThrow = "not offered: " + ($notOffered -join ', ') }
Expect "every setting the registrations name is offered by its mod here ($($inventory.Count) named, $($offered.Count) offered)" ($notOffered.Count -eq 0)

# The shipped profiles and the profile values in the registrations: every profile
# reads without a problem, and its modules' values are quality settings their
# modules take; every PROFILE block names a shipped profile and sets quality
# settings of its registration, with values the game takes where its build is the
# one installed -- ProfileApplier.Refusal is called here, the method the game asks.
# High sets only its deviations from the defaults. A mod not installed here builds
# no settings: its values are named rather than refused. Ids and keys compare
# case-sensitively, as the game's lookups do.
try {
    $fromNode = $mod.GetType('ReDefinition.Settings.GraphicsProfile', $true).GetMethod('FromConfigNode')
    $refusalMethod = $mod.GetType('ReDefinition.Settings.ProfileApplier', $true).GetMethod('Refusal', $sflags)
    $moduleValues = $mod.GetType('ReDefinition.ModuleProfiles', $true).GetMethod('Values', $sflags)
    # What High may set beyond the defaults (docs/player/graphics-profiles.md).
    $highDeviations = @('ksp.TEXTURE_QUALITY', 'ksp.terrainDetail', 'ksp.TERRAIN_SHADER_QUALITY', 'ksp.REFLECTION_PROBE_REFRESH_MODE')
    $unchecked = New-Object 'System.Collections.Generic.SortedSet[string]'
    function Kind-Of($id, $key) {
        $entry = Setting-Of $id $key
        if ($null -eq $entry) { return $null }
        return "$($entry.Kind)"
    }
    function Refusal($m, $key, $value) {
        if (-not $m.IsInstalled -and @($m.Settings).Count -eq 0) { [void]$unchecked.Add($m.Id); return $null }
        $setting = $null
        foreach ($s in $m.Settings) { if ($s.Key -ceq ($m.Id + '.' + $key)) { $setting = $s; break } }
        # A member listed as dropped here -- AERO_FX_QUALITY next to Firefly -- is
        # still a real setting; a key that is nowhere is a typo.
        $droppedHere = $false
        foreach ($d in @($m.DroppedMembers)) { if (($d -split ' -- ')[0] -ceq $key) { $droppedHere = $true } }
        if ($null -eq $setting -and -not $droppedHere) { return 'no such setting' }
        if ((Kind-Of $m.Id $key) -ne 'Quality') { return 'not a quality setting' }
        if ($null -eq $setting) { return $null }
        return $refusalMethod.Invoke($null, [object[]]@($setting, [string]$value))
    }

    $refused = @()
    $profileNames = New-Object 'System.Collections.Generic.List[string]'
    foreach ($file in [IO.Directory]::GetFiles([IO.Path]::Combine($repo, 'GameData\ReDefinition\Profiles'), '*.cfg')) {
        $fileName = [IO.Path]::GetFileName($file)
        $root = $loadNode.Invoke($null, [object[]]@($file))
        foreach ($v in $root.values) { $refused += "${fileName}: '$($v.name) = $($v.value)' stands outside any node" }
        foreach ($node in $root.GetNodes()) {
            if ($node.name -cne 'GRAPHICS_PROFILE') { $refused += "${fileName}: '$($node.name)' is no profile"; continue }
            $problems = (New-Object 'System.Collections.Generic.List[string]').PSObject.BaseObject
            $profile = $fromNode.Invoke($null, [object[]]@($node, $problems))
            if ($null -eq $profile) { $refused += "a profile without a name in $fileName"; continue }
            $profileNames.Add($profile.Name)
            foreach ($p in $problems) { $refused += "'$($profile.Name)': $p" }
            # ReDefinition's modules' part, as ModuleProfiles reads it: every value a
            # quality setting of a module, one its setting takes.
            $moduleProblems = (New-Object 'System.Collections.Generic.List[string]').PSObject.BaseObject
            [void]$moduleValues.Invoke($null, [object[]]@($profile, $moduleProblems))
            foreach ($p in $moduleProblems) { $refused += $p }
        }
    }

    $blocks = 0
    foreach ($r in $registrations) {
        foreach ($block in $r.Profiles) {
            $blocks++
            $label = "$($r.Name), profile '$($block.Name)'" + $(if ($block.Build) { " for build $($block.Build)" } else { '' })
            if (-not $profileNames.Contains($block.Name)) { $refused += "${label}: no shipped profile of that name" }
            if (-not $byId.ContainsKey($r.Name)) { continue }
            $m = $byId[$r.Name]
            foreach ($pair in $block.Values.GetEnumerator()) {
                # A block for another build than the one installed here: its keys
                # must be quality settings of the registration; its values can be
                # judged only where that build is installed.
                if (-not $block.Build -or $m.Build -ceq $block.Build) { $why = Refusal $m $pair.Key $pair.Value }
                elseif ($null -eq (Setting-Of $r.Name $pair.Key)) { $why = 'not in the registration' }
                elseif ((Kind-Of $r.Name $pair.Key) -ne 'Quality') { $why = 'not a quality setting' }
                else { $why = $null }
                if ($why) { $refused += "$label, $($pair.Key) = $($pair.Value): $why" }
                if ($block.Name -ceq 'high' -and $highDeviations -notcontains "$($r.Name).$($pair.Key)") {
                    $refused += "'high' sets $($r.Name).$($pair.Key), which is not one of its deviations from the defaults"
                }
            }
        }
    }
    # What every profile sets (ALL_PROFILES): settings of the registration, of any
    # kind, with values the game takes where the block's build is the one installed.
    $allBlocks = 0
    foreach ($r in $registrations) {
        foreach ($block in $r.AllProfiles) {
            $allBlocks++
            $label = "$($r.Name), every profile" + $(if ($block.Build) { " for build $($block.Build)" } else { '' })
            foreach ($pair in $block.Values.GetEnumerator()) {
                if ($null -eq (Setting-Of $r.Name $pair.Key)) { $refused += "$label, $($pair.Key): not in the registration"; continue }
                if (-not $byId.ContainsKey($r.Name)) { continue }
                $m = $byId[$r.Name]
                if ($block.Build -and $m.Build -cne $block.Build) { continue }
                $setting = $null
                foreach ($s in $m.Settings) { if ($s.Key -ceq ($m.Id + '.' + $pair.Key)) { $setting = $s; break } }
                if ($null -eq $setting) { continue }
                $why = $refusalMethod.Invoke($null, [object[]]@($setting, [string]$pair.Value))
                if ($why) { $refused += "$label, $($pair.Key) = $($pair.Value): $why" }
            }
        }
    }

    # What conflicts with the upscaler, switched off by every profile on every build
    # it concerns: a profile that left one on would antialias or jitter the image a
    # second time in front of the upscaler. The reset may turn them on -- it switches ReDefinition off.
    $conflicts = @(
        @{ Mod = 'scatterer'; Key = 'useTemporalAntiAliasing'; Value = 'False'; Build = $null },
        @{ Mod = 'scatterer'; Key = 'useSubpixelMorphologicalAntialiasing'; Value = 'False'; Build = $null },
        @{ Mod = 'deferred'; Key = 'useSmaaInEditors'; Value = 'False'; Build = $null }
    )
    foreach ($scene in 'profileFlight', 'profileMap', 'profileInternal', 'profileEditor', 'profileSpaceCenter', 'profileTrackingStation', 'profileMainMenu') {
        $conflicts += @{ Mod = 'tufx'; Key = $scene; Value = 'Blackrack_TUFX'; Build = 'volumetric' }
    }
    foreach ($c in $conflicts) {
        $r = @($registrations | Where-Object { $_.Name -ceq $c.Mod })[0]
        if ($null -eq $r) { $refused += "$($c.Mod).$($c.Key): no registration sets it"; continue }
        # Judged per build as ModDefaults.Fitting takes the blocks -- those for
        # every build, then the build's own, the last value counting: the build
        # named, or none and every build the registration knows.
        $builds = @()
        if ($c.Build) { $builds = @($c.Build) }
        else {
            $builds = @('')
            foreach ($b in $r.Builds) { if ($builds -notcontains $b.Name) { $builds += $b.Name } }
            foreach ($block in $r.AllProfiles) { if ($block.Build -and $builds -notcontains $block.Build) { $builds += $block.Build } }
        }
        foreach ($build in $builds) {
            $value = $null
            $fitting = @($r.AllProfiles | Where-Object { -not $_.Build }) + @($r.AllProfiles | Where-Object { $_.Build -and $_.Build -ceq $build })
            foreach ($block in $fitting) { if ($block.Values.ContainsKey($c.Key)) { $value = $block.Values[$c.Key] } }
            if ($value -cne $c.Value) {
                $refused += "$($c.Mod).$($c.Key): every profile leaves it at '$value', not $($c.Value)" + $(if ($build) { " on build $build" } else { '' })
            }
        }
    }

    foreach ($r in $refused) { "      $r" }
    if ($unchecked.Count -gt 0) { "note  not installed here, so their profile values are not checked: " + (@($unchecked) -join ', ') }
    Expect "the shipped profiles read without a problem, the registrations set quality settings for them with values the game takes -- High its deviations only -- and every profile switches off what fights FSR ($($profileNames.Count) profiles, $blocks blocks, $allBlocks for every profile, $($conflicts.Count) conflicts)" (($refused.Count -eq 0) -and ($profileNames.Count -ge 5) -and ($blocks -ge 20) -and ($allBlocks -ge 3))
}
catch {
    $script:lastThrow = "profile check threw: " + $_.Exception.GetBaseException().Message
    Expect "the shipped profiles could be checked" $false
}

# The conversions the window and bundled.cfg rely on: plain .NET, callable here.
$settingValues = $mod.GetType('ReDefinition.Settings.SettingValues', $true)
$text = $settingValues.GetMethod('Text', $sflags)
$parse = $settingValues.GetMethod('Parse', $sflags)
$normalize = $settingValues.GetMethod('Normalize', $sflags)
$invert = $settingValues.GetMethod('Invert', $sflags)
$same = $settingValues.GetMethod('Same', $sflags)
# A conversion that throws must fail its check, not skip it: without this the
# statement would end and the script still report all checks passed.
function Call($method, [object[]]$callArgs) {
    $script:lastThrow = $null
    try { return $method.Invoke($null, $callArgs) }
    catch {
        $e = $_.Exception
        if ($e.InnerException) { $e = $e.InnerException }
        # Kept for the next Expect rather than emitted: a string beside $null
        # would make a two-element array, and an array of two is true -- which
        # would pass the plain assertions for a method that crashed. $null
        # fails every assertion here, and the failure is counted once, by
        # Expect.
        $script:lastThrow = "$($method.Name) threw: $($e.Message)"
        return $null
    }
}

# The defaults (docs/reference/mod-defaults.md), as ModDefaults selects them from the
# registrations for the builds installed here: every setting an installed mod
# offers has one -- but KSP's terrain shader quality, which KSP's own reset
# leaves alone and no code of KSP assigns -- every value is one the game takes
# (ProfileApplier.Refusal, as for the profiles), and the versions they were read
# from are the installed ones wherever the mod can tell.
$chosen = $null
try {
    $select = $mod.GetType('ReDefinition.Settings.ModDefaults', $true).GetMethod('Select', $sflags)
    $withoutDefault = @('ksp.TERRAIN_SHADER_QUALITY')
    $defaultProblems = (New-Object 'System.Collections.Generic.List[string]').PSObject.BaseObject
    $chosen = $select.Invoke($null, [object[]]@($installedList, $defaultProblems))
    $defaultIssues = @($defaultProblems)
    foreach ($m in $installedList) {
        $values = $null
        if (-not $chosen.TryGetValue($m.Id, [ref]$values)) { $defaultIssues += "no defaults for $($m.Id)"; continue }
        foreach ($s in $m.Settings) {
            $member = $s.Key.Substring($m.Id.Length + 1)
            if (-not $values.ContainsKey($member)) {
                if ($withoutDefault -notcontains $s.Key) { $defaultIssues += "$($s.Key): no default for build $($m.Build)" }
                continue
            }
            $why = $refusalMethod.Invoke($null, [object[]]@($s, [string]$values[$member]))
            if ($why) { $defaultIssues += "$($s.Key) = $($values[$member]): $why" }
        }
    }
    foreach ($i in $defaultIssues) { "      $i" }
    Expect "the registrations hold a default the game takes for every setting of every installed build, read from the installed versions" ($defaultIssues.Count -eq 0)
}
catch {
    $script:lastThrow = $_.Exception.ToString().Split("`n")[0]
    Expect "the defaults could be checked" $false
}

# A registration reaches its own mod's folder only: KSP's reaches GameSettings,
# not the rest of the Managed folder; Waterfall's not Parallax.
try {
    $folderOf = $mod.GetType('ReDefinition.Settings.ModFolder', $true).GetMethod('Of')
    $exists = $mod.GetType('ReDefinition.Settings.MemberPath', $true).GetMethod('Exists', [Reflection.BindingFlags]'Static, Public')
    $gameSettings = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('GameSettings', $false) } | Where-Object { $_ } | Select-Object -First 1
    $kspFolder = $folderOf.Invoke($null, [object[]]@($gameSettings.Assembly))
    $reachesOwn = $exists.Invoke($null, [object[]]@('GameSettings.SaveSettings', $kspFolder))
    $reachesUnity = $exists.Invoke($null, [object[]]@('UnityEngine.Application.Quit', $kspFolder))
    $waterfallSettings = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Waterfall.Settings', $false) } | Where-Object { $_ } | Select-Object -First 1
    $reachesOther = $false
    if ($waterfallSettings) {
        $waterfallFolder = $folderOf.Invoke($null, [object[]]@($waterfallSettings.Assembly))
        $reachesOther = $exists.Invoke($null, [object[]]@('Parallax.ConfigLoader', $waterfallFolder))
    }
    Expect "a registration reaches only its mod's folder: KSP's GameSettings, not UnityEngine; not Parallax from Waterfall" ($reachesOwn -and -not $reachesUnity -and -not $reachesOther)
}
catch {
    $script:lastThrow = "folder check threw: " + $_.Exception.GetBaseException().Message
    Expect "the folder a registration reaches could be checked" $false
}

# The registrations beyond the shipped ones: the worked example and the template in
# docs\modders\examples, and every registration another mod or a pack puts into the
# installed GameData -- with the assemblies of the folder it stands in loaded, as KSP
# loads them. Each reads and builds without a problem; one whose mod is loaded here finds
# every member it names, and its defaults are values the game takes.
try {
    # The examples and the installed GameData apart: a registration copied from
    # docs\modders\examples into GameData, as the guide says to try one, is checked there, and
    # the example in the repository still is. A cfg directly in GameData stands in no folder
    # whose assemblies could be loaded.
    $gameDataNodes = (New-Object 'System.Collections.Generic.List[object]').PSObject.BaseObject
    $ourFolder = [IO.Path]::Combine($gameData, 'ReDefinition') + [IO.Path]::DirectorySeparatorChar
    foreach ($file in [IO.Directory]::EnumerateFiles($gameData, '*.cfg', [IO.SearchOption]::AllDirectories)) {
        if ($file.StartsWith($ourFolder, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not [IO.File]::ReadAllText($file).Contains('MOD_SETTINGS')) { continue }
        foreach ($n in $loadNode.Invoke($null, [object[]]@($file)).GetNodes('MOD_SETTINGS')) { $gameDataNodes.Add($n) }
        $relative = $file.Substring($gameData.Length + 1)
        if ($relative.IndexOf([IO.Path]::DirectorySeparatorChar) -lt 0) { continue }
        $folder = $relative.Split([IO.Path]::DirectorySeparatorChar)[0]
        foreach ($dll in [IO.Directory]::GetFiles([IO.Path]::Combine($gameData, $folder), '*.dll', [IO.SearchOption]::AllDirectories)) {
            try { [void][Reflection.Assembly]::LoadFrom($dll) } catch { "note  $dll not loaded: $($_.Exception.Message)" }
        }
    }
    $exampleNodes = (New-Object 'System.Collections.Generic.List[object]').PSObject.BaseObject
    foreach ($file in [IO.Directory]::GetFiles([IO.Path]::Combine($repo, 'docs\modders\examples'), '*.cfg')) {
        foreach ($n in $loadNode.Invoke($null, [object[]]@($file)).GetNodes('MOD_SETTINGS')) { $exampleNodes.Add($n) }
    }

    function Check-Registrations($label, $nodes, $atLeast) {
        $nodes = $nodes.PSObject.BaseObject
        $extraProblems = (New-Object 'System.Collections.Generic.List[string]').PSObject.BaseObject
        $extraList = $readRegistrations.Invoke($null, [object[]]@($nodes, $extraProblems))
        $extraBuilt = @($buildRegistered.Invoke($null, [object[]]@($extraList, $extraProblems)))
        foreach ($p in $extraProblems) { "      $p" }
        $extraNames = @($extraBuilt | ForEach-Object { $_.Id }) -join ', '
        Expect "the registrations in $label read and build without a problem ($extraNames)" (($extraProblems.Count -eq 0) -and ($extraBuilt.Count -ge $atLeast))
        foreach ($m in $extraBuilt) {
            if (-not (Internal $m 'Detected')) {
                if (@($m.DroppedMembers).Count -gt 0) { foreach ($d in @($m.DroppedMembers)) { "note  $($m.ModName) does not bundle: $d" } }
                else { "note  $($m.ModName) is not installed here: its registration is read, not built against it" }
                continue
            }
            foreach ($d in @($m.DroppedMembers)) { "note  $($m.ModName) does not bundle: $d" }
            $defaultIssues = @()
            $defaultsOf = $mod.GetType('ReDefinition.Settings.ModDefaults', $true).GetMethod('Select', $sflags).Invoke($null, [object[]]@((As-Mods @($m)), (New-Object 'System.Collections.Generic.List[string]').PSObject.BaseObject))
            $values = $null
            [void]$defaultsOf.TryGetValue($m.Id, [ref]$values)
            foreach ($s in $m.Settings) {
                $member = $s.Key.Substring($m.Id.Length + 1)
                if ($null -eq $values -or -not $values.ContainsKey($member)) { $defaultIssues += "$($s.Key): no default"; continue }
                $why = $mod.GetType('ReDefinition.Settings.ProfileApplier', $true).GetMethod('Refusal', $sflags).Invoke($null, [object[]]@($s, [string]$values[$member]))
                if ($why) { $defaultIssues += "$($s.Key) = $($values[$member]): $why" }
            }
            foreach ($i in $defaultIssues) { "      $i" }
            Expect "the registration of $($m.ModName) in $label finds every member it names, with defaults the game takes ($(@($m.Settings).Count) settings)" ($m.IsInstalled -and @($m.DroppedMembers).Count -eq 0 -and $defaultIssues.Count -eq 0)
        }
    }
    Check-Registrations 'docs\modders\examples' $exampleNodes 2
    if ($gameDataNodes.Count -gt 0) { Check-Registrations 'the installed GameData' $gameDataNodes 1 }
}
catch {
    $script:lastThrow = "registrations beyond the shipped ones threw: " + $_.Exception.GetBaseException().Message
    Expect "the registrations beyond the shipped ones could be checked" $false
}

# What a registration does where a build of its mod differs from the one it was
# written against: registrations of Waterfall, installed here, naming members it
# does not have.
try {
    $parseNode = $configNodeType.GetMethod('Parse', [Type[]]@([string]))
    function Build-Registered($text) {
        $nodes = (New-Object 'System.Collections.Generic.List[object]').PSObject.BaseObject
        foreach ($n in $parseNode.Invoke($null, [object[]]@($text)).GetNodes('MOD_SETTINGS')) { $nodes.Add($n) }
        $script:caseProblems = (New-Object 'System.Collections.Generic.List[string]').PSObject.BaseObject
        $regs = $readRegistrations.Invoke($null, [object[]]@($nodes, $script:caseProblems))
        return @($buildRegistered.Invoke($null, [object[]]@($regs, $script:caseProblems)))
    }
    function Case-Mod($name, $body) { return "MOD_SETTINGS`n{`n name = $name`n detect = Waterfall.Settings`n$body}`n" }
    function Case-Setting($name, $more) { return " SETTING`n {`n  name = $name`n  member = Waterfall.Settings.$name`n$more`n }`n" }
    $waterfallHere = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Waterfall.Settings', $false) } | Where-Object { $_ } | Select-Object -First 1
    if (-not $waterfallHere) {
        "note  Waterfall is not installed here: what a registration does with a build of another shape is not checked"
    }
    else {
        $m = @(Build-Registered (Case-Mod 'case_needs' (" needs = Waterfall.Settings.EnableLights, Waterfall.Settings.NoSuchGroup`n" + (Case-Setting 'EnableLights' '  default = True'))))[0]
        Expect "a mod without one of its needs is not bundled, and says why" ((-not $m.IsInstalled) -and @($m.DroppedMembers | Where-Object { $_ -like '(the whole mod) -- *NoSuchGroup*' }).Count -eq 1)
        $m = @(Build-Registered (Case-Mod 'case_required' ((Case-Setting 'EnableLights' '  default = True') + (Case-Setting 'NoSuchSetting' "  required = True`n  default = True"))))[0]
        Expect "a mod without the member of a required setting is not bundled" (-not $m.IsInstalled)
        $m = @(Build-Registered (Case-Mod 'case_missing' ((Case-Setting 'EnableLights' '  default = True') + (Case-Setting 'NoSuchSetting' '  default = True'))))[0]
        Expect "a missing member that is not required leaves only its setting out" ($m.IsInstalled -and @($m.Settings).Count -eq 1 -and @($m.DroppedMembers | Where-Object { $_ -like 'NoSuchSetting -- *' }).Count -eq 1)
        $m = @(Build-Registered (Case-Mod 'case_default' ((Case-Setting 'EnableLights' '  default = x9') + (Case-Setting 'EnableDistortion' '  default = True'))))[0]
        Expect "a default that is no value of its member's type leaves the setting out, with a problem" ($m.IsInstalled -and @($m.Settings).Count -eq 1 -and @($caseProblems | Where-Object { $_ -like "*EnableLights*default 'x9'*" }).Count -eq 1)
        $m = @(Build-Registered (Case-Mod 'case_after' (Case-Setting 'EnableLights' "  takesEffect = Live`n  after = Waterfall.Settings.NoSuchMethod`n  default = True")))[0]
        Expect "a setting whose after is not there takes effect from the next scene, not live" ("$(@($m.Settings)[0].Window)" -eq 'NextScene')
        $m = @(Build-Registered (Case-Mod 'case_optional' ((Case-Setting 'EnableLights' '  default = True') + (Case-Setting 'NoSuchSetting' '  optional = False'))))[0]
        Expect "optional = False is no reason: the missing member is reported" (@($caseProblems | Where-Object { $_ -like '*NoSuchSetting*' }).Count -eq 1 -and @($m.DroppedMembers | Where-Object { $_ -like '* -- False' }).Count -eq 0)
        $pair = @(Build-Registered ((Case-Mod 'case_loaded' (Case-Setting 'NoSuchSetting' "  required = True`n  default = True")) + (Case-Mod 'case_holder' ((Case-Setting 'EnableLights' "  leftOutWith = case_loaded`n  default = True") + (Case-Setting 'EnableDistortion' '  default = True')))))
        # By id: the mods stand by title.
        $loadedCase = @($pair | Where-Object { $_.Id -eq 'case_loaded' })[0]
        $holderCase = @($pair | Where-Object { $_.Id -eq 'case_holder' })[0]
        Expect "a setting another mod holds is left out while that mod is loaded, bundled or not" ((-not $loadedCase.IsInstalled) -and $holderCase.IsInstalled -and @($holderCase.Settings).Count -eq 1 -and "$(@($holderCase.Settings)[0].Key)" -eq 'case_holder.EnableDistortion')
        $m = @(Build-Registered (Case-Mod 'case_ready' (" ready = Waterfall.Settings.NoSuchObject`n" + (Case-Setting 'EnableLights' '  default = True'))))[0]
        Expect "a mod whose ready member is not there is not bundled, and says why" ((-not $m.IsInstalled) -and @($m.DroppedMembers | Where-Object { $_ -like '(the whole mod) -- *NoSuchObject*' }).Count -eq 1)
    }
}
catch {
    $script:lastThrow = "registration cases threw: " + $_.Exception.GetBaseException().Message
    Expect "what a registration does with a build of another shape could be checked" $false
}

# The requirements (docs/reference/requirements.md), as Requirements reads them
# from the registrations: every rule names a registered setting, a check it names
# is one the setting's mod answers, and its fix passes its own test wherever the
# fix can be worked out outside the game. Where a rule changes a default of an
# installed mod here, that is named: the game changes it the same way
# (Requirements.Adjust).
try {
    $requirementsType = $mod.GetType('ReDefinition.Settings.Requirements', $true)
    $rulesOf = $requirementsType.GetMethod('RulesOf', $sflags)
    $passes = $requirementsType.GetMethod('Passes', $sflags)
    $fixOf = $requirementsType.GetMethod('FixOf', $sflags)
    $rules = @($rulesOf.Invoke($null, [object[]]@($registeredList, $false)))
    $inForce = @($rulesOf.Invoke($null, [object[]]@($registeredList, $true))).Count
    $ruleIssues = @()
    foreach ($rule in $rules) {
        $modId = $rule.Key.Substring(0, [Math]::Max(0, $rule.Key.IndexOf('.')))
        $member = $rule.Key.Substring($modId.Length + 1)
        if ($null -eq (Setting-Of $modId $member)) { $ruleIssues += "$($rule.Id): $($rule.Key) is no registered setting"; continue }
        $setting = $null
        if ($byId.ContainsKey($modId)) { foreach ($s in $byId[$modId].Settings) { if ($s.Key -ceq $rule.Key) { $setting = $s } } }
        if ("$($rule.Registration.Test)" -eq 'Check') {
            $behaviour = if ($byId.ContainsKey($modId)) { Internal $byId[$modId] 'MainBehaviour' } else { $null }
            if ($null -eq $behaviour) {
                "note  $($rule.Id): $modId is not installed here, so its check '$($rule.Registration.Value)' is not asked"
            } elseif (-not $behaviour.Provides($rule.Registration.Value)) {
                $ruleIssues += "$($rule.Id): $modId answers no check '$($rule.Registration.Value)'"
            }
        }
        $fix = $fixOf.Invoke($null, [object[]]@($rule, $setting))
        if ($null -eq $fix) { "note  $($rule.Id): its fix needs the game"; continue }
        if (-not $passes.Invoke($null, [object[]]@($rule, $setting, [string]$fix))) { $ruleIssues += "$($rule.Id): its fix '$fix' fails its own test" }
        $values = $null
        if ($chosen -and $chosen.TryGetValue($modId, [ref]$values) -and $values.ContainsKey($member) -and -not $passes.Invoke($null, [object[]]@($rule, $setting, [string]$values[$member]))) {
            "note  $($rule.Id) sets $($rule.Key) from its default $($values[$member]) to $fix while its mod is loaded"
        }
    }
    foreach ($i in $ruleIssues) { "      $i" }
    Expect "every requirement names a registered setting and a check its mod answers, and its fix passes its own test ($($rules.Count) rules, $inForce in force here)" (($ruleIssues.Count -eq 0) -and ($rules.Count -ge 1))
}
catch {
    $script:lastThrow = $_.Exception.ToString().Split("`n")[0]
    Expect "the requirements could be checked" $false
}

Expect "a float is written as a ConfigNode holds it: 0.9 as 0.9" ((Call $text @([single]0.9)) -eq '0.9')
Expect "a value from a file compares as the value it is: 0.9 is 0.899999976" (Call $same @('0.9', '0.899999976'))
Expect "true is True" (Call $same @('true', 'True'))
# Tested against False rather than negated: a throw returns $null here, and
# `-not $null` would pass for a method that crashed.
Expect "names compare exactly: Cinematic is not cinematic" ((Call $same @('Cinematic', 'cinematic')) -eq $false)
Expect "a whole number goes into an int: 128" ((Call $parse @('128', [int])) -eq 128)
Expect "a file's float is normalised: 0.209999993 as 0.21" ((Call $normalize @('0.209999993', [single])) -eq '0.21')
# Scatterer's cascade splits are vectors: parsed from a file's spacing, written
# back as a ConfigNode holds them, and compared item by item.
$vectorType = $null
foreach ($asm in $loaded) { $candidate = $asm.GetType('UnityEngine.Vector3', $false); if ($candidate) { $vectorType = $candidate; break } }
if ($vectorType) {
    Expect "a vector goes into a Vector3 and back: '0.0015, 0.015, 0.15' as 0.0015,0.015,0.15" ((Call $text @((Call $parse @('0.0015, 0.015, 0.15', $vectorType)))) -eq '0.0015,0.015,0.15')
}
else {
    Expect "UnityEngine.Vector3 could be found" $false
}
Expect "a list compares item by item: 0.00150000001,0.0149999997 is 0.0015,0.015" (Call $same @('0.00150000001,0.0149999997', '0.0015,0.015'))
Expect "names in a list compare exactly: ORBITING,FLYING is not ORBITING,flying" ((Call $same @('ORBITING,FLYING', 'ORBITING,flying')) -eq $false)
Expect "text with a comma is no list of numbers: 'A, B' is not 'A,B'" ((Call $same @('A, B', 'A,B')) -eq $false)
$threw = $false
try { [void]$parse.Invoke($null, [object[]]@('3000.5', [int])) } catch { $threw = $true }
Expect "a fraction does not go into an int: 3000.5 is refused, not rounded" $threw
$threw = $false
try { [void]$parse.Invoke($null, [object[]]@('NaN', [int])) } catch { $threw = $true }
Expect "NaN goes into no int" $threw
$threw = $false
try { [void]$parse.Invoke($null, [object[]]@('NaN', [single])) } catch { $threw = $true }
Expect "NaN goes into no float" $threw
if ($vectorType) {
    $threw = $false
    try { [void]$parse.Invoke($null, [object[]]@('NaN,0,0', $vectorType)) } catch { $threw = $true }
    Expect "NaN goes into no vector" $threw
}
Expect "Firefly's disable switches show the other way round" ((Call $invert @('True')) -eq 'False')

$launcher = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('KSP.UI.Screens.ApplicationLauncher', $false) } | Where-Object { $_ } | Select-Object -First 1
$any = [Reflection.BindingFlags]'Instance, Static, Public, NonPublic'
Expect "KSP's launcher keeps its mod buttons in appListMod and appListModHidden" ($launcher -and $launcher.GetField('appListMod', $any) -and $launcher.GetField('appListModHidden', $any))
$buttonType = $launcher.Assembly.GetType('KSP.UI.Screens.ApplicationLauncherButton')
$prop = $buttonType.GetProperty('VisibleInScenes')
Expect "ApplicationLauncherButton.VisibleInScenes can be set" ($prop -and $prop.CanWrite)

$tc = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('ToolbarControl_NS.ToolbarControl', $false) } | Where-Object { $_ } | Select-Object -First 1
if ($null -eq $tc) {
    "note  ToolbarControl is not installed here: how it keeps its instances is not checked"
} else {
    $tcList = $tc.GetField('tcList', $any)
    Expect "ToolbarControl keeps its instances in the static tcList, each with nameSpace and stockButton" ($tcList -and $tcList.IsStatic -and $tc.GetField('nameSpace', $any) -and $tc.GetField('stockButton', $any))
}

""
if ($failures -eq 0) { "All checks passed." } else { "$failures check(s) failed."; exit 1 }
