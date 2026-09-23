param(
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [Parameter(Mandatory = $true)][string]$Rid
)

$ErrorActionPreference = 'Stop'

function Write-JsonEvidence {
    param([string]$Path, [object]$Value)
    $Value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8
}

function Invoke-CapturedProcess {
    param(
        [string]$Path,
        [string[]]$Arguments,
        [string]$MarkerPath
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Path
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $startInfo.EnvironmentVariables['ORELAY_PROBE_MARKER'] = $MarkerPath

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Could not start process: $Path" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            try { $process.Kill($true) } catch { }
            throw "Process timed out after 30 seconds: $Path"
        }
        $process.WaitForExit()
        return [pscustomobject]@{
            exitCode = $process.ExitCode
            stdout = $stdoutTask.Result
            stderr = $stderrTask.Result
        }
    } finally {
        $process.Dispose()
    }
}

$runRootFull = [IO.Path]::GetFullPath($RunRoot)
$evidenceRoot = Join-Path $runRootFull 'evidence'
$stateParent = Join-Path $runRootFull 'install-safety-state'
New-Item -ItemType Directory -Path $evidenceRoot, $stateParent -Force | Out-Null
$stateRoot = Join-Path $stateParent ([Guid]::NewGuid().ToString('N'))
$stateRootFull = [IO.Path]::GetFullPath($stateRoot)
if (-not $stateRootFull.StartsWith(([IO.Path]::GetFullPath($stateParent) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Owned state path escaped its parent.'
}
New-Item -ItemType Directory -Path $stateRootFull | Out-Null

$fixtureDirectory = Join-Path $stateRootFull 'target'
New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
$fixtureName = if ($Rid.StartsWith('win-')) { 'orelay.exe' } else { 'orelay' }
$fixturePath = Join-Path $fixtureDirectory $fixtureName
$markerPath = Join-Path $stateRootFull 'fixture-executed.marker'
$evidencePath = Join-Path $evidenceRoot "install-safety-$Rid.json"
$serviceName = 'orelay-safety-' + [Guid]::NewGuid().ToString('N')

try {
    if ($Rid.StartsWith('win-')) {
        $source = @'
using System;
using System.IO;
using System.Reflection;
[assembly: AssemblyVersion("0.0.0.0")]
class InstallSafetyFixture {
    static int Main(string[] args) {
        string marker = Environment.GetEnvironmentVariable("ORELAY_PROBE_MARKER");
        if (!String.IsNullOrEmpty(marker)) File.WriteAllText(marker, "fixture executed");
        Console.WriteLine("{\"version\":\"0.0.0\"}");
        return 0;
    }
}
'@
        $sourcePath = Join-Path $stateRootFull 'fixture.cs'
        $compilerPath = Join-Path $stateRootFull 'compile-fixture.ps1'
        Set-Content -LiteralPath $sourcePath -Value $source -Encoding utf8
        @'
param([Parameter(Mandatory=$true)][string]$OutputPath, [Parameter(Mandatory=$true)][string]$SourcePath)
$ErrorActionPreference = 'Stop'
$sourceText = [IO.File]::ReadAllText($SourcePath)
Add-Type -TypeDefinition $sourceText -OutputType ConsoleApplication -OutputAssembly $OutputPath
'@ | Set-Content -LiteralPath $compilerPath -Encoding utf8
        $windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $compileResult = Invoke-CapturedProcess -Path $windowsPowerShell -Arguments @(
            '-NoProfile', '-File', $compilerPath, '-OutputPath', $fixturePath, '-SourcePath', $sourcePath
        ) -MarkerPath $markerPath
        if ($compileResult.exitCode -ne 0 -or -not (Test-Path -LiteralPath $fixturePath -PathType Leaf)) {
            throw "Windows PowerShell fixture compilation failed with exit code $($compileResult.exitCode)."
        }
    } else {
        @'
#!/bin/sh
if [ -n "$ORELAY_PROBE_MARKER" ]; then
  printf '%s' 'fixture executed' > "$ORELAY_PROBE_MARKER"
fi
printf '%s\n' '{"version":"0.0.0"}'
'@ | Set-Content -LiteralPath $fixturePath -Encoding ascii
        & chmod 755 $fixturePath
        if ($LASTEXITCODE -ne 0) { throw 'Could not mark the Unix fixture executable.' }
    }

    $positiveControl = Invoke-CapturedProcess -Path $fixturePath -Arguments @('--version') -MarkerPath $markerPath
    if ($positiveControl.exitCode -ne 0 -or -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw 'Authored fixture positive control did not execute and create its marker.'
    }
    Remove-Item -LiteralPath $markerPath
    $fixtureHashBefore = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash.ToLowerInvariant()

    $installer = [IO.Path]::GetFullPath($ExecutablePath)
    $installResult = Invoke-CapturedProcess -Path $installer -Arguments @(
        'install', '--install-dir', $fixtureDirectory, '--name', $serviceName, '--json'
    ) -MarkerPath $markerPath
    $fixtureHashAfter = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $json = $null
    try { $json = $installResult.stdout | ConvertFrom-Json } catch { }

    $proof = [pscustomobject]@{
        passed = $false
        rid = $Rid
        installerPath = $installer
        installerSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
        fixturePath = $fixturePath
        fixtureSha256Before = $fixtureHashBefore
        fixtureSha256After = $fixtureHashAfter
        serviceName = $serviceName
        positiveControl = $positiveControl
        positiveControlMarkerObserved = $true
        install = [pscustomobject]@{
            exitCode = $installResult.exitCode
            stdout = $installResult.stdout
            stderr = $installResult.stderr
            succeeded = if ($null -ne $json) { $json.succeeded } else { $null }
            errorCode = if ($null -ne $json) { $json.errorCode } else { $null }
        }
        markerPresentAfterInstall = Test-Path -LiteralPath $markerPath
    }
    try {
        if ($installResult.exitCode -ne 3) { throw "Installer refusal returned exit code $($installResult.exitCode), expected 3." }
        if ($null -eq $json -or $json.succeeded -ne $false -or $json.errorCode -ne 'InstallFailure') {
            throw 'Installer did not return succeeded=false with errorCode=InstallFailure.'
        }
        if (Test-Path -LiteralPath $markerPath) { throw 'Installer executed the unrelated target fixture.' }
        if ($fixtureHashBefore -cne $fixtureHashAfter) { throw 'Installer altered the unrelated target fixture bytes.' }
        $proof.passed = $true
    } catch {
        $proof | Add-Member -NotePropertyName failure -NotePropertyValue $_.Exception.Message
        Write-JsonEvidence -Path $evidencePath -Value $proof
        throw
    }
    Write-JsonEvidence -Path $evidencePath -Value $proof
} finally {
    $stateParentFull = [IO.Path]::GetFullPath($stateParent)
    if ($stateRootFull.StartsWith(($stateParentFull + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $stateRootFull -PathType Container)) {
        Remove-Item -LiteralPath $stateRootFull -Recurse -Force
    }
}
