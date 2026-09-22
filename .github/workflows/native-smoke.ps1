[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [Parameter(Mandatory = $true)]
    [string]$PublishedDirectory,

    [Parameter(Mandatory = $true)]
    [string]$RunRoot,

    [Parameter(Mandatory = $true)]
    [string]$Rid,

    [Parameter(Mandatory = $true)]
    [string]$ExecutableName,

    [string]$ManagedProbePath,

    [switch]$HideRuntimeForProof
)

$ErrorActionPreference = 'Stop'

function Write-NativeEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$OutputFile,

        [int]$ExpectedExitCode = 0
    )

    $output = @(& $script:ExecutionPath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output | Set-Content -LiteralPath $OutputFile
    if ($exitCode -ne $ExpectedExitCode) {
        throw "'$($Arguments -join ' ')' exited with code $exitCode; expected $ExpectedExitCode."
    }

    return $output
}

function Quote-ProcessArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Start-NativeServer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConfigPath,

        [Parameter(Mandatory = $true)]
        [int]$Port,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [string]$StandardOutputPath,

        [Parameter(Mandatory = $true)]
        [string]$StandardErrorPath
    )

    $arguments = @(
        '--config-file'
        (Quote-ProcessArgument $ConfigPath)
        'server'
        '--port'
        ([string]$Port)
    )
    $startParameters = @{
        FilePath = $script:ExecutionPath
        ArgumentList = $arguments
        WorkingDirectory = $WorkingDirectory
        PassThru = $true
        RedirectStandardOutput = $StandardOutputPath
        RedirectStandardError = $StandardErrorPath
    }
    if ($IsWindows) {
        $startParameters.WindowStyle = 'Hidden'
    }
    return Start-Process @startParameters
}

function Get-DotnetRuntimeLayout {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw 'The dotnet command is required to derive the runner runtime layout.'
    }

    $dotnetCommandPath = [System.IO.Path]::GetFullPath($dotnetCommand.Source)
    $runtimeLines = @(& $dotnetCommand.Source --list-runtimes 2>&1)
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "dotnet --list-runtimes failed with exit code $exitCode."
    }

    $runtimeLine = $runtimeLines | Where-Object {
        "$_" -match '\[(?<runtimePath>[^\]]+)\]'
    } | Select-Object -First 1
    if ($null -eq $runtimeLine -or "${runtimeLine}" -notmatch '\[(?<runtimePath>[^\]]+)\]') {
        throw 'dotnet --list-runtimes did not report a runtime installation path.'
    }

    $runtimeFamilyPath = [System.IO.Path]::GetFullPath($Matches.runtimePath)
    $sharedPath = [System.IO.Path]::GetFullPath([System.IO.Path]::GetDirectoryName($runtimeFamilyPath))
    $dotnetRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetDirectoryName($sharedPath))
    $relativeSharedPath = [System.IO.Path]::GetRelativePath($dotnetRoot, $sharedPath)
    if ($relativeSharedPath -ne 'shared' -or
        [System.IO.Path]::GetFileName($sharedPath) -cne 'shared' -or
        -not (Test-Path -LiteralPath $sharedPath -PathType Container)) {
        throw "The selected dotnet shared runtime directory is not a validated child of its installation root: '$sharedPath'."
    }

    return [pscustomobject]@{
        CommandPath = $dotnetCommandPath
        InstallationRoot = $dotnetRoot
        SharedPath = $sharedPath
        RuntimeFamilyPath = $runtimeFamilyPath
        RuntimeLines = $runtimeLines
    }
}

function Hide-DotnetRuntimeForProof {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Layout
    )

    if (-not $HideRuntimeForProof) {
        return
    }
    if ($env:GITHUB_ACTIONS -ne 'true') {
        throw '-HideRuntimeForProof is reserved for GitHub Actions so local developer runtimes are never moved.'
    }

    $hiddenPath = Join-Path $Layout.InstallationRoot "shared.orelay-hidden-$([Guid]::NewGuid().ToString('N'))"
    if (Test-Path -LiteralPath $hiddenPath) {
        throw "The run-owned runtime hide path already exists: '$hiddenPath'."
    }

    Assert-RuntimeMoveScope -Root $Layout.InstallationRoot -Path $Layout.SharedPath -ExpectedLeaf 'shared'
    Assert-RuntimeMoveScope -Root $Layout.InstallationRoot -Path $hiddenPath -ExpectedLeafPattern '^shared\.orelay-hidden-[0-9a-f]{32}$'
    $usedSudo = Move-RuntimeDirectory -Source $Layout.SharedPath -Destination $hiddenPath
    $script:RuntimeHideState = [pscustomobject]@{
        InstallationRoot = $Layout.InstallationRoot
        OriginalPath = $Layout.SharedPath
        HiddenPath = $hiddenPath
        UsedSudo = $usedSudo
    }
    [pscustomobject]@{
        Enabled = $true
        InstallationRoot = $Layout.InstallationRoot
        OriginalPath = $Layout.SharedPath
        HiddenPath = $hiddenPath
        UsedSudo = $usedSudo
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $script:EvidencePath runtime-hide.json)
}

