#requires -Version 5.1
param([Parameter(Mandatory)][string] $RunDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$run = (Get-Item -LiteralPath $RunDirectory).FullName
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/release')).TrimEnd('\') + '\'
if (-not $run.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Select an owned release run.' }
$record = Get-Content -LiteralPath (Join-Path $run 'artifact-hash.json') -Raw | ConvertFrom-Json
if ([IO.Path]::GetFileName($record.file) -ne $record.file) { throw 'Invalid package filename.' }
$inputPackage = Join-Path $run $record.file
if ((Get-FileHash -LiteralPath $inputPackage -Algorithm SHA256).Hash -ne $record.sha256) { throw 'Package hash mismatch.' }
$testRoot = Join-Path $root ('artifacts/local-install-tests/' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testRoot
$journal = Join-Path $testRoot 'certificate-journal.json'
$certificate = $null
try {
    $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=GameLingoAI.Development' `
        -FriendlyName 'GameLingoAI temporary local acceptance - remove after testing' `
        -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -HashAlgorithm SHA256 -KeyLength 2048 `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddDays(2) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    [ordered]@{ Thumbprint = $certificate.Thumbprint; Subject = $certificate.Subject;
        ExpiresUtc = $certificate.NotAfter.ToUniversalTime().ToString('O');
        PrivateKeyStore = 'CurrentUser/My'; TrustStore = 'LocalMachine/TrustedPeople';
        CleanupRequired = $true } | ConvertTo-Json | Set-Content -LiteralPath $journal -Encoding UTF8
    $null = Export-Certificate -Cert $certificate -FilePath (Join-Path $testRoot 'test-public.cer')
    $signed = Join-Path $testRoot 'GameLingoAI-test-signed.msix'
    Copy-Item -LiteralPath $inputPackage -Destination $signed
    & 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe' sign /fd SHA256 /s My /sha1 $certificate.Thumbprint $signed
    if ($LASTEXITCODE -ne 0) { throw 'Test signing failed.' }
    Write-Output "Test signing complete; private key is non-exportable. Journal: $journal"
}
catch {
    if ($null -ne $certificate) { Remove-Item -LiteralPath ('Cert:\CurrentUser\My\' + $certificate.Thumbprint) -DeleteKey }
    throw
}
