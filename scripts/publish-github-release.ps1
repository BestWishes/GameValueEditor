[CmdletBinding()]
param(
    [ValidatePattern('^$|^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = '',

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$Tag,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ReleaseName,

    [Parameter(Mandatory)]
    [string]$AssetPath,

    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._/-]*$')]
    [string]$TargetCommitish = 'main',

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$RemoteName = 'origin',

    [string]$Body = '',
    [string]$ProxyUrl = '',
    [switch]$DisableProxy,
    [switch]$PushRefs,
    [switch]$VerifyOnly,

    [ValidateRange(1, 5)]
    [int]$MaximumUploadAttempts = 3,

    [ValidateRange(15, 600)]
    [int]$NoProgressTimeoutSeconds = 60,

    [ValidateRange(1, 1048576)]
    [int]$MinimumBytesPerSecond = 1024,

    [ValidateRange(60, 7200)]
    [int]$MaximumUploadSeconds = 1800
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($DisableProxy -and -not [string]::IsNullOrWhiteSpace($ProxyUrl)) {
    throw 'ProxyUrl and DisableProxy cannot be used together.'
}
if ($VerifyOnly -and $PushRefs) {
    throw 'VerifyOnly is read-only and cannot be combined with PushRefs.'
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRootPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
$resolvedAsset = (Resolve-Path -LiteralPath $AssetPath).Path
if (-not $resolvedAsset.StartsWith($repoRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release asset must stay inside the repository: $resolvedAsset"
}
$assetName = [IO.Path]::GetFileName($resolvedAsset)
if ($assetName -like '*complete-offline*') {
    throw 'Complete offline bundles are local-only and cannot be uploaded to GitHub Releases.'
}
if ($Tag -notmatch '^v(?<version>(?:0|[1-9][0-9]*)\.[0-9]\.[0-9])$') {
    throw "Release tag must be a decimal-counter version such as v0.4.4: $Tag"
}
. (Join-Path $PSScriptRoot 'versioning.ps1')
Assert-NextReleaseVersion -RepositoryRoot $repoRoot -Version $matches.version -AllowExistingTag
$expectedAssetName = "GameValueEditor-v$($matches.version)-win-x64.zip"
if ($assetName -cne $expectedAssetName) {
    throw "Release asset must be named exactly $expectedAssetName."
}
$localSize = (Get-Item -LiteralPath $resolvedAsset).Length
$localHash = (Get-FileHash -LiteralPath $resolvedAsset -Algorithm SHA256).Hash.ToLowerInvariant()
$headCommit = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve HEAD.' }
$packageProbeRoot = Join-Path ([IO.Path]::GetTempPath()) "gve-release-provenance-$([Guid]::NewGuid().ToString('N'))"
try {
    Expand-Archive -LiteralPath $resolvedAsset -DestinationPath $packageProbeRoot
    foreach ($binaryName in @('GameValueEditor.exe', 'GameValueEditor.Updater.exe')) {
        $binaryPath = Join-Path $packageProbeRoot $binaryName
        if (-not (Test-Path -LiteralPath $binaryPath)) { throw "Release package is missing $binaryName." }
        $productVersion = (Get-Item -LiteralPath $binaryPath).VersionInfo.ProductVersion
        if ($productVersion -notmatch "\+$([regex]::Escape($headCommit))$") {
            throw "$binaryName ProductVersion is not traceable to HEAD $headCommit`: $productVersion"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $packageProbeRoot) { Remove-Item -LiteralPath $packageProbeRoot -Recurse -Force }
}

function ConvertTo-ProxyUri {
    param([Parameter(Mandatory)][string]$Value)

    $candidate = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($candidate)) { return $null }
    if ($candidate -notmatch '^[a-zA-Z][a-zA-Z0-9+.-]*://') {
        $candidate = "http://$candidate"
    }
    $uri = $null
    if (-not [Uri]::TryCreate($candidate, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -notin @('http', 'https') -or [string]::IsNullOrWhiteSpace($uri.Host)) {
        throw "Unsupported proxy URL: $Value"
    }
    return $uri.AbsoluteUri.TrimEnd('/')
}

function Get-WindowsProxyUrl {
    if (-not $IsWindows) { return $null }
    try {
        $settings = Get-ItemProperty -LiteralPath `
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
        if ([int]$settings.ProxyEnable -ne 1 -or [string]::IsNullOrWhiteSpace([string]$settings.ProxyServer)) {
            return $null
        }
        $server = [string]$settings.ProxyServer
        if ($server.Contains(';') -or $server.Contains('=')) {
            $entries = @{}
            foreach ($part in $server.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
                $pair = $part.Split('=', 2)
                if ($pair.Count -eq 2) { $entries[$pair[0].Trim().ToLowerInvariant()] = $pair[1].Trim() }
            }
            $server = if ($entries.ContainsKey('https')) { $entries.https }
                elseif ($entries.ContainsKey('http')) { $entries.http }
                else { return $null }
        }
        return ConvertTo-ProxyUri $server
    }
    catch {
        Write-Warning "Unable to read the Windows proxy setting: $($_.Exception.Message)"
        return $null
    }
}

function Resolve-ProxyUrl {
    if ($DisableProxy) { return $null }
    if (-not [string]::IsNullOrWhiteSpace($ProxyUrl)) { return ConvertTo-ProxyUri $ProxyUrl }
    foreach ($name in @('HTTPS_PROXY', 'https_proxy', 'HTTP_PROXY', 'http_proxy', 'ALL_PROXY', 'all_proxy')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { return ConvertTo-ProxyUri $value }
    }
    return Get-WindowsProxyUrl
}

function Get-ProxyDisplay {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return 'direct' }
    $uri = [Uri]$Value
    $port = if ($uri.IsDefaultPort) { '' } else { ":$($uri.Port)" }
    return "$($uri.Scheme)://$($uri.Host)$port"
}

$resolvedProxy = Resolve-ProxyUrl
$proxyDisplay = Get-ProxyDisplay $resolvedProxy
Write-Host "Network route: $proxyDisplay"

function Invoke-GitHubApi {
    param(
        [Parameter(Mandatory)][ValidateSet('Get', 'Post', 'Delete')][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][hashtable]$Headers,
        [AllowNull()][string]$JsonBody = $null
    )

    $status = 0
    $parameters = @{
        Method = $Method
        Uri = $Uri
        Headers = $Headers
        SkipHttpErrorCheck = $true
        StatusCodeVariable = 'status'
        ErrorAction = 'Stop'
    }
    if (-not [string]::IsNullOrWhiteSpace($resolvedProxy)) {
        $parameters.Proxy = $resolvedProxy
    }
    else {
        $parameters.NoProxy = $true
    }
    if ($null -ne $JsonBody) {
        $parameters.Body = $JsonBody
        $parameters.ContentType = 'application/json; charset=utf-8'
    }
    $response = Invoke-RestMethod @parameters
    return [PSCustomObject]@{ Status = [int]$status; Body = $response }
}

function Invoke-GitCommand {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & git @Arguments
    return $LASTEXITCODE
}

function Resolve-GitHubRepository {
    $remoteUrl = (& git remote get-url $RemoteName).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remoteUrl)) {
        throw "Unable to read Git remote: $RemoteName"
    }

    $patterns = @(
        '^https?://github\.com/(?<repository>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?/?$',
        '^git@github\.com:(?<repository>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?/?$',
        '^ssh://git@(?:ssh\.)?github\.com(?::443)?/(?<repository>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?/?$'
    )
    $remoteRepository = ''
    foreach ($pattern in $patterns) {
        if ($remoteUrl -match $pattern) {
            $remoteRepository = $matches.repository
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($remoteRepository)) {
        throw "Remote $RemoteName is not a supported GitHub URL."
    }
    if (-not [string]::IsNullOrWhiteSpace($Repository) -and
        -not $Repository.Equals($remoteRepository, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Repository $Repository does not match Git remote $RemoteName ($remoteRepository)."
    }
    return $remoteRepository
}

function Assert-LocalReleaseRefs {
    $branchRef = "refs/heads/$TargetCommitish"
    $tagRef = "refs/tags/$Tag"
    $branchCommit = (& git rev-parse --verify "$branchRef^{commit}").Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branchCommit)) {
        throw "Local branch does not exist: $TargetCommitish"
    }
    $tagType = (& git cat-file -t $tagRef).Trim()
    if ($LASTEXITCODE -ne 0 -or $tagType -ne 'tag') {
        throw "Local tag $Tag must be an annotated tag."
    }
    $tagCommit = (& git rev-list -n 1 $tagRef).Trim()
    if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $branchCommit) {
        throw "Local tag $Tag does not point to $TargetCommitish."
    }
}

function Push-ReleaseRefs {
    Assert-LocalReleaseRefs

    $branchRef = "refs/heads/$TargetCommitish"
    $tagRef = "refs/tags/$Tag"

    $arguments = @()
    if (-not [string]::IsNullOrWhiteSpace($resolvedProxy)) {
        $arguments += @('-c', "http.proxy=$resolvedProxy")
    }
    $arguments += @('push', $RemoteName, "$branchRef`:$branchRef", "$tagRef`:$tagRef")
    Write-Host "Pushing $TargetCommitish and $Tag through $proxyDisplay ..."
    $exitCode = Invoke-GitCommand $arguments
    if ($exitCode -eq 0) { return 'configured remote' }

    $sshUrl = "ssh://git@ssh.github.com:443/$Repository.git"
    Write-Warning "Configured Git transport failed with code $exitCode; retrying through GitHub SSH 443."
    $exitCode = Invoke-GitCommand @('push', $sshUrl, "$branchRef`:$branchRef", "$tagRef`:$tagRef")
    if ($exitCode -ne 0) { throw "Git push failed through both configured transport and SSH 443 (code $exitCode)." }
    return 'ssh 443 fallback'
}

$Repository = Resolve-GitHubRepository
if (-not $VerifyOnly -and -not $PushRefs) { Assert-LocalReleaseRefs }
$pushTransport = if ($PushRefs) { Push-ReleaseRefs } else { 'not requested' }

$credentialLines = "protocol=https`nhost=github.com`n`n" | git credential fill
if ($LASTEXITCODE -ne 0) { throw 'Unable to read the GitHub credential.' }
$credential = @{}
foreach ($line in $credentialLines) {
    if ($line -match '^([^=]+)=(.*)$') { $credential[$matches[1]] = $matches[2] }
}
if (-not $credential.ContainsKey('password') -or [string]::IsNullOrWhiteSpace($credential.password)) {
    throw 'GitHub credential is unavailable.'
}

$headers = @{
    Authorization = "Bearer $($credential.password)"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'GameValueEditor-release-tool'
}
$apiRoot = "https://api.github.com/repos/$Repository"
$encodedTag = [Uri]::EscapeDataString($Tag)

$tagResponse = Invoke-GitHubApi -Method Get -Uri "$apiRoot/git/ref/tags/$encodedTag" -Headers $headers
if ($tagResponse.Status -eq 404) {
    throw "Remote tag $Tag does not exist. Push the verified annotated tag before publishing."
}
if ($tagResponse.Status -ne 200) {
    throw "Unable to read remote tag $Tag (HTTP $($tagResponse.Status))."
}

$releaseResponse = Invoke-GitHubApi -Method Get -Uri "$apiRoot/releases/tags/$encodedTag" -Headers $headers
if ($releaseResponse.Status -eq 404) {
    if ($VerifyOnly) { throw "Release $Tag does not exist." }
    $payload = @{
        tag_name = $Tag
        target_commitish = $TargetCommitish
        name = $ReleaseName
        body = $Body
        draft = $false
        prerelease = $false
    } | ConvertTo-Json
    $releaseResponse = Invoke-GitHubApi -Method Post -Uri "$apiRoot/releases" -Headers $headers -JsonBody $payload
    if ($releaseResponse.Status -ne 201) {
        throw "GitHub release creation failed with HTTP $($releaseResponse.Status)."
    }
}
elseif ($releaseResponse.Status -ne 200) {
    throw "GitHub release lookup failed with HTTP $($releaseResponse.Status)."
}
$release = $releaseResponse.Body

function Invoke-CurlDownload {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Destination
    )

    $curl = (Get-Command curl.exe -ErrorAction Stop).Source
    $config = @('header = "User-Agent: GameValueEditor-release-tool"')
    if (-not [string]::IsNullOrWhiteSpace($resolvedProxy)) {
        $escapedProxy = $resolvedProxy.Replace('\', '\\').Replace('"', '\"')
        $config += "proxy = `"$escapedProxy`""
    }
    else {
        $config += 'noproxy = "*"'
    }
    $arguments = @(
        '--config', '-', '--fail', '--show-error', '--progress-bar', '--location',
        '--connect-timeout', '15', '--max-time', $MaximumUploadSeconds.ToString(),
        '--speed-limit', $MinimumBytesPerSecond.ToString(),
        '--speed-time', $NoProgressTimeoutSeconds.ToString(),
        '--output', $Destination, $Uri
    )
    ($config -join "`n") | & $curl @arguments
    return $LASTEXITCODE
}

function Assert-RemoteAsset {
    param([Parameter(Mandatory)]$Asset)

    if ([string]$Asset.state -ne 'uploaded') {
        throw "Release asset $assetName is not complete (state: $($Asset.state))."
    }
    if ([long]$Asset.size -ne $localSize) {
        throw "Release asset $assetName has a different size. Increment the version; do not replace it."
    }
    $digest = if ($Asset.PSObject.Properties.Name -contains 'digest') { [string]$Asset.digest } else { '' }
    if ($digest -match '^sha256:([0-9a-fA-F]{64})$') {
        if ($matches[1].ToLowerInvariant() -ne $localHash) {
            throw "Release asset $assetName exists with a different SHA-256. Increment the version; do not replace it."
        }
        return
    }

    $temporaryFile = [IO.Path]::GetTempFileName()
    try {
        Write-Host "GitHub did not return a digest; downloading $assetName for verification ..."
        $exitCode = Invoke-CurlDownload -Uri ([string]$Asset.browser_download_url) -Destination $temporaryFile
        if ($exitCode -ne 0) { throw "Unable to download the release asset for verification (curl code $exitCode)." }
        $remoteHash = (Get-FileHash -LiteralPath $temporaryFile -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remoteHash -ne $localHash) {
            throw "Release asset $assetName exists with a different SHA-256. Increment the version; do not replace it."
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryFile) { Remove-Item -LiteralPath $temporaryFile -Force }
    }
}

function Get-Release {
    $response = Invoke-GitHubApi -Method Get -Uri "$apiRoot/releases/tags/$encodedTag" -Headers $headers
    if ($response.Status -ne 200) { throw "Unable to refresh release $Tag (HTTP $($response.Status))." }
    return $response.Body
}

function Remove-IncompleteAsset {
    param([Parameter(Mandatory)]$Asset)

    if ([string]$Asset.name -ne $assetName -or [string]$Asset.state -ne 'starter') {
        throw 'Refusing to delete an uploaded, unrelated, or unrecognized release asset.'
    }
    $response = Invoke-GitHubApi -Method Delete -Uri "$apiRoot/releases/assets/$($Asset.id)" -Headers $headers
    if ($response.Status -ne 204) {
        throw "Unable to remove incomplete release asset $assetName (HTTP $($response.Status))."
    }
    Write-Warning "Removed incomplete GitHub asset before retry: $assetName"
}

function Invoke-CurlUpload {
    param(
        [Parameter(Mandatory)][string]$UploadUri,
        [Parameter(Mandatory)][string]$ResponsePath
    )

    $curl = (Get-Command curl.exe -ErrorAction Stop).Source
    $token = [string]$credential.password
    $escapedToken = $token.Replace('\', '\\').Replace('"', '\"')
    $config = @(
        "header = `"Authorization: Bearer $escapedToken`"",
        'header = "Accept: application/vnd.github+json"',
        'header = "X-GitHub-Api-Version: 2022-11-28"',
        'header = "Content-Type: application/zip"',
        'header = "User-Agent: GameValueEditor-release-tool"'
    )
    if (-not [string]::IsNullOrWhiteSpace($resolvedProxy)) {
        $escapedProxy = $resolvedProxy.Replace('\', '\\').Replace('"', '\"')
        $config += "proxy = `"$escapedProxy`""
    }
    else {
        $config += 'noproxy = "*"'
    }
    $arguments = @(
        '--config', '-', '--fail-with-body', '--show-error', '--progress-bar',
        '--connect-timeout', '15', '--max-time', $MaximumUploadSeconds.ToString(),
        '--speed-limit', $MinimumBytesPerSecond.ToString(),
        '--speed-time', $NoProgressTimeoutSeconds.ToString(),
        '--request', 'POST', '--data-binary', "@$resolvedAsset",
        '--output', $ResponsePath, $UploadUri
    )
    ($config -join "`n") | & $curl @arguments
    return $LASTEXITCODE
}

$matchingAssets = @($release.assets | Where-Object { $_.name -eq $assetName })
if ($matchingAssets.Count -gt 1) {
    throw "Release $Tag contains duplicate assets named $assetName. Resolve the release manually."
}
$asset = if ($matchingAssets.Count -eq 1) { $matchingAssets[0] } else { $null }
if ($null -ne $asset -and [string]$asset.state -eq 'uploaded') {
    Assert-RemoteAsset $asset
}
elseif ($VerifyOnly) {
    throw "Release $Tag does not contain a complete $assetName asset."
}
else {
    if ($null -ne $asset) { Remove-IncompleteAsset $asset }
    $uploadBase = [string]$release.upload_url -replace '\{\?name,label\}$', ''
    $uploadUri = "${uploadBase}?name=$([Uri]::EscapeDataString($assetName))"
    $lastUploadError = ''
    for ($attempt = 1; $attempt -le $MaximumUploadAttempts; $attempt++) {
        $responsePath = [IO.Path]::GetTempFileName()
        try {
            Write-Host "Uploading $assetName ($localSize bytes), attempt $attempt/$MaximumUploadAttempts ..."
            $exitCode = Invoke-CurlUpload -UploadUri $uploadUri -ResponsePath $responsePath
            $lastUploadError = if ($exitCode -eq 0) {
                'upload request completed but the asset was not visible during verification'
            } else {
                "curl code $exitCode"
            }
        }
        finally {
            if (Test-Path -LiteralPath $responsePath) { Remove-Item -LiteralPath $responsePath -Force }
        }

        $release = Get-Release
        $candidate = @($release.assets | Where-Object { $_.name -eq $assetName }) | Select-Object -First 1
        if ($null -ne $candidate -and [string]$candidate.state -eq 'uploaded') {
            Assert-RemoteAsset $candidate
            $asset = $candidate
            break
        }
        if ($null -ne $candidate) { Remove-IncompleteAsset $candidate }
        if ($attempt -lt $MaximumUploadAttempts) {
            $delaySeconds = [Math]::Min([Math]::Pow(2, $attempt), 8)
            Write-Warning "Upload failed ($lastUploadError). Retrying in $delaySeconds seconds."
            Start-Sleep -Seconds $delaySeconds
        }
    }
    if ($null -eq $asset -or [string]$asset.state -ne 'uploaded') {
        throw "GitHub asset upload failed after $MaximumUploadAttempts attempts ($lastUploadError)."
    }
}

$verifiedRelease = Get-Release
$verifiedAsset = @($verifiedRelease.assets | Where-Object { $_.name -eq $assetName }) | Select-Object -First 1
if ($null -eq $verifiedAsset) { throw "Release $Tag lost asset $assetName during final verification." }
Assert-RemoteAsset $verifiedAsset

[PSCustomObject]@{
    ReleaseUrl = [string]$verifiedRelease.html_url
    AssetUrl = [string]$verifiedAsset.browser_download_url
    Sha256 = $localHash.ToUpperInvariant()
    Size = $localSize
    Proxy = $proxyDisplay
    PushTransport = $pushTransport
    Verified = $true
}
