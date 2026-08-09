[CmdletBinding()]
param(
    [string]$InnoCompiler,
    [switch]$ValidateOnly,
    [string]$ValidationProbePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts'))
$publishDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot 'publish'))
$portableDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot 'portable'))
$releaseBuildDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot 'release-build'))
$applicationProject = Join-Path $repositoryRoot 'src\Moment.App\Moment.App.csproj'
$windowsProject = Join-Path $repositoryRoot 'src\Moment.Windows\Moment.Windows.csproj'
$solution = Join-Path $repositoryRoot 'Moment.slnx'
$installerScript = Join-Path $repositoryRoot 'installer\Moment.iss'
$installerValidationScript = Join-Path $repositoryRoot 'scripts\validate-installer.ps1'

function Test-SemanticVersion {
    param([AllowEmptyString()][string]$Value)

    if ([string]::IsNullOrEmpty($Value)) {
        return $false
    }
    $pattern =
        '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)' +
        '(?:-(?:(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)' +
        '(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?' +
        '(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'
    return $Value -cmatch $pattern
}

function Assert-SemVerValidationContract {
    $valid = @(
        '0.2.0',
        '0.2.0-alpha',
        '0.2.0-alpha.1',
        '0.2.0-0',
        '0.2.0-rc.1+build.001',
        '1.2.3+001'
    )
    $invalid = @(
        '0.2',
        '01.2.3',
        '0.2.0-01',
        '0.2.0-alpha.01',
        '0.2.0-',
        '0.2.0+'
    )
    foreach ($candidate in $valid) {
        if (-not (Test-SemanticVersion $candidate)) {
            throw "SemVer validator rejected valid probe '$candidate'."
        }
    }
    foreach ($candidate in $invalid) {
        if (Test-SemanticVersion $candidate) {
            throw "SemVer validator accepted invalid probe '$candidate'."
        }
    }
}

function Get-RequiredProperty {
    param(
        [Parameter(Mandatory = $true)]
        [psobject]$Properties,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $value = [string]$Properties.$Name
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Evaluated MSBuild property '$Name' is missing."
    }
    return $value
}

function Get-ReleaseMetadata {
    $output = & dotnet msbuild $applicationProject `
        -nologo `
        '-getProperty:AssemblyName,Version,Product,ReleaseDate,SemanticVersion'
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild property evaluation failed with exit code $LASTEXITCODE."
    }

    try {
        $properties = (($output -join [Environment]::NewLine) |
            ConvertFrom-Json).Properties
    }
    catch {
        throw "MSBuild property evaluation returned invalid JSON: $($_.Exception.Message)"
    }

    $assemblyName = Get-RequiredProperty $properties 'AssemblyName'
    $semanticVersion = Get-RequiredProperty $properties 'SemanticVersion'
    $version = Get-RequiredProperty $properties 'Version'
    $product = Get-RequiredProperty $properties 'Product'
    $releaseDate = Get-RequiredProperty $properties 'ReleaseDate'

    if (-not (Test-SemanticVersion $semanticVersion)) {
        throw "Evaluated SemanticVersion '$semanticVersion' is not valid semantic versioning."
    }
    if ($version -cne $semanticVersion) {
        throw "Evaluated Version '$version' does not match SemanticVersion '$semanticVersion'."
    }

    $parsedDate = [DateTime]::MinValue
    if (-not [DateTime]::TryParseExact(
            $releaseDate,
            'yyyy-MM-dd',
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None,
            [ref]$parsedDate) -or
        $parsedDate.ToString(
            'yyyy-MM-dd',
            [Globalization.CultureInfo]::InvariantCulture) -cne $releaseDate) {
        throw "Evaluated ReleaseDate '$releaseDate' is not a valid ISO date (yyyy-MM-dd)."
    }

    if ($assemblyName.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
        throw "Evaluated AssemblyName '$assemblyName' is not safe for release filenames."
    }

    return [pscustomobject]@{
        AssemblyName = $assemblyName
        Version = $semanticVersion
        Product = $product
        ReleaseDate = $releaseDate
    }
}

Assert-SemVerValidationContract
$releaseMetadata = Get-ReleaseMetadata
$portableArchive = Join-Path $artifactsRoot (
    $releaseMetadata.AssemblyName + '-Portable-x64.zip')
$installerArtifact = Join-Path $artifactsRoot (
    $releaseMetadata.AssemblyName + '-Setup-x64.exe')

function Assert-ExactStagingDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Candidate,
        [Parameter(Mandatory = $true)]
        [string]$Expected
    )

    $candidatePath = [System.IO.Path]::GetFullPath($Candidate)
    $expectedPath = [System.IO.Path]::GetFullPath($Expected)
    $artifactPrefix = $artifactsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar

    if (-not $candidatePath.StartsWith(
            $artifactPrefix,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $candidatePath,
            $expectedPath,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing release cleanup outside the exact staging directory: $candidatePath"
    }
}

function Remove-ExactStagingDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Candidate,
        [Parameter(Mandatory = $true)]
        [string]$Expected
    )

    Assert-ExactStagingDirectory -Candidate $Candidate -Expected $Expected
    if (Test-Path -LiteralPath $Candidate) {
        Remove-Item -LiteralPath $Candidate -Recurse -Force
    }
}

function Resolve-InnoCompiler {
    if (-not [string]::IsNullOrWhiteSpace($InnoCompiler)) {
        $explicitPath = [System.IO.Path]::GetFullPath($InnoCompiler)
        if (-not (Test-Path -LiteralPath $explicitPath -PathType Leaf)) {
            throw "The explicit Inno Setup compiler does not exist: $explicitPath"
        }
        return $explicitPath
    }

    $candidates = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path ([Environment]::GetFolderPath(
            [Environment+SpecialFolder]::LocalApplicationData
        )) 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw 'Inno Setup 6 was not found in any approved deterministic location. Use -InnoCompiler with an official installation.'
}

