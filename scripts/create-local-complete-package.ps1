[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string]$ApplicationVersion = "0.4.1",
    [string]$ModuleRepository = "D:\MyOtherProjects\GameValueEditor-Modules",
    [string[]]$ModuleIds = @()
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$moduleRoot = [System.IO.Path]::GetFullPath($ModuleRepository)
$sourceDirectory = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\package"))
$packageDirectory = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\local-complete-package"))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "dist\GameValueEditor-v$ApplicationVersion-complete-offline-win-x64.zip"))
$catalogPath = Join-Path $moduleRoot "catalog.json"
$moduleCache = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\module-cache"))

foreach ($path in @($packageDirectory, $archivePath)) {
    if (-not $path.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Local package path escaped the host repository: $path"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory "GameValueEditor.exe"))) {
    throw "Run scripts/publish.ps1 before creating the complete local package."
}
if (Test-Path -LiteralPath (Join-Path $sourceDirectory "data")) {
    throw "The host package source already contains portable user data."
}
if (-not (Test-Path -LiteralPath $catalogPath)) {
    throw "Module catalog not found: $catalogPath"
}

$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$selectedModules = if ($ModuleIds.Count -eq 0) {
    @($catalog.modules)
} else {
    @($ModuleIds | ForEach-Object {
        $requestedId = $_
        $entry = $catalog.modules | Where-Object id -eq $requestedId | Select-Object -First 1
        if ($null -eq $entry) { throw "Module is not present in catalog.json: $requestedId" }
        $entry
    })
}
if ($selectedModules.Count -eq 0) { throw "No modules were selected for the complete package." }

if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $sourceDirectory "*") -Destination $packageDirectory -Recurse -Force

$installedModules = @()
foreach ($module in $selectedModules) {
    if ($module.id -notmatch '^game\.[a-z0-9.-]+$' -or $module.version -notmatch '^\d+\.\d+\.\d+$') {
        throw "Catalog contains an unsafe module identity: $($module.id) v$($module.version)"
    }
    $assetName = [System.IO.Path]::GetFileName(([Uri]$module.downloadUrl).AbsolutePath)
    $localArchive = [System.IO.Path]::GetFullPath((Join-Path $moduleRoot "dist\$assetName"))
    $cachedArchive = [System.IO.Path]::GetFullPath((Join-Path $moduleCache "$($module.sha256)-$assetName"))
    $moduleArchive = $null
    foreach ($candidate in @($localArchive, $cachedArchive)) {
        if (Test-Path -LiteralPath $candidate) {
            $candidateHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
            if ([string]::Equals($candidateHash, [string]$module.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                $moduleArchive = $candidate
                break
            }
        }
    }
    if ($null -eq $moduleArchive) {
        New-Item -ItemType Directory -Path $moduleCache -Force | Out-Null
        $temporaryDownload = "$cachedArchive.tmp"
        if (Test-Path -LiteralPath $temporaryDownload) { Remove-Item -LiteralPath $temporaryDownload -Force }
        try {
            Invoke-WebRequest -Uri $module.downloadUrl -OutFile $temporaryDownload -Headers @{ "User-Agent" = "GameValueEditor-local-bundle" }
            $downloadHash = (Get-FileHash -LiteralPath $temporaryDownload -Algorithm SHA256).Hash
            if (-not [string]::Equals($downloadHash, [string]$module.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Downloaded module hash does not match catalog.json: $assetName"
            }
            Move-Item -LiteralPath $temporaryDownload -Destination $cachedArchive -Force
            $moduleArchive = $cachedArchive
        } finally {
            if (Test-Path -LiteralPath $temporaryDownload) { Remove-Item -LiteralPath $temporaryDownload -Force }
        }
    }

    $moduleDirectory = [System.IO.Path]::GetFullPath(
        (Join-Path $packageDirectory "data\modules\packages\$($module.id)\$($module.version)"))
    $modulesRoot = [System.IO.Path]::GetFullPath((Join-Path $packageDirectory "data\modules\packages")) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not ($moduleDirectory + [System.IO.Path]::DirectorySeparatorChar).StartsWith(
            $modulesRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Module destination escaped the complete package: $moduleDirectory"
    }
    New-Item -ItemType Directory -Path $moduleDirectory -Force | Out-Null
    Expand-Archive -LiteralPath $moduleArchive -DestinationPath $moduleDirectory -Force
    $packagedManifest = Get-Content -LiteralPath (Join-Path $moduleDirectory "module.json") -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if ($packagedManifest.id -ne $module.id -or $packagedManifest.version -ne $module.version) {
        throw "Packaged module manifest does not match catalog.json: $($module.id)"
    }
    $installedModules += [ordered]@{
        Id = [string]$module.id
        Version = [string]$module.version
        InstalledUtc = [DateTime]::UtcNow
    }
}

$installedPath = Join-Path $packageDirectory "data\modules\installed.json"
[ordered]@{ SchemaVersion = 1; Modules = $installedModules } |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath $installedPath -Encoding utf8NoBOM
@(
    "这是本地完整离线包。"
    "内置模块仅支持各自 module.json 声明并经过验证的游戏构建。"
    "应用更新和未来模块更新仍可能需要访问 GitHub。"
) | Set-Content -LiteralPath (Join-Path $packageDirectory "OFFLINE-PACKAGE.txt") -Encoding UTF8

if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal
Write-Host "Created local complete package: $archivePath"
Write-Host "Modules: $($selectedModules.id -join ', ')"
Write-Host "SHA256: $((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash)"
