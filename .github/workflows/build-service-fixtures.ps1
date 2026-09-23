[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$RunRoot,
    [Parameter(Mandatory = $true)] [string]$Rid,
    [Parameter(Mandatory = $true)] [string]$ExecutableName,
    [Parameter(Mandatory = $true)] [string]$ProductionExecutablePath
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$fixture = Join-Path $RunRoot 'service-fixture-source'
$output = Join-Path $RunRoot 'service-fixtures'
New-Item -ItemType Directory -Path $fixture, $output -Force | Out-Null
$productionHash = (Get-FileHash -LiteralPath $ProductionExecutablePath -Algorithm SHA256).Hash
$production = ((@(& $ProductionExecutablePath --version --json) -join '') | ConvertFrom-Json).version.Split('+')[0]
if ($LASTEXITCODE -ne 0 -or $production -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-|$)') {
    throw "Production executable did not report a parseable SemVer version: '$production'."
}
$major = [int]$Matches.major
$minor = [int]$Matches.minor
$patch = [int]$Matches.patch
$previousVersion = '0.0.1'
if ($major -eq 0 -and $minor -eq 0 -and $patch -le 1) {
    throw "Production version '$production' is not above the previous service fixture '$previousVersion'."
}
$nextVersion = "$($major + 1).0.0"
$failingVersion = "$($major + 2).0.0"

# Copy tracked and new project inputs. Fixture edits never touch the checkout.
foreach ($relative in @(git -C $repo ls-files --cached --others --exclude-standard -- src/ORelay)) {
    if ($relative -match '(^|/)(bin|obj)/') { continue }
    $source = Join-Path $repo $relative
    $target = Join-Path $fixture $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
}
$worker = Join-Path $fixture 'src/ORelay/Updating/ServiceAutoUpdateWorker.cs'
$originalWorker = Get-Content -LiteralPath $worker -Raw
$engineLine = 'var engine = new UpdateEngine(runtime: _runtime, services: _services);'
if ([regex]::Matches($originalWorker, [regex]::Escape($engineLine)).Count -ne 1) {
    throw 'The worker source no longer has the expected single fixture injection point.'
}
$fixtureWorker = $originalWorker.Replace($engineLine,
    'var engine = new UpdateEngine(http: new HttpClient(new ServiceAutoUpdateFixtureHandler()), runtime: _runtime, services: _services, token: "disposable-ci-fixture");')
Set-Content -LiteralPath $worker -Value $fixtureWorker
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ServiceAutoUpdateFixtureHandler.cs') `
    -Destination (Join-Path $fixture 'src/ORelay/Updating/ServiceAutoUpdateFixtureHandler.cs')
foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'NuGet.Config', 'global.json')) {
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination (Join-Path $fixture $name)
}
$nativeName = if ($Rid.StartsWith('win-')) { 'e_sqlite3.lib' } else { 'libe_sqlite3.a' }
$nativeSource = Join-Path $repo "artifacts/native/$Rid/$nativeName"
$nativeTarget = Join-Path $fixture "artifacts/native/$Rid/$nativeName"
New-Item -ItemType Directory -Path (Split-Path $nativeTarget -Parent) -Force | Out-Null
Copy-Item -LiteralPath $nativeSource -Destination $nativeTarget

$project = Join-Path $fixture 'src/ORelay/ORelay.csproj'
$evidence = Join-Path $RunRoot 'evidence'
function Publish-Fixture([string]$Name, [string]$Version) {
    $destination = Join-Path $output $Name
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $log = @(dotnet publish $project --configuration Release --runtime $Rid --self-contained true `
        --output $destination -p:PublishAot=true -p:PublishSingleFile=true `
        -p:StripSymbols=false "-p:MinVerVersionOverride=$Version" 2>&1)
    $log | Set-Content -LiteralPath (Join-Path $evidence "service-fixture-$Name-build.txt")
    if ($LASTEXITCODE -ne 0) { throw "Native service fixture '$Name' failed to publish." }
    $executable = Join-Path $destination $ExecutableName
    $reported = ((@(& $executable --version --json) -join '') | ConvertFrom-Json).version.Split('+')[0]
    if ($LASTEXITCODE -ne 0 -or $reported -cne $Version) {
        throw "Service fixture '$Name' reports version '$reported', expected '$Version'."
    }
    [ordered]@{ Version = $reported; Sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash; Path = $executable } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence "service-fixture-$Name.json")
}

Publish-Fixture 'previous' $previousVersion
Publish-Fixture 'next' $nextVersion

function New-ReleaseFixture([string]$Name, [string]$Version) {
    $payload = Join-Path $output $Name
    $archiveName = "orelay-$Version-$Rid" + $(if ($Rid.StartsWith('win-')) { '.zip' } else { '.tar.gz' })
    $archive = Join-Path $payload $archiveName
    $executable = Join-Path $payload $ExecutableName
    if ($Rid.StartsWith('win-')) {
        Compress-Archive -LiteralPath $executable -DestinationPath $archive
    }
    else {
        & tar -czf $archive -C $payload $ExecutableName
        if ($LASTEXITCODE -ne 0) { throw "Could not package service fixture '$Name'." }
    }
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksum = "$archive.sha256"
    Set-Content -LiteralPath $checksum -Value "$hash  $archiveName" -NoNewline
    [ordered]@{ Name = $archiveName; Archive = $archive; Checksum = $checksum; Sha256 = $hash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence "service-fixture-$Name-release.json")
}

$program = Join-Path $fixture 'src/ORelay/Program.cs'
@'
using ORelay;
using ORelay.Cli;
using ORelay.Updating;

// Verification-only candidate: startup fails only while the run-owned marker exists.
if (args.Contains("server", StringComparer.Ordinal) &&
    File.Exists(Path.Combine(AppContext.BaseDirectory, "failing-service-enable.marker")))
{
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "failing-service-start.marker"), "server startup reached");
    return 70;
}
if (args.Length > 0 && string.Equals(args[0], "__auto-update", StringComparison.Ordinal))
{
    if (args.Length != 3) return 3;
    return await new ServiceAutoUpdateWorker().RunAsync(args[1], args[2]);
}
return await CliApplication.ExecuteAsync(args, commandHandler: ApplicationCommands.ExecuteAsync);
'@ | Set-Content -LiteralPath $program
Publish-Fixture 'failing' $failingVersion
New-ReleaseFixture 'failing' $failingVersion
if ((Get-FileHash -LiteralPath $ProductionExecutablePath -Algorithm SHA256).Hash -cne $productionHash) {
    throw 'Fixture publishing changed the production executable.'
}
[ordered]@{ Version = $production; Sha256 = $productionHash; Path = $ProductionExecutablePath } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'service-fixture-production.json')
