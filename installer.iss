; Installateur Audio Share — Inno Setup 6
; Compiler : iscc installer.iss  (après dotnet publish, voir README)

#define MyAppName "Audio Share"
#define MyAppVersion "1.0.0"
#define MyAppExeName "AudioShare.exe"

[Setup]
AppId={{7C1E9A4D-52B8-4F63-9A0E-3B2D71C55A10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Selim
DefaultDirName={autopf}\Audio Share
DisableProgramGroupPage=yes
; Installation par utilisateur : aucun droit administrateur requis
PrivilegesRequired=lowest
OutputDir=installer
OutputBaseFilename=AudioShare-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "Lancer {#MyAppName} au démarrage de Windows"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "AudioShare"; ValueData: """{app}\{#MyAppExeName}"""; \
    Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent
