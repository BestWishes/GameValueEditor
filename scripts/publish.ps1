#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidatePattern('^(0|[1-9][0-9]*)\.[0-9]\.[0-9]$')] [string]$Version = "0.5.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot 'versioning.ps1')
. (Join-Path $PSScriptRoot 'package-archive-names.ps1')
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
. (Join-Path $PSScriptRoot 'application-package.ps1')
Invoke-ApplicationPackage -Root $repoRoot -Version $Version -Commit $headCommit `
    -OutputPath (Join-Path $repoRoot "dist/GameValueEditor-v$Version-win-x64.zip")
