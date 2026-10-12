#requires -Version 7.0
param([Parameter(Mandatory)][string] $Destination)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$Destination = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $Destination) { throw 'Refuse to overwrite an existing source archive.' }
$directories = @('src','tests','tools','scripts','packaging','docs','prompts','.github')
$rootFiles = @('LICENSE','README.md','AGENTS.md','ARCHITECTURE.md','CODING_RULES.md','DATABASE.md',
    'PRODUCT_REQUIREMENTS.md','ROADMAP.md','TASKS.md','TEST_PLAN.md','.editorconfig','.gitignore',
    'Directory.Build.props','Directory.Packages.props','GameLingoAI.sln','global.json')
$files = [Collections.Generic.List[object]]::new()
foreach ($name in $rootFiles) { $files.Add((Get-Item -LiteralPath (Join-Path $root $name))) }
foreach ($directory in $directories) {
    $directoryInfo = Get-Item -LiteralPath (Join-Path $root $directory)
    if ($directoryInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source directory is linked.' }
    foreach ($item in (Get-ChildItem -LiteralPath $directoryInfo.FullName -Recurse -Force)) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source links require explicit review.' }
        if (-not $item.PSIsContainer) { $files.Add($item) }
    }
}
$allowed = @('.md','.cs','.csproj','.json','.xml','.ps1','.txt','.pubxml','.yml','.yaml',
    '.props','.targets','.sln','.runsettings','.xaml','.manifest','.cmd')
$inventory = [Collections.Generic.List[object]]::new()
foreach ($item in $files) {
    $relative = [IO.Path]::GetRelativePath($root,$item.FullName).Replace('\','/')
    # Domain Models is source, not downloaded OCR weights. Only generated build trees
    # are excluded here; the top-level allowlist already omits runtime data directories.
    if ($relative -match '(?i)(^|/)(bin|obj|artifacts[^/]*|\.git)(/|$)') { continue }
    if (($relative -notin $rootFiles -and $relative -ne 'packaging/licenses/.gitattributes' -and $item.Extension -notin $allowed) -or
        $item.Name -match '(?i)^(secrets|settings|appsettings\.local)\.json$') {
        throw "Review excluded/generated source candidate: $relative"
    }
    $inventory.Add([ordered]@{ path=$relative; bytes=$item.Length; sha256=(Get-FileHash -LiteralPath $item.FullName).Hash })
}
if (@($inventory | Where-Object { $_.path -like 'src/GameLingoAI.Core/Models/*.cs' }).Count -eq 0) {
    throw 'Domain model source files are required.'
}
$null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Destination)) -Force
$archive = [IO.Compression.ZipFile]::Open($Destination,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($record in $inventory) {
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,
            (Join-Path $root $record.path),$record.path,[IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $archive.Dispose() }
$archive = [IO.Compression.ZipFile]::OpenRead($Destination)
try {
    if ($archive.Entries.Count -ne $inventory.Count) { throw 'Source archive count mismatch.' }
    foreach ($record in $inventory) {
        $entry=$archive.GetEntry($record.path)
        if ($null -eq $entry -or $entry.Length -ne $record.bytes) { throw 'Source archive entry mismatch.' }
        $stream=$entry.Open()
        try { $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -ne $record.sha256) { throw 'Source archive hash mismatch.' }
    }
}
finally { $archive.Dispose() }
[ordered]@{file=[IO.Path]::GetFileName($Destination);sha256=(Get-FileHash -LiteralPath $Destination).Hash
    files=$inventory.ToArray(); scope='Allowlisted source export; not a complete secret or license audit'} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath ($Destination+'.manifest.json') -Encoding utf8NoBOM
Write-Output "Source archive verified: $Destination ($($inventory.Count) files)."
