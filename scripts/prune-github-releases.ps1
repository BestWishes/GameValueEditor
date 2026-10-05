[CmdletBinding(SupportsShouldProcess)]
param([string]$ProxyUrl = 'http://127.0.0.1:7897')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'versioning.ps1')
. (Join-Path $PSScriptRoot 'release-retention.ps1')

$remoteUrl = (& git -C $repoRoot remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or $remoteUrl -notmatch 'github\.com[/:]BestWishes/GameValueEditor(?:\.git)?$') {
    throw 'origin is not the expected BestWishes/GameValueEditor repository.'
}
$credentialLines = "protocol=https`nhost=github.com`n`n" | git credential fill
if ($LASTEXITCODE -ne 0) { throw 'Unable to read the GitHub credential.' }
$credential = @{}
foreach ($line in $credentialLines) { if ($line -match '^([^=]+)=(.*)$') { $credential[$matches[1]] = $matches[2] } }
if (-not $credential.ContainsKey('password')) { throw 'GitHub credential is unavailable.' }
$headers = @{
    Authorization = "Bearer $($credential.password)"
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'GameValueEditor-release-retention'
}
$request = @{ Headers = $headers; Proxy = $ProxyUrl; ErrorAction = 'Stop' }
$releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/BestWishes/GameValueEditor/releases?per_page=100' @request
$plan = Get-ReleaseRetentionPlan -Releases $releases -TagPrefix 'v' -ExpectedAssetName {
    param($version) "GameValueEditor-v$version-win-x64.zip"
}
foreach ($item in $plan.Delete) {
    $tag = [string]$item.Release.tag_name
    if ($PSCmdlet.ShouldProcess("GitHub Release $tag", 'Delete release and assets; preserve Git tag')) {
        Invoke-RestMethod -Method Delete -Uri "https://api.github.com/repos/BestWishes/GameValueEditor/releases/$($item.Release.id)" @request | Out-Null
    }
}
[pscustomobject]@{
    RetainedTags = @($plan.Keep | ForEach-Object { [string]$_.Release.tag_name })
    DeletedTags = @($plan.Delete | ForEach-Object { [string]$_.Release.tag_name })
    GitTagsPreserved = $true
}
