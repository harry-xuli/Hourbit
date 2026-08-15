[CmdletBinding()]
param(
    [string]$PortableArchive,
    [string]$InstallerArtifact,
    [switch]$ValidateOnly,
    [string]$ResultFile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedEvents = @(
    'normal-delivery',
    'important-delivery',
    'completed',
    'snoozed',
    'restart-recovered',
    'missed-recovery',
    'single-instance-protocol',
    'schema-v1-upgrade',
    'schema-v2-upgrade',
    'todos-created',
    'todo-scheduler-exclusion',
    'release-metadata'
)
$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$applicationProject = Join-Path $repositoryRoot 'src\Hourbit.App\Hourbit.App.csproj'
$evaluatedProperties = (& dotnet msbuild $applicationProject `
    -nologo `
    '-getProperty:AssemblyName,Product,Version,ReleaseDate' |
    Out-String | ConvertFrom-Json).Properties
$assemblyName = [string]$evaluatedProperties.AssemblyName
$productName = [string]$evaluatedProperties.Product
$semanticVersion = [string]$evaluatedProperties.Version
$releaseDate = [string]$evaluatedProperties.ReleaseDate
if ($LASTEXITCODE -ne 0 -or
    [string]::IsNullOrWhiteSpace($assemblyName) -or
    [string]::IsNullOrWhiteSpace($productName) -or
    [string]::IsNullOrWhiteSpace($semanticVersion) -or
    [string]::IsNullOrWhiteSpace($releaseDate)) {
    throw 'Could not evaluate complete application release metadata for smoke testing.'
}
$versionLabel = [string]::Concat([char]0x7248, [char]0x672C)
$releasedOnLabel = [string]::Concat(
    [char]0x53D1, [char]0x5E03, [char]0x4E8E)
$settingsFooter =
    "$versionLabel $semanticVersion $([char]0x00B7) $releasedOnLabel $releaseDate"
if ([string]::IsNullOrWhiteSpace($PortableArchive)) {
    $PortableArchive = Join-Path $repositoryRoot (
        "artifacts\$assemblyName-Portable-x64.zip")
} elseif (-not [System.IO.Path]::IsPathFullyQualified($PortableArchive)) {
    $PortableArchive = Join-Path $repositoryRoot $PortableArchive
}
$PortableArchive = [System.IO.Path]::GetFullPath($PortableArchive)
if ([string]::IsNullOrWhiteSpace($InstallerArtifact)) {
    $InstallerArtifact = Join-Path $repositoryRoot (
        "artifacts\$assemblyName-Setup-x64.exe")
} elseif (-not [System.IO.Path]::IsPathFullyQualified($InstallerArtifact)) {
    $InstallerArtifact = Join-Path $repositoryRoot $InstallerArtifact
}
$InstallerArtifact = [System.IO.Path]::GetFullPath($InstallerArtifact)

function Test-SmokeResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Self-test result file is missing: $Path"
    }

    $counts = @{}
    $releaseMetadata = $null
    foreach ($expected in $expectedEvents) {
        $counts[$expected] = 0
    }

    $lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            throw 'The self-test result contains an empty line.'
        }
        try {
            $entry = $line | ConvertFrom-Json
        }
        catch {
            throw "The self-test result contains invalid JSON: $line"
        }
        if ($null -eq $entry.event -or
            -not $counts.ContainsKey([string]$entry.event)) {
            throw "The self-test result contains an unknown event: $($entry.event)"
        }
        $counts[[string]$entry.event]++
        if ([string]$entry.event -ceq 'release-metadata') {
            $releaseMetadata = $entry
        }
    }

    foreach ($expected in $expectedEvents) {
        if ($counts[$expected] -ne 1) {
            throw "Expected event '$expected' exactly once; observed $($counts[$expected])."
        }
    }
    if ($lines.Count -ne $expectedEvents.Count) {
        throw "Expected exactly $($expectedEvents.Count) JSONL entries; observed $($lines.Count)."
    }

    if ($null -eq $releaseMetadata) {
        throw 'The self-test result has no release metadata entry.'
    }
    $metadataChecks = @(
        @{ Name = 'productName'; Actual = [string]$releaseMetadata.productName; Expected = $productName },
        @{ Name = 'executableName'; Actual = [string]$releaseMetadata.executableName; Expected = $assemblyName },
        @{ Name = 'semanticVersion'; Actual = [string]$releaseMetadata.semanticVersion; Expected = $semanticVersion },
        @{ Name = 'releaseDate'; Actual = [string]$releaseMetadata.releaseDate; Expected = $releaseDate },
        @{ Name = 'settingsFooter'; Actual = [string]$releaseMetadata.settingsFooter; Expected = $settingsFooter }
    )
    foreach ($check in $metadataChecks) {
        if ($check.Actual -cne $check.Expected) {
            throw "Release metadata '$($check.Name)' mismatch: expected '$($check.Expected)', observed '$($check.Actual)'."
        }
    }

    return $lines
}

