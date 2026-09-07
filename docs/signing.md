# Code signing and release integrity

AFKLocker releases can be Authenticode-signed. Signing is **optional**: the project builds and
runs identically without a certificate, which is the case for anyone building from source.

## What a user can verify

| Question | How |
|---|---|
| Were these bytes built from this repository, by its workflow? | GitHub build provenance attestation |
| Have the files been modified since? | `SHA256SUMS.txt`, or the attestation |
| Who published the release? | The attestation records the workflow, repository and commit |
| Is the digital signature valid? | Windows file properties, or `signtool verify` - when signing is configured |

Provenance attestation is produced on **every** release, signed or not. It is the stronger
guarantee of the two: it ties the exact bytes to a specific workflow run and commit, and does not
depend on anyone buying a certificate.

```powershell
# Verify a downloaded file came from this repository's release workflow
gh attestation verify AFKLocker-0.3.0-setup.exe --repo augbastos/AFKLocker
```

## Verifying a signature, when one is present

```powershell
Get-AuthenticodeSignature .\AFKLocker-0.3.0-setup.exe | Format-List Status, SignerCertificate
```

`Status` of `Valid` means the signature is intact and the certificate chains to a trusted root.
Right-clicking the file and looking at the **Digital Signatures** tab shows the same thing.

## What certificate is expected

An **Authenticode code signing certificate** as a password-protected `.pfx` (PKCS #12), from a
certificate authority Windows trusts. Either type works:

- **OV (Organisation Validation)** - the cheaper option. Signed binaries still accumulate
  SmartScreen reputation over time rather than being trusted immediately.
- **EV (Extended Validation)** - carries SmartScreen reputation from the first download, and
  normally requires the key to live on a hardware token, which does not fit an unattended CI
  build without a cloud signing service.

A self-signed certificate is fine for testing the pipeline (below) and useless for distribution:
Windows will not trust it.

## Configuring it in GitHub

Three settings on the repository. Nothing is committed, and nothing appears in logs.

| Name | Kind | Contents |
|---|---|---|
| `AFKLOCKER_SIGNING_CERTIFICATE` | Secret | The `.pfx`, base64-encoded |
| `AFKLOCKER_SIGNING_PASSWORD` | Secret | The `.pfx` password |
| `AFKLOCKER_SIGNING_TIMESTAMP_URL` | Variable (optional) | RFC 3161 timestamp server; defaults to DigiCert's |

To produce the base64:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes("certificate.pfx")) | Set-Clipboard
```

**When both secrets are present, the release signs and every failure is fatal** - a broken signing
setup fails the release rather than quietly publishing unsigned binaries. When they are absent,
the release publishes unsigned and says so in its log.

The secrets are referenced by exactly one step in `release.yml`. The unsigned build path does not
mention them, so they are not in that step's environment at all. `Sign.ps1` never echoes the
password: `signtool` output is captured, and the password is masked before anything is printed.

## What gets signed

`AFKLocker.exe`, `AFKLockerSetup.exe`, `AFKLockerWatcher.exe`, `AFKLocker.Core.dll`, and the
installer. The binaries are signed **before** the installer is built, so the files it packages are
the signed ones, and the installer is signed afterwards.

Every signature is timestamped, so binaries stay valid after the certificate expires.

Each signature is verified after being applied. Signing that "succeeds" but produces something
Windows rejects is worse than not signing at all.

## Testing locally

Without a certificate, nothing happens and the build succeeds:

```powershell
pwsh -File tools/Build.ps1 -Sign          # prints "signing skipped", builds normally
pwsh -File tools/Build.ps1 -RequireSigning # fails: signing was required but not configured
```

To exercise the whole path with a throwaway certificate:

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject 'CN=AFKLocker Signing Test' -CertStoreLocation Cert:\CurrentUser\My `
    -NotAfter (Get-Date).AddDays(2) -KeyExportPolicy Exportable
$password = ConvertTo-SecureString 'test-only' -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath test.pfx -Password $password | Out-Null

$env:AFKLOCKER_SIGNING_CERTIFICATE = [Convert]::ToBase64String([IO.File]::ReadAllBytes("test.pfx"))
$env:AFKLOCKER_SIGNING_PASSWORD = 'test-only'

# -AllowUntrustedSigningRoot accepts a self-signed chain. Testing only.
pwsh -File tools/Build.ps1 -RequireSigning -AllowUntrustedSigningRoot -Installer

Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force
Remove-Item test.pfx
```

`-AllowUntrustedSigningRoot` checks that a signature and a timestamp are present and that the
signature matches the file, but skips chain trust - which a self-signed certificate can never
satisfy. **The release never passes it.** There, an untrusted chain means Windows would reject the
binary, and the release should fail.

## Workflow hardening

Both workflows:

- pin third-party actions to **immutable commit SHAs**, not tags. A tag can be moved to point at
  different code; a SHA cannot.
- start from `permissions: contents: read`, with each job asking for only what it needs. The
  release job needs `contents: write` to publish, plus `id-token: write` and `attestations: write`
  for provenance. CI needs neither and gets neither.
- check out with `persist-credentials: false`, so no token is left in the git config for later
  steps to pick up.
- keep signing out of CI entirely: nothing is published from there, so the secrets are never
  available to pull request builds.
