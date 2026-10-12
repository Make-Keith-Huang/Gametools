#requires -Version 7.0
param(
    [Parameter(Mandatory)][string] $Payload,
    [string] $NoticeRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging/licenses')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$records = @(Get-ChildItem -LiteralPath $NoticeRoot -Filter provenance.json -Recurse -File)
if ($records.Count -eq 0) { throw 'No reviewed native notice records.' }
$copies = @()
$destinations = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($record in $records) {
    $data = Get-Content -LiteralPath $record.FullName -Raw | ConvertFrom-Json -AsHashtable
    if ($data.schemaVersion -ne 1 -or $data.notices.Count -eq 0 -or $data.nativeFiles.Count -eq 0) { throw 'Invalid native notice record.' }
    foreach ($native in $data.nativeFiles.GetEnumerator()) {
        if ($native.Key -notmatch '^[A-Za-z0-9_.-]+\.dll$') { throw 'Native notice target must be a DLL basename.' }
        $path = Join-Path $Payload $native.Key
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $native.Value) { throw 'Native version changed; review notices before packaging.' }
    }
    foreach ($notice in $data.notices) {
        if ($notice.file -notmatch '^[A-Za-z0-9_.-]+\.txt$' -or -not $destinations.Add($notice.file)) { throw 'Invalid/duplicate native notice filename.' }
        $source = Get-Item -LiteralPath (Join-Path $record.DirectoryName $notice.file)
        if ($source.Length -ne $notice.bytes -or (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash -ne $notice.sha256) {
            throw 'Native notice text changed; verify upstream attribution before packaging.'
        }
        $destination = Join-Path $Payload ('licenses/' + $notice.file)
        if (Test-Path -LiteralPath $destination) { throw 'Refuse to overwrite an existing notice.' }
        $copies += @{ source=$source.FullName; destination=$destination }
    }
}
# Validate the whole set before copying; a failed package run is retained by its owner.
$null = New-Item -ItemType Directory -Path (Join-Path $Payload 'licenses') -Force
foreach ($copy in $copies) { Copy-Item -LiteralPath $copy.source -Destination $copy.destination }
Write-Host "Verified native binaries and copied $($copies.Count) upstream notice texts. Other attribution remains pending."
