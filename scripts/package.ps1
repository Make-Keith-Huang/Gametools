#requires -Version 7.0
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string] $Version = '0.1.0.0',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]{2,49}$')][string] $Identity = 'GameLingoAI.Development',
    [ValidatePattern('^CN=')][string] $Publisher = 'CN=GameLingoAI.Development',
    [string] $SdkDirectory = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64',
    [string] $DataRoot = 'D:\ai工作数据和目录存放点\GameLingoAI'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$parsedVersion = [version]$Version
if ($parsedVersion.Major -gt 65535 -or $parsedVersion.Minor -gt 65535 -or $parsedVersion.Build -gt 65535 -or $parsedVersion.Revision -gt 65535) {
    throw 'MSIX version components must be 0..65535.'
}
$makeAppx = Join-Path $SdkDirectory 'makeappx.exe'
if (-not (Test-Path -LiteralPath $makeAppx -PathType Leaf)) { throw 'Windows SDK MakeAppx is required. Supply -SdkDirectory.' }

$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if (-not (Test-Path -LiteralPath ([IO.Path]::GetPathRoot($DataRoot)))) { throw 'Packaging data drive is unavailable.' }
$settings = @{
    TEMP = (Join-Path $DataRoot 'temp'); TMP = (Join-Path $DataRoot 'temp')
    DOTNET_CLI_HOME = (Join-Path $DataRoot 'dotnet-cli')
    NUGET_PACKAGES = (Join-Path $DataRoot 'nuget/packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $DataRoot 'nuget/http-cache')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $DataRoot 'nuget/plugins-cache')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'; MSBUILDDISABLENODEREUSE = '1'
}
$previous = @{}
foreach ($key in $settings.Keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
try {
foreach ($path in @($settings.TEMP, $settings.DOTNET_CLI_HOME, $settings.NUGET_PACKAGES,
    $settings.NUGET_HTTP_CACHE_PATH, $settings.NUGET_PLUGINS_CACHE_PATH)) {
    $null = New-Item -ItemType Directory -Path $path -Force
}
foreach ($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process') }
# Every invocation owns a fresh directory. Failed runs remain inspectable; nothing is deleted or overwritten.
$runRoot = Join-Path $DataRoot ('artifacts/release/' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $runRoot 'payload'
$build = Join-Path $runRoot 'build'
$null = New-Item -ItemType Directory -Path $stage
$profile = Join-Path $root 'packaging/Release.pubxml'
$restoreOptions = @()
$sharedPackages = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
if (Test-Path -LiteralPath $sharedPackages) { $restoreOptions += "-p:RestoreFallbackFolders=$sharedPackages" }
& dotnet publish (Join-Path $root 'src/GameLingoAI.App/GameLingoAI.App.csproj') -c Release -r win-x64 --self-contained true `
    -o $stage "-p:PublishProfileFullPath=$profile" "-p:ArtifactsPath=$build" "-p:Version=$Version" -m:1 @restoreOptions
if ($LASTEXITCODE -ne 0) { throw 'Clean self-contained publish failed; no package was produced.' }

[xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'packaging/AppxManifest.xml') -Raw
$manifest.Package.Identity.Name = $Identity
$manifest.Package.Identity.Publisher = $Publisher
$manifest.Package.Identity.Version = $Version
$manifest.Save((Join-Path $stage 'AppxManifest.xml'))
Copy-Item -LiteralPath (Join-Path $root 'packaging/model-manifest.json') -Destination (Join-Path $stage 'model-manifest.json')

# Simple project-authored geometric tile assets, generated locally without downloaded art or fonts.
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $stage 'Assets'
$null = New-Item -ItemType Directory -Path $assets
foreach ($tile in @(@('StoreLogo', 50), @('Square44x44Logo', 44), @('Square150x150Logo', 150))) {
    $size = [int]$tile[1]
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $brush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    try {
        $graphics.Clear([System.Drawing.Color]::FromArgb(22, 50, 79))
        $graphics.FillRectangle($brush, [int]($size * .2), [int]($size * .3), [int]($size * .6), [int]($size * .12))
        $graphics.FillRectangle($brush, [int]($size * .2), [int]($size * .56), [int]($size * .42), [int]($size * .12))
        $bitmap.Save((Join-Path $assets ($tile[0] + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $brush.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}

# Metadata is extracted only from restored package manifests, never arbitrary files in the workspace/profile.
$assetsFile = Join-Path $build 'obj/GameLingoAI.App/project.assets.json'
$resolved = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json -AsHashtable
$components = [System.Collections.Generic.List[object]]::new()
$notices = [System.Collections.Generic.List[string]]::new()
$notices.Add("GameLingoAI $Version - development preview; third-party distribution review remains incomplete.")
$notices.Add('NuGet license metadata follows. Native bundled dependency notices still require release review; this inventory does not certify license compliance.')
$packages = @{}
foreach ($entry in $resolved.libraries.GetEnumerator()) {
    if ($entry.Value.type -eq 'package') { $packages[$entry.Key] = $entry.Value }
}
foreach ($framework in $resolved.project.frameworks.Values) {
    foreach ($dependency in $framework.downloadDependencies) {
        if ($dependency.name -notmatch '\.Runtime\.win-x64$') { continue }
        $runtimeVersion = $dependency.version.Trim('[', ']').Split(',')[0].Trim()
        $packages["$($dependency.name)/$runtimeVersion"] = @{ path = "$($dependency.name.ToLowerInvariant())/$runtimeVersion" }
    }
}
$licenseDirectory = Join-Path $stage 'licenses'
$null = New-Item -ItemType Directory -Path $licenseDirectory
foreach ($entry in ($packages.GetEnumerator() | Sort-Object Key)) {
    $parts = $entry.Key.Split('/')
    $packageRoot = $null
    foreach ($folder in $resolved.packageFolders.Keys) {
        $candidate = Join-Path $folder $entry.Value.path
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packageRoot = $candidate; break }
    }
    if ($null -eq $packageRoot) { throw 'A resolved dependency cannot be located for provenance.' }
    $nuspecPath = Join-Path $packageRoot ($parts[0].ToLowerInvariant() + '.nuspec')
    [xml]$nuspec = Get-Content -LiteralPath $nuspecPath -Raw
    $metadata = $nuspec.SelectSingleNode('//*[local-name()="metadata"]')
    $licenseNode = $metadata.SelectSingleNode('*[local-name()="license"]')
    $licenseUrlNode = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
    $license = if ($null -ne $licenseNode) { $licenseNode.InnerText } else { 'NOASSERTION' }
    $licenseUrl = if ($null -ne $licenseUrlNode) { $licenseUrlNode.InnerText } else { '' }
    $component = [ordered]@{ type = 'library'; name = $parts[0]; version = $parts[1]; purl = "pkg:nuget/$($parts[0])@$($parts[1])" }
    if ($null -ne $licenseNode -and $licenseNode.GetAttribute('type') -eq 'expression') {
        $component.licenses = @(@{ expression = $license })
    }
    $components.Add($component)
    $notices.Add("`n$($entry.Key)`nLicense: $license`nLicense URL: $licenseUrl")
    $copyright = $metadata.SelectSingleNode('*[local-name()="copyright"]')
    if ($null -ne $copyright) { $notices.Add($copyright.InnerText) }
    foreach ($notice in (Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD[-_]?PARTY[-_]?NOTICES)(\..*)?$' })) {
        $relativeNotice = [IO.Path]::GetRelativePath($packageRoot, $notice.FullName).Replace('\', '_').Replace('/', '_')
        Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenseDirectory ("$($parts[0])-$($parts[1])-$relativeNotice.txt"))
    }
}
$nativeFiles = @(& (Join-Path $PSScriptRoot 'native-provenance.ps1') -AssetsFile $assetsFile -Payload $stage)
& (Join-Path $PSScriptRoot 'copy-native-notices.ps1') -Payload $stage
$notices.Add('ONNX Runtime 1.11.1: two DLLs matched the official archive by SHA-256. See licenses/onnxruntime-1.11.1-LICENSE.txt and licenses/onnxruntime-1.11.1-ThirdPartyNotices.txt.txt. Other native attribution is not yet complete.')
$notices.Add('Intel MKLML/OpenMP 2019.0.5.20190502: both DLLs matched the Paddle-referenced upstream archive. Intel Simplified Software License (April 2018) and third-party statement are under licenses/mklml-*. NuGet wrapper Apache-2.0 metadata does not replace these terms.')
$notices.Add('Paddle2ONNX 1.0.0rc2: DLL matched official archive. Main, ONNX and optimizer licenses are under licenses/paddle2onnx-*. Static protobuf version/attribution remains unresolved; these notices are not a completed distribution audit.')
$notices.Add('oneDNN 3.6.2: mkldnn.dll reports commit 28d696724426b943e2f4a0e607f90cdc19aedaf6. Full LICENSE and THIRD-PARTY-PROGRAMS from that commit are under licenses/onednn-*. No reproducible-build attestation is claimed.')
$notices.Add('SQLite 3.53.3: library source ID matched official sqlite3.h; see licenses/sqlite-3.53.3-PUBLIC-DOMAIN.txt. SQLitePCLRaw 2.1.12 wrapper has separate LICENSE/NOTICE under licenses/sqlitepclraw-*.')
$notices.Add('This software is based in part on the work of the Independent JPEG Group.')
$notices.Add('OpenCV codec source notices: see licenses/opencv-libjpeg-turbo-*, opencv-libpng-*, opencv-libwebp-*, opencv-libtiff-*, opencv-zlib-* and opencv-xz-*. These fixed-version texts do not close the remaining native attribution review.')
@{ schemaVersion = 1; scope = 'NuGet native assets only; .NET runtime packs are inventoried separately'; files = $nativeFiles } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stage 'native-provenance.json') -Encoding utf8NoBOM
foreach ($native in $nativeFiles) {
    $components.Add([ordered]@{
        type = 'file'; name = $native.file; 'bom-ref' = "native:$($native.file)";
        hashes = @(@{ alg = 'SHA-256'; content = $native.sha256 });
        properties = @(
            @{ name = 'gamelingo:source-package'; value = $native.packagePurl },
            @{ name = 'gamelingo:source-asset'; value = $native.packageAsset },
            @{ name = 'gamelingo:upstream-source-verified'; value = 'false' },
            @{ name = 'gamelingo:bundled-license-review-complete'; value = 'false' }
        )
    })
}
$sbom = [ordered]@{ bomFormat = 'CycloneDX'; specVersion = '1.6'; version = 1; components = @($components.ToArray()) }
$sbom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $stage 'sbom.cdx.json') -Encoding utf8NoBOM
$notices | Set-Content -LiteralPath (Join-Path $stage 'THIRD-PARTY-NOTICES.txt') -Encoding utf8NoBOM
[ordered]@{ version = $Version; runtime = 'win-x64'; deployment = 'self-contained'; signingStatus = 'Unsigned';
    publicReleaseReady = $false; cleanWindows10Verified = $false; cleanWindows11Verified = $false;
    licenseReviewComplete = $false; modelPayloadIncluded = $false; uninstallDataPolicy = 'Preserve LocalAppData/GameLingoAI; explicit user removal only' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'release-status.json') -Encoding utf8NoBOM

$inventory = @(& (Join-Path $PSScriptRoot 'scan-package.ps1') -Payload $stage)
$inventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'payload-inventory.json') -Encoding utf8NoBOM
$package = Join-Path $runRoot ("GameLingoAI_$($Version)_x64_unsigned.msix")
& $makeAppx pack /d $stage /p $package /h SHA256
if ($LASTEXITCODE -ne 0) { throw 'MSIX validation/packing failed; inspect this run directory.' }
$hash = Get-FileHash -LiteralPath $package -Algorithm SHA256
[ordered]@{ file = [IO.Path]::GetFileName($package); sha256 = $hash.Hash; bytes = (Get-Item -LiteralPath $package).Length;
    signature = 'Unsigned'; distributable = $false } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'artifact-hash.json') -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot 'verify-package.ps1') -RunDirectory $runRoot
Write-Host "Unsigned development package and reports: $runRoot"
Write-Host 'NOT a release: signing, complete third-party notices and clean Windows 10/11 install/upgrade/uninstall gates remain.'
}
finally {
    foreach ($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
}
