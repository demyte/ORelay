[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$ExecutablePath,
    [Parameter(Mandatory = $true)] [string]$RunRoot,
    [string]$Rid = ''
)

$ErrorActionPreference = 'Stop'

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); return $listener.LocalEndpoint.Port }
    finally { $listener.Stop(); $listener.Dispose() }
}

function Get-Lines([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return @() }
    return @(Get-Content -LiteralPath $Path | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Wait-Log([string]$Path, [string]$Pattern, [int]$Seconds = 8) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if ((Test-Path -LiteralPath $Path) -and (Get-Content -Raw -LiteralPath $Path) -match $Pattern) { return }
        Start-Sleep -Milliseconds 75
    } while ([DateTime]::UtcNow -lt $until)
    throw 'Expected server log entry was not observed before the bounded wait expired.'
}

function Wait-Ready([System.Diagnostics.Process]$Process, [System.Net.Http.HttpClient]$Client, [string]$Uri) {
    $until = [DateTime]::UtcNow.AddSeconds(12)
    do {
        if ($Process.HasExited) { throw "Server exited before readiness with code $($Process.ExitCode)." }
        try {
            $response = $Client.GetAsync($Uri).GetAwaiter().GetResult()
            $health = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
            $healthy = $response.IsSuccessStatusCode -and $health.identity -eq 'orelay' -and $health.status -eq 'ok'
            $response.Dispose()
            if ($healthy) { return }
        } catch { }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $until)
    throw 'Server did not become ready on its run-owned loopback port.'
}

function Quote-Argument([string]$Value) {
    if ($Value -notmatch '[\s"]') { return $Value }
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Stop-OwnedProcess([System.Diagnostics.Process]$Process) {
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) { $Process.Kill($true) }
        if (-not $Process.WaitForExit(10000)) { throw 'Owned logging process did not stop.' }
    } finally { $Process.Dispose() }
}

function Assert-Quiet([string]$Path, [int]$ExpectedCount) {
    $until = [DateTime]::UtcNow.AddSeconds(2)
    do {
        $count = (Get-Lines $Path).Count
        if ($count -gt $ExpectedCount) { throw 'Health or successful lease renewal produced an unexpected log entry.' }
        Start-Sleep -Milliseconds 75
    } while ([DateTime]::UtcNow -lt $until)
}

$runRootPath = [System.IO.Path]::GetFullPath($RunRoot)
$executable = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Published executable was not found: $executable" }
New-Item -ItemType Directory -Path $runRootPath -Force | Out-Null

