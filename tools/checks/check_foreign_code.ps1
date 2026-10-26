# Checks what ReDefinition takes for granted about other mods' code
# (src/Core/ForeignAssumptions.cs) against the mods installed, outside the game:
# loads their assemblies and the built DLL, and evaluates every assumption --
# whether the types, methods, fields and string literals it names are there, and
# whether the methods it rests on are the code it was checked against
# (their fingerprints) or the versions it was.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_foreign_code.ps1
#
# Per assumption: PASS where it is verified or its mod is not installed; FAIL
# where something it names is missing, or its code or version changed -- with
# the installed fingerprint, to add to the catalogue once the new code has been
# read. Exit code: the number of FAILs.
#
# Runs in Windows PowerShell 5.1 and in PowerShell 7. The KSP folder is taken
# from the KspRoot environment variable, as in Directory.Build.props, and
# defaults to the Steam location.

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
$all = $catalogue.GetField('All', [Reflection.BindingFlags]'Static, Public, NonPublic').GetValue($null)
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
