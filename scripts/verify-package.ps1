[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$packages = [IO.Path]::GetFullPath($PackageDirectory)
$packagePath = Join-Path $packages "ORelay.Aspire.Hosting.$ExpectedVersion.nupkg"
if (-not (Test-Path -LiteralPath $packagePath)) { throw 'Expected versioned hosting package is missing.' }
$scratch = Join-Path $repo "work/verification/package-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $scratch | Out-Null
Push-Location $repo
try {
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not identify the package source commit.' }
    [IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $scratch)
    [xml]$manifest = Get-Content -Raw -LiteralPath (Join-Path $scratch 'ORelay.Aspire.Hosting.nuspec')
    $metadata = $manifest.package.metadata
    if ($metadata.id -cne 'ORelay.Aspire.Hosting' -or $metadata.version -cne $ExpectedVersion -or
        $metadata.repository.url -cne 'https://github.com/demyte/ORelay' -or $metadata.repository.commit -cne $commit -or
        $metadata.license.InnerText -cne 'MIT') { throw 'Package identity, version, source commit, repository, or license metadata is incorrect.' }
    if (@($metadata.dependencies.group.dependency | Where-Object id -EQ 'MinVer').Count -ne 0) {
        throw 'The build-only versioning tool leaked into package dependencies.'
    }
    $dll = Join-Path $scratch 'lib/net10.0/ORelay.Aspire.Hosting.dll'
    $stamp = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
    $numeric = $ExpectedVersion.Split('-')[0] + '.0'
    if ($stamp.FileVersion -cne $numeric -or $stamp.ProductVersion -cne "$ExpectedVersion+$commit" -or
        [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString() -cne $numeric) {
        throw 'Packaged DLL stamps do not match the package version and source commit.'
    }
    $config = Join-Path $scratch 'NuGet.Config'
    $escapedPackages = [Security.SecurityElement]::Escape($packages)
    @"
<configuration>
  <packageSources><clear/><add key="local" value="$escapedPackages"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><packageSource key="local"><package pattern="ORelay.Aspire.Hosting"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $config
    # An isolated cache prevents an older package with the same version from
    # making this check pass without consuming the artifact under test.
    $cache = Join-Path $scratch 'cache'
    dotnet restore tests/ORelay.Aspire.Hosting.Tests --configfile $config --packages $cache -p:UseORelayPackage=true "-p:ORelayPackageVersion=$ExpectedVersion"
    if ($LASTEXITCODE -ne 0) { throw 'Package consumer restore failed.' }
    dotnet test tests/ORelay.Aspire.Hosting.Tests --configuration Release --no-restore -p:UseORelayPackage=true "-p:ORelayPackageVersion=$ExpectedVersion" --filter 'Category!=AspireIntegration'
    if ($LASTEXITCODE -ne 0) { throw 'Package consumer tests failed.' }
    Write-Output "Verified hosting package $ExpectedVersion, DLL stamps, and AppHost package consumption."
} finally {
    Pop-Location
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $repo 'work/verification')) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($scratch).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe package verification cleanup path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
