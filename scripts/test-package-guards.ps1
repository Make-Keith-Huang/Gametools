#requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$artifactRoot = if ($env:GAMELINGOAI_ARTIFACTS_PATH) { [IO.Path]::GetFullPath($env:GAMELINGOAI_ARTIFACTS_PATH) } else { Join-Path $root 'artifacts' }
$fixtureParent = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'package-guard-tests'))
$testRoot = Join-Path $fixtureParent ([guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
[xml]$profile = Get-Content -LiteralPath (Join-Path $root 'packaging/Release.pubxml') -Raw
$version = $profile.Project.PropertyGroup.GameLingoReleaseRuntimeVersion
function New-Fixture([string] $Name, [string] $Omit = '') {
    $path = Join-Path $testRoot $Name
    $null = New-Item -ItemType Directory -Path $path
    foreach ($file in @('GameLingoAI.App.exe','GameLingoAI.App.dll','coreclr.dll','hostfxr.dll','PresentationFramework.dll',
        'OpenCvSharpExtern.dll','paddle_inference_c.dll','e_sqlite3.dll','AppxManifest.xml','sbom.cdx.json',
        'THIRD-PARTY-NOTICES.txt','release-status.json','model-manifest.json','native-provenance.json')) {
        if ($file -ne $Omit) { [IO.File]::WriteAllText((Join-Path $path $file), 'synthetic fixture, not executable') }
    }
    $frameworks = @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App','Microsoft.AspNetCore.App')
    @{ runtimeOptions = @{ includedFrameworks = @($frameworks | ForEach-Object { @{ name=$_; version=$version } }) } } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $path 'GameLingoAI.App.runtimeconfig.json')
    $libraries = @{}
    foreach ($framework in $frameworks) { $libraries["runtimepack.$framework.Runtime.win-x64/$version"] = @{} }
    @{ libraries=$libraries } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $path 'GameLingoAI.App.deps.json')
    return $path
}
function Assert-Rejected([scriptblock] $Action) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Expected the package guard to reject this fixture.' }
}
$valid = New-Fixture 'valid'
$inventory = @(& (Join-Path $PSScriptRoot 'scan-package.ps1') -Payload $valid)
if ($inventory.Count -ne 16) { throw 'Valid fixture inventory count differs.' }
foreach ($forbidden in @('secrets.json','private.pfx','gamelingo.db','testhost.dll','opencv_videoio_ffmpeg.dll','extra.exe')) {
    $fixture = New-Fixture ($forbidden.Replace('.', '-'))
    [IO.File]::WriteAllText((Join-Path $fixture $forbidden), 'fixture')
    Assert-Rejected { & (Join-Path $PSScriptRoot 'scan-package.ps1') -Payload $fixture }
}
$missing = New-Fixture 'missing' 'coreclr.dll'
Assert-Rejected { & (Join-Path $PSScriptRoot 'scan-package.ps1') -Payload $missing }
$drift = New-Fixture 'runtime-drift'
[IO.File]::WriteAllText((Join-Path $drift 'GameLingoAI.App.deps.json'), '{"libraries":{}}')
Assert-Rejected { & (Join-Path $PSScriptRoot 'scan-package.ps1') -Payload $drift }

# Fake ZIP contents test integrity guards only, not MakeAppx's manifest/schema validation.
$archiveRoot = Join-Path $testRoot 'archive'
$null = New-Item -ItemType Directory -Path $archiveRoot
$package = Join-Path $archiveRoot 'fixture.msix'
$zip = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $valid -File)) {
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $file.Name)
    }
    $null = $zip.CreateEntry('AppxBlockMap.xml')
    $null = $zip.CreateEntry('[Content_Types].xml')
} finally { $zip.Dispose() }
$inventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $archiveRoot 'payload-inventory.json')
@{ file='fixture.msix'; bytes=(Get-Item -LiteralPath $package).Length; sha256=(Get-FileHash -LiteralPath $package).Hash } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $archiveRoot 'artifact-hash.json')
& (Join-Path $PSScriptRoot 'verify-package.ps1') -RunDirectory $archiveRoot
$inventory[0].Sha256 = '0' * 64
$inventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $archiveRoot 'payload-inventory.json')
Assert-Rejected { & (Join-Path $PSScriptRoot 'verify-package.ps1') -RunDirectory $archiveRoot }
Write-Host 'Package guard tests passed: valid inventory/archive, six forbidden paths, missing runtime, version drift, tampered payload hash.'

