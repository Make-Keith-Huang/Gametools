#requires -Version 7.0
param(
    [Parameter(Mandatory)][string] $AssetsFile,
    [Parameter(Mandatory)][string] $Payload
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
$targets = @($assets.targets.Keys | Where-Object { $_.EndsWith('/win-x64', [StringComparison]::Ordinal) })
if ($targets.Count -ne 1) { throw 'Expected one Windows x64 dependency target.' }
$payloadRoot = (Get-Item -LiteralPath $Payload).FullName
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($library in ($assets.targets[$targets[0]].GetEnumerator() | Sort-Object Key)) {
    if (-not $library.Value.ContainsKey('native')) { continue }
    $metadata = $assets.libraries[$library.Key]
    if ($metadata.type -ne 'package') { throw 'Native dependency must identify a restored package.' }
    $packageRoot = $null
    foreach ($folder in $assets.packageFolders.Keys) {
        $candidate = Join-Path $folder $metadata.path
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packageRoot = $candidate; break }
    }
    if ($null -eq $packageRoot) { throw 'Native package source is unavailable.' }
    foreach ($relative in ($library.Value.native.Keys | Sort-Object)) {
        $name = [IO.Path]::GetFileName($relative)
        # The publish profile explicitly removes optional FFmpeg transport.
        if ($name -eq 'opencv_videoio_ffmpeg4110_64.dll') {
            if (Test-Path -LiteralPath (Join-Path $payloadRoot $name)) { throw 'Excluded FFmpeg transport is present.' }
            continue
        }
        if (-not $relative.StartsWith('runtimes/win-x64/native/', [StringComparison]::Ordinal) -or
            $relative.Contains('..') -or -not $name.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Unreviewed native asset layout.'
        }
        if (-not $seen.Add($name)) { throw 'Multiple native packages claim the same payload name.' }
        $source = Get-Item -LiteralPath (Join-Path $packageRoot $relative)
        $shipped = Get-Item -LiteralPath (Join-Path $payloadRoot $name)
        if ($source.PSIsContainer -or $shipped.PSIsContainer -or
            ($source.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            ($shipped.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Native assets must be regular files.' }
        $hash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
        if ($hash -ne (Get-FileHash -LiteralPath $shipped.FullName -Algorithm SHA256).Hash) {
            throw "Published native asset differs from restored package: $name"
        }
        $parts = $library.Key.Split('/')
        [pscustomobject][ordered]@{
            file = $name
            sha256 = $hash
            bytes = $shipped.Length
            package = $library.Key
            packagePurl = "pkg:nuget/$($parts[0])@$($parts[1])"
            packageContentSha512 = $metadata.sha512
            packageAsset = $relative
            observedFileVersion = $source.VersionInfo.FileVersion
            observedProductVersion = $source.VersionInfo.ProductVersion
            observedCopyright = $source.VersionInfo.LegalCopyright
            upstreamSourceVerified = $false
            bundledLicenseReviewComplete = $false
        }
    }
}
if ($seen.Count -eq 0) { throw 'No native assets were traced.' }
