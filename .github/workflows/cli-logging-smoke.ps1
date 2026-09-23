[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$ExecutablePath,
    [Parameter(Mandatory = $true)] [string]$RunRoot
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$executable = [IO.Path]::GetFullPath($ExecutablePath)
$root = [IO.Path]::GetFullPath($RunRoot)
$work = Join-Path $root 'work/cli-logging'
$evidence = Join-Path $root 'evidence/cli-logging'
if ((Test-Path -LiteralPath $work) -or (Test-Path -LiteralPath $evidence)) { throw 'CLI logging smoke needs fresh directories.' }
New-Item -ItemType Directory -Path $work, $evidence -Force | Out-Null
$config = Join-Path $work 'orelay.json'
$log = Join-Path $work 'logs/orelay.log'
$name = 'orelay-log-test-' + [Guid]::NewGuid().ToString('N')
$script:cliLogSequence = 0

function Invoke-Cli([string[]]$Command, [int[]]$ExpectedExit = @(0)) {
    $script:cliLogSequence++
    $prefix = Join-Path $evidence $script:cliLogSequence.ToString('00')
    $arguments = @('--config-file', $config, '--json') + $Command
    & $executable @arguments 1> "$prefix.stdout.json" 2> "$prefix.stderr.txt"
    $code = $LASTEXITCODE
    [pscustomobject]@{ Arguments = $arguments; ExitCode = $code } | ConvertTo-Json | Set-Content -LiteralPath "$prefix.command.json"
    if ($code -notin $ExpectedExit) { throw "CLI logging command $script:cliLogSequence returned unexpected exit code $code." }
    return Get-Content -Raw -LiteralPath "$prefix.stdout.json" | ConvertFrom-Json
}

try {
    # Read-only commands must not create logs or configuration, even on failure.
    $null = Invoke-Cli @('config', 'get')
    $null = Invoke-Cli @('--version')
    $null = Invoke-Cli @('doctor', '--name', $name) @(1)
    $status = Invoke-Cli @('service', 'status', '--name', $name) @(0, 3, 69)
    if ($status.succeeded -and $status.state -ne 'NotInstalled') { throw 'Owned service name unexpectedly exists.' }
    if (Test-Path -LiteralPath (Split-Path $log)) { throw 'Read-only CLI commands created logs.' }
    if (Test-Path -LiteralPath $config) { throw 'Read-only CLI commands created configuration.' }

    $null = Invoke-Cli @('init')
    $null = Invoke-Cli @('config', 'set', 'hostname', 'synthetic-secret.example')
    $before = [IO.File]::ReadAllText($config)
    $null = Invoke-Cli @('config', 'set', 'port', 'synthetic-secret-value') @(3)
    if ([IO.File]::ReadAllText($config) -ne $before) { throw 'Rejected configuration edit changed saved settings.' }
    $null = Invoke-Cli @('config', 'clear', 'hostname')
    $setup = Invoke-Cli @('setup', '--defaults', '--yes', '--skip-path')
    if (-not $setup.skipped) { throw 'Setup did not preserve existing configuration.' }
    $null = Invoke-Cli @('service', 'start', '--name', $name) @(3, 69)

    # Install only into owned scratch directories; do not download or install a service.
    $installDirectory = Join-Path $work 'synthetic-secret-install'
    $installed = Invoke-Cli @('install', '--install-dir', $installDirectory, '--name', $name)
    if (-not $installed.changed) { throw 'Owned native installation did not change the target.' }
    $repeat = Invoke-Cli @('install', '--install-dir', $installDirectory, '--name', $name)
    if ($repeat.changed) { throw 'Repeated native installation unexpectedly changed the target.' }
    $invalidDirectory = Join-Path $work 'invalid-install'
    New-Item -ItemType Directory -Path $invalidDirectory | Out-Null
    $invalidTarget = Join-Path $invalidDirectory ([IO.Path]::GetFileName($executable))
    [IO.File]::WriteAllText($invalidTarget, 'synthetic-secret-unrelated-file')
    $null = Invoke-Cli @('install', '--install-dir', $invalidDirectory, '--name', $name) @(3)
    if ([IO.File]::ReadAllText($invalidTarget) -ne 'synthetic-secret-unrelated-file') { throw 'Failed install changed the unrelated target.' }

    $text = [IO.File]::ReadAllText($log)
    foreach ($expected in @(
        'CLI command completed: init; exit=0',
        'CLI command completed: config set; exit=0',
        'CLI command failed: config set; exit=3',
        'CLI command completed: config clear; exit=0',
        'CLI command completed: setup; exit=0',
        'CLI command failed: service start;',
        'CLI command completed: install; exit=0',
        'CLI command failed: install; exit=3',
        'Update candidate version verified:',
        'Replacing executable.',
        'Executable replacement completed:',
        'Manual install completed:',
        'Manual install failed:',
        'changed=True',
        'changed=False',
        'error=InstallFailure'
    )) {
        if (-not $text.Contains($expected)) { throw "CLI log is missing expected operation: $expected" }
    }
    if ($text.Contains('synthetic-secret') -or $text.Contains($work)) { throw 'CLI file logging included raw values or paths.' }
    $before = (Get-FileHash -LiteralPath $log).Hash
    $null = Invoke-Cli @('config', 'get')
    $null = Invoke-Cli @('service', 'status', '--name', $name) @(0, 3, 69)
    if ((Get-FileHash -LiteralPath $log).Hash -ne $before) { throw 'Read-only CLI commands appended to existing logs.' }
    [pscustomobject]@{ Status = 'passed'; Commands = $script:cliLogSequence } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'result.json')
}
finally {
    if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination (Join-Path $evidence 'orelay.log') }
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'work')) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($work)
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe CLI logging cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output "CLI logging smoke passed. Evidence: $evidence"
