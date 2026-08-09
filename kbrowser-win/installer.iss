#define MyAppName "KBrowser"
#define MyAppVersion "1.1.0"
#define MyAppExeName "KBrowser.exe"

[Setup]
AppId={{F0DC9812-A7C6-40F2-80A9-5F804E78D5E1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\KBrowser
DefaultGroupName=KBrowser
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=KBrowser-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\KBrowser.exe

[Files]
Source: "dist\portable\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\KBrowser"; Filename: "{app}\KBrowser.exe"
Name: "{autodesktop}\KBrowser"; Filename: "{app}\KBrowser.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "바탕 화면에 KBrowser 바로가기 만들기"; GroupDescription: "바로가기:"; Flags: unchecked

[Run]
Filename: "{app}\KBrowser.exe"; Description: "KBrowser 실행"; Flags: nowait postinstall skipifsilent
