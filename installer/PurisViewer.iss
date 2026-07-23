#define MyAppName "Puris Viewer"
#define MyAppVersion "0.1.0-alpha"
#define MyAppPublisher "Puris"
#define MyAppExeName "PurisViewer.exe"

[Setup]
; NOTE: The AppId uniquely identifies this application. Do not use the same AppId for different applications.
AppId={{D37F8A1B-4A2E-41F6-B20C-6C8E6D380064}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; Uncomment this line and ensure the icon.ico file is in your app folder before building!
; SetupIconFile=..\app\icon.ico
OutputDir=..\Output
OutputBaseFilename=PurisViewer_Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\build\windows\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\windows\*.pck"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\build\windows\*.dll"; DestDir: "{app}"; Flags: ignoreversion
; Include any additional folders created by Godot export (e.g. .NET runtime folders)
Source: "..\build\windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
