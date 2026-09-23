[CmdletBinding()]
param(
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$ExecutablePath = '',
    [string]$RunId = ''
)

$ErrorActionPreference = 'Stop'

function Write-JsonFile {
    param([string]$Path, $Value)
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Write-Evidence {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)]$Value)
    $script:EventNumber++
    Write-JsonFile -Path (Join-Path $script:EvidenceRoot ('{0:D3}-{1}.json' -f $script:EventNumber, $Name)) -Value ([ordered]@{
        utc = [DateTimeOffset]::UtcNow.ToString('O')
        name = $Name
        result = $Value
    })
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Equal {
    param($Actual, $Expected, [string]$Message)
    if ($Actual -cne $Expected) { throw "$Message Expected '$Expected', got '$Actual'." }
}

function Assert-SafeSegment {
    param([string]$Value, [string]$Name)
    if ($Value -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$') {
        throw "$Name must contain only letters, numbers, hyphens, and underscores, and must be 1 to 64 characters long."
    }
}

function Resolve-ChildPath {
    param([string]$Child, [string]$Parent, [string]$Name)
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $childFull = [System.IO.Path]::GetFullPath($Child)
    $comparison = if ([System.OperatingSystem]::IsWindows()) { [System.StringComparison]::OrdinalIgnoreCase } else { [System.StringComparison]::Ordinal }
    if ($childFull.Equals($parentFull, $comparison) -or
        -not $childFull.StartsWith($parentFull + [System.IO.Path]::DirectorySeparatorChar, $comparison)) {
        throw "$Name '$childFull' is not a child of '$parentFull'."
    }
    return $childFull
}

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
        $listener.Dispose()
    }
}

function New-HttpClient {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(4)
    $script:HttpClients.Add($client)
    return $client
}

function Invoke-Http {
    param(
        [System.Net.Http.HttpClient]$Client,
        [string]$Method,
        [string]$Url,
        [string]$Body,
        [string]$EvidenceName
    )
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $Url)
    if ($null -ne $Body) {
        $request.Content = [System.Net.Http.StringContent]::new($Body, [System.Text.Encoding]::UTF8, 'application/json')
    }
    try {
        $response = $Client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $location = if ($null -ne $response.Headers.Location) { $response.Headers.Location.OriginalString } else { $null }
            $result = [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $text; Location = $location }
            Write-Evidence -Name $EvidenceName -Value ([ordered]@{ method = $Method; url = $Url; status = $result.Status; location = $location; body = $text })
            return $result
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

function Start-Relay {
    param([string]$ConfigPath, [string]$Name, [int]$PortOverride = 0)
    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add('--config-file'); $arguments.Add($ConfigPath)
    if ($PortOverride -gt 0) { $arguments.Add('--port'); $arguments.Add([string]$PortOverride) }
    $arguments.Add('server')

    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $script:ExecutablePath
    $info.WorkingDirectory = $script:ScratchRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $arguments) { [void]$info.ArgumentList.Add($argument) }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Could not start the owned relay process '$Name'." }
    $entry = [pscustomobject]@{
        Name = $Name
        Process = $process
        Pid = $process.Id
        ConfigPath = $ConfigPath
        StdoutTask = $process.StandardOutput.ReadToEndAsync()
        StderrTask = $process.StandardError.ReadToEndAsync()
    }
    $script:OwnedRelays.Add($entry)
    Write-Evidence -Name "$Name-start" -Value ([ordered]@{ command = @('orelay') + @($arguments); pid = $process.Id; configPath = $ConfigPath })
    return $entry
}

function Read-ProcessOutput {
    param([System.Threading.Tasks.Task[string]]$Task)
    if (-not $Task.Wait(5000)) { return '[output capture timed out]' }
    return $Task.GetAwaiter().GetResult()
}

function Wait-Health {
    param([System.Net.Http.HttpClient]$Client, [string]$Url, $Relay, [int]$TimeoutSeconds = 30)
    $health = $null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($Relay.Process.HasExited) { throw "Owned relay $($Relay.Pid) exited with code $($Relay.Process.ExitCode) before health at $Url." }
        try {
            $result = Invoke-Http -Client $Client -Method 'GET' -Url "$Url/health" -Body $null -EvidenceName 'health-probe'
            if ($result.Status -eq 200) {
                $health = $result.Body | ConvertFrom-Json
                if ($health.identity -eq 'orelay' -and $health.status -eq 'ok') { break }
            }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $health -or $health.identity -ne 'orelay' -or $health.status -ne 'ok') {
        throw "Owned relay $($Relay.Pid) did not report the expected health identity at $Url within $TimeoutSeconds seconds."
    }
    $target = [Uri]$Url
    [void](& "$PSScriptRoot/doctor.ps1" -ExecutablePath $script:ExecutablePath -ConfigPath $Relay.ConfigPath `
        -ListenerPort $target.Port -ListenerHostname '127.0.0.1' `
        -EvidencePath (Join-Path $script:EvidenceRoot "doctor-ready-$($Relay.Pid)-$([Guid]::NewGuid().ToString('N').Substring(0, 8)).json"))
    return $health
}

