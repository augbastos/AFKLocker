# Architecture

Short notes on why AFKLocker is built the way it is. The decisions here were made for a utility
that does one small thing on Windows and should keep working with no maintenance.

## .NET Framework 4.8, compiled with `csc.exe`

**Decision:** C# targeting .NET Framework 4.8, compiled directly with the `csc.exe` that lives in
`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`.

**Why:**

- **Nothing to install for the user.** .NET Framework 4.8 is part of Windows 10 (1903+) and
  Windows 11. `AFKLocker.exe` is ~47 KB. A .NET 8 build would have meant either a runtime
  dependency or a ~70 MB self-contained executable, for a program whose job is to call one Win32
  function.
- **Nothing to install for the builder.** No SDK, no NuGet restore, no lockfile. A clean Windows
  machine and a clean CI runner both build it with what they already have, and get the same
  result.
- **P/Invoke is first-class.** The whole product is Windows power management API calls. There is
  no cross-platform story to preserve and nothing to gain from a portable runtime.

**Cost, stated honestly:** that compiler only supports **C# 5**. No string interpolation, no
`nameof`, no exception filters, no expression-bodied members. The code reads slightly older than
modern C# as a direct result. For roughly 1,200 lines that never change shape, that was judged a
fair trade for a zero-dependency build.

`tools/Build.ps1` passes `/warnaserror+` with `/warn:4`, so compiler warnings fail the build.
That is the static analysis gate; there is no separate linter.

## Two executables, neither resident

| Binary | Type | Job |
|---|---|---|
| `AFKLocker.exe` | `winexe` | Lock the session and exit. |
| `AFKLockerSetup.exe` | `winexe` | Check readiness, configure, restore. |
| `AFKLocker.Core.dll` | library | All the logic worth testing. |

Both are `winexe` rather than `exe` so double-clicking never flashes a console window. That
single compiler flag is most of what "feels like a real utility" means in practice.

**No background service, no tray icon, no scheduled task.** What keeps the machine awake with the
lid closed is the Windows power configuration, which persists on its own. A resident process
would add a failure mode (what if it crashes?), an attack surface, and a thing to uninstall, in
exchange for nothing. AFKLocker runs for a few milliseconds and exits.

## The power API, not `powercfg.exe`

Settings are read and written through `powrprof.dll`: `PowerGetActiveScheme`,
`PowerReadACValueIndex` / `PowerReadDCValueIndex`, `PowerWriteACValueIndex` /
`PowerWriteDCValueIndex`, `PowerSetActiveScheme`, and `CallNtPowerInformation` for hardware
capabilities.

Shelling out to `powercfg.exe` and parsing its output would have been less code. It also would
have broken on every non-English Windows, because that output is localized. The API returns raw
DWORDs and GUIDs, which mean the same thing everywhere.

One thing this surfaced: `SYSTEM_POWER_CAPABILITIES.LidPresent` returns **false** on the laptop
this was developed on, which certainly has a lid. So readiness checks key off whether the lid
close *setting* exists, and the capability flag is only used to word a message.

## Everything testable is behind an interface

`ISessionLocker` exists for one reason: a test that called `LockWorkStation` for real would lock
the machine running the test suite. `IPowerConfiguration`, `IPowerInformation` and `IBackupStore`
exist so the test suite can describe machines that don't exist here - a desktop with no lid, a
laptop with hibernation on, a domain machine where every write is refused.

The logic that decides things (`ReadinessEvaluator`, `ConfigurationPlanner`, `PowerBackup`) is
pure: it takes a snapshot and returns a verdict. That is where the tests concentrate.

## Backups are plain text, one file per power plan

`%LOCALAPPDATA%\AFKLocker\power-backup-<scheme guid>.txt`, in `key=value` form.

- **Plain text** because the person most likely to read this file is someone whose machine is
  behaving strangely. They should be able to open it and understand it without tooling.
- **One file per scheme** because a user can configure AFKLocker on Balanced, later switch to High
  performance and configure that too. Restore has to put each plan back the way it was, and has to
  target the plan the backup came from rather than whichever is active at uninstall time.
- **First value wins.** `RecordOriginal` refuses to overwrite an existing entry. Running Setup a
  second time must not record the values AFKLocker itself just wrote, or restore would "restore"
  the machine to the configured state.
- **Saved before writing.** If a write fails halfway through, the record of the original state
  already exists on disk.

## Testing without a framework

`tests/AFKLocker.Tests` is a console executable with a ~120-line runner that reflects over
`[Test]` methods and returns a non-zero exit code on failure.

This is a deliberate consequence of the build decision above: adding xUnit or NUnit would have
meant adding NuGet restore, which is exactly the dependency the build was designed to avoid. For
a project of this size the trade is worth it. For anything larger it would not be.

## Installer

Inno Setup 6, script at `installer/AFKLocker.iss`, per-user by default
(`PrivilegesRequired=lowest`) so getting a desktop shortcut does not require an elevation prompt.
Changing power settings on the user's own plan does not need administrator rights on a standard
Windows install; when Windows does refuse, Setup detects `ERROR_ACCESS_DENIED` and offers to
relaunch elevated.

Uninstall asks whether to restore the power settings rather than doing it silently, because
"leave my machine configured this way, just remove the shortcut" is a legitimate answer.
