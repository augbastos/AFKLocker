<#
.SYNOPSIS
    End-to-end check of turning automatic lid lock on and off.

.DESCRIPTION
    Unit tests cover the decision logic with fakes. This exercises the real
    thing: the real settings file, the real HKCU Run key, and a real watcher
    process. It does not touch power settings and never closes a lid, so it
    cannot lock the machine running it.

    Everything it changes, it changes back - including restoring whatever lock
    mode was configured before it ran.

.NOTES
    Not part of CI: it starts a process and writes to the registry.
    Run it against an installed build, or against build\ after tools\Build.ps1.
#>
[CmdletBinding()]
param(
    [string]$AppDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build')
)

$ErrorActionPreference = 'Stop'

$coreDll = Join-Path $AppDirectory 'AFKLocker.Core.dll'
$watcherExe = Join-Path $AppDirectory 'AFKLockerWatcher.exe'
if (-not (Test-Path $coreDll)) { throw "Build first: $coreDll not found." }
if (-not (Test-Path $watcherExe)) { throw "Build first: $watcherExe not found." }

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

function Get-AutostartValue {
    (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).$runValue
}

function New-Manager {
    $settings = New-Object AFKLocker.Core.FileSettingsStore
    $autostart = New-Object AFKLocker.Core.RunKeyAutostartRegistry
    $watcher = New-Object AFKLocker.Core.WatcherController
    return New-Object AFKLocker.Core.AutoLockManager($settings, $autostart, $watcher, $watcherExe)
}

# Remember the starting state so this script leaves no trace.
$originalMode = (New-Object AFKLocker.Core.FileSettingsStore).Load().Mode
$originalAutostart = Get-AutostartValue
Write-Host "Auto lock integration test"
Write-Host ("  starting mode: {0}" -f $originalMode)

# Power settings must be untouched by anything in this test.
$lidBefore = (powercfg /q SCHEME_CURRENT SUB_BUTTONS LIDACTION | Select-String 'Current AC').ToString().Trim()
$sleepBefore = (powercfg /q SCHEME_CURRENT SUB_SLEEP STANDBYIDLE | Select-String 'Current AC').ToString().Trim()

try {
    # ---------------------------------------------------------------- enable ---
    $manager = New-Manager
    Check "enable reports success" $true $manager.Enable()
    Start-Sleep -Milliseconds 800

    Check "settings file written" $true (Test-Path $settingsPath)
    Check "mode recorded as automatic" "Automatic" (New-Object AFKLocker.Core.FileSettingsStore).Load().Mode
    Check "autostart registered" $true ((Get-AutostartValue) -ne $null)
    Check "autostart points at the watcher" $true ((Get-AutostartValue) -like "*AFKLockerWatcher.exe*")
    Check "watcher is running" $true ([AFKLocker.Core.WatcherController]::IsAnyRunning)

    $status = $manager.GetStatus()
    Check "status reports automatic" "Automatic" $status.Mode
    Check "status reports watcher running" $true $status.WatcherRunning
    Check "status reports watcher installed" $true $status.WatcherInstalled

    # ------------------------------------------------- watcher stays in its lane ---
    $lidDuring = (powercfg /q SCHEME_CURRENT SUB_BUTTONS LIDACTION | Select-String 'Current AC').ToString().Trim()
    $sleepDuring = (powercfg /q SCHEME_CURRENT SUB_SLEEP STANDBYIDLE | Select-String 'Current AC').ToString().Trim()
    Check "watcher did not touch the lid setting" $lidBefore $lidDuring
    Check "watcher did not touch the sleep timeout" $sleepBefore $sleepDuring

    $process = Get-Process AFKLockerWatcher -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($process) {
        Check "watcher has no window" 0 $process.MainWindowHandle
        Write-Host ("  info  idle working set: {0:N1} MB" -f ($process.WorkingSet64 / 1MB))

        # The README promises zero network requests. Check it rather than assert it.
        $connections = @(Get-NetTCPConnection -OwningProcess $process.Id -ErrorAction SilentlyContinue)
        $udp = @(Get-NetUDPEndpoint -OwningProcess $process.Id -ErrorAction SilentlyContinue)
        Check "watcher holds no TCP connections" 0 $connections.Count
        Check "watcher holds no UDP endpoints" 0 $udp.Count

        # And it must not be holding the machine awake by itself.
        $requests = (powercfg /requests) -join "`n"
        Check "watcher holds no power requests" $false ($requests -match 'AFKLockerWatcher')
    }

    # A second launch must not produce a second watcher.
    Start-Process $watcherExe -Wait | Out-Null
    Start-Sleep -Milliseconds 500
    $count = (Get-Process AFKLockerWatcher -ErrorAction SilentlyContinue | Measure-Object).Count
    Check "still exactly one watcher process" 1 $count

    # --------------------------------------------------------------- disable ---
    Check "disable reports success" $true (New-Manager).Disable()
    Start-Sleep -Milliseconds 500

    Check "watcher stopped" $false ([AFKLocker.Core.WatcherController]::IsAnyRunning)
    Check "no watcher process left" 0 (Get-Process AFKLockerWatcher -ErrorAction SilentlyContinue | Measure-Object).Count
    Check "autostart removed" $true ((Get-AutostartValue) -eq $null)
    Check "mode back to manual" "Manual" (New-Object AFKLocker.Core.FileSettingsStore).Load().Mode

    # ------------------------------------------------------- uninstall cleanup ---
    (New-Manager).Enable() | Out-Null
    Start-Sleep -Milliseconds 800
    Check "re-enabled for the cleanup check" $true ([AFKLocker.Core.WatcherController]::IsAnyRunning)

    Check "cleanup reports success" $true (New-Manager).Cleanup()
    Start-Sleep -Milliseconds 500
    Check "cleanup stopped the watcher" $false ([AFKLocker.Core.WatcherController]::IsAnyRunning)
    Check "cleanup removed autostart" $true ((Get-AutostartValue) -eq $null)
}
finally {
    # Leave the machine exactly as it was found.
    (New-Manager).Cleanup() | Out-Null
    if ($originalMode -eq 'Automatic') {
        (New-Manager).Enable() | Out-Null
    }
    elseif (Test-Path $settingsPath) {
        Remove-Item $settingsPath -Force -ErrorAction SilentlyContinue
    }
    if ($originalAutostart) {
        Set-ItemProperty $runKey -Name $runValue -Value $originalAutostart
    }
    Write-Host ("  restored mode: {0}" -f (New-Object AFKLocker.Core.FileSettingsStore).Load().Mode)
}

Write-Host ""
if ($failures -gt 0) { Write-Host "$failures check(s) failed."; exit 1 }
Write-Host "Auto lock integration checks passed."
