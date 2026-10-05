[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.[0-9]\.[0-9]$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$AssetPath,

    [ValidateRange(1, 100)] [int]$MinimumModuleHostApi = 2,
    [ValidateRange(1, 100)] [int]$MaximumModuleHostApi = 7,
    [ValidateRange(1, 100)] [int]$MaximumCatalogSchemaVersion = 5
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'versioning.ps1')
Assert-NextReleaseVersion -RepositoryRoot $repoRoot -Version $Version -AllowExistingTag

if ($MaximumModuleHostApi -lt $MinimumModuleHostApi) {
    throw 'MaximumModuleHostApi cannot be lower than MinimumModuleHostApi.'
}
$resolvedAsset = (Resolve-Path -LiteralPath $AssetPath).Path
$expectedAssetName = "GameValueEditor-v$Version-win-x64.zip"
if ([IO.Path]::GetFileName($resolvedAsset) -cne $expectedAssetName) {
    throw "Expected release asset $expectedAssetName."
}
$sourceCommit = (& git -C $repoRoot rev-list -n 1 "v$Version").Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw "Annotated release tag v$Version is missing."
}
$probeRoot = Join-Path ([IO.Path]::GetTempPath()) "gve-index-probe-$([Guid]::NewGuid().ToString('N'))"
try {
    Expand-Archive -LiteralPath $resolvedAsset -DestinationPath $probeRoot
    foreach ($binaryName in @('GameValueEditor.exe', 'GameValueEditor.Updater.exe')) {
        $productVersion = (Get-Item -LiteralPath (Join-Path $probeRoot $binaryName)).VersionInfo.ProductVersion
        if ($productVersion -notmatch "\+$([regex]::Escape($sourceCommit))$") {
            throw "$binaryName is not traceable to tag commit $sourceCommit`: $productVersion"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $probeRoot) { Remove-Item -LiteralPath $probeRoot -Recurse -Force }
}

$indexPath = Join-Path $repoRoot 'release-index.json'
$index = Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8 | ConvertFrom-Json
$newEntry = [pscustomobject]@{
    version = $Version
    assetName = $expectedAssetName
    downloadUrl = "https://github.com/BestWishes/GameValueEditor/releases/download/v$Version/$expectedAssetName"
    sizeBytes = (Get-Item -LiteralPath $resolvedAsset).Length
    sha256 = (Get-FileHash -LiteralPath $resolvedAsset -Algorithm SHA256).Hash
    sourceCommit = $sourceCommit
    minimumModuleHostApi = $MinimumModuleHostApi
    maximumModuleHostApi = $MaximumModuleHostApi
    maximumCatalogSchemaVersion = $MaximumCatalogSchemaVersion
}
$entries = @($newEntry) + @($index.releases | Where-Object { [string]$_.version -ne $Version })
$index.schemaVersion = 1
$index.releases = @($entries | Sort-Object { [version]$_.version } -Descending | Select-Object -First 3)
$index | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $indexPath -Encoding utf8NoBOM
if (-not ((Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8) |
        Test-Json -SchemaFile (Join-Path $repoRoot 'release-index.schema.json'))) {
    throw 'release-index.json failed schema validation.'
}
Write-Host "Updated: $indexPath"
