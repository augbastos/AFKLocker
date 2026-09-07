# Changelog

All notable changes to this project are documented here.
This project follows [Semantic Versioning](https://semver.org/).

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
- Physically tested on one machine (Acer Nitro AN515-45, Windows 11 Home). Everything else is
  covered by tests against simulated machines.
- Binaries are not code-signed, so SmartScreen will warn on first run.

[0.2.0]: https://github.com/augbastos/AFKLocker/releases/tag/v0.2.0
[0.1.1]: https://github.com/augbastos/AFKLocker/releases/tag/v0.1.1
[0.1.0]: https://github.com/augbastos/AFKLocker/releases/tag/v0.1.0
