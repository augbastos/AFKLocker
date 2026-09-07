<#
.SYNOPSIS
    Authenticode-signs AFKLocker binaries, when a certificate is available.

.DESCRIPTION
    Signing is optional by design. A developer with no certificate - which is
    everyone building from source - gets working binaries; the release workflow
    signs them only when the signing secrets are configured.

    The certificate arrives as base64 (so it can live in a GitHub secret),
    is written to a temporary file outside the repository, used, and deleted in
    a finally block. Neither the certificate nor the password is ever echoed,
    written to the build output, or passed on a command line that gets logged.

    Every signature is timestamped, so binaries stay valid after the signing
    certificate expires.

.PARAMETER Files
    The files to sign.

.PARAMETER CertificateBase64
    The .pfx as base64. Defaults to $env:AFKLOCKER_SIGNING_CERTIFICATE.

.PARAMETER CertificatePassword
    The .pfx password. Defaults to $env:AFKLOCKER_SIGNING_PASSWORD.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server. Defaults to $env:AFKLOCKER_SIGNING_TIMESTAMP_URL,
    then to DigiCert's public server.

.PARAMETER Require
    Fail if signing is not possible. The release workflow passes this when the
    secrets are present, so a broken signing setup fails the release rather than
    silently publishing unsigned binaries.

.EXAMPLE
    # Local test with a self-signed certificate (see docs/signing.md)
    pwsh -File tools/Sign.ps1 -Files build/AFKLocker.exe -CertificateBase64 $b64 -CertificatePassword $pw
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Files,
    [string]$CertificateBase64 = $env:AFKLOCKER_SIGNING_CERTIFICATE,
    [string]$CertificatePassword = $env:AFKLOCKER_SIGNING_PASSWORD,
    [string]$TimestampUrl = $env:AFKLOCKER_SIGNING_TIMESTAMP_URL,
    [switch]$Require,

    # Testing only. Accepts a signature whose certificate chain is not trusted,
    # which is the case for the self-signed certificate used to exercise this
    # script. A real release never passes this: there, an untrusted chain means
    # Windows would reject the binary, and the release should fail.
    [switch]$AllowUntrustedRoot
)

$ErrorActionPreference = 'Stop'

if (-not $TimestampUrl) { $TimestampUrl = 'http://timestamp.digicert.com' }

function Find-SignTool {
    $onPath = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if ($onPath) { return $onPath }

    # Windows SDK, newest version first.
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $roots) {
        $found = Get-ChildItem $root -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

# ------------------------------------------------------- is signing possible ---
if (-not $CertificateBase64) {
    if ($Require) {
        throw "Signing was required but no certificate was provided. Set AFKLOCKER_SIGNING_CERTIFICATE."
    }
    Write-Host "  signing skipped: no certificate configured (this is normal for local builds)"
    exit 0
}

$signTool = Find-SignTool
if (-not $signTool) {
    if ($Require) { throw "Signing was required but signtool.exe was not found. Install the Windows SDK." }
    Write-Host "  signing skipped: signtool.exe not found"
    exit 0
}

# --------------------------------------------------------------------- sign ---
# Outside the repository and outside the build output, so a stray 'git add' or
# an artifact upload cannot pick it up.
$certPath = Join-Path ([System.IO.Path]::GetTempPath()) ("afklocker-signing-" + [guid]::NewGuid().ToString('N') + ".pfx")

try {
    try {
        [System.IO.File]::WriteAllBytes($certPath, [Convert]::FromBase64String($CertificateBase64))
    }
    catch {
        throw "The signing certificate is not valid base64."
    }

    Write-Host ("  signtool  {0}" -f $signTool)
    Write-Host ("  timestamp {0}" -f $TimestampUrl)

    foreach ($file in $Files) {
        if (-not (Test-Path $file)) { throw "Cannot sign a file that does not exist: $file" }

        $arguments = @(
            'sign'
            '/fd', 'SHA256'          # file digest
            '/td', 'SHA256'          # timestamp digest
            '/tr', $TimestampUrl     # RFC 3161 timestamp
            '/f', $certPath
        )
        if ($CertificatePassword) { $arguments += @('/p', $CertificatePassword) }
        $arguments += $file

        # Output is captured rather than streamed: signtool echoes its own
        # arguments on some failures, and those include the password.
        $output = & $signTool @arguments 2>&1
        if ($LASTEXITCODE -ne 0) {
            $safe = ($output | Out-String)
            if ($CertificatePassword) { $safe = $safe.Replace($CertificatePassword, '***') }
            Write-Host $safe
            throw "Signing failed for $file"
        }

        Write-Host ("  signed    {0}" -f (Split-Path $file -Leaf))
    }

    # ------------------------------------------------------------- verify ---
    # Signing that "succeeded" but produced something Windows will not accept is
    # worse than not signing, so every file is checked before we call it done.
    foreach ($file in $Files) {
        $name = Split-Path $file -Leaf

        if ($AllowUntrustedRoot) {
            # Structural check: a signature is present, covers this file, and
            # carries a timestamp. Chain trust is deliberately not required.
            $signature = Get-AuthenticodeSignature $file
            if (-not $signature.SignerCertificate) {
                throw "No signature was produced for $file"
            }
            if ($signature.Status -eq 'HashMismatch') {
                throw "The signature does not match the contents of $file"
            }
            if (-not $signature.TimeStamperCertificate) {
                throw "The signature on $file is not timestamped"
            }
            Write-Host ("  verified  {0} (signature and timestamp present; chain trust not checked)" -f $name)
            continue
        }

        $verify = & $signTool verify /pa /q $file 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host ($verify | Out-String)
            throw "Signature verification failed for $file"
        }
        Write-Host ("  verified  {0}" -f $name)
    }
}
finally {
    if (Test-Path $certPath) {
        Remove-Item $certPath -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "  signing complete"
