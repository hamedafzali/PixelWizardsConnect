; installer.iss — Inno Setup script for PixelWizard Connect.
;
; This is NOT run by the build scripts. Compile it on Windows AFTER running
; packaging\windows\build-windows.ps1 (which produces
; dist\PixelWizardConnect-win-x64\):
;
;     iscc packaging\windows\installer.iss
;
; Install Inno Setup (https://jrsoftware.org/isinfo.php) to get the `iscc`
; compiler. The resulting installer is written to dist\.
;
; NOTE: Code signing the installer (and the contained exe) is a separate manual
; step requiring a Windows code-signing certificate. See build-windows.ps1.

#define MyAppName "PixelWizard Connect"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "PixelWizard"
#define MyAppExeName "PixelWizard.AvaloniaClient.exe"
#define MySourceDir "..\..\dist\PixelWizardConnect-win-x64"

[Setup]
AppId={{8F3C2A1B-9D4E-4F7A-B2C1-PIXELWIZARD01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PixelWizard Connect
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\..\dist
OutputBaseFilename=PixelWizardConnect-Setup-{#MyAppVersion}
SetupIconFile=PixelWizard.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
