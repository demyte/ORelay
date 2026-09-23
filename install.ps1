[CmdletBinding()]
param(
    [string] $Version,
    [string] $InstallDir,
    [string] $ConfigFile,
    [string] $Name,
    [switch] $RestartService
)

$ErrorActionPreference = 'Stop'
$NetTls12 = [Net.SecurityProtocolType]::Tls12
[Net.ServicePointManager]::SecurityProtocol = $NetTls12
$repo = 'demyte/ORelay'
$tag = $null
$rid = $null
$tempRoot = $null

function Get-RuntimeId {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This script is for Windows.' }
    $architecture = $null
    try {
        $runtimeInfo = [type]::GetType('System.Runtime.InteropServices.RuntimeInformation, System.Runtime.InteropServices.RuntimeInformation')
        if ($runtimeInfo) { $architecture = [string]$runtimeInfo.GetProperty('OSArchitecture').GetValue($null, $null) }
    } catch { }
    if (-not $architecture) {
        $architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    }
    switch ($architecture) {
        { $_ -in 'X64', 'AMD64', 'x86_64' } { $arch = 'x64' }
        { $_ -in 'Arm64', 'ARM64' } { $arch = 'arm64' }
        default { throw "Unsupported processor architecture: $architecture" }
    }
    return "win-$arch"
}

function Get-ReleaseInfo([string] $RequestedVersion, [bool] $UseGh) {
    if ($UseGh) {
        $target = if ($RequestedVersion) { "v$RequestedVersion" } else { $null }
        $arguments = @('release', 'view')
        if ($target) { $arguments += $target }
        $arguments += @('--repo', $repo, '--json', 'tagName,assets,isDraft,isPrerelease', '--jq', '{tagName: .tagName, assets: [.assets[] | {name: .name}], draft: .isDraft, prerelease: .isPrerelease}')
        $json = & gh @arguments 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the ORelay release from GitHub.' }
        $ghRelease = $json -join "`n" | ConvertFrom-Json
        return [pscustomobject]@{ tag_name = $ghRelease.tagName; assets = $ghRelease.assets; draft = $ghRelease.draft; prerelease = $ghRelease.prerelease }
    }
    $uri = if ($RequestedVersion) { "https://api.github.com/repos/$repo/releases/tags/v$RequestedVersion" } else { "https://api.github.com/repos/$repo/releases/latest" }
    $headers = @{ 'User-Agent' = 'ORelay-bootstrap'; 'Accept' = 'application/vnd.github+json' }
    $token = if ($env:GH_TOKEN) { $env:GH_TOKEN } else { $env:GITHUB_TOKEN }
    if ($token) { $headers.Authorization = "Bearer $token" }
    try { return Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -TimeoutSec 30 } catch { throw 'Could not read the ORelay release from GitHub.' }
}

function Save-ReleaseAsset($Release, [string] $AssetName, [string] $Destination, [bool] $UseGh) {
    if ($UseGh) {
        & gh release download $tag --repo $repo --pattern $AssetName --dir $tempRoot 2>$null
        if ($LASTEXITCODE -ne 0) { throw "Could not download release asset $AssetName." }
        return
    }
    $asset = @($Release.assets | Where-Object { $_.name -ceq $AssetName })
    if ($asset.Count -ne 1) { throw "Release $tag does not contain exactly one asset named $AssetName." }
    $headers = @{ 'User-Agent' = 'ORelay-bootstrap' }
    $token = if ($env:GH_TOKEN) { $env:GH_TOKEN } else { $env:GITHUB_TOKEN }
    if ($token -and $asset[0].url) {
        $apiUri = [Uri]$asset[0].url
        if ($apiUri.Host -cne 'api.github.com' -or $apiUri.Scheme -cne 'https') { throw "Could not download release asset $AssetName." }
        $request = [Net.HttpWebRequest]::Create($apiUri)
        $request.AllowAutoRedirect = $false
        $request.Method = 'GET'
        $request.UserAgent = 'ORelay-bootstrap'
        $request.Accept = 'application/octet-stream'
        $request.Timeout = 30000
        $request.ReadWriteTimeout = 30000
        $request.Headers['Authorization'] = "Bearer $token"
        $response = $null
        try { $response = $request.GetResponse() } catch [Net.WebException] { $response = $_.Exception.Response }
        if (-not $response) { throw "Could not download release asset $AssetName." }
        try {
            $status = [int]$response.StatusCode
            if ($status -ge 200 -and $status -lt 300) {
                $stream = $response.GetResponseStream()
                $file = [IO.File]::Create($Destination)
                try { $stream.CopyTo($file) } finally { $file.Dispose(); $stream.Dispose() }
                return
            }
            if ($status -lt 300 -or $status -ge 400) { throw "Could not download release asset $AssetName." }
            $redirect = [Uri]$response.Headers['Location']
            if (-not $redirect -or $redirect.Scheme -ne 'https') { throw "Could not download release asset $AssetName." }
        } finally { $response.Dispose() }
        try { Invoke-WebRequest -UseBasicParsing -Uri $redirect.AbsoluteUri -Headers $headers -OutFile $Destination -MaximumRedirection 5 -TimeoutSec 30 } catch { throw "Could not download release asset $AssetName." }
        return
    } else { $downloadUri = $asset[0].browser_download_url }
    try { Invoke-WebRequest -UseBasicParsing -Uri $downloadUri -Headers $headers -OutFile $Destination -MaximumRedirection 5 -TimeoutSec 30 } catch { throw "Could not download release asset $AssetName." }
}

