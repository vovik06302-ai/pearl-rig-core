#define AppName "Pearl Rig Control"
#define AppVersion "3.4.0"
#define AppPublisher "Pearl Rig Control"
#define AppExeName "PearlRigControl.exe"

[Setup]
AppId={{D5C60A71-6B5B-4FB7-99F4-5BAE302C9CF0}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\Pearl Rig Control
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=PearlRigControl-Setup
SetupIconFile=pearl-icon.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные ярлыки:"; Flags: unchecked

[Files]
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "rig-config.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "pearl-icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "onezerominer.exe"; DestDir: "{app}\miner"; Flags: ignoreversion skipifsourcedoesntexist
Source: "miner\*"; DestDir: "{app}\miner"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\pearl-icon.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\pearl-icon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Запустить {#AppName}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent
