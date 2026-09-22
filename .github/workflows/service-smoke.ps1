[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [Parameter(Mandatory = $true)]
    [string]$RunRoot,

    [Parameter(Mandatory = $true)]
    [string]$Rid,

    [switch]$RequireServiceProof
)

$ErrorActionPreference = 'Stop'

function Write-ServiceEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $script:EvidencePath $Name)
}

function Invoke-ProcessWithEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$EvidenceName
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $script:WorkPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = $null
    $standardOutput = ''
    $standardError = ''
    $exitCode = -1
    try {
        $process = [System.Diagnostics.Process]::Start($startInfo)
        if ($null -eq $process) {
            throw "Could not start '$FilePath'."
        }

        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $waitTask = $process.WaitForExitAsync()
        if (-not $waitTask.Wait([TimeSpan]::FromSeconds(90))) {
            try {
                if (-not $process.HasExited) {
                    $process.Kill($true)
                }
            }
            catch {
            }
            [void]$waitTask.Wait([TimeSpan]::FromSeconds(5))
            throw "'$FilePath' exceeded the 90-second process timeout."
        }

        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    }
    finally {
        if ($null -ne $process) {
            $process.Dispose()
        }
    }

    $standardOutput | Set-Content -LiteralPath (Join-Path $script:EvidencePath "$EvidenceName.stdout.txt")
    $standardError | Set-Content -LiteralPath (Join-Path $script:EvidencePath "$EvidenceName.stderr.txt")
    [pscustomobject]@{
        FilePath = $FilePath
        Arguments = $Arguments
        ExitCode = $exitCode
        StandardOutput = $standardOutput
        StandardError = $standardError
    }
}

