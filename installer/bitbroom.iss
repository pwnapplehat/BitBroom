; BitBroom Inno Setup script.
; Build with:  iscc installer\bitbroom.iss
; Expects published binaries in dist\win-x64 (run build\publish.ps1 first).

#define AppName "BitBroom"
#define AppVersion "1.0.0"
#define AppPublisher "BitBroom Contributors"
#define AppURL "https://github.com/pwnapplehat/BitBroom"
#define DistDir "..\dist\win-x64"

[Setup]
AppId={{7D4C1B22-9B77-4B39-A0C7-52B04B4E9F31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
DefaultDirName={autopf}\{#AppName}
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
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\BitBroom.exe

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
Filename: "{app}\BitBroom.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
