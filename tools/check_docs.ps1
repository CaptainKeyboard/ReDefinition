# What the pages under docs/ and the repository's README.md have to hold
# (docs/development/writing-these-pages.md): links that lead somewhere, paths that
# exist, no dash asides, lines that fit, and an opening that says who the page is for.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_docs.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_docs.ps1 -Strict
#
# Without -Strict only links and paths fail the run; the style counts are reported.
# With it, every rule fails the run.
[CmdletBinding()]
param([switch]$Strict)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failures = 0
$styleIssues = 0

function Fail([string]$text) {
    Write-Host "FAIL  $text"
    $script:failures++
}

function Style([string]$text) {
    Write-Host "style $text"
    $script:styleIssues++
}

# The mod's own README is its front page and follows none of these rules. The review
# log is a log: its entries keep the wording they were written with.
$pages = @(Get-ChildItem -Path (Join-Path $root 'docs') -Filter *.md -Recurse -File)
$pages = $pages | Where-Object { $_.FullName -notlike '*\docs\project\reviews.md' }

Write-Host "Pages: $($pages.Count)"

foreach ($page in $pages) {
    $relative = $page.FullName.Substring($root.Length + 1).Replace('\', '/')
    $lines = Get-Content -LiteralPath $page.FullName
    $inCode = $false
    $lineNumber = 0
    $prose = New-Object System.Collections.Generic.List[string]

    foreach ($line in $lines) {
        $lineNumber++
        if ($line -match '^\s*```') { $inCode = -not $inCode; continue }
        if ($inCode) { continue }

        # Links and paths, in prose and in tables alike.
        foreach ($match in [regex]::Matches($line, '\]\(([^)]+)\)')) {
            $target = $match.Groups[1].Value
            if ($target -match '^(https?:|#|mailto:)') { continue }
            $path = ($target -split '#')[0]
            if ([string]::IsNullOrWhiteSpace($path)) { continue }
            $full = Join-Path (Split-Path -Parent $page.FullName) $path
            if (-not (Test-Path -LiteralPath $full)) {
                Fail "$relative`:$lineNumber -- link to $target leads nowhere"
            }
        }
        # Paths in the repository. A GameData path is the player's install, not this repo.
        foreach ($match in [regex]::Matches($line, '`((?:src|tools|tests|unity|licenses|third_party)/[^`]+)`')) {
            $path = $match.Groups[1].Value.TrimEnd('.', ',')
            if ($path -match '[*?<>]') { continue }
            if (-not (Test-Path -LiteralPath (Join-Path $root $path))) {
                Fail "$relative`:$lineNumber -- $path is not there"
            }
        }

        if ($line -match '^\s*\|') { continue }   # a table row may be longer and hold a dash
        $prose.Add("$lineNumber`t$line")
    }

    if ($inCode) { Fail "$relative -- a code block is not closed" }

    # The opening: the three lines every page starts with.
    $head = ($lines | Select-Object -First 8) -join "`n"
    if ($head -notmatch '\*\*For:\*\*' -or $head -notmatch '\*\*You need:\*\*' -or $head -notmatch '\*\*You get:\*\*') {
        Style "$relative -- no For / You need / You get at the top"
    }

    foreach ($entry in $prose) {
        $number, $text = $entry -split "`t", 2
        # A dash inside code font is the rule itself, or a command line.
        $outsideCode = [regex]::Replace($text, '`[^`]*`', '')
        if ($outsideCode -match ' -- ') { Style "$relative`:$number -- a dash aside" }
        # German in English words (docs/development/writing-these-pages.md).
        foreach ($calque in @('stands in', 'stand in', 'stands there', 'counts over',
                              'count over', 'put right', 'the frame before')) {
            if ($outsideCode -match [regex]::Escape($calque)) {
                Style "$relative`:$number -- `"$calque`""
            }
        }
        if ($text.Length -gt 95) { Style "$relative`:$number -- $($text.Length) characters" }
    }
}

if ($styleIssues -gt 0) { Write-Host "Style: $styleIssues line(s) to rewrite" }
if ($Strict) { $failures += $styleIssues }

if ($failures -gt 0) {
    Write-Host "$failures check(s) failed."
    exit 1
}
Write-Host "All checks passed."