function Assert-RuntimeMoveScope {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$ExpectedLeaf,

        [string]$ExpectedLeafPattern
    )

    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if ([System.IO.Path]::GetDirectoryName($fullPath) -cne $fullRoot) {
        throw "Runtime move path is outside the validated dotnet installation root: '$fullPath'."
    }

    $leaf = [System.IO.Path]::GetFileName($fullPath)
    if (($ExpectedLeaf -and $leaf -cne $ExpectedLeaf) -or
        ($ExpectedLeafPattern -and $leaf -notmatch $ExpectedLeafPattern)) {
        throw "Runtime move path has an unexpected leaf name: '$leaf'."
    }
}

function Move-RuntimeDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    try {
        Move-Item -LiteralPath $Source -Destination $Destination
        return $false
    }
    catch [System.UnauthorizedAccessException] {
        if ($IsWindows) {
            throw
        }

        $sudo = Get-Command sudo -ErrorAction SilentlyContinue
        if ($null -eq $sudo) {
            throw
        }

        $output = @(& $sudo.Source -n mv -- $Source $Destination 2>&1)
        if ($LASTEXITCODE -ne 0) {
            throw "sudo mv could not move the selected dotnet runtime directory: $($output -join ' ')"
        }
        return $true
    }
}

function Restore-DotnetRuntimeAfterProof {
    if ($null -eq $script:RuntimeHideState) {
        return
    }

    Assert-RuntimeMoveScope -Root $script:RuntimeHideState.InstallationRoot `
        -Path $script:RuntimeHideState.OriginalPath -ExpectedLeaf 'shared'
    Assert-RuntimeMoveScope -Root $script:RuntimeHideState.InstallationRoot `
        -Path $script:RuntimeHideState.HiddenPath -ExpectedLeafPattern '^shared\.orelay-hidden-[0-9a-f]{32}$'
    if (Test-Path -LiteralPath $script:RuntimeHideState.OriginalPath) {
        throw "Cannot restore the dotnet shared runtime because the original path is no longer empty: '$($script:RuntimeHideState.OriginalPath)'."
    }
    if (-not (Test-Path -LiteralPath $script:RuntimeHideState.HiddenPath -PathType Container)) {
        throw "The hidden dotnet shared runtime was not found at '$($script:RuntimeHideState.HiddenPath)'."
    }

    $usedSudo = Move-RuntimeDirectory -Source $script:RuntimeHideState.HiddenPath -Destination $script:RuntimeHideState.OriginalPath
    [pscustomobject]@{
        Restored = $true
        OriginalPath = $script:RuntimeHideState.OriginalPath
        UsedSudo = $usedSudo
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $script:EvidencePath runtime-restore.json)
    $script:RuntimeHideState = $null
}

$originalPath = $env:PATH
$originalDotnetRoot = $env:DOTNET_ROOT
$originalDotnetRootX64 = $env:DOTNET_ROOT_x64
$script:RuntimeHideState = $null
try {
$runRootPath = [System.IO.Path]::GetFullPath($RunRoot)
$publishPath = [System.IO.Path]::GetFullPath($PublishedDirectory)
$evidencePath = Join-Path $runRootPath 'evidence'
$executionPath = Join-Path $runRootPath 'execution'
$workPath = Join-Path $runRootPath 'run owned path'
New-Item -ItemType Directory -Path $evidencePath, $executionPath, $workPath -Force | Out-Null

$publishedExecutable = Join-Path $publishPath $ExecutableName
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "Native executable was not produced: $publishedExecutable"
}

$managedFiles = @(Get-ChildItem -LiteralPath $publishPath -File | Where-Object {
    $_.Name.EndsWith('.dll', [System.StringComparison]::OrdinalIgnoreCase) -or
    $_.Name.EndsWith('.deps.json', [System.StringComparison]::OrdinalIgnoreCase) -or
    $_.Name.EndsWith('.runtimeconfig.json', [System.StringComparison]::OrdinalIgnoreCase)
})
if ($managedFiles.Count -gt 0) {
    throw "Native publish contains managed runtime files: $($managedFiles.Name -join ', ')"
}

$script:ExecutionPath = Join-Path $executionPath $ExecutableName
Copy-Item -LiteralPath $publishedExecutable -Destination $script:ExecutionPath
Get-ChildItem -LiteralPath $publishPath -File |
    Select-Object Name, Length, LastWriteTime |
    ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $evidencePath inventory.json)
Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256 |
    ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $evidencePath executable.sha256.json)
