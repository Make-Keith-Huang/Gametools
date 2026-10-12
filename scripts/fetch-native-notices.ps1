#requires -Version 7.0
param([ValidateSet('OnnxRuntime', 'Mklml')][string] $Component = 'OnnxRuntime')
# Explicit maintainer operation, never called by application startup or the offline verification suite.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$url = 'https://github.com/microsoft/onnxruntime/releases/download/v1.11.1/onnxruntime-win-x64-1.11.1.zip'
$archiveSha = '1F127B9D41F445A2D03356C86C125CB79DC3E66D391872C9BABE6B444A51A93D'
$expectedDlls = @{
    'onnxruntime.dll' = '24CCAA3BA9C5A79D88B3EA03B4399FE6D6D5C75B826661422C09613843B78213'
    'onnxruntime_providers_shared.dll' = 'FE691536E41E7D7AC0D5C6723A9AA69CE168E03676C89C50FC5BD98158029584'
}
$componentName = 'ONNX Runtime'
$version = '1.11.1'
$prefix = 'onnxruntime-win-x64-1.11.1/'
$filePrefix = 'onnxruntime-1.11.1-'
$noticeNames = @('LICENSE', 'ThirdPartyNotices.txt')
if ($Component -eq 'Mklml') {
    $componentName = 'Intel MKLML and OpenMP'
    $version = '2019.0.5.20190502'
    $url = 'https://paddlepaddledeps.bj.bcebos.com/mklml_win_2019.0.5.20190502.zip'
    $archiveSha = '535857B17643D7F7546B58FC621244E7CFCC4FFF2AA2EBD3FC5B4E126BFC36CF'
    $prefix = 'mklml_win_2019.0.5.20190502/'
    $filePrefix = 'mklml-2019.0.5.20190502-'
    $noticeNames = @('license.txt', 'third-party-programs.txt')
    $expectedDlls = @{
        'mklml.dll' = 'E2A7FD93E1534568626CFFE22676B15164A5A2B3A62F1919A55993478B45811E'
        'libiomp5md.dll' = 'BC9E17190505A3BEDA7FEEDD90BA3AB7B6DC3F7CA5D980B2140C53AE459FC5AE'
    }
}
$root = Split-Path -Parent $PSScriptRoot
$client = [Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(90)
$client.MaxResponseContentBufferSize = 64MB
$memory = $null
$zip = $null
try {
    $bytes = $client.GetByteArrayAsync($url).GetAwaiter().GetResult()
    if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ne $archiveSha) {
        throw 'Official archive checksum differs; no notices were committed.'
    }
    $memory = [IO.MemoryStream]::new($bytes, $false)
    $zip = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read)
    foreach ($name in $expectedDlls.Keys) {
        $entries = @($zip.Entries | Where-Object FullName -CEQ ($prefix + 'lib/' + $name))
        if ($entries.Count -ne 1) { throw 'Expected exactly one upstream DLL entry.' }
        $stream = $entries[0].Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -ne $expectedDlls[$name]) { throw 'Upstream DLL does not match the reviewed payload.' }
    }
    $noticeBytes = @{}
    foreach ($name in $noticeNames) {
        $entries = @($zip.Entries | Where-Object FullName -CEQ ($prefix + $name))
        if ($entries.Count -ne 1 -or $entries[0].Length -le 0 -or $entries[0].Length -gt 1MB) {
            throw 'Missing, duplicate or oversized upstream notice.'
        }
        $stream = $entries[0].Open()
        $buffer = [IO.MemoryStream]::new()
        try { $stream.CopyTo($buffer); $noticeBytes[$name] = $buffer.ToArray() }
        finally { $buffer.Dispose(); $stream.Dispose() }
    }
    # Generated evidence is isolated, never overwrites source files or existing notices.
    $output = Join-Path $root ('artifacts/native-notices/' + [guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $output
    $notices = @()
    foreach ($name in ($noticeBytes.Keys | Sort-Object)) {
        $destinationName = $filePrefix + $name + '.txt'
        [IO.File]::WriteAllBytes((Join-Path $output $destinationName), $noticeBytes[$name])
        $notices += @{ file = $destinationName; sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($noticeBytes[$name])); bytes = $noticeBytes[$name].Length }
    }
    @{ schemaVersion=1; component=$componentName; version=$version; source=$url; archiveSha256=$archiveSha;
        nativeFiles=$expectedDlls; notices=$notices; remainingNativeAttributionComplete=$false } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'provenance.json') -Encoding utf8NoBOM
    Write-Output "Verified official archive, two DLL hashes and exact notice bytes: $output"
}
finally {
    if ($null -ne $zip) { $zip.Dispose() }
    if ($null -ne $memory) { $memory.Dispose() }
    $client.Dispose()
}
