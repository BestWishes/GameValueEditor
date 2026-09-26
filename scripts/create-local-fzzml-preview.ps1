[CmdletBinding()]
param(
    [string]$ApplicationVersion = "0.3.0-preview.3",
    [string]$ModuleVersion = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sourceDirectory = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\package"))
$previewDirectory = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\local-fzzml-preview"))
$moduleArchive = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "dist\modules\GameValueEditor.Module.Fzzml-v$ModuleVersion.zip"))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "dist\GameValueEditor-v$ApplicationVersion-with-fzzml-local-test.zip"))

foreach ($path in @($previewDirectory, $archivePath)) {
    if (-not $path.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Local preview path escaped the repository: $path"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory "GameValueEditor.exe"))) {
    throw "Run scripts/publish.ps1 before creating the local preview."
}
if (-not (Test-Path -LiteralPath $moduleArchive)) {
    throw "Run scripts/publish-module.ps1 before creating the local preview."
}
if (Test-Path -LiteralPath $previewDirectory) {
    Remove-Item -LiteralPath $previewDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $previewDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $sourceDirectory "*") -Destination $previewDirectory -Recurse -Force

$moduleDirectory = Join-Path $previewDirectory "data\modules\packages\game.fzzml.inventory.v1\$ModuleVersion"
New-Item -ItemType Directory -Path $moduleDirectory -Force | Out-Null
Expand-Archive -LiteralPath $moduleArchive -DestinationPath $moduleDirectory -Force
$installed = [ordered]@{
    SchemaVersion = 1
    Modules = @([ordered]@{
        Id = "game.fzzml.inventory.v1"
        Version = $ModuleVersion
        InstalledUtc = [DateTime]::UtcNow
    })
}
$installedPath = Join-Path $previewDirectory "data\modules\installed.json"
$installed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $installedPath -Encoding utf8NoBOM

if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Compress-Archive -Path (Join-Path $previewDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal
Write-Host "Created local test package: $archivePath"
