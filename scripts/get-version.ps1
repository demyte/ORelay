[CmdletBinding()]
param([string]$Tag = '')

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    if ($Tag) {
        $identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
        $pattern = "\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-$identifier(?:\.$identifier)*)?\z"
        if ($Tag -cnotmatch $pattern) {
            throw 'Release tags must be vMAJOR.MINOR.PATCH with optional SemVer prerelease identifiers and no build metadata.'
        }
        foreach ($part in $Tag.Substring(1).Split('-')[0].Split('.')) {
            if ([long]$part -gt 65534) { throw 'Each numeric version component must fit the .NET assembly limit of 65534.' }
        }
        $tagCommit = git rev-parse --verify "refs/tags/${Tag}^{commit}"
        if ($LASTEXITCODE -ne 0) { throw 'Release tag does not exist locally. Fetch complete history and tags.' }
        $headCommit = git rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $tagCommit -cne $headCommit) { throw 'Release tag must point to the checked-out commit.' }
        git merge-base --is-ancestor HEAD refs/remotes/origin/main
        if ($LASTEXITCODE -ne 0) { throw 'Release commit must be part of origin/main.' }
        $changes = git status --porcelain --untracked-files=all
        if ($LASTEXITCODE -ne 0 -or $changes) { throw 'Release builds require an unchanged checkout.' }
    }

    $restore = @(dotnet restore src/ORelay/ORelay.csproj --verbosity quiet 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Version restore failed: $($restore -join [Environment]::NewLine)" }
    $output = @(dotnet msbuild src/ORelay/ORelay.csproj -target:ORelayGetVersion -getProperty:Version,AssemblyVersion,FileVersion,InformationalVersion,SourceRevisionId)
    if ($LASTEXITCODE -ne 0) { throw 'Version calculation failed.' }
    $properties = ($output -join [Environment]::NewLine | ConvertFrom-Json).Properties
    if ($Tag -and $properties.Version -cne $Tag.Substring(1)) {
        throw 'Calculated version does not match the requested tag. Check for competing version tags on this commit.'
    }
    [ordered]@{
        version = $properties.Version
        assemblyVersion = $properties.AssemblyVersion
        fileVersion = $properties.FileVersion
        informationalVersion = $properties.InformationalVersion
        commit = $properties.SourceRevisionId
    } | ConvertTo-Json -Compress
} finally { Pop-Location }
