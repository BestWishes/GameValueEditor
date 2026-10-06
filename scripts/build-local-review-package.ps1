#Requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'application-package.ps1')
$version = [string]([xml](Get-Content -LiteralPath (Join-Path $root 'src/GameValueEditor/GameValueEditor.csproj') -Raw -Encoding UTF8)).Project.PropertyGroup.Version
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve local source commit.' }
$directory = Assert-OfflinePath (Join-Path $root ('artifacts/local-review-' + [guid]::NewGuid().ToString('N'))) (Join-Path $root 'artifacts')
$result = Invoke-ApplicationPackage $root $version $commit (Join-Path $directory "GameValueEditor-local-review-v$version-win-x64.zip")
[IO.File]::WriteAllText((Join-Path $directory 'LOCAL-REVIEW.txt'), "LOCAL ONLY / NOT A RELEASE. Built from the working tree, including uncommitted changes. Version $version, base commit $commit. SHA256 $($result.Sha256).", [Text.UTF8Encoding]::new($false))
$result