function Invoke-PrivilegedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$EvidenceName
    )

    if ($script:IsLinuxPlatform -and -not $script:IsRootUser) {
        if ($null -eq $script:SudoPath) {
            return Invoke-ProcessWithEvidence -FilePath $FilePath -Arguments $Arguments -EvidenceName $EvidenceName
        }

        return Invoke-ProcessWithEvidence -FilePath $script:SudoPath `
            -Arguments (@('-n', $FilePath) + $Arguments) -EvidenceName $EvidenceName
    }

    return Invoke-ProcessWithEvidence -FilePath $FilePath -Arguments $Arguments -EvidenceName $EvidenceName
}

function Invoke-ServiceAction {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('install', 'start', 'status', 'stop', 'restart', 'uninstall')]
        [string]$Action,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $command = @(
        'service'
        $Action
        '--name'
        $Name
        '--config-file'
        $script:ConfigPath
        '--json'
    )
    $filePath = $script:ServiceLauncher
    $arguments = $script:ServiceLauncherPrefix + $command
    $processResult = Invoke-ProcessWithEvidence -FilePath $filePath -Arguments $arguments -EvidenceName "service-$Name-$Action"
    $payload = $null
    try {
        $payload = $processResult.StandardOutput | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Service '$Action' did not return JSON. Exit code $($processResult.ExitCode): $($processResult.StandardError.Trim())"
    }

    $result = [pscustomobject]@{
        Action = $Action
        ExitCode = $processResult.ExitCode
        Result = $payload
    }
    Write-ServiceEvidence -Name "service-$Name-$Action.json" -Value $result
    return $result
}

function Mark-ServiceProofSkipped {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Reason,

        [string]$Detail = ''
    )

    Write-ServiceEvidence -Name 'service-skipped.json' -Value ([ordered]@{
            Rid = $Rid
            Platform = $env:RUNNER_OS
            Reason = $Reason
            Detail = $Detail
            Privilege = [ordered]@{
                IsRoot = $script:IsRootUser
                SudoAvailable = $null -ne $script:SudoPath
                ServiceLauncher = $script:ServiceLauncher
            }
        })
    Write-Warning "Service proof skipped: $Reason $Detail"
    if ($RequireServiceProof) {
        throw "Required service proof was skipped: $Reason $Detail"
    }
}

function Assert-ServiceResult {
    param(
        [Parameter(Mandatory = $true)]
        [object]$ActionResult,

        [string]$ExpectedState
    )

    if ($ActionResult.ExitCode -ne 0 -or -not $ActionResult.Result.succeeded) {
        throw "Service '$($ActionResult.Action)' failed: $($ActionResult.Result.message)"
    }

    if ($ExpectedState -and $ActionResult.Result.state -ne $ExpectedState) {
        throw "Service '$($ActionResult.Action)' returned state '$($ActionResult.Result.state)', expected '$ExpectedState'."
    }
}

function Invoke-HttpRequest {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$Client,

        [Parameter(Mandatory = $true)]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [string]$Body
    )

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::new($Method),
        $Uri)
    try {
        if ($null -ne $Body) {
            $request.Content = [System.Net.Http.StringContent]::new(
                $Body,
                [System.Text.Encoding]::UTF8,
                'application/json')
        }

        return $Client.SendAsync($request).GetAwaiter().GetResult()
    }
    finally {
        $request.Dispose()
    }
}

function Wait-RelayHealth {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$Client,

        [Parameter(Mandatory = $true)]
        [int]$Port
    )

    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $response = Invoke-HttpRequest -Client $Client -Method 'GET' -Uri "http://127.0.0.1:$Port/health"
            try {
                if ([int]$response.StatusCode -eq 200) {
                    $health = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                    if ($health.identity -eq 'orelay' -and $health.status -eq 'ok') {
                        Write-ServiceEvidence -Name 'service-health.json' -Value $health
                        return
                    }
                }
            }
            finally {
                $response.Dispose()
            }
        }
        catch {
        }
        Start-Sleep -Milliseconds 500
    }

    throw "Service did not expose a healthy relay on port $Port."
}

function Invoke-CallbackProof {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$Client,

        [Parameter(Mandatory = $true)]
        [int]$Port,

        [Parameter(Mandatory = $true)]
        [string]$EvidenceName
    )

    do {
        $destinationPort = Get-Random -Minimum 20000 -Maximum 40000
    } while ($destinationPort -eq $Port)
    $destinationUrl = "http://127.0.0.1:$destinationPort/callback"
    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Parse('127.0.0.1'),
        $destinationPort)
    $destinationClient = $null
    $destinationStream = $null
    try {
        $listener.Start()
        $destinationRequest = $listener.AcceptTcpClientAsync()
        $registrationBody = [ordered]@{ callbackUrl = $destinationUrl } | ConvertTo-Json -Compress
        $registrationResponse = Invoke-HttpRequest -Client $Client -Method 'POST' `
            -Uri "http://127.0.0.1:$Port/registrations" -Body $registrationBody
        try {
            if ([int]$registrationResponse.StatusCode -ne 201) {
                throw "Registration returned status $([int]$registrationResponse.StatusCode)."
            }
            $registration = $registrationResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        }
        finally {
            $registrationResponse.Dispose()
        }

        if ([string]::IsNullOrWhiteSpace($registration.id)) {
            throw 'Registration response did not contain an ID.'
        }

        $state = "$($registration.id).service-smoke.part with space"
        $rawQuery = "state=$([System.Uri]::EscapeDataString($state))&code=service-smoke&field=a%2Bb&field=two%20words&empty="
        $callbackUri = "http://127.0.0.1:$Port/callback?$rawQuery"
        $redirect = Invoke-HttpRequest -Client $Client -Method 'GET' -Uri $callbackUri
        try {
            $location = $redirect.Headers.Location?.OriginalString
            $expectedLocation = "${destinationUrl}?${rawQuery}"
            if ([int]$redirect.StatusCode -ne 302 -or $location -cne $expectedLocation) {
                throw 'Service callback did not return the expected exact redirect location.'
            }
        }
        finally {
            $redirect.Dispose()
        }

        $followTask = $Client.GetAsync($location)
        if (-not $destinationRequest.Wait(5000)) {
            throw 'Service callback destination did not receive a request.'
        }

        $destinationClient = $destinationRequest.Result
        $destinationStream = $destinationClient.GetStream()
        $destinationStream.ReadTimeout = 5000
        $requestBytes = [System.IO.MemoryStream]::new()
        $buffer = [byte[]]::new(4096)
        do {
            $read = $destinationStream.Read($buffer, 0, $buffer.Length)
            if ($read -gt 0) {
                $requestBytes.Write($buffer, 0, $read)
            }
            $requestText = [System.Text.Encoding]::ASCII.GetString($requestBytes.ToArray())
        } while ($read -gt 0 -and $requestText.IndexOf("`r`n`r`n", [System.StringComparison]::Ordinal) -lt 0)

        if ($requestText -notmatch '^(?<method>\S+) (?<target>\S+) HTTP/\d\.\d\r\n') {
            throw 'Service callback destination received an invalid HTTP request.'
        }
        $actualRawUrl = $Matches.target
        $expectedRawUrl = "/callback?$rawQuery"
        if ($actualRawUrl -cne $expectedRawUrl) {
            throw "Service destination received a different raw request target. Expected '$expectedRawUrl'."
        }
        $responseBytes = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 204 No Content`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
        $destinationStream.Write($responseBytes, 0, $responseBytes.Length)
        $destinationStream.Flush()
        $follow = $followTask.GetAwaiter().GetResult()
        try {
            if ([int]$follow.StatusCode -ne 204) {
                throw "Service callback destination returned status $([int]$follow.StatusCode), expected 204."
            }
        }
        finally {
            $follow.Dispose()
        }

        $evidence = [ordered]@{
            RedirectStatus = 302
            RedirectLocation = $location
            DestinationStatus = 204
            ExpectedRawUrl = $expectedRawUrl
            ActualRawUrl = $actualRawUrl
            State = $state
            CallbackUri = $callbackUri
        }
        Write-ServiceEvidence -Name "$EvidenceName.json" -Value $evidence
        return [pscustomobject]@{ State = $state; CallbackUri = $callbackUri }
    }
    finally {
        if ($null -ne $destinationStream) {
            $destinationStream.Dispose()
        }
        if ($null -ne $destinationClient) {
            $destinationClient.Dispose()
        }
        try {
            $listener.Stop()
        }
        catch {
        }
        $listener.Dispose()
    }
}

function Stop-ExactServiceProcess {
    try {
        $processes = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
                try {
                    $_.Path -eq $script:ServiceExecutable
                }
                catch {
                    $false
                }
            })
        $processes | Stop-Process -Force -ErrorAction SilentlyContinue
        return $processes.Count
    }
    catch {
        return -1
    }
}

function Get-ExactServiceProcessCount {
    try {
        return @(
            Get-Process -ErrorAction SilentlyContinue | Where-Object {
                try {
                    $_.Path -eq $script:ServiceExecutable
                }
                catch {
                    $false
                }
            }
        ).Count
    }
    catch {
        return -1
    }
}

$runRootPath = [System.IO.Path]::GetFullPath($RunRoot)
$script:EvidencePath = Join-Path $runRootPath 'evidence'
$script:WorkPath = Join-Path $runRootPath 'service $ smoke path'
New-Item -ItemType Directory -Path $script:EvidencePath, $script:WorkPath -Force | Out-Null

$script:IsLinuxPlatform = $Rid.StartsWith('linux-', [System.StringComparison]::OrdinalIgnoreCase)
$script:IsRootUser = $false
$script:SudoPath = $null
if ($script:IsLinuxPlatform) {
    $uid = (& id -u 2>$null).Trim()
    $script:IsRootUser = $uid -eq '0'
    if (-not $script:IsRootUser) {
        $sudo = Get-Command sudo -ErrorAction SilentlyContinue
        if ($null -ne $sudo) {
            $script:SudoPath = $sudo.Source
        }
    }
}

$serviceFileName = if ($Rid.StartsWith('win-', [System.StringComparison]::OrdinalIgnoreCase)) {
    'orelay service executable.exe'
}
else {
    'orelay service executable'
}
$script:ServiceExecutable = Join-Path $script:WorkPath $serviceFileName
$script:ConfigPath = Join-Path $script:WorkPath 'service config path.json'
if ($script:IsLinuxPlatform -and -not $script:IsRootUser -and $null -ne $script:SudoPath) {
    $script:ServiceLauncher = $script:SudoPath
    $script:ServiceLauncherPrefix = @('-n', $script:ServiceExecutable)
}
else {
    $script:ServiceLauncher = $script:ServiceExecutable
    $script:ServiceLauncherPrefix = @()
}
$serviceName = "orelay-ci-$([Guid]::NewGuid().ToString('N'))"
$conflictName = "orelay-ci-conflict-$([Guid]::NewGuid().ToString('N'))"
$ownedServiceInstalled = $false
$ownedServiceWasInstalled = $false
$ownedServiceCleanupAttempted = $false
$script:PrimaryError = $null
$conflictDefinitionCreated = $false
$httpClient = $null

Copy-Item -LiteralPath $ExecutablePath -Destination $script:ServiceExecutable -Force
if ($script:IsLinuxPlatform) {
    & chmod 755 $script:ServiceExecutable
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not mark the service executable as executable.'
    }
}

Write-ServiceEvidence -Name 'service-environment.json' -Value ([ordered]@{
        Rid = $Rid
        RunnerOS = $env:RUNNER_OS
        RunnerArch = $env:RUNNER_ARCH
        IsRoot = $script:IsRootUser
        SudoAvailable = $null -ne $script:SudoPath
        ServiceExecutable = $script:ServiceExecutable
        ConfigPath = $script:ConfigPath
        ServiceName = $serviceName
        ConflictName = $conflictName
    })

try {
    $port = Get-Random -Minimum 20000 -Maximum 40000
    $init = Invoke-ProcessWithEvidence -FilePath $script:ServiceExecutable -Arguments @(
        '--config-file', $script:ConfigPath,
        '--port', [string]$port,
        '--bind', '127.0.0.1',
        'init'
    ) -EvidenceName 'service-init'
    if ($init.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $script:ConfigPath -PathType Leaf)) {
        throw "Selected service configuration could not be initialized. Exit code $($init.ExitCode)."
    }

    $unownedDefinition = Join-Path $script:WorkPath 'unowned service definition'
    if ($script:IsLinuxPlatform) {
        $unitPath = Join-Path ([System.IO.Path]::DirectorySeparatorChar.ToString()) "etc/systemd/system/$conflictName.service"
        @(
            '[Unit]'
            'Description=Unowned ORelay CI conflict fixture'
            '[Service]'
            'Type=oneshot'
            'ExecStart=/bin/true'
        ) | Set-Content -LiteralPath $unownedDefinition
        $copy = Invoke-PrivilegedProcess -FilePath 'cp' -Arguments @($unownedDefinition, $unitPath) -EvidenceName 'service-conflict-copy'
        if ($copy.ExitCode -ne 0) {
            Mark-ServiceProofSkipped -Reason 'systemd unit directory is not writable by the runner.' -Detail $copy.StandardError.Trim()
            return
        }
        $conflictDefinitionCreated = $true
        $reload = Invoke-PrivilegedProcess -FilePath 'systemctl' -Arguments @('daemon-reload') -EvidenceName 'service-conflict-reload'
        if ($reload.ExitCode -ne 0) {
            Mark-ServiceProofSkipped -Reason 'systemd is unavailable on the runner.' -Detail $reload.StandardError.Trim()
            return
        }
    }
    else {
        $sc = Join-Path $env:SystemRoot 'System32\sc.exe'
        $create = Invoke-PrivilegedProcess -FilePath $sc -Arguments @(
            'create', $conflictName,
            'binPath=', "$env:SystemRoot\System32\svchost.exe -k netsvcs",
            'start=', 'demand',
            'DisplayName=', 'Unowned ORelay CI conflict fixture'
        ) -EvidenceName 'service-conflict-create'
        if ($create.ExitCode -ne 0) {
            Mark-ServiceProofSkipped -Reason 'Windows SCM is not writable by the runner.' -Detail $create.StandardError.Trim()
            return
        }
        $conflictDefinitionCreated = $true
    }

    $conflictInstall = Invoke-ServiceAction -Action install -Name $conflictName
    if ($conflictInstall.ExitCode -eq 0 -or $conflictInstall.Result.succeeded -or $conflictInstall.Result.errorCode -ne 'Conflict') {
        throw 'Service install did not refuse the unowned conflicting definition.'
    }
    Write-ServiceEvidence -Name 'service-conflict-result.json' -Value $conflictInstall

    if ($script:IsLinuxPlatform) {
        $unitPath = Join-Path ([System.IO.Path]::DirectorySeparatorChar.ToString()) "etc/systemd/system/$conflictName.service"
        $cleanupConflict = Invoke-PrivilegedProcess -FilePath 'rm' -Arguments @('-f', $unitPath) -EvidenceName 'service-conflict-remove'
        if ($cleanupConflict.ExitCode -ne 0) {
            throw "Could not remove the unowned conflict fixture: $($cleanupConflict.StandardError.Trim())"
        }
        $conflictDefinitionCreated = $false
        [void](Invoke-PrivilegedProcess -FilePath 'systemctl' -Arguments @('daemon-reload') -EvidenceName 'service-conflict-final-reload')
    }
    else {
        $sc = Join-Path $env:SystemRoot 'System32\sc.exe'
        $delete = Invoke-PrivilegedProcess -FilePath $sc -Arguments @('delete', $conflictName) -EvidenceName 'service-conflict-delete'
        if ($delete.ExitCode -ne 0) {
            throw "Could not remove the unowned conflict fixture: $($delete.StandardError.Trim())"
        }
        $conflictDefinitionCreated = $false
    }

    $install = Invoke-ServiceAction -Action install -Name $serviceName
    if ($install.ExitCode -ne 0 -or -not $install.Result.succeeded) {
        if ($install.Result.errorCode -in @('PermissionDenied', 'ManagerUnavailable')) {
            Mark-ServiceProofSkipped -Reason 'service manager requires privileges or is unavailable on this runner.' -Detail $install.Result.message
            return
        }
        throw "Owned service install failed: $($install.Result.message)"
    }
    $ownedServiceInstalled = $true
    $ownedServiceWasInstalled = $true
    Assert-ServiceResult -ActionResult $install -ExpectedState 'Stopped'

    $installAgain = Invoke-ServiceAction -Action install -Name $serviceName
    Assert-ServiceResult -ActionResult $installAgain
    if ($installAgain.Result.changed) {
        throw 'Repeated service install unexpectedly changed the owned definition.'
    }

    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action status -Name $serviceName) -ExpectedState 'Stopped'
    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action start -Name $serviceName) -ExpectedState 'Running'
    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action start -Name $serviceName) -ExpectedState 'Running'

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $httpClient = [System.Net.Http.HttpClient]::new($handler)
    $httpClient.Timeout = [TimeSpan]::FromSeconds(5)
    Wait-RelayHealth -Client $httpClient -Port $port
    $callback = Invoke-CallbackProof -Client $httpClient -Port $port -EvidenceName 'service-callback'

    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action restart -Name $serviceName) -ExpectedState 'Running'
    Wait-RelayHealth -Client $httpClient -Port $port
    $oldCallback = Invoke-HttpRequest -Client $httpClient -Method 'GET' -Uri $callback.CallbackUri
    try {
        if ([int]$oldCallback.StatusCode -ne 404) {
            throw "Restart retained the old registration; callback returned status $([int]$oldCallback.StatusCode)."
        }
        Write-ServiceEvidence -Name 'service-restart-registration-loss.json' -Value ([ordered]@{
                StatusCode = [int]$oldCallback.StatusCode
                CallbackUri = $callback.CallbackUri
                RegistrationLost = $true
            })
    }
    finally {
        $oldCallback.Dispose()
    }

    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action stop -Name $serviceName) -ExpectedState 'Stopped'
    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action stop -Name $serviceName) -ExpectedState 'Stopped'
    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action status -Name $serviceName) -ExpectedState 'Stopped'
    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action uninstall -Name $serviceName) -ExpectedState 'NotInstalled'
    $ownedServiceInstalled = $false
    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action uninstall -Name $serviceName)
    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action status -Name $serviceName) -ExpectedState 'NotInstalled'
    if (-not (Test-Path -LiteralPath $script:ConfigPath -PathType Leaf)) {
        throw 'Service uninstall removed the selected configuration.'
    }
    Write-ServiceEvidence -Name 'service-config-preservation.json' -Value ([ordered]@{
            ConfigurationPath = $script:ConfigPath
            ExistsAfterUninstall = $true
        })
}
catch {
    $script:PrimaryError = $_.Exception
    throw
}
finally {
    if ($null -ne $httpClient) {
        $httpClient.Dispose()
    }
    $cleanupError = $null
    $finalServiceStatus = $null
    if ($ownedServiceWasInstalled) {
        $ownedServiceCleanupAttempted = $true
        if ($ownedServiceInstalled) {
            try {
                [void](Invoke-ServiceAction -Action stop -Name $serviceName)
            }
            catch {
                $cleanupError = "Owned service stop cleanup failed: $($_.Exception.Message)"
            }
            try {
                [void](Invoke-ServiceAction -Action uninstall -Name $serviceName)
            }
            catch {
                if ($null -eq $cleanupError) {
                    $cleanupError = "Owned service uninstall cleanup failed: $($_.Exception.Message)"
                }
            }
        }
        try {
            $finalServiceStatus = Invoke-ServiceAction -Action status -Name $serviceName
            if ($finalServiceStatus.ExitCode -ne 0 -or
                -not $finalServiceStatus.Result.succeeded -or
                $finalServiceStatus.Result.state -ne 'NotInstalled') {
                if ($null -eq $cleanupError) {
                    $cleanupError = "Owned service cleanup ended in state '$($finalServiceStatus.Result.state)'."
                }
            }
        }
        catch {
            if ($null -eq $cleanupError) {
                $cleanupError = "Could not verify owned service cleanup: $($_.Exception.Message)"
            }
        }
    }
    $stoppedProcessCount = Stop-ExactServiceProcess
    Write-ServiceEvidence -Name 'service-cleanup.json' -Value ([ordered]@{
            StoppedOwnedProcessCount = $stoppedProcessCount
            OwnedProcessCountAfterCleanup = Get-ExactServiceProcessCount
            ServiceName = $serviceName
            OwnedServiceWasInstalled = $ownedServiceWasInstalled
            OwnedServiceCleanupAttempted = $ownedServiceCleanupAttempted
            FinalServiceStatus = $finalServiceStatus
            PrimaryError = if ($null -eq $script:PrimaryError) { $null } else { $script:PrimaryError.Message }
            CleanupError = $cleanupError
        })
    if ($conflictDefinitionCreated) {
        try {
            if ($script:IsLinuxPlatform) {
                $unitPath = Join-Path ([System.IO.Path]::DirectorySeparatorChar.ToString()) "etc/systemd/system/$conflictName.service"
                [void](Invoke-PrivilegedProcess -FilePath 'rm' -Arguments @('-f', $unitPath) -EvidenceName 'service-conflict-final-remove')
                [void](Invoke-PrivilegedProcess -FilePath 'systemctl' -Arguments @('daemon-reload') -EvidenceName 'service-conflict-final-daemon-reload')
            }
            else {
                $sc = Join-Path $env:SystemRoot 'System32\sc.exe'
                [void](Invoke-PrivilegedProcess -FilePath $sc -Arguments @('delete', $conflictName) -EvidenceName 'service-conflict-final-delete')
            }
        }
        catch {
        }
    }
    if ($null -ne $cleanupError -and $null -eq $script:PrimaryError) {
        throw $cleanupError
    }
    if ($null -ne $cleanupError -and $null -ne $script:PrimaryError) {
        Write-Warning "Service cleanup also failed after the primary error: $cleanupError"
    }
}
