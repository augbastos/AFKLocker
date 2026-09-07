# Security

## Supported versions

Only the latest release is supported. AFKLocker is a small single-purpose
utility maintained by one person, so fixes go into the next release rather than
being backported.

| Version | Supported |
|---|---|
| Latest release | Yes |
| Anything older | No — please update first |

The latest release is always at
[Releases](https://github.com/augbastos/AFKLocker/releases).

## Reporting a vulnerability

**Please do not open a public issue for a security problem.** A public issue is
readable by everyone, including before there is a fix.

Use GitHub's private reporting instead:

1. Go to the [Security tab](https://github.com/augbastos/AFKLocker/security).
2. Choose **Report a vulnerability**.

That opens a private advisory visible only to you and the maintainer. If private
reporting is unavailable to you for any reason, open a public issue saying only
that you have a security report and asking for a private channel — no details.

### What helps

- What an attacker gains, not only what misbehaves.
- The Windows version and build.
- Steps to reproduce, or the reasoning if it is not directly reproducible.
- A diagnostics bundle if it is relevant: **AFKLocker Setup → Diagnostics →
  Export diagnostics**. It contains no username, machine name, network address
  or keyboard activity, and you can read the whole thing before sending it.

### What to expect

This is a personal project, not a company. There is no service level agreement.
What is committed to:

- an acknowledgement that the report was received;
- an honest answer about whether it is a real problem, and if not, why not;
- a fix in a release if it is;
- credit in the release notes if you want it.

## Responsible disclosure

Please give a reasonable amount of time for a fix before publishing. There is no
bug bounty and nothing to claim; the request is simply that users get a version
to update to before the problem is public.

## Scope

In scope: anything in this repository — the three executables, the installer,
the build and release workflows.

Out of scope, because they are not defects in AFKLocker:

- **SmartScreen warns on first run.** The binaries are not code-signed; a
  certificate is a recurring cost this project does not have. Every release
  carries a build provenance attestation instead, which ties the exact bytes to
  the commit and workflow that produced them:
  `gh attestation verify <file> --repo augbastos/AFKLocker`.
- **AFKLocker does not stop somebody with physical access.** It locks a session.
  A locked machine with the lid closed is still a machine that is running, and
  the README says so.
- **Windows power settings can be changed by anything running as you.**
  AFKLocker configures them; it does not defend them.

## What AFKLocker does not do

Stated here because these are the assumptions a reviewer would otherwise have to
verify from scratch:

- **No network access of any kind.** It makes no requests, has no server, no
  account, and no update check.
- **No telemetry.** Diagnostics are produced only when a person presses the
  button, written to a file that person chooses, and never sent anywhere.
- **No keyboard monitoring.** The global hotkey uses `RegisterHotKey`, so
  Windows reports one registered combination and nothing else about the
  keyboard. There is no keyboard hook anywhere in the source.
- **No elevation unless asked.** It runs as the signed-in user. Setup offers to
  elevate only when Windows refuses a power setting write, and only after
  telling you why. An elevated Setup window exists for that write and nothing
  else: the mode and hotkey controls are switched off in it, and it will not
  start the background helper, because a child process inherits its parent's
  token.
- **No service and no scheduled task.** The optional background helper is a
  normal user-session process, started from `HKCU\...\Run`, visible in Task
  Manager's Startup tab, and never running with more rights than the session it
  looks after.