function Read-Config {
    param([string]$Path)
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable)
}

function Save-Config {
    param([string]$Path, $Config, [string]$Reason)
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    $json = $Config | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($temporary, $json, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::Move($temporary, $Path, $true)
    Write-Evidence -Name 'configuration-write' -Value ([ordered]@{
        reason = $Reason
        path = $Path
        replacement = 'same-directory atomic file replacement'
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        settings = $Config
    })
}

function Write-DirectConfig {
    param([string]$Path, $Config, [string]$Reason)
    $json = $Config | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
    Write-Evidence -Name 'configuration-direct-write' -Value ([ordered]@{
        reason = $Reason
        path = $Path
        replacement = 'direct file write'
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        settings = $Config
    })
}

function New-Config {
    param([int]$Port, [string]$Hostname, [int]$LeaseSeconds = 300, [int]$MaxRegistrations = 1000)
    return [ordered]@{
        schemaVersion = 1
        port = $Port
        bind = '127.0.0.1'
        publicUrl = $null
        hostname = $Hostname
        autoDiscovery = 'none'
        leaseSeconds = $LeaseSeconds
        maxRegistrations = $MaxRegistrations
        autoUpdate = $false
        autoUpdateIntervalSeconds = 86400
    }
}

function Get-BaseUrl {
    param([int]$Port, [string]$Bind = '127.0.0.1')
    return "http://${Bind}:$Port"
}

function New-Registration {
    param([System.Net.Http.HttpClient]$Client, [string]$BaseUrl, [int]$CallbackPort, [int]$ExpectedStatus = 201)
    $callback = "http://127.0.0.1:${CallbackPort}/return"
    $body = @{ callbackUrl = $callback } | ConvertTo-Json -Compress
    $result = Invoke-Http -Client $Client -Method 'POST' -Url "$BaseUrl/registrations" -Body $body -EvidenceName 'registration-create'
    Assert-Equal $result.Status $ExpectedStatus 'Unexpected registration response.'
    if ($ExpectedStatus -ne 201) { return $result }
    return ($result.Body | ConvertFrom-Json)
}

function Renew-Registration {
    param([System.Net.Http.HttpClient]$Client, [string]$BaseUrl, [string]$Id, [int]$ExpectedStatus = 200)
    $result = Invoke-Http -Client $Client -Method 'PUT' -Url "$BaseUrl/registrations/$Id/lease" -Body $null -EvidenceName 'registration-renew'
    Assert-Equal $result.Status $ExpectedStatus 'Unexpected registration renewal response.'
    if ($ExpectedStatus -ne 200) { return $result }
    return ($result.Body | ConvertFrom-Json)
}

function Wait-RenewedSettings {
    param(
        [System.Net.Http.HttpClient]$Client,
        [string]$BaseUrl,
        [string]$Id,
        [string]$ExpectedCallback,
        [int]$ExpectedLease,
        [int]$TimeoutSeconds = 12
    )
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $last = Renew-Registration -Client $Client -BaseUrl $BaseUrl -Id $Id
        if ($last.relayCallbackUrl -eq $ExpectedCallback -and $last.leaseSeconds -eq $ExpectedLease) { return $last }
        Start-Sleep -Milliseconds 250
    }
    throw "Configuration was not reflected in renewal within $TimeoutSeconds seconds. Last callback '$($last.relayCallbackUrl)', lease $($last.leaseSeconds)."
}

function Start-OwnedListener {
    param([string]$Name, [string]$Address = '127.0.0.1')
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse($Address), 0)
    $listener.Start()
    $entry = [pscustomobject]@{ Name = $Name; Listener = $listener; Port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port; Stopped = $false }
    $script:OwnedListeners.Add($entry)
    Write-Evidence -Name "$Name-listener-start" -Value ([ordered]@{ address = $Address; port = $entry.Port })
    return $entry
}

