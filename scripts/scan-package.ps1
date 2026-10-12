#requires -Version 7.0
param([Parameter(Mandatory)][string] $Payload)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$directory = Get-Item -LiteralPath $Payload
if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Select a real payload directory.' }
$root = $directory.FullName
$required = @('GameLingoAI.App.exe', 'GameLingoAI.App.dll', 'GameLingoAI.App.runtimeconfig.json', 'GameLingoAI.App.deps.json', 'coreclr.dll',
    'hostfxr.dll', 'PresentationFramework.dll', 'OpenCvSharpExtern.dll', 'paddle_inference_c.dll', 'e_sqlite3.dll',
    'AppxManifest.xml', 'sbom.cdx.json', 'THIRD-PARTY-NOTICES.txt', 'release-status.json', 'model-manifest.json', 'native-provenance.json')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $name) -PathType Leaf)) { throw "Required package file missing: $name" }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $root 'GameLingoAI.App.runtimeconfig.json') -Raw | ConvertFrom-Json -AsHashtable
$deps = Get-Content -LiteralPath (Join-Path $root 'GameLingoAI.App.deps.json') -Raw | ConvertFrom-Json -AsHashtable
[xml]$profile = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'packaging/Release.pubxml') -Raw
$expectedVersion = $profile.Project.PropertyGroup.GameLingoReleaseRuntimeVersion
$expectedFrameworks = @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App', 'Microsoft.AspNetCore.App')
$frameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
if ($frameworks.Count -ne 3 -or @($frameworks.name | Select-Object -Unique).Count -ne 3) { throw 'Expected three distinct self-contained frameworks.' }
foreach ($framework in $frameworks) {
    if ($framework.name -notin $expectedFrameworks -or $framework.version -ne $expectedVersion) { throw 'Unreviewed runtime version or framework.' }
    $pack = "runtimepack.$($framework.name).Runtime.win-x64/$($framework.version)"
    if (-not $deps.libraries.ContainsKey($pack)) { throw "Runtime declaration and resolved package disagree: $($framework.name)" }
}
$items = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
foreach ($item in $items) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package reparse points are forbidden.' }
    $relative = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/')
    if ($relative -match '(^|/)(\.git|\.dotnet-cli|obj|logs|models|\.downloads|secrets)(/|$)' -or
        $relative -match '(?i)(\.pfx|\.p12|\.pem|\.key|\.db(-wal|-shm)?|\.sqlite|\.nupkg|\.pdb|\.partial|\.tar)$' -or
        $relative -match '(?i)(secrets\.json|appsettings\.Local\.json|testhost|xunit|coverlet|GameLingoAI\.(Tests|QualityHarness)|opencv_videoio_ffmpeg|whisper|NAudio|EasyHook|Detours)') {
        throw "Forbidden/private or development-only package path: $relative"
    }
    if ($item.PSIsContainer) { continue }
    if ($item.Extension -notin @('.exe', '.dll', '.json', '.xml', '.config', '.txt', '.png', '.dat', '.pri')) {
        throw "Unreviewed package file type: $relative"
    }
    if ($item.Extension -eq '.exe' -and $item.Name -ne 'GameLingoAI.App.exe') { throw "Unexpected executable: $relative" }
    [pscustomobject]@{ Path = $relative; Bytes = $item.Length; Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
}
