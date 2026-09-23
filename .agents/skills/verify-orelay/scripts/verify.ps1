[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$ExecutablePath = '',
    [string]$RunId = '',
    [switch]$CheckAutoUpdate
)

$ErrorActionPreference = 'Stop'

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if ($Actual -cne $Expected) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-SafePathSegment {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Value -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$') {
        throw "$Name must contain only letters, numbers, hyphens, and underscores, and must be 1 to 64 characters long."
    }
}

function Resolve-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Child,
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $childFull = [System.IO.Path]::GetFullPath($Child)
    $parentPrefix = $parentFull + [System.IO.Path]::DirectorySeparatorChar
    if ($childFull.Equals($parentFull, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not $childFull.StartsWith($parentPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name '$childFull' is not a child of '$parentFull'."
    }

    return $childFull
}

function Read-CompletedText {
    param(
        [Parameter(Mandatory = $true)][System.Threading.Tasks.Task[string]]$Task,
        [int]$TimeoutMilliseconds = 5000
    )

    if (-not $Task.Wait($TimeoutMilliseconds)) {
        return "[output capture did not finish within $TimeoutMilliseconds ms]"
    }

    return $Task.GetAwaiter().GetResult()
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Parse('127.0.0.1'),
        0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
        $listener.Dispose()
    }
}

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$EvidenceName,
        [int]$ExpectedExitCode = 0,
        [int]$TimeoutMilliseconds = 30000
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:ExecutablePath
    $startInfo.WorkingDirectory = $script:ScratchRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Could not start '$script:ExecutablePath'."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($TimeoutMilliseconds)
    if ($timedOut) {
        for ($attempt = 0; $attempt -lt 2 -and -not $process.HasExited; $attempt++) {
            try {
                $process.Kill($true)
            }
            catch [System.InvalidOperationException] {
            }
            [void]$process.WaitForExit(5000)
        }
    }
    $stdout = Read-CompletedText -Task $stdoutTask
    $stderr = Read-CompletedText -Task $stderrTask
    $processStillRunning = -not $process.HasExited
    $exitCode = if ($processStillRunning) { $null } else { $process.ExitCode }
    $process.Dispose()

    $record = [ordered]@{
        command = @('orelay') + $Arguments
        expectedExitCode = $ExpectedExitCode
        exitCode = $exitCode
        timedOut = $timedOut
        processStillRunning = $processStillRunning
        stdout = $stdout
        stderr = $stderr
    }
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot "$EvidenceName.json") -Value $record
    $script:CommandRecords.Add($record)

    if ($timedOut) {
        throw "'$($Arguments -join ' ')' did not exit within $TimeoutMilliseconds ms. Its owned process was terminated. Still running: $processStillRunning. See $EvidenceName.json."
    }

    if ($exitCode -ne $ExpectedExitCode) {
        throw "'$($Arguments -join ' ')' exited with code $exitCode, expected $ExpectedExitCode. See $EvidenceName.json."
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Stdout = $stdout
        Stderr = $stderr
    }
}

function Start-OwnedServer {
    param(
        [Parameter(Mandatory = $true)][string]$ConfigPath,
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$EvidencePrefix
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:ExecutablePath
    $startInfo.WorkingDirectory = $script:ScratchRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('--config-file', $ConfigPath, '--port', [string]$Port, 'server')) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Could not start the relay on port $Port."
    }

    $entry = [pscustomobject]@{
        Process = $process
        Pid = $process.Id
        Port = $Port
        EvidencePrefix = $EvidencePrefix
        StdoutTask = $process.StandardOutput.ReadToEndAsync()
        StderrTask = $process.StandardError.ReadToEndAsync()
    }
    $script:OwnedServers.Add($entry)
    return $entry
}

function Stop-OwnedServer {
    param(
        [Parameter(Mandatory = $true)]$Entry
    )

    $process = $Entry.Process
    $wasRunning = -not $process.HasExited
    if ($wasRunning) {
        for ($attempt = 0; $attempt -lt 2 -and -not $process.HasExited; $attempt++) {
            try {
                $process.Kill($true)
            }
            catch [System.InvalidOperationException] {
            }
            [void]$process.WaitForExit(5000)
        }
    }

    $stdout = ''
    $stderr = ''
    try {
        $stdout = Read-CompletedText -Task $Entry.StdoutTask
    }
    catch {
        $stdout = "Could not read relay stdout: $($_.Exception.Message)"
    }
    try {
        $stderr = Read-CompletedText -Task $Entry.StderrTask
    }
    catch {
        $stderr = "Could not read relay stderr: $($_.Exception.Message)"
    }

    $exitCode = $null
    try {
        $exitCode = $process.ExitCode
    }
    catch {
    }

    Set-Content -LiteralPath (Join-Path $script:EvidenceRoot "$($Entry.EvidencePrefix).stdout.txt") -Value $stdout -Encoding utf8
    Set-Content -LiteralPath (Join-Path $script:EvidenceRoot "$($Entry.EvidencePrefix).stderr.txt") -Value $stderr -Encoding utf8
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot "$($Entry.EvidencePrefix).process.json") -Value ([ordered]@{
        pid = $Entry.Pid
        port = $Entry.Port
        wasRunningAtCleanup = $wasRunning
        exitedAfterCleanup = $process.HasExited
        exitCode = $exitCode
    })
}

