#requires -Version 7.0
# Explicit read-only upstream inspection. No archive entries are executed or extracted to disk.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$url = 'https://raw.githubusercontent.com/opencv/opencv_3rdparty/c934a2a15a6df020446ac3dfa07e3acf72b63a8f/ippicv/ippicv_2022.2.0_win_intel64_20250730_general.zip'
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(90)
$client.MaxResponseContentBufferSize = 128MB
$memory = $null
$archive = $null
try {
    $bytes = $client.GetByteArrayAsync($url).GetAwaiter().GetResult()
    $md5 = [Convert]::ToHexString([Security.Cryptography.MD5]::HashData($bytes))
    if ($md5 -ne '7C0973976AB0716BC33F03A76A50017F') { throw 'Upstream archive digest mismatch.' }
    $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    if ($sha -ne '56C7BADA1067982AFD5DD43636F8A9B5A5B80447EC452B754252168CDE9D8BE8') { throw 'Reviewed archive SHA-256 mismatch.' }
    $memory = [IO.MemoryStream]::new($bytes, $false)
    $archive = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read)
    $notices = foreach ($entry in $archive.Entries) {
        if ($entry.Name -notmatch '(?i)license|copying|third.party|notice|eula') { continue }
        if ($entry.Length -le 0 -or $entry.Length -gt 1MB) { throw 'Unexpected notice size.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $value = $reader.ReadToEnd().Replace("`r`n", "`n") } finally { $reader.Dispose() }
        if (-not $value.EndsWith("`n")) { $value += "`n" }
        $content = [Text.Encoding]::UTF8.GetBytes($value)
        @{ file='opencv-ipp-' + $entry.FullName.Replace('/', '-').Replace('\', '-') + '.txt'; entry=$entry.FullName;
            text=$value; bytes=$content.Length; sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($content)); source=$url }
    }
    @{ source=$url; archiveSha256=$sha; archiveBytes=$bytes.Length; notices=@($notices);
        textEntries=@($archive.Entries | Where-Object { $_.Name -match '\.(txt|md|htm|html|rtf)$' } | Select-Object FullName,Length) } | ConvertTo-Json -Depth 6
}
finally {
    if ($null -ne $archive) { $archive.Dispose() }
    if ($null -ne $memory) { $memory.Dispose() }
    $client.Dispose()
}
