<#
.SYNOPSIS
    在本机用个人代码签名证书给已构建的发布产物打 Authenticode 签名。

.DESCRIPTION
    代码签名证书的私钥被标记为「不可导出」，因此 CI（无证书）无法签名，
    产物保持 NotSigned。本脚本在装有证书的本机运行，给便携包内的 exe/dll
    和 Setup 安装程序打上个人签名，从而减少 SmartScreen「未知发布者」提示。

    仅对「已构建好的产物」签名；如需从源码重新构建，请改用
    build-release.ps1（不带 -SkipSign 即会自动签名）。

.PARAMETER PortableZip
    便携包 zip 的路径。会解压 → 给其中的 exe/dll 签名 → 重新打包覆盖原文件。

.PARAMETER SetupExe
    Inno Setup 安装程序（Hourbit-Setup-x64.exe）的路径。

.PARAMETER Thumbprint
    证书指纹。默认使用本项目的个人证书（CN=Harry）。

.PARAMETER SkipTimestamp
    不附加时间戳。离线环境可用，但证书过期后签名会失效（显示警告）。

.EXAMPLE
    .\scripts\sign-release.ps1 `
        -PortableZip artifacts\Hourbit-Portable-x64.zip `
        -SetupExe artifacts\Hourbit-Setup-x64.exe

.EXAMPLE
    # 离线环境（无法访问时间戳服务器）：
    .\scripts\sign-release.ps1 -PortableZip artifacts\Hourbit-Portable-x64.zip -SkipTimestamp
#>
[CmdletBinding()]
param(
    [string]$PortableZip,
    [string]$SetupExe,
    [string]$Thumbprint = '9CE426CC31B420A308F33BD233587D9A7071FED8',
    [switch]$SkipTimestamp
)

$ErrorActionPreference = 'Stop'

function Resolve-SigningCertificate {
    param([string]$Thumbprint)

    if (-not [string]::IsNullOrWhiteSpace($Thumbprint)) {
        $certificate = Get-ChildItem Cert:\CurrentUser\My |
            Where-Object { $_.Thumbprint -eq $Thumbprint -and $_.HasPrivateKey } |
            Select-Object -First 1
        if ($null -ne $certificate) {
            return $certificate
        }
        Write-Warning "未找到指纹为 '$Thumbprint' 的证书，回退到第一个可用的代码签名证书。"
    }

    $certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.HasPrivateKey } |
        Select-Object -First 1
    if ($null -eq $certificate) {
        throw '未在“当前用户\个人”证书存储中找到带私钥的代码签名证书。'
    }
    return $certificate
}

function Add-ReleaseSignature {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Path,
        [Parameter(Mandatory = $true)]
        $Certificate,
        [switch]$SkipTimestamp
    )

    foreach ($file in $Path) {
        $params = @{
            FilePath      = $file
            Certificate   = $Certificate
            HashAlgorithm = 'SHA256'
        }
        if (-not $SkipTimestamp) {
            $params.TimestampServer = 'http://timestamp.digicert.com'
        }

        $signature = Set-AuthenticodeSignature @params
        $allowedStatuses = @('Valid', 'UnknownError', 'NotTrusted')
        $signerThumbprint = if ($null -eq $signature.SignerCertificate) {
            $null
        } else {
            $signature.SignerCertificate.Thumbprint
        }
        if ($signature.Status -notin $allowedStatuses -or
            $signerThumbprint -ne $Certificate.Thumbprint) {
            throw "签名失败：$file -> $($signature.Status) $($signature.StatusMessage)"
        }
        Write-Output "已签名：$file（状态：$($signature.Status)）"
    }
}

function Sign-PortableZip {
    param(
        [string]$PortableZip,
        $Certificate,
        [switch]$SkipTimestamp
    )

    if ([string]::IsNullOrWhiteSpace($PortableZip)) { return }
    if (-not (Test-Path -LiteralPath $PortableZip)) {
        throw "便携包不存在：$PortableZip"
    }

    $tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('hourbit-sign-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $tempDirectory | Out-Null
        Expand-Archive -LiteralPath $PortableZip -DestinationPath $tempDirectory

        $binaries = Get-ChildItem -LiteralPath $tempDirectory -Recurse -File |
            Where-Object { $_.Extension -in '.exe', '.dll' }
        if ($binaries.Count -eq 0) {
            Write-Warning "便携包内未发现 exe/dll：$PortableZip"
            return
        }

        Add-ReleaseSignature -Path $binaries.FullName -Certificate $Certificate -SkipTimestamp:$SkipTimestamp

        $backup = $PortableZip + '.unsigned'
        if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
        Move-Item -LiteralPath $PortableZip -Destination $backup
        Compress-Archive -Path (Join-Path $tempDirectory '*') -DestinationPath $PortableZip -CompressionLevel Optimal
        Write-Output "便携包已重新打包（原文件备份为 $backup）。"
    } finally {
        if (Test-Path -LiteralPath $tempDirectory) {
            Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

$certificate = Resolve-SigningCertificate -Thumbprint $Thumbprint
Write-Output "使用证书：$($certificate.Subject)（$($certificate.Thumbprint)）"

if (-not [string]::IsNullOrWhiteSpace($SetupExe)) {
    if (-not (Test-Path -LiteralPath $SetupExe)) {
        throw "Setup 安装程序不存在：$SetupExe"
    }
    Add-ReleaseSignature -Path @($SetupExe) -Certificate $certificate -SkipTimestamp:$SkipTimestamp
}

Sign-PortableZip -PortableZip $PortableZip -Certificate $certificate -SkipTimestamp:$SkipTimestamp

Write-Output '签名完成。'
