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
  StartupApprovedSubkey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  LegacyStartupValueName = 'Moment';
  LegacyExecutableName = 'Moment.App.exe';
  StartupApprovedDataLength = 12;
  StartupApprovedEnabledState = 2;
  WinErrorSuccess = 0;
  WinErrorFileNotFound = 2;
  WinRegBinary = 3;
  RRF_RT_REG_BINARY = 8;
  RRF_SUBKEY_WOW6464KEY = 65536;
  RRF_ZEROONFAILURE = 536870912;

type
  TStartupApprovalData = record
    State: Cardinal;
    TimestampLow: Cardinal;
    TimestampHigh: Cardinal;
  end;

function RegGetValueNative(
  RootKey: Integer; SubKey, ValueName: String; Flags: Cardinal;
  var ValueType: Cardinal; var Data: TStartupApprovalData;
  var DataSize: Cardinal): Longint;
  external 'RegGetValueW@advapi32.dll stdcall';

function IsLegacyStartupCommand(const Command: String): Boolean;
var
  Normalized: String;
  LegacyPath: String;
begin
  Normalized := Trim(Command);
  StringChangeEx(Normalized, '/', '\', True);
  LegacyPath := ExpandConstant('{app}\') + LegacyExecutableName;
  Result :=
    (CompareText(Normalized, LegacyPath + ' --background') = 0) or
    (CompareText(Normalized, '"' + LegacyPath + '" --background') = 0);
end;

function IsRecognizedEnabledApproval(
  const ApprovalData: TStartupApprovalData): Boolean;
begin
  { The canonical enabled value is 02 followed by eleven zero bytes. Treat
    every other present encoding conservatively because this format is not
    publicly documented by Windows. }
  Result :=
    (ApprovalData.State = StartupApprovedEnabledState) and
    (ApprovalData.TimestampLow = 0) and
    (ApprovalData.TimestampHigh = 0);
end;

function StartupApprovedQueryFlags(): Cardinal;
begin
  Result := RRF_RT_REG_BINARY or RRF_ZEROONFAILURE;
  if IsWin64 then
    Result := Result or RRF_SUBKEY_WOW6464KEY;
end;

function IsStartupApprovedForMigration(): Boolean;
var
  ApprovalData: TStartupApprovalData;
  DataSize: Cardinal;
  QueryResult: Longint;
  ValueType: Cardinal;
begin
  { RegGetValueW preserves the exact Win32 status and needs no opened handle.
    The fixed-size buffer plus RRF_ZEROONFAILURE prevents use of partial data.
    On 64-bit Windows, query Explorer's 64-bit view explicitly. }
  ApprovalData.State := 0;
  ApprovalData.TimestampLow := 0;
  ApprovalData.TimestampHigh := 0;
  DataSize := SizeOf(ApprovalData);
  ValueType := 0;
  QueryResult := RegGetValueNative(
    HKCU, StartupApprovedSubkey, LegacyStartupValueName,
    StartupApprovedQueryFlags(), ValueType,
    ApprovalData, DataSize);

  { Only a verified missing key/value uses normal Run-key semantics. Access
    denial, wrong type/size, malformed data, and every unexpected error fail
    closed. Never write or delete StartupApproved here. }
  if QueryResult = WinErrorFileNotFound then
  begin
    Result := True;
    exit;
  end;
  if QueryResult <> WinErrorSuccess then
  begin
    Result := False;
    exit;
  end;
  if (ValueType <> WinRegBinary) or
      (DataSize <> StartupApprovedDataLength) then
  begin
    Result := False;
    exit;
  end;
  Result := IsRecognizedEnabledApproval(ApprovalData);
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
  Result := IsLegacyStartupCommand(ExistingCommand) and
    IsStartupApprovedForMigration();
end;
