<#
.SYNOPSIS
    Proves the enable rollback against the real registry, settings file and
    watcher process.

.DESCRIPTION
    The unit tests cover rollback with fakes. This forces a real failure - a
    watcher path that exists but is not a watcher, so it starts and never
    signals readiness - and checks that the machine is left in the clean manual
    state rather than half-configured.

    It restores whatever mode was configured before it ran.

.NOTES
    Not part of CI: it writes to HKCU and starts a process.
#>
[CmdletBinding()]
param(
    [string]$AppDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build')
)

$ErrorActionPreference = 'Stop'

$coreDll = Join-Path $AppDirectory 'AFKLocker.Core.dll'
if (-not (Test-Path $coreDll)) { throw "Build first: $coreDll not found." }
Add-Type -Path $coreDll

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'AFKLocker Watcher'
$settingsPath = Join-Path $env:LOCALAPPDATA 'AFKLocker\settings.txt'

$failures = 0
function Check {
    param([string]$What, $Expected, $Actual)
    if ("$Expected" -eq "$Actual") { Write-Host "  PASS  $What" }
    else { Write-Host ("  FAIL  {0} (expected {1}, got {2})" -f $What, $Expected, $Actual); $script:failures++ }
}

function Get-AutostartValue { (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).$runValue }

$originalMode = (New-Object AFKLocker.Core.FileSettingsStore).Load().Mode
$originalAutostart = Get-AutostartValue
Write-Host "Rollback integration test"
Write-Host ("  starting mode: {0}" -f $originalMode)

# A real file that is not a watcher: it starts, exits immediately, and never
# signals readiness. Exactly the failure the rollback exists for.
$impostor = Join-Path $env:TEMP ("afklocker-impostor-" + [guid]::NewGuid().ToString('N') + ".exe")
Copy-Item (Join-Path $env:WINDIR 'System32\where.exe') $impostor

try {
    # A short readiness timeout keeps the test quick; the production default is
    # longer, but the code path is identical.
    $watcher = New-Object AFKLocker.Core.WatcherController([TimeSpan]::FromSeconds(4))
    $manager = New-Object AFKLocker.Core.AutoLockManager(
        (New-Object AFKLocker.Core.FileSettingsStore),
        (New-Object AFKLocker.Core.RunKeyAutostartRegistry),
        $watcher, $impostor)

    Write-Host "  forcing a watcher that can never become ready..."
    $result = $manager.Enable()

    Check "enable reports failure" $false $result.Success
    Check "the failure is named" $true ($result.Failure -ne 'None')
    Check "a rollback was performed" $true $result.RolledBack
    Check "the rollback was clean" $true $result.IsClean
    Write-Host ("  info  failure: {0} - {1}" -f $result.Failure, $result.Message)

    # The point of the whole exercise: nothing half-applied survives.
    $modeAfter = (New-Object AFKLocker.Core.FileSettingsStore).Load().Mode
    Check "mode was rolled back" "$originalMode" "$modeAfter"
    Check "no autostart entry was left behind" $true ((Get-AutostartValue) -eq $originalAutostart)
    Check "no watcher process was left running" 0 `
        (Get-Process AFKLockerWatcher -ErrorAction SilentlyContinue | Measure-Object).Count
    Check "no impostor process was left running" 0 `
        (Get-Process ([IO.Path]::GetFileNameWithoutExtension($impostor)) -ErrorAction SilentlyContinue | Measure-Object).Count
    Check "no stale readiness signal" $false ([AFKLocker.Core.WatcherController]::IsAnyReady)

    # And the machine is still usable afterwards: a real enable still works.
    $realWatcher = Join-Path $AppDirectory 'AFKLockerWatcher.exe'
    if (Test-Path $realWatcher) {
        $good = New-Object AFKLocker.Core.AutoLockManager(
            (New-Object AFKLocker.Core.FileSettingsStore),
            (New-Object AFKLocker.Core.RunKeyAutostartRegistry),
            (New-Object AFKLocker.Core.WatcherController), $realWatcher)
        $recovered = $good.Enable()
        Check "a real enable still succeeds after the failed one" $true $recovered.Success
        $good.Disable() | Out-Null
    }
}
finally {
    $watcher = New-Object AFKLocker.Core.WatcherController
    $cleanup = New-Object AFKLocker.Core.AutoLockManager(
        (New-Object AFKLocker.Core.FileSettingsStore),
        (New-Object AFKLocker.Core.RunKeyAutostartRegistry),
        $watcher, (Join-Path $AppDirectory 'AFKLockerWatcher.exe'))
    $cleanup.Cleanup() | Out-Null

    if ($originalMode -eq 'Automatic') { $cleanup.Enable() | Out-Null }
    elseif (Test-Path $settingsPath) { Remove-Item $settingsPath -Force -ErrorAction SilentlyContinue }
    if ($originalAutostart) { Set-ItemProperty $runKey -Name $runValue -Value $originalAutostart }

    Remove-Item $impostor -Force -ErrorAction SilentlyContinue
    Write-Host ("  restored mode: {0}" -f (New-Object AFKLocker.Core.FileSettingsStore).Load().Mode)
}

Write-Host ""
if ($failures -gt 0) { Write-Host "$failures check(s) failed."; exit 1 }
Write-Host "Rollback integration checks passed."
