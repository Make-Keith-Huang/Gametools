#requires -Version 7.0
param([Parameter(Mandatory)][string] $RunDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Get-Item -LiteralPath $RunDirectory).FullName
$artifact = Get-Content -LiteralPath (Join-Path $root 'artifact-hash.json') -Raw | ConvertFrom-Json
if ([IO.Path]::GetFileName($artifact.file) -ne $artifact.file -or [IO.Path]::GetExtension($artifact.file) -ne '.msix') {
    throw 'Artifact name must be a plain MSIX filename.'
}
$package = Join-Path $root $artifact.file
if ((Get-Item -LiteralPath $package).Length -ne $artifact.bytes -or
    (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $artifact.sha256) { throw 'MSIX size/hash mismatch.' }
$expected = @{}
foreach ($item in (Get-Content -LiteralPath (Join-Path $root 'payload-inventory.json') -Raw | ConvertFrom-Json)) {
    if ($expected.ContainsKey($item.Path)) { throw 'Duplicate payload inventory path.' }
    $expected[$item.Path] = $item
}
if ($expected.Count -eq 0) { throw 'Empty payload inventory cannot pass.' }
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if (-not $seen.Add($name)) { throw 'Duplicate archive path.' }
        if ($name -in @('[Content_Types].xml', 'AppxBlockMap.xml')) { continue }
        if (-not $expected.ContainsKey($name)) { throw "Unexpected archive entry: $name" }
        $record = $expected[$name]
        if ($entry.Length -ne $record.Bytes) { throw "Payload size mismatch: $name" }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -ne $record.Sha256) { throw "Payload hash mismatch: $name" }
    }
    foreach ($name in $expected.Keys) {
        if (-not $seen.Contains($name)) { throw "Payload missing from archive: $name" }
    }
    if (-not $seen.Contains('AppxBlockMap.xml') -or -not $seen.Contains('[Content_Types].xml')) { throw 'MSIX metadata missing.' }
}
finally { $archive.Dispose() }
Write-Host "Verified unsigned MSIX SHA-256 and $($expected.Count) payload hashes. This does not verify installation or trust."
