<#
.SYNOPSIS
    Downloads the pinned Xray core (xray.exe, wintun.dll) and seed geo files into assets/.

.DESCRIPTION
    - Xray version is read from tools/xray-version.txt (pinned on purpose: XHTTP parameters
      must stay compatible with the server).
    - The Xray zip is verified against the SHA2-256 from the release .dgst file.
    - Geo files come from runetfreedom/russia-v2ray-rules-dat (latest release) and are verified
      against their .sha256sum files.
    Works on Windows PowerShell 5.1 and PowerShell 7+.

.PARAMETER Force
    Re-download even if files already exist.

.PARAMETER Proxy
    Optional HTTP proxy for downloads, e.g. http://127.0.0.1:10809 (GitHub may be unreachable directly).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/fetch-xray.ps1
    powershell -ExecutionPolicy Bypass -File tools/fetch-xray.ps1 -Force -Proxy http://127.0.0.1:10809
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Proxy
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is very slow with the progress bar on PS 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$repoRoot = Split-Path -Parent $PSScriptRoot
$coreDir  = Join-Path $repoRoot 'assets\core'
$geoDir   = Join-Path $repoRoot 'assets\geo'
$version  = (Get-Content (Join-Path $PSScriptRoot 'xray-version.txt') -Raw).Trim()

$xrayZipUrl = "https://github.com/XTLS/Xray-core/releases/download/$version/Xray-windows-64.zip"
$geoBaseUrl = 'https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download'

function Invoke-Download([string]$Url, [string]$OutFile) {
    Write-Host "  GET $Url"
    $params = @{ Uri = $Url; OutFile = $OutFile; UseBasicParsing = $true }
    if ($Proxy) { $params.Proxy = $Proxy }
    Invoke-WebRequest @params
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("simplevpn-fetch-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $tmp, $coreDir, $geoDir | Out-Null

try {
    # ---------------- Xray core ----------------
    $xrayExe = Join-Path $coreDir 'xray.exe'
    $versionStamp = Join-Path $coreDir 'VERSION'
    $installed = if (Test-Path $versionStamp) { (Get-Content $versionStamp -Raw).Trim() } else { '' }

    if ($Force -or -not (Test-Path $xrayExe) -or $installed -ne $version) {
        Write-Host "Xray $version"
        $zip = Join-Path $tmp 'xray.zip'
        $dgst = Join-Path $tmp 'xray.zip.dgst'
        Invoke-Download $xrayZipUrl $zip
        Invoke-Download "$xrayZipUrl.dgst" $dgst

        $expected = Select-String -Path $dgst -Pattern '^SHA2-256=\s*([0-9a-fA-F]{64})' |
            Select-Object -First 1 | ForEach-Object { $_.Matches[0].Groups[1].Value.ToLowerInvariant() }
        if (-not $expected) { throw "SHA2-256 not found in $xrayZipUrl.dgst" }
        $actual = Get-Sha256 $zip
        if ($actual -ne $expected) { throw "Xray zip checksum mismatch: expected $expected, got $actual" }
        Write-Host "  sha256 OK"

        $unz = Join-Path $tmp 'xray'
        Expand-Archive -Path $zip -DestinationPath $unz -Force
        foreach ($name in 'xray.exe', 'wintun.dll', 'LICENSE') {
            $src = Join-Path $unz $name
            if (Test-Path $src) {
                Copy-Item $src (Join-Path $coreDir $name) -Force
            } elseif ($name -ne 'LICENSE') {
                throw "$name not found in the Xray $version archive"
            }
        }
        Set-Content -Path $versionStamp -Value $version -Encoding ascii
    } else {
        Write-Host "Xray $version already present (use -Force to re-download)"
    }

    # ---------------- Geo files ----------------
    foreach ($name in 'geoip.dat', 'geosite.dat') {
        $target = Join-Path $geoDir $name
        if (-not $Force -and (Test-Path $target)) {
            Write-Host "$name already present (use -Force to re-download)"
            continue
        }
        Write-Host $name
        $file = Join-Path $tmp $name
        $sum = Join-Path $tmp "$name.sha256sum"
        Invoke-Download "$geoBaseUrl/$name" $file
        Invoke-Download "$geoBaseUrl/$name.sha256sum" $sum

        $expected = ((Get-Content $sum -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
        $actual = Get-Sha256 $file
        if ($actual -ne $expected) { throw "$name checksum mismatch: expected $expected, got $actual" }
        Write-Host "  sha256 OK"
        Copy-Item $file $target -Force
    }

    Write-Host ""
    Write-Host "Done:"
    Get-ChildItem $coreDir, $geoDir | Select-Object @{n='File';e={$_.FullName.Substring($repoRoot.Length + 1)}}, Length | Format-Table -AutoSize
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
