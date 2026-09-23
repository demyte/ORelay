[CmdletBinding()]
param(
    [ValidatePattern('^[a-z0-9]+(?:-[a-z0-9]+)+$')]
    [string]$RuntimeIdentifier = 'win-x64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
$project = "$PSScriptRoot\..\src\ORelay\ORelay.csproj"
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = "$PSScriptRoot\..\artifacts\publish\$RuntimeIdentifier"
}

& "$PSScriptRoot/build-sqlite.ps1" -RuntimeIdentifier $RuntimeIdentifier
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet publish $project --configuration $Configuration --runtime $RuntimeIdentifier --self-contained true --output $OutputPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

exit 0
