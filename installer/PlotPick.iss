#define AppName "图拾 PlotPick"
#define AppVersion "0.3.0"
[Setup]
AppId={{607553B3-639C-437D-B83F-C9F628A079F3}
AppName={#AppName}
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\PlotPick
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=PlotPick-0.3.0-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\branding\PlotPick.ico
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\PlotPick.exe
CloseApplications=yes
RestartApplications=no
[Files]
Source: "..\artifacts\publish-0.3.0\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\PlotPick.exe"; IconFilename: "{app}\PlotPick.exe"; IconIndex: 0
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\PlotPick.exe"; IconFilename: "{app}\PlotPick.exe"; IconIndex: 0; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
[Run]
Filename: "{app}\PlotPick.exe"; Description: "Launch PlotPick"; Flags: nowait postinstall skipifsilent
