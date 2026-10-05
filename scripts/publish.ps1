[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.[0-9]\.[0-9]$')] [string]$Version = "0.4.5"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot 'versioning.ps1')
Assert-ReleaseVersionContract
Assert-NextReleaseVersion -RepositoryRoot $repoRoot -Version $Version
$workingTree = @(& git -C $repoRoot status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $workingTree.Count -ne 0) {
    throw 'Formal application packages must be rebuilt from a clean committed working tree.'
}
$headCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
$appProjectVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src\GameValueEditor\GameValueEditor.csproj') -Raw -Encoding UTF8)).Project.PropertyGroup.Version
$updaterProjectVersion = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src\GameValueEditor.Updater\GameValueEditor.Updater.csproj') -Raw -Encoding UTF8)).Project.PropertyGroup.Version
if ($appProjectVersion -ne $Version -or $updaterProjectVersion -ne $Version) {
    throw "Application and updater project versions must both equal $Version."
}
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

foreach ($oldArchive in Get-ChildItem -LiteralPath $distDir -File -Filter "GameValueEditor-v*.zip") {
    if (-not [string]::Equals($oldArchive.FullName, $archivePath, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $oldArchive.FullName -Force
    }
}

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

foreach ($binary in @(
    (Join-Path $publishDir "GameValueEditor.exe"),
    (Join-Path $updaterPublishDir "GameValueEditor.Updater.exe"))) {
    $productVersion = (Get-Item -LiteralPath $binary).VersionInfo.ProductVersion
    if ($productVersion -notmatch "\+$([regex]::Escape($headCommit))$") {
        throw "Published binary ProductVersion is not traceable to HEAD $headCommit`: $productVersion"
    }
}

Copy-Item -LiteralPath (Join-Path $publishDir "GameValueEditor.exe") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $updaterPublishDir "GameValueEditor.Updater.exe") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $repoRoot "SECURITY.md") -Destination $packageDir

if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $archivePath -CompressionLevel Optimal

Write-Host "Created: $archivePath"
