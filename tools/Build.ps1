<#
.SYNOPSIS
    Builds AFKLocker.

.DESCRIPTION
    Compiles with the C# compiler that ships with the .NET Framework, which is
    present on every supported version of Windows. There is no SDK to install
    and no package restore step, so this script produces the same binaries on a
    developer machine and on a clean CI runner.

.PARAMETER Test
    Run the test suite after building.

.PARAMETER Installer
    Build the Inno Setup installer as well. Requires ISCC.exe on PATH or in the
    usual install locations, or the path given by -Iscc.

.PARAMETER Sign
    Authenticode-sign the binaries, and the installer if one is built. Needs
    AFKLOCKER_SIGNING_CERTIFICATE and AFKLOCKER_SIGNING_PASSWORD in the
    environment; without them it is a no-op, so a normal build is unaffected.

.PARAMETER RequireSigning
    Turn a skipped signature into a build failure. The release workflow passes
    this when the signing secrets exist, so a broken signing setup cannot
    silently publish unsigned binaries.

.EXAMPLE
    pwsh -File tools/Build.ps1 -Test
#>
[CmdletBinding()]
param(
    [switch]$Test,
    [switch]$Installer,
    [string]$Iscc,
    [string]$OutputDirectory,

    # Sign the binaries and installer. Without a certificate configured this
    # does nothing; with -RequireSigning it fails instead of quietly skipping.
    [switch]$Sign,
    [switch]$RequireSigning,

    # Testing only: accept a signature from an untrusted (self-signed)
    # certificate, so the signing path can be exercised without a real one.
    [switch]$AllowUntrustedSigningRoot
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'build' }

$src = Join-Path $root 'src'
$tests = Join-Path $root 'tests'
$assets = Join-Path $root 'assets'

function Get-CSharpCompiler {
    $candidates = @(
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
        (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }
    throw "Could not find the .NET Framework C# compiler (csc.exe). Expected it at: $($candidates -join ', ')"
}

function Invoke-Csc {
    param(
        [string]$Target,
        [string]$Output,
        [string[]]$Sources,
        [string[]]$References = @(),
        [string]$Icon,
        [string]$Manifest
    )

    $arguments = @(
        '/nologo'
        '/optimize+'
        '/debug-'
        '/platform:anycpu'
        '/warn:4'
        '/warnaserror+'          # warnings are build failures: this is the lint gate
        '/langversion:5'
        "/target:$Target"
        "/out:$Output"
    )
    foreach ($reference in $References) { $arguments += "/reference:$reference" }
    if ($Icon) { $arguments += "/win32icon:$Icon" }
    if ($Manifest) { $arguments += "/win32manifest:$Manifest" }
    $arguments += $Sources

    # Not $output: PowerShell variable names are case-insensitive, so that would
    # overwrite the $Output parameter.
    $compilerOutput = & $script:Csc @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $compilerOutput | ForEach-Object { Write-Host $_ }
        throw "Compilation failed for $Output"
    }
    Write-Host ("  built  {0}" -f (Split-Path $Output -Leaf))
}

# ------------------------------------------------------------------ build ----
$script:Csc = Get-CSharpCompiler
Write-Host "AFKLocker build"
Write-Host ("  csc    {0}" -f $script:Csc)

$icon = Join-Path $assets 'afklocker.ico'
if (-not (Test-Path $icon)) {
    Write-Host "  icon assets missing - generating them first"
    & (Join-Path $PSScriptRoot 'Build-Icon.ps1') | Out-Null
}