try {
    $rid = Get-RuntimeId
    if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be a stable X.Y.Z release.' }
    if (-not $InstallDir) {
        if (-not $env:LOCALAPPDATA) { throw 'LOCALAPPDATA is not set.' }
        $InstallDir = Join-Path $env:LOCALAPPDATA 'ORelay'
    }
    $useGh = $false
    if (Get-Command gh -ErrorAction SilentlyContinue) {
        & gh auth status -h github.com 1>$null 2>$null
        $useGh = ($LASTEXITCODE -eq 0)
    }
    $release = Get-ReleaseInfo -RequestedVersion $Version -UseGh $useGh
    $tag = [string]$release.tag_name
    if ($tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw 'GitHub returned a release tag that is not a stable X.Y.Z version.' }
    if ($Version -and $Matches[1] -cne $Version) { throw 'GitHub returned a different version than requested.' }
    if ($release.draft -or $release.prerelease) { throw 'The selected ORelay release is not stable.' }
    $versionText = $Matches[1]
    $archive = "orelay-$versionText-$rid.zip"
    $checksum = "$archive.sha256"
    foreach ($assetName in @($archive, $checksum)) {
        if (@($release.assets | Where-Object { $_.name -ceq $assetName }).Count -ne 1) { throw "Release $tag does not contain exactly one asset named $assetName." }
    }
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("orelay-bootstrap-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    Save-ReleaseAsset $release $archive (Join-Path $tempRoot $archive) $useGh
    Save-ReleaseAsset $release $checksum (Join-Path $tempRoot $checksum) $useGh
    $archivePath = Join-Path $tempRoot $archive
    $checksumText = (Get-Content -Raw -LiteralPath (Join-Path $tempRoot $checksum)).Trim()
    $escapedName = [regex]::Escape($archive)
    if ($checksumText -notmatch "^(?<hash>[0-9a-fA-F]{64})\s+$escapedName$") { throw 'The release checksum has an invalid name or format.' }
    $expectedHash = $Matches.hash
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($expectedHash -ine $actualHash) { throw 'The release checksum does not match.' }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    $seenPaths = @()
    $executableEntry = $null
    try {
        foreach ($entry in $zip.Entries) {
            $path = $entry.FullName.Replace('\', '/')
            if ($path.StartsWith('/') -or $path -match '(^|/)\.\.(\/|$)' -or $path -match '^[A-Za-z]:') { throw 'The release archive contains an unsafe path.' }
            if ($seenPaths -contains $path) { throw 'The release archive contains duplicate paths.' }
            $seenPaths += $path
            $unixType = ([uint32]$entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixType -ne 0 -and $unixType -ne 0x8000 -and $unixType -ne 0x4000) { throw 'The release archive contains a link or special file.' }
            if ($path -ceq 'orelay.exe') { $executableEntry = $entry }
        }
        if (-not $executableEntry) { throw 'The release archive does not contain orelay.exe.' }
        $unpack = Join-Path $tempRoot 'unpacked'
        New-Item -ItemType Directory -Path $unpack | Out-Null
        $executable = Join-Path $unpack 'orelay.exe'
        $entryStream = $executableEntry.Open()
        $fileStream = [IO.File]::Create($executable)
        try { $entryStream.CopyTo($fileStream) } finally { $fileStream.Dispose(); $entryStream.Dispose() }
    } finally { $zip.Dispose() }

    $installerArgs = @('install', '--install-dir', $InstallDir)
    if ($ConfigFile) { $installerArgs += @('--config-file', $ConfigFile) }
    if ($Name) { $installerArgs += @('--name', $Name) }
    if ($RestartService) { $installerArgs += '--restart-service' }
    & $executable @installerArgs
    exit $LASTEXITCODE
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} finally {
    if ($tempRoot -and (Test-Path -LiteralPath $tempRoot)) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
