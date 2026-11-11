# Builds the release archive for friends: publish/SimpleVpn-<version>-win-x64.zip with a SimpleVpn\ folder
# inside (self-contained app, Xray core, geo files, the "portable" marker and README.txt).
# Run tools/fetch-xray.ps1 first. -SkipPublish packs an existing publish/SimpleVpn (used by CI).
param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
$out = Join-Path $root 'publish\SimpleVpn'
$zip = Join-Path $root "publish\SimpleVpn-$version-win-x64.zip"

if (-not $SkipPublish) {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish (Join-Path $root 'src\SimpleVpn.Desktop') -c Release -r win-x64 --self-contained -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}

foreach ($required in 'SimpleVpn.exe', 'core\xray.exe', 'core\wintun.dll', 'geo\geoip.dat', 'geo\geosite.dat') {
    if (-not (Test-Path (Join-Path $out $required))) { throw "$required is missing in $out (run tools/fetch-xray.ps1 before publishing)" }
}

# Never ship data from local test runs; mark the copy as portable; add the guide for friends.
Remove-Item (Join-Path $out 'data') -Recurse -Force -ErrorAction SilentlyContinue
Set-Content -Path (Join-Path $out 'portable') -Value '' -NoNewline
Copy-Item (Join-Path $root 'docs\README-friends.txt') (Join-Path $out 'README.txt') -Force

if (Test-Path $zip) { Remove-Item $zip -Force }
# Entries are added one by one: ZipFile.CreateFromDirectory on Windows PowerShell 5.1 writes '\' separators,
# which other unzip tools turn into file names with backslashes.
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $out -Recurse -File) {
        $name = 'SimpleVpn/' + $file.FullName.Substring($out.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose() }
"{0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
