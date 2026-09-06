; AFKLocker installer (Inno Setup 6)
;
; Installs per-user by default so no elevation prompt is needed just to get a
; shortcut on the desktop. The power settings AFKLocker changes belong to the
; user's own power plan, which does not require administrator rights on a
; normal Windows install.
;
; Build:  ISCC.exe /O<output dir> installer\AFKLocker.iss
; Expects the compiled binaries in build\ (run tools\Build.ps1 first).

#define AppName        "AFKLocker"
#define AppVersion     "0.1.0"
#define AppPublisher   "Augusto Bastos"
#define AppUrl         "https://github.com/augbastos/AFKLocker"
#define AppExe         "AFKLocker.exe"
#define SetupExe       "AFKLockerSetup.exe"

[Setup]
AppId={{7C4E1F2A-9B3D-4E6F-8A15-2D7C4B9E0A33}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
LicenseFile=..\LICENSE
OutputBaseFilename=AFKLocker-{#AppVersion}-setup
SetupIconFile=..\assets\afklocker.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=6.3
AppCopyright=Copyright (C) 2026 {#AppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "displayoff"; Description: "Also create a ""Display Off"" shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\build\AFKLocker.exe";       DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\AFKLockerSetup.exe";  DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\AFKLocker.Core.dll";  DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md";                 DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE";                   DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";       Filename: "{app}\{#AppExe}";   Comment: "Lock this PC and keep it running"
Name: "{group}\{#AppName} Setup"; Filename: "{app}\{#SetupExe}"; Comment: "Check and configure Windows power settings"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}";   Comment: "Lock this PC and keep it running"; Tasks: desktopicon
Name: "{autodesktop}\{#AppName} Display Off"; Filename: "{app}\{#AppExe}"; Parameters: "--display-off"; Comment: "Turn the display off without locking"; Tasks: displayoff

[Run]
; Opening setup after install is the honest default: AFKLocker is not useful
; until Windows is actually configured for closed-lid operation.
Filename: "{app}\{#SetupExe}"; Description: "Check this machine's power settings now"; Flags: postinstall nowait skipifsilent

[Code]
// Uninstalling asks whether to put the power settings back. Some people
// deliberately keep the machine configured this way and only want the
// shortcuts gone, so this is a question rather than an automatic revert.
// A silent uninstall restores, which is the least surprising default for
// unattended removal.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SetupPath: String;
  ResultCode: Integer;
  ShouldRestore: Boolean;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  SetupPath := ExpandConstant('{app}\{#SetupExe}');
  if not FileExists(SetupPath) then
    Exit;

  if UninstallSilent then
    ShouldRestore := True
  else
    ShouldRestore :=
      MsgBox('Restore the Windows power settings AFKLocker changed?' + #13#10 + #13#10 +
             'Yes - put the lid close and sleep settings back the way they were.' + #13#10 +
             'No  - leave this machine configured for closed-lid operation.',
             mbConfirmation, MB_YESNO) = IDYES;

  if ShouldRestore then
  begin
    // A failure here must never block the uninstall: the backup files stay on
    // disk either way, so the values can still be restored by hand.
    Exec(SetupPath, '--restore-silent', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
