# Checks what ReDefinition takes for granted about other mods' code
# (src/Core/ForeignAssumptions.cs, src/Planner/PlannerAssumptions.cs and
# src/Atmospheres/Infrastructure/Bridge/AtmospheresAssumptions.cs with the classes marked
# [AtmospheresAssumptions]) against the mods installed, outside the game.
# It loads their assemblies and the built DLL, and evaluates every assumption.
# That is whether the types, methods, fields and string literals it names are there.
# It is also whether the methods it rests on are the code it was checked against
# (their fingerprints) or the versions it was.
#
# Per assumption: PASS where it is verified or its mod is not installed. FAIL
# where something it names is missing, or its code or version changed. A FAIL gives
# the installed fingerprint, to add to the catalogue once the new code has been
# read. Exit code: the number of FAILs.
#
# Runs in Windows PowerShell 5.1 and in PowerShell 7. The KSP folder is taken
# from the KspRoot environment variable, as in Directory.Build.props, and
# defaults to the Steam location.
#
# Usage:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\checks\check_foreign_code.ps1

. ([IO.Path]::Combine($PSScriptRoot, 'check_common.ps1'))
$resolveFolders = @($managed)
$resolveUnder = $gameData

[void][Reflection.Assembly]::LoadFrom([IO.Path]::Combine($managed, 'Assembly-CSharp.dll'))
[void][Reflection.Assembly]::LoadFrom([IO.Path]::Combine($managed, 'UnityEngine.CoreModule.dll'))

# Every plugin assembly in GameData but ReDefinition's own, as KSP loads them.
foreach ($f in [IO.Directory]::GetFiles($gameData, '*.dll', [IO.SearchOption]::AllDirectories)) {
    if ($f -like '*\ReDefinition\*') { continue }
    if ($f -notlike '*\Plugins\*' -and $f -notlike '*\Scatterer\*' -and $f -notlike '*\zzz_Deferred\*') { continue }
    try { [void][Reflection.Assembly]::LoadFrom($f) } catch { }
}

$mod = [Reflection.Assembly]::LoadFrom([IO.Path]::Combine($repo, 'build\ReDefinition.dll'))
$catalogue = $mod.GetType('ReDefinition.Core.ForeignAssumptions', $true)
$member = [Reflection.BindingFlags]'Static, Public, NonPublic'
$all = @($catalogue.GetProperty('All', $member).GetValue($null, $null))
# The planner's own entries join the catalogue when the planner registers them in the game; here they are read from its DLL.
$plannerDll = [IO.Path]::Combine($repo, 'build\ReDefinition.Planner.dll')
if ([IO.File]::Exists($plannerDll)) {
    $planner = [Reflection.Assembly]::LoadFrom($plannerDll)
    $all += @($planner.GetType('ReDefinition.Planner.PlannerAssumptions', $true).GetField('All', $member).GetValue($null))
}
# The atmospheres' entries (src/Atmospheres/Infrastructure/Bridge/AtmospheresAssumptions.cs): every class marked
# [AtmospheresAssumptions] in ReDefinition.Atmospheres.dll, collected by AtmospheresAssumptions.All, ordered by id. The type
# of the entries is Common's ForeignAssumption, the same as the main catalogue's.
$atmospheresDll = [IO.Path]::Combine($repo, 'build\ReDefinition.Atmospheres.dll')
if ([IO.File]::Exists($atmospheresDll)) {
    $atmospheres = [Reflection.Assembly]::LoadFrom($atmospheresDll)
    $atmospheresCatalogue = $atmospheres.GetType('ReDefinition.Atmospheres.AtmospheresAssumptions', $false)
    if ($null -ne $atmospheresCatalogue) {
        $all += @($atmospheresCatalogue.GetProperty('All', $member).GetValue($null, $null))
    } else {
        Write-Output 'FAIL  ReDefinition.Atmospheres.AtmospheresAssumptions is not in ReDefinition.Atmospheres.dll'
        $failures++
    }
}
$flags = [Reflection.BindingFlags]'Instance, Public, NonPublic'
# The value of a property, or else of a field, of that name on the object, public or not.
function Get($object, $name) {
    $property = $object.GetType().GetProperty($name, $flags)
    if ($null -ne $property) { return $property.GetValue($object, $null) }
    return $object.GetType().GetField($name, $flags).GetValue($object)
}

foreach ($assumption in $all) {
    $id = Get $assumption 'Id'
    $state = (Get $assumption 'State').ToString()
    $version = Get $assumption 'Version'
    $fingerprint = Get $assumption 'Fingerprint'
    $problem = Get $assumption 'Problem'
    $details = @()
    if ($version) { $details += "version $version" }
    if ($fingerprint) { $details += "code $fingerprint" }
    $line = "$state".PadRight(9) + " $id" + $(if ($details.Count -gt 0) { " (" + ($details -join ', ') + ")" } else { '' })
    if ($state -eq 'Absent' -or $state -eq 'Verified') {
        "PASS  $line"
    } else {
        "FAIL  $line"
        "      $problem"
        $failures++
    }
}
exit $failures
