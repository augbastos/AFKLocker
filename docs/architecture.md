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

## Three executables, and only one that can be resident

| Binary | Type | Job |
|---|---|---|
| `AFKLocker.exe` | `winexe` | Lock the session and exit. |
| `AFKLockerSetup.exe` | `winexe` | Check readiness, configure, restore, choose lock mode. |
| `AFKLockerWatcher.exe` | `winexe` | Optional. Lock when the lid closes. |
| `AFKLocker.Core.dll` | library | All the logic worth testing. |

All are `winexe` rather than `exe` so nothing ever flashes a console window. That single compiler
flag is most of what "feels like a real utility" means in practice.

**AFKLocker remains non-resident by default. Automatic lid lock is explicitly opt-in, and is the
only feature that requires a user-session background process.**

The original design note said "no background service, no tray icon, no scheduled task", and that
is still true of manual mode and still the reason it is the default: what keeps the machine awake
with the lid closed is the Windows power configuration, which persists on its own. A process
holding that open would add a failure mode, an attack surface and a thing to uninstall, in
exchange for nothing.

Automatic locking is different in kind. Nothing in the power configuration can express "lock when
the lid closes" - that is not a power setting, it is a reaction to an event, and something has to
be listening. So the watcher exists, and the cost is contained deliberately:

- it runs **only** when the user switches the mode on;
- it is a **user-session process**, not a service - it must be in the interactive session to
  receive `GUID_LIDSWITCH_STATE_CHANGE` and to lock that session, and it needs no elevation to do
  either;
- it **does not poll**. The process is blocked in a message loop until Windows posts an event.
  Idle it holds roughly 5 MB and no measurable CPU;
- it has **one job**. It does not change power settings, hold execution state, or keep the
  machine awake. Those responsibilities stay with the power configuration, and Setup shows the
  two separately so nobody assumes one implies the other.

The startup working set is handed back with `SetProcessWorkingSetSize(-1, -1)` once registration
is done: a process that will sit idle for hours has no business holding the pages the CLR touched
while starting. That took idle memory from about 23 MB to about 5 MB.

## Autostart: HKCU Run

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, one value, written when automatic mode is
turned on and deleted when it is turned off or the program is uninstalled.

Considered and rejected:

- **A Windows service.** Wrong session. A service runs in session 0 and cannot lock the user's
  interactive session or receive its power notifications; it would also need administrator rights
  to install, for a feature that otherwise needs none.
- **A scheduled task.** It works, but it is a heavier object to create, inspect and remove, and
  it can require elevation depending on how it is registered. Nothing about this problem needs a
  scheduler.
- **The Startup folder.** Roughly equivalent, but it means shipping and cleaning up a `.lnk`
  rather than setting and deleting a string.

The Run key is the smallest mechanism that does the job, needs no elevation, is one value to
remove, and — the part that matters most — is **visible to the user** in Task Manager's Startup
tab. A background process that starts itself should be findable by the person whose machine it is.

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

## The watcher handshake: running is not ready

Three named kernel objects in the session's namespace (`Local\`), so each signed-in user has their
own and switching users does not cross the wires:

| Object | Meaning |
|---|---|
| `Local\AFKLocker.Watcher.Running` | A mutex held for the lifetime of the process |
| `Local\AFKLocker.Watcher.Ready` | An event set once the watcher can actually receive lid events |
| `Local\AFKLocker.Watcher.Stop` | An event set to ask the watcher to exit |

**Running and Ready are separate because they answer different questions.** The process claims the
mutex almost immediately, long before the runtime is up, the notification window exists, or
Windows has accepted the lid registration. Waiting on the mutex alone therefore proves nothing: a
watcher that fails to register for lid events would hold it and look perfectly healthy while never
locking anything.

Ready is set only after every step needed to do the job has succeeded - single instance, session
tracking started, window created, `RegisterPowerSettingNotification` accepted, handlers attached -
and is reset the moment the process starts going away, so a dying watcher never leaves a ready
signal behind.

`WatcherController.Start` waits for **Ready**, the process to die, or a timeout, whichever comes
first. A watcher that cannot register exits with a distinct code and the caller learns
immediately rather than waiting out the full timeout. That gives four states the UI can report
honestly instead of one boolean: `NotRunning`, `Starting`, `Ready`, `Unhealthy`.

## Enabling automatic mode is transactional

Turning automatic mode on touches three things - the saved mode, the autostart entry, the running
process - and any of them can fail. Half-applied is the worst outcome: a machine that believes it
is protected and is not.

So each step records how to undo itself, and a failure unwinds the ones before it. `Enable` leaves
exactly one of two states behind:

1. **Automatic and genuinely working** - mode saved, autostart registered, watcher Ready.
2. **Manual and clean** - previous mode restored, no autostart, no watcher.

There is deliberately no third outcome. What the previous state *was* is restored, not assumed: an
autostart entry that already pointed somewhere else is put back as it was rather than deleted.