function Wait-ForHealth {
    param(
        [Parameter(Mandatory = $true)][System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)]$Process
    )

    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($Process.HasExited) {
            throw "Relay process $($Process.Id) exited before health became ready with code $($Process.ExitCode)."
        }

        try {
            $response = $Client.GetAsync($Url).GetAwaiter().GetResult()
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $response.Dispose()
            if ([int]$response.StatusCode -eq 200) {
                $health = $body | ConvertFrom-Json
                if ($health.identity -eq 'orelay' -and $health.status -eq 'ok') {
                    return $health
                }
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 250
    }

    throw "Relay did not become ready at $Url."
}

function Start-CallbackListener {
    param([Parameter(Mandatory = $true)][int]$Port)

    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Parse('127.0.0.1'),
        $Port)
    $listener.Start()
    $script:OwnedListeners.Add($listener)
    return $listener
}

function Receive-RawHttpRequest {
    param(
        [Parameter(Mandatory = $true)][System.Net.Sockets.TcpListener]$Listener,
        [int]$TimeoutMilliseconds = 5000
    )

    $acceptTask = $Listener.AcceptTcpClientAsync()
    if (-not $acceptTask.Wait($TimeoutMilliseconds)) {
        throw "Callback listener did not receive a request within $TimeoutMilliseconds ms."
    }

    $client = $acceptTask.Result
    $stream = $client.GetStream()
    $stream.ReadTimeout = $TimeoutMilliseconds
    $bytes = [System.IO.MemoryStream]::new()
    $buffer = [byte[]]::new(4096)
    try {
        do {
            $read = $stream.Read($buffer, 0, $buffer.Length)
            if ($read -gt 0) {
                $bytes.Write($buffer, 0, $read)
            }
            $text = [System.Text.Encoding]::ASCII.GetString($bytes.ToArray())
        }
        while ($read -gt 0 -and $text.IndexOf("`r`n`r`n", [System.StringComparison]::Ordinal) -lt 0)

        $requestLine = ($text -split "`r`n", 2)[0]
        if ($requestLine -notmatch '^(?<method>\S+) (?<target>\S+) HTTP/\d\.\d$') {
            throw "Callback listener received an invalid request line."
        }

        return [pscustomobject]@{
            Client = $client
            Stream = $stream
            RawText = $text
            Method = $Matches.method
            RequestTarget = $Matches.target
        }
    }
    catch {
        $stream.Dispose()
        $client.Dispose()
        throw
    }
    finally {
        $bytes.Dispose()
    }
}

