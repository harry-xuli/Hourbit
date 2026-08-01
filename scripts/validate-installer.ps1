[CmdletBinding()]
param(
    [string]$InstallerScript
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($InstallerScript)) {
    $InstallerScript = Join-Path $PSScriptRoot '..\installer\Moment.iss'
}
$installerPath = [IO.Path]::GetFullPath($InstallerScript)
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Installer script is missing: $installerPath"
}
$source = Get-Content -Raw -LiteralPath $installerPath

function Get-SectionLines {
    param([Parameter(Mandatory = $true)][string]$Name)

    $match = [regex]::Match(
        $source,
        "(?ms)^\[$([regex]::Escape($Name))\]\s*\r?\n(?<body>.*?)(?=^\[|\z)")
    if (-not $match.Success) {
        throw "Installer section [$Name] is missing."
    }
    return @($match.Groups['body'].Value -split '\r?\n' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -ne '' -and -not $_.StartsWith(';') })
}

$expectedDeletes = @(
    'Type: files; Name: "{app}\Moment.App.exe"',
    'Type: files; Name: "{userprograms}\时刻\时刻.lnk"',
    'Type: files; Name: "{userdesktop}\时刻.lnk"'
)
$actualDeletes = @(Get-SectionLines 'InstallDelete')
if ($actualDeletes.Count -ne $expectedDeletes.Count -or
    @(Compare-Object $expectedDeletes $actualDeletes).Count -ne 0) {
    throw "[InstallDelete] must contain only the three approved legacy targets. Observed: $($actualDeletes -join ' | ')"
}
if (($actualDeletes -join "`n") -match
    '(?i)(\*|\?|recursesubdirs|userappdata|moment\.db|\\data(?:\\|"))') {
    throw '[InstallDelete] contains a wildcard, recursive flag, or user-data target.'
}

$registryLines = @(Get-SectionLines 'Registry')
$expectedRegistry =
    'Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ' +
    'ValueType: string; ValueName: "Moment"; ' +
    'ValueData: """{app}\{#AppAssemblyName}.exe"" --background"; ' +
    'Flags: preservestringtype; Check: ShouldMigrateLegacyStartup'
if ($registryLines.Count -ne 1 -or $registryLines[0] -cne $expectedRegistry) {
    throw '[Registry] must contain only the conditional preserved startup migration.'
}

$requiredCode = @(
    "LegacyStartupSubkey = 'Software\Microsoft\Windows\CurrentVersion\Run';",
    "LegacyStartupValueName = 'Moment';",
    "LegacyExecutableName = 'Moment.App.exe';",
    "StringChangeEx(Normalized, '/', '\', True);",
    "LegacyPath := ExpandConstant('{app}\') + LegacyExecutableName;",
    "(CompareText(Normalized, LegacyPath) = 0) or",
    "(CompareText(Normalized, LegacyPath + ' --background') = 0) or",
    '(CompareText(Normalized, ''"'' + LegacyPath + ''"'') = 0) or',
    '(CompareText(Normalized, ''"'' + LegacyPath + ''" --background'') = 0);',
    'if not RegQueryStringValue(',
    'HKCU, LegacyStartupSubkey, LegacyStartupValueName, ExistingCommand) then',
    'Result := IsLegacyStartupCommand(ExistingCommand);'
)
foreach ($fragment in $requiredCode) {
    if (-not $source.Contains($fragment)) {
        throw "Installer startup migration is missing required logic: $fragment"
    }
}
if ($source -match '(?i)RegDeleteValue|UninstallDelete') {
    throw 'Installer compatibility logic must not delete registry values or user data.'
}

function Test-LegacyStartupCommandContract {
    param(
        [AllowEmptyString()][string]$Command,
        [Parameter(Mandatory = $true)][string]$LegacyPath
    )

    $normalized = $Command.Trim().Replace('/', '\')
    return @(
        $LegacyPath,
        "$LegacyPath --background",
        ('"' + $LegacyPath + '"'),
        ('"' + $LegacyPath + '" --background')
    ) -icontains $normalized
}

$probePath = 'C:\Users\Name With Space\AppData\Local\Programs\Moment\Moment.App.exe'
$matchingCommands = @(
    $probePath,
    "  $probePath --background  ",
    ('"' + $probePath + '"'),
    ('"' + $probePath.ToUpperInvariant().Replace('\', '/') + '" --background')
)
$nonMatchingCommands = @(
    '',
    '"C:\Other\Moment.App.exe" --background',
    '"C:\Users\Name With Space\AppData\Local\Programs\Moment\Hourbit.exe" --background',
    ('"' + $probePath + '" --unexpected')
)
foreach ($command in $matchingCommands) {
    if (-not (Test-LegacyStartupCommandContract $command $probePath)) {
        throw "Startup migration rejected a legacy command form: $command"
    }
}
foreach ($command in $nonMatchingCommands) {
    if (Test-LegacyStartupCommandContract $command $probePath) {
        throw "Startup migration accepted a non-legacy command: $command"
    }
}

Write-Output 'Installer upgrade contract validation passed.'
Write-Output 'Validated cleanup: {app}\Moment.App.exe'
Write-Output 'Validated cleanup: legacy start-menu and desktop shortcuts only'
Write-Output 'Validated startup migration: existing HKCU Moment value, old executable only'
Write-Output 'Validated startup command matrix: quoted/unquoted, case and separator forms'
