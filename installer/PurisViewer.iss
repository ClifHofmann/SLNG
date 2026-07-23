#define MyAppName "Puris Viewer"
; Overridable via ISCC's /DMyAppVersion=... command-line define (the CI workflow passes the
; git tag that triggered the build) so the installer filename/AppVersion always matches the
; actual release instead of this hardcoded fallback, which is only for local manual builds.
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0-alpha"
#endif
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
OutputBaseFilename=PurisViewer_Setup_{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; A single recursive catch-all: Godot's .NET export puts PurisViewer.exe/.pck directly in
; build\windows\ but the game-logic DLLs (SLNG.App/Core/Net/Assets) inside a
; data_SLNG.App_windows_x86_64\ subfolder, not at the top level -- a separate top-level
; "*.dll" line (this file's original round) matches zero files and hard-errors the compile
; ("No files found matching ...\*.dll", live-tested), since Inno Setup treats a Source:
; wildcard matching nothing as an error by default. recursesubdirs already covers the .exe,
; .pck, and the DLL subfolder in one pass, so the specific lines were redundant besides.
Source: "..\build\windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
