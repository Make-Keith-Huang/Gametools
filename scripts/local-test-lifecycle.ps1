#requires -Version 5.1
param(
    [Parameter(Mandatory)][ValidateSet('Upgrade','Uninstall')][string] $Action,
    [Parameter(Mandatory)][string] $TestDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$testRoot = (Get-Item -LiteralPath $TestDirectory).FullName
$allowed = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/local-install-tests')).TrimEnd('\') + '\'
if (-not $testRoot.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Select an owned local test directory.' }
if (Get-Process GameLingoAI.App -ErrorAction SilentlyContinue) { throw 'Close GameLingoAI normally before lifecycle testing.' }
$package = @(Get-AppxPackage -Name 'GameLingoAI.Development')
if ($package.Count -ne 1 -or $package[0].Publisher -ne 'CN=GameLingoAI.Development') { throw 'Expected exactly the development test package.' }
$expectedVersion = if ($Action -eq 'Upgrade') { '0.1.0.0' } else { '0.1.0.1' }
if ($package[0].Version.ToString() -ne $expectedVersion) { throw 'Unexpected installed test version; no changes made.' }
$dataRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'GameLingoAI'
$before = @{}
if (Test-Path -LiteralPath $dataRoot) {
    if ((Get-Item -LiteralPath $dataRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refuse redirected data root.' }
    foreach ($item in (Get-ChildItem -LiteralPath $dataRoot -Recurse -Force)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refuse redirected data entries.' }
        if ($item.PSIsContainer) { continue }
        $before[$item.FullName] = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
    }
}
if ($Action -eq 'Upgrade') {
    $signed = Join-Path $testRoot 'GameLingoAI-upgrade-test-signed.msix'
    $journal = Get-Content -LiteralPath (Join-Path $testRoot 'certificate-journal.json') -Raw | ConvertFrom-Json
    $signature = Get-AuthenticodeSignature -LiteralPath $signed
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $journal.Thumbprint) { throw 'Upgrade signature is not the owned trusted certificate.' }
    Add-AppxPackage -Path $signed
    $after = @(Get-AppxPackage -Name 'GameLingoAI.Development')
    if ($after.Count -ne 1 -or $after[0].Version.ToString() -ne '0.1.0.1' -or $after[0].Status.ToString() -ne 'Ok') { throw 'Upgrade result mismatch.' }
}
else {
    Remove-AppxPackage -Package $package[0].PackageFullName
    if (Get-AppxPackage -Name 'GameLingoAI.Development') { throw 'Test package remains installed.' }
}
foreach ($path in $before.Keys) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $before[$path]) {
        throw 'User data preservation check failed; inspect locally. No cleanup of user data was attempted.'
    }
}
[ordered]@{ Action = $Action; Passed = $true; PreservedFileCount = $before.Count; VerifiedUtc = [DateTime]::UtcNow.ToString('O') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot ($Action.ToLowerInvariant() + '-result.json')) -Encoding UTF8
Write-Output "$Action passed; $($before.Count) existing user-data files retained with identical SHA-256."