function Complete-CallbackRequest {
    param(
        [Parameter(Mandatory = $true)]$Request,
        [int]$StatusCode = 204,
        [string]$Reason = 'No Content'
    )

    try {
        $payload = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $StatusCode $Reason`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
        $Request.Stream.Write($payload, 0, $payload.Length)
        $Request.Stream.Flush()
    }
    finally {
        $Request.Stream.Dispose()
        $Request.Client.Dispose()
    }
}

function Get-Registration {
    param(
        [Parameter(Mandatory = $true)][System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)][string]$RelayBase,
        [Parameter(Mandatory = $true)][string]$CallbackUrl
    )

    $content = [System.Net.Http.StringContent]::new(
        (@{ callbackUrl = $CallbackUrl } | ConvertTo-Json -Compress),
        [System.Text.Encoding]::UTF8,
        'application/json')
    try {
        $response = $Client.PostAsync("$RelayBase/registrations", $content).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        $response.Dispose()
        if ($status -ne 201) {
            throw "Registration for '$CallbackUrl' returned HTTP $status."
        }

        return ($body | ConvertFrom-Json)
    }
    finally {
        $content.Dispose()
    }
}

function Get-CallbackResponse {
    param(
        [Parameter(Mandatory = $true)][System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)][string]$Url
    )

    $response = $Client.GetAsync($Url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $location = $null
    if ($null -ne $response.Headers.Location) {
        $location = $response.Headers.Location.OriginalString
    }
    $result = [pscustomobject]@{
        StatusCode = [int]$response.StatusCode
        Location = $location
        Body = $body
    }
    $response.Dispose()
    return $result
}

$scriptRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
Assert-SafePathSegment -Value $RuntimeIdentifier -Name 'RuntimeIdentifier'
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $scriptRoot "artifacts\publish\$RuntimeIdentifier\orelay.exe"
}
$script:ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $script:ExecutablePath -PathType Leaf)) {
    throw "Native executable was not found: $script:ExecutablePath"
}

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = "$(Get-Date -Format 'yyyyMMdd-HHmmss')-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
}
Assert-SafePathSegment -Value $RunId -Name 'RunId'

$script:EvidenceVerificationRoot = Join-Path $scriptRoot '.artifacts\verification'
$script:WorkVerificationRoot = Join-Path $scriptRoot 'work\verification'
$script:EvidenceRoot = Resolve-ChildPath -Child (Join-Path $script:EvidenceVerificationRoot $RunId) -Parent $script:EvidenceVerificationRoot -Name 'Evidence path'
$script:ScratchRoot = Resolve-ChildPath -Child (Join-Path $script:WorkVerificationRoot $RunId) -Parent $script:WorkVerificationRoot -Name 'Scratch path'
if (Test-Path -LiteralPath $script:EvidenceRoot) {
    throw "Evidence path already exists. Choose a new RunId: $script:EvidenceRoot"
}
if (Test-Path -LiteralPath $script:ScratchRoot) {
    throw "Scratch path already exists. Choose a new RunId: $script:ScratchRoot"
}
New-Item -ItemType Directory -Force -Path $script:EvidenceVerificationRoot, $script:WorkVerificationRoot | Out-Null
New-Item -ItemType Directory -Path $script:EvidenceRoot, $script:ScratchRoot | Out-Null
$script:CommandRecords = [System.Collections.Generic.List[object]]::new()
$script:OwnedServers = [System.Collections.Generic.List[object]]::new()
$script:OwnedListeners = [System.Collections.Generic.List[object]]::new()
$script:HttpClients = [System.Collections.Generic.List[object]]::new()
$failure = $null
$completed = $false

try {
    $publishedFiles = @(Get-ChildItem -LiteralPath ([System.IO.Path]::GetDirectoryName($script:ExecutablePath)) -File |
        Select-Object Name, Length, LastWriteTime)
    $managedFiles = @($publishedFiles | Where-Object {
        $_.Name -match '\.(dll|deps\.json|runtimeconfig\.json)$'
    })
    Assert-True ($managedFiles.Count -eq 0) 'The selected Native AOT directory contains managed runtime files.'
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'native-inventory.json') -Value $publishedFiles
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'native-sha256.json') -Value (Get-FileHash -LiteralPath $script:ExecutablePath -Algorithm SHA256)

    [void](Invoke-Native -Arguments @('--version') -EvidenceName 'cli-version')
    [void](Invoke-Native -Arguments @('--help') -EvidenceName 'cli-help')
    [void](Invoke-Native -Arguments @('invalid-command') -EvidenceName 'cli-invalid-command' -ExpectedExitCode 64)
    [void](Invoke-Native -Arguments @('doctor', '--help') -EvidenceName 'doctor-help')

    $defaultsPath = Join-Path $script:ScratchRoot 'defaults.json'
    [void](Invoke-Native -Arguments @('--config-file', $defaultsPath, 'init') -EvidenceName 'config-defaults-init')
    $defaults = Get-Content -Raw -LiteralPath $defaultsPath | ConvertFrom-Json
    $expectedDefaults = @{
        schemaVersion = 1
        port = 12987
        bind = '127.0.0.1'
        hostname = 'localhost'
        autoDiscovery = 'none'
        leaseSeconds = 300
        maxRegistrations = 1000
        autoUpdate = $false
        autoUpdateIntervalSeconds = 86400
    }
    Assert-Equal @($defaults.PSObject.Properties).Count $expectedDefaults.Count 'Default configuration has unexpected fields.'
    foreach ($entry in $expectedDefaults.GetEnumerator()) {
        Assert-Equal $defaults.($entry.Key) $entry.Value "Unexpected default setting: $($entry.Key)"
    }
    Copy-Item -LiteralPath $defaultsPath -Destination (Join-Path $script:EvidenceRoot 'default-config.json')

    if ($CheckAutoUpdate) {
        $workerServiceName = 'orelay-worker-verify-' + [Guid]::NewGuid().ToString('N')
        [void](Invoke-Native -Arguments @('__auto-update', $defaultsPath, $workerServiceName) -EvidenceName 'auto-update-worker-disabled')
        $workerResultPath = [System.IO.Path]::ChangeExtension($defaultsPath, 'auto-update.json')
        $disabledResult = Get-Content -Raw -LiteralPath $workerResultPath | ConvertFrom-Json
        Assert-Equal $disabledResult.status 'disabled' 'Disabled native worker did not refuse the update.'
        Copy-Item -LiteralPath $workerResultPath -Destination (Join-Path $script:EvidenceRoot 'auto-update-worker-disabled-result.json')
        [void](Invoke-Native -Arguments @('--config-file', $defaultsPath, 'config', 'set', 'autoUpdate', 'true') -EvidenceName 'auto-update-worker-enable')
        [void](Invoke-Native -Arguments @('__auto-update', $defaultsPath, $workerServiceName) -EvidenceName 'auto-update-worker-missing-service' -ExpectedExitCode 3)
        $missingServiceResult = Get-Content -Raw -LiteralPath $workerResultPath | ConvertFrom-Json
        Assert-Equal $missingServiceResult.status 'failed' 'Native worker did not reject the missing service.'
        Copy-Item -LiteralPath $workerResultPath -Destination (Join-Path $script:EvidenceRoot 'auto-update-worker-missing-service-result.json')
    }

    $relayPort = Get-FreePort
    $overridePort = Get-FreePort
    $callbackPortA = Get-FreePort
    $callbackPortB = Get-FreePort
    $configPath = Join-Path $script:ScratchRoot 'orelay.json'
    $missingDoctorConfig = Join-Path $script:ScratchRoot 'missing-doctor.json'
    $relayBase = "http://127.0.0.1:$relayPort"
    $callbackUrlA = "http://127.0.0.1:$callbackPortA/oauth/callback"
    $callbackUrlB = "http://127.0.0.1:$callbackPortB/oauth/callback"

    [void](Invoke-Native -Arguments @('--config-file', $configPath, '--port', [string]$relayPort, '--lease-seconds', '3', '--max-registrations', '8', 'init') -EvidenceName 'config-init')
    Assert-True (Test-Path -LiteralPath $configPath -PathType Leaf) 'Init did not create the run-owned configuration file.'
    $initialConfig = Get-Content -Raw -LiteralPath $configPath
    Set-Content -LiteralPath (Join-Path $script:EvidenceRoot 'config-after-init.json') -Value $initialConfig -Encoding utf8

    [void](Invoke-Native -Arguments @('--config-file', $configPath, 'config', 'get', 'port', '--json') -EvidenceName 'config-get-port-initial')
    [void](Invoke-Native -Arguments @('--config-file', $configPath, 'config', 'set', 'port', [string]$relayPort, '--json') -EvidenceName 'config-set-port')
    $afterSet = Get-Content -Raw -LiteralPath $configPath
    [void](Invoke-Native -Arguments @('--config-file', $configPath, 'config', 'clear', 'port', '--json') -EvidenceName 'config-clear-port')
    $afterClear = Get-Content -Raw -LiteralPath $configPath
    Assert-True ($afterClear -notmatch '"port"') 'Clearing port left a saved port override in the configuration file.'
    $defaultPort = Invoke-Native -Arguments @('--config-file', $configPath, 'config', 'get', 'port', '--json') -EvidenceName 'config-get-port-default'
    $defaultPortJson = $defaultPort.Stdout | ConvertFrom-Json
    Assert-Equal $defaultPortJson.value 12987 'Clearing port did not restore the built-in default.'
    [void](Invoke-Native -Arguments @('--config-file', $configPath, 'config', 'set', 'port', [string]$relayPort, '--json') -EvidenceName 'config-restore-port')

    [void](Invoke-Native -Arguments @('--config-file', $configPath, 'config', 'set', 'autoUpdate', 'true', '--json') -EvidenceName 'config-auto-update-on')
    [void](Invoke-Native -Arguments @('--config-file', $configPath, 'config', 'set', 'autoUpdateIntervalSeconds', '60', '--json') -EvidenceName 'config-auto-update-interval')
    [void](Invoke-Native -Arguments @('--config-file', $configPath, 'config', 'set', 'autoUpdateIntervalSeconds', '59', '--json') -EvidenceName 'config-auto-update-invalid' -ExpectedExitCode 3)
    $autoConfig = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
    Assert-Equal $autoConfig.autoUpdate $true 'Automatic update opt-in was not saved.'
    Assert-Equal $autoConfig.autoUpdateIntervalSeconds 60 'Invalid interval changed the saved setting.'

    $overrideServer = Start-OwnedServer -ConfigPath $configPath -Port $overridePort -EvidencePrefix 'server-override'
    $httpHandler = [System.Net.Http.HttpClientHandler]::new()
    $httpHandler.AllowAutoRedirect = $false
    $http = [System.Net.Http.HttpClient]::new($httpHandler)
    $http.Timeout = [TimeSpan]::FromSeconds(8)
    $script:HttpClients.Add($http)
    $overrideHealth = Wait-ForHealth -Client $http -Url "http://127.0.0.1:$overridePort/health" -Process $overrideServer.Process
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'config-precedence-health.json') -Value ([ordered]@{ port = $overridePort; health = $overrideHealth })
    Stop-OwnedServer -Entry $overrideServer
    $savedAfterOverride = Get-Content -Raw -LiteralPath $configPath
    Assert-True ($savedAfterOverride -match ([regex]::Escape([string]$relayPort))) 'A server invocation override rewrote the saved port.'

    $relayServer = Start-OwnedServer -ConfigPath $configPath -Port $relayPort -EvidencePrefix 'server-relay'
    $health = Wait-ForHealth -Client $http -Url "$relayBase/health" -Process $relayServer.Process
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'health.json') -Value $health

    if ($CheckAutoUpdate) {
        # Keep this longer check opt-in so ordinary callback feedback stays fast.
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        while ($watch.Elapsed.TotalSeconds -lt 65) {
            Start-Sleep -Seconds 1
            Assert-True (-not $relayServer.Process.HasExited) 'Foreground server exited during the auto-update interval.'
        }
        $updateResult = [System.IO.Path]::ChangeExtension($configPath, 'auto-update.json')
        Assert-True (-not (Test-Path -LiteralPath $updateResult)) 'Foreground server ran an automatic update worker.'
        $foregroundHealth = Wait-ForHealth -Client $http -Url "$relayBase/health" -Process $relayServer.Process
        Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'auto-update-foreground.json') -Value ([ordered]@{
            elapsedSeconds = $watch.Elapsed.TotalSeconds
            savedEnabled = $true
            savedIntervalSeconds = 60
            workerResultExists = $false
            health = $foregroundHealth
        })
    }

    $doctorBeforeHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
    $doctorServiceName = 'orelay-verify-' + [Guid]::NewGuid().ToString('N')
    $doctor = Invoke-Native -Arguments @('--config-file', $configPath, 'doctor', '--name', $doctorServiceName, '--json') -EvidenceName 'doctor-readonly'
    Assert-Equal $doctor.ExitCode 0 'Doctor did not report the owned healthy relay.'
    $doctorJson = $doctor.Stdout | ConvertFrom-Json
    Assert-True $doctorJson.healthy 'Doctor reported an unhealthy owned relay.'
    $doctorAfterHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
    Assert-Equal $doctorAfterHash $doctorBeforeHash 'Read-only doctor changed the selected configuration.'

    $listenerA = Start-CallbackListener -Port $callbackPortA
    $listenerB = Start-CallbackListener -Port $callbackPortB
    $registrationTaskA = $http.PostAsync(
        "$relayBase/registrations",
        [System.Net.Http.StringContent]::new((@{ callbackUrl = $callbackUrlA } | ConvertTo-Json -Compress), [System.Text.Encoding]::UTF8, 'application/json'))
    $registrationTaskB = $http.PostAsync(
        "$relayBase/registrations",
        [System.Net.Http.StringContent]::new((@{ callbackUrl = $callbackUrlB } | ConvertTo-Json -Compress), [System.Text.Encoding]::UTF8, 'application/json'))
    Assert-True ([System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($registrationTaskA, $registrationTaskB), 8000)) 'Concurrent registrations did not complete.'
    $registrationResponseA = $registrationTaskA.GetAwaiter().GetResult()
    $registrationResponseB = $registrationTaskB.GetAwaiter().GetResult()
    $registrationBodyA = $registrationResponseA.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $registrationBodyB = $registrationResponseB.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-Equal ([int]$registrationResponseA.StatusCode) 201 'First concurrent registration failed.'
    Assert-Equal ([int]$registrationResponseB.StatusCode) 201 'Second concurrent registration failed.'
    $registrationResponseA.Dispose()
    $registrationResponseB.Dispose()
    $registrationA = $registrationBodyA | ConvertFrom-Json
    $registrationB = $registrationBodyB | ConvertFrom-Json
    Assert-True ($registrationA.id -ne $registrationB.id) 'Concurrent registrations received the same routing ID.'
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'registrations-concurrent.json') -Value ([ordered]@{
        first = $registrationA
        second = $registrationB
        distinctIds = $registrationA.id -ne $registrationB.id
    })

    $rawQueryA = "state=$($registrationA.id).worktree.alpha%20with%20space&code=synthetic%2Fcode%2Balpha&field=one&field=two%20words&empty=&error_description=synthetic%20denial"
    $callbackA = Get-CallbackResponse -Client $http -Url "$relayBase/callback?$rawQueryA"
    Assert-Equal $callbackA.StatusCode 302 'Valid callback A did not redirect.'
    Assert-Equal $callbackA.Location "$($callbackUrlA)?$rawQueryA" 'Callback A did not preserve the exact raw query.'
    $followTaskA = $http.GetAsync($callbackA.Location, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead)
    $requestA = Receive-RawHttpRequest -Listener $listenerA
    Assert-Equal $requestA.Method 'GET' 'Callback listener A did not receive GET.'
    Assert-Equal $requestA.RequestTarget "/oauth/callback?$rawQueryA" 'Callback listener A received a changed raw target.'
    Complete-CallbackRequest -Request $requestA
    $followA = $followTaskA.GetAwaiter().GetResult()
    Assert-Equal ([int]$followA.StatusCode) 204 'Callback destination A did not return its response.'
    $followA.Dispose()

    $rawQueryB = "state=$($registrationB.id).worktree.beta%20with%20space&code=synthetic%2Fcode%2Bbeta&field=one&field=two%20words&empty=&error_description=synthetic%20denial"
    $callbackB = Get-CallbackResponse -Client $http -Url "$relayBase/callback?$rawQueryB"
    Assert-Equal $callbackB.StatusCode 302 'Valid callback B did not redirect.'
    Assert-Equal $callbackB.Location "$($callbackUrlB)?$rawQueryB" 'Callback B did not preserve the exact raw query.'
    $followTaskB = $http.GetAsync($callbackB.Location, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead)
    $requestB = Receive-RawHttpRequest -Listener $listenerB
    Assert-Equal $requestB.RequestTarget "/oauth/callback?$rawQueryB" 'Callback listener B received a changed raw target.'
    Complete-CallbackRequest -Request $requestB
    $followB = $followTaskB.GetAwaiter().GetResult()
    Assert-Equal ([int]$followB.StatusCode) 204 'Callback destination B did not return its response.'
    $followB.Dispose()
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'callbacks-raw-query.json') -Value ([ordered]@{
        first = [ordered]@{ status = $callbackA.StatusCode; location = $callbackA.Location; receivedTarget = "/oauth/callback?$rawQueryA" }
        second = [ordered]@{ status = $callbackB.StatusCode; location = $callbackB.Location; receivedTarget = "/oauth/callback?$rawQueryB" }
    })

    Start-Sleep -Milliseconds 1300
    $renewAResponse = $http.PutAsync("$relayBase/registrations/$($registrationA.id)/lease", $null).GetAwaiter().GetResult()
    $renewABody = $renewAResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-Equal ([int]$renewAResponse.StatusCode) 200 'Renewing live registration A failed.'
    $renewAResponse.Dispose()
    Start-Sleep -Milliseconds 2200
    $expiredB = Get-CallbackResponse -Client $http -Url "$relayBase/callback?state=$($registrationB.id).after-expiry&code=synthetic"
    Assert-Equal $expiredB.StatusCode 404 'Expired registration B still routed a callback.'
    Assert-True ($expiredB.Body -notmatch [regex]::Escape($registrationB.id)) 'Expired registration ID appeared in the error body.'
    $liveA = Get-CallbackResponse -Client $http -Url "$relayBase/callback?state=$($registrationA.id).renewed&code=synthetic"
    Assert-Equal $liveA.StatusCode 302 'Renewed registration A did not remain live.'
    Assert-Equal $liveA.Location "$($callbackUrlA)?state=$($registrationA.id).renewed&code=synthetic" 'Renewed callback A location was not exact.'
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'lease-renewal-expiry.json') -Value ([ordered]@{
        renewedStatus = 200
        expiredStatus = $expiredB.StatusCode
        renewedCallbackStatus = $liveA.StatusCode
        renewalResponse = ($renewABody | ConvertFrom-Json)
    })

    $deleteA = $http.DeleteAsync("$relayBase/registrations/$($registrationA.id)").GetAwaiter().GetResult()
    $deleteAgainA = $http.DeleteAsync("$relayBase/registrations/$($registrationA.id)").GetAwaiter().GetResult()
    Assert-Equal ([int]$deleteA.StatusCode) 204 'Deleting registration A failed.'
    Assert-Equal ([int]$deleteAgainA.StatusCode) 204 'Deleting registration A was not idempotent.'
    $deleteA.Dispose()
    $deleteAgainA.Dispose()
    $deletedA = Get-CallbackResponse -Client $http -Url "$relayBase/callback?state=$($registrationA.id).after-delete&code=synthetic"
    Assert-Equal $deletedA.StatusCode 404 'Deleted registration A still routed a callback.'
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'deregistration.json') -Value ([ordered]@{
        firstDelete = 204
        secondDelete = 204
        callbackAfterDelete = $deletedA.StatusCode
    })

    Stop-OwnedServer -Entry $relayServer

    # Use a separate, longer lease so process startup does not decide this result.
    $persistenceConfig = Join-Path $script:ScratchRoot 'persistence.json'
    $isolatedConfig = Join-Path $script:ScratchRoot 'isolated.json'
    $persistencePort = Get-FreePort
    $isolatedPort = Get-FreePort
    $persistenceBase = "http://127.0.0.1:$persistencePort"
    $isolatedBase = "http://127.0.0.1:$isolatedPort"
    $databasePath = [System.IO.Path]::ChangeExtension($persistenceConfig, 'registrations.db')
    [void](Invoke-Native -Arguments @('--config-file', $persistenceConfig, '--port', [string]$persistencePort, '--lease-seconds', '30', 'init') -EvidenceName 'persistence-init')
    [void](Invoke-Native -Arguments @('--config-file', $isolatedConfig, '--port', [string]$isolatedPort, '--lease-seconds', '30', 'init') -EvidenceName 'isolation-init')
    $persistentServer = Start-OwnedServer -ConfigPath $persistenceConfig -Port $persistencePort -EvidencePrefix 'server-persistence-first'
    [void](Wait-ForHealth -Client $http -Url "$persistenceBase/health" -Process $persistentServer.Process)
    $persistent = Get-Registration -Client $http -RelayBase $persistenceBase -CallbackUrl $callbackUrlA
    $persistentQuery = "state=$($persistent.id).restart&code=synthetic-persistence-code"
    $beforeCrash = Get-CallbackResponse -Client $http -Url "$persistenceBase/callback?$persistentQuery"
    Assert-Equal $beforeCrash.StatusCode 302 'Registration did not route before relay termination.'
    Assert-Equal $beforeCrash.Location "$($callbackUrlA)?$persistentQuery" 'Initial persistent callback location changed.'
    Assert-True (Test-Path -LiteralPath $databasePath -PathType Leaf) 'Register did not create the config-specific database.'
    Stop-OwnedServer -Entry $persistentServer
    $databaseBytes = [System.IO.File]::ReadAllBytes($databasePath)
    $databaseText = [System.Text.Encoding]::UTF8.GetString($databaseBytes)
    Assert-True (-not $databaseText.Contains('synthetic-persistence-code')) 'The database contains a callback authorization code.'
    $persistentServer = Start-OwnedServer -ConfigPath $persistenceConfig -Port $persistencePort -EvidencePrefix 'server-persistence-restart'
    [void](Wait-ForHealth -Client $http -Url "$persistenceBase/health" -Process $persistentServer.Process)
    $afterCrash = Get-CallbackResponse -Client $http -Url "$persistenceBase/callback?$persistentQuery"
    Assert-Equal $afterCrash.StatusCode 302 'Live registration was lost across relay termination.'
    Assert-Equal $afterCrash.Location $beforeCrash.Location 'Restart changed the callback destination or raw query.'
    $renewPersistent = $http.PutAsync("$persistenceBase/registrations/$($persistent.id)/lease", $null).GetAwaiter().GetResult()
    Assert-Equal ([int]$renewPersistent.StatusCode) 200 'Persisted registration could not be renewed.'
    $renewPersistent.Dispose()
    Stop-OwnedServer -Entry $persistentServer
    $persistentServer = Start-OwnedServer -ConfigPath $persistenceConfig -Port $persistencePort -EvidencePrefix 'server-persistence-renewed'
    [void](Wait-ForHealth -Client $http -Url "$persistenceBase/health" -Process $persistentServer.Process)
    $afterRenewRestart = Get-CallbackResponse -Client $http -Url "$persistenceBase/callback?$persistentQuery"
    Assert-Equal $afterRenewRestart.StatusCode 302 'Renewal was lost across relay termination.'
    Assert-Equal $afterRenewRestart.Location $beforeCrash.Location 'Renewal changed callback routing.'

    $isolatedServer = Start-OwnedServer -ConfigPath $isolatedConfig -Port $isolatedPort -EvidencePrefix 'server-isolated'
    [void](Wait-ForHealth -Client $http -Url "$isolatedBase/health" -Process $isolatedServer.Process)
    $isolatedCallback = Get-CallbackResponse -Client $http -Url "$isolatedBase/callback?$persistentQuery"
    Assert-Equal $isolatedCallback.StatusCode 404 'A second config read another config database.'
    Stop-OwnedServer -Entry $isolatedServer
    $deletePersistent = $http.DeleteAsync("$persistenceBase/registrations/$($persistent.id)").GetAwaiter().GetResult()
    Assert-Equal ([int]$deletePersistent.StatusCode) 204 'Persisted registration deletion failed.'
    $deletePersistent.Dispose()
    Stop-OwnedServer -Entry $persistentServer
    $persistentServer = Start-OwnedServer -ConfigPath $persistenceConfig -Port $persistencePort -EvidencePrefix 'server-persistence-deleted'
    [void](Wait-ForHealth -Client $http -Url "$persistenceBase/health" -Process $persistentServer.Process)
    $afterDeleteRestart = Get-CallbackResponse -Client $http -Url "$persistenceBase/callback?$persistentQuery"
    Assert-Equal $afterDeleteRestart.StatusCode 404 'Deleted registration returned after restart.'
    Stop-OwnedServer -Entry $persistentServer
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'persistence-restarts.json') -Value ([ordered]@{
        databasePath = $databasePath
        databaseSha256 = (Get-FileHash -LiteralPath $databasePath -Algorithm SHA256).Hash
        registrationId = $persistent.id
        initialStatus = $beforeCrash.StatusCode
        initialLocation = $beforeCrash.Location
        afterRestartStatus = $afterCrash.StatusCode
        afterRestartLocation = $afterCrash.Location
        renewalStatus = 200
        afterRenewalRestartStatus = $afterRenewRestart.StatusCode
        isolatedConfigStatus = $isolatedCallback.StatusCode
        afterDeleteRestartStatus = $afterDeleteRestart.StatusCode
        containsSyntheticCode = $false
    })

    # Expiry is absolute: time spent with the relay stopped counts against the lease.
    $expiryConfig = Join-Path $script:ScratchRoot 'downtime-expiry.json'
    $expiryPort = Get-FreePort
    $expiryBase = "http://127.0.0.1:$expiryPort"
    [void](Invoke-Native -Arguments @('--config-file', $expiryConfig, '--port', [string]$expiryPort, '--lease-seconds', '2', 'init') -EvidenceName 'downtime-expiry-init')
    $expiryServer = Start-OwnedServer -ConfigPath $expiryConfig -Port $expiryPort -EvidencePrefix 'server-expiry-before-downtime'
    [void](Wait-ForHealth -Client $http -Url "$expiryBase/health" -Process $expiryServer.Process)
    $expiring = Get-Registration -Client $http -RelayBase $expiryBase -CallbackUrl $callbackUrlB
    Stop-OwnedServer -Entry $expiryServer
    Start-Sleep -Milliseconds 2400
    $expiryServer = Start-OwnedServer -ConfigPath $expiryConfig -Port $expiryPort -EvidencePrefix 'server-expiry-after-downtime'
    [void](Wait-ForHealth -Client $http -Url "$expiryBase/health" -Process $expiryServer.Process)
    $expiredDuringDowntime = Get-CallbackResponse -Client $http -Url "$expiryBase/callback?state=$($expiring.id).downtime&code=synthetic"
    Assert-Equal $expiredDuringDowntime.StatusCode 404 'Relay restart revived an expired registration.'
    $expiredRenewal = $http.PutAsync("$expiryBase/registrations/$($expiring.id)/lease", $null).GetAwaiter().GetResult()
    Assert-Equal ([int]$expiredRenewal.StatusCode) 404 'An expired registration renewed after downtime.'
    $expiredRenewal.Dispose()
    Stop-OwnedServer -Entry $expiryServer
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'persistence-downtime-expiry.json') -Value ([ordered]@{
        registrationId = $expiring.id
        leaseSeconds = 2
        downtimeMilliseconds = 2400
        callbackStatus = $expiredDuringDowntime.StatusCode
        renewalStatus = 404
    })

    $missingDoctor = Invoke-Native -Arguments @('--config-file', $missingDoctorConfig, 'doctor', '--json') -EvidenceName 'doctor-readonly-missing' -ExpectedExitCode 1
    Assert-True (-not (Test-Path -LiteralPath $missingDoctorConfig)) 'Read-only doctor created a missing configuration file.'
    $completed = $true
}
catch {
    $failure = [ordered]@{
        message = $_.Exception.Message
        type = $_.Exception.GetType().FullName
        stack = $_.ScriptStackTrace
    }
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'failure.json') -Value $failure
}
finally {
    foreach ($server in @($script:OwnedServers | Sort-Object Pid -Descending)) {
        try {
            if (-not $server.Process.HasExited) {
                Stop-OwnedServer -Entry $server
            }
            $server.Process.Dispose()
        }
        catch {
            Write-JsonFile -Path (Join-Path $script:EvidenceRoot "cleanup-server-$($server.Pid)-error.json") -Value ([ordered]@{ message = $_.Exception.Message })
        }
    }

    foreach ($listener in $script:OwnedListeners) {
        try {
            $listener.Stop()
            $listener.Dispose()
        }
        catch {
        }
    }

    foreach ($client in $script:HttpClients) {
        try {
            $client.Dispose()
        }
        catch {
        }
    }

    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'commands.json') -Value @($script:CommandRecords)
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'cleanup.json') -Value ([ordered]@{
        runId = $RunId
        completed = $completed
        evidenceRetained = Test-Path -LiteralPath $script:EvidenceRoot
        scratchPath = $script:ScratchRoot
        ownedProcessIds = @($script:OwnedServers | ForEach-Object { $_.Pid })
        ownedListenerCount = $script:OwnedListeners.Count
    })

    $safeScratchRoot = Resolve-ChildPath -Child $script:ScratchRoot -Parent $script:WorkVerificationRoot -Name 'Scratch path'
    if (Test-Path -LiteralPath $safeScratchRoot) {
        Remove-Item -LiteralPath $safeScratchRoot -Recurse -Force
    }
}

if ($null -ne $failure) {
    throw $failure.message
}

Write-Output "ORelay verification passed. Evidence: $script:EvidenceRoot"
