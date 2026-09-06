<#
.SYNOPSIS
    End-to-end check of apply and restore against real Windows power settings.

.DESCRIPTION
    Unit tests cover the logic against simulated machines. This script exercises
    the real powrprof.dll path, but does it on a throwaway power plan so the
    machine's own configuration is never touched:

      1. duplicate the active plan into a temporary one and activate it
      2. set it to the worst case: sleep on lid close, sleep after 10 minutes
      3. run AFKLocker's plan/apply and verify Windows really changed
      4. run restore and verify the original values came back
      5. reactivate the original plan and delete the temporary one

    The original plan is restored in a finally block, so an interruption still
    leaves the machine on the plan it started with.

.NOTES
    Not part of the CI run: it writes real power settings. Run it locally.
#>
[CmdletBinding()]
param(
    [string]$CoreDll = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build\AFKLocker.Core.dll')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $CoreDll)) { throw "Build first: $CoreDll not found." }
Add-Type -Path $CoreDll

$failures = 0
function Check {
    param([string]$What, $Expected, $Actual)
    if ($Expected -eq $Actual) {
        Write-Host ("  PASS  {0}" -f $What)
    }
    else {
        Write-Host ("  FAIL  {0} (expected {1}, got {2})" -f $What, $Expected, $Actual)
        $script:failures++
    }
}

$originalScheme = (powercfg /getactivescheme) -replace '.*GUID:\s*([0-9a-f-]+).*', '$1'
Write-Host "Integration test - original plan: $originalScheme"

$tempScheme = $null
try {
    # 1. throwaway plan
    $duplicate = powercfg /duplicatescheme $originalScheme
    $tempScheme = ($duplicate -replace '.*GUID:\s*([0-9a-f-]+).*', '$1').Trim()
    if (-not $tempScheme -or $tempScheme -eq $originalScheme) { throw "Could not duplicate the power scheme." }
    Write-Host "  temporary plan: $tempScheme"

    powercfg /changename $tempScheme "AFKLocker integration test" | Out-Null
    powercfg /setactive $tempScheme

    # 2. worst case starting point
    powercfg /setacvalueindex $tempScheme SUB_BUTTONS LIDACTION 1      # sleep on lid close
    powercfg /setacvalueindex $tempScheme SUB_SLEEP STANDBYIDLE 600    # sleep after 10 minutes
    powercfg /setdcvalueindex $tempScheme SUB_BUTTONS LIDACTION 1
    powercfg /setdcvalueindex $tempScheme SUB_SLEEP STANDBYIDLE 300
    powercfg /setactive $tempScheme

    $power = New-Object AFKLocker.Core.WindowsPowerConfiguration
    $info = New-Object AFKLocker.Core.WindowsPowerInformation
    $backupDir = Join-Path $env:TEMP ("afklocker-integration-" + [guid]::NewGuid().ToString('N'))
    $store = New-Object AFKLocker.Core.FileBackupStore($backupDir)

    # 3. it should read as not ready, then apply
    $before = [AFKLocker.Core.PowerSnapshot]::Read($power, $info)
    $reportBefore = [AFKLocker.Core.ReadinessEvaluator]::Evaluate($before)
    Check "a machine that sleeps on lid close reads as not ready" $false $reportBefore.IsReady

    $plan = [AFKLocker.Core.ConfigurationPlanner]::Create($before, $false)
    Check "the plan covers lid and sleep on AC" 2 $plan.Changes.Count

    $configurator = New-Object AFKLocker.Core.PowerConfigurator($power, $store)
    $applyResult = $configurator.Apply($plan)
    Check "both settings applied" 2 $applyResult.Applied.Count

    # verified through powercfg, not through our own API, so a bug in the
    # read path cannot make the write path look correct
    $lidAfter = ((powercfg /q $tempScheme SUB_BUTTONS LIDACTION | Select-String 'Current AC') -split ':')[1].Trim()
    $sleepAfter = ((powercfg /q $tempScheme SUB_SLEEP STANDBYIDLE | Select-String 'Current AC') -split ':')[1].Trim()
    Check "Windows reports lid close = Do nothing" "0x00000000" $lidAfter
    Check "Windows reports sleep = Never" "0x00000000" $sleepAfter

    $afterApply = [AFKLocker.Core.PowerSnapshot]::Read($power, $info)
    Check "the machine now reads as ready" $true ([AFKLocker.Core.ReadinessEvaluator]::Evaluate($afterApply).IsReady)

    $dcUntouched = ((powercfg /q $tempScheme SUB_BUTTONS LIDACTION | Select-String 'Current DC') -split ':')[1].Trim()
    Check "battery settings were left alone" "0x00000001" $dcUntouched

    # 4. restore
    $restoreResult = $configurator.RestoreAll()
    Check "both settings restored" 2 $restoreResult.SettingsRestored

    $lidRestored = ((powercfg /q $tempScheme SUB_BUTTONS LIDACTION | Select-String 'Current AC') -split ':')[1].Trim()
    $sleepRestored = ((powercfg /q $tempScheme SUB_SLEEP STANDBYIDLE | Select-String 'Current AC') -split ':')[1].Trim()
    Check "lid close is back to Sleep" "0x00000001" $lidRestored
    Check "sleep is back to 600 seconds" "0x00000258" $sleepRestored
    Check "the backup was consumed" $false $configurator.HasBackup

    if (Test-Path $backupDir) { Remove-Item $backupDir -Recurse -Force }
}
finally {
    # Always put the machine back on the plan it started on.
    if ($originalScheme) { powercfg /setactive $originalScheme }
    if ($tempScheme -and $tempScheme -ne $originalScheme) {
        powercfg /delete $tempScheme 2>&1 | Out-Null
        Write-Host "  temporary plan deleted"
    }
    $nowActive = (powercfg /getactivescheme) -replace '.*GUID:\s*([0-9a-f-]+).*', '$1'
    Write-Host ("  active plan restored: {0}" -f $nowActive)
}

Write-Host ""
if ($failures -gt 0) {
    Write-Host "$failures check(s) failed."
    exit 1
}
Write-Host "Integration checks passed."
