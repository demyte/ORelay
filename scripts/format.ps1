[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
dotnet format "$PSScriptRoot\..\ORelay.sln" --verify-no-changes --no-restore
exit $LASTEXITCODE
