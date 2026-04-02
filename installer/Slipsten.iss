#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef PublishDir
  #define PublishDir "artifacts\publish"
#endif

#define MyAppName "Slipsten"
#define MyAppExeName "Slipsten.exe"

[Setup]
AppId={{D5A0A9E5-6AEF-4630-8E46-FE93A5C2D4D1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Slipsten
DefaultDirName={localappdata}\Programs\Slipsten
DefaultGroupName=Slipsten
DisableProgramGroupPage=yes
OutputDir=artifacts\installer
OutputBaseFilename=Slipsten-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
SetupIconFile=..\src\Slipsten\Resources\slipsten.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Slipsten"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Uninstall Slipsten"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Slipsten"; Flags: nowait postinstall skipifsilent
