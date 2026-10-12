#requires -Version 7.0
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string[]] $KeepRun,
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/release'))
if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) { throw 'Release directory missing.' }
foreach ($kept in $KeepRun) {
    if (-not (Test-Path -LiteralPath (Join-Path $releaseRoot $kept) -PathType Container)) { throw "Kept run missing: $kept" }
}
# Refuse junctions/symlinks before resolving destructive targets. Never follow a redirected artifact root.
foreach ($ancestor in @($projectRoot, (Join-Path $projectRoot 'artifacts'), $releaseRoot)) {
    if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse root rejected.' }
}
$targets = [Collections.Generic.List[object]]::new()
foreach ($run in Get-ChildItem -LiteralPath $releaseRoot -Directory) {
    if ($run.Name -in $KeepRun -or $run.Name -notmatch '^[a-f0-9]{32}$') { continue }
    if ($run.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse run rejected.' }
    $hashPath = Join-Path $run.FullName 'artifact-hash.json'
    $inventory = Join-Path $run.FullName 'payload-inventory.json'
    # Incomplete runs need separate review, never inferred safe from age alone.
    if (-not (Test-Path -LiteralPath $hashPath -PathType Leaf) -or -not (Test-Path -LiteralPath $inventory -PathType Leaf)) { continue }
    $record = Get-Content -LiteralPath $hashPath -Raw | ConvertFrom-Json
    if ($record.file -notmatch '^GameLingoAI_\d+\.\d+\.\d+\.\d+_x64_unsigned\.msix$') { throw 'Unexpected archive name.' }
    $package = Join-Path $run.FullName $record.file
    if ((Get-Item -LiteralPath $package -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse archive rejected.' }
    if ((Get-Item -LiteralPath $package).Length -ne $record.bytes -or
        (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $record.sha256) { throw 'Retained package integrity check failed.' }
    foreach ($child in @('build', 'payload')) {
        $target = [IO.Path]::GetFullPath((Join-Path $run.FullName $child))
        if (-not $target.StartsWith($releaseRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetDirectoryName($target) -ne $run.FullName) { throw 'Target escaped release run.' }
        if (-not (Test-Path -LiteralPath $target -PathType Container)) { continue }
        $entries = @((Get-Item -LiteralPath $target -Force)) + @(Get-ChildItem -LiteralPath $target -Recurse -Force)
        if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Reparse content rejected.' }
        $bytes = ($entries | Where-Object { -not $_.PSIsContainer } | Measure-Object Length -Sum).Sum
        $targets.Add([pscustomobject]@{ path = $target; bytes = [long]$bytes; retainedPackage = $package })
    }
}
$total = 0L
foreach ($item in $targets) { $total += $item.bytes }
Write-Host ("{0}: {1} intermediate directories, {2:N2} GiB. Packages, manifests, kept runs and user data are preserved." -f $(if ($Apply) { 'Apply' } else { 'Dry run' }), $targets.Count, ($total / 1GB))
$targets | Select-Object path, bytes
if (-not $Apply -or $targets.Count -eq 0) { return }
$reportRoot = Join-Path $projectRoot 'artifacts/cleanup'
$null = New-Item -ItemType Directory -Path $reportRoot -Force
$reportPath = Join-Path $reportRoot ('release-intermediates-' + [guid]::NewGuid().ToString('N') + '.json')
$report = [ordered]@{ utc = [DateTimeOffset]::UtcNow; keptRuns = $KeepRun; targets = $targets.ToArray(); removed = @(); status = 'started' }
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
try {
    foreach ($item in $targets) {
        Remove-Item -LiteralPath $item.path -Recurse -Force
        $report.removed += $item.path
        $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
    }
    $report.status = 'completed'
} finally {
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
}
Write-Host "Cleanup evidence: $reportPath"
