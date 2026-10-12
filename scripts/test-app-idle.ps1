param(
    [ValidateSet('Smoke', 'EightHours')][string] $Mode = 'Smoke',
    [string] $DataRoot = 'D:\ai工作数据和目录存放点\GameLingoAI',
    [string] $ApplicationPath
)

# Observe the real application process, not the headless replay harness.
$ErrorActionPreference = 'Stop'
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$exe = if ($ApplicationPath) { [IO.Path]::GetFullPath($ApplicationPath) }
    else { Join-Path $DataRoot 'artifacts/bin/GameLingoAI.App/release/GameLingoAI.App.exe' }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Build the routed Release application first.' }
$duration = if ($Mode -eq 'EightHours') { 28800 } else { 30 }
$warmup = if ($Mode -eq 'EightHours') { 300 } else { 5 }
$interval = if ($Mode -eq 'EightHours') { 10 } else { 1 }
$runRoot = Join-Path $DataRoot ('reports/app-idle/' + [guid]::NewGuid().ToString('N'))
$tempRoot = Join-Path $runRoot 'temp'
$null = New-Item -ItemType Directory -Path $tempRoot -Force
$reportPath = Join-Path $runRoot 'result.json'
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
$appProcess = $null
$failure = $null
$baseline = $null
$peak = $null
$samples = [Collections.Generic.List[object]]::new()
$watch = [Diagnostics.Stopwatch]::new()
$passed = $false
$exitCode = $null
$forcedStop = $false
$report = [ordered]@{
    version = 1; scope = 'real-application-idle'; mode = $Mode
    releaseDurationSatisfied = $false; passed = $false
    executable = $exe; executableSha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    managedAssemblySha256 = (Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($exe, '.dll')) -Algorithm SHA256).Hash
    startedUtc = [DateTimeOffset]::UtcNow; processId = $null
    requestedSeconds = $duration; warmupSeconds = $warmup
}
try {
    # No navigation, capture, model download, provider request or user-settings change.
    $env:TEMP = $tempRoot
    $env:TMP = $tempRoot
    $appProcess = Start-Process -FilePath $exe -ArgumentList '--synthetic-test' -WindowStyle Hidden -PassThru
    $report.processId = $appProcess.Id
    $startup = [Diagnostics.Stopwatch]::StartNew()
    do {
        $appProcess.Refresh()
        if ($appProcess.HasExited) { throw 'Application exited during startup.' }
        if ($appProcess.MainWindowTitle -eq 'GameLingoAI') { break }
        if ($startup.Elapsed.TotalSeconds -ge 30) { throw 'Main window did not become ready.' }
        Start-Sleep -Milliseconds 250
    } while ($true)
    $watch.Start()
    do {
        $appProcess.Refresh()
        if ($appProcess.HasExited -or -not $appProcess.Responding -or $appProcess.MainWindowTitle -ne 'GameLingoAI') {
            throw 'Application exited, stopped responding or lost its expected main window.'
        }
        $elapsed = $watch.Elapsed.TotalSeconds
        if ($samples.Count -gt 0 -and $elapsed - $samples[$samples.Count - 1].elapsedSeconds -gt ($interval * 3 + 2)) {
            throw 'Sampling gap prevents continuous idle verification.'
        }
        $private = $appProcess.PrivateMemorySize64
        if ($private -le 0) { throw 'Private-memory counter is unavailable.' }
        if ($elapsed -ge $warmup) {
            if ($null -eq $baseline) { $baseline = $private; $peak = $private }
            $peak = [Math]::Max($peak, $private)
        }
        $samples.Add([ordered]@{ elapsedSeconds = $elapsed; privateBytes = $private
            cpuSeconds = $appProcess.TotalProcessorTime.TotalSeconds; handles = $appProcess.HandleCount })
        if ($elapsed -ge $duration) { break }
        Start-Sleep -Seconds $interval
    } while ($true)
    $passed = $null -ne $baseline -and $samples.Count -ge 3
}
catch {
    # Do not export arbitrary exception text or local user data.
    $failure = $_.Exception.GetType().FullName
}
finally {
    $watch.Stop()
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    if ($null -ne $appProcess) {
        try {
            if (-not $appProcess.HasExited) {
                $requested = $appProcess.CloseMainWindow()
                if (-not $requested -or -not $appProcess.WaitForExit(15000)) {
                    $forcedStop = $true
                    $appProcess.Kill()
                    $null = $appProcess.WaitForExit(5000)
                }
            }
            if ($appProcess.HasExited) { $exitCode = $appProcess.ExitCode }
        }
        catch { $failure = $_.Exception.GetType().FullName }
        finally { $appProcess.Dispose() }
    }
    $passed = $passed -and $null -eq $failure -and -not $forcedStop -and $exitCode -eq 0
    $report.passed = $passed
    $report.releaseDurationSatisfied = $passed -and $Mode -eq 'EightHours' -and $watch.Elapsed.TotalSeconds -ge 28800
    $report.elapsedSeconds = $watch.Elapsed.TotalSeconds
    $report.baselinePrivateBytes = $baseline
    $report.peakPrivateBytes = $peak
    $report.peakGrowthBytes = if ($null -ne $baseline) { $peak - $baseline } else { $null }
    $report.exitCode = $exitCode
    $report.forcedStop = $forcedStop
    $report.failureType = $failure
    $report.samples = $samples.ToArray()
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
}
Write-Output "Idle result: passed=$passed; report=$reportPath"
if (-not $passed) { exit 1 }
