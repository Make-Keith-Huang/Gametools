#requires -Version 7.0
# Explicit maintainer-only source reads; no local modifications or execution of remote code.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sources = @(
    @{ repo='tronkko/dirent'; ref='1.23.2'; path='LICENSE'; name='dirent-1.23.2' },
    @{ repo='Reference-LAPACK/lapack'; ref='v3.10.0'; path='LICENSE'; name='lapack-3.10.0' },
    @{ repo='weidai11/cryptopp'; ref='9dcc26c58213abb8351fbb1b2a7a1d2c667366e4'; path='License.txt'; name='cryptopp-9dcc26c' }
)
$notices = foreach ($item in $sources) {
    $commit = Invoke-RestMethod ('https://api.github.com/repos/' + $item.repo + '/commits/' + $item.ref) -TimeoutSec 30
    if ($commit.sha -notmatch '^[a-f0-9]{40}$') { throw 'Unexpected commit identifier.' }
    $url = 'https://raw.githubusercontent.com/' + $item.repo + '/' + $commit.sha + '/' + $item.path
    $text = [string](Invoke-WebRequest $url -TimeoutSec 30).Content
    $text = $text.Replace("`r`n", "`n")
    if (-not $text.EndsWith("`n")) { $text += "`n" }
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    if ($bytes.Length -lt 100 -or $bytes.Length -gt 1MB) { throw 'Unexpected notice size.' }
    @{ file='paddle-' + $item.name + '-LICENSE.txt'; source=$url; sourceCommit=$commit.sha;
        text=$text; bytes=$bytes.Length; sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) }
}
ConvertTo-Json -Depth 5 -InputObject @($notices)
