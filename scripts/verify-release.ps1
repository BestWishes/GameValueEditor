[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.[0-9]\.[0-9]$')] [string]$Version = "0.5.1"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$archivePath = Join-Path $repoRoot "dist\GameValueEditor-v$Version-win-x64.zip"

& (Join-Path $repoRoot "scripts\test-release-retention.ps1")
& (Join-Path $repoRoot "scripts\test-application-package.ps1")
& (Join-Path $repoRoot "scripts\test-offline-bundle.ps1")

dotnet build (Join-Path $repoRoot "GameValueEditor.sln") -c Release
if ($LASTEXITCODE -ne 0) { throw "Release build failed with code $LASTEXITCODE." }

dotnet run --project (Join-Path $repoRoot "tests\GameValueEditor.SmokeTests\GameValueEditor.SmokeTests.csproj") `
    -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed with code $LASTEXITCODE." }

& (Join-Path $repoRoot "scripts\publish.ps1") -Version $Version

& (Join-Path $repoRoot "scripts\test-package-startup.ps1") -ArchivePath $archivePath

# publish.ps1 tests the candidate updater before promoting the ZIP.

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
Write-Host "Release verification passed: $archivePath"
Write-Host "SHA256: $hash"