function Receive-Callback {
    param([System.Net.Sockets.TcpListener]$Listener, [int]$TimeoutMilliseconds = 5000)
    $listenerTask = $Listener.AcceptTcpClientAsync()
    if (-not $listenerTask.Wait($TimeoutMilliseconds)) { throw "Owned callback listener did not receive a request within $TimeoutMilliseconds ms." }
    $client = $listenerTask.Result
    try {
        $stream = $client.GetStream()
        $stream.ReadTimeout = $TimeoutMilliseconds
        $memory = [System.IO.MemoryStream]::new()
        try {
            $buffer = [byte[]]::new(4096)
            do {
                $read = $stream.Read($buffer, 0, $buffer.Length)
                if ($read -gt 0) { $memory.Write($buffer, 0, $read) }
                $text = [System.Text.Encoding]::ASCII.GetString($memory.ToArray())
            } while ($read -gt 0 -and -not $text.Contains("`r`n`r`n"))
            $line = ($text -split "`r`n", 2)[0]
            if ($line -notmatch '^(?<method>\S+) (?<target>\S+) HTTP/1\.1$') { throw 'Owned callback listener received an invalid HTTP request line.' }
            $response = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 204 No Content`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
            $stream.Write($response, 0, $response.Length)
            $stream.Flush()
            return [pscustomobject]@{ Method = $Matches.method; Target = $Matches.target }
        }
        finally { $memory.Dispose(); $stream.Dispose() }
    }
    finally { $client.Dispose() }
}

function Invoke-NativeConfigSet {
    param([string]$ConfigPath, [string]$Key, [string]$Value, [string]$Name)
    $arguments = @('--config-file', $ConfigPath, 'config', 'set', $Key, $Value, '--json')
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $script:ExecutablePath
    $info.WorkingDirectory = $script:ScratchRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $arguments) { [void]$info.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Could not start CLI config set for '$Key'." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(15000)) {
        $process.Kill($true)
        [void]$process.WaitForExit(5000)
        throw "CLI config set '$Key' timed out."
    }
    $record = [ordered]@{
        command = @('orelay') + $arguments
        exitCode = $process.ExitCode
        stdout = $stdoutTask.GetAwaiter().GetResult()
        stderr = $stderrTask.GetAwaiter().GetResult()
    }
    $process.Dispose()
    Write-Evidence -Name $Name -Value $record
    Assert-Equal $record.exitCode 0 "CLI config set '$Key' failed. See evidence."
}

function Stop-OwnedRelay {
    param($Relay)
    $process = $Relay.Process
    $wasRunning = -not $process.HasExited
    if ($wasRunning) {
        try { $process.Kill($true) } catch [System.InvalidOperationException] { }
        [void]$process.WaitForExit(10000)
    }
    $stdout = Read-ProcessOutput -Task $Relay.StdoutTask
    $stderr = Read-ProcessOutput -Task $Relay.StderrTask
    Set-Content -LiteralPath (Join-Path $script:EvidenceRoot "$($Relay.Name).stdout.txt") -Value $stdout -Encoding utf8
    Set-Content -LiteralPath (Join-Path $script:EvidenceRoot "$($Relay.Name).stderr.txt") -Value $stderr -Encoding utf8
    Write-Evidence -Name "$($Relay.Name)-cleanup" -Value ([ordered]@{
        pid = $Relay.Pid
        wasRunningAtCleanup = $wasRunning
        exited = $process.HasExited
        exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
    })
}

$scriptRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
Assert-SafeSegment $RuntimeIdentifier 'RuntimeIdentifier'
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $executableName = if ([System.OperatingSystem]::IsWindows()) { 'orelay.exe' } else { 'orelay' }
    $ExecutablePath = Join-Path $scriptRoot "artifacts\publish\$RuntimeIdentifier\$executableName"
}
if (-not [System.IO.Path]::IsPathFullyQualified($ExecutablePath)) { throw 'ExecutablePath must be absolute.' }
$script:ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $script:ExecutablePath -PathType Leaf)) { throw "Native executable was not found: $script:ExecutablePath" }
if ([string]::IsNullOrWhiteSpace($RunId)) { $RunId = "reload-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$([Guid]::NewGuid().ToString('N').Substring(0, 8))" }
Assert-SafeSegment $RunId 'RunId'
$hostSuffix = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($RunId))).Substring(0, 12).ToLowerInvariant()

$script:EvidenceVerificationRoot = Join-Path $scriptRoot '.artifacts\verification'
$script:WorkVerificationRoot = Join-Path $scriptRoot 'work\verification'
$script:EvidenceRoot = Resolve-ChildPath -Child (Join-Path $script:EvidenceVerificationRoot $RunId) -Parent $script:EvidenceVerificationRoot -Name 'Evidence path'
$script:ScratchRoot = Resolve-ChildPath -Child (Join-Path $script:WorkVerificationRoot $RunId) -Parent $script:WorkVerificationRoot -Name 'Scratch path'
if (Test-Path -LiteralPath $script:EvidenceRoot) { throw "Evidence path already exists. Choose a new RunId: $script:EvidenceRoot" }
    if (Test-Path -LiteralPath $script:ScratchRoot) { throw "Scratch path already exists. Choose a new RunId: $script:ScratchRoot" }
New-Item -ItemType Directory -Force -Path $script:EvidenceVerificationRoot, $script:WorkVerificationRoot | Out-Null
New-Item -ItemType Directory -Path $script:EvidenceRoot, $script:ScratchRoot | Out-Null
$script:OwnedRelays = [System.Collections.Generic.List[object]]::new()
$script:OwnedListeners = [System.Collections.Generic.List[object]]::new()
$script:HttpClients = [System.Collections.Generic.List[object]]::new()
$script:EventNumber = 0
$failure = $null
$success = $false

try {
    Write-Evidence -Name 'native-artifact' -Value ([ordered]@{ path = $script:ExecutablePath; sha256 = (Get-FileHash -LiteralPath $script:ExecutablePath -Algorithm SHA256).Hash; length = (Get-Item -LiteralPath $script:ExecutablePath).Length })
    $mainPort = Get-FreePort
    $mainConfigPath = Join-Path $script:ScratchRoot 'main.json'
    $mainConfig = New-Config -Port $mainPort -Hostname "before-${hostSuffix}.test" -LeaseSeconds 10 -MaxRegistrations 10
    Save-Config -Path $mainConfigPath -Config $mainConfig -Reason 'initial run-owned settings'
    $client = New-HttpClient
    $relay = Start-Relay -ConfigPath $mainConfigPath -Name 'main-relay'
    $null = Wait-Health -Client $client -Url (Get-BaseUrl $mainPort) -Relay $relay

    # Two live IDs distinguish a renewal that uses the new lease from an
    # untouched ID whose original absolute expiry must remain unchanged.
    $callbackA = Get-FreePort
    $callbackB = Get-FreePort
    $expiringRegistration = New-Registration -Client $client -BaseUrl (Get-BaseUrl $mainPort) -CallbackPort $callbackA
    $expiryId = $expiringRegistration.id
    $expiryAt = [DateTimeOffset]$expiringRegistration.expiresAt
    $renewId = (New-Registration -Client $client -BaseUrl (Get-BaseUrl $mainPort) -CallbackPort $callbackB).id
    $directHost = "direct-${hostSuffix}.test"
    $mainConfig = Read-Config $mainConfigPath
    $mainConfig.hostname = $directHost
    $mainConfig.leaseSeconds = 60
    $mainConfig.maxRegistrations = 2
    Write-DirectConfig -Path $mainConfigPath -Config $mainConfig -Reason 'direct edit updates hostname, lease, and capacity'
    $directCallback = "http://${directHost}:$mainPort/callback"
    $directRenew = Wait-RenewedSettings -Client $client -BaseUrl (Get-BaseUrl $mainPort) -Id $renewId -ExpectedCallback $directCallback -ExpectedLease 60
    Write-Evidence -Name 'direct-edit-live-policy' -Value ([ordered]@{ pid = $relay.Pid; renewedId = $renewId; leaseSeconds = $directRenew.leaseSeconds; expiresAt = $directRenew.expiresAt; callback = $directRenew.relayCallbackUrl; capacity = 2 })
    $blocked = New-Registration -Client $client -BaseUrl (Get-BaseUrl $mainPort) -CallbackPort (Get-FreePort) -ExpectedStatus 503
    Write-Evidence -Name 'lowered-capacity-blocks-new-registration' -Value ([ordered]@{ status = $blocked.Status; existingIds = @($expiryId, $renewId); limit = 2 })

    # CLI config set writes through the product's atomic replacement path.
    Invoke-NativeConfigSet -ConfigPath $mainConfigPath -Key 'hostname' -Value "cli-${hostSuffix}.test" -Name 'cli-config-set'
    $cliHost = "cli-${hostSuffix}.test"
    $cliCallback = "http://${cliHost}:$mainPort/callback"
    $null = Wait-RenewedSettings -Client $client -BaseUrl (Get-BaseUrl $mainPort) -Id $renewId -ExpectedCallback $cliCallback -ExpectedLease 60

    # A malformed file leaves the last valid runtime policy active. Repair it
    # through a run-owned atomic write and wait for the new advertised URL.
    [System.IO.File]::WriteAllText($mainConfigPath, '{ invalid json', [System.Text.UTF8Encoding]::new($false))
    Write-Evidence -Name 'invalid-json-written' -Value ([ordered]@{ path = $mainConfigPath; sha256 = (Get-FileHash -LiteralPath $mainConfigPath -Algorithm SHA256).Hash })
    Start-Sleep -Seconds 3
    [void](& "$PSScriptRoot/doctor.ps1" -ExecutablePath $script:ExecutablePath -ConfigPath $mainConfigPath `
        -ExpectedExitCode 1 -EvidencePath (Join-Path $script:EvidenceRoot 'doctor-invalid-config.json'))
    $invalidHealth = Invoke-Http -Client $client -Method 'GET' -Url "$(Get-BaseUrl $mainPort)/health" -Body $null -EvidenceName 'health-with-invalid-config'
    Assert-Equal $invalidHealth.Status 200 'Invalid configuration stopped the active relay.'
    Assert-Equal (($invalidHealth.Body | ConvertFrom-Json).identity) 'orelay' 'Active listener lost its identity after an invalid edit.'
    $invalidRenew = Renew-Registration -Client $client -BaseUrl (Get-BaseUrl $mainPort) -Id $renewId
    Assert-Equal $invalidRenew.relayCallbackUrl $cliCallback 'Malformed JSON changed the active callback URL.'
    Assert-Equal $invalidRenew.leaseSeconds 60 'Malformed JSON changed the active lease policy.'
    $mainConfig = New-Config -Port $mainPort -Hostname "repaired-${hostSuffix}.test" -LeaseSeconds 45 -MaxRegistrations 2
    Save-Config -Path $mainConfigPath -Config $mainConfig -Reason 'repair malformed settings with valid values'
    $repairedHost = "repaired-${hostSuffix}.test"
    $repairedCallback = "http://${repairedHost}:$mainPort/callback"
    $null = Wait-RenewedSettings -Client $client -BaseUrl (Get-BaseUrl $mainPort) -Id $renewId -ExpectedCallback $repairedCallback -ExpectedLease 45

    # The older unrenewed ID must expire at its first advertised expiry even
    # though the active lease duration is now 45 seconds.
    while ([DateTimeOffset]::UtcNow -lt $expiryAt) { Start-Sleep -Milliseconds 150 }
    $expired = Renew-Registration -Client $client -BaseUrl (Get-BaseUrl $mainPort) -Id $expiryId -ExpectedStatus 404
    Write-Evidence -Name 'original-expiry-preserved' -Value ([ordered]@{ id = $expiryId; originalExpiryUtc = $expiryAt.ToString('O'); renewStatus = $expired.Status })
    $callbackListener = Start-OwnedListener -Name 'public-url-callback'
    $newRegistration = New-Registration -Client $client -BaseUrl (Get-BaseUrl $mainPort) -CallbackPort $callbackListener.Port
    Assert-Equal $newRegistration.leaseSeconds 45 'New registration did not use the reloaded lease.'
    Assert-Equal $newRegistration.relayCallbackUrl $repairedCallback 'New registration did not use the reloaded hostname.'
    Write-Evidence -Name 'new-registration-after-reload' -Value $newRegistration
    $callbackForRoute = $newRegistration.callbackUrl

    # Port changes and then a bind change on that port must retain the same
    # process and the already issued registration ID.
    $portAfterReload = Get-FreePort
    $mainConfig = Read-Config $mainConfigPath
    $mainConfig.port = $portAfterReload
    $portHost = "port-${hostSuffix}.test"
    $mainConfig.hostname = $portHost
    Save-Config -Path $mainConfigPath -Config $mainConfig -Reason 'change listener port'
    $null = Wait-Health -Client $client -Url (Get-BaseUrl $portAfterReload) -Relay $relay -TimeoutSeconds 12
    Assert-Equal $relay.Process.Id $relay.Pid 'Relay process identity changed during port reload.'
    $portRenew = Wait-RenewedSettings -Client $client -BaseUrl (Get-BaseUrl $portAfterReload) -Id $newRegistration.id -ExpectedCallback "http://${portHost}:${portAfterReload}/callback" -ExpectedLease 45
    Assert-Equal $portRenew.id $newRegistration.id 'Registration ID changed during port reload.'

    $mainConfig = Read-Config $mainConfigPath
    $mainConfig.bind = 'localhost'
    $bindHost = "bind-${hostSuffix}.test"
    $mainConfig.hostname = $bindHost
    Save-Config -Path $mainConfigPath -Config $mainConfig -Reason 'change bind address while retaining listener port'
    $null = Wait-Health -Client $client -Url (Get-BaseUrl $portAfterReload 'localhost') -Relay $relay -TimeoutSeconds 12
    Assert-Equal $relay.Process.Id $relay.Pid 'Relay process identity changed during bind reload.'
    $bindRenew = Wait-RenewedSettings -Client $client -BaseUrl (Get-BaseUrl $portAfterReload 'localhost') -Id $newRegistration.id -ExpectedCallback "http://${bindHost}:${portAfterReload}/callback" -ExpectedLease 45
    Assert-Equal $bindRenew.id $newRegistration.id 'Registration was lost during bind reload.'
    Write-Evidence -Name 'port-and-bind-reload-retained-state' -Value ([ordered]@{ pid = $relay.Pid; id = $bindRenew.id; port = $portAfterReload; bind = 'localhost'; callback = $bindRenew.relayCallbackUrl })

    # Occupy the destination on the active bind. A failed paired port/hostname
    # update must restore the old address and leave the old advertisement.
    $occupied = Start-OwnedListener -Name 'occupied-target-port' -Address '127.0.0.1'
    $mainConfig = Read-Config $mainConfigPath
    $mainConfig.port = $occupied.Port
    $mainConfig.bind = '127.0.0.1'
    $mainConfig.hostname = "rejected-${hostSuffix}.test"
    Save-Config -Path $mainConfigPath -Config $mainConfig -Reason 'request occupied port with a hostname change'
    Start-Sleep -Seconds 11
    $null = Wait-Health -Client $client -Url (Get-BaseUrl $portAfterReload 'localhost') -Relay $relay
    $oldHealth = Invoke-Http -Client $client -Method 'GET' -Url "$(Get-BaseUrl $portAfterReload 'localhost')/health" -Body $null -EvidenceName 'health-after-rejected-listener'
    Assert-Equal $oldHealth.Status 200 'Old listener did not recover after rejecting an occupied port.'
    Assert-Equal $relay.Process.Id $relay.Pid 'Relay process identity changed after listener rejection.'
    $rejectedRenew = Renew-Registration -Client $client -BaseUrl (Get-BaseUrl $portAfterReload 'localhost') -Id $newRegistration.id
    Assert-Equal $rejectedRenew.relayCallbackUrl $bindRenew.relayCallbackUrl 'Hostname changed even though listener application failed.'
    Write-Evidence -Name 'occupied-listener-restored-old-state' -Value ([ordered]@{ pid = $relay.Pid; oldPort = $portAfterReload; targetPort = $occupied.Port; healthStatus = $oldHealth.Status; callback = $rejectedRenew.relayCallbackUrl })

    $occupied.Listener.Stop()
    $occupied.Listener.Dispose()
    $occupied.Stopped = $true
    $targetRecovered = $occupied.Port
    $mainConfig = New-Config -Port $targetRecovered -Hostname "recovered-${hostSuffix}.test" -LeaseSeconds 45 -MaxRegistrations 2
    $mainConfig.bind = 'localhost'
    Save-Config -Path $mainConfigPath -Config $mainConfig -Reason 'correct listener configuration after releasing owned port'
    $null = Wait-Health -Client $client -Url (Get-BaseUrl $targetRecovered 'localhost') -Relay $relay -TimeoutSeconds 12
    $recoveredRenew = Renew-Registration -Client $client -BaseUrl (Get-BaseUrl $targetRecovered 'localhost') -Id $newRegistration.id
    Assert-Equal $recoveredRenew.relayCallbackUrl "http://recovered-${hostSuffix}.test:${targetRecovered}/callback" 'Corrected listener configuration did not apply.'

    # Removing the selected file keeps the last valid settings. Recreating it
    # with a new hostname must wake the same process and apply that file.
    $heldPath = Join-Path $script:ScratchRoot 'held.json'
    [System.IO.File]::Move($mainConfigPath, $heldPath)
    Write-Evidence -Name 'configuration-file-removed' -Value ([ordered]@{ path = $mainConfigPath; heldPath = $heldPath })
    Start-Sleep -Seconds 3
    [void](& "$PSScriptRoot/doctor.ps1" -ExecutablePath $script:ExecutablePath -ConfigPath $mainConfigPath `
        -ExpectedExitCode 1 -EvidencePath (Join-Path $script:EvidenceRoot 'doctor-missing-config.json'))
    $missingHealth = Invoke-Http -Client $client -Method 'GET' -Url "$(Get-BaseUrl $targetRecovered 'localhost')/health" -Body $null -EvidenceName 'health-with-missing-config'
    Assert-Equal $missingHealth.Status 200 'Missing configuration stopped the active relay.'
    $missingRenew = Renew-Registration -Client $client -BaseUrl (Get-BaseUrl $targetRecovered 'localhost') -Id $newRegistration.id
    Assert-Equal $missingRenew.relayCallbackUrl $recoveredRenew.relayCallbackUrl 'Missing configuration changed active settings.'
    $heldConfig = Read-Config $heldPath
    $heldConfig.hostname = "restored-${hostSuffix}.test"
    Save-Config -Path $heldPath -Config $heldConfig -Reason 'prepare restored configuration while selected path is missing'
    [System.IO.File]::Move($heldPath, $mainConfigPath)
    $restoredCallback = "http://restored-${hostSuffix}.test:${targetRecovered}/callback"
    $restored = Wait-RenewedSettings -Client $client -BaseUrl (Get-BaseUrl $targetRecovered 'localhost') -Id $newRegistration.id -ExpectedCallback $restoredCallback -ExpectedLease 45
    Write-Evidence -Name 'restored-file-reapplied' -Value ([ordered]@{ pid = $relay.Pid; callback = $restored.relayCallbackUrl; id = $restored.id })

    # CLI port precedence is checked on a separate owned process and config.
    $overrideConfigPort = Get-FreePort
    $overridePort = Get-FreePort
    $overrideConfigPath = Join-Path $script:ScratchRoot 'override.json'
    Save-Config -Path $overrideConfigPath -Config (New-Config -Port $overrideConfigPort -Hostname "override-before-${hostSuffix}.test") -Reason 'initial config for command line override'
    $overrideRelay = Start-Relay -ConfigPath $overrideConfigPath -Name 'override-relay' -PortOverride $overridePort
    $null = Wait-Health -Client $client -Url (Get-BaseUrl $overridePort) -Relay $overrideRelay
    $overrideConfig = Read-Config $overrideConfigPath
    $overrideConfig.port = Get-FreePort
    $overrideConfig.hostname = "override-after-${hostSuffix}.test"
    Save-Config -Path $overrideConfigPath -Config $overrideConfig -Reason 'edit saved port and hostname beneath command line port override'
    $overrideId = (New-Registration -Client $client -BaseUrl (Get-BaseUrl $overridePort) -CallbackPort (Get-FreePort)).id
    $overrideCallback = "http://override-after-${hostSuffix}.test:${overridePort}/callback"
    $overrideRenew = Wait-RenewedSettings -Client $client -BaseUrl (Get-BaseUrl $overridePort) -Id $overrideId -ExpectedCallback $overrideCallback -ExpectedLease 300
    $savedPort = (Read-Config $overrideConfigPath).port
    Assert-Equal $savedPort $overrideConfig.port 'Starting with --port rewrote the saved port.'
    Assert-Equal $overrideRelay.Process.Id $overrideRelay.Pid 'Command line override process was restarted during configuration reload.'
    Write-Evidence -Name 'command-line-port-precedence-retained' -Value ([ordered]@{ pid = $overrideRelay.Pid; listenerPort = $overridePort; savedPort = $savedPort; callback = $overrideRenew.relayCallbackUrl })

    # Updating publicUrl changes the callback route path as well as the
    # advertised URL. Exercise the route and raw query to a run-owned listener.
    $publicPath = "/reload-$RunId/oauth"
    $publicUrl = "http://localhost:${targetRecovered}${publicPath}"
    $mainConfig = Read-Config $mainConfigPath
    $mainConfig.publicUrl = $publicUrl
    Save-Config -Path $mainConfigPath -Config $mainConfig -Reason 'change public URL base path'
    $publicCallback = "$publicUrl/callback"
    $null = Wait-RenewedSettings -Client $client -BaseUrl (Get-BaseUrl $targetRecovered 'localhost') -Id $newRegistration.id -ExpectedCallback $publicCallback -ExpectedLease 45
    $oldRoute = Invoke-Http -Client $client -Method 'GET' -Url "$(Get-BaseUrl $targetRecovered 'localhost')/callback?state=$($newRegistration.id).worktree-state&code=synthetic-reload-code" -Body $null -EvidenceName 'old-callback-route-after-public-url-change'
    Assert-Equal $oldRoute.Status 404 'Old callback route remained active after the public URL path changed.'
    $routeUrl = "$(Get-BaseUrl $targetRecovered 'localhost')$publicPath/callback?state=$($newRegistration.id).worktree-state&code=synthetic-reload-code"
    $redirect = Invoke-Http -Client $client -Method 'GET' -Url $routeUrl -Body $null -EvidenceName 'updated-public-url-route'
    Assert-True ($redirect.Status -in @(302, 307, 308)) 'Updated public URL path did not route the callback.'
    $expectedLocation = "$callbackForRoute`?state=$($newRegistration.id).worktree-state&code=synthetic-reload-code"
    Assert-Equal $redirect.Location $expectedLocation 'Updated callback route redirected to an unexpected destination.'
    $followRequest = $client.GetAsync($redirect.Location)
    $received = Receive-Callback -Listener $callbackListener.Listener
    $followResponse = $followRequest.GetAwaiter().GetResult()
    $followStatus = [int]$followResponse.StatusCode
    $followResponse.Dispose()
    Assert-Equal $followStatus 204 'Following the callback redirect did not complete at the owned worktree listener.'
    Assert-Equal $received.Method 'GET' 'Callback method changed during forwarding.'
    Assert-Equal $received.Target ("/return?state=" + $newRegistration.id + '.worktree-state&code=synthetic-reload-code') 'Updated callback route did not preserve its raw query.'
    Write-Evidence -Name 'public-url-path-forwarded' -Value ([ordered]@{ pid = $relay.Pid; publicUrl = $publicUrl; route = "$publicPath/callback"; receivedTarget = $received.Target })

    $success = $true
}
catch {
    $failure = [ordered]@{ message = $_.Exception.Message; scriptStack = $_.ScriptStackTrace }
    try { Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'failure.json') -Value $failure } catch { }
}
finally {
    foreach ($entry in $script:OwnedListeners) {
        try { $entry.Listener.Stop(); $entry.Listener.Dispose(); $entry.Stopped = $true } catch { }
    }
    foreach ($relayEntry in $script:OwnedRelays) {
        try { Stop-OwnedRelay -Relay $relayEntry } catch {
            try { Write-Evidence -Name "$($relayEntry.Name)-cleanup-error" -Value ([ordered]@{ message = $_.Exception.Message; pid = $relayEntry.Pid }) } catch { }
        }
    }
    foreach ($httpClient in $script:HttpClients) { try { $httpClient.Dispose() } catch { } }
    $allExited = @($script:OwnedRelays | Where-Object { -not $_.Process.HasExited }).Count -eq 0
    $success = $success -and $allExited
    try {
        Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'cleanup.json') -Value ([ordered]@{
            success = $success
            allOwnedRelaysExited = $allExited
            processIds = @($script:OwnedRelays | ForEach-Object { $_.Pid })
            listeners = @($script:OwnedListeners | ForEach-Object { [ordered]@{ name = $_.Name; port = $_.Port; stopped = $_.Stopped } })
            scratchPath = $script:ScratchRoot
            scratchRemoved = $false
        })
    }
    catch { }
    if ($allExited) {
        $resolvedScratch = Resolve-ChildPath -Child $script:ScratchRoot -Parent $script:WorkVerificationRoot -Name 'Scratch path'
        Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
        $cleanup = Get-Content -LiteralPath (Join-Path $script:EvidenceRoot 'cleanup.json') -Raw | ConvertFrom-Json
        $cleanup.scratchRemoved = $true
        Write-JsonFile -Path (Join-Path $script:EvidenceRoot 'cleanup.json') -Value $cleanup
    }
    foreach ($relayEntry in $script:OwnedRelays) { try { $relayEntry.Process.Dispose() } catch { } }
}

if (-not $success) {
    if ($null -ne $failure) { throw "Live reload verification failed: $($failure.message). See $script:EvidenceRoot" }
    throw "Live reload verification failed. See $script:EvidenceRoot"
}

Write-Output "Live reload verification passed. Evidence: $script:EvidenceRoot"
