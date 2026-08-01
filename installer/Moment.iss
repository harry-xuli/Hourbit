#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "..\artifacts\publish"
#endif
#ifndef ArtifactsDir
  #define ArtifactsDir AddBackslash(SourcePath) + "..\artifacts"
#endif
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{8E5D37F4-A701-4B84-A71E-B7C0A8E46D51}
AppName=时刻
AppVersion={#AppVersion}
AppPublisher=Moment
DefaultDirName={localappdata}\Programs\Moment
DefaultGroupName=时刻
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=时刻
SetupIconFile=..\src\Moment.App\Assets\moment.ico
UninstallDisplayIcon={app}\Moment.App.exe
OutputDir={#ArtifactsDir}
OutputBaseFilename=Moment-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\时刻"; Filename: "{app}\Moment.App.exe"
Name: "{autodesktop}\时刻"; Filename: "{app}\Moment.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Moment.App.exe"; Description: "启动时刻"; Flags: nowait postinstall skipifsilent
