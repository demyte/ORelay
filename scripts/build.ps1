[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$solution = "$PSScriptRoot\..\ORelay.sln"
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet build $solution --configuration $Configuration --no-restore
exit $LASTEXITCODE
