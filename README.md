<div align="center">

<img src="assets/afklocker-128.png" alt="" width="96" height="96">

# AFKLocker

**Lock your PC. Close the lid. Keep it running.**

A small Windows utility that keeps your laptop working while you are away.

[![Build](https://github.com/augbastos/AFKLocker/actions/workflows/ci.yml/badge.svg)](https://github.com/augbastos/AFKLocker/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**[Download](https://github.com/augbastos/AFKLocker/releases/latest)** ·
[How it works](docs/architecture.md) ·
[Security](SECURITY.md) ·
[Changelog](CHANGELOG.md)

<img src="assets/flow.svg" alt="Double-click AFKLocker, Windows locks, close the lid, everything keeps running" width="880">

</div>

---

## Quick start

1. Download the installer (or the portable zip) from
   [Releases](https://github.com/augbastos/AFKLocker/releases/latest).
2. When you step away, double-click **AFKLocker** on your desktop. The PC locks and the screens go
   dark.
3. Close the lid if you like. Builds, downloads and agents keep running.
4. When you are back, touch the keyboard or mouse and sign in as usual.

> **SmartScreen warns on first run** because the binaries are not code-signed. Choose
> **More info → Run anyway**, or verify the download first with
> `gh attestation verify AFKLocker-<version>-setup.exe --repo augbastos/AFKLocker` or the
> release's `SHA256SUMS.txt`. See [docs/signing.md](docs/signing.md).

## While you are away

- **The screens stay off**, including an external monitor that Windows wakes when the lid closes.
- **Opening the lid** turns the screens on without unlocking anything. Closing it again turns them
  off.
- **The PC stays awake** with the lid closed. In Automatic mode on battery, only if you tick that
  in Setup; otherwise it locks and then sleeps.
- **Keyboard lighting turns off**, if you switch that on in Setup and your PC supports it.
- **Keyboard or mouse input** ends the session and puts everything back.

## Ways to lock

| | How | Runs in the background? |
|---|---|---|
| **Manual** *(default)* | Double-click the icon | No |
| **Hotkey** *(opt-in)* | A key combination you choose | A small helper, waiting for that key |
| **Automatic** *(opt-in)* | Close the lid | A small helper, waiting for the lid |

Switch the opt-in ones on in **AFKLocker Setup**. The hotkey is a Windows registration, not a
keyboard watcher: AFKLocker is told about that one combination and never sees any other key.

## What it changes

- **Manual and hotkey: no permanent power changes.** During a session the lid-close action is set to
  *Do nothing* and the PC is kept awake. Both are restored when you return, or at the next sign-in
  if the session was cut short.
- **Automatic** needs Windows set up before the lid closes, so after asking, Setup keeps these on
  your power plan: lid close *Do nothing*, sleep *Never*, hibernate *Never* — plugged in, and on
  battery only if you tick it. The old values are backed up first, and **Restore previous** puts
  them back.
- **Keyboard lighting** *(optional)* asks for administrator approval once, then installs a small
  helper under `Program Files` and two on-demand scheduled tasks that run it with administrator
  rights, only to turn the lighting off or restore it. Switching it off removes them. Supported
  today: Acer gaming laptops that expose the firmware's gaming WMI interface; other PCs show
  *Not supported*.

Display timeouts are never changed.

## ⚠️ Safety

**Never leave a running, lid-closed laptop in a bag, sleeve or drawer** — it keeps producing heat.
A locked PC is still a running PC: AFKLocker does not protect against someone with physical access.

## Privacy

No network access, no telemetry, no account. **Setup → Diagnostics** can export a report to attach
to an [issue](https://github.com/augbastos/AFKLocker/issues); it stays on disk and leaves out your
username, computer name, network addresses and files.

## Limitations

- Windows 10 (1903+) and 11, x64, with .NET Framework 4.8 (already part of both).
- Not code-signed (see above).
- Physically tested on one machine; everything else is covered by simulated tests.
- On Modern Standby laptops closed-lid behaviour is up to the firmware, and vendor power utilities
  or Group Policy can override settings. Setup tells you what Windows reports.
- Automatic mode needs a laptop that reports lid events. Setup says so if yours does not.

## Build from source

Windows only — no SDK, no package restore.

```powershell
git clone https://github.com/augbastos/AFKLocker.git
cd AFKLocker
.\tools\Build.ps1 -Test
```

## License

MIT — see [LICENSE](LICENSE).
