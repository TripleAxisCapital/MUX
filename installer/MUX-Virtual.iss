#ifndef SourceDir
  #define SourceDir "..\artifacts\MUX.Virtual"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installers"
#endif

#define MyAppName "MUX Virtual Displays"
#define MyAppPublisher "Triple Axis Capital"
#define MyAppExeName "MUX.Virtual.exe"
#define MyAppVersion GetFileVersion(AddBackslash(SourceDir) + MyAppExeName)

[Setup]
AppId={{D2018F1B-66D5-4A67-8B16-637A82D0AFD2}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\MUX Virtual Displays
DefaultGroupName=MUX Virtual Displays
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=MUX-Virtual-Setup-x64
SetupIconFile=..\src\MUX.App\Assets\mux-app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=MUX Virtual Displays Installer
VersionInfoProductName={#MyAppName}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MUX Virtual Displays"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\MUX Virtual Displays"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch MUX Virtual Displays"; Flags: nowait postinstall skipifsilent

; The display driver is deliberately not trusted or staged by Setup.
; Driver activation remains explicit in MUX Virtual until it is production-signed.
