[CmdletBinding()]
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\modules\fzzml"))
$buildDir = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "build"))
$packageDir = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "package"))
$distDir = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "dist\modules"))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $distDir "GameValueEditor.Module.Fzzml-v$Version.zip"))

foreach ($path in @($buildDir, $packageDir)) {
    if (-not $path.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the repository: $path"
    }
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

dotnet build (Join-Path $repoRoot "src\GameValueEditor.Modules.Fzzml\GameValueEditor.Modules.Fzzml.csproj") `
    -c Release `
    -p:ModuleVersion=$Version `
    -o $buildDir
if ($LASTEXITCODE -ne 0) { throw "module dotnet build failed." }

$manifestPath = Join-Path $repoRoot "src\GameValueEditor.Modules.Fzzml\module.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.id -ne "game.fzzml.inventory.v1" -or $manifest.version -ne $Version) {
    throw "module.json id/version does not match the requested package."
}

Copy-Item -LiteralPath (Join-Path $buildDir "GameValueEditor.Modules.Fzzml.dll") -Destination $packageDir
Copy-Item -LiteralPath $manifestPath -Destination $packageDir
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
Compress-Archive -Path (Join-Path $packageDir "*") -DestinationPath $archivePath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$catalogPath = Join-Path $repoRoot "modules\catalog.json"
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$entry = $catalog.modules | Where-Object { $_.id -eq $manifest.id -and $_.version -eq $Version } | Select-Object -First 1
if ($null -eq $entry) { throw "modules/catalog.json has no entry for $($manifest.id) v$Version." }
$entry.sha256 = $hash
$catalog | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $catalogPath -Encoding utf8

Write-Host "Created: $archivePath"
Write-Host "SHA256: $hash"
Write-Host "Updated: $catalogPath"
