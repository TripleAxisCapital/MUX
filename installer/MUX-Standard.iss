#ifndef SourceDir
  #define SourceDir "..\artifacts\MUX"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installers"
#endif
#ifndef SetupIconPath
  #define SetupIconPath "..\src\MUX.App\Assets\mux-app.ico"
#endif

#define MyAppName "MUX"
#define MyAppPublisher "Triple Axis Capital"
#define MyAppExeName "MUX.exe"
#define MyAppVersion GetVersionNumbersString(AddBackslash(SourceDir) + MyAppExeName)

[Setup]
AppId={{3D06F57F-0E79-42E0-B40B-1FEF0DDF63D1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\MUX
DefaultGroupName=MUX
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=MUX-Standard-Setup-x64
SetupIconFile={#SetupIconPath}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
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
VersionInfoDescription=MUX Standard Installer
VersionInfoProductName={#MyAppName}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MUX"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\MUX"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch MUX"; Flags: nowait postinstall skipifsilent