# Fake assets prove file-to-package matching, not provenance of an upstream compiled library.
$nativeRoot = Join-Path $testRoot 'native'
$sourceRoot = Join-Path $nativeRoot 'packages/fixture/1.0/runtimes/win-x64/native'
$nativePayload = Join-Path $nativeRoot 'payload'
$null = New-Item -ItemType Directory -Path $sourceRoot, $nativePayload
[IO.File]::WriteAllText((Join-Path $sourceRoot 'fixture.dll'), 'not an executable')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'fixture.dll') -Destination $nativePayload
$nativeAssets = Join-Path $nativeRoot 'assets.json'
$assetGraph = @{
    targets = @{ 'net8.0/win-x64' = @{ 'fixture/1.0' = @{ native = @{ 'runtimes/win-x64/native/fixture.dll' = @{} } } } }
    libraries = @{ 'fixture/1.0' = @{ type='package'; path='fixture/1.0'; sha512='synthetic-fixture' } }
    packageFolders = @{ (Join-Path $nativeRoot 'packages') = @{} }
}
$assetGraph | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $nativeAssets
$provenance = @(& (Join-Path $PSScriptRoot 'native-provenance.ps1') -AssetsFile $nativeAssets -Payload $nativePayload)
if ($provenance.Count -ne 1 -or $provenance[0].package -ne 'fixture/1.0' -or
    $provenance[0].sha256 -ne (Get-FileHash -LiteralPath (Join-Path $sourceRoot 'fixture.dll')).Hash -or
    $provenance[0].upstreamSourceVerified -or $provenance[0].bundledLicenseReviewComplete) {
    throw 'Native provenance fixture was inaccurately attributed.'
}
[IO.File]::WriteAllText((Join-Path $nativePayload 'fixture.dll'), 'changed fixture')
Assert-Rejected { & (Join-Path $PSScriptRoot 'native-provenance.ps1') -AssetsFile $nativeAssets -Payload $nativePayload }
$absentPayload = Join-Path $nativeRoot 'absent'
$null = New-Item -ItemType Directory -Path $absentPayload
Assert-Rejected { & (Join-Path $PSScriptRoot 'native-provenance.ps1') -AssetsFile $nativeAssets -Payload $absentPayload }
Write-Host 'Native provenance tests passed: exact source/hash, honest review flags, changed and missing asset rejection.'

$noticeRoot = Join-Path $testRoot 'notices'
$null = New-Item -ItemType Directory -Path $noticeRoot
$noticePath = Join-Path $noticeRoot 'fixture-license.txt'
[IO.File]::WriteAllText($noticePath, 'synthetic notice')
$noticeRecord = @{
    schemaVersion=1
    nativeFiles=@{ 'fixture.dll'=(Get-FileHash -LiteralPath (Join-Path $nativePayload 'fixture.dll')).Hash }
    notices=@(@{ file='fixture-license.txt'; bytes=(Get-Item -LiteralPath $noticePath).Length; sha256=(Get-FileHash -LiteralPath $noticePath).Hash })
}
$noticeRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $noticeRoot 'provenance.json')
& (Join-Path $PSScriptRoot 'copy-native-notices.ps1') -Payload $nativePayload -NoticeRoot $noticeRoot
if ((Get-FileHash -LiteralPath (Join-Path $nativePayload 'licenses/fixture-license.txt')).Hash -ne $noticeRecord.notices[0].sha256) {
    throw 'Copied notice differs.'
}
Assert-Rejected { & (Join-Path $PSScriptRoot 'copy-native-notices.ps1') -Payload $nativePayload -NoticeRoot $noticeRoot }
[IO.File]::WriteAllText($noticePath, 'tampered notice')
Assert-Rejected { & (Join-Path $PSScriptRoot 'copy-native-notices.ps1') -Payload $nativePayload -NoticeRoot $noticeRoot }
[IO.File]::WriteAllText($noticePath, 'synthetic notice')
[IO.File]::WriteAllText((Join-Path $nativePayload 'fixture.dll'), 'new native version')
Assert-Rejected { & (Join-Path $PSScriptRoot 'copy-native-notices.ps1') -Payload $nativePayload -NoticeRoot $noticeRoot }
Write-Host 'Native notice tests passed: exact copy, no overwrite, changed notice and native version rejection.'

# Only this successful run's synthetic fixtures are disposable; failed runs remain evidence.
$resolved = (Get-Item -LiteralPath $testRoot).FullName
if ([IO.Path]::GetDirectoryName($resolved) -ne $fixtureParent -or
    [IO.Path]::GetFileName($resolved) -notmatch '^[a-f0-9]{32}$' -or
    ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    @(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
    throw 'Refusing fixture cleanup outside the exact generated non-link directory.'
}
Remove-Item -LiteralPath $resolved -Recurse -Force
Write-Host 'Removed this successful run''s synthetic package fixtures.'
