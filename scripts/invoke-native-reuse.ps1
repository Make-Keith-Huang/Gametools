#requires -Version 7.0
param(
    [ValidateSet('reuse', 'reuse-cropped', 'reuse-integrated', 'reuse-lifecycle', 'reuse-lifecycle-cached', 'reuse-sustained', 'reuse-cancellation', 'reuse-multiline', 'reuse-multiline-batched', 'reuse-production')][string] $Mode = 'reuse',
    [ValidateRange(120, 3600)][int] $SoakSeconds = 120
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dataRoot = 'D:\ai工作数据和目录存放点\GameLingoAI'
$artifacts = Join-Path $dataRoot 'artifacts'
$settings = @{
    TEMP = (Join-Path $dataRoot 'temp')
    TMP = (Join-Path $dataRoot 'temp')
    DOTNET_CLI_HOME = (Join-Path $dataRoot 'dotnet-cli')
    NUGET_PACKAGES = (Join-Path $dataRoot 'nuget/packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $dataRoot 'nuget/http-cache')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $dataRoot 'nuget/plugins-cache')
    GAMELINGOAI_PROBE_REPORT_ROOT = (Join-Path $dataRoot 'reports')
    GAMELINGOAI_PROBE_SOAK_SECONDS = $SoakSeconds.ToString()
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_NOLOGO = '1'
    MSBUILDDISABLENODEREUSE = '1'
}
$previous = @{}
foreach ($key in $settings.Keys) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    if ($settings[$key].StartsWith('D:\', [StringComparison]::OrdinalIgnoreCase)) {
        $null = New-Item -ItemType Directory -Path $settings[$key] -Force
    }
    [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
}
Push-Location $projectRoot
try {
    $project = 'tests/GameLingoAI.NativeCompatibilityProbe/GameLingoAI.NativeCompatibilityProbe.csproj'
    # Existing shared packages are read-only fallback inputs; new cache writes are confined to D:.
    $restoreOptions = @()
    $sharedPackages = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
    if (Test-Path -LiteralPath $sharedPackages) { $restoreOptions += "-p:RestoreFallbackFolders=$sharedPackages" }
    & dotnet restore $project "-p:ArtifactsPath=$artifacts" @restoreOptions --ignore-failed-sources --disable-parallel
    if ($LASTEXITCODE -ne 0) { throw 'Probe restore failed.' }
    & dotnet build $project -c Release --no-restore "-p:ArtifactsPath=$artifacts" -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    & dotnet format whitespace tests/GameLingoAI.NativeCompatibilityProbe --folder --verify-no-changes
    if ($LASTEXITCODE -ne 0) { throw 'Probe whitespace verification failed.' }
    $executables = @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'bin/GameLingoAI.NativeCompatibilityProbe') -Filter 'GameLingoAI.NativeCompatibilityProbe.exe' -File -Recurse)
    if ($executables.Count -ne 1) { throw 'Expected one isolated executable.' }
    & $executables[0].FullName $Mode (Join-Path $projectRoot 'artifacts/native-compatibility/models')
    if ($LASTEXITCODE -ne 0) { throw "Probe failed with exit code $LASTEXITCODE." }
} finally {
    Pop-Location
    foreach ($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
    Get-PSDrive C | Select-Object Name,@{Name='FreeGiB';Expression={[math]::Round($_.Free/1GB,3)}}
}
