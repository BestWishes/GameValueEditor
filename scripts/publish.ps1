[CmdletBinding()]
param(
    [string]$Version = "0.3.0-preview.11"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$publishDir = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "win-x64"))
$updaterPublishDir = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "updater-win-x64"))
$distDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "dist"))
$packageDir = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "package"))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $distDir "GameValueEditor-v$Version-win-x64.zip"))

foreach ($path in @($publishDir, $updaterPublishDir, $packageDir)) {
    if (-not $path.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the repository: $path"
    }
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

dotnet publish (Join-Path $repoRoot "src\GameValueEditor\GameValueEditor.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

dotnet publish (Join-Path $repoRoot "src\GameValueEditor.Updater\GameValueEditor.Updater.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $updaterPublishDir

if ($LASTEXITCODE -ne 0) { throw "updater dotnet publish failed." }

Copy-Item -LiteralPath (Join-Path $publishDir "GameValueEditor.exe") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $updaterPublishDir "GameValueEditor.Updater.exe") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $repoRoot "CONTRIBUTING.md") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $repoRoot "SECURITY.md") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $repoRoot "docs") -Destination $packageDir -Recurse

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host "Created: $archivePath"
