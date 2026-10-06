#Requires -Version 7.4
[CmdletBinding()]
param([string]$ApplicationVersion = '0.5.0')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offline-bundle.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$null = Assert-OfflineVersion $ApplicationVersion
$source = $null
$candidate = $null
$work = Assert-OfflinePath (Join-Path $root ('artifacts/module-verifier-tests-' + [guid]::NewGuid().ToString('N'))) (Join-Path $root 'artifacts')
try {
    $source = Open-OfflineArchive (Join-Path $root "dist/GameValueEditor-v$ApplicationVersion-complete-offline-win-x64.zip")
    $receipt = (Read-OfflineJsonFile (Get-OfflineReceiptPath $root $source.Hash)) | ConvertFrom-Json -AsHashtable
    if ($receipt.Sha256 -cne $source.Hash -or $receipt.SchemaVersion -ne 1) { throw 'This integration test requires a previously verified bundle receipt.' }
    Assert-OfflineEntries $source @($receipt.Files.Keys)
    foreach ($name in $receipt.Files.Keys) {
        if ((Get-OfflineEntryHash $source $name) -cne $receipt.Files[$name]) { throw 'Integration input differs from its receipt.' }
    }
    $package = Join-Path $work 'package'
    [IO.Directory]::CreateDirectory($package) | Out-Null
    foreach ($name in $source.Entries.Keys) { Copy-OfflineEntry $source $name (Join-Path $package $name) $package }
    $module = $receipt.Modules[0].Snapshot
    $manifestPath = Join-Path $package "data/modules/packages/$($module.id)/$($module.version)/module.json"
    $manifest = (Read-OfflineJsonFile $manifestPath) | ConvertFrom-Json -AsHashtable
    $dllPath = Join-Path ([IO.Path]::GetDirectoryName($manifestPath)) $manifest.assemblyFile
    $dllBytes = [IO.File]::ReadAllBytes($dllPath)
    foreach ($scenario in @('invalid-dll','invalid-page-registration')) {
        if ($scenario -eq 'invalid-dll') { [IO.File]::WriteAllText($dllPath, 'not a managed assembly') }
        else {
            [IO.File]::WriteAllBytes($dllPath, $dllBytes)
            $manifest.editors[0].id = 'invalid.fixture.page'
            [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 50))
        }
        $archive = Join-Path $work "$scenario.zip"
        $stream = [IO.File]::Open($archive, 'CreateNew', 'Write', 'None')
        try { Write-OfflineZip $stream $package @($source.Entries.Keys) } finally { $stream.Dispose() }
        $candidate = Open-OfflineArchive $archive
        $failure = ''
        try { Test-OfflineBundleModules $candidate (Join-Path $work $scenario) $ApplicationVersion $receipt.Modules }
        catch { $failure = $_.Exception.Message }
        finally { Close-OfflineArchive $candidate; $candidate = $null }
        if ($failure -notmatch 'Actual module load/page verification failed') { throw "Verifier accepted $scenario, or failed for an unrelated reason: $failure" }
        Write-Host "Actual module verifier rejected: $scenario"
    }
    Write-Host 'Actual module verifier negative integrations passed: 2 cases.'
} finally {
    Close-OfflineArchive $candidate
    Close-OfflineArchive $source
    if ([IO.Directory]::Exists($work)) {
        $null = Assert-OfflinePath $work (Join-Path $root 'artifacts')
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}
