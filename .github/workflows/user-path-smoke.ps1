[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [Parameter(Mandatory = $true)][string]$Rid,
    [switch]$AllowWindowsUserPath
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$directory = Split-Path -Parent $executable
$root = [IO.Path]::GetFullPath($RunRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Run root does not exist: $root" }
$windowsMutationAllowed = $IsWindows -and $AllowWindowsUserPath -and $env:GITHUB_ACTIONS -ceq 'true'
if ($IsWindows -and -not $windowsMutationAllowed) {
    throw 'Windows user PATH proof requires -AllowWindowsUserPath on a disposable GitHub Actions runner.'
}
$state = Join-Path $root 'user-path-smoke-state'
if (Test-Path -LiteralPath $state) { throw "User PATH smoke state already exists: $state" }
$evidence = Join-Path $root 'evidence'
New-Item -ItemType Directory -Path $state, $evidence -Force | Out-Null
$records = [Collections.Generic.List[object]]::new()
$originalUserPath = $null
$userPathCaptured = $false

function Invoke-Native {
    param([string]$Name, [string]$FilePath, [string[]]$Arguments, [hashtable]$Environment = @{})
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.WorkingDirectory = $state
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = $true
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.StandardInput.Close()
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw "'$Name' exceeded the 30-second timeout."
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $exitCode = $process.ExitCode
    }
    finally { $process.Dispose() }
    $stdout | Set-Content -LiteralPath (Join-Path $evidence "user-path-$Name.stdout.txt")
    $stderr | Set-Content -LiteralPath (Join-Path $evidence "user-path-$Name.stderr.txt")
    $records.Add([ordered]@{ name = $Name; exitCode = $exitCode; arguments = $Arguments })
    return [pscustomobject]@{ ExitCode = $exitCode; Stdout = $stdout; Stderr = $stderr }
}

function Invoke-Setup {
    param([string]$Name, [string]$Config, [string[]]$Arguments, [hashtable]$Environment = @{})
    $run = Invoke-Native -Name $Name -FilePath $executable -Environment $Environment `
        -Arguments (@('--config-file', $Config, 'setup') + $Arguments + @('--json'))
    if ($run.ExitCode -ne 0) { throw "Setup '$Name' failed: $($run.Stderr.Trim()) $($run.Stdout.Trim())" }
    $result = $run.Stdout | ConvertFrom-Json
    if (-not $result.succeeded) { throw "Setup '$Name' reported failure: $($result.message)" }
    return $result
}

function Assert-OnlyUserPathChanged {
    param([System.Collections.IDictionary]$Before, [System.Collections.IDictionary]$After)
    foreach ($key in @($Before.Keys) + @($After.Keys) | Sort-Object -Unique) {
        if ($key -ieq 'PATH') { continue }
        if ($Before[$key] -cne $After[$key]) { throw "Windows user environment variable '$key' changed during PATH setup." }
    }
}

try {
    $versionProbe = Invoke-Native 'published-version' $executable @('--version')
    $expectedVersion = $versionProbe.Stdout.Trim()
    if ($versionProbe.ExitCode -ne 0 -or $expectedVersion -notmatch '^orelay [^\r\n]+$') {
        throw 'The published executable did not report a usable version.'
    }
    if ($IsWindows) {
        $before = [Environment]::GetEnvironmentVariables('User')
        $originalUserPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
        $userPathCaptured = $true
        $config = Join-Path $state 'windows.json'
        $defaults = Invoke-Setup 'windows-defaults' $config @('--defaults', '--yes')
        if ($defaults.pathChanged) { throw 'Unattended defaults changed Windows user PATH.' }
        if ([Environment]::GetEnvironmentVariable('PATH', 'User') -cne $originalUserPath) { throw 'Unattended defaults changed Windows user PATH.' }
        $configHash = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash

        $add = Invoke-Setup 'windows-add' $config @('--if-needed', '--yes', '--add-to-path')
        $userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
        $expected = $directory + $(if ([string]::IsNullOrEmpty($originalUserPath)) { '' } else { ";$originalUserPath" })
        if (-not $add.pathChanged -or $userPath -cne $expected) { throw 'Windows setup did not prepend the published executable directory to user PATH.' }
        Assert-OnlyUserPathChanged $before ([Environment]::GetEnvironmentVariables('User'))
        if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -cne $configHash) { throw 'PATH setup changed relay configuration.' }

        $repeat = Invoke-Setup 'windows-repeat' $config @('--if-needed', '--yes', '--add-to-path')
        if ($repeat.pathChanged -or [Environment]::GetEnvironmentVariable('PATH', 'User') -cne $expected) { throw 'Repeated Windows PATH setup was not idempotent.' }
        if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -cne $configHash) { throw 'Repeated Windows PATH setup changed relay configuration.' }

        $environment = @{ PATH = $userPath + ';' + [Environment]::GetEnvironmentVariable('PATH', 'Machine') }
        $probe = Invoke-Native 'windows-fresh-process' (Get-Command pwsh).Source `
            @('-NoProfile', '-Command', '(Get-Command orelay -CommandType Application).Source; & orelay --version') $environment
        $lines = @($probe.Stdout -split '\r?\n')
        if ($probe.ExitCode -ne 0 -or $lines -cnotcontains $executable -or $lines -cnotcontains $expectedVersion) {
            throw 'A fresh process could not resolve the published orelay executable from user PATH.'
        }
        $records.Add([ordered]@{ name = 'windows-user-path'; expected = $expected; actual = $userPath; resolvedVersion = $probe.Stdout.Trim() })
    }
    else {
        $shells = if ($IsMacOS) { @('zsh', 'bash', 'sh') } else { @('bash', 'sh') }
        if (Get-Command fish -ErrorAction SilentlyContinue) { $shells += 'fish' }
        foreach ($shell in $shells) {
            $shellCommand = Get-Command $shell -ErrorAction Stop
            $profileHome = Join-Path $state $shell
            $zdot = Join-Path $profileHome 'zdot'
            $xdg = Join-Path $profileHome 'xdg'
            New-Item -ItemType Directory -Path $profileHome, $zdot, $xdg -Force | Out-Null
            $config = Join-Path $profileHome 'orelay.json'
            $environment = @{ HOME = $profileHome; SHELL = $shellCommand.Source; ZDOTDIR = $zdot; XDG_CONFIG_HOME = $xdg }
            $homeProbe = Invoke-Native "$shell-home" (Get-Command pwsh).Source `
                @('-NoProfile', '-Command', '[Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)') $environment
            if ($homeProbe.ExitCode -ne 0 -or $homeProbe.Stdout.Trim() -cne $profileHome) {
                throw "The $shell child process would use a home outside the run-owned directory."
            }
            [string[]]$profilePaths = switch ($shell) {
                'bash' { @((Join-Path $profileHome '.bashrc'), (Join-Path $profileHome '.profile')) }
                'sh' { @((Join-Path $profileHome '.profile')) }
                'zsh' { @((Join-Path $zdot '.zshrc')) }
                'fish' { @((Join-Path $xdg 'fish/conf.d/orelay.fish')) }
            }

            $defaults = Invoke-Setup "$shell-defaults" $config @('--defaults', '--yes') $environment
            if ($defaults.pathChanged -or @($profilePaths | Where-Object { Test-Path -LiteralPath $_ }).Count -ne 0) {
                throw "Unattended defaults wrote a $shell profile."
            }
            $configHash = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash
            $add = Invoke-Setup "$shell-add" $config @('--if-needed', '--yes', '--add-to-path') $environment
            if (-not $add.pathChanged) { throw "$shell setup did not add the published directory to PATH." }
            foreach ($profile in $profilePaths) {
                if (-not (Test-Path -LiteralPath $profile -PathType Leaf) -or
                    -not (Get-Content -LiteralPath $profile -Raw).Contains($directory, [StringComparison]::Ordinal)) {
                    throw "$shell setup did not write the expected profile '$profile'."
                }
            }
            if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -cne $configHash) { throw "$shell PATH setup changed relay configuration." }
            $profileHashes = @($profilePaths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
            $repeat = Invoke-Setup "$shell-repeat" $config @('--if-needed', '--yes', '--add-to-path') $environment
            if ($repeat.pathChanged) { throw "Repeated $shell PATH setup changed a profile." }
            if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -cne $configHash) { throw "Repeated $shell PATH setup changed relay configuration." }
            $repeatedHashes = @($profilePaths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
            if (($profileHashes -join ',') -cne ($repeatedHashes -join ',')) { throw "Repeated $shell PATH setup changed a profile." }

            $probeArguments = if ($shell -eq 'sh') {
                $quotedProfile = "'" + $profilePaths[0].Replace("'", "'\''", [StringComparison]::Ordinal) + "'"
                @('-c', ". $quotedProfile; command -v orelay; orelay --version")
            } else {
                @('-ic', 'command -v orelay; orelay --version')
            }
            $probe = Invoke-Native "$shell-fresh-shell" $shellCommand.Source $probeArguments $environment
            $lines = @($probe.Stdout -split '\r?\n')
            if ($probe.ExitCode -ne 0 -or $lines -cnotcontains $executable -or $lines -cnotcontains $expectedVersion) {
                throw "A fresh $shell shell did not resolve and run the published executable."
            }
            if ($shell -eq 'bash') {
                $login = Invoke-Native 'bash-login-shell' $shellCommand.Source @('-lc', 'command -v orelay; orelay --version') $environment
                $loginLines = @($login.Stdout -split '\r?\n')
                if ($login.ExitCode -ne 0 -or $loginLines -cnotcontains $executable -or $loginLines -cnotcontains $expectedVersion) {
                    throw 'A fresh bash login shell did not load the run-owned login profile.'
                }
            }
            $records.Add([ordered]@{ name = "$shell-profile"; files = $profilePaths; resolvedVersion = $probe.Stdout.Trim() })
        }
    }

    [ordered]@{ rid = $Rid; executable = $executable; executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash; result = 'passed'; checks = $records } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'user-path-smoke.json')
    Write-Host "User PATH smoke passed for $Rid. Evidence: $evidence"
}
catch {
    [ordered]@{ rid = $Rid; result = 'failed'; error = $_.Exception.Message; checks = $records } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $evidence 'user-path-smoke.json')
    throw
}
finally {
    if ($windowsMutationAllowed -and $userPathCaptured) {
        [Environment]::SetEnvironmentVariable('PATH', $originalUserPath, 'User')
        $restored = [Environment]::GetEnvironmentVariable('PATH', 'User') -ceq $originalUserPath
        [ordered]@{ restored = $restored; originalWasNull = $null -eq $originalUserPath } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'user-path-restore.json')
        if (-not $restored) {
            throw 'Windows user PATH restoration failed.'
        }
    }
    $resolvedState = [IO.Path]::GetFullPath($state)
    if ($resolvedState.StartsWith($root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedState)) {
        Remove-Item -LiteralPath $resolvedState -Recurse -Force
    }
}
