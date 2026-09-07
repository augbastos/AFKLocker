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
modern C# as a direct result. For a codebase of this size and shape - a few thousand lines of
source, heavily commented, with no framework surface to speak of - that was judged a fair trade
for a zero-dependency build. It has cost real time exactly twice, both times as a compile error
rather than a bug: an exception filter and a string interpolation, each rewritten in a minute.

`tools/Build.ps1` passes `/warnaserror+` with `/warn:4`, so compiler warnings fail the build.
That is the static analysis gate; there is no separate linter.

## Three executables, and what each one's lifetime actually is

| Binary | Type | Job |
|---|---|---|
| `AFKLocker.exe` | `winexe` | Lock the session, then keep the screens dark until it is unlocked. |
| `AFKLockerSetup.exe` | `winexe` | Check readiness, configure, restore, choose lock mode. |
| `AFKLockerWatcher.exe` | `winexe` | Optional background helper: lid lock, global hotkey. |
| `AFKLocker.Core.dll` | library | All the logic worth testing. |

All are `winexe` rather than `exe` so nothing ever flashes a console window. That single compiler
flag is most of what "feels like a real utility" means in practice.

**AFKLocker remains non-resident by default. Both features that need a background process are
explicitly opt-in and off by default.**

The original design note said "no background service, no tray icon, no scheduled task", and that
is still true with both features off, and still the reason that is the default: what keeps the
machine awake with the lid closed is the Windows power configuration, which persists on its own. A
process holding that open would add a failure mode, an attack surface and a thing to uninstall, in
exchange for nothing.

### Residency follows the features, not the lock mode

`AFKLockerWatcher.exe` began as a lid watcher and is now a helper with two separate jobs. The file
name has not changed, because renaming it would rewrite every existing user's sign-in entry to buy
nothing.

| Automatic | Global hotkey | Helper |
|---|---|---|
| off | off | none |
| off | on | runs, for the hotkey only |
| on | off | runs, for the lid only |
| on | on | one process, both jobs |

`LockMode.Manual` therefore no longer implies "no helper". The desired state is derived from
`AutoLockSettings.RequiredFeatures`, and every part of the lifecycle - status, reconciliation,
enable, disable - asks that rather than the mode. The two mistakes this prevents are both silent:
killing a helper that the other feature still needs, and leaving one running when nothing does.

Neither feature can be expressed as a power setting. "Lock when the lid closes" and "lock when
this key is pressed" are reactions to events, and something has to be listening. So the helper
exists, and the cost is contained deliberately:

- it runs **only** when the user switches one of the two features on;
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

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, one value, written when a feature that needs
the helper is turned on and deleted when the last one is turned off, or the program is
uninstalled.

### The entry is validated, not merely counted

"A value exists" is not the question. An entry left behind by an uninstalled copy in a folder that
no longer exists satisfies it, looks healthy in every status screen, and starts nothing. So the
entry is parsed and compared instead, and reported as one of five states: absent, correct, wrong
target, malformed, or unreadable.

Comparing the strings would be wrong in both directions. The same command can be written many ways
— quoted or not, different casing, an environment variable, a short 8.3 path, a trailing space —
and none of those are a real difference. Meanwhile `D:\Old\AFKLockerWatcher.exe` differs from the
installed helper only in the part a `Contains("AFKLockerWatcher.exe")` check throws away. So:

- the command is split into an executable and arguments, handling the genuine ambiguity of an
  unquoted path with spaces the way Windows does, by trying each prefix until one exists;
- the executable is canonicalised: environment variables expanded, `GetFullPath` applied, 8.3
  names expanded through `GetLongPathName`, trailing separators dropped;
- the comparison is case-insensitive on the result, and arguments must match too, because
  AFKLocker writes none.

Canonicalising deliberately does **not** require the file to exist. An entry pointing at a deleted
folder must still be recognisable as pointing elsewhere rather than collapsing into "unknown".

`Reconcile()` rewrites a wrong entry rather than tolerating it, which is what turns this from a
report into a repair.

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
| `Local\AFKLocker.Watcher.Ready` | An event set once every enabled feature is genuinely working |
| `Local\AFKLocker.Watcher.Stop` | An event set to ask the helper to exit |

