; Inno Setup script for RescueDesk.
; Build: dotnet publish (see build-installer.ps1), then compile this file with ISCC.exe.

#define AppName "RescueDesk"
; The version is passed in by build-installer.ps1 (/DAppVersion=x.y.z); this is only a fallback.
#ifndef AppVersion
  #define AppVersion "0.0.0-local"
#endif
#define AppExe "RescueDesk.exe"

[Setup]
AppId={{8D0F2B0A-D61B-4FAB-9983-FD843252692F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Jack Mutsers
AppPublisherURL=https://github.com/Jack-Mutsers/RescueDesk
AppCopyright=Copyright (C) 2026 Jack Mutsers. Licensed under GPL-3.0.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Assets\RescueDesk.ico
OutputDir=..\dist
OutputBaseFilename=RescueDesk-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; RescueDesk itself always runs as administrator (it reads drives directly), so install machine-wide.
PrivilegesRequired=admin

[Languages]
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; shellexec so the app's "run as administrator" manifest is honoured.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent shellexec
