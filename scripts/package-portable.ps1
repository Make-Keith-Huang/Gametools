#requires -Version 7.0
param([Parameter(Mandatory)][string] $RunDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$run = (Get-Item -LiteralPath $RunDirectory).FullName
& (Join-Path $PSScriptRoot 'verify-package.ps1') -RunDirectory $run
$artifact = Get-Content -LiteralPath (Join-Path $run 'artifact-hash.json') -Raw | ConvertFrom-Json
$source = Join-Path $run $artifact.file
$version = $artifact.file -replace '^GameLingoAI_', '' -replace '_x64_unsigned\.msix$', ''
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Unexpected package version.' }
if ([version]$version -lt [version]'0.1.0.16') { throw 'The portable launcher requires data-root support from version 0.1.0.16 or later.' }
$destination = Join-Path $run ("GameLingoAI-$version-win-x64-preview.zip")
if (Test-Path -LiteralPath $destination) { throw 'Refuse to overwrite an existing portable artifact.' }
$expected = @{}
foreach ($item in (Get-Content -LiteralPath (Join-Path $run 'payload-inventory.json') -Raw | ConvertFrom-Json)) {
    $expected['app/' + $item.Path] = @{ bytes = $item.Bytes; sha256 = $item.Sha256 }
}
$extras = @{
    'Start-GameLingoAI.cmd' = (Join-Path $root 'packaging/Start-GameLingoAI.cmd')
    'README.txt' = (Join-Path $root 'packaging/PORTABLE-README.txt')
    'LICENSE.txt' = (Join-Path $root 'LICENSE')
    'settings.example.json' = (Join-Path $root 'src/GameLingoAI.App/appsettings.sample.json')
}
$inputZip = [IO.Compression.ZipFile]::OpenRead($source)
$outputZip = [IO.Compression.ZipFile]::Open($destination, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $inputZip.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name -in @('[Content_Types].xml', 'AppxBlockMap.xml')) { continue }
        $copy = $outputZip.CreateEntry('app/' + $name, [IO.Compression.CompressionLevel]::Optimal)
        $from = $entry.Open()
        $to = $copy.Open()
        try { $from.CopyTo($to) } finally { $to.Dispose(); $from.Dispose() }
    }
    foreach ($name in $extras.Keys) {
        $path = $extras[$name]
        $expected[$name] = @{ bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path).Hash }
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($outputZip, $path, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $outputZip.Dispose(); $inputZip.Dispose() }
$check = [IO.Compression.ZipFile]::OpenRead($destination)
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
try {
    foreach ($entry in $check.Entries) {
        if (-not $seen.Add($entry.FullName) -or -not $expected.ContainsKey($entry.FullName)) { throw 'Unexpected/duplicate portable entry.' }
        $record = $expected[$entry.FullName]
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($entry.Length -ne $record.bytes -or $hash -ne $record.sha256) { throw 'Portable entry integrity mismatch.' }
    }
    if ($seen.Count -ne $expected.Count) { throw 'Portable entry missing.' }
}
finally { $check.Dispose() }
[ordered]@{ file = [IO.Path]::GetFileName($destination); bytes = (Get-Item -LiteralPath $destination).Length
    sha256 = (Get-FileHash -LiteralPath $destination).Hash; verifiedEntries = $seen.Count
    signature = 'Unsigned'; publicReleaseReady = $false; sourceMsixSha256 = $artifact.sha256 } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'portable-hash.json') -Encoding utf8NoBOM
Write-Output "Verified portable preview: $destination ($($seen.Count) entries). Public release review remains incomplete."
