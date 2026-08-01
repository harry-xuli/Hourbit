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
    "StartupApprovedSubkey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';",
    "LegacyStartupValueName = 'Moment';",
    "LegacyExecutableName = 'Moment.App.exe';",
    "StringChangeEx(Normalized, '/', '\', True);",
    "LegacyPath := ExpandConstant('{app}\') + LegacyExecutableName;",
    "(CompareText(Normalized, LegacyPath + ' --background') = 0) or",
    '(CompareText(Normalized, ''"'' + LegacyPath + ''" --background'') = 0);',
    'if not RegValueExists(',
    'HKCU, StartupApprovedSubkey, LegacyStartupValueName) then',
    'if not RegQueryBinaryValue(',
    'HKCU, StartupApprovedSubkey, LegacyStartupValueName, ApprovalData) then',
    'Result := IsRecognizedEnabledApproval(ApprovalData);',
    'if not RegQueryStringValue(',
    'HKCU, LegacyStartupSubkey, LegacyStartupValueName, ExistingCommand) then',
    'Result := IsLegacyStartupCommand(ExistingCommand) and',
    'IsStartupApprovedForMigration();'
)
foreach ($fragment in $requiredCode) {
    if (-not $source.Contains($fragment)) {
        throw "Installer startup migration is missing required logic: $fragment"
    }
}
if ($source -match '(?i)RegDeleteValue|UninstallDelete') {
    throw 'Installer compatibility logic must not delete registry values or user data.'
}
if ($source.Contains('(CompareText(Normalized, LegacyPath) = 0)') -or
    $source.Contains('(CompareText(Normalized, ''"'' + LegacyPath + ''"'') = 0)')) {
    throw 'Startup migration must reject legacy commands that omit --background.'
}

function Test-LegacyStartupCommandContract {
    param(
        [AllowEmptyString()][string]$Command,
        [Parameter(Mandatory = $true)][string]$LegacyPath
    )

    $normalized = $Command.Trim().Replace('/', '\')
    return @(
        "$LegacyPath --background",
        ('"' + $LegacyPath + '" --background')
    ) -icontains $normalized
}

$probePath = 'C:\Users\Name With Space\AppData\Local\Programs\Moment\Moment.App.exe'
$matchingCommands = @(
    "  $probePath --background  ",
    ($probePath.ToUpperInvariant().Replace('\', '/') + ' --BACKGROUND'),
    ('"' + $probePath.ToUpperInvariant().Replace('\', '/') + '" --background')
)
$nonMatchingCommands = @(
    '',
    $probePath,
    ('"' + $probePath + '"'),
    '"C:\Other\Moment.App.exe" --background',
    '"C:\Users\Name With Space\AppData\Local\Programs\Moment\Hourbit.exe" --background',
    ('"' + $probePath + '" --unexpected')
)
foreach ($command in $matchingCommands) {
    if (-not (Test-LegacyStartupCommandContract $command $probePath)) {
        throw "Startup migration rejected a legacy command form: $command"
    }
}

function Test-StartupApprovedContract {
    param(
        [Parameter(Mandatory = $true)][bool]$Present,
        [AllowNull()][byte[]]$Data
    )

    if (-not $Present) {
        return $true
    }
    if ($null -eq $Data -or $Data.Length -ne 12 -or $Data[0] -ne 2) {
        return $false
    }
    return @($Data[1..11] | Where-Object { $_ -ne 0 }).Count -eq 0
}

$enabledState = [byte[]](2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
$approvalCases = @(
    @{ Name = 'absent'; Present = $false; Data = $null; Expected = $true },
    @{ Name = 'canonical enabled'; Present = $true; Data = $enabledState; Expected = $true },
    @{ Name = 'disabled 03'; Present = $true; Data = [byte[]](3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8); Expected = $false },
    @{ Name = 'disabled 07'; Present = $true; Data = [byte[]](7, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8); Expected = $false },
    @{ Name = 'short unknown'; Present = $true; Data = [byte[]](2); Expected = $false },
    @{ Name = 'nonzero enabled payload'; Present = $true; Data = [byte[]](2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0); Expected = $false },
    @{ Name = 'unknown state 04'; Present = $true; Data = [byte[]](4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0); Expected = $false },
    @{ Name = 'empty present value'; Present = $true; Data = [byte[]]@(); Expected = $false }
)
foreach ($case in $approvalCases) {
    $actual = Test-StartupApprovedContract -Present $case.Present -Data $case.Data
    if ($actual -ne $case.Expected) {
        throw "StartupApproved contract failed for $($case.Name): expected $($case.Expected), got $actual"
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
Write-Output 'Validated startup command matrix: --background required; quoted/unquoted, case and separator forms'
Write-Output 'Validated StartupApproved matrix: absent or canonical 12-byte enabled state only'
