# Changelog

All notable changes to this project are documented here.
This project follows [Semantic Versioning](https://semver.org/).

## [0.5.1] - 2026-09-07

### Fixed

- **Opening the lid never actually paused the display guard.** The pause was
  written, tested at the decision level, and then thrown away: the lid handler
  passed its decision to a method that only knew how to turn the display off or
  stop, so "wait, somebody is here" did nothing at all. Opening a laptop could
  therefore have the screen go dark again while you were still reaching for the
  keyboard. Two code paths for four outcomes, both of which compiled. There is
  one path now, and a test that fails if an outcome is ever added without being
  handled.

- **A stray input event right after locking suppressed the display-off
  entirely.** The most recent input at that moment is the click or key that did
  the locking, and reading it as "somebody is at the machine" started a
  ninety-second pause against the very action that asked for the screen to go
  out. Input now means that only once the screen has actually been dark; opening
  the lid still means it immediately, because that one is unambiguous.

- **Applying power settings is now all-or-nothing.** A backup was written before
  the first change, which made recovery possible while still leaving the machine
  half-configured and the user unaware — and for these particular settings that
  means a laptop that believes it will keep running with the lid shut and will
  actually sleep. A write that fails unexpectedly now unwinds the writes that
  already landed, in reverse, and says whether that unwinding was complete. The
  original error is carried rather than swallowed, the backup is never deleted
  on a failure, and access-denied still surfaces as an exception so Setup can
  offer to elevate.

- **Uninstalling checks whether its cleanup worked.** Both the `Exec` return
  value and the exit code were discarded, so an uninstall could report success
  while a sign-in entry survived — pointing into a folder about to be deleted,
  with no AFKLocker left to remove it. It now says so, tells you where to look,
  and continues. The same applies to restoring the power settings: if that
  cannot be completed, it says where your original values are saved rather than
  letting you believe the machine was put back. A silent uninstall shows nothing
  and writes both outcomes to the installer log. `AFKLockerSetup.exe` returns
  distinct exit codes so the caller can tell "done", "mostly done" and "not
  done" apart.

- **Documentation described the old display behaviour.** The README still said
  the guard lasted 45 seconds, that input or opening the lid ended it, and that
  five requests ended it — none of which had been true since those were changed
  to a defer, a back-off, and a guard that lasts as long as the session is
  locked. It also said turning automatic mode off always leaves nothing
  resident, which stopped being true when the hotkey could need the same helper.
  The architecture notes described `AFKLocker.exe` as "lock and exit" and put
  the codebase at roughly 1,200 lines, which is out by about six times.

### Added

- `SECURITY.md`: supported versions, how to report a vulnerability privately,
  and the properties a reviewer would otherwise have to establish from scratch —
  no network access, no telemetry, no keyboard hook, no service.
- `tools/Check-Version.ps1`, wired into both workflows. The version lives in
  three files; bumping two of the three is an ordinary slip that produces an
  installer whose file name, Programs and Features entry and About text
  disagree. On a tag it also checks the tag matches.
- Tests for the display guard's timing, which was the one part of that class no
  test could reach: the grace window now takes "now" as an argument instead of
  reading the clock. No `IClock`, no restructuring — the decision that needed
  testing was extracted and the rest left alone.

### Changed

- README examples use `AFKLocker-<version>-setup.exe` rather than a version that
  goes stale the day after a release.

## [0.5.0] - 2026-09-07

### Added

- **Global lock hotkey, opt-in and off by default.** Choose any key or combination in AFKLocker
  Setup and press it anywhere to lock, with exactly the same behaviour as double-clicking the
  icon - the same code runs, so the two cannot drift apart.

  It uses Windows' `RegisterHotKey`, never a keyboard hook. AFKLocker asks Windows to be told when
  one combination is pressed and Windows tells it only that; no other keystroke reaches the
  program. **AFKLocker still does not monitor what you type**, and the diagnostics bundle reports
  the key you chose and nothing about keys you press.

  Measured rather than assumed: the **Menu** key (`VK_APPS`) registers on its own, so it can be
  bound without a hook. Windows reports its own reservations - F12, `Win`+`L`, `Ctrl`+`Esc`,
  `Alt`+`Tab` all refuse with "already registered" - so there is no invented list of forbidden
  keys; the API is asked and its answer shown. The single hand-written rule is that a bare
  modifier is refused, because `RegisterHotKey` accepts it and then nothing ever fires.

  Setup checks a combination with Windows before saving it, so a conflict with another program is
  a sentence while you are choosing rather than a helper that fails to start later.

### Changed

- **Whether a background helper runs now follows the features switched on, not the lock mode.**
  `AFKLockerWatcher.exe` was a lid watcher and is now a helper with two separate jobs. Manual mode
  no longer means "no helper, ever" - it means "no helper unless the hotkey needs one".

  | Automatic | Hotkey | Helper |
  |---|---|---|
  | off | off | none |
  | off | on | runs, for the hotkey |
  | on | off | runs, for the lid |
  | on | on | one process, both jobs |

  Turning one feature off no longer stops a helper the other still needs, and the autostart entry
  follows the same rule. With both off, nothing is resident and nothing is registered, exactly as
  before.

- **READY now means every enabled feature is working**, not just lid registration. A helper
  started for the hotkey signals ready only once `RegisterHotKey` succeeded; with both features on
  it signals only when both did. New exit codes distinguish a hotkey Windows refused (4) from a
  helper that had nothing to do (5).

- A helper found running but never ready is now reported as a contradiction rather than a
  transient. Enabling waits for readiness before returning, so that state means stuck, not
  mid-launch, and reconciliation restarts it.

### Fixed

- **The autostart entry is validated, not merely counted.** The old check asked whether the
  command contained `AFKLockerWatcher.exe`, which accepts an entry left behind by an uninstalled
  copy in a folder that no longer exists: it looks healthy in every status screen and starts
  nothing.

  The command is now parsed and the executable compared semantically - environment variables
  expanded, 8.3 names resolved, casing and quoting and redundant path segments ignored - and
  reported as absent, correct, wrong target, malformed, or unreadable. `D:\Old\AFKLockerWatcher.exe`
  is no longer accepted because it ends in the right file name, and `Reconcile()` rewrites a wrong
  entry rather than tolerating it. Diagnostics says which, with the path redacted as usual.

- Asking to enable a hotkey that could never work reported **success** while manual mode was on.
  An unusable key contributes no feature, so the request looked identical to asking for nothing at
  all: the machine stood down, said it worked, and saved a hotkey switched on with a key that
  could never fire. It is now refused before anything is touched.

## [0.4.1] - 2026-09-07

### Fixed

- **Locking now turns the screens off, in both modes.** Manual mode never touched the display at
  all: it locked and exited, and the screens stayed lit until Windows' *console lock display off
  timeout* fired. That timeout is sixty seconds by default, is hidden from `powercfg /q`, and on
  some machines does not fire at all - so "lock and walk away" could leave the lock screen
  glowing indefinitely, which with an external monitor is exactly where anyone can see it. The
  request AFKLocker makes puts the panel into standby rather than painting it black.

- **The display stays off when the lid closes afterwards.** Closing the lid makes Windows
  reconfigure the displays, and that reconfiguration wakes the external monitor back up - after
  the lock, so a single display-off request has already been made and lost. AFKLocker now holds
  the screens dark for up to 45 seconds instead of asking once.

  It stops immediately on any of: the session being unlocked, a key or mouse movement, the lid
  being opened, five requests made (something else on the machine wants the display on and gets
  to win), or the time limit. Blanking too little is a nuisance; blanking the screen of someone
  typing their password is a malfunction, so every ambiguous case fails towards leaving the
  display on.

- **A stuck AFKLocker no longer disables display-off silently.** The first version of the holder
  tore its own notification window down from inside that window's message handler. When that
  failed the "finished" event never fired, the process never exited, and - because it held a
  single-instance mutex - every later lock skipped blanking without a word. The teardown is now
  deferred to the next turn of the message loop, "finished" is raised before any cleanup so that
  nothing depends on cleanup succeeding, the mutex is gone, and a watchdog ends the process
  regardless.

### Changed

- `AFKLocker.exe` now lives for up to 45 seconds after locking rather than exiting immediately.
  It still holds no execution state, starts no service, and never keeps the machine awake; the
  lock itself is complete before any of this begins, and nothing here can undo it.

## [0.4.0] - 2026-09-07

### Added

- **Diagnostics.** AFKLocker Setup gains a Diagnostics window that runs a self-test and reports
  what this machine can and cannot do: whether Windows reports a lid, whether the power settings
  allow closed-lid operation, whether the watcher reaches READY, and whether the saved
  configuration matches reality.
  - **Test lid detection** asks the user to close and open the lid and reports what Windows
    delivered. It never locks the session - it only listens.
  - **Test the watcher** exercises the start/ready/stop handshake and restores the previous state.
  - **Export diagnostics** writes a zip containing `summary.txt`, `diagnostics.json` (with a
    schema version, for tooling) and `self-test.txt`.
  - **Report a problem** opens the issue page. Nothing is uploaded, and the report is never put
    into a URL.
- Issue template asking for the things that actually help, with the diagnostics bundle attached.
- **Code signing support.** Optional and off unless configured, so builds from source are
  unaffected. When the signing secrets are present the release signs all four binaries and the
  installer with RFC 3161 timestamping, verifies each signature, and fails the release if any part
  of it fails. See [docs/signing.md](docs/signing.md).
- **Build provenance attestation** on every release, signed or not, so anyone can verify the
  published bytes came from this repository's workflow at a specific commit.

### Changed

- **Enabling automatic mode is now transactional.** It touches the saved mode, the autostart entry
  and the running process, and any of them can fail; a failure now unwinds the steps before it.
  Enable leaves exactly one of two states: automatic and genuinely working, or manual and clean.
  A rollback that cannot complete is reported as residue rather than passed over.
- **The watcher now performs a readiness handshake.** A running process is no longer treated as a
  working watcher: a new `Local\AFKLocker.Watcher.Ready` event is set only after the lid
  registration has actually succeeded. Starting waits for that, for the process to die, or for a
  timeout - so a watcher that cannot receive lid events fails the enable and is rolled back
  instead of sitting there looking healthy.
  Setup reports four states (`NotRunning`, `Starting`, `Ready`, `Unhealthy`) instead of a boolean.
- **Setup reconciles on open.** If the watcher was killed, the startup entry removed, or a stale
  signal left behind, it repairs the state - or converges to manual and clean if it cannot.
- Workflows hardened: third-party actions pinned to immutable commit SHAs rather than tags,
  least-privilege permissions per job, `persist-credentials: false` on checkout, and signing
  secrets scoped to the single step that uses them and absent from CI entirely.

### Fixed

- **The watcher now turns the displays off after locking on lid close.** On a single screen the
  lid does that itself; with an external monitor attached it does not - closing the lid makes
  Windows reconfigure the displays, which wakes the external one and leaves the lock screen lit on
  a desk the user has walked away from. This only happens on a lock that just occurred: never on
  lid-open, never on a session that was already locked, and never on its own.

### Privacy

- The diagnostics bundle excludes usernames, computer names, IP and MAC addresses, Wi-Fi networks,
  files, installed programs, processes and environment variables. Paths are replaced with
  placeholders, and a path outside the known folders is reduced to its file name. A custom power
  plan's name is never reported, only that it is custom. Manufacturer and model are opt-in and
  default to off.
- A test searches the exported bundle for shapes of personal data - MAC addresses, IPs, emails,
  un-redacted profile paths - so anything added carelessly later is caught.

## [0.3.0] - 2026-09-07

### Added

- **"AFKLocker Setup" in the right-click menu of the AFKLocker shortcut.** The settings window is
  now one click away from the desktop icon, instead of a trip to the Start menu.

  The entry is scoped to AFKLocker's own shortcuts. A verb registered on `lnkfile` would otherwise
  appear on every shortcut the user owns, which is exactly the kind of thing a small utility has
  no business doing. Scoping by the shortcut's resolved target was tried first and silently
  matches nothing, so it is scoped by file name with a wildcard - which also survives renaming the
  shortcut.

  On Windows 11 it appears in the full context menu, which is the one shown after "Show more
  options" unless the classic menu is enabled. Putting an entry in the compact Windows 11 menu
  requires a signed MSIX package, which this project has no way to produce.

  Installing registers it, uninstalling removes it, and the shell is notified either way so it
  appears and disappears without restarting Explorer.

## [0.2.2] - 2026-09-07

### Added

- Setup now detects machines that do not report lid state to Windows and says so, instead of
  leaving automatic mode looking healthy while never firing. It names the likely cause - the
  **ACPI Lid** device disabled in Device Manager - so it can be acted on.

  This was found on the development machine itself: automatic mode reported a running watcher,
  closing the lid did nothing, and there was no way to tell why without a debugger. Disabling the
  ACPI Lid device is a common trick for stopping a laptop sleeping on lid close, which AFKLocker
  makes unnecessary - and which silently breaks this feature.

### Changed

- The battery note and the no-lid warning now come from one place, so the more serious problem
  wins: a machine that cannot lock at all is reported ahead of one that merely sleeps afterwards
  on battery.

## [0.2.1] - 2026-09-07

### Fixed

- Setup reported "Watcher: NOT running" immediately after turning automatic mode on, even though
  the watcher had started correctly. `Process.Start` returns as soon as the process exists, well
  before it has started the runtime and claimed its mutex, and the window refreshed in that gap.
  Starting the watcher now waits until it has actually registered, so the status reflects reality.
- The watcher status message was clipped mid-sentence by a fixed control height. It now grows to
  fit, like the other notes in the window.
- Reworded that status so the recovery step is a single clear action.

## [0.2.0] - 2026-09-06

### Added

- **Automatic lid lock**, an opt-in mode that locks Windows when the laptop lid closes.
  Manual double-click remains the default and is unchanged.
  - `AFKLockerWatcher.exe` - a windowless user-session process that registers for
    `GUID_LIDSWITCH_STATE_CHANGE` and locks on a close event. No polling, no tray icon, no
    console, no network. Idle footprint is roughly 5 MB.
  - Autostart via one `HKCU\...\CurrentVersion\Run` value, written when the mode is turned on and
    removed when it is turned off or the program is uninstalled.
  - `--status` and `--stop` on the watcher for diagnostics.
- **Lock behaviour** section in Setup, separate from closed-lid readiness, showing the mode and
  whether the watcher is installed and running.
- Setup reports when automatic lock will work but Windows may still sleep on battery, rather than
  letting the two look like one setting.
- `tools/Test-AutoLock-Integration.ps1` - end-to-end check of enabling and disabling automatic
  lock against the real settings file, registry and watcher process, including that the watcher
  holds no network handles and no power requests.

### Behaviour worth knowing

- Opening the lid never unlocks anything, in any circumstance.
- The first lid event after the watcher starts is treated as a starting position rather than a
  change, so a laptop that is already docked and shut does not get locked out from under someone
  working on an external monitor. The same reset happens after resume.
- The watcher never changes power settings and never keeps the machine awake by itself.
- Installing does not enable automatic mode. Upgrading from 0.1.x keeps manual mode, the desktop
  shortcut, and any existing power settings backup.

## [0.1.1] - 2026-09-06

### Fixed

- Backup files are now written atomically, to a temporary file that is then moved into place.
  Writing directly truncated the existing file before the new content landed, so an interruption
  at that moment - a crash, power loss, or full disk - could leave a half-written file. Since that
  file is the only record of the user's original power settings, losing it meant losing the
  ability to restore them. Applying saves twice, so this rewrote a good backup on every run.
- A backup file that cannot be parsed now reports its full path, so it can be read by hand or
  deleted to get unstuck, instead of surfacing a bare parse error.

## [0.1.0] - 2026-09-06

Initial release.

### Added

- `AFKLocker.exe` - locks the Windows session and exits. No console window, nothing resident.
- `AFKLocker Setup` - reports whether Windows is configured for closed-lid operation, and applies
  the configuration on request:
  - lid close action and system sleep timeout while plugged in
  - hibernate timeout, when hibernation is enabled
  - battery equivalents, opt-in and off by default
- Backup and restore of every power setting AFKLocker changes, as plain text under
  `%LOCALAPPDATA%\AFKLocker\`, one file per power plan.
- `--display-off` to turn the display off without locking, with an optional desktop shortcut.
- Detection and reporting of Modern Standby machines, where closed-lid operation is not guaranteed.
- Inno Setup installer, per-user by default, that offers to restore the original power settings on
  uninstall.
- Unit test suite covering readiness evaluation, backup format, apply, restore and failure paths.
- `tools/Test-Integration.ps1` - end-to-end apply/restore check against real Windows power
  settings, run on a throwaway power plan.

### Known limitations

- Modern Standby (S0 low power idle) machines may still enter a low power state with the lid
  closed. AFKLocker detects this and says so rather than promising otherwise.
- Physically tested on one machine only. Everything else is covered by tests against simulated
  machines.
- Binaries are not code-signed, so SmartScreen will warn on first run.

[0.5.1]: https://github.com/augbastos/AFKLocker/releases/tag/v0.5.1
[0.5.0]: https://github.com/augbastos/AFKLocker/releases/tag/v0.5.0
[0.4.1]: https://github.com/augbastos/AFKLocker/releases/tag/v0.4.1
[0.4.0]: https://github.com/augbastos/AFKLocker/releases/tag/v0.4.0
[0.3.0]: https://github.com/augbastos/AFKLocker/releases/tag/v0.3.0
[0.2.2]: https://github.com/augbastos/AFKLocker/releases/tag/v0.2.2
[0.2.1]: https://github.com/augbastos/AFKLocker/releases/tag/v0.2.1
[0.2.0]: https://github.com/augbastos/AFKLocker/releases/tag/v0.2.0
[0.1.1]: https://github.com/augbastos/AFKLocker/releases/tag/v0.1.1
[0.1.0]: https://github.com/augbastos/AFKLocker/releases/tag/v0.1.0
