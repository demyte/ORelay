[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [Parameter(Mandatory = $true)][string]$Rid
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$root = [IO.Path]::GetFullPath($RunRoot)
$state = Join-Path $root 'setup-smoke-state'
$evidence = Join-Path $root 'evidence'
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Run root does not exist: $root" }
if (Test-Path -LiteralPath $state) { throw "Setup smoke state already exists: $state" }
New-Item -ItemType Directory -Path $state, $evidence -Force | Out-Null
$config = Join-Path $state 'orelay.json'
$records = [Collections.Generic.List[object]]::new()

function Invoke-Setup {
    param([string]$Name, [string[]]$Arguments)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $executable
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    $start.WorkingDirectory = $state
    foreach ($argument in @('--config-file', $config) + $Arguments) { [void]$start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.Close()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $code = $process.ExitCode
    }
    finally { $process.Dispose() }
    $stdout | Set-Content -LiteralPath (Join-Path $evidence "setup-$Name.stdout.txt")
    $stderr | Set-Content -LiteralPath (Join-Path $evidence "setup-$Name.stderr.txt")
    $record = [ordered]@{ name = $Name; arguments = $Arguments; exitCode = $code; configExists = (Test-Path -LiteralPath $config -PathType Leaf) }
    $records.Add($record)
    return [pscustomobject]@{ ExitCode = $code; Stdout = $stdout; Stderr = $stderr }
}

try {
    $defaults = Invoke-Setup 'defaults' @('setup', '--defaults', '--yes', '--json')
    if ($defaults.ExitCode -ne 0) { throw 'Unattended defaults failed.' }
    $defaultsResult = $defaults.Stdout | ConvertFrom-Json
    if (-not $defaultsResult.succeeded -or $defaultsResult.callbackUrl -cne 'http://localhost:12987/callback') {
        throw 'Defaults JSON did not report the expected local callback.'
    }
    $saved = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    if ($saved.bind -cne '127.0.0.1' -or $saved.port -ne 12987) { throw 'Defaults did not save local settings.' }
    $defaultHash = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash

    $ifNeeded = Invoke-Setup 'if-needed' @('setup', '--if-needed', '--yes', '--port', '13999', '--json')
    if ($ifNeeded.ExitCode -ne 0 -or -not ($ifNeeded.Stdout | ConvertFrom-Json).skipped) { throw '--if-needed did not skip existing configuration.' }
    if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -cne $defaultHash) { throw '--if-needed changed the existing configuration.' }

    $repeatDefaults = Invoke-Setup 'defaults-existing' @('setup', '--defaults', '--yes', '--json')
    if ($repeatDefaults.ExitCode -ne 0 -or -not ($repeatDefaults.Stdout | ConvertFrom-Json).skipped) { throw '--defaults did not skip existing configuration.' }
    if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -cne $defaultHash) { throw '--defaults changed the existing configuration.' }

    $lan = Invoke-Setup 'lan' @('setup', '--yes', '--access', 'lan', '--hostname', 'relay.test', '--port', '13871', '--json')
    if ($lan.ExitCode -ne 0 -or -not ($lan.Stdout | ConvertFrom-Json).succeeded) { throw 'Unattended LAN setup failed.' }
    $saved = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
    if ($saved.bind -cne '0.0.0.0' -or $saved.hostname -cne 'relay.test' -or $saved.port -ne 13871) {
        throw 'LAN setup did not save the requested settings.'
    }
    $lanHash = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash

    $redirected = Invoke-Setup 'redirected-without-yes' @('setup', '--access', 'local')
    if ($redirected.ExitCode -eq 0 -or $redirected.Stderr -notmatch 'interactive terminal') { throw 'Redirected setup without --yes was accepted.' }
    if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -cne $lanHash) { throw 'Redirected setup changed configuration.' }

    $invalid = Invoke-Setup 'invalid-access' @('setup', '--yes', '--access', 'invalid')
    if ($invalid.ExitCode -eq 0 -or $invalid.Stderr -notmatch 'access must be') { throw 'Invalid access was accepted.' }
    if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -cne $lanHash) { throw 'Invalid setup changed configuration.' }

    $missing = Join-Path $state 'missing.json'
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    foreach ($argument in @('--config-file', $missing, 'setup', '--yes', '--access', 'local', '--port', '0')) { [void]$start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.Close()
        $badOut = $process.StandardOutput.ReadToEndAsync()
        $badErr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $badCode = $process.ExitCode
        $badOut.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $evidence 'setup-invalid-missing.stdout.txt')
        $badErr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $evidence 'setup-invalid-missing.stderr.txt')
    }
    finally { $process.Dispose() }
    $records.Add([ordered]@{ name = 'invalid-missing'; exitCode = $badCode; configExists = (Test-Path -LiteralPath $missing) })
    if ($badCode -eq 0 -or (Test-Path -LiteralPath $missing)) { throw 'Invalid setup created a missing configuration.' }

    Copy-Item -LiteralPath $config -Destination (Join-Path $evidence 'setup-final-config.json')
    [ordered]@{ rid = $Rid; executable = $executable; executableSha256 = (Get-FileHash $executable -Algorithm SHA256).Hash; checks = $records; result = 'passed' } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'setup-smoke.json')
    Write-Host "Setup smoke passed for $Rid. Evidence: $evidence"
}
catch {
    [ordered]@{ rid = $Rid; result = 'failed'; error = $_.Exception.Message; checks = $records } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'setup-smoke.json')
    throw
}
finally {
    $resolvedState = [IO.Path]::GetFullPath($state)
    if ($resolvedState.StartsWith($root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedState)) {
        Remove-Item -LiteralPath $resolvedState -Recurse -Force
    }
}
