; Inno Setup script for SawnTask. Built by tools\build-release.ps1, which passes the version
; and publishes the single-file SawnTask.exe to dist\portable first:
;   ISCC.exe /DAppVersion=1.0.0 installer\SawnTask.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6C2E8A4F-4B7D-4E0A-9C1B-5D3F2A7E9B10}
AppName=SawnTask
AppVersion={#AppVersion}
AppVerName=SawnTask {#AppVersion}
AppPublisher=soldsawn
AppPublisherURL=https://github.com/soldsawn/SawnTask
AppSupportURL=https://github.com/soldsawn/SawnTask/issues
AppUpdatesURL=https://github.com/soldsawn/SawnTask/releases
DefaultDirName={autopf}\SawnTask
DefaultGroupName=SawnTask
DisableProgramGroupPage=yes
; Installs for the current user without administrator rights; the setup offers
; "install for all users" (which asks for elevation) as an option.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
SetupIconFile=..\src\SawnTask\Assets\SawnTask.ico
UninstallDisplayIcon={app}\SawnTask.exe
OutputDir=..\dist
OutputBaseFilename=SawnTask-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\portable\SawnTask.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\SawnTask"; Filename: "{app}\SawnTask.exe"
Name: "{autodesktop}\SawnTask"; Filename: "{app}\SawnTask.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SawnTask.exe"; Description: "{cm:LaunchProgram,SawnTask}"; Flags: nowait postinstall skipifsilent
