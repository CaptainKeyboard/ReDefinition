# Shared by the check scripts, dot-sourced at their top:
#
#   . ([IO.Path]::Combine($PSScriptRoot, 'check_common.ps1'))
#
# Sets $repo, $kspRoot (the KspRoot environment variable, as in
# Directory.Build.props, by default the Steam location), $managed and $gameData,
# resolves KSP's assemblies, and gives Expect.

$repo = Split-Path -Parent $PSScriptRoot
$kspRoot = if ($env:KspRoot) { $env:KspRoot } else { 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program' }
$managed = [IO.Path]::Combine($kspRoot, 'KSP_x64_Data\Managed')
$gameData = [IO.Path]::Combine($kspRoot, 'GameData')

# Where the resolver looks: each folder in $resolveFolders, then, where set,
# every folder under $resolveUnder. A script changes them after the dot-source.
$resolveFolders = @($managed, [IO.Path]::Combine($gameData, '000_Harmony'))
$resolveUnder = $null

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
        foreach ($d in $resolveFolders) {
            $p = [IO.Path]::Combine($d, $n + '.dll')
            if ([IO.File]::Exists($p)) { return [Reflection.Assembly]::LoadFrom($p) }
        }
        if ($resolveUnder) {
            foreach ($f in [IO.Directory]::GetFiles($resolveUnder, $n + '.dll', [IO.SearchOption]::AllDirectories)) {
                return [Reflection.Assembly]::LoadFrom($f)
            }
        }
        return $null
    }
    finally { $global:resolving = $false }
})

$failures = 0
$lastThrow = $null
function Expect($what, $ok) {
    # A method that threw, or a cause set in $lastThrow, can never be a pass,
    # whatever the assertion made of it: the net belongs here, not at each call site.
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
