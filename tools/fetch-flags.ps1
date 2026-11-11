# Downloads country flag pictures (40 px wide PNG, public domain) from flagcdn.com into
# src/SimpleXray.Desktop/Assets/flags. The files are committed; rerun only to refresh the set.
# Windows fonts cannot draw flag emoji, so the UI shows these pictures next to server names.
param([string]$Proxy)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$url = 'https://flagcdn.com/w40.zip'
$out = Join-Path $PSScriptRoot '..\src\SimpleXray.Desktop\Assets\flags'
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("sxray-flags-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'w40.zip'
    $request = @{ Uri = $url; OutFile = $zip; UseBasicParsing = $true }
    if ($Proxy) { $request.Proxy = $Proxy }
    Invoke-WebRequest @request

    New-Item -ItemType Directory -Force $out | Out-Null
    Get-ChildItem $out -Filter '*.png' | Remove-Item
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    $count = 0
    try {
        foreach ($entry in $archive.Entries) {
            # Countries only (two letters); skip regions like gb-eng and anything with a path.
            if ($entry.FullName -notmatch '^[a-z]{2}\.png$' -or $entry.Length -gt 65536) { continue }
            $target = Join-Path $out $entry.FullName
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            $head = [System.IO.File]::ReadAllBytes($target)[0..3]
            if ($head[0] -ne 0x89 -or $head[1] -ne 0x50 -or $head[2] -ne 0x4E -or $head[3] -ne 0x47) {
                Remove-Item $target
                throw "Not a PNG: $($entry.FullName)"
            }
            $count++
        }
    }
    finally { $archive.Dispose() }
    "$count flags written to $out"
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
