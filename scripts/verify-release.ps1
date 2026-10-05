[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')] [string]$Version = "0.4.2"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$archivePath = Join-Path $repoRoot "dist\GameValueEditor-v$Version-win-x64.zip"
$updaterPath = Join-Path $repoRoot "artifacts\updater-win-x64\GameValueEditor.Updater.exe"

dotnet build (Join-Path $repoRoot "GameValueEditor.sln") -c Release
if ($LASTEXITCODE -ne 0) { throw "Release build failed with code $LASTEXITCODE." }

dotnet run --project (Join-Path $repoRoot "tests\GameValueEditor.SmokeTests\GameValueEditor.SmokeTests.csproj") `
    -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed with code $LASTEXITCODE." }

& (Join-Path $repoRoot "scripts\publish.ps1") -Version $Version
if ($LASTEXITCODE -ne 0) { throw "Publish failed with code $LASTEXITCODE." }

& (Join-Path $repoRoot "scripts\test-package-startup.ps1") -ArchivePath $archivePath
if ($LASTEXITCODE -ne 0) { throw "Packaged startup test failed with code $LASTEXITCODE." }

& (Join-Path $repoRoot "scripts\test-updater.ps1") -UpdaterPath $updaterPath
if ($LASTEXITCODE -ne 0) { throw "Updater sandbox test failed with code $LASTEXITCODE." }

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
Write-Host "Release verification passed: $archivePath"
Write-Host "SHA256: $hash"
