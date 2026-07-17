; BitBroom Inno Setup script.
; Build with:  iscc installer\bitbroom.iss
; Expects published binaries in dist\win-x64 (run build\publish.ps1 first).

#define AppName "BitBroom"
#define AppVersion "1.2.5"
#define AppPublisher "BitBroom Contributors"
#define AppURL "https://bitbroom.app"
#define RepoURL "https://github.com/pwnapplehat/BitBroom"
#define DistDir "..\dist\win-x64"

[Setup]
AppId={{7D4C1B22-9B77-4B39-A0C7-52B04B4E9F31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#RepoURL}/issues
; Per-user install under %LocalAppData%\Programs — no UAC, no elevation. This is what
; lets the in-app updater install a new version fully silently (the VS Code model).
DefaultDirName={localappdata}\Programs\{#AppName}
UsePreviousAppDir=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=BitBroom-{#AppVersion}-setup
SetupIconFile=..\assets\icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Fixed per-user privileges: no "all users / just me" dialog, so /VERYSILENT never blocks.
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\BitBroom.exe
; In-app updater: silent installs wait for the running instance to exit,
; and close/restart it automatically when possible.
AppMutex=BitBroom.App.SingleInstance
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#DistDir}\BitBroom.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DistDir}\bitbroom-cli.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\BitBroom.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\BitBroom.exe"; Tasks: desktopicon

[Run]
; No skipifsilent: the in-app updater installs with /SILENT and the user expects the
; app to come back afterwards (the update flow closes it for the file swap).
Filename: "{app}\BitBroom.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall
