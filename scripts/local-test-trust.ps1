#requires -Version 5.1
param(
    [Parameter(Mandatory)][ValidateSet('Add','Remove')][string] $Action,
    [Parameter(Mandatory)][string] $CertificateFile,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{40}$')][string] $Thumbprint
)
$ErrorActionPreference = 'Stop'
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($CertificateFile)
try {
    if ($certificate.Thumbprint -ne $Thumbprint -or $certificate.Subject -ne 'CN=GameLingoAI.Development' -or
        $certificate.HasPrivateKey -or $certificate.NotAfter -gt (Get-Date).AddDays(3)) { throw 'Not the authorized short-lived public test certificate.' }
    $storePath = 'Cert:\LocalMachine\TrustedPeople\' + $Thumbprint
    if ($Action -eq 'Add') {
        if (Test-Path -LiteralPath $storePath) { throw 'Certificate is already trusted; refuse ambiguous ownership.' }
        $null = Import-Certificate -FilePath $CertificateFile -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
        if (-not (Test-Path -LiteralPath $storePath)) { throw 'Trust import did not complete.' }
    }
    elseif (Test-Path -LiteralPath $storePath) {
        Remove-Item -LiteralPath $storePath
    }
}
finally { $certificate.Dispose() }