if (Test-Path $OutputDirectory) { Remove-Item $OutputDirectory -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$versionInfo = Join-Path $src 'AssemblyVersion.cs'
$manifest = Join-Path $src 'app.manifest'

# Wrap each glob in @() before concatenating: a directory with a single .cs
# file yields a string, and "string + string" concatenates instead of building
# a list.
function Get-Sources {
    param([string]$Directory, [switch]$IncludeVersionInfo)
    # -Recurse so subfolders (Core\Diagnostics) are picked up.
    $files = @(Get-ChildItem (Join-Path $root $Directory) -Filter *.cs -Recurse | ForEach-Object { $_.FullName })
    if ($IncludeVersionInfo) { $files = $files + @($versionInfo) }
    return $files
}

$coreDll = Join-Path $OutputDirectory 'AFKLocker.Core.dll'
Invoke-Csc -Target 'library' -Output $coreDll `
    -Sources (Get-Sources 'src\AFKLocker.Core' -IncludeVersionInfo) `
    -References @('System.dll', 'System.Core.dll', 'System.IO.Compression.dll',
                  'System.IO.Compression.FileSystem.dll')

Invoke-Csc -Target 'winexe' -Output (Join-Path $OutputDirectory 'AFKLocker.exe') `
    -Sources (Get-Sources 'src\AFKLocker.App' -IncludeVersionInfo) `
    -References @('System.dll', 'System.Windows.Forms.dll', $coreDll) `
    -Icon $icon -Manifest $manifest

Invoke-Csc -Target 'winexe' -Output (Join-Path $OutputDirectory 'AFKLockerSetup.exe') `
    -Sources (Get-Sources 'src\AFKLocker.Setup' -IncludeVersionInfo) `
    -References @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', $coreDll) `
    -Icon $icon -Manifest $manifest

# The watcher is a windowless program: winexe so it never shows a console,
# even when run with --status from a terminal.
Invoke-Csc -Target 'winexe' -Output (Join-Path $OutputDirectory 'AFKLockerWatcher.exe') `
    -Sources (Get-Sources 'src\AFKLocker.Watcher' -IncludeVersionInfo) `
    -References @('System.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', $coreDll) `
    -Icon $icon -Manifest $manifest

$testExe = Join-Path $OutputDirectory 'AFKLocker.Tests.exe'
Invoke-Csc -Target 'exe' -Output $testExe `
    -Sources (Get-Sources 'tests\AFKLocker.Tests') `
    -References @('System.dll', 'System.Core.dll', 'System.IO.Compression.dll',
                  'System.IO.Compression.FileSystem.dll', $coreDll)

# ----------------------------------------------------------------- signing ----
# Before the installer, so the files it packages are the signed ones.
$signScript = Join-Path $PSScriptRoot 'Sign.ps1'
if ($Sign -or $RequireSigning) {
    Write-Host ""
    Write-Host "Signing binaries"
    $binaries = @(
        (Join-Path $OutputDirectory 'AFKLocker.exe')
        (Join-Path $OutputDirectory 'AFKLockerSetup.exe')
        (Join-Path $OutputDirectory 'AFKLockerWatcher.exe')
        (Join-Path $OutputDirectory 'AFKLocker.Core.dll')
    )
    & $signScript -Files $binaries -Require:$RequireSigning -AllowUntrustedRoot:$AllowUntrustedSigningRoot
    if ($LASTEXITCODE -ne 0) { throw "Signing failed." }
}

# ------------------------------------------------------------------ tests ----
if ($Test) {
    Write-Host ""
    Write-Host "Running tests"
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

# -------------------------------------------------------------- installer ----
if ($Installer) {
    Write-Host ""
    Write-Host "Building installer"

    if (-not $Iscc) {
        $isccCandidates = @(
            (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
            'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
            'C:\Program Files\Inno Setup 6\ISCC.exe'
        ) | Where-Object { $_ -and (Test-Path $_) }
        $Iscc = $isccCandidates | Select-Object -First 1
    }
    if (-not $Iscc -or -not (Test-Path $Iscc)) {
        throw "Could not find ISCC.exe (Inno Setup compiler). Pass -Iscc <path>."
    }

    $script = Join-Path $root 'installer\AFKLocker.iss'
    & $Iscc "/O$OutputDirectory" $script
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }

    if ($Sign -or $RequireSigning) {
        $setupExe = Get-ChildItem $OutputDirectory -Filter 'AFKLocker-*-setup.exe' |
            Select-Object -First 1
        if (-not $setupExe) { throw "The installer was not found after building it." }
        Write-Host ""
        Write-Host "Signing installer"
        & $signScript -Files @($setupExe.FullName) -Require:$RequireSigning -AllowUntrustedRoot:$AllowUntrustedSigningRoot
        if ($LASTEXITCODE -ne 0) { throw "Signing the installer failed." }
    }
}

Write-Host ""
Write-Host ("Output: {0}" -f $OutputDirectory)
Get-ChildItem $OutputDirectory | Where-Object { -not $_.PSIsContainer } |
    Select-Object Name, @{ Name = 'KB'; Expression = { [math]::Round($_.Length / 1KB, 1) } } |
    Format-Table -AutoSize | Out-String | Write-Host