Rollback can itself fail, and that is reported rather than smoothed over. `AutoLockResult`
distinguishes "it failed and everything was put back" from "it failed and something is still
half-configured", and lists what could not be undone. The two call for different reactions from
the user, so they are not flattened into one boolean.

`Disable` and `Cleanup` work the other way round: there is nothing to roll back to, because the
safest reachable state is always "manual and clean". Every step is attempted even if an earlier
one fails, and residue is reported.

`Reconcile` runs when the setup window opens. State drifts for ordinary reasons - the watcher was
killed in Task Manager, a cleanup tool removed the startup entry, a crash left a stale signal - and
repairing it there means the window shows a working machine instead of a puzzle. If automatic mode
cannot be restored, it converges to manual and clean rather than leaving the machine in between.

## Diagnostics: everything the user chooses to share, nothing else

`SelfTest` is deliberately free of any UI, so every path - including the awkward ones - is driven
from tests with fakes. It reads the environment, the power configuration and AFKLocker's own
state, optionally exercises the watcher handshake, and returns a report.

The rule that shapes the whole design: **a bundle is meant to be sent to a stranger.** So:

- Every path goes through `PathRedactor` before it reaches the report. Known folders become
  `%LOCALAPPDATA%` and friends; the user name is removed wherever it appears; and a path outside
  every known folder is reduced to its file name, because a folder named after a person or a
  client is exactly the kind of thing that would otherwise survive.
- A **custom power plan's name is never reported** - only that it is custom. Users rename those,
  sometimes after themselves.
- Manufacturer and model are opt-in and default to off.
- Nothing enumerates processes, installed programs, network adapters or environment variables, and
  no registry is read outside two well-known version keys and AFKLocker's own values.

The self-test restores what it touches: if automatic mode was on before, it is on afterwards. A
diagnostic that left the machine worse than it found it would be worse than no diagnostic.

The privacy check in the test suite searches the exported bundle for *shapes* of personal data -
MAC addresses, IPs, emails, un-redacted profile paths - rather than for topic words, because the
privacy notice itself mentions "IP or MAC addresses" while promising not to include any. It is
written as a search for what must not be there, so that data added carelessly later is caught.

## Lid events: the window is not message-only

The watcher owns a hidden window and registers it with
`RegisterPowerSettingNotification(hwnd, &GUID_LIDSWITCH_STATE_CHANGE, DEVICE_NOTIFY_WINDOW_HANDLE)`.
Windows then posts `WM_POWERBROADCAST` / `PBT_POWERSETTINGCHANGE`, and the payload is a
`POWERBROADCAST_SETTING` whose `Data` is a DWORD: `0` closed, `1` opened.

That window is an ordinary top-level window that is simply never shown, with `WS_EX_TOOLWINDOW` to
keep it out of the taskbar and Alt+Tab — **not** a message-only (`HWND_MESSAGE`) window.
Message-only windows are documented as not receiving broadcast messages, and `WM_POWERBROADCAST`
is one. Registration does deliver directly to the registered handle, so a message-only window may
well work, but relying on that is betting on an implementation detail to save nothing.

Three behaviours fall out of the documentation and are worth stating:

- **The first event is a position, not a change.** Windows reports the current lid state as soon
  as you register. Acting on it would lock the session of someone working on an external monitor
  with the laptop docked shut, which is the worst possible moment to do it. The policy ignores the
  first event it sees, and does the same after a resume, when the lid may have moved unobserved.
- **No event at all is the "no lid" answer.** The docs say the callback is not made "until a lid
  device is found and its current state is known". Registration succeeds on a desktop; nothing
  ever arrives. There is no up-front way to distinguish that from a lid that has not moved yet, so
  the UI says what it knows rather than claiming support.
- **Opening never unlocks.** Not after resume, not after a watcher restart, not ever. Security
  beats convenience, and the code has no unlock path at all.

## Everything testable is behind an interface

`ISessionLocker` exists for one reason: a test that called `LockWorkStation` for real would lock
the machine running the test suite. `IPowerConfiguration`, `IPowerInformation` and `IBackupStore`
exist so the test suite can describe machines that don't exist here - a desktop with no lid, a
laptop with hibernation on, a domain machine where every write is refused.

Automatic lock adds `ILidEventProvider` (so lid events can be simulated with no laptop),
`ISessionState`, `IAutostartRegistry` and `IWatcherProcess` (so enabling and disabling can be
tested without writing to the registry or launching anything).

The logic that decides things (`ReadinessEvaluator`, `ConfigurationPlanner`, `PowerBackup`,
`AutoLockPolicy`) is pure: it takes input and returns a verdict. That is where the tests
concentrate. `AutoLockPolicy` in particular is where every awkward lid case is decided - first
event, repeats, opening, an already-locked session - so each one is a test rather than a comment.

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
