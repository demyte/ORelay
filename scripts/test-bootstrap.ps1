param([string] $Shell = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe")

$ErrorActionPreference = 'Stop'
if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
    throw 'Run this fixture test on Windows, including Windows PowerShell 5.1.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("orelay-bootstrap-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $fixture = Join-Path $testRoot 'fixture'
    $bin = Join-Path $testRoot 'bin'
    New-Item -ItemType Directory -Path $fixture, $bin | Out-Null
    $rid = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
    $archiveName = "orelay-1.2.3-$rid.zip"
    $sourceExe = Join-Path $fixture 'orelay.exe'
    $source = @'
using System;
using System.IO;
class BootstrapFixture {
  static int Main(string[] args) {
    if (args.Length == 0) return 88;
    if (args[0] == "install") {
      File.WriteAllLines(Environment.GetEnvironmentVariable("ARG_LOG"), args);
      int installCode;
      if (Int32.TryParse(Environment.GetEnvironmentVariable("FIXTURE_EXIT"), out installCode) && installCode != 0) return installCode;
      int index = Array.IndexOf(args, "--install-dir");
      if (index < 0 || index + 1 >= args.Length) return 89;
      string target = args[index + 1];
      Directory.CreateDirectory(target);
      File.Copy(System.Reflection.Assembly.GetExecutingAssembly().Location, Path.Combine(target, "orelay.exe"), true);
      return 0;
    }
    if (args[0] == "setup") {
      var lines = new string[args.Length + 1];
      lines[0] = System.Reflection.Assembly.GetExecutingAssembly().Location;
      Array.Copy(args, 0, lines, 1, args.Length);
      File.WriteAllLines(Environment.GetEnvironmentVariable("SETUP_LOG"), lines);
      int setupCode;
      return Int32.TryParse(Environment.GetEnvironmentVariable("FIXTURE_SETUP_EXIT"), out setupCode) ? setupCode : 0;
    }
    return 88;
  }
}
'@
    Add-Type -TypeDefinition $source -OutputType ConsoleApplication -OutputAssembly $sourceExe
    $archivePath = Join-Path $fixture $archiveName
    Compress-Archive -LiteralPath $sourceExe -DestinationPath $archivePath
    $digest = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$archivePath.sha256" -Value "$digest  $archiveName" -NoNewline

    @'
param([Parameter(ValueFromRemainingArguments=$true)][string[]]$ArgsFromCaller)
$ErrorActionPreference = 'Stop'
switch ($ArgsFromCaller[0] + ' ' + $ArgsFromCaller[1]) {
  'auth status' { exit 0 }
  'release view' {
    $assets = @()
    if ($env:FIXTURE_MISSING -ne 'archive') { $assets += @{ name = $env:FIXTURE_ARCHIVE } }
    if ($env:FIXTURE_MISSING -ne 'checksum') { $assets += @{ name = "$env:FIXTURE_ARCHIVE.sha256" } }
    $tag = if ($env:FIXTURE_TAG) { $env:FIXTURE_TAG } else { 'v1.2.3' }
    [pscustomobject]@{ tagName = $tag; assets = $assets; draft = $false; prerelease = $false } | ConvertTo-Json -Depth 5 -Compress
    exit 0
  }
  'release download' {
    $text = $ArgsFromCaller -join ' '
    $destination = [regex]::Match($text, '--dir\s+"?([^"\s]+)').Groups[1].Value
    if (-not $destination) { throw 'Missing download directory.' }
    Copy-Item -LiteralPath (Join-Path $env:FIXTURE_ROOT $env:FIXTURE_ARCHIVE) -Destination $destination
    if ($env:FIXTURE_MISSING -ne 'checksum') { Copy-Item -LiteralPath (Join-Path $env:FIXTURE_ROOT "$env:FIXTURE_ARCHIVE.sha256") -Destination $destination }
    exit 0
  }
  default { throw 'Unexpected gh invocation.' }
}
'@ | Set-Content -LiteralPath (Join-Path $bin 'gh-stub.ps1')
    @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0gh-stub.ps1" %*
exit /b %ERRORLEVEL%
'@ | Set-Content -LiteralPath (Join-Path $bin 'gh.cmd') -Encoding Ascii

    $env:PATH = "$bin;$env:PATH"
    $env:TEMP = $testRoot
    $env:TMP = $testRoot
    $env:FIXTURE_ROOT = $fixture
    $env:FIXTURE_ARCHIVE = $archiveName
    $env:ARG_LOG = Join-Path $testRoot 'arguments.txt'
    $env:SETUP_LOG = Join-Path $testRoot 'setup-arguments.txt'
    $env:GH_TOKEN = 'fixture-token'
    $arguments = @('-NoProfile', '-File', (Join-Path $repoRoot 'install.ps1'), '-Version', '1.2.3', '-InstallDir', (Join-Path $testRoot 'install path'), '-ConfigFile', (Join-Path $testRoot 'state file.json'), '-Name', 'relay service', '-RestartService')
    & $Shell @arguments
    if ($LASTEXITCODE -ne 0) { throw "Bootstrap failed with exit code $LASTEXITCODE." }
    $actual = Get-Content -LiteralPath $env:ARG_LOG
    foreach ($expected in @('install', '--install-dir', (Join-Path $testRoot 'install path'), '--config-file', (Join-Path $testRoot 'state file.json'), '--name', 'relay service', '--restart-service')) {
        if ($actual -cnotcontains $expected) { throw "Installer argument was not preserved: $expected" }
    }
    if (Test-Path -LiteralPath $env:SETUP_LOG) { throw 'Unattended install unexpectedly started setup.' }

    & $Shell @arguments -Defaults -AddToPath
    if ($LASTEXITCODE -ne 0) { throw "Default setup failed with exit code $LASTEXITCODE." }
    $setupActual = Get-Content -LiteralPath $env:SETUP_LOG
    if ($setupActual[0] -ine (Join-Path $testRoot 'install path\orelay.exe')) { throw 'Setup did not run from installed executable.' }
    foreach ($expected in @('setup', '--if-needed', '--config-file', (Join-Path $testRoot 'state file.json'), '--name', 'relay service', '--defaults', '--yes', '--add-to-path')) {
        if ($setupActual -cnotcontains $expected) { throw "Setup argument was not preserved: $expected" }
    }
    & $Shell @arguments -Defaults -SkipPath
    if ($LASTEXITCODE -ne 0) { throw "Skip PATH setup failed with exit code $LASTEXITCODE." }
    $setupActual = Get-Content -LiteralPath $env:SETUP_LOG
    if ($setupActual -cnotcontains '--skip-path' -or $setupActual -ccontains '--add-to-path') { throw 'Skip PATH option was not forwarded correctly.' }
    $env:FIXTURE_SETUP_EXIT = '23'
    $ErrorActionPreference = 'Continue'
    $failureOutput = & $Shell @arguments -Defaults 2>&1
    $failureCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($failureCode -ne 1 -or "$failureOutput" -notmatch 'setup failed with exit code 23') { throw 'Setup failure was not reported.' }
    Remove-Item Env:FIXTURE_SETUP_EXIT
    Remove-Item -LiteralPath $env:SETUP_LOG
    & $Shell @arguments -SkipSetup
    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $env:SETUP_LOG)) { throw 'Skip setup unexpectedly ran setup.' }
    $previousErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $conflictOutput = & $Shell @arguments -Defaults -SkipSetup 2>&1
    $conflictCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorPreference
    if ($conflictCode -eq 0) { throw 'Conflicting setup flags were accepted.' }
    foreach ($invalidFlags in @(@('-AddToPath', '-SkipPath'), @('-AddToPath', '-SkipSetup'), @('-SkipPath', '-SkipSetup'), @('-AddToPath'))) {
        Remove-Item -LiteralPath $env:ARG_LOG -ErrorAction SilentlyContinue
        $ErrorActionPreference = 'Continue'
        $invalidOutput = & $Shell @arguments @invalidFlags 2>&1
        $invalidCode = $LASTEXITCODE
        $ErrorActionPreference = $previousErrorPreference
        if ($invalidCode -eq 0 -or (Test-Path -LiteralPath $env:ARG_LOG)) { throw "Invalid PATH flags started download or install: $($invalidFlags -join ' ')" }
    }
    $env:FIXTURE_EXIT = '17'
    $ErrorActionPreference = 'Continue'
    $failureOutput = & $Shell @arguments 2>&1
    $failureCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($failureCode -ne 1 -or "$failureOutput" -notmatch 'installation failed with exit code 17') { throw 'Installation failure was not reported.' }
    Remove-Item Env:FIXTURE_EXIT

    # Exercise the pasted commands inside a caller, where exit would close its shell.
    $env:BOOTSTRAP_SOURCE = Join-Path $repoRoot 'install.ps1'
    $caller = Join-Path $testRoot 'caller.ps1'
    @'
