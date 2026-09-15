<div align="center">

<img src="assets/afklocker-128.png" alt="" width="96" height="96">

# AFKLocker

**Lock your PC. Close the lid. Keep it running.**

A small Windows utility for people who need the laptop to keep working while they are away.

[![Build](https://github.com/augbastos/AFKLocker/actions/workflows/ci.yml/badge.svg)](https://github.com/augbastos/AFKLocker/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**[Download](https://github.com/augbastos/AFKLocker/releases/latest)** ·
[Architecture](docs/architecture.md) ·
[Security](SECURITY.md) ·
[Changelog](CHANGELOG.md)

<img src="assets/flow.svg" alt="Double-click AFKLocker, Windows locks, close the lid, everything keeps running" width="880">

</div>

---

## Why

You have a build, a download, a local server or an AI agent running, and you need to leave.
Closing the lid normally suspends the machine, so you either leave it open or you stop working.

Windows can be configured not to suspend, but the settings are scattered across Control Panel,
easy to get half-right, and nothing tells you whether you got it right. AFKLocker checks them,
fixes them if you agree, and gives you a one-action way to lock before you go.

## Quick start

1. Download from [Releases](https://github.com/augbastos/AFKLocker/releases/latest) — installer,
   or a portable zip if you would rather not install anything.
2. Run it. You get an **AFKLocker** shortcut on your desktop.
3. Open **AFKLocker Setup**, check the readiness list, apply the configuration if it asks.
4. From then on: **double-click AFKLocker**, close the lid, walk away.

> **SmartScreen will warn on first run.** The binaries are not code-signed — a certificate is a
> recurring cost this project does not have. Click **More info → Run anyway**, or verify the
> download first:
>
> ```powershell
> # proves these bytes came from this repository's release workflow
> gh attestation verify AFKLocker-<version>-setup.exe --repo augbastos/AFKLocker
> ```
>
> Or check the hash against `SHA256SUMS.txt` from the release.
> Details in [docs/signing.md](docs/signing.md).

## Three ways to lock

| | How | Running while you work? |
|---|---|---|
| **Manual** *(default)* | Double-click the icon | Nothing |
| **Global hotkey** *(opt-in)* | Press a key you choose | A small helper, waiting for the key |
| **Automatic** *(opt-in)* | Close the lid | A small helper, waiting for the lid |

All three do the same thing: lock the session and put the screens out. Both opt-in modes are off
until you switch them on in Setup, and they share one helper — switching both on does not run two
of them. Turning one off leaves the helper running if the other still needs it.

Whichever mode you use, AFKLocker stays alive only for the active AFK session to look after the
screens. Keyboard or mouse input ends that session and restores the temporary settings.

**Opening the lid wakes the screens but never unlocks anything.** If you close it again without
using the keyboard or mouse, AFKLocker returns to the same dark AFK state.

### The hotkey is not a keyboard watcher

You pick the keys — AFKLocker ships no default. The **Menu** key, next to the right `Ctrl` on many
keyboards, works on its own and is a good candidate because almost nothing else uses it.

AFKLocker asks Windows to be told when that one combination is pressed, and Windows tells it only
that. **No other keystroke reaches the program**, and none is recorded anywhere. If another
program already owns a combination, Setup says so while you are choosing it.

### The screens

Locking does not darken a screen on its own, and closing a lid can reconfigure the display
topology and wake an external monitor. AFKLocker immediately requests display-off, watches
Windows' display-state notifications, and rejects every relight while the lid is closed. A short,
fast retry burst covers the topology changes directly after lid close; a slower check remains for
the whole AFK session.

Opening the lid explicitly requests display-on. Closing it again requests display-off. Real
keyboard or mouse input ends AFK mode instead, so AFKLocker stops owning the screens and restores
normal Windows behaviour.

### Keyboard lighting

**AFKLocker Setup → Turn off keyboard lighting during AFK** (off by default). Setup detects on its
own whether this PC exposes keyboard lighting AFKLocker knows how to drive and shows
**Supported**, **Unsupported** or **Detection failed** — there is nothing to configure and no
hardware to pick. When it is on, AFK mode saves the current lighting, turns it off, and restores
the saved state when AFK ends, reporting an error if the firmware reads back something different.
On unsupported hardware the option is simply unavailable.

Detected today: Acer gaming laptops that expose the firmware's gaming WMI interface. Windows only
lets administrators call it, so switching the option on asks for administrator approval **once**.
AFKLocker then installs a small helper under `Program Files` and two on-demand scheduled tasks that
can only turn the lighting off or restore it. Setup then runs off and restore once through those
tasks as a self-test, and removes everything again if either fails. Switching the option off removes them,
again with approval. Nothing runs in the background.

Windows' own lighting API (Dynamic Lighting) is not used. Background control there needs package
identity, the `com.microsoft.windows.lighting` app extension and the user ranking the app in
Settings; AFKLocker is unpackaged, and has no foreground window while the session is locked.

## What it changes on your machine

Manual mode changes only the AC and battery lid-close actions, only while AFK is active. It also
makes a process-scoped keep-awake request. The original lid values are saved before the change,
restored on keyboard/mouse input or unlock. If the session never gets that far — the process is
killed, or Windows restarts overnight — a one-shot sign-in entry restores them at the next sign-in.
Normal display, sleep and hibernate timeouts are not written.

Automatic mode is different: Windows must already be configured before the physical close event
arrives. If you explicitly enable Automatic mode, Setup can persist these settings on the active
power plan:

| Setting | Set to | When |
|---|---|---|
| Lid close action (plugged in) | Do nothing | Always |
| System sleep timeout (plugged in) | Never | Always |
| Hibernate timeout (plugged in) | Never | Only if hibernation is enabled |
| The same three on battery | — | Only if you tick "also on battery" |

**Battery is off by default**, because keeping a machine awake with the lid shut on battery drains
it and can overheat it in a bag. The trade-off: if you use Automatic mode unplugged without
ticking battery, closing the lid will lock and *then* let Windows sleep. Setup says so when that
applies to you.

Display timeouts are never written in either mode. Manual mode never writes sleep or hibernate
timeouts either.

The previous values are backed up in plain text under `%LOCALAPPDATA%\AFKLocker` before anything
is changed. If a change fails partway, AFKLocker puts back the ones that already happened — and
if it cannot, it tells you exactly what is still changed instead of reporting a clean failure.

**Restore:** AFKLocker Setup → **Restore previous**. Uninstalling always attempts to remove the
helper and its startup entry, whether or not you restore the settings, and tells you if it could
not.

## ⚠️ Safety

**Never leave a running, lid-closed laptop in a bag, sleeve, drawer or backpack.** AFKLocker's
whole purpose is to stop the machine sleeping when you shut it, which means it keeps generating
heat with its vents against fabric. Setup says this too, before you turn anything on.

A locked machine is still a running machine. AFKLocker locks a session; it does not defend against
someone with physical access.

## Privacy

**No network access of any kind.** No telemetry, no analytics, no update check, no server, no
account. Nothing about your machine leaves it.

**No keyboard monitoring.** The hotkey is a registration, not a watcher — see above.

If something does not work, **AFKLocker Setup → Diagnostics** runs a self-test and can export a
zip you can attach to an issue. It is written to disk and never transmitted, and it deliberately
excludes your username, computer name, network addresses, files, installed programs and
processes. Paths become placeholders. Manufacturer and model are opt-in and start unticked.

## Compatibility and limitations

- **Windows 10 (1903+) and Windows 11**, x64. Needs .NET Framework 4.8, which both already have.
  The installer will run on Windows 8.1, but that has not been tested and 4.8 is not there by
  default.
- No administrator rights needed for screen and power handling. Switching keyboard lighting on
  needs one administrator approval because the firmware interface behind it is privileged.
- **Keyboard lighting control has not been tried on real hardware yet.** Its logic is tested
  against simulated firmware only.
- **No code signing.** SmartScreen warns; the attestation above is what exists instead.
- **Earlier versions were physically tested on one machine; this version's screen, lid and
  recovery changes have not been yet.** Everything else is covered by tests against simulated
  machines — which is why the diagnostics export exists.
- **Modern Standby (S0 low power idle) is a known gap.** Those machines do not use the classic
  sleep settings AFKLocker configures, so closed-lid behaviour is up to the firmware. Setup
  detects Modern Standby and says so rather than promising it will work.
- **OEM utilities and Group Policy can override you.** Vendor power software may re-apply its own
  settings, and a managed machine may refuse the changes outright. Setup reports what Windows
  actually says, so re-check the readiness list if you suspect this.
- **Automatic mode needs a machine that reports lid events.** Some laptops have the ACPI Lid
  device disabled, often deliberately to stop them sleeping. Setup detects that and says so,
  because otherwise the helper looks perfectly healthy and simply never fires.

## Building from source

Needs only Windows. No SDK, no NuGet, no package restore.

```powershell
git clone https://github.com/augbastos/AFKLocker.git
cd AFKLocker
.\tools\Build.ps1 -Test
```

## More

- [Architecture and design decisions](docs/architecture.md) — how it works, and why it is built
  this way
- [Security and privacy](SECURITY.md) — reporting a vulnerability, and what the program does not do
- [Changelog](CHANGELOG.md)
- [Code signing](docs/signing.md)
- [Releases](https://github.com/augbastos/AFKLocker/releases)

Something not working? **AFKLocker Setup → Diagnostics → Export diagnostics** produces a file you
can attach to an [issue](https://github.com/augbastos/AFKLocker/issues).

## License

MIT — see [LICENSE](LICENSE).
