; Installateur Audio Share — Inno Setup 6
; Compiler : iscc installer.iss  (après dotnet publish, voir README)

#define MyAppName "Audio Share"
#define MyAppVersion "1.6.3"
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
; Installation interactive : case « Lancer Audio Share » en fin d'assistant
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent
; Mise à jour silencieuse (auto-update) : relancer l'app d'office
Filename: "{app}\{#MyAppExeName}"; Flags: nowait postinstall skipifnotsilent

[Code]
// Mise à jour : l'app vit dans la zone de notification sans fenêtre visible,
// on la ferme d'office avant de remplacer ses fichiers. Grâce à l'AppId
// identique, réinstaller par-dessus une version existante met simplement à
// jour en place (même dossier, même entrée de désinstallation).
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#MyAppExeName}', '',
         SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;