**Running and Ready are separate because they answer different questions.** The process claims the
mutex almost immediately, long before the runtime is up, the notification window exists, or
Windows has accepted the lid registration. Waiting on the mutex alone therefore proves nothing: a
watcher that fails to register for lid events would hold it and look perfectly healthy while never
locking anything.

Ready is set only after every step needed to do the job has succeeded, and **what that means
depends on which features are switched on**:

| Enabled | Ready means |
|---|---|
| Automatic only | `RegisterPowerSettingNotification` for the lid was accepted |
| Hotkey only | `RegisterHotKey` succeeded for the chosen combination |
| Both | both of the above, or the helper does not signal at all |

It is reset the moment the process starts going away, so a dying helper never leaves a ready
signal behind.

`WatcherController.Start` waits for **Ready**, the process to die, or a timeout, whichever comes
first. A helper that cannot do what it was started for exits with a distinct code and the caller
learns immediately rather than waiting out the full timeout:

| Code | Meaning |
|---|---|
| 2 | Could not register for lid notifications |
| 3 | Another helper already holds this session |
| 4 | Windows would not reserve the hotkey - usually another application has it |
| 5 | Nothing is switched on, so there was nothing to do |

Code 5 matters more than it looks. A helper launched with no features enabled that sat there
idling would make "helper running" stop meaning anything; exiting keeps the state honest.

That gives four states the UI can report honestly instead of one boolean: `NotRunning`,
`Starting`, `Ready`, `Unhealthy`. `Starting` is treated as a contradiction rather than a
transient, because enabling waits for readiness before returning - so a helper found running but
never ready is stuck, not mid-launch.

## The global hotkey: RegisterHotKey, never a keyboard hook

The feature is "lock when I press this key", and there are two ways to build it. A low-level
keyboard hook (`WH_KEYBOARD_LL`) sees **every keystroke on the machine** and decides which one
mattered. `RegisterHotKey` inverts it: Windows is told one combination, keeps the keyboard to
itself, and posts a single `WM_HOTKEY` when exactly that is pressed.

Only the second one lets AFKLocker keep saying it does not monitor what you type, so it is the
only one considered. `MOD_NOREPEAT` is set, so holding the key locks once rather than repeating.

What was measured on a real machine rather than assumed:

- **`VK_APPS` (the Menu key) registers on its own**, with and without `MOD_NOREPEAT`. No hook is
  needed to bind it, which was the open question.
- **Windows reports its own reservations.** F12, `Win`+`L`, `Ctrl`+`Esc`, `Alt`+`Tab` and
  `Ctrl`+`Alt`+`Del` all come back as `ERROR_HOTKEY_ALREADY_REGISTERED` (1409). There is therefore
  no hand-written list of forbidden combinations: the API is asked, and its answer is shown.
- **The one case Windows does not catch** is a bare modifier. `RegisterHotKey` accepts
  `VK_CONTROL`, `VK_SHIFT`, `VK_MENU` and `VK_LWIN` as the key, and then nothing ever fires. That
  looks configured and is not, so `HotkeyBinding` refuses it - the only behaviour-based rule in the
  code, and it exists because the API says yes when it should say no.

Setup asks Windows before saving, by registering the candidate and releasing it in the same
breath, so a conflict is a sentence at configuration time rather than a helper that will not start
later. The probe registers against the calling thread rather than a window, so there is nothing
left to leak if the release fails.

## Turning the screens off is the hardest easy thing here

Locking does not darken a screen, and Windows cannot be relied on to do it
either. Three facts, all measured on a real machine rather than assumed, decide
the whole design:

**1. The console lock display off timeout may never fire.** `VIDEOCONLOCK`
(`8EC4B3A5-6868-48c2-BE75-4F3044BE88A7` under `SUB_VIDEO`) is a hidden setting —
invisible to `powercfg /q`, readable and writable only through the API. On the
development machine it simply does not fire: session locked, no input, minutes
passing, and no `GUID_CONSOLE_DISPLAY_STATE` change at all. Changing its value
does nothing, so AFKLocker does not touch it.

**2. Windows ignores `SC_MONITORPOWER` while there has been recent user input.**
The request returns success and nothing happens. AFKLocker asks about a second
after the click or key that locked the machine, so the first request is very
often discarded. Measured: with 94 seconds of idle, the same request took effect
in 200 milliseconds.

