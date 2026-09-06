# Changelog

All notable changes to this project are documented here.
This project follows [Semantic Versioning](https://semver.org/).

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

[0.1.0]: https://github.com/augbastos/AFKLocker/releases/tag/v0.1.0
