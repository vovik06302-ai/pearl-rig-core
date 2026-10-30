#define AppName "Pearl Rig Control"
#define AppVersion "3.3.10"
#define AppPublisher "Pearl Rig Control"
#define AppExeName "pearl_watchdog_agent.bat"

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
ArchitecturesInstallIn64BitMode=x64
OutputDir=Output
OutputBaseFilename=PearlRigControl-Setup
SetupIconFile=pearl-icon.ico
UninstallDisplayIcon={app}\pearl-icon.ico
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
Source: "pearl_watchdog_agent.bat"; DestDir: "{app}"; Flags: ignoreversion
Source: "pearl-icon.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "miner\*"; DestDir: "{app}\miner"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\pearl-icon.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\pearl-icon.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Запустить {#AppName}"; Flags: postinstall nowait skipifsilent
