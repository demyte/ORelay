[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [Parameter(Mandatory = $true)]
    [string]$RunRoot,

    [Parameter(Mandatory = $true)]
    [string]$PreviousExecutablePath,

    [Parameter(Mandatory = $true)]
    [string]$NextExecutablePath,

    [Parameter(Mandatory = $true)]
    [string]$FailingExecutablePath,

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
        [string]$EvidenceName,

        [System.Management.Automation.PSCredential]$Credential
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $script:WorkPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if ($null -ne $Credential) {
        if (-not $IsWindows) {
            throw 'Credential-backed service proof is supported only on Windows.'
        }

        $credentialParts = $Credential.UserName.Split('\', 2)
        if ($credentialParts.Count -eq 2) {
            $startInfo.Domain = $credentialParts[0]
            $startInfo.UserName = $credentialParts[1]
        }
        else {
            $startInfo.Domain = '.'
            $startInfo.UserName = $Credential.UserName
        }
        $startInfo.Password = $Credential.Password
        $startInfo.LoadUserProfile = $false
    }
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

function Capture-ServiceFailureDiagnostics {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $records = [System.Collections.Generic.List[object]]::new()
    if ($script:IsLinuxPlatform) {
        $unitPath = $script:UnitPath
        try {
            if (Test-Path -LiteralPath $unitPath -PathType Leaf) {
                Get-Content -LiteralPath $unitPath |
                    Set-Content -LiteralPath (Join-Path $script:EvidencePath 'service-failure-unit.service')
                $records.Add([pscustomobject]@{
                        Kind = 'unit-file'
                        Path = $unitPath
                        Evidence = 'service-failure-unit.service'
                    })
            }
            else {
                $records.Add([pscustomobject]@{
                        Kind = 'unit-file'
                        Path = $unitPath
                        Missing = $true
                    })
            }
        }
        catch {
            $records.Add([pscustomobject]@{
                    Kind = 'unit-file'
                    Path = $unitPath
                    Error = $_.Exception.Message
                })
        }

        $commands = @(
            @('show', "$Name.service", '--no-page', '--property=LoadState,ActiveState,SubState,Result,ExecMainStatus,ExecStart', 'service-failure-systemctl-show'),
            @('status', "$Name.service", '--no-pager', '--full', 'service-failure-systemctl-status'),
            @('-u', "$Name.service", '--no-pager', '--no-hostname', '-n', '100', '--since=-5min', 'service-failure-journal')
        )
        foreach ($command in $commands) {
            $evidenceName = $command[-1]
            $arguments = [string[]]$command[0..($command.Count - 2)]
            try {
                $result = Invoke-PrivilegedProcess -FilePath $(if ($evidenceName -eq 'service-failure-journal') { 'journalctl' } else { 'systemctl' }) `
                    -Arguments $arguments -EvidenceName $evidenceName
                $records.Add([pscustomobject]@{
                        Kind = 'command'
                        FilePath = if ($evidenceName -eq 'service-failure-journal') { 'journalctl' } else { 'systemctl' }
                        Arguments = $arguments
                        Evidence = @("$evidenceName.stdout.txt", "$evidenceName.stderr.txt")
                        ExitCode = $result.ExitCode
                    })
            }
            catch {
                $records.Add([pscustomobject]@{
                        Kind = 'command'
                        FilePath = if ($evidenceName -eq 'service-failure-journal') { 'journalctl' } else { 'systemctl' }
                        Arguments = $arguments
                        Error = $_.Exception.Message
                    })
            }
        }
    }
    else {
        $scPath = Join-Path $env:SystemRoot 'System32\sc.exe'
        foreach ($command in @(
                @('qc', $Name, 'service-failure-sc-qc'),
                @('queryex', $Name, 'service-failure-sc-queryex')
            )) {
            $evidenceName = $command[-1]
            $arguments = [string[]]$command[0..($command.Count - 2)]
            try {
                $result = Invoke-ProcessWithEvidence -FilePath $scPath -Arguments $arguments -EvidenceName $evidenceName
                $records.Add([pscustomobject]@{
                        Kind = 'command'
                        FilePath = $scPath
                        Arguments = $arguments
                        Evidence = @("$evidenceName.stdout.txt", "$evidenceName.stderr.txt")
                        ExitCode = $result.ExitCode
                    })
            }
            catch {
                $records.Add([pscustomobject]@{
                        Kind = 'command'
                        FilePath = $scPath
                        Arguments = $arguments
                        Error = $_.Exception.Message
                    })
            }
        }
    }

    Write-ServiceEvidence -Name 'service-failure-diagnostics.json' -Value ([ordered]@{
            ServiceName = $Name
            Rid = $Rid
            CapturedBeforeCleanup = $true
            Records = $records
    })
}

function Invoke-LinuxUnprivilegedInstallProof {
    if (-not $script:IsLinuxPlatform) {
        return
    }
    if ($script:IsRootUser) {
        Write-ServiceEvidence -Name 'service-unprivileged-install.json' -Value ([ordered]@{
                Skipped = $true
                Reason = 'The runner account is root; an unprivileged install proof would be invalid.'
            })
        return
    }

    $result = Invoke-ProcessWithEvidence -FilePath $script:ServiceExecutable -Arguments @(
        'service'
        'install'
        '--name'
        $script:UnprivilegedServiceName
        '--config-file'
        $script:ConfigPath
        '--json'
    ) -EvidenceName 'service-unprivileged-install'
    $payload = $null
    try {
        $payload = $result.StandardOutput | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "The unprivileged Linux service install did not return JSON: $($_.Exception.Message)"
    }

    Write-ServiceEvidence -Name 'service-unprivileged-install.json' -Value ([ordered]@{
            ExpectedErrorCode = 'PermissionDenied'
            ExitCode = $result.ExitCode
            Result = $payload
            StandardOutput = $result.StandardOutput
            StandardError = $result.StandardError
        })
    $unitCreated = Test-Path -LiteralPath $script:UnprivilegedUnitPath -PathType Leaf
    if ($unitCreated) {
        $script:UnprivilegedUnitCreated = $true
    }
    if ($result.ExitCode -ne 3 -or $payload.succeeded -or
        "$($payload.errorCode)" -cne 'PermissionDenied') {
        throw 'An unprivileged Linux service install did not return PermissionDenied.'
    }
    if ($unitCreated) {
        throw "An unprivileged Linux service install created a unit: '$($script:UnprivilegedUnitPath)'."
    }
}

function Invoke-WindowsUnprivilegedInstallProof {
    if (-not $IsWindows) {
        return
    }
    if ($env:GITHUB_ACTIONS -ne 'true') {
        Write-ServiceEvidence -Name 'service-unprivileged-install.json' -Value ([ordered]@{
                Skipped = $true
                Reason = 'The disposable Windows account proof is restricted to GitHub Actions.'
            })
        return
    }

    $accountName = 'orelayu' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $passwordText = 'Orelay-' + [Guid]::NewGuid().ToString('N') + 'a1!'
    $accountCreated = $false
    $securePassword = $null
    $credential = $null
    $proofError = $null
    $accountCleanupError = $null
    $accountAclGranted = $false
    try {
        $securePassword = ConvertTo-SecureString -String $passwordText -AsPlainText -Force
        New-LocalUser -Name $accountName -Password $securePassword -AccountNeverExpires `
            -PasswordNeverExpires -UserMayNotChangePassword -Description 'Temporary ORelay CI service proof account.' |
            Out-Null
        $accountCreated = $true
        $usersGroup = Get-LocalGroup -SID 'S-1-5-32-545' -ErrorAction Stop
        Add-LocalGroupMember -Group $usersGroup.Name -Member $accountName -ErrorAction Stop
        $icaclsPath = Join-Path $env:SystemRoot 'System32\icacls.exe'
        $accountAclGranted = $true
        $aclGrant = Invoke-ProcessWithEvidence -FilePath $icaclsPath -Arguments @(
            $script:WorkPath
            '/grant:r'
            "${accountName}:(OI)(CI)M"
            '/T'
            '/C'
        ) -EvidenceName 'service-unprivileged-acl-grant'
        if ($aclGrant.ExitCode -ne 0) {
            throw "Could not grant the temporary proof account access to the run-owned scratch path."
        }
        $credential = [System.Management.Automation.PSCredential]::new($accountName, $securePassword)
        $result = Invoke-ProcessWithEvidence -FilePath $script:ServiceExecutable -Arguments @(
            'service'
            'install'
            '--name'
            $script:UnprivilegedServiceName
            '--config-file'
            $script:ConfigPath
            '--json'
        ) -EvidenceName 'service-unprivileged-install' -Credential $credential

        $payload = $null
        try {
            $payload = $result.StandardOutput | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            throw "The unprivileged Windows service install did not return JSON: $($_.Exception.Message)"
        }

        $scPath = Join-Path $env:SystemRoot 'System32\sc.exe'
        $serviceQuery = Invoke-ProcessWithEvidence -FilePath $scPath -Arguments @('query', $script:UnprivilegedServiceName) `
            -EvidenceName 'service-unprivileged-query'
        Write-ServiceEvidence -Name 'service-unprivileged-install.json' -Value ([ordered]@{
                AccountName = $accountName
                ExpectedErrorCode = 'PermissionDenied'
                ExpectedExitCode = 3
                ExitCode = $result.ExitCode
                Result = $payload
                ServiceQueryExitCode = $serviceQuery.ExitCode
                StandardOutput = $result.StandardOutput
                StandardError = $result.StandardError
            })
        $serviceCreated = $serviceQuery.ExitCode -eq 0
        if ($serviceCreated) {
            $script:UnprivilegedServiceCreated = $true
        }
        if ($result.ExitCode -ne 3 -or $payload.succeeded -or
            "$($payload.errorCode)" -cne 'PermissionDenied') {
            throw 'An unprivileged Windows service install did not return PermissionDenied with exit code 3.'
        }
        if ($serviceQuery.ExitCode -ne 1060) {
            if ($serviceCreated) {
                throw "An unprivileged Windows service install created service '$($script:UnprivilegedServiceName)'."
            }
            throw "The unprivileged Windows service absence check returned SCM exit code $($serviceQuery.ExitCode), expected 1060."
        }
    }
    catch {
        $proofError = $_.Exception
    }
    finally {
        $passwordText = $null
        $securePassword = $null
        $credential = $null
        if ($accountAclGranted) {
            try {
                $icaclsPath = Join-Path $env:SystemRoot 'System32\icacls.exe'
                $aclRemove = Invoke-ProcessWithEvidence -FilePath $icaclsPath -Arguments @(
                    $script:WorkPath
                    '/remove'
                    $accountName
                    '/T'
                    '/C'
                ) -EvidenceName 'service-unprivileged-acl-remove'
                if ($aclRemove.ExitCode -ne 0) {
                    throw 'Could not remove the temporary proof account ACL.'
                }
            }
            catch {
                $accountCleanupError = $_.Exception
                Write-ServiceEvidence -Name 'service-unprivileged-acl-cleanup.json' -Value ([ordered]@{
                        AccountName = $accountName
                        Removed = $false
                        Error = $_.Exception.Message
                    })
            }
        }
        if ($accountCreated) {
            try {
                Remove-LocalUser -Name $accountName -Confirm:$false -ErrorAction Stop
                Write-ServiceEvidence -Name 'service-unprivileged-account-cleanup.json' -Value ([ordered]@{
                        AccountName = $accountName
                        Removed = $true
                    })
            }
            catch {
                if ($null -eq $accountCleanupError) {
                    $accountCleanupError = $_.Exception
                }
                Write-ServiceEvidence -Name 'service-unprivileged-account-cleanup.json' -Value ([ordered]@{
                        AccountName = $accountName
                        Removed = $false
                        Error = $_.Exception.Message
                    })
            }
        }
    }
    if ($null -ne $accountCleanupError) {
        if ($null -ne $proofError) {
            Write-Warning "Windows unprivileged proof failed and account cleanup also failed: $($accountCleanupError.Message)"
            throw $proofError
        }
        throw $accountCleanupError
    }
    if ($null -ne $proofError) {
        throw $proofError
    }
}

function Invoke-ServiceAction {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('install', 'start', 'status', 'stop', 'restart', 'uninstall', 'enable', 'disable')]
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

function Assert-ServiceBootMode {
    param([string]$Name, [bool]$Enabled)

    if ($script:IsLinuxPlatform) {
        $actual = Invoke-PrivilegedProcess -FilePath 'systemctl' -Arguments @('is-enabled', "$Name.service") `
            -EvidenceName "service-$Name-boot-$Enabled"
        $expected = if ($Enabled) { 'enabled' } else { 'disabled' }
        if ($actual.StandardOutput.Trim() -cne $expected) {
            throw "Systemd boot setting for '$Name' was '$($actual.StandardOutput.Trim())', expected '$expected'."
        }
    }
    else {
        $sc = Join-Path $env:SystemRoot 'System32\sc.exe'
        $actual = Invoke-PrivilegedProcess -FilePath $sc -Arguments @('qc', $Name) `
            -EvidenceName "service-$Name-boot-$Enabled"
        $expected = if ($Enabled) { 'AUTO_START' } else { 'DEMAND_START' }
        if ($actual.ExitCode -ne 0 -or $actual.StandardOutput -cnotmatch $expected) {
            throw "Windows boot setting for '$Name' did not report '$expected'."
        }
    }

    Write-ServiceEvidence -Name "service-$Name-boot-$Enabled.json" -Value ([ordered]@{
            ServiceName = $Name
            Enabled = $Enabled
            NativeExitCode = $actual.ExitCode
            NativeOutput = $actual.StandardOutput
        })
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
        return [pscustomobject]@{ State = $state; CallbackUri = $callbackUri; RedirectLocation = $location }
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
    'orelay.exe'
}
else {
    'orelay'
}

function Get-ExecutableIdentity {
    param([Parameter(Mandatory = $true)] [string]$Path)
    $result = @(& $Path --version --json)
    if ($LASTEXITCODE -ne 0) { throw "Could not read the executable version at '$Path'." }
    $version = (($result -join '') | ConvertFrom-Json).version.Split('+')[0]
    return [pscustomobject]@{
        Version = $version
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
}

function Assert-PersistedCallback {
    param(
        [Parameter(Mandatory = $true)] [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)] [object]$Callback,
        [Parameter(Mandatory = $true)] [string]$EvidenceName
    )
    $response = Invoke-HttpRequest -Client $Client -Method 'GET' -Uri $Callback.CallbackUri
    try {
        $location = $response.Headers.Location?.OriginalString
        if ([int]$response.StatusCode -ne 302 -or $location -cne $Callback.RedirectLocation) {
            throw "The callback changed after '$EvidenceName'. Status: $([int]$response.StatusCode)."
        }
        Write-ServiceEvidence -Name "$EvidenceName.json" -Value ([ordered]@{
                StatusCode = [int]$response.StatusCode
                CallbackUri = $Callback.CallbackUri
                ExpectedLocation = $Callback.RedirectLocation
                ActualLocation = $location
                RegistrationPersisted = $true
            })
    }
    finally { $response.Dispose() }
}

function Assert-InstallTransition {
    param(
        [Parameter(Mandatory = $true)] [string]$SourcePath,
        [Parameter(Mandatory = $true)] [string]$EvidenceName,
        [Parameter(Mandatory = $true)] [string]$ExpectedState,
        [Parameter(Mandatory = $true)] [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)] [object]$Callback,
        [Parameter(Mandatory = $true)] [int]$Port
    )
    $before = Get-ExecutableIdentity -Path $script:ServiceExecutable
    $source = Get-ExecutableIdentity -Path $SourcePath
    $beforeStatus = Invoke-ServiceAction -Action status -Name $script:OwnedServiceName
    Assert-ServiceResult -ActionResult $beforeStatus -ExpectedState $ExpectedState
    if ($before.Version -ceq $source.Version -or $before.Sha256 -ceq $source.Sha256) {
        throw "'$EvidenceName' requires different source and installed versions and bytes."
    }
    $configHash = (Get-FileHash -LiteralPath $script:ConfigPath -Algorithm SHA256).Hash
    $result = Invoke-PrivilegedProcess -FilePath $SourcePath -Arguments @(
        '--config-file', $script:ConfigPath,
        'install', '--install-dir', $script:WorkPath,
        '--name', $script:OwnedServiceName, '--restart-service', '--json'
    ) -EvidenceName $EvidenceName
    $payload = $result.StandardOutput | ConvertFrom-Json
    if ($result.ExitCode -ne 0 -or -not $payload.succeeded -or -not $payload.changed) {
        throw "'$EvidenceName' did not replace the installed executable. Exit code: $($result.ExitCode)."
    }
    $status = Invoke-ServiceAction -Action status -Name $script:OwnedServiceName
    Assert-ServiceResult -ActionResult $status -ExpectedState $ExpectedState
    $after = Get-ExecutableIdentity -Path $script:ServiceExecutable
    if ($after.Version -cne $source.Version -or $after.Sha256 -cne $source.Sha256) {
        throw "'$EvidenceName' left a different installed executable."
    }
    if ((Get-FileHash -LiteralPath $script:ConfigPath -Algorithm SHA256).Hash -cne $configHash) {
        throw "'$EvidenceName' changed the service configuration."
    }
    $databasePath = [IO.Path]::ChangeExtension($script:ConfigPath, 'registrations.db')
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) {
        throw "'$EvidenceName' lost the registration database."
    }
    if ($ExpectedState -eq 'Running') {
        Wait-RelayHealth -Client $Client -Port $Port
        Assert-PersistedCallback -Client $Client -Callback $Callback -EvidenceName "$EvidenceName-callback"
    }
    Write-ServiceEvidence -Name "$EvidenceName-transition.json" -Value ([ordered]@{
            PreviousVersion = $before.Version; PreviousSha256 = $before.Sha256
            PreviousServiceState = $beforeStatus.Result.state
            SourceVersion = $source.Version; SourceSha256 = $source.Sha256
            InstalledVersion = $after.Version; InstalledSha256 = $after.Sha256
            Changed = $payload.changed; ServiceState = $status.Result.state
            ConfigurationSha256 = $configHash
            DatabaseExists = $true
        })
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
$script:OwnedServiceName = $serviceName
$conflictName = "orelay-ci-conflict-$([Guid]::NewGuid().ToString('N'))"
$script:UnprivilegedServiceName = "orelay-ci-unprivileged-$([Guid]::NewGuid().ToString('N'))"
$script:UnitPath = if ($script:IsLinuxPlatform) {
    Join-Path ([System.IO.Path]::DirectorySeparatorChar.ToString()) "etc/systemd/system/$serviceName.service"
}
else {
    $null
}
$script:UnprivilegedUnitPath = if ($script:IsLinuxPlatform) {
    Join-Path ([System.IO.Path]::DirectorySeparatorChar.ToString()) "etc/systemd/system/$($script:UnprivilegedServiceName).service"
}
else {
    $null
}
$script:UnprivilegedUnitCreated = $false
$script:UnprivilegedServiceCreated = $false
$ownedServiceInstalled = $false
$ownedServiceWasInstalled = $false
$ownedServiceCleanupAttempted = $false
$script:PrimaryError = $null
$conflictDefinitionCreated = $false
$httpClient = $null

$identities = [ordered]@{
    Previous = Get-ExecutableIdentity -Path $PreviousExecutablePath
    Production = Get-ExecutableIdentity -Path $ExecutablePath
    Next = Get-ExecutableIdentity -Path $NextExecutablePath
    Failing = Get-ExecutableIdentity -Path $FailingExecutablePath
}
$previousNumber = [version]$identities.Previous.Version.Split('-')[0]
$productionNumber = [version]$identities.Production.Version.Split('-')[0]
$nextNumber = [version]$identities.Next.Version.Split('-')[0]
$failingNumber = [version]$identities.Failing.Version.Split('-')[0]
if ($previousNumber -ge $productionNumber -or $productionNumber -ge $nextNumber -or $nextNumber -ge $failingNumber) {
    throw 'Service fixture versions must increase from previous through production, next, and failing.'
}
if (@($identities.Values | ForEach-Object Sha256 | Select-Object -Unique).Count -ne 4) {
    throw 'Service fixture executable hashes must all differ.'
}
Write-ServiceEvidence -Name 'service-version-order.json' -Value $identities
Copy-Item -LiteralPath $PreviousExecutablePath -Destination $script:ServiceExecutable -Force
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
        UnprivilegedServiceName = $script:UnprivilegedServiceName
    })

try {
    $port = Get-Random -Minimum 20000 -Maximum 40000
    $init = Invoke-ProcessWithEvidence -FilePath $script:ServiceExecutable -Arguments @(
        '--config-file', $script:ConfigPath,
        '--port', [string]$port,
        '--bind', '127.0.0.1',
        '--lease-seconds', '3600',
        'init'
    ) -EvidenceName 'service-init'
    if ($init.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $script:ConfigPath -PathType Leaf)) {
        throw "Selected service configuration could not be initialized. Exit code $($init.ExitCode)."
    }

    Invoke-LinuxUnprivilegedInstallProof
    Invoke-WindowsUnprivilegedInstallProof

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

    Assert-ServiceBootMode -Name $serviceName -Enabled $false
    $enable = Invoke-ServiceAction -Action enable -Name $serviceName
    Assert-ServiceResult -ActionResult $enable -ExpectedState 'Stopped'
    if (-not $enable.Result.changed) { throw 'First service enable did not change boot startup.' }
    Assert-ServiceBootMode -Name $serviceName -Enabled $true
    $enableAgain = Invoke-ServiceAction -Action enable -Name $serviceName
    Assert-ServiceResult -ActionResult $enableAgain -ExpectedState 'Stopped'
    if ($enableAgain.Result.changed) { throw 'Repeated service enable changed boot startup.' }
    $disable = Invoke-ServiceAction -Action disable -Name $serviceName
    Assert-ServiceResult -ActionResult $disable -ExpectedState 'Stopped'
    if (-not $disable.Result.changed) { throw 'Service disable did not change boot startup.' }
    Assert-ServiceBootMode -Name $serviceName -Enabled $false

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
        $restartLocation = $oldCallback.Headers.Location?.OriginalString
        if ([int]$oldCallback.StatusCode -ne 302 -or $restartLocation -cne $callback.RedirectLocation) {
            throw "Restart did not preserve the registration and exact callback location. Status: $([int]$oldCallback.StatusCode)."
        }
        Write-ServiceEvidence -Name 'service-restart-registration-persistence.json' -Value ([ordered]@{
                StatusCode = [int]$oldCallback.StatusCode
                CallbackUri = $callback.CallbackUri
                ExpectedLocation = $callback.RedirectLocation
                ActualLocation = $restartLocation
                RegistrationPersisted = $true
            })
    }
    finally {
        $oldCallback.Dispose()
    }

    Assert-InstallTransition -SourcePath $ExecutablePath -EvidenceName 'service-install-replace-running' `
        -ExpectedState 'Running' -Client $httpClient -Callback $callback -Port $port

    $setup = Invoke-PrivilegedProcess -FilePath $script:ServiceExecutable -Arguments @(
        '--config-file', $script:ConfigPath,
        'setup', '--yes', '--mode', 'service', '--name', $serviceName,
        '--enable-startup', '--start', '--json'
    ) -EvidenceName 'service-setup-production'
    $setupResult = $setup.StandardOutput | ConvertFrom-Json
    if ($setup.ExitCode -ne 0 -or -not $setupResult.succeeded -or $setupResult.serviceName -cne $serviceName) {
        throw "Production setup did not configure the owned service: $($setupResult.message)"
    }
    Assert-ServiceBootMode -Name $serviceName -Enabled $true
    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action status -Name $serviceName) -ExpectedState 'Running'
    Wait-RelayHealth -Client $httpClient -Port $port
    Assert-PersistedCallback -Client $httpClient -Callback $callback -EvidenceName 'service-setup-production-callback'
    Write-ServiceEvidence -Name 'service-setup-production.json' -Value $setupResult
    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action disable -Name $serviceName) -ExpectedState 'Running'
    Assert-ServiceBootMode -Name $serviceName -Enabled $false

    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action stop -Name $serviceName) -ExpectedState 'Stopped'
    Assert-InstallTransition -SourcePath $NextExecutablePath -EvidenceName 'service-install-preserve-stopped' `
        -ExpectedState 'Stopped' -Client $httpClient -Callback $callback -Port $port
    Assert-ServiceResult -ActionResult (Invoke-ServiceAction -Action start -Name $serviceName) -ExpectedState 'Running'
    Wait-RelayHealth -Client $httpClient -Port $port
    Assert-PersistedCallback -Client $httpClient -Callback $callback -EvidenceName 'service-stopped-transition-callback'

    $beforeFailure = Get-ExecutableIdentity -Path $script:ServiceExecutable
    $beforeFailureStatus = Invoke-ServiceAction -Action status -Name $serviceName
    Assert-ServiceResult -ActionResult $beforeFailureStatus -ExpectedState 'Running'
    $configBeforeFailure = (Get-FileHash -LiteralPath $script:ConfigPath -Algorithm SHA256).Hash
    $failingStartMarker = Join-Path $script:WorkPath 'failing-service-start.marker'
    if (Test-Path -LiteralPath $failingStartMarker) { throw 'Failing candidate startup marker already exists.' }
    $failedInstall = Invoke-PrivilegedProcess -FilePath $FailingExecutablePath -Arguments @(
        '--config-file', $script:ConfigPath,
        'install', '--install-dir', $script:WorkPath,
        '--name', $serviceName, '--restart-service', '--json'
    ) -EvidenceName 'service-install-failed-start-rollback'
    $failure = $failedInstall.StandardOutput | ConvertFrom-Json
    if ($failedInstall.ExitCode -ne 3 -or $failure.succeeded -or $failure.changed -or
        $failure.errorCode -cne 'ServiceFailure') {
        throw 'Failing service candidate did not report the expected service startup failure.'
    }
    if (-not (Test-Path -LiteralPath $failingStartMarker -PathType Leaf) -or
        (Get-Content -LiteralPath $failingStartMarker -Raw).Trim() -cne 'server startup reached') {
        throw 'The failing candidate did not reach service server startup.'
    }
    $afterFailure = Get-ExecutableIdentity -Path $script:ServiceExecutable
    if ($afterFailure.Version -cne $beforeFailure.Version -or $afterFailure.Sha256 -cne $beforeFailure.Sha256) {
        throw 'Failed service candidate did not restore the previous executable.'
    }
    if ((Get-FileHash -LiteralPath $script:ConfigPath -Algorithm SHA256).Hash -cne $configBeforeFailure) {
        throw 'Failed service candidate changed the service configuration.'
    }
    if (-not (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($script:ConfigPath, 'registrations.db')) -PathType Leaf)) {
        throw 'Failed service candidate lost the registration database.'
    }
    $rollbackStatus = Invoke-ServiceAction -Action status -Name $serviceName
    Assert-ServiceResult -ActionResult $rollbackStatus -ExpectedState 'Running'
    Wait-RelayHealth -Client $httpClient -Port $port
    Assert-PersistedCallback -Client $httpClient -Callback $callback -EvidenceName 'service-rollback-callback'
    Write-ServiceEvidence -Name 'service-install-rollback.json' -Value ([ordered]@{
            PreviousVersion = $beforeFailure.Version; PreviousSha256 = $beforeFailure.Sha256
            PreviousServiceState = $beforeFailureStatus.Result.state
            CandidateVersion = $identities.Failing.Version; CandidateSha256 = $identities.Failing.Sha256
            RestoredVersion = $afterFailure.Version; RestoredSha256 = $afterFailure.Sha256
            CandidateExitCode = $failedInstall.ExitCode; CandidateErrorCode = $failure.errorCode
            CandidateServerStarted = $true
            ServiceState = $rollbackStatus.Result.state; ConfigurationSha256 = $configBeforeFailure
            DatabaseExists = Test-Path -LiteralPath ([IO.Path]::ChangeExtension($script:ConfigPath, 'registrations.db')) -PathType Leaf
        })

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
    try {
        Capture-ServiceFailureDiagnostics -Name $serviceName
    }
    catch {
        Write-ServiceEvidence -Name 'service-failure-diagnostics-error.json' -Value ([ordered]@{
                ServiceName = $serviceName
                Error = $_.Exception.Message
            })
    }
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
    if ($script:UnprivilegedUnitCreated) {
        try {
            [void](Invoke-PrivilegedProcess -FilePath 'rm' -Arguments @('-f', $script:UnprivilegedUnitPath) -EvidenceName 'service-unprivileged-final-remove')
            [void](Invoke-PrivilegedProcess -FilePath 'systemctl' -Arguments @('daemon-reload') -EvidenceName 'service-unprivileged-final-daemon-reload')
        }
        catch {
        }
    }
    if ($script:UnprivilegedServiceCreated) {
        try {
            [void](Invoke-ServiceAction -Action stop -Name $script:UnprivilegedServiceName)
        }
        catch {
            if ($null -eq $cleanupError) {
                $cleanupError = "Unprivileged service stop cleanup failed: $($_.Exception.Message)"
            }
        }
        try {
            [void](Invoke-ServiceAction -Action uninstall -Name $script:UnprivilegedServiceName)
        }
        catch {
            if ($null -eq $cleanupError) {
                $cleanupError = "Unprivileged service uninstall cleanup failed: $($_.Exception.Message)"
            }
        }
        try {
            $unprivilegedStatus = Invoke-ServiceAction -Action status -Name $script:UnprivilegedServiceName
            if ($unprivilegedStatus.ExitCode -ne 0 -or
                -not $unprivilegedStatus.Result.succeeded -or
                $unprivilegedStatus.Result.state -ne 'NotInstalled') {
                if ($null -eq $cleanupError) {
                    $cleanupError = "Unprivileged service cleanup ended in state '$($unprivilegedStatus.Result.state)'."
                }
            }
        }
        catch {
            if ($null -eq $cleanupError) {
                $cleanupError = "Could not verify unprivileged service cleanup: $($_.Exception.Message)"
            }
        }
    }
    if ($null -ne $cleanupError -and $null -eq $script:PrimaryError) {
        throw $cleanupError
    }
    if ($null -ne $cleanupError -and $null -ne $script:PrimaryError) {
        Write-Warning "Service cleanup also failed after the primary error: $cleanupError"
    }
}
