<#
.SYNOPSIS
    Fails when the version is not the same everywhere.

.DESCRIPTION
    The version lives in three files and has to agree in all of them. Nothing
    enforced that, and nothing needed to go wrong for it to drift: bumping two
    of the three is an ordinary slip, and it produces an installer whose file
    name, Programs and Features entry and About text disagree.

    Run on a tag, it also checks the tag matches, which is the case that
    actually reaches other people.

    This is a check, not a build step. It writes nothing and generates nothing.

.PARAMETER Tag
    A tag such as "v0.5.0" to compare against. Optional; CI passes the tag it
    is building.
#>
[CmdletBinding()]
param(
    [string] $Tag
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$problems = @()

function Read-Single {
    param([string] $Path, [string] $Pattern, [string] $What)

    $full = Join-Path $root $Path
    if (-not (Test-Path $full)) {
        $script:problems += "$What`: $Path does not exist"
        return $null
    }

    $found = Select-String -Path $full -Pattern $Pattern -AllMatches |
             ForEach-Object { $_.Matches } |
             ForEach-Object { $_.Groups[1].Value }

    if (-not $found) {
        $script:problems += "$What`: no version found in $Path"
        return $null
    }

    $distinct = @($found | Select-Object -Unique)
    if ($distinct.Count -gt 1) {
        $script:problems += "$What`: $Path disagrees with itself ($($distinct -join ', '))"
        return $null
    }

    return $distinct[0]
}

Write-Host "AFKLocker version check"

# AssemblyVersion and AssemblyFileVersion are four-part; the informational one
# is the three-part number a person would say out loud.
$informational = Read-Single 'src/AssemblyVersion.cs' 'AssemblyInformationalVersion\("([0-9]+\.[0-9]+\.[0-9]+)"\)' 'assembly'

# Read separately rather than with one Assembly(File)?Version pattern. Sharing a
# regex meant either attribute could be deleted and the check would still pass on
# the other - and a missing one does not vanish, it defaults, which is drift with
# no error message.
$assembly      = Read-Single 'src/AssemblyVersion.cs' 'AssemblyVersion\("([0-9]+\.[0-9]+\.[0-9]+)\.[0-9]+"\)' 'AssemblyVersion'
$fileVersion   = Read-Single 'src/AssemblyVersion.cs' 'AssemblyFileVersion\("([0-9]+\.[0-9]+\.[0-9]+)\.[0-9]+"\)' 'AssemblyFileVersion'
$installer     = Read-Single 'installer/AFKLocker.iss' '#define\s+AppVersion\s+"([0-9]+\.[0-9]+\.[0-9]+)"' 'installer'

# The newest released section in the changelog. [Unreleased] is skipped on
# purpose: work in progress is allowed to sit above the last release.
$changelogPath = Join-Path $root 'CHANGELOG.md'
$changelog = (Select-String -Path $changelogPath -Pattern '^##\s+\[([0-9]+\.[0-9]+\.[0-9]+)\]' |
              ForEach-Object { $_.Matches[0].Groups[1].Value } |
              Select-Object -First 1)
if (-not $changelog) { $problems += 'changelog: no released version heading found' }

$values = [ordered]@{
    'AssemblyInformationalVersion' = $informational
    'AssemblyVersion'              = $assembly
    'AssemblyFileVersion'          = $fileVersion
    'installer AppVersion'         = $installer
    'CHANGELOG newest release'     = $changelog
}

foreach ($name in $values.Keys) {
    "  {0,-30} {1}" -f $name, ($values[$name] ?? '(missing)') | Write-Host
}

$distinct = @($values.Values | Where-Object { $_ } | Select-Object -Unique)
if ($distinct.Count -gt 1) {
    $problems += "these do not agree: $($distinct -join ', ')"
}

if ($Tag) {
    $tagVersion = $Tag.TrimStart('v', 'V')
    "  {0,-30} {1}" -f 'tag', $Tag | Write-Host

    if ($tagVersion -ne $informational) {
        $problems += "tag $Tag does not match the assembly version $informational"
    }
}

Write-Host ''
if ($problems.Count -gt 0) {
    Write-Host 'Version check FAILED:' -ForegroundColor Red
    foreach ($problem in $problems) { Write-Host "  - $problem" -ForegroundColor Red }
    exit 1
}

Write-Host "Version check passed: $informational" -ForegroundColor Green
exit 0