[pscustomobject]@{
    Rid = $Rid
    RunnerOS = $env:RUNNER_OS
    RunnerArch = $env:RUNNER_ARCH
    Dotnet = $env:DOTNET_VERSION
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidencePath environment.json)
dotnet --info 2>&1 | Set-Content -LiteralPath (Join-Path $evidencePath dotnet-info.txt)

$dotnetLayout = Get-DotnetRuntimeLayout
$dotnetLayout | Select-Object CommandPath, InstallationRoot, SharedPath, RuntimeFamilyPath |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidencePath dotnet-location.json)
$managedProbeFullPath = $null
if ($HideRuntimeForProof) {
    if ([string]::IsNullOrWhiteSpace($ManagedProbePath) -or
        -not (Test-Path -LiteralPath $ManagedProbePath -PathType Leaf)) {
        throw "A framework-dependent managed probe is required when runtime hiding is enabled: '$ManagedProbePath'."
    }

    $managedProbeFullPath = [System.IO.Path]::GetFullPath($ManagedProbePath)
    $probeDotnetRoot = $env:DOTNET_ROOT
    $probeDotnetRootX64 = $env:DOTNET_ROOT_x64
    try {
        $env:DOTNET_ROOT = $dotnetLayout.InstallationRoot
        $env:DOTNET_ROOT_x64 = $dotnetLayout.InstallationRoot
        $positiveProbeOutput = @(& $dotnetLayout.CommandPath $managedProbeFullPath '--version' 2>&1)
        $positiveProbeExitCode = $LASTEXITCODE
    }
    finally {
        $env:DOTNET_ROOT = $probeDotnetRoot
        $env:DOTNET_ROOT_x64 = $probeDotnetRootX64
    }
    [pscustomobject]@{
        Path = $managedProbeFullPath
        ExitCode = $positiveProbeExitCode
        Output = $positiveProbeOutput
        RuntimeHidden = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidencePath managed-runtime-positive-control.json)
    if ($positiveProbeExitCode -ne 0) {
        throw "Framework-dependent control exited with code $positiveProbeExitCode before runtime hiding."
    }
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -ne $dotnetCommand) {
    $pathEntries = @($env:PATH -split [System.IO.Path]::PathSeparator | Where-Object {
        $_ -and
        $_ -ne $dotnetLayout.InstallationRoot
    })
    $env:PATH = $pathEntries -join [System.IO.Path]::PathSeparator
}
$env:DOTNET_ROOT = Join-Path $runRootPath 'no-dotnet-runtime'
$env:DOTNET_ROOT_x64 = $env:DOTNET_ROOT

Hide-DotnetRuntimeForProof -Layout $dotnetLayout
if ($HideRuntimeForProof) {
    $probeDotnetRoot = $env:DOTNET_ROOT
    $probeDotnetRootX64 = $env:DOTNET_ROOT_x64
    try {
        $env:DOTNET_ROOT = $dotnetLayout.InstallationRoot
        $env:DOTNET_ROOT_x64 = $dotnetLayout.InstallationRoot
        $probeOutput = @(& $dotnetLayout.CommandPath $managedProbeFullPath '--version' 2>&1)
        $probeExitCode = $LASTEXITCODE
    }
    finally {
        $env:DOTNET_ROOT = $probeDotnetRoot
        $env:DOTNET_ROOT_x64 = $probeDotnetRootX64
    }
    [pscustomobject]@{
        Path = $managedProbeFullPath
        ExitCode = $probeExitCode
        Output = $probeOutput
        RuntimeHidden = $true
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidencePath managed-runtime-negative-control.json)
    $negativeProbeText = $probeOutput -join [Environment]::NewLine
    if ($probeExitCode -eq 0 -or
        $negativeProbeText -notmatch '(?i)(No frameworks were found|You must install or update \.NET)' -or
        $negativeProbeText -notmatch '(?i)Microsoft\.NETCore\.App') {
        throw 'The framework-dependent negative control did not report the expected missing Microsoft.NETCore.App framework while the selected runtime directory was hidden.'
    }
}

[void](Write-NativeEvidence -Arguments @('--help') -OutputFile (Join-Path $evidencePath help.txt))
[void](Write-NativeEvidence -Arguments @('--version') -OutputFile (Join-Path $evidencePath version.txt))
[void](Write-NativeEvidence -Arguments @('invalid-command') -OutputFile (Join-Path $evidencePath invalid-command.txt) -ExpectedExitCode 64)

$configPath = Join-Path $workPath 'orelay config.json'
[void](Write-NativeEvidence -Arguments @('--config-file', $configPath, 'init') -OutputFile (Join-Path $evidencePath init.txt))
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw 'Isolated config initialization did not create the selected file.'
}

