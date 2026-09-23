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
    [pscustomobject]@{ tagName = 'v1.2.3'; assets = $assets; draft = $false; prerelease = $false } | ConvertTo-Json -Depth 5 -Compress
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
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments
    if ($LASTEXITCODE -ne 0) { throw "Bootstrap failed with exit code $LASTEXITCODE." }
    $actual = Get-Content -LiteralPath $env:ARG_LOG
    foreach ($expected in @('install', '--install-dir', (Join-Path $testRoot 'install path'), '--config-file', (Join-Path $testRoot 'state file.json'), '--name', 'relay service', '--restart-service')) {
        if ($actual -cnotcontains $expected) { throw "Installer argument was not preserved: $expected" }
    }
    if (Test-Path -LiteralPath $env:SETUP_LOG) { throw 'Unattended install unexpectedly started setup.' }

    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments -Defaults
    if ($LASTEXITCODE -ne 0) { throw "Default setup failed with exit code $LASTEXITCODE." }
    $setupActual = Get-Content -LiteralPath $env:SETUP_LOG
    if ($setupActual[0] -ine (Join-Path $testRoot 'install path\orelay.exe')) { throw 'Setup did not run from installed executable.' }
    foreach ($expected in @('setup', '--if-needed', '--config-file', (Join-Path $testRoot 'state file.json'), '--name', 'relay service', '--defaults', '--yes')) {
        if ($setupActual -cnotcontains $expected) { throw "Setup argument was not preserved: $expected" }
    }
    $env:FIXTURE_SETUP_EXIT = '23'
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments -Defaults
    if ($LASTEXITCODE -ne 23) { throw "Setup exit code was not passed through. Expected 23, got $LASTEXITCODE." }
    Remove-Item Env:FIXTURE_SETUP_EXIT
    Remove-Item -LiteralPath $env:SETUP_LOG
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments -SkipSetup
    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $env:SETUP_LOG)) { throw 'Skip setup unexpectedly ran setup.' }
    $previousErrorPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $conflictOutput = & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments -Defaults -SkipSetup 2>&1
    $conflictCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorPreference
    if ($conflictCode -eq 0) { throw 'Conflicting setup flags were accepted.' }
    $env:FIXTURE_EXIT = '17'
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments
    if ($LASTEXITCODE -ne 17) { throw "Installer exit code was not passed through. Expected 17, got $LASTEXITCODE." }
    Remove-Item Env:FIXTURE_EXIT

    $env:FIXTURE_MISSING = 'archive'
    Remove-Item -LiteralPath $env:ARG_LOG -ErrorAction SilentlyContinue
    $ErrorActionPreference = 'Continue'
    $failureOutput = & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments 2>&1
    $failureCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorPreference
    if ($failureCode -eq 0 -or (Test-Path -LiteralPath $env:ARG_LOG)) { throw 'Missing asset unexpectedly ran the installer.' }
    Remove-Item Env:FIXTURE_MISSING

    Set-Content -LiteralPath "$archivePath.sha256" -Value (('0' * 64) + "  $archiveName") -NoNewline
    $env:FIXTURE_BAD_CHECKSUM = '1'
    Remove-Item -LiteralPath $env:ARG_LOG -ErrorAction SilentlyContinue
    $ErrorActionPreference = 'Continue'
    $failureOutput = & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments 2>&1
    $failureCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorPreference
    if ($failureCode -eq 0 -or (Test-Path -LiteralPath $env:ARG_LOG)) { throw 'Checksum failure unexpectedly ran the installer.' }
    if (@(Get-ChildItem -LiteralPath $testRoot -Directory -Filter 'orelay-bootstrap-*').Count -gt 0) { throw 'Bootstrap left a temporary download directory behind.' }
    Write-Output 'Windows bootstrap fixture checks passed.'
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
    Remove-Item Env:GH_TOKEN, Env:FIXTURE_ROOT, Env:FIXTURE_ARCHIVE, Env:FIXTURE_MISSING, Env:FIXTURE_BAD_CHECKSUM, Env:FIXTURE_EXIT, Env:FIXTURE_SETUP_EXIT, Env:ARG_LOG, Env:SETUP_LOG -ErrorAction SilentlyContinue
}
exit 0
