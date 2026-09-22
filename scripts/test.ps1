[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$solution = "$PSScriptRoot\..\ORelay.sln"
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$testArguments = @($solution, '--configuration', $Configuration, '--no-restore')
if ($NoBuild) { $testArguments += '--no-build' }
dotnet test @testArguments
exit $LASTEXITCODE
