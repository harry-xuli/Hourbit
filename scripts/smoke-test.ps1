[CmdletBinding()]
param(
    [string]$PortableArchive,
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
    'single-instance-protocol'
)
$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PortableArchive)) {
    $PortableArchive = Join-Path $repositoryRoot 'artifacts\Moment-Portable-x64.zip'
} elseif (-not [System.IO.Path]::IsPathFullyQualified($PortableArchive)) {
    $PortableArchive = Join-Path $repositoryRoot $PortableArchive
}
$PortableArchive = [System.IO.Path]::GetFullPath($PortableArchive)

function Test-SmokeResult {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Self-test result file is missing: $Path"
    }

    $counts = @{}
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
    }

    foreach ($expected in $expectedEvents) {
        if ($counts[$expected] -ne 1) {
            throw "Expected event '$expected' exactly once; observed $($counts[$expected])."
        }
    }
    if ($lines.Count -ne $expectedEvents.Count) {
        throw "Expected exactly $($expectedEvents.Count) JSONL entries; observed $($lines.Count)."
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

$temporaryRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar)
$runName = 'Moment-Smoke-' + [Guid]::NewGuid().ToString('N')
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
    $executable = Join-Path $portableDirectory 'Moment.App.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published executable is missing from the portable archive: $executable"
    }
    if (-not (Test-Path -LiteralPath (
            Join-Path $portableDirectory 'portable.flag') -PathType Leaf)) {
        throw 'portable.flag is missing from the portable archive.'
    }

    $quotedOutputDirectory = '"' + $outputDirectory + '"'
    $process = Start-Process `
        -FilePath $executable `
        -ArgumentList @('--self-test', $quotedOutputDirectory) `
        -WindowStyle Hidden `
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
        throw "Portable self-test exited with code $exitCode."
    }

    $resultPath = Join-Path $outputDirectory 'self-test.jsonl'
    $resultLines = Test-SmokeResult -Path $resultPath
    $resultLines
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
            'Moment-Smoke-',
            [System.StringComparison]::Ordinal)) {
        throw "Refusing to clean an unexpected smoke-test path: $resolvedRunRoot"
    }
    if (Test-Path -LiteralPath $resolvedRunRoot) {
        Remove-Item -LiteralPath $resolvedRunRoot -Recurse -Force
    }
}
