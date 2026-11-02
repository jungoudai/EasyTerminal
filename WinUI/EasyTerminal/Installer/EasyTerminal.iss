; Per-user setup: installs under %LOCALAPPDATA%\Programs, no administrator rights or certificate needed.
; Build through Build-Installer.ps1, which publishes the app and passes these defines.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\EasyTerminal\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish"
#endif

[Setup]
AppId={{7B0B5C7E-9D0E-4C55-9A38-5E2C8F6D1A40}
AppName=EasyTerminal
AppVersion={#AppVersion}
AppPublisher=gouzu
DefaultDirName={autopf}\EasyTerminal
DefaultGroupName=EasyTerminal
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=Output
OutputBaseFilename=EasyTerminal-Setup-{#AppVersion}-x64
SetupIconFile=..\EasyTerminal\Assets\EasyTerminal.ico
UninstallDisplayIcon={app}\EasyTerminal.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\EasyTerminal"; Filename: "{app}\EasyTerminal.exe"
Name: "{autodesktop}\EasyTerminal"; Filename: "{app}\EasyTerminal.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\EasyTerminal.exe"; Description: "{cm:LaunchProgram,EasyTerminal}"; Flags: nowait postinstall skipifsilent
