#Requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offline-bundle.ps1')

# Tiny fixtures exercise packaging without redistributing an EXE. These overrides are
# confined to this script scope; the production command offers no bypass switches.
$script:offlineTestBinaryVersion = '0.4.9+' + ('a' * 40)
$script:offlineTestUpdaterVersion = $script:offlineTestBinaryVersion
$script:offlineTestFailStartup = $false
$script:offlineTestFailModules = $false
$script:offlineTestCount = 0
function Get-OfflineBinaryProductVersion {
    param([string]$Path)
    if ([IO.Path]::GetFileName($Path) -eq 'GameValueEditor.Updater.exe') { return $script:offlineTestUpdaterVersion }
    return $script:offlineTestBinaryVersion
}
function Test-OfflineBundleStartup {
    param($Bundle, [string]$Directory)
    if ($script:offlineTestFailStartup) { throw 'Injected isolated startup failure.' }
}
function Test-OfflineBundleModules {
    param($Bundle, [string]$Directory, [string]$Version, $Modules)
    if ($script:offlineTestFailModules) { throw 'Injected module page failure.' }
}
function Assert-OfflineTest {
    param([bool]$Passed, [string]$Message)
    if (-not $Passed) { throw "Offline regression failed: $Message" }
    $script:offlineTestCount++
}
function Assert-OfflineThrows {
    param([scriptblock]$Action, [string]$Pattern)
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    Assert-OfflineTest ($failure -and $failure -match $Pattern) "Expected '$Pattern', received '$failure'"
}
function Write-OfflineFixtureJson {
    param([string]$Path, $Value)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 50), [Text.UTF8Encoding]::new($false))
}
function Write-OfflineFixtureZip {
    param([string]$Path, $Files)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $Files.Keys) {
            $entry = $zip.CreateEntry($name)
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write([string]$Files[$name]) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
}
function New-OfflineFixture {
    param([string]$Root)
    $base = Join-Path $Root ([guid]::NewGuid().ToString('N'))
    $app = Join-Path $base 'app'
    $mods = Join-Path $base 'GameValueEditor-Modules'
    foreach ($directory in @("$app/dist", "$mods/dist", "$mods/schemas", "$app/src/GameValueEditor")) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    [IO.File]::WriteAllText("$app/src/GameValueEditor/GameValueEditor.csproj", '<Project><PropertyGroup><Version>0.4.9</Version></PropertyGroup></Project>')
    $capability = @{ MinimumModuleHostApi = 2; MaximumModuleHostApi = 7; MaximumCatalogSchemaVersion = 5 }
    $hostFiles = [ordered]@{
        'GameValueEditor.exe' = 'synthetic host'; 'GameValueEditor.Updater.exe' = 'synthetic updater'
        'README.md' = 'readme'; 'LICENSE' = 'license'; 'SECURITY.md' = 'security'
        'release-compatibility.json' = (@{ SchemaVersion = 1; Version = '0.4.9'; Compatibility = $capability } | ConvertTo-Json)
    }
    $hostZip = "$app/dist/GameValueEditor-v0.4.9-win-x64.zip"
    Write-OfflineFixtureZip $hostZip $hostFiles
    $record = @{ version = '0.4.9'; assetName = [IO.Path]::GetFileName($hostZip); sourceCommit = ('a' * 40)
        sha256 = (Get-FileHash -LiteralPath $hostZip).Hash; sizeBytes = (Get-Item -LiteralPath $hostZip).Length
        minimumModuleHostApi = 2; maximumModuleHostApi = 7; maximumCatalogSchemaVersion = 5 }
    Write-OfflineFixtureJson "$app/release-index.json" @{ schemaVersion = 1; releases = @($record) }
    $fingerprint = @{ executableSha256 = ('1' * 64); gameAssemblySha256 = ('2' * 64); metadataSha256 = ('3' * 64) }
    $editor = @{ id = 'game.fixture.resources'; displayName = '资源'; order = 1; sessionOnly = $true }
    $manifest = @{ id = 'game.fixture'; version = '1.2.3'; displayName = 'Fixture'; gameDisplayName = 'Fixture'
        description = 'fixture'; assemblyFile = 'Fixture.dll'; hostApiVersion = 7; minimumHostVersion = '0.4.4'
        processNames = @('Fixture'); compatibleBuilds = @($fingerprint); editors = @($editor) }
    $moduleZip = "$mods/dist/Fixture-v1.2.3.zip"
    Write-OfflineFixtureZip $moduleZip @{ 'module.json' = ($manifest | ConvertTo-Json -Depth 30); 'Fixture.dll' = 'synthetic dll' }
    $release = @{ version = '1.2.3'; hostApiVersion = 7; minimumHostVersion = '0.4.4'; editors = @($editor)
        compatibleBuilds = @($fingerprint); downloadUrl = 'https://github.com/BestWishes/GameValueEditor-Modules/releases/download/fixture-v1.2.3/Fixture-v1.2.3.zip'
        sha256 = (Get-FileHash -LiteralPath $moduleZip).Hash; sizeBytes = (Get-Item -LiteralPath $moduleZip).Length }
    $entry = @{} + $manifest
    $entry.Remove('assemblyFile')
    foreach ($key in $release.Keys) { $entry[$key] = $release[$key] }
    $entry.releases = @($release)
    $entry.contributors = @()
    $catalog = @{ schemaVersion = 5; hostApiVersion = 6; modules = @($entry) }
    Write-OfflineFixtureJson "$mods/catalog.json" $catalog
    # Fixture schemas intentionally cover only the fixture protocol; real end-to-end
    # verification uses the module repository's full official schemas.
    Write-OfflineFixtureJson "$mods/schemas/catalog.schema.json" @{ type = 'object'; required = @('schemaVersion','hostApiVersion','modules')
        properties = @{ schemaVersion = @{ const = 5 }; hostApiVersion = @{ type = 'integer' }; modules = @{ type = 'array'; minItems = 1 } } }
    Write-OfflineFixtureJson "$mods/schemas/module.schema.json" @{ type = 'object'
        required = @('id','version','assemblyFile','hostApiVersion','minimumHostVersion','editors','compatibleBuilds','processNames') }
    return @{ App = $app; Mods = $mods; HostZip = $hostZip; ModuleZip = $moduleZip; Catalog = $catalog; Release = $release
        Manifest = $manifest; HostFiles = $hostFiles; Record = $record; Capability = $capability
        Output = "$app/dist/GameValueEditor-v0.4.9-complete-offline-win-x64.zip"
        Args = @{ ApplicationRepository = $app; ModuleRepository = $mods } }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureRoot = Assert-OfflinePath (Join-Path $repoRoot ('artifacts/offline-tests-' + [guid]::NewGuid().ToString('N'))) (Join-Path $repoRoot 'artifacts')
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
try {
    $f = New-OfflineFixture $fixtureRoot
    $argsForFixture = $f.Args
    $first = Invoke-OfflineBundle @argsForFixture
    Assert-OfflineTest ($first.Verified -and -not $first.Reused -and $first.Modules[0].Version -eq '1.2.3') 'Schema 5 / API 7 complete build'
    $second = Invoke-OfflineBundle @argsForFixture
    Assert-OfflineTest ($second.Reused -and $first.Sha256 -ceq $second.Sha256) 'Idempotent reuse preserves archive bytes'
    Assert-OfflineTest ((Invoke-OfflineBundle @argsForFixture -VerifyOnly).VerifyOnly) 'VerifyOnly success'
    Assert-OfflineTest ([IO.File]::Exists($first.ReceiptPath)) 'Independent receipt created without modifying the ZIP'
    Assert-OfflineTest ((Invoke-OfflineBundle @argsForFixture -ModuleIds game.fixture -OutputPath $f.Output).Reused) 'Explicit selection can reuse identical bytes without rewriting frozen recipe intent'
    $f.Catalog.modules[0].sha256 = '0' * 64
    $f.Release.sha256 = '0' * 64
    Write-OfflineFixtureJson "$($f.Mods)/catalog.json" $f.Catalog
    $changedAsset = Invoke-OfflineBundle @argsForFixture -VerifyOnly
    Assert-OfflineTest ($changedAsset.IntegrityVerified -and $null -eq $changedAsset.IsLatest) 'Catalog mutation of an immutable version cannot invalidate the old bundle or falsely mark it latest'
    $f.Release.sha256 = (Get-FileHash -LiteralPath $f.ModuleZip).Hash
    $f.Catalog.modules[0].sha256 = $f.Release.sha256
    $futureRelease = @{} + $f.Release
    $futureRelease.version = '1.2.4'
    foreach ($key in $futureRelease.Keys) { $f.Catalog.modules[0][$key] = $futureRelease[$key] }
    $f.Catalog.modules[0].releases = @($futureRelease, $f.Release)
    Write-OfflineFixtureJson "$($f.Mods)/catalog.json" $f.Catalog
    $older = Invoke-OfflineBundle @argsForFixture -VerifyOnly
    Assert-OfflineTest ($older.IntegrityVerified -and $older.IsLatest -eq $false -and $older.Sha256 -ceq $first.Sha256) 'Older recipe remains intact without requiring the newer ZIP'
    [IO.File]::Move($f.ModuleZip, $f.ModuleZip + '.saved')
    [IO.File]::Move($f.HostZip, $f.HostZip + '.saved')
    [IO.File]::Move("$($f.Mods)/catalog.json", "$($f.Mods)/catalog.json.saved")
    $detached = Invoke-OfflineBundle @argsForFixture -VerifyOnly
    Assert-OfflineTest ($detached.IntegrityVerified -and $null -eq $detached.IsLatest -and $detached.LatestStatus -eq 'Unknown') 'Receipt verifies without original source ZIPs or available catalog'
    $savedReceipt = [IO.File]::ReadAllText($first.ReceiptPath)
    $badReceipt = $savedReceipt | ConvertFrom-Json -AsHashtable
    $badReceipt.Files['README.md'] = '0' * 64
    Write-OfflineFixtureJson $first.ReceiptPath $badReceipt
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -VerifyOnly } 'Receipt file hash mismatch'
    [IO.File]::WriteAllText($first.ReceiptPath, $savedReceipt)
    [IO.File]::Move($first.ReceiptPath, $first.ReceiptPath + '.saved')
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -VerifyOnly } 'Missing verification evidence'
    [IO.File]::Move($first.ReceiptPath + '.saved', $first.ReceiptPath)
    [IO.File]::Move($f.ModuleZip + '.saved', $f.ModuleZip)
    [IO.File]::Move($f.HostZip + '.saved', $f.HostZip)
    [IO.File]::Move("$($f.Mods)/catalog.json.saved", "$($f.Mods)/catalog.json")
    [IO.File]::Move($first.ReceiptPath, $first.ReceiptPath + '.saved')
    Assert-OfflineTest ((Invoke-OfflineBundle @argsForFixture -VerifyOnly).IsLatest -eq $false) 'Legacy verification matches bundled versions rather than catalog latest'
    [IO.File]::Move($first.ReceiptPath + '.saved', $first.ReceiptPath)
    foreach ($key in $f.Release.Keys) { $f.Catalog.modules[0][$key] = $f.Release[$key] }
    $f.Catalog.modules[0].releases = @($f.Release)
    Write-OfflineFixtureJson "$($f.Mods)/catalog.json" $f.Catalog
    $script:offlineTestFailModules = $true
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -VerifyOnly } 'Injected module page failure'
    Assert-OfflineTest ((Get-FileHash -LiteralPath $f.Output).Hash -ceq $first.Sha256) 'Module page failure preserves the old ZIP'
    $script:offlineTestFailModules = $false
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -Force -VerifyOnly } 'cannot be combined'
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -ModuleIds game.unknown } 'Unknown module'
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -ModuleIds game.fixture,game.fixture } 'Duplicate identity'
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -OutputPath "$($f.App)-outside/test.zip" } 'escaped'
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -OutputPath $f.HostZip } 'standard application'
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -OutputPath "$($f.App)/dist/missing.zip" -VerifyOnly } 'requires an existing'
    $subset = Invoke-OfflineBundle @argsForFixture -ModuleIds game.fixture
    Assert-OfflineTest ($subset.ArchivePath -match 'selected-offline' -and $subset.Modules.Count -eq 1) 'Explicit subset has distinct name'
    [IO.File]::WriteAllText($f.Output, 'old bytes')
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture } 'not reusable'
    $script:offlineTestFailStartup = $true
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -Force } 'Injected isolated startup'
    Assert-OfflineTest ([IO.File]::ReadAllText($f.Output) -ceq 'old bytes') 'Failed replacement preserves prior output'
    $script:offlineTestFailStartup = $false
    Assert-OfflineTest ((Invoke-OfflineBundle @argsForFixture -Force).Verified) 'Explicit replacement after successful checks'
    [IO.File]::WriteAllText($f.Output, 'locked old bytes')
    $lockedOutput = [IO.File]::Open($f.Output, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -Force } '.'
        Assert-OfflineTest ([IO.File]::ReadAllText($f.Output) -ceq 'locked old bytes') 'Failed atomic promotion preserves locked prior output'
    } finally { $lockedOutput.Dispose() }
    $readLockedSource = Open-OfflineArchive $f.HostZip
    try { Assert-OfflineThrows { [IO.File]::WriteAllText($f.HostZip, 'attempted concurrent write') } '.' }
    finally { Close-OfflineArchive $readLockedSource }
    $script:offlineTestUpdaterVersion = '0.4.9+' + ('b' * 40)
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture } 'source commits differ'
    $script:offlineTestUpdaterVersion = $script:offlineTestBinaryVersion
    $script:offlineTestBinaryVersion = '0.4.8+' + ('a' * 40)
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture } 'ProductVersion mismatch'
    $script:offlineTestBinaryVersion = $script:offlineTestUpdaterVersion
    $f.Record.maximumModuleHostApi = 6
    Write-OfflineFixtureJson "$($f.App)/release-index.json" @{ schemaVersion = 1; releases = @($f.Record) }
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture } 'capability mismatch'
    $f.Record.maximumModuleHostApi = 7
    $f.Record.sha256 = '0' * 64
    Write-OfflineFixtureJson "$($f.App)/release-index.json" @{ schemaVersion = 1; releases = @($f.Record) }
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -ExpectedHostSha256 ((Get-FileHash -LiteralPath $f.HostZip).Hash) } 'does not match release-index'
    $unindexedHash = (Get-FileHash -LiteralPath $f.HostZip).Hash
    [IO.File]::Move("$($f.App)/release-index.json", "$($f.App)/release-index.json.saved")
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture } 'independently verified'
    Assert-OfflineTest ((Invoke-OfflineBundle @argsForFixture -ExpectedHostSha256 $unindexedHash -Force).Verified) 'Explicit verified unindexed host digest'
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -ExpectedHostSha256 ('0' * 64) } 'independently verified'

    $f = New-OfflineFixture $fixtureRoot; $argsForFixture = $f.Args
    $f.Catalog.modules[0].minimumHostVersion = '0.5.0'; $f.Release.minimumHostVersion = '0.5.0'
    $oldManifest = @{} + $f.Manifest
    $oldManifest.version = '1.2.2'; $oldManifest.hostApiVersion = 6
    $oldZip = "$($f.Mods)/dist/Fixture-v1.2.2.zip"
    Write-OfflineFixtureZip $oldZip @{ 'module.json' = ($oldManifest | ConvertTo-Json -Depth 30); 'Fixture.dll' = 'older synthetic dll' }
    $oldRelease = @{} + $f.Release
    $oldRelease.version = '1.2.2'; $oldRelease.hostApiVersion = 6; $oldRelease.minimumHostVersion = '0.4.4'
    $oldRelease.downloadUrl = 'https://github.com/BestWishes/GameValueEditor-Modules/releases/download/fixture-v1.2.2/Fixture-v1.2.2.zip'
    $oldRelease.sha256 = (Get-FileHash -LiteralPath $oldZip).Hash; $oldRelease.sizeBytes = (Get-Item -LiteralPath $oldZip).Length
    $f.Catalog.modules[0].releases += $oldRelease
    Write-OfflineFixtureJson "$($f.Mods)/catalog.json" $f.Catalog
    $fallback = Invoke-OfflineBundle @argsForFixture
    Assert-OfflineTest ($fallback.Modules[0].Version -ceq '1.2.2') 'Older compatible package is actually installed, with its original bytes'
    $bundleSource = Open-OfflineArchive $fallback.ArchivePath
    $tamperedFiles = @{}
    try { foreach ($name in $bundleSource.Entries.Keys) { $tamperedFiles[$name] = Read-OfflineEntryText $bundleSource $name } }
    finally { Close-OfflineArchive $bundleSource }
    $installedTamper = $tamperedFiles['data/modules/installed.json'] | ConvertFrom-Json -AsHashtable
    $installedTamper.Modules[0].LocalUserPath = 'forbidden personal state'
    $tamperedFiles['data/modules/installed.json'] = $installedTamper | ConvertTo-Json -Depth 10
    Write-OfflineFixtureZip $f.Output $tamperedFiles
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -VerifyOnly } 'installed record keys'
    Assert-OfflineTest ((Invoke-OfflineBundle @argsForFixture -Force).Verified) 'Bad installation metadata regenerated only with explicit Force'

    $f = New-OfflineFixture $fixtureRoot; $argsForFixture = $f.Args
    $f.Catalog.schemaVersion = 6
    Write-OfflineFixtureJson "$($f.Mods)/catalog.json" $f.Catalog
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture } 'JSON'
    Write-OfflineFixtureJson "$($f.Mods)/schemas/catalog.schema.json" @{ '$ref' = 'https://example.invalid/never-fetch.json' }
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture } 'External JSON Schema'

    $f = New-OfflineFixture $fixtureRoot
    $argsForFixture = $f.Args
    $f.Release.minimumHostVersion = '0.5.0'
    $f.Catalog.modules[0].minimumHostVersion = '0.5.0'
    $older = @{} + $f.Release
    $older.version = '1.2.2'; $older.minimumHostVersion = '0.4.4'; $older.hostApiVersion = 6
    $f.Catalog.modules[0].releases += $older
    $selection = @(Select-OfflineModules $f.Catalog $f.Capability '0.4.9' @())
    Assert-OfflineTest ($selection[0].version -ceq '1.2.2') 'Highest compatible retained snapshot (not latest incompatible)'
    $older.maximumHostVersion = '0.4.8'
    Assert-OfflineThrows { Select-OfflineModules $f.Catalog $f.Capability '0.4.9' @() } 'No host-compatible'
    $older.maximumHostVersion = '0.4.3'
    Assert-OfflineThrows { Select-OfflineModules $f.Catalog $f.Capability '0.4.9' @() } 'Invalid host version range'
    $f.Capability.MaximumCatalogSchemaVersion = 4
    Assert-OfflineThrows { Select-OfflineModules $f.Catalog $f.Capability '0.4.9' @() } 'Unsupported catalog'
    $f.Capability.MaximumCatalogSchemaVersion = 5; $f.Capability.MaximumModuleHostApi = 5
    Assert-OfflineThrows { Select-OfflineModules $f.Catalog $f.Capability '0.4.9' @() } 'Catalog API'

    foreach ($mutation in @('hash','size','missing','fingerprint','editors','api','range','unlisted','duplicate')) {
        $f = New-OfflineFixture $fixtureRoot; $argsForFixture = $f.Args
        $pattern = 'mismatch'
        switch ($mutation) {
            'hash' { $f.Release.sha256 = '0' * 64; $f.Catalog.modules[0].sha256 = $f.Release.sha256 }
            'size' { $f.Release.sizeBytes++; $f.Catalog.modules[0].sizeBytes++ }
            'missing' { [IO.File]::Move($f.ModuleZip, "$($f.ModuleZip).away"); $pattern = 'Required local ZIP is missing' }
            'fingerprint' { $f.Manifest.compatibleBuilds = @(@{ executableSha256 = ('9' * 64); gameAssemblySha256 = ('2' * 64); metadataSha256 = ('3' * 64) }) }
            'editors' { $f.Manifest.editors = @(@{ id = 'different'; displayName = '资源'; order = 1; sessionOnly = $true }) }
            'api' { $f.Manifest.hostApiVersion = 6 }
            'range' { $f.Manifest.minimumHostVersion = '0.4.3' }
            'unlisted' { $f.Manifest.supportsUnlistedBuildValidation = $true }
            'duplicate' { $f.Catalog.modules += $f.Catalog.modules[0]; $pattern = 'Duplicate identity' }
        }
        if ($mutation -in @('fingerprint','editors','api','range','unlisted')) {
            Write-OfflineFixtureZip $f.ModuleZip @{ 'module.json' = ($f.Manifest | ConvertTo-Json -Depth 30); 'Fixture.dll' = 'synthetic dll' }
            $f.Release.sha256 = (Get-FileHash -LiteralPath $f.ModuleZip).Hash; $f.Release.sizeBytes = (Get-Item -LiteralPath $f.ModuleZip).Length
            $f.Catalog.modules[0].sha256 = $f.Release.sha256; $f.Catalog.modules[0].sizeBytes = $f.Release.sizeBytes
        }
        Write-OfflineFixtureJson "$($f.Mods)/catalog.json" $f.Catalog
        Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture } $pattern
        Assert-OfflineTest (-not [IO.File]::Exists($f.Output)) "Rejected $mutation never publishes an output"
    }
    foreach ($badName in @('../escaped.dll','/absolute.dll','data/','bad:ads.dll','trailing.','CON.dll','bad\path.dll')) {
        $zipPath = Join-Path $fixtureRoot ([guid]::NewGuid().ToString('N') + '.zip')
        Write-OfflineFixtureZip $zipPath @{ $badName = 'unsafe' }
        Assert-OfflineThrows { $opened = Open-OfflineArchive $zipPath; Close-OfflineArchive $opened } 'Unsafe'
    }
    $zipPath = Join-Path $fixtureRoot 'case-collision.zip'
    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try { $null = $zip.CreateEntry('a.dll'); $null = $zip.CreateEntry('A.dll') } finally { $zip.Dispose(); $stream.Dispose() }
    Assert-OfflineThrows { $opened = Open-OfflineArchive $zipPath; Close-OfflineArchive $opened } 'Duplicate ZIP'
    $zipPath = Join-Path $fixtureRoot 'symlink.zip'
    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try { $entry = $zip.CreateEntry('link.dll'); $entry.ExternalAttributes = -1577058304 } finally { $zip.Dispose(); $stream.Dispose() }
    Assert-OfflineThrows { $opened = Open-OfflineArchive $zipPath; Close-OfflineArchive $opened } 'Non-regular'
    $f = New-OfflineFixture $fixtureRoot; $argsForFixture = $f.Args
    $junction = "$($f.App)/dist/redirect"
    New-Item -ItemType Junction -Path $junction -Target $fixtureRoot | Out-Null
    try { Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -OutputPath "$junction/test.zip" } 'Reparse point' }
    finally { Remove-Item -LiteralPath $junction -Force }
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture -OutputPath "$($f.App)/dist/gamevalueeditor-v0.4.9-win-x64.zip" } 'standard application'
    Write-OfflineFixtureZip $f.ModuleZip @{ 'module.json' = ($f.Manifest | ConvertTo-Json -Depth 30); 'Fixture.dll' = 'synthetic dll'; 'user-save.txt' = 'unexpected' }
    $f.Release.sha256 = (Get-FileHash -LiteralPath $f.ModuleZip).Hash; $f.Release.sizeBytes = (Get-Item -LiteralPath $f.ModuleZip).Length
    $f.Catalog.modules[0].sha256 = $f.Release.sha256; $f.Catalog.modules[0].sizeBytes = $f.Release.sizeBytes
    Write-OfflineFixtureJson "$($f.Mods)/catalog.json" $f.Catalog
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture } 'Unexpected files'
    $f = New-OfflineFixture $fixtureRoot; $argsForFixture = $f.Args
    $f.HostFiles['data/profile.json'] = 'personal data'
    Write-OfflineFixtureZip $f.HostZip $f.HostFiles
    Assert-OfflineThrows { Invoke-OfflineBundle @argsForFixture } 'Unexpected files'
    foreach ($name in @('GameValueEditor-v0.4.9-complete-offline-win-x64.zip','GameValueEditor-v0.4.9-selected-offline-win-x64.zip','GameValueEditor-v0.4.9-win-x64-review.zip')) {
        Assert-OfflineTest (-not (Test-StandardApplicationArchiveName $name)) "Standard cleanup preserves $name"
    }
    Assert-OfflineTest (Test-StandardApplicationArchiveName 'GameValueEditor-v0.4.9-win-x64.zip') 'Standard cleanup recognizes standard archive'
    Assert-OfflineTest (@(Get-ChildItem -LiteralPath "$($f.App)/artifacts" -Directory -Filter 'offline-build-*').Count -eq 0) 'Owned staging cleaned after failures'
    # Use the production assertion even though fixture bundles replace the child process.
    # The expected API belongs to the frozen target archive, never a hardcoded current API.
    $taskModules = @(@{ Snapshot = @{ id = 'game.fixture'; version = '1.2.3'; editors = @(@{ id = 'game.fixture.resources' }) } })
    foreach ($taskApi in @(7, 8, 9)) {
        $taskTarget = @{ SchemaVersion = 1; Version = '0.5.1'; Compatibility = @{ MinimumModuleHostApi = 2; MaximumModuleHostApi = $taskApi } }
        $taskReport = @{ SchemaVersion = 1; ApplicationVersion = '0.5.1'; HostApiVersion = $taskApi; Verified = $true
            Modules = @(@{ Id = 'game.fixture'; Version = '1.2.3'; EditorIds = @('game.fixture.resources') }) }
        Assert-OfflineModuleVerifierReport $taskReport '0.5.1' $taskTarget $taskModules
        Assert-OfflineTest $true "Report accepted the target's API $taskApi"
        $taskReport.HostApiVersion++
        Assert-OfflineThrows { Assert-OfflineModuleVerifierReport $taskReport '0.5.1' $taskTarget $taskModules } 'target host version and API'
    }
    $taskTarget.Compatibility.MaximumModuleHostApi = 8; $taskReport.HostApiVersion = 8
    foreach ($taskMutation in @('schema','version','api-type','verified','pages')) {
        $taskBadReport = @{} + $taskReport
        switch ($taskMutation) {
            'schema' { $taskBadReport.SchemaVersion = 2 }
            'version' { $taskBadReport.ApplicationVersion = '0.5.0' }
            'api-type' { $taskBadReport.HostApiVersion = '8' }
            'verified' { $taskBadReport.Verified = $false }
            'pages' { $taskBadReport.Modules = @() }
        }
        Assert-OfflineThrows { Assert-OfflineModuleVerifierReport $taskBadReport '0.5.1' $taskTarget $taskModules } 'target host version and API|actual loaded modules/pages'
    }
    foreach ($taskMutation in @('schema','version','api-type','zero-api','range')) {
        $taskBadTarget = @{} + $taskTarget; $taskBadTarget.Compatibility = @{} + $taskTarget.Compatibility
        switch ($taskMutation) {
            'schema' { $taskBadTarget.SchemaVersion = 2 }
            'version' { $taskBadTarget.Version = '0.5.0' }
            'api-type' { $taskBadTarget.Compatibility.MaximumModuleHostApi = '8' }
            'zero-api' { $taskBadTarget.Compatibility.MaximumModuleHostApi = 0 }
            'range' { $taskBadTarget.Compatibility.MinimumModuleHostApi = 9 }
        }
        Assert-OfflineThrows { Assert-OfflineModuleVerifierReport $taskReport '0.5.1' $taskBadTarget $taskModules } 'Invalid target host compatibility'
    }
    Write-Host "Offline bundle regressions passed: $script:offlineTestCount assertions."
} finally {
    $null = Assert-OfflinePath $fixtureRoot (Join-Path $repoRoot 'artifacts')
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
}
