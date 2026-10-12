#Requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'local-artifact-cleanup.ps1')
$script:checks = 0
function Check([bool]$Result, [string]$Message) {
    if (-not $Result) { throw "Cleanup regression: $Message" }
    $script:checks++
}
function Fails([scriptblock]$Action, [string]$Pattern) {
    $errorText = ''
    try { & $Action | Out-Null } catch { $errorText = $_.Exception.Message }
    Check ($errorText -match $Pattern) "Expected $Pattern; got $errorText"
}
function JsonFile([string]$Path, $Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
}
function ZipFile([string]$Path, $Files) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    $stream = [IO.File]::Open($Path, 'CreateNew', 'Write', 'None')
    $zip = [IO.Compression.ZipArchive]::new($stream, 'Create')
    try {
        foreach ($name in $Files.Keys) {
            $entry = $zip.CreateEntry($name)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write($Files[$name]) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
}
function HostZip([string]$Root, [string]$Version) {
    $path = Join-Path $Root "dist/GameValueEditor-v$Version-win-x64.zip"
    ZipFile $path @{ 'GameValueEditor.exe' = 'fixture'; 'GameValueEditor.Updater.exe' = 'updater'
        'release-compatibility.json' = (@{ SchemaVersion = 1; Version = $Version } | ConvertTo-Json) }
    return @{ version = $Version; assetName = [IO.Path]::GetFileName($path); sha256 = (Get-FileHash -LiteralPath $path).Hash
        sizeBytes = (Get-Item -LiteralPath $path).Length }
}
function CompleteZip([string]$Root, [string]$Name, [string]$Version, [int]$Value) {
    $path = Join-Path $Root "dist/$Name"
    ZipFile $path @{ 'GameValueEditor.exe' = "fixture-$Value"; 'data/modules/installed.json' = '{}' }
    $source = Open-OfflineArchive $path
    try {
        $files = @{}; foreach ($name in $source.Entries.Keys) { $files[$name] = Get-OfflineEntryHash $source $name }
        JsonFile (Get-OfflineReceiptPath $Root $source.Hash) @{ SchemaVersion = 1; Sha256 = $source.Hash
            SizeBytes = $source.Stream.Length; ApplicationVersion = $Version; Complete = $true; Files = $files
            Modules = @(@{ Snapshot = @{ id = 'game.fixture'; version = '0.0.1' } }) }
    } finally { Close-OfflineArchive $source }
    (Get-Item -LiteralPath $path).LastWriteTimeUtc = [DateTime]::UtcNow.AddMinutes($Value - 10)
    return $path
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$work = Assert-OfflinePath (Join-Path $repository ('artifacts/cleanup-tests-' + [guid]::NewGuid().ToString('N'))) (Join-Path $repository 'artifacts')
[IO.Directory]::CreateDirectory($work) | Out-Null
try {
    $app = Join-Path $work 'app'
    $records = @('0.5.3','0.5.2','0.5.1') | ForEach-Object { HostZip $app $_ }
    JsonFile (Join-Path $app 'release-index.json') @{ schemaVersion = 1; releases = @($records) }
    $null = HostZip $app '0.5.0'
    $null = HostZip $app '0.5.4' # Pending/future release is not stale.
    $bundles = @(1..4 | ForEach-Object { CompleteZip $app "GameValueEditor-v0.5.3-20261012$_.zip" '0.5.3' $_ })
    $duplicate = Join-Path $app 'dist/GameValueEditor-v0.5.3-complete-offline-win-x64.zip'
    Copy-Item -LiteralPath $bundles[3] -Destination $duplicate
    ZipFile (Join-Path $app 'dist/my-personal-backup.zip') @{ 'save.dat' = 'personal' }
    ZipFile (Join-Path $app 'dist/GameValueEditor-v0.5.3-unverified.zip') @{ 'GameValueEditor.exe' = 'unknown' }
    ZipFile (Join-Path $app 'dist/arbitrary-child/untouched.zip') @{ 'content' = 'must not recurse' }
    JsonFile (Join-Path $app 'dist/library.json') @{ Games = @('personal') }
    $plan = Get-LocalArtifactCleanupPlan -ApplicationRepository $app
    Check ($plan.DeleteCount -eq 3) 'Expected one old host, one old bundle, one duplicate.'
    Check (@($plan.Rows | Where-Object { $_.Category -eq 'PublishedHost' -and $_.Action -eq 'Keep' }).Count -eq 3) 'Host inputs were not protected.'
    Check (@($plan.Rows | Where-Object { $_.Category -eq 'OfflineBundle' -and $_.Action -eq 'Keep' }).Count -eq 3) 'Distinct offline retention is not three.'
    Check (@($plan.Rows | Where-Object Name -eq 'GameValueEditor-v0.5.4-win-x64.zip')[0].Action -eq 'Keep') 'Pending release was deleted.'
    Check (@($plan.Rows | Where-Object Name -eq 'my-personal-backup.zip')[0].Action -eq 'Keep') 'Unknown ZIP was deleted.'
    Check (@($plan.Rows | Where-Object Name -eq 'GameValueEditor-v0.5.3-unverified.zip')[0].Action -eq 'Keep') 'Unreceipted bundle was deleted.'
    Check (@($plan.Rows | Where-Object Name -eq 'untouched.zip').Count -eq 0) 'Arbitrary subdirectory was scanned.'
    $whatIf = Invoke-LocalArtifactCleanup -Plan $plan -WhatIf
    Check ($whatIf.DeletedCount -eq 0 -and (Test-Path -LiteralPath $bundles[0])) 'WhatIf deleted a fixture.'
    $plan.Rows[0].Path = Join-Path $work 'outside.zip'
    Fails { Invoke-LocalArtifactCleanup -Plan $plan -Confirm:$false } 'plan changed'
    Check (Test-Path -LiteralPath $bundles[0]) 'A tampered plan partially deleted an allowed file.'
    $plan = Get-LocalArtifactCleanupPlan -ApplicationRepository $app
    $old = Join-Path $app 'dist/GameValueEditor-v0.5.0-win-x64.zip'
    (Get-Item -LiteralPath $old).LastWriteTimeUtc = [DateTime]::UtcNow.AddHours(1)
    Fails { Invoke-LocalArtifactCleanup -Plan $plan -Confirm:$false } 'plan changed'
    Check (Test-Path -LiteralPath $bundles[0]) 'Changed plan partially deleted another target.'
    $plan = Get-LocalArtifactCleanupPlan -ApplicationRepository $app
    $result = Invoke-LocalArtifactCleanup -Plan $plan -Confirm:$false
    Check ($result.DeletedCount -eq 3 -and $result.FreedBytes -eq $plan.DeleteBytes) 'Apply did not match preview.'
    Check ((Get-LocalArtifactCleanupPlan -ApplicationRepository $app).DeleteCount -eq 0) 'Second cleanup is not idempotent.'
    Check (Test-Path -LiteralPath (Join-Path $app 'dist/library.json')) 'Non-ZIP data was deleted.'
    Check ((Get-ChildItem -LiteralPath (Join-Path $app 'artifacts/offline-bundle-receipts') -File).Count -eq 4) 'Receipts were deleted.'
    $subset = CompleteZip $app 'GameValueEditor-v0.5.4-selected-offline-win-x64.zip' '0.5.4' 5
    $subsetHash = (Get-FileHash -LiteralPath $subset).Hash
    $subsetReceipt = Get-OfflineReceiptPath $app $subsetHash
    $subsetRecord = Get-Content -LiteralPath $subsetReceipt -Raw | ConvertFrom-Json -AsHashtable
    $subsetRecord.Complete = $false; JsonFile $subsetReceipt $subsetRecord
    $subsetPlan = Get-LocalArtifactCleanupPlan -ApplicationRepository $app
    Check (@($subsetPlan.Rows | Where-Object { $_.Complete -and $_.Action -eq 'Keep' }).Count -ge 1) 'Selected recipes evicted the latest complete bundle.'
    Check (@($subsetPlan.Rows | Where-Object { $_.Category -eq 'OfflineBundle' -and $_.Action -eq 'Keep' }).Count -eq 3) 'Selected recipes escaped offline retention.'
    $mods = Join-Path $work 'mods'
    $snapshots = @(1..3 | ForEach-Object {
        $version = "0.0.$_"; $name = "GameValueEditor.Module.Fixture-v$version.zip"
        $path = Join-Path $mods "dist/$name"
        ZipFile $path @{ 'module.json' = (@{ id = 'game.fixture'; version = $version } | ConvertTo-Json) }
        @{ version = $version; downloadUrl = "https://example.invalid/$name"; sha256 = (Get-FileHash -LiteralPath $path).Hash; sizeBytes = (Get-Item -LiteralPath $path).Length }
    })
    $module = @{} + $snapshots[2]; $module.id = 'game.fixture'; $module.releases = $snapshots
    JsonFile (Join-Path $mods 'catalog.json') @{ schemaVersion = 5; modules = @($module) }
    ZipFile (Join-Path $mods 'dist/GameValueEditor.Module.Fixture-v0.0.0.zip') @{ 'module.json' = '{"id":"game.fixture","version":"0.0.0"}' }
    ZipFile (Join-Path $mods 'dist/GameValueEditor.Module.Fixture-v0.0.4.zip') @{ 'module.json' = '{"id":"game.fixture","version":"0.0.4"}' }
    Fails { Get-LocalArtifactCleanupPlan -ApplicationRepository $app -IncludeModuleArchives } 'explicit ModuleRepository'
    $modulePlan = Get-LocalArtifactCleanupPlan -ApplicationRepository $app -ModuleRepository $mods -IncludeModuleArchives
    Check (@($modulePlan.Rows | Where-Object { $_.Category -eq 'PublishedModule' }).Count -eq 3) 'Retained module inputs were not protected.'
    Check (@($modulePlan.Rows | Where-Object { $_.Category -eq 'HistoricalModule' -and $_.Action -eq 'Delete' }).Count -eq 1) 'Old module was not identified.'
    Check (@($modulePlan.Rows | Where-Object Name -eq 'GameValueEditor.Module.Fixture-v0.0.4.zip')[0].Action -eq 'Keep') 'Future module was deleted.'
    $moduleResult = Invoke-LocalArtifactCleanup -Plan $modulePlan -Confirm:$false
    Check ($moduleResult.DeletedCount -eq $modulePlan.DeleteCount) 'Optional module apply differs from preview.'
    $subsetRecord.SizeBytes = -1; JsonFile $subsetReceipt $subsetRecord
    Fails { Get-LocalArtifactCleanupPlan -ApplicationRepository $app } 'Invalid receipt'
    $subsetRecord.SizeBytes = (Get-Item -LiteralPath $subset).Length; JsonFile $subsetReceipt $subsetRecord
    $indexed = Join-Path $app "dist/$($records[0].assetName)"
    [IO.File]::AppendAllText($indexed, 'changed')
    Fails { Get-LocalArtifactCleanupPlan -ApplicationRepository $app } 'Protected release differs'
    $linkApp = Join-Path $work 'linked'
    [IO.Directory]::CreateDirectory($linkApp) | Out-Null
    JsonFile (Join-Path $linkApp 'release-index.json') @{ schemaVersion = 1; releases = @($records) }
    New-Item -ItemType Junction -Path (Join-Path $linkApp 'dist') -Target (Join-Path $app 'dist') | Out-Null
    Fails { Get-LocalArtifactCleanupPlan -ApplicationRepository $linkApp } 'Reparse point'
    # Remove only the junction itself, not its target, before fixture tree cleanup.
    Remove-Item -LiteralPath (Join-Path $linkApp 'dist') -Force
    Write-Host "Local artifact cleanup regressions passed: $script:checks checks; isolated tiny fixtures only."
} finally {
    $null = Assert-OfflinePath $work (Join-Path $repository 'artifacts')
    if ([IO.Directory]::Exists($work)) { Remove-Item -LiteralPath $work -Recurse -Force }
}
