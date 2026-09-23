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
    File.WriteAllLines(Environment.GetEnvironmentVariable("ARG_LOG"), args);
    int code;
    return Int32.TryParse(Environment.GetEnvironmentVariable("FIXTURE_EXIT"), out code) ? code : 0;
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
    $env:GH_TOKEN = 'fixture-token'
    $arguments = @('-NoProfile', '-File', (Join-Path $repoRoot 'install.ps1'), '-Version', '1.2.3', '-InstallDir', (Join-Path $testRoot 'install path'), '-ConfigFile', (Join-Path $testRoot 'state file.json'), '-Name', 'relay service', '-RestartService')
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments
    if ($LASTEXITCODE -ne 0) { throw "Bootstrap failed with exit code $LASTEXITCODE." }
    $actual = Get-Content -LiteralPath $env:ARG_LOG
    foreach ($expected in @('install', '--install-dir', (Join-Path $testRoot 'install path'), '--config-file', (Join-Path $testRoot 'state file.json'), '--name', 'relay service', '--restart-service')) {
        if ($actual -cnotcontains $expected) { throw "Installer argument was not preserved: $expected" }
    }
    $env:FIXTURE_EXIT = '17'
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" @arguments
    if ($LASTEXITCODE -ne 17) { throw "Installer exit code was not passed through. Expected 17, got $LASTEXITCODE." }
    Remove-Item Env:FIXTURE_EXIT

    $env:FIXTURE_MISSING = 'archive'
    Remove-Item -LiteralPath $env:ARG_LOG -ErrorAction SilentlyContinue
    $previousErrorPreference = $ErrorActionPreference
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
    Remove-Item Env:GH_TOKEN, Env:FIXTURE_ROOT, Env:FIXTURE_ARCHIVE, Env:FIXTURE_MISSING, Env:FIXTURE_BAD_CHECKSUM, Env:FIXTURE_EXIT, Env:ARG_LOG -ErrorAction SilentlyContinue
}
