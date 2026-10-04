[CmdletBinding()]
param(
    [string]$ApplicationVersion = "0.3.0",
    [string]$ModuleVersion = "2.0.1",
    [string]$ModuleRepository = "D:\MyOtherProjects\GameValueEditor-Modules"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$sourceDirectory = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\package"))
$packageDirectory = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\local-fzzml-package"))
$moduleArchive = [System.IO.Path]::GetFullPath((Join-Path $ModuleRepository "dist\GameValueEditor.Module.Fzzml-v$ModuleVersion.zip"))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "dist\GameValueEditor-v$ApplicationVersion-with-fzzml-local-test.zip"))

foreach ($path in @($packageDirectory, $archivePath)) {
    if (-not $path.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Local package path escaped the repository: $path"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory "GameValueEditor.exe"))) {
    throw "Run scripts/publish.ps1 before creating the local package."
}
if (-not (Test-Path -LiteralPath $moduleArchive)) {
    throw "Run GameValueEditor-Modules/scripts/publish-fzzml.ps1 before creating the local package."
}
if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $sourceDirectory "*") -Destination $packageDirectory -Recurse -Force

$moduleDirectory = Join-Path $packageDirectory "data\modules\packages\game.fzzml\$ModuleVersion"
New-Item -ItemType Directory -Path $moduleDirectory -Force | Out-Null
Expand-Archive -LiteralPath $moduleArchive -DestinationPath $moduleDirectory -Force
$installed = [ordered]@{
    SchemaVersion = 1
    Modules = @([ordered]@{
        Id = "game.fzzml"
        Version = $ModuleVersion
        InstalledUtc = [DateTime]::UtcNow
    })
}
$installedPath = Join-Path $packageDirectory "data\modules\installed.json"
$installed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $installedPath -Encoding utf8NoBOM

if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal
Write-Host "Created local test package: $archivePath"
