#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "..\artifacts\publish"
#endif
#ifndef ArtifactsDir
  #define ArtifactsDir AddBackslash(SourcePath) + "..\artifacts"
#endif
#ifndef AppVersion
  #error AppVersion must be provided by build-release.ps1
#endif
#ifndef AppProductName
  #error AppProductName must be provided by build-release.ps1
#endif
#ifndef AppAssemblyName
  #error AppAssemblyName must be provided by build-release.ps1
#endif

[Setup]
AppId={{8E5D37F4-A701-4B84-A71E-B7C0A8E46D51}
AppName={#AppProductName}
AppVersion={#AppVersion}
AppPublisher={#AppAssemblyName}
DefaultDirName={localappdata}\Programs\Moment
DefaultGroupName={#AppProductName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppProductName}
SetupIconFile=..\src\Moment.App\Assets\moment.ico
UninstallDisplayIcon={app}\{#AppAssemblyName}.exe
OutputDir={#ArtifactsDir}
OutputBaseFilename={#AppAssemblyName}-Setup-x64
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

[InstallDelete]
Type: files; Name: "{app}\Moment.App.exe"
Type: files; Name: "{userprograms}\时刻\时刻.lnk"
Type: files; Name: "{userdesktop}\时刻.lnk"

[Icons]
Name: "{group}\{#AppProductName}"; Filename: "{app}\{#AppAssemblyName}.exe"
Name: "{autodesktop}\{#AppProductName}"; Filename: "{app}\{#AppAssemblyName}.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Moment"; ValueData: """{app}\{#AppAssemblyName}.exe"" --background"; Flags: preservestringtype; Check: ShouldMigrateLegacyStartup

[Run]
Filename: "{app}\{#AppAssemblyName}.exe"; Description: "启动 {#AppProductName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  LegacyStartupSubkey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  LegacyStartupValueName = 'Moment';
  LegacyExecutableName = 'Moment.App.exe';

function IsLegacyStartupCommand(const Command: String): Boolean;
var
  Normalized: String;
  LegacyPath: String;
begin
  Normalized := Trim(Command);
  StringChangeEx(Normalized, '/', '\', True);
  LegacyPath := ExpandConstant('{app}\') + LegacyExecutableName;
  Result :=
    (CompareText(Normalized, LegacyPath) = 0) or
    (CompareText(Normalized, LegacyPath + ' --background') = 0) or
    (CompareText(Normalized, '"' + LegacyPath + '"') = 0) or
    (CompareText(Normalized, '"' + LegacyPath + '" --background') = 0);
end;

function ShouldMigrateLegacyStartup(): Boolean;
var
  ExistingCommand: String;
begin
  { InstallDelete runs before [Registry], so compare the configured command
    path without requiring the obsolete executable to still exist. }
  if not RegQueryStringValue(
      HKCU, LegacyStartupSubkey, LegacyStartupValueName, ExistingCommand) then
  begin
    Result := False;
    exit;
  end;
  Result := IsLegacyStartupCommand(ExistingCommand);
end;