function Write-Sha256File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $hash = Get-FileHash -LiteralPath $Path -Algorithm SHA256
    $line = '{0}  {1}' -f
        $hash.Hash.ToLowerInvariant(),
        [System.IO.Path]::GetFileName($Path)
    Set-Content -LiteralPath ($Path + '.sha256') -Value $line -Encoding Ascii -NoNewline
}

$publishProbe = if ($ValidateOnly -and
    -not [string]::IsNullOrWhiteSpace($ValidationProbePath)) {
    $ValidationProbePath
} else {
    $publishDirectory
}
Assert-ExactStagingDirectory -Candidate $publishProbe -Expected $publishDirectory
Assert-ExactStagingDirectory -Candidate $portableDirectory -Expected $portableDirectory

& powershell -NoProfile -ExecutionPolicy Bypass `
    -File $installerValidationScript `
    -InstallerScript $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Installer upgrade validation failed with exit code $LASTEXITCODE."
}

if ($ValidateOnly) {
    Write-Output 'Validated strict SemVer 2.0 probe matrix.'
    Write-Output "Validated product: $($releaseMetadata.Product)"
    Write-Output "Validated executable: $($releaseMetadata.AssemblyName).exe"
    Write-Output "Validated semantic version: $($releaseMetadata.Version)"
    Write-Output "Validated release date: $($releaseMetadata.ReleaseDate)"
    Write-Output "Validated artifact: $portableArchive"
    Write-Output "Validated artifact: $installerArtifact"
    Write-Output "Validated cleanup target: $publishDirectory"
    Write-Output "Validated cleanup target: $portableDirectory"
    exit 0
}
if (-not [string]::IsNullOrWhiteSpace($ValidationProbePath)) {
    throw '-ValidationProbePath can only be used with -ValidateOnly.'
}

$resolvedCompiler = Resolve-InnoCompiler
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

Push-Location $repositoryRoot
try {
    & dotnet test $solution -c Release
    if ($LASTEXITCODE -ne 0) {
        throw "Release tests failed with exit code $LASTEXITCODE."
    }

    Remove-ExactStagingDirectory `
        -Candidate $publishDirectory `
        -Expected $publishDirectory
    Remove-ExactStagingDirectory `
        -Candidate $portableDirectory `
        -Expected $portableDirectory
    Remove-ExactStagingDirectory `
        -Candidate $releaseBuildDirectory `
        -Expected $releaseBuildDirectory

    # Generate the RID-specific PRI before publishing the app. ProjectReference
    # property propagation is not sufficient on a clean checkout: the app PRI
    # build consumes Moment.Windows.pri from this exact output directory.
    & dotnet build $windowsProject `
        -c Release `
        -r win-x64 `
        --artifacts-path $releaseBuildDirectory `
        -p:EnableMsixTooling=true `
        -p:WindowsAppSDKSingleFileVerifyConfiguration=false
    if ($LASTEXITCODE -ne 0) {
        throw "Windows runtime resource build failed with exit code $LASTEXITCODE."
    }

    # The Windows PRI build can leave an MSBuild node holding Moment.Core.dll
    # briefly, which races the immediately following RID publish on Windows.
    & dotnet build-server shutdown
    if ($LASTEXITCODE -ne 0) {
        throw "Build-server shutdown failed with exit code $LASTEXITCODE."
    }

    & dotnet publish $applicationProject `
        -m:1 `
        -c Release `
        -r win-x64 `
        --self-contained true `
        --artifacts-path $releaseBuildDirectory `
        -p:PublishSingleFile=true `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Release publish failed with exit code $LASTEXITCODE."
    }

    $forbiddenPublishFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse |
        Where-Object {
            $_.Name -match '^(?i:moment\.db(?:-wal|-shm)?|settings\.json)$' -or
            $_.Extension -match '^(?i:\.sqlite|\.moment-backup)$'
        })
    if ($forbiddenPublishFiles.Count -ne 0) {
        throw "Release publish contains user data: $($forbiddenPublishFiles.FullName -join ', ')"
    }

    Copy-Item -LiteralPath $publishDirectory -Destination $portableDirectory -Recurse
    New-Item -Path (Join-Path $portableDirectory 'portable.flag') `
        -ItemType File -Force | Out-Null

    Compress-Archive `
        -Path (Join-Path $portableDirectory '*') `
        -DestinationPath $portableArchive `
        -CompressionLevel Optimal `
        -Force

    & $resolvedCompiler `
        "/DPublishDir=$publishDirectory" `
        "/DArtifactsDir=$artifactsRoot" `
        "/DAppVersion=$($releaseMetadata.Version)" `
        "/DAppProductName=$($releaseMetadata.Product)" `
        "/DAppAssemblyName=$($releaseMetadata.AssemblyName)" `
        $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $portableArchive -PathType Leaf) -or
        -not (Test-Path -LiteralPath $installerArtifact -PathType Leaf)) {
        throw 'One or more expected release artifacts were not created.'
    }

    Write-Sha256File -Path $portableArchive
    Write-Sha256File -Path $installerArtifact

    Get-Item -LiteralPath $portableArchive, $installerArtifact |
        Select-Object FullName, Length
    Get-FileHash -LiteralPath $portableArchive, $installerArtifact -Algorithm SHA256
}
finally {
    Pop-Location
}