$server = $null
$destinationListener = $null
$destinationClient = $null
$client = $null
try {
    $port = Get-Random -Minimum 20000 -Maximum 40000
    $destinationPort = $port + 1
    $server = Start-NativeServer -ConfigPath $configPath -Port $port -WorkingDirectory $workPath `
        -StandardOutputPath (Join-Path $evidencePath server.stdout.txt) `
        -StandardErrorPath (Join-Path $evidencePath server.stderr.txt)

    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 250
        $tcp = [System.Net.Sockets.TcpClient]::new()
        try {
            $connect = $tcp.ConnectAsync('127.0.0.1', $port)
            if ($connect.Wait(2000) -and $tcp.Connected) {
                $ready = $true
                break
            }
        }
        catch {
        }
        finally {
            $tcp.Dispose()
        }
        if ($server.HasExited) {
            throw "Server exited before readiness with code $($server.ExitCode)."
        }
    }
    if (-not $ready) {
        throw 'Server did not become ready on its owned loopback port.'
    }

    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 5
    if ($health.identity -ne 'orelay' -or $health.status -ne 'ok') {
        throw 'The ready process did not identify itself as ORelay.'
    }
    $health | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidencePath health.json)

    $destinationUrl = "http://127.0.0.1:$destinationPort/callback"
    $destinationListener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Parse('127.0.0.1'),
        $destinationPort)
    $destinationListener.Start()
    $destinationRequest = $destinationListener.AcceptTcpClientAsync()

    $registrationBody = [ordered]@{ callbackUrl = $destinationUrl } | ConvertTo-Json -Compress
    $registration = Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/registrations" `
        -ContentType 'application/json' -Body $registrationBody -TimeoutSec 5
    if ([string]::IsNullOrWhiteSpace($registration.id)) {
        throw 'Registration response did not contain an ID.'
    }

    $state = "$($registration.id).native-smoke.part with space"
    $rawQuery = "state=$([System.Uri]::EscapeDataString($state))&code=native-smoke&field=a%2Bb&field=two%20words&empty="
    $callbackUri = "http://127.0.0.1:$port/callback?$rawQuery"
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $redirect = $client.GetAsync($callbackUri).GetAwaiter().GetResult()
        $location = $redirect.Headers.Location?.OriginalString
        $expectedLocation = "${destinationUrl}?${rawQuery}"
        [pscustomobject]@{
            StatusCode = [int]$redirect.StatusCode
            Location = $location
            ExpectedLocation = $expectedLocation
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidencePath callback-redirect.json)
        if ([int]$redirect.StatusCode -ne 302 -or $location -cne $expectedLocation) {
            throw 'Synthetic callback did not return the expected exact redirect location.'
        }

        $followTask = $client.GetAsync($location)
        if (-not $destinationRequest.Wait(5000)) {
            throw 'Synthetic callback destination did not receive a request.'
        }

        $destinationClient = $destinationRequest.Result
        try {
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
                throw 'Synthetic callback destination received an invalid HTTP request.'
            }
            $actualRawUrl = $Matches.target
            $expectedRawUrl = "/callback?$rawQuery"
            if ($actualRawUrl -cne $expectedRawUrl) {
                throw "Destination received a different raw request target. Expected '$expectedRawUrl'."
            }
            $responseBytes = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 204 No Content`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
            $destinationStream.Write($responseBytes, 0, $responseBytes.Length)
            $destinationStream.Flush()
            $follow = $followTask.GetAwaiter().GetResult()
            if ([int]$follow.StatusCode -ne 204) {
                throw "Synthetic callback destination returned status $([int]$follow.StatusCode), expected 204."
            }
            [pscustomobject]@{
                RedirectStatus = [int]$redirect.StatusCode
                RedirectLocation = $location
                DestinationStatus = [int]$follow.StatusCode
                ExpectedRawUrl = $expectedRawUrl
                ActualRawUrl = $actualRawUrl
            } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidencePath callback.json)
        }
        finally {
            if ($null -ne $destinationClient) {
                $destinationClient.Dispose()
            }
        }
    }
    finally {
        if ($null -ne $client) {
            $client.Dispose()
        }
    }
}
finally {
    if ($null -ne $destinationListener) {
        try {
            $destinationListener.Stop()
        }
        catch {
        }
        $destinationListener.Dispose()
    }
    if ($null -ne $server) {
        try {
            if (-not $server.HasExited) {
                $server.Kill($true)
            }
        }
        catch {
        }
        try {
            $server.WaitForExit(10000) | Out-Null
        }
        catch {
        }
        $server.Dispose()
    }
}
}
finally {
    try {
        Restore-DotnetRuntimeAfterProof
    }
    finally {
        if ($null -ne $originalPath) {
            $env:PATH = $originalPath
        }
        $env:DOTNET_ROOT = $originalDotnetRoot
        $env:DOTNET_ROOT_x64 = $originalDotnetRootX64
    }
}
