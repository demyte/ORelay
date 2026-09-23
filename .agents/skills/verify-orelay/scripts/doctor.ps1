[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ConfigPath,
    [Parameter(Mandatory)][string]$EvidencePath,
    [int]$ListenerPort = 0,
    [string]$ListenerHostname = '',
    [ValidateSet(0, 1)][int]$ExpectedExitCode = 0
)

$ErrorActionPreference = 'Stop'
$process = $null
$started = $false
$diagnosticPath = $null
try {
    $before = if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) { (Get-FileHash -LiteralPath $ConfigPath).Hash } else { $null }
    $selectedPath = $ConfigPath
    $snapshot = $null
    if ($ListenerPort -gt 0 -or $ListenerHostname) {
        # Doctor accepts saved configuration only. A separate file describes the
        # observed listener without modifying the file watched by the relay.
        $snapshot = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
        if ($ListenerPort -gt 0) { $snapshot.port = $ListenerPort }
        if ($ListenerHostname) { $snapshot.hostname = $ListenerHostname; $snapshot.publicUrl = $null; $snapshot.autoDiscovery = 'none' }
        $diagnosticPath = Join-Path (Split-Path $ConfigPath -Parent) "doctor-$([Guid]::NewGuid().ToString('N')).json"
        $snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $diagnosticPath -Encoding utf8
        $selectedPath = $diagnosticPath
    }
    $arguments = @('--config-file', $selectedPath, 'doctor', '--name', "orelay-verify-$([Guid]::NewGuid().ToString('N'))", '--json')
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $ExecutablePath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $started = $process.Start()
    if (-not $started) { throw 'Could not start the read-only doctor.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit(30000)
    if ($timedOut) { $process.Kill($true); [void]$process.WaitForExit(5000) }
    $record = [ordered]@{
        command = @($ExecutablePath) + $arguments
        exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
        timedOut = $timedOut
        stdout = if ($stdout.Wait(5000)) { $stdout.GetAwaiter().GetResult() } else { '[capture timeout]' }
        stderr = if ($stderr.Wait(5000)) { $stderr.GetAwaiter().GetResult() } else { '[capture timeout]' }
        diagnosticSettings = $snapshot
        configurationHashBefore = $before
        configurationHashAfter = if (Test-Path -LiteralPath $ConfigPath -PathType Leaf) { (Get-FileHash -LiteralPath $ConfigPath).Hash } else { $null }
    }
    $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $EvidencePath -Encoding utf8
    if ($timedOut -or $record.exitCode -ne $ExpectedExitCode) { throw "Doctor failed its expected exit-code check. See $EvidencePath" }
    if ($record.configurationHashAfter -cne $before) { throw "Read-only doctor changed configuration. See $EvidencePath" }
    $report = $record.stdout | ConvertFrom-Json
    if (($ExpectedExitCode -eq 0) -ne [bool]$report.healthy) { throw "Doctor health disagrees with its expected result. See $EvidencePath" }
    return $report
}
finally {
    if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
    if ($null -ne $process) { $process.Dispose() }
    if ($diagnosticPath) { [IO.File]::Delete($diagnosticPath) }
}