foreach ($mode in @('default', 'json')) {
    $caseRoot = Join-Path $runRootPath "evidence/logging-$mode"
    $workRoot = Join-Path $runRootPath "work/logging-$mode"
    if ((Test-Path -LiteralPath $caseRoot) -or (Test-Path -LiteralPath $workRoot)) { throw 'Logging smoke requires fresh case directories.' }
    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
    $stdoutPath = Join-Path $caseRoot 'server.stdout.txt'
    $stderrPath = Join-Path $caseRoot 'server.stderr.txt'
    $resultPath = Join-Path $caseRoot 'result.json'
    $configPath = Join-Path $workRoot 'orelay.json'
    $process = $null
    $client = $null
    $caseStatus = 'failed'
    try {
        $port = Get-FreePort
        $destinationPort = Get-FreePort
        $destinationPath = "/synthetic-untrusted-$mode-path"
        & $executable --config-file $configPath init --port $port --lease-seconds 120 --json > (Join-Path $caseRoot 'init.json')
        if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the owned logging configuration.' }
        $arguments = @('--config-file', (Quote-Argument $configPath), '--bind', '127.0.0.1', '--port', "$port", '--lease-seconds', '120', 'server')
        if ($mode -eq 'json') { $arguments = @('--json') + $arguments }
        $start = @{
            FilePath = $executable; ArgumentList = $arguments; WorkingDirectory = $workRoot; PassThru = $true
            RedirectStandardOutput = $stdoutPath; RedirectStandardError = $stderrPath
            Environment = @{ NO_COLOR = ''; TERM = 'xterm-256color' }
        }
        if ($IsWindows) { $start.WindowStyle = 'Hidden' }
        $process = Start-Process @start
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $client = [System.Net.Http.HttpClient]::new($handler)
        $client.Timeout = [TimeSpan]::FromSeconds(3)
        Wait-Ready $process $client "http://127.0.0.1:$port/health"
        $doctor = Join-Path $PSScriptRoot '../../.agents/skills/verify-orelay/scripts/doctor.ps1'
        $null = & $doctor -ExecutablePath $executable -ConfigPath $configPath -EvidencePath (Join-Path $caseRoot 'doctor.json')
        Wait-Log $stderrPath 'Listening on'

        $callbackUrl = "http://127.0.0.1:$destinationPort$destinationPath"
        $body = [System.Net.Http.StringContent]::new((@{ callbackUrl = $callbackUrl } | ConvertTo-Json -Compress), [Text.Encoding]::UTF8, 'application/json')
        $registrationResponse = $client.PostAsync("http://127.0.0.1:$port/registrations", $body).GetAwaiter().GetResult()
        if ([int]$registrationResponse.StatusCode -ne 201) { throw 'Registration did not return HTTP 201.' }
        $registration = $registrationResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        $registrationResponse.Dispose(); $body.Dispose()
        if ([string]::IsNullOrWhiteSpace($registration.id) -or $registration.id.Length -lt 8) { throw 'Registration response did not contain a usable ID.' }
        $idPrefix = $registration.id.Substring(0, 8)
        Wait-Log $stderrPath "Registered worktree $idPrefix"

        $quietCount = (Get-Lines $stderrPath).Count
        $healthResponse = $client.GetAsync("http://127.0.0.1:$port/health").GetAwaiter().GetResult()
        $leaseResponse = $client.PutAsync("http://127.0.0.1:$port/registrations/$($registration.id)/lease", $null).GetAwaiter().GetResult()
        if (-not $healthResponse.IsSuccessStatusCode -or -not $leaseResponse.IsSuccessStatusCode) { throw 'Health or lease renewal did not succeed.' }
        $healthResponse.Dispose(); $leaseResponse.Dispose()
        Assert-Quiet $stderrPath $quietCount

        $state = "$($registration.id).synthetic-$mode-state-secret"
        $rawQuery = "state=$([Uri]::EscapeDataString($state))&code=synthetic-$mode-code-secret&scope=read"
        $callback = $client.GetAsync("http://127.0.0.1:$port/callback?$rawQuery").GetAwaiter().GetResult()
        $location = $callback.Headers.Location.AbsoluteUri
        if ([int]$callback.StatusCode -ne 302 -or $location -cne "${callbackUrl}?$rawQuery") { throw 'Successful callback did not return the expected redirect.' }
        $callback.Dispose()
        Wait-Log $stderrPath "Callback redirect issued for $idPrefix"

        $malformed = $client.GetAsync("http://127.0.0.1:$port/callback?state=synthetic-$mode-malformed-state&code=synthetic-$mode-code-secret&error=synthetic-$mode-error-secret").GetAwaiter().GetResult()
        if ([int]$malformed.StatusCode -ne 400) { throw 'Malformed callback did not return HTTP 400.' }
        $malformed.Dispose()
        Wait-Log $stderrPath 'Request rejected: invalid_routing_state \(HTTP 400\)'

        $delete = $client.DeleteAsync("http://127.0.0.1:$port/registrations/$($registration.id)").GetAwaiter().GetResult()
        if ([int]$delete.StatusCode -ne 204) { throw 'Registration deletion did not return HTTP 204.' }
        $delete.Dispose()
        Wait-Log $stderrPath "Removed worktree $idPrefix"

        $stderr = Get-Content -Raw -LiteralPath $stderrPath
        if ($stderr -match "`e|\x1b\[") { throw 'Redirected stderr contained ANSI escape sequences.' }
        foreach ($sentinel in @($destinationPath, "synthetic-$mode-state-secret", "synthetic-$mode-malformed-state", "synthetic-$mode-code-secret", "synthetic-$mode-error-secret")) {
            if ($stderr.Contains($sentinel)) { throw 'A synthetic callback value or untrusted destination path leaked into logs.' }
        }
        $lines = @(Get-Lines $stderrPath)
        if ($mode -eq 'json') {
            $stdoutLines = @(Get-Lines $stdoutPath)
            if ($stdoutLines.Count -ne 1) { throw 'JSON server stdout did not contain exactly one readiness object.' }
            $ready = $stdoutLines[0] | ConvertFrom-Json
            if ($ready.identity -ne 'orelay' -or $ready.port -ne $port -or $ready.relayCallbackUrl -ne "http://localhost:$port/callback") { throw 'JSON readiness object did not identify the owned relay.' }
            foreach ($line in $lines) {
                $record = $line | ConvertFrom-Json
                if ($null -eq $record.Message -or $record.PSObject.Properties.Name -notcontains 'LogLevel') { throw 'JSON stderr contained a non-log record.' }
            }
            $rejection = $lines | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object Message -Like 'Request rejected:*'
            if ($rejection.LogLevel -ne 'Warning') { throw 'Rejected callback was not logged as a warning.' }
        } elseif ((Get-Lines $stdoutPath).Count -ne 0) {
            throw 'Default server wrote unexpected stdout text.'
        } else {
            foreach ($line in $lines) {
                if ($line -notmatch '^\d{2}:\d{2}:\d{2} (info|warn): ORelay\[\d+\] ') { throw 'Text logs did not include the timestamp and level.' }
            }
        }
        $logDirectory = Join-Path $workRoot 'logs'
        $fileLog = Join-Path $logDirectory 'orelay.log'
        Wait-Log $fileLog 'Removed worktree'
        $fileText = Get-Content -Raw -LiteralPath $fileLog
        foreach ($event in @('started in foreground mode; automatic update scheduling unavailable outside a native service', 'Listening on', 'Registered worktree', 'Callback redirect issued', 'Request rejected', 'Removed worktree')) {
            if (-not $fileText.Contains($event)) { throw 'File log is missing an observed relay action.' }
        }
        foreach ($sentinel in @($destinationPath, "synthetic-$mode-state-secret", "synthetic-$mode-malformed-state", "synthetic-$mode-code-secret", "synthetic-$mode-error-secret")) {
            if ($fileText.Contains($sentinel)) { throw 'A synthetic callback value or destination path leaked into file logs.' }
        }
        Copy-Item -LiteralPath $fileLog -Destination (Join-Path $caseRoot 'before-rotation.log')

        # Seed the size boundary, then make the real server write and rotate.
        # Four generations prove that the oldest of three retained files is removed.
        for ($generation = 1; $generation -le 4; $generation++) {
            [IO.File]::AppendAllText($fileLog, "retention-marker-$generation`n")
            $padding = [IO.File]::Open($fileLog, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::Read)
            try { $padding.SetLength(2 * 1024 * 1024) } finally { $padding.Dispose() }
            $rejected = $client.GetAsync("http://127.0.0.1:$port/callback?state=invalid").GetAwaiter().GetResult()
            if ([int]$rejected.StatusCode -ne 400) { throw 'Rotation trigger request did not fail as expected.' }
            $rejected.Dispose()
        }
        $logFiles = @(Get-ChildItem -LiteralPath $logDirectory -Filter '*.log')
        if ($logFiles.Count -ne 3 -or @($logFiles | Where-Object Length -GT (2 * 1024 * 1024)).Count) {
            throw 'File log rotation violated the three-file or 2 MiB limit.'
        }
        $retained = ($logFiles | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }) -join ''
        if ($retained.Contains('retention-marker-1') -or $retained.Contains('retention-marker-2') -or
            -not $retained.Contains('retention-marker-3') -or -not $retained.Contains('retention-marker-4')) {
            throw 'Log rotation did not keep the newest generations.'
        }

        # The detached updater and server must append to the same log safely.
        $workerConfig = Join-Path $workRoot 'worker.json'
        '{"schemaVersion":1,"autoUpdate":false}' | Set-Content -LiteralPath $workerConfig
        $workerStart = [Diagnostics.ProcessStartInfo]::new($executable)
        $workerStart.UseShellExecute = $false
        $workerStart.CreateNoWindow = $true
        foreach ($argument in @('__auto-update', $workerConfig, 'orelay-logging-smoke')) { $workerStart.ArgumentList.Add($argument) }
        $worker = [Diagnostics.Process]::Start($workerStart)
        $workerId = $worker.Id
        try {
            for ($i = 0; $i -lt 20; $i++) {
                $rejected = $client.GetAsync("http://127.0.0.1:$port/callback?state=invalid").GetAwaiter().GetResult()
                if ([int]$rejected.StatusCode -ne 400) { throw 'Concurrent logging request failed.' }
                $rejected.Dispose()
            }
            if (-not $worker.WaitForExit(10000) -or $worker.ExitCode -ne 0) { throw 'Owned disabled worker did not finish successfully.' }
        } finally { Stop-OwnedProcess $worker }
        Wait-Log $fileLog 'Automatic update completed: disabled'
        $currentLines = @(Get-Lines $fileLog)
        if (@($currentLines | Where-Object { $_ -like '*Request rejected:*' }).Count -ne 21 -or
            @($currentLines | Where-Object { $_ -like '*Automatic update check started*' }).Count -ne 1) {
            throw 'Concurrent server and worker logging lost records.'
        }
        if (@($currentLines | Where-Object { $_ -notmatch '^\d{4}-\d{2}-\d{2}T.*\[(Information|Warning)\] ORelay' }).Count) {
            throw 'Concurrent file logging produced an incomplete record.'
        }
        $serverRecord = "[pid $($process.Id)] [Warning] ORelay[5]: Request rejected:"
        $workerRecord = "[pid $workerId] [Information] ORelay.Updating.ServiceAutoUpdateWorker[4]: Automatic update completed: disabled; reason=disabled-in-configuration, service=orelay-logging-smoke,"
        if (@($currentLines | Where-Object { $_.Contains($serverRecord) }).Count -ne 21 -or
            @($currentLines | Where-Object { $_.Contains($workerRecord) }).Count -ne 1) {
            throw 'File records did not identify the correct server and worker processes or outcome.'
        }
        [pscustomobject]@{ Mode = $mode; Rid = $Rid; Status = 'passed'; StderrLines = $lines.Count; FileCount = $logFiles.Count; MaximumFileBytes = 2 * 1024 * 1024; ConcurrentRecords = $currentLines.Count } |
            ConvertTo-Json | Set-Content -LiteralPath $resultPath
        $caseStatus = 'passed'
    }
    finally {
        if ($null -ne $client) { $client.Dispose() }
        Stop-OwnedProcess $process
        $logDirectory = Join-Path $workRoot 'logs'
        if (Test-Path -LiteralPath $logDirectory) {
            Copy-Item -LiteralPath $logDirectory -Destination (Join-Path $caseRoot 'logs') -Recurse
        }
        $allowedRoot = [IO.Path]::GetFullPath((Join-Path $runRootPath 'work')) + [IO.Path]::DirectorySeparatorChar
        $resolvedWork = [IO.Path]::GetFullPath($workRoot)
        if (-not $resolvedWork.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe logging cleanup path.' }
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
        if ($caseStatus -ne 'passed') {
            [pscustomobject]@{ Mode = $mode; Rid = $Rid; Status = $caseStatus } |
                ConvertTo-Json | Set-Content -LiteralPath $resultPath
        }
    }
}

& (Join-Path $PSScriptRoot 'cli-logging-smoke.ps1') -ExecutablePath $executable -RunRoot $runRootPath
Write-Output "Logging smoke passed. Evidence: $(Join-Path $runRootPath 'evidence')"