The answer is not a cleverer request, it is persistence — ask again every few
seconds for as long as the session stays locked, and the first attempt after the
person actually leaves is the one that lands.

**3. `SendMessage` to `HWND_BROADCAST` can block forever.** A broadcast is
synchronous against every top-level window on the desktop, so one application
that has stopped pumping messages blocks the whole call with no timeout, and
that gets likelier the longer a machine has been running. Observed live on a
machine with about a week of uptime; the process had to be killed.
`SendMessageTimeout` with `SMTO_ABORTIFHUNG` is used instead — noting that its
timeout is **per window, not total**, so a 2-second timeout was measured taking
9.9 seconds. Sending `SC_MONITORPOWER` to a window of our own instead of
broadcasting does not work at all.

### Knowing when to stop is the harder half

Blanking too little leaves a lit lock screen, which is a nuisance. Blanking too
much darkens the screen of somebody standing at the machine typing their
password, which is a malfunction. The two costs are not symmetrical, so anything
ambiguous waits rather than acts.

Exactly one thing ends the guard: **the session being unlocked**. Everything
else only changes how long it waits.

| Signal | Response |
|---|---|
| Display lights up again | ask again — 1.2s while the lid is still settling, then a slower retry |
| Keyboard or mouse used | leave it alone for 90 seconds, restarted by every further touch |
| Lid opened | the same 90-second pause |
| Request cap reached | back off to a slow retry; **never** give up while locked |
| 12 hours | a backstop for a session that never reports being unlocked |

Two defects here were only ever visible on a real machine, and both are worth
remembering because the code and the tests looked correct:

- **The blanker assumed the display was on when it started.** The first request
  usually lands, so the display is already off and Windows sends no state-change
  notification — there was no change. The wrong assumption was never corrected,
  a verification tick spent a request against it every 2.5 seconds, and the
  whole budget was gone about twelve seconds after locking. Windows sends the
  current state as soon as the notification is registered, so waiting for that
  costs milliseconds and removes the guess.
- **Lid-open produced a decision nobody carried out.** The pause was decided and
  tested at the policy level and then thrown away, because the lid handler
  passed its result to a method that only knew two of the four outcomes. Two
  code paths for four outcomes, both of which compiled.

The other trap is that input immediately after locking **is** the click that did
the locking. Treating it as somebody arriving starts the 90-second pause against
the very action that asked for the screen to go out, so input carries that
meaning only once the screen has actually been dark. Lid-open carries it
immediately, because nobody opens a laptop by accident on their way out.

### Machines this cannot fix

Two classes of machine are outside what any of this reaches, and both are
detected and reported rather than promised:

- **Modern Standby (S0 low power idle).** The classic sleep timeouts AFKLocker
  configures are not the whole story there; the system can still drop into a low
  power state with the lid closed, because that decision belongs to the
  firmware. `ReadinessEvaluator` reports Modern Standby as a warning rather than
  a pass, and the diagnostics bundle records it.
- **Managed machines and OEM power utilities.** Group Policy can refuse the
  writes outright, and vendor software can re-apply its own settings afterwards.
  Nothing here detects a later overwrite, so the honest answer when a machine
  reads Ready and still sleeps is to suspect one of those first.

### What this costs

`AFKLocker.exe` stays alive while the session is locked and exits when it is
unlocked. That is not resident in the sense the project promises — nothing
exists while you are working — and it holds no execution state and keeps nothing
awake. A watchdog thread ends the process regardless, because a stuck AFKLocker
is worse than an abrupt one.

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

That relaunched window is deliberately narrower than the one it came from. A process started with
`Process.Start` inherits its parent's token, so a helper launched from there would run as
administrator for the rest of the session — holding a global hotkey registration and locking the
session with rights it has no use for. `WatcherController.Start` refuses outright when the calling
process is elevated, which is the single line the helper can be launched from; and
`AutoLockManager` stands aside before that refusal can be reached, because a refusal arriving
mid-transaction would look like "automatic mode is broken" and be grounds to switch the user's
features off. So the elevated window writes power settings and nothing else, and says so where the
mode and hotkey controls would otherwise be.

Uninstall asks whether to restore the power settings rather than doing it silently, because
"leave my machine configured this way, just remove the shortcut" is a legitimate answer.
