; Inno Setup 6 script for Mendrain.
; Built by tools/package.ps1, which passes the defines below. To build by hand:
;   ISCC.exe /DAppVersion=1.2.3 /DSourceDir=<publish folder> /DOutputDir=<output folder> installer\Mendrain.iss
;
; Installs per user (no administrator rights) into %LOCALAPPDATA%\Programs\Mendrain.
; The installer is not code-signed unless signing is added to the release process.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\Mendrain"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
; Keep AppId unchanged across releases so upgrades and uninstall find the existing installation.
AppId={{6B0E2F3A-8C4D-4E1B-9A57-3F2D8E6C1A90}
AppName=Mendrain
AppVersion={#AppVersion}
AppVerName=Mendrain {#AppVersion}
AppPublisher=Esteria Vaelis
AppCopyright=© 2026 Esteria Vaelis
AppComments=A cleaner way to create, extract, and manage archives on Windows.
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Mendrain Setup
DefaultDirName={autopf}\Mendrain
DefaultGroupName=Mendrain
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Mendrain-Setup-x64
SetupIconFile=..\src\Mendrain\Assets\Mendrain.ico
UninstallDisplayIcon={app}\Mendrain.exe
UninstallDisplayName=Mendrain
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startmenuicon"; Description: "Create a &Start menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Mendrain"; Filename: "{app}\Mendrain.exe"; Tasks: startmenuicon
Name: "{autodesktop}\Mendrain"; Filename: "{app}\Mendrain.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Mendrain.exe"; Description: "{cm:LaunchProgram,Mendrain}"; Flags: nowait postinstall skipifsilent

; Uninstall removes everything installed above. Preferences and the recent-archives list in
; %LOCALAPPDATA%\Mendrain are left in place; they are small and are not user documents.
