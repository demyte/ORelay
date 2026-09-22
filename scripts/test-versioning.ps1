[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$runId = "versioning-$([Guid]::NewGuid().ToString('N'))"
$scratch = Join-Path $repo "work/verification/$runId"
$evidence = Join-Path $repo ".artifacts/verification/$runId"
New-Item -ItemType Directory -Path $scratch, $evidence | Out-Null
$fixture = Join-Path $scratch 'repo'

function Invoke-CheckedGit([string[]]$Arguments) {
    $result = @(& git @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git operation failed: $($result -join [Environment]::NewLine)" }
}

function Check-Build([string]$Version, [string]$Tag) {
    $details = & './scripts/get-version.ps1' -Tag $Tag | ConvertFrom-Json
    if ($details.version -cne $Version) { throw "Expected version $Version; received $($details.version)." }
    $build = @(dotnet build src/ORelay/ORelay.csproj -c Release -p:PublishAot=false -p:SelfContained=false -p:PublishSingleFile=false -p:EnableRequestDelegateGenerator=true 2>&1)
    $build | Set-Content -LiteralPath (Join-Path $evidence "$Version-build.txt")
    if ($LASTEXITCODE -ne 0) { throw "Version fixture build failed for $Version." }
    $dll = Join-Path $fixture 'src/ORelay/bin/Release/net10.0/orelay.dll'
    $actual = @(& dotnet $dll --version --json) -join '' | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $actual.version -cne $details.informationalVersion) { throw 'Built CLI did not report the tagged version and commit.' }
    $stamp = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
    if ($stamp.FileVersion -cne $details.fileVersion -or $stamp.ProductVersion -cne $details.informationalVersion -or
        [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString() -cne $details.assemblyVersion) {
        throw 'Built DLL version stamps disagree with the calculated version.'
    }
    $packageOutput = Join-Path $scratch "packages/$Version"
    $pack = @(dotnet pack src/ORelay.Aspire.Hosting -c Release -o $packageOutput 2>&1)
    $pack | Set-Content -LiteralPath (Join-Path $evidence "$Version-pack.txt")
    if ($LASTEXITCODE -ne 0) { throw 'Version fixture package build failed.' }
    $package = Join-Path $packageOutput "ORelay.Aspire.Hosting.$Version.nupkg"
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $reader = [IO.StreamReader]::new($archive.GetEntry('ORelay.Aspire.Hosting.nuspec').Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($manifest.package.metadata.version -cne $Version) { throw 'NuGet package does not use the tag version.' }
    } finally { $archive.Dispose() }
    $details | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence "$Version.json")
}

Push-Location $repo
try {
    Invoke-CheckedGit @('init', '--quiet', '--initial-branch=main', $fixture)
    # Copy current source edits too, so this check verifies the working tree
    # before its changes are committed. Build outputs and evidence are ignored.
    $sourceFiles = @(git ls-files --cached --others --exclude-standard)
    foreach ($relative in $sourceFiles) {
        $source = Join-Path $repo $relative
        $target = Join-Path $fixture $relative
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $target -Force
        } elseif (Test-Path -LiteralPath $target -PathType Leaf) {
            Remove-Item -LiteralPath $target
        }
    }
    Set-Location $fixture
    Invoke-CheckedGit @('config', 'user.name', 'ORelay version verification')
    Invoke-CheckedGit @('config', 'user.email', 'verification@example.invalid')
    Invoke-CheckedGit @('add', '--all')
    Invoke-CheckedGit @('commit', '--quiet', '--allow-empty', '-m', 'Version verification fixture')
    Invoke-CheckedGit @('update-ref', 'refs/remotes/origin/main', 'HEAD')
    Invoke-CheckedGit @('tag', '-a', 'v0.8.3-rc.1', '-m', 'Annotated prerelease fixture')
    Check-Build '0.8.3-rc.1' 'v0.8.3-rc.1'
    Invoke-CheckedGit @('tag', 'v0.8.3')
    Check-Build '0.8.3' 'v0.8.3'
    foreach ($invalid in @('v01.2.3', 'v1.2.3-01', 'v1.2.3+build', 'V1.2.3', 'v1.2', 'v65535.0.0')) {
        $rejected = $false
        try { & './scripts/get-version.ps1' -Tag $invalid | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw 'Invalid release tag was accepted.' }
    }
    Add-Content -LiteralPath 'README.md' -Value 'Uncommitted release fixture edit.'
    $rejected = $false
    try { & './scripts/get-version.ps1' -Tag 'v0.8.3' | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'An uncommitted source change was accepted for release.' }
    Invoke-CheckedGit @('restore', '--', 'README.md')
    Set-Content -LiteralPath 'src/ORelay/UntrackedReleaseFixture.cs' -Value '// Untracked release fixture source.'
    $rejected = $false
    try { & './scripts/get-version.ps1' -Tag 'v0.8.3' | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'An untracked source file was accepted for release.' }
    Remove-Item -LiteralPath 'src/ORelay/UntrackedReleaseFixture.cs'
    Invoke-CheckedGit @('commit', '--quiet', '--allow-empty', '-m', 'Development after release')
    Check-Build '0.8.4-dev.0.1' ''
    $rejected = $false
    try { & './scripts/get-version.ps1' -Tag 'v0.8.3' | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'A tag from a different commit was accepted for release.' }
    Invoke-CheckedGit @('tag', 'v0.8.4-rc.1')
    $rejected = $false
    try { & './scripts/get-version.ps1' -Tag 'v0.8.4-rc.1' | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'A release commit outside origin/main was accepted.' }
    Write-Output "Stable, prerelease, development, and invalid-tag checks passed. Evidence: $evidence"
} finally {
    Pop-Location
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repo 'work/verification')) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($scratch).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe version fixture cleanup path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
