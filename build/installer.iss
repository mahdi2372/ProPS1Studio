; ProPS1 Studio installer script — compile with ISCC.exe
; Output: publish\ProPS1Studio-Setup-1.0.0-x64.exe

#define MyAppName "ProPS1 Studio"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
  #define MyAppPublisher "Mahdi"
#define MyAppURL "https://github.com/MScholtes/PS2EXE"
#define MyAppExeName "ProPS1Studio.exe"

[Setup]
AppId={{B7E4A1C2-9D53-4F0E-8A6B-3C21F5D7E904}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductTextVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=ProPS1Studio-Setup-{#MyAppVersion}-x64
SetupIconFile=..\src\ProPS1Studio.App\Assets\props1.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; per-user runtime data (logs/settings/tools) is left in %LOCALAPPDATA%\ProPS1Studio on purpose
Type: files; Name: "{app}\*.tmp"