if ($ValidateOnly) {
    if (-not [string]::IsNullOrWhiteSpace($ResultFile)) {
        $validatedLines = Test-SmokeResult -Path (
            [System.IO.Path]::GetFullPath($ResultFile))
        $validatedLines
    }
    Write-Output 'Smoke-test validation passed.'
    exit 0
}
if (-not [string]::IsNullOrWhiteSpace($ResultFile)) {
    throw '-ResultFile can only be used with -ValidateOnly.'
}
if (-not (Test-Path -LiteralPath $PortableArchive -PathType Leaf)) {
    throw "Portable archive is missing: $PortableArchive"
}
if (-not (Test-Path -LiteralPath $InstallerArtifact -PathType Leaf)) {
    throw "Installer artifact is missing: $InstallerArtifact"
}
$expectedPortableName = "$assemblyName-Portable-x64.zip"
$expectedInstallerName = "$assemblyName-Setup-x64.exe"
if ([System.IO.Path]::GetFileName($PortableArchive) -cne $expectedPortableName) {
    throw "Portable artifact name must be '$expectedPortableName'."
}
if ([System.IO.Path]::GetFileName($InstallerArtifact) -cne $expectedInstallerName) {
    throw "Installer artifact name must be '$expectedInstallerName'."
}

$temporaryRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar)
$runPrefix = $assemblyName + '-Smoke-'
$runName = $runPrefix + [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $temporaryRoot $runName
$portableDirectory = Join-Path $runRoot 'portable'
$outputDirectory = Join-Path $runRoot 'self-test-output'
$process = $null

New-Item -ItemType Directory -Path $portableDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

try {
    Expand-Archive `
        -LiteralPath $PortableArchive `
        -DestinationPath $portableDirectory `
        -Force
    $executable = Join-Path $portableDirectory ($assemblyName + '.exe')
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published executable is missing from the portable archive: $executable"
    }
    if (-not (Test-Path -LiteralPath (
            Join-Path $portableDirectory 'portable.flag') -PathType Leaf)) {
        throw 'portable.flag is missing from the portable archive.'
    }

    $executableVersion =
        [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    if ($executableVersion.ProductName -cne $productName -or
        -not ($executableVersion.ProductVersion -ceq $semanticVersion -or
            $executableVersion.ProductVersion.StartsWith(
                $semanticVersion + '+',
                [System.StringComparison]::Ordinal))) {
        throw "Published EXE metadata does not agree with $productName $semanticVersion."
    }
    $installerVersion =
        [Diagnostics.FileVersionInfo]::GetVersionInfo($InstallerArtifact)
    # Inno Setup writes these fixed-width VERSIONINFO strings padded with
    # trailing spaces. Normalize only that representation detail before the
    # exact identity comparison.
    $installerProductName = ([string]$installerVersion.ProductName).TrimEnd()
    $installerProductVersion =
        ([string]$installerVersion.ProductVersion).TrimEnd()
    if ($installerProductName -cne $productName -or
        $installerProductVersion -cne $semanticVersion) {
        throw "Installer metadata does not agree with $productName $semanticVersion."
    }

    $quotedOutputDirectory = '"' + $outputDirectory + '"'
    $stderrPath = Join-Path $outputDirectory 'self-test-stderr.txt'
    $stdoutPath = Join-Path $outputDirectory 'self-test-stdout.txt'
    $process = Start-Process `
        -FilePath $executable `
        -ArgumentList @('--self-test', $quotedOutputDirectory) `
        -WindowStyle Hidden `
        -RedirectStandardError $stderrPath `
        -RedirectStandardOutput $stdoutPath `
        -PassThru

    if (-not $process.WaitForExit(30000)) {
        $process.Kill()
        $process.WaitForExit()
        throw 'Portable self-test exceeded the 30-second timeout.'
    }
    $process.WaitForExit()
    $process.Refresh()
    $exitCode = $process.ExitCode
    if ($exitCode -ne 0) {
        $stderr = if (Test-Path $stderrPath) { Get-Content $stderrPath -Raw } else { '' }
        $stdout = if (Test-Path $stdoutPath) { Get-Content $stdoutPath -Raw } else { '' }
        throw "Portable self-test exited with code $exitCode.`nSTDERR: $stderr`nSTDOUT: $stdout"
    }

    $resultPath = Join-Path $outputDirectory 'self-test.jsonl'
    $resultLines = Test-SmokeResult -Path $resultPath
    $resultLines
    Write-Output "Validated release agreement: $productName $semanticVersion ($releaseDate)."
    Write-Output "Validated settings footer: $settingsFooter"
    Write-Output "Validated artifacts: $expectedPortableName and $expectedInstallerName"
    Write-Output "Portable smoke test passed in $($process.TotalProcessorTime.TotalMilliseconds) ms CPU time."
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }

    $resolvedRunRoot = [System.IO.Path]::GetFullPath($runRoot)
    $resolvedParent = [System.IO.Path]::GetDirectoryName($resolvedRunRoot)
    $resolvedLeaf = [System.IO.Path]::GetFileName($resolvedRunRoot)
    if (-not [string]::Equals(
            $resolvedParent,
            $temporaryRoot,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $resolvedLeaf.StartsWith(
            $runPrefix,
            [System.StringComparison]::Ordinal)) {
        throw "Refusing to clean an unexpected smoke-test path: $resolvedRunRoot"
    }
    if (Test-Path -LiteralPath $resolvedRunRoot) {
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
