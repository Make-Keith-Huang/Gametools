param(
    [switch] $IncludePlatform,
    [string] $DataRoot = 'D:\ai工作数据和目录存放点\GameLingoAI'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Invoke-DotNetStep {
    param(
        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    Write-Host "==> $Name"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if (-not (Test-Path -LiteralPath ([IO.Path]::GetPathRoot($DataRoot)))) { throw 'Verification data drive is unavailable.' }
$artifacts = Join-Path $DataRoot 'artifacts'
$reports = Join-Path $DataRoot 'reports'
$settings = @{
    TEMP = (Join-Path $DataRoot 'temp')
    TMP = (Join-Path $DataRoot 'temp')
    DOTNET_CLI_HOME = (Join-Path $DataRoot 'dotnet-cli')
    NUGET_PACKAGES = (Join-Path $DataRoot 'nuget/packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $DataRoot 'nuget/http-cache')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $DataRoot 'nuget/plugins-cache')
    GAMELINGOAI_ARTIFACTS_PATH = $artifacts
    MSBUILDDISABLENODEREUSE = '1'
    DOTNET_NOLOGO = '1'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
}
$previous = @{}
foreach ($key in $settings.Keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process') }
function Invoke-QualityStep {
    param([string] $Name, [string[]] $ModeArguments, [string] $ReportName)
    # The harness deliberately uses CreateNew; publish only after a successful run.
    # A failed attempt retains its uniquely named report instead of replacing good evidence.
    $pending = Join-Path $reports ('quality/pending-' + [guid]::NewGuid().ToString('N') + '.json')
    Invoke-DotNetStep $Name (@('run', '--project', 'tools/GameLingoAI.QualityHarness', '-c', 'Release', '--no-build', '--') + $ModeArguments + @($pending))
    Move-Item -LiteralPath $pending -Destination (Join-Path $reports ('quality/' + $ReportName)) -Force
}
Push-Location $root
try {
    foreach ($path in @($artifacts, $reports, (Join-Path $reports 'quality'), $settings.TEMP,
        $settings.DOTNET_CLI_HOME, $settings.NUGET_PACKAGES, $settings.NUGET_HTTP_CACHE_PATH, $settings.NUGET_PLUGINS_CACHE_PATH)) {
        $null = New-Item -ItemType Directory -Path $path -Force
    }
    foreach ($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process') }

    $restoreArguments = @('restore', 'GameLingoAI.sln', '--disable-parallel', '-m:1')
    # Existing shared packages may be read, but new package/cache writes use DataRoot.
    $sharedPackages = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
    if (Test-Path -LiteralPath $sharedPackages) { $restoreArguments += "-p:RestoreFallbackFolders=$sharedPackages" }
    Invoke-DotNetStep 'Restore' $restoreArguments
    Invoke-DotNetStep 'Release build' @('build', 'GameLingoAI.sln', '-c', 'Release', '--no-restore', '-m:1')
    Invoke-DotNetStep 'Tests' @(
        'test',
        'GameLingoAI.sln',
        '-c', 'Release',
        '--no-build',
        '--settings', 'tests/default.runsettings',
        '--collect', 'XPlat Code Coverage',
        '--results-directory', (Join-Path $reports 'default-tests'),
        '--logger', 'trx;LogFileName=tests.trx',
        '-m:1'
    )
    Invoke-DotNetStep 'Formatting verification' @('format', 'GameLingoAI.sln', '--verify-no-changes', '--no-restore')
    Write-Host '==> Package guard fixtures (PowerShell 7; no installation)'
    & pwsh -NoProfile -File scripts/test-package-guards.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Package guard fixtures failed.' }
    Invoke-QualityStep 'Synthetic benchmark' @('benchmark', '40') 'benchmark-current.json'
    Invoke-QualityStep 'Short wall-clock soak (not long-duration acceptance)' @('soak', 'ci') 'soak-ci-current.json'
    if ($IncludePlatform) {
        Invoke-DotNetStep 'Interactive Windows platform tests' @(
            'test', 'GameLingoAI.sln', '-c', 'Release', '--no-build',
            '--settings', 'tests/platform.runsettings',
            '--results-directory', (Join-Path $reports 'platform-tests'),
            '--logger', 'trx;LogFileName=platform.trx', '-m:1'
        )
    }
}
finally {
    Pop-Location
    foreach ($key in $settings.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
}
