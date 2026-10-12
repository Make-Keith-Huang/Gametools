#requires -Version 7.0
# Explicit maintainer network read only. Never called by packaging or application startup.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sources = @(
    @{ repo='libjpeg-turbo/libjpeg-turbo'; tag='3.1.3'; paths=@('LICENSE.md', 'README.ijg') },
    @{ repo='pnggroup/libpng'; tag='v1.6.55'; paths=@('LICENSE') },
    @{ repo='webmproject/libwebp'; tag='v1.6.0'; paths=@('COPYING', 'PATENTS') },
    @{ repo='madler/zlib'; tag='v1.3.1'; paths=@('LICENSE') },
    @{ repo='tukaani-project/xz'; tag='v5.8.2'; paths=@('COPYING', 'COPYING.0BSD') },
    @{ repo='libtiff/libtiff'; tag='v4.7.1'; commit='5fe20d0e9aba49a6a350ed533459d1505203838f'; paths=@('LICENSE.md') }
)
$records = foreach ($item in $sources) {
    $commit = if ($item.ContainsKey('commit')) { @{ sha=$item.commit } }
        else { Invoke-RestMethod ('https://api.github.com/repos/' + $item.repo + '/commits/' + $item.tag) }
    if ($commit.sha -notmatch '^[a-f0-9]{40}$') { throw 'Invalid upstream commit.' }
    foreach ($path in $item.paths) {
        $url = 'https://raw.githubusercontent.com/' + $item.repo + '/' + $commit.sha + '/' + $path
        if ($item.ContainsKey('commit')) {
            $url = 'https://gitlab.com/' + $item.repo + '/-/raw/' + $commit.sha + '/' + $path
        }
        $value = [string](Invoke-WebRequest $url).Content
        $value = $value.Replace("`r`n", "`n")
        if (-not $value.EndsWith("`n")) { $value += "`n" }
        $bytes = [Text.Encoding]::UTF8.GetBytes($value)
        if ($bytes.Length -lt 100 -or $bytes.Length -gt 1048576) { throw 'Unexpected notice size.' }
        @{
            file = 'opencv-' + $item.repo.Split('/')[1] + '-' + $item.tag + '-' + $path.Replace('.', '-') + '.txt'
            source = $url
            sourceCommit = $commit.sha
            version = $item.tag
            text = $value
            bytes = $bytes.Length
            sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        }
    }
}
ConvertTo-Json -Depth 6 -InputObject @($records)