param([string] $Mode, [string] $ExpectedError)
$ErrorActionPreference = 'Continue'
$env:LOCALAPPDATA = $env:FIXTURE_ROOT
$source = Get-Content -Raw -LiteralPath $env:BOOTSTRAP_SOURCE
$caught = $null
try {
    if ($Mode -eq 'iex') {
        $source | Invoke-Expression
    } else {
        & ([scriptblock]::Create($source)) -InstallDir (Join-Path $env:FIXTURE_ROOT 'scoped install') -Defaults -SkipPath
    }
} catch { $caught = $_.Exception.Message }
if ($ExpectedError -and $caught -notlike "*$ExpectedError*") { throw "Missing expected failure: $caught" }
if (-not $ExpectedError -and $caught) { throw $caught }
if ($ErrorActionPreference -ne 'Continue') { throw 'Installer changed caller error preferences.' }
if (Get-Command Get-RuntimeId -ErrorAction SilentlyContinue) { throw 'Installer leaked helper functions.' }
Write-Output 'caller-alive'
exit 0
'@ | Set-Content -LiteralPath $caller
    foreach ($mode in @('iex', 'scriptblock')) {
        foreach ($failure in @('', 'install', 'setup')) {
            if ($mode -eq 'iex' -and $failure -eq 'setup') { continue }
            $expectedError = ''
            if ($failure -eq 'install') { $env:FIXTURE_EXIT = '17'; $expectedError = 'installation failed with exit code 17' }
            if ($failure -eq 'setup') { $env:FIXTURE_SETUP_EXIT = '23'; $expectedError = 'setup failed with exit code 23' }
            $callerArguments = @('-NoProfile', '-File', $caller, '-Mode', $mode)
            if ($expectedError) { $callerArguments += @('-ExpectedError', $expectedError) }
            $callerOutput = & $Shell @callerArguments
            if ($LASTEXITCODE -ne 0 -or $callerOutput -cnotcontains 'caller-alive') { throw "The $mode caller did not survive $failure installation." }
            Remove-Item Env:FIXTURE_EXIT, Env:FIXTURE_SETUP_EXIT -ErrorAction SilentlyContinue
        }
    }

    $env:FIXTURE_TAG = 'v0.1.2'
    Remove-Item -LiteralPath $env:ARG_LOG -ErrorAction SilentlyContinue
    $oldArguments = @('-NoProfile', '-File', (Join-Path $repoRoot 'install.ps1'), '-Version', '0.1.2', '-InstallDir', (Join-Path $testRoot 'old release'))
    $ErrorActionPreference = 'Continue'
    $failureOutput = & $Shell @oldArguments 2>&1
    $failureCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($failureCode -eq 0 -or "$failureOutput" -notmatch 'predates the installer' -or (Test-Path -LiteralPath $env:ARG_LOG) -or (Test-Path -LiteralPath (Join-Path $testRoot 'old release'))) { throw 'Old release was not rejected before installation.' }
    Remove-Item Env:FIXTURE_TAG

    $env:FIXTURE_MISSING = 'archive'
    Remove-Item -LiteralPath $env:ARG_LOG -ErrorAction SilentlyContinue
    $ErrorActionPreference = 'Continue'
    $failureOutput = & $Shell @arguments 2>&1
    $failureCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorPreference
    if ($failureCode -eq 0 -or (Test-Path -LiteralPath $env:ARG_LOG)) { throw 'Missing asset unexpectedly ran the installer.' }
    Remove-Item Env:FIXTURE_MISSING

    Set-Content -LiteralPath "$archivePath.sha256" -Value (('0' * 64) + "  $archiveName") -NoNewline
    $env:FIXTURE_BAD_CHECKSUM = '1'
    Remove-Item -LiteralPath $env:ARG_LOG -ErrorAction SilentlyContinue
    $ErrorActionPreference = 'Continue'
    $failureOutput = & $Shell @arguments 2>&1
    $failureCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorPreference
    if ($failureCode -eq 0 -or (Test-Path -LiteralPath $env:ARG_LOG)) { throw 'Checksum failure unexpectedly ran the installer.' }
    foreach ($mode in @('iex', 'scriptblock')) {
        $callerOutput = & $Shell -NoProfile -File $caller -Mode $mode -ExpectedError 'checksum does not match'
        if ($LASTEXITCODE -ne 0 -or $callerOutput -cnotcontains 'caller-alive') { throw "The $mode caller did not survive a checksum failure." }
    }
    if (@(Get-ChildItem -LiteralPath $testRoot -Directory -Filter 'orelay-bootstrap-*').Count -gt 0) { throw 'Bootstrap left a temporary download directory behind.' }
    Write-Output 'Windows bootstrap fixture checks passed.'
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
    Remove-Item Env:GH_TOKEN, Env:FIXTURE_ROOT, Env:FIXTURE_ARCHIVE, Env:FIXTURE_MISSING, Env:FIXTURE_BAD_CHECKSUM, Env:FIXTURE_EXIT, Env:FIXTURE_SETUP_EXIT, Env:ARG_LOG, Env:SETUP_LOG, Env:BOOTSTRAP_SOURCE, Env:FIXTURE_TAG -ErrorAction SilentlyContinue
}
exit 0
