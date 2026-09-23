[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
$hostOs = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { throw 'Unsupported SQLite build host.' }
$hostArch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($RuntimeIdentifier -ne "$hostOs-$hostArch") {
    throw "SQLite static builds require the matching native host: $hostOs-$hostArch."
}
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = Join-Path $repo 'artifacts/sqlite-source/3.53.3'
$archive = Join-Path $sourceRoot 'sqlite-amalgamation-3530300.zip'
$source = Join-Path $sourceRoot 'sqlite-amalgamation-3530300/sqlite3.c'
$nativeDir = Join-Path $repo "artifacts/native/$RuntimeIdentifier"
$expectedArchiveSha256 = '646421E12AAC110282EF8CC68F1A62D4BB15FC7B8F09DA0B53E29EE690500431'
$expectedSourceSha256 = '87497AB605BEDD0DBEE27A209C1EEFF8C89B229B13F921A7EFDBB81A13F779FD'

New-Item -ItemType Directory -Force -Path $sourceRoot, $nativeDir | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest 'https://www.sqlite.org/2026/sqlite-amalgamation-3530300.zip' -OutFile $archive
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash -ne $expectedArchiveSha256) {
    throw 'The SQLite source archive checksum does not match the pinned release.'
}
if (-not (Test-Path -LiteralPath $source)) {
    Expand-Archive -LiteralPath $archive -DestinationPath $sourceRoot -Force
}
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash -ne $expectedSourceSha256) {
    throw 'The SQLite amalgamation checksum does not match the pinned release.'
}

if ($IsWindows) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ Build Tools are required.' }
    $component = if ($hostArch -eq 'arm64') { 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' } else { 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64' }
    $vs = (& $vswhere -latest -products '*' -requires $component -property installationPath).Trim()
    if (-not $vs) { throw 'Visual Studio C++ Build Tools are required.' }
    $vsdev = Join-Path $vs 'Common7/Tools/VsDevCmd.bat'
    $arch = if ($hostArch -eq 'arm64') { 'arm64' } else { 'amd64' }
    $object = Join-Path $nativeDir 'sqlite3.obj'
    $library = Join-Path $nativeDir 'e_sqlite3.lib'
    $command = "call `"$vsdev`" -arch=$arch -host_arch=$arch >nul && cl /nologo /O2 /DSQLITE_THREADSAFE=1 /DSQLITE_OMIT_LOAD_EXTENSION /c `"$source`" /Fo`"$object`" && lib /nologo /OUT:`"$library`" `"$object`""
    cmd /d /s /c $command
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else {
    $object = Join-Path $nativeDir 'sqlite3.o'
    $library = Join-Path $nativeDir 'libe_sqlite3.a'
    & cc -O2 -fPIC -DSQLITE_THREADSAFE=1 -DSQLITE_OMIT_LOAD_EXTENSION -c $source -o $object
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & ar rcs $library $object
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not (Test-Path -LiteralPath $library)) { throw 'SQLite static library was not created.' }
Write-Output $library
