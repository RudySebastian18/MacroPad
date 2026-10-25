; Instalador de MacroPad. Requiere Inno Setup 6.3 o superior (https://jrsoftware.org/isinfo.php).
; Antes de compilarlo, genera el ejecutable con publicar.bat (carpeta publish\win-x64).

#define MyAppName "MacroPad"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Tu nombre"
#define MyAppExeName "MacroPad.exe"

[Setup]
AppId={{15EC49A9-A854-4BDB-B48D-65C1071C8105}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\installer-output
OutputBaseFilename=MacroPad-Setup-{#MyAppVersion}
SetupIconFile=..\MacroPad\Assets\MacroPad.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Instalación solo para el usuario actual: no pide permisos de administrador
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; Flags: unchecked

[Files]
Source: "..\publish\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir MacroPad"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Quita el inicio automático con Windows si el usuario lo había activado
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v MacroPad /f"; Flags: runhidden; RunOnceId: "RemoveMacroPadAutoStart"
