<#
.SYNOPSIS
    Captures the AFKLocker Setup window to a PNG for the README.

.DESCRIPTION
    Uses PrintWindow, which asks the window to render itself into an off-screen
    bitmap. Deliberately not a screen grab: a screen grab captures whatever
    pixels happen to be at those coordinates, which can include unrelated
    windows and anything private that is on screen at the time.

.NOTES
    Run after building. The window is opened and closed by this script.
#>
[CmdletBinding()]
param(
    [string]$SetupExe = (Join-Path (Split-Path $PSScriptRoot -Parent) 'build\AFKLockerSetup.exe'),
    [string]$OutputPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'assets\screenshot-setup.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Only P/Invoke declarations here: inline C# cannot see System.Drawing under
# PowerShell 7, so the bitmap work stays in PowerShell.
Add-Type -Namespace Win -Name Capture -MemberDefinition @'
[DllImport("user32.dll")]
public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

[DllImport("user32.dll")]
public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
'@

if (-not (Test-Path $SetupExe)) { throw "Build first: $SetupExe not found." }

$PW_RENDERFULLCONTENT = 2

$process = Start-Process $SetupExe -PassThru
try {
    for ($i = 0; $i -lt 80 -and $process.MainWindowHandle -eq 0; $i++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    }
    if ($process.MainWindowHandle -eq 0) { throw "The setup window did not appear." }

    Start-Sleep -Milliseconds 1500   # let the window finish painting

    $rect = New-Object Win.Capture+RECT
    # The window rect, not the client rect: PrintWindow renders the whole
    # window including its frame, so a client-sized bitmap clips the bottom.
    if (-not [Win.Capture]::GetWindowRect($process.MainWindowHandle, [ref]$rect)) {
        throw "Could not measure the window."
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) { throw "The window has no client area yet." }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $hdc = $graphics.GetHdc()
            try {
                if (-not [Win.Capture]::PrintWindow($process.MainWindowHandle, $hdc, $PW_RENDERFULLCONTENT)) {
                    throw "PrintWindow failed."
                }
            }
            finally {
                $graphics.ReleaseHdc($hdc)
            }
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
        Write-Host ("captured {0}x{1} -> {2}" -f $bitmap.Width, $bitmap.Height, $OutputPath)
    }
    finally {
        $bitmap.Dispose()
    }
}
finally {
    if (-not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 600
        if (-not $process.HasExited) { $process.Kill() }
    }
}
